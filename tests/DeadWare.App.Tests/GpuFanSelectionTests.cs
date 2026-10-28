using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class GpuFanSelectionTests
{
    [Fact]
    public void Stable_first_sensor_is_selected_without_using_other_hardware_or_control_percent()
    {
        var source=new GpuFanSpeed();
        SensorReading[] sensors=[Sensor("/fan/1",1800),Sensor("/fan/0",900),
            Sensor("/fan/-1",99) with {Unit="%",SensorType="Control"},
            Sensor("/other/fan/0",3000) with {HardwareId="/gpu/1"}];
        Assert.Equal(900d,source.Read("/gpu/0",sensors));
        Assert.Equal(900d,source.Read("/gpu/0",sensors.Reverse().ToArray()));
        Assert.Equal(3000d,source.Read("/gpu/1",sensors));
    }

    [Fact]
    public void Outage_or_absence_of_the_first_fan_never_substitutes_the_second_fan()
    {
        var source=new GpuFanSpeed();
        Assert.Null(source.Read("/gpu/0",new[]{Sensor("/fan/1",1800),Sensor("/fan/0",null)}));
        Assert.Null(source.Read("/gpu/0",new[]{Sensor("/fan/1",1800)}));
        Assert.Equal(1200d,source.Read("/gpu/0",new[]{Sensor("/fan/0",1200),Sensor("/fan/1",1800)}));
    }

    [Theory]
    [InlineData(0d,0d)][InlineData(1400d,1400d)][InlineData(-1d,null)]
    [InlineData(double.NaN,null)][InlineData(double.PositiveInfinity,null)][InlineData(double.NegativeInfinity,null)]
    public void Rpm_values_keep_zero_and_reject_invalid_readings(double value,double? expected)
    {
        var source=new GpuFanSpeed();
        Assert.Equal(expected,source.Read("/gpu/0",new[]{Sensor("/fan/0",value)}));
    }

    [Fact]
    public void Fan_metadata_requires_rpm_and_discovery_can_follow_an_empty_publication()
    {
        var source=new GpuFanSpeed();
        Assert.Null(source.Read("/gpu/0",new[]{Sensor("/fan/0",75) with {Unit="%"}}));
        Assert.Equal(1500d,source.Read("/gpu/0",new[]{Sensor("/fan/0",1500)}));
        Assert.Null(source.Read("/cpu/0",new[]{Sensor("/fan/0",1500) with {HardwareId="/cpu/0",HardwareType="Cpu"}}));
    }

    private static SensorReading Sensor(string id,double? value)=>new(id,"Test GPU","GPU Fan","RPM",value,"/gpu/0","GpuNvidia","Fan");
}
