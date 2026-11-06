using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class ProcessHistoryServiceTests
{
    private static readonly DateTimeOffset Start = UsageMetrics.Floor(DateTimeOffset.UtcNow.AddMinutes(-10));

    [Theory][InlineData(true, true, 1)][InlineData(true, false, 0)][InlineData(false, true, 0)]
    public async Task Records_only_when_both_switches_are_on(bool monitoring, bool usage, int minutes)
    {
        using var fixture = new Fixture(); var service = new ProcessHistoryService(fixture.Path, "tablet");
        service.Configure(monitoring, usage); service.Start();
        // One minute of activities; StopAsync saves the open minute.
        for (int i = 0; i < 60; i++) service.TryRecord(Reading(i));
        await service.StopAsync();
        var view = await Read(service);
        Assert.Equal(minutes, view.RecordedMinutes);
        // The first activity only sets the baseline, so 59 of the 60 add their 0.1 s of CPU.
        string[] names = minutes == 1 ? ["chrome.exe"] : [];
        Assert.Equal(names, view.Rows.Select(r => r.Name));
        Assert.Equal(5.9 * minutes, view.RankTotal, 6);
        Assert.False(service.Failed);
    }

    [Fact]
    public async Task Full_queue_drops_and_counts()
    {
        using var fixture = new Fixture(); var service = new ProcessHistoryService(fixture.Path, "tablet");
        ((ProcessHistoryStore)typeof(ProcessHistoryService).GetField("store", All)!.GetValue(service)!).Initialize();
        // The worker waits on this initialization, so nothing leaves the 120-slot queue.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(ProcessHistoryService).GetField("initialization", All)!.SetValue(service, ready.Task);
        service.Configure(true, true); service.Start();
        try
        {
            for (int i = 0; i < 200; i++) service.TryRecord(Reading(i));
            Assert.Equal(80, service.DroppedSamples);
            ready.SetResult(); await service.StopAsync();
            Assert.Contains("80 publications dropped", service.Status);
            Assert.False(service.Failed);
            // The 120 queued activities (minutes 0 and 1) were still saved.
            Assert.Equal(2, (await Read(service)).RecordedMinutes);
        }
        finally { ready.TrySetResult(); await service.StopAsync(); }
    }

    [Fact]
    public async Task Stop_flushes_the_open_minute()
    {
        using var fixture = new Fixture(); var service = new ProcessHistoryService(fixture.Path, "tablet");
        service.Configure(true, true); service.Start();
        for (int i = 0; i < 30; i++) service.TryRecord(Reading(i));
        await service.StopAsync();
        var view = await Read(service);
        Assert.Equal(1, view.RecordedMinutes);
        Assert.Equal(2.9, Assert.Single(view.Rows).CpuSeconds, 6);
        Assert.Equal(ProcessHistoryService.SavedStatus, service.Status);
        // A range over 31 days is the caller's error, not a storage failure.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadApplicationsAsync(Start, Start.AddDays(32), ProcessRank.CpuTime));
        Assert.False(service.Failed);
    }

    [Fact]
    public async Task Timer_saves_a_closed_minute_before_stop()
    {
        using var fixture = new Fixture(); var service = new ProcessHistoryService(fixture.Path, "tablet", flushInterval: TimeSpan.FromMilliseconds(30));
        service.Configure(true, true); service.Start();
        try
        {
            // The activity at 60 s closes minute 0; only the timer can save it before stop.
            for (int i = 0; i <= 60; i++) service.TryRecord(Reading(i));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while ((await Read(service)).RecordedMinutes == 0) await Task.Delay(20, deadline.Token);
            Assert.Equal(1, (await Read(service)).RecordedMinutes);
        }
        finally { await service.StopAsync(); }
    }

    [Fact]
    public async Task A_recording_change_saves_the_open_minute_and_skips_the_paused_one()
    {
        using var fixture = new Fixture(); var service = new ProcessHistoryService(fixture.Path, "tablet");
        service.Configure(true, true); service.Start();
        for (int i = 0; i < 60; i++) service.TryRecord(Reading(i));
        service.Configure(false, true);
        Assert.Contains("Per-process monitoring", service.Status);
        for (int i = 60; i < 120; i++) service.TryRecord(Reading(i));
        service.Configure(true, true);
        for (int i = 120; i < 180; i++) service.TryRecord(Reading(i));
        await service.StopAsync();
        var series = await service.ReadSeriesAsync("chrome.exe", ProcessRank.CpuTime, Start, Start.AddMinutes(3), 3);
        // Minute 0 was fully observed; minute 1 was paused; minute 2 follows a break, so it is incomplete.
        Assert.Equal([Start, Start.AddMinutes(2)], series.Buckets.Select(b => b.Start));
        Assert.Equal([1L, 0L], series.Buckets.Select(b => b.CompleteMinutes));
        Assert.Equal(2, (await Read(service, 3)).RecordedMinutes);
        Assert.Equal(2, (await service.TryReadApplicationsAsync(Start, Start.AddMinutes(3), ProcessRank.CpuTime))!.RecordedMinutes);
        Assert.NotNull(await service.TryReadSeriesAsync("chrome.exe", ProcessRank.CpuTime, Start, Start.AddMinutes(3), 3));
    }

    [Fact]
    public async Task Store_failure_stops_only_this_worker()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(fixture.Path);
        // The database path is a directory, so this store cannot open.
        var service = new ProcessHistoryService(fixture.Path, "tablet"); var usage = new UsageHistoryService(fixture.Path + ".usage", "tablet");
        service.Configure(true, true); service.Start(); usage.Start();
        for (int i = 0; i < 60; i++) { var reading = Reading(i); service.TryRecord(reading); usage.TryRecord(reading); }
        await Task.WhenAll(service.StopAsync(), usage.StopAsync());
        Assert.True(service.Failed); Assert.False(usage.Failed);
        Assert.Contains("unavailable", service.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await usage.ReadMetricAsync(UsageMetrics.Cpu, null, Start, Start.AddMinutes(1))).Minutes);
        service.TryRecord(Reading(60));
        Assert.True(Directory.Exists(fixture.Path));
    }

    [Fact]
    public async Task A_foreign_file_is_preserved_and_reported_without_throwing()
    {
        using var fixture = new Fixture(); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        await File.WriteAllTextAsync(fixture.Path, "foreign database fixture");
        var service = new ProcessHistoryService(fixture.Path, "tablet");
        service.Configure(true, true); service.Start(); service.TryRecord(Reading(0)); await service.StopAsync();
        Assert.True(service.Failed);
        Assert.Contains("(InvalidDataException)", service.Status);
        var view = await Read(service);
        Assert.Equal((0L, 0), (view.RecordedMinutes, view.Rows.Count));
        Assert.Empty((await service.ReadSeriesAsync("chrome.exe", ProcessRank.Io, Start, Start.AddMinutes(1), 1)).Buckets);
        // The Try reads tell the failure apart from an empty period.
        Assert.Null(await service.TryReadApplicationsAsync(Start, Start.AddMinutes(1), ProcessRank.CpuTime));
        Assert.Null(await service.TryReadSeriesAsync("chrome.exe", ProcessRank.Io, Start, Start.AddMinutes(1), 1));
        Assert.Equal("foreign database fixture", await File.ReadAllTextAsync(fixture.Path));
    }

    [Fact]
    public async Task Session_switches_configure_process_history_and_need_no_service()
    {
        using var fixture = new Fixture();
        // UI-check fixtures build sessions without process history; both switches must still work there.
        var bare = Session(fixture, null, "bare");
        bare.SetProcessMonitoring(true); bare.SetUsageRecording(false); bare.SetUsageRecording(true); bare.SetProcessMonitoring(false);
        Assert.Equal((false, true), (bare.Settings.ProcessMonitoring, bare.Settings.UsageRecording));
        var service = new ProcessHistoryService(fixture.Path, "tablet"); var session = Session(fixture, service, "wired");
        const string paused = "recorded while Per-process monitoring and usage history are both on";
        Assert.Contains(paused, service.Status);
        session.SetProcessMonitoring(true); Assert.DoesNotContain(paused, service.Status);
        session.SetUsageRecording(false); Assert.Contains(paused, service.Status);
        session.SetUsageRecording(true); Assert.DoesNotContain(paused, service.Status);
        session.SetProcessMonitoring(false); Assert.Contains(paused, service.Status);
        await Task.WhenAll(Saved(bare), Saved(session));
    }

    private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>A session wired only for the two recording switches, like the UI-check fixtures. Never starts hardware or a window.</summary>
    private static UiSession Session(Fixture fixture, ProcessHistoryService? history, string name)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        void Set(string field, object? value) => typeof(UiSession).GetField(field, All)!.SetValue(session, value);
        Set("<Collector>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(Collector)));
        Set("<Settings>k__BackingField", new UserSettings { ProcessMonitoring = false, UsageRecording = true });
        Set("<Usage>k__BackingField", new UsageHistoryService(fixture.Path + "." + name + ".usage", "tablet"));
        Set("<ProcessHistory>k__BackingField", history);
        Set("layout", new DetachedWindowState([]));
        Set("writer", new SettingsSnapshotWriter(fixture.Path + "." + name + ".json"));
        return session;
    }

    /// <summary>Waits until the session's last settings revision is on disk, so no background save outlives the fixture.</summary>
    private static async Task Saved(UiSession session)
    {
        long revision = (long)typeof(UiSession).GetField("saveRevision", All)!.GetValue(session)!;
        var writer = typeof(UiSession).GetField("writer", All)!.GetValue(session)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((long)writer.GetType().GetField("savedRevision", All)!.GetValue(writer)! != revision) await Task.Delay(10, deadline.Token);
    }

    private static Task<ProcessResourceView> Read(ProcessHistoryService service, int minutes = 2) =>
        service.ReadApplicationsAsync(Start, Start.AddMinutes(minutes), ProcessRank.CpuTime);

    /// <summary>One 1 s poll at <paramref name="second"/> after <see cref="Start"/>: chrome.exe used 0.1 s of CPU since the previous poll.</summary>
    private static Reading Reading(int second)
    {
        var stamp = Start.AddSeconds(second); double? delta = second == 0 ? null : 1;
        var chrome = new ProcessSample(10, 1, "chrome.exe", 1, 10, 1000, 0, 0, ProcessGap.None,
            delta is null ? null : new ProcessDeltas(1_000_000, 0, 0, 1000, 1));
        var activity = new ProcessActivity(ProcessActivityKind.Available, second + 1, stamp, second, 1, 8, [chrome], ProcessOmitted.None, 1, delta);
        return new Reading(new Snapshot(stamp, second, new Dictionary<string, double?> { ["cpu/usage"] = 40 }), [], [], [], "CPU", "", "", "", false)
            { ProcessActivity = activity };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-process-history-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "process-history.db");
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
