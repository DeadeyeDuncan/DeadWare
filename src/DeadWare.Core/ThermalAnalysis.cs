namespace DeadWare.Core;

public sealed class ThermalAggregator
{
    private const double WindowSeconds = 60;
    private const double WarmupSeconds = 300;
    private const double MaximumGapSeconds = 4;
    private const int MaximumActiveDevices = 32;
    private readonly Dictionary<(string Id, string Configuration, string Map), DeviceState> states = new();
    private long sequence;

    /// <summary>Called by one background owner. No I/O, timers, hardware access or retained sample lists.</summary>
    public IReadOnlyList<ThermalWindow> Add(ThermalSample sample)
    {
        if (!double.IsFinite(sample.MonotonicSeconds) || sample.MonotonicSeconds < 0) return [];
        sample = Normalize(sample);
        var key = (sample.Device.Id, sample.Device.ConfigurationId, sample.Device.SensorMap);
        if (!states.TryGetValue(key, out var state))
        {
            ThermalWindow? retired = null;
            if (states.Count >= MaximumActiveDevices)
            {
                var oldest = states.MinBy(p => p.Value.LastSeen);
                retired = oldest.Value.Window.Finish(oldest.Value.StableSince, "Partial window: device tracking limit reached");
                states.Remove(oldest.Key);
            }
            states[key] = new DeviceState(sample, ++sequence);
            return retired is null ? [] : [retired];
        }

        var result = new List<ThermalWindow>(1);
        double elapsed = sample.MonotonicSeconds - state.Last.MonotonicSeconds;
        if (sample.SessionId == state.Last.SessionId && elapsed <= 0) return [];
        double wallElapsed = (sample.Timestamp - state.Last.Timestamp).TotalSeconds;
        bool discontinuity = sample.SessionId != state.Last.SessionId || elapsed > MaximumGapSeconds ||
            Math.Abs(wallElapsed - elapsed) > 3;
        if (discontinuity)
        {
            result.Add(state.Window.Finish(state.StableSince, "Gap, clock change or session restart"));
            states[key] = new DeviceState(sample, ++sequence);
            return result;
        }

        bool changed = Changed(state.Last.Power, sample.Power, PowerTolerance) ||
            Changed(state.Last.Load, sample.Load, _ => 15);
        if (sample.MonotonicSeconds - state.Window.First.MonotonicSeconds >= WindowSeconds)
        {
            result.Add(state.Window.Finish(state.StableSince));
            state.Window = new WindowBuilder(sample);
        }
        else state.Window.Add(sample);

        if (changed || !HasRequired(sample) || sample.Throttled || !state.Window.IsStable)
            state.StableSince = sample.MonotonicSeconds;
        state.Last = sample;
        state.LastSeen = ++sequence;
        return result;
    }

    /// <summary>End-of-run data is retained for viewing but cannot train a stable reference.</summary>
    public IReadOnlyList<ThermalWindow> Flush()
    {
        var result = states.Values.Select(s => s.Window.Finish(s.StableSince, "Partial window at recording stop")).ToArray();
        states.Clear();
        return result;
    }

    public void Reset() { states.Clear(); sequence = 0; }

    private static double PowerTolerance(double mean) => Math.Max(8, Math.Abs(mean) * .15);
    private static bool Changed(double? before, double? after, Func<double, double> tolerance) =>
        before.HasValue && after.HasValue && Math.Abs(after.Value - before.Value) > tolerance(before.Value);
    private static bool HasRequired(ThermalSample s) => s.Device.ComparisonAllowed && s.Temperature.HasValue && s.Load.HasValue && s.Power is > 5;
    private static double? FiniteRange(double? value, double minimum, double maximum = double.MaxValue) =>
        value.HasValue && double.IsFinite(value.Value) && value >= minimum && value <= maximum ? value : null;
    private static ThermalSample Normalize(ThermalSample sample) => sample with
    {
        Timestamp = sample.Timestamp.ToUniversalTime(),
        Temperature = FiniteRange(sample.Temperature, -100, 200),
        Load = FiniteRange(sample.Load, 0, 100),
        Power = FiniteRange(sample.Power, 0),
        Clock = FiniteRange(sample.Clock, 0),
        FanRpm = FiniteRange(sample.FanRpm, 0)
    };

