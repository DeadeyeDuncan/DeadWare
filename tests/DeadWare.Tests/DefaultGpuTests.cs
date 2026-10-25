using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class DefaultGpuTests
{
    [Fact] public void InitialSelectionPrefersDeviceWithCoreTemperatureWhileExplicitChoiceWins()
    {
        var devices=new[]{new Device("integrated","Integrated",false),new Device("discrete","Discrete",true)};
        Assert.Equal("discrete",DeviceSelection.Resolve(null,devices));
        Assert.Equal("integrated",DeviceSelection.Resolve("integrated",devices));
    }
}
