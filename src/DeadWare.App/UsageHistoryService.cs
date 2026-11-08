using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One background writer folds publications into minutes and saves them; reads use independent connections.</summary>
public sealed class UsageHistoryService
{
    public const string SavedStatus = "Usage history saved locally · up to 400 days.";
    /// <summary>How often closed minutes are saved by default; a page's save delay is this plus the polling interval.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(15);
    private sealed record Pending(Reading Reading, long Generation, long Drops);
    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly UsageHistoryStore store;
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
    private int recording = 1;
    private long generation, dropped;
    private string status = "Usage history starts with monitoring.";
    private bool failed;

    public UsageHistoryService(string path, string machineKey, TimeProvider? timeProvider = null, TimeSpan? flushInterval = null)
    {
        store = new(path); machine = machineKey; time = timeProvider ?? TimeProvider.System; interval = flushInterval ?? DefaultFlushInterval;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
        markerPath = Path.ChangeExtension(Path.GetFullPath(path), ".clear.json");
    }
    public long DroppedSamples => Interlocked.Read(ref dropped);
    public int PendingSamples => queue.Reader.Count;
    public bool Failed => Volatile.Read(ref failed);
    /// <summary>Raised on the history worker for every minute the folder closes, in order. Handlers must be quick and must not throw.</summary>
    public event Action<UsageMinuteBatch>? MinuteClosed;
    public string Status => (Volatile.Read(ref failed) ? Volatile.Read(ref status) : Volatile.Read(ref recording) == 0
        ? "Usage recording paused. Saved history is retained." : Volatile.Read(ref status)) +
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
            var folder = new UsageMinuteFolder(); var pending = new List<UsageMinuteBatch>(64);
            long previousGeneration = 0, previousDrops = 0;
            // The boundary rule of an All time clear: in-scope bytes of minutes before Before are dropped until a minute at or after
            // Before is folded or BoundaryLimit has passed since it was installed. Worker-only state.
            HistoryClear? boundary = null; long boundarySince = 0; bool stepping = false;
            void InstallBoundary(HistoryClear clear) { boundary = clear; boundarySince = Stopwatch.GetTimestamp(); }
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
                if (boundary is not null && Stopwatch.GetElapsedTime(boundarySince) >= HistoryClear.BoundaryLimit) boundary = null;
                folder.Excluded = Volatile.Read(ref exclusions); folder.Boundary = boundary;
                bool captureAvailable = reading.Connections?.Available ?? reading.Processes.Count > 0;
                if (folder.Observe(reading.Sample, reading.Processes, captureAvailable, Gpus(reading)) is { } batch) Closed(batch);
                // The rule ends once a minute at or after Before is folded.
                if (boundary is { } rule && UsageMetrics.Floor(reading.Sample.Timestamp) >= rule.Before) boundary = folder.Boundary = null;
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
            // The clear command in the spec's order: marker, All time Forget and boundary, Flush, then the steps. A resume (Try again with
            // the marker saved) repeats nothing already done. A Flush failure is the worker's own failure.
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
                if (!command.Resume && clear.AllTime) { folder.Forget(clear.AppKey); InstallBoundary(clear); }
                else if (command.Resume && clear.Before > time.GetUtcNow()) InstallBoundary(clear);
                Flush();
                stepping = true;
            }
            // One step. ClearStep throws without a readable pending clear, so a marker already gone is Done.
            void Step()
            {
                if (clears.StepOverride is null && store.PendingClear is null) { clears.Done(); stepping = false; return; }
                if (clears.Step(budget => store.ClearStep(budget))) stepping = false;
            }
            using var timer = new PeriodicTimer(interval, time);
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask(), available = queue.Reader.WaitToReadAsync().AsTask(),
                command = clears.Reader.WaitToReadAsync().AsTask();
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
                    Flush(); tick = timer.WaitForNextTickAsync().AsTask();
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
            if (folder.Flush() is { } last) Closed(last);
            Flush();
        }
        catch (Exception error)
        {
            Stopped(error);
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
    /// <summary>Every saved application name for the first-use seed, or null when the store cannot be read or a clear is in progress.</summary>
    public async Task<IReadOnlyList<ApplicationSeen>?> ReadApplicationNamesAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplicationNames(machine)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Fail(error);
            return null;
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
    /// <summary>The traffic strip's series, or null when the store cannot be read, so a caller can tell a failure from an empty period;
    /// <see cref="Status"/> then says why.</summary>
    public async Task<TrafficStrip?> TryReadApplicationSeriesAsync(DateTimeOffset from, DateTimeOffset to, int maxPoints)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplicationSeries(machine, from, to, maxPoints)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return null;
        }
    }
    /// <summary>Like <see cref="ReadApplicationsAsync"/>, but null when the store cannot be read; <see cref="Status"/> then says why.</summary>
    public async Task<UsageApplicationView?> TryReadApplicationsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.ReadApplications(machine, from, to)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return null;
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
