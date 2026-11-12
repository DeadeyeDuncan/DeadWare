using System.Diagnostics;
using System.Globalization;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
namespace DeadWare.App;

/// <summary>A per-process disk trace: started and stopped by <see cref="DiskTraceController"/>, taken once per poll.</summary>
internal interface IDiskTrace : IAsyncDisposable
{
    void Start();
    Task StopAsync();
    DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index);
}

/// <summary>
/// Per-process physical disk transfers from a second kernel session. It never shares the network session or its
/// attribution, so disk event loss can never reset network attribution. No file names, paths or payloads.
/// </summary>
internal sealed class EtwDisk : IDiskTrace
{
    public const int LossStormTakes=10;
    public static readonly TimeSpan StormPause=TimeSpan.FromMinutes(5);
    /// <summary>TraceEvent's own default: the larger of 64 MB and 2 MB per logical processor.</summary>
    public static int DefaultBufferSizeMB=>Math.Max(64,2*Environment.ProcessorCount);
    public static int BufferSizeMB=>DefaultBufferSizeMB*ProcessFeatures.DiskBufferMultiplier;
    internal const string StartingStatus="Starting disk tracing…";
    internal const string LossStatus="Windows dropped disk events";
    internal const string PausedStatus="Disk tracing paused for 5 minutes after Windows dropped events repeatedly";
    internal const string StoppedStatus="Disk tracing stopped unexpectedly";
    internal static string Unavailable(Exception e)=>$"Disk tracing unavailable ({e.GetType().Name})";
    internal static DiskTake Starting {get;}=new(DiskTraceState.Starting,StartingStatus,false,false,Array.Empty<DiskProcessRate>(),0,0);
    internal static DiskTake Paused {get;}=new(DiskTraceState.Paused,PausedStatus,false,false,Array.Empty<DiskProcessRate>(),0,0);
    internal static DiskTake Failed(string status)=>new(DiskTraceState.Failed,status,false,false,Array.Empty<DiskProcessRate>(),0,0);
    private const double ReconcileSeconds=5;
    private const string DiskPrefix="DeadWare-Disk-",NetworkPrefix="DeadWare-";

    private readonly int bufferSizeMB;
    private readonly ProcessAttribution attribution;
    private readonly DiskRateWindow window=new();
    private readonly Func<long>? readLoss;
    private readonly Action stopNative,disposeNative;
    // The outer gate makes attribution, the event counts, loss and the rate window one decision per take.
    private readonly object accountingGate=new(),lifecycleGate=new();
    private TraceEventSession? session;
    private Task? processing,stop,disposal;
    // Guarded by accountingGate.
    private DiskTraceState state=DiskTraceState.Starting;
    private string status=StartingStatus;
    private bool stopping,paused;
    private long observedLoss,events,unattributed;
    private int lossyRun;
    private double lastReconciliation=double.NegativeInfinity;
    private int resourcesDisposed;

    public EtwDisk(int bufferSizeMB)
    {
        this.bufferSizeMB=bufferSizeMB;attribution=new(NativeProcessIdentity.Read);
        readLoss=()=>Volatile.Read(ref session)?.EventsLost??0;stopNative=()=>Volatile.Read(ref session)?.Stop();disposeNative=()=>Volatile.Read(ref session)?.Dispose();
    }
    /// <summary>Test seam: already running, with no kernel session.</summary>
    internal EtwDisk(Func<int,ProcessIdentity?> resolve,Func<long>? readLoss=null,Action? stopNative=null,Action? disposeNative=null)
    {attribution=new(resolve);state=DiskTraceState.Running;status="";this.readLoss=readLoss;this.stopNative=stopNative??(()=>{});this.disposeNative=disposeNative??(()=>{});}

