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
    // Ruling AB, on the captured Node Pro: with lights off its card still names its own effect, and it doesn't stay lit (it has Static).
    [Fact] public void Views_name_the_Node_Pros_own_effect_with_lights_off()
    {
        var v=svc.Views(new LightingSettings{LightsOn=false})[6];
        Assert.Equal("Rainbow Wave",v.OwnEffect);
        Assert.False(v.StaysLitWhenOff);
        Assert.Equal("Static",v.Mode);
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
    // Ruling AQ, DeadLight's seedZones: DeadWare's OpenRGB keeps its own settings, where the Node Pro's resizable channels start with no LEDs,
    // so its Rainbow Wave would run on none. Channel 1 (zone 0), which carries the three LL fans, is sized to 48 LEDs when it reports 0;
    // channel 2 is physically empty and is never sized (oversizing it strobed the chain under DeadLight).
    [Fact] public void Node_Pro_channel_1_with_no_LEDs_is_sized_to_48_and_channel_2_never()
    {
        var node=IndexOf(Node);
        session.SizeZone(node,0,0);
        svc.Rescan();
        Assert.Equal([(node,0,48)],session.Resizes);
        var nodePro=svc.Devices.Single(d=>d.Name=="Corsair Lighting Node Pro");
        Assert.Equal(48,nodePro.Zones[0].LedCount); // rescanned after the resize
        Assert.Equal(0,nodePro.Zones[1].LedCount);
        Assert.Contains("Corsair Lighting Node Pro",File.ReadAllText(Path.Combine(dir,"devices.log")));
    }
    // A resize that held isn't repeated on this connection, even if channel 1 reads 0 again; a new OpenRGB process, or a reconnect, sizes it again.
    [Fact] public void Node_Pro_is_sized_once_per_connection()
    {
        var node=IndexOf(Node);
        session.SizeZone(node,0,0);
        svc.Rescan();svc.Rescan();
        Assert.Single(session.Resizes);
        session.SizeZone(node,0,0);
        svc.Rescan();
        Assert.Single(session.Resizes);
        svc.Reset();svc.Rescan();
        Assert.Equal(2,session.Resizes.Count);
    }
    // B9: OpenRGB sends no reply to a resize and ignores one outside the zone's limits, so the success line waits for a read that shows 48.
    [Fact] public void Node_Pro_sizing_is_logged_once_a_read_shows_48()
    {
        var node=IndexOf(Node);
        session.SizeZone(node,0,0);
        svc.Rescan();
        var text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(1,Count(text,"sized Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs"));
        Assert.Equal(0,Count(text,"didn't hold"));
    }
    // B9: a resize OpenRGB ignored gets one failure line and no success line, and is tried again at each rescan, quietly, until it holds.
    [Fact] public void Node_Pro_sizing_that_does_not_hold_is_logged_once_and_retried_at_each_rescan()
    {
        var node=IndexOf(Node);
        session.KeepResizes=false;
        session.SizeZone(node,0,0);
        svc.Rescan();svc.Rescan();svc.Rescan();
        Assert.Equal(3,session.Resizes.Count);
        var text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(0,Count(text,"sized Corsair Lighting Node Pro"));
        Assert.Equal("sizing Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs didn't hold: OpenRGB reports 0 LEDs; trying again at each rescan where it reads 0 LEDs",Assert.Single(Lines(text,"didn't hold")));
        session.KeepResizes=true;
        svc.Rescan(); // the fourth try holds
        svc.Rescan(); // and channel 1 now reads 48, so nothing more is sent
        Assert.Equal(4,session.Resizes.Count);
        text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(1,Count(text,"sized Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs"));
        Assert.Equal(1,Count(text,"didn't hold"));
    }
    // Polish round 2, item 9: the read straight after the resize still shows 0, and the next rescan's shows 48, either because OpenRGB
    // applied that resize late or because the retry held. The effect is sent again for the new size, success is logged once (after the one
    // failure line), and the sizing latches: a later 0 isn't sized again on this connection.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_Node_Pro_size_that_shows_a_rescan_late_resends_its_effect_and_is_logged_once(bool resizedAgain)
    {
        var s=new LightingSettings();var node=IndexOf(Node);
        svc.Paint(s);
        session.KeepResizes=false;
        session.SizeZone(node,0,0);
        svc.Rescan();svc.Paint(s);
        if(resizedAgain) session.KeepResizes=true; // the retry at the next rescan holds at once
        else session.SizeZone(node,0,48);           // the first resize shows late, before the next rescan reads
        svc.Rescan();svc.Paint(s);
        svc.Rescan();svc.Paint(s);
        var calls=session.Calls.Where(c=>c.StartsWith("resize")||c.StartsWith($"mode {node}/")).ToList();
        string[] resizes=resizedAgain?["resize "+node+"/0/48","resize "+node+"/0/48"]:["resize "+node+"/0/48"];
        Assert.Equal(["mode "+node+"/1",..resizes,"mode "+node+"/1"],calls);
        var text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(1,Count(text,"sized Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs"));
        Assert.Equal(1,Count(text,"didn't hold"));
        session.SizeZone(node,0,0);
        svc.Rescan();
        Assert.Equal(resizes.Length,session.Resizes.Count);
    }
    // A resize that didn't hold changed no LEDs, so the hardware effect isn't sent again for it: a retry every rescan doesn't restart the wave.
    [Fact] public void A_Node_Pro_sizing_that_does_not_hold_leaves_its_effect_alone()
    {
        var s=new LightingSettings();var node=IndexOf(Node);
        svc.Paint(s);
        session.KeepResizes=false;
        session.SizeZone(node,0,0);
        svc.Rescan();svc.Paint(s);svc.Rescan();svc.Paint(s);
        Assert.Equal(2,session.Resizes.Count);
        Assert.Single(session.Modes,m=>m.Index==node);
    }
    // Whole-branch review D6: a resize after which channel 1 reads another size than it had (OpenRGB clamped it, say) changed its LEDs,
    // so the hardware effect, set up for the old size, is sent again. F3 item 1: that size is never sized again, since DeadWare sizes
    // channel 1 only when it reads 0 LEDs, as DeadLight does, and its one log line says so.
    [Fact] public void A_Node_Pro_resize_that_leaves_another_size_resends_its_effect()
    {
        var s=new LightingSettings();var node=IndexOf(Node);
        svc.Paint(s);
        session.SizeZone(node,0,0);
        session.ResizeInstead=(i,z,_)=>session.SizeZone(i,z,16);
        svc.Rescan();svc.Paint(s);
        var calls=session.Calls.Where(c=>c.StartsWith("resize")||c.StartsWith($"mode {node}/")).ToList();
        Assert.Equal(["mode "+node+"/1","resize "+node+"/0/48","mode "+node+"/1"],calls);
        var sent=session.Calls;var colours=session.LedCount;
        svc.Rescan();svc.Paint(s); // channel 1 still reads 16: nothing is sent
        // F4 item 4d (whole-branch review D5): nothing of any kind, no resize, no mode and no colours.
        Assert.Equal(sent,session.Calls);
        Assert.Equal(colours,session.LedCount);
        Assert.Single(session.Resizes);
        Assert.Equal("sizing Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs didn't hold: OpenRGB reports 16 LEDs; not trying again unless it reads 0 LEDs",Assert.Single(Lines(File.ReadAllText(Path.Combine(dir,"devices.log")),"didn't hold")));
    }
    // D6: a resize after which the read shows no channel 1 at all gets its one failure line, and the line says so. F2 item 5: channel 1's
    // LEDs changed as well, since it has none now, so the hardware effect is forgotten and the next paint sends it again. The Node Pro
    // itself is still there, so Rescan's own cleanup, which drops only what was sent to devices that are gone, isn't what forgets it.
    // F3 item 1: it is sized again only at a rescan where channel 1 is back and reads 0 LEDs, as the line says.
    [Fact] public void A_Node_Pro_channel_gone_after_a_resize_is_logged_as_no_such_zone_and_resends_its_effect()
    {
        var s=new LightingSettings();var node=IndexOf(Node);
        svc.Paint(s);
        session.SizeZone(node,0,0);
        var channel1=session.Controllers[node].Zones.Single(z=>z.Index==LightingDeviceService.NodeProZone);
        session.ResizeInstead=(i,z,_)=>session.Controllers[i]=session.Controllers[i] with{Zones=session.Controllers[i].Zones.Where(x=>x.Index!=z).ToList()};
        svc.Rescan();svc.Paint(s);
        var text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal("sizing Corsair Lighting Node Pro Corsair Channel 1 to 48 LEDs didn't hold: OpenRGB reports no such zone; trying again at each rescan where it reads 0 LEDs",Assert.Single(Lines(text,"didn't hold")));
        var nodePro=Assert.Single(svc.Devices,d=>d.Name==LightingDeviceService.NodePro);
        Assert.DoesNotContain(nodePro.Zones,z=>z.Index==LightingDeviceService.NodeProZone);
        var calls=session.Calls.Where(c=>c.StartsWith("resize")||c.StartsWith($"mode {node}/")).ToList();
        Assert.Equal(["mode "+node+"/1","resize "+node+"/0/48","mode "+node+"/1"],calls);
        svc.Rescan(); // still no channel 1: nothing to size
        Assert.Single(session.Resizes);
        session.ResizeInstead=null;
        session.Controllers[node]=session.Controllers[node] with{Zones=[channel1 with{LedCount=0},..session.Controllers[node].Zones]};
        svc.Rescan(); // back with 0 LEDs: sized again
        Assert.Equal(2,session.Resizes.Count);
    }
    // D6: Reset starts a connection's sizing afresh, the failure latch included, as DeviceHost.ConnectLocked's Reset and Rescan do on a
    // reconnect. After a resize that didn't hold, a reconnect whose first read already shows 48 sized nothing on that connection, so it
    // logs no success; and a resize that then fails on it is logged again.
    [Fact] public void Reset_clears_the_Node_Pro_sizing_failure_latch()
    {
        var node=IndexOf(Node);
        session.KeepResizes=false;
        session.SizeZone(node,0,0);
        svc.Rescan();
        session.SizeZone(node,0,48);
        svc.Reset();svc.Rescan();
        var text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(0,Count(text,"sized Corsair Lighting Node Pro"));
        Assert.Equal(1,Count(text,"didn't hold"));
        session.SizeZone(node,0,0);
        svc.Rescan();
        text=File.ReadAllText(Path.Combine(dir,"devices.log"));
        Assert.Equal(2,session.Resizes.Count);
        Assert.Equal(2,Count(text,"didn't hold"));
    }
    static int Count(string text,string what)=>text.Split(what).Length-1;
    // The log's lines that contain what, each without the time stamp it starts with.
    static string[] Lines(string text,string what)=>text.Split('\n').Where(l=>l.Contains(what)).Select(l=>l[(l.IndexOf(' ')+1)..]).ToArray();
    [Fact] public void Only_the_Node_Pros_channel_1_is_ever_sized()
    {
        var node=IndexOf(Node);var board=IndexOf("SERIAL-8");
        session.SizeZone(node,1,0);   // channel 2 empty, channel 1 sized: nothing to do
        session.SizeZone(board,0,0);  // another device's empty first zone: not DeadLight's rule
        svc.Rescan();
        Assert.Empty(session.Resizes);
    }
    [Fact] public void Sizing_the_Node_Pro_resends_its_hardware_effect_for_the_new_size()
    {
        var s=new LightingSettings();var node=IndexOf(Node);
        svc.Paint(s);
        Assert.Single(session.Modes,m=>m.Index==node);
        session.SizeZone(node,0,0);
        svc.Rescan();
        svc.Paint(s);
        var calls=session.Calls.Where(c=>c.StartsWith("resize")||c.StartsWith($"mode {node}/")).ToList();
        Assert.Equal(["mode "+node+"/1","resize "+node+"/0/48","mode "+node+"/1"],calls);
    }
    // Task 8: a mode's colour slots come from the last controller read, so a mode change makes no extra controller-data round trip, and a
    // mode or device index that read doesn't have is refused plainly instead of indexing past the end.
    [Fact] public void Mode_colours_are_shaped_from_the_last_read_and_an_index_it_lacks_is_refused()
    {
        IReadOnlyList<IReadOnlyList<int>> slots=[[0,1,3]];
        Rgb r=new(1,2,3);
        Assert.Equal([r,r,r],OpenRgbSession.ShapeModeColors(slots,0,2,[r])!);
        Assert.Null(OpenRgbSession.ShapeModeColors(slots,0,0,[r]));
        Assert.Null(OpenRgbSession.ShapeModeColors(slots,0,2,null));
        Assert.Throws<InvalidOperationException>(()=>OpenRgbSession.ShapeModeColors(slots,0,3,[r]));
        Assert.Throws<InvalidOperationException>(()=>OpenRgbSession.ShapeModeColors(slots,1,0,[r]));
        Assert.Throws<InvalidOperationException>(()=>OpenRgbSession.ShapeModeColors(slots,0,-1,[r]));
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
