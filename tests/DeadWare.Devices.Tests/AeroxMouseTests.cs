using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// Lighting 4, Task 7: the receiver mouse's sleep gate and the silent-mouse rule (P1), against the fake transport, fake time and a fake
// pointer. A frame is five writes: brightness (0x63), then the four zones; the sleep time adds 0x69 and, once per value, the save 0x51.
public sealed class AeroxMouseTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-mouse-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    readonly FakeAeroxTransport fake=new();
    readonly FakeInput input=new();
    readonly FakeTimeProvider time=new();
    readonly AeroxDriver driver;
    readonly AeroxMouse mouse;
    static readonly byte[] Ack=[0x61];

    public AeroxMouseTests()
    {
        log=new HostLog(Path.Combine(dir,"logs"));
        fake.Products.Add(AeroxDriver.Receiver);
        driver=new AeroxDriver(fake,log);
        mouse=new AeroxMouse(driver,input,time,log);
        mouse.Tick();mouse.Scan();
    }
    public void Dispose(){fake.HangWrite?.Set();driver.Dispose();log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    static Rgb[] Four(int v){var c=new Rgb((byte)v,(byte)(v>>8),7);return [c,c,c,c];}
    void Step(int ms=33){time.Advance(TimeSpan.FromMilliseconds(ms));mouse.Tick();}
    void Move(){var a=input.At;input.At=Tuple.Create(a.Item1+1,a.Item2);}
    int Writes=>fake.Writes.Count;
    int LogCount(string text){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllText(p).Split(text).Length-1:0;}
    // The colours of the last frame written: zone writes carry R G B at [4..6].
    Rgb[] LastFrame()=>fake.Writes.TakeLast(4).Select(w=>new Rgb(w[4],w[5],w[6])).ToArray();
    // The first frame (a must-send, since the receiver just appeared) delivered with this sleep time.
    void Deliver(int? sleep,int v=1){Assert.True(mouse.Paint(Four(v),sleep));Step();}

    [Fact]
    public void With_no_sleep_time_frames_flow_while_still()
    {
        Deliver(null);
        for(var v=2;v<=20;v++){Assert.True(mouse.Paint(Four(v),null));Step();}
        Assert.Equal(20*5,Writes);
    }

    [Fact]
    public void With_never_frames_flow_while_still()
    {
        Deliver(0);
        Assert.Equal(7,Writes);                                   // 0x69 0 ms, the save, then the frame
        for(var v=2;v<=20;v++){Assert.True(mouse.Paint(Four(v),0));Step();}
        Assert.Equal(7+19*5,Writes);
    }

    [Fact]
    public void With_a_sleep_time_a_still_mouse_gets_no_effect_frames()
    {
        Deliver(5);
        Assert.Equal(7,Writes);
        for(var v=2;v<152;v++){Assert.False(mouse.Paint(Four(v),5));Step();}
        Assert.Equal(7,Writes);
    }

    [Fact]
    public void Moving_releases_frames_for_one_second()
    {
        Deliver(5);
        Move();Step();                                            // the move is stamped on this tick
        var sent=0;
        for(var k=0;k<61;k++){if(mouse.Paint(Four(100+k),5)) sent++;Step();}
        Assert.Equal(31,sent);                                    // 0, 33, ... 990 ms after the move; 1023 ms is held
        Assert.Equal(7+31*5,Writes);
    }

    [Fact]
    public void A_must_send_goes_through_while_still()
    {
        Deliver(5,9);
        Step(5000);
        mouse.Must();                                             // lights off
        Assert.True(mouse.Paint([Rgb.Black,Rgb.Black,Rgb.Black,Rgb.Black],5));
        Assert.Equal(7+5,Writes);
        Assert.All(LastFrame(),c=>Assert.Equal(Rgb.Black,c));
        Assert.False(mouse.Paint(Four(3),5));                     // the next frame that isn't one is held again
    }

    [Fact]
    public void A_held_frame_is_not_remembered_as_sent()
    {
        Deliver(5,1);
        Assert.False(mouse.Paint(Four(2),5));
        Step(2000);
        Move();Step();
        Assert.True(mouse.Paint(Four(2),5));                      // the same colours as the held frame are written after the release
        Assert.Equal(Four(2),LastFrame());
    }

    [Fact]
    public void An_unanswered_must_send_waits_for_movement_not_a_timer()
    {
        Deliver(null);
        fake.Reply=_=>null;
        mouse.Must();
        Assert.False(mouse.Paint(Four(1),null));
        Assert.Equal(5+1,Writes);                                 // stops at the first silent write
        for(var k=0;k<300;k++){Assert.False(mouse.Paint(Four(1),null));Step();}   // ten seconds still
        fake.Reply=_=>Ack;
        for(var k=0;k<30;k++){Assert.False(mouse.Paint(Four(1),null));Step();}
        Assert.Equal(6,Writes);
        Move();Step();
        Assert.True(mouse.Paint(Four(1),null));
        Assert.Equal(6+5,Writes);
        Assert.False(mouse.Paint(Four(1),null));                  // the must-send was used up
    }

    // Ruling P1: after a missing reply nothing goes until the pointer moves, then at most one attempt per MoveWindow.
    [Fact]
    public void A_silent_mouse_is_tried_at_most_once_a_second_while_moving()
    {
        fake.Reply=_=>null;
        Assert.False(mouse.Paint(Four(1),null));
        Assert.Equal(1,Writes);
        for(var k=1;k<=90;k++){Move();Step();mouse.Paint(Four(k),null);}   // three seconds of movement, a new frame each tick
        Assert.Equal(3,Writes);                                   // at 1023 and 2046 ms
    }

    [Fact]
    public void The_gate_needs_the_sleep_time_delivered_first()
    {
        Deliver(null,4);
        Assert.True(mouse.Paint(Four(4),5));                      // sleep chosen, not yet delivered: the same colours go, carrying it
        Assert.Equal(1,fake.Count(0x69));
        Assert.True(driver.SleepSent(5));
        Step();
        Assert.False(mouse.Paint(Four(5),5));                     // delivered: now a still mouse is held
    }

    [Fact]
    public void Holding_and_release_are_logged_once()
    {
        Deliver(5);
        for(var v=2;v<12;v++){mouse.Paint(Four(v),5);Step();}
        Assert.Equal(1,LogCount("mouse: holding frames (still)"));
        Move();Step();
        for(var v=20;v<25;v++){mouse.Paint(Four(v),5);Step();}
        Assert.Equal(1,LogCount("mouse: released (moved)"));
        Step(2000);
        for(var v=30;v<35;v++){mouse.Paint(Four(v),5);Step();}
        Assert.Equal(2,LogCount("mouse: holding frames (still)"));
        mouse.Must();
        Assert.True(mouse.Paint(Four(40),5));
        Assert.Equal(1,LogCount("mouse: released (must-send)"));
        Assert.Equal(2,LogCount("mouse: released"));
    }

    // Ruling P6: a port (re)opened is a must-send, so a mouse switched off and on doesn't keep its firmware colours until a colour changes.
    [Fact]
    public void A_reopened_port_gets_the_unchanged_frame()
    {
        Deliver(null,3);
        Assert.False(mouse.Paint(Four(3),null));                  // unchanged: nothing goes
        fake.ThrowOnWrite=true;
        Assert.False(mouse.Paint(Four(5),null));                  // a real failure closes the port
        fake.ThrowOnWrite=false;
        var before=Writes;
        Assert.False(mouse.Paint(Four(3),null));                  // nothing until the slow work's scan
        Assert.Equal(before,Writes);
        mouse.Scan();
        Assert.True(mouse.Paint(Four(3),null));
        Assert.Equal(2,fake.Opens);
        mouse.Reopen();                                           // wake
        Assert.True(mouse.Paint(Four(3),null));
        Assert.Equal(3,fake.Opens);
        Assert.False(mouse.Paint(Four(3),null));
    }

    // Ruling P6: the mouse answering again (here a battery read notices) is a must-send and ends the silence.
    [Fact]
    public void A_mouse_that_answers_again_gets_the_unchanged_frame()
    {
        Deliver(null,3);
        fake.Reply=_=>null;
        Assert.False(mouse.Paint(Four(4),null));                  // no reply: silent
        Step();
        fake.Reply=_=>Ack;
        Assert.Equal(AeroxResult.Failed,driver.Battery(false,out _,out _));   // an answer, though not a battery reading
        Assert.True(driver.Answering);
        mouse.Tick();
        var before=Writes;
        Assert.True(mouse.Paint(Four(3),null));                   // the colours last delivered, sent again while still
        Assert.Equal(before+5,Writes);
    }

    // Fix round 1, R14: a port (re)opened gets one attempt while the mouse is silent, without movement; a missing reply silences it again.
    [Fact]
    public void A_reopened_port_gets_one_attempt_while_silent()
    {
        Deliver(null);
        fake.Reply=_=>null;
        Assert.False(mouse.Paint(Four(2),null));                  // silent
        Step(2000);
        mouse.Reopen();                                           // wake
        Assert.False(mouse.Paint(Four(2),null));
        Assert.Equal(5+2,Writes);                                 // one attempt, on a fresh port
        Assert.Equal(2,fake.Opens);
        for(var k=0;k<90;k++){Assert.False(mouse.Paint(Four(2),null));Step();}
        Assert.Equal(7,Writes);                                   // silent again until the pointer moves
        Move();Step();
        Assert.False(mouse.Paint(Four(2),null));
        Assert.Equal(8,Writes);
        // A thrown failure closes the port; the slow work's Scan reopens it, and that reopen gets its one attempt too.
        Step(2000);Move();Step();
        fake.ThrowOnWrite=true;
        Assert.False(mouse.Paint(Four(2),null));
        fake.ThrowOnWrite=false;
        Step(2000);
        var before=Writes;
        Assert.False(mouse.Paint(Four(2),null));
        Assert.Equal(before,Writes);                              // nothing before the Scan
        mouse.Scan();
        Assert.False(mouse.Paint(Four(2),null));
        Assert.False(mouse.Paint(Four(2),null));
        Assert.Equal(before+1,Writes);
    }

    // Fix round 1, R17: an exchange that runs into the driver's deadline counts as a missing reply: later ticks and Scans send nothing until
    // the pointer moves, so a HID call that hangs and returns can't hold the host's lock every 5 s.
    [Fact]
    public void A_timed_out_exchange_silences_like_a_missing_reply()
    {
        using var slow=new AeroxDriver(fake,log,TimeSpan.FromMilliseconds(100));
        var m=new AeroxMouse(slow,input,time,log);
        m.Tick();m.Scan();
        var hang=new ManualResetEventSlim(false);
        fake.HangWrite=hang;
        Assert.False(m.Paint(Four(1),null));                      // Failed at the deadline
        Assert.True(slow.TimedOut);
        hang.Set();fake.HangWrite=null;
        Assert.True(SpinWait.SpinUntil(()=>fake.Closes==1,RealTimeCap));   // the worker returned and disposed the abandoned port
        Thread.Sleep(50);
        var before=Writes;
        for(var k=0;k<200;k++)
        {
            time.Advance(TimeSpan.FromMilliseconds(33));m.Tick();
            if(k%150==0) m.Scan();
            Assert.False(m.Paint(Four(1),null));
        }
        Assert.Equal(before,Writes);
        var a=input.At;input.At=Tuple.Create(a.Item1+1,a.Item2);
        time.Advance(TimeSpan.FromMilliseconds(33));m.Tick();
        Assert.True(m.Paint(Four(1),null));                       // movement: one attempt, answered
        Assert.Equal(before+5,Writes);
    }

    // Fix round 1, minor 2: a release that isn't for a must-send or movement (the sleep time due again, the gate switched off) logs no reason.
    [Fact]
    public void A_release_for_another_reason_logs_none()
    {
        Deliver(5);
        Assert.False(mouse.Paint(Four(2),5));
        Assert.True(mouse.Paint(Four(2),10));                     // a new sleep time goes out at once
        Assert.False(mouse.Paint(Four(3),10));
        Assert.True(mouse.Paint(Four(3),null));                   // the gate is off
        Assert.Equal(2,LogCount("mouse: holding frames (still)"));
        Assert.Equal(0,LogCount("mouse: released"));
    }
}

