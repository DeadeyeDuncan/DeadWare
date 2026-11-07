using System.Globalization;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ConnectionDestinationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionLocation Located = new("US", "United States", "California", "Mountain View", new(37.4, -122.1), "test");
    private static readonly ConnectionLocation Located2 = new("US", "United States", "New York", "New York", new(40.7, -74.0), "test");
    private static readonly ConnectionLocation CountryOnly = new("AU", "Australia", null, null, null, "test");

    private static LocatedConnection P(string remote, string name = "a.exe", int pid = 10, int localPort = 100, int port = 443,
        ConnectionProtocol protocol = ConnectionProtocol.Tcp, long received = 10, long sent = 5, double down = 1, double up = 2,
        DateTimeOffset? first = null, DateTimeOffset? last = null, ConnectionLocation? location = null) =>
        new(new(new(new(pid, 1, name), "192.168.1.5", localPort, remote, port, protocol), first ?? T0, last ?? T0, received, sent, down, up), location);

    [Fact] public void Live_destinations_group_by_remote_address()
    {
        var l = ConnectionDestinations.FromLive([P("8.8.8.8", received: long.MaxValue, first: T0.AddSeconds(5)),
            P("8.8.8.8", name: "b.exe", pid: 11, localPort: 101, received: 1, last: T0.AddSeconds(9)),
            P("8.8.8.8", localPort: 102, port: 53, protocol: ConnectionProtocol.Udp)], true);
        var r = Assert.Single(l.Rows);
        Assert.Equal("8.8.8.8", r.Address);
        Assert.Equal(new NetworkAddressClass(NetworkAddressKind.Public, "Internet"), r.Class);
        Assert.Equal((2, 2, 3), (r.AppCount, r.PortCount, r.Connections));
        Assert.Equal(long.MaxValue, r.ReceivedBytes); Assert.Equal(15, r.SentBytes);
        Assert.Equal((3d, 6d), (r.DownloadBytesPerSecond, r.UploadBytesPerSecond));
        Assert.Equal((T0, T0.AddSeconds(9)), (r.FirstSeen, r.LastSeen));
        Assert.Null(r.Minutes); Assert.Null(r.Complete);
        Assert.Equal(new ConnectionDestinationCounts(1, 1, 0, 0, 1), l.Counts);
    }

    [Fact] public void Live_rates_are_null_when_capture_is_unavailable()
    {
        var r = ConnectionDestinations.FromLive([P("8.8.8.8")], false).Rows[0];
        Assert.Null(r.DownloadBytesPerSecond); Assert.Null(r.UploadBytesPerSecond);
        Assert.Equal((10L, 5L, 1), (r.ReceivedBytes, r.SentBytes, r.Connections));
    }

    [Fact] public void Live_app_names_group_case_insensitively_and_keep_the_first_casing()
    {
        var r = ConnectionDestinations.FromLive([P("8.8.8.8", name: "\tChrome.exe  "), P("8.8.8.8", name: "chrome.exe", pid: 11)], true).Rows[0];
        Assert.Equal(("Chrome.exe", 1), (r.Apps[0].Name, r.AppCount));
        Assert.Equal((20L, 10L), (r.Apps[0].ReceivedBytes, r.Apps[0].SentBytes));
    }

    [Fact] public void Live_unlocated_and_counts_follow_the_summary_rules()
    {
        var l = ConnectionDestinations.FromLive([P("8.8.8.8", location: Located), P("1.0.0.1", location: CountryOnly), P("9.9.9.9"),
            P("192.168.1.1"), P("100.64.0.1", location: Located2)], true);
        Assert.Equal(new ConnectionDestinationCounts(5, 3, 1, 1, 2), l.Counts);
        Assert.Equal([Located2.Point!], l.Rows.Single(r => r.Address == "100.64.0.1").Points);
        Assert.Equal([Located.Point!], l.Rows.Single(r => r.Address == "8.8.8.8").Points);
        Assert.Equal(CountryOnly, l.Rows.Single(r => r.Address == "1.0.0.1").Location);
        Assert.Empty(l.Rows.Single(r => r.Address == "1.0.0.1").Points);
        Assert.Equal(["1.0.0.1", "9.9.9.9"], l.Rows.Where(r => r.Unlocated).Select(r => r.Address).Order(StringComparer.Ordinal));
        Assert.False(l.Rows.Single(r => r.Address == "192.168.1.1").Unlocated);
        Assert.False(l.Rows.Single(r => r.Address == "100.64.0.1").Unlocated);
    }

    [Fact] public void Live_location_is_the_first_non_null_one_and_an_invalid_point_is_unlocated()
    {
        var invalid = Located with { Point = new(double.NaN, 0) };
        var l = ConnectionDestinations.FromLive([P("8.8.8.8"), P("8.8.8.8", pid: 11, location: Located2), P("8.8.8.8", pid: 12, location: Located),
            P("9.9.9.9", location: invalid)], true);
        var google = l.Rows.Single(r => r.Address == "8.8.8.8");
        Assert.Equal(Located2, google.Location); Assert.Equal([Located2.Point!], google.Points); Assert.False(google.Unlocated);
        var bad = l.Rows.Single(r => r.Address == "9.9.9.9");
        Assert.Empty(bad.Points); Assert.True(bad.Unlocated); Assert.Equal(1, l.Counts.Unlocated);
    }

    [Fact] public void Apps_and_ports_keep_the_ten_busiest_with_exact_counts()
    {
        var conns = Enumerable.Range(0, 12).Select(i => i == 10
            ? P("8.8.8.8", name: "app10.exe", pid: 100 + i, localPort: 2000 + i, port: 1011, protocol: ConnectionProtocol.Udp, received: 12, sent: 0)
            : P("8.8.8.8", name: $"app{i}.exe", pid: 100 + i, localPort: 2000 + i, port: 1000 + i, received: i + 1, sent: 0)).ToList();
        var r = ConnectionDestinations.FromLive(conns, true).Rows[0];
        Assert.Equal((10, 12, 10, 12), (r.Apps.Count, r.AppCount, r.Ports.Count, r.PortCount));
        Assert.Equal(("app10.exe", "app11.exe"), (r.Apps[0].Name, r.Apps[1].Name));
        Assert.Equal((1011, ConnectionProtocol.Tcp, 1011, ConnectionProtocol.Udp), (r.Ports[0].Port, r.Ports[0].Protocol, r.Ports[1].Port, r.Ports[1].Protocol));
        Assert.Equal(["app10.exe", "app11.exe", "app9.exe", "app8.exe", "app7.exe", "app6.exe", "app5.exe", "app4.exe", "app3.exe", "app2.exe"], r.Apps.Select(a => a.Name));
        Assert.Equal((12L, 0L), (r.Ports[0].ReceivedBytes, r.Ports[0].SentBytes));
    }

    [Fact] public void Busiest_app_sums_saturate()
    {
        var r = ConnectionDestinations.FromLive([P("8.8.8.8", name: "b.exe", received: long.MaxValue, sent: long.MaxValue),
            P("8.8.8.8", name: "a.exe", pid: 11, received: long.MaxValue - 1, sent: long.MaxValue)], true).Rows[0];
        Assert.Equal(["a.exe", "b.exe"], r.Apps.Select(a => a.Name));   // both saturate to long.MaxValue, so the tie goes by key
        Assert.Equal((long.MaxValue, long.MaxValue), (r.ReceivedBytes, r.SentBytes));
    }

    [Fact] public void Scopes_partition_kinds_and_special_is_all_only()
    {
        var l = ConnectionDestinations.FromLive([P("8.8.8.8"), P("192.168.1.1"), P("239.255.255.250")], true);
        Assert.Equal(3, ConnectionDestinations.InScope(l, DestinationScope.All).Count);
        Assert.Equal(["8.8.8.8"], ConnectionDestinations.InScope(l, DestinationScope.Internet).Select(r => r.Address));
        Assert.Equal(["192.168.1.1"], ConnectionDestinations.InScope(l, DestinationScope.Local).Select(r => r.Address));
        Assert.Equal(new ConnectionDestinationCounts(3, 1, 1, 1, 1), l.Counts);
    }

    [Fact] public void Each_scope_keeps_its_own_top_1000()
    {
        static string Public(int i) => $"20.0.{i / 256}.{i % 256}";
        static string Local(int i) => $"10.0.{i / 256}.{i % 256}";
        // 20.0.0.199 ties 20.0.0.200 at 400 bytes, right at the Internet cut: the lower address text stays.
        var conns = Enumerable.Range(0, 1200).Select(i => P(Public(i), received: i == 199 ? 400 : 2L * i, sent: 0))
            .Concat(Enumerable.Range(0, 1100).Select(i => P(Local(i), received: 2L * i + 1, sent: 0))).ToList();
        var l = ConnectionDestinations.FromLive(conns, true);
        var internet = ConnectionDestinations.InScope(l, DestinationScope.Internet);
        var local = ConnectionDestinations.InScope(l, DestinationScope.Local);
        Assert.Equal(1000, internet.Count); Assert.Equal(1000, local.Count);
        Assert.Contains("20.0.0.199", internet.Select(r => r.Address)); Assert.DoesNotContain("20.0.0.200", internet.Select(r => r.Address));
        Assert.Equal(Public(1199), internet[0].Address);
        Assert.Equal(Local(1099), local[0].Address); Assert.Equal(Local(100), local[^1].Address);
        Assert.True(l.Rows.Count <= 3000);
        var overallTop1000 = conns.Select(c => (Address: c.Traffic.Peer.RemoteAddress, Bytes: c.Traffic.ReceivedBytes))
            .OrderByDescending(c => c.Bytes).ThenBy(c => c.Address, StringComparer.Ordinal).Take(1000).Select(c => c.Address);
        Assert.Equal(overallTop1000, ConnectionDestinations.InScope(l, DestinationScope.All).Select(r => r.Address));
        Assert.Equal(new ConnectionDestinationCounts(2300, 1200, 1100, 0, 1200), l.Counts);
    }

    [Fact] public void Scope_text_parses_ordinally()
    {
        Assert.Equal(DestinationScope.Internet, ConnectionDestinations.ParseScope("Internet"));
        Assert.Equal(DestinationScope.Local, ConnectionDestinations.ParseScope("Local"));
        foreach (var text in new[] { null, "", "All", "local", "INTERNET", "1", " Local" })
            Assert.Equal(DestinationScope.All, ConnectionDestinations.ParseScope(text));
    }

    private static ConnectionDestination D(string address, string label, ConnectionLocation? location, string? app, int appCount,
        (int Port, ConnectionProtocol Protocol)? port, long received, long sent, double? down, double? up, int? connections, long? minutes,
        int first, int last)
    {
        var kind = label == "Internet" ? NetworkAddressKind.Public : label.StartsWith("Local", StringComparison.Ordinal) ? NetworkAddressKind.Local : NetworkAddressKind.Special;
        return new(address, new(kind, label), location, [], false, app is null ? [] : [new(app, received, sent)], appCount,
            port is { } p ? [new(p.Port, p.Protocol, received, sent)] : [], port is null ? 0 : 1, received, sent, down, up, connections, minutes,
            T0.AddSeconds(first), T0.AddSeconds(last), null);
    }

    [Fact] public void Destination_sort_rules()
    {
        var r1 = D("9.9.9.9", "Internet", new("US", "United States", "Texas", "Austin", null, "test"), "zeta.exe", 1, (443, ConnectionProtocol.Tcp), 300, 1, 3, null, 2, 5, 2, 5);
        var r2 = D("2001:4860::8888", "Internet", new("DE", "Germany", null, "Berlin", null, "test"), "Alpha.exe", 3, (53, ConnectionProtocol.Udp), 100, 3, null, null, null, null, 0, 9);
        var r3 = D("10.0.0.1", "Local · private", Located, "alpha.exe", 1, (53, ConnectionProtocol.Tcp), 200, 2, 1, 2, 1, 1, 1, 1);
        var r4 = D("bad", "Special · unrecognized", null, null, 0, null, 100, 0, null, null, null, null, 3, 3);
        ConnectionDestination[] rows = [r4, r2, r3, r1];
        IEnumerable<string> Sorted(ConnectionDestinationColumn key, bool descending) =>
            rows.Order(Comparer<ConnectionDestination>.Create((a, b) => ConnectionDestinationSort.Compare(a, b, key, descending))).Select(r => r.Address);
        void Check(ConnectionDestinationColumn key, ConnectionDestination[] ascending, ConnectionDestination[] descending)
        {
            Assert.Equal(ascending.Select(r => r.Address), Sorted(key, false));
            Assert.Equal(descending.Select(r => r.Address), Sorted(key, true));
        }
        Check(ConnectionDestinationColumn.Destination, [r1, r3, r2, r4], [r2, r3, r1, r4]);   // IPv4 before IPv6 by bytes; unparseable last both ways
        Check(ConnectionDestinationColumn.Class, [r2, r1, r3, r4], [r4, r3, r2, r1]);         // Internet, Local, Special; equal labels tie by address
        Check(ConnectionDestinationColumn.Location, [r2, r1, r3, r4], [r1, r2, r3, r4]);      // "—" (no location, Local, Special) last both ways
        Check(ConnectionDestinationColumn.Apps, [r2, r3, r1, r4], [r1, r3, r2, r4]);          // name ignoring case, then more apps first
        Check(ConnectionDestinationColumn.Ports, [r3, r2, r1, r4], [r1, r2, r3, r4]);         // port, then Tcp before Udp
        Check(ConnectionDestinationColumn.Download, [r3, r1, r2, r4], [r1, r3, r2, r4]);      // null last both ways
        Check(ConnectionDestinationColumn.Upload, [r3, r2, r1, r4], [r3, r2, r1, r4]);
        Check(ConnectionDestinationColumn.Received, [r2, r4, r3, r1], [r1, r3, r2, r4]);      // equal Received ties by address both ways
        Check(ConnectionDestinationColumn.Sent, [r4, r1, r3, r2], [r2, r3, r1, r4]);
        Check(ConnectionDestinationColumn.Connections, [r3, r1, r2, r4], [r1, r3, r2, r4]);
        Check(ConnectionDestinationColumn.Minutes, [r3, r1, r2, r4], [r1, r3, r2, r4]);
        Check(ConnectionDestinationColumn.FirstSeen, [r2, r3, r1, r4], [r4, r1, r3, r2]);
        Check(ConnectionDestinationColumn.LastSeen, [r3, r4, r1, r2], [r2, r1, r4, r3]);
        Assert.Equal(0, ConnectionDestinationSort.Compare(r1, r1 with { }, ConnectionDestinationColumn.Received, true));

        ConnectionDestinationColumn[] ascendingFirst = [ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class,
            ConnectionDestinationColumn.Location, ConnectionDestinationColumn.Apps, ConnectionDestinationColumn.Ports];
        foreach (var key in Enum.GetValues<ConnectionDestinationColumn>())
            Assert.Equal(!ascendingFirst.Contains(key), ConnectionDestinationSort.DescendingFirst(key));

        Assert.Equal([ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class, ConnectionDestinationColumn.Location,
            ConnectionDestinationColumn.Apps, ConnectionDestinationColumn.Ports, ConnectionDestinationColumn.Download, ConnectionDestinationColumn.Upload,
            ConnectionDestinationColumn.Received, ConnectionDestinationColumn.Sent, ConnectionDestinationColumn.Connections], ConnectionDestinationSort.Columns(false));
        Assert.Equal([ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class, ConnectionDestinationColumn.Location,
            ConnectionDestinationColumn.Apps, ConnectionDestinationColumn.Ports, ConnectionDestinationColumn.Received, ConnectionDestinationColumn.Sent,
            ConnectionDestinationColumn.Minutes, ConnectionDestinationColumn.FirstSeen, ConnectionDestinationColumn.LastSeen], ConnectionDestinationSort.Columns(true));
        foreach (var key in new[] { ConnectionDestinationColumn.Download, ConnectionDestinationColumn.Upload, ConnectionDestinationColumn.Connections })
        { Assert.Equal(ConnectionDestinationColumn.Received, ConnectionDestinationSort.InMode(key, true)); Assert.Equal(key, ConnectionDestinationSort.InMode(key, false)); }
        foreach (var key in new[] { ConnectionDestinationColumn.Minutes, ConnectionDestinationColumn.FirstSeen, ConnectionDestinationColumn.LastSeen })
        { Assert.Equal(ConnectionDestinationColumn.Received, ConnectionDestinationSort.InMode(key, false)); Assert.Equal(key, ConnectionDestinationSort.InMode(key, true)); }
        Assert.Equal(ConnectionDestinationColumn.Apps, ConnectionDestinationSort.InMode(ConnectionDestinationColumn.Apps, true));
        Assert.Equal(ConnectionDestinationColumn.Sent, ConnectionDestinationSort.InMode(ConnectionDestinationColumn.Sent, false));
    }

    [Fact] public void Destination_copy_texts()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.", DestinationCopy.CountLine(new(3, 2, 1, 0, 1), DestinationScope.All, false));
            Assert.Equal("1 destination: 1 Internet (0 unlocated), 0 local, 0 special-purpose.", DestinationCopy.CountLine(new(1, 1, 0, 0, 0), DestinationScope.All, true));
            Assert.Equal("0 destinations: 0 Internet (0 unlocated), 0 local, 0 special-purpose.", DestinationCopy.CountLine(new(0, 0, 0, 0, 0), DestinationScope.Local, true));
            Assert.Equal("1,234 destinations: 1,234 Internet (1,234 unlocated), 0 local, 0 special-purpose. Showing the top 1,000 by traffic (received + sent); search or narrow the interval to see the rest.",
                DestinationCopy.CountLine(new(1234, 1234, 0, 0, 1234), DestinationScope.Internet, true));
            Assert.EndsWith(" Showing the top 1,000 by traffic (received + sent); search to see the rest.", DestinationCopy.CountLine(new(1234, 1234, 0, 0, 0), DestinationScope.All, false));
            Assert.DoesNotContain("Showing", DestinationCopy.CountLine(new(1234, 1234, 0, 0, 0), DestinationScope.Local, false));
            Assert.DoesNotContain("Showing", DestinationCopy.CountLine(new(1000, 1000, 0, 0, 0), DestinationScope.All, false));
            Assert.EndsWith("search to see the rest.", DestinationCopy.CountLine(new(1001, 0, 1001, 0, 0), DestinationScope.Local, false));
            Assert.Equal("— destinations: — Internet (— unlocated), — local, — special-purpose.", DestinationCopy.UnavailableCountLine);

            const string footnote = "A destination is one remote address; its numbers add every app and port that reached it under the current filters. Special-purpose addresses (shared CGNAT, multicast, broadcast, documentation and other reserved ranges) are listed under All only and count as neither local nor unlocated. Locations are approximate.";
            Assert.Equal(footnote + " Live rows cover peers active in the last 60 s.", DestinationCopy.Footnote(false));
            Assert.Equal(footnote + " History lists saved minutes; a minute appears a few seconds after it ends.", DestinationCopy.Footnote(true));

            Assert.Equal("No destinations: per-app capture is not running. The capture status above says why.", DestinationCopy.LiveEmpty(false, false, false, DestinationScope.All));
            Assert.Equal("No destinations yet. Apps that send or receive traffic appear here.", DestinationCopy.LiveEmpty(true, false, true, DestinationScope.Internet));
            Assert.Equal("No destinations match the filters.", DestinationCopy.LiveEmpty(true, true, true, DestinationScope.All));
            Assert.Equal("No Internet destinations right now.", DestinationCopy.LiveEmpty(true, true, true, DestinationScope.Internet));
            Assert.Equal("No local destinations right now.", DestinationCopy.LiveEmpty(false, true, false, DestinationScope.Local));

            Assert.Equal("Choose a valid interval to list saved destinations.", DestinationCopy.InvalidInterval);
            Assert.Equal("Reading saved destinations…", DestinationCopy.Reading);
            Assert.Equal("Destinations unavailable; no saved observations shown.", DestinationCopy.Unavailable);
            Assert.Equal("No saved destinations in this interval.", DestinationCopy.HistoryEmpty(false, false, DestinationScope.All));
            Assert.Equal("No saved destinations in this minute.", DestinationCopy.HistoryEmpty(true, false, DestinationScope.All));
            Assert.Equal("No saved destinations match the filters in this interval.", DestinationCopy.HistoryEmpty(false, true, DestinationScope.All));
            Assert.Equal("No saved destinations match the filters in this minute.", DestinationCopy.HistoryEmpty(true, true, DestinationScope.All));
            Assert.Equal("No saved Internet destinations in this interval.", DestinationCopy.HistoryEmpty(false, true, DestinationScope.Internet));
            Assert.Equal("No saved local destinations in this minute.", DestinationCopy.HistoryEmpty(true, false, DestinationScope.Local));

            Assert.Equal("Public Internet address.", DestinationCopy.ClassTip(NetworkAddressKind.Public));
            Assert.Equal("Local address: counted under Local.", DestinationCopy.ClassTip(NetworkAddressKind.Local));
            Assert.Equal("Special-purpose address: listed under All only and counted as neither local nor unlocated.", DestinationCopy.ClassTip(NetworkAddressKind.Special));
            Assert.Equal("Country or region only: not drawn on the map; counted as unlocated.", DestinationCopy.CountryOnly);
            Assert.Equal("Some saved minutes have no map position; the address also counts as unlocated.", DestinationCopy.SomeMinutesUnlocated);
            Assert.Equal("No location database. Use Download / update locations at the bottom of the page.", DestinationCopy.NoDatabase);
            Assert.Equal("Not in the location database, or its lookup has not finished.", DestinationCopy.NotInDatabase);
            Assert.Equal("No location was saved with this address.", DestinationCopy.NoSavedLocation);
            Assert.Equal("Local addresses are not located.", DestinationCopy.LocalNotLocated);
            Assert.Equal("Special-purpose addresses are not located.", DestinationCopy.SpecialNotLocated);

            Assert.Equal(("Since each flow was first observed.", "Saved in the shown interval."), (DestinationCopy.HeaderTip(ConnectionDestinationColumn.Received, false), DestinationCopy.HeaderTip(ConnectionDestinationColumn.Received, true)));
            Assert.Equal(("Since each flow was first observed.", "Saved in the shown interval."), (DestinationCopy.HeaderTip(ConnectionDestinationColumn.Sent, false), DestinationCopy.HeaderTip(ConnectionDestinationColumn.Sent, true)));
            Assert.Equal("Observed flows to this address: one per app process, local address and port, remote port and protocol. A flow idle for 60 s drops out.", DestinationCopy.HeaderTip(ConnectionDestinationColumn.Connections, false));
            Assert.Equal("Saved minutes with traffic to this address in the shown interval.", DestinationCopy.HeaderTip(ConnectionDestinationColumn.Minutes, true));
            Assert.Null(DestinationCopy.HeaderTip(ConnectionDestinationColumn.Connections, true)); Assert.Null(DestinationCopy.HeaderTip(ConnectionDestinationColumn.Minutes, false));
            foreach (var column in new[] { ConnectionDestinationColumn.Destination, ConnectionDestinationColumn.Class, ConnectionDestinationColumn.Location, ConnectionDestinationColumn.Apps,
                ConnectionDestinationColumn.Ports, ConnectionDestinationColumn.Download, ConnectionDestinationColumn.Upload, ConnectionDestinationColumn.FirstSeen, ConnectionDestinationColumn.LastSeen })
            { Assert.Null(DestinationCopy.HeaderTip(column, false)); Assert.Null(DestinationCopy.HeaderTip(column, true)); }

            Assert.Equal($"Observed since {T0.ToLocalTime():g}.", DestinationCopy.ObservedSince(T0));
            Assert.Equal("and 1,234 more", DestinationCopy.MoreLine(1234));
            Assert.Equal(("Capture is incomplete; totals may be low.", "Some minutes were captured incompletely; totals may be low."), (DestinationCopy.LiveIncomplete, DestinationCopy.HistoryIncomplete));
            Assert.Equal("Endpoint capture is stale or unavailable; no current rate.", DestinationCopy.RateUnavailable);

            Assert.Equal("Highlighted 1 map node.", DestinationCopy.Highlighted(1));
            Assert.Equal("Highlighted 1,234 map nodes.", DestinationCopy.Highlighted(1234));
            Assert.Equal("Not on the map: its location is not among the 256 drawn.", DestinationCopy.NotAmongDrawn);
            Assert.Equal("Not on the map: local addresses are not located.", DestinationCopy.NotOnMap(NetworkAddressKind.Local));
            Assert.Equal("Not on the map: special-purpose addresses are not located.", DestinationCopy.NotOnMap(NetworkAddressKind.Special));
            Assert.Equal("Not on the map: no map position for this address.", DestinationCopy.NotOnMap(NetworkAddressKind.Public));

            Assert.Equal("Copied to the clipboard.", DestinationCopy.Copied);
            Assert.Equal("Copy failed (ExternalException); try again.", DestinationCopy.CopyFailed("ExternalException"));
            Assert.Equal("Export is available in History.", DestinationCopy.ExportLiveOnly);
            Assert.Equal("This interval holds too many saved rows to export destinations. Choose a shorter interval.", DestinationCopy.ExportLimit);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact] public void Count_line_uses_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.StartsWith("1.234 destinations: 1.234 Internet", DestinationCopy.CountLine(new(1234, 1234, 0, 0, 0), DestinationScope.All, false));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
