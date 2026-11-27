using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// Lighting 4, Task 8: the Aerox 9's battery cadence (spec "Battery") against the fake transport and fake time. The receiver is asked 0xD2,
// the cable 0x92; by default the receiver answers 75% not charging and the cable 95% charging.
public sealed class AeroxBatteryTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-battery-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    readonly FakeAeroxTransport fake=new();
    readonly FakeTimeProvider time=new();
    readonly AeroxDriver driver;
    readonly AeroxBattery battery;

    public AeroxBatteryTests()
    {
        log=new HostLog(Path.Combine(dir,"logs"));
        fake.Products.Add(AeroxDriver.Receiver);
        fake.Reply=Good;
        driver=new AeroxDriver(fake,log,RealTimeCap);driver.Scan();   // see DeviceHostMouseTests: the 1.5 s deadline is real time
        battery=new AeroxBattery(driver,time,log);
    }
    public void Dispose(){fake.HangWrite?.Set();driver.Dispose();log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    static byte[]? Good(byte[] w)=>w[1] switch{0xD2=>[0xD2,16],0x92=>[0x92,0x94],_=>[0x61]};
    void Cable(bool on){if(on) fake.Products.Add(AeroxDriver.Cable);else fake.Products.Remove(AeroxDriver.Cable);driver.Scan();}
    void Step(int ms)=>time.Advance(TimeSpan.FromMilliseconds(ms));
    int Receiver=>fake.Count(0xD2);
    int Wired=>fake.Count(0x92);
    int LogCount(string text){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllText(p).Split(text).Length-1:0;}
    void Fresh(int percent,bool charging){Assert.False(battery.Stale);Assert.Equal(percent,battery.Percent);Assert.Equal(charging,battery.Charging);}

    [Fact]
    public void The_receiver_is_asked_only_after_movement()
    {
        Assert.False(battery.Poll(false,true,false));
        Assert.Equal(0,Receiver);
        Assert.Null(battery.Percent);Assert.Null(battery.Charging);Assert.True(battery.Stale);
        Assert.True(battery.Poll(false,true,true));
        Assert.Equal(1,Receiver);
        Fresh(75,false);
    }

    [Fact]
    public void The_receiver_is_asked_at_most_once_a_minute()
    {
        battery.Poll(false,true,true);
        for(var k=0;k<59;k++){Step(1000);battery.Poll(false,true,true);}
        Assert.Equal(1,Receiver);
        Step(1000);battery.Poll(false,true,true);
        Assert.Equal(2,Receiver);
    }

    [Fact]
    public void A_still_mouse_is_never_asked_even_after_a_minute()
    {
        battery.Poll(false,true,true);
        for(var k=0;k<300;k++){Step(1000);battery.Poll(false,true,false);}
        Assert.Equal(1,Receiver);
        Assert.Single(fake.Writes);
    }

    [Fact]
    public void The_cable_is_read_every_ten_seconds_without_movement()
    {
        Cable(true);
        Assert.True(battery.Poll(true,true,false));
        Assert.Equal(1,Wired);
        Fresh(95,true);
        Step(9999);battery.Poll(true,true,false);
        Assert.Equal(1,Wired);
        Step(1);battery.Poll(true,true,false);
        Assert.Equal(2,Wired);
        Assert.Equal(0,Receiver);                                 // never the receiver while the cable is in
        Assert.Equal([AeroxDriver.Cable,AeroxDriver.Cable],fake.Opened);   // a short-lived port per reading
        Assert.Equal(2,fake.Closes);
    }

    [Fact]
    public void A_reading_goes_stale_after_two_minutes_and_keeps_its_value()
    {
        battery.Poll(false,true,true);
        Step(120_000);
        Assert.False(battery.Poll(false,true,false));
        Assert.False(battery.Stale);
        Step(1);
        Assert.True(battery.Poll(false,true,false));             // the published reading changed: now stale
        Assert.True(battery.Stale);
        Assert.Equal(75,battery.Percent);Assert.Equal(false,battery.Charging);
        Assert.False(battery.Poll(false,true,false));
    }

    public static TheoryData<byte[]?> BadReplies=>AeroxDriverTests.BadReplies;

    [Theory]
    [MemberData(nameof(BadReplies))]
    public void A_bad_reply_keeps_the_last_value_as_stale(byte[]? reply)
    {
        battery.Poll(false,true,true);
        Step(60_000);
        fake.Reply=_=>reply;
        Assert.True(battery.Poll(false,true,true));
        Assert.Equal(2,Receiver);
        Assert.True(battery.Stale);
        Assert.Equal(75,battery.Percent);Assert.Equal(false,battery.Charging);
    }

    [Fact]
    public void Plugging_or_unplugging_the_cable_makes_it_stale()
    {
        battery.Poll(false,true,true);
        Fresh(75,false);
        Step(1000);
        Assert.True(battery.Poll(true,true,false));              // plugged, the driver not told yet: nothing to read, but stale at once
        Assert.True(battery.Stale);Assert.Equal(75,battery.Percent);
        Cable(true);
        battery.Poll(true,true,false);                            // read at once, not ten seconds after the receiver's reading
        Assert.Equal(1,Wired);
        Fresh(95,true);
        Step(1000);
        Cable(false);
        Assert.True(battery.Poll(false,true,false));              // unplugged: stale, and a still mouse isn't asked
        Assert.True(battery.Stale);Assert.Equal(95,battery.Percent);Assert.Equal(true,battery.Charging);
        Assert.Equal(1,Receiver);
    }

    [Fact]
    public void A_busy_driver_is_skipped_and_retried_next_turn()
    {
        // Fix wave 1, T-I2: a 1 s deadline, so a thread-pool worker that starts late on a loaded machine still writes before it (the hang is
        // the HangWrite event's, so the deadline's length proves nothing more).
        using var slow=new AeroxDriver(fake,log,TimeSpan.FromSeconds(1));
        fake.Products.Add(AeroxDriver.Cable);slow.Scan();
        var b=new AeroxBattery(slow,time,log);
        b.Poll(true,true,false);
        Assert.Equal(1,Wired);
        Step(10_000);
        var hang=new ManualResetEventSlim(false);
        fake.HangWrite=hang;
        Assert.Equal(AeroxResult.Failed,slow.Paint([Rgb.Black,Rgb.Black,Rgb.Black,Rgb.Black],null));   // its worker still hangs
        Assert.False(b.Poll(true,true,false));                   // busy: skipped, neither a reading nor a failure
        Assert.Equal(1,Wired);
        Assert.False(b.Stale);Assert.Equal(95,b.Percent);
        hang.Set();fake.HangWrite=null;
        // No time passes: each turn while the abandoned worker still runs is skipped again; the first turn after it returns reads.
        Assert.True(SpinWait.SpinUntil(()=>{b.Poll(true,true,false);return Wired==2;},RealTimeCap));
        Assert.Equal(2,Wired);
        Assert.False(b.Stale);
    }

    [Fact]
    public void A_receiver_failure_does_not_spoil_a_cable_reading()
    {
        Cable(true);
        battery.Poll(true,true,false);
        battery.ReceiverFailed();
        Fresh(95,true);
        Step(1000);
        Cable(false);
        battery.Poll(false,true,true);
        Fresh(75,false);
        battery.ReceiverFailed();                                 // a receiver reading does go stale with it
        Assert.True(battery.Stale);Assert.Equal(75,battery.Percent);
    }

    [Fact]
    public void Each_reading_is_logged()
    {
        battery.Poll(false,true,true);
        Assert.Equal(1,LogCount("battery: receiver 75% not charging"));
        Cable(true);
        battery.Poll(true,true,false);
        Assert.Equal(1,LogCount("battery: cable 95% charging"));
        Step(10_000);
        fake.Reply=_=>[0x92,0];
        battery.Poll(true,true,false);                            // an invalid reply is not a reading
        Assert.Equal(2,LogCount("battery: "));
    }

    // Fix wave 2, F: a reading is logged only when its source, percentage or charging state changed since the last logged one, and once after a
    // stale spell ends: five identical cable readings give one line.
    [Fact]
    public void Only_a_changed_reading_is_logged()
    {
        Cable(true);
        for(int k=0;k<5;k++){battery.Poll(true,true,false);Step(10_000);}
        Assert.Equal(5,Wired);
        Assert.Equal(1,LogCount("battery: "));
        fake.Reply=w=>w[1]==0x92?[0x92,0x93]:Good(w);                   // 90% charging: changed
        battery.Poll(true,true,false);
        Assert.Equal(1,LogCount("battery: cable 90% charging"));
        Step(10_000);
        fake.Reply=_=>[0x92,0];
        battery.Poll(true,true,false);                            // invalid: stale
        Assert.True(battery.Stale);
        Step(10_000);
        fake.Reply=w=>w[1]==0x92?[0x92,0x93]:Good(w);
        battery.Poll(true,true,false);                            // the same 90% again, after a stale spell: logged once
        Assert.Equal(2,LogCount("battery: cable 90% charging"));
        Assert.Equal(3,LogCount("battery: "));
    }

    // Ruling R2: the wake's end marks the reading stale and lets the next allowed poll read at once.
    [Fact]
    public void Invalidate_makes_it_stale_and_reads_again_at_once()
    {
        Cable(true);
        battery.Poll(true,true,false);
        Step(1000);
        battery.Invalidate();
        Assert.True(battery.Stale);Assert.Equal(95,battery.Percent);
        battery.Poll(true,true,false);
        Assert.Equal(2,Wired);
        Fresh(95,true);
    }
}
