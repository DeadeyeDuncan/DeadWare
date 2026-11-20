using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class RestartBackoffTests
{
    // A clock moved by hand. Its timestamps tick at the given frequency, and its wall time is set apart from them, as the system clock
    // can step while the monotonic clock runs on.
    sealed class ManualClock(long frequency=TimeSpan.TicksPerSecond):TimeProvider
    {
        long now=1_000_000;
        public DateTimeOffset Wall=new(2026,1,1,0,0,0,TimeSpan.Zero);
        public override long GetTimestamp()=>now;
        public override long TimestampFrequency=>frequency;
        public override DateTimeOffset GetUtcNow()=>Wall;
        public ManualClock Advance(TimeSpan d){now+=(long)((Int128)d.Ticks*frequency/TimeSpan.TicksPerSecond);Wall+=d;return this;}
    }
    static TimeSpan? Fail(RestartBackoff b,ManualClock c,TimeSpan after)=>b.RecordFailure(c.Advance(after));
    static readonly TimeSpan Second=TimeSpan.FromSeconds(1);

    [Fact] public void Delays_double_from_four_seconds()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        var d=Enumerable.Range(0,4).Select(_=>Fail(b,c,Second)!.Value.TotalSeconds).ToArray();
        Assert.Equal(new[]{4.0,8,16,32},d);
    }
    [Fact] public void Fifth_failure_within_ten_minutes_gives_up()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        for(int i=0;i<4;i++) Assert.NotNull(Fail(b,c,Second));
        Assert.Null(Fail(b,c,Second));
    }
    [Fact] public void Failures_older_than_ten_minutes_are_forgotten()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        for(int i=0;i<3;i++) Fail(b,c,Second);
        Assert.Equal(TimeSpan.FromSeconds(4),Fail(b,c,TimeSpan.FromMinutes(11)));
    }
    [Fact] public void Delay_never_exceeds_cap()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        for(int i=0;i<RestartBackoff.MaxFailures-1;i++) Assert.True(Fail(b,c,Second)<=RestartBackoff.Cap);
    }
    [Fact] public void Delay_never_below_four_seconds()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        for(int i=0;i<RestartBackoff.MaxFailures-1;i++) Assert.True(Fail(b,c,Second)>=TimeSpan.FromSeconds(4));
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(4),Fail(b,c,TimeSpan.FromSeconds(10)));
    }

    // F6 item 4 (whole-branch review D4): the window runs on the monotonic clock. A wall-clock step back of an hour doesn't keep four
    // failures counted past ten minutes, and a step forward of an hour doesn't drop them early.
    [Fact] public void A_wall_clock_step_neither_stretches_nor_shortens_the_window()
    {
        var b=new RestartBackoff();var c=new ManualClock();
        for(int i=0;i<4;i++) Fail(b,c,Second);
        c.Wall-=TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromSeconds(4),Fail(b,c,TimeSpan.FromMinutes(11)));
        b.Reset();
        for(int i=0;i<4;i++) Fail(b,c,Second);
        c.Wall+=TimeSpan.FromHours(1);
        Assert.Null(Fail(b,c,Second));
    }

    // F6 item 5 (whole-branch review D5): at a timestamp frequency other than TimeSpan's (3 MHz here), ten minutes are still ten minutes:
    // failures 11 minutes old are forgotten, and ones 9 minutes old still count.
    [Fact] public void The_window_holds_at_another_timestamp_frequency()
    {
        var b=new RestartBackoff();var c=new ManualClock(3_000_000);
        for(int i=0;i<3;i++) Fail(b,c,Second);
        Assert.Equal(TimeSpan.FromSeconds(4),Fail(b,c,TimeSpan.FromMinutes(11)));
        b.Reset();
        for(int i=0;i<4;i++) Fail(b,c,Second);
        Assert.Null(Fail(b,c,TimeSpan.FromMinutes(9)));
    }
}
