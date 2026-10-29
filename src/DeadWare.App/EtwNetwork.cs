using System.Net;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
namespace DeadWare.App;

internal sealed record NetworkCaptureSnapshot(IReadOnlyList<ProcessTraffic> Processes,ConnectionSample Connections);

/// <summary>Captures process lifecycle and observed TCP/UDP endpoints and byte counts. No payloads.</summary>
internal sealed class EtwNetwork : IAsyncDisposable
{
    private readonly bool captureDiagnostics;
    private DiagnosticFailure? failure;
    public DiagnosticFailure? Failure=>Volatile.Read(ref failure);
    public EtwNetwork(bool captureDiagnostics=false) {this.captureDiagnostics=captureDiagnostics;attribution=new(NativeProcessIdentity.Read);readLoss=()=>session?.EventsLost??0;stopNative=()=>session?.Stop();disposeNative=()=>session?.Dispose();}
    internal EtwNetwork(Func<int,ProcessIdentity?> resolve,Func<long>? readLoss=null,Action? stopNative=null,Action? disposeNative=null)
    {attribution=new(resolve);active=true;this.readLoss=readLoss;this.stopNative=stopNative??(()=>{});this.disposeNative=disposeNative??(()=>{});}
    private readonly Action stopNative,disposeNative;
    private readonly Func<long>? readLoss;
    private readonly ProcessAttribution attribution;
    private readonly ConnectionLedger connections=new();
    // This outer gate makes attribution, endpoint recording, lifecycle repair, loss and both snapshots one decision.
    private readonly object accountingGate=new(),lifecycleGate=new();
    private TraceEventSession? session;
    private Task? processing,stop,disposal;
    private volatile string status="Starting application network accounting…";
    private volatile bool stopping,active,normalStop,terminalFailure;
    private long observedLoss;
    private double lastReconciliation=double.NegativeInfinity;
    private int resourcesDisposed;
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
                session=new TraceEventSession($"DeadWare-{Environment.ProcessId}-{Guid.NewGuid():N}") {StopOnDispose=true};
                phase="Enable kernel provider";
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP|KernelTraceEventParser.Keywords.Process);
                phase="Attach source callbacks";
                var parser=session.Source.Kernel;
                parser.ProcessStart+=data=>{lock(accountingGate)attribution.Refresh(data.ProcessID);};
                parser.ProcessDCStart+=data=>{lock(accountingGate)attribution.Refresh(data.ProcessID);};
                parser.ProcessStop+=data=>{lock(accountingGate)attribution.Stop(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks);};
                parser.TcpIpRecv+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,false,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Tcp);
                parser.TcpIpSend+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,true,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Tcp);
                parser.TcpIpRecvIPV6+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,false,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Tcp);
                parser.TcpIpSendIPV6+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,true,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Tcp);
                parser.UdpIpRecv+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,false,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Udp);
                parser.UdpIpSend+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,true,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Udp);
                // TraceEvent names the IPv6 UDP payload UpdIpV6TraceData (upstream spelling).
                parser.UdpIpRecvIPV6+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,false,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Udp);
                parser.UdpIpSendIPV6+=data=>RecordEndpoint(data.ProcessID,data.TimeStamp.ToUniversalTime(),data.size,true,data.saddr,data.sport,data.daddr,data.dport,ConnectionProtocol.Udp);
                status="Per-app accounting active · TCP/UDP · IPv4/IPv6 · session totals are estimates";active=true;
                processing=Task.Run(()=>
                {
                    try {session.Source.Process();if(!stopping)status="Per-app ETW collection stopped unexpectedly. Restart DeadWare to retry.";}
                    catch(Exception e) {if(!stopping){failure=DiagnosticFailure.Capture("Process event stream",e,captureDiagnostics);status=$"Per-app ETW collection failed ({e.GetType().Name}). Restart DeadWare to retry.";}}
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
    internal void RecordEndpoint(int pid,DateTimeOffset timestamp,int bytes,bool send,IPAddress source,int sourcePort,IPAddress destination,int destinationPort,ConnectionProtocol protocol)
    {
        lock(accountingGate)
        {
            if(terminalFailure)return;
            if(!attribution.Record(pid,timestamp.UtcTicks,send?0:bytes,send?bytes:0,out var identity))
            {connections.Record(null!,timestamp,bytes,send);return;}
            connections.Record(NetworkAddress.FromEndpoints(identity!,source,sourcePort,destination,destinationPort,protocol,send),timestamp,bytes,send);
        }
    }
    internal void ObserveLoss(long cumulative)
    {
        lock(accountingGate)
        {
            if(cumulative<=observedLoss)return;
            long increment=cumulative-observedLoss;observedLoss=cumulative;
            attribution.ObserveLoss(cumulative);connections.Break(increment);
            status=$"ETW lost {cumulative:N0} events · attribution reset; totals cover activity since the latest loss";
        }
    }
    public IReadOnlyList<ProcessTraffic> Take(double now)=>TakeSnapshot(DateTimeOffset.UtcNow,now).Processes;
    internal NetworkCaptureSnapshot TakeSnapshot(DateTimeOffset timestamp,double now,bool available=true)
    {
        lock(accountingGate)
        {
            bool usable=available&&(active||normalStop&&stopping&&failure is null);
            if(readLoss is not null&&!stopping)
            {
                try {ObserveLoss(readLoss());}
                catch(Exception e) {failure=DiagnosticFailure.Capture("Query event loss",e,captureDiagnostics);status=$"ETW status unavailable ({e.GetType().Name})";usable=false;}
            }
            if(usable&&now-lastReconciliation>=5) {attribution.Reconcile();lastReconciliation=now;}
            if(!usable)connections.Break();
            var sample=connections.Take(timestamp,now);
            if(!usable)sample=sample with {Available=false,Complete=false,CoverageFrom=null,Status=status};
            else if(sample.UnattributedEvents>0)sample=sample with {Status=sample.Status+$" · {sample.UnattributedEvents:N0} unattributed observations"};
            if(stopping&&usable)sample=sample with {Complete=false,Status=sample.Status+" · Final drain; stop-time loss not verified."};
            return new(usable?attribution.Take(now):[],sample);
        }
    }
    internal Task StopAsync()
    {
        lock(lifecycleGate)return stop??=StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        lock(accountingGate)
        {
            normalStop=active;
            try {if(readLoss is not null)ObserveLoss(readLoss());}
            catch(Exception e) {failure=DiagnosticFailure.Capture("Query event loss before stop",e,captureDiagnostics);normalStop=false;}
            stopping=true;
        }
        try
        {
            stopNative();
            if(processing is not null)await processing.ConfigureAwait(false);
        }
        catch(Exception e)
        {
            StopFailed("Stop event stream",e);
            // A failed native Stop must still release the owned source/session. Do not wait
            // on a processing loop that the failed stop never unblocked or claim a valid drain.
            ReleaseResources();
        }
        finally {active=false;}
    }
    private void StopFailed(string phase,Exception error)
    {
        lock(accountingGate)
        {
            normalStop=false;active=false;terminalFailure=true;
            failure=DiagnosticFailure.Capture(phase,error,captureDiagnostics);
            status=$"ETW shutdown unavailable ({error.GetType().Name}); final coverage is incomplete.";
        }
    }
    private void ReleaseResources()
    {
        if(Interlocked.Exchange(ref resourcesDisposed,1)!=0)return;
        try {disposeNative();}
        catch(Exception e) {StopFailed("Dispose event stream",e);}
        finally {session=null;}
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
