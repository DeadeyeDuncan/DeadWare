using System.Diagnostics;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 4: the Aerox receiver driver against a fake transport, ported from DeadLight's tests/aerox-wireless.test.ts and
// tests/aerox-battery.test.ts. Offsets are DeadLight's: a written packet has the report id at [0] and the opcode at [1]; a reply starts at
// the opcode.
public sealed class AeroxDriverTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-aerox-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    readonly FakeAeroxTransport fake=new();
    static readonly Rgb[] Four=[new(1,2,3),new(1,2,3),new(1,2,3),new(1,2,3)];

    public AeroxDriverTests(){log=new HostLog(Path.Combine(dir,"logs"));fake.Products.Add(AeroxDriver.Receiver);}

    public void Dispose()
    {
        fake.HangWrite?.Set();
        log.Dispose();
        try{Directory.Delete(dir,true);}catch{}
    }

    AeroxDriver Driver(TimeSpan? deadline=null){var d=new AeroxDriver(fake,log,deadline);d.Scan();return d;}
    string LogText(){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllText(p):"";}
    int LogCount(string text)=>LogText().Split(text).Length-1;
    static byte[] Head(byte[] p,int n)=>p.Take(n).ToArray();

    [Fact]
    public void It_writes_brightness_then_four_zones_with_wireless_opcodes()
    {
        using var d=Driver();
        var c=new Rgb(2,30,140);
        Assert.Equal(AeroxResult.Sent,d.Paint([c,c,c,c],null));
        var w=fake.Writes;
        Assert.Equal(5,w.Count);
        Assert.Equal([0,0x63,10,0,0,0,0],Head(w[0],7));
        Assert.Equal([0,0x61,1,0,2,30,140],Head(w[1],7));
        Assert.Equal([0,0x61,1,1,2,30,140],Head(w[2],7));
        Assert.Equal([0,0x61,1,2,2,30,140],Head(w[3],7));
        Assert.Equal([0,0x66,1,0,2,30,140],Head(w[4],7));
        Assert.All(w,p=>Assert.Equal(64,p.Length));
        Assert.All(w,p=>Assert.All(p.Skip(7),b=>Assert.Equal(0,b)));
        Assert.Equal(5,fake.Reads);
        Assert.All(fake.Limits,l=>Assert.Equal(AeroxDriver.ReadLimitMs,l));
        Assert.True(d.Answering);
        Assert.Equal(1,LogCount("receiver opened"));
    }

    [Theory]
    [InlineData(5,0xE0,0x93,0x04)]
    [InlineData(0,0,0,0)]
    [InlineData(1,0x60,0xEA,0x00)]
    public void The_sleep_time_goes_first_in_little_endian_milliseconds(int minutes,int b0,int b1,int b2)
    {
        using var d=Driver();
        Assert.False(d.SleepSent(minutes));
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,minutes));
        var w=fake.Writes;
        Assert.Equal([0,0x69,(byte)b0,(byte)b1,(byte)b2],Head(w[0],5));
        Assert.Equal([0,0x51,0],Head(w[1],3));
        Assert.Equal(0x63,w[2][1]);
        Assert.Equal(7,w.Count);
        Assert.True(d.SleepSent(minutes));
        Assert.False(d.SleepSent(minutes+1));
        Assert.Equal(1,LogCount($"mouse sleep time sent: {minutes} min"));
        Assert.Equal(1,LogCount("mouse sleep time saved"));
    }

    [Fact]
    public void The_save_goes_once_per_changed_value()
    {
        using var d=Driver();
        d.Paint(Four,1);d.Paint(Four,1);
        d.Close();
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,1));
        Assert.Equal(1,fake.Count(0x51));
        Assert.Equal(2,fake.Count(0x69));
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,2));
        Assert.Equal(2,fake.Count(0x51));
        Assert.Equal(3,fake.Count(0x69));
    }

    [Fact]
    public void The_sleep_time_is_sent_again_after_a_reopen()
    {
        using var d=Driver();
        d.Paint(Four,5);d.Paint(Four,5);
        Assert.Equal(1,fake.Count(0x69));
        d.Close();
        Assert.False(d.SleepSent(5));
        Assert.Equal(1,LogCount("receiver closed"));
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,5));
        Assert.Equal(2,fake.Count(0x69));
        Assert.Equal(2,fake.Opens);
        Assert.Equal(1,fake.Closes);
        Assert.True(d.SleepSent(5));
    }

    [Fact]
    public void A_missing_save_reply_is_accepted()
    {
        using var d=Driver();
        fake.Reply=p=>p[1]==0x51?null:[p[1]];
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,1));
        Assert.Equal(7,fake.Writes.Count);
        Assert.True(d.SleepSent(1));
        Assert.True(d.Answering);
        Assert.Equal(0,LogCount("no reply"));
    }

    [Fact]
    public void Null_sleep_minutes_sends_no_sleep_time()
    {
        using var d=Driver();
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,null));
        Assert.Equal(0,fake.Count(0x69));
        Assert.Equal(0,fake.Count(0x51));
        Assert.Equal(5,fake.Writes.Count);
    }

    [Fact]
    public void A_cable_only_mouse_is_not_painted()
    {
        fake.Products.Clear();fake.Products.Add(AeroxDriver.Cable);
        using var d=Driver();
        Assert.False(d.ReceiverPresent);
        Assert.True(d.CablePresent);
        Assert.Equal(AeroxResult.Absent,d.Paint(Four,5));
        Assert.Equal(0,fake.Opens);
        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void No_reply_keeps_the_port_open_and_is_not_a_failure()
    {
        using var d=Driver();
        fake.Reply=_=>null;
        for(var i=0;i<3;i++) Assert.Equal(AeroxResult.NoReply,d.Paint(Four,5));
        Assert.Equal(1,fake.Opens);
        Assert.Equal(0,fake.Closes);
        Assert.Equal(3,fake.Writes.Count);           // a silent mouse costs one write per attempt, not a whole frame
        Assert.False(d.Answering);
        Assert.False(d.SleepSent(5));
        Assert.Equal(1,LogCount("receiver: no reply (asleep or out of reach)"));
        Assert.Equal(0,LogCount("receiver failed"));
        fake.Reply=p=>[p[1]];
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,5));
        Assert.True(d.Answering);
        Assert.True(d.SleepSent(5));
        Assert.Equal(1,LogCount("receiver: replying again"));
        Assert.Equal(1,fake.Opens);
    }

    [Fact]
    public void A_colour_write_without_a_reply_ends_the_frame_unrecorded()
    {
        using var d=Driver();
        fake.Reply=p=>p[1]==0x61?null:[p[1]];
        Assert.Equal(AeroxResult.NoReply,d.Paint(Four,5));
        Assert.Equal([0x69,0x51,0x63,0x61],fake.Writes.Select(w=>(int)w[1]).ToArray());
        Assert.False(d.SleepSent(5));                // a frame that ends NoReply is not recorded as sent
        Assert.Equal(0,LogCount("mouse sleep time sent"));
    }

    [Fact]
    public void A_throwing_write_closes_the_port_and_the_next_paint_reopens()
    {
        using var d=Driver();
        fake.ThrowOnWrite=true;
        Assert.Equal(AeroxResult.Failed,d.Paint(Four,5));
        Assert.Equal(1,fake.Closes);
        Assert.False(d.SleepSent(5));
        Assert.Equal(1,LogCount("receiver failed: the device was removed"));
        Assert.Equal(1,LogCount("receiver closed"));
        fake.ThrowOnWrite=false;
        Assert.Equal(AeroxResult.Sent,d.Paint(Four,5));
        Assert.Equal(2,fake.Opens);
        Assert.True(d.SleepSent(5));
    }

    [Fact]
    public async Task A_stuck_write_fails_at_the_deadline_and_later_calls_are_busy()
    {
        using var d=Driver(TimeSpan.FromMilliseconds(100));
        fake.HangWrite=new ManualResetEventSlim(false);
        var clock=Stopwatch.StartNew();
        Assert.Equal(AeroxResult.Failed,d.Paint(Four,null));
        Assert.Equal(AeroxResult.Busy,d.Paint(Four,null));
        Assert.Equal(AeroxResult.Busy,d.Battery(false,out _,out _));
        Assert.Single(fake.Writes);
        Assert.True(clock.Elapsed<TimeSpan.FromSeconds(2),$"took {clock.Elapsed}");
        Assert.Equal(0,fake.Closes);                 // the abandoned port is not disposed on the caller's thread
        Assert.Equal(1,LogCount("receiver failed:"));
        fake.HangWrite.Set();
        await Waits.Until(()=>fake.Closes==1);       // the worker disposes the abandoned port when the write returns
        Assert.Single(fake.Writes);                  // and writes nothing more on it
        fake.HangWrite=null;
        await Waits.Until(()=>d.Paint(Four,null)!=AeroxResult.Busy);
        Assert.Equal(2,fake.Opens);                  // never reused
    }

    [Fact]
    public void An_unplugged_receiver_closes_its_port_on_scan()
    {
        using var d=Driver();
        d.Paint(Four,null);
        fake.Products.Clear();
        d.Scan();
        Assert.False(d.ReceiverPresent);
        Assert.Equal(1,fake.Closes);
        Assert.Equal(1,LogCount("receiver closed"));
        Assert.Equal(AeroxResult.Absent,d.Paint(Four,null));
        Assert.Equal(1,fake.Opens);
    }

    [Theory]
    [InlineData(new byte[]{0xD2,16},false,75,false)]
    [InlineData(new byte[]{0xD2,0x95},false,100,true)]
    [InlineData(new byte[]{0xD2,1},false,0,false)]
    [InlineData(new byte[]{0x92,0x94},true,95,true)]
    public void Battery_decodes_five_percent_steps_and_charging(byte[] reply,bool cable,int percent,bool charging)
    {
        if(cable){fake.Products.Clear();fake.Products.Add(AeroxDriver.Cable);}
        using var d=Driver();
        fake.Reply=_=>reply;
        Assert.Equal(AeroxResult.Sent,d.Battery(cable,out var p,out var c));
        Assert.Equal(percent,p);
        Assert.Equal(charging,c);
        Assert.Equal([0,cable?(byte)0x92:(byte)0xD2],Head(fake.Writes[0],2));
        Assert.Equal(64,fake.Writes[0].Length);
        Assert.True(AeroxDriver.TryBattery(reply,reply[0],out p,out c));
        Assert.Equal(percent,p);
        Assert.Equal(charging,c);
    }

    public static TheoryData<byte[]?> BadReplies=>new(){null,new byte[]{0xD2},new byte[]{0xD2,0},new byte[]{0xD2,127},new byte[]{0x61,16}};

    [Theory]
    [MemberData(nameof(BadReplies))]
    public void Bad_battery_replies_are_rejected(byte[]? reply)
    {
        Assert.False(AeroxDriver.TryBattery(reply,0xD2,out _,out _));
        using var d=Driver();
        fake.Reply=_=>reply;
        var r=d.Battery(false,out var p,out var c);
        Assert.Equal(reply is null?AeroxResult.NoReply:AeroxResult.Failed,r);
        Assert.Equal(0,p);
        Assert.False(c);
        Assert.Equal(0,fake.Closes);                 // a bad or missing reply is not an I/O fault
        Assert.Single(fake.Writes);                  // never resent
    }

    [Fact]
    public void Cable_battery_skips_lighting_acknowledgements()
    {
        fake.Products.Clear();fake.Products.Add(AeroxDriver.Cable);
        using var d=Driver();
        fake.Queue.Enqueue([0x21,0]);fake.Queue.Enqueue([0x23,0]);fake.Queue.Enqueue([0x92,0x94]);
        Assert.Equal(AeroxResult.Sent,d.Battery(true,out var p,out var c));
        Assert.Equal(95,p);
        Assert.True(c);
        Assert.Single(fake.Writes);
        Assert.Equal(3,fake.Reads);
        Assert.All(fake.Limits,l=>Assert.InRange(l,1,AeroxDriver.ReadLimitMs));
    }

    [Fact]
    public void Battery_skips_at_most_sixteen_replies()
    {
        using var d=Driver();
        fake.Reply=_=>[0x61,0];
        Assert.Equal(AeroxResult.Failed,d.Battery(false,out _,out _));
        Assert.Single(fake.Writes);
        Assert.True(fake.Reads<=1+AeroxDriver.MaxSkipped,$"{fake.Reads} reads");
    }

    [Fact]
    public void Cable_battery_opens_and_closes_a_port_per_reading()
    {
        fake.Products.Add(AeroxDriver.Cable);
        using var d=Driver();
        fake.Reply=p=>[p[1],0x93];
        Assert.Equal(AeroxResult.Sent,d.Battery(true,out var p1,out _));
        Assert.Equal(AeroxResult.Sent,d.Battery(true,out _,out _));
        Assert.Equal(90,p1);
        Assert.Equal([AeroxDriver.Cable,AeroxDriver.Cable],fake.Opened);
        Assert.Equal(2,fake.Closes);
        Assert.Equal(0,LogCount("receiver opened"));
        fake.Products.Remove(AeroxDriver.Cable);d.Scan();
        Assert.Equal(AeroxResult.Absent,d.Battery(true,out _,out _));
        Assert.Equal(2,fake.Opens);
    }

    [Fact]
    public async Task A_battery_read_during_a_paint_is_busy()
    {
        using var d=Driver(TimeSpan.FromSeconds(30));
        fake.HangWrite=new ManualResetEventSlim(false);
        var paint=Task.Run(()=>d.Paint(Four,null));
        await Waits.Until(()=>fake.Writes.Count==1);
        Assert.Equal(AeroxResult.Busy,d.Battery(false,out _,out _));
        Assert.Single(fake.Writes);
        fake.HangWrite.Set();
        Assert.Equal(AeroxResult.Sent,await paint);
        fake.HangWrite=null;
        fake.Reply=p=>[p[1],16];
        Assert.Equal(AeroxResult.Sent,d.Battery(false,out var pct,out _));
        Assert.Equal(75,pct);
    }
}
