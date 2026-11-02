using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Gpu = "Fixture GPU";

    private static AlertRule Rule(AlertKind kind, string target, double threshold, int minutes = 5, string id = "rule-1", bool enabled = true)
        => new(id, kind, target, threshold, minutes, enabled, T0);
    private static UsageMetricMinute Metric(DateTimeOffset minute, string metric, double average, string? hardware = null, double? maximum = null)
        => new(metric, hardware, minute, average, average, maximum ?? average, 60, true, "seg");
    private static UsageMinuteBatch Batch(DateTimeOffset minute, double? cpu = 10, double? gpu = null, double? download = null,
        bool gap = false, params (string Name, long Bytes)[] apps)
    {
        var metrics = new List<UsageMetricMinute>();
        if (cpu is { } c) metrics.Add(Metric(minute, UsageMetrics.Cpu, c, maximum: c + 2));
        if (gpu is { } g) metrics.Add(Metric(minute, UsageMetrics.Gpu, g, Gpu));
        if (download is { } d) metrics.Add(Metric(minute, UsageMetrics.Download, d));
        return new(minute, metrics, apps.Select(a => new UsageApplicationMinute(a.Name, minute, a.Bytes, 0, "seg")).ToArray(), gap, "seg");
    }
    private static AlertEvaluator Evaluator(params AlertRule[] rules)
    {
        var evaluator = new AlertEvaluator(TimeZoneInfo.Utc);
        Assert.Empty(evaluator.Replace(rules, T0));
        return evaluator;
    }
    private static AlertEvaluation Minute(AlertEvaluator evaluator, int minute, double? cpu = 10, double? gpu = null, double? download = null)
        => evaluator.ObserveMinute(Batch(T0.AddMinutes(minute), cpu, gpu, download), Gpu);

    [Fact]
    public void A_sustained_metric_starts_one_episode_and_hysteresis_ends_it()
    {
        var evaluator = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 5));
        for (int i = 0; i < 4; i++) Assert.Empty(Minute(evaluator, i, 95).Started);
        var fifth = Minute(evaluator, 4, 97);
        var episode = Assert.Single(fifth.Started);
        Assert.Same(episode, Assert.Single(fifth.Notify));
        Assert.Equal((T0, 99.0, "CPU usage ≥ 90% for 5 minutes"), (episode.Start, episode.Peak, episode.Summary));
        Assert.Null(episode.End);
        var sixth = Minute(evaluator, 5, 99);
        Assert.Empty(sixth.Started); Assert.Empty(sixth.Ended);
        Assert.Equal(101, Assert.Single(evaluator.OpenEpisodes).Peak);
        // A dip to 88 is below the threshold but above the re-arm level: the run is broken, the episode stays open.
        Assert.Empty(Minute(evaluator, 6, 88).Ended);
        for (int i = 7; i < 11; i++) Assert.Empty(Minute(evaluator, i, 80).Ended);
        var ended = Assert.Single(Minute(evaluator, 11, 80).Ended);
        Assert.Equal(episode.Id, ended.Id); Assert.Equal(T0.AddMinutes(12), ended.End); Assert.Equal(101, ended.Peak);
        Assert.Empty(evaluator.OpenEpisodes);
    }

    [Fact]
    public void Re_arm_within_the_hour_logs_but_does_not_notify()
    {
        var evaluator = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 1));
        var first = Minute(evaluator, 0, 95);
        Assert.Single(first.Started); Assert.Single(first.Notify);
        Assert.Single(Minute(evaluator, 1, 10).Ended);
        var second = Minute(evaluator, 2, 95);
        Assert.Single(second.Started); Assert.Empty(second.Notify);
        Assert.Single(Minute(evaluator, 3, 10).Ended);
        var later = Minute(evaluator, 61, 95);
        Assert.Single(later.Started); Assert.Single(later.Notify);
    }

    [Fact]
    public void A_missing_minute_resets_the_run_but_an_unavailable_reading_does_not()
    {
        var evaluator = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 5));
        for (int i = 0; i < 3; i++) Minute(evaluator, i, 95);
        Assert.Empty(Minute(evaluator, 4, 95).Started);
        for (int i = 5; i < 8; i++) Assert.Empty(Minute(evaluator, i, 95).Started);
        var episode = Assert.Single(Minute(evaluator, 8, 95).Started);
        Assert.Equal(T0.AddMinutes(4), episode.Start);
        var fresh = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 5));
        Minute(fresh, 0, 95); Minute(fresh, 1, 95);
        Assert.Empty(fresh.ObserveMinute(Batch(T0.AddMinutes(2), cpu: null), Gpu).Started);
        Minute(fresh, 3, 95); Minute(fresh, 4, 95);
        Assert.Equal(T0, Assert.Single(Minute(fresh, 5, 95).Started).Start);
    }

    [Fact]
    public void Gpu_and_network_rules_read_their_own_rows()
    {
        var evaluator = Evaluator(Rule(AlertKind.Metric, "gpu", 80, 1, id: "gpu"), Rule(AlertKind.NetworkRate, "download", 1_000_000, 1, id: "net"));
        var both = Minute(evaluator, 0, gpu: 90, download: 2_000_000);
        Assert.Equal(new[] { "gpu", "net" }, both.Started.Select(e => e.RuleId).OrderBy(id => id));
        Assert.Equal("Download ≥ 1.0 MB/s for 1 minute", both.Started.Single(e => e.RuleId == "net").Summary);
        var other = Evaluator(Rule(AlertKind.Metric, "gpu", 80, 1));
        Assert.Empty(other.ObserveMinute(Batch(T0, gpu: 90), null).Started);
        Assert.Empty(other.ObserveMinute(Batch(T0.AddMinutes(1), gpu: 90), "Other GPU").Started);
    }

    [Fact]
    public void Disabled_rules_never_fire_and_replacing_a_rule_ends_its_episode()
    {
        var disabled = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 1, enabled: false));
        Assert.Empty(Minute(disabled, 0, 99).Started);
        var evaluator = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 1));
        var episode = Assert.Single(Minute(evaluator, 0, 99).Started);
        Assert.Empty(evaluator.Replace([Rule(AlertKind.Metric, "cpu", 90, 1)], T0.AddMinutes(1)));
        Assert.Single(evaluator.OpenEpisodes);
        var ended = Assert.Single(evaluator.Replace([Rule(AlertKind.Metric, "cpu", 95, 1)], T0.AddMinutes(2)));
        Assert.Equal(episode.Id, ended.Id); Assert.Equal(T0.AddMinutes(2), ended.End);
        Assert.Empty(evaluator.OpenEpisodes);
        Assert.Single(Minute(evaluator, 3, 99).Started);
        Assert.Single(evaluator.Replace([], T0.AddMinutes(4)));
        Assert.Empty(evaluator.Rules);
    }

    [Fact]
    public void Restored_open_episodes_keep_their_state_and_the_hour_floor()
    {
        var evaluator = Evaluator(Rule(AlertKind.Metric, "cpu", 90, 1));
        var open = new AlertEpisode("old", "rule-1", AlertKind.Metric, "CPU usage ≥ 90% for 1 minute", T0.AddMinutes(-30), null, 96);
        evaluator.Restore([open, open with { Id = "closed", End = T0.AddMinutes(-20) }, open with { Id = "unknown", RuleId = "nope" }]);
        Assert.Equal("old", Assert.Single(evaluator.OpenEpisodes).Id);
        Assert.Empty(Minute(evaluator, 0, 99).Started);
        Assert.Equal(101, Assert.Single(evaluator.OpenEpisodes).Peak);
        Assert.Equal("old", Assert.Single(Minute(evaluator, 1, 10).Ended).Id);
        var again = Minute(evaluator, 2, 99);
        Assert.Single(again.Started); Assert.Empty(again.Notify);
    }

    [Fact]
    public void Application_budgets_fire_once_per_local_day_across_a_dst_change_and_seed_from_the_store()
    {
        var transition = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var back = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), transition, back);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Test Standard", "Test Daylight", [adjustment]);
        var evaluator = new AlertEvaluator(zone);
        Assert.Empty(evaluator.Replace([Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000, 1)], T0));
        // 2026-11-01 is the 25-hour local day: clocks fall back at 06:00Z. Both minutes below are local November 1.
        var early = new DateTimeOffset(2026, 11, 1, 4, 30, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2026, 11, 2, 4, 30, 0, TimeSpan.Zero);
        var nextDay = new DateTimeOffset(2026, 11, 2, 5, 30, 0, TimeSpan.Zero);
        UsageMinuteBatch Apps(DateTimeOffset minute, long bytes) => Batch(minute, apps: [("Chrome.exe", bytes)]);
        Assert.Empty(evaluator.ObserveMinute(Apps(early, 600), null).Started);
        var fired = Assert.Single(evaluator.ObserveMinute(Apps(late, 600), null).Started);
        Assert.Equal((late, 1200.0, "chrome.exe ≥ 1.0 KB in a day"), (fired.Start, fired.Peak, fired.Summary));
        Assert.Empty(evaluator.ObserveMinute(Apps(late.AddMinutes(1), 600), null).Started);
        Assert.Equal(1800, Assert.Single(evaluator.OpenEpisodes).Peak);
        var rollover = evaluator.ObserveMinute(Apps(nextDay, 600), null);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), Assert.Single(rollover.Ended).End);
        Assert.Empty(rollover.Started);
        Assert.Single(evaluator.ObserveMinute(Apps(nextDay.AddMinutes(1), 600), null).Started);
        // Capture-gap minutes neither add bytes nor reset the day.
        Assert.Empty(evaluator.ObserveMinute(Batch(nextDay.AddMinutes(2), gap: true), null).Ended);
        Assert.Equal(1200, Assert.Single(evaluator.OpenEpisodes).Peak);
        var seeded = new AlertEvaluator(zone);
        seeded.Replace([Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000, 1)], T0);
        seeded.SeedDay(early, new Dictionary<string, long> { ["CHROME.EXE"] = 900, ["steam.exe"] = 5000 });
        Assert.Single(seeded.ObserveMinute(Apps(early.AddMinutes(1), 100), null).Started);
        var ignored = new AlertEvaluator(zone);
        ignored.Replace([Rule(AlertKind.ApplicationDaily, "chrome.exe", 1000, 1)], T0);
        ignored.ObserveMinute(Apps(early, 1), null);
        ignored.SeedDay(nextDay, new Dictionary<string, long> { ["chrome.exe"] = 900 });
        Assert.Empty(ignored.ObserveMinute(Apps(early.AddMinutes(1), 100), null).Started);
    }

    [Fact]
    public void Battery_rules_fire_immediately_and_re_arm_on_charge_or_recovery()
    {
        var evaluator = Evaluator(Rule(AlertKind.Battery, "", 20, 1));
        static BatteryReading Reading(double? percent, BatteryPowerState state = BatteryPowerState.OnBattery) => new(percent, state, false);
        Assert.Empty(evaluator.ObserveBattery(T0, Reading(25)).Started);
        var low = evaluator.ObserveBattery(T0.AddMinutes(1), Reading(20));
        var episode = Assert.Single(low.Started); Assert.Single(low.Notify);
        Assert.Equal(("Battery ≤ 20% on battery", 20.0), (episode.Summary, episode.Peak));
        Assert.Empty(evaluator.ObserveBattery(T0.AddMinutes(2), Reading(18)).Started);
        Assert.Equal(18, Assert.Single(evaluator.OpenEpisodes).Peak);
        Assert.Empty(evaluator.ObserveBattery(T0.AddMinutes(3), Reading(null, BatteryPowerState.Unknown)).Ended);
        var charging = evaluator.ObserveBattery(T0.AddMinutes(4), Reading(18, BatteryPowerState.Charging));
        Assert.Equal(T0.AddMinutes(4), Assert.Single(charging.Ended).End);
        var again = evaluator.ObserveBattery(T0.AddMinutes(5), Reading(19));
        Assert.Single(again.Started); Assert.Empty(again.Notify);
        Assert.Empty(evaluator.ObserveBattery(T0.AddMinutes(6), Reading(23)).Ended);
        Assert.Single(evaluator.ObserveBattery(T0.AddMinutes(7), Reading(25)).Ended);
        Assert.Empty(Minute(evaluator, 8, 99).Started);
    }
}