    public void Start()
    {
        lock(lifecycleGate)
        {
            if(stop is not null)return;
            // Best effort: an orphan that cannot be listed or stopped only costs its buffers until the next start.
            try {RemoveOrphans(TraceEventSession.GetActiveSessionNames(),IsRunningDeadWare,StopSession);}
            catch(Exception) {}
            try
            {
                var created=new TraceEventSession($"{DiskPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}") {StopOnDispose=true,BufferSizeMB=bufferSizeMB};
                Volatile.Write(ref session,created);
                created.EnableKernelProvider(KernelTraceEventParser.Keywords.DiskIO|KernelTraceEventParser.Keywords.Thread|KernelTraceEventParser.Keywords.Process);
                var parser=created.Source.Kernel;
                parser.ProcessStart+=data=>{lock(accountingGate)attribution.Refresh(data.ProcessID);};
                parser.ProcessDCStart+=data=>{lock(accountingGate)attribution.Refresh(data.ProcessID);};
                parser.ProcessStop+=data=>{lock(accountingGate)attribution.Stop(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks);};
                // ProcessID is TraceEvent's issuing-thread fix-up; -1 means it could not tell.
                parser.DiskIORead+=data=>RecordIo(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.TransferSize,write:false);
                parser.DiskIOWrite+=data=>RecordIo(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.TransferSize,write:true);
                lock(accountingGate){state=DiskTraceState.Running;status="";}
                processing=Task.Run(()=>
                {
                    try {created.Source.Process();Fail(StoppedStatus);}
                    catch(Exception e) {Fail($"Disk tracing failed ({e.GetType().Name})");}
                });
            }
            catch(Exception e)
            {
                Fail(Unavailable(e));
                Volatile.Read(ref session)?.Dispose();Volatile.Write(ref session,null);
            }
        }
    }
    // A stream that ends during a requested stop is not a failure.
    private void Fail(string reason) {lock(accountingGate) if(!stopping){state=DiskTraceState.Failed;status=reason;}}
    internal void RecordIo(int pid,long eventUtcTicks,int bytes,bool write)
    {
        lock(accountingGate)
        {
            events++;
            if(pid==-1){unattributed++;return;}
            attribution.Record(pid,eventUtcTicks,write?0:bytes,write?bytes:0);
        }
    }
    public DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index)
    {
        var take=TakeCore(now,index,out bool storm);
        // Outside the gate: the stop runs on the pool and takes the gates in its own order.
        if(storm)_=StopAsync();
        return take;
    }
    private DiskTake TakeCore(double now,IReadOnlyDictionary<int,ProcessIdentity> index,out bool storm)
    {
        storm=false;
        lock(accountingGate)
        {
            if(paused)return Paused;
            if(stopping)return DiskTake.Off;
            if(state==DiskTraceState.Starting)return Starting;
            if(state!=DiskTraceState.Running)return Failed(status);
            bool lossy;
            try
            {
                long lost=readLoss?.Invoke()??0;
                lossy=lost>observedLoss;
                if(lossy){observedLoss=lost;attribution.ObserveLoss(lost);}
            }
            catch(Exception e) {state=DiskTraceState.Failed;status=Unavailable(e);return Failed(status);}
            if(lossy)
            {
                // Totals were reset with the attribution, so the window starts over from this take.
                window.Reset(now);
                if(++lossyRun>=LossStormTakes){paused=true;storm=true;return Paused;}
            }
            else lossyRun=0;
            if(now-lastReconciliation>=ReconcileSeconds) {attribution.Reconcile(pid=>index.GetValueOrDefault(pid));lastReconciliation=now;}
            // Renamed at this boundary: the shared ledger's received/sent fields carry bytes read/written here.
            var traffic=attribution.Take(now);
            var totals=new DiskProcessTotal[traffic.Count];
            for(int i=0;i<totals.Length;i++) {var t=traffic[i];totals[i]=new(t.Pid,t.Generation,BytesRead:t.ReceivedBytes,BytesWritten:t.SentBytes);}
            window.Observe(now,totals,events,unattributed);
            if(lossy)return new(DiskTraceState.Running,LossStatus,true,false,Array.Empty<DiskProcessRate>(),0,unattributed);
            return new(DiskTraceState.Running,"",false,window.Warm,window.Rates(),window.UnattributedShare,unattributed);
        }
    }
    /// <summary>The PID in a DeadWare session name (<c>DeadWare-Disk-&lt;pid&gt;-*</c> or <c>DeadWare-&lt;pid&gt;-*</c>), else null.</summary>
    internal static int? OrphanPid(string sessionName)
    {
        string? rest=sessionName.StartsWith(DiskPrefix,StringComparison.Ordinal)?sessionName[DiskPrefix.Length..]
            :sessionName.StartsWith(NetworkPrefix,StringComparison.Ordinal)?sessionName[NetworkPrefix.Length..]:null;
        if(rest is null)return null;
        int dash=rest.IndexOf('-');
        return dash>0&&int.TryParse(rest.AsSpan(0,dash),NumberStyles.None,CultureInfo.InvariantCulture,out int pid)&&pid>0?pid:null;
    }
    /// <summary>Stops the DeadWare sessions a crashed DeadWare left behind; sessions of a running DeadWare are kept.</summary>
    internal static void RemoveOrphans(IEnumerable<string> sessionNames,Func<int,bool> isRunningDeadWare,Action<string> stopSession)
    {
        foreach(string name in sessionNames)
        {
            if(OrphanPid(name) is not {} pid||isRunningDeadWare(pid))continue;
            // One session that refuses to stop never keeps the others, or the new session, from starting.
            try {stopSession(name);}
            catch(Exception) {}
        }
    }
    private static bool IsRunningDeadWare(int pid)
    {
        if(pid==Environment.ProcessId)return true;
        try {using var process=Process.GetProcessById(pid);return process.ProcessName.Equals("DeadWare",StringComparison.OrdinalIgnoreCase);}
        catch(ArgumentException) {return false;}
        // Cannot tell, so never stop a session that might be live.
        catch(Exception) {return true;}
    }
    private static void StopSession(string name) {using var orphan=TraceEventSession.GetActiveSession(name);orphan?.Stop();}
    public Task StopAsync()
    {
        // Native stop never runs on the caller's thread.
        lock(lifecycleGate)return stop??=Task.Run(StopCoreAsync);
    }
    private async Task StopCoreAsync()
    {
        lock(accountingGate)stopping=true;
        try
        {
            stopNative();
            if(processing is not null)await processing.ConfigureAwait(false);
        }
        catch(Exception)
        {
            // A failed native Stop must still release the session; do not wait on a loop it never unblocked.
            ReleaseResources();
        }
    }
    private void ReleaseResources()
    {
        if(Interlocked.Exchange(ref resourcesDisposed,1)!=0)return;
        try {disposeNative();}
        catch(Exception) {}
        finally {Volatile.Write(ref session,null);}
    }
    public ValueTask DisposeAsync()
    {
        lock(lifecycleGate)return new(disposal??=DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try {await StopAsync().ConfigureAwait(false);}
        finally {ReleaseResources();}
    }
}

