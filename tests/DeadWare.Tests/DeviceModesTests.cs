using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class DeviceModesTests
{
    // A keyboard of one zone of 3 lights at X 0, 0.5 and 1.
    static readonly LightingDevice Kb=new("k|l","Apex",KeyMaps.KeyboardType,[new LightingZone(0,"Keyboard",3,new ZoneMatrix(3,1,[0,1,2]))],["Direct"],0);
    static DeviceMode M(string kind,int speed=0)=>new(){Kind=kind,Speed=speed};
    static LightingSettings Red(){var s=new LightingSettings();s.ZoneColors[LightingKeys.Zone(Kb.Key,0)]=new ZoneColorEntry{Color="#FF8040",ZoneName="Keyboard"};return s;}

    [Fact] public void Static_is_the_saved_zone_colour_on_every_light()=>Assert.Equal(Enumerable.Repeat(new Rgb(255,128,64),3),DeviceModes.Frame(Red(),Kb,M(DeviceModes.Static),1234));
    // Speed 0 is 0.05 cycles a second, so 5000 ms and 10000 ms are phases 0.75 and 0.5 (the phase runs backwards, as the desk effects' does;
    // 0.75 dims by the same half as 0.25).
    [Fact] public void Breathing_pulses_every_light_together_by_half_minus_half_cos()
    {
        var s=Red();
        Assert.Equal(Enumerable.Repeat(Rgb.Black,3),DeviceModes.Frame(s,Kb,M(DeviceModes.Breathing),0));
        Assert.Equal(Enumerable.Repeat(new Rgb(128,64,32),3),DeviceModes.Frame(s,Kb,M(DeviceModes.Breathing),5000));
        Assert.Equal(Enumerable.Repeat(new Rgb(255,128,64),3),DeviceModes.Frame(s,Kb,M(DeviceModes.Breathing),10000));
    }
    [Fact] public void Breathing_without_a_saved_colour_pulses_the_default_colour()=>
        Assert.Equal(Enumerable.Repeat(new Rgb(0x46,0x82,0xB4),3),DeviceModes.Frame(new LightingSettings(),Kb,M(DeviceModes.Breathing),10000));
    [Fact] public void Colour_cycle_is_one_hue_for_the_whole_keyboard()
    {
        var f=DeviceModes.Frame(Red(),Kb,M(DeviceModes.Cycle),5000);                                                 // phase 0.75: hue 270
        Assert.Equal(Enumerable.Repeat(LightingEffects.HsvToRgb(270,1,1),3),f);
        Assert.Equal(new Rgb(128,0,255),f[0]);
    }
    [Fact] public void Rainbow_wave_runs_left_to_right_with_wavelength_1_6()
    {
        var f=DeviceModes.Frame(Red(),Kb,M(DeviceModes.Wave),0);
        Assert.Equal(new[]{0.0,0.5,1.0}.Select(x=>LightingEffects.HsvToRgb(LightingEffects.Wrap01(x/1.6)*360,1,1)),f);
        Assert.Equal(new[]{LightingEffects.HsvToRgb(0,1,1),LightingEffects.HsvToRgb(112.5,1,1),LightingEffects.HsvToRgb(225,1,1)},f);
        // Light 3 is in no cell, so it takes X 0.5.
        var gap=new LightingDevice("k|l","Apex",5,[new LightingZone(0,"Keyboard",4,new ZoneMatrix(3,1,[0,1,2]))],["Direct"],0);
        Assert.Equal(f[1],DeviceModes.Frame(Red(),gap,M(DeviceModes.Wave),0)[3]);
    }
    [Fact] public void Speed_maps_as_the_desk_effects_do()
    {
        const double t=1234;
        var phase=LightingEffects.Wrap01(-t/1000*LightingEffects.CyclesPerSecond(70));
        Assert.Equal(LightingEffects.HsvToRgb(phase*360,1,1),DeviceModes.Frame(Red(),Kb,M(DeviceModes.Cycle,70),t)[0]);
        Assert.Equal(LightingEffects.HsvToRgb(LightingEffects.Wrap01(1/1.6-t/1000*LightingEffects.CyclesPerSecond(70))*360,1,1),DeviceModes.Frame(Red(),Kb,M(DeviceModes.Wave,70),t)[2]);
        var level=0.5-0.5*Math.Cos(2*Math.PI*phase);
        Assert.Equal(new Rgb((byte)Math.Round(255*level,MidpointRounding.AwayFromZero),(byte)Math.Round(128*level,MidpointRounding.AwayFromZero),(byte)Math.Round(64*level,MidpointRounding.AwayFromZero)),DeviceModes.Frame(Red(),Kb,M(DeviceModes.Breathing,70),t)[1]);
    }
    [Fact] public void CantRun_gives_the_two_sentences()
    {
        var mouse=Kb with{Type=6};
        Assert.Equal("Lighting modes are for keyboards.",DeviceModes.CantRun(mouse,DeviceModes.Breathing));
        Assert.Equal("Lighting modes are for keyboards.",DeviceModes.CantRun(mouse,DeviceModes.Static));
        var blind=Kb with{Zones=[new LightingZone(0,"Keyboard",3)]};
        Assert.Equal("This keyboard doesn't report its key positions.",DeviceModes.CantRun(blind,DeviceModes.Wave));
        Assert.Null(DeviceModes.CantRun(blind,DeviceModes.Cycle));
        Assert.Null(DeviceModes.CantRun(Kb,DeviceModes.Wave));
        Assert.True(DeviceModes.Animated(DeviceModes.Wave));Assert.True(DeviceModes.Animated(DeviceModes.Cycle));Assert.True(DeviceModes.Animated(DeviceModes.Breathing));
        Assert.False(DeviceModes.Animated(DeviceModes.Static));Assert.False(DeviceModes.Animated(null));
    }
    [Fact] public void A_mode_that_cant_run_is_not_running_but_stays_saved()
    {
        var s=Red();s.Devices[Kb.Key]=new DeviceTuning{Mode=M(DeviceModes.Wave,30)};
        Assert.Same(s.Devices[Kb.Key].Mode,DeviceModes.Running(s,Kb));
        var blind=Kb with{Zones=[new LightingZone(0,"Keyboard",3)]};
        Assert.Null(DeviceModes.Running(s,blind));
        Assert.Equal(DeviceModes.Wave,s.Devices[Kb.Key].Mode!.Kind);
        Assert.Null(DeviceModes.Running(new LightingSettings(),Kb));
    }
}
