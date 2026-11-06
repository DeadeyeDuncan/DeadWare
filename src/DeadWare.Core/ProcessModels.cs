namespace DeadWare.Core;
/// <summary>One process from a scan. <see cref="Name"/> is the raw image name, "" when empty.</summary>
public sealed record ProcessScanEntry(int Pid,long Generation,string Name,int SessionId,int Threads,long PrivateWorkingSet,long KernelTime,long UserTime,long ReadTransfer,long WriteTransfer)
{
    public long CpuTime=>KernelTime+UserTime;
}
public sealed record ProcessScan(DateTimeOffset UtcTime,double MonotonicSeconds,double PollingIntervalSeconds,IReadOnlyList<ProcessScanEntry> Processes,int Threads,int BufferBytes);
