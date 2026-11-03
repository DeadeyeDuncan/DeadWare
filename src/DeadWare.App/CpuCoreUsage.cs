using System.Text.RegularExpressions;

namespace DeadWare.App;
/// <summary>Selects the per-core CPU load sensors in a stable order (P-cores, E-cores, then generic cores).</summary>
public static class CpuCoreUsage
{
    public const int MaximumCores=64;
    private static readonly Regex CoreName=new(@"^(CPU Core|P-Core|E-Core)(?: #(\d+))?(?: Thread #(\d+))?$",RegexOptions.Compiled|RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    public static string Key(int index)=>$"cpu/core/{index}/usage";
    public static IReadOnlyList<double?> FromSensors(IReadOnlyList<SensorReading> sensors)
    {
        var groups=new SortedDictionary<(int Group,int K),(double? Plain,bool HasPlain,SortedDictionary<int,double?> Threads)>();
        foreach(var s in sensors)
        {
            if(s.HardwareType!="Cpu"||s.Unit!="%"||s.SensorType!="Load")continue;
            var m=CoreName.Match(s.Name);
            if(!m.Success)continue;
            var prefix=m.Groups[1].Value.ToLowerInvariant();
            int group=prefix=="p-core"?0:prefix=="e-core"?1:2;
            int k=m.Groups[2].Success?int.Parse(m.Groups[2].Value):1;
            if(!groups.TryGetValue((group,k),out var g))g=(null,false,new());
            if(m.Groups[3].Success)g.Threads[int.Parse(m.Groups[3].Value)]=s.Value;
            else{g.Plain=s.Value;g.HasPlain=true;}
            groups[(group,k)]=g;
        }
        var result=new List<double?>();
        foreach(var g in groups.Values)
            foreach(var v in g.HasPlain?new[]{g.Plain}:g.Threads.Values.ToArray())
            {
                if(result.Count>=MaximumCores)return result;
                result.Add(v is null?null:Math.Round(v.Value,1));
            }
        return result;
    }
}
