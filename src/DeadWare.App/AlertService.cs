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
    private sealed record RulesWork(IReadOnlyList<AlertRule> Rules, DateTimeOffset At) : Work;
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

    /// <summary>Raised on the alert worker for each episode that may notify. Subscribers marshal to their own thread.</summary>
    public event Action<AlertEpisode>? EpisodeStarted;

    public AlertService(string path, string machineKey, UsageHistoryService usage, Func<string?> gpuKey, TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null, TimeZoneInfo? localZone = null)
    {
        store = new(path); machine = machineKey; this.usage = usage; this.gpuKey = gpuKey;
        time = timeProvider ?? TimeProvider.System; interval = flushInterval ?? TimeSpan.FromSeconds(15); zone = localZone ?? TimeZoneInfo.Local;
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(flushInterval));
    }
    public long DroppedObservations => Interlocked.Read(ref dropped);
    public int PendingObservations => queue.Reader.Count;
    public bool Failed => Volatile.Read(ref failed);
    public IReadOnlyList<AlertRule> Rules => Volatile.Read(ref rules);
    public bool NotificationsEnabled => Volatile.Read(ref notifications) == 1;
    public string Status => Volatile.Read(ref status) + (DroppedObservations > 0 ? $" · {DroppedObservations} observations dropped." : "");

    /// <summary>Installs rules and the notification switch. The evaluator adopts them on its own worker; episodes of changed rules end there.</summary>
    public void Configure(IReadOnlyList<AlertRule> alertRules, bool notificationsEnabled)
    {
        var normalized = AlertRules.Normalize(alertRules);
        Volatile.Write(ref rules, normalized);
        Volatile.Write(ref notifications, notificationsEnabled ? 1 : 0);
        Post(new RulesWork(normalized, time.GetUtcNow()));
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
    private DateTimeOffset StartOfLocalDay(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var midnight = local.Date;
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
    private async Task RunAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            var evaluator = new AlertEvaluator(zone); var pending = new List<AlertEpisode>(64);
            var startedAt = time.GetUtcNow();
            pending.AddRange(evaluator.Replace(Volatile.Read(ref rules), startedAt));
            var open = store.Open(machine);
            evaluator.Restore(open);
            // Open episodes whose rule no longer exists cannot end on their own; close them at startup.
            var adopted = new HashSet<string>(evaluator.OpenEpisodes.Select(e => e.Id), StringComparer.Ordinal);
            pending.AddRange(open.Where(e => !adopted.Contains(e.Id)).Select(e => e with { End = startedAt }));
            try
            {
                var today = await usage.ReadApplicationsAsync(StartOfLocalDay(startedAt), startedAt, 1000).ConfigureAwait(false);
                var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var application in today.Applications) totals[application.Name] = totals.GetValueOrDefault(application.Name) + application.Received + application.Sent;
                evaluator.SeedDay(startedAt, totals);
            }
            catch (Exception) { /* Without a seed the day's budget counts from now; usage history reports its own failure. */ }
            void Flush()
            {
                if (pending.Count == 0) return;
                store.Append(machine, pending, time.GetUtcNow()); pending.Clear();
                Volatile.Write(ref status, SavedStatus);
            }
            void Apply(AlertEvaluation evaluation)
            {
                pending.AddRange(evaluation.Started); pending.AddRange(evaluation.Ended);
                if (Volatile.Read(ref notifications) == 1)
                    foreach (var episode in evaluation.Notify)
                        try { EpisodeStarted?.Invoke(episode); } catch { /* A failing notifier never fails the log. */ }
                if (pending.Count >= 64) Flush();
            }
            Flush();
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
                    for (int i = 0; i < 128 && queue.Reader.TryRead(out var work); i++)
                        switch (work)
                        {
                            case MinuteWork minute: Apply(evaluator.ObserveMinute(minute.Batch, gpuKey())); break;
                            case BatteryWork battery: Apply(evaluator.ObserveBattery(battery.At, battery.Reading)); break;
                            case RulesWork replaced: pending.AddRange(evaluator.Replace(replaced.Rules, replaced.At)); break;
                        }
                    available = queue.Reader.WaitToReadAsync().AsTask();
                }
            }
            Flush();
        }
        catch (Exception error)
        {
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
