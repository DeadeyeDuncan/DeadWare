using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class UsageHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string GpuId = "/gpu-nvidia/0";

    private static Snapshot Sample(double seconds, double interval = 1, double? cpu = 40, double? cpuTemp = 55, double? gpu = 20,
        double? gpuTemp = 48, double? memory = 61, double? download = 1000, double? upload = 200, DateTimeOffset? timestamp = null)
    {
        var values = new Dictionary<string, double?>
        {
            ["cpu/usage"] = cpu, ["cpu/temperature"] = cpuTemp, [GpuId + "/usage"] = gpu, [GpuId + "/temperature"] = gpuTemp,
            ["memory/usage"] = memory, ["network/download"] = download, ["network/upload"] = upload,
        };
        return new Snapshot(timestamp ?? Start.AddSeconds(seconds), seconds, values) { PollingIntervalSeconds = interval };
    }

    private static UsageMetricMinute Metric(UsageMinuteBatch batch, string metric)
        => Assert.Single(batch.Metrics, row => row.Metric == metric);

    private static List<UsageMinuteBatch> Run(UsageMinuteFolder folder, double from, double to, double interval = 1,
        Func<double, Snapshot>? make = null, IReadOnlyList<ProcessTraffic>? processes = null, bool capture = true, string? gpu = GpuId)
    {
        var closed = new List<UsageMinuteBatch>();
        for (double seconds = from; seconds < to; seconds += interval)
            if (folder.Observe(make?.Invoke(seconds) ?? Sample(seconds, interval), processes ?? [], capture, gpu) is { } batch) closed.Add(batch);
        return closed;
    }

    [Fact]
    public void A_full_minute_at_one_second_polling_is_complete_with_exact_statistics()
    {
        var folder = new UsageMinuteFolder();
        var closed = Run(folder, 0, 61, make: seconds => Sample(seconds, cpu: seconds % 60));
        var batch = Assert.Single(closed);
        Assert.Equal(Start, batch.Minute);
        var cpu = Metric(batch, UsageMetrics.Cpu);
        Assert.Equal(60, cpu.Samples); Assert.True(cpu.Complete);
        Assert.Equal(0, cpu.Minimum); Assert.Equal(59, cpu.Maximum); Assert.Equal(29.5, cpu.Average, 9);
        Assert.Null(cpu.HardwareId);
        Assert.Equal(GpuId, Metric(batch, UsageMetrics.Gpu).HardwareId);
        Assert.Equal(GpuId, Metric(batch, UsageMetrics.GpuTemperature).HardwareId);
        Assert.Equal(7, batch.Metrics.Count);
        Assert.All(batch.Metrics, row => Assert.True(row.Complete));
        Assert.False(batch.CaptureGap);
        Assert.Equal(1000, Metric(batch, UsageMetrics.Download).Average);
        Assert.Equal(200, Metric(batch, UsageMetrics.Upload).Average);
    }

    [Fact]
    public void Ten_second_polling_needs_only_its_promised_samples_minus_one()
    {
        Assert.Equal(6, UsageMetrics.ExpectedSamples(10));
        Assert.Equal(120, UsageMetrics.ExpectedSamples(0.5));
        Assert.Equal(60, UsageMetrics.ExpectedSamples(double.NaN));
        var folder = new UsageMinuteFolder();
        var batch = Assert.Single(Run(folder, 0, 70, interval: 10));
        Assert.Equal(6, Metric(batch, UsageMetrics.Cpu).Samples);
        Assert.True(Metric(batch, UsageMetrics.Cpu).Complete);
        var slow = new UsageMinuteFolder();
        var late = Assert.Single(Run(slow, 10, 70, interval: 10));
        Assert.Equal(5, Metric(late, UsageMetrics.Cpu).Samples);
        Assert.True(Metric(late, UsageMetrics.Cpu).Complete);
        var sparse = new UsageMinuteFolder();
        var missing = Assert.Single(Run(sparse, 20, 70, interval: 10));
        Assert.Equal(4, Metric(missing, UsageMetrics.Cpu).Samples);
        Assert.False(Metric(missing, UsageMetrics.Cpu).Complete);
    }

    [Fact]
    public void Partial_first_and_flushed_last_minutes_are_incomplete()
    {
        var folder = new UsageMinuteFolder();
        var closed = Run(folder, 30, 90);
        var first = Assert.Single(closed);
        Assert.Equal(30, Metric(first, UsageMetrics.Cpu).Samples);
        Assert.False(Metric(first, UsageMetrics.Cpu).Complete);
        var last = folder.Flush();
        Assert.NotNull(last);
        Assert.Equal(Start.AddMinutes(1), last.Minute);
        Assert.Equal(30, Metric(last, UsageMetrics.Cpu).Samples);
        Assert.False(Metric(last, UsageMetrics.Cpu).Complete);
        Assert.Null(folder.Flush());
    }

    [Fact]
    public void A_polling_interval_change_inside_a_minute_leaves_it_incomplete()
    {
        var folder = new UsageMinuteFolder();
        var closed = new List<UsageMinuteBatch>();
        for (double seconds = 0; seconds < 30; seconds += 1) if (folder.Observe(Sample(seconds, 1), [], true, GpuId) is { } b) closed.Add(b);
        for (double seconds = 30; seconds < 70; seconds += 10) if (folder.Observe(Sample(seconds, 10), [], true, GpuId) is { } b) closed.Add(b);
        var batch = Assert.Single(closed);
        var cpu = Metric(batch, UsageMetrics.Cpu);
        Assert.Equal(33, cpu.Samples);
        Assert.False(cpu.Complete);
        Assert.False(batch.CaptureGap);
    }

    [Fact]
    public void Unavailable_readings_do_not_count_as_samples_and_absent_metrics_are_not_written()
    {
        var folder = new UsageMinuteFolder();
        var closed = Run(folder, 0, 61, make: seconds => Sample(seconds, cpuTemp: seconds < 30 ? null : 70, gpu: double.NaN, memory: double.PositiveInfinity));
        var batch = Assert.Single(closed);
        var temperature = Metric(batch, UsageMetrics.CpuTemperature);
        Assert.Equal(30, temperature.Samples); Assert.False(temperature.Complete); Assert.Equal(70, temperature.Average);
        Assert.DoesNotContain(batch.Metrics, row => row.Metric == UsageMetrics.Gpu);
        Assert.DoesNotContain(batch.Metrics, row => row.Metric == UsageMetrics.Memory);
        Assert.True(Metric(batch, UsageMetrics.Cpu).Complete);
        var noGpu = new UsageMinuteFolder();
        var withoutGpu = Assert.Single(Run(noGpu, 0, 61, gpu: null));
        Assert.DoesNotContain(withoutGpu.Metrics, row => row.HardwareId is not null);
        Assert.Equal(5, withoutGpu.Metrics.Count);
    }

    [Fact]
    public void Sleep_restart_and_clock_jumps_break_segments_and_completeness()
    {
        var folder = new UsageMinuteFolder();
        Run(folder, 0, 30);
        // Twenty minutes asleep: the open minute closes as discontinuous and the new one starts a segment.
        var afterSleep = folder.Observe(Sample(1230), [], true, GpuId);
        Assert.NotNull(afterSleep);
        Assert.Equal(Start, afterSleep.Minute);
        Assert.False(Metric(afterSleep, UsageMetrics.Cpu).Complete);
        Assert.True(afterSleep.CaptureGap);
        var resumed = Assert.Single(Run(folder, 1231, 1290));
        Assert.Equal(Start.AddSeconds(1230).AddSeconds(-30), resumed.Minute);
        Assert.NotEqual(afterSleep.Segment, resumed.Segment);
        Assert.False(Metric(resumed, UsageMetrics.Cpu).Complete);
        // Wall clock jumps an hour ahead while monotonic time continues. The minute collected before the
        // jump keeps its continuous segment; samples after the jump start a new one.
        var jumped = folder.Observe(Sample(1291, timestamp: Start.AddSeconds(1291).AddHours(1)), [], true, GpuId);
        Assert.NotNull(jumped);
        Assert.Equal(Start.AddSeconds(1260), jumped.Minute);
        Assert.Equal(resumed.Segment, jumped.Segment);
        Assert.False(Metric(jumped, UsageMetrics.Cpu).Complete);
        // Wall clock jumps backwards: the open minute closes rather than merging with an older one.
        var backwards = folder.Observe(Sample(1292, timestamp: Start.AddSeconds(1292)), [], true, GpuId);
        Assert.NotNull(backwards);
        Assert.Equal(Start.AddSeconds(1260).AddHours(1), backwards.Minute);
        Assert.NotEqual(jumped.Segment, backwards.Segment);
        Assert.Equal(1, Metric(backwards, UsageMetrics.Cpu).Samples);
        var fresh = new UsageMinuteFolder();
        Assert.Null(fresh.Observe(Sample(double.NaN), [], true, GpuId));
        Assert.Null(fresh.Flush());
    }

    [Fact]
    public void Repeated_or_reordered_publications_are_ignored()
    {
        var folder = new UsageMinuteFolder();
        Run(folder, 0, 30);
        Assert.Null(folder.Observe(Sample(29, cpu: 100), [], true, GpuId));
        Assert.Null(folder.Observe(Sample(10, cpu: 100), [], true, GpuId));
        var batch = Assert.Single(Run(folder, 30, 61));
        Assert.Equal(60, Metric(batch, UsageMetrics.Cpu).Samples);
        Assert.Equal(40, Metric(batch, UsageMetrics.Cpu).Maximum);
    }

    [Fact]
    public void Switching_gpu_inside_a_minute_keeps_separate_hardware_rows()
    {
        var folder = new UsageMinuteFolder();
        var closed = new List<UsageMinuteBatch>();
        for (double seconds = 0; seconds < 61; seconds++)
        {
            string gpu = seconds < 20 ? GpuId : "/gpu-amd/1";
            var values = new Dictionary<string, double?> { ["cpu/usage"] = 10, [gpu + "/usage"] = seconds < 20 ? 30 : 80 };
            if (folder.Observe(new Snapshot(Start.AddSeconds(seconds), seconds, values), [], true, gpu) is { } b) closed.Add(b);
        }
        var batch = Assert.Single(closed);
        var rows = batch.Metrics.Where(row => row.Metric == UsageMetrics.Gpu).OrderBy(row => row.Samples).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(GpuId, rows[0].HardwareId); Assert.Equal(20, rows[0].Samples); Assert.Equal(30, rows[0].Average); Assert.False(rows[0].Complete);
        Assert.Equal("/gpu-amd/1", rows[1].HardwareId); Assert.Equal(40, rows[1].Samples); Assert.Equal(80, rows[1].Average); Assert.False(rows[1].Complete);
        Assert.True(Metric(batch, UsageMetrics.Cpu).Complete);
    }

    [Fact]
    public void Application_bytes_sum_same_names_across_pids_and_count_only_growth()
    {
        var folder = new UsageMinuteFolder();
        // The first publication establishes baselines only: its counters predate this folder.
        Assert.Null(folder.Observe(Sample(0), [new ProcessTraffic(10, 1, "chrome.exe", null, null, 5000, 1000)], true, GpuId));
        Assert.Null(folder.Observe(Sample(1), [
            new ProcessTraffic(10, 1, "chrome.exe", null, null, 5300, 1100),
            new ProcessTraffic(11, 1, "Chrome.exe", null, null, 700, 50),
            new ProcessTraffic(12, 1, "steam.exe", null, null, 0, 0)], true, GpuId));
        Assert.Null(folder.Observe(Sample(2), [
            new ProcessTraffic(10, 1, "chrome.exe", null, null, 5300, 1100),
            new ProcessTraffic(11, 1, "Chrome.exe", null, null, 900, 50),
            new ProcessTraffic(12, 1, "steam.exe", null, null, 4000, 10)], true, GpuId));
        var batch = folder.Observe(Sample(60), [], true, GpuId);
        Assert.NotNull(batch);
        Assert.False(batch.CaptureGap);
        var chrome = Assert.Single(batch.Applications, app => app.Name == "chrome.exe");
        Assert.Equal((1200L, 150L), (chrome.Received, chrome.Sent));
        var steam = Assert.Single(batch.Applications, app => app.Name == "steam.exe");
        Assert.Equal((4000L, 10L), (steam.Received, steam.Sent));
        Assert.Equal(2, batch.Applications.Count);
        Assert.All(batch.Applications, app => Assert.Equal(Start, app.Minute));
    }

    [Fact]
    public void Pid_reuse_and_counter_resets_never_double_count()
    {
        var folder = new UsageMinuteFolder();
        Assert.Null(folder.Observe(Sample(0), [new ProcessTraffic(10, 1, "app.exe", null, null, 100, 0)], true, GpuId));
        // Same PID, new generation: a different process whose bytes all belong to this minute.
        Assert.Null(folder.Observe(Sample(1), [new ProcessTraffic(10, 2, "app.exe", null, null, 40, 0)], true, GpuId));
        // Counter moved backwards for the same process: the ledger reset, so only the new value counts.
        Assert.Null(folder.Observe(Sample(2), [new ProcessTraffic(10, 2, "app.exe", null, null, 15, 0)], true, GpuId));
        Assert.Null(folder.Observe(Sample(3), [new ProcessTraffic(10, 2, "app.exe", null, null, 25, 0)], true, GpuId));
        var batch = folder.Observe(Sample(60), [], true, GpuId)!;
        var app = Assert.Single(batch.Applications);
        Assert.Equal(40 + 15 + 10, app.Received);
    }

    [Fact]
    public void Names_are_trimmed_bounded_and_never_empty()
    {
        Assert.Equal("chrome.exe", UsageMetrics.NormalizeName("  chrome.exe \t"));
        Assert.Equal(UsageMetrics.UnknownName, UsageMetrics.NormalizeName(null));
        Assert.Equal(UsageMetrics.UnknownName, UsageMetrics.NormalizeName(" \r\n"));
        Assert.Equal("ab", UsageMetrics.NormalizeName("a\u0000b"));
        Assert.Equal(UsageMetrics.MaximumNameLength, UsageMetrics.NormalizeName(new string('x', 1000)).Length);
        var folder = new UsageMinuteFolder();
        Assert.Null(folder.Observe(Sample(0), [], true, GpuId));
        Assert.Null(folder.Observe(Sample(1), [new ProcessTraffic(1, 1, "   ", null, null, 10, 0), new ProcessTraffic(2, 1, new string('y', 300), null, null, 5, 5)], true, GpuId));
        var batch = folder.Observe(Sample(60), [], true, GpuId)!;
        Assert.Contains(batch.Applications, app => app.Name == UsageMetrics.UnknownName && app.Received == 10);
        Assert.Contains(batch.Applications, app => app.Name.Length == UsageMetrics.MaximumNameLength && app.Sent == 5);
    }

    [Fact]
    public void Unavailable_capture_marks_a_gap_instead_of_zero_traffic()
    {
        var folder = new UsageMinuteFolder();
        Run(folder, 0, 30, capture: false);
        var batch = Assert.Single(Run(folder, 30, 61, capture: true));
        Assert.True(batch.CaptureGap);
        Assert.Empty(batch.Applications);
        Assert.True(Metric(batch, UsageMetrics.Cpu).Complete);
        var next = Assert.Single(Run(folder, 61, 121));
        Assert.False(next.CaptureGap);
    }

    [Fact]
    public void Metric_identifiers_are_fixed_and_gpu_rows_carry_hardware()
    {
        Assert.Equal(new[] { "cpu", "cputemp", "gpu", "gputemp", "memory", "download", "upload" }, UsageMetrics.All);
        Assert.True(UsageMetrics.IsKnown("cpu")); Assert.False(UsageMetrics.IsKnown("gpufan")); Assert.False(UsageMetrics.IsKnown(null));
        Assert.True(UsageMetrics.UsesHardwareId("gputemp")); Assert.False(UsageMetrics.UsesHardwareId("memory"));
        var local = new DateTimeOffset(2026, 11, 2, 7, 5, 59, 999, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 5, 0, TimeSpan.Zero), UsageMetrics.Floor(local));
    }
}