    private sealed class DeviceState
    {
        public ThermalSample Last;
        public WindowBuilder Window;
        public double StableSince;
        public long LastSeen;
        public DeviceState(ThermalSample sample, long lastSeen)
        {
            Last = sample;
            Window = new(sample);
            StableSince = sample.MonotonicSeconds;
            LastSeen = lastSeen;
        }
    }

    private sealed class MetricBuilder
    {
        private int count;
        private double minimum, maximum, average;
        public void Add(double? value)
        {
            if (!value.HasValue) return;
            if (count == 0) minimum = maximum = average = value.Value;
            else
            {
                minimum = Math.Min(minimum, value.Value);
                maximum = Math.Max(maximum, value.Value);
                average += (value.Value - average) / (count + 1);
            }
            count++;
        }
        public MetricAggregate Finish() => count == 0 ? new(0, null, null, null) : new(count, minimum, maximum, average);
    }

    private sealed class WindowBuilder
    {
        public ThermalSample First { get; }
        private ThermalSample last;
        private int count;
        private bool throttled;
        private string? unavailableReason;
        private readonly MetricBuilder temperature = new(), load = new(), power = new(), clock = new(), fan = new();
        public WindowBuilder(ThermalSample first) { First = last = first; Add(first); }
        public void Add(ThermalSample sample)
        {
            last = sample;
            count++;
            temperature.Add(sample.Temperature); load.Add(sample.Load); power.Add(sample.Power);
            clock.Add(sample.Clock); fan.Add(sample.FanRpm);
            throttled |= sample.Throttled;
            if (!HasRequired(sample)) unavailableReason ??= sample.UnavailableReason;
        }

        public bool IsStable
        {
            get
            {
                var p = power.Finish(); var l = load.Finish();
                return p.Average is > 5 && p.Maximum - p.Minimum <= PowerTolerance(p.Average.Value) &&
                    l.Maximum - l.Minimum <= 15;
            }
        }

        public ThermalWindow Finish(double stableSince, string? incomplete = null)
        {
            var t = temperature.Finish(); var l = load.Finish(); var p = power.Finish();
            double duration = Math.Max(0, last.MonotonicSeconds - First.MonotonicSeconds);
            string reason = incomplete ??
                (!First.Device.ComparisonAllowed ? First.Device.IdentityNote :
                t.Count != count || l.Count != count || p.Count != count ? "Missing required readings" +
                    (string.IsNullOrWhiteSpace(unavailableReason) ? "" : ": " + unavailableReason) :
                throttled ? "Reported throttling" :
                p.Average is null or <= 5 ? "Insufficient package power" :
                count < 30 || duration < 50 ? "Insufficient sample coverage" :
                !IsStable ? "Changing load or package power" :
                First.MonotonicSeconds - stableSince < WarmupSeconds ? "Learning: waiting for five minutes of steady operation" :
                "Stable");
            return new(First.Device, First.SessionId, First.Timestamp, last.Timestamp, First.MonotonicSeconds,
                duration, count, t, l, p, clock.Finish(), fan.Finish(), reason == "Stable", reason);
        }
    }
}

public static class CoolingAnalyzer
{
    public const int RequiredRecentWindows = 10;
    public const int MinimumReferenceWindows = 30;
    public const int MinimumMatchesPerWindow = 10;

