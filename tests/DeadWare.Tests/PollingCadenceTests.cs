using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class PollingCadenceTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static readonly ThermalDevice Device = new("cpu", "CPU", "CPU", "setup", "map");

    [Theory]
    [InlineData(0.5)] [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(10)]
    public void Settings_preserve_supported_polling_intervals(double interval)
    {
        var decoded = SettingsCodec.Read(SettingsCodec.Write(new UserSettings { PollingIntervalSeconds = interval }));
        Assert.Equal(interval, decoded.Settings.PollingIntervalSeconds);
        Assert.Null(decoded.Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(3)] [InlineData(int.MaxValue)]
    public void Invalid_polling_settings_restore_the_one_second_default(int interval)
    {
        var decoded = SettingsCodec.Read("{\"PollingIntervalSeconds\":" + interval + "}");
        Assert.Equal(1, decoded.Settings.PollingIntervalSeconds);
        Assert.NotNull(decoded.Error);
    }

    [Fact]
    public void Older_settings_keep_the_original_polling_interval()
    {
        var decoded = SettingsCodec.Read("{}");
        Assert.Equal(1, decoded.Settings.PollingIntervalSeconds);
        Assert.Null(decoded.Error);
    }

    [Theory]
    [InlineData(0.5)] [InlineData(2)] [InlineData(5)] [InlineData(10)]
    public void Sparse_graph_history_connects_planned_samples_and_preserves_missed_poll_gaps(double interval)
    {
        var history = new SampleHistory();
        foreach (int index in new[] { 0, 1, 2, 7 })
            history.Add(new(Epoch.AddSeconds(index * interval), index * interval,
                new Dictionary<string, double?> { ["cpu"] = index }) { PollingIntervalSeconds = interval });
        var points = history.Series("cpu", 7 * interval, 120);
        Assert.All(points, point => Assert.Equal(interval, point.PollingIntervalSeconds));
        var reduced = SampleHistory.ReduceForChart(points, 7 * interval, 120, 600);
        Assert.Equal(new double?[] { 0, 1, 2, null, 7 }, reduced.Select(point => point.Value));
    }

    [Fact]
    public void Changing_cadence_does_not_join_restart_segments_or_erase_missing_values()
    {
        var history = new SampleHistory();
        foreach (var (time, value, interval, session) in new (int, double?, int, string?)[]
            { (0, 1, 1, "old"), (10, 2, 10, "old"), (20, null, 10, "old"), (30, 3, 10, null) })
            history.Add(new(Epoch.AddSeconds(time), time, new Dictionary<string, double?> { ["cpu"] = value })
                { PollingIntervalSeconds = interval, SessionId = session });
        var reduced = SampleHistory.ReduceForChart(history.Series("cpu", 30, 60), 30, 60, 600);
        Assert.Equal(new double?[] { 1, 2, null, 3 }, reduced.Select(point => point.Value));
        Assert.True(reduced[^1].BreakBefore);
    }

    [Fact]
    public void Slow_battery_polling_keeps_minute_observations_and_real_gaps()
    {
        var recorder = new BatteryRecorder();
        var battery = new BatteryReading(80, BatteryPowerState.OnBattery, false);
        var saved = new List<BatterySample>();
        for (int seconds = 0; seconds <= 60; seconds += 10)
            if (recorder.Observe(Epoch.AddSeconds(seconds), seconds, battery, 10) is { } observation) saved.Add(observation);
        Assert.Equal(2, saved.Count);
        Assert.Equal(saved[0].Segment, saved[1].Segment);
        var resumed = recorder.Observe(Epoch.AddSeconds(90), 90, battery, 10)!;
        Assert.NotEqual(saved[1].Segment, resumed.Segment);
    }

    [Theory]
    [InlineData(0.5, true)] [InlineData(2, true)] [InlineData(5, false)] [InlineData(10, false)]
    public void Cooling_records_sparse_history_without_relaxing_the_thirty_sample_floor(double interval, bool canQualify)
    {
        var aggregator = new ThermalAggregator();
        var windows = new List<ThermalWindow>();
        for (double seconds = 0; seconds <= 1200; seconds += interval)
            windows.AddRange(aggregator.Add(Thermal(seconds, interval)));
        Assert.Equal(20, windows.Count);
        Assert.All(windows, window =>
        {
            Assert.Equal(interval, window.PollingIntervalSeconds);
            Assert.DoesNotContain("Gap", window.Quality);
            Assert.Equal((int)(60 / interval), window.SampleCount);
        });
        Assert.Equal(canQualify, windows.Any(window => window.Eligible));
        Assert.All(windows.Where(window => window.Eligible), window => Assert.True(window.SampleCount >= 30));
    }

    [Fact]
    public void Changing_thermal_cadence_ends_the_partial_window_and_real_gaps_stay_visible()
    {
        var aggregator = new ThermalAggregator();
        aggregator.Add(Thermal(0, 1)); aggregator.Add(Thermal(1, 1));
        var changed = Assert.Single(aggregator.Add(Thermal(2, 10)));
        Assert.False(changed.Eligible);
        Assert.Contains("cadence", changed.Quality, StringComparison.OrdinalIgnoreCase);
        var gap = Assert.Single(aggregator.Add(Thermal(30, 10)));
        Assert.False(gap.Eligible);
        Assert.Contains("Gap", gap.Quality);
    }

    private static ThermalSample Thermal(double seconds, double interval) =>
        new(Device, "session", Epoch.AddSeconds(seconds), seconds, 60, 50, 100, 3000, null)
            { PollingIntervalSeconds = interval };
}
