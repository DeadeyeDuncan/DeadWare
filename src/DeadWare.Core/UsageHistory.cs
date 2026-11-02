namespace DeadWare.Core;

/// <summary>One recorded minute of a device metric. Values summarize the finite samples observed in that minute.</summary>
public sealed record UsageMetricMinute(string Metric, string? HardwareId, DateTimeOffset Minute, double Average, double Minimum, double Maximum,
    int Samples, bool Complete, string Segment);

/// <summary>Bytes attributed to one application name during one minute, summed across its processes.</summary>
public sealed record UsageApplicationMinute(string Name, DateTimeOffset Minute, long Received, long Sent, string Segment);

/// <summary>Everything the folder closed for one minute. CaptureGap means per-application traffic is not trustworthy for it.</summary>
public sealed record UsageMinuteBatch(DateTimeOffset Minute, IReadOnlyList<UsageMetricMinute> Metrics,
    IReadOnlyList<UsageApplicationMinute> Applications, bool CaptureGap, string Segment);

public static class UsageMetrics
{
    public const string Cpu = "cpu", CpuTemperature = "cputemp", Gpu = "gpu", GpuTemperature = "gputemp",
        Memory = "memory", Download = "download", Upload = "upload";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Cpu, CpuTemperature, Gpu, GpuTemperature, Memory, Download, Upload });
    public const int MaximumNameLength = 256;
    public const string UnknownName = "Unknown application";

    public static bool IsKnown(string? metric) => metric is not null && All.Contains(metric, StringComparer.Ordinal);
    public static bool UsesHardwareId(string metric) => metric is Gpu or GpuTemperature;

    /// <summary>Start of the UTC minute containing the timestamp.</summary>
    public static DateTimeOffset Floor(DateTimeOffset timestamp)
        => new(timestamp.UtcTicks - timestamp.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>Publications a full minute promises at a polling interval.</summary>
    public static int ExpectedSamples(double pollingIntervalSeconds) => (int)Math.Ceiling(60 / PollingPolicy.Normalize(pollingIntervalSeconds));

    /// <summary>Executable name as the Network page shows it: trimmed, bounded and never empty.</summary>
    public static string NormalizeName(string? name)
    {
        string text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0) text = UnknownName;
        return text.Length > MaximumNameLength ? text[..MaximumNameLength] : text;
    }
}

/// <summary>Folds one-second (or slower) publications into whole UTC minutes. A pure policy object: no timers, threads or storage. Only the usage history worker owns an instance.</summary>
public sealed class UsageMinuteFolder
{
    private sealed class Accumulator
    {
        public int Count; public double Sum, Minimum = double.PositiveInfinity, Maximum = double.NegativeInfinity;
        public void Add(double value) { Count++; Sum += value; if (value < Minimum) Minimum = value; if (value > Maximum) Maximum = value; }
    }

    private readonly Dictionary<(string Metric, string? Hardware), Accumulator> metrics = new();
    private readonly Dictionary<string, (long Received, long Sent)> applications = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<(int Pid, long Generation), (long Received, long Sent)> baselines = new();
    private DateTimeOffset? minute;
    private double? previousSeconds, interval;
    private DateTimeOffset? previousTimestamp;
    private double previousInterval = 1;
    private bool broken, intervalChanged, captureGap, hasBaseline;
    private string segment = NewSegment(), minuteSegment = "";

    private static string NewSegment() => Guid.NewGuid().ToString("N");

    /// <summary>Marks the open minute as discontinuous and starts a new segment for later samples.</summary>
    public void Break()
    {
        broken = true; segment = NewSegment();
        previousSeconds = null; previousTimestamp = null;
        baselines = new(); hasBaseline = false;
    }

    /// <summary>Folds one publication with a single GPU whose value prefix and stored key are the same identifier.</summary>
    public UsageMinuteBatch? Observe(Snapshot sample, IReadOnlyList<ProcessTraffic> processes, bool captureAvailable, string? gpuId)
        => Observe(sample, processes, captureAvailable, string.IsNullOrEmpty(gpuId) ? Array.Empty<(string, string)>() : new[] { (gpuId, gpuId) });

