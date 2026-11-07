using System.Globalization;
namespace DeadWare.Core;
/// <summary>One ready engine instance of a process above 0 %. <see cref="Label"/> is the GPU engine cell text ("GPU 1 · 3D"), <see cref="Adapter"/> the adapter
/// description (null when DXGI did not name it) and <see cref="Percent"/> is clamped to 0–100.</summary>
public sealed record GpuEngineUse(long Luid,int Phys,int Engine,string Type,string Label,string? Adapter,double Percent);
public sealed record GpuJoinResult(IReadOnlyList<ProcessSample> Processes,ProcessColumnState State,double? IntervalSeconds);
/// <summary>Joins one GPU counter read to scanned processes by process ID (spec, Joining to processes). A value nothing can vouch for is null, never 0.</summary>
public static class GpuProcessJoin
{
    public const int MaximumEngines=32;
    const long TicksPerSecond=10_000_000;
    const string NumberPrefix="GPU ",NumberSuffix=" · ";
    /// <summary>The read's interval when it may add GPU time: an available read joined with credit. Null otherwise, and null when it has no previous collection.</summary>
    public static double? IntervalSeconds(GpuCounterRead? read)=>read is{State.Available:true,Credit:true}?read.IntervalSeconds:null;
    /// <summary>Engine order, used for every tie: numbered adapters by number, then "GPU ?" adapters by LUID, then phys, then engine. The number is read from the
    /// "GPU &lt;n&gt; · " prefix that <see cref="GpuAdapterName.Label"/> writes, because DXGI numbers adapters in enumeration order, not LUID order.</summary>
    public static int CompareEngineOrder(GpuEngineUse a,GpuEngineUse b)
    {
        int? na=AdapterNumber(a.Label),nb=AdapterNumber(b.Label);
        if(na.HasValue!=nb.HasValue)return na.HasValue?-1:1;
        int c=(na??0).CompareTo(nb??0);
        // One read numbers each LUID once, so for numbered adapters the LUID only keeps the order total.
        if(c==0)c=a.Luid.CompareTo(b.Luid);
        if(c==0)c=a.Phys.CompareTo(b.Phys);
        if(c==0)c=a.Engine.CompareTo(b.Engine);
        return c;
    }
    /// <param name="read">Null before any read has completed (loop placement): every GPU value is then a first sample, so each "—" has a reason.</param>
    public static GpuJoinResult Join(IReadOnlyList<ProcessSample> samples,GpuCounterRead? read)
    {
        if(read is null)return new(Map(samples,p=>Joined(p,null,null,null,ProcessGap.FirstSample,null)),ProcessColumnState.Ready,null);
        if(!read.State.Available)return new(Map(samples,p=>Joined(p,null,null,null,ProcessGap.None,null)),read.State,null);
        var interval=IntervalSeconds(read);
        // A counter whose array call was not ready is treated like no previous collection (engines) or an unknown value (memory) for every process.
        var engines=read.EnginesReady?ByPid(read.Engines,e=>e.Instance):null;
        var dedicated=read.DedicatedReady?ByPid(read.Dedicated,m=>m.Instance):null;
        var shared=read.SharedReady?ByPid(read.Shared,m=>m.Instance):null;
        // PDH rates an instance against the previous raw value of the same name, which a process reusing the ID inherits; so a process created at or
        // after the previous collection less the tracker's 1 s tolerance has no rate yet. This comes before the known-zero rule.
        long? ratedBefore=read.PreviousUtcTime is{} previous?previous.UtcTicks-TicksPerSecond:null;
        return new(Map(samples,p=>
        {
            var uses=engines is not null&&ratedBefore is{} before&&p.Generation<before?Uses(engines.GetValueOrDefault(p.Pid),read.Adapters):null;
            long? dedicatedBytes=Bytes(dedicated,p.Pid),sharedBytes=Bytes(shared,p.Pid);
            var gap=uses is null||dedicatedBytes is null||sharedBytes is null?ProcessGap.FirstSample:ProcessGap.None;
            return Joined(p,uses,dedicatedBytes,sharedBytes,gap,interval);
        }),read.State,interval);
    }
    static ProcessSample Joined(ProcessSample p,IReadOnlyList<GpuEngineUse>? uses,long? dedicatedBytes,long? sharedBytes,ProcessGap gap,double? interval)
    {
        var busiest=uses is{Count:>0}?uses[0]:null;
        double? percent=uses is null?null:busiest?.Percent??0;
        return p with
        {
            GpuPercent=percent,GpuEngine=uses is null?null:busiest?.Label??"",GpuAdapter=busiest?.Adapter,GpuEngines=uses,
            GpuDedicatedBytes=dedicatedBytes,GpuSharedBytes=sharedBytes,GpuGap=gap,
            Deltas=p.Deltas is{} d?d with{GpuSeconds=percent/100*interval,GpuMemoryByteSeconds=dedicatedBytes*interval}:null
        };
    }
    /// <summary>Null when the process has engine instances but none is ready; empty (a known 0 %) when it has none, or only idle ones.</summary>
    static IReadOnlyList<GpuEngineUse>? Uses(List<GpuEngineItem>? items,IReadOnlyDictionary<long,GpuAdapterName> adapters)
    {
        if(items is null)return [];
        var ready=false;
        List<GpuEngineUse>? uses=null;
        foreach(var item in items)
        {
            // A not-ready item's value is a placeholder: it is never read.
            if(!item.Ready)continue;
            ready=true;
            var percent=Math.Clamp(item.Percent,0,100);
            if(!(percent>0))continue;
            var i=item.Instance;
            var adapter=adapters.GetValueOrDefault(i.Luid)??GpuAdapterName.Unknown;
            var type=i.Type??"";
            (uses??=[]).Add(new(i.Luid,i.Phys,i.Engine??0,type,adapter.Label(type),adapter.Description,percent));
        }
        if(!ready)return null;
        if(uses is null)return [];
        uses.Sort(BusiestFirst);
        if(uses.Count>MaximumEngines)uses.RemoveRange(MaximumEngines,uses.Count-MaximumEngines);
        return uses;
    }
    static int BusiestFirst(GpuEngineUse a,GpuEngineUse b)
    {
        int c=b.Percent.CompareTo(a.Percent);
        return c!=0?c:CompareEngineOrder(a,b);
    }
    /// <summary>Null when the counter or one of the process's items is not ready; 0 for a process with no item.</summary>
    static long? Bytes(Dictionary<int,List<GpuMemoryItem>>? byPid,int pid)
    {
        if(byPid is null)return null;
        if(!byPid.TryGetValue(pid,out var items))return 0;
        long sum=0;
        foreach(var item in items)
        {
            if(!item.Ready)return null;
            sum+=item.Bytes;
        }
        return sum;
    }
    static int? AdapterNumber(string label)
    {
        if(!label.StartsWith(NumberPrefix,StringComparison.Ordinal))return null;
        var rest=label.AsSpan(NumberPrefix.Length);
        var end=rest.IndexOf(NumberSuffix,StringComparison.Ordinal);
        return end>0&&int.TryParse(rest[..end],NumberStyles.None,CultureInfo.InvariantCulture,out var number)?number:null;
    }
    static Dictionary<int,List<T>> ByPid<T>(IReadOnlyList<T> items,Func<T,GpuCounterInstance> instance)
    {
        var map=new Dictionary<int,List<T>>();
        foreach(var item in items)
        {
            var pid=instance(item).Pid;
            if(!map.TryGetValue(pid,out var list))map[pid]=list=[];
            list.Add(item);
        }
        return map;
    }
    static IReadOnlyList<ProcessSample> Map(IReadOnlyList<ProcessSample> samples,Func<ProcessSample,ProcessSample> f)
    {
        var result=new ProcessSample[samples.Count];
        for(int i=0;i<result.Length;i++)result[i]=f(samples[i]);
        return result;
    }
}
