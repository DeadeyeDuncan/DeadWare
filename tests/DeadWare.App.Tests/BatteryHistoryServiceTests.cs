using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class BatteryHistoryServiceTests
{
    [Fact]
    public async Task Queue_loss_breaks_the_first_accepted_observation_after_the_loss()
    {
        using var fixture = new Fixture(); var service = new BatteryHistoryService(fixture.Path, "tablet");
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var store = (BatteryHistoryStore)typeof(BatteryHistoryService).GetField("store", flags)!.GetValue(service)!;
        store.Initialize();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(BatteryHistoryService).GetField("initialization", flags)!.SetValue(service, ready.Task);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5); service.Start();
        try
        {
            for (int i = 0; i < 121; i++) service.TryRecord(Reading(start.AddSeconds(i), i, saver: i % 2 == 1));
            Assert.Equal(1, service.DroppedSamples); ready.SetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (service.PendingSamples > 0) await Task.Delay(10, timeout.Token);
            service.TryRecord(Reading(start.AddSeconds(122), 122)); await service.StopAsync();
            using var writer = new StringWriter(); store.ExportCsv("tablet", writer, null, DateTimeOffset.UtcNow);
            var rows = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(line => line.Trim().Split(',')).ToArray();
            Assert.Equal(121, rows.Length);
            Assert.All(rows.Take(120), row => Assert.Equal(rows[0][4], row[4]));
            Assert.NotEqual(rows[119][4], rows[120][4]);
        }
        finally { ready.TrySetResult(); await service.StopAsync(); }
    }
    [Fact]
    public async Task Normal_quit_saves_sparse_observations_and_reopen_keeps_saver_and_charging()
    {
        using var fixture = new Fixture(); var service = new BatteryHistoryService(fixture.Path, "tablet");
        var start = DateTimeOffset.UtcNow.AddMinutes(-3); service.Start();
        for (int i = 0; i < 100; i++) service.TryRecord(Reading(start.AddSeconds(i), i,
            i >= 30 ? BatteryPowerState.Charging : BatteryPowerState.OnBattery, i >= 31));
        await service.StopAsync();
        var reopened = new BatteryHistoryService(fixture.Path, "tablet");
        var view = await reopened.ReadViewAsync(0);
        Assert.Equal(4, view.TotalObservations); Assert.Equal(3, view.Buckets.Sum(b => b.ChargingSamples));
        Assert.Equal(2, view.Buckets.Sum(b => b.SaverSamples));
        Assert.Contains("saved", view.Status, StringComparison.OrdinalIgnoreCase);
        await reopened.StopAsync();
    }

    [Fact]
    public async Task Recording_toggle_preserves_old_data_and_creates_a_new_segment()
    {
        using var fixture = new Fixture(); var service = new BatteryHistoryService(fixture.Path, "tablet");
        var start = DateTimeOffset.UtcNow.AddMinutes(-5); service.Start();
        service.TryRecord(Reading(start, 0)); service.Configure(false);
        service.TryRecord(Reading(start.AddSeconds(1), 1)); service.Configure(true);
        service.TryRecord(Reading(start.AddSeconds(2), 2)); await service.StopAsync();
        var view = await service.ReadViewAsync(0);
        Assert.Equal(2, view.TotalObservations);
        using var writer = new StringWriter(); var store = new BatteryHistoryStore(fixture.Path); store.Initialize();
        store.ExportCsv("tablet", writer, null, DateTimeOffset.UtcNow);
        var segments = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(line => line.Trim().Split(',')[4]).Distinct().ToArray();
        Assert.Equal(2, segments.Length);
    }

    [Fact]
    public async Task A_corrupt_store_reports_failure_without_throwing_into_the_collector()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, "corrupt database fixture");
        var service = new BatteryHistoryService(fixture.Path, "tablet"); service.Start();
        service.TryRecord(Reading(DateTimeOffset.UtcNow, 0)); await service.StopAsync();
        Assert.Contains("unavailable", service.Status, StringComparison.OrdinalIgnoreCase);
        var view = await service.ReadViewAsync(0); Assert.Empty(view.Buckets);
        Assert.Contains("unavailable", view.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("corrupt database fixture", await File.ReadAllTextAsync(fixture.Path));
    }

    [Fact]
    public async Task Timer_flushes_without_waiting_for_another_minute_and_csv_is_complete()
    {
        using var fixture = new Fixture(); var service = new BatteryHistoryService(fixture.Path, "tablet", TimeSpan.FromMilliseconds(30));
        service.Start(); service.TryRecord(Reading(DateTimeOffset.UtcNow.AddSeconds(-1), 0));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await service.ReadViewAsync(0)).TotalObservations == 0) await Task.Delay(20, deadline.Token);
            string export = fixture.Path + ".csv"; await service.ExportCsvAsync(export, 0);
            Assert.Equal(2, (await File.ReadAllLinesAsync(export)).Length);
            await Assert.ThrowsAsync<IOException>(() => service.ExportCsvAsync(export, 0));
        }
        finally { await service.StopAsync(); }
    }

    private static Reading Reading(DateTimeOffset stamp, double seconds, BatteryPowerState state = BatteryPowerState.OnBattery, bool? saver = false) =>
        new(new Snapshot(stamp, seconds, new Dictionary<string, double?>()), [], [], [], "CPU", "", "", "", false)
        { Battery = new(80 - seconds / 100, state, saver) };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-battery-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "battery.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
