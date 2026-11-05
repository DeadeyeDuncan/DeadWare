using System.Buffers.Binary;

namespace DeadWare.Core;

public enum BatteryPowerState { Unknown, NoBattery, OnBattery, Charging, PluggedIn }

public sealed record BatteryReading(double? Percentage, BatteryPowerState PowerState, bool? SaverEnabled)
{
    public static BatteryReading Unavailable { get; } = new(null, BatteryPowerState.Unknown, null);

    /// <param name="saver">SYSTEM_POWER_STATUS.SystemStatusFlag, the legacy battery-saver flag: 0 off, 1 on, anything else unknown.</param>
    /// <param name="energySaver">The Windows 11 24H2+ Energy saver level (0 off, 1 standard, 2 high savings), or null when Windows has
    /// reported none, as on older Windows; the legacy flag then decides alone.</param>
    public static BatteryReading FromWindows(byte ac, byte flags, byte percentage, byte saver, int? energySaver = null)
    {
        var state = flags == 255 ? BatteryPowerState.Unknown
            : (flags & 128) != 0 ? BatteryPowerState.NoBattery
            : (flags & 8) != 0 ? BatteryPowerState.Charging
            : ac == 0 ? BatteryPowerState.OnBattery
            : ac == 1 ? BatteryPowerState.PluggedIn : BatteryPowerState.Unknown;
        bool? legacy = saver <= 1 ? saver == 1 : null;
        // Energy saver's standard level leaves the legacy flag at 0, so a reported level settles "on"; off still honours the flag.
        bool? saving = energySaver switch { 1 or 2 => true, 0 => legacy == true, _ => legacy };
        return new(state != BatteryPowerState.NoBattery && percentage <= 100 ? percentage : null, state, saving);
    }
}

/// <summary>Windows 11 24H2+ Energy saver, reported through the GUID_ENERGY_SAVER_STATUS power setting as 0 off, 1 standard or
/// 2 high savings. It replaced battery saver; the legacy SYSTEM_POWER_STATUS.SystemStatusFlag reads 0 in the standard level.</summary>
public static class EnergySaver
{
    public static readonly Guid StatusSetting = new("550E8400-E29B-41D4-A716-446655440000");

    /// <summary>The level in a POWERBROADCAST_SETTING (the setting GUID, a DWORD DataLength, then the data from offset 20),
    /// or null for another setting, a buffer without a DWORD of data, or an unknown level.</summary>
    public static int? Parse(ReadOnlySpan<byte> setting)
    {
        if (setting.Length < 24 || new Guid(setting[..16]) != StatusSetting) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(setting[16..20]) < 4) return null;
        uint level = BinaryPrimitives.ReadUInt32LittleEndian(setting[20..24]);
        return level <= 2 ? (int)level : null;
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