    /// <summary>Folds one publication. Returns the previous minute when this sample starts a later one; otherwise null.
    /// Each GPU's Id prefixes its sample values and its Key is stored as the row's hardware identity.</summary>
    public UsageMinuteBatch? Observe(Snapshot sample, IReadOnlyList<ProcessTraffic> processes, bool captureAvailable, IReadOnlyList<(string Id, string Key)> gpus)
    {
        ArgumentNullException.ThrowIfNull(sample);
        processes ??= []; gpus ??= [];
        if (!double.IsFinite(sample.MonotonicSeconds)) { Break(); return null; }
        if (previousSeconds is { } last && sample.MonotonicSeconds <= last) return null;
        double sampleInterval = PollingPolicy.Normalize(sample.PollingIntervalSeconds);
        if (previousSeconds is { } before && previousTimestamp is { } beforeStamp &&
            (sample.MonotonicSeconds - before > Math.Max(5, Math.Max(previousInterval, sampleInterval) + 3) ||
             Math.Abs((sample.Timestamp - beforeStamp).TotalSeconds - (sample.MonotonicSeconds - before)) > 3)) Break();

        var current = UsageMetrics.Floor(sample.Timestamp);
        UsageMinuteBatch? closed = null;
        if (minute is { } open && current != open)
        {
            if (current < open) Break();
            closed = Close();
        }
        if (minute is null) { minute = current; minuteSegment = segment; }

        if (interval is { } known && known != sampleInterval) intervalChanged = true;
        interval = sampleInterval;

        Fold(UsageMetrics.Cpu, null, sample, "cpu/usage");
        Fold(UsageMetrics.CpuTemperature, null, sample, "cpu/temperature");
        Fold(UsageMetrics.Memory, null, sample, "memory/usage");
        Fold(UsageMetrics.Download, null, sample, "network/download");
        Fold(UsageMetrics.Upload, null, sample, "network/upload");
        foreach (var gpu in gpus)
        {
            if (string.IsNullOrEmpty(gpu.Id) || string.IsNullOrEmpty(gpu.Key)) continue;
            Fold(UsageMetrics.Gpu, gpu.Key, sample, gpu.Id + "/usage");
            Fold(UsageMetrics.GpuTemperature, gpu.Key, sample, gpu.Id + "/temperature");
        }

        if (!captureAvailable) captureGap = true;
        FoldApplications(processes);

        previousSeconds = sample.MonotonicSeconds; previousTimestamp = sample.Timestamp; previousInterval = sampleInterval;
        return closed;
    }

    /// <summary>Closes the open minute early, for Quit or a recording pause. Its rows keep their own completeness and capture flags;
    /// call <see cref="Break"/> afterwards when the samples that follow should start a discontinuous minute.</summary>
    public UsageMinuteBatch? Flush()
    {
        if (minute is null) return null;
        var batch = Close();
        segment = NewSegment(); previousSeconds = null; previousTimestamp = null; baselines = new(); hasBaseline = false;
        return batch;
    }

    private void Fold(string metric, string? hardware, Snapshot sample, string key)
    {
        if (!sample.Values.TryGetValue(key, out var raw)) return;
        double? value = SampleHistory.Finite(raw);
        if (value is null) return;
        var id = (metric, hardware);
        if (!metrics.TryGetValue(id, out var accumulator)) metrics[id] = accumulator = new Accumulator();
        accumulator.Add(value.Value);
    }

    private void FoldApplications(IReadOnlyList<ProcessTraffic> processes)
    {
        var next = new Dictionary<(int Pid, long Generation), (long Received, long Sent)>(processes.Count);
        foreach (var process in processes)
        {
            if (process is null) continue;
            long received = Math.Max(0, process.ReceivedBytes), sent = Math.Max(0, process.SentBytes);
            var key = (process.Pid, process.Generation);
            if (next.ContainsKey(key)) continue;
            next[key] = (received, sent);
            long deltaReceived, deltaSent;
            if (baselines.TryGetValue(key, out var baseline))
            {
                // A counter that moved backwards was reset by the ledger; count only what it now holds.
                deltaReceived = received >= baseline.Received ? received - baseline.Received : received;
                deltaSent = sent >= baseline.Sent ? sent - baseline.Sent : sent;
            }
            else if (hasBaseline) { deltaReceived = received; deltaSent = sent; }
            else continue; // The first publication only establishes baselines; its bytes predate this folder.
            if (deltaReceived == 0 && deltaSent == 0) continue;
            string name = UsageMetrics.NormalizeName(process.Name);
            var total = applications.TryGetValue(name, out var existing) ? existing : (0L, 0L);
            applications[name] = (total.Item1 + deltaReceived, total.Item2 + deltaSent);
        }
        baselines = next; hasBaseline = true;
    }

    private UsageMinuteBatch Close()
    {
        var at = minute!.Value;
        int expected = UsageMetrics.ExpectedSamples(interval ?? 1);
        bool continuous = !broken && !intervalChanged;
        var rows = metrics.Where(pair => pair.Value.Count > 0).Select(pair => new UsageMetricMinute(pair.Key.Metric, pair.Key.Hardware, at,
                pair.Value.Sum / pair.Value.Count, pair.Value.Minimum, pair.Value.Maximum, pair.Value.Count,
                continuous && pair.Value.Count >= expected - 1, minuteSegment)).ToArray();
        var apps = applications.Where(pair => pair.Value.Received > 0 || pair.Value.Sent > 0)
            .Select(pair => new UsageApplicationMinute(pair.Key, at, pair.Value.Received, pair.Value.Sent, minuteSegment)).ToArray();
        var batch = new UsageMinuteBatch(at, rows, apps, captureGap || broken, minuteSegment);
        metrics.Clear(); applications.Clear();
        minute = null; interval = null; broken = false; intervalChanged = false; captureGap = false;
        return batch;
    }
}
