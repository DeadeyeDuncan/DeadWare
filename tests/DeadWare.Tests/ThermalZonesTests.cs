using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ThermalZonesTests
{
    private static readonly (string Name,double Celsius)[] Laptop=[(@"\_TZ.GFXZ",71),(@"\_TZ.CPUZ",55.5),(@"\_TZ.BATZ",38)];

    [Fact] public void A_desktop_gets_no_fallback()
    {
        Assert.Null(ThermalZones.CpuFallback(Laptop,portable:false));
        Assert.Null(ThermalZones.CpuFallback([(@"\_TZ.TZ00",27.85)],portable:false));
    }
    [Fact] public void A_zone_named_for_the_cpu_wins_over_hotter_zones()
    {
        Assert.Equal(new ThermalZoneFallback(55.5,"Windows thermal zone CPUZ"),ThermalZones.CpuFallback(Laptop,portable:true));
        // Any case, anywhere in the name; the hottest of several CPU zones.
        Assert.Equal(new ThermalZoneFallback(62,"Windows thermal zone Cpu1"),ThermalZones.CpuFallback([(@"\_SB.TZ01",80),(@"\_SB.Cpu0",58),(@"\_SB.Cpu1",62)],portable:true));
    }
    [Fact] public void Otherwise_the_hottest_zone_stands_in()
    {
        var zones=new[]{(@"\_SB.TZ01",41.5),(@"\_SB.TZ02",48.25),(@"\_SB.TZ03",39d)};
        Assert.Equal(new ThermalZoneFallback(48.25,"the hottest of 3 Windows thermal zones"),ThermalZones.CpuFallback(zones,portable:true));
    }
    [Fact] public void Implausible_readings_are_ignored()
    {
        // 0 K, garbage, not a number, infinite and too cold: only TZ05 is left, and a CPU zone out of range does not win.
        var zones=new[]{(@"\_TZ.CPUZ",-273.15),(@"\_SB.TZ01",130),(@"\_SB.TZ02",double.NaN),(@"\_SB.TZ03",double.PositiveInfinity),(@"\_SB.TZ04",4.9),(@"\_SB.TZ05",44d)};
        Assert.Equal(new ThermalZoneFallback(44,"Windows thermal zone TZ05"),ThermalZones.CpuFallback(zones,portable:true));
        Assert.True(ThermalZones.IsPlausible(5));Assert.True(ThermalZones.IsPlausible(125));
        Assert.False(ThermalZones.IsPlausible(4.99));Assert.False(ThermalZones.IsPlausible(125.01));Assert.False(ThermalZones.IsPlausible(double.NegativeInfinity));
    }
    [Fact] public void No_plausible_zone_gives_null()
    {
        Assert.Null(ThermalZones.CpuFallback([],portable:true));
        Assert.Null(ThermalZones.CpuFallback([(@"\_TZ.TZ00",-273.15),(@"\_TZ.CPUZ",double.NaN)],portable:true));
    }
    [Fact] public void The_source_names_a_zone_without_its_scope()
    {
        Assert.Equal("Windows thermal zone TZ00",ThermalZones.CpuFallback([(@"\_TZ.TZ00",45)],portable:true)!.Source);
        Assert.Equal("CPUZ",ThermalZones.ShortName(@"\_TZ.CPUZ"));Assert.Equal("TZ01",ThermalZones.ShortName(@"\_SB.TZ01"));
        Assert.Equal("TPOL",ThermalZones.ShortName(@"\TPOL"));Assert.Equal("TZ00",ThermalZones.ShortName(@" \_TZ.TZ00 "));
        Assert.Equal("TZ00",ThermalZones.ShortName("TZ00"));Assert.Equal(@"\_TZ.",ThermalZones.ShortName(@"\_TZ."));Assert.Equal("",ThermalZones.ShortName(""));
    }
    [Fact] public void Only_a_device_with_a_battery_is_portable()
    {
        Assert.True(ThermalZones.IsPortable(BatteryPowerState.OnBattery));Assert.True(ThermalZones.IsPortable(BatteryPowerState.Charging));
        Assert.True(ThermalZones.IsPortable(BatteryPowerState.PluggedIn));
        Assert.False(ThermalZones.IsPortable(BatteryPowerState.NoBattery));Assert.False(ThermalZones.IsPortable(BatteryPowerState.Unknown));
    }
    [Fact] public void Status_notes_say_where_the_reading_came_from_or_why_there_is_none()
    {
        string zone=ThermalZones.StatusNote(new(48.25,"the hottest of 3 Windows thermal zones"),lowLevelAvailable:false);
        string standard=ThermalZones.StatusNote(null,lowLevelAvailable:false),unsupported=ThermalZones.StatusNote(null,lowLevelAvailable:true);
        Assert.Equal(" · CPU temperature from the hottest of 3 Windows thermal zones (approximate).",zone);
        Assert.Equal(" · CPU temperature unavailable (Intel and AMD processors need DeadWare running as administrator with PawnIO installed; Windows reports no usable thermal zone).",standard);
        Assert.Equal(" · CPU temperature unavailable (this processor's sensor is not supported; Windows reports no usable thermal zone).",unsupported);
        // The Storage page shows the hardware status's drive, disk and storage parts; none of these notes is one.
        foreach(var note in new[]{zone,standard,unsupported})Assert.Equal("",StoragePresenter.Status(null,[],"12 sensors · Standard user · CPU/GPU sensors"+note));
    }
}
