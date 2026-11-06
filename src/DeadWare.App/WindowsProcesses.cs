using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.App;
/// <summary>One whole-system process scan per call; owned by one thread.</summary>
internal interface IProcessSource
{
    int ProcessorCount {get;}
    /// <summary>Null when the scan failed or is waiting to retry; <see cref="Failure"/> then says why.</summary>
    ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds);
    /// <summary>The hardware-status note: the failure text once scans have failed for 60 s, otherwise "".</summary>
    string Status {get;}
    /// <summary>The current run of failures' latest failure text, published at once; "" after a success.</summary>
    string Failure {get;}
}

/// <summary>NtQuerySystemInformation(SystemProcessInformation) into a reused pinned buffer, parsed by <see cref="ProcessSnapshotParser"/>. Never throws to the collector.</summary>
internal sealed class WindowsProcesses : IProcessSource
{
    internal const int InitialBufferBytes=1<<20,GrowthBytes=256*1024,MaximumBufferBytes=32<<20,MaximumRetries=3;
    internal const double RetrySeconds=60,StatusAfterSeconds=60;
    internal const string NeedsX64="Process scan needs 64-bit Windows";
    private const int SystemProcessInformation=5;
    private const uint StatusInfoLengthMismatch=0xC0000004;
    /// <summary>Fills <paramref name="buffer"/> and returns the NTSTATUS; <paramref name="returned"/> is the bytes written, or needed on a length mismatch.</summary>
    internal delegate uint Query(byte[] buffer,out int returned);
    private readonly Query query;
    private byte[]? buffer;
    private IReadOnlyDictionary<(int Pid,long Generation),string>? knownNames;
    private double now;
    private double? failingSince,lastFailure;
    private string failure="";

    public WindowsProcesses():this(QuerySystem) {}
    internal WindowsProcesses(Query query)=>this.query=query;

    public int ProcessorCount {get {int count=(int)GetActiveProcessorCount(0xFFFF);return count>0?count:Environment.ProcessorCount;}}
    public string Status=>X64?PacedStatus(now,failingSince,failure):NeedsX64;
    public string Failure=>X64?failure:NeedsX64;
    // The parser reads the x64 SYSTEM_PROCESS_INFORMATION layout only.
    private static bool X64=>IntPtr.Size==8;

    internal static int? NextBufferSize(int returnedBytes)
    {
        long next=(long)returnedBytes+GrowthBytes;
        return next>MaximumBufferBytes?null:(int)next;
    }
    internal static bool ShouldAttempt(double now,double? lastFailure)=>lastFailure is not double last||now-last>=RetrySeconds;
    internal static string FailureStatus(uint ntStatus)=>$"Process scan unavailable (NTSTATUS 0x{ntStatus:X8})";
    internal static string PacedStatus(double now,double? failingSince,string failure)=>failingSince is double since&&now-since>=StatusAfterSeconds?failure:"";

    public ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds)
    {
        now=monotonicSeconds;
        if(!X64||!ShouldAttempt(monotonicSeconds,lastFailure))return null;
        var error=TryScan(monotonicSeconds,pollingIntervalSeconds,out var scan);
        if(error is null){failingSince=null;lastFailure=null;failure="";return scan;}
        failingSince??=monotonicSeconds;lastFailure=monotonicSeconds;failure=error;
        return null;
    }

    private string? TryScan(double monotonicSeconds,double pollingIntervalSeconds,out ProcessScan? scan)
    {
        scan=null;
        buffer??=GC.AllocateUninitializedArray<byte>(InitialBufferBytes,pinned:true);
        for(int attempt=0;;attempt++)
        {
            uint status;int returned;
            try {status=query(buffer,out returned);}
            catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException) {return $"Process scan unavailable ({e.GetType().Name})";}
            // A returned size below the current buffer still grows it, so every retry makes progress.
            if(status==StatusInfoLengthMismatch&&attempt<MaximumRetries&&NextBufferSize(Math.Max(returned,buffer.Length)) is int size)
            {
                buffer=GC.AllocateUninitializedArray<byte>(size,pinned:true);
                continue;
            }
            if(status!=0)return FailureStatus(status);
            var data=buffer.AsSpan(0,Math.Clamp(returned,0,buffer.Length));
            ulong address=(ulong)Marshal.UnsafeAddrOfPinnedArrayElement(buffer,0);
            if(!ProcessSnapshotParser.TryParse(data,address,knownNames,out var entries,out int threads,out var parseError))return $"Process scan unavailable ({parseError})";
            var names=new Dictionary<(int Pid,long Generation),string>(entries.Count);
            foreach(var e in entries)names[(e.Pid,e.Generation)]=e.Name;
            knownNames=names;
            scan=new ProcessScan(DateTimeOffset.UtcNow,monotonicSeconds,pollingIntervalSeconds,entries,threads,buffer.Length);
            return null;
        }
    }

    private static uint QuerySystem(byte[] buffer,out int returned)
    {
        uint status=NtQuerySystemInformation(SystemProcessInformation,Marshal.UnsafeAddrOfPinnedArrayElement(buffer,0),(uint)buffer.Length,out uint length);
        returned=(int)Math.Min(length,int.MaxValue);
        return status;
    }
    [DllImport("ntdll.dll")] private static extern uint NtQuerySystemInformation(int informationClass,IntPtr information,uint length,out uint returned);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
}
