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
    private static AlertService FirstUseService(Fixture fixture, UsageHistoryService usage)
        => new(fixture.AlertPath, "tablet", usage, () => null, flushInterval: TimeSpan.FromMilliseconds(30), localZone: TimeZoneInfo.Utc,
            firstUseGroupWindow: TimeSpan.FromMilliseconds(100));
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
            Assert.Equal(["a.exe", "b.exe", .. Enumerable.Range(0, 1100).Select(i => $"app{i:D5}.exe")], saved.Select(e => e.Application!).Order(StringComparer.Ordinal));
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

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeadWare-alerts-" + Guid.NewGuid().ToString("N"));
        public string UsagePath => Path.Combine(root, "usage.db");
        public string AlertPath => Path.Combine(root, "alerts.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
