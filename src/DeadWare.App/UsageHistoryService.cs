using System.IO;
using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One background writer folds publications into minutes and saves them; reads use independent connections.</summary>
public sealed class UsageHistoryService
{
    public const string SavedStatus = "Usage history saved locally · up to 400 days.";
    private sealed record Pending(Reading Reading, long Generation, long Drops);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly UsageHistoryStore store;
    private readonly string machine;
    private readonly TimeProvider time;
    private readonly TimeSpan interval;
    private readonly object lifecycle = new();
    private Task? worker, initialization;
    private bool stopped, accepting;
    private int recording = 1;
    private long generation, dropped;
    private string status = "Usage history starts with monitoring.";
    private bool failed;

    public UsageHistoryService(string path, string machineKey, TimeProvider? timeProvider = null, TimeSpan? flushInterval = null)
    {
        store = new(path); machine = machineKey; time = timeProvider ?? TimeProvider.System; interval = flushInterval ?? TimeSpan.FromSeconds(15);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
    }
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public int PendingSamples => queue.Reader.Count;
    public bool Failed => Volatile.Read(ref failed);
    /// <summary>Raised on the history worker for every minute the folder closes, in order. Handlers must be quick and must not throw.</summary>
    public event Action<UsageMinuteBatch>? MinuteClosed;
    public string Status => (Volatile.Read(ref failed) ? Volatile.Read(ref status) : Volatile.Read(ref recording) == 0
        ? "Usage recording paused. Saved history is retained." : Volatile.Read(ref status)) +
        (DroppedSamples > 0 ? $" · {DroppedSamples} publications dropped; history contains gaps." : "");

    /// <summary>GPUs recorded under their model name, which survives enumeration-order changes. Duplicate models are ambiguous and skipped.</summary>
    public static IReadOnlyList<(string Id, string Key)> Gpus(Reading reading) => reading.Gpus
        .Where(gpu => !string.IsNullOrWhiteSpace(gpu.Id) && !string.IsNullOrWhiteSpace(gpu.Name) &&
            reading.Gpus.Count(other => other.Name.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase)) == 1)
        .Take(16).Select(gpu => (gpu.Id, UsageMetrics.NormalizeName(gpu.Name))).ToArray();

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
    /// <summary>The collector only enqueues its immutable publication; it never waits for disk.</summary>
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
        lock (lifecycle) return initialization ??= Task.Run(() => { store.Initialize(); Volatile.Write(ref status, SavedStatus); });
    }
    private void Fail(Exception error)
    {
        Volatile.Write(ref status, $"Usage history unavailable ({error.GetType().Name}). Saved data was not deleted. Live monitoring continues; resolve storage access and restart to retry.");
        Volatile.Write(ref failed, true);
    }
    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var folder = new UsageMinuteFolder(); var pending = new List<UsageMinuteBatch>(64);
            long previousGeneration = 0, previousDrops = 0;
            void Flush()
            {
                if (pending.Count == 0) return;
                store.Append(machine, pending, time.GetUtcNow()); pending.Clear();
                Volatile.Write(ref status, SavedStatus);
            }
            void Closed(UsageMinuteBatch batch)
            {
                pending.Add(batch);
                // A listener's failure must not turn a saved minute into a history failure.
                try { MinuteClosed?.Invoke(batch); } catch { }
            }
            void Process(Pending item)
            {
                var reading = item.Reading;
                // A pause, resume or queue loss ends the open minute as incomplete and starts a new segment.
                if (item.Generation != previousGeneration || item.Drops != previousDrops)
                {
                    if (folder.Flush() is { } closed) pending.Add(closed);
                    folder.Break();
                }
                previousGeneration = item.Generation; previousDrops = item.Drops;
                bool captureAvailable = reading.Connections?.Available ?? reading.Processes.Count > 0;
                if (folder.Observe(reading.Sample, reading.Processes, captureAvailable, Gpus(reading)) is { } batch) Closed(batch);
                if (pending.Count >= 30) Flush();
            }
            using var timer = new PeriodicTimer(interval, time);
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
            if (folder.Flush() is { } last) Closed(last);
            Flush();
        }
        catch (Exception error)
        {
            Fail(error); Volatile.Write(ref accepting, false); queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }
    public async Task<UsageHistoryView> ReadMetricAsync(string metric, string? hardwareId, DateTimeOffset from, DateTimeOffset to, int maxPoints = 1500)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadMetric(machine, metric, hardwareId, from, to, maxPoints)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return new(from, to, 60_000, [], new(null, null, null), 0, null, 0);
        }
    }
    public async Task<IReadOnlyList<UsageApplicationMinute>> ReadApplicationMinutesAsync(string name, DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplicationMinutes(machine, name, from, to)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return [];
        }
    }
    public async Task<UsageApplicationView> ReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, int maxNames = UsageHistoryStore.MaximumApplications)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplications(machine, from, to, maxNames)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return new(from, to, [], 0, 0, 0, 0, 0, null, 0);
        }
    }
    public async Task<(long AtOrAbove, long Total)> ShareAtOrAboveAsync(string metric, string? hardwareId, DateTimeOffset from, DateTimeOffset to, double threshold)
    {
        await InitializeAsync().ConfigureAwait(false);
        return await Task.Run(() => store.ShareAtOrAbove(machine, metric, hardwareId, from, to, threshold)).ConfigureAwait(false);
    }
    /// <summary>Writes a new file; an existing file is never replaced.</summary>
    public async Task ExportCsvAsync(string path, bool applications, DateTimeOffset from, DateTimeOffset to)
    {
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            if (applications) store.ExportApplicationsCsv(machine, writer, from, to);
            else store.ExportMetricsCsv(machine, writer, from, to);
        }).ConfigureAwait(false);
    }
}
