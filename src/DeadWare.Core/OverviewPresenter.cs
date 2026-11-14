using System.Globalization;
namespace DeadWare.Core;

/// <summary>The Overview cards' texts and layout arithmetic, free of any UI framework.</summary>
public static class OverviewPresenter
{
    /// <summary>The fixed cards in order; one Disk card per present drive follows them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[] { "cpu", "gpu", "cputemp", "gputemp", "memory", "network", "storage" });
    public const double RowHeight = 270;
    /// <summary>How many polls in a row another drive must be busier before the Disk card names it instead.</summary>
    public const int BusiestHold = 5;

    public static string Title(string kind) => kind switch
    {
        "cpu" => "CPU usage", "cputemp" => "CPU temperature", "cpupump" => "CPU pump speed", "gpu" => "GPU usage", "gputemp" => "GPU temperature",
        "gpufan" => "GPU fan speed", "usage" => "CPU / GPU usage", "temperature" => "CPU / GPU temperature", "memory" => "RAM usage", "storage" => "Disk activity",
        _ => "Network activity",
    };

    /// <summary>The graph colour key a card's reading is tinted with.</summary>
    public static string ColorKey(string kind) => kind switch { "network" => "download", "storage" => "diskread", _ => kind };

    /// <summary>One, two or three columns by page width.</summary>
    public static int Columns(double width) => width < 900 ? 1 : width < 1500 ? 2 : 3;
    public static int Rows(int count, int columns) => Math.Max(1, (count + Math.Max(1, columns) - 1) / Math.Max(1, columns));
    /// <summary>The card grid's height: its rows at the minimum row height, grown into spare viewport height.</summary>
    public static double GridHeight(int count, int columns, double viewportHeight, double footerHeight)
    {
        double minimum = Rows(count, columns) * RowHeight, spare = Math.Floor(viewportHeight - footerHeight);
        return double.IsFinite(spare) ? Math.Max(minimum, spare) : minimum;
    }

    public static string Number(double? value, string unit = "") =>
        value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString("0.0", CultureInfo.CurrentCulture) + unit : "—";

    /// <summary>The card's large reading.</summary>
    public static string Value(string kind, double? current) => kind is "network" or "storage" ? HistoryChartModel.Bytes(current, true)
        : Number(current, kind.Contains("temp", StringComparison.Ordinal) ? " °C" : " %");

    /// <summary>One of MIN / AVG / MAX.</summary>
    public static string Statistic(string kind, double? value) => kind is "network" or "storage" ? HistoryChartModel.Bytes(value, true)
        : Number(value, kind.Contains("temp", StringComparison.Ordinal) ? "°C" : "%");

    /// <summary>A fan or pump line: its speed, or why there is none, then how its trace is drawn.</summary>
    public static string Rpm(string label, bool stale, double? rpm, string pattern) =>
        $"{label} · {(stale ? "stale" : rpm.HasValue && double.IsFinite(rpm.Value) ? rpm.Value.ToString("0", CultureInfo.CurrentCulture) + " RPM" : "unavailable")} · {pattern} · left RPM";

    /// <summary>The line pattern in words: the saved one, else the trace's default.</summary>
    public static string Pattern(string? saved, bool dashed = false, bool dotted = false) =>
        saved is { Length: > 0 } && saved != "default" ? saved : dotted ? "dotted" : dashed ? "dashed" : "solid";

    public static string NetworkSubtitle(string? downloadPattern, string? uploadPattern)
    {
        static string Cased(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text);
        return $"{Cased(Pattern(downloadPattern))}: download    ·    {Cased(Pattern(uploadPattern, dashed: true))}: upload    ·    Shared scale";
    }

    public static string MemorySubtitle(bool discovered, bool stale, bool hasValue, double? used, double? total) =>
        !discovered ? "Discovering memory…" : stale ? "RAM readings stale" : !hasValue ? "RAM usage unavailable"
        : $"{HistoryChartModel.Bytes(used)} / {HistoryChartModel.Bytes(total)} in use";

