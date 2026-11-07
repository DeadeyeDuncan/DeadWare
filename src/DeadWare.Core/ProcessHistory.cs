using System.Text;

namespace DeadWare.Core;

public static class ProcessHistory
{
    public const string EverythingElse="Everything else";
    public const int TopPerMetric=10,MaximumApplications=40;

    /// <summary>The name an application's minutes are saved under, or null when they belong in Everything else: an empty name, one that reads as Everything else (ignoring case), or one holding '\', '/' or '*'. Names are cut at 256 characters, each unpaired surrogate becomes U+FFFD (the form SQLite's UTF-8 stores) and the end is trimmed again, so the result is unchanged when saved again.</summary>
    public static string? SavedName(string? name)
    {
        string text=UsageMetrics.NormalizeName(name);
        if(text.Any(char.IsSurrogate))text=Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));
        text=text.TrimEnd();
        if(text==UsageMetrics.UnknownName||text.Equals(EverythingElse,StringComparison.OrdinalIgnoreCase)||text.IndexOfAny(['\\','/','*'])>=0)return null;
        return text;
    }
}

/// <summary>One application's totals for a minute. Memory is the sum of private bytes × seconds; divide by the batch's covered seconds for the average.</summary>
public sealed record ProcessAppMinute(string Name,long CpuTime,long IoRead,long IoWrite,double MemoryByteSeconds,bool EverythingElse)
{
    /// <summary>GPU-seconds (utilization ÷ 100 × the GPU read interval); null when the minute had no GPU-covered time.</summary>
    public double? GpuSeconds{get;init;}
    /// <summary>Dedicated GPU memory bytes × seconds; null when the minute had no GPU-covered time.</summary>
    public double? GpuMemoryByteSeconds{get;init;}
}

/// <summary>One closed minute of per-application activity. CoveredSeconds is the total of the scan intervals that contributed.</summary>
public sealed record ProcessMinuteBatch(DateTimeOffset Minute,double CoveredSeconds,int ProcessorCount,bool Complete,string Segment,IReadOnlyList<ProcessAppMinute> Applications)
{
    /// <summary>The total of the GPU read intervals that contributed; 0 when the minute had no GPU data.</summary>
    public double GpuCoveredSeconds{get;init;}
}

/// <summary>Folds per-poll process activity into per-minute records: the union of the top ten applications by CPU, memory, I/O and GPU, plus Everything else.
/// A pure policy object like <see cref="UsageMinuteFolder"/>: no timers, threads or storage.</summary>
public sealed class ProcessMinuteFolder
{
    private sealed class Totals
    {
        public string Name="";
        public long Cpu,Read,Write;
        public double Memory,Gpu,GpuMemory;
        public long IoSum=>Read+Write;
    }

    private readonly Dictionary<string,Totals> apps=new(StringComparer.Ordinal);
    private readonly Totals other=new(){Name=ProcessHistory.EverythingElse};
    private DateTimeOffset? minute;
    private double? previousSeconds,interval;
    private DateTimeOffset? previousTimestamp;
    private double previousInterval=1;
    private bool broken,intervalChanged;
    private double covered,gpuCovered;
    private int publications,processorCount;
    private string segment=NewSegment(),minuteSegment="";

    private static string NewSegment()=>Guid.NewGuid().ToString("N");

    /// <summary>Marks the open minute as discontinuous and starts a new segment for later activities.</summary>
    public void Break()
    {
        broken=true;segment=NewSegment();
        previousSeconds=null;previousTimestamp=null;
    }

    /// <summary>Folds one activity. Returns the previous minute when this activity starts a later one; otherwise null.
    /// Only available activities with a valid interval add; any other kind is ignored.</summary>
    public ProcessMinuteBatch? Observe(ProcessActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if(activity.Kind!=ProcessActivityKind.Available)return null;
        if(!double.IsFinite(activity.MonotonicSeconds)){Break();return null;}
        if(previousSeconds is{}last&&activity.MonotonicSeconds<=last)return null;
        double activityInterval=PollingPolicy.Normalize(activity.IntervalSeconds);
        if(previousSeconds is{}before&&previousTimestamp is{}beforeStamp&&
            (activity.MonotonicSeconds-before>Math.Max(5,Math.Max(previousInterval,activityInterval)+3)||
             Math.Abs((activity.Timestamp-beforeStamp).TotalSeconds-(activity.MonotonicSeconds-before))>3))Break();

        var current=UsageMetrics.Floor(activity.Timestamp);
        ProcessMinuteBatch? closed=null;
        if(minute is{}open&&current!=open)
        {
            if(current<open)Break();
            closed=Close();
        }
        if(minute is null){minute=current;minuteSegment=segment;}

        if(interval is{}known&&known!=activityInterval)intervalChanged=true;
        interval=activityInterval;
        processorCount=activity.ProcessorCount;

        if(activity.DeltaSeconds is{}seconds&&seconds>0&&double.IsFinite(seconds))Fold(activity,seconds);

        previousSeconds=activity.MonotonicSeconds;previousTimestamp=activity.Timestamp;previousInterval=activityInterval;
        return closed;
    }

