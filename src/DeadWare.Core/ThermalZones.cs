namespace DeadWare.Core;

/// <summary>A CPU temperature taken from Windows' thermal zones, and where it came from, for the status line.</summary>
public sealed record ThermalZoneFallback(double Celsius, string Source);

/// <summary>
/// The CPU temperature fallback from Windows' ACPI thermal zones, for when the hardware library reports none: Intel and AMD
/// sensors need administrator rights and the PawnIO driver, and the hardware library has no sensor it can read on an ARM processor
/// such as a Snapdragon Surface's. Zones need neither, since Windows' own thermal management uses them, but they are board
/// sensors near the processor rather than its own, so the reading is approximate.
/// </summary>
public static class ThermalZones
{
    public const double MinimumCelsius = 5, MaximumCelsius = 125;

    /// <summary>A reading a running device could have. A zone whose firmware leaves it unset reports 0 K or other garbage.</summary>
    public static bool IsPlausible(double celsius) => celsius is >= MinimumCelsius and <= MaximumCelsius;

    /// <summary>
    /// A laptop or tablet: Windows reports a battery. Desktops (no battery, or an unknown state) are not, since their boards often
    /// expose fixed placeholder zones, such as a constant 27.85 °C, that would pass for a CPU temperature.
    /// </summary>
    public static bool IsPortable(BatteryPowerState state) =>
        state is BatteryPowerState.OnBattery or BatteryPowerState.Charging or BatteryPowerState.PluggedIn;

    /// <summary>
    /// The CPU temperature from one poll's zones, or null on a desktop or without a plausible zone. A zone named for the CPU wins
    /// (the hottest, if several); otherwise the hottest zone stands in, since on a fanless tablet the processor is the main heat source.
    /// </summary>
    public static ThermalZoneFallback? CpuFallback(IReadOnlyList<(string Name, double Celsius)> zones, bool portable)
    {
        if (!portable) return null;
        var plausible = zones.Where(zone => IsPlausible(zone.Celsius)).ToArray();
        if (plausible.Length == 0) return null;
        var cpu = plausible.Where(zone => zone.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (cpu.Length > 0 || plausible.Length == 1)
        {
            var chosen = (cpu.Length > 0 ? cpu : plausible).MaxBy(zone => zone.Celsius);
            return new(chosen.Celsius, "Windows thermal zone " + ShortName(chosen.Name));
        }
        // The hottest zone can change from poll to poll, so the source counts the zones instead of naming one.
        return new(plausible.Max(zone => zone.Celsius), $"the hottest of {plausible.Length} Windows thermal zones");
    }

    /// <summary>A zone's own ACPI name without its scope: "CPUZ" for \_TZ.CPUZ, "TZ01" for \_SB.TZ01.</summary>
    public static string ShortName(string name)
    {
        string trimmed = name.Trim();
        int cut = trimmed.LastIndexOfAny(['.', '\\']);
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
    }

    /// <summary>
    /// The hardware status note when no processor sensor gave the CPU temperature: the zone it came from, else why there is none.
    /// It never says "drive", "disk" or "storage", which would put it on the Storage page's status line.
    /// </summary>
    public static string StatusNote(ThermalZoneFallback? fallback, bool lowLevelAvailable) =>
        fallback is not null ? $" · CPU temperature from {fallback.Source} (approximate)."
        : lowLevelAvailable ? " · CPU temperature unavailable (this processor's sensor is not supported; Windows reports no usable thermal zone)."
        : " · CPU temperature unavailable (Intel and AMD processors need DeadWare running as administrator with PawnIO installed; Windows reports no usable thermal zone).";
}
