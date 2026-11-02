namespace DeadWare.Core;

/// <summary>What one observation changed: episodes that started, episodes that ended, and the started ones that may notify.</summary>
public sealed record AlertEvaluation(IReadOnlyList<AlertEpisode> Started, IReadOnlyList<AlertEpisode> Ended, IReadOnlyList<AlertEpisode> Notify)
{
    public static AlertEvaluation Empty { get; } = new([], [], []);
}

/// <summary>Pure rule evaluation over closed usage minutes and battery readings. No timers, threads or storage; one owner.</summary>
public sealed class AlertEvaluator
{
    private sealed class State
    {
        public AlertRule Rule = null!;
        public int Above, Below;
        public DateTimeOffset? AboveSince;
        public AlertEpisode? Open;
        public DateTimeOffset? LastNotified;
        public double Peak;
        public bool Armed = true;
        public DateOnly? Day;
        public long DayBytes;
        public bool FiredToday;
        /// <summary>Stored minutes the day total was seeded from, by minute and writing segment, so a closed minute delivered after the seed is not counted twice.</summary>
        public Dictionary<DateTimeOffset, string> Seeded = new();
    }

    private readonly TimeZoneInfo zone;
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private DateTimeOffset? lastMinute;

    public AlertEvaluator(TimeZoneInfo? localZone = null) { zone = localZone ?? TimeZoneInfo.Local; }

    public IReadOnlyList<AlertRule> Rules => states.Values.Select(s => s.Rule).ToArray();
    public IReadOnlyList<AlertEpisode> OpenEpisodes => states.Values.Where(s => s.Open is not null).Select(s => s.Open!).ToArray();

