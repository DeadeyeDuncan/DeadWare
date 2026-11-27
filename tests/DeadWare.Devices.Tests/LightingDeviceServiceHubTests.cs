using DeadWare.Core;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5, Task 8: the hub as a second own device beside the mouse.
public sealed class LightingDeviceServiceHubTests : IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-svch-"+Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time=new();
    readonly FakeOpenRgbSession session=FakeOpenRgbSession.FromFixture(owner:true);
    readonly HostLog log;
    readonly FakeHub hub=new();
    readonly LightingDeviceService svc;
    const string OpenRgbHubKey="SERIAL-6|HID: \\\\?\\HID#FAKE-3";
    public LightingDeviceServiceHubTests(){log=new HostLog(dir);svc=new(session,time,log,null,hub);}
    public void Dispose(){log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    static HubPart Fan(int channel)=>new(channel,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan");
    static readonly HubPart Pump=new(13,0x11,0x02,"iCUE LINK TITAN 360",20,true,"pump");
    static HubListing Owners(bool driving)=>new([Fan(1),Fan(2),Fan(3),Pump],driving);
    int HubIndex=>session.Controllers.First(c=>c.Name==FakeOpenRgbSession.OwnerHub).Index;
    DeviceView HubView()=>svc.Views(new LightingSettings()).Single(v=>v.Key=="linkhub");
    static LightingSettings Colour()=>new(){DefaultColor="#102030",Brightness=100};

    [Fact] public void One_OpenRGB_hub_is_listed_under_linkhub()
    {
        svc.Rescan();
        Assert.Equal(OpenRgbHubKey,svc.HubOldKey);Assert.False(svc.TwoHubs);
        var d=Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        Assert.Equal(FakeOpenRgbSession.OwnerHub,d.Name);
        Assert.Equal("openrgb",HubView().Hub);
        svc.Paint(Colour());
        Assert.Contains(session.Leds,l=>l.Index==HubIndex);
        Assert.Empty(hub.Posts);
    }

    [Fact] public void Two_OpenRGB_hubs_keep_their_own_keys()
    {
        var c=session.Controllers.First(x=>x.Name==FakeOpenRgbSession.OwnerHub);
        session.Controllers=session.Controllers.Append(c with{Index=session.Controllers.Count,Serial="SERIAL-60"}).ToList();
        svc.Rescan();
        Assert.True(svc.TwoHubs);Assert.Null(svc.HubOldKey);
        Assert.DoesNotContain(svc.Devices,x=>x.Key=="linkhub");
        Assert.Equal(2,svc.Devices.Count(x=>x.Name==FakeOpenRgbSession.OwnerHub));
    }

    [Fact] public void A_driven_hub_is_listed_from_its_parts_and_posted()
    {
        svc.Hub=Owners(true);svc.Rescan();
        var d=Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        Assert.Equal(["iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK RX RGB","iCUE LINK TITAN 360"],d.Zones.Select(z=>z.Name).ToArray());
        Assert.Equal([8,8,8,20],d.Zones.Select(z=>z.LedCount).ToArray());
        Assert.Equal("driven",HubView().Hub);
        svc.Paint(Colour());
        Assert.Equal([new Rgb(16,32,48),new Rgb(16,32,48),new Rgb(16,32,48),new Rgb(16,32,48)],hub.Posts.Last());
        Assert.DoesNotContain(session.Leds,l=>l.Index==HubIndex);
    }

    [Fact] public void Each_zone_posts_its_own_colour()
    {
        svc.Hub=Owners(true);svc.Rescan();
        var s=Colour();s.ZoneColors[LightingKeys.Zone("linkhub",1)]=new ZoneColorEntry{Color="#FF0000",ZoneName="iCUE LINK RX RGB"};
        svc.Paint(s);
        var last=hub.Posts.Last();
        Assert.Equal(new Rgb(255,0,0),last[1]);
        Assert.Equal(new Rgb(16,32,48),last[0]);
    }

    [Fact] public void A_hub_on_its_own_is_listed_never_posted_and_has_no_preview()
    {
        svc.Hub=Owners(false);svc.Rescan();
        Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        svc.Paint(Colour());
        Assert.Empty(hub.Posts);
        Assert.DoesNotContain(svc.Preview(Colour(),0).Keys,k=>k.StartsWith("linkhub#"));
        Assert.Equal("own",HubView().Hub);
    }

    [Fact] public void With_the_hub_ours_OpenRGBs_hub_is_left_out()
    {
        var index=HubIndex;
        svc.Hub=Owners(true);svc.Rescan();
        Assert.True(svc.OpenRgbListsHub);
        Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        svc.Paint(Colour());time.Advance(TimeSpan.FromMilliseconds(500));svc.Paint(Colour());
        Assert.DoesNotContain(session.Leds,l=>l.Index==index);
        svc.Reset();
        Assert.False(svc.OpenRgbListsHub);
    }

    [Fact] public void In_mode_openrgb_the_listed_hub_is_not_reported_as_still_there()
    {
        svc.Rescan();
        Assert.False(svc.OpenRgbListsHub);
    }

    [Fact] public void A_change_of_the_hub_listing_changes_the_shape()
    {
        svc.Hub=Owners(true);svc.Rescan();
        svc.Hub=Owners(false);
        Assert.True(svc.Rescan());
        Assert.False(svc.Rescan());
    }

    [Fact] public void Hub_frames_are_not_held_to_33_ms()
    {
        svc.Hub=Owners(true);svc.Rescan();
        var s=Colour();svc.Paint(s);
        time.Advance(TimeSpan.FromMilliseconds(1));
        s.DefaultColor="#304050";svc.Paint(s);
        Assert.Equal(2,hub.Posts.Count);
        Assert.Equal(new Rgb(0x30,0x40,0x50),hub.Posts.Last()[0]);
    }

    [Fact] public void A_part_without_leds_gets_no_zone()
    {
        svc.Hub=new([Fan(1),new HubPart(2,0x13,0x00,"iCUE LINK RX",0,false,"fan"),Pump],true);svc.Rescan();
        var d=Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        Assert.Equal([8,20],d.Zones.Select(z=>z.LedCount).ToArray());
        svc.Paint(Colour());
        Assert.Equal(2,hub.Posts.Last().Length);
    }

    [Fact] public void Reset_keeps_the_listing()
    {
        svc.Hub=Owners(true);svc.Rescan();
        svc.Reset();
        Assert.NotNull(svc.Hub);Assert.True(svc.Hub!.Driving);
        Assert.Single(svc.Devices,x=>x.Key=="linkhub");
    }

    [Fact] public void Saved_parts_list_the_hub_as_own_before_any_take()
    {
        hub.Snapshot=HubSnapshot.Idle;
        svc.Hub=new HubListing(HubParts.FromSaved([new(){Channel=1,Type=15,Model=0},new(){Channel=2,Type=15,Model=0},new(){Channel=3,Type=15,Model=0},new(){Channel=13,Type=17,Model=2}])!,false);
        svc.Rescan();
        var d=Assert.Single(svc.Devices,x=>x.Key=="linkhub");
        Assert.Equal([8,8,8,20],d.Zones.Select(z=>z.LedCount).ToArray());
        Assert.Equal("own",HubView().Hub);
        svc.Paint(Colour());
        Assert.Empty(hub.Posts);
        Assert.DoesNotContain(svc.Preview(Colour(),0).Keys,k=>k.StartsWith("linkhub#"));
    }

    [Fact] public void Between_switching_off_and_the_rescan_the_hub_is_own_and_unlit()
    {
        svc.Hub=Owners(true);svc.Rescan();
        svc.Hub=null;
        Assert.Equal("own",HubView().Hub);
        svc.Paint(Colour());
        Assert.Empty(hub.Posts);
        Assert.DoesNotContain(svc.Preview(Colour(),0).Keys,k=>k.StartsWith("linkhub#"));
    }

    [Fact] public void The_two_hub_names_are_one()
    {
        Assert.Equal("Corsair iCUE Link System Hub",OpenRgbConfig.HubDetectorName);
        Assert.Equal("Corsair iCUE Link System Hub",HubIdentity.Name);
    }
}
