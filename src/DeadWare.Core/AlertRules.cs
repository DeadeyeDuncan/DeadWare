using System.Globalization;

namespace DeadWare.Core;

public enum AlertKind { Metric, NetworkRate, ApplicationDaily, Battery }

/// <summary>A user-set threshold. Target is a usage metric id, "download"/"upload", an executable name, or empty for battery.</summary>
public sealed record AlertRule(string Id, AlertKind Kind, string Target, double Threshold, int Minutes, bool Enabled, DateTimeOffset Created);

/// <summary>One crossing of a rule's threshold. End stays null while the episode is open.</summary>
public sealed record AlertEpisode(string Id, string RuleId, AlertKind Kind, string Summary, DateTimeOffset Start, DateTimeOffset? End,
    double Peak, bool Acknowledged = false);

public static class AlertRules
{
    public const int MaximumRules = 64;
    public static IReadOnlyList<int> Durations { get; } = Array.AsReadOnly(new[] { 1, 5, 15, 60 });
    public static IReadOnlyList<string> MetricTargets { get; } = Array.AsReadOnly(new[]
        { UsageMetrics.Cpu, UsageMetrics.CpuTemperature, UsageMetrics.Gpu, UsageMetrics.GpuTemperature, UsageMetrics.Memory });
    public static IReadOnlyList<string> NetworkTargets { get; } = Array.AsReadOnly(new[] { UsageMetrics.Download, UsageMetrics.Upload });
    public static TimeSpan NotificationFloor { get; } = TimeSpan.FromHours(1);

    public static bool IsTemperature(string target) => target is UsageMetrics.CpuTemperature or UsageMetrics.GpuTemperature;
    public static bool IsPercentage(AlertKind kind, string target)
        => kind == AlertKind.Battery || kind == AlertKind.Metric && !IsTemperature(target);

    /// <summary>Returns the rule with every field in range, or null when it cannot be repaired.</summary>
    public static AlertRule? Normalize(AlertRule? rule)
    {
        if (rule is null || !Enum.IsDefined(rule.Kind) || !double.IsFinite(rule.Threshold)) return null;
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || rule.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return null;
        string target; double threshold; int minutes = Durations.Contains(rule.Minutes) ? rule.Minutes : 5;
        switch (rule.Kind)
        {
            case AlertKind.Metric:
                target = rule.Target?.Trim().ToLowerInvariant() ?? "";
                if (!MetricTargets.Contains(target, StringComparer.Ordinal)) return null;
                threshold = IsTemperature(target) ? Math.Clamp(Math.Round(rule.Threshold), 1, 150) : Math.Clamp(Math.Round(rule.Threshold), 1, 100);
                break;
            case AlertKind.NetworkRate:
                target = rule.Target?.Trim().ToLowerInvariant() ?? "";
                if (!NetworkTargets.Contains(target, StringComparer.Ordinal)) return null;
                threshold = Math.Clamp(Math.Round(rule.Threshold), 1, 1e12);
                break;
            case AlertKind.ApplicationDaily:
                target = UsageMetrics.NormalizeName(rule.Target);
                if (target == UsageMetrics.UnknownName) return null;
                threshold = Math.Clamp(Math.Round(rule.Threshold), 1, 1e15); minutes = 1;
                break;
            default:
                target = ""; threshold = Math.Clamp(Math.Round(rule.Threshold), 1, 99); minutes = 1;
                break;
        }
        var created = rule.Created == default ? DateTimeOffset.UnixEpoch : rule.Created;
        return new(rule.Id, rule.Kind, target, threshold, minutes, rule.Enabled, created);
    }

    /// <summary>Drops invalid rules and later duplicates of an id, keeping at most <see cref="MaximumRules"/>.</summary>
    public static List<AlertRule> Normalize(IEnumerable<AlertRule?>? rules)
    {
        var result = new List<AlertRule>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in rules ?? [])
        {
            if (Normalize(candidate) is not { } rule || !seen.Add(rule.Id)) continue;
            result.Add(rule);
            if (result.Count == MaximumRules) break;
        }
        return result;
    }

    /// <summary>The value a metric must fall below before the rule re-arms: 3°C, 5 percentage points or 10% of a byte figure.</summary>
    public static double Hysteresis(AlertRule rule) => rule.Kind switch
    {
        AlertKind.Metric when IsTemperature(rule.Target) => 3,
        AlertKind.Metric or AlertKind.Battery => 5,
        _ => rule.Threshold * 0.1,
    };

    public static string TargetName(AlertKind kind, string target) => kind switch
    {
        AlertKind.Metric => target switch
        {
            UsageMetrics.Cpu => "CPU usage", UsageMetrics.CpuTemperature => "CPU temperature", UsageMetrics.Gpu => "GPU usage",
            UsageMetrics.GpuTemperature => "GPU temperature", _ => "RAM usage",
        },
        AlertKind.NetworkRate => target == UsageMetrics.Upload ? "Upload" : "Download",
        AlertKind.ApplicationDaily => target,
        _ => "Battery",
    };

    public static string FormatThreshold(AlertRule rule) => rule.Kind switch
    {
        AlertKind.Metric when IsTemperature(rule.Target) => $"{rule.Threshold:0}°C",
        AlertKind.Metric or AlertKind.Battery => $"{rule.Threshold:0}%",
        AlertKind.NetworkRate => FormatBytes(rule.Threshold) + "/s",
        _ => FormatBytes(rule.Threshold),
    };

    /// <summary>A recorded value in the rule's unit: "97%", "88°C", "5.2 MB/s" or "2.3 GB".</summary>
    public static string FormatValue(AlertRule rule, double value) => rule.Kind switch
    {
        AlertKind.Metric when IsTemperature(rule.Target) => $"{value:0}°C",
        AlertKind.Metric or AlertKind.Battery => $"{value:0}%",
        AlertKind.NetworkRate => FormatBytes(value) + "/s",
        _ => FormatBytes(value),
    };

    /// <summary>"CPU temperature ≥ 85°C for 15 minutes", "chrome.exe ≥ 2.0 GB in a day", "Battery ≤ 20% on battery".</summary>
    public static string Describe(AlertRule rule) => rule.Kind switch
    {
        AlertKind.ApplicationDaily => $"{rule.Target} ≥ {FormatThreshold(rule)} in a day",
        AlertKind.Battery => $"Battery ≤ {FormatThreshold(rule)} on battery",
        _ => $"{TargetName(rule.Kind, rule.Target)} ≥ {FormatThreshold(rule)} for {rule.Minutes} minute{(rule.Minutes == 1 ? "" : "s")}",
    };

    public static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (bytes >= 1000 && i < units.Length - 1) { bytes /= 1000; i++; }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", bytes, units[i]);
    }
}
