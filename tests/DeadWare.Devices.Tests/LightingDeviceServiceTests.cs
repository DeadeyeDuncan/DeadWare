using DeadWare.Core;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class LightingDeviceServiceTests : IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-svc-"+Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider time=new();
    readonly FakeOpenRgbSession session=FakeOpenRgbSession.FromFixture();
    readonly HostLog log;
    readonly LightingDeviceService svc;
    public LightingDeviceServiceTests(){log=new HostLog(dir);svc=new(session,time,log);svc.Rescan();}
    public void Dispose(){log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    int IndexOf(string serial)=>session.Controllers.First(c=>c.Serial==serial).Index;
    const string Dram="SERIAL-1",Hid="SERIAL-4",Node="SERIAL-7";
    void Ms(int ms)=>time.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact] public void Rescan_builds_keys_from_serial_and_location()
    {
        Assert.Equal(8,svc.Devices.Count);
        Assert.Equal(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"),svc.Devices[0].Key);
        Assert.Equal(LightingKeys.Device("SERIAL-7",@"HID: \\?\HID#FAKE-4"),svc.Devices[6].Key);
        Assert.False(svc.Rescan());
    }
    [Fact] public void Direct_device_switches_mode_once_then_only_updates_leds()
    {
        var s=new LightingSettings();
        session.Controllers[3]=session.Controllers[3] with{ActiveMode=2};svc.Rescan();
        svc.Paint(s);Ms(500);svc.Paint(s);Ms(500);svc.Paint(s);
        Assert.Equal(0,Assert.Single(session.Modes,m=>m.Index==3).Mode);
        Assert.Equal(3,session.Leds.Count(l=>l.Index==3));
        Assert.DoesNotContain(session.Modes,m=>m.Index==0);
    }
    [Fact] public void Node_Pro_gets_rainbow_wave_by_name_and_no_led_writes()
    {
        var s=new LightingSettings();var node=IndexOf(Node);var modes=session.Controllers[node].Modes.ToList();
        svc.Paint(s);Ms(1000);svc.Paint(s);
        var m=Assert.Single(session.Modes,x=>x.Index==node);
        Assert.Equal(modes.IndexOf("Rainbow Wave"),m.Mode);
        Assert.Empty(m.Colors??[]);
        s.LightsOn=false;svc.Paint(s);svc.Paint(s);
        var all=session.Modes.Where(x=>x.Index==node).ToList();
        Assert.Equal(2,all.Count);
        Assert.Equal(modes.IndexOf("Static"),all[1].Mode);
        Assert.Equal([Rgb.Black],all[1].Colors!);
        s.LightsOn=true;svc.Paint(s);
        Assert.Equal(3,session.Modes.Count(x=>x.Index==node));
        Assert.DoesNotContain(session.Leds,l=>l.Index==node);
    }
    [Fact] public void Unchanged_frame_is_not_resent_before_400ms_and_is_after()
    {
        var s=new LightingSettings();var i=IndexOf(Hid);
        svc.Paint(s);Ms(399);svc.Paint(s);
        Assert.Single(session.Leds,l=>l.Index==i);
        Ms(1);svc.Paint(s);
        Assert.Equal(2,session.Leds.Count(l=>l.Index==i));
    }
    [Fact] public void Dram_device_is_capped_at_200ms()
    {
        var s=new LightingSettings();var i=IndexOf(Dram);
        svc.Paint(s);s.Brightness=50;Ms(199);svc.Paint(s);
        Assert.Single(session.Leds,l=>l.Index==i);
        Ms(1);svc.Paint(s);
        Assert.Equal(2,session.Leds.Count(l=>l.Index==i));
    }
    [Fact] public void Hid_device_is_capped_at_33ms()
    {
        var s=new LightingSettings();var i=IndexOf(Hid);
        svc.Paint(s);s.Brightness=50;Ms(32);svc.Paint(s);
        Assert.Single(session.Leds,l=>l.Index==i);
        Ms(1);svc.Paint(s);
        Assert.Equal(2,session.Leds.Count(l=>l.Index==i));
    }
    [Fact] public void Lights_off_refreshes_black()
    {
        var s=new LightingSettings{LightsOn=false};var i=IndexOf(Hid);
        svc.Paint(s);Ms(400);svc.Paint(s);
        var writes=session.Leds.Where(l=>l.Index==i).ToList();
        Assert.Equal(2,writes.Count);
        Assert.All(writes,w=>Assert.All(w.Colors,c=>Assert.Equal(Rgb.Black,c)));
    }
    [Fact] public void Index_change_on_rescan_writes_to_new_index_by_key()
    {
        var s=new LightingSettings();
        svc.Paint(s);
        session.Reorder(l=>{var x=l.ToList();var d=x[3];x.RemoveAt(3);x.Insert(0,d);return x;});
        Assert.True(svc.Rescan());
        Assert.Equal(0,IndexOf(Hid));
        session.ClearLeds();Ms(1000);svc.Paint(s);
        var w=Assert.Single(session.Leds,l=>l.Colors.Length==112);
        Assert.Equal(0,w.Index);
    }
    [Fact] public void Reset_clears_cache_so_next_paint_resends_and_reswitches_mode()
    {
        var s=new LightingSettings();var hid=IndexOf(Hid);var node=IndexOf(Node);
        session.Controllers[3]=session.Controllers[3] with{ActiveMode=2};svc.Rescan();
        svc.Paint(s);
        Assert.Single(session.Modes,m=>m.Index==hid);
        session.Controllers[3]=session.Controllers[3] with{ActiveMode=2};
        svc.Reset();svc.Rescan();
        svc.Paint(s);
        Assert.Equal(2,session.Modes.Count(m=>m.Index==hid));
        Assert.Equal(2,session.Leds.Count(l=>l.Index==hid));
        Assert.Equal(2,session.Modes.Count(m=>m.Index==node));
    }
    [Fact] public void Views_mark_own_colour_and_default_colour_zones()
    {
        var s=new LightingSettings();var dev=svc.Devices[2];var z0=dev.Zones[0];var z1=dev.Zones[1];
        s.ZoneColors[LightingKeys.Zone(dev.Key,z0.Index)]=new(){Color="#112233",ZoneName=z0.Name};
        s.ZoneColors[LightingKeys.Zone(dev.Key,z1.Index)]=new(){Color="#445566",ZoneName="renamed"};
        var v=svc.Views(s);
        Assert.Equal(8,v.Count);
        var gpu=v[2];
        Assert.Equal(dev.Key,gpu.Key);Assert.Equal(PaintKind.Direct,gpu.Kind);
        Assert.Equal("#112233",gpu.Zones[0].Color);Assert.True(gpu.Zones[0].OwnColor);
        Assert.Equal("#4682B4",gpu.Zones[1].Color);Assert.False(gpu.Zones[1].OwnColor);
        Assert.Equal(PaintKind.Hardware,v[6].Kind);Assert.Equal("Rainbow Wave",v[6].Mode);
    }
    [Fact] public void One_device_failure_does_not_stop_others_and_is_retried()
    {
        var s=new LightingSettings();var bad=IndexOf(Hid);var other=IndexOf("SERIAL-5");
        session.FailLeds=i=>i==bad;
        svc.Paint(s);
        Assert.Contains(session.Leds,l=>l.Index==other);
        session.FailLeds=null;Ms(1);svc.Paint(s);
        Assert.Contains(session.Leds,l=>l.Index==bad);
    }
    [Fact] public void Reset_without_rescan_reissues_direct_switch()
    {
        var s=new LightingSettings();var hid=IndexOf(Hid);
        svc.Paint(s);
        Assert.DoesNotContain(session.Modes,m=>m.Index==hid); // already Direct in the fixture
        svc.Reset();Ms(1000);svc.Paint(s);
        Assert.Equal(0,Assert.Single(session.Modes,m=>m.Index==hid).Mode);
    }
    [Fact] public void ModeColors_shapes_to_the_slot_count()
    {
        Rgb r=new(1,2,3),g=new(4,5,6);
        Assert.Null(OpenRgbSession.ModeColors(null,3));
        Assert.Null(OpenRgbSession.ModeColors([r],0));
        Assert.Equal([r,r,r],OpenRgbSession.ModeColors([r],3)!);
        Assert.Equal([r],OpenRgbSession.ModeColors([r,g],1)!);
    }
}
