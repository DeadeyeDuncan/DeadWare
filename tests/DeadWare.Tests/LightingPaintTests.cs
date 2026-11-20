using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightingPaintTests
{
    private static LightingDevice Dev(string name="Kb",params string[] modes)=>
        new("k|l",name,0,[new LightingZone(0,"Keyboard",3),new LightingZone(1,"Logo",1)],modes.Length==0?["Direct"]:modes,0);

    private readonly LightingSettings s=new();
    private readonly LightingDevice d=new("e|l","Strip",0,[new LightingZone(0,"A",2),new LightingZone(1,"B",3)],["Direct"],0);
    public LightingPaintTests(){s.Effect.Kind="Wave";s.Effect.Wave.Colors=["#FF0000","#0000FF"];s.Effect.Wave.Spread=50;}

    [Fact] public void A_wave_gives_each_zone_its_points_colour_on_every_led()
    {
        s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Layout.Zones[LightingKeys.Zone(d.Key,1)]=new(0.8,0);   // phases 0 and 0.5
        var p=LightingPaint.Paint(s,d,0);
        Assert.Equal(new[]{new Rgb(255,0,0),new Rgb(255,0,0),new Rgb(0,0,255),new Rgb(0,0,255),new Rgb(0,0,255)},p.Colors);
    }
    [Fact] public void A_zone_without_a_point_keeps_its_static_colour(){s.Layout.Zones.Clear();Assert.All(LightingPaint.Paint(s,d,0).Colors,c=>Assert.Equal(new Rgb(0x46,0x82,0xB4),c));}
    [Fact] public void An_unplaced_device_keeps_its_static_colour(){s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Layout.Unplaced.Add(d.Key);Assert.Equal(new Rgb(0x46,0x82,0xB4),LightingPaint.Paint(s,d,0).Colors[0]);}
    [Fact] public void Brightness_is_applied_after_the_effect(){s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0,0);s.Brightness=50;Assert.Equal(new Rgb(128,0,0),LightingPaint.Paint(s,d,0).Colors[0]);}
    [Fact] public void Lights_off_is_black_whatever_the_effect(){s.LightsOn=false;Assert.All(LightingPaint.Paint(s,d,1234).Colors,c=>Assert.Equal(Rgb.Black,c));Assert.False(LightingPaint.EffectRunning(s));}
    [Fact] public void A_hardware_device_is_untouched_by_an_effect()
    {
        var node=new LightingDevice("n|1","Corsair Lighting Node Pro",0,[new(0,"Channel 1",48)],["Direct","Rainbow Wave"],1);
        var p=LightingPaint.Paint(s,node,999);
        Assert.Equal(PaintKind.Hardware,p.Kind);Assert.Equal("Rainbow Wave",p.Mode);Assert.Empty(p.Colors);
    }
    [Fact] public void Breathing_uses_the_shared_colour_for_a_zone_with_none_of_its_own()
    {
        s.Effect.Kind="Breathing";s.Layout.Zones[LightingKeys.Zone(d.Key,0)]=new(0.5,0.5);
        var half=1000/LightingEffects.CyclesPerSecond(40)/2;                                                       // phase 0.5: full colour
        Assert.Equal(new Rgb(0x46,0x82,0xB4),LightingPaint.Paint(s,d,-half).Colors[0]);
        Assert.Equal(Rgb.Black,LightingPaint.Paint(s,d,0).Colors[0]);                                                // phase 0: black
        // L6: a zone with a colour of its own pulses that colour, not the shared one.
        s.Layout.Zones[LightingKeys.Zone(d.Key,1)]=new(0.9,0.1);s.ZoneColors[LightingKeys.Zone(d.Key,1)]=new ZoneColorEntry{Color="#FF0000",ZoneName="B"};
        var p=LightingPaint.Paint(s,d,-half).Colors;
        Assert.Equal(new Rgb(0x46,0x82,0xB4),p[0]);Assert.Equal(new Rgb(255,0,0),p[2]);Assert.Equal(new Rgb(255,0,0),p[4]);
        Assert.Equal(Rgb.Black,LightingPaint.Paint(s,d,0).Colors[2]);
    }
    [Fact] public void EffectRunning_needs_lights_on_and_a_moving_kind(){Assert.True(LightingPaint.EffectRunning(s));s.Effect.Kind="Static";Assert.False(LightingPaint.EffectRunning(s));}

    [Fact]
    public void Paint_uses_zone_entry_when_name_matches()
    {
        var s=new LightingSettings();
        s.ZoneColors["k|l#0"]=new ZoneColorEntry{Color="#FF0000",ZoneName="Keyboard"};
        var p=LightingPaint.Paint(s,Dev());
        Assert.Equal(PaintKind.Direct,p.Kind);
        Assert.Equal(4,p.Colors.Length);
        Assert.Equal(new Rgb(255,0,0),p.Colors[0]);
        Assert.Equal(new Rgb(255,0,0),p.Colors[2]);
        Assert.Equal(new Rgb(0x46,0x82,0xB4),p.Colors[3]);
    }

    [Fact]
    public void Paint_falls_back_to_default_when_zone_name_changed()
    {
        var s=new LightingSettings();
        s.ZoneColors["k|l#0"]=new ZoneColorEntry{Color="#FF0000",ZoneName="Old name"};
        var p=LightingPaint.Paint(s,Dev());
        Assert.All(p.Colors,c=>Assert.Equal(new Rgb(0x46,0x82,0xB4),c));
    }

    [Fact]
    public void Paint_scales_by_brightness()
    {
        var s=new LightingSettings{Brightness=50,DefaultColor="#640000"};
        var p=LightingPaint.Paint(s,Dev());
        Assert.All(p.Colors,c=>Assert.Equal(new Rgb(50,0,0),c));
    }

    [Fact]
    public void Paint_lights_off_is_black_on_every_led()
    {
        var p=LightingPaint.Paint(new LightingSettings{LightsOn=false},Dev());
        Assert.Equal(PaintKind.Direct,p.Kind);
        Assert.Equal(4,p.Colors.Length);
        Assert.All(p.Colors,c=>Assert.Equal(Rgb.Black,c));
    }

    [Fact]
    public void Node_Pro_defaults_to_rainbow_wave_and_never_direct()
    {
        var d=Dev("Corsair Lighting Node Pro","Direct","Rainbow Wave");
        var p=LightingPaint.Paint(new LightingSettings(),d);
        Assert.Equal(PaintKind.Hardware,p.Kind);
        Assert.Equal("Rainbow Wave",p.Mode);
    }

    [Fact]
    public void Explicit_hardware_mode_wins_only_when_device_offers_it()
    {
        var s=new LightingSettings();
        s.HardwareModes["k|l"]="Breathing";
        Assert.Equal("Breathing",LightingPaint.HardwareModeFor(s,Dev("Kb","Direct","Breathing")));
        Assert.Null(LightingPaint.HardwareModeFor(s,Dev("Kb","Direct")));
    }

    // Task 3: the explicit "Direct" choice (how step 3 will record "DeadWare effects") is no hardware mode, so the device is painted, even
    // one with a hardware default such as the Node Pro. It must never come back as a hardware mode, which would switch the device to Direct
    // and then send it no frames.
    [Fact]
    public void Explicit_Direct_is_no_hardware_mode_so_the_device_is_painted()
    {
        var s=new LightingSettings{DefaultColor="#FF0000"};
        s.HardwareModes["k|l"]="Direct";
        var d=Dev("Corsair Lighting Node Pro","Direct","Rainbow Wave");
        Assert.Null(LightingPaint.HardwareModeFor(s,d));
        var p=LightingPaint.Paint(s,d);
        Assert.Equal(PaintKind.Direct,p.Kind);
        Assert.Equal(4,p.Colors.Length);
        Assert.All(p.Colors,c=>Assert.Equal(new Rgb(255,0,0),c));
    }

    [Fact]
    public void Hardware_device_lights_off_uses_static_black_when_offered()
    {
        var d=Dev("Corsair Lighting Node Pro","Static","Rainbow Wave");
        var p=LightingPaint.Paint(new LightingSettings{LightsOn=false},d);
        Assert.Equal(PaintKind.Hardware,p.Kind);
        Assert.Equal("Static",p.Mode);
        Assert.Equal([Rgb.Black],p.Colors);
        Assert.False(p.StaysLitWhenOff);
    }

    [Fact]
    public void Hardware_device_without_static_stays_lit_when_off()
    {
        var d=Dev("Corsair Lighting Node Pro","Rainbow Wave");
        var p=LightingPaint.Paint(new LightingSettings{LightsOn=false},d);
        Assert.Equal(PaintKind.Hardware,p.Kind);
        Assert.True(p.StaysLitWhenOff);
    }

    // Ruling AB: a device's view carries its own hardware effect and whether it stays lit while lights are off, both whatever Lights on
    // is, so its card can say before the lights go off what will happen. Mode stays the current paint (Static black while lights are off).
    [Fact]
    public void View_reports_the_own_effect_and_the_lights_off_capability_whatever_lights_on_is()
    {
        var node=Dev("Corsair Lighting Node Pro","Direct","Rainbow Wave","Static");
        foreach(var on in new[]{true,false})
        {
            var v=LightingPaint.View(new LightingSettings{LightsOn=on},node);
            Assert.Equal(PaintKind.Hardware,v.Kind);
            Assert.Equal("Rainbow Wave",v.OwnEffect);
            Assert.False(v.StaysLitWhenOff); // it offers Static, so lights off turns it black
            Assert.Equal(on?"Rainbow Wave":"Static",v.Mode);
        }
        foreach(var on in new[]{true,false})
            Assert.True(LightingPaint.View(new LightingSettings{LightsOn=on},Dev("Corsair Lighting Node Pro","Direct","Rainbow Wave")).StaysLitWhenOff);
        var painted=LightingPaint.View(new LightingSettings(),Dev());
        Assert.Equal(PaintKind.Direct,painted.Kind);
        Assert.Null(painted.OwnEffect);
        Assert.False(painted.StaysLitWhenOff);
    }

    [Fact]
    public void View_marks_own_and_shared_zone_colours()
    {
        var s=new LightingSettings();
        s.ZoneColors["k|l#0"]=new ZoneColorEntry{Color="#112233",ZoneName="Keyboard"};
        s.ZoneColors["k|l#1"]=new ZoneColorEntry{Color="#445566",ZoneName="Renamed"};
        var v=LightingPaint.View(s,Dev());
        Assert.Equal(["k|l#0","k|l#1"],v.Zones.Select(z=>z.Key));
        Assert.True(v.Zones[0].OwnColor);Assert.Equal("#112233",v.Zones[0].Color);
        Assert.False(v.Zones[1].OwnColor);Assert.Equal("#4682B4",v.Zones[1].Color);
    }

    [Fact]
    public void Device_without_direct_or_default_is_unsupported()
    {
        var p=LightingPaint.Paint(new LightingSettings(),Dev("Mystery","Breathing"));
        Assert.Equal(PaintKind.Unsupported,p.Kind);
    }
}
