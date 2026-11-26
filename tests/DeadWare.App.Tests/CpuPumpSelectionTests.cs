using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class CpuPumpSelectionTests
{
    [Fact]
    public void Labeled_pump_wins_over_cpu_fan_and_unlabeled_or_percent_headers_are_ignored()
    {
        var source=new CpuPumpSpeed();
        SensorReading[] sensors=[Fan("sys","System Fan #1",700),Fan("cpu","CPU Fan",900),
            Fan("duty","Pump Fan",90) with {Unit="%",SensorType="Control"},Fan("pump","Pump Fan",2376)];
        Assert.Equal(2376d,source.Read(sensors,1));
        Assert.Equal(900d,new CpuPumpSpeed().Read([Fan("sys","System Fan #1",700),Fan("cpu","CPU Fan",900)],1));
        Assert.Equal(2800d,new CpuPumpSpeed().Read([Fan("aio","Pump",2800) with {HardwareType="Cooler"}],1));
        Assert.Null(new CpuPumpSpeed().Read([Fan("f1","Fan #1",1200)],1));
    }

    [Fact]
    public void Outage_keeps_the_chosen_header_and_multi_socket_hosts_get_no_pump()
    {
        var source=new CpuPumpSpeed();
        Assert.Equal(2376d,source.Read([Fan("pump","Pump Fan",2376),Fan("cpu","CPU Fan",900)],1));
        Assert.Null(source.Read([Fan("pump","Pump Fan",null),Fan("cpu","CPU Fan",900)],1));
        Assert.Null(source.Read([Fan("cpu","CPU Fan",900)],1));
        Assert.Equal(2400d,source.Read([Fan("pump","Pump Fan",2400)],1));
        Assert.Null(new CpuPumpSpeed().Read([Fan("pump","Pump Fan",2376)],2));
        Assert.Null(new CpuPumpSpeed().Read([Fan("pump","Pump Fan",2376)],0));
    }

    [Fact]
    public void The_chosen_header_names_its_hardware_and_multi_socket_hosts_choose_none()
    {
        // The collector keeps that hardware updating every poll while it slows the rest of the board down.
        var source=new CpuPumpSpeed();Assert.Null(source.HardwareId);
        source.Read([Fan("pump","Pump Fan",2376)],1);Assert.Equal("/lpc/nct6687d/0",source.HardwareId);
        source.Read([Fan("pump","Pump Fan",null)],1);Assert.Equal("/lpc/nct6687d/0",source.HardwareId);
        var aio=new CpuPumpSpeed();aio.Read([Fan("aio","Pump",2800) with {HardwareType="Cooler",HardwareId="/nzxt/kraken/0"}],1);Assert.Equal("/nzxt/kraken/0",aio.HardwareId);
        var dual=new CpuPumpSpeed();dual.Read([Fan("pump","Pump Fan",2376)],2);Assert.Null(dual.HardwareId);
        var none=new CpuPumpSpeed();none.Read([Fan("f1","Fan #1",1200)],1);Assert.Null(none.HardwareId);
    }

    [Theory]
    [InlineData(0d,0d)][InlineData(2370d,2370d)][InlineData(-1d,null)]
    [InlineData(double.NaN,null)][InlineData(double.PositiveInfinity,null)]
    public void Rpm_values_keep_zero_and_reject_invalid_readings(double value,double? expected)
        =>Assert.Equal(expected,new CpuPumpSpeed().Read([Fan("pump","Pump Fan",value)],1));

    [Fact]
    public void Graph_mapping_adds_pump_to_the_existing_cpu_identity()
    {
        var reading=new Reading(new(DateTimeOffset.UtcNow,1,new Dictionary<string,double?>()),[],
            [new("/cpu/0/temperature/0","Test CPU","CPU Package","°C",60,"/cpu/0","Cpu","Temperature")],[],"Test CPU","","","",false);
        var mappings=GraphHistoryProjection.Mappings(reading,"machine");
        string prefix=Assert.Single(mappings,p=>p.Value=="cpu/temperature").Key[..^"temperature".Length];
        Assert.Equal("cpu/pump",mappings[prefix+"pump"]);
        Assert.Equal("cpu/usage",mappings[prefix+"usage"]);
    }

    [Fact]
    public void Graph_mapping_adds_coolant_to_the_existing_cpu_identity()
    {
        var reading=new Reading(new(DateTimeOffset.UtcNow,1,new Dictionary<string,double?>()),[],
            [new("/cpu/0/temperature/0","Test CPU","CPU Package","°C",60,"/cpu/0","Cpu","Temperature")],[],"Test CPU","","","",false);
        var mappings=GraphHistoryProjection.Mappings(reading,"machine");
        string prefix=Assert.Single(mappings,p=>p.Value=="cpu/temperature").Key[..^"temperature".Length];
        Assert.Equal("cpu/coolant",mappings[prefix+"coolant"]);
        Assert.Equal("cpu/pump",mappings[prefix+"pump"]);
    }

    private static SensorReading Fan(string id,string name,double? value)=>new(id,"Nuvoton NCT6687D",name,"RPM",value,"/lpc/nct6687d/0","SuperIO","Fan");
}
