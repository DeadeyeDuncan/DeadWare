using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class FirstUseTrackerTests
{
    private static readonly TimeZoneInfo Z = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset S = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset E = FirstNetworkUse.LearningEnd(S, Z);
    private static readonly AlertRule R = AlertRules.DefaultFirstNetworkUse(S);

    private static FirstUseTracker Tr(AlertRule? rule = null, IEnumerable<string>? ignored = null, IEnumerable<string>? excluded = null, bool noRule = false,
        FirstUseState? state = null)
    {
        var t = new FirstUseTracker(Z, TimeSpan.FromSeconds(60));
        t.Configure(noRule ? null : rule ?? R, ignored ?? [], excluded ?? []);
        t.Restore(state ?? new FirstUseState(null, null, null, null, []));
        return t;
    }
    private static FirstUseState Learned(IReadOnlyList<KnownApplication> known) => new(null, null, S, E, known);
    private static NetworkUse U(string name, DateTimeOffset at) => new(name, at);
    private static IReadOnlyList<AlertEpisode> Obs(FirstUseTracker t, DateTimeOffset at, params NetworkUse[] u) => t.Observe(at, u, at);
    private static IReadOnlyList<KnownApplication> Ups(FirstUseTracker t) => t.TakeChanges(1024).Upserts;
    private static List<KnownApplication> Keys(int count, DateTimeOffset last) =>
        Enumerable.Range(0, count).Select(i => new KnownApplication($"K{i:D4}", last.AddDays(-1), last.AddMinutes(i))).ToList();

    [Fact]
    public void Apps_seen_during_learning_are_learned_quietly_and_after_learning_alert_once()
    {
        var t = Tr();
        Assert.Empty(Obs(t, S, U("a.exe", S)));
        Assert.Empty(Obs(t, E.AddMinutes(-1), U("b.exe", E.AddMinutes(-1))));
        var e = Assert.Single(Obs(t, E.AddMinutes(5), U("c.exe", E.AddMinutes(4.5))));
        Assert.Equal((AlertKind.FirstNetworkUse, E.AddMinutes(4.5), (DateTimeOffset?)E.AddMinutes(4.5), 0d, "c.exe", "c.exe used the network for the first time", "first-network-use"),
            (e.Kind, e.Start, e.End, e.Peak, e.Application, e.Summary, e.RuleId));
        Assert.Equal(32, e.Id.Length);
        Assert.DoesNotContain(Ups(t), k => k.Key == "C.EXE");
        Assert.Empty(Obs(t, E.AddMinutes(9), U("C.exe", E.AddMinutes(9))));
        var c = Assert.Single(Ups(t), k => k.Key == "C.EXE");
        Assert.Equal((E.AddMinutes(4), E.AddMinutes(9)), (c.First, c.Last));
    }

    [Fact]
    public void Seeded_names_merge_by_key_never_alert_never_evict_and_a_failed_store_stays_unseeded()
    {
        var t = Tr(excluded: ["ex.exe"]); Obs(t, S); var now = E.AddDays(1);
        Assert.True(t.NeedsSeed(FirstUseSource.Usage));
        t.Seed(FirstUseSource.Usage, [new("chrome.exe", now.AddDays(-9), now.AddDays(-2)), new("Chrome.exe", now.AddDays(-10), now.AddDays(-3)), new("PID 4", now, now),
            new("Unknown application", now, now), new("old.exe", now.AddDays(-500), now.AddDays(-401)), new("ex.exe", now, now)], now);
        var c = t.TakeChanges(1024); var k = Assert.Single(c.Upserts);
        Assert.Equal(("CHROME.EXE", now.AddDays(-10), now.AddDays(-2)), (k.Key, k.First, k.Last));
        Assert.Equal(now, c.Metadata.SeededUsage);
        Assert.False(t.NeedsSeed(FirstUseSource.Usage));
        Assert.Empty(Obs(t, now, U("chrome.exe", now)));
        Assert.Single(Obs(t, now, U("new.exe", now)));
        t.Seed(FirstUseSource.Connections, null, now); Assert.True(t.NeedsSeed(FirstUseSource.Connections)); Assert.False(t.Status.Seeded);
        t.Seed(FirstUseSource.Connections, [], now); Assert.False(t.NeedsSeed(FirstUseSource.Connections)); Assert.True(t.Status.Seeded);

        var known = Keys(10, now.AddDays(-300));
        var big = Tr(state: new FirstUseState(null, null, S, E, known));
        var names = Enumerable.Range(0, 8200).Select(i => new ApplicationSeen($"s{i:D4}.exe", now.AddDays(-30), now.AddMinutes(-i))).ToList();
        big.Seed(FirstUseSource.Usage, names, now);
        Assert.Equal(8192, big.Status.KnownApps);
        var taken = big.TakeChanges(16384);
        Assert.Empty(taken.Deletes);
        Assert.Equal(8182, taken.Upserts.Count);
        Assert.Equal(Enumerable.Range(0, 8182).Select(i => $"S{i:D4}.EXE").Order(StringComparer.Ordinal), taken.Upserts.Select(u => u.Key).Order(StringComparer.Ordinal));
        foreach (var key in known) Assert.Empty(Obs(big, now, U(key.Key, now)));
    }

    [Fact]
    public void Learning_starts_at_the_first_available_capture_and_survives_rule_switch_removal_and_re_adding()
    {
        var t = Tr(); Assert.Null(t.Status.LearningStart);
        Obs(t, S); var start = t.Status.LearningStart; Assert.Equal(S, start);
        t.Configure(R with { Id = "other" }, [], []); t.Configure(R with { Enabled = false }, [], []); t.Configure(null, [], []); t.Configure(R, [], []);
        Obs(t, S.AddHours(1));
        Assert.Equal((start, E), (t.Status.LearningStart, t.Status.LearningEnd));
        var m = t.TakeChanges(1024).Metadata;
        Assert.Equal((S, E), (m.LearningStart, m.LearningEnd));
        Assert.Null(t.TakeChanges(1024).Metadata.LearningStart);
    }

    [Fact]
    public void A_switched_off_rule_learns_quietly_and_no_rule_learns_nothing()
    {
        var off = Tr(R with { Enabled = false });
        Assert.Empty(Obs(off, E.AddHours(1), U("a.exe", E.AddHours(1))));
        Assert.Single(Ups(off));
        var none = Tr(noRule: true);
        Assert.Empty(Obs(none, E.AddHours(1), U("a.exe", E.AddHours(1))));
        Assert.Empty(none.TakeChanges(1024).Upserts);
        Assert.Null(none.Status.LearningStart);
        Assert.Equal(0, none.Status.KnownApps);
    }

    [Fact]
    public void Excluded_pid_and_unknown_names_are_never_learned_or_alerted_and_dont_alert_names_are_learned_without_an_episode()
    {
        var t = Tr(ignored: ["quiet.exe"], excluded: ["ex.exe"]); Obs(t, S);
        Assert.Empty(Obs(t, E.AddHours(1), U("PID 4", E), U("Unknown application", E), U("ex.exe", E), U("quiet.exe", E)));
        Assert.Equal(["QUIET.EXE"], Ups(t).Select(k => k.Key));
        t.Configure(R, ["quiet.exe"], []);
        Assert.Single(Obs(t, E.AddHours(2), U("ex.exe", E.AddHours(2))));

        Assert.Empty(Obs(t, S.AddHours(1), U("known.exe", S.AddHours(1))));
        Ups(t);
        t.Configure(R, [], ["KNOWN.EXE"]);
        Assert.Empty(Obs(t, E.AddHours(3), U("known.exe", E.AddHours(3))));
        Assert.Empty(Ups(t));
        t.Configure(R, [], []);
        Assert.Empty(Obs(t, E.AddHours(4), U("known.exe", E.AddHours(4))));
        var k = Assert.Single(Ups(t));
        Assert.Equal(("KNOWN.EXE", S.AddHours(1), E.AddHours(4)), (k.Key, k.First, k.Last));
    }

    [Fact]
    public void Apps_within_60_seconds_of_the_first_share_one_group_and_an_app_at_the_due_time_opens_the_next()
    {
        var t = Tr(); Obs(t, S); var w = E.AddHours(1);
        Assert.Null(t.GroupDue);
        t.Observe(w, [U("a.exe", w)], w); t.Observe(w, [U("b.exe", w)], w.AddMilliseconds(59_999)); t.Observe(w, [U("c.exe", w)], w.AddSeconds(60));
        Assert.Equal(w.AddSeconds(60), t.GroupDue);
        Assert.Null(t.TakeDueGroup(w.AddSeconds(59)));
        var g = t.TakeDueGroup(w.AddSeconds(60))!;
        Assert.Equal((w, 2), (g.First, g.Count)); Assert.Equal(["a.exe", "b.exe"], g.Names);
        Assert.Equal(w.AddSeconds(120), t.GroupDue);

        // At close, entries now ignored, excluded or no longer known are dropped; a group emptied that way returns null.
        var w2 = w.AddMinutes(10);
        t.Observe(w2, [U("d.exe", w2.AddSeconds(5)), U("e.exe", w2.AddSeconds(3)), U("f.exe", w2.AddSeconds(4))], w2);
        t.Configure(R, ["c.exe", "d.exe"], ["e.exe"]);
        Assert.Null(t.TakeDueGroup(w.AddSeconds(120)));
        t.Forget("F.EXE", w2);
        Assert.Null(t.TakeDueGroup(w2.AddSeconds(60)));
        Assert.Null(t.GroupDue);

        t.Configure(R, [], []);
        var w3 = w.AddMinutes(20);
        t.Observe(w3, [U("g.exe", w3.AddSeconds(-5)), U("h.exe", w3.AddSeconds(-8))], w3);
        var g3 = t.TakeDueGroup(w3.AddMinutes(5))!;
        Assert.Equal((w3.AddSeconds(-8), 2), (g3.First, g3.Count)); Assert.Equal(["g.exe", "h.exe"], g3.Names);

        // With the rule switched off it returns null.
        t.Observe(w3, [U("i.exe", w3)], w3);
        t.Configure(R with { Enabled = false }, [], []);
        Assert.Null(t.TakeDueGroup(w3.AddMinutes(1)));
        t.Configure(R, [], []);

        // 70 apps in one group: Names holds the first 64 in detection order and Count is 70.
        var w4 = w.AddMinutes(30);
        t.Observe(w4, Enumerable.Range(0, 70).Select(i => U($"n{i:D2}.exe", w4)).ToArray(), w4);
        var g4 = t.TakeDueGroup(w4.AddMinutes(1))!;
        Assert.Equal(70, g4.Count);
        Assert.Equal(Enumerable.Range(0, 64).Select(i => $"n{i:D2}.exe"), g4.Names);
    }

    [Fact]
    public void Forgetting_one_key_or_every_key_makes_the_next_use_alert_keeps_the_learning_start_and_leaves_the_open_group()
    {
        var t = Tr(state: new FirstUseState(S, S, S, E, [])); var w = E.AddHours(1);
        Assert.Equal(2, t.Observe(w, [U("a.exe", w), U("b.exe", w)], w).Count);
        var n = w.AddSeconds(10);
        t.Forget("A.EXE", n);
        Assert.Equal(1, t.Status.KnownApps);
        var again = Assert.Single(t.Observe(n, [U("a.exe", w)], n.AddSeconds(1)));
        Assert.Equal(n, again.Start);
        Assert.Empty(t.Observe(n, [U("a.exe", n)], n.AddSeconds(2)));
        var g = t.TakeDueGroup(w.AddSeconds(60))!;
        Assert.Equal(["b.exe", "a.exe"], g.Names); Assert.Equal(2, g.Count);
        Assert.Equal(w, g.First);

        // A pending upsert of A.EXE is dropped and a pending delete stays.
        var u = Tr(ignored: ["a.exe"], state: Learned(Keys(8191, S.AddDays(-1))));
        Obs(u, w, U("a.exe", w));
        Obs(u, w, U("z.exe", w));
        u.Forget("A.EXE", w);
        u.Forget("Z.EXE", w);
        var c = u.TakeChanges(1024);
        Assert.Equal(["K0000"], c.Deletes);
        Assert.Empty(c.Upserts);

        t.Forget(null, n);
        Assert.Equal(0, t.Status.KnownApps);
        Assert.Equal((S, E, true), (t.Status.LearningStart, t.Status.LearningEnd, t.Status.Seeded));
        Assert.False(t.NeedsSeed(FirstUseSource.Usage)); Assert.False(t.NeedsSeed(FirstUseSource.Connections));
        Assert.Single(Obs(t, w.AddHours(1), U("b.exe", w.AddHours(1))));
    }

    [Fact]
    public void A_full_known_list_forgets_the_app_seen_least_recently_to_make_room()
    {
        var old = S.AddDays(-30);
        var known = Keys(8192, S.AddDays(-10));
        known[1] = known[1] with { Last = old }; known[0] = known[0] with { Last = old };
        known.Reverse();
        var t = Tr(state: Learned(known));
        var w = E.AddHours(1);
        Assert.Single(Obs(t, w, U("new.exe", w)));
        var c = t.TakeChanges(1024);
        Assert.Equal(["K0000"], c.Deletes);
        Assert.Empty(c.Upserts);
        Assert.Equal(8192, t.Status.KnownApps);
        Assert.Empty(Obs(t, w, U("K0001", w)));
    }

    [Fact]
    public void Apps_unseen_for_400_days_are_forgotten_at_the_next_expire_and_alert_again_in_the_same_session()
    {
        var n = E.AddDays(500); var old = n.AddDays(-400).AddMinutes(-1);
        var t = Tr(ignored: ["old.exe"], state: Learned([new("KEEP.EXE", n.AddDays(-400), n.AddDays(-400))]));
        Assert.Empty(Obs(t, old, U("old.exe", old)));
        t.Configure(R, [], []);
        t.Expire(n);
        Assert.Equal(1, t.Status.KnownApps);
        Assert.Empty(t.TakeChanges(1024).Upserts);
        Assert.Single(Obs(t, n, U("old.exe", n)));
        Assert.Empty(Obs(t, n, U("keep.exe", n)));
    }

    [Fact]
    public void Changes_are_taken_in_bounded_batches_and_a_key_never_has_both_an_upsert_and_a_delete()
    {
        var now = E.AddDays(1);
        var t = Tr();
        t.Seed(FirstUseSource.Usage, Enumerable.Range(0, 3000).Select(i => new ApplicationSeen($"s{i}.exe", now.AddDays(-1), now.AddMinutes(-i))).ToList(), now);
        Assert.True(t.HasChanges);
        var a = t.TakeChanges(1024); var b = t.TakeChanges(1024); var c = t.TakeChanges(1024);
        Assert.Equal((1024, 1024, 952), (a.Upserts.Count, b.Upserts.Count, c.Upserts.Count));
        Assert.Equal((null, null, now), (a.Metadata.SeededUsage, b.Metadata.SeededUsage, c.Metadata.SeededUsage));
        Assert.False(t.HasChanges);
        Assert.Equal(3000, a.Upserts.Concat(b.Upserts).Concat(c.Upserts).Select(k => k.Key).Distinct().Count());

        // A key upserted then forgotten by the list cap appears only as a delete.
        var during = S.AddHours(1);
        var u = Tr(state: Learned(Keys(8191, S)));
        Obs(u, during, U("x.exe", S.AddDays(-100)));
        Obs(u, during, U("y.exe", during));
        var d = u.TakeChanges(1);
        Assert.Equal(["X.EXE"], d.Deletes);
        Assert.Equal(["Y.EXE"], d.Upserts.Select(k => k.Key));
        Assert.False(u.HasChanges);

        // Forgotten then seen again, only as an upsert.
        Obs(u, during, U("v.exe", S.AddDays(-200)));
        Obs(u, during, U("w.exe", during));
        Obs(u, during, U("v.exe", during));
        var e = u.TakeChanges(1024);
        Assert.Equal(["V.EXE", "W.EXE"], e.Upserts.Select(k => k.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["K0000", "K0001"], e.Deletes.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("V.EXE", e.Deletes);
    }
}