    /// <summary>Closes the open minute early, for Quit or a recording pause. Call <see cref="Break"/> afterwards when later activities should start a discontinuous minute.</summary>
    public ProcessMinuteBatch? Flush()
    {
        if(minute is null)return null;
        var batch=Close();
        segment=NewSegment();previousSeconds=null;previousTimestamp=null;
        return batch;
    }

    private void Fold(ProcessActivity activity,double seconds)
    {
        covered+=seconds;publications++;
        double gpuInterval=activity.GpuIntervalSeconds is{}g&&g>0&&double.IsFinite(g)?g:0;
        gpuCovered+=gpuInterval;
        var named=gpuInterval>0?new Dictionary<Totals,List<ProcessSample>>():null;
        foreach(var p in activity.Processes)
        {
            if(p is null)continue;
            Totals target;
            if(ProcessHistory.SavedName(p.Name) is not{} name)
            {
                target=other;
                target.Gpu+=p.Deltas?.GpuSeconds??0;
                target.GpuMemory+=p.Deltas?.GpuMemoryByteSeconds??0;
            }
            else
            {
                string key=ProcessGrouping.Key(name);
                if(!apps.TryGetValue(key,out target!))apps[key]=target=new Totals{Name=name};
                if(named is not null)
                {
                    if(!named.TryGetValue(target,out var members))named[target]=members=[];
                    members.Add(p);
                }
            }
            target.Cpu+=p.Deltas?.CpuTime??0;
            target.Read+=p.Deltas?.IoReadBytes??0;
            target.Write+=p.Deltas?.IoWriteBytes??0;
            target.Memory+=p.Deltas?.MemoryByteSeconds??0;
        }
        if(named is not null)
            foreach(var (t,members) in named)
            {
                if(ProcessGrouping.GpuUse(members).Percent is{} percent)t.Gpu+=percent/100*gpuInterval;
                // Summed as double: LINQ's long Sum is checked, and an overflow here would fail the history worker.
                t.GpuMemory+=members.Sum(m=>(double)(m.GpuDedicatedBytes??0))*gpuInterval;
            }
        var omitted=activity.Omitted.Deltas;
        other.Cpu+=omitted.CpuTime??0;other.Read+=omitted.IoReadBytes??0;other.Write+=omitted.IoWriteBytes??0;
        other.Memory+=omitted.MemoryByteSeconds;
        other.Gpu+=omitted.GpuSeconds??0;other.GpuMemory+=omitted.GpuMemoryByteSeconds??0;
    }

    private static IEnumerable<string> Top(IEnumerable<KeyValuePair<string,Totals>> items,Func<Totals,double> value)
        =>items.Select(i=>(i.Key,Value:value(i.Value))).Where(i=>i.Value>0)
            .OrderByDescending(i=>i.Value).ThenBy(i=>i.Key,StringComparer.Ordinal).Take(ProcessHistory.TopPerMetric).Select(i=>i.Key);

    private ProcessMinuteBatch? Close()
    {
        var at=minute!.Value;
        ProcessMinuteBatch? batch=null;
        if(covered>0)
        {
            var keep=new HashSet<string>(StringComparer.Ordinal);
            keep.UnionWith(Top(apps,t=>t.Cpu));
            keep.UnionWith(Top(apps,t=>t.Memory/covered));
            keep.UnionWith(Top(apps,t=>t.IoSum));
            keep.UnionWith(Top(apps,t=>t.Gpu));
            bool hasGpu=gpuCovered>0;
            var rows=new List<ProcessAppMinute>();
            long cpu=other.Cpu,read=other.Read,write=other.Write;double memory=other.Memory,gpu=other.Gpu,gpuMemory=other.GpuMemory;
            foreach(var pair in apps.OrderByDescending(p=>p.Value.Cpu).ThenBy(p=>p.Key,StringComparer.Ordinal))
            {
                var t=pair.Value;
                if(keep.Contains(pair.Key))rows.Add(new(t.Name,t.Cpu,t.Read,t.Write,t.Memory,false){GpuSeconds=hasGpu?t.Gpu:null,GpuMemoryByteSeconds=hasGpu?t.GpuMemory:null});
                else{cpu+=t.Cpu;read+=t.Read;write+=t.Write;memory+=t.Memory;gpu+=t.Gpu;gpuMemory+=t.GpuMemory;}
            }
            rows.Add(new(ProcessHistory.EverythingElse,cpu,read,write,memory,true){GpuSeconds=hasGpu?gpu:null,GpuMemoryByteSeconds=hasGpu?gpuMemory:null});
            bool continuous=!broken&&!intervalChanged;
            batch=new ProcessMinuteBatch(at,covered,processorCount,continuous&&publications>=UsageMetrics.ExpectedSamples(interval??1)-1,minuteSegment,rows){GpuCoveredSeconds=gpuCovered};
        }
        apps.Clear();other.Cpu=other.Read=other.Write=0;other.Memory=other.Gpu=other.GpuMemory=0;
        minute=null;interval=null;broken=false;intervalChanged=false;covered=gpuCovered=0;publications=0;
        return batch;
    }
}
