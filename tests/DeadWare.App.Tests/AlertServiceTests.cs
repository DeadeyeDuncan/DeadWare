using System.IO;
using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class AlertServiceTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    // Ten minutes ago, but never before today's UTC midnight: the application-budget test counts a local (UTC) day.
    private static readonly DateTimeOffset Start = Later(UsageMetrics.Floor(DateTimeOffset.UtcNow.AddMinutes(-10)), new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero));
    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static AlertRule Rule(AlertKind kind, string target, double threshold, int minutes = 1, string id = "rule-1")
        => new(id, kind, target, threshold, minutes, true, Start);
    private static Reading Reading(DateTimeOffset stamp, double seconds, double cpu = 99, BatteryReading? battery = null, IReadOnlyList<ProcessTraffic>? processes = null)
        => new(new Snapshot(stamp, seconds, new Dictionary<string, double?>
            { ["cpu/usage"] = cpu, ["memory/usage"] = 50, ["network/download"] = 1000, ["network/upload"] = 100 }),
            [], [], processes ?? [new ProcessTraffic(99, 1, "fixture.exe", null, null, 0, 0)], "CPU", "", "", "", false)
        { Battery = battery ?? BatteryReading.Unavailable };
    private static (UsageHistoryService Usage, AlertService Alerts) Services(Fixture fixture)
    {
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        var alerts = new AlertService(fixture.AlertPath, "tablet", usage, () => null, flushInterval: TimeSpan.FromMilliseconds(30), localZone: TimeZoneInfo.Utc);
        return (usage, alerts);
    }
    private static UsageHistoryStore UsageStore(UsageHistoryService usage)
    {
        var store = (UsageHistoryStore)typeof(UsageHistoryService).GetField("store", Fields)!.GetValue(usage)!;
        store.Initialize(); return store;
    }
    /// <summary>Publishes one reading per second and lets the usage worker drain its 120-slot queue every 50 readings, so none is dropped.</summary>
    private static async Task Feed(UsageHistoryService usage, int fromSecond, int toSecond, double cpu = 99)
    {
        for (int i = fromSecond; i < toSecond; i++)
        {
            usage.TryRecord(Reading(Start.AddSeconds(i), i, cpu));
            if ((i - fromSecond) % 50 == 49) await Until(() => Task.FromResult(usage.PendingSamples == 0));
        }
        await Until(() => Task.FromResult(usage.PendingSamples == 0));
        Assert.Equal(0, usage.DroppedSamples);
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        while (!await condition()) await Task.Delay(25, deadline.Token);
    }
    private static async Task<AlertLogView> Log(AlertService alerts) => await alerts.ReadEpisodesAsync(Start.AddDays(-1), Start.AddDays(1));

    [Fact]
    public async Task A_closed_usage_minute_reaches_the_evaluator_and_the_episode_is_logged_and_notified()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        alerts.Configure([Rule(AlertKind.Metric, "cpu", 90)], true);
        var notified = new TaskCompletionSource<AlertEpisode>(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.EpisodeStarted += episode => notified.TrySetResult(episode);
        usage.Start(); alerts.Start();
        try
        {
            await Feed(usage, 0, 61);
            var episode = await notified.Task.WaitAsync(Timeout);
            Assert.Equal("CPU usage ≥ 90% for 1 minute", episode.Summary); Assert.Equal(Start, episode.Start); Assert.Null(episode.End);
            await alerts.StopAsync(); await usage.StopAsync();
            var saved = Assert.Single((await Log(alerts)).Episodes);
            Assert.Equal(episode.Id, saved.Id); Assert.Null(saved.End); Assert.False(saved.Acknowledged);
            Assert.True(await alerts.AcknowledgeAsync(saved.Id));
            Assert.True(Assert.Single((await Log(alerts)).Episodes).Acknowledged);
            Assert.Equal(AlertService.SavedStatus, alerts.Status); Assert.False(alerts.Failed);
            Assert.Equal(0, alerts.DroppedObservations);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Muted_notifications_still_log_and_a_throwing_notifier_never_fails_the_service()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        alerts.Configure([Rule(AlertKind.Metric, "cpu", 90)], false);
        int notifications = 0;
        alerts.EpisodeStarted += _ => { Interlocked.Increment(ref notifications); throw new InvalidOperationException("notifier failure"); };
        usage.Start(); alerts.Start();
        try
        {
            await Feed(usage, 0, 61);
            await Until(async () => (await Log(alerts)).Total == 1);
            Assert.Equal(0, Volatile.Read(ref notifications));
            alerts.Configure([Rule(AlertKind.Metric, "cpu", 90), Rule(AlertKind.Metric, "memory", 40, id: "ram")], true);
            await Feed(usage, 61, 121);
            await Until(async () => (await Log(alerts)).Total == 2);
            Assert.Equal(1, Volatile.Read(ref notifications));
            Assert.False(alerts.Failed); Assert.Equal(AlertService.SavedStatus, alerts.Status);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Restart_restores_the_open_episode_and_keeps_the_hour_floor_and_removed_rules_end_their_episodes()
    {
        using var fixture = new Fixture();
        var first = Services(fixture);
        first.Alerts.Configure([Rule(AlertKind.Metric, "cpu", 90)], true);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Alerts.EpisodeStarted += _ => notified.TrySetResult();
        first.Usage.Start(); first.Alerts.Start();
        await Feed(first.Usage, 0, 61);
        await notified.Task.WaitAsync(Timeout);
        await first.Alerts.StopAsync(); await first.Usage.StopAsync();
        var second = Services(fixture);
        second.Alerts.Configure([Rule(AlertKind.Metric, "cpu", 90)], true);
        int notifications = 0; second.Alerts.EpisodeStarted += _ => Interlocked.Increment(ref notifications);
        second.Usage.Start(); second.Alerts.Start();
        try
        {
            await Feed(second.Usage, 60, 120, cpu: 10); await Feed(second.Usage, 120, 181, cpu: 99);
            await Until(async () => (await Log(second.Alerts)).Total == 2);
            var log = await Log(second.Alerts);
            Assert.NotNull(log.Episodes[1].End); Assert.Null(log.Episodes[0].End);
            Assert.Equal(Start.AddMinutes(2), log.Episodes[0].Start);
            Assert.Equal(0, Volatile.Read(ref notifications));
            second.Alerts.Configure([], true);
            await Until(async () => (await Log(second.Alerts)).Episodes.All(e => e.End is not null));
        }
        finally { await second.Alerts.StopAsync(); await second.Usage.StopAsync(); }
    }

    [Fact]
    public async Task Battery_readings_fire_immediately_and_only_changes_are_queued()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        alerts.Configure([Rule(AlertKind.Battery, "", 20)], true);
        var notified = new TaskCompletionSource<AlertEpisode>(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.EpisodeStarted += episode => notified.TrySetResult(episode);
        usage.Start(); alerts.Start();
        try
        {
            alerts.TryRecordBattery(Reading(Start, 0, battery: new(25, BatteryPowerState.OnBattery, false)));
            for (int i = 1; i < 5; i++) alerts.TryRecordBattery(Reading(Start.AddSeconds(i), i, battery: new(15, BatteryPowerState.OnBattery, false)));
            var episode = await notified.Task.WaitAsync(Timeout);
            Assert.Equal("Battery ≤ 20% on battery", episode.Summary); Assert.Equal(Start.AddSeconds(1), episode.Start); Assert.Equal(15, episode.Peak);
            alerts.TryRecordBattery(Reading(Start.AddSeconds(5), 5, battery: new(15, BatteryPowerState.Charging, false)));
            await Until(async () => (await Log(alerts)).Episodes.FirstOrDefault()?.End is not null);
            Assert.Equal(Start.AddSeconds(5), (await Log(alerts)).Episodes[0].End);
            alerts.TryRecordBattery(Reading(Start.AddSeconds(6), 6, battery: BatteryReading.Unavailable));
            Assert.Equal(0, alerts.DroppedObservations);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Application_budgets_seed_todays_total_from_the_store()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        var store = UsageStore(usage);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        var seeded = Start.AddMinutes(-1) < today ? today : Start.AddMinutes(-1);
        store.Append("tablet", [new UsageMinuteBatch(seeded, [], [new UsageApplicationMinute("Chrome.exe", seeded, 600, 300, "earlier")], false, "earlier")], DateTimeOffset.UtcNow);
        alerts.Configure([Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000)], true);
        var notified = new TaskCompletionSource<AlertEpisode>(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.EpisodeStarted += episode => notified.TrySetResult(episode);
        usage.Start(); alerts.Start();
        try
        {
            for (int i = 0; i < 61; i++)
            {
                usage.TryRecord(Reading(Start.AddSeconds(i), i, processes: [new ProcessTraffic(7, 1, "chrome.exe", null, null, 2L * i, 0)]));
                if (i % 50 == 49) await Until(() => Task.FromResult(usage.PendingSamples == 0));
            }
            var episode = await notified.Task.WaitAsync(Timeout);
            Assert.Equal("chrome.exe ≥ 1.0 KB in a day", episode.Summary);
            Assert.InRange(episode.Peak, 1000, 1100);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Suggestions_report_recorded_maximum_average_and_share_above_the_threshold()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        var store = UsageStore(usage);
        var batches = Enumerable.Range(0, 100).Select(i => new UsageMinuteBatch(Start.AddMinutes(-200 + i),
            [new UsageMetricMinute(UsageMetrics.Cpu, null, Start.AddMinutes(-200 + i), i, i, i, 60, true, "seg")],
            [new UsageApplicationMinute("steam.exe", Start.AddMinutes(-200 + i), 1000, 500, "seg")], false, "seg")).ToArray();
        store.Append("tablet", batches, DateTimeOffset.UtcNow);
        try
        {
            var cpu = await alerts.SuggestAsync(Rule(AlertKind.Metric, "cpu", 90));
            Assert.Equal(99, cpu.Week.Maximum); Assert.Equal(49.5, cpu.Week.Average!.Value, 9); Assert.Equal(0.1, cpu.Week.ShareAtOrAbove!.Value, 9);
            Assert.Equal(100, cpu.Week.Minutes); Assert.Equal(cpu.Week, cpu.Month);
            var steam = await alerts.SuggestAsync(Rule(AlertKind.ApplicationDaily, "STEAM.EXE", 1_000_000));
            Assert.Equal(150_000, steam.Week.Maximum); Assert.Equal(150_000d / 7, steam.Week.Average!.Value, 6); Assert.Null(steam.Week.ShareAtOrAbove);
            Assert.Equal(150_000d / 30, steam.Month.Average!.Value, 6);
            var battery = await alerts.SuggestAsync(Rule(AlertKind.Battery, "", 20));
            Assert.Null(battery.Week.Maximum); Assert.Equal(0, battery.Week.Minutes);
            var gpu = await alerts.SuggestAsync(Rule(AlertKind.Metric, "gpu", 50));
            Assert.Null(gpu.Month.Maximum);
            await Assert.ThrowsAsync<ArgumentException>(() => alerts.SuggestAsync(Rule(AlertKind.Metric, "gpufan", 50)));
            Assert.False(alerts.Failed);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    // First network use: a fixture alerts.db whose seed markers and learning dates lie in the past, so a new app alerts at once.
    private static readonly DateTimeOffset LongAgo = UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-10));
    private static void Store(string path)
    {
        var store = new AlertStore(path); store.Initialize();
        store.Append("tablet", [], DateTimeOffset.UtcNow, new FirstUseChanges([], [], new FirstUseMetadata(LongAgo, LongAgo, LongAgo, LongAgo.AddDays(3))));
    }
    private static ConnectionDelta Use(string name, string remote = "8.8.8.8", DateTimeOffset? firstSeen = null)
    {
        var seen = firstSeen ?? Start;
        return new(UsageMetrics.Floor(seen), new ConnectionPeer(new ProcessIdentity(42, 1, name), "192.168.1.2", 50000, remote, 443, ConnectionProtocol.Tcp), 10, 10, seen, seen);
    }
    private static Reading R(DateTimeOffset at, bool available = true, params ConnectionDelta[] deltas)
        => Reading(at, (at - Start).TotalSeconds) with
        {
            Connections = new ConnectionSample(at, (at - Start).TotalSeconds, 1, "seg", [], deltas, at, available, true, 0, 0, 0, "")
        };
    private static ConnectionDelta[] Apps(int from, int count, DateTimeOffset firstSeen)
        => Enumerable.Range(from, count).Select(i => Use($"app{i:D5}.exe", firstSeen: firstSeen)).ToArray();
    private static AlertService FirstUseService(Fixture fixture, UsageHistoryService usage, TimeSpan? window = null, TimeSpan? flush = null,
        Func<Task<IReadOnlyList<ApplicationSeen>?>>? connectionNames = null)
        => new(fixture.AlertPath, "tablet", usage, () => null, flushInterval: flush ?? TimeSpan.FromMilliseconds(30), localZone: TimeZoneInfo.Utc,
            connectionNames: connectionNames, firstUseGroupWindow: window ?? TimeSpan.FromMilliseconds(100));
    private static async Task<IReadOnlyList<AlertEpisode>> Saved(AlertService alerts, DateTimeOffset from, DateTimeOffset to)
        => (await alerts.ReadEpisodesAsync(from, to, 5000)).Episodes.Where(e => e.Kind == AlertKind.FirstNetworkUse).ToArray();
    /// <summary>Takes every queued work from a service whose worker is not running; returns the uses of each NetworkUseWork in order.</summary>
    private static List<IReadOnlyList<NetworkUse>> Drain(AlertService alerts)
    {
        var channel = typeof(AlertService).GetField("queue", Fields)!.GetValue(alerts)!;
        var reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        var tryRead = reader.GetType().GetMethod("TryRead")!;
        var works = new List<IReadOnlyList<NetworkUse>>(); var args = new object?[1];
        while ((bool)tryRead.Invoke(reader, args)!)
            if (args[0]!.GetType().Name == "NetworkUseWork") works.Add((IReadOnlyList<NetworkUse>)args[0]!.GetType().GetProperty("Uses")!.GetValue(args[0])!);
        return works;
    }

    [Fact]
    public async Task Network_use_is_posted_from_the_collector_thread_once_per_app_per_minute()
    {
        using var fixture = new Fixture(); Store(fixture.AlertPath);
        var rule = AlertRules.DefaultFirstNetworkUse(Start);
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));

        // Detection alone: a service that accepts work but runs no worker keeps every posted work in its queue.
        var probe = FirstUseService(fixture, usage);
        typeof(AlertService).GetField("accepting", Fields)!.SetValue(probe, true);
        typeof(AlertService).GetField("learningStarted", Fields)!.SetValue(probe, true);
        probe.Configure([rule], true, null, ["blocked.exe"]);
        Assert.Equal(1, probe.PendingObservations); Assert.Empty(Drain(probe));
        probe.TryRecordNetworkUse(R(Start, false, Use("a.exe")));
        Assert.Equal(0, probe.PendingObservations);
        probe.Configure([], true, null, ["blocked.exe"]); Drain(probe);
        probe.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
        Assert.Equal(0, probe.PendingObservations);
        probe.Configure([rule], true, null, ["blocked.exe"]); Drain(probe);
        probe.TryRecordNetworkUse(R(Start, true, Use("lo.exe", "127.0.0.2"), Use("lo6.exe", "::1")));
        probe.TryRecordNetworkUse(R(Start, true, Use("blocked.exe"), Use("Blocked.EXE"), Use("PID 4"), Use("")));
        Assert.Equal(0, probe.PendingObservations);
        probe.TryRecordNetworkUse(R(Start, true, Use("a.exe", firstSeen: Start.AddSeconds(5)), Use("A.exe", firstSeen: Start.AddSeconds(2)), Use("lo.exe", "127.0.0.2")));
        var use = Assert.Single(Assert.Single(Drain(probe)));
        Assert.Equal("a.exe", use.Name); Assert.Equal(Start.AddSeconds(2), use.FirstSeen);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(10), true, Use("a.exe")));
        Assert.Equal(0, probe.PendingObservations);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(60), true, Use("a.exe"), Use("b.exe")));
        Assert.Equal(["a.exe", "b.exe"], Assert.Single(Drain(probe)).Select(u => u.Name));
        probe.TryRecordNetworkUse(R(Start.AddSeconds(70), true, Use("a.exe")));
        Assert.Equal(0, probe.PendingObservations);
        var generation = typeof(AlertService).GetField("knownGeneration", Fields)!;
        generation.SetValue(probe, (long)generation.GetValue(probe)! + 1);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(80), true, Use("a.exe")));
        Assert.Equal("a.exe", Assert.Single(Assert.Single(Drain(probe))).Name);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(120), true, Apps(0, 1100, Start.AddSeconds(120))));
        Assert.Equal(1024, Assert.Single(Drain(probe)).Count);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(125), true, Apps(0, 1100, Start.AddSeconds(120))));
        Assert.Equal(Enumerable.Range(1024, 76).Select(i => $"app{i:D5}.exe"), Assert.Single(Drain(probe)).Select(u => u.Name));
        // Before the worker records a learning start, the first poll of a minute posts the "capture has run" signal even with no app.
        typeof(AlertService).GetField("learningStarted", Fields)!.SetValue(probe, false);
        probe.TryRecordNetworkUse(R(Start.AddSeconds(180), true, Use("lo.exe", "127.0.0.2")));
        Assert.Empty(Assert.Single(Drain(probe)));
        probe.TryRecordNetworkUse(R(Start.AddSeconds(190), true, Use("lo.exe", "127.0.0.2")));
        Assert.Equal(0, probe.PendingObservations);

        // The same polls through a running worker: each app is saved once, as a closed kind-4 episode.
        var alerts = FirstUseService(fixture, usage);
        alerts.Configure([rule], true, null, ["blocked.exe"]);
        int notified = 0; alerts.EpisodeStarted += _ => Interlocked.Increment(ref notified);
        alerts.Start();
        try
        {
            alerts.TryRecordNetworkUse(R(Start, false, Use("hidden.exe")));
            alerts.TryRecordNetworkUse(R(Start, true, Use("lo.exe", "127.0.0.2"), Use("lo6.exe", "::1"), Use("blocked.exe")));
            alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(10), true, Use("a.exe")));
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(60), true, Use("a.exe"), Use("b.exe")));
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(120), true, Apps(0, 1100, Start.AddSeconds(120))));
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(125), true, Apps(0, 1100, Start.AddSeconds(120))));
            await Until(async () => (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Count == 1102 || alerts.Failed);
            await alerts.StopAsync();
            var saved = await Saved(alerts, Start.AddDays(-1), Start.AddDays(1));
            Assert.Equal(1102, saved.Count);
            string[] expected = ["a.exe", "b.exe", .. Enumerable.Range(0, 1100).Select(i => $"app{i:D5}.exe")];
            Assert.Equal(expected.Order(StringComparer.Ordinal), saved.Select(e => e.Application!).Order(StringComparer.Ordinal));
            Assert.All(saved, e => { Assert.Equal(e.Start, e.End); Assert.Equal(rule.Id, e.RuleId); });
            Assert.Equal(Start, saved.Single(e => e.Application == "a.exe").Start);
            Assert.Equal(0, Volatile.Read(ref notified));
            Assert.False(alerts.Failed); Assert.Equal(0, alerts.DroppedObservations);
            Assert.Equal(1102, alerts.FirstUse.KnownApps);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task A_work_with_1024_new_apps_saves_in_batches_of_at_most_1024_and_never_fails_the_log()
    {
        using var fixture = new Fixture(); Store(fixture.AlertPath);
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        var alerts = FirstUseService(fixture, usage);
        alerts.Configure([AlertRules.DefaultFirstNetworkUse(Start)], true);
        alerts.Start();
        try
        {
            // Ten polls, each in its own minute, posted faster than the worker reads them.
            for (int poll = 0; poll < 10; poll++)
                alerts.TryRecordNetworkUse(R(Start.AddMinutes(poll), true, Apps(poll * 1024, 1024, Start.AddMinutes(poll))));
            await Until(async () => (await alerts.ReadEpisodesAsync(Start.AddDays(-1), Start.AddDays(1), 1)).Total == 10 * 1024 || alerts.Failed);
            await alerts.StopAsync();
            Assert.False(alerts.Failed); Assert.Equal(AlertService.SavedStatus, alerts.Status); Assert.Equal(0, alerts.DroppedObservations);
            for (int poll = 0; poll < 10; poll++)
            {
                var saved = await Saved(alerts, Start.AddMinutes(poll), Start.AddMinutes(poll + 1));
                Assert.Equal(Enumerable.Range(poll * 1024, 1024).Select(i => $"app{i:D5}.exe"), saved.Select(e => e.Application!).Order(StringComparer.Ordinal));
            }
            // Every app's known row is saved with its episode; at 8,192 known apps the least recently seen make room, so polls 0-1 were forgotten.
            var store = new AlertStore(fixture.AlertPath); store.Initialize();
            var known = store.ReadFirstUse("tablet").Known;
            Assert.Equal(Enumerable.Range(2 * 1024, 8 * 1024).Select(i => AppKey.Of($"app{i:D5}.exe")), known.Select(k => k.Key).Order(StringComparer.Ordinal));
            Assert.All(known, k => Assert.Equal(Start.AddMinutes(int.Parse(k.Key[3..8]) / 1024), k.Last));
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Excluded_budget_reseeds_when_the_app_is_removed()
    {
        using var fixture = new Fixture(); var (usage, alerts) = Services(fixture);
        var store = UsageStore(usage);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);
        var seeded = Start.AddMinutes(-1) < today ? today : Start.AddMinutes(-1);
        store.Append("tablet", [new UsageMinuteBatch(seeded, [], [new UsageApplicationMinute("chrome.exe", seeded, 400, 200, "earlier"),
            new UsageApplicationMinute("steam.exe", seeded, 400, 200, "earlier")], false, "earlier")], DateTimeOffset.UtcNow);
        AlertRule[] rules = [Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000), Rule(AlertKind.ApplicationDaily, "steam.exe", 1000, id: "steam")];
        alerts.Configure(rules, true);
        var started = new List<AlertEpisode>(); var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alerts.EpisodeStarted += episode => { lock (started) { started.Add(episode); if (started.Count == 2) both.TrySetResult(); } };
        var minute = typeof(AlertService).GetMethod("OnMinute", Fields)!;
        void Minute(int offset, params UsageApplicationMinute[] apps)
            => minute.Invoke(alerts, [new UsageMinuteBatch(Start.AddMinutes(offset), [], apps, false, "live")]);
        UsageApplicationMinute App(string name, int offset, long bytes) => new(name, Start.AddMinutes(offset), bytes, 0, "live");
        alerts.Start();
        try
        {
            // Today's 600 saved bytes each are seeded at startup. Excluded, chrome's total is 0 and 600 more bytes start nothing.
            alerts.Configure(rules, true, null, ["chrome.exe"]);
            Minute(1, App("chrome.exe", 1, 600), App("steam.exe", 1, 300));
            // Removed from the list, chrome alone re-seeds from today's 600 saved bytes; steam keeps its 900.
            alerts.Configure(rules, true, null, []);
            Minute(2, App("chrome.exe", 2, 500), App("steam.exe", 2, 150));
            await both.Task.WaitAsync(Timeout);
            lock (started)
            {
                var chrome = started.Single(e => e.RuleId == "rule-1"); var steam = started.Single(e => e.RuleId == "steam");
                Assert.Equal(Start.AddMinutes(2), chrome.Start); Assert.Equal(1100, chrome.Peak);
                Assert.Equal(Start.AddMinutes(2), steam.Start); Assert.Equal(1050, steam.Peak);
            }
            Assert.False(alerts.Failed);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task A_new_app_after_learning_is_logged_closed_and_one_balloon_groups_apps_within_the_window()
    {
        using var fixture = new Fixture(); Store(fixture.AlertPath);
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        var alerts = FirstUseService(fixture, usage);
        alerts.Configure([AlertRules.DefaultFirstNetworkUse(Start)], true);
        var groups = new List<FirstUseGroup>(); int notified = 0;
        alerts.FirstUseGroupClosed += group => { lock (groups) groups.Add(group); };
        alerts.EpisodeStarted += _ => Interlocked.Increment(ref notified);
        int Groups() { lock (groups) return groups.Count; }
        alerts.Start();
        try
        {
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(5), true, Use("a.exe", firstSeen: Start.AddSeconds(3)), Use("b.exe", firstSeen: Start.AddSeconds(1))));
            await Until(async () => Groups() > 0 && (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Count == 2 || alerts.Failed);
            await Task.Delay(300);
            await alerts.StopAsync();
            FirstUseGroup group;
            lock (groups) group = Assert.Single(groups);
            Assert.Equal(2, group.Count); Assert.Equal(["a.exe", "b.exe"], group.Names); Assert.Equal(Start.AddSeconds(1), group.First);
            var saved = await Saved(alerts, Start.AddDays(-1), Start.AddDays(1));
            Assert.Equal(["a.exe", "b.exe"], saved.Select(e => e.Application!).Order(StringComparer.Ordinal));
            Assert.All(saved, e => { Assert.Equal(e.Start, e.End); Assert.Equal(0, e.Peak); });
            var store = new AlertStore(fixture.AlertPath); store.Initialize();
            Assert.Equal([AppKey.Of("a.exe"), AppKey.Of("b.exe")], store.ReadFirstUse("tablet").Known.Select(k => k.Key).Order(StringComparer.Ordinal));
            Assert.Equal(0, Volatile.Read(ref notified));
            Assert.False(alerts.Failed);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task First_use_groups_skip_the_hour_floor_and_muted_notifications_still_log()
    {
        using var fixture = new Fixture(); Store(fixture.AlertPath);
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        // The flush tick is a minute away, so only the group's own wake-up can close a group within the test.
        var alerts = FirstUseService(fixture, usage, flush: TimeSpan.FromMinutes(1));
        AlertRule[] rules = [AlertRules.DefaultFirstNetworkUse(Start)];
        alerts.Configure(rules, true);
        var groups = new List<FirstUseGroup>();
        alerts.FirstUseGroupClosed += group => { lock (groups) groups.Add(group); };
        int Groups() { lock (groups) return groups.Count; }
        alerts.Start();
        try
        {
            alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
            await Until(() => Task.FromResult(Groups() == 1));
            await Task.Delay(1000);
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(1), true, Use("b.exe", firstSeen: Start.AddSeconds(1))));
            await Until(() => Task.FromResult(Groups() == 2));
            lock (groups) Assert.Equal(["a.exe", "b.exe"], groups.Select(g => Assert.Single(g.Names)));
            alerts.Configure(rules, false);
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(2), true, Use("c.exe", firstSeen: Start.AddSeconds(2))));
            await Task.Delay(1000);
            alerts.TryRecordNetworkUse(R(Start.AddSeconds(3), true, Use("d.exe", firstSeen: Start.AddSeconds(3))));
            await Task.Delay(500);
            await alerts.StopAsync();
            Assert.Equal(2, Groups());
            Assert.Equal(["a.exe", "b.exe", "c.exe", "d.exe"], (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Select(e => e.Application!).Order(StringComparer.Ordinal));
            Assert.False(alerts.Failed);
        }
        finally { await alerts.StopAsync(); await usage.StopAsync(); }
    }

    [Fact]
    public async Task Seeding_reads_each_store_once_retries_a_failed_store_and_skips_a_store_with_a_clear_in_progress()
    {
        var seenAt = Start.AddMinutes(-5);
        // An alerts.db with learning dates in the past but no seed marker.
        void Unseeded(string path)
        {
            var store = new AlertStore(path); store.Initialize();
            store.Append("tablet", [], DateTimeOffset.UtcNow, new FirstUseChanges([], [], new FirstUseMetadata(null, null, LongAgo, LongAgo.AddDays(3))));
        }
        void Usage(UsageHistoryService usage, string name, DateTimeOffset minute)
            => UsageStore(usage).Append("tablet", [new UsageMinuteBatch(minute, [], [new UsageApplicationMinute(name, minute, 400, 200, "earlier")], false, "earlier")], DateTimeOffset.UtcNow);
        FirstUseState Read(Fixture fixture) { var store = new AlertStore(fixture.AlertPath); store.Initialize(); return store.ReadFirstUse("tablet"); }
        string[] Keys(FirstUseState state) => [.. state.Known.Select(k => k.Key).Order(StringComparer.Ordinal)];
        Func<Task<IReadOnlyList<ApplicationSeen>?>> Names(Action called, params ApplicationSeen[] names)
            => () => { called(); return Task.FromResult<IReadOnlyList<ApplicationSeen>?>(names); };

        // Both stores are read once; their names are known with no episode and both markers are set.
        using (var fixture = new Fixture())
        {
            Unseeded(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            Usage(usage, "seen.exe", seenAt);
            int calls = 0;
            var alerts = FirstUseService(fixture, usage, connectionNames: Names(() => Interlocked.Increment(ref calls),
                new("conn.exe", LongAgo, LongAgo.AddMinutes(5)), new("Seen.EXE", LongAgo, LongAgo)));
            alerts.Configure([AlertRules.DefaultFirstNetworkUse(Start)], true);
            alerts.Start();
            try
            {
                await Until(() => Task.FromResult(alerts.FirstUse.Seeded || alerts.Failed));
                await alerts.StopAsync();
                var state = Read(fixture);
                Assert.NotNull(state.SeededUsage); Assert.NotNull(state.SeededConnections);
                Assert.Equal([AppKey.Of("conn.exe"), AppKey.Of("seen.exe")], Keys(state));
                var seen = state.Known.Single(k => k.Key == AppKey.Of("seen.exe"));
                Assert.Equal(LongAgo, seen.First); Assert.Equal(seenAt, seen.Last);
                Assert.Empty(await Saved(alerts, Start.AddDays(-30), Start.AddDays(1)));
                Assert.Equal(1, calls); Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // A throwing connection delegate leaves its marker unset and the next start reads it; after both markers no store is read again.
        using (var fixture = new Fixture())
        {
            Unseeded(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            Usage(usage, "seen.exe", seenAt);
            AlertRule[] rules = [AlertRules.DefaultFirstNetworkUse(Start)];
            int throwing = 0;
            var first = FirstUseService(fixture, usage, connectionNames: () => { Interlocked.Increment(ref throwing); throw new InvalidOperationException("no connections"); });
            first.Configure(rules, true); first.Start();
            try { await Until(() => Task.FromResult(first.FirstUse.KnownApps == 1 || first.Failed)); }
            finally { await first.StopAsync(); }
            var state = Read(fixture);
            Assert.NotNull(state.SeededUsage); Assert.Null(state.SeededConnections); Assert.Equal(1, throwing);
            Assert.Equal([AppKey.Of("seen.exe")], Keys(state));

            // A name saved after the usage marker is never seeded: Usage history is not read again.
            Usage(usage, "late.exe", seenAt.AddMinutes(1));
            int calls = 0;
            var second = FirstUseService(fixture, usage, connectionNames: Names(() => Interlocked.Increment(ref calls), new ApplicationSeen("conn.exe", LongAgo, LongAgo)));
            second.Configure(rules, true); second.Start();
            try { await Until(() => Task.FromResult(second.FirstUse.Seeded || second.Failed)); }
            finally { await second.StopAsync(); }
            state = Read(fixture);
            Assert.NotNull(state.SeededConnections); Assert.Equal(1, calls);
            Assert.Equal([AppKey.Of("conn.exe"), AppKey.Of("seen.exe")], Keys(state));

            int again = 0;
            var third = FirstUseService(fixture, usage, connectionNames: Names(() => Interlocked.Increment(ref again)));
            third.Configure(rules, true); third.Start();
            try
            {
                // The worker reaches its loop only after startup's seed: a seeded app stays quiet and a new one alerts.
                third.TryRecordNetworkUse(R(Start, true, Use("seen.exe"), Use("fresh.exe")));
                await Until(async () => (await Saved(third, Start.AddDays(-1), Start.AddDays(1))).Count == 1 || third.Failed);
                await third.StopAsync();
                Assert.Equal("fresh.exe", Assert.Single(await Saved(third, Start.AddDays(-1), Start.AddDays(1))).Application);
                Assert.Equal(0, again); Assert.False(third.Failed);
            }
            finally { await third.StopAsync(); await usage.StopAsync(); }
        }

        // With a usage clear in progress, the usage read returns null and only Connections history's marker is set.
        using (var fixture = new Fixture())
        {
            Unseeded(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            Usage(usage, "seen.exe", seenAt);
            UsageStore(usage).BeginClear(HistoryClear.Create(null, true, DateTimeOffset.UtcNow));
            Assert.True(File.Exists(Path.ChangeExtension(fixture.UsagePath, ".clear.json")));
            var alerts = FirstUseService(fixture, usage, connectionNames: Names(() => { }, new ApplicationSeen("conn.exe", LongAgo, LongAgo)));
            alerts.Configure([AlertRules.DefaultFirstNetworkUse(Start)], true); alerts.Start();
            try { await Until(() => Task.FromResult(alerts.FirstUse.KnownApps == 1 || alerts.Failed)); }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
            var state = Read(fixture);
            Assert.Null(state.SeededUsage); Assert.NotNull(state.SeededConnections);
            Assert.Equal([AppKey.Of("conn.exe")], Keys(state));
        }

        // StopAsync while the connection read is pending: nothing is written, not even the usage names already read.
        using (var fixture = new Fixture())
        {
            Unseeded(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            Usage(usage, "seen.exe", seenAt);
            var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var names = new TaskCompletionSource<IReadOnlyList<ApplicationSeen>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var alerts = FirstUseService(fixture, usage, connectionNames: () => { called.TrySetResult(); return names.Task; });
            alerts.Configure([AlertRules.DefaultFirstNetworkUse(Start)], true); alerts.Start();
            try
            {
                await called.Task.WaitAsync(Timeout);
                var stopping = alerts.StopAsync();
                names.SetResult([new("conn.exe", LongAgo, LongAgo)]);
                await stopping.WaitAsync(Timeout);
                var state = Read(fixture);
                Assert.Null(state.SeededUsage); Assert.Null(state.SeededConnections); Assert.Empty(state.Known);
                Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }
    }

    [Fact]
    public async Task Restart_keeps_known_apps_and_learning_dates_and_a_group_open_at_quit_shows_no_balloon()
    {
        using var fixture = new Fixture(); Store(fixture.AlertPath);
        var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        AlertRule[] rules = [AlertRules.DefaultFirstNetworkUse(Start)];
        int closed = 0;
        var first = FirstUseService(fixture, usage, window: TimeSpan.FromSeconds(60));
        first.Configure(rules, true);
        first.FirstUseGroupClosed += _ => Interlocked.Increment(ref closed);
        first.Start();
        FirstUseStatus before;
        try
        {
            first.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
            await Until(async () => (await Saved(first, Start.AddDays(-1), Start.AddDays(1))).Count == 1 || first.Failed);
            before = first.FirstUse;
            await first.StopAsync();
            Assert.Equal(0, Volatile.Read(ref closed)); Assert.False(first.Failed);
        }
        finally { await first.StopAsync(); }

        var second = FirstUseService(fixture, usage);
        second.Configure(rules, true);
        second.FirstUseGroupClosed += _ => Interlocked.Increment(ref closed);
        second.Start();
        try
        {
            await Until(() => Task.FromResult(second.FirstUse.Restored || second.Failed));
            Assert.Equal(1, second.FirstUse.KnownApps);
            Assert.Equal(before.LearningStart, second.FirstUse.LearningStart); Assert.Equal(LongAgo, second.FirstUse.LearningStart);
            Assert.Equal(before.LearningEnd, second.FirstUse.LearningEnd);
            second.TryRecordNetworkUse(R(Start.AddMinutes(1), true, Use("a.exe", firstSeen: Start.AddMinutes(1)), Use("b.exe", firstSeen: Start.AddMinutes(1))));
            await Until(async () => (await Saved(second, Start.AddDays(-1), Start.AddDays(1))).Count == 2 || second.Failed);
            await Until(() => Task.FromResult(Volatile.Read(ref closed) == 1));
            await second.StopAsync();
            Assert.Equal(["a.exe", "b.exe"], (await Saved(second, Start.AddDays(-1), Start.AddDays(1))).Select(e => e.Application!).Order(StringComparer.Ordinal));
            Assert.Equal(1, Volatile.Read(ref closed)); Assert.False(second.Failed);
        }
        finally { await second.StopAsync(); await usage.StopAsync(); }
    }

    // The alert-log clear (Task 18). Requested is the real clock, so every fixture poll (Start + n) is before it.
    private static AlertStore AlertStoreOf(AlertService alerts) => (AlertStore)typeof(AlertService).GetField("store", Fields)!.GetValue(alerts)!;
    private static long Generation(AlertService alerts) => (long)typeof(AlertService).GetField("knownGeneration", Fields)!.GetValue(alerts)!;
    private static IReadOnlyList<string> IgnoredList(AlertService alerts) => (IReadOnlyList<string>)typeof(AlertService).GetField("ignoredApps", Fields)!.GetValue(alerts)!;
    private static long Sql(Fixture fixture, string sql) => UsageHistoryServiceTests.Count(fixture.AlertPath, sql);
    private static long Known(Fixture fixture, string key, string machine = "tablet")
        => Sql(fixture, $"SELECT COUNT(*) FROM known_apps WHERE machine='{machine}' AND app_key='{key}'");
    private static long LastSeen(Fixture fixture, string key) => Sql(fixture, $"SELECT COALESCE(MAX(last_seen),0) FROM known_apps WHERE machine='tablet' AND app_key='{key}'");
    private static long PendingClears(Fixture fixture) => Sql(fixture, "SELECT COUNT(*) FROM pending_clear");
    private static async Task<AlertEpisode[]> FirstUses(AlertService alerts, string app)
        => (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Where(e => e.Application == app).ToArray();
    private static async Task UntilPhase(AlertService alerts, HistoryClearPhase phase)
        => await Until(() => Task.FromResult(alerts.ClearState.Phase == phase || alerts.Failed));
    /// <summary>A poll <paramref name="minute"/> minutes after the next one, whose known row then shows that minute as last seen: the worker
    /// has handled it. It is after Requested, because an episode clamped to Requested saves its known row at that minute.</summary>
    private static async Task Seen(Fixture fixture, AlertService alerts, int minute, string app = "a.exe")
    {
        var at = UsageMetrics.Floor(DateTimeOffset.UtcNow).AddMinutes(1 + minute);
        alerts.TryRecordNetworkUse(R(at, true, Use(app, firstSeen: at)));
        await Until(() => Task.FromResult(LastSeen(fixture, AppKey.Of(app)) == at.ToUnixTimeMilliseconds() || alerts.Failed));
    }
    private static void ReadOnly(Fixture fixture, bool on)
    {
        // SQLite can drop its -shm/-wal companions between the listing and the attribute call; a file that is gone needs no attribute.
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fixture.AlertPath)!, "alerts.db*"))
        {
            try { File.SetAttributes(file, on ? File.GetAttributes(file) | FileAttributes.ReadOnly : File.GetAttributes(file) & ~FileAttributes.ReadOnly); }
            catch (FileNotFoundException) { }
        }
    }

    [Fact]
    public async Task Clearing_an_app_forgets_it_so_its_next_poll_alerts_again_and_keeps_the_dont_alert_list()
    {
        AlertRule[] rules = [AlertRules.DefaultFirstNetworkUse(Start)];
        string[] ignored = ["quiet.exe"];
        // One app, all time: the known row goes in BeginClear's transaction; a poll between it and the last chunk is kept.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = FirstUseService(fixture, usage);
            alerts.Configure(rules, true, ignored);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe"), Use("b.exe"), Use("quiet.exe")));
                await Until(async () => (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Count == 2 || alerts.Failed);
                Assert.Equal(1, Known(fixture, "A.EXE")); Assert.Equal(1, Known(fixture, "QUIET.EXE"));
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var store = AlertStoreOf(alerts);
                alerts.ClearStepOverride = budget => release.Task.IsCompleted ? store.ClearStep("tablet", budget) : new HistoryClearProgress(0, null, false);
                var requested = DateTimeOffset.UtcNow;
                alerts.Clear(HistoryClear.Create("A.EXE", true, requested));
                await Until(() => Task.FromResult(Generation(alerts) == 1 || alerts.Failed));
                // The generation moves before the Flush that publishes FirstUse, so the count is awaited on its own.
                await Until(() => Task.FromResult(alerts.FirstUse.KnownApps == 2 || alerts.Failed));
                Assert.Equal(HistoryClearPhase.Clearing, alerts.ClearState.Phase); Assert.True(alerts.ClearState.MarkerSaved);
                Assert.EndsWith(" · Clearing history.", alerts.Status);
                Assert.Equal(0, Known(fixture, "A.EXE")); Assert.Equal(1, Known(fixture, "B.EXE")); Assert.Equal(1, PendingClears(fixture));
                Assert.Equal(2, alerts.FirstUse.KnownApps);
                // The same minute as before: only the generation increment lets detection post a.exe again. FirstSeen is before Requested.
                alerts.TryRecordNetworkUse(R(Start.AddSeconds(30), true, Use("a.exe"), Use("quiet.exe")));
                await Until(async () => (await FirstUses(alerts, "a.exe")).Length == 2 || alerts.Failed);
                Assert.Equal(1, Known(fixture, "A.EXE"));
                release.SetResult();
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Equal(new HistoryClearState(HistoryClearPhase.Done, 100, null, false, false), alerts.ClearState);
                var kept = Assert.Single(await FirstUses(alerts, "a.exe"));
                Assert.Equal(requested.ToUnixTimeMilliseconds(), kept.Start.ToUnixTimeMilliseconds());
                Assert.Single(await FirstUses(alerts, "b.exe")); Assert.Empty(await FirstUses(alerts, "quiet.exe"));
                Assert.Equal(1, Known(fixture, "A.EXE")); Assert.Equal(1, Known(fixture, "B.EXE")); Assert.Equal(0, PendingClears(fixture));
                await Seen(fixture, alerts, 1);
                Assert.Single(await FirstUses(alerts, "a.exe"));
                Assert.Equal(ignored, IgnoredList(alerts)); Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // Older than 7 days changes no known row; all apps, all time empties known_apps under every machine key and keeps the metadata.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var other = new AlertStore(fixture.AlertPath); other.Initialize();
            other.Append("other", [], DateTimeOffset.UtcNow, new FirstUseChanges([new KnownApplication("X.EXE", LongAgo, LongAgo), new KnownApplication("A.EXE", LongAgo, LongAgo)], [],
                new FirstUseMetadata(LongAgo, LongAgo, LongAgo, LongAgo.AddDays(3))));
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = FirstUseService(fixture, usage);
            alerts.Configure(rules, true, ignored);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe"), Use("b.exe")));
                await Until(async () => (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Count == 2 || alerts.Failed);
                var before = AlertStoreOf(alerts).ReadFirstUse("tablet");
                alerts.Clear(HistoryClear.Create(null, false, DateTimeOffset.UtcNow));
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Equal(4, Sql(fixture, "SELECT COUNT(*) FROM known_apps"));
                Assert.Equal(2, alerts.FirstUse.KnownApps); Assert.Equal(0, Generation(alerts));
                Assert.Equal(2, (await Saved(alerts, Start.AddDays(-1), Start.AddDays(1))).Count);

                alerts.Clear(HistoryClear.Create(null, true, DateTimeOffset.UtcNow));
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Equal(0, Sql(fixture, "SELECT COUNT(*) FROM known_apps"));
                Assert.Empty(await Saved(alerts, Start.AddDays(-1), Start.AddDays(1)));
                Assert.Equal(0, alerts.FirstUse.KnownApps);
                var after = AlertStoreOf(alerts).ReadFirstUse("tablet");
                Assert.Equal((before.SeededUsage, before.SeededConnections, before.LearningStart, before.LearningEnd),
                    (after.SeededUsage, after.SeededConnections, after.LearningStart, after.LearningEnd));
                Assert.Equal(LongAgo, after.LearningStart);
                Assert.Equal(ignored, IgnoredList(alerts)); Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // BeginClear failing on a read-only file deletes no known row and forgets nothing.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = FirstUseService(fixture, usage);
            alerts.Configure(rules, true, ignored);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
                await Until(async () => (await FirstUses(alerts, "a.exe")).Length == 1 || alerts.Failed);
                ReadOnly(fixture, true);
                try
                {
                    alerts.Clear(HistoryClear.Create("A.EXE", true, DateTimeOffset.UtcNow));
                    await UntilPhase(alerts, HistoryClearPhase.Failed);
                    Assert.False(alerts.ClearState.MarkerSaved); Assert.NotNull(alerts.ClearState.Error);
                    Assert.EndsWith($" · Clearing history failed ({alerts.ClearState.Error}); not cleared.", alerts.Status);
                    Assert.Equal(1, Known(fixture, "A.EXE")); Assert.Equal(0, PendingClears(fixture));
                    Assert.Equal(1, alerts.FirstUse.KnownApps); Assert.Equal(0, Generation(alerts));
                }
                finally { ReadOnly(fixture, false); }
                Assert.True(alerts.CanRetryClear);
                await Seen(fixture, alerts, 1);
                Assert.Single(await FirstUses(alerts, "a.exe"));
                Assert.Equal(ignored, IgnoredList(alerts)); Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // A restart mid-clear deletes only episodes: the known row and episode written after BeginClear stay.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var first = FirstUseService(fixture, usage);
            first.Configure(rules, true, ignored);
            first.Start();
            AlertEpisode again;
            try
            {
                first.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
                await Until(async () => (await FirstUses(first, "a.exe")).Length == 1 || first.Failed);
                first.ClearStepOverride = _ => new HistoryClearProgress(0, null, false);
                first.Clear(HistoryClear.Create("A.EXE", true, DateTimeOffset.UtcNow));
                await Until(() => Task.FromResult(Generation(first) == 1 || first.Failed));
                first.TryRecordNetworkUse(R(Start.AddSeconds(30), true, Use("a.exe")));
                await Until(async () => (await FirstUses(first, "a.exe")).Length == 2 || first.Failed);
                again = (await FirstUses(first, "a.exe")).MaxBy(e => e.Start)!;
                await first.StopAsync();
                Assert.Equal(1, PendingClears(fixture)); Assert.False(first.Failed);
            }
            finally { await first.StopAsync(); }
            var second = FirstUseService(fixture, usage);
            second.Configure(rules, true, ignored);
            second.Start();
            try
            {
                await UntilPhase(second, HistoryClearPhase.Done);
                Assert.True(second.ClearState.Resumed);
                Assert.Equal(again.Id, Assert.Single(await FirstUses(second, "a.exe")).Id);
                Assert.Equal(1, Known(fixture, "A.EXE")); Assert.Equal(1, second.FirstUse.KnownApps); Assert.Equal(0, PendingClears(fixture));
                await Seen(fixture, second, 1);
                Assert.Single(await FirstUses(second, "a.exe"));
                Assert.Equal(ignored, IgnoredList(second)); Assert.False(second.Failed);
            }
            finally { await second.StopAsync(); await usage.StopAsync(); }
        }

        // A step that throws once after a.exe was learned again: Try again finishes and keeps the new row and episode.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = FirstUseService(fixture, usage);
            alerts.Configure(rules, true, ignored);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("a.exe")));
                await Until(async () => (await FirstUses(alerts, "a.exe")).Length == 1 || alerts.Failed);
                var learned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                alerts.ClearStepOverride = _ =>
                {
                    if (!learned.Task.IsCompleted) return new HistoryClearProgress(0, null, false);
                    alerts.ClearStepOverride = null; throw new IOException("Fixture step failure.");
                };
                alerts.Clear(HistoryClear.Create("A.EXE", true, DateTimeOffset.UtcNow));
                await Until(() => Task.FromResult(Generation(alerts) == 1 || alerts.Failed));
                alerts.TryRecordNetworkUse(R(Start.AddSeconds(30), true, Use("a.exe")));
                await Until(async () => (await FirstUses(alerts, "a.exe")).Length == 2 || alerts.Failed);
                var again = (await FirstUses(alerts, "a.exe")).MaxBy(e => e.Start)!;
                learned.SetResult();
                await UntilPhase(alerts, HistoryClearPhase.Failed);
                Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "IOException", true, false), alerts.ClearState);
                Assert.True(alerts.CanRetryClear); Assert.False(alerts.Failed);
                alerts.RetryClear();
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Equal(again.Id, Assert.Single(await FirstUses(alerts, "a.exe")).Id);
                Assert.Equal(1, Known(fixture, "A.EXE")); Assert.Equal(0, PendingClears(fixture)); Assert.Equal(1, Generation(alerts));
                await Seen(fixture, alerts, 1);
                Assert.Single(await FirstUses(alerts, "a.exe"));
                Assert.Equal(ignored, IgnoredList(alerts)); Assert.False(alerts.Failed); Assert.False(alerts.CanRetryClear);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }
    }

    [Fact]
    public async Task Alert_clear_runs_in_steps_and_deletes_an_open_budget_episode_for_good()
    {
        AlertRule[] rules = [Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000), AlertRules.DefaultFirstNetworkUse(Start)];
        string[] ignored = ["quiet.exe"];
        var minute = typeof(AlertService).GetMethod("OnMinute", Fields)!;
        void Minute(AlertService alerts, int offset, long bytes)
            => minute.Invoke(alerts, [new UsageMinuteBatch(Start.AddMinutes(offset), [], [new UsageApplicationMinute("chrome.exe", Start.AddMinutes(offset), bytes, 0, "live")], false, "live")]);
        async Task<AlertEpisode[]> Budgets(AlertService alerts) => (await Log(alerts)).Episodes.Where(e => e.RuleId == "rule-1").ToArray();
        // Two later 600-byte minutes, then a new app's poll that follows them through the queue.
        async Task LaterMinutes(AlertService alerts, int from, string marker)
        {
            Minute(alerts, from, 600); Minute(alerts, from + 1, 600);
            alerts.TryRecordNetworkUse(R(Start.AddMinutes(from + 1), true, Use(marker, firstSeen: Start.AddMinutes(from + 1))));
            await Until(async () => (await FirstUses(alerts, marker)).Length == 1 || alerts.Failed);
        }
        AlertService Service(Fixture fixture, UsageHistoryService usage)
        {
            var alerts = new AlertService(fixture.AlertPath, "tablet", usage, () => null, flushInterval: TimeSpan.FromMilliseconds(30), localZone: TimeZoneInfo.Utc,
                firstUseGroupWindow: TimeSpan.FromMilliseconds(100));
            alerts.Configure(rules, true, ignored);
            return alerts;
        }

        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = Service(fixture, usage);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("chrome.exe")));
                Minute(alerts, 1, 1100);
                await Until(async () => (await Budgets(alerts)).Any(e => e.End is null) || alerts.Failed);
                Assert.Equal(1, Known(fixture, "CHROME.EXE"));
                alerts.Clear(HistoryClear.Create("chrome.exe", true, DateTimeOffset.UtcNow));
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Empty(await Budgets(alerts)); Assert.Empty(await FirstUses(alerts, "chrome.exe"));
                Assert.Equal(0, Known(fixture, "CHROME.EXE"));
                await LaterMinutes(alerts, 2, "z.exe");
                Assert.Empty(await Budgets(alerts));
                Assert.Equal(ignored, IgnoredList(alerts)); Assert.Equal(rules.Select(r => r.Id), alerts.Rules.Select(r => r.Id));
                Assert.False(alerts.Failed);
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // StopAsync mid-clear starts no further step, returns promptly and keeps the marker. An open budget episode (as if a crash lost steps 3 and 6) is
        // ended and flushed again by the next service, which finishes the clear.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var first = Service(fixture, usage);
            first.Start();
            try
            {
                first.TryRecordNetworkUse(R(Start, true, Use("chrome.exe")));
                await Until(async () => (await FirstUses(first, "chrome.exe")).Length == 1 || first.Failed);
                // Once armed, the next step requests StopAsync from inside itself, so the stop arrives while a step is in progress; any
                // later step is counted.
                Task? stopping = null; bool armed = false; int stepsAfterStop = 0;
                var watch = new System.Diagnostics.Stopwatch();
                first.ClearStepOverride = _ =>
                {
                    if (Volatile.Read(ref stopping) is not null) Interlocked.Increment(ref stepsAfterStop);
                    else if (Volatile.Read(ref armed)) { watch.Start(); Volatile.Write(ref stopping, first.StopAsync()); }
                    Thread.Sleep(100); return new HistoryClearProgress(0, null, false);
                };
                first.Clear(HistoryClear.Create("chrome.exe", true, DateTimeOffset.UtcNow));
                await Until(() => Task.FromResult(Generation(first) == 1 || first.Failed));
                await Task.Delay(300);
                Assert.Equal(HistoryClearPhase.Clearing, first.ClearState.Phase);
                Volatile.Write(ref armed, true);
                await Until(() => Task.FromResult(Volatile.Read(ref stopping) is not null || first.Failed));
                await stopping!;
                watch.Stop();
                // The guarantee is no step after stop; the time (the step in progress plus the final synchronous=FULL flush) varies with
                // the CI disk, and 1 s failed there at 2.31 s, so the bound is loose.
                Assert.Equal(0, Volatile.Read(ref stepsAfterStop));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {watch.Elapsed}.");
                Assert.Equal(1, PendingClears(fixture)); Assert.False(first.Failed);
            }
            finally { await first.StopAsync(); }
            var file = new AlertStore(fixture.AlertPath); file.Initialize();
            file.Append("tablet", [new AlertEpisode("open-budget", "rule-1", AlertKind.ApplicationDaily, "chrome.exe ≥ 1.0 KB in a day", Start.AddMinutes(1), null, 1100)], DateTimeOffset.UtcNow);
            var second = Service(fixture, usage);
            second.Start();
            try
            {
                await UntilPhase(second, HistoryClearPhase.Done);
                Assert.True(second.ClearState.Resumed);
                Assert.Empty(await Budgets(second)); Assert.Empty(await FirstUses(second, "chrome.exe")); Assert.Equal(0, PendingClears(fixture));
                await LaterMinutes(second, 2, "z.exe");
                Assert.Empty(await Budgets(second));
                Assert.Equal(ignored, IgnoredList(second)); Assert.False(second.Failed);
            }
            finally { await second.StopAsync(); await usage.StopAsync(); }
        }

        // A step that throws: Failed with the marker saved, the log keeps working, and Try again finishes.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = Service(fixture, usage);
            alerts.Start();
            try
            {
                alerts.TryRecordNetworkUse(R(Start, true, Use("chrome.exe")));
                await Until(async () => (await FirstUses(alerts, "chrome.exe")).Length == 1 || alerts.Failed);
                alerts.ClearStepOverride = _ => { alerts.ClearStepOverride = null; throw new IOException("Fixture step failure."); };
                alerts.Clear(HistoryClear.Create("chrome.exe", true, DateTimeOffset.UtcNow));
                await UntilPhase(alerts, HistoryClearPhase.Failed);
                Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "IOException", true, false), alerts.ClearState);
                Assert.EndsWith(" · Clearing history failed (IOException); it resumes when DeadWare next starts.", alerts.Status);
                Assert.False(alerts.Failed); Assert.Equal(1, PendingClears(fixture));
                alerts.TryRecordNetworkUse(R(Start.AddMinutes(1), true, Use("later.exe", firstSeen: Start.AddMinutes(1))));
                await Until(async () => (await FirstUses(alerts, "later.exe")).Length == 1 || alerts.Failed);
                Assert.False(alerts.Failed);
                alerts.RetryClear();
                await UntilPhase(alerts, HistoryClearPhase.Done);
                Assert.Empty(await FirstUses(alerts, "chrome.exe")); Assert.Single(await FirstUses(alerts, "later.exe")); Assert.Equal(0, PendingClears(fixture));
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }

        // A worker stopped on a store failure: the clear is Unavailable with that failure's type and writes nothing.
        using (var fixture = new Fixture())
        {
            Store(fixture.AlertPath);
            var usage = new UsageHistoryService(fixture.UsagePath, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
            var alerts = Service(fixture, usage);
            alerts.Start();
            try
            {
                await Until(() => Task.FromResult(alerts.FirstUse.Restored || alerts.Failed));
                ReadOnly(fixture, true);
                try
                {
                    alerts.TryRecordNetworkUse(R(Start, true, Use("chrome.exe")));
                    await Until(() => Task.FromResult(alerts.Failed));
                    alerts.Clear(HistoryClear.Create("chrome.exe", true, DateTimeOffset.UtcNow));
                    Assert.Equal(HistoryClearPhase.Unavailable, alerts.ClearState.Phase);
                    Assert.NotNull(alerts.ClearState.Error);
                    Assert.StartsWith($"Alert log unavailable ({alerts.ClearState.Error}).", alerts.Status);
                    Assert.False(alerts.CanRetryClear);
                }
                finally { ReadOnly(fixture, false); }
                await Task.Delay(200);
                Assert.Equal(HistoryClearPhase.Unavailable, alerts.ClearState.Phase);
                Assert.Equal(0, PendingClears(fixture));
            }
            finally { await alerts.StopAsync(); await usage.StopAsync(); }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeadWare-alerts-" + Guid.NewGuid().ToString("N"));
        public string UsagePath => Path.Combine(root, "usage.db");
        public string AlertPath => Path.Combine(root, "alerts.db");
        // A read-only attribute left by ReadOnly, or a pooled SQLite handle, makes a plain delete throw on Windows; clear both and retry.
        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(root)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, true); return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 10) return;
                    Thread.Sleep(100);
                }
            }
        }
    }
}
