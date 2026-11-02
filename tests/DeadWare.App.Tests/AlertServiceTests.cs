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
    private static void Feed(UsageHistoryService usage, int fromSecond, int toSecond, double cpu = 99)
    { for (int i = fromSecond; i < toSecond; i++) usage.TryRecord(Reading(Start.AddSeconds(i), i, cpu)); }
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
            Feed(usage, 0, 61);
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
            Feed(usage, 0, 61);
            await Until(async () => (await Log(alerts)).Total == 1);
            Assert.Equal(0, Volatile.Read(ref notifications));
            alerts.Configure([Rule(AlertKind.Metric, "cpu", 90), Rule(AlertKind.Metric, "memory", 40, id: "ram")], true);
            Feed(usage, 61, 121);
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
        Feed(first.Usage, 0, 61);
        await notified.Task.WaitAsync(Timeout);
        await first.Alerts.StopAsync(); await first.Usage.StopAsync();
        var second = Services(fixture);
        second.Alerts.Configure([Rule(AlertKind.Metric, "cpu", 90)], true);
        int notifications = 0; second.Alerts.EpisodeStarted += _ => Interlocked.Increment(ref notifications);
        second.Usage.Start(); second.Alerts.Start();
        try
        {
            Feed(second.Usage, 60, 120, cpu: 10); Feed(second.Usage, 120, 181, cpu: 99);
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
                usage.TryRecord(Reading(Start.AddSeconds(i), i, processes: [new ProcessTraffic(7, 1, "chrome.exe", null, null, 2L * i, 0)]));
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

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "DeadWare-alerts-" + Guid.NewGuid().ToString("N"));
        public string UsagePath => Path.Combine(root, "usage.db");
        public string AlertPath => Path.Combine(root, "alerts.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
