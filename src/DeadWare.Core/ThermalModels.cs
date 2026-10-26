namespace DeadWare.Core;

/// <summary>Local identity and the typed sensor mapping used for a thermal series.</summary>
public sealed record ThermalDevice(string Id, string Name, string Kind, string ConfigurationId, string SensorMap)
{
    public bool ComparisonAllowed { get; init; } = true;
    public string IdentityNote { get; init; } = "";
}

public sealed record ThermalSample(
    ThermalDevice Device, string SessionId, DateTimeOffset Timestamp, double MonotonicSeconds,
    double? Temperature, double? Load, double? Power, double? Clock, double? FanRpm,
    bool Throttled = false, string? UnavailableReason = null);

public sealed record MetricAggregate(int Count, double? Minimum, double? Maximum, double? Average);

/// <summary>Counts refer to valid values, not zeros substituted for missing samples.</summary>
public sealed record ThermalWindow(
    ThermalDevice Device, string SessionId, DateTimeOffset Start, DateTimeOffset End,
    double StartMonotonic, double DurationSeconds, int SampleCount,
    MetricAggregate Temperature, MetricAggregate Load, MetricAggregate Power,
    MetricAggregate Clock, MetricAggregate FanRpm, bool Eligible, string Quality,
    int ResolutionSeconds = 60);

public sealed record ThermalReference(
    string Id, string DeviceId, string ConfigurationId, string Label, DateTimeOffset Created,
    DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<ThermalWindow> Windows);

public sealed record CoolingAssessment(
    string State, string Message, int MatchedWindows, int RequiredWindows,
    double? CurrentTemperature, double? ReferenceTemperature, double? Delta,
    double? ReferenceMinimum, double? ReferenceMaximum, bool ShouldWarn);

public sealed record CoolingWarning(
    string Id, string DeviceId, string ConfigurationId, string ReferenceId,
    DateTimeOffset Created, string Message, double Delta, bool Acknowledged = false);
