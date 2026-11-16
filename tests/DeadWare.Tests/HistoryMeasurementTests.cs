using System.Diagnostics;
using DeadWare.Core;
using Xunit;
using Xunit.Abstractions;

namespace DeadWare.Tests;

/// <summary>
/// Chart build time and history memory for six hours at half-second polling, written to the test output rather than asserted;
/// the build filter leaves *MeasurementTests out. Run them with --filter FullyQualifiedName~HistoryMeasurementTests and
/// --logger "console;verbosity=detailed". Only public members are used, so the same file measures an older build to compare.
/// </summary>
public sealed class HistoryMeasurementTests(ITestOutputHelper output)
{
    const double Interval = 0.5;
    const int Polls = 43200;
    static readonly string[] Drives = ["d0", "d1", "d2", "d3", "d4", "d5", "d6"];

    /// <summary>The widget's 30 lines: CPU and GPU with fan and pump, three cores, memory, network with its local share, and seven drives.</summary>
    static readonly string[] Lines = ["cpu/usage", "cpu/temperature", "cpu/pump", "gpu0/usage", "gpu0/temperature", "gpu0/fan", "memory/usage",
        "network/download", "network/upload", "network/download/local", "network/upload/local", "storage/total/read", "storage/total/write",
        "cpu/core/0/usage", "cpu/core/1/usage", "cpu/core/2/usage", .. Drives.SelectMany(drive => new[] { $"storage/{drive}/read", $"storage/{drive}/write" })];

    static SampleHistory SixHours(string[] metrics, int polls = Polls)
    {
        var history = new SampleHistory(); var random = new Random(1);
        for (int i = 0; i < polls; i++)
        {
            var values = new Dictionary<string, double?>(metrics.Length);
            foreach (string metric in metrics) values[metric] = random.Next(0, 1000);
            history.Add(new Snapshot(DateTimeOffset.UnixEpoch.AddSeconds(i * Interval), i * Interval, values) { PollingIntervalSeconds = Interval });
        }
        return history;
    }

    [Fact]
    public void Six_hour_widget_chart_builds()
    {
        Assert.Equal(30, Lines.Length);
        var history = SixHours(Lines);
        double now = (Polls - 1) * Interval;
        var plot = new ChartPlot(8, 14, 600, 120);
        (string Kind, bool Compare, HistoryChartOptions Options)[] widget =
        [
            ("cpu", true, new(Cores: 3, CoresWithDevices: true)), ("cputemp", true, new(GpuFan: true, CpuPump: true, PumpWithDevices: true)),
            ("memory", false, new()), ("network", false, new()), ("storage", false, new(Drives: Drives)),
        ];
        foreach (bool smoothed in new[] { true, false })
        {
            double total = 0;
            foreach (var chart in widget)
            {
                var request = new HistoryChartRequest(history, chart.Kind, "gpu0", chart.Compare, HistoryRanges.MaximumSeconds, now, DateTimeOffset.UnixEpoch.AddSeconds(now),
                    plot, TimeZoneInfo.Utc, Options: chart.Options with { Smoothed = smoothed }, Lead: 1);
                for (int i = 0; i < 3; i++) HistoryChartModel.Build(request);
                var milliseconds = new double[9];
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < milliseconds.Length; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    HistoryChartModel.Build(request);
                    milliseconds[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                double kilobytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1024d / milliseconds.Length;
                Array.Sort(milliseconds);
                total += milliseconds[milliseconds.Length / 2];
                output.WriteLine($"{(smoothed ? "smoothed" : "raw"),-8} {chart.Kind,-8} median {milliseconds[milliseconds.Length / 2],7:0.00} ms, {kilobytes,8:0} KB allocated a build");
            }
            output.WriteLine($"{(smoothed ? "smoothed" : "raw"),-8} all five widget charts: {total:0.00} ms a refresh");
        }
    }

    [Fact]
    public void Six_hour_history_memory()
    {
        // 300 values a poll, as with seven drives and extended sensors. Polls past the 1,024 of a chunk show its steady cost.
        var metrics = Enumerable.Range(0, 300).Select(i => $"/hardware/{i / 10}/sensor/{i % 10}").ToArray();
        const int polls = 8192;
        long before = GC.GetTotalMemory(true);
        var history = SixHours(metrics, polls);
        long after = GC.GetTotalMemory(true);
        double perPoll = (after - before) / (double)polls;
        output.WriteLine($"{perPoll:0} bytes held a poll of 300 values; {perPoll * HistoryRanges.MaximumSamples / 1048576:0} MB for six hours at 0.5 s ({history.Count} polls measured)");
        // The Sensors page reads every sensor's minimum, average and maximum over the range on each refresh.
        var milliseconds = new double[5];
        for (int i = 0; i < milliseconds.Length; i++)
        {
            long started = Stopwatch.GetTimestamp();
            history.Summaries(metrics, polls * Interval, HistoryRanges.MaximumSeconds);
            milliseconds[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(milliseconds);
        double median = milliseconds[milliseconds.Length / 2];
        output.WriteLine($"Sensors page statistics for 300 sensors: {median:0.0} ms over {polls} polls, about {median * HistoryRanges.MaximumSamples / polls:0} ms over six hours");
        GC.KeepAlive(history);
    }
}
