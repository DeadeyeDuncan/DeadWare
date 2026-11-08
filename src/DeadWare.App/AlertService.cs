using System.Collections.Frozen;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Recorded statistics beside a threshold input: what the metric did over a window, and how often it was at or above the input.</summary>
public sealed record AlertWindowStats(double? Maximum, double? Average, double? ShareAtOrAbove, long Minutes);
public sealed record AlertSuggestion(AlertWindowStats Week, AlertWindowStats Month);

/// <summary>One background worker owns the evaluator and the episode log; the usage history worker hands it each closed minute.</summary>
public sealed class AlertService
{
    public const string SavedStatus = "Alert log saved locally · up to 400 days.";
    private abstract record Work;
    private sealed record MinuteWork(UsageMinuteBatch Batch) : Work;
    private sealed record BatteryWork(DateTimeOffset At, BatteryReading Reading) : Work;
    private sealed record RulesWork(IReadOnlyList<AlertRule> Rules, DateTimeOffset At, IReadOnlyList<string> Ignored, IReadOnlyList<string> Excluded) : Work;
    /// <summary>At most 1,024 apps' first traffic in one poll; empty when it only signals that capture has run.</summary>
    private sealed record NetworkUseWork(DateTimeOffset At, IReadOnlyList<NetworkUse> Uses) : Work;
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(512)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly AlertStore store;
    private readonly string machine;
    private readonly UsageHistoryService usage;
    private readonly Func<string?> gpuKey;
    private readonly TimeProvider time;
    private readonly TimeSpan interval;
    private readonly TimeZoneInfo zone;
    private readonly object lifecycle = new();
    private Task? worker, initialization;
    private bool stopped, accepting, subscribed;
    private int notifications = 1;
    private long dropped;
    private string status = "Alerts start with monitoring.";
    private bool failed;
    private IReadOnlyList<AlertRule> rules = [];
    private BatteryReading? lastBattery;
    private readonly TimeSpan groupWindow;
    private readonly Func<Task<IReadOnlyList<ApplicationSeen>?>>? connectionNames;
    private IReadOnlyList<string> ignoredApps = [], excludedApps = [];
    private FrozenSet<string> excludedKeys = FrozenSet<string>.Empty;
    private bool firstUseRule, learningStarted;
    // Incremented on the worker after a clear forgets apps, so detection posts them again within the minute.
    private long knownGeneration = 0;
    private FirstUseStatus firstUse = new(false, false, null, null, null);
    private readonly HistoryClearCommand clears = new();
    // The exception type that stopped the worker, or null while it has not stopped on a failure.
    private string? stoppedBy;
    // Detection state, touched only on the collector thread.
    private readonly HashSet<string> posted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> nameKeys = new(StringComparer.Ordinal);
    private long postedMinute = long.MinValue, postedGeneration;
    private bool postedThisMinute;
    private const int NameKeyCacheSize = 4096;

    /// <summary>Raised on the alert worker for each episode that may notify. Subscribers marshal to their own thread.</summary>
    public event Action<AlertEpisode>? EpisodeStarted;
    /// <summary>Raised on the alert worker when a first network use group closes with apps left and notifications are on; at most one a group
    /// window. A group still open at Quit or sign-out raises nothing. Subscribers marshal to their own thread.</summary>
    public event Action<FirstUseGroup>? FirstUseGroupClosed;