// Lighting 4, Task 7: the receiver mouse in the host (rulings P1, P5, R2) and the copy of the cable's colours, through DeviceHostTests'
// harness.
public sealed class DeviceHostMouseTests:IAsyncLifetime
{
    readonly DeviceHostTests t=new();
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-hostmouse-"+Guid.NewGuid().ToString("N"));
    readonly HostLog mouseLog;
    readonly FakeAeroxTransport fake=new();
    readonly FakeInput input=new();
    readonly FakeSignals signals=new();
    AeroxDriver? driver;
    static readonly string CableKey=LightingKeys.Device("SERIAL-5",@"HID: \\?\HID#FAKE-2");

    public DeviceHostMouseTests(){mouseLog=new HostLog(Path.Combine(dir,"logs"));}
    public Task InitializeAsync()=>t.InitializeAsync();
    public async Task DisposeAsync(){await t.DisposeAsync();driver?.Dispose();mouseLog.Dispose();try{Directory.Delete(dir,true);}catch{}}

    void Save(Action<LightingSettings> edit){var s=new LightingSettings();edit(s);new LightingStore(t.LightingPath).Save(s);}
    static void Wave(LightingSettings s){s.Effect.Kind="Wave";s.Effect.Wave.Colors=["#FF0000","#0000FF"];}
    // The receiver alone: OpenRGB doesn't list the cable device.
    async Task<DeviceHost> Receiver()
    {
        t.Session.Reorder(l=>l.Where(c=>!AeroxIdentity.Is(c.Name)).ToList());
        fake.Products.Add(AeroxDriver.Receiver);
        driver=new AeroxDriver(fake,mouseLog);
        var h=t.BuildHost(signals,input,mouse:new AeroxMouse(driver,input,t.Time,mouseLog));t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        return h;
    }
    int ApexLeds=>t.Session.Leds.Count(l=>l.Index==t.ApexIndex);

