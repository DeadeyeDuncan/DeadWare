using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One background writer folds published process activity into minutes and saves them to process-history.db, like
/// <see cref="UsageHistoryService"/>; reads use independent connections. Records only while Per-process monitoring and usage history are both on;
/// the writer runs either way, so days past the 30-day retention leave at start and when the UTC day changes.</summary>
public sealed class ProcessHistoryService
{
    public const string SavedStatus = "Application resources saved locally · up to 30 days.";
    /// <summary>How often closed minutes are saved unless a caller chooses otherwise.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(15);
    private const string PausedStatus = "Application resources are recorded while Per-process monitoring and usage history are both on. Saved history is retained.";
    private sealed record Pending(ProcessActivity Activity, long Sequence, long Generation, long Drops);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly ProcessHistoryStore store;
    private readonly string machine;
    private readonly TimeProvider time;
    private readonly TimeSpan interval;
    private readonly object lifecycle = new();
    private Task? worker, initialization;
    private bool stopped, accepting;
    // Off until Configure: Per-process monitoring is off by default.
    private int recording;
    private long generation, dropped;
    private string status = "Application resource history starts with monitoring.";
    private bool failed;

    public ProcessHistoryService(string path, string machineKey, TimeProvider? timeProvider = null, TimeSpan? flushInterval = null)
    {
        store = new(path); machine = machineKey; time = timeProvider ?? TimeProvider.System; interval = flushInterval ?? DefaultFlushInterval;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
    }
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public bool Failed => Volatile.Read(ref failed);
    public string Status => (Volatile.Read(ref failed) ? Volatile.Read(ref status) : Volatile.Read(ref recording) == 0
        ? PausedStatus : Volatile.Read(ref status)) +
        (DroppedSamples > 0 ? $" · {DroppedSamples} publications dropped; history contains gaps." : "");

    public void Configure(bool monitoring, bool usageRecording)
    {
        int next = monitoring && usageRecording ? 1 : 0;
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
    /// <summary>The collector only enqueues its immutable activity and sequence; it never waits for disk.</summary>
    public void TryRecord(Reading reading)
    {
        if (!Volatile.Read(ref accepting) || Volatile.Read(ref recording) == 0) return;
        // Only a scan's activity carries deltas; the folder would ignore any other kind.
        if (reading.ProcessActivity is not { Kind: ProcessActivityKind.Available } activity) return;
        if (!queue.Writer.TryWrite(new(activity, activity.Sequence, Interlocked.Read(ref generation), DroppedSamples))) Interlocked.Increment(ref dropped);
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
        Volatile.Write(ref status, $"Application resource history unavailable ({error.GetType().Name}). Saved data was not deleted. Live monitoring continues; resolve storage access and restart to retry.");
        Volatile.Write(ref failed, true);
    }
    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            // Each save expires old days first, but nothing is saved while recording is off: expire at start and on each new UTC day too.
            DateTime Expire() { var now = time.GetUtcNow(); store.Expire(now); return now.UtcDateTime.Date; }
            var expired = Expire();
            var folder = new ProcessMinuteFolder(); var pending = new List<ProcessMinuteBatch>(64);
            long? previousGeneration = null; long previousDrops = 0, previousSequence = 0; DateTimeOffset previousMinute = default;
            void Flush()
            {
                if (pending.Count == 0) return;
                store.Append(machine, pending, time.GetUtcNow()); pending.Clear();
                Volatile.Write(ref status, SavedStatus);
            }
            void Process(Pending item)
            {
                // A repeated or reordered activity would count its deltas twice.
                if (item.Sequence <= previousSequence) return;
                var minute = UsageMetrics.Floor(item.Activity.Timestamp);
                bool lost = false;
                if (previousGeneration is { } before)
                {
                    // A pause or resume ends the open minute and starts a new segment.
                    if (item.Generation != before)
                    {
                        if (folder.Flush() is { } closed) pending.Add(closed);
                        folder.Break();
                    }
                    // A dropped or missed activity leaves the open minute incomplete.
                    else if (item.Drops != previousDrops || item.Sequence != previousSequence + 1) { folder.Break(); lost = true; }
                }
                previousGeneration = item.Generation; previousDrops = item.Drops; previousSequence = item.Sequence;
                if (folder.Observe(item.Activity) is { } batch) pending.Add(batch);
                // Across a minute boundary the old minute may have lost its tail and this one its head: mark this one too.
                if (lost && minute != previousMinute) folder.Break();
                previousMinute = minute;
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
                    Flush();
                    if (time.GetUtcNow().UtcDateTime.Date != expired) expired = Expire();
                    tick = timer.WaitForNextTickAsync().AsTask();
                }
                if (available.IsCompleted)
                {
                    if (!await available.ConfigureAwait(false)) break;
                    for (int i = 0; i < 120 && queue.Reader.TryRead(out var item); i++) Process(item);
                    available = queue.Reader.WaitToReadAsync().AsTask();
                }
            }
            if (folder.Flush() is { } last) pending.Add(last);
            Flush();
        }
        catch (Exception error)
        {
            // Only this worker stops; the collector and the other histories continue.
            Fail(error); Volatile.Write(ref accepting, false); queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }
    /// <summary>The period's per-application totals, read off the UI thread in one snapshot. An unreadable store reports
    /// <see cref="Status"/> and returns an empty view; an invalid range or rank throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public async Task<ProcessResourceView> ReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, ProcessRank rank)
        => await TryReadApplicationsAsync(from, to, rank).ConfigureAwait(false)
            ?? new(from, to, [], new(ProcessHistory.EverythingElse, 0, 0, 0, 0, 0, 0, true, 0), 0, 0, null, null, null, 0);
    /// <summary>Like <see cref="ReadApplicationsAsync"/>, but null when the store cannot be read, so a caller can tell a failure from an
    /// empty period; <see cref="Status"/> then says why.</summary>
    public async Task<ProcessResourceView?> TryReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, ProcessRank rank)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplications(machine, from, to, rank, time.GetUtcNow())).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return null;
        }
    }
    /// <summary>One application's metric over the period, bucketed for a chart and read like <see cref="ReadApplicationsAsync"/>.</summary>
    public async Task<UsageHistoryView> ReadSeriesAsync(string name, ProcessRank metric, DateTimeOffset from, DateTimeOffset to, int maxPoints)
        => await TryReadSeriesAsync(name, metric, from, to, maxPoints).ConfigureAwait(false)
            ?? new(from, to, 60_000, [], new(null, null, null), 0, null, 0);
    /// <summary>Like <see cref="ReadSeriesAsync"/>, but null when the store cannot be read.</summary>
    public async Task<UsageHistoryView?> TryReadSeriesAsync(string name, ProcessRank metric, DateTimeOffset from, DateTimeOffset to, int maxPoints)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadSeries(machine, name, metric, from, to, maxPoints, time.GetUtcNow())).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return null;
        }
    }
}
