using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Projects existing publications. It never discovers or polls hardware.</summary>
public static class ThermalProjection
{
    public static IReadOnlyList<ThermalSample> Project(Reading reading, string sessionId, string machineKey, string configurationId)
    {
        var result = new List<ThermalSample>();
        var groups = reading.Sensors.Where(s => s.HardwareId.Length > 0 &&
            s.HardwareType is "Cpu" or "GpuAmd" or "GpuNvidia" or "GpuIntel")
            .GroupBy(s => (s.HardwareId, s.HardwareType, s.Hardware)).Take(16).ToArray();
        // Board and AIO headers are machine-wide, so they describe the CPU cooler only on single-socket hosts.
        var cooler = groups.Count(g => g.Key.HardwareType == "Cpu") == 1 ? CpuCooler(reading.Sensors) : null;
        foreach (var group in groups)
        {
            bool cpu = group.Key.HardwareType == "Cpu";
            var sensors = group.ToArray();
            // Select by metadata, even when the value is absent, to keep outages from
            // silently switching sensors and changing the baseline's meaning.
            SensorReading? Pick(string type, params string[] names) => names.Select(name =>
                sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(s => s is not null);
            var temperature = cpu
                ? Pick("Temperature", "CPU Package", "Core (Tctl/Tdie)", "CPU (Tctl/Tdie)", "Core (Tdie)", "Core Average")
                : Pick("Temperature", "GPU Core", "GPU Temperature");
            var load = cpu ? Pick("Load", "CPU Total") : Pick("Load", "GPU Core", "GPU D3D 3D");
            var power = cpu ? Pick("Power", "CPU Package", "Package") : Pick("Power", "GPU Package", "GPU Total", "GPU Power");
            var clock = cpu ? Pick("Clock", "Cores (Average Effective)", "Cores (Average)", "CPU Core #1", "Core #1") : Pick("Clock", "GPU Core");
            var fan = cpu ? cooler : sensors.FirstOrDefault(s => s.SensorType == "Fan");
            bool ambiguous=groups.Count(g=>g.Key.HardwareType==group.Key.HardwareType&&g.Key.Hardware==group.Key.Hardware)>1;
            bool windowsLoad=load is null&&cpu&&groups.Count(g=>g.Key.HardwareType=="Cpu")==1;
            var sources = new[] { temperature, load, power, clock, fan }.Where(s => s is not null)
                .Select(s => new { s!.Id, s.Name, s.HardwareId, s.HardwareType, s.SensorType, s.Unit }).ToArray();
            string mapping = JsonSerializer.Serialize(new { Version = 1, Sources = sources, WindowsCpuLoad=windowsLoad, AmbiguousIdentity=ambiguous });
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                machineKey + "|" + group.Key + "|" + mapping+(ambiguous?"|session:"+sessionId:""))));
            var device = new ThermalDevice(identity, group.Key.Hardware, cpu ? "CPU" : "GPU", configurationId, mapping)
            {
                ComparisonAllowed=!ambiguous,
                IdentityNote=ambiguous?"Multiple devices share this model name. Their physical identity cannot be verified; history is separated by session and reference comparisons are disabled.":""
            };
            double? Valid(SensorReading? s) => SampleHistory.Finite(s?.Value);
            double? utilization = Valid(load);
            // Windows utilization is machine-wide; it is not a fallback for a particular CPU in a multi-socket host.
            if (windowsLoad)
                utilization = SampleHistory.Finite(reading.Sample.Values.GetValueOrDefault("cpu/usage"));
            bool throttled = sensors.Any(s => s.SensorType == "Factor" && s.Value > 0 &&
                (s.Name.Contains("Thermal Throttling", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("PROCHOT", StringComparison.OrdinalIgnoreCase)));
            string? unavailable = new[] { temperature, power }.Any(s => s?.Value is null) || utilization is null
                ? "Temperature, package power or load unavailable; check supported sensors and administrator access." : null;
            result.Add(new(device, sessionId, reading.Sample.Timestamp.ToUniversalTime(), reading.Sample.MonotonicSeconds,
                Valid(temperature), utilization, Valid(power), Valid(clock), Valid(fan), throttled, unavailable)
                { PollingIntervalSeconds = reading.Sample.PollingIntervalSeconds });
        }
        return result;
    }

    private static SensorReading? CpuCooler(IEnumerable<SensorReading> sensors)
    {
        var fans = sensors.Where(s => s.SensorType == "Fan" && s.HardwareType is "SuperIO" or "Cooler" or "EmbeddedController")
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        // Unlabeled headers such as "Fan #2" cannot be attributed to the CPU cooler.
        return fans.FirstOrDefault(s => s.Name.Contains("Pump", StringComparison.OrdinalIgnoreCase))
            ?? fans.FirstOrDefault(s => s.Name.Equals("CPU Fan", StringComparison.OrdinalIgnoreCase));
    }
}
