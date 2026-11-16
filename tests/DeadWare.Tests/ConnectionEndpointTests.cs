using System.Net;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

/// <summary>
/// <see cref="ConnectionLedger.RecordEndpoint"/>, the ETW fast path, must record exactly what <c>Record(NetworkAddress.FromEndpoints(...))</c>
/// records, the path it replaced: the same peers (normalization, IPv4-mapped IPv6, scope ids), the same validation and drops, the same totals.
/// </summary>
public class ConnectionEndpointTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 15, 12, 0, 30, TimeSpan.Zero);
    private sealed record Event(ProcessIdentity? Process, IPAddress Source, int SourcePort, IPAddress Destination, int DestinationPort, ConnectionProtocol Protocol, bool Send, DateTimeOffset Time, int Bytes);

    private static readonly ProcessIdentity App = new(10, 100, "app.exe"), Browser = new(20, 200, "browser.exe");
    private static IPAddress Ip(string text) => IPAddress.Parse(text);
    private static IPAddress Scoped(string text, long scope) => new(IPAddress.Parse(text).GetAddressBytes(), scope);

    /// <summary>Every kind of observation the ETW callbacks can pass, including the invalid ones the ledger drops.</summary>
    private static List<Event> Mixed(DateTimeOffset at)
    {
        var local4 = Ip("192.168.1.5");
        var events = new List<Event>
        {
            new(App, local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at, 1200),
            new(App, Ip("8.8.8.8"), 443, local4, 50000, ConnectionProtocol.Tcp, false, at.AddMilliseconds(5), 64000),
            // The same remote as IPv4-mapped IPv6, as a dual-stack socket reports it: one peer, "8.8.8.8".
            new(App, Ip("::ffff:192.168.1.5"), 50000, Ip("::ffff:8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(6), 300),
            // A mapped address with a scope id still folds to the bare IPv4 text.
            new(App, Scoped("::ffff:192.168.1.5", 7), 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(7), 10),
            new(Browser, Ip("2001:db8::10"), 51000, Ip("2606:4700::1111"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(8), 900),
            new(Browser, Ip("2606:4700::1111"), 443, Ip("2001:db8::10"), 51000, ConnectionProtocol.Tcp, false, at.AddMilliseconds(9), 15000),
            // Link-local addresses on two interfaces differ only by scope: two peers, each with its "%scope" text.
            new(Browser, Scoped("fe80::1", 3), 5353, Scoped("fe80::2", 3), 5353, ConnectionProtocol.Udp, true, at.AddMilliseconds(10), 80),
            new(Browser, Scoped("fe80::1", 4), 5353, Scoped("fe80::2", 4), 5353, ConnectionProtocol.Udp, true, at.AddMilliseconds(11), 81),
            new(Browser, Ip("::1"), 9000, Ip("::1"), 9001, ConnectionProtocol.Tcp, true, at.AddMilliseconds(12), 5),
            new(App, local4, 60000, Ip("1.1.1.1"), 53, ConnectionProtocol.Udp, true, at.AddMilliseconds(13), 60),
            new(App, Ip("1.1.1.1"), 53, local4, 60000, ConnectionProtocol.Udp, false, at.AddMilliseconds(14), 120),
            // An identity equal to App but a different object (an attribution refresh): the same peer.
            new(new ProcessIdentity(10, 100, "app.exe"), local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(15), 7),
            // The size limits: 16 MiB is accepted, more is dropped, and so is a negative size.
            new(App, Ip("8.8.8.8"), 443, local4, 50000, ConnectionProtocol.Tcp, false, at.AddMilliseconds(16), 16 * 1024 * 1024),
            new(App, Ip("8.8.8.8"), 443, local4, 50000, ConnectionProtocol.Tcp, false, at.AddMilliseconds(17), 16 * 1024 * 1024 + 1),
            new(App, Ip("8.8.8.8"), 443, local4, 50000, ConnectionProtocol.Tcp, false, at.AddMilliseconds(18), -1),
            // Fields out of range are dropped, every time they are seen.
            new(App, local4, 70000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(19), 1),
            new(App, local4, 70000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(20), 1),
            new(App, local4, 50000, Ip("8.8.8.8"), -1, ConnectionProtocol.Tcp, true, at.AddMilliseconds(21), 1),
            new(App, local4, 50000, Ip("8.8.8.8"), 443, (ConnectionProtocol)7, true, at.AddMilliseconds(22), 1),
            new(new ProcessIdentity(30, -5, "bad.exe"), local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(23), 1),
            new(new ProcessIdentity(31, 5, ""), local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(24), 1),
            new(new ProcessIdentity(32, 5, new string('x', 261)), local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(25), 1),
            // Unattributed: no identity, or a negative PID.
            new(null, local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(26), 1),
            new(new ProcessIdentity(-1, 5, "neg.exe"), local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddMilliseconds(27), 1),
            // The next minute: a second delta for the same peer.
            new(App, local4, 50000, Ip("8.8.8.8"), 443, ConnectionProtocol.Tcp, true, at.AddSeconds(31), 2000),
        };
        return events;
    }

    private static void Old(ConnectionLedger ledger, Event e) =>
        ledger.Record(NetworkAddress.FromEndpoints(e.Process!, e.Source, e.SourcePort, e.Destination, e.DestinationPort, e.Protocol, e.Send), e.Time, e.Bytes, e.Send);
    private static void New(ConnectionLedger ledger, Event e) =>
        ledger.RecordEndpoint(e.Process!, e.Source, e.SourcePort, e.Destination, e.DestinationPort, e.Protocol, e.Send, e.Time, e.Bytes);

    private static string Key(ConnectionPeer p) => $"{p.Process.Pid}|{p.Process.Generation}|{p.Process.Name}|{p.LocalAddress}|{p.LocalPort}|{p.RemoteAddress}|{p.RemotePort}|{p.Protocol}";

    /// <summary>Everything a sample carries except its random segment id, in a stable order.</summary>
    private static void AssertSame(ConnectionSample expected, ConnectionSample actual)
    {
        Assert.Equal(expected.Live.OrderBy(t => Key(t.Peer), StringComparer.Ordinal), actual.Live.OrderBy(t => Key(t.Peer), StringComparer.Ordinal));
        Assert.Equal(expected.Deltas.OrderBy(d => Key(d.Peer) + d.Minute.UtcTicks, StringComparer.Ordinal), actual.Deltas.OrderBy(d => Key(d.Peer) + d.Minute.UtcTicks, StringComparer.Ordinal));
        Assert.Equal((expected.Available, expected.Complete, expected.DroppedEvents, expected.LostEvents, expected.UnattributedEvents, expected.Status, expected.Sequence, expected.CoverageFrom),
            (actual.Available, actual.Complete, actual.DroppedEvents, actual.LostEvents, actual.UnattributedEvents, actual.Status, actual.Sequence, actual.CoverageFrom));
    }

    [Fact]
    public void The_fast_path_records_what_the_general_path_records()
    {
        ConnectionLedger old = new(), fast = new();
        // Snapshots keep wall and monotonic time in step, so the ledger sees no clock gap of its own.
        ConnectionSample Take(ConnectionLedger ledger, int seconds) => ledger.Take(Epoch.AddSeconds(seconds), seconds);
        foreach (var e in Mixed(Epoch)) { Old(old, e); New(fast, e); }
        var first = Take(old, 32);
        AssertSame(first, Take(fast, 32));
        // The fixture covers what it claims: folded and scoped peers, drops and unattributed observations.
        Assert.Contains(first.Live, t => t.Peer.RemoteAddress == "8.8.8.8" && t.Peer.LocalAddress == "192.168.1.5" && t.Peer.Protocol == ConnectionProtocol.Tcp);
        Assert.DoesNotContain(first.Live, t => t.Peer.RemoteAddress.StartsWith("::ffff:", StringComparison.Ordinal) || t.Peer.LocalAddress.StartsWith("::ffff:", StringComparison.Ordinal));
        Assert.Contains(first.Live, t => t.Peer.LocalAddress == "fe80::1%3");
        Assert.Contains(first.Live, t => t.Peer.LocalAddress == "fe80::1%4");
        Assert.Equal(6, first.Live.Count);
        // Nine invalid observations, and the two unattributed ones, which also count as dropped.
        Assert.Equal(11, first.DroppedEvents);
        Assert.Equal(2, first.UnattributedEvents);
        Assert.Equal(2, first.Deltas.Count(d => d.Peer.RemoteAddress == "8.8.8.8" && d.Peer.LocalPort == 50000 && d.Peer.Process.Pid == 10));

        // Again with every peer already known, then an observation too far from the last snapshot, then after a break.
        foreach (var e in Mixed(Epoch.AddSeconds(33))) { Old(old, e); New(fast, e); }
        AssertSame(Take(old, 65), Take(fast, 65));
        var late = Mixed(Epoch.AddSeconds(127))[0];
        Old(old, late); New(fast, late);
        var gap = Take(old, 66);
        AssertSame(gap, Take(fast, 66));
        Assert.Equal(23, gap.DroppedEvents);
        old.Break(4); fast.Break(4);
        foreach (var e in Mixed(Epoch.AddSeconds(67))) { Old(old, e); New(fast, e); }
        AssertSame(Take(old, 99), Take(fast, 99));
    }

    [Fact]
    public void Both_paths_fill_the_same_limits_and_drop_the_same_observations()
    {
        ConnectionLedger old = new(maximumPeers: 3, maximumDeltaKeys: 4), fast = new(maximumPeers: 3, maximumDeltaKeys: 4);
        for (int port = 1000; port < 1010; port++)
        {
            var e = new Event(App, Ip("10.0.0.2"), port, Ip("9.9.9.9"), 443, ConnectionProtocol.Tcp, port % 2 == 0, Epoch.AddMilliseconds(port), 100);
            Old(old, e); New(fast, e);
        }
        var expected = old.Take(Epoch.AddSeconds(2), 1);
        AssertSame(expected, fast.Take(Epoch.AddSeconds(2), 1));
        Assert.Equal(3, expected.Live.Count); Assert.Equal(7, expected.DroppedEvents);
    }

    [Fact]
    public void A_known_peer_is_found_by_its_endpoint_and_builds_no_new_strings()
    {
        var ledger = new ConnectionLedger();
        IPAddress local = Ip("192.168.1.5"), remote = Ip("2606:4700::1111");
        ledger.RecordEndpoint(App, local, 50000, remote, 443, ConnectionProtocol.Tcp, true, Epoch, 10);
        var first = Assert.Single(ledger.Take(Epoch, 1).Live).Peer;
        ledger.RecordEndpoint(App, remote, 443, local, 50000, ConnectionProtocol.Tcp, false, Epoch.AddSeconds(0.1), 10);
        long before = GC.GetAllocatedBytesForCurrentThread();
        // Sends and receives, which ETW reports with source and destination swapped.
        for (int i = 0; i < 10_000; i++)
            if (i % 2 == 0) ledger.RecordEndpoint(App, local, 50000, remote, 443, ConnectionProtocol.Tcp, true, Epoch.AddSeconds(0.5), 10);
            else ledger.RecordEndpoint(App, remote, 443, local, 50000, ConnectionProtocol.Tcp, false, Epoch.AddSeconds(0.5), 10);
        long perEvent = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
        var live = Assert.Single(ledger.Take(Epoch.AddSeconds(1), 2).Live);
        // The peer published is the one first built, not a copy rebuilt per event.
        Assert.Same(first, live.Peer);
        Assert.Equal(20 + 10_000 * 10, live.ReceivedBytes + live.SentBytes);
        Assert.True(perEvent < 16, $"{perEvent} bytes allocated per event");
    }

    [Fact]
    public void A_mapped_and_a_plain_IPv4_endpoint_count_as_one_peer_on_the_fast_path()
    {
        var ledger = new ConnectionLedger();
        ledger.RecordEndpoint(App, Ip("::ffff:10.1.2.3"), 4000, Ip("::ffff:203.0.113.9"), 80, ConnectionProtocol.Tcp, true, Epoch, 5);
        ledger.RecordEndpoint(App, Ip("10.1.2.3"), 4000, Ip("203.0.113.9"), 80, ConnectionProtocol.Tcp, true, Epoch, 7);
        var live = Assert.Single(ledger.Take(Epoch.AddSeconds(1), 1).Live);
        Assert.Equal(("10.1.2.3", "203.0.113.9", 12L), (live.Peer.LocalAddress, live.Peer.RemoteAddress, live.SentBytes));
    }
}