    /// <summary>Conservative evidence comparison; no absolute temperature is a health certification.</summary>
    public static CoolingAssessment Assess(IReadOnlyList<ThermalWindow> recent, ThermalReference? reference, DateTimeOffset now)
    {
        if(recent.LastOrDefault()?.Device is {ComparisonAllowed:false} uncertain)
            return Unavailable("Identity not verified",uncertain.IdentityNote);
        if (reference is null)
            return Unavailable("Learning", "Record a reference period before comparing cooling behavior.");
        if (recent.Count == 0)
            return Unavailable("Learning", "Waiting for recent temperature, package power and load readings.");

        var latest = recent.OrderBy(w => w.End).ThenBy(w => w.StartMonotonic).Last();
        if (latest.Device.Id != reference.DeviceId || latest.Device.ConfigurationId != reference.ConfigurationId)
            return Unavailable("Reference needed", "This device or cooling setup needs its own reference.");
        if (reference.Windows.Any(w => w.Device.Id != reference.DeviceId || w.Device.ConfigurationId != reference.ConfigurationId ||
            w.Device.SensorMap != latest.Device.SensorMap))
            return Unavailable("Reference needed", "The reference sensor mapping differs from the current device.");

        var baseline = reference.Windows.Where(IsComparable)
            .DistinctBy(w => (w.SessionId, w.StartMonotonic)).ToArray();
        if (baseline.Length < MinimumReferenceWindows)
            return Unavailable("Learning", $"The reference needs at least {MinimumReferenceWindows} eligible minute windows; {baseline.Length} are available.");

        double age = (now - latest.End).TotalSeconds;
        if (age < 0 || age > 120)
            return Unavailable("Stale", "No recent complete minute window is available; comparison is paused.");

        var windows = recent.OrderBy(w => w.End).ThenBy(w => w.StartMonotonic).TakeLast(RequiredRecentWindows).ToArray();
        if (windows.Length < RequiredRecentWindows || windows.Any(w => !IsComparable(w) || !SameDevice(w.Device, latest.Device)))
            return Unavailable("Not enough comparable data", "Waiting for ten consecutive stable minutes with temperature, package power and load readings.");
        for (int i = 1; i < windows.Length; i++)
        {
            var previous = windows[i - 1]; var current = windows[i];
            double wallGap = (current.Start - previous.End).TotalSeconds;
            double monotonicGap = current.StartMonotonic - (previous.StartMonotonic + previous.DurationSeconds);
            if (previous.SessionId != current.SessionId || monotonicGap <= 0 || monotonicGap > 4 ||
                wallGap <= 0 || wallGap > 4 || Math.Abs(wallGap - monotonicGap) > 3)
                return Unavailable("Not enough comparable data", "A recording gap, restart or clock change interrupted the ten-minute comparison.");
        }

        var evidence = new List<(double Current, double Expected, double Low, double High, bool Elevated, bool MissingFan)>();
        foreach (var window in windows)
        {
            double currentPower = window.Power.Average!.Value;
            double currentLoad = window.Load.Average!.Value;
            var matches = baseline.Where(w => Math.Abs(w.Power.Average!.Value - currentPower) <= Math.Max(5, currentPower * .1) &&
                Math.Abs(w.Load.Average!.Value - currentLoad) <= 10 && FanMatches(window, w)).ToArray();
            if (matches.Length < MinimumMatchesPerWindow) continue;
            var temperatures = matches.Select(w => w.Temperature.Average!.Value).Order().ToArray();
            double expected = temperatures.Average();
            double upper = Quantile(temperatures, .95);
            double current = window.Temperature.Average!.Value;
            evidence.Add((current, expected, Quantile(temperatures, .05), upper, current - expected >= 5 && current - upper >= 3,
                FanValue(window) is null || matches.Any(w => FanValue(w) is null)));
        }
        if (evidence.Count < RequiredRecentWindows)
            return Unavailable("Not enough comparable data", $"Only {evidence.Count} of ten recent minutes have enough reference data at comparable power, load and available fan speed.", evidence.Count);

        double currentMean = evidence.Average(e => e.Current);
        double expectedMean = evidence.Average(e => e.Expected);
        double delta = currentMean - expectedMean;
        bool warning = evidence.All(e => e.Elevated);
        string message = warning
            ? $"Temperature has remained {delta:0.0}°C above the reference at comparable power and load for ten minutes. Check cooling conditions; the cause is not identified."
            : $"Ten recent minutes matched the reference at comparable power and load; average temperature difference {delta:+0.0;-0.0;0.0}°C. This is a comparison, not a hardware health verdict.";
        message += evidence.Any(e => e.MissingFan) ? " Fan context is incomplete; coolant and ambient temperatures are not measured." :
            " Available fan speed was matched; coolant and ambient temperatures are not measured.";
        return new(warning ? "Cooling change" : "Compared", message, evidence.Count, RequiredRecentWindows,
            currentMean, expectedMean, delta, evidence.Average(e => e.Low), evidence.Average(e => e.High), warning);
    }

