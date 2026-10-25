using System.Diagnostics;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
namespace DeadWare.App;

/// <summary>Captures only process lifecycle and TCP/UDP byte counts. No payloads, endpoints, file or disk providers.</summary>
internal sealed class EtwNetwork : IAsyncDisposable
{
    private readonly ProcessLedger ledger=new();
    private readonly Dictionary<int,long> generations=new();
    private readonly object gate=new();
    private TraceEventSession? session;
    private Task? processing;
    private volatile string status="Starting application network accounting…";
    private volatile bool stopping;
    private volatile bool active;
    private long unattributed;
    public string Status=>status;
    public void Start()
    {
        if(!Collector.IsElevated) {status="Per-app traffic unavailable: administrator access is required. Interface totals still work.";return;}
        try
        {
            // Unique name prevents this app from attaching to or stopping another tool's trace.
            session=new TraceEventSession($"DeadWare-{Environment.ProcessId}-{Guid.NewGuid():N}") {StopOnDispose=true};
            var parser=session.Source.Kernel;
            parser.ProcessStart+=data=>StartProcess(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.ImageFileName);
            parser.ProcessDCStart+=data=>SeedProcess(data.ProcessID,data.ImageFileName);
            parser.ProcessStop+=data=>StopProcess(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks);
            parser.TcpIpRecv+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.TcpIpSend+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.TcpIpRecvIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.TcpIpSendIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.UdpIpRecv+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.UdpIpSend+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            parser.UdpIpRecvIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,false);
            parser.UdpIpSendIPV6+=data=>Record(data.ProcessID,data.TimeStamp.ToUniversalTime().Ticks,data.size,true);
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP|KernelTraceEventParser.Keywords.Process);
            status="Per-app accounting active · TCP/UDP · IPv4/IPv6 · session totals are estimates";
            active=true;
            processing=Task.Run(()=>
            {
                try {session.Source.Process();if(!stopping) status="Per-app ETW collection stopped unexpectedly. Restart DeadWare to retry.";}
                catch(Exception e) {if(!stopping) status=$"Per-app ETW collection failed ({e.GetType().Name}). Restart DeadWare to retry.";}
                finally {active=false;}
            });
        }
        catch(Exception e)
        {
            status=$"Per-app ETW unavailable ({e.GetType().Name}). Access or tracing policy may block this session.";
            session?.Dispose();session=null;
        }
    }
    private void StartProcess(int pid,long created,string name)
    {
        lock(gate)
        {
            if(generations.TryGetValue(pid,out long existing)&&existing>=created) return;
            generations[pid]=created;ledger.Start(pid,created,SafeName(name,pid));
        }
    }
    private void SeedProcess(int pid,string? suggested=null)
    {
        lock(gate)
        {
            if(generations.ContainsKey(pid)) return;
            try
            {
                using var process=Process.GetProcessById(pid);
                long created=process.StartTime.ToUniversalTime().Ticks;
                generations[pid]=created;ledger.Start(pid,created,SafeName(suggested??process.ProcessName,pid));
            }
            catch(ArgumentException) { }
            catch(System.ComponentModel.Win32Exception) { }
            catch(InvalidOperationException) { }
        }
    }
    private static string SafeName(string? name,int pid)=>string.IsNullOrWhiteSpace(name)?$"PID {pid}":System.IO.Path.GetFileName(name);
    private void StopProcess(int pid,long eventTime)
    {
        lock(gate) if(generations.TryGetValue(pid,out var created)&&eventTime>=created) {ledger.Stop(pid,created);generations.Remove(pid);}
    }
    private void Record(int pid,long eventTime,int bytes,bool send)
    {
        if(pid<0||bytes<0) return;
        lock(gate)
        {
            if(!generations.ContainsKey(pid)) SeedProcess(pid);
            if(generations.TryGetValue(pid,out var created)&&eventTime>=created) ledger.Add(pid,created,send?0:bytes,send?bytes:0);
            else Interlocked.Increment(ref unattributed);
        }
    }
    public IReadOnlyList<ProcessTraffic> Take(double now)
    {
        if(!active) return [];
        if(session is not null)
        {
            try
            {
                if(session.EventsLost>0) status=$"ETW lost {session.EventsLost:N0} events · per-app rates and totals are incomplete";
                else if(Interlocked.Read(ref unattributed)>0) status=$"ETW active · {Interlocked.Read(ref unattributed):N0} events could not be attributed · per-app totals are incomplete";
            }
            catch(Exception e) {status=$"ETW status unavailable ({e.GetType().Name})";}
        }
        return ledger.Take(now);
    }
    public async ValueTask DisposeAsync()
    {
        stopping=true;
        // Stop only our unique session, which unblocks the processing thread before disposing its source.
        try {session?.Stop();if(processing is not null) await processing.ConfigureAwait(false);}
        finally {session?.Dispose();}
    }
}
