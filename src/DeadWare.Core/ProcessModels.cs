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
    public static ProcessDeltas Zero{get;}=new(0,0,0,0,0);
    public ProcessDeltas Add(ProcessDeltas other)=>new(Sum(CpuTime,other.CpuTime),Sum(IoReadBytes,other.IoReadBytes),Sum(IoWriteBytes,other.IoWriteBytes),MemoryByteSeconds+other.MemoryByteSeconds,Seconds+other.Seconds);
    static long? Sum(long? a,long? b)=>a is null&&b is null?null:(a??0)+(b??0);
}
public sealed record ProcessSample(int Pid,long Generation,string Name,int SessionId,double? CpuPercent,long PrivateBytes,double? IoReadPerSecond,double? IoWritePerSecond,ProcessGap RateGap,ProcessDeltas? Deltas)
{
    public double? DownloadPerSecond{get;init;}
    public double? UploadPerSecond{get;init;}
    public ProcessGap NetworkGap{get;init;}
    public double? DiskReadPerSecond{get;init;}
    public double? DiskWritePerSecond{get;init;}
    public ProcessGap DiskGap{get;init;}
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
    public int NetworkMisses{get;init;}
    public int DiskMisses{get;init;}
    public long UnattributedDiskEvents{get;init;}
    public static ProcessActivity None{get;}=Empty(ProcessActivityKind.None);
    public static ProcessActivity Off{get;}=Empty(ProcessActivityKind.Off);
    public static ProcessActivity Unavailable(string status)=>Empty(ProcessActivityKind.Unavailable) with{Scan=new(false,status)};
    static ProcessActivity Empty(ProcessActivityKind kind)=>new(kind,0,DateTimeOffset.MinValue,0,0,0,Array.Empty<ProcessSample>(),ProcessOmitted.None,0,null);
}
public sealed record TrackedScan(ProcessScan Scan,IReadOnlyList<ProcessSample> Processes,ProcessOmitted Omitted,double? DeltaSeconds,int TotalProcesses,IReadOnlyDictionary<int,ProcessIdentity> Index);
