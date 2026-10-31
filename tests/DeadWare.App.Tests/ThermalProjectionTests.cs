using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class ThermalProjectionTests
{
    [Theory]
    [InlineData(0.5)] [InlineData(10)]
    public void Projection_keeps_the_cadence_captured_with_the_publication(double interval)
    {
        var reading=Reading(Sensor("t","CPU Package","Temperature",60));
        reading=reading with {Sample=reading.Sample with {PollingIntervalSeconds=interval}};
        Assert.Equal(interval,Assert.Single(ThermalProjection.Project(reading,"session","machine","setup")).PollingIntervalSeconds);
    }

    [Fact]
    public void Identical_devices_do_not_reuse_references_after_enumeration_changes()
    {
        var reading=Reading(Sensor("t1","GPU Core","Temperature",60,"/gpu/0","GpuAmd"),
            Sensor("t2","GPU Core","Temperature",70,"/gpu/1","GpuAmd"));
        var first=ThermalProjection.Project(reading,"run1","m","c");
        var second=ThermalProjection.Project(reading,"run2","m","c");
        Assert.Equal(2,first.Count);Assert.All(first,s=>Assert.False(s.Device.ComparisonAllowed));
        Assert.DoesNotContain(first[0].Device.Id,second.Select(s=>s.Device.Id));
    }
    internal static Reading Reading(params SensorReading[] sensors) => new(
        new(DateTimeOffset.UtcNow, 100, new Dictionary<string, double?> { ["cpu/usage"] = 40 }),
        [], sensors, [], "Test CPU", "", "", "", false);
    internal static SensorReading Sensor(string id, string name, string type, double? value,
        string hardware="/cpu/0", string kind="Cpu") => new(id, "Test " + kind, name,
            type switch { "Temperature" => "°C", "Power" => "W", "Fan" => "RPM", _ => "%" },
            value, hardware, kind, type, value.HasValue ? "Available" : "Unavailable");

    [Fact]
    public void Package_power_and_load_are_typed_and_missing_does_not_become_zero()
    {
        var reading = Reading(Sensor("t", "Core (Tctl/Tdie)", "Temperature", 65),
            Sensor("p", "Package", "Power", 70), Sensor("l", "CPU Total", "Load", 40),
            Sensor("c", "CPU Total", "Control", 99));
        var sample = Assert.Single(ThermalProjection.Project(reading, "s", "machine", "setup"));
        Assert.Equal(70, sample.Power); Assert.Equal(40, sample.Load); Assert.Equal(65, sample.Temperature);
        Assert.Null(sample.FanRpm); Assert.Contains("Temperature", sample.Device.SensorMap);
        var absent = Assert.Single(ThermalProjection.Project(reading with { Sensors = reading.Sensors.Where(s=>s.SensorType!="Power").ToArray() }, "s", "machine", "setup"));
        Assert.Null(absent.Power);
    }

    [Fact]
    public void Hotspot_and_soc_are_not_core_temperature_or_package_power()
    {
        var result = Assert.Single(ThermalProjection.Project(Reading(
            Sensor("hot", "GPU Hot Spot", "Temperature", 85, "/gpu/0", "GpuAmd"),
            Sensor("soc", "GPU SoC", "Power", 5, "/gpu/0", "GpuAmd"),
            Sensor("fan", "GPU Fan", "Fan", 0, "/gpu/0", "GpuAmd")), "s", "m", "c"));
        Assert.Null(result.Temperature); Assert.Null(result.Power); Assert.Equal(0, result.FanRpm);
    }

    [Fact]
    public void Cpu_cooler_speed_prefers_a_labeled_pump_then_the_cpu_fan_header()
    {
        var cpu = Sensor("t", "CPU Package", "Temperature", 60);
        var pump = Sensor("pump", "Pump Fan", "Fan", 2376, "/lpc/nct6687d/0", "SuperIO");
        var cpuFan = Sensor("cpufan", "CPU Fan", "Fan", 900, "/lpc/nct6687d/0", "SuperIO");
        var system = Sensor("sys", "System Fan #1", "Fan", 700, "/lpc/nct6687d/0", "SuperIO");
        var duty = Sensor("duty", "Pump Fan", "Control", 90, "/lpc/nct6687d/0", "SuperIO");
        var withPump = Assert.Single(ThermalProjection.Project(Reading(cpu, system, cpuFan, duty, pump), "s", "m", "c"));
        Assert.Equal(2376, withPump.FanRpm); Assert.Contains("SuperIO", withPump.Device.SensorMap);
        Assert.Equal(900, Assert.Single(ThermalProjection.Project(Reading(cpu, system, cpuFan), "s", "m", "c")).FanRpm);
        var cooler = Sensor("aio", "Pump", "Fan", 2800, "/nzxt/kraken/0", "Cooler");
        Assert.Equal(2800, Assert.Single(ThermalProjection.Project(Reading(cpu, cooler), "s", "m", "c")).FanRpm);
        // Unlabeled headers cannot be attributed to the CPU cooler.
        Assert.Null(Assert.Single(ThermalProjection.Project(Reading(cpu,
            Sensor("f1", "Fan #1", "Fan", 1200, "/lpc/nct6798d/0", "SuperIO")), "s", "m", "c")).FanRpm);
    }

    [Fact]
    public void Cpu_cooler_speed_keeps_its_mapping_through_outages_and_skips_multi_socket_hosts()
    {
        var cpu = Sensor("t", "CPU Package", "Temperature", 60);
        var pump = Sensor("pump", "Pump Fan", "Fan", 2376, "/lpc/nct6687d/0", "SuperIO");
        var live = Assert.Single(ThermalProjection.Project(Reading(cpu, pump), "s", "m", "c"));
        var outage = Assert.Single(ThermalProjection.Project(Reading(cpu, pump with { Value = null }), "s", "m", "c"));
        Assert.Null(outage.FanRpm); Assert.Equal(live.Device.Id, outage.Device.Id);
        var sockets = ThermalProjection.Project(Reading(cpu, cpu with { Id = "t2", HardwareId = "/cpu/1" }, pump), "s", "m", "c");
        Assert.Equal(2, sockets.Count); Assert.All(sockets, s => Assert.Null(s.FanRpm));
        var gpu = Assert.Single(ThermalProjection.Project(Reading(pump,
            Sensor("g", "GPU Core", "Temperature", 50, "/gpu/0", "GpuAmd")), "s", "m", "c"));
        Assert.Null(gpu.FanRpm);
    }

    [Fact]
    public void Sensor_outages_keep_identity_but_mapping_machine_or_device_changes_separate_it()
    {
        var r=Reading(Sensor("t", "CPU Package", "Temperature", 60));
        var original=Assert.Single(ThermalProjection.Project(r,"s","m","c"));
        var missing=Assert.Single(ThermalProjection.Project(r with {Sensors=[r.Sensors[0] with {Value=null}]},"s","m","c"));
        Assert.Equal(original.Device.Id,missing.Device.Id);
        Assert.NotEqual(original.Device.Id,Assert.Single(ThermalProjection.Project(r,"s","other","c")).Device.Id);
        Assert.NotEqual(original.Device.Id,Assert.Single(ThermalProjection.Project(r with {Sensors=[r.Sensors[0] with {Id="other"}]},"s","m","c")).Device.Id);
        Assert.Equal("new",Assert.Single(ThermalProjection.Project(r,"s","m","new")).Device.ConfigurationId);
    }
}
