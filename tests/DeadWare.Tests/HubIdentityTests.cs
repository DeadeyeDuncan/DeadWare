using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 5, Task 8: the hub under one fixed key, and the one copy of its old entries.
public class HubIdentityTests
{
    const string Old=@"SERIAL-6|HID: \\?\HID#FAKE-3",Keyboard=@"SERIAL-4|HID: \\?\HID#FAKE-1";
    static readonly string[] Names=["iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"];

    static void Hub(LightingSettings s,string key,string color)
    {
        for(var i=0;i<4;i++) s.ZoneColors[LightingKeys.Zone(key,i)]=new ZoneColorEntry{Color=color,ZoneName=Names[i]};
    }
    static string? Color(LightingSettings s,string key,int i)=>s.ZoneColors.TryGetValue(LightingKeys.Zone(key,i),out var e)?e.Color:null;

    [Fact]
    public void Only_the_exact_name_is_the_hub()
    {
        Assert.True(HubIdentity.Is("Corsair iCUE Link System Hub"));
        Assert.False(HubIdentity.Is("Corsair iCUE Link Hub (test)"));
        Assert.False(HubIdentity.Is("corsair icue link system hub"));
        Assert.False(HubIdentity.Is(null));
    }

    [Fact]
    public void Old_colours_are_copied_and_the_old_entries_stay()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");
        Assert.True(HubIdentity.Adopt(s,Old));
        for(var i=0;i<4;i++)
        {
            Assert.Equal("#112233",Color(s,"linkhub",i));
            Assert.Equal(Names[i],s.ZoneColors["linkhub#"+i].ZoneName);
            Assert.Equal("#112233",Color(s,Old,i));
        }
        Assert.Equal(8,s.ZoneColors.Count);
    }

    [Fact]
    public void Colours_already_under_linkhub_are_not_overwritten()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");
        s.ZoneColors["linkhub#2"]=new ZoneColorEntry{Color="#ABCDEF",ZoneName="iCUE LINK RX RGB"};
        Assert.False(HubIdentity.Adopt(s,Old));
        Assert.Equal("#ABCDEF",Color(s,"linkhub",2));
        Assert.Null(Color(s,"linkhub",0));
    }

    [Fact]
    public void Tuning_is_copied_separately()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");Hub(s,"linkhub","#445566");
        s.Devices[Old]=new DeviceTuning{Brightness=40,Correction=new DeviceCorrection{R=90,G=100,B=80}};
        Assert.True(HubIdentity.Adopt(s,Old));
        Assert.Equal("#445566",Color(s,"linkhub",0));
        var t=s.Devices["linkhub"];
        Assert.Equal(40,t.Brightness);Assert.Equal(80,t.Correction!.B);
        Assert.NotSame(s.Devices[Old],t);Assert.NotSame(s.Devices[Old].Correction,t.Correction);
        Assert.Equal(40,s.Devices[Old].Brightness);
    }

    [Fact]
    public void Without_an_old_key_the_zone_names_find_the_entry()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");
        Assert.True(HubIdentity.Adopt(s,null));
        Assert.Equal("#112233",Color(s,"linkhub",3));
    }

    [Fact]
    public void A_device_with_any_other_zone_name_is_never_taken()
    {
        var s=new LightingSettings();
        s.ZoneColors[LightingKeys.Zone(Keyboard,0)]=new ZoneColorEntry{Color="#FF0000",ZoneName="Keyboard"};
        s.ZoneColors[LightingKeys.Zone("SERIAL-9|HID: X",0)]=new ZoneColorEntry{Color="#00FF00",ZoneName="iCUE LINK RX RGB"};
        s.ZoneColors[LightingKeys.Zone("SERIAL-9|HID: X",1)]=new ZoneColorEntry{Color="#00FF00",ZoneName="Keyboard"};
        s.Devices[Keyboard]=new DeviceTuning{Brightness=10};
        Assert.False(HubIdentity.CanAdopt(s,null));
        Assert.False(HubIdentity.Adopt(s,null));
        Assert.DoesNotContain(s.ZoneColors.Keys,k=>k.StartsWith("linkhub"));
        Assert.False(s.Devices.ContainsKey("linkhub"));
    }

    [Fact]
    public void Hardware_modes_and_layout_are_not_copied()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");
        s.HardwareModes[Old]="Direct";
        s.Layout.Zones[LightingKeys.Zone(Old,0)]=new DeskPoint(0.5,0.5);
        Assert.True(HubIdentity.Adopt(s,Old));
        Assert.False(s.HardwareModes.ContainsKey("linkhub"));
        Assert.DoesNotContain(s.Layout.Zones.Keys,k=>k.StartsWith("linkhub#"));
    }

    [Fact]
    public void Adopt_twice_copies_once()
    {
        var s=new LightingSettings();Hub(s,Old,"#112233");
        s.Devices[Old]=new DeviceTuning{Brightness=40};
        Assert.True(HubIdentity.Adopt(s,Old));
        s.ZoneColors[LightingKeys.Zone(Old,0)].Color="#000000";
        Assert.False(HubIdentity.Adopt(s,Old));
        Assert.Equal("#112233",Color(s,"linkhub",0));
    }

    [Fact]
    public void The_device_is_a_cooler_with_Direct_only()
    {
        var d=HubIdentity.Device(Names.Zip([8,8,8,20]));
        Assert.Equal("linkhub",d.Key);Assert.Equal("Corsair iCUE Link System Hub",d.Name);Assert.Equal(3,d.Type);
        Assert.Equal([0,1,2,3],d.Zones.Select(z=>z.Index).ToArray());
        Assert.Equal([8,8,8,20],d.Zones.Select(z=>z.LedCount).ToArray());
        Assert.Equal(Names,d.Zones.Select(z=>z.Name).ToArray());
        Assert.Equal(["Direct"],d.Modes.ToArray());
        Assert.Equal(0,d.ActiveMode);
    }
}
