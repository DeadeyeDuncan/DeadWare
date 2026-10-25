using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class PollingPolicyTests
{
    [Theory][InlineData(0.2,0.8)][InlineData(0.9,0.25)][InlineData(1,1)][InlineData(3.5,1)]
    public void SlowPollAlwaysYieldsBeforeAnotherScan(double elapsed,double expectedDelay)
    {Assert.Equal(expectedDelay,PollingPolicy.DelayAfter(elapsed).TotalSeconds,8);}
    [Fact] public void NormalMonitoringDoesNotProbeExtendedBusesOrStorage()
    {
        var s=new CollectorOptions().Sources;
        Assert.True(s.Cpu);Assert.True(s.Gpu);Assert.False(s.Memory);Assert.False(s.Motherboard);Assert.False(s.Controllers);Assert.False(s.Storage);
    }
    [Fact] public void ExtendedSourcesRequireExplicitOptInAndIsolationDisablesEveryHardwareSource()
    {
        var extended=new CollectorOptions(ExtendedSensors:true).Sources;
        Assert.True(extended.Motherboard);Assert.True(extended.Controllers);Assert.True(extended.Storage);Assert.True(extended.Memory);
        var isolated=new CollectorOptions(HardwareEnabled:false,ExtendedSensors:true).Sources;
        Assert.False(isolated.Cpu);Assert.False(isolated.Gpu);Assert.False(isolated.Memory);Assert.False(isolated.Motherboard);Assert.False(isolated.Controllers);Assert.False(isolated.Storage);
    }
}
