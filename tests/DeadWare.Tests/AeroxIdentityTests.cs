using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 4, Task 7: the Aerox 9 under one fixed key, and the one copy of its old cable entries.
public class AeroxIdentityTests
{
    const string Cable="SERIAL-5|HID: \\\\?\\HID#FAKE-2",OtherPort="SERIAL-5|HID: \\\\?\\HID#FAKE-9",Keyboard="SERIAL-4|HID: \\\\?\\HID#FAKE-1";
    static readonly string[] MouseZones=AeroxIdentity.Zones;

    static void Mouse(LightingSettings s,string key,string color)
    {
        for(var i=0;i<4;i++) s.ZoneColors[LightingKeys.Zone(key,i)]=new ZoneColorEntry{Color=color,ZoneName=MouseZones[i]};
    }
    static string? Color(LightingSettings s,string key,int i)=>s.ZoneColors.TryGetValue(LightingKeys.Zone(key,i),out var e)?e.Color:null;

    [Theory]
    [InlineData("SteelSeries Aerox 9 Wireless Wired",true)]
    [InlineData("SteelSeries Aerox 9 Wireless",true)]
    [InlineData("SteelSeries Apex Pro",false)]
    [InlineData("steelseries aerox 9 wireless",false)]
    public void Both_names_are_the_mouse(string name,bool mouse)=>Assert.Equal(mouse,AeroxIdentity.Is(name));

    [Fact]
    public void Cable_colours_are_copied_and_the_old_entries_stay()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");
        Assert.True(AeroxIdentity.Adopt(s,Cable));
        for(var i=0;i<4;i++)
        {
            Assert.Equal("#112233",Color(s,AeroxIdentity.Key,i));
            Assert.Equal(MouseZones[i],s.ZoneColors[LightingKeys.Zone(AeroxIdentity.Key,i)].ZoneName);
            Assert.Equal("#112233",Color(s,Cable,i));
        }
        Assert.Equal(8,s.ZoneColors.Count);
    }

    [Fact]
    public void Colours_already_under_the_fixed_key_are_not_overwritten()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");
        s.ZoneColors[LightingKeys.Zone(AeroxIdentity.Key,2)]=new ZoneColorEntry{Color="#ABCDEF",ZoneName="Rear"};
        Assert.False(AeroxIdentity.Adopt(s,Cable));
        Assert.Equal("#ABCDEF",Color(s,AeroxIdentity.Key,2));
        Assert.Null(Color(s,AeroxIdentity.Key,0));
    }

    [Fact]
    public void Tuning_is_copied_separately_from_colours()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");Mouse(s,AeroxIdentity.Key,"#445566");
        s.Devices[Cable]=new DeviceTuning{Brightness=40,Correction=new DeviceCorrection{R=90,G=100,B=80}};
        Assert.True(AeroxIdentity.Adopt(s,Cable));
        Assert.Equal("#445566",Color(s,AeroxIdentity.Key,0));
        var t=s.Devices[AeroxIdentity.Key];
        Assert.Equal(40,t.Brightness);Assert.Equal(80,t.Correction!.B);
        Assert.NotSame(s.Devices[Cable],t);Assert.NotSame(s.Devices[Cable].Correction,t.Correction);   // a deep copy
        Assert.Equal(40,s.Devices[Cable].Brightness);
    }

    [Fact]
    public void Without_a_cable_key_the_zone_names_find_the_old_entry()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");
        s.HardwareModes[Cable]="Rainbow Breathing";
        Assert.True(AeroxIdentity.Adopt(s,null));
        Assert.Equal("#112233",Color(s,AeroxIdentity.Key,3));
        Assert.Equal("Rainbow Breathing",s.HardwareModes[AeroxIdentity.Key]);
    }

    [Fact]
    public void A_keyboard_entry_is_never_mistaken_for_the_mouse()
    {
        var s=new LightingSettings();
        s.ZoneColors[LightingKeys.Zone(Keyboard,0)]=new ZoneColorEntry{Color="#FF0000",ZoneName="Keyboard"};
        s.ZoneColors[LightingKeys.Zone(Keyboard,1)]=new ZoneColorEntry{Color="#FF0000",ZoneName="Reactive"};   // Reactive, but not only mouse zones
        s.ZoneColors[LightingKeys.Zone("SERIAL-6|HID: X",0)]=new ZoneColorEntry{Color="#00FF00",ZoneName="Front"};   // mouse names without Reactive
        s.Devices[Keyboard]=new DeviceTuning{Brightness=10};
        Assert.False(AeroxIdentity.Adopt(s,null));
        Assert.False(AeroxIdentity.HasSaved(s));
        Assert.DoesNotContain(s.ZoneColors.Keys,k=>k.StartsWith(AeroxIdentity.Key));
        Assert.False(s.Devices.ContainsKey(AeroxIdentity.Key));
    }

    [Fact]
    public void Two_old_entries_take_the_first_by_key()
    {
        var s=new LightingSettings();Mouse(s,OtherPort,"#999999");Mouse(s,Cable,"#111111");
        Assert.True(AeroxIdentity.Adopt(s,null));
        Assert.Equal("#111111",Color(s,AeroxIdentity.Key,0));    // FAKE-2 sorts before FAKE-9
        var t=new LightingSettings();Mouse(t,OtherPort,"#999999");Mouse(t,Cable,"#111111");
        Assert.True(AeroxIdentity.Adopt(t,OtherPort));
        Assert.Equal("#999999",Color(t,AeroxIdentity.Key,0));    // the listed cable device's entry comes first
    }

    [Fact]
    public void A_layout_point_alone_is_saved_but_blocks_no_copy()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");
        s.Layout.Zones[LightingKeys.Zone(AeroxIdentity.Key,0)]=new DeskPoint(0.5,0.5);
        Assert.True(AeroxIdentity.HasSaved(s));
        Assert.True(AeroxIdentity.Adopt(s,null));
        Assert.Equal("#112233",Color(s,AeroxIdentity.Key,0));
        Assert.Single(s.Layout.Zones);                           // layout is not copied
        Assert.False(AeroxIdentity.HasSaved(new LightingSettings()));
    }

    [Fact]
    public void Adopt_twice_copies_once()
    {
        var s=new LightingSettings();Mouse(s,Cable,"#112233");
        s.Devices[Cable]=new DeviceTuning{Brightness=40};s.HardwareModes[Cable]="Direct";
        Assert.True(AeroxIdentity.Adopt(s,Cable));
        s.ZoneColors[LightingKeys.Zone(Cable,0)].Color="#000000";
        Assert.False(AeroxIdentity.Adopt(s,Cable));
        Assert.Equal("#112233",Color(s,AeroxIdentity.Key,0));
    }

    [Fact]
    public void The_receiver_device_is_four_one_light_zones_with_Direct_only()
    {
        var d=AeroxIdentity.Receiver();
        Assert.Equal(AeroxIdentity.Key,d.Key);Assert.Equal("SteelSeries Aerox 9 Wireless",d.Name);Assert.Equal(6,d.Type);
        Assert.Equal(MouseZones,d.Zones.Select(z=>z.Name));Assert.All(d.Zones,z=>Assert.Equal(1,z.LedCount));
        Assert.Equal(["Direct"],d.Modes);Assert.Equal(0,d.ActiveMode);
    }
}
