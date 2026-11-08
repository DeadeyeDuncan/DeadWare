using System.Text.RegularExpressions;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class ConnectionMinuteFolderTests : IDisposable
{
    private static readonly DateTimeOffset M0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionLocation L1 = new("US", "United States", "California", "Mountain View", new(37.4, -122.1), "fixture");
    private readonly Dictionary<string, ConnectionLocation?> Cache = new();
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DeadWare-minute-folder-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private ConnectionMinuteFolder F(int threshold = 16384) => new(Cache.TryGetValue, threshold);

    private static ConnectionSample Pub(double t, double? from, ConnectionDelta[] deltas, bool available = true, string segment = "s") =>
        new(M0.AddSeconds(t), t, (long)(t * 10), segment, [], deltas, from is { } f ? M0.AddSeconds(f) : null,
            available, available, 0, 0, 0, "");

    private static ConnectionDelta D(double t, long received = 10, int localPort = 100, string remote = "8.8.8.8", int port = 443,
        string name = "app.exe", int pid = 10)
    {
        var at = M0.AddSeconds(t);
        var minute = new DateTimeOffset(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        return new(minute, new(new(pid, 1, name), "192.168.1.5", localPort, remote, port, ConnectionProtocol.Tcp), received, 0, at, at);
    }

    private static int Merge(ConnectionMinuteFolder f, ConnectionSample p, long generation = 1, bool complete = true, GeoPoint? origin = null) =>
        f.Merge(p, generation, origin, complete, p.Deltas);

    private static IReadOnlyList<ConnectionMinuteUnit> Units(ConnectionMinuteFolder f) => f.Sendable(M0.AddHours(1), false).Send;

    private ConnectionHistoryStore Store()
    {
        var store = new ConnectionHistoryStore(Path.Combine(directory, "connections.db"), 268435456);
        store.Initialize();
        return store;
    }

    private ConnectionHistoryView AppendAndRead(IReadOnlyList<ConnectionMinuteUnit> units)
    {
        var store = Store();
        store.Append([.. units.SelectMany(u => u.Records)], [.. units.SelectMany(u => u.Coverage)], M0.AddMinutes(10));
        return store.Read(new(M0, M0.AddMinutes(5)));
    }

    [Fact] public void One_row_per_pair_per_minute_with_saturating_totals()
    {
        var f = F();
        Merge(f, Pub(1, null, [D(0.5)]));
        for (int t = 2; t <= 60; t++)
            Merge(f, Pub(t, t - 1, t == 30 ? [D(t - 0.5), D(29.7, localPort: 200)] : [D(t - 0.5)]));
        f.FreezeAll();
        var u = Assert.Single(Units(f)); var r = Assert.Single(u.Records);
        Assert.Equal((610L, M0.AddSeconds(0.5), M0.AddSeconds(59.5), true), (r.Delta.ReceivedBytes, r.Delta.FirstSeen, r.Delta.LastSeen, r.Complete));
        Assert.Equal(61, u.Observations);
        var c = Assert.Single(u.Coverage);
        Assert.Equal((M0.AddSeconds(1), M0.AddMinutes(1), true), (c.From, c.To, c.Complete));

        var big = F();
        Merge(big, Pub(10, 9, [D(9.2, received: long.MaxValue / 2 + 1), D(9.4, received: long.MaxValue / 2 + 1)]));
        big.FreezeAll();
        Assert.Equal(long.MaxValue, Units(big)[0].Records[0].Delta.ReceivedBytes);
    }

    [Fact] public void Completeness_is_the_and_of_contributing_publications()
    {
        var f = F();
        for (int t = 1; t <= 10; t++) Merge(f, Pub(t, t - 1, [D(t - 0.5)]), complete: t != 5);
        f.FreezeAll();
        var u = Units(f)[0];
        Assert.False(u.Records[0].Complete);
        Assert.Equal([(0d, 4d, true), (4d, 5d, false), (5d, 10d, true)],
            u.Coverage.Select(c => ((c.From - M0).TotalSeconds, (c.To - M0).TotalSeconds, c.Complete)));
    }

    [Fact] public void Minute_closes_two_seconds_and_one_poll_after_it_ends()
    {
        var open = F();
        Merge(open, Pub(30, 29, [D(29.5)]));
        Assert.Equal(0, Merge(open, Pub(62.9, 61.9, [])));
        Assert.Equal(0, open.Waiting);

        var closed = F();
        Merge(closed, Pub(30, 29, [D(29.5)]));
        Assert.Equal(1, Merge(closed, Pub(63, 62, [])));
        Assert.True(closed.Waiting > 0);

        var slow = F();
        Merge(slow, Pub(50, 40, [D(45)]));
        Merge(slow, Pub(60, 50, [D(55)]));
        Assert.Equal(0, Merge(slow, Pub(70, 60, [D(59.5)])));
        Assert.Equal(1, Merge(slow, Pub(80, 70, [D(59.8)])));
        var r = Units(slow)[0].Records[0];
        Assert.Equal((40L, M0.AddSeconds(59.8)), (r.Delta.ReceivedBytes, r.Delta.LastSeen)); // the late delta is in

        var none = F();
        Merge(none, Pub(30, 29, [D(29.5)]));
        Assert.Equal(0, Merge(none, Pub(65, null, [])));
        Assert.Equal(0, none.Waiting);
    }

    [Fact] public void Late_delta_becomes_a_new_unit_with_a_new_batch_id()
    {
        var f = F();
        Merge(f, Pub(30, 29, [D(29.5)]));
        Merge(f, Pub(63, 62, []));
        Assert.Equal(1, Merge(f, Pub(64, 63, [D(59.9)])));
        var minute0 = Units(f).Where(u => u.Minute == M0).ToArray();
        Assert.Equal(2, minute0.Length);
        Assert.NotEqual(minute0[0].BatchId, minute0[1].BatchId);

        var stalled = F();
        Merge(stalled, Pub(30, 29, [D(29.5)]));
        Assert.Equal(1, stalled.Tick(M0.AddSeconds(90), 1));
        Assert.Equal(0, Merge(stalled, Pub(40, 30, [D(35)])));
        Assert.Equal(0, Merge(stalled, Pub(61, 40, [])));
        Assert.Equal(1, Merge(stalled, Pub(70, 62.5, [])));
        // Both minute-0 units appended to a real store read back as one row with the summed bytes.
        Assert.Equal(20, AppendAndRead(Units(stalled)).Rows.Single(r => r.Minute == M0).ReceivedBytes);
    }

    [Fact] public void Segment_or_generation_change_closes_old_units()
    {
        var f = F();
        Merge(f, Pub(30, 29, [D(29.5)], segment: "a"));
        Assert.Equal(1, Merge(f, Pub(31, null, [], available: false, segment: "b")));
        Assert.Equal(0, f.Held);
        int waiting = f.Waiting;
        foreach (var s in new[] { "c", "d", "e" }) Assert.Equal(0, Merge(f, Pub(32, null, [], available: false, segment: s)));
        Assert.Equal((0, waiting), (f.Held, f.Waiting));

        var g = F();
        Merge(g, Pub(40, 39, [D(39.5)]), generation: 1);
        Assert.Equal(1, Merge(g, Pub(41, 40, [D(40.5)]), generation: 2));
        Assert.True(g.Held > 0);
    }

    [Fact] public void Tick_closes_old_generation_and_stalled_minutes()
    {
        var f = F();
        Merge(f, Pub(30, 29, [D(29.5)]), generation: 1);
        Assert.Equal(1, f.Tick(M0.AddSeconds(31), 2));

        var s = F();
        Merge(s, Pub(30, 29, [D(29.5)]));
        Assert.Equal(0, s.Tick(M0.AddSeconds(89), 1));
        Assert.Equal(1, s.Tick(M0.AddSeconds(90), 1));
    }

    [Fact] public void Pressure_counts_open_entries_and_runs_only()
    {
        var f = F(4);
        Merge(f, Pub(10, null, [D(9.1, remote: "1.1.1.1"), D(9.2, remote: "1.1.1.2"), D(9.3, remote: "1.1.1.3")]));
        f.FreezeAll();
        Assert.Equal(0, Merge(f, Pub(20, null, [D(19.1, remote: "2.2.2.1"), D(19.2, remote: "2.2.2.2"), D(19.3, remote: "2.2.2.3")])));
        Assert.Equal(3, f.Held);
        Assert.Equal(1, Merge(f, Pub(21, null, [D(20.1, remote: "2.2.2.4")])));
        Assert.Equal(0, f.Held);
    }

    [Fact] public void Sendable_units_stay_within_append_limits()
    {
        var f = F();
        Merge(f, Pub(10, 9, [D(9.5), D(9.6, remote: "9.9.9.9")])); f.FreezeAll();     // minute 0: 2 records + 1 run
        Merge(f, Pub(70, 69, [D(69.5), D(69.6, remote: "9.9.9.9")])); f.FreezeAll();  // minute 1: 2 records + 1 run
        Merge(f, Pub(130, null, [D(129.5)])); f.FreezeAll();                            // minute 2: 1 record
        var first = f.Sendable(M0.AddHours(1), false, 3, 3);
        Assert.Equal([M0], first.Send.Select(u => u.Minute));
        Assert.Empty(first.Retired);
        Assert.Equal(7, f.Waiting);
        f.Sent(first.Send);
        var rest = f.Sendable(M0.AddHours(1), false, 3, 3);
        Assert.Equal([M0.AddMinutes(1), M0.AddMinutes(2)], rest.Send.Select(u => u.Minute));
        Assert.Empty(rest.Retired);
        f.Sent(rest.Send);
        Assert.Equal(0, f.Waiting);

        // The first sendable unit is always taken, even over the limits.
        var over = F();
        Merge(over, Pub(10, 9, [D(9.5), D(9.6, remote: "9.9.9.9")])); over.FreezeAll(); // 2 records + 1 run
        Assert.Equal([M0], over.Sendable(M0.AddHours(1), false, 1, 1).Send.Select(u => u.Minute));
    }

    [Fact] public void Full_payload_still_retires_later_units_in_the_same_pass()
    {
        var f = F();
        Merge(f, Pub(10, 9, [D(9.5), D(9.6, remote: "9.9.9.9")])); f.FreezeAll();    // minute 0: 2 records + 1 run, latest M0+10 s
        Merge(f, Pub(70, 69, [D(69.5), D(69.6, remote: "9.9.9.9")])); f.FreezeAll(); // minute 1: 2 records + 1 run, latest M0+70 s; does not fit
        Merge(f, Pub(200, 199, [D(199.5)])); f.FreezeAll();                           // minute 3: latest M0+200 s, 128 s ahead
        var result = f.Sendable(M0.AddSeconds(70), false, 3, 3);
        Assert.Equal([M0], result.Send.Select(u => u.Minute));
        Assert.Equal([M0.AddMinutes(3)], result.Retired.Select(u => u.Minute));
        Assert.Equal(6, f.Waiting);
    }

    [Fact] public void Clock_check_holds_up_to_sixty_seconds_then_retires()
    {
        var f = F();
        Merge(f, Pub(10, 9, [D(9.5)])); f.FreezeAll();    // latest time M0+10 s
        Merge(f, Pub(130, 129, [D(129.5)])); f.FreezeAll(); // latest time M0+130 s
        // now + 2 s = M0+9 s: the first unit is 1 s ahead and held; the second is 121 s ahead and retired.
        var early = f.Sendable(M0.AddSeconds(7), false);
        Assert.Empty(early.Send);
        Assert.Equal([M0.AddMinutes(2)], early.Retired.Select(u => u.Minute));
        Assert.Equal(2, f.Waiting);
        Assert.Equal([M0], f.Sendable(M0.AddSeconds(8), false).Send.Select(u => u.Minute));
        Assert.Equal([M0], f.Sendable(M0.AddSeconds(7), true).Retired.Select(u => u.Minute));
        Assert.Equal(0, f.Waiting);

        // Boundary: latest M0+10 s, so excess = M0+8 s - now. Exactly ClockHold is held; one tick more is retired.
        var edge = F();
        Merge(edge, Pub(10, 9, [D(9.5)])); edge.FreezeAll();
        var atHold = M0.AddSeconds(8) - ConnectionMinuteFolder.ClockHold;
        var held = edge.Sendable(atHold, false);
        Assert.Empty(held.Send); Assert.Empty(held.Retired); Assert.Equal(2, edge.Waiting);
        Assert.Equal([M0], edge.Sendable(atHold.AddTicks(-1), false).Retired.Select(u => u.Minute));
        Assert.Equal(0, edge.Waiting);

        var dropped = F();
        Merge(dropped, Pub(10, 9, [D(9.5)])); dropped.FreezeAll();
        Merge(dropped, Pub(70, 69, [])); dropped.FreezeAll();
        Assert.Equal([M0, M0.AddMinutes(1)], dropped.DropWaiting().Select(u => u.Minute));
        Assert.Equal(0, dropped.Waiting);
        Assert.Empty(dropped.DropWaiting());
    }

    [Fact] public void Origin_change_keeps_two_entries_in_one_minute()
    {
        var f = F();
        Merge(f, Pub(10, 9, [D(9.5)]), origin: new(1, 2));
        Merge(f, Pub(11, 10, [D(10.5)]), origin: new(3, 4));
        f.FreezeAll();
        Assert.Equal([new GeoPoint(1, 2), new GeoPoint(3, 4)], Units(f).SelectMany(u => u.Records).Select(r => r.Origin).OrderBy(o => o!.Latitude));
    }

    [Fact] public void Location_is_the_cache_value_at_freeze_else_the_last_known()
    {
        var hit = F();
        Merge(hit, Pub(10, 9, [D(9.5)]));
        Cache["8.8.8.8"] = L1; hit.FreezeAll();
        Assert.Equal(L1, Units(hit)[0].Records[0].Location);
        Cache.Clear();

        var nul = F();
        Cache["8.8.8.8"] = L1; Merge(nul, Pub(10, 9, [D(9.5)]));
        Cache["8.8.8.8"] = null; nul.FreezeAll();
        Assert.Null(Units(nul)[0].Records[0].Location);
        Cache.Clear();

        var miss = F();
        Cache["8.8.8.8"] = L1; Merge(miss, Pub(10, 9, [D(9.5)]));
        Cache.Clear(); miss.FreezeAll();
        Assert.Equal(L1, Units(miss)[0].Records[0].Location);
    }

    [Fact] public void Frozen_unit_never_changes()
    {
        var f = F();
        Merge(f, Pub(30, 29, [D(29.5)]));
        Merge(f, Pub(63, 62, []));
        var u = Units(f)[0];
        var (records, coverage, obs) = (u.Records.ToArray(), u.Coverage.ToArray(), u.Observations);
        Merge(f, Pub(64, 63, [D(59.9), D(63.5)]));
        Merge(f, Pub(126, 125, [D(125.5)]));
        var again = Units(f).Single(x => x.BatchId == u.BatchId);
        Assert.Same(u, again);
        Assert.Equal(records, again.Records);
        Assert.Equal(coverage, again.Coverage);
        Assert.Equal(obs, again.Observations);
    }

    [Fact] public void Batch_ids_are_unique_and_fit_the_store_bound()
    {
        var last = new DateTimeOffset(DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.MaxValue.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
        string segment = new('s', 128);
        string id = ConnectionMinuteFolder.BatchId(segment, last, long.MaxValue, long.MaxValue);
        Assert.True(id.Length <= 192, $"{id.Length} characters");
        var delta = new ConnectionDelta(M0, new(new(10, 1, "app.exe"), "192.168.1.5", 100, "8.8.8.8", 443, ConnectionProtocol.Tcp), 10, 0, M0.AddSeconds(1), M0.AddSeconds(2));
        var store = Store();
        store.Append([new(id, segment, delta, null, null, true)], [new(id, segment, M0, M0.AddSeconds(10), true)], M0.AddMinutes(1));
        Assert.Equal(10, Assert.Single(store.Read(new(M0, M0.AddMinutes(1))).Rows).ReceivedBytes);

        var f = F();
        for (int i = 0; i < 10_000; i++) { Merge(f, Pub(i * 60 + 10, i * 60 + 9, [D(i * 60 + 9.5)])); f.FreezeAll(); }
        var ids = f.Sendable(M0.AddDays(8), false).Send.Select(u => u.BatchId).ToArray();
        Assert.Equal(10_000, ids.Distinct().Count());
        Assert.DoesNotContain(ids, x => Regex.IsMatch(x, @"^s:\d+:\d+$"));
    }

    [Fact] public void FreezeAll_and_Held_count_entries_and_runs()
    {
        var f = F();
        Merge(f, Pub(10, 9, [D(9.5), D(9.6, remote: "9.9.9.9")]));
        Merge(f, Pub(11, 10, []), complete: false);
        Assert.Equal(4, f.Held);
        Assert.Equal(1, f.FreezeAll());
        Assert.Equal((0, 4), (f.Held, f.Waiting));
    }

    [Fact] public void Polling_interval_change_mid_minute_keeps_one_row_and_contiguous_coverage()
    {
        var f = F();
        Merge(f, Pub(1, null, [D(0.5)]));
        for (int t = 2; t <= 30; t++) Merge(f, Pub(t, t - 1, [D(t - 0.5)]));
        foreach (int t in new[] { 40, 50, 60, 70 }) Assert.Equal(0, Merge(f, Pub(t, t - 10, [D(Math.Min(t - 0.5, 59.5))])));
        Assert.Equal(1, Merge(f, Pub(80, 70, [])));
        var u = Units(f).Single(x => x.Minute == M0);
        Assert.Single(u.Records);
        var c = Assert.Single(u.Coverage);
        Assert.Equal((M0.AddSeconds(1), M0.AddMinutes(1), true), (c.From, c.To, c.Complete));
    }

    [Fact] public void Pressure_threshold_below_one_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => F(0));
    }

    [Fact] public void Forget_removes_one_app_or_every_entry_and_keeps_coverage(){ var f=F();
        Merge(f,Pub(10,9,[D(9.5,name:"Chrome.exe"),D(9.6,name:"other.exe",remote:"9.9.9.9")]));Merge(f,Pub(11,10,[D(10.5,name:"CHROME.EXE",localPort:200)]));int held=f.Held;
        var frozen=F();Merge(frozen,Pub(30,29,[D(29.5,name:"chrome.exe")]));Merge(frozen,Pub(63,62,[]));var before=Units(frozen)[0];Assert.Equal(0,frozen.Forget("CHROME.EXE"));Assert.Same(before,Units(frozen)[0]);
        Assert.Equal(2,f.Forget("CHROME.EXE"));Assert.Equal(held-2,f.Held);   // "Chrome.exe" and "CHROME.EXE" are two entries of one key
        f.FreezeAll();var u=Assert.Single(Units(f));Assert.Equal(["other.exe"],u.Records.Select(r=>r.Delta.Peer.Process.Name));
        Assert.Equal((M0.AddSeconds(9),M0.AddSeconds(11)),(u.Coverage[0].From,u.Coverage[^1].To));
        var all=F();Merge(all,Pub(10,null,[D(9.5)]));Assert.Equal(1,all.Forget(null));Assert.Equal(0,all.Held);all.FreezeAll();Assert.Empty(Units(all));}   // the unit had no run: removed

    [Fact] public void RetireWaiting_removes_and_returns_waiting_units_in_scope_and_lowers_Waiting()
    {
        var f=F(); Merge(f,Pub(30,29,[D(29.5,name:"chrome.exe")])); Merge(f,Pub(40,39,[D(39.5,name:"other.exe",remote:"9.9.9.9")],segment:"t"),generation:1);
        f.FreezeAll(); int waitingBefore=f.Waiting; Assert.True(waitingBefore>=4);
        var retired=f.RetireWaiting(r=>r.Delta.Peer.Process.Name=="chrome.exe");
        var unit=Assert.Single(retired); Assert.Equal("s",unit.Segment); Assert.Equal(waitingBefore-unit.Size,f.Waiting);
        Assert.Equal("t",Assert.Single(Units(f)).Segment);
    }
}