    /// <param name="connectionNames">Reads Connections history's names for the first-use seed; called only after <see cref="Start"/>. Throwing or missing counts as a failed read.</param>
    /// <param name="firstUseGroupWindow">How long apps first seen after the first one share its balloon group: above 0 and at most 60 s (the default).</param>
    public AlertService(string path, string machineKey, UsageHistoryService usage, Func<string?> gpuKey, TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null, TimeZoneInfo? localZone = null, Func<Task<IReadOnlyList<ApplicationSeen>?>>? connectionNames = null,
        TimeSpan? firstUseGroupWindow = null)
    {
        store = new(path); machine = machineKey; this.usage = usage; this.gpuKey = gpuKey;
        time = timeProvider ?? TimeProvider.System; interval = flushInterval ?? TimeSpan.FromSeconds(15); zone = localZone ?? TimeZoneInfo.Local;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
        groupWindow = firstUseGroupWindow ?? FirstNetworkUse.GroupWindow;
        if (groupWindow <= TimeSpan.Zero || groupWindow > FirstNetworkUse.GroupWindow) throw new ArgumentOutOfRangeException(nameof(firstUseGroupWindow));
        this.connectionNames = connectionNames;
    }
    public long DroppedObservations => Interlocked.Read(ref dropped);
    public int PendingObservations => queue.Reader.Count;
    public bool Failed => Volatile.Read(ref failed);
    public IReadOnlyList<AlertRule> Rules => Volatile.Read(ref rules);
    public bool NotificationsEnabled => Volatile.Read(ref notifications) == 1;
    public string Status => Volatile.Read(ref status) + (DroppedObservations > 0 ? $" · {DroppedObservations} observations dropped." : "") +
        HistoryClearCopy.StatusSuffix(clears.State, true);
    /// <summary>The first-use tracker's state as the worker last published it; KnownApps is null until the worker has restored it.</summary>
    public FirstUseStatus FirstUse => Volatile.Read(ref firstUse);

    /// <summary>This store's clear. The internal setter is a test seam; the app never calls it.</summary>
    public HistoryClearState ClearState { get => clears.State; internal set => clears.State = value; }
    /// <summary>Puts the request in the worker's command channel (capacity 1) and sets Queued; the worker runs it between data items.
    /// Refused while a clear is active. After the worker stopped on a failure the request is Unavailable with that failure's type, and
    /// nothing is written for it.</summary>
    public void Clear(HistoryClear request)
    {
        clears.Post(request);
        if (Volatile.Read(ref stoppedBy) is { } failure) clears.Unavailable(failure);
    }
    /// <summary>Re-posts the kept request while Failed and the worker runs: the full first step without a saved marker, otherwise a resume
    /// (EndScope for All time, Flush, then the steps) that forgets no key again.</summary>
    public void RetryClear() => clears.Retry(WorkerRunning);
    public bool CanRetryClear => clears.CanRetry(WorkerRunning);
    /// <summary>A test seam that replaces the store's step.</summary>
    internal Func<TimeSpan, HistoryClearProgress>? ClearStepOverride { get => clears.StepOverride; set => clears.StepOverride = value; }
    private bool WorkerRunning { get { lock (lifecycle) return !stopped && Volatile.Read(ref stoppedBy) is null && worker is { IsCompleted: false }; } }

