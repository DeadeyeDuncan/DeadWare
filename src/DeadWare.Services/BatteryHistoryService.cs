using System.IO;
using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One background writer; bounded publications, sparse durable observations, independent read connections.</summary>
public sealed class BatteryHistoryService
{
    private sealed record Pending(Reading Reading, long Generation, long Drops);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly BatteryHistoryStore store;
    private readonly string machine;
    private readonly TimeSpan interval;
    private readonly object lifecycle = new();
    private Task? worker, initialization;
    private bool stopped, accepting;
    private int recording = 1;
    private long generation, dropped;
    private string status = "Battery history starts with monitoring.";
    private bool failed;

    public BatteryHistoryService(string path, string machineKey, TimeSpan? flushInterval = null)
    {
        store = new(path); machine = machineKey; interval = flushInterval ?? TimeSpan.FromSeconds(15);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
    }
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public int PendingSamples => queue.Reader.Count;
    public string Status => (Volatile.Read(ref failed) ? Volatile.Read(ref status) : Volatile.Read(ref recording) == 0
        ? "Battery recording paused. Saved history is retained." : Volatile.Read(ref status)) +
        (DroppedSamples > 0 ? $" · {DroppedSamples} publications dropped; history contains gaps." : "");

    public void Configure(bool enabled)
    {
        int next = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref recording, next) != next) Interlocked.Increment(ref generation);
    }
    public void Start()
    {
        lock (lifecycle)
        {
            if (worker is not null || stopped) return;
            Volatile.Write(ref accepting, true); worker = Task.Run(RunAsync);
        }
    }
    public void TryRecord(Reading reading)
    {
        if (!Volatile.Read(ref accepting) || Volatile.Read(ref recording) == 0) return;
        if (!queue.Writer.TryWrite(new(reading, Interlocked.Read(ref generation), DroppedSamples))) Interlocked.Increment(ref dropped);
    }
    public Task StopAsync()
    {
        lock (lifecycle)
        {
            stopped = true; Volatile.Write(ref accepting, false); queue.Writer.TryComplete();
            return worker ?? Task.CompletedTask;
        }
    }
    private Task InitializeAsync()
    {
        lock (lifecycle) return initialization ??= Task.Run(() =>
        {
            store.Initialize(); Volatile.Write(ref status, "Battery history saved locally · retained indefinitely.");
        });
    }
    private void Fail(Exception error)
    {
        Volatile.Write(ref status, $"Battery history unavailable ({error.GetType().Name}). Saved data was not deleted. Restart DeadWare after resolving storage access.");
        Volatile.Write(ref failed, true);
    }
    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var recorder = new BatteryRecorder(); var pending = new List<BatterySample>(120);
            long previousGeneration = 0, previousDrops = 0;
            void Flush()
            {
                store.Append(machine, pending); pending.Clear();
                Volatile.Write(ref status, "Battery history saved locally · retained indefinitely.");
            }
            void Process(Pending item)
            {
                if (item.Generation != previousGeneration || item.Drops != previousDrops) recorder.Break();
                previousGeneration = item.Generation; previousDrops = item.Drops;
                if (recorder.Observe(item.Reading.Sample.Timestamp, item.Reading.Sample.MonotonicSeconds, item.Reading.Battery,
                    item.Reading.Sample.PollingIntervalSeconds) is { } sample)
                    pending.Add(sample);
                if (pending.Count >= 120) Flush();
            }
            using var timer = new PeriodicTimer(interval);
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask(), available = queue.Reader.WaitToReadAsync().AsTask();
            while (true)
            {
                await Task.WhenAny(tick, available).ConfigureAwait(false);
                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    Flush(); tick = timer.WaitForNextTickAsync().AsTask();
                }
                if (available.IsCompleted)
                {
                    if (!await available.ConfigureAwait(false)) break;
                    for (int i = 0; i < 120 && queue.Reader.TryRead(out var item); i++) Process(item);
                    available = queue.Reader.WaitToReadAsync().AsTask();
                }
            }
            Flush();
        }
        catch (Exception error) { Fail(error); Volatile.Write(ref accepting, false); }
    }
    public async Task<BatteryHistoryView> ReadViewAsync(int days)
    {
        ValidateDays(days); var to = DateTimeOffset.UtcNow; DateTimeOffset? from = days == 0 ? null : to.AddDays(-days);
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var view = await Task.Run(() => store.Read(machine, from, to)).ConfigureAwait(false);
            return view with { Status = Status };
        }
        catch (Exception error)
        {
            Fail(error);
            return new(from ?? to.AddDays(-1), to, [], 0, null, 0, Status);
        }
    }
    public async Task ExportCsvAsync(string path, int days)
    {
        ValidateDays(days); await InitializeAsync().ConfigureAwait(false);
        var to = DateTimeOffset.UtcNow; DateTimeOffset? from = days == 0 ? null : to.AddDays(-days);
        await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            store.ExportCsv(machine, writer, from, to);
        }).ConfigureAwait(false);
    }
    private static void ValidateDays(int days)
    { if (days is not (0 or 1 or 7 or 30 or 365)) throw new ArgumentOutOfRangeException(nameof(days)); }
}
