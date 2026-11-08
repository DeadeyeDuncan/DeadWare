using System.Diagnostics;
using System.IO;
using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
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
    public async Task Usage_clear_interleaves_with_recording_and_resumes_after_quit()
    {
        using var fixture = new Fixture();
        new UsageHistoryStore(fixture.Path).Initialize();
        // 440 minutes × 100 apps: eleven all-apps delete transactions of at most 4,096 rows. The first service stops after the fourth, so
        // the resumed one runs seven: few enough for a slow CI disk (about 0.7 s a transaction there), and the resume means the same.
        SeedApplications(fixture.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-3)), 440, 100);
        Assert.Equal(44_000, Count(fixture.Path, "SELECT COUNT(*) FROM application_minutes"));
        var service = new UsageHistoryService(fixture.Path, "tablet");
        var store = Store(service);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? stopping = null; int commits = 0, steps = 0, stepsAfterStop = 0; var sinceStop = new Stopwatch();
        // Counts every step and those that start once StopAsync was requested (the spec: a step never starts after StopAsync).
        service.ClearStepOverride = budget =>
        {
            steps++;
            if (Volatile.Read(ref stopping) is not null) stepsAfterStop++;
            return store.ClearStep(budget);
        };
        // Each committed delete waits 40 ms while publications keep arriving. The fourth commit requests StopAsync from inside its step, so a
        // step is always in progress when the stop arrives; from then on the step's clock reads past its budget, so that step ends after
        // this transaction and seven remain for the resumed service whatever the disk's speed.
        typeof(UsageHistoryStore).GetField("clearCommitted", All)!.SetValue(store, (Action<int>)(_ =>
        {
            committed.TrySetResult();
            if (++commits == 4) { sinceStop.Start(); Volatile.Write(ref stopping, service.StopAsync()); }
            Thread.Sleep(40);
        }));
        typeof(UsageHistoryStore).GetField("clearClock", All)!.SetValue(store, (Func<TimeSpan>)(() =>
            HistoryClearStep.MonotonicClock() + (Volatile.Read(ref stopping) is null ? TimeSpan.Zero : TimeSpan.FromHours(1))));
        service.Start();
        using var publishing = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            for (int i = 0; !publishing.IsCancellationRequested; i++) { service.TryRecord(Reading(Start.AddSeconds(i), i)); await Task.Delay(10); }
        });
        var request = HistoryClear.Create(null, allTime: true, DateTimeOffset.UtcNow);
        service.Clear(request);
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Until(() => Volatile.Read(ref stopping) is not null, 120);
        Assert.Equal(HistoryClearPhase.Clearing, service.ClearState.Phase);
        await stopping!;
        sinceStop.Stop();
        publishing.Cancel(); await publisher;
        // Quit waits only for the step in progress (250 ms plus one transaction) and the final save; no further step starts.
        Assert.True(steps > 0);
        Assert.Equal(4, commits);
        Assert.Equal(0, stepsAfterStop);
        Assert.True(sinceStop.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {sinceStop.Elapsed}.");
        Assert.Equal(0, service.DroppedSamples);
        Assert.False(service.Failed);
        Assert.True(File.Exists(Marker(fixture.Path)));
        // Exactly four transactions ran: the step in progress at the stop ended after its transaction.
        Assert.Equal(44_000 - 4 * 4096, Count(fixture.Path, $"SELECT COUNT(*) FROM application_minutes WHERE minute<{request.Before.ToUnixTimeMilliseconds()}"));

        var resumed = new UsageHistoryService(fixture.Path, "tablet");
        resumed.Start();
        try
        {
            // Seven transactions: about 5 s on the CI runner; 60 s leaves room for its slowest disk.
            await Until(() => resumed.ClearState.Phase == HistoryClearPhase.Done, 60);
            Assert.True(resumed.ClearState.Resumed);
        }
        finally { await resumed.StopAsync(); }
        Assert.False(File.Exists(Marker(fixture.Path)));
        Assert.Equal(0, Count(fixture.Path, $"SELECT COUNT(*) FROM application_minutes WHERE minute<{request.Before.ToUnixTimeMilliseconds()}"));

        // Invalid marker content: deleted, reported, and nothing is cleared for it.
        using var invalid = new Fixture();
        new UsageHistoryStore(invalid.Path).Initialize();
        SeedApplications(invalid.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-3)), 10, 3);
        await File.WriteAllTextAsync(Marker(invalid.Path), "{");
        var discarded = new UsageHistoryService(invalid.Path, "tablet");
        discarded.Start();
        try { await Until(() => discarded.ClearState.Phase != HistoryClearPhase.None); }
        finally { await discarded.StopAsync(); }
        Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "InvalidDataException", false, true), discarded.ClearState);
        Assert.Equal(30, Count(invalid.Path, "SELECT COUNT(*) FROM application_minutes"));

        // A marker that cannot be written: nothing is cleared.
        using var blocked = new Fixture();
        new UsageHistoryStore(blocked.Path).Initialize();
        SeedApplications(blocked.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-3)), 10, 3);
        Directory.CreateDirectory(Marker(blocked.Path) + ".tmp");
        var unsaved = new UsageHistoryService(blocked.Path, "tablet");
        unsaved.Start();
        try
        {
            unsaved.Clear(HistoryClear.Create(null, allTime: true, DateTimeOffset.UtcNow));
            await Until(() => unsaved.ClearState.Phase == HistoryClearPhase.Failed);
            Assert.False(unsaved.ClearState.MarkerSaved);
            Assert.True(unsaved.CanRetryClear);
        }
        finally { await unsaved.StopAsync(); }
        Assert.False(File.Exists(Marker(blocked.Path)));
        Assert.Equal(30, Count(blocked.Path, "SELECT COUNT(*) FROM application_minutes"));
    }

    [Fact]
    public async Task Clear_runs_while_recording_is_paused()
    {
        using var fixture = new Fixture();
        new UsageHistoryStore(fixture.Path).Initialize();
        SeedApplications(fixture.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-3)), 50, 4);
        string processPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "process-history.db");
        var processStore = new ProcessHistoryStore(processPath); processStore.Initialize();
        processStore.Append("tablet", [new ProcessMinuteBatch(Start.AddMinutes(-5), 60, 8, true, "fixture", [new("chrome.exe", 1, 0, 0, 0, false)])], DateTimeOffset.UtcNow);
        var usage = new UsageHistoryService(fixture.Path, "tablet"); usage.Configure(false);
        var process = new ProcessHistoryService(processPath, "tablet"); process.Configure(false, false);
        usage.Start(); process.Start();
        try
        {
            var request = HistoryClear.Create(null, allTime: true, DateTimeOffset.UtcNow);
            usage.Clear(request); process.Clear(request);
            for (int i = 0; i < 120; i++) { usage.TryRecord(Reading(Start.AddSeconds(i), i)); process.TryRecord(ProcessHistoryServiceTests.Reading(i)); }
            await Until(() => usage.ClearState.Phase == HistoryClearPhase.Done && process.ClearState.Phase == HistoryClearPhase.Done);
        }
        finally { await Task.WhenAll(usage.StopAsync(), process.StopAsync()); }
        Assert.False(usage.Failed); Assert.False(process.Failed);
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM application_minutes"));
        // Nothing was recorded meanwhile.
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM metric_minutes"));
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM capture_gaps"));
        Assert.Equal(0, Count(processPath, "SELECT COUNT(*) FROM minutes"));
        Assert.Equal(0, Count(processPath, "SELECT COUNT(*) FROM app_minutes"));
    }

    [Fact]
    public async Task Try_again_after_a_failed_step_finishes_the_clear()
    {
        using var fixture = new Fixture();
        new UsageHistoryStore(fixture.Path).Initialize();
        SeedApplications(fixture.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-9)), 10, 3);
        var service = new UsageHistoryService(fixture.Path, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        // The first step throws; Try again runs the store's own step.
        service.ClearStepOverride = _ => { service.ClearStepOverride = null; throw new IOException("Fixture step failure."); };
        service.Start();
        try
        {
            var request = HistoryClear.Create(null, allTime: false, DateTimeOffset.UtcNow);
            service.Clear(request);
            await Until(() => service.ClearState.Phase == HistoryClearPhase.Failed);
            Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "IOException", true, false), service.ClearState);
            Assert.True(service.CanRetryClear);
            const string suffix = " · Clearing history failed (IOException); it resumes when DeadWare next starts.";
            Assert.Equal(suffix, HistoryClearCopy.StatusSuffix(service.ClearState, false));
            Assert.EndsWith(suffix, service.Status);
            Assert.True(File.Exists(Marker(fixture.Path)));
            // Recording continues.
            for (int i = 0; i <= 60; i++) service.TryRecord(Reading(Start.AddSeconds(i), i));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while ((await service.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(1))).Minutes == 0) await Task.Delay(20, deadline.Token);
            service.RetryClear();
            await Until(() => service.ClearState.Phase == HistoryClearPhase.Done);
            Assert.False(File.Exists(Marker(fixture.Path)));
            Assert.False(service.CanRetryClear);
            Assert.Equal(0, Count(fixture.Path, $"SELECT COUNT(*) FROM application_minutes WHERE minute<{request.Before.ToUnixTimeMilliseconds()}"));
            Assert.False(service.Failed);
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task An_unreadable_marker_waits_for_Try_again_and_a_stopped_worker_is_unavailable()
    {
        using var fixture = new Fixture();
        new UsageHistoryStore(fixture.Path).Initialize();
        SeedApplications(fixture.Path, UsageMetrics.Floor(DateTimeOffset.UtcNow.AddDays(-3)), 10, 3);
        var request = HistoryClear.Create(null, allTime: true, DateTimeOffset.UtcNow);
        HistoryClearMarker.Write(Marker(fixture.Path), request);
        var service = new UsageHistoryService(fixture.Path, "tablet");
        try
        {
            // Held without sharing, the marker cannot be read at start: it is kept and the clear waits.
            using (new FileStream(Marker(fixture.Path), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                service.Start();
                await Until(() => service.ClearState.Phase != HistoryClearPhase.None);
                Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "IOException", true, true), service.ClearState);
                Assert.True(service.CanRetryClear);
                Assert.Throws<InvalidOperationException>(() => service.Clear(request));
                Assert.Equal(30, Count(fixture.Path, "SELECT COUNT(*) FROM application_minutes"));
            }
            // Try again reads the marker again and finishes its clear.
            service.RetryClear();
            await Until(() => service.ClearState.Phase == HistoryClearPhase.Done);
            Assert.True(service.ClearState.Resumed);
        }
        finally { await service.StopAsync(); }
        Assert.False(File.Exists(Marker(fixture.Path)));
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM application_minutes"));

        // A worker stopped by a failure: the request is unavailable with that failure's type.
        using var broken = new Fixture(); Directory.CreateDirectory(broken.Path);
        var stopped = new UsageHistoryService(broken.Path, "tablet");
        stopped.Start();
        await Until(() => stopped.Failed);
        stopped.Clear(request);
        var state = stopped.ClearState;
        Assert.Equal(HistoryClearPhase.Unavailable, state.Phase);
        Assert.Contains($"({state.Error})", stopped.Status);
        Assert.False(stopped.CanRetryClear);
        await stopped.StopAsync();
    }

    [Fact]
    public async Task Excluded_apps_are_not_recorded_by_either_worker()
    {
        using var fixture = new Fixture();
        var usage = new UsageHistoryService(fixture.Path, "tablet");
        usage.SetExclusions(AppExclusions.Keys(["CHROME.EXE"]));
        usage.Start();
        for (int i = 0; i < 60; i++) usage.TryRecord(Reading(Start.AddSeconds(i), i,
            processes: [new ProcessTraffic(10, 1, "chrome.exe", null, null, 100 * (i + 1), 0), new ProcessTraffic(11, 1, "steam.exe", null, null, 10 * (i + 1), 0)]));
        await usage.StopAsync();
        var apps = await usage.ReadApplicationsAsync(Start, Start.AddMinutes(1));
        Assert.Equal(["steam.exe"], apps.Applications.Select(a => a.Name));

        string processPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Path)!, "process-history.db");
        var process = new ProcessHistoryService(processPath, "tablet");
        process.SetExclusions(AppExclusions.Keys(["chrome.exe"]));
        process.Configure(true, true); process.Start();
        for (int i = 0; i < 30; i++) process.TryRecord(ProcessHistoryServiceTests.Reading(i));
        await process.StopAsync();
        var view = await process.ReadApplicationsAsync(Start, Start.AddMinutes(1), ProcessRank.CpuTime);
        Assert.Empty(view.Rows);
        Assert.Equal(2.9, view.EverythingElse.CpuSeconds, 6);
    }

    [Fact]
    public void Gpus_are_keyed_by_unique_model_name()
    {
        var reading = Reading(Start, 0) with { Gpus = [new("/gpu/0", "Model A"), new("/gpu/1", "Model B"), new("/gpu/2", "Model B"), new("/gpu/3", " ")] };
        Assert.Equal(new[] { ("/gpu/0", "Model A") }, UsageHistoryService.Gpus(reading));
    }

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic;

    private static UsageHistoryStore Store(UsageHistoryService service) => (UsageHistoryStore)typeof(UsageHistoryService).GetField("store", All)!.GetValue(service)!;

    internal static string Marker(string database) => System.IO.Path.ChangeExtension(database, ".clear.json");

    /// <summary>Application rows written directly, <paramref name="apps"/> per minute for <paramref name="minutes"/> minutes from <paramref name="first"/>.</summary>
    private static void SeedApplications(string database, DateTimeOffset first, int minutes, int apps)
    {
        using var db = Open(database, SqliteOpenMode.ReadWrite);
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO application_minutes(machine,name,minute,received,sent,segment) VALUES('tablet',$name,$minute,10,1,'seed')";
        var name = command.Parameters.Add("$name", SqliteType.Text); var minute = command.Parameters.Add("$minute", SqliteType.Integer);
        for (int m = 0; m < minutes; m++)
            for (int a = 0; a < apps; a++) { name.Value = $"app{a}.exe"; minute.Value = first.AddMinutes(m).ToUnixTimeMilliseconds(); command.ExecuteNonQuery(); }
        transaction.Commit();
    }

    internal static long Count(string database, string sql)
    {
        using var db = Open(database, SqliteOpenMode.ReadOnly);
        using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string database, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = mode, Pooling = false }.ToString());
        db.Open(); return db;
    }

    internal static async Task Until(Func<bool> condition, int seconds = 30)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(10, deadline.Token);
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
        public string Path => System.IO.Path.Combine(root, "usage-history.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
