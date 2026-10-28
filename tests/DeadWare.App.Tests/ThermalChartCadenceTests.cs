using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class ThermalChartCadenceTests
{
    [Fact]
    public void Sparse_minute_windows_connect_while_real_gaps_restarts_and_missing_metrics_remain_separate()
    {
        var type=typeof(Collector).Assembly.GetType("DeadWare.App.ThermalHistoryChart")!;
        var method=type.GetMethod("CanConnect",BindingFlags.Static|BindingFlags.NonPublic)!;
        bool Connect(ThermalWindow first,ThermalWindow second)=>(bool)method.Invoke(null,[first,second,"temperature"])!;
        var first=Window(0);var next=Window(60);
        Assert.True(Connect(first,next));
        Assert.False(Connect(first,Window(80)));
        Assert.False(Connect(first,next with {SessionId="restarted"}));
        Assert.False(Connect(first,next with {Temperature=new(5,60,60,60)}));
        Assert.False(Connect(first,next with {Start=next.Start.AddSeconds(-4),End=next.End.AddSeconds(-4)}));
        Assert.False(Connect(first with {PollingIntervalSeconds=1},next with {PollingIntervalSeconds=1}));
    }

    private static ThermalWindow Window(int seconds)=>new(new("cpu","CPU","CPU","setup","map"),"session",
        DateTimeOffset.UnixEpoch.AddSeconds(seconds),DateTimeOffset.UnixEpoch.AddSeconds(seconds+50),seconds,50,6,
        new(6,60,60,60),new(6,50,50,50),new(6,100,100,100),new(0,null,null,null),new(0,null,null,null),false,"Insufficient sample coverage")
        {PollingIntervalSeconds=10};
}