    [Fact]
    public async Task A_silent_mouse_does_not_slow_the_keyboard()
    {
        Save(Wave);
        fake.Reply=_=>null;
        var h=await Receiver();
        Assert.Contains(h.State.Devices,d=>d.Key==AeroxIdentity.Key&&d.Name==AeroxIdentity.ReceiverName);
        await t.StepUntil(()=>t.Time.Waiting(DeviceHost.FastTick),tick:DeviceHost.FastTick);
        var from=ApexLeds;
        await t.StepFor(TimeSpan.FromSeconds(2),DeviceHost.FastTick);
        Assert.True(ApexLeds-from>=55,$"{ApexLeds-from} keyboard frames in 2 s");   // about one per 33 ms tick
        Assert.Single(fake.Writes);                               // one silent write in all, not one per tick
    }

    [Fact]
    public async Task Movement_releases_a_held_frame()
    {
        Save(s=>{Wave(s);s.Mouse.SleepMinutes=5;});
        var h=await Receiver();
        await t.StepUntil(()=>fake.Count(0x69)==1,tick:DeviceHost.FastTick);   // the first frame carries the sleep time
        await t.StepFor(TimeSpan.FromMilliseconds(500),DeviceHost.FastTick);
        var held=fake.Writes.Count;
        await t.StepFor(TimeSpan.FromSeconds(2),DeviceHost.FastTick);
        Assert.Equal(held,fake.Writes.Count);                     // the wave moves, the still mouse gets nothing
        input.At=Tuple.Create(5,5);
        await t.StepUntil(()=>fake.Writes.Count>=held+10,tick:DeviceHost.FastTick);   // frames flow again
    }