    /// <summary>A CPU or GPU card's device line.</summary>
    public static string DeviceSubtitle(string kind, bool discovered, string cpuName, string gpuName, int shownCores, bool hasValue, bool stale)
    {
        if (!discovered) return "Discovering hardware…";
        string text = kind.StartsWith("cpu", StringComparison.Ordinal) ? cpuName : gpuName;
        if (kind == "cpu" && shownCores > 0) text += $" · {shownCores} cores";
        if (!hasValue) text += " · " + (stale ? "stale" : "sensor unavailable");
        return text;
    }

    /// <summary>The all-drives Disk card's line: the busiest drive and how many there are.</summary>
    public static string StorageSubtitle(bool discovered, int drives, string? busiestModel) =>
        !discovered ? "Discovering drives…" : drives == 0 || busiestModel is null ? "No drives detected" : $"{busiestModel} · {drives} drive{(drives == 1 ? "" : "s")}";

    /// <summary>A drive card's line; the model is its title.</summary>
    public static string DriveSubtitle(StorageDrive? drive, double? writeRate) => drive is null ? "Drive not detected"
        : $"{drive.BusType} · {HistoryChartModel.Bytes(drive.SizeBytes)}{(drive.Removable ? " · removable" : "")} · ↑ {HistoryChartModel.Bytes(writeRate, true)} write";

    public static string Footer(double? used, double? total, DateTimeOffset sample) =>
        $"Memory  {HistoryChartModel.Bytes(used)} / {HistoryChartModel.Bytes(total)}    ·    Last sample {sample.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}";

    /// <summary>The page's status lines, most urgent first.</summary>
    public static string Status(bool discovered, bool stale, string hardware, string network, string history, string settings, bool coresWanted, int cores)
    {
        if (!discovered) return "Discovering hardware and starting the shared collector…\n" + history;
        string text = (stale ? "STALE — the collector has not published a recent sample. " : "") + hardware + "\n" + network + "\n" + history + (settings.Length > 0 ? "\n" + settings : "");
        return coresWanted && cores == 0 ? text + " · Per-core CPU usage unavailable." : text;
    }

    /// <summary>Which present drives keep their card, in disk order, when the set of ids changes: each kept card is handed out once, so a repeated id gets its own.</summary>
    public static IReadOnlyList<int?> Reuse(IReadOnlyList<string> previous, IReadOnlyList<string> next)
    {
        var free = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int i = 0; i < previous.Count; i++)
        {
            if (!free.TryGetValue(previous[i], out var queue)) free[previous[i]] = queue = new();
            queue.Enqueue(i);
        }
        var result = new int?[next.Count];
        for (int i = 0; i < next.Count; i++)
            result[i] = free.TryGetValue(next[i], out var kept) && kept.Count > 0 ? kept.Dequeue() : null;
        return result;
    }
}

/// <summary>The drive the Disk card names: the current one while present, until a different drive has been strictly busier for
/// <see cref="OverviewPresenter.BusiestHold"/> polls in a row, so bursty I/O does not flip the name every second.</summary>
public sealed class BusiestDrive
{
    private string? id;
    private int streak;

    public StorageDrive Pick(IReadOnlyList<StorageDrive> drives, Func<StorageDrive, double> rate)
    {
        var leader = drives.OrderByDescending(rate).ThenBy(drive => drive.Index).First();
        var current = id is null ? null : drives.FirstOrDefault(drive => drive.Id == id);
        if (current is null || leader.Id == current.Id) { id = leader.Id; streak = 0; return current ?? leader; }
        if (rate(leader) <= rate(current)) { streak = 0; return current; }
        if (++streak < OverviewPresenter.BusiestHold) return current;
        id = leader.Id; streak = 0;
        return leader;
    }
}
