namespace DeadWare.Core;
public enum DiskTraceState{Off,Starting,Running,Failed,Paused}
public sealed record DiskProcessTotal(int Pid,long Generation,long BytesRead,long BytesWritten);
public sealed record DiskProcessRate(int Pid,long Generation,double Read,double Write);
public sealed record DiskTake(DiskTraceState State,string Status,bool Lossy,bool Warm,IReadOnlyList<DiskProcessRate> Rates,double UnattributedShare,long UnattributedEvents)
{
    public static DiskTake Off{get;}=new(DiskTraceState.Off,"",false,false,Array.Empty<DiskProcessRate>(),0,0);
}
/// <summary>Turns cumulative per-process disk totals into rates over a window of at least <paramref name="minimumSeconds"/>.</summary>
public sealed class DiskRateWindow(double minimumSeconds=2)
{
    private sealed record Observation(double Now,IReadOnlyList<DiskProcessTotal> Totals,long Events,long Unattributed);
    private readonly List<Observation> observations=new();
    private double origin=double.NegativeInfinity;
    /// <summary>Forgets every observation; observations stamped before <paramref name="now"/> are ignored afterwards.</summary>
    public void Reset(double now){observations.Clear();origin=now;}
    public void Observe(double now,IReadOnlyList<DiskProcessTotal> totals,long events,long unattributed)
    {
        if(now<origin||observations.Count>0&&now<=observations[^1].Now)return;
        observations.Add(new(now,totals,events,unattributed));
        int baseline=BaselineIndex();
        if(baseline>0)observations.RemoveRange(0,baseline);
    }
    // Newest observation at least minimumSeconds older than the latest, or -1.
    private int BaselineIndex()
    {
        if(observations.Count<2)return -1;
        double latest=observations[^1].Now;
        for(int i=observations.Count-2;i>=0;i--) if(latest-observations[i].Now>=minimumSeconds)return i;
        return -1;
    }
    public bool Warm=>BaselineIndex()>=0;
    public IReadOnlyList<DiskProcessRate> Rates()
    {
        int baseline=BaselineIndex();
        if(baseline<0)return Array.Empty<DiskProcessRate>();
        var older=observations[baseline];var latest=observations[^1];
        double seconds=latest.Now-older.Now;
        var before=older.Totals.ToDictionary(t=>(t.Pid,t.Generation));
        var rates=new List<DiskProcessRate>(latest.Totals.Count);
        foreach(var t in latest.Totals)
        {
            before.TryGetValue((t.Pid,t.Generation),out var b);
            rates.Add(new(t.Pid,t.Generation,Math.Max(0,t.BytesRead-(b?.BytesRead??0))/seconds,Math.Max(0,t.BytesWritten-(b?.BytesWritten??0))/seconds));
        }
        return rates;
    }
    public double UnattributedShare
    {
        get
        {
            int baseline=BaselineIndex();
            if(baseline<0)return 0;
            long events=observations[^1].Events-observations[baseline].Events;
            return events<=0?0:Math.Clamp((double)(observations[^1].Unattributed-observations[baseline].Unattributed)/events,0,1);
        }
    }
}
