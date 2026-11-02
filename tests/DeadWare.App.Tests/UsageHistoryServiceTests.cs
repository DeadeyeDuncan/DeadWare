using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class UsageHistoryServiceTests
{
    private static readonly DateTimeOffset Start = UsageMetrics.Floor(DateTimeOffset.UtcNow.AddMinutes(-10));

    [Fact]
    public async Task Normal_quit_saves_the_closed_minute_complete_and_the_open_minute_incomplete()
    {
        using var fixture = new Fixture(); var service = new UsageHistoryService(fixture.Path, "tablet");
        service.Start();
        for (int i = 0; i < 90; i++) service.TryRecord(Reading(Start.AddSeconds(i), i, cpu: i % 60,
            processes: [new ProcessTraffic(10, 1, "chrome.exe", null, null, 100 * (i + 1), 10 * (i + 1))]));
        await service.StopAsync();
        Assert.Equal(UsageHistoryService.SavedStatus, service.Status);
        var reopened = new UsageHistoryService(fixture.Path, "tablet");
        var view = await reopened.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(2), 2);
        Assert.Equal(2, view.Buckets.Count);
        Assert.Equal((60L, 1L), (view.Buckets[0].Samples, view.Buckets[0].CompleteMinutes));
        Assert.Equal((30L, 0L), (view.Buckets[1].Samples, view.Buckets[1].CompleteMinutes));
        Assert.Equal(29.5, view.Buckets[0].Average, 9); Assert.Equal(0, view.Statistics.Minimum); Assert.Equal(59, view.Statistics.Maximum);
        Assert.Equal(1, (await reopened.ReadMetricAsync(UsageMetrics.Gpu, "Fixture GPU", Start, Start.AddMinutes(2))).Buckets.Sum(b => b.CompleteMinutes));
        var apps = await reopened.ReadApplicationsAsync(Start, Start.AddMinutes(2));
        var chrome = Assert.Single(apps.Applications);
        // The first publication only sets baselines, so 89 of the 90 publications contribute 100/10 bytes each.
        Assert.Equal(("chrome.exe", 8900L, 890L, 2L), (chrome.Name, chrome.Received, chrome.Sent, chrome.Minutes));
        Assert.Equal(0, apps.CaptureGapMinutes); Assert.Equal(2, apps.RecordedMinutes);
        await reopened.StopAsync();
    }

    [Fact]
    public async Task Pausing_and_resuming_recording_merges_the_minute_as_incomplete()
    {
        using var fixture = new Fixture(); var service = new UsageHistoryService(fixture.Path, "tablet");
        service.Start();
        for (int i = 0; i < 10; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
        service.Configure(false);
        for (int i = 10; i < 20; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
        Assert.Contains("paused", service.Status, StringComparison.OrdinalIgnoreCase);
        service.Configure(true);
        for (int i = 20; i < 61; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
        await service.StopAsync();
        var view = await service.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(1));
        var bucket = Assert.Single(view.Buckets);
        Assert.Equal(50, bucket.Samples); Assert.Equal(0, bucket.CompleteMinutes);
        Assert.Equal(1, (await service.ReadApplicationsAsync(Start, Start.AddMinutes(1))).CaptureGapMinutes);
    }

    [Fact]
    public async Task Queue_loss_leaves_the_minute_incomplete_and_counts_the_drop()
    {
        using var fixture = new Fixture(); var service = new UsageHistoryService(fixture.Path, "tablet");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var store = (UsageHistoryStore)typeof(UsageHistoryService).GetField("store", flags)!.GetValue(service)!;
        store.Initialize();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(UsageHistoryService).GetField("initialization", flags)!.SetValue(service, ready.Task);
        service.Start();
        try
        {
            for (int i = 0; i < 121; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
            Assert.Equal(1, service.DroppedSamples); ready.SetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (service.PendingSamples > 0) await Task.Delay(10, timeout.Token);
            for (int i = 121; i < 181; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
            await service.StopAsync();
            Assert.Contains("dropped", service.Status, StringComparison.OrdinalIgnoreCase);
            var view = store.ReadMetric("tablet", UsageMetrics.Cpu, null, Start, Start.AddMinutes(3), 3);
            Assert.Equal(3, view.Buckets.Count);
            // The dropped publication was minute 2's first second: minutes 0 and 1 were fully covered, minute 2 was not.
            Assert.Equal(1, view.Buckets[0].CompleteMinutes);
            Assert.Equal(1, view.Buckets[1].CompleteMinutes);
            Assert.Equal(0, view.Buckets[2].CompleteMinutes);
            Assert.Equal(1, store.ReadApplications("tablet", Start, Start.AddMinutes(3)).CaptureGapMinutes);
        }
        finally { ready.TrySetResult(); await service.StopAsync(); }
    }

    [Fact]
    public async Task A_corrupt_store_reports_failure_without_throwing_into_the_collector()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, "corrupt database fixture");
        var service = new UsageHistoryService(fixture.Path, "tablet"); service.Start();
        service.TryRecord(Reading(DateTimeOffset.UtcNow, 0)); await service.StopAsync();
        Assert.True(service.Failed);
        Assert.Contains("unavailable", service.Status, StringComparison.OrdinalIgnoreCase);
        var view = await service.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(1)); Assert.Empty(view.Buckets);
        Assert.Empty((await service.ReadApplicationsAsync(Start, Start.AddMinutes(1))).Applications);
        Assert.Equal("corrupt database fixture", await File.ReadAllTextAsync(fixture.Path));
    }

    [Fact]
    public async Task Timer_flushes_a_closed_minute_before_stop_and_exports_do_not_replace_files()
    {
        using var fixture = new Fixture(); var service = new UsageHistoryService(fixture.Path, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        service.Start();
        for (int i = 0; i <= 60; i++) service.TryRecord(Reading(Start.AddSeconds(i), i, processes: [new ProcessTraffic(1, 1, "steam.exe", null, null, i, 0)]));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await service.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(1))).Minutes == 0) await Task.Delay(20, deadline.Token);
            string metrics = fixture.Path + ".metrics.csv", apps = fixture.Path + ".apps.csv";
            await service.ExportCsvAsync(metrics, false, Start, Start.AddMinutes(1));
            await service.ExportCsvAsync(apps, true, Start, Start.AddMinutes(1));
            Assert.Equal(UsageMetrics.All.Count, (await File.ReadAllLinesAsync(metrics)).Length - 1);
            Assert.Equal(2, (await File.ReadAllLinesAsync(apps)).Length);
            await Assert.ThrowsAsync<IOException>(() => service.ExportCsvAsync(metrics, false, Start, Start.AddMinutes(1)));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReadMetricAsync("gpufan", null, Start, Start.AddMinutes(1)));
            Assert.False(service.Failed);
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public void Gpus_are_keyed_by_unique_model_name()
    {
        var reading = Reading(Start, 0) with { Gpus = [new("/gpu/0", "Model A"), new("/gpu/1", "Model B"), new("/gpu/2", "Model B"), new("/gpu/3", " ")] };
        Assert.Equal(new[] { ("/gpu/0", "Model A") }, UsageHistoryService.Gpus(reading));
    }

    private static Reading Reading(DateTimeOffset stamp, double seconds, double? cpu = 40, IReadOnlyList<ProcessTraffic>? processes = null) =>
        new(new Snapshot(stamp, seconds, new Dictionary<string, double?>
        {
            ["cpu/usage"] = cpu, ["cpu/temperature"] = 55, ["/gpu/0/usage"] = 20, ["/gpu/0/temperature"] = 48,
            ["memory/usage"] = 61, ["network/download"] = 1000, ["network/upload"] = 200,
        }), [new("/gpu/0", "Fixture GPU")], [], processes ?? [new ProcessTraffic(99, 1, "fixture.exe", null, null, 0, 0)], "CPU", "", "", "", false);
    // A zero-byte fixture process keeps per-application capture "available" without adding application rows.

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-usage-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "usage.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
