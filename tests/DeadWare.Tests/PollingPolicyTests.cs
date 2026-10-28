using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class PollingPolicyTests
{
    [Theory][InlineData(0.2,0.8)][InlineData(0.9,0.25)][InlineData(1,1)][InlineData(3.5,1)]
    public void SlowPollAlwaysYieldsBeforeAnotherScan(double elapsed,double expectedDelay)
    {Assert.Equal(expectedDelay,PollingPolicy.DelayAfter(elapsed).TotalSeconds,8);}
    [Theory]
    [InlineData(2,0.2,1.8)][InlineData(5,4.9,0.25)][InlineData(10,9.8,0.25)]
    [InlineData(2,2,2)][InlineData(5,8,5)][InlineData(10,12,10)]
    [InlineData(5,-1,5)][InlineData(10,double.NaN,10)][InlineData(2,double.PositiveInfinity,2)]
    public void ConfiguredPollPreservesTargetCadenceAndGuaranteedIdle(int interval,double elapsed,double expectedDelay)
    {Assert.Equal(expectedDelay,PollingPolicy.DelayAfter(elapsed,interval).TotalSeconds,8);}
    [Theory][InlineData(0.5,0.1,0.4)][InlineData(0.5,0.4,0.25)][InlineData(0.5,0.5,0.5)][InlineData(0.5,1.2,0.5)]
    public void FasterPollingStillYieldsBetweenProviderPasses(double interval,double elapsed,double expectedDelay)
    {Assert.Equal(expectedDelay,PollingPolicy.DelayAfter(elapsed,interval).TotalSeconds,8);}
    [Theory][InlineData(0.5)][InlineData(1)][InlineData(2)][InlineData(5)][InlineData(10)]
    public void SupportedIntervalsRemainValidAndFreshUntilAfterTheirNextExpectedReading(double interval)
    {
        Assert.Contains(interval,PollingPolicy.Intervals);
        Assert.Equal(interval,PollingPolicy.Normalize(interval));
        Assert.Equal(Math.Max(4,interval+3),PollingPolicy.FreshnessSeconds(interval));
        Assert.Equal(interval,new CollectorOptions(PollingIntervalSeconds:interval).PollingIntervalSeconds);
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(0.25)][InlineData(3)][InlineData(30)][InlineData(double.MaxValue)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void UnsupportedIntervalsFallBackToTheSafeDefault(double interval)
    {
        Assert.Equal(1,PollingPolicy.Normalize(interval));
        Assert.Equal(4,PollingPolicy.FreshnessSeconds(interval));
        Assert.Equal(0.8,PollingPolicy.DelayAfter(0.2,interval).TotalSeconds,8);
    }
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
