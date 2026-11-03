using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class CollectorPollingTests
{
    [Theory][InlineData(0.5,0.5)][InlineData(5,5)][InlineData(0.25,1)]
    public async Task PollingCanBeConfiguredAndChangedWithoutStartingProviders(double configured,double expected)
    {
        // Construction creates no worker, hardware computer or ETW trace; never call Start.
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:configured));
        Assert.Equal(expected,collector.PollingIntervalSeconds);
        collector.SetPollingInterval(10);Assert.Equal(10,collector.PollingIntervalSeconds);
        collector.SetPollingInterval(0.5);Assert.Equal(0.5,collector.PollingIntervalSeconds);
        collector.SetPollingInterval(double.NaN);Assert.Equal(1,collector.PollingIntervalSeconds);
        Assert.Equal("Not started",collector.Stage);
        Assert.True(collector.Completion.IsCompletedSuccessfully);
        Assert.Null(collector.Latest);
        Assert.Empty(collector.History.Read());
    }

    [Fact] public void PublishCores_writes_zero_based_keys_and_returns_count()
    {
        var values=new Dictionary<string,double?>();
        Assert.Equal(3,Collector.PublishCores(values,[10,null,30]));
        Assert.Equal(10d,values["cpu/core/0/usage"]);Assert.Null(values["cpu/core/1/usage"]);Assert.Equal(30d,values["cpu/core/2/usage"]);
        Assert.Equal(0,Collector.PublishCores(new(),[]));
    }
}
