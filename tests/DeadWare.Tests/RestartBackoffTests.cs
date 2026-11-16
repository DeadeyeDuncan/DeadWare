using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class RestartBackoffTests
{
    static readonly DateTimeOffset T0=new(2026,1,1,0,0,0,TimeSpan.Zero);
    [Fact] public void Delays_double_from_four_seconds()
    {
        var b=new RestartBackoff();
        var d=Enumerable.Range(0,4).Select(i=>b.RecordFailure(T0.AddSeconds(i))!.Value.TotalSeconds).ToArray();
        Assert.Equal(new[]{4.0,8,16,32},d);
    }
    [Fact] public void Fifth_failure_within_ten_minutes_gives_up()
    {
        var b=new RestartBackoff();
        for(int i=0;i<4;i++) Assert.NotNull(b.RecordFailure(T0.AddSeconds(i)));
        Assert.Null(b.RecordFailure(T0.AddSeconds(4)));
    }
    [Fact] public void Failures_older_than_ten_minutes_are_forgotten()
    {
        var b=new RestartBackoff();
        for(int i=0;i<3;i++) b.RecordFailure(T0.AddSeconds(i));
        Assert.Equal(TimeSpan.FromSeconds(4),b.RecordFailure(T0.AddMinutes(11)));
    }
    [Fact] public void Delay_never_exceeds_cap()
    {
        var b=new RestartBackoff();
        for(int i=0;i<RestartBackoff.MaxFailures-1;i++) Assert.True(b.RecordFailure(T0.AddSeconds(i))<=RestartBackoff.Cap);
    }
    [Fact] public void Delay_never_below_four_seconds()
    {
        var b=new RestartBackoff();
        for(int i=0;i<RestartBackoff.MaxFailures-1;i++) Assert.True(b.RecordFailure(T0.AddSeconds(i))>=TimeSpan.FromSeconds(4));
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(4),b.RecordFailure(T0.AddSeconds(10)));
    }
}
