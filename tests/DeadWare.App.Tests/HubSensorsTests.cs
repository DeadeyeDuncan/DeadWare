using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class HubSensorsTests
{
    // The owner's hub as it reads: the pump and the coolant temperature on channel 13, the three radiator fans on 1 to 3.
    static HubReadings Owners()=>new([new(13,"pump","Pump",2774,43.7),new(1,"fan","Fan",1660),new(2,"fan","Fan",1654),new(3,"fan","Fan",1663)]);

    [Fact]
    public void The_owners_hub_gives_five_rows()
    {
        var rows=HubSensors.Sensors(Owners(),TimeSpan.Zero,false);
        Assert.Equal(["Coolant","Pump","Fan 1","Fan 2","Fan 3"],rows.Select(r=>r.Name));
        Assert.Equal(["/corsairlink/hub/13/coolant","/corsairlink/hub/13/pump","/corsairlink/hub/1/fan","/corsairlink/hub/2/fan","/corsairlink/hub/3/fan"],rows.Select(r=>r.Id));
        Assert.Equal([43.7,2774,1660,1654,1663],rows.Select(r=>r.Value));
        Assert.Equal(["°C","RPM","RPM","RPM","RPM"],rows.Select(r=>r.Unit));
        Assert.Equal(["Temperature","Fan","Fan","Fan","Fan"],rows.Select(r=>r.SensorType));
        Assert.All(rows,r=>{Assert.Equal("Corsair iCUE LINK hub",r.Hardware);Assert.Equal("/corsairlink/hub",r.HardwareId);Assert.Equal("CorsairLink",r.HardwareType);Assert.Equal("Available",r.Validity);});
    }

    [Fact]
    public void With_the_picture_fans_are_radiator_fans()
    {
        var rows=HubSensors.Sensors(Owners(),TimeSpan.Zero,true);
        Assert.Equal(["Coolant","Pump","Radiator fan 1","Radiator fan 2","Radiator fan 3"],rows.Select(r=>r.Name));
        Assert.Equal("/corsairlink/hub/3/fan",rows[4].Id);
    }

    [Theory]
    [InlineData(0,5)][InlineData(10000,5)][InlineData(10001,0)][InlineData(60000,0)]
    public void A_reading_older_than_10_s_gives_no_rows(int milliseconds,int rows)
        =>Assert.Equal(rows,HubSensors.Sensors(Owners(),TimeSpan.FromMilliseconds(milliseconds),false).Count);

    [Fact]
    public void No_readings_give_no_rows()=>Assert.Empty(HubSensors.Sensors(null,TimeSpan.Zero,false));

    [Fact]
    public void A_part_without_a_reading_keeps_its_row()
    {
        var rows=HubSensors.Sensors(new HubReadings([new(13,"pump","Pump",null,null),new(1,"fan","Fan",null),new(2,"fan","Fan",1654)]),TimeSpan.Zero,false);
        Assert.Equal(["Coolant","Pump","Fan 1","Fan 2"],rows.Select(r=>r.Name));
        Assert.Equal([null,null,null,1654],rows.Select(r=>r.Value));
    }

    [Fact]
    public void Two_coolant_sensors_are_named_after_their_parts()
    {
        var rows=HubSensors.Sensors(new HubReadings([new(13,"pump","XD5",2774,43.7),new(14,"pump","XD6",2700,41.2)]),TimeSpan.Zero,false);
        Assert.Equal(["Coolant · XD5","Coolant · XD6","Pump · XD5","Pump · XD6"],rows.Select(r=>r.Name));
        Assert.Equal(["/corsairlink/hub/13/coolant","/corsairlink/hub/14/coolant","/corsairlink/hub/13/pump","/corsairlink/hub/14/pump"],rows.Select(r=>r.Id));
        Assert.Equal([43.7,41.2,2774,2700],rows.Select(r=>r.Value));
    }

    [Fact]
    public void Hub_rows_never_become_the_CPU_pump()
    {
        var board=new SensorReading("/lpc/nct6687d/0/fan/3","Nuvoton NCT6687D","Pump Fan","RPM",2716,"/lpc/nct6687d/0","SuperIO","Fan");
        var hub=HubSensors.Sensors(Owners(),TimeSpan.Zero,false);
        Assert.Equal(board,CpuPumpSpeed.Select([board]));
        Assert.Equal(board,CpuPumpSpeed.Select([..hub,board]));
        Assert.Equal(board,CpuPumpSpeed.Select([board,..hub]));
        Assert.Null(CpuPumpSpeed.Select(hub));
        Assert.Null(new CpuPumpSpeed().Read(hub,1));

        var cpu=new SensorReading("/cpu/0/temperature/0","Test CPU","CPU Package","°C",60,"/cpu/0","Cpu","Temperature");
        string Cooler(IReadOnlyList<SensorReading> sensors)
        {
            var reading=new Reading(new(DateTimeOffset.UtcNow,1,new Dictionary<string,double?>()),[],sensors,[],"Test CPU","","","",false);
            var sample=Assert.Single(ThermalProjection.Project(reading,"session","machine","setup"));
            return sample.Device.Id+"|"+sample.Device.SensorMap+"|"+sample.FanRpm;
        }
        Assert.Equal(Cooler([cpu,board]),Cooler([cpu,..hub,board]));
        Assert.Equal(Cooler([cpu]),Cooler([cpu,..hub]));
    }

    [Fact]
    public void Coolant_is_the_first_coolant_reading_and_null_when_old()
    {
        Assert.Equal(43.7,HubSensors.Coolant(Owners(),TimeSpan.Zero));
        Assert.Equal(43.7,HubSensors.Coolant(Owners(),TimeSpan.FromSeconds(10)));
        Assert.Null(HubSensors.Coolant(Owners(),TimeSpan.FromMilliseconds(10001)));
        Assert.Null(HubSensors.Coolant(null,TimeSpan.Zero));
        Assert.Null(HubSensors.Coolant(new HubReadings([new(1,"fan","Fan",1660)]),TimeSpan.Zero));
        Assert.Null(HubSensors.Coolant(new HubReadings([new(13,"pump","Pump",2774,null)]),TimeSpan.Zero));
        Assert.Equal(41.2,HubSensors.Coolant(new HubReadings([new(13,"pump","XD5",2774,null),new(14,"pump","XD6",2700,41.2)]),TimeSpan.Zero));
        Assert.Equal("cpu/coolant",HubSensors.CoolantKey);
    }
}
