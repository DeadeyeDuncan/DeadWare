using System.Diagnostics;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
namespace DeadWare.App;

/// <summary>Captures only process lifecycle and TCP/UDP byte counts. No payloads, endpoints, file or disk providers.</summary>
internal sealed class EtwNetwork : IAsyncDisposable
{
    private readonly bool captureDiagnostics;
    private DiagnosticFailure? failure;
    public DiagnosticFailure? Failure=>Volatile.Read(ref failure);
    public EtwNetwork(bool captureDiagnostics=false) {this.captureDiagnostics=captureDiagnostics;}
    private readonly ProcessAttribution attribution=new(ResolveProcess);
    private readonly object lifecycleGate=new();
    private TraceEventSession? session;
    private Task? processing;
    private volatile string status="Starting application network accounting…";
    private volatile bool stopping;
    private volatile bool active;
    private long unattributed;
    private double lastReconciliation=double.NegativeInfinity;
    private int disposalStarted;
    public string Status=>status;
    public void Start()
    {
        lock(lifecycleGate)
        {
        if(stopping)return;
        if(!Collector.IsElevated) {status="Per-app traffic unavailable: administrator access is required. Interface totals still work.";return;}
        string phase="Create session";
        try
        {
            // Unique name prevents this app from attaching to or stopping another tool's trace.
            session=new TraceEventSession($"DeadWare-{Environment.ProcessId}-{Guid.NewGuid():N}") {StopOnDispose=true};
            // Source access starts a regular named session. Kernel mode must be configured first.
            phase="Enable kernel provider";
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP|KernelTraceEventParser.Keywords.Process);
            phase="Attach source callbacks";
            var parser=session.Source.Kernel;
            parser.ProcessStart+=data=>attribution.Refresh(data.ProcessID);
            parser.ProcessDCStart+=data=>attribution.Refresh(data.ProcessID);
            parser.ProcessStop+=data=>attribution.Stop(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks);
            parser.TcpIpRecv+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.TcpIpSend+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.TcpIpRecvIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.TcpIpSendIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.UdpIpRecv+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.UdpIpSend+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.UdpIpRecvIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.UdpIpSendIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            status="Per-app accounting active · TCP/UDP · IPv4/IPv6 · session totals are estimates";
            active=true;
            processing=Task.Run(()=>
            {
                try {session.Source.Process();if(!stopping) status="Per-app ETW collection stopped unexpectedly. Restart DeadWare to retry.";}
                catch(Exception e)
                {
                    if(!stopping){failure=DiagnosticFailure.Capture("Process event stream",e,captureDiagnostics);status=$"Per-app ETW collection failed ({e.GetType().Name}). Restart DeadWare to retry.";}
                }
                finally {active=false;}
            });
        }
        catch(Exception e)
        {
            failure=DiagnosticFailure.Capture(phase,e,captureDiagnostics);
            status=$"Per-app ETW unavailable ({e.GetType().Name}). Local diagnostics can identify the startup failure.";
            session?.Dispose();session=null;
        }
        }
    }
    private static ProcessIdentity? ResolveProcess(int pid)=>NativeProcessIdentity.Read(pid);
    private void Record(int pid,long eventTime,int bytes,bool send)
    {
        if(!attribution.Record(pid,eventTime,send?0:bytes,send?bytes:0)) Interlocked.Increment(ref unattributed);
    }
    public IReadOnlyList<ProcessTraffic> Take(double now)
    {
        if(!active) return [];
        if(session is not null)
        {
            try
            {
                long lost=session.EventsLost;
                attribution.ObserveLoss(lost);
                if(lost>0) status=$"ETW lost {lost:N0} events · attribution reset; totals cover activity since the latest loss";
                else if(Interlocked.Read(ref unattributed)>0) status=$"ETW active · {Interlocked.Read(ref unattributed):N0} events could not be attributed · per-app totals are incomplete";
            }
            catch(Exception e) {failure=DiagnosticFailure.Capture("Query event loss",e,captureDiagnostics);status=$"ETW status unavailable ({e.GetType().Name})";}
        }
        // A lost lifecycle event can otherwise leave an exited/reused PID cached indefinitely.
        if(now-lastReconciliation>=5) {attribution.Reconcile();lastReconciliation=now;}
        return attribution.Take(now);
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposalStarted,1)!=0)return;
        TraceEventSession? ownedSession;Task? processingTask;
        lock(lifecycleGate)
        {
            stopping=true;active=false;
            ownedSession=session;processingTask=processing;
        }
        // Stop only our unique session, which unblocks the processing thread before disposing its source.
        try {ownedSession?.Stop();if(processingTask is not null) await processingTask.ConfigureAwait(false);}
        finally {ownedSession?.Dispose();}
    }
}
