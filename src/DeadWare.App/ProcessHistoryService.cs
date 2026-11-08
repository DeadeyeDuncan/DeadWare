using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
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
    // The store's clear marker, read here only to name why it cannot be read.
    private readonly string markerPath;
    private readonly HistoryClearCommand clears = new();
    private IReadOnlySet<string> exclusions = FrozenSet<string>.Empty;
    // The exception type that stopped the worker, or null while it has not stopped on a failure.
    private string? stoppedBy;
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
        markerPath = Path.ChangeExtension(Path.GetFullPath(path), ".clear.json");
    }
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public bool Failed => Volatile.Read(ref failed);
    public string Status => (Volatile.Read(ref failed) ? Volatile.Read(ref status) : Volatile.Read(ref recording) == 0
        ? PausedStatus : Volatile.Read(ref status)) +
        (DroppedSamples > 0 ? $" · {DroppedSamples} publications dropped; history contains gaps." : "") +
        HistoryClearCopy.StatusSuffix(clears.State, false);

    /// <summary>This store's clear. The internal setter is a test seam; the app never calls it.</summary>
    public HistoryClearState ClearState { get => clears.State; internal set => clears.State = value; }
    /// <summary>Puts the request in the worker's command channel (capacity 1) and sets Queued. Refused while a clear is active. After the
    /// worker stopped on a failure the request is Unavailable with that failure's type, and nothing is written for it.</summary>
    public void Clear(HistoryClear request)
    {
        clears.Post(request);
        if (Volatile.Read(ref stoppedBy) is { } failure) clears.Unavailable(failure);
    }
    /// <summary>Re-posts the kept request while Failed and the worker runs: the full order without a saved marker, otherwise a resume that
    /// repeats nothing already done, or reads an unreadable marker again.</summary>
    public void RetryClear() => clears.Retry(WorkerRunning);
    public bool CanRetryClear => clears.CanRetry(WorkerRunning);
    /// <summary>A test seam that replaces the store's step.</summary>
    internal Func<TimeSpan, HistoryClearProgress>? ClearStepOverride { get => clears.StepOverride; set => clears.StepOverride = value; }
    private bool WorkerRunning { get { lock (lifecycle) return !stopped && Volatile.Read(ref stoppedBy) is null && worker is { IsCompleted: false }; } }

    /// <summary>The Don't record keys (<see cref="AppKey.Of"/>, ordinal). The worker sets them on its folder before each publication it folds.</summary>
    public void SetExclusions(IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Volatile.Write(ref exclusions, keys);
    }

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
    /// <summary>The worker stopped on <paramref name="error"/>: a clear it was running fails (its marker, when saved, resumes at the next
    /// start), and a queued one is Unavailable.</summary>
    private void Stopped(Exception error)
    {
        string type = error.GetType().Name;
        Volatile.Write(ref stoppedBy, type);
        var state = clears.State;
        if (state.Phase == HistoryClearPhase.Clearing || state is { Phase: HistoryClearPhase.Queued, MarkerSaved: true }) clears.Fail(type, store.ClearPending);
        else if (state.Phase == HistoryClearPhase.Queued) clears.Unavailable(type);
    }
    /// <summary>Why the kept marker cannot be read: the exception type of a fresh read.</summary>
    private string MarkerReadError()
    {
        try { HistoryClearMarker.Read(markerPath); return nameof(IOException); }
        catch (Exception error) { return error.GetType().Name; }
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
            // The boundary rule of an All time clear: in-scope activity of minutes before Before is counted in Everything else (one app)
            // or dropped (all apps) until a minute at or after Before is folded or BoundaryLimit has passed since it was installed.
            HistoryClear? boundary = null; long boundarySince = 0; bool stepping = false;
            void InstallBoundary(HistoryClear clear) { boundary = clear; boundarySince = Stopwatch.GetTimestamp(); }
            HistoryClear? Boundary()
            {
                if (boundary is not null && Stopwatch.GetElapsedTime(boundarySince) >= HistoryClear.BoundaryLimit) boundary = null;
                return boundary;
            }
            void Flush()
            {
                if (pending.Count == 0) return;
                store.Append(machine, pending, time.GetUtcNow()); pending.Clear();
                Volatile.Write(ref status, SavedStatus);
            }
            // During an all-apps boundary window, a closed minute before Before is discarded, not saved.
            void Closed(ProcessMinuteBatch batch, HistoryClear? rule)
            {
                if (rule is { AppKey: null } && batch.Minute < rule.Before) return;
                pending.Add(batch);
            }
            void Process(Pending item)
            {
                // A repeated or reordered activity would count its deltas twice.
                if (item.Sequence <= previousSequence) return;
                var rule = Boundary();
                folder.Excluded = Volatile.Read(ref exclusions); folder.Boundary = rule;
                var minute = UsageMetrics.Floor(item.Activity.Timestamp);
                bool lost = false;
                if (previousGeneration is { } before)
                {
                    // A pause or resume ends the open minute and starts a new segment.
                    if (item.Generation != before)
                    {
                        if (folder.Flush() is { } closed) Closed(closed, rule);
                        folder.Break();
                    }
                    // A dropped or missed activity leaves the open minute incomplete.
                    else if (item.Drops != previousDrops || item.Sequence != previousSequence + 1) { folder.Break(); lost = true; }
                }
                previousGeneration = item.Generation; previousDrops = item.Drops; previousSequence = item.Sequence;
                if (folder.Observe(item.Activity) is { } batch) Closed(batch, rule);
                // Across a minute boundary the old minute may have lost its tail and this one its head: mark this one too.
                if (lost && minute != previousMinute) folder.Break();
                previousMinute = minute;
                // The rule ends once a minute at or after Before is folded.
                if (rule is not null && minute >= rule.Before) boundary = folder.Boundary = null;
                if (pending.Count >= 30) Flush();
            }
            // The state of a marker Initialize read: resumed, discarded as invalid, or kept because it cannot be read.
            void Resume()
            {
                if (store.PendingClear is { } marker)
                {
                    clears.Resume(marker);
                    if (marker.Before > time.GetUtcNow()) InstallBoundary(marker);
                    stepping = true;
                }
                else if (store.ClearPending) clears.Unreadable(MarkerReadError());
                else if (store.DiscardedInvalidMarker) clears.State = new(HistoryClearPhase.Failed, null, nameof(InvalidDataException), false, true);
            }
            // The clear command in the spec's order: marker, the All time open-minute handling and boundary, Flush, then the steps. A resume
            // (Try again with the marker saved) repeats nothing already done. A Flush failure is the worker's own failure.
            void RunClear(HistoryClearCommand.Command command)
            {
                var clear = command.Clear;
                clears.Clearing(null);
                if (ReferenceEquals(clear, HistoryClearCommand.Unread))
                {
                    // Try again for a marker that could not be read: read it again, as Initialize does at start.
                    try { store.Initialize(); }
                    catch (Exception error) { clears.Fail(error.GetType().Name, store.ClearPending); return; }
                    Resume();
                    if (!store.ClearPending && !store.DiscardedInvalidMarker) clears.Done();
                    return;
                }
                try { store.BeginClear(clear); }
                catch (Exception error) { clears.Fail(error.GetType().Name, command.Resume && store.ClearPending); return; }
                clears.MarkerSaved();
                if (!command.Resume && clear.AllTime)
                {
                    if (clear.AppKey is { } key) folder.Forget(key);
                    else if (folder.OpenMinute is { } open && open < clear.Before) folder.Discard();
                    InstallBoundary(clear);
                }
                else if (command.Resume && clear.Before > time.GetUtcNow()) InstallBoundary(clear);
                Flush();
                stepping = true;
            }
            // One step. ClearStep throws without a readable pending clear, so a marker already gone is Done.
            void Step()
            {
                if (clears.StepOverride is null && store.PendingClear is null) { clears.Done(); stepping = false; return; }
                if (clears.Step(budget => store.ClearStep(budget, time.GetUtcNow()))) stepping = false;
            }
            using var timer = new PeriodicTimer(interval, time);
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask(), available = queue.Reader.WaitToReadAsync().AsTask(),
                command = clears.Reader.WaitToReadAsync().AsTask();
            // After the start-up expiry, which waits while a marker exists.
            Resume();
            bool yielded = false;
            while (true)
            {
                // While a clear steps, the worker alternates one data pass with one step; it waits only after a step that yielded early.
                if (!stepping) await Task.WhenAny(tick, available, command).ConfigureAwait(false);
                else if (yielded) await Task.WhenAny(tick, available, command, Task.Delay(HistoryClear.StepBudget)).ConfigureAwait(false);
                if (command.IsCompleted)
                {
                    // The command comes before the next queue item. None starts after StopAsync.
                    bool open = await command.ConfigureAwait(false), ending = Volatile.Read(ref stopped);
                    if (open && !ending && clears.Reader.TryRead(out var request)) RunClear(request);
                    command = open && !ending ? clears.Reader.WaitToReadAsync().AsTask() : new TaskCompletionSource<bool>().Task;
                }
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
                    for (int i = 0; i < 120 && queue.Reader.TryRead(out var item); i++)
                    {
                        Process(item);
                        if (command.IsCompleted) break;
                    }
                    available = queue.Reader.WaitToReadAsync().AsTask();
                }
                yielded = false;
                // A step never starts after StopAsync.
                if (stepping && !Volatile.Read(ref stopped))
                {
                    long started = Stopwatch.GetTimestamp(); Step();
                    yielded = stepping && Stopwatch.GetElapsedTime(started) < HistoryClear.StepBudget / 2;
                }
            }
            var final = Boundary();
            if (folder.Flush() is { } last) Closed(last, final);
            Flush();
        }
        catch (Exception error)
        {
            // Only this worker stops; the collector and the other histories continue.
            Stopped(error);
            Fail(error); Volatile.Write(ref accepting, false); queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }
    /// <summary>The period's per-application totals, read off the UI thread in one snapshot. An unreadable store reports
    /// <see cref="Status"/> and returns an empty view; an invalid range or rank throws <see cref="ArgumentOutOfRangeException"/>.</summary>
    public async Task<ProcessResourceView> ReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, ProcessRank rank)
        => await TryReadApplicationsAsync(from, to, rank).ConfigureAwait(false)
            ?? new(from, to, [], 0, new(ProcessHistory.EverythingElse, 0, 0, 0, 0, 0, 0, true, 0), 0, 0, null, null, null, 0);
    /// <summary>Like <see cref="ReadApplicationsAsync"/>, but null when the store cannot be read, so a caller can tell a failure from an
    /// empty period; <see cref="Status"/> then says why. <paramref name="limit"/> is the store's row limit (at most 1,000).</summary>
    public async Task<ProcessResourceView?> TryReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to, ProcessRank rank, int limit = 200)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplications(machine, from, to, rank, time.GetUtcNow(), limit)).ConfigureAwait(false);
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