    /// <summary>Installs the rule set. Episodes of rules that were removed, disabled or changed end at <paramref name="now"/>.</summary>
    public IReadOnlyList<AlertEpisode> Replace(IReadOnlyList<AlertRule> rules, DateTimeOffset now)
    {
        var ended = new List<AlertEpisode>();
        var next = AlertRules.Normalize(rules).ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var (id, state) in states.ToArray())
        {
            if (next.TryGetValue(id, out var rule) && rule == state.Rule) continue;
            if (state.Open is { } open) ended.Add(open with { End = now });
            states.Remove(id);
        }
        foreach (var rule in next.Values)
            if (!states.ContainsKey(rule.Id)) states[rule.Id] = new State { Rule = rule };
        return ended;
    }

    /// <summary>Re-adopts episodes that were still open when the app last stopped, so the hour floor and the open state survive a restart.</summary>
    public void Restore(IReadOnlyList<AlertEpisode> open)
    {
        foreach (var episode in open)
        {
            if (episode.End is not null || !states.TryGetValue(episode.RuleId, out var state) || state.Open is not null) continue;
            state.Open = episode; state.Peak = episode.Peak; state.LastNotified = episode.Start; state.Armed = false;
            if (state.Rule.Kind == AlertKind.ApplicationDaily) { state.Day = LocalDay(episode.Start); state.FiredToday = true; }
        }
    }

    /// <summary>Seeds today's application totals from the store so a restart mid-day does not forget the morning.</summary>
    public void SeedDay(DateTimeOffset day, IReadOnlyDictionary<string, long> bytesByName)
    {
        var date = LocalDay(day);
        foreach (var state in states.Values)
        {
            if (state.Rule.Kind != AlertKind.ApplicationDaily) continue;
            if (state.Day is { } current && current != date) continue;
            state.Day = date;
            state.DayBytes = bytesByName.FirstOrDefault(p => p.Key.Equals(state.Rule.Target, StringComparison.OrdinalIgnoreCase)).Value;
            state.Seeded = new();
        }
    }

    /// <summary>Seeds today's application totals from stored minute rows. A later closed minute whose row was already stored by the same
    /// segment is skipped, so the seed and the live stream never count one minute twice; a row from another segment (a restart inside the
    /// minute) still adds.</summary>
    public void SeedDay(DateTimeOffset day, IReadOnlyList<UsageApplicationMinute> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var date = LocalDay(day);
        foreach (var state in states.Values)
        {
            if (state.Rule.Kind != AlertKind.ApplicationDaily) continue;
            if (state.Day is { } current && current != date) continue;
            state.Day = date; state.DayBytes = 0; state.Seeded = new();
            foreach (var row in rows)
            {
                if (!row.Name.Equals(state.Rule.Target, StringComparison.OrdinalIgnoreCase) || LocalDay(row.Minute) != date) continue;
                state.DayBytes += row.Received + row.Sent;
                state.Seeded[row.Minute] = row.Segment;
            }
        }
    }

    public AlertEvaluation ObserveMinute(UsageMinuteBatch batch, string? gpuKey)
    {
        ArgumentNullException.ThrowIfNull(batch);
        // A missing minute (sleep, restart) breaks every sustained run; a repeated minute continues it.
        bool gap = lastMinute is { } previous && batch.Minute != previous && batch.Minute != previous.AddMinutes(1);
        lastMinute = batch.Minute;
        var started = new List<AlertEpisode>(); var ended = new List<AlertEpisode>(); var notify = new List<AlertEpisode>();
        foreach (var state in states.Values)
        {
            var rule = state.Rule;
            if (!rule.Enabled || rule.Kind == AlertKind.Battery) continue;
            if (rule.Kind == AlertKind.ApplicationDaily) { ObserveApplication(state, batch, started, ended, notify); continue; }
            if (gap) { state.Above = 0; state.Below = 0; state.AboveSince = null; }
            var row = Row(batch, rule, gpuKey);
            if (row is null) continue;
            double hysteresis = AlertRules.Hysteresis(rule);
            if (row.Average >= rule.Threshold)
            {
                state.Above++; state.Below = 0; state.AboveSince ??= batch.Minute;
                if (state.Open is { } open) { if (row.Maximum > state.Peak) { state.Peak = row.Maximum; state.Open = open with { Peak = state.Peak }; } }
                else if (state.Above >= rule.Minutes && state.Armed)
                    Start(state, started, notify, state.AboveSince.Value, batch.Minute.AddMinutes(1), row.Maximum);
            }
            else
            {
                state.Above = 0; state.AboveSince = null;
                if (row.Average < rule.Threshold - hysteresis)
                {
                    state.Below++;
                    if (state.Below >= rule.Minutes)
                    {
                        if (state.Open is { } open) { ended.Add(open with { End = batch.Minute.AddMinutes(1) }); state.Open = null; }
                        state.Armed = true;
                    }
                }
                else state.Below = 0;
            }
        }
        return new(started, ended, notify);
    }

    public AlertEvaluation ObserveBattery(DateTimeOffset at, BatteryReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var started = new List<AlertEpisode>(); var ended = new List<AlertEpisode>(); var notify = new List<AlertEpisode>();
        foreach (var state in states.Values)
        {
            var rule = state.Rule;
            if (!rule.Enabled || rule.Kind != AlertKind.Battery) continue;
            // An unknown percentage or power state says nothing; the episode keeps its state until a real reading arrives.
            if (reading.Percentage is not { } level || reading.PowerState == BatteryPowerState.Unknown) continue;
            bool onBattery = reading.PowerState == BatteryPowerState.OnBattery;
            if (onBattery && level <= rule.Threshold)
            {
                if (state.Open is { } open) { if (level < state.Peak) { state.Peak = level; state.Open = open with { Peak = level }; } }
                else if (state.Armed) Start(state, started, notify, at, at, level);
            }
            else if (!onBattery || level >= rule.Threshold + AlertRules.Hysteresis(rule))
            {
                if (state.Open is { } open) { ended.Add(open with { End = at }); state.Open = null; }
                state.Armed = true;
            }
        }
        return new(started, ended, notify);
    }

    private void ObserveApplication(State state, UsageMinuteBatch batch, List<AlertEpisode> started, List<AlertEpisode> ended, List<AlertEpisode> notify)
    {
        var day = LocalDay(batch.Minute);
        if (state.Day != day)
        {
            if (state.Open is { } open) { ended.Add(open with { End = StartOfDay(day) }); state.Open = null; }
            state.Day = day; state.DayBytes = 0; state.FiredToday = false; state.Armed = true; state.Seeded = new();
        }
        foreach (var application in batch.Applications)
        {
            if (!application.Name.Equals(state.Rule.Target, StringComparison.OrdinalIgnoreCase)) continue;
            if (state.Seeded.TryGetValue(application.Minute, out var segment) && segment == application.Segment) continue;
            state.DayBytes += application.Received + application.Sent;
        }
        if (state.Open is { } current) { if (state.DayBytes > state.Peak) { state.Peak = state.DayBytes; state.Open = current with { Peak = state.Peak }; } }
        else if (!state.FiredToday && state.DayBytes >= state.Rule.Threshold)
        {
            state.FiredToday = true;
            Start(state, started, notify, batch.Minute, batch.Minute.AddMinutes(1), state.DayBytes);
        }
    }

    private void Start(State state, List<AlertEpisode> started, List<AlertEpisode> notify, DateTimeOffset start, DateTimeOffset now, double peak)
    {
        var episode = new AlertEpisode(Guid.NewGuid().ToString("N"), state.Rule.Id, state.Rule.Kind, AlertRules.Describe(state.Rule), start, null, peak);
        state.Open = episode; state.Peak = peak; state.Armed = false; started.Add(episode);
        if (state.LastNotified is null || now - state.LastNotified.Value >= AlertRules.NotificationFloor)
        { state.LastNotified = now; notify.Add(episode); }
    }

    private static UsageMetricMinute? Row(UsageMinuteBatch batch, AlertRule rule, string? gpuKey)
    {
        string? hardware = UsageMetrics.UsesHardwareId(rule.Target) ? gpuKey : null;
        if (UsageMetrics.UsesHardwareId(rule.Target) && string.IsNullOrEmpty(hardware)) return null;
        return batch.Metrics.FirstOrDefault(m => m.Metric == rule.Target && string.Equals(m.HardwareId, hardware, StringComparison.Ordinal));
    }

    private DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
    private DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
