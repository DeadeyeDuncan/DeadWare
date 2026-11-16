using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightingPaintTests
{
    private static LightingDevice Dev(string name="Kb",params string[] modes)=>
        new("k|l",name,0,[new LightingZone(0,"Keyboard",3),new LightingZone(1,"Logo",1)],modes.Length==0?["Direct"]:modes,0);

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

    [Fact]
    public void Device_without_direct_or_default_is_unsupported()
    {
        var p=LightingPaint.Paint(new LightingSettings(),Dev("Mystery","Breathing"));
        Assert.Equal(PaintKind.Unsupported,p.Kind);
    }
}
