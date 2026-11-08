namespace DeadWare.Core;

/// <summary>A request to clear history older than <see cref="Before"/>, for one application or for all.</summary>
public sealed record HistoryClear(string? AppKey, DateTimeOffset Before, DateTimeOffset Requested)
{
    public static HistoryClear Create(string? application, bool allTime, DateTimeOffset requested)
    {
        var floor = UsageMetrics.Floor(requested);
        return new(application is null ? null : Core.AppKey.Of(application), allTime ? floor.AddMinutes(1) : floor.AddDays(-7), requested);
    }

    public bool AllTime => Before > Requested;
    public DateTimeOffset AlertCutoff => Before < Requested ? Before : Requested;

    public bool Covers(string? name, DateTimeOffset minute)
        => minute < Before && (AppKey is null || string.Equals(Core.AppKey.Of(name), AppKey, StringComparison.Ordinal));
}