    private static CoolingAssessment Unavailable(string state, string message, int matched = 0) =>
        new(state, message, matched, RequiredRecentWindows, null, null, null, null, null, false);
    private static bool SameDevice(ThermalDevice a, ThermalDevice b) =>
        a.Id == b.Id && a.ConfigurationId == b.ConfigurationId && a.SensorMap == b.SensorMap;

    private static bool IsComparable(ThermalWindow window) =>
        window.Device.ComparisonAllowed && window.Eligible && window.ResolutionSeconds == 60 && window.SampleCount >= 30 &&
        double.IsFinite(window.StartMonotonic) && double.IsFinite(window.DurationSeconds) &&
        window.DurationSeconds is >= 50 and <= 63 &&
        Math.Abs((window.End - window.Start).TotalSeconds - window.DurationSeconds) <= 3 &&
        ValidMetric(window.Temperature, window.SampleCount) && ValidMetric(window.Power, window.SampleCount) &&
        ValidMetric(window.Load, window.SampleCount) && window.Power.Average > 5 &&
        window.Load.Minimum >= 0 && window.Load.Maximum <= 100 &&
        window.Temperature.Minimum >= -100 && window.Temperature.Maximum <= 200 && window.Power.Minimum >= 0 &&
        window.Power.Maximum - window.Power.Minimum <= Math.Max(8, window.Power.Average!.Value * .15) &&
        window.Load.Maximum - window.Load.Minimum <= 15;

    private static bool ValidMetric(MetricAggregate metric, int count) =>
        metric.Count == count && metric.Minimum.HasValue && metric.Maximum.HasValue && metric.Average.HasValue &&
        double.IsFinite(metric.Minimum.Value) && double.IsFinite(metric.Maximum.Value) && double.IsFinite(metric.Average.Value) &&
        metric.Minimum <= metric.Average && metric.Average <= metric.Maximum;

    private static double? FanValue(ThermalWindow window) =>
        ValidMetric(window.FanRpm, window.SampleCount) && window.FanRpm.Minimum >= 0 ? window.FanRpm.Average : null;

    private static bool FanMatches(ThermalWindow current, ThermalWindow reference)
    {
        var currentFan = FanValue(current); var referenceFan = FanValue(reference);
        if (currentFan is null || referenceFan is null) return true;
        // Fan-stop is a different operating mode, even when the numeric difference is small.
        if (currentFan == 0 || referenceFan == 0) return currentFan == referenceFan;
        return Math.Abs(currentFan.Value - referenceFan.Value) <= Math.Max(250, currentFan.Value * .2);
    }

    private static double Quantile(double[] sorted, double quantile)
    {
        double position = (sorted.Length - 1) * quantile;
        int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

public static class CoolingAlertPolicy
{
    /// <summary>Use persisted warnings so restarting the app cannot reset acknowledgment or cooldown.</summary>
    public static bool ShouldNotify(CoolingAssessment assessment, ThermalReference reference,
        IReadOnlyList<CoolingWarning> warnings, DateTimeOffset now)
    {
        if (!assessment.ShouldWarn) return false;
        return !warnings.Any(w => w.ReferenceId == reference.Id &&
            (!w.Acknowledged || now - w.Created < TimeSpan.FromHours(24)));
    }
}