    // Ruling R2: the wake's end reopens the receiver and sends a frame while the mouse lies still.
    // Fix round 1, R14: a mouse silent before sleep gets exactly one attempt after the wake without movement, then silence until it moves.
    [Fact]
    public async Task After_wake_a_silent_mouse_gets_one_attempt()
    {
        fake.Reply=_=>null;
        var h=await Receiver();
        await t.StepUntil(()=>fake.Writes.Count==1);
        signals.Suspend();
        await Until(()=>h.State.LightsOffReason==LightsOff.Waking);
        signals.Resume();
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepUntil(()=>fake.Writes.Count==2);
        Assert.Equal(2,fake.Opens);
        await t.StepFor(TimeSpan.FromSeconds(6),DeviceHost.Tick);   // past slow work too
        Assert.Equal(2,fake.Writes.Count);
        input.At=Tuple.Create(5,5);
        await t.StepUntil(()=>fake.Writes.Count==3);
    }

    [Fact]
    public async Task The_wake_reopens_the_receiver_and_repaints_it()
    {
        Save(s=>s.Mouse.SleepMinutes=5);
        var h=await Receiver();
        await t.StepUntil(()=>fake.Count(0x63)==1);
        signals.Suspend();
        await Until(()=>h.State.LightsOffReason==LightsOff.Waking);
        signals.Resume();
        await t.StepUntil(()=>h.State.LightsOffReason is null);
        await t.StepUntil(()=>fake.Count(0x63)==2);
        Assert.Equal(2,fake.Opens);
        Assert.Equal(2,fake.Count(0x69));                         // the fresh port is sent the sleep time again
    }

