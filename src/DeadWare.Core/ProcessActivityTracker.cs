namespace DeadWare.Core;
/// <summary>Turns successive process scans into CPU %, memory, I/O rates and exact deltas. Not thread-safe: one scan loop owns it.</summary>
public sealed class ProcessActivityTracker(int processorCount)
{
    public const int MaximumPublished=4096;
    const double TicksPerSecond=10_000_000;
    readonly record struct Baseline(long CpuTime,long Read,long Write);
    Dictionary<(int Pid,long Generation),Baseline> baselines=new();
    ProcessScan? previous;
    public int TrackedCount=>baselines.Count;
    public void Reset(){baselines=new();previous=null;}
    public TrackedScan Update(ProcessScan scan)
    {
        double? dt=null;
        if(previous is not null)
        {
            double elapsed=scan.MonotonicSeconds-previous.MonotonicSeconds;
            if(elapsed>0&&elapsed<=PollingPolicy.FreshnessSeconds(Math.Max(previous.PollingIntervalSeconds,scan.PollingIntervalSeconds)))dt=elapsed;
        }
        // A process missing from the previous scan only counts from zero if it started within 1 s of that scan.
        long startedSince=previous is null?long.MinValue:previous.UtcTime.UtcTicks-(long)TicksPerSecond;
        var next=new Dictionary<(int,long),Baseline>(scan.Processes.Count);
        var index=new Dictionary<int,ProcessIdentity>(scan.Processes.Count);
        var samples=new List<ProcessSample>(scan.Processes.Count);
        foreach(var e in scan.Processes)
        {
            index[e.Pid]=new ProcessIdentity(e.Pid,e.Generation,e.Name);
            next[(e.Pid,e.Generation)]=new Baseline(e.CpuTime,e.ReadTransfer,e.WriteTransfer);
            double? cpu=null,read=null,write=null;
            ProcessDeltas? deltas=null;
            if(dt is double seconds)
            {
                Baseline b=default;bool known=baselines.TryGetValue((e.Pid,e.Generation),out b);
                bool usable=known||e.Generation>=startedSince;
                bool backwards=known&&(e.CpuTime<b.CpuTime||e.ReadTransfer<b.Read||e.WriteTransfer<b.Write);
                long? dCpu=null,dRead=null,dWrite=null;
                if(usable&&!backwards)
                {
                    dCpu=e.CpuTime-b.CpuTime;dRead=e.ReadTransfer-b.Read;dWrite=e.WriteTransfer-b.Write;
                    cpu=dCpu.Value/(seconds*TicksPerSecond*processorCount)*100;
                    read=dRead.Value/seconds;write=dWrite.Value/seconds;
                }
                deltas=new ProcessDeltas(dCpu,dRead,dWrite,e.PrivateWorkingSet*seconds,seconds);
            }
            samples.Add(new ProcessSample(e.Pid,e.Generation,e.Name,e.SessionId,cpu,e.PrivateWorkingSet,read,write,cpu is null?ProcessGap.FirstSample:ProcessGap.None,deltas));
        }
        baselines=next;
        previous=scan;
        var omitted=ProcessOmitted.None;
        IReadOnlyList<ProcessSample> published=samples;
        if(samples.Count>MaximumPublished)
        {
            var ranked=samples.OrderByDescending(s=>s.CpuPercent??0).ThenByDescending(s=>(s.IoReadPerSecond??0)+(s.IoWritePerSecond??0))
                .ThenByDescending(s=>s.PrivateBytes).ThenBy(s=>s.Pid).ToList();
            published=ranked.GetRange(0,MaximumPublished);
            var rest=ranked.GetRange(MaximumPublished,ranked.Count-MaximumPublished);
            // Unknown stays unknown: with a valid Δt the counters start null and only known deltas add in.
            var sum=dt is null?ProcessDeltas.Zero:new ProcessDeltas(null,null,null,0,0);
            foreach(var s in rest)if(s.Deltas is not null)sum=sum.Add(s.Deltas);
            omitted=new ProcessOmitted(rest.Count,rest.Sum(s=>s.PrivateBytes),sum with{Seconds=dt??0});
        }
        return new TrackedScan(scan,published,omitted,dt,samples.Count,index);
    }
}
