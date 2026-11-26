using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5: the hub core over the fakes of Task 4, stepped by each test itself on fake time. Every test ends with Settled(): the fake lock is
// not held and every Enter had its Exit; reading Held also fails the test for any check the fakes keep (a write or read outside the lock or from
// another thread, a split transaction, a frame off the list) that the core's own catch swallowed. Tests that block a step on a second thread
// (HangRead) release it or Abort the core before anything else steps it.
public sealed class HubCoreTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-hubcore-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    readonly FakeTimeProvider time=new();
    readonly FakeHubLock corsairLock=new();
    readonly FakeHubTransport fake;
    readonly HubCore core;
    readonly DateTimeOffset start;
    readonly ManualResetEventSlim hang=new(false),entered=new(false);
    static readonly Rgb A=new(10,20,30),B=new(1,2,3),C=new(4,5,6),D=new(7,8,9);
    static readonly Rgb[] Four=[A,B,C,D];

    public HubCoreTests()
    {
        log=new HostLog(Path.Combine(dir,"logs"));
        fake=new(time,corsairLock);
        core=new(fake,corsairLock,time,log);
        start=time.GetUtcNow();
    }

    public void Dispose()
    {
        hang.Set();
        core.Abort();
        log.Dispose();
        try{Directory.Delete(dir,true);}catch{}
    }

    void Settled(){Assert.False(corsairLock.Held);Assert.Equal(corsairLock.Enters,corsairLock.Exits);}
    // Moves the fake clock to `seconds` after the test's start (never back).
    void To(double seconds)
    {
        var d=start+TimeSpan.FromTicks((long)Math.Round(seconds*TimeSpan.TicksPerSecond))-time.GetUtcNow();
        if(d>TimeSpan.Zero) time.Advance(d);
    }
    TimeSpan At(double seconds){To(seconds);return core.Step();}
    void Take(){core.Allow(null);core.Step();Assert.Equal("driving",core.Snapshot.State);}
    static string Hex(byte[] b)=>string.Join(" ",b.Select(x=>x.ToString("X2")));
    List<string> Commands()=>fake.Commands.Select(Hex).ToList();
    string Last()=>Commands()[^1];
    // The log's lines without their time prefix.
    string[] Lines(){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllLines(p).Select(l=>l[(l.IndexOf(' ')+1)..]).ToArray():[];}
    int LogCount(string line)=>Lines().Count(l=>l==line);
    HubPartReading Pump()=>core.Snapshot.Readings!.Parts.Single(p=>p.Kind=="pump");
    int?[] Fans()=>core.Snapshot.Readings!.Parts.Where(p=>p.Kind=="fan").Select(p=>p.Rpm).ToArray();
    static Rgb[] Leds(params (Rgb Colour,int Count)[] runs)=>runs.SelectMany(r=>Enumerable.Repeat(r.Colour,r.Count)).ToArray();
    static readonly Rgb[] FourLit=Leds((A,8),(B,8),(C,8),(D,20));

    [Fact]
    public void A_core_nobody_allowed_never_opens_the_hub()
    {
        for(int k=1;k<=12;k++) At(5*k);
        Assert.Equal(0,fake.Opens);
        Assert.Empty(fake.Writes);
        Assert.Equal("off",core.Snapshot.State);
        Settled();
    }

    [Fact]
    public void The_take_sends_its_frames_in_order()
    {
        core.Allow(null);
        core.Step();
        Assert.Equal(new[]{"02 13","05 01 01 21","0D 01 21","05 01 01 21","01 03 00 02",
            "05 01 01 36","0D 01 36","08 01","05 01 01 36","05 01 01 21","0D 01 21","08 01","05 01 01 21","05 01 01 17","0D 01 17","08 01","05 01 01 17"},Commands());
        Assert.Equal(1,corsairLock.Enters);
        Assert.Equal(1,corsairLock.Exits);
        var s=core.Snapshot;
        Assert.Equal("driving",s.State);
        Assert.Equal(4,s.Parts!.Count);
        Assert.Equal(43.7,Pump().Celsius);
        Assert.Equal(2774,Pump().Rpm);
        Assert.Equal(new int?[]{1660,1654,1663},Fans());
        Assert.Equal(time.GetTimestamp(),s.SoftwareAt);
        Assert.Equal(0,fake.Count(0x06,0x00));
        Settled();
    }

    [Theory]
    [InlineData(true,"hub was still held")]
    [InlineData(false,"hub was in its own mode")]
    public void The_take_logs_its_trace(bool software,string probe)
    {
        fake.Software=software;
        Take();
        Assert.Equal(new[]{"hub: take started","hub firmware 4.1.656",probe,"hub: software mode entered",
            "hub part: channel 1, type 0x0F, model 0x00, 8 LEDs","hub part: channel 2, type 0x0F, model 0x00, 8 LEDs","hub part: channel 3, type 0x0F, model 0x00, 8 LEDs",
            "hub part: channel 13, type 0x11, model 0x02, 20 LEDs","hub taken"},Lines());
        Settled();
    }

    [Fact]
    public void A_busy_lock_at_the_take_skips_it()
    {
        corsairLock.Busy=true;
        core.Allow(null);
        core.Step();
        Assert.Empty(fake.Writes);
        Assert.Equal(1,fake.Opens);
        Assert.Equal(fake.Opens,fake.Closes);
        Assert.NotEqual("driving",core.Snapshot.State);
        At(1);                                                               // tried again a second later, still busy
        Assert.Equal(2,fake.Opens);
        Assert.Empty(fake.Writes);
        Assert.Equal(1,LogCount("hub: take started"));                     // a busy spell is not a new take
        corsairLock.Busy=false;
        At(2);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(1,LogCount("Corsair lock busy"));
        Assert.Equal(1,LogCount("Corsair lock free again"));
        Settled();
    }

    // Fix round 1, M3 (ruling R21): a take refused for a busy lock is tried again no sooner than 1 s later.
    [Fact]
    public void A_take_refused_for_a_busy_lock_waits_1_s_before_the_next_try()
    {
        corsairLock.Busy=true;
        core.Allow(null);
        Assert.Equal(TimeSpan.FromSeconds(1),At(0));
        At(0.25);At(0.999);
        Assert.Equal(1,fake.Opens);
        At(1);
        Assert.Equal(2,fake.Opens);
        corsairLock.Busy=false;
        At(1.5);
        Assert.Equal(2,fake.Opens);
        At(2);
        Assert.Equal("driving",core.Snapshot.State);
        Settled();
    }

    [Fact]
    public void The_first_frame_follows_the_first_post_and_fills_44_leds()
    {
        Take();
        int before=fake.Writes.Count;
        core.Post(Four);
        core.Step();
        var sent=Commands().Skip(before).ToList();
        Assert.Equal(4,sent.Count);
        Assert.Equal("05 01 01 22",sent[0]);
        Assert.Equal("0D 00 22",sent[1]);
        Assert.StartsWith("06 00 86 00 00 00 12 00 0A 14 1E",sent[2]);
        Assert.Equal("05 01 01 22",sent[3]);
        Assert.Equal(FourLit,fake.LastColours);
        Assert.Equal(1,LogCount("hub: first frame sent"));
        Assert.Equal(2,corsairLock.Enters);
        Settled();
    }

    [Fact]
    public void Changed_colours_go_at_most_every_125_ms()
    {
        Take();
        core.Post(Four);
        At(0);
        Assert.Equal(1,fake.Count(0x06,0x00));
        core.Post([B,B,B,B]);
        Assert.Equal(TimeSpan.FromMilliseconds(75),At(0.050));
        Assert.Equal(1,fake.Count(0x06,0x00));
        core.Post([C,C,C,C]);
        At(0.100);
        Assert.Equal(1,fake.Count(0x06,0x00));
        At(0.125);
        Assert.Equal(2,fake.Count(0x06,0x00));
        Assert.All(fake.LastColours!,c=>Assert.Equal(C,c));
        Assert.Equal(1,LogCount("hub: first frame sent"));
        Settled();
    }

    [Fact]
    public void Unchanged_colours_are_sent_again_every_2_s()
    {
        Take();
        core.Post(Four);
        At(0);
        At(1.999);
        Assert.Equal(1,fake.Count(0x06,0x00));
        At(2);
        Assert.Equal(2,fake.Count(0x06,0x00));
        Assert.Equal(FourLit,fake.LastColours);
        Settled();
    }

    [Fact]
    public void Readings_come_every_3_s()
    {
        Take();
        int raised=0;
        core.Changed+=()=>raised++;
        fake.Coolant=44.1;
        fake.Rpm[13]=2800;
        int before=fake.Writes.Count;
        At(2.999);
        Assert.Equal(before,fake.Writes.Count);
        Assert.Equal(0,raised);
        At(3);
        Assert.Equal(new[]{"05 01 01 21","0D 01 21","08 01","05 01 01 21","05 01 01 17","0D 01 17","08 01","05 01 01 17"},Commands().Skip(before));
        Assert.Equal(1,raised);
        Assert.Equal(44.1,Pump().Celsius);
        Assert.Equal(2800,Pump().Rpm);
        Assert.Equal(time.GetTimestamp(),core.Snapshot.ReadingsAt);
        Settled();
    }

    [Fact]
    public void Dark_sends_black_and_keeps_readings()
    {
        Take();
        core.Post(Four);
        At(0);
        core.SetDark(true);
        At(0.125);
        Assert.Equal(2,fake.Count(0x06,0x00));
        Assert.Equal(new Rgb[44],fake.LastColours);
        int reads=fake.Count(0x0D,0x01,0x17);
        At(3);
        Assert.Equal(reads+1,fake.Count(0x0D,0x01,0x17));
        Assert.Equal(new Rgb[44],fake.LastColours);
        Assert.Equal("driving",core.Snapshot.State);
        core.SetDark(false);
        At(3.125);
        Assert.Equal(FourLit,fake.LastColours);
        Assert.Equal(0,fake.Count(0x01,0x03,0x00,0x01));
        Settled();
    }

    [Fact]
    public void Dark_needs_no_posted_frame()
    {
        Take();
        core.SetDark(true);
        core.Step();
        Assert.Equal(1,fake.Count(0x06,0x00));
        Assert.Equal(new Rgb[44],fake.LastColours);
        Settled();
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_ignored()
    {
        Take();
        core.Post([A,B,C]);
        At(0);
        core.Post([A,B,C,D,A]);
        At(0.5);
        Assert.Equal(0,fake.Count(0x06,0x00));
        Assert.Equal(1,LogCount("hub: a frame of 3 colours was ignored (the hub has 4 lit parts)"));
        Assert.Single(Lines(),l=>l.Contains("was ignored"));
        core.Post(Four);
        At(1);
        Assert.Equal(1,fake.Count(0x06,0x00));
        Assert.Equal(FourLit,fake.LastColours);
        Settled();
    }

    [Fact]
    public void A_hub_with_no_leds_gets_no_colour_write()
    {
        fake.Parts=new(){[1]=(0x13,0x00),[2]=(0x13,0x00)};
        Take();
        Assert.Equal(2,core.Snapshot.Parts!.Count);
        core.SetDark(true);
        core.Post([]);
        for(int k=0;k<=20;k++) At(0.5*k);
        Assert.Equal(0,fake.Count(0x06,0x00));
        Assert.Equal(0,fake.Count(0x0D,0x00,0x22));
        Assert.Equal(4,fake.Count(0x0D,0x01,0x17));                        // the take's reading, then at 3, 6 and 9 s
        Assert.Equal("driving",core.Snapshot.State);
        Settled();
    }

    [Fact]
    public void Quiet_sends_nothing_not_even_hardware_mode()
    {
        Take();
        core.Post(Four);
        At(0);
        int before=fake.Writes.Count;
        core.Quiet();
        for(int k=1;k<=20;k++){core.Post([B,B,B,B]);At(0.5*k);}
        Assert.Equal(before,fake.Writes.Count);
        Assert.Equal(1,fake.Closes);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("waking",core.Snapshot.Reason);
        Settled();
    }

    // Lighting 5 (extra): Quiet reaches a step already under way: it sends nothing after the call, though its inputs were copied before.
    [Fact]
    public async Task Quiet_during_a_step_stops_its_writes()
    {
        Take();
        core.Post(Four);
        At(0);
        int before=fake.Writes.Count;
        fake.HangRead=hang;fake.ReadEntered=entered;
        To(3);
        var stepping=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));                      // the reading round's first drain read, before any write
        core.Quiet();
        hang.Set();
        await stepping.WaitAsync(Waits.RealTimeCap);
        Assert.Equal(before,fake.Writes.Count);
        Assert.Equal(1,fake.Closes);
        Assert.Equal("waking",core.Snapshot.Reason);
        Settled();
    }

    // Fix round 1, I1 (a): a Quiet that lands during a dialog's reading ends the step there, though holding is allowed: no take during suspend.
    [Fact]
    public async Task Quiet_during_a_dialogs_reading_takes_nothing()
    {
        core.Allow(null);
        core.RequestReading();
        fake.HangRead=hang;fake.ReadEntered=entered;
        var stepping=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));                      // the reading's first drain read, before any write
        core.Quiet();
        hang.Set();
        await stepping.WaitAsync(Waits.RealTimeCap);
        Assert.Empty(fake.Writes);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("waking",core.Snapshot.Reason);
        Assert.False(core.Snapshot.Read!.Ok);
        Assert.Equal(fake.Opens,fake.Closes);
        At(5);
        Assert.Empty(fake.Writes);
        Settled();
    }

    // Fix round 1, I1 (b): a Quiet that lands during the stopped-pump hand-back still leaves PumpStopped in the snapshot (no hardware mode is sent),
    // and after the wake's end the snapshot is the pump-stop let-go again (I2).
    [Fact]
    public async Task Quiet_during_the_stopped_pump_hand_back_keeps_PumpStopped()
    {
        fake.Rpm[13]=0;
        Take();                                                              // the first stalled reading
        At(3);At(6);
        int closes=0;
        fake.ReadEntered=entered;
        fake.Reply=(command,reply)=>{if(Hex(command)=="05 01 01 17"&&++closes==2) fake.HangRead=hang;return reply;};   // the fourth round's last close
        To(9);
        var stepping=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));
        core.Quiet();
        fake.Reply=null;
        hang.Set();
        await stepping.WaitAsync(Waits.RealTimeCap);
        Assert.Equal(1,LogCount("hub: the pump read 0 RPM for 4 readings"));
        Assert.Equal(0,fake.Count(0x01,0x03,0x00,0x01));
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.True(core.Snapshot.PumpStopped);
        core.Resume();At(20);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Null(core.Snapshot.Reason);
        Assert.True(core.Snapshot.PumpStopped);
        Assert.Equal(1,fake.Opens);
        Settled();
    }

    // Fix round 1, I2 (ruling R20): after the wake's end a core that does not take shows the state it is in, not "waking".
    [Fact]
    public void After_the_wake_a_lock_that_cannot_be_made_shows_noLock_again()
    {
        corsairLock.Problem="the Corsair lock could not be made";
        core.Allow(null);
        At(0);
        Assert.Equal("noLock",core.Snapshot.Reason);
        core.Quiet();At(1);
        Assert.Equal("waking",core.Snapshot.Reason);
        core.Resume();At(2);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("noLock",core.Snapshot.Reason);
        Assert.Equal(0,fake.Opens);
        Settled();
    }

    [Fact]
    public void After_the_wake_a_core_nobody_allowed_is_off_again()
    {
        core.RequestReading();
        At(0);
        var read=core.Snapshot.Read;
        Assert.True(read!.Ok);
        core.Quiet();At(1);
        Assert.Equal("waking",core.Snapshot.Reason);
        core.Resume();At(2);
        Assert.Equal("off",core.Snapshot.State);
        Assert.Null(core.Snapshot.Reason);
        Assert.Same(read,core.Snapshot.Read);
        Assert.Equal(4,core.Snapshot.Parts!.Count);
        Assert.Equal(1,fake.Opens);
        Settled();
    }

    [Fact]
    public void Resume_takes_again_and_reads_the_list_again()
    {
        Take();
        core.Quiet();
        At(1);
        fake.Parts[4]=(0x01,0x00);
        core.Resume();
        At(2);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(new[]{1,2,3,4,13},core.Snapshot.Parts!.Select(p=>p.Channel));
        Assert.Equal(2,fake.Opens);
        Settled();
    }

    [Fact]
    public void A_busy_lock_skips_the_round()
    {
        Take();
        int before=fake.Writes.Count;
        corsairLock.Busy=true;
        At(3);At(6);At(9);
        Assert.Equal(before,fake.Writes.Count);
        Assert.Equal("driving",core.Snapshot.State);
        corsairLock.Busy=false;
        At(12);
        Assert.Equal(before+8,fake.Writes.Count);
        Assert.Equal(1,LogCount("Corsair lock busy"));
        Assert.Equal(1,LogCount("Corsair lock free again"));
        Settled();
    }

    [Fact]
    public void No_reply_lets_go_and_retries_at_30_s_then_60_s()
    {
        fake.Silent=true;
        core.Allow(null);
        core.Step();
        Assert.Equal(1,fake.Opens);
        Assert.Equal(1,fake.Closes);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("silent",core.Snapshot.Reason);
        Assert.Equal(1,LogCount("hub: let go (the hub stopped answering)"));
        var opened=new List<int>{0};
        for(int t=5;t<=2770;t+=5)
        {
            To(t);
            if(t==2700) fake.Silent=false;
            core.Recheck();core.Allow("icue");core.Allow(null);
            int opens=fake.Opens;
            core.Step();
            if(fake.Opens==opens) continue;
            opened.Add(t);
            if(t==2730){Assert.Equal("driving",core.Snapshot.State);fake.Silent=true;}
        }
        // Gaps 30, 60, 120, 240, 480, then 600 and 600; a good take at 2730 s, its reading fails at 2735 s, and the next gap is 30 s again.
        Assert.Equal(new[]{0,30,90,210,450,930,1530,2130,2730,2765},opened);
        Assert.Equal("silent",core.Snapshot.Reason);
        Settled();
    }

    [Fact]
    public void Resume_ends_a_retry_wait()
    {
        fake.Silent=true;
        core.Allow(null);
        At(0);
        Assert.Equal(1,fake.Opens);
        fake.Silent=false;
        At(4);
        Assert.Equal(1,fake.Opens);
        To(5);
        core.Resume();
        core.Step();
        Assert.Equal(2,fake.Opens);
        Assert.Equal("driving",core.Snapshot.State);
        Settled();
    }

    [Theory]
    [InlineData("type")]
    [InlineData("short")]
    [InlineData("status")]
    public void A_wrong_data_type_or_a_short_reply_is_a_failure(string what)
    {
        fake.Reply=(command,reply)=>
        {
            if(command[0]!=0x08||reply[4]!=0x25) return reply;              // only the speeds read
            if(what=="type"){reply[4]=0x10;return reply;}                    // temperatures' data type: never data for 17
            if(what=="short") return reply[..20];                            // 26 entries do not fit
            reply[3]=0x01;return reply;                                      // a status neither 00 nor 03
        };
        core.Allow(null);
        core.Step();
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("silent",core.Snapshot.Reason);
        Assert.Equal(1,fake.Closes);
        Assert.Equal(0,fake.Count(0x01,0x03,0x00,0x01));
        Settled();
    }

    // Lighting 5 (extra, Task 3's ruling): a Read() reply with status 03 is wrong mode (software mode again and the transaction repeated), neither
    // data nor a failure.
    [Fact]
    public void A_read_answering_03_is_wrong_mode()
    {
        Take();
        int once=0;
        fake.Reply=(command,reply)=>{if(command[0]==0x08&&reply[4]==0x25&&once++==0) reply[3]=0x03;return reply;};
        At(3);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(1,LogCount("hub fell back to its own mode: entering software mode again (1 of 3)"));
        Assert.Equal(2774,Pump().Rpm);
        Settled();
    }

    [Theory]
    [InlineData(32,"driving")]
    [InlineData(33,"letGo")]
    public void More_than_16_unmatched_replies_fail(int stale,string state)
    {
        fake.Stale=stale;                                                    // 16 are drained before the first write; the rest come before its reply
        core.Allow(null);
        core.Step();
        Assert.Equal(state,core.Snapshot.State);
        Settled();
    }

    [Fact]
    public void Stale_reports_are_drained_first()
    {
        fake.Stale=3;
        Take();
        Assert.Equal(new[]{1,1,1,1,250},fake.Limits.Take(5));
        Assert.Equal(2774,Pump().Rpm);
        Settled();
    }

    [Theory]
    [InlineData(180,"driving")]                                              // eight reads to a read transaction: 1.44 s
    [InlineData(400,"letGo")]                                                // 1.6 s at the open's reply
    public void A_transaction_longer_than_1_5_s_is_a_failure(int latencyMs,string state)
    {
        Take();
        fake.Latency=TimeSpan.FromMilliseconds(latencyMs);
        At(3);
        Assert.Equal(state,core.Snapshot.State);
        if(state=="letGo"){Assert.Equal("silent",core.Snapshot.Reason);Assert.Equal(1,fake.Closes);}
        Settled();
    }

    [Fact]
    public void An_unexpected_exception_releases_the_lock_and_lets_go()
    {
        Take();
        To(3);
        fake.ThrowOnRead=new InvalidOperationException("a read that went wrong");
        Assert.Null(Record.Exception(()=>core.Step()));
        Assert.False(corsairLock.Held);
        Assert.Equal(corsairLock.Enters,corsairLock.Exits);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("silent",core.Snapshot.Reason);
        Assert.Equal(1,fake.Closes);
        Settled();
    }

    [Fact]
    public void Wrong_mode_enters_software_mode_again_three_times_and_lets_go_on_the_fourth()
    {
        Take();
        int before=fake.Writes.Count;
        fake.WrongMode=1;
        At(3);
        Assert.Equal(new[]{"05 01 01 21","0D 01 21","01 03 00 02","05 01 01 21","0D 01 21","08 01","05 01 01 21","05 01 01 17","0D 01 17","08 01","05 01 01 17"},
            Commands().Skip(before));
        Assert.Equal(1,LogCount("hub fell back to its own mode: entering software mode again (1 of 3)"));
        fake.WrongMode=1;At(6);
        fake.WrongMode=1;At(9);
        Assert.Equal(1,LogCount("hub fell back to its own mode: entering software mode again (2 of 3)"));
        Assert.Equal(1,LogCount("hub fell back to its own mode: entering software mode again (3 of 3)"));
        Assert.Equal("driving",core.Snapshot.State);
        fake.WrongMode=1;At(12);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("fallsBack",core.Snapshot.Reason);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal(1,fake.Closes);
        At(41);
        Assert.Equal(1,fake.Opens);                                          // the retry is armed: 30 s after 12 s
        At(42);
        Assert.Equal("driving",core.Snapshot.State);
        fake.WrongMode=1;
        At(3+600+1);                                                          // 10 min 1 s after the first
        Assert.Equal(2,LogCount("hub fell back to its own mode: entering software mode again (1 of 3)"));
        Assert.Equal("driving",core.Snapshot.State);
        Settled();
    }

    // Lighting 5 (extra): the take counts as holding from software mode on, so a 03 after it is wrong mode there too, inside the take's one unit.
    [Fact]
    public void Wrong_mode_during_the_take_enters_software_mode_again()
    {
        fake.WrongMode=2;                                                    // the probe's open, then the device list's
        Take();
        Assert.Equal(1,LogCount("hub was in its own mode"));
        Assert.Equal(1,LogCount("hub fell back to its own mode: entering software mode again (1 of 3)"));
        Assert.Equal(2,fake.Count(0x01,0x03,0x00,0x02));
        Assert.Equal(1,corsairLock.Enters);
        Settled();
    }

    [Fact]
    public void Let_go_sends_hardware_mode_once_then_closes()
    {
        Take();
        core.Allow("noLighting");
        var done=core.LetGoAsync();
        Assert.False(done.IsCompleted);
        At(1);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal(1,fake.Closes);
        Assert.True(done.IsCompleted);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("noLighting",core.Snapshot.Reason);
        Assert.Equal(0,core.Snapshot.SoftwareAt);
        At(2);At(10);
        Assert.Equal(1,fake.Count(0x01,0x03,0x00,0x01));
        Assert.Equal(1,LogCount("hub: hardware mode sent"));
        Assert.Equal(1,LogCount("hub: let go (lighting has stopped)"));
        Settled();
    }

    [Fact]
    public void Let_go_when_not_held_touches_nothing()
    {
        var done=core.LetGoAsync();
        Assert.False(done.IsCompleted);
        core.Step();
        Assert.True(done.IsCompleted);
        Assert.Empty(fake.Writes);
        Assert.Equal(0,fake.Opens);
        Settled();
    }

    [Fact]
    public void A_failed_hardware_mode_still_closes()
    {
        Take();
        fake.Silent=true;
        core.Allow("icue");
        At(1);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal(1,fake.Closes);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("icue",core.Snapshot.Reason);
        Assert.Equal(1,LogCount("hub: hardware mode not sent: the hub stopped answering"));
        Settled();
    }

    [Fact]
    public async Task A_let_go_during_a_take_waits_for_it_and_hands_back()
    {
        fake.ReadEntered=entered;
        fake.Reply=(command,reply)=>{if(Hex(command)=="01 03 00 02") fake.HangRead=hang;return reply;};
        core.Allow(null);
        var taking=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));                      // software mode is sent; its reply is held
        core.Allow("noLighting");
        var done=core.LetGoAsync();
        Assert.False(done.IsCompleted);
        fake.Reply=null;
        hang.Set();
        await taking.WaitAsync(Waits.RealTimeCap);
        Assert.False(done.IsCompleted);                                      // the take finished; the next step hands back
        Assert.Equal(0,fake.Count(0x01,0x03,0x00,0x01));
        core.Step();
        Assert.Equal("01 03 00 01",Last());
        Assert.True(done.IsCompleted);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("noLighting",core.Snapshot.Reason);
        Settled();
    }

    [Fact]
    public void Forbidding_lets_go_with_its_reason_and_allowing_takes_again_at_once()
    {
        Take();
        core.Allow("icue");
        At(1);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("icue",core.Snapshot.Reason);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal(1,LogCount("hub: let go (iCUE is running)"));
        core.Allow(null);
        At(2);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(1,LogCount("hub taken again"));
        Assert.Equal(2,fake.Opens);
        Settled();
    }

    [Fact]
    public void No_hub_and_two_hubs_wait_for_a_recheck()
    {
        fake.Hubs=0;
        core.Allow(null);
        At(0);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("noHub",core.Snapshot.Reason);
        for(int t=30;t<=600;t+=30) At(t);
        Assert.Equal(0,fake.Opens);
        fake.Hubs=1;
        core.Recheck();
        At(630);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(1,LogCount("hub appeared"));
        fake.Hubs=2;
        core.Allow("off");At(631);
        core.Allow(null);At(632);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("twoHubs",core.Snapshot.Reason);
        Assert.Equal(1,LogCount("more than one hub listed"));
        for(int t=660;t<=1260;t+=30) At(t);
        core.Recheck();At(1290);
        Assert.Equal(1,fake.Opens);
        Assert.Equal("twoHubs",core.Snapshot.Reason);
        Assert.Equal(1,LogCount("more than one hub listed"));
        Settled();
    }

    [Fact]
    public void An_unknown_part_hands_back_and_waits()
    {
        fake.Parts[5]=(0x1A,0x01);
        core.Allow(null);
        At(0);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("unknownPart",core.Snapshot.Reason);
        Assert.Equal("type 0x1A, model 0x01",core.Snapshot.Detail);
        Assert.Equal(0,fake.Count(0x06,0x00));
        Assert.Equal(1,LogCount("hub unsupported: the hub has a part DeadWare doesn't know (type 0x1A, model 0x01)"));
        for(int t=30;t<=600;t+=30){core.Recheck();At(t);}
        Assert.Equal(1,fake.Opens);
        core.Resume();At(601);
        Assert.Equal(2,fake.Opens);
        Assert.Equal("unknownPart",core.Snapshot.Reason);
        // A Recheck that finds the hub after it was missing ends the hold too.
        fake.Hubs=0;core.Recheck();At(602);
        fake.Parts.Remove(5);
        fake.Hubs=1;core.Recheck();At(603);
        Assert.Equal(3,fake.Opens);
        Assert.Equal("driving",core.Snapshot.State);
        Settled();
    }

    [Fact]
    public void A_lock_that_cannot_be_made_is_never_retried()
    {
        corsairLock.Problem="the Corsair lock could not be made";
        core.Allow(null);
        At(0);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("noLock",core.Snapshot.Reason);
        for(int t=60;t<=1200;t+=60)
        {
            core.Recheck();core.Allow("icue");core.Allow(null);
            if(t==600) core.Resume();
            At(t);
        }
        Assert.Equal(0,fake.Opens);
        Assert.Empty(fake.Writes);
        Assert.Single(Lines(),l=>l.StartsWith("hub: let go ("));
        Settled();
    }

    [Fact]
    public void The_dialogs_reading_sends_no_mode_command()
    {
        core.RequestReading();
        core.Step();
        Assert.Equal(new[]{"02 13","05 01 01 36","0D 01 36","08 01","05 01 01 36","05 01 01 21","0D 01 21","08 01","05 01 01 21",
            "05 01 01 17","0D 01 17","08 01","05 01 01 17"},Commands());
        Assert.DoesNotContain(Commands(),c=>c.StartsWith("01"));
        Assert.Equal(1,fake.Opens);
        Assert.Equal(1,fake.Closes);
        Assert.Equal(1,corsairLock.Enters);
        var s=core.Snapshot;
        Assert.True(s.Read!.Ok);
        Assert.Equal(time.GetUtcNow(),s.Read.At);
        var pump=s.Read.Readings!.Parts.Single(p=>p.Kind=="pump");
        Assert.Equal(43.7,pump.Celsius);
        Assert.Equal(2774,pump.Rpm);
        Assert.Equal("off",s.State);
        Assert.Equal(4,s.Parts!.Count);
        Assert.Null(s.Readings);
        Assert.Equal(0,s.ReadingsAt);
        Assert.Equal(1,LogCount("hub reading: coolant 43.7 °C, pump 2,774 RPM, fans 1,660 · 1,654 · 1,663 RPM"));
        Settled();
    }

    [Fact]
    public void A_reading_of_a_hub_nobody_holds_could_not_read()
    {
        fake.Software=false;
        core.RequestReading();
        core.Step();
        Assert.False(core.Snapshot.Read!.Ok);
        Assert.Equal(0,fake.Count(0x01,0x03));
        Assert.False(fake.Software);
        Assert.Equal(1,fake.Closes);
        Assert.Equal(1,LogCount("hub reading: could not read (nobody holds the hub)"));
        Settled();
    }

    [Theory]
    [InlineData(70,true)]                                                    // 26 reads: 1.82 s
    [InlineData(300,false)]
    public void A_reading_slower_than_2_s_could_not_read(int latencyMs,bool ok)
    {
        fake.Latency=TimeSpan.FromMilliseconds(latencyMs);
        core.RequestReading();
        core.Step();
        Assert.Equal(ok,core.Snapshot.Read!.Ok);
        Assert.Equal(1,fake.Closes);
        Settled();
    }

    [Fact]
    public void A_reading_of_an_unknown_part_carries_the_blocker()
    {
        fake.Parts[5]=(0x1A,0x01);
        core.RequestReading();
        core.Step();
        var read=core.Snapshot.Read!;
        Assert.True(read.Ok);
        Assert.Equal("unknownPart",read.Blocker);
        Assert.Equal("type 0x1A, model 0x01",read.Detail);
        Assert.Equal(0,fake.Count(0x01,0x03));
        Assert.Equal(0,fake.Count(0x0D,0x01,0x21));                        // a hub DeadWare would refuse is not read further
        Assert.Empty(core.Snapshot.Parts!);
        Assert.Equal(1,LogCount("hub reading: the hub has a part DeadWare doesn't know (type 0x1A, model 0x01)"));
        Settled();
    }

    // Lighting 5 (extra): the blockers that come from Count().
    [Theory]
    [InlineData(0,"noHub")]
    [InlineData(2,"twoHubs")]
    public void A_reading_without_exactly_one_hub_carries_why(int hubs,string blocker)
    {
        fake.Hubs=hubs;
        core.RequestReading();
        core.Step();
        Assert.Equal(blocker,core.Snapshot.Read!.Blocker);
        Assert.Equal(0,fake.Opens);
        Settled();
    }

    // Lighting 5 (extra): a busy lock skips the reading's round, and it is tried again inside the 2 s since the request.
    [Fact]
    public void A_reading_is_tried_again_while_the_lock_is_busy_for_2_s()
    {
        corsairLock.Busy=true;
        core.RequestReading();
        At(0);
        At(1);
        Assert.Null(core.Snapshot.Read);
        Assert.Equal(2,fake.Opens);
        Assert.Equal(2,fake.Closes);
        corsairLock.Busy=false;
        At(1.5);
        Assert.True(core.Snapshot.Read!.Ok);
        corsairLock.Busy=true;
        To(2);
        core.RequestReading();
        core.Step();
        At(3.999);
        Assert.True(core.Snapshot.Read!.Ok);                                 // still the one of 1.5 s
        At(4);
        Assert.False(core.Snapshot.Read!.Ok);
        Assert.Equal(1,LogCount("hub reading: could not read (the Corsair lock stayed busy)"));
        Settled();
    }

    // Fix round 1, M1: the 2 s run from the request, also through the unit itself: busy until 1.75 s, then a unit of 0.52 s could not read.
    [Fact]
    public void A_reading_ends_2_s_after_its_request_however_long_the_lock_was_busy()
    {
        corsairLock.Busy=true;
        core.RequestReading();
        for(int k=0;k<=6;k++) At(0.25*k);
        Assert.Null(core.Snapshot.Read);
        corsairLock.Busy=false;
        fake.Latency=TimeSpan.FromMilliseconds(20);                          // 26 reads: 0.52 s
        At(1.75);
        Assert.False(core.Snapshot.Read!.Ok);
        Assert.Equal(8,fake.Opens);                                          // seven busy tries and the slow one
        Assert.Equal(fake.Opens,fake.Closes);
        Settled();
    }

    [Fact]
    public void Four_stalled_pump_readings_hand_back_and_three_do_not()
    {
        Take();
        fake.Rpm[13]=0;
        At(3);At(6);At(9);
        fake.Rpm[13]=2774;
        At(12);
        Assert.Equal("driving",core.Snapshot.State);
        fake.Rpm[13]=0;
        At(15);At(18);At(21);
        Assert.Equal("driving",core.Snapshot.State);
        At(24);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.True(core.Snapshot.PumpStopped);
        Assert.Equal(1,LogCount("hub: the pump read 0 RPM for 4 readings"));
        fake.Rpm[13]=2774;
        core.Allow(null);
        for(int t=25;t<=120;t+=5) At(t);
        Assert.Equal(1,fake.Opens);
        core.Allow("off");At(121);
        Assert.True(core.Snapshot.PumpStopped);
        core.Allow(null);At(122);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.False(core.Snapshot.PumpStopped);
        Assert.Equal(2,fake.Opens);
        Settled();
    }

    [Fact]
    public void A_pump_without_a_reading_counts_as_stalled()
    {
        fake.Rpm[13]=null;
        Take();                                                              // the take's reading is the first of four
        At(3);At(6);
        Assert.Equal("driving",core.Snapshot.State);
        At(9);
        Assert.True(core.Snapshot.PumpStopped);
        Assert.Equal("01 03 00 01",Last());
        Assert.Equal(1,LogCount("hub: channel 13 has no speed reading"));  // fix round 1, M5: not the dialog's "hub reading:" prefix
        Settled();
    }

    // Fix round 1, M2: only a forbid after the stop counts; a forbid and an allow that arrive while the stop's own step runs do not end the hold.
    [Fact]
    public void A_forbid_and_allow_during_the_stop_do_not_end_the_pump_hold()
    {
        fake.Rpm[13]=0;
        Take();
        At(3);At(6);
        int reads=0;
        fake.Reply=(command,reply)=>{if(command[0]==0x08&&reply[4]==0x25&&++reads==1){core.Allow("off");core.Allow(null);}return reply;};
        At(9);
        fake.Reply=null;
        Assert.True(core.Snapshot.PumpStopped);
        At(10);
        Assert.True(core.Snapshot.PumpStopped);
        Assert.Equal(1,fake.Opens);
        Settled();
    }

    [Fact]
    public void Fans_at_zero_do_not_stop_it()
    {
        Take();
        fake.Rpm[1]=0;fake.Rpm[2]=0;fake.Rpm[3]=0;
        for(int t=3;t<=15;t+=3) At(t);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.False(core.Snapshot.PumpStopped);
        Assert.Equal(new int?[]{0,0,0},Fans());
        Settled();
    }

    [Fact]
    public async Task Abort_from_another_thread_ends_a_blocked_read()
    {
        Take();
        fake.HangRead=hang;fake.ReadEntered=entered;
        To(3);
        var stepping=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));
        Assert.NotEqual(0L,core.BusySince);
        core.Abort();
        await stepping.WaitAsync(Waits.RealTimeCap);
        Assert.Equal("letGo",core.Snapshot.State);
        Assert.Equal("silent",core.Snapshot.Reason);
        Assert.False(corsairLock.Held);
        Assert.Equal(0L,core.BusySince);
        Assert.Equal(1,LogCount("hub: a USB call was stuck; its handle was closed"));
        core.Abort();
        Assert.Equal(1,LogCount("hub: a USB call was stuck; its handle was closed"));
        Assert.Equal(1,fake.Closes);
        Settled();
    }

    [Fact]
    public async Task Calls_return_while_a_step_is_blocked()
    {
        Take();
        fake.HangRead=hang;fake.ReadEntered=entered;
        To(3);
        var stepping=Task.Run(()=>core.Step());
        Assert.True(entered.Wait(Waits.RealTimeCap));
        static void Quick(Action a)=>Assert.True(Task.Run(a).Wait(Waits.RealTimeCap));
        Quick(()=>core.Post(Four));
        Quick(()=>core.SetDark(true));
        Quick(()=>core.Allow("icue"));
        Quick(()=>core.Quiet());
        Quick(()=>core.Recheck());
        Quick(()=>core.RequestReading());
        Quick(()=>_=core.Snapshot);
        Task? done=null;
        Quick(()=>done=core.LetGoAsync());
        Assert.False(done!.IsCompleted);
        Assert.False(stepping.IsCompleted);
        core.Abort();
        await stepping.WaitAsync(Waits.RealTimeCap);
        core.Step();
        Assert.True(done.IsCompleted);
        Settled();
    }

    [Fact]
    public void Step_returns_the_wait_to_the_next_due_work()
    {
        Assert.Equal(TimeSpan.FromSeconds(5),core.Step());                  // nobody allowed: idle
        core.Allow(null);
        Assert.Equal(TimeSpan.FromSeconds(3),core.Step());                  // taken, no frame: the next reading
        core.Post(Four);
        Assert.Equal(TimeSpan.FromSeconds(2),At(0.5));                      // the frame's keep-alive at 2.5 s comes before the reading at 3 s
        Assert.Equal(TimeSpan.FromMilliseconds(500),At(2.5));               // kept alive; the reading at 3 s is next
        fake.Silent=true;
        Assert.Equal(TimeSpan.FromSeconds(30),At(3));                       // the reading failed: the retry
        Assert.Equal(TimeSpan.FromSeconds(29),At(4));
        Settled();
    }

    // A clock that can be made to throw: the one way to make the core fault outside every unit.
    sealed class FaultyClock:FakeTimeProvider{public volatile bool Fail;public override DateTimeOffset GetUtcNow()=>Fail?throw new InvalidOperationException("a clock fault"):base.GetUtcNow();}

    // Fix round 1, M6: a fault of the core's own outside every unit (here the clock, while a quiet step answers the dialog's reading) still
    // completes the let-go tasks that step took.
    [Fact]
    public void A_fault_outside_the_units_still_completes_the_let_go_tasks()
    {
        var clock=new FaultyClock();
        var faulty=new HubCore(fake,corsairLock,clock,log);
        faulty.Quiet();
        faulty.RequestReading();
        var done=faulty.LetGoAsync();
        clock.Fail=true;
        Assert.Null(Record.Exception(()=>faulty.Step()));
        Assert.True(done.IsCompleted);
        Assert.Equal(1,LogCount("hub: a step failed (InvalidOperationException)"));
        Assert.Empty(fake.Writes);
        Settled();
    }

    // Lighting 5 (extra): every call only stores and nudges; none touches the hub.
    [Fact]
    public void Every_call_nudges_and_touches_nothing()
    {
        int nudges=0;
        core.Nudge=()=>nudges++;
        core.Post(Four);core.SetDark(true);core.Allow(null);core.Quiet();core.Resume();core.Recheck();core.RequestReading();_=core.LetGoAsync();
        Assert.Equal(8,nudges);
        Assert.Empty(fake.Writes);
        Assert.Equal(0,fake.Opens);
        Settled();
    }
}
