using System.Globalization;

namespace DeadWare.Core;

public enum FirstUseSource { Usage, Connections }

/// <summary>One application's attributed, non-loopback traffic in a poll.</summary>
public sealed record NetworkUse(string Name, DateTimeOffset FirstSeen);

/// <summary>A name read from a history store for the seed, with the first and last minute it was recorded.</summary>
public sealed record ApplicationSeen(string Name, DateTimeOffset First, DateTimeOffset Last);

public sealed record KnownApplication(string Key, DateTimeOffset First, DateTimeOffset Last);

public sealed record FirstUseMetadata(DateTimeOffset? SeededUsage, DateTimeOffset? SeededConnections, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd);

public sealed record FirstUseState(DateTimeOffset? SeededUsage, DateTimeOffset? SeededConnections, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd,
    IReadOnlyList<KnownApplication> Known);

public sealed record FirstUseChanges(IReadOnlyList<KnownApplication> Upserts, IReadOnlyList<string> Deletes, FirstUseMetadata Metadata);

public sealed record FirstUseGroup(DateTimeOffset First, IReadOnlyList<string> Names, int Count);

/// <summary>KnownApps is null until Restore.</summary>
public sealed record FirstUseStatus(bool Restored, bool Seeded, DateTimeOffset? LearningStart, DateTimeOffset? LearningEnd, int? KnownApps);

public static class FirstNetworkUse
{
    public const string RuleId = "first-network-use";
    public const int MaximumKnown = 8192, MaximumIgnored = 256, MaximumUses = 1024, MaximumGroupNames = 64, MaximumSeedNames = 65536, MaximumChanges = 1024, ForgetDays = 400;
    public static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(60);

    /// <summary>False for "Unknown application" and "PID n" names, which are never learned or alerted.</summary>
    public static bool IsLearnable(string? name)
    {
        string key = AppKey.Of(name);
        return key != AppKey.Of(UsageMetrics.UnknownName) && !AppKey.IsPidName(name);
    }

    /// <summary>The ledger has already mapped IPv4-mapped addresses to IPv4, so this test is exact.</summary>
    public static bool IsLoopback(string remoteAddress)
        => remoteAddress is not null && (remoteAddress.StartsWith("127.", StringComparison.Ordinal) || remoteAddress == "::1");

    public static List<string> NormalizeIgnored(IEnumerable<string?>? names)
    {
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names is null) return result;
        foreach (var name in names)
        {
            if (result.Count >= MaximumIgnored) break;
            if (!IsLearnable(name) || !seen.Add(AppKey.Of(name))) continue;
            result.Add(UsageMetrics.NormalizeName(name));
        }
        return result;
    }

    public static string Summary(string name) => $"{name} used the network for the first time";

    /// <summary>The local midnight that begins the third calendar day after the start's local date, as UTC.</summary>
    public static DateTimeOffset LearningEnd(DateTimeOffset start, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime).AddDays(3);
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string BalloonText(FirstUseGroup group, TimeZoneInfo zone, CultureInfo culture)
    {
        string time = TimeZoneInfo.ConvertTime(group.First, zone).ToString("t", culture);
        var names = group.Names.Take(3).Select(Shorten).ToArray();
        string Name(int i) => i < names.Length ? names[i] : "";
        switch (group.Count)
        {
            case <= 1:
                return $"{Name(0)} used the network for the first time at {time}. Open Alerts to review.";
            case 2:
                return $"2 apps used the network for the first time from {time}: {Name(0)} and {Name(1)}. Open Alerts to review.";
            case 3:
                return $"3 apps used the network for the first time from {time}: {Name(0)}, {Name(1)} and {Name(2)}. Open Alerts to review.";
            default:
                return $"{group.Count.ToString("N0", culture)} apps used the network for the first time from {time}: {Name(0)}, {Name(1)}, {Name(2)} and {(group.Count - 3).ToString("N0", culture)} more. Open Alerts to review.";
        }
    }

    /// <summary>In the balloon only, a name over 40 text elements keeps its first 39 and an ellipsis, so a surrogate pair is never split.</summary>
    private static string Shorten(string name)
    {
        var info = new StringInfo(name);
        return info.LengthInTextElements > 40 ? info.SubstringByTextElements(0, 39) + "…" : name;
    }
}
