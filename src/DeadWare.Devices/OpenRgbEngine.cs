using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DeadWare.Core;

namespace DeadWare.Devices;

internal enum EngineStatus { Starting, Running, Restarting, Failed, Stopped }

// Runs OpenRGB as a child, waits for its SDK port and restarts it at a safe pace (RestartBackoff: never sooner than 4 s; rapid cycles have wedged USB firmware).
internal sealed class OpenRgbEngine : IAsyncDisposable
{
    public const int Port=6744;
    public static readonly TimeSpan PortWait=TimeSpan.FromSeconds(30),PortProbe=TimeSpan.FromMilliseconds(500);
    readonly string exePath,configDir;
    readonly IProcessRunner runner;
    readonly Func<int,CancellationToken,Task<bool>> portOpen;
    readonly TimeProvider time;
    readonly HostLog log;
    readonly RestartBackoff backoff=new();
    readonly CancellationTokenSource cts=new();
    readonly TaskCompletionSource firstAttempt=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    IRunningProcess? current;
    Task? supervisor;
    volatile bool quitting;
    bool started;
    EngineStatus status=EngineStatus.Stopped;
    int? processId;
    string? failureReason;
    public OpenRgbEngine(string exePath,string configDir,IProcessRunner runner,Func<int,CancellationToken,Task<bool>> portOpen,TimeProvider time,HostLog log)
    {this.exePath=exePath;this.configDir=configDir;this.runner=runner;this.portOpen=portOpen;this.time=time;this.log=log;}
    public static IReadOnlyList<string> Arguments(string configDir)=>["--server","--server-port",Port.ToString(CultureInfo.InvariantCulture),"--noautoconnect","--config",configDir];
    public int? ProcessId{get{lock(gate)return processId;}}
    public EngineStatus Status{get{lock(gate)return status;}}
    public string? FailureReason{get{lock(gate)return failureReason;}}
    public event Action? Changed;

    // Returns once the first launch attempt has resolved (Running, Restarting or Failed); supervision carries on in the background.
    public Task StartAsync(CancellationToken token)
    {
        string? pending=null;
        lock(gate)
        {
            if(started) return firstAttempt.Task;
            started=true;
            if(!File.Exists(exePath)){SetLocked(EngineStatus.Failed,$"OpenRGB.exe was not found at {exePath}.",out pending);firstAttempt.TrySetResult();}
            else supervisor=Task.Run(()=>Supervise(cts.Token));
        }
        Notify(pending);
        if(pending is not null) return Task.CompletedTask;
        return firstAttempt.Task.WaitAsync(token);
    }

    async Task Supervise(CancellationToken ct)
    {
        try
        {
            while(!quitting)
            {
                Set(EngineStatus.Starting,null);
                IRunningProcess? proc=null;
                try
                {
                    var info=new ProcessStartInfo(exePath){WorkingDirectory=Path.GetDirectoryName(exePath)??""};
                    foreach(var a in Arguments(configDir)) info.ArgumentList.Add(a);
                    proc=runner.Start(info);
                    lock(gate){current=proc;processId=proc.Id;}
                    if(await WaitForPort(proc,ct).ConfigureAwait(false))
                    {
                        Set(EngineStatus.Running,null);
                        firstAttempt.TrySetResult();
                        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                        log.Write("openrgb exited");
                    }
                    else if(!quitting) log.Write("openrgb port did not open");
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){}
                catch(Exception e) when(e is not OperationCanceledException){log.Write("openrgb launch failed: "+e.Message);}
                finally
                {
                    if(proc is not null){try{proc.KillTree();}catch{}try{proc.Dispose();}catch{}}
                    lock(gate){current=null;processId=null;}
                }
                if(quitting) break;
                var delay=backoff.RecordFailure(time.GetUtcNow());
                if(delay is null){Set(EngineStatus.Failed,$"OpenRGB stopped {RestartBackoff.MaxFailures} times in {(int)RestartBackoff.Window.TotalMinutes} minutes.");return;}
                var wait=Task.Delay(delay.Value,time,ct); // registered before the status change so a clock advance can't miss it
                Set(EngineStatus.Restarting,null);
                firstAttempt.TrySetResult();
                await wait.ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}
        finally{firstAttempt.TrySetResult();}
    }

    async Task<bool> WaitForPort(IRunningProcess proc,CancellationToken ct)
    {
        using var timeout=new CancellationTokenSource(PortWait,time);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        try
        {
            while(!proc.HasExited)
            {
                if(await portOpen(Port,limit.Token).ConfigureAwait(false)) return true;
                await Task.Delay(PortProbe,time,limit.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return false;}
    }

    void Set(EngineStatus next,string? reason)
    {
        string? line;
        lock(gate)SetLocked(next,reason,out line);
        Notify(line);
    }
    // Records the change under the lock; the caller logs and raises Changed outside it.
    void SetLocked(EngineStatus next,string? reason,out string? line)
    {
        line=null;
        if(status==next&&failureReason==reason) return;
        status=next;failureReason=reason;
        line="openrgb "+next+(reason is null?"":": "+reason);
    }
    // A throwing Changed subscriber must never fault the supervisor: log it once and carry on.
    void Notify(string? line)
    {
        if(line is null) return;
        log.Write(line);
        try{Changed?.Invoke();}catch(Exception e){log.Write("openrgb Changed handler failed: "+e.Message);}
    }

    public async Task StopAsync()
    {
        quitting=true;
        Task? sup;
        IRunningProcess? proc;
        lock(gate){sup=supervisor;proc=current;}
        try{cts.Cancel();}catch(ObjectDisposedException){}
        try{proc?.KillTree();}catch{}
        if(sup is not null){try{await sup.ConfigureAwait(false);}catch{}}
        Set(EngineStatus.Stopped,null);
    }
    public async ValueTask DisposeAsync(){await StopAsync().ConfigureAwait(false);cts.Dispose();}

    // Real port probe: one TCP connect to 127.0.0.1.
    public static async Task<bool> ProbePort(int port,CancellationToken token)
    {
        try{using var client=new TcpClient();await client.ConnectAsync(System.Net.IPAddress.Loopback,port,token).ConfigureAwait(false);return true;}
        catch(Exception e) when(e is SocketException or IOException){return false;}
    }
}
