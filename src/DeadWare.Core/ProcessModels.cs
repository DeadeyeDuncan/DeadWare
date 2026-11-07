namespace DeadWare.Core;
/// <summary>One process from a scan. <see cref="Name"/> is the raw image name, "" when empty.</summary>
public sealed record ProcessScanEntry(int Pid,long Generation,string Name,int SessionId,int Threads,long PrivateWorkingSet,long KernelTime,long UserTime,long ReadTransfer,long WriteTransfer)
{
    public long CpuTime=>KernelTime+UserTime;
}
public sealed record ProcessScan(DateTimeOffset UtcTime,double MonotonicSeconds,double PollingIntervalSeconds,IReadOnlyList<ProcessScanEntry> Processes,int Threads,int BufferBytes);

public enum ProcessGap{None,FirstSample,NotMatched}
/// <summary>Exact counter deltas over one scan interval. CPU is in 100 ns units; a null counter is unknown, never zero.</summary>
public sealed record ProcessDeltas(long? CpuTime,long? IoReadBytes,long? IoWriteBytes,double MemoryByteSeconds,double Seconds)
{
    /// <summary>GPU % ÷ 100 × the joined GPU read's interval; null when either is unknown.</summary>
    public double? GpuSeconds{get;init;}
    /// <summary>Dedicated GPU memory bytes × the joined GPU read's interval; null when either is unknown.</summary>
    public double? GpuMemoryByteSeconds{get;init;}
    public static ProcessDeltas Zero{get;}=new(0,0,0,0,0){GpuSeconds=0,GpuMemoryByteSeconds=0};
    public ProcessDeltas Add(ProcessDeltas other)=>new(Sum(CpuTime,other.CpuTime),Sum(IoReadBytes,other.IoReadBytes),Sum(IoWriteBytes,other.IoWriteBytes),MemoryByteSeconds+other.MemoryByteSeconds,Seconds+other.Seconds)
        {GpuSeconds=Sum(GpuSeconds,other.GpuSeconds),GpuMemoryByteSeconds=Sum(GpuMemoryByteSeconds,other.GpuMemoryByteSeconds)};
    static long? Sum(long? a,long? b)=>a is null&&b is null?null:(a??0)+(b??0);
    static double? Sum(double? a,double? b)=>a is null&&b is null?null:(a??0)+(b??0);
}
public sealed record ProcessSample(int Pid,long Generation,string Name,int SessionId,double? CpuPercent,long PrivateBytes,double? IoReadPerSecond,double? IoWritePerSecond,ProcessGap RateGap,ProcessDeltas? Deltas)
{
    public double? DownloadPerSecond{get;init;}
    public double? UploadPerSecond{get;init;}
    public ProcessGap NetworkGap{get;init;}
    public double? DiskReadPerSecond{get;init;}
    public double? DiskWritePerSecond{get;init;}
    public ProcessGap DiskGap{get;init;}
    /// <summary>The busiest ready engine's clamped percent (<see cref="GpuProcessJoin"/>); null exactly when <see cref="GpuEngines"/> is null.</summary>
    public double? GpuPercent{get;init;}
    /// <summary>The busiest engine's label ("GPU 1 · 3D"), "" for a known 0 %.</summary>
    public string? GpuEngine{get;init;}
    /// <summary>The busiest engine's adapter description, for the tooltip only; null when DXGI did not name it.</summary>
    public string? GpuAdapter{get;init;}
    public IReadOnlyList<GpuEngineUse>? GpuEngines{get;init;}
    public long? GpuDedicatedBytes{get;init;}
    public long? GpuSharedBytes{get;init;}
    public ProcessGap GpuGap{get;init;}
}
/// <summary>Processes cut by the published cap, kept as exact totals so the sums still add up.</summary>
public sealed record ProcessOmitted(int Count,long PrivateBytes,ProcessDeltas Deltas)
{
    public static ProcessOmitted None{get;}=new(0,0,ProcessDeltas.Zero);
}
public sealed record ProcessColumnState(bool Available,string Status)
{
    public static ProcessColumnState Ready{get;}=new(true,"");
}
public enum ProcessActivityKind{None,Off,Unavailable,Available}
public sealed record ProcessActivity(ProcessActivityKind Kind,long Sequence,DateTimeOffset Timestamp,double MonotonicSeconds,double IntervalSeconds,int ProcessorCount,IReadOnlyList<ProcessSample> Processes,ProcessOmitted Omitted,int TotalProcesses,double? DeltaSeconds)
{
    public ProcessColumnState Scan{get;init;}=ProcessColumnState.Ready;
    public ProcessColumnState Disk{get;init;}=ProcessColumnState.Ready;
    public ProcessColumnState Network{get;init;}=ProcessColumnState.Ready;
    public ProcessColumnState Gpu{get;init;}=ProcessColumnState.Ready;
    /// <summary>The joined GPU read's interval (<see cref="GpuProcessJoin.IntervalSeconds"/>); null when no read was joined or it adds no GPU time.</summary>
    public double? GpuIntervalSeconds{get;init;}
    /// <summary>Loop placement only: the joined read is older than two loop periods, so its GPU values are a stale reading.</summary>
    public bool GpuStale{get;init;}
    /// <summary>Set only with diagnostic timings, for <see cref="ProcessSummary.Gpu"/>.</summary>
    public GpuCounterSummary? GpuSummary{get;init;}
    public int NetworkMisses{get;init;}
    public int DiskMisses{get;init;}
    public long UnattributedDiskEvents{get;init;}
    public static ProcessActivity None{get;}=Empty(ProcessActivityKind.None);
    public static ProcessActivity Off{get;}=Empty(ProcessActivityKind.Off);
    public static ProcessActivity Unavailable(string status)=>Empty(ProcessActivityKind.Unavailable) with{Scan=new(false,status)};
    static ProcessActivity Empty(ProcessActivityKind kind)=>new(kind,0,DateTimeOffset.MinValue,0,0,0,Array.Empty<ProcessSample>(),ProcessOmitted.None,0,null);
}
/// <summary>
/// For diagnostics, the poll's GPU counter read: instances read and skipped, adapters, the read time (null unless a read ran on the poll, or the
/// loop's read was taken fresh), the column state and the placement. Counts, states and timings only: never a process name or adapter description.
/// </summary>
public sealed record GpuCounterSummary(int Instances,int Skipped,int Adapters,double? ReadMilliseconds,ProcessColumnState State,string Placement)
{
    public const string EveryPoll="every poll",OwnLoop="own loop";
}
/// <summary>Counts and states only, for diagnostics: never a process name or path.</summary>
public sealed record ProcessSummary(string State,int Processes,int Omitted,ProcessColumnState Scan,ProcessColumnState Disk,ProcessColumnState Network,int NetworkMisses,int DiskMisses,long UnattributedDiskEvents)
{
    /// <summary>Null unless the activity is available and carries a summary.</summary>
    public GpuCounterSummary? Gpu{get;init;}
    public static ProcessSummary From(ProcessActivity activity)=>new(activity.Kind.ToString(),activity.Processes.Count,activity.Omitted.Count,activity.Scan,activity.Disk,activity.Network,activity.NetworkMisses,activity.DiskMisses,activity.UnattributedDiskEvents)
        {Gpu=activity.GpuSummary};
}
public sealed record TrackedScan(ProcessScan Scan,IReadOnlyList<ProcessSample> Processes,ProcessOmitted Omitted,double? DeltaSeconds,int TotalProcesses,IReadOnlyDictionary<int,ProcessIdentity> Index)
{
    public ProcessColumnState Gpu{get;init;}=ProcessColumnState.Ready;
    /// <summary>The joined GPU read's interval (<see cref="GpuProcessJoin.IntervalSeconds"/>); null when no read was joined or it adds no GPU time.</summary>
    public double? GpuIntervalSeconds{get;init;}
}
