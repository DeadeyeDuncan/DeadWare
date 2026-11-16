using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class WindowsThermalZonesTests
{
    [Fact] public void Zones_become_sensor_rows_under_their_own_hardware()
    {
        var rows=WindowsThermalZones.Sensors([(@"\_TZ.CPUZ",52.5),(@"\_TZ.TZ00",150),(@"\_SB.TZ01",double.NaN)]).ToArray();
        Assert.Equal([@"/windows/thermalzones/\_TZ.CPUZ",@"/windows/thermalzones/\_TZ.TZ00",@"/windows/thermalzones/\_SB.TZ01"],rows.Select(row=>row.Id));
        Assert.All(rows,row=>
        {
            Assert.Equal(("Windows thermal zones","/windows/thermalzones","ThermalZone","Temperature","°C"),(row.Hardware,row.HardwareId,row.HardwareType,row.SensorType,row.Unit));
            // Never a CPU, fan or GPU row, so the collector's CPU, pump and graph selections cannot pick one up.
            Assert.NotEqual("Cpu",row.HardwareType);
        });
        Assert.Equal(@"\_TZ.CPUZ",rows[0].Name);Assert.Equal(52.5,rows[0].Value);Assert.Equal("Available",rows[0].Validity);
        // An implausible reading is no value, and the validity keeps what the zone said for a diagnostic report.
        Assert.Null(rows[1].Value);Assert.Equal("Implausible reading (150.0 °C)",rows[1].Validity);
        Assert.Null(rows[2].Value);Assert.Equal("Missing or invalid provider value",rows[2].Validity);
        Assert.Null(CpuPumpSpeed.Select(rows));Assert.Empty(CpuCoreUsage.FromSensors(rows,out _));
    }

    [Fact] public void Reading_this_machine_never_throws_and_records_its_time()
    {
        var zones=new WindowsThermalZones();
        try
        {
            var timings=new Dictionary<string,double>();
            var first=zones.Read(timings);
            Assert.True(timings["windows/thermal"]>=0);
            Assert.All(first,zone=>Assert.False(string.IsNullOrWhiteSpace(zone.Name)));
            // Without zones (most desktops and test runners) the query is closed, and a read within the minute opens nothing.
            if(first.Count==0)Assert.Empty(zones.Read(timings));
        }
        finally {zones.Close();zones.Close();}
    }
}
