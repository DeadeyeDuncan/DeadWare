using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class SensorValidityTests
{
    [Fact] public void MissingLowLevelAccessSuppressesPlaceholderTemperatureWithoutHidingValidZeroLoad()
    {Assert.Null(SensorValidity.Normalize(0,true,false));Assert.Equal(0,SensorValidity.Normalize(0,false,false));Assert.Equal(0,SensorValidity.Normalize(0,true,true));}
}
