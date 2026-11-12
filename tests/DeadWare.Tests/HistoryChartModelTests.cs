using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HistoryChartModelTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    static readonly ChartPlot Plot = new(Left: 10, Top: 20, Width: 600, Height: 100);

    static SampleHistory History(params (double Seconds, string Metric, double? Value)[] samples)
    {
        var history = new SampleHistory();
        foreach (var group in samples.GroupBy(s => s.Seconds).OrderBy(g => g.Key))
            history.Add(new Snapshot(Epoch.AddSeconds(group.Key), group.Key, group.ToDictionary(s => s.Metric, s => s.Value)));
        return history;
    }

    static HistoryChartRequest Request(string kind, SampleHistory history, double now = 60, int seconds = 60, string gpu = "gpu0", bool compare = false) =>
        new(history, kind, gpu, compare, seconds, now, Epoch.AddSeconds(now), Plot, TimeZoneInfo.Utc);

    [Fact] public void A_percentage_chart_has_a_fixed_0_to_100_axis_and_maps_time_right_to_left()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((58, "cpu/usage", 0), (59, "cpu/usage", 50), (60, "cpu/usage", 100))));
        Assert.Equal(100, chart.Maximum);
        Assert.Equal(["0%", "50%", "100%"], chart.Lines.Select(l => l.Label));
        Assert.Equal([120d, 70d, 20d], chart.Lines.Select(l => l.Y));
        var trace = Assert.Single(chart.Traces);
        Assert.Equal("cpu", trace.ColorKey); Assert.False(trace.Dashed); Assert.Equal(1, trace.Fill);
        var run = Assert.Single(trace.Runs);
        // One-second samples: a longer silence between samples is a gap, not a line.
        Assert.Equal([new ChartPoint(590, 120), new ChartPoint(600, 70), new ChartPoint(610, 20)], run);
    }

    [Fact] public void A_missing_reading_splits_the_trace_into_runs()
    {
        var chart = HistoryChartModel.Build(Request("memory", History((57, "memory/usage", 10), (58, "memory/usage", null), (59, "memory/usage", 30), (60, "memory/usage", 40))));
        var trace = Assert.Single(chart.Traces);
        Assert.Equal(2, trace.Runs.Count);
        Assert.Single(trace.Runs[0]); Assert.Equal(2, trace.Runs[1].Count);
        Assert.True(chart.HasReadings);
    }

    [Fact] public void Network_draws_download_solid_and_upload_dashed_on_one_byte_axis_with_headroom()
    {
        var chart = HistoryChartModel.Build(Request("network", History((0, "network/download", 2_000_000), (0, "network/upload", 500_000), (60, "network/download", 1_000_000), (60, "network/upload", 4_000_000))));
        Assert.Equal(4_600_000, chart.Maximum, 3);                       // the larger peak plus 15 %
        Assert.Equal(["0.0 B/s", "2.3 MB/s", "4.6 MB/s"], chart.Lines.Select(l => l.Label));
        Assert.Equal(["download", "upload"], chart.Traces.Select(t => t.ColorKey));
        Assert.Equal([false, true], chart.Traces.Select(t => t.Dashed));
        Assert.Equal([0.65, 0.42], chart.Traces.Select(t => t.Fill));
    }

    [Fact] public void A_quiet_byte_chart_keeps_a_floor_of_1000_bytes_a_second()
    {
        var chart = HistoryChartModel.Build(Request("storage", History((0, "storage/total/read", 10), (0, "storage/total/write", 0))));
        Assert.Equal(1000, chart.Maximum);
        Assert.Equal(["diskread", "diskwrite"], chart.Traces.Select(t => t.ColorKey));
    }

    [Fact] public void A_temperature_axis_grows_past_100_with_ten_percent_headroom()
    {
        var hot = HistoryChartModel.Build(Request("cputemp", History((0, "cpu/temperature", 110))));
        Assert.Equal(121, hot.Maximum, 3);
        Assert.Equal("121°", hot.Lines[2].Label);
        var cool = HistoryChartModel.Build(Request("cputemp", History((0, "cpu/temperature", 45))));
        Assert.Equal(100, cool.Maximum);
    }

    [Fact] public void Comparing_devices_adds_the_gpu_dashed_and_rounds_the_temperature_axis_up_to_a_multiple_of_20()
    {
        var chart = HistoryChartModel.Build(Request("cputemp", History((0, "cpu/temperature", 95), (0, "gpu0/temperature", 60)), compare: true));
        Assert.Equal(120, chart.Maximum);                                 // 95 * 1.1 = 104.5, rounded up to 120
        Assert.Equal(["cputemp", "gputemp"], chart.Traces.Select(t => t.ColorKey));
        Assert.Equal([false, true], chart.Traces.Select(t => t.Dashed));
        Assert.Equal("120°C", chart.Lines[2].Label);
        var usage = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 5), (0, "gpu0/usage", 9)), compare: true));
        Assert.Equal(["cpu", "gpu"], usage.Traces.Select(t => t.ColorKey));
        Assert.Equal([0.65, 0.45], usage.Traces.Select(t => t.Fill));
    }

    [Fact] public void A_gpu_chart_reads_the_selected_gpu_and_none_selected_reads_nothing()
    {
        var history = History((0, "gpu1/usage", 42), (60, "gpu1/usage", 43));
        Assert.True(HistoryChartModel.Build(Request("gpu", history, gpu: "gpu1")).HasReadings);
        var none = HistoryChartModel.Build(Request("gpu", history, gpu: null!));
        Assert.False(none.HasReadings);
        Assert.Empty(Assert.Single(none.Traces).Runs);
    }

    [Fact] public void A_silence_between_samples_is_a_gap_not_a_line()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 10), (1, "cpu/usage", 10), (59, "cpu/usage", 20), (60, "cpu/usage", 20))));
        Assert.Equal(2, Assert.Single(chart.Traces).Runs.Count);
    }

    [Fact] public void Statistics_summarise_the_primary_trace_over_the_range()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 10), (30, "cpu/usage", 20), (60, "cpu/usage", 60))));
        Assert.Equal(new Summary(10, 60, 30), chart.Statistics);
    }

    [Fact] public void Samples_older_than_the_range_are_left_out()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 99), (100, "cpu/usage", 1), (120, "cpu/usage", 2)), now: 120, seconds: 60));
        Assert.All(Assert.Single(chart.Traces).Runs.SelectMany(r => r), p => Assert.InRange(p.X, Plot.Left, Plot.Left + Plot.Width));
        Assert.Equal(2, chart.Statistics.Maximum);
    }

    [Fact] public void Time_ticks_are_placed_across_the_plot_by_age()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 1)), now: 60, seconds: 60));
        Assert.NotEmpty(chart.Ticks);
        Assert.All(chart.Ticks, t => Assert.InRange(t.X, Plot.Left, Plot.Left + Plot.Width));
        Assert.Equal(chart.Ticks.OrderBy(t => t.X).Select(t => t.Label), chart.Ticks.Select(t => t.Label));
    }

    [Theory]
    [InlineData(0, 100)] [InlineData(600, 0)] [InlineData(-5, 100)] [InlineData(double.NaN, 100)]
    public void A_plot_with_no_area_gives_an_empty_chart_without_throwing(double width, double height)
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 1))) with { Plot = new ChartPlot(0, 0, width, height) });
        Assert.Empty(chart.Traces); Assert.Empty(chart.Lines); Assert.Empty(chart.Ticks); Assert.False(chart.HasReadings);
    }

    [Fact] public void An_empty_history_gives_axes_and_no_readings()
    {
        var chart = HistoryChartModel.Build(Request("cpu", new SampleHistory()));
        Assert.False(chart.HasReadings);
        Assert.Equal(3, chart.Lines.Count);
        Assert.Equal(new Summary(null, null, null), chart.Statistics);
    }

    [Fact] public void An_unknown_kind_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => HistoryChartModel.Build(Request("nonsense", new SampleHistory())));
    }

    [Theory]
    [InlineData(null, "—")] [InlineData(double.NaN, "—")] [InlineData(999d, "999.0 B/s")] [InlineData(1000d, "1.0 KB/s")] [InlineData(2_500_000d, "2.5 MB/s")]
    public void Byte_rates_are_formatted_in_decimal_units(double? value, string expected) => Assert.Equal(expected, HistoryChartModel.Bytes(value, rate: true));
}