    /// <summary>Installs rules, the notification switch, the Don't alert list and the Don't record list. The evaluator and the first-use tracker
    /// adopt them on the worker; episodes of changed rules, and budget episodes of newly excluded apps, end there.</summary>
    public void Configure(IReadOnlyList<AlertRule> alertRules, bool notificationsEnabled, IReadOnlyList<string>? ignoredApps = null, IReadOnlyList<string>? excludedApps = null)
    {
        var normalized = AlertRules.Normalize(alertRules);
        // Copies: the settings lists the session passes stay mutable.
        IReadOnlyList<string> ignored = [.. ignoredApps ?? []], excluded = [.. excludedApps ?? []];
        Volatile.Write(ref rules, normalized);
        Volatile.Write(ref notifications, notificationsEnabled ? 1 : 0);
        Volatile.Write(ref this.ignoredApps, ignored);
        Volatile.Write(ref this.excludedApps, excluded);
        Volatile.Write(ref excludedKeys, AppExclusions.Keys(excluded));
        Volatile.Write(ref firstUseRule, normalized.Any(r => r.Kind == AlertKind.FirstNetworkUse));
        if (FirstUse.LearningStart is not null) Volatile.Write(ref learningStarted, true);
        Post(new RulesWork(normalized, time.GetUtcNow(), ignored, excluded));
    }
    public void Start()
    {
        lock (lifecycle)
        {
            if (worker is not null || stopped) return;
            Volatile.Write(ref accepting, true);
            usage.MinuteClosed += OnMinute; subscribed = true;
            worker = Task.Run(RunAsync);
        }
    }
    /// <summary>Battery readings arrive with every publication; only changes are queued.</summary>
    public void TryRecordBattery(Reading reading)
    {
        if (!Volatile.Read(ref accepting)) return;
        var battery = reading.Battery;
        if (battery.Percentage is null || battery.PowerState == BatteryPowerState.Unknown) return;
        if (lastBattery is { } last && last.Percentage == battery.Percentage && last.PowerState == battery.PowerState) return;
        lastBattery = battery;
        Post(new BatteryWork(reading.Sample.Timestamp, battery));
    }
    /// <summary>
    /// First network use detection, on the collector thread: no I/O and no locks. Each learnable, non-excluded app with non-loopback traffic is
    /// posted at most once per UTC minute (and again after a clear forgets apps), at most 1,024 per work; the rest follow in the next poll.
    /// </summary>
    public void TryRecordNetworkUse(Reading reading)
    {
        if (!Volatile.Read(ref accepting) || !Volatile.Read(ref firstUseRule)) return;
        if (reading.Connections is not { Available: true } sample) return;
        long minute = sample.Timestamp.UtcTicks / TimeSpan.TicksPerMinute, generation = Volatile.Read(ref knownGeneration);
        if (minute != postedMinute || generation != postedGeneration)
        {
            posted.Clear(); postedMinute = minute; postedGeneration = generation; postedThisMinute = false;
        }
        var excluded = Volatile.Read(ref excludedKeys);
        List<NetworkUse>? uses = null; Dictionary<string, int>? recorded = null;
        foreach (var delta in sample.Deltas)
        {
            if (FirstNetworkUse.IsLoopback(delta.Peer.RemoteAddress)) continue;
            string name = delta.Peer.Process.Name ?? "";
            if (!nameKeys.TryGetValue(name, out var key))
            {
                if (nameKeys.Count >= NameKeyCacheSize) nameKeys.Clear();
                // "" marks a name that can never be learned ("PID n", Unknown application); a real key is never empty.
                nameKeys[name] = key = FirstNetworkUse.IsLearnable(name) ? AppKey.Of(name) : "";
            }
            if (key.Length == 0 || excluded.Contains(key)) continue;
            if (recorded is not null && recorded.TryGetValue(key, out int index))
            {
                if (delta.FirstSeen < uses![index].FirstSeen) uses[index] = uses[index] with { FirstSeen = delta.FirstSeen };
                continue;
            }
            // Keys over the cap stay out of the posted set, so the next poll posts them.
            if ((uses?.Count ?? 0) >= FirstNetworkUse.MaximumUses || !posted.Add(key)) continue;
            (uses ??= []).Add(new NetworkUse(UsageMetrics.NormalizeName(name), delta.FirstSeen));
            (recorded ??= new(StringComparer.Ordinal))[key] = uses.Count - 1;
        }
        // Until the worker records a learning start, the first poll of each minute also says that capture has run.
        if (uses is not null || (!Volatile.Read(ref learningStarted) && !postedThisMinute))
        {
            Post(new NetworkUseWork(sample.Timestamp, uses ?? (IReadOnlyList<NetworkUse>)[]));
            postedThisMinute = true;
        }
    }
    public Task StopAsync()
    {
        lock (lifecycle)
        {
            stopped = true; Volatile.Write(ref accepting, false);
            if (subscribed) { usage.MinuteClosed -= OnMinute; subscribed = false; }
            queue.Writer.TryComplete();
            return worker ?? Task.CompletedTask;
        }
    }
    private void OnMinute(UsageMinuteBatch batch) => Post(new MinuteWork(batch));
    private void Post(Work work)
    {
        if (!Volatile.Read(ref accepting)) return;
        if (!queue.Writer.TryWrite(work)) Interlocked.Increment(ref dropped);
    }
    private Task InitializeAsync()
    {
        lock (lifecycle) return initialization ??= Task.Run(() => { store.Initialize(); Volatile.Write(ref status, SavedStatus); });
    }
    private void Fail(Exception error)
    {
        Volatile.Write(ref status, $"Alert log unavailable ({error.GetType().Name}). Saved episodes were not deleted. Live monitoring continues; resolve storage access and restart to retry.");
        Volatile.Write(ref failed, true);
    }
    /// <summary>The worker stopped on <paramref name="error"/>: a clear it was running fails (its marker, when saved, resumes at the next
    /// start), and a queued one is Unavailable.</summary>
    private void Stopped(Exception error)
    {
        string type = error.GetType().Name;
        Volatile.Write(ref stoppedBy, type);
        var state = clears.State;
        if (state.Phase == HistoryClearPhase.Clearing || state is { Phase: HistoryClearPhase.Queued, MarkerSaved: true }) clears.Fail(type, store.PendingClear is not null);
        else if (state.Phase == HistoryClearPhase.Queued) clears.Unavailable(type);
    }
    private DateTimeOffset StartOfLocalDay(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var midnight = local.Date;
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
    private static AlertRule? FirstRule(IReadOnlyList<AlertRule> list) => list.FirstOrDefault(r => r.Kind == AlertKind.FirstNetworkUse);
    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var evaluator = new AlertEvaluator(zone); var pending = new List<AlertEpisode>(64);
            var tracker = new FirstUseTracker(zone, groupWindow);
            var startedAt = time.GetUtcNow();
            // Work posted before Start was dropped, so the stored snapshot is what Configure last installed.
            var startRules = Volatile.Read(ref rules); var startExcluded = Volatile.Read(ref excludedApps);
            pending.AddRange(evaluator.Replace(startRules, startedAt));
            var open = store.Open(machine);
            evaluator.Restore(open);
            // Open episodes whose rule no longer exists cannot end on their own; close them at startup.
            var adopted = new HashSet<string>(evaluator.OpenEpisodes.Select(e => e.Id), StringComparer.Ordinal);
            pending.AddRange(open.Where(e => !adopted.Contains(e.Id)).Select(e => e with { End = startedAt }));
            // The Don't record set goes after Replace and Restore (Restore ignores it), so an excluded budget's restored episode ends now; the seed below skips it.
            pending.AddRange(evaluator.SetExcluded(AppExclusions.Keys(startExcluded), startedAt).Ended);
            tracker.Restore(store.ReadFirstUse(machine));
            var seedRule = FirstRule(startRules);
            tracker.Configure(seedRule, Volatile.Read(ref ignoredApps), startExcluded);
            PublishFirstUse();
            await SeedAsync(seedRule).ConfigureAwait(false);
            try
            {
                // Per-minute rows let the evaluator skip a minute the usage worker already handed over before this seed was read.
                var rows = new List<UsageApplicationMinute>();
                foreach (var name in evaluator.Rules.Where(r => r.Kind == AlertKind.ApplicationDaily).Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase))
                    rows.AddRange(await usage.ReadApplicationMinutesAsync(name, StartOfLocalDay(startedAt), startedAt.AddMinutes(1)).ConfigureAwait(false));
                evaluator.SeedDay(startedAt, rows);
            }
            catch (Exception) { /* Without a seed the day's budget counts from now; usage history reports its own failure. */ }
            bool stepping = false;
            void PublishFirstUse()
            {
                var current = tracker.Status;
                Volatile.Write(ref firstUse, current);
                if (current.LearningStart is not null) Volatile.Write(ref learningStarted, true);
            }
            void Flush()
            {
                var now = time.GetUtcNow();
                tracker.Expire(now);
                bool wrote = false;
                // An episode and its known row always share an Append; each Append holds at most 1,024 episodes, upserts and deletes.
                while (true)
                {
                    var changes = tracker.HasChanges ? tracker.TakeChanges(FirstNetworkUse.MaximumChanges) : null;
                    int count = Math.Min(pending.Count, AlertStore.MaximumBatch);
                    if (count == 0 && changes is null) break;
                    store.Append(machine, count == pending.Count ? pending : pending.GetRange(0, count), now, changes);
                    pending.RemoveRange(0, count); wrote = true;
                }
                PublishFirstUse();
                if (wrote) Volatile.Write(ref status, SavedStatus);
            }
            void CloseDueGroups()
            {
                // Once Quit or sign-out has begun, a group still open shows no balloon.
                if (Volatile.Read(ref stopped)) return;
                var now = time.GetUtcNow();
                // TakeDueGroup closes only the earliest due group, and returns null also when that group emptied, so drain every due one.
                while (tracker.GroupDue is { } due && due <= now)
                    if (tracker.TakeDueGroup(now) is { } group && Volatile.Read(ref notifications) == 1)
                        try { FirstUseGroupClosed?.Invoke(group); } catch { /* A failing notifier never fails the log. */ }
            }
            // Seeds each history store whose marker is missing, only while a FirstNetworkUse rule exists. Both reads finish before anything is
            // merged, and the stop flag is checked before each read and before the first write, so a seed cut short by Quit writes nothing
            // and runs at the next start. The seed adds known rows and markers only, never episodes; Flush writes them in chunks of 1,024.
            async Task SeedAsync(AlertRule? rule)
            {
                if (rule is null) return;
                var read = new List<(FirstUseSource Source, IReadOnlyList<ApplicationSeen>? Names)>(2);
                foreach (var source in (FirstUseSource[])[FirstUseSource.Usage, FirstUseSource.Connections])
                {
                    if (!tracker.NeedsSeed(source)) continue;
                    if (Volatile.Read(ref stopped)) return;
                    read.Add((source, await ReadNamesAsync(source).ConfigureAwait(false)));
                }
                if (read.Count == 0 || Volatile.Read(ref stopped)) return;
                var now = time.GetUtcNow();
                // A null result leaves that store's marker unset, so it is read again at the next start.
                foreach (var (source, names) in read) tracker.Seed(source, names, now);
                Flush();
            }
            async Task<IReadOnlyList<ApplicationSeen>?> ReadNamesAsync(FirstUseSource source)
            {
                // Each read runs on its own store's side (its read connection or gate), never here; a throwing or missing delegate counts as null.
                try
                {
                    if (source == FirstUseSource.Usage) return await usage.ReadApplicationNamesAsync().ConfigureAwait(false);
                    return connectionNames is null ? null : await connectionNames().ConfigureAwait(false);
                }
                catch (Exception) { return null; }
            }
            async Task ReseedAsync(IReadOnlyList<string> targets)
            {
                if (targets.Count == 0) return;
                var now = time.GetUtcNow();
                foreach (var target in targets)
                {
                    try
                    {
                        var rows = await usage.ReadApplicationMinutesAsync(target, StartOfLocalDay(now), now.AddMinutes(1)).ConfigureAwait(false);
                        evaluator.SeedDay(now, rows, new HashSet<string>(StringComparer.Ordinal) { AppKey.Of(target) });
                    }
                    catch (Exception) { /* A failed read leaves the rule's day total at 0, as at startup; usage history reports its own failure. */ }
                }
            }
            void Apply(AlertEvaluation evaluation)
            {
                pending.AddRange(evaluation.Started); pending.AddRange(evaluation.Ended);
                if (Volatile.Read(ref notifications) == 1)
                    foreach (var episode in evaluation.Notify)
                        try { EpisodeStarted?.Invoke(episode); } catch { /* A failing notifier never fails the log. */ }
                if (pending.Count >= 64) Flush();
            }
            // The clear's first step, in the spec's order with no other work in between: Flush (the work pending before the request),
            // the marker with this machine's in-scope known rows, then for All time EndScope, Forget and the generation increment, and
            // Flush again. A marker failure clears nothing and leaves the tracker unchanged; a Flush failure is the worker's own failure.
            void RunClear(HistoryClearCommand.Command command)
            {
                var clear = command.Clear;
                clears.Clearing(null);
                if (command.Resume) { ResumeClear(clear); return; }
                Flush();
                try { store.BeginClear(machine, clear); }
                catch (Exception error) { clears.Fail(error.GetType().Name, markerSaved: false); return; }
                clears.MarkerSaved();
                if (clear.AllTime)
                {
                    pending.AddRange(evaluator.EndScope(clear.AppKey, clear.Requested, resetDay: true));
                    tracker.Forget(clear.AppKey, clear.Requested);
                    // Detection posts a running app again at its next poll, even within the minute.
                    Interlocked.Increment(ref knownGeneration);
                }
                Flush();
                stepping = true;
            }
            // A marker found at startup, or Try again after it was saved: EndScope (All time) and Flush again, because a crash can lose
            // them, then the steps. The known rows went with the marker's transaction, so Forget and the increment are not repeated.
            void ResumeClear(HistoryClear clear)
            {
                if (clear.AllTime) pending.AddRange(evaluator.EndScope(clear.AppKey, clear.Requested, resetDay: true));
                Flush();
                stepping = true;
            }
            // One step. ClearStep throws when no clear is pending, so a marker already gone is Done; a step's exception is Failed with
            // the marker saved and never reaches the worker's catch.
            void Step()
            {
                if (clears.StepOverride is null && store.PendingClear is null) { clears.Done(); stepping = false; return; }
                if (clears.Step(budget => store.ClearStep(machine, budget))) stepping = false;
            }
            // Resume after Restore and the seeds.
            if (store.PendingClear is { } marker) { clears.Resume(marker); ResumeClear(marker); }
            Flush();
            using var timer = new PeriodicTimer(interval, time);
            Task<bool> tick = timer.WaitForNextTickAsync().AsTask(), available = queue.Reader.WaitToReadAsync().AsTask(),
                command = clears.Reader.WaitToReadAsync().AsTask();
            bool yielded = false;
            // The third wake-up: while a first-use group is open, the worker wakes at its due time, not at the next flush tick.
            Task? wake = null; DateTimeOffset? wakeFor = null;
            while (true)
            {
                if (tracker.GroupDue is { } due)
                {
                    if (wake is null || wakeFor != due)
                    {
                        var delay = due - time.GetUtcNow();
                        wake = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time); wakeFor = due;
                    }
                }
                else { wake = null; wakeFor = null; }
                // While a clear steps, the worker alternates one data pass with one step; it waits only after a step that yielded early.
                if (!stepping || yielded)
                {
                    var waits = new List<Task>(5) { tick, available, command };
                    if (wake is not null) waits.Add(wake);
                    if (stepping) waits.Add(Task.Delay(HistoryClear.StepBudget));
                    await Task.WhenAny(waits).ConfigureAwait(false);
                }
                if (command.IsCompleted)
                {
                    // The command comes before the next queue item. None starts after StopAsync.
                    bool commandOpen = await command.ConfigureAwait(false), ending = Volatile.Read(ref stopped);
                    if (commandOpen && !ending && clears.Reader.TryRead(out var request)) RunClear(request);
                    command = commandOpen && !ending ? clears.Reader.WaitToReadAsync().AsTask() : new TaskCompletionSource<bool>().Task;
                }
                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    CloseDueGroups(); Flush(); tick = timer.WaitForNextTickAsync().AsTask();
                }
                if (wake is { IsCompleted: true })
                {
                    // A delay that ends a little early leaves the group open; the next pass waits again for what is left.
                    wake = null; wakeFor = null;
                    CloseDueGroups();
                }
                if (available.IsCompleted)
                {
                    if (!await available.ConfigureAwait(false)) break;
                    for (int i = 0; i < 128 && !command.IsCompleted && queue.Reader.TryRead(out var work); i++)
                        switch (work)
                        {
                            case MinuteWork minute: Apply(evaluator.ObserveMinute(minute.Batch, gpuKey())); break;
                            case BatteryWork battery: Apply(evaluator.ObserveBattery(battery.At, battery.Reading)); break;
                            case RulesWork replaced:
                                pending.AddRange(evaluator.Replace(replaced.Rules, replaced.At));
                                var nextRule = FirstRule(replaced.Rules);
                                bool ruleAdded = seedRule is null && nextRule is not null;
                                seedRule = nextRule;
                                tracker.Configure(nextRule, replaced.Ignored, replaced.Excluded);
                                var exclusion = evaluator.SetExcluded(AppExclusions.Keys(replaced.Excluded), replaced.At);
                                pending.AddRange(exclusion.Ended);
                                // Only the budgets whose app left the list re-read today's saved minutes; other totals stay.
                                await ReseedAsync(exclusion.Reseed).ConfigureAwait(false);
                                // The first FirstNetworkUse rule seeds the stores whose marker is missing, after the new Don't record set is in place.
                                if (ruleAdded) await SeedAsync(nextRule).ConfigureAwait(false);
                                break;
                            case NetworkUseWork use:
                                // First-use episodes never pass through the evaluator or EpisodeStarted, so the hour floor never applies.
                                pending.AddRange(tracker.Observe(use.At, use.Uses, time.GetUtcNow()));
                                // A work of 1,024 new apps, up to 128 works a wake, would exceed one Append's batch without this.
                                if (pending.Count >= 64) Flush();
                                break;
                        }
                    CloseDueGroups(); PublishFirstUse();
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
            Flush();
        }
        catch (Exception error)
        {
            Stopped(error);
            Fail(error); Volatile.Write(ref accepting, false); queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out _)) Interlocked.Increment(ref dropped);
        }
    }
    public async Task<AlertLogView> ReadEpisodesAsync(DateTimeOffset from, DateTimeOffset to, int maxRows = AlertStore.MaximumRows)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.Read(machine, from, to, maxRows)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException)
        {
            Fail(error);
            return new(from, to, [], 0, 0);
        }
    }
    public async Task<bool> AcknowledgeAsync(string id)
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(() => store.Acknowledge(machine, id)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ArgumentException) { Fail(error); return false; }
    }
    /// <summary>Recorded statistics for the rule's metric over the last 7 and 30 days. Battery rules have no history to suggest from.</summary>
    public async Task<AlertSuggestion> SuggestAsync(AlertRule rule)
    {
        if (AlertRules.Normalize(rule) is not { } normalized) throw new ArgumentException("Invalid alert rule.", nameof(rule));
        var to = time.GetUtcNow();
        return new(await WindowAsync(normalized, to.AddDays(-7), to, 7).ConfigureAwait(false), await WindowAsync(normalized, to.AddDays(-30), to, 30).ConfigureAwait(false));
    }
    private async Task<AlertWindowStats> WindowAsync(AlertRule rule, DateTimeOffset from, DateTimeOffset to, int days)
    {
        switch (rule.Kind)
        {
            case AlertKind.Metric:
            case AlertKind.NetworkRate:
            {
                string? hardware = UsageMetrics.UsesHardwareId(rule.Target) ? gpuKey() : null;
                if (UsageMetrics.UsesHardwareId(rule.Target) && string.IsNullOrEmpty(hardware)) return new(null, null, null, 0);
                var view = await usage.ReadMetricAsync(rule.Target, hardware, from, to, 60).ConfigureAwait(false);
                if (view.Minutes == 0) return new(null, null, null, 0);
                var share = await usage.ShareAtOrAboveAsync(rule.Target, hardware, from, to, rule.Threshold).ConfigureAwait(false);
                return new(view.Statistics.Maximum, view.Statistics.Average, share.Total == 0 ? null : (double)share.AtOrAbove / share.Total, view.Minutes);
            }
            case AlertKind.ApplicationDaily:
            {
                var view = await usage.ReadApplicationsAsync(from, to, 1000).ConfigureAwait(false);
                var match = view.Applications.FirstOrDefault(a => a.Name.Equals(rule.Target, StringComparison.OrdinalIgnoreCase));
                if (match is null) return new(null, null, null, view.RecordedMinutes);
                double total = match.Received + match.Sent;
                return new(total, total / days, null, view.RecordedMinutes);
            }
            default:
                return new(null, null, null, 0);
        }
    }
}