    // Must-sends from the host: a change of the saved switch (Mutate) and a change of effective lights (the lock) reach a still mouse.
    [Fact]
    public async Task Lights_off_reaches_a_still_mouse()
    {
        Save(s=>{s.Mouse.SleepMinutes=5;s.Everyday.LockOff=true;});
        var h=await Receiver();
        await t.StepUntil(()=>fake.Count(0x63)==1);
        Assert.True((await DeviceHostTests.SendTo(h,new SetLightsOnRequest(false))).Ok);
        await t.StepUntil(()=>fake.Count(0x63)==2&&Black());
        Assert.True((await DeviceHostTests.SendTo(h,new SetLightsOnRequest(true))).Ok);
        await t.StepUntil(()=>fake.Count(0x63)==3&&!Black());
        signals.Lock(true);
        await t.StepUntil(()=>h.State.LightsOffReason==LightsOff.Locked);
        await t.StepUntil(()=>fake.Count(0x63)==4&&Black());
    }
    [Fact]
    public async Task A_colour_edit_reaches_a_still_mouse()
    {
        Save(s=>s.Mouse.SleepMinutes=5);
        var h=await Receiver();
        await t.StepUntil(()=>fake.Count(0x63)==1);
        Assert.True((await DeviceHostTests.SendTo(h,new SetAllColorsRequest("#00FF00"))).Ok);
        await t.StepUntil(()=>fake.Count(0x63)==2);
        Assert.All(fake.Writes.TakeLast(4),w=>Assert.Equal([0,255,0],w[4..7]));
    }
    bool Black()=>fake.Writes.TakeLast(4).All(w=>w[1] is 0x61 or 0x66&&w[4]==0&&w[5]==0&&w[6]==0);

    [Fact]
    public async Task The_cable_colours_are_adopted_before_the_layout_is_arranged()
    {
        Save(s=>{for(var i=0;i<4;i++) s.ZoneColors[LightingKeys.Zone(CableKey,i)]=new ZoneColorEntry{Color="#123456",ZoneName=AeroxIdentity.Zones[i]};});
        var h=t.BuildHost(null,null);t.StartHost(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        var mouse=Assert.Single(h.State.Devices,d=>d.Key==AeroxIdentity.Key);
        Assert.All(mouse.Zones,z=>Assert.Equal("#123456",z.Color));
        Assert.All(mouse.Zones,z=>Assert.True(z.OwnColor));
        var file=new LightingStore(t.LightingPath).Load().Settings;
        for(var i=0;i<4;i++)
        {
            Assert.Equal("#123456",file.ZoneColors[LightingKeys.Zone(AeroxIdentity.Key,i)].Color);
            Assert.Equal("#123456",file.ZoneColors[LightingKeys.Zone(CableKey,i)].Color);   // the old entries stay
        }
        Assert.Contains(file.Layout.Zones.Keys,k=>k.StartsWith(AeroxIdentity.Key+"#"));
        Assert.DoesNotContain(file.Layout.Zones.Keys,k=>k.StartsWith(CableKey));
    }
}
