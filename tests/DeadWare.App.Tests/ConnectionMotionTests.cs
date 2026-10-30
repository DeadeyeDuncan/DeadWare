using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class ConnectionMotionTests
{
    [Fact]
    public void Quiet_traffic_eases_brightness_and_speed_then_stops_at_its_current_phase()
    {
        var motion=new ConnectionMotion(true);motion.Advance(1);
        motion.Active=false;
        Assert.Equal(1,motion.Level);Assert.Equal(1,motion.Time);
        motion.Advance(.4);Assert.InRange(motion.Level,.2,.6);Assert.InRange(motion.Time,1.1,1.4);
        double early=motion.Level;
        motion.Advance(.4);Assert.True(motion.Level<early);Assert.True(motion.Moving);
        motion.Advance(3);Assert.Equal(0,motion.Level);Assert.False(motion.Moving);
        double stopped=motion.Time;motion.Advance(10);Assert.Equal(stopped,motion.Time);
    }

    [Fact]
    public void Returning_traffic_reverses_the_fade_without_resetting_phase_or_brightness()
    {
        var motion=new ConnectionMotion(true);motion.Advance(2);motion.Active=false;motion.Advance(.3);
        double level=motion.Level,time=motion.Time;
        motion.Active=true;Assert.Equal(level,motion.Level);Assert.Equal(time,motion.Time);
        motion.Advance(.1);Assert.InRange(motion.Level,level,1);Assert.InRange(motion.Time,time,time+.1);
        motion.Advance(2);Assert.Equal(1,motion.Level);Assert.True(motion.Moving);
    }

    [Fact]
    public void Quiet_initial_data_and_invalid_elapsed_times_do_not_animate()
    {
        var motion=new ConnectionMotion(false);
        foreach(double elapsed in new[]{double.NaN,double.PositiveInfinity,-1,0,10})motion.Advance(elapsed);
        Assert.Equal(0,motion.Time);Assert.Equal(0,motion.Level);Assert.False(motion.Moving);
    }

    [Fact]
    public void Easing_is_consistent_across_frame_rates()
    {
        var slow=new ConnectionMotion(true){Active=false};var fast=new ConnectionMotion(true){Active=false};
        for(int i=0;i<12;i++)slow.Advance(.1);
        for(int i=0;i<120;i++)fast.Advance(.01);
        Assert.Equal(slow.Level,fast.Level,10);Assert.Equal(slow.Time,fast.Time,10);
    }
}
