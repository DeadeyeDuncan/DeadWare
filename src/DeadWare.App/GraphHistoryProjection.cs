using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeadWare.App;

public static class GraphHistoryProjection
{
    /// <summary>Stable graph keys contain no names and do not depend on GPU enumeration order.</summary>
    public static IReadOnlyDictionary<string,string> Mappings(Reading reading,string machineKey)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        void Add(string type,string model,string prefix,params string[] metrics)
        {
            string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[]{"graph-v1",machineKey,type,model}))));
            foreach(string metric in metrics)result[identity+"/"+metric]=prefix+"/"+metric;
        }
        Add("Network","interface totals","network","download","upload");
        Add("Memory","physical memory","memory","usage");
        var cpus=reading.Sensors.Where(s=>s.HardwareType=="Cpu"&&s.HardwareId.Length>0&&s.Hardware.Length>0)
            .GroupBy(s=>s.HardwareId,StringComparer.Ordinal).ToArray();
        // CPU graph values are machine-wide. Multiple sockets cannot be assigned a
        // physical identity from the collector's single current CPU label.
        if(cpus.Length==1)
            Add("Cpu",cpus[0].First().Hardware,"cpu","usage","temperature","pump");
        foreach(var gpu in reading.Gpus.Take(16))
        {
            if(string.IsNullOrWhiteSpace(gpu.Name)||reading.Gpus.Count(g=>g.Name.Equals(gpu.Name,StringComparison.OrdinalIgnoreCase))!=1)continue;
            var metadata=reading.Sensors.FirstOrDefault(s=>s.HardwareId==gpu.Id&&s.HardwareType is "GpuAmd" or "GpuNvidia" or "GpuIntel");
            if(metadata is not null)Add(metadata.HardwareType,gpu.Name,gpu.Id,"usage","temperature","fan");
        }
        return result;
    }
}
