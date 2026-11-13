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
        var totals = chart.Traces.Where(t => t.Role == ChartTraceRole.Main).ToArray();
        Assert.Equal(["download", "upload"], totals.Select(t => t.ColorKey));
        Assert.Equal([false, true], totals.Select(t => t.Dashed));
        Assert.Equal([0.65, 0.42], totals.Select(t => t.Fill));
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

    [Fact] public void Hovering_a_sample_gives_its_time_value_and_dot()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((58, "cpu/usage", 0), (59, "cpu/usage", 50), (60, "cpu/usage", 100))));
        var hover = HistoryChartModel.Hover(chart, x: 601, y: 60, pollingSeconds: 1)!;       // nearest to the sample at 59 s (x = 600)
        Assert.Equal(600, hover.X);
        Assert.Equal("00:00:59", hover.Time);
        var line = Assert.Single(hover.Lines);
        Assert.Equal(("cpu", "50%", (ChartPoint?)new ChartPoint(600, 70)), (line.ColorKey, line.Text, line.Dot));
    }

    [Fact] public void Hovering_a_two_trace_chart_lists_both_in_trace_order()
    {
        var chart = HistoryChartModel.Build(Request("network", History((59, "network/download", 2_000_000), (59, "network/upload", 500_000), (60, "network/download", 1_000_000), (60, "network/upload", 4_000_000))));
        var hover = HistoryChartModel.Hover(chart, x: 610, y: 30, pollingSeconds: 1)!;
        Assert.Equal(["download", "upload"], hover.Lines.Select(l => l.ColorKey));
        Assert.Equal(["1.0 MB/s", "4.0 MB/s"], hover.Lines.Select(l => l.Text));
    }

    [Fact] public void Hovering_a_gap_or_outside_the_plot_gives_nothing()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 10), (1, "cpu/usage", 10), (59, "cpu/usage", 20), (60, "cpu/usage", 20))));
        Assert.Null(HistoryChartModel.Hover(chart, x: 310, y: 60, pollingSeconds: 1));     // 30 s: nothing was sampled near it
        Assert.Null(HistoryChartModel.Hover(chart, x: 5, y: 60, pollingSeconds: 1));       // left of the plot
        Assert.Null(HistoryChartModel.Hover(chart, x: 600, y: 300, pollingSeconds: 1));    // below the plot
        Assert.NotNull(HistoryChartModel.Hover(chart, x: 605, y: 60, pollingSeconds: 1));
    }

    [Fact] public void Hovering_an_empty_chart_gives_nothing()
    {
        var empty = HistoryChartModel.Build(Request("cpu", History((0, "cpu/usage", 1))) with { Plot = new ChartPlot(0, 0, 0, 0) });
        Assert.Null(HistoryChartModel.Hover(empty, 10, 10, 1));
        Assert.Null(HistoryChartModel.Hover(HistoryChartModel.Build(Request("cpu", new SampleHistory())), 300, 60, 1));
    }

    [Fact] public void A_trace_with_no_reading_at_the_hovered_time_is_left_out_of_the_readout()
    {
        var chart = HistoryChartModel.Build(Request("cpu", History((59, "cpu/usage", 5), (60, "cpu/usage", 6), (60, "gpu0/usage", 9)), compare: true));
        Assert.Equal(["cpu"], HistoryChartModel.Hover(chart, x: 600, y: 60, pollingSeconds: 0.25)!.Lines.Select(l => l.ColorKey));
        Assert.Equal(["cpu", "gpu"], HistoryChartModel.Hover(chart, x: 610, y: 60, pollingSeconds: 0.25)!.Lines.Select(l => l.ColorKey));
    }

    static (double, string, double?)[] At(double seconds, params (string Metric, double? Value)[] values) => values.Select(v => (seconds, v.Metric, v.Value)).ToArray();
    static SampleHistory Two(params (string Metric, double? Value)[] values) => History([.. At(59, values), .. At(60, values)]);

    [Fact] public void Per_core_traces_join_a_cpu_chart_and_hide_the_aggregate_line()
    {
        var history = Two(("cpu/usage", 40), ("cpu/core/0/usage", 30), ("cpu/core/1/usage", 50));
        var chart = HistoryChartModel.Build(Request("cpu", history) with { Options = new(Cores: 2) });
        Assert.Equal([ChartTraceRole.Main, ChartTraceRole.Core, ChartTraceRole.Core], chart.Traces.Select(t => t.Role));
        Assert.True(chart.Traces[0].Hidden);
        Assert.Equal(["cpucore/0", "cpucore/1"], chart.Traces.Skip(1).Select(t => t.ColorKey));
        Assert.Equal([0, 1], chart.Traces.Skip(1).Select(t => t.Index));
        Assert.All(chart.Traces.Skip(1), t => { Assert.Equal(0, t.Fill); Assert.Single(t.Runs); });
    }

    [Fact] public void Cores_stay_off_a_device_comparison_unless_asked_for()
    {
        var history = Two(("cpu/usage", 40), ("gpu0/usage", 10), ("cpu/core/0/usage", 30));
        var plain = HistoryChartModel.Build(Request("cpu", history, compare: true) with { Options = new(Cores: 1) });
        Assert.DoesNotContain(plain.Traces, t => t.Role == ChartTraceRole.Core);
        Assert.False(plain.Traces[0].Hidden);
        var widget = HistoryChartModel.Build(Request("cpu", history, compare: true) with { Options = new(Cores: 1, CoresWithDevices: true) });
        Assert.Contains(widget.Traces, t => t.Role == ChartTraceRole.Core);
    }

    [Fact] public void Fan_and_pump_share_a_left_rpm_axis_rounded_up_to_a_thousand()
    {
        var history = Two(("cpu/temperature", 60), ("gpu0/temperature", 50), ("gpu0/fan", 1450), ("cpu/pump", 2300));
        var chart = HistoryChartModel.Build(Request("cputemp", history, compare: true) with { Options = new(GpuFan: true, CpuPump: true, PumpWithDevices: true) });
        Assert.Equal(3000, chart.RpmMaximum);
        Assert.Equal(["0 RPM", "1500 RPM", "3000 RPM"], chart.RpmLines.Select(l => l.Label));
        var rpm = chart.Traces.Where(t => t.Role == ChartTraceRole.Rpm).ToArray();
        Assert.Equal(["gpufan", "cpupump"], rpm.Select(t => t.ColorKey));
        Assert.All(rpm, t => { Assert.True(t.Dotted); Assert.Equal(0, t.Fill); Assert.Equal(3000, t.Maximum); });
        Assert.Equal(Plot.Top + Plot.Height * (1 - 1450d / 3000), rpm[0].Runs[0][0].Y, 6);
    }

    [Fact] public void Fan_and_pump_follow_the_dashboard_rules_for_which_charts_carry_them()
    {
        var history = Two(("cpu/temperature", 60), ("gpu0/temperature", 50), ("gpu0/fan", 1450), ("cpu/pump", 2300));
        string[] Rpm(string kind, bool compare, HistoryChartOptions options) =>
            HistoryChartModel.Build(Request(kind, history, compare: compare) with { Options = options }).Traces.Where(t => t.Role == ChartTraceRole.Rpm).Select(t => t.ColorKey).ToArray();
        var both = new HistoryChartOptions(GpuFan: true, CpuPump: true);
        Assert.Equal(["gpufan"], Rpm("gputemp", false, both));                 // the GPU chart carries its fan
        Assert.Equal(["cpupump"], Rpm("cputemp", false, both));                // the CPU chart alone carries the pump, not the GPU fan
        Assert.Equal(["gpufan"], Rpm("cputemp", true, both));                  // compared: the fan joins, the pump leaves
        Assert.Empty(Rpm("cpu", false, both));
        var none = HistoryChartModel.Build(Request("cputemp", history));
        Assert.Empty(none.RpmLines); Assert.Equal(0, none.RpmMaximum);
    }

    [Fact] public void Network_carries_the_local_share_muted_under_the_totals()
    {
        var history = Two(("network/download", 2_000_000), ("network/upload", 1_000_000), ("network/download/local", 500_000), ("network/upload/local", 100_000));
        var chart = HistoryChartModel.Build(Request("network", history));
        Assert.Equal(["network/download", "network/upload", "network/download/local", "network/upload/local"], chart.Traces.Select(t => t.Metric));
        Assert.Equal([ChartTraceRole.Main, ChartTraceRole.Main, ChartTraceRole.Local, ChartTraceRole.Local], chart.Traces.Select(t => t.Role));
        Assert.Equal(["download", "upload", "download", "upload"], chart.Traces.Select(t => t.ColorKey));
        Assert.Equal([0.65, 0.42, 0.3, 0.2], chart.Traces.Select(t => t.Fill));
        Assert.Equal([false, true, false, true], chart.Traces.Select(t => t.Dashed));
    }

    [Fact] public void Storage_traces_each_drive_s_read_rate_after_the_totals()
    {
        var history = Two(("storage/total/read", 3_000_000), ("storage/total/write", 1_000_000), ("storage/d0/read", 1_000_000), ("storage/d1/read", 2_000_000));
        var chart = HistoryChartModel.Build(Request("storage", history) with { Options = new(Drives: ["d0", "d1"]) });
        var drives = chart.Traces.Where(t => t.Role == ChartTraceRole.Drive).ToArray();
        Assert.Equal(["storage/d0/read", "storage/d1/read"], drives.Select(t => t.Metric));
        Assert.Equal([0, 1], drives.Select(t => t.Index));
        Assert.All(drives, t => Assert.Equal("diskread", t.ColorKey));
    }

    [Fact] public void Hover_lists_the_eight_busiest_cores_and_counts_the_rest()
    {
        var values = Enumerable.Range(0, 10).Select(i => ($"cpu/core/{i}/usage", (double?)(i * 5))).Append(("cpu/usage", 40)).ToArray();
        var chart = HistoryChartModel.Build(Request("cpu", Two(values)) with { Options = new(Cores: 10) });
        var hover = HistoryChartModel.Hover(chart, 610, 60, 1)!;
        Assert.Equal("40%", hover.Lines[0].Text); Assert.Null(hover.Lines[0].Dot);          // the hidden aggregate keeps its line, without a dot
        Assert.Equal("Core 10 · 45%", hover.Lines[1].Text); Assert.Equal(9, hover.Lines[1].Index);
        Assert.Equal("Core 3 · 10%", hover.Lines[8].Text);
        Assert.Equal(("+2 more", "muted"), (hover.Lines[9].Text, hover.Lines[9].ColorKey));
        Assert.Equal(10, hover.Lines.Count);
    }

    [Fact] public void Hover_gives_the_local_share_one_muted_line_and_the_fan_in_rpm()
    {
        var network = HistoryChartModel.Build(Request("network", Two(("network/download", 2_000_000), ("network/upload", 3_000_000), ("network/download/local", 1_000_000), ("network/upload/local", 2_000_000))));
        var lines = HistoryChartModel.Hover(network, 610, 60, 1)!.Lines;
        Assert.Equal(["2.0 MB/s", "3.0 MB/s", "Local ↓ 1.0 MB/s ↑ 2.0 MB/s"], lines.Select(l => l.Text));
        Assert.Equal("muted", lines[2].ColorKey); Assert.Null(lines[2].Dot);
        var gpu = HistoryChartModel.Build(Request("gputemp", Two(("gpu0/temperature", 50), ("gpu0/fan", 1450))) with { Options = new(GpuFan: true) });
        Assert.Equal(["50°", "1450 RPM"], HistoryChartModel.Hover(gpu, 610, 60, 1)!.Lines.Select(l => l.Text));
    }

    [Fact] public void Hover_lists_drives_busiest_first_by_name_and_skips_idle_unknowns()
    {
        var history = Two(("storage/total/read", 4_000_000), ("storage/total/write", 1_000_000), ("storage/d0/read", 1_000_000), ("storage/d0/write", 0), ("storage/d1/read", 3_000_000), ("storage/d1/write", 1_000_000));
        var chart = HistoryChartModel.Build(Request("storage", history) with { Options = new(Drives: ["d0", "d1", "d2"], DriveNames: new Dictionary<string, string> { ["d0"] = "Samsung", ["d1"] = "WD" }) });
        var lines = HistoryChartModel.Hover(chart, 610, 60, 1)!.Lines;
        Assert.Equal(["4.0 MB/s", "1.0 MB/s", "WD · 3.0 MB/s ↓ 1.0 MB/s ↑", "Samsung · 1.0 MB/s ↓ 0.0 B/s ↑"], lines.Select(l => l.Text));
        Assert.Equal([1, 0], lines.Skip(2).Select(l => l.Index));
    }

    [Theory]
    [InlineData(null, "—")] [InlineData(double.NaN, "—")] [InlineData(999d, "999.0 B/s")] [InlineData(1000d, "1.0 KB/s")] [InlineData(2_500_000d, "2.5 MB/s")]
    public void Byte_rates_are_formatted_in_decimal_units(double? value, string expected) => Assert.Equal(expected, HistoryChartModel.Bytes(value, rate: true));
}
