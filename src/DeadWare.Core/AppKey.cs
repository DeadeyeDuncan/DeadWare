using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace DeadWare.Core;

/// <summary>The one identity of an application across the process table, the histories and the exclusion list.</summary>
public static class AppKey
{
    private static readonly Regex PidName = new("^PID [0-9]{1,10}$", RegexOptions.CultureInvariant);

    /// <summary>The normalized name with each unpaired surrogate as U+FFFD (the UTF-8 round trip a saved name takes), trimmed and upper-cased. Never null or empty.</summary>
    public static string Of(string? name)
    {
        string text = UsageMetrics.NormalizeName(name);
        if (text.Any(char.IsSurrogate)) text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
        return text.TrimEnd().ToUpperInvariant();
    }

    /// <summary>True when the key reads "PID n", the label of a process with no usable name.</summary>
    public static bool IsPidName(string? name) => PidName.IsMatch(Of(name));
}

/// <summary>The "Don't record these apps" list.</summary>
public static class AppExclusions
{
    public const int MaximumApps = 64;

    public static bool Accepts(string? name) => ProcessHistory.SavedName(name) is not null && !AppKey.IsPidName(name);

    public static List<string> Normalize(IEnumerable<string?>? names)
    {
        var result = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names is null) return result;
        foreach (var name in names)
        {
            if (result.Count >= MaximumApps) break;
            if (!Accepts(name) || !seen.Add(AppKey.Of(name))) continue;
            result.Add(ProcessHistory.SavedName(name)!);
        }
        return result;
    }

    public static FrozenSet<string> Keys(IEnumerable<string> names) => names.Select(AppKey.Of).ToFrozenSet(StringComparer.Ordinal);
}