/// <summary>
/// Owns the disk trace's lifecycle. Every start and stop runs in order on one pool task, never on the poll thread, and
/// each step brings the trace to the latest request, so quick toggles end in the state of the last one.
/// <see cref="StopAsync"/> closes it to new starts: a queued start is dropped, and a running start finishes and is then stopped.
/// </summary>
internal sealed class DiskTraceController(Func<IDiskTrace> factory)
{
    private readonly object gate=new();
    // Guarded by gate.
    private Task tail=Task.CompletedTask;
    private Task? closing;
    private IDiskTrace? trace;
    private bool requested,closed;
    private double? pausedUntil,lastOpenMilliseconds;
    private string? failure;
    /// <summary>How long the latest completed start took, or null before any.</summary>
    public double? LastOpenMilliseconds {get{lock(gate)return lastOpenMilliseconds;}}
    /// <summary>The newest queued lifecycle step.</summary>
    internal Task Idle {get{lock(gate)return tail;}}
    public void Request(bool on)
    {
        lock(gate)
        {
            if(closed)return;
            requested=on;Enqueue(ApplyAsync);
        }
    }
    public Task StopAsync()
    {
        lock(gate) {closed=true;return closing??=Enqueue(ApplyAsync);}
    }
    public DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index)
    {
        IDiskTrace? current;
        lock(gate)
        {
            if(pausedUntil is {} until)
            {
                if(now<until)return EtwDisk.Paused;
                pausedUntil=null;Enqueue(ApplyAsync);
            }
            current=trace;
            if(current is null)return !requested||closed?DiskTake.Off:failure is not null?EtwDisk.Failed(failure):EtwDisk.Starting;
        }
        var take=current.Take(now,index);
        if(take.State==DiskTraceState.Paused)
            lock(gate) if(ReferenceEquals(trace,current))
            {
                // The trace already stopped itself; release it and restart once the pause ends, if still requested.
                trace=null;pausedUntil=now+EtwDisk.StormPause.TotalSeconds;Enqueue(()=>RetireAsync(current));
            }
        return take;
    }
    // Called under gate.
    private Task Enqueue(Func<Task> step)=>tail=tail.ContinueWith(_=>step(),CancellationToken.None,TaskContinuationOptions.DenyChildAttach,TaskScheduler.Default).Unwrap();
    private async Task ApplyAsync()
    {
        IDiskTrace? current;bool wanted;
        lock(gate) {wanted=Wanted;current=trace;}
        if(!wanted)
        {
            lock(gate) {failure=null;if(ReferenceEquals(trace,current))trace=null;}
            if(current is not null)await RetireAsync(current).ConfigureAwait(false);
            return;
        }
        if(current is not null)return;
        long started=Stopwatch.GetTimestamp();
        IDiskTrace? created=null;
        try {created=factory();created.Start();}
        catch(Exception e)
        {
            lock(gate)failure=EtwDisk.Unavailable(e);
            if(created is not null)await RetireAsync(created).ConfigureAwait(false);
            return;
        }
        double opened=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        bool keep;
        lock(gate) {lastOpenMilliseconds=opened;failure=null;keep=Wanted;if(keep)trace=created;}
        // The request changed, or disposal closed the lifecycle, while this start ran.
        if(!keep)await RetireAsync(created).ConfigureAwait(false);
    }
    private bool Wanted=>requested&&!closed&&pausedUntil is null;
    // A trace that fails to stop has nothing left to report; its session is released by its own disposal.
    private static async Task RetireAsync(IDiskTrace retired)
    {
        try {await retired.StopAsync().ConfigureAwait(false);}
        catch(Exception) {}
        try {await retired.DisposeAsync().ConfigureAwait(false);}
        catch(Exception) {}
    }
}
