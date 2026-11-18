using DeadWare.Core;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// Ruling AO's bound on every OpenRGB call, on a fake clock: the watchdog fires only when the test moves the clock.
public sealed class BoundedSessionTests
{
    readonly FakeOpenRgbSession fake=FakeOpenRgbSession.FromFixture();
    readonly FakeTimeProvider clock=new();
    BoundedSession Bounded(){var s=new BoundedSession(fake,clock);s.Connect();return s;}

    [Fact]
    public async Task Hung_call_is_cut_off_at_the_bound_and_the_session_stays_lost_until_it_reconnects()
    {
        var s=Bounded();
        fake.HangWhen=call=>call=="ReadControllers";
        var read=Task.Run(()=>s.ReadControllers());
        await Until(()=>fake.Hanging);
        clock.Advance(BoundedSession.CallBound-TimeSpan.FromTicks(1));
        await Task.Delay(50);
        Assert.True(fake.Hanging);
        clock.Advance(TimeSpan.FromTicks(1));
        var lost=await Assert.ThrowsAsync<OpenRgbSessionLostException>(()=>read.WaitAsync(RealTimeCap));
        Assert.Contains("didn't answer",lost.Message);
        Assert.Equal(1,fake.Aborts);
        // Lost until a Connect succeeds: later calls fail at once and never reach OpenRGB.
        fake.HangWhen=null;
        var reads=fake.Reads;
        Assert.Throws<OpenRgbSessionLostException>(()=>s.ReadControllers());
        Assert.Throws<OpenRgbSessionLostException>(()=>s.UpdateLeds(0,[Rgb.Black]));
        Assert.Equal(reads,fake.Reads);
        s.Connect();
        Assert.Equal(8,s.ReadControllers().Count);
    }

    [Fact]
    public void Broken_connection_drops_the_session()
    {
        var s=new BoundedSession(fake,clock);
        fake.FailConnect=true;
        var lost=Assert.Throws<OpenRgbSessionLostException>(()=>s.Connect());
        Assert.Contains("fake connect failure",lost.Message);
        Assert.IsType<IOException>(lost.InnerException);
        Assert.Throws<OpenRgbSessionLostException>(()=>s.ReadControllers());
    }

    [Fact]
    public void Device_failure_passes_through_and_keeps_the_session()
    {
        var s=Bounded();
        fake.FailLeds=_=>true;
        Assert.Throws<InvalidOperationException>(()=>s.UpdateLeds(0,[Rgb.Black]));
        fake.FailLeds=null;
        s.UpdateLeds(0,[Rgb.Black]);
        Assert.Single(fake.Leds);
        Assert.Equal(0,fake.Aborts);
    }

    [Fact]
    public async Task Abort_from_another_thread_fails_a_waiting_call_as_a_lost_session()
    {
        var s=Bounded();
        fake.HangWhen=call=>call=="UpdateMode";
        var change=Task.Run(()=>s.UpdateMode(6,1,null));
        await Until(()=>fake.Hanging);
        s.Abort();
        await Assert.ThrowsAsync<OpenRgbSessionLostException>(()=>change.WaitAsync(RealTimeCap));
    }
}
