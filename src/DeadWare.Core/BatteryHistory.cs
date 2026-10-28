namespace DeadWare.Core;

public enum BatteryPowerState { Unknown, NoBattery, OnBattery, Charging, PluggedIn }

public sealed record BatteryReading(double? Percentage, BatteryPowerState PowerState, bool? SaverEnabled)
{
    public static BatteryReading Unavailable { get; } = new(null, BatteryPowerState.Unknown, null);

    public static BatteryReading FromWindows(byte ac, byte flags, byte percentage, byte saver)
    {
        var state = flags == 255 ? BatteryPowerState.Unknown
            : (flags & 128) != 0 ? BatteryPowerState.NoBattery
            : (flags & 8) != 0 ? BatteryPowerState.Charging
            : ac == 0 ? BatteryPowerState.OnBattery
            : ac == 1 ? BatteryPowerState.PluggedIn : BatteryPowerState.Unknown;
        return new(state != BatteryPowerState.NoBattery && percentage <= 100 ? percentage : null,
            state, saver <= 1 ? saver == 1 : null);
    }
}

public sealed record BatterySample(DateTimeOffset Timestamp, double MonotonicSeconds, BatteryReading Reading, string Segment);

/// <summary>One minute observations plus state changes. Only the history worker owns this policy.</summary>
public sealed class BatteryRecorder
{
    private BatterySample? previous, saved;
    private double previousInterval=1;
    private string segment = Guid.NewGuid().ToString("N");

    public void Break() { previous = saved = null; segment = Guid.NewGuid().ToString("N"); }

    public BatterySample? Observe(DateTimeOffset timestamp, double seconds, BatteryReading reading, double pollingIntervalSeconds=1)
    {
        if (!double.IsFinite(seconds)) { Break(); return null; }
        if (previous is not null && seconds <= previous.MonotonicSeconds) return null;
        if (reading.Percentage is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100))
            reading = reading with { Percentage = null };
        double interval=PollingPolicy.Normalize(pollingIntervalSeconds);
        if (previous is not null && (seconds - previous.MonotonicSeconds > Math.Max(5,Math.Max(previousInterval,interval)+3) ||
            Math.Abs((timestamp - previous.Timestamp).TotalSeconds - (seconds - previous.MonotonicSeconds)) > 3)) Break();
        var observation = new BatterySample(timestamp, seconds, reading, segment);
        previous = observation;
        previousInterval=interval;
        bool available = reading.Percentage.HasValue || reading.PowerState is BatteryPowerState.Charging or BatteryPowerState.OnBattery or BatteryPowerState.PluggedIn;
        bool wasAvailable = saved is not null && (saved.Reading.Percentage.HasValue ||
            saved.Reading.PowerState is BatteryPowerState.Charging or BatteryPowerState.OnBattery or BatteryPowerState.PluggedIn);
        if (!available && !wasAvailable) return null;
        if (saved is null || seconds - saved.MonotonicSeconds >= 60 ||
            saved.Reading.PowerState != reading.PowerState || saved.Reading.SaverEnabled != reading.SaverEnabled ||
            saved.Reading.Percentage.HasValue != reading.Percentage.HasValue)
        {
            saved = observation;
            return observation;
        }
        return null;
    }
}
