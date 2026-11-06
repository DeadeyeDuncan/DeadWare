namespace DeadWare.Core;
public sealed record ProcessJoinResult(IReadOnlyList<ProcessSample> Processes,ProcessColumnState State,int Misses);
/// <summary>Joins per-app network and disk rates to scanned processes by (Pid, Generation).</summary>
public static class ProcessJoins
{
    public const int MinimumMatchedRows=4;
    public const string NetworkUnmatched="Per-app traffic could not be matched to processes";
    public const string DiskUnmatched="Per-app disk activity could not be matched to processes";
    public static ProcessJoinResult Network(IReadOnlyList<ProcessSample> processes,IReadOnlyList<ProcessTraffic> capture,bool captureAvailable,string captureStatus)
    {
        if(!captureAvailable)return new(Map(processes,p=>p with{DownloadPerSecond=null,UploadPerSecond=null,NetworkGap=ProcessGap.None}),new(false,captureStatus),0);
        var rows=Latest(capture,c=>c.Pid);
        CountMisses(processes,rows,(c,gen)=>c.Generation==gen,out int rowsInScan,out int misses);
        if(Tripped(rowsInScan,misses,0))return new(Map(processes,p=>p with{DownloadPerSecond=null,UploadPerSecond=null,NetworkGap=ProcessGap.None}),new(false,NetworkUnmatched),misses);
        return new(Map(processes,p=>
        {
            if(!rows.TryGetValue(p.Pid,out var c))return p with{DownloadPerSecond=0,UploadPerSecond=0,NetworkGap=ProcessGap.None};
            if(c.Generation!=p.Generation)return p with{DownloadPerSecond=null,UploadPerSecond=null,NetworkGap=ProcessGap.NotMatched};
            if(c.DownloadBytesPerSecond is null||c.UploadBytesPerSecond is null)return p with{DownloadPerSecond=null,UploadPerSecond=null,NetworkGap=ProcessGap.FirstSample};
            return p with{DownloadPerSecond=c.DownloadBytesPerSecond,UploadPerSecond=c.UploadBytesPerSecond,NetworkGap=ProcessGap.None};
        }),ProcessColumnState.Ready,misses);
    }
    public static ProcessJoinResult Disk(IReadOnlyList<ProcessSample> processes,DiskTake take)
    {
        if(take.State!=DiskTraceState.Running||take.Lossy)return new(Map(processes,Cleared),new(false,take.Status),0);
        if(!take.Warm)return new(Map(processes,p=>p with{DiskReadPerSecond=null,DiskWritePerSecond=null,DiskGap=ProcessGap.FirstSample}),ProcessColumnState.Ready,0);
        var rows=Latest(take.Rates,r=>r.Pid);
        CountMisses(processes,rows,(r,gen)=>r.Generation==gen,out int rowsInScan,out int misses);
        if(Tripped(rowsInScan,misses,take.UnattributedShare))return new(Map(processes,Cleared),new(false,DiskUnmatched),misses);
        return new(Map(processes,p=>
        {
            if(!rows.TryGetValue(p.Pid,out var r))return p with{DiskReadPerSecond=0,DiskWritePerSecond=0,DiskGap=ProcessGap.None};
            if(r.Generation!=p.Generation)return p with{DiskReadPerSecond=null,DiskWritePerSecond=null,DiskGap=ProcessGap.NotMatched};
            return p with{DiskReadPerSecond=r.Read,DiskWritePerSecond=r.Write,DiskGap=ProcessGap.None};
        }),ProcessColumnState.Ready,misses);
    }
    static ProcessSample Cleared(ProcessSample p)=>p with{DiskReadPerSecond=null,DiskWritePerSecond=null,DiskGap=ProcessGap.None};
    static bool Tripped(int rowsInScan,int misses,double unattributedShare)=>rowsInScan>=MinimumMatchedRows&&misses*2>rowsInScan||unattributedShare>0.5;
    static Dictionary<int,T> Latest<T>(IReadOnlyList<T> rows,Func<T,int> pid)
    {
        var map=new Dictionary<int,T>(rows.Count);
        foreach(var r in rows)map[pid(r)]=r;
        return map;
    }
    // Counts rows whose PID is in the scan and how many of those carry another generation.
    static void CountMisses<T>(IReadOnlyList<ProcessSample> processes,Dictionary<int,T> rows,Func<T,long,bool> sameGeneration,out int rowsInScan,out int misses)
    {
        var generations=new Dictionary<int,long>(processes.Count);
        foreach(var p in processes)generations[p.Pid]=p.Generation;
        rowsInScan=0;misses=0;
        foreach(var (pid,row) in rows)
            if(generations.TryGetValue(pid,out long gen)){rowsInScan++;if(!sameGeneration(row,gen))misses++;}
    }
    static IReadOnlyList<ProcessSample> Map(IReadOnlyList<ProcessSample> processes,Func<ProcessSample,ProcessSample> f)
    {
        var result=new ProcessSample[processes.Count];
        for(int i=0;i<result.Length;i++)result[i]=f(processes[i]);
        return result;
    }
}
