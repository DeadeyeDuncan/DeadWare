using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.App;

/// <summary>A running DeadWare.Devices process: the client waits on it, and kills it only when it will not stop.</summary>
internal interface IDeviceHostProcess : IDisposable { int Id {get;} bool HasExited {get;} Task WaitForExitAsync(CancellationToken token); void Kill(); }
internal enum ServeEnd { Closed, VersionMismatch, WrongElevation }
internal interface IDeviceHostLauncher { IDeviceHostProcess Start(string openRgbPath,bool startup); IDeviceHostProcess? FindRunning(); }
/// <summary>
/// The window's side of the lighting host: it finds or launches DeviceHost, connects to its pipe, keeps the latest <see cref="HostState"/>,
/// relaunches the host after crashes with backoff, and stops it on quit. Nothing here throws for connection problems: they become
/// <see cref="Problem"/> or a failed <see cref="Reply"/>. <see cref="Changed"/> may be raised on any thread, never under a lock.
/// </summary>
internal sealed class DeviceHostClient : IAsyncDisposable
{
    internal const string ElevatedProblem="The lighting helper is running as administrator. Restart DeadWare as administrator to control it.",
        UnverifiedProblem="Another program is using the lighting helper's connection, so DeadWare won't send it commands.",
        GaveUpProblem="The lighting helper stopped 5 times in 10 minutes. Turn lighting off and on in Settings to try again.",
        VersionProblem="The lighting helper is a different version. Restart DeadWare.",
        LaunchProblem="The lighting helper couldn't be started.",
        FailedProblem="The lighting helper connection failed.";
    static readonly TimeSpan RetryDelay=TimeSpan.FromMilliseconds(500),ReplyTimeout=TimeSpan.FromSeconds(5),ExitTimeout=TimeSpan.FromSeconds(5);
    static readonly Reply NotConnected=new(false,"The lighting helper isn't connected."),NoAnswer=new(false,"The lighting helper didn't answer.");
    sealed class Conn(Stream stream) {public readonly Stream Stream=stream;public readonly SemaphoreSlim Write=new(1,1);public readonly Dictionary<long,TaskCompletionSource<Reply>> Pending=new();public bool Dead,Checked;}
    readonly string openRgbPath;readonly IDeviceHostLauncher launcher;readonly Func<CancellationToken,Task<Stream>> connect;readonly TimeProvider time;readonly bool windowElevated;
    readonly object gate=new();readonly RestartBackoff backoff=new();
    readonly CancellationTokenSource lifetime=new();readonly CancellationTokenSource pacing;readonly TaskCompletionSource disposed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenRegistration startToken;
    // stopWindow: the running stop's 5 s window; stopSent: its one StopRequest has been claimed, by the stop or by a connection registered after it began.
    IDeviceHostProcess? process;Conn? conn;HostState? state;string? problem;Task? loop,stopTask;CancellationTokenSource? stopWindow;bool stopping,stopSent,disposing,startupPending;long nextId;
    public DeviceHostClient(string openRgbPath,IDeviceHostLauncher launcher,Func<CancellationToken,Task<Stream>> connect,TimeProvider time,bool windowElevated=false)
    {this.windowElevated=windowElevated;this.openRgbPath=openRgbPath;this.launcher=launcher;this.connect=connect;this.time=time;pacing=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);}
    public HostState? State {get {lock(gate)return state;}}
    public string? Problem {get {lock(gate)return problem;}}
    public event Action? Changed;
    /// <summary>Starts the background connection loop and returns at once; <paramref name="startup"/> is true for a Windows sign-in launch and applies to the first launch only.</summary>
    public Task StartAsync(bool startup,CancellationToken token)
    {
        lock(gate)
        {
            if(loop is not null||stopping)return Task.CompletedTask;
            startupPending=startup;
            if(token.CanBeCanceled)startToken=token.Register(()=>{try {lifetime.Cancel();} catch(ObjectDisposedException) {}});
            loop=Task.Run(()=>RunAsync(lifetime.Token));
        }
        return Task.CompletedTask;
    }
    bool IsStopping {get {lock(gate)return stopping;}}
    IDeviceHostProcess? CurrentProcess {get {lock(gate)return process;}}
    /// <summary>Raises <see cref="Changed"/>, never under the lock. A subscriber that throws is contained, so it can neither end the connection loop nor starve the others.</summary>
    void RaiseChanged() {if(Changed is {} handlers)foreach(Action handler in handlers.GetInvocationList())try {handler();} catch(Exception) {}}
    void SetProblem(string? value) {lock(gate){if(problem==value)return;problem=value;}RaiseChanged();}
    async Task<bool> Pause(TimeSpan delay,CancellationToken ct)
    {
        try {await Task.Delay(delay,time,pacing.Token).ConfigureAwait(false);return !ct.IsCancellationRequested;}
        catch(OperationCanceledException) {return false;}
    }
    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while(!ct.IsCancellationRequested&&!IsStopping)
            {
                Stream stream;
                try {stream=await connect(ct).ConfigureAwait(false);}
                catch(OperationCanceledException) when(ct.IsCancellationRequested) {return;}
                catch(UnauthorizedAccessException) {SetProblem(ElevatedProblem);return;}
                catch(InvalidDataException) {SetProblem(UnverifiedProblem);return;}
                catch(Exception e) when(e is IOException or TimeoutException or OperationCanceledException)
                {
                    // No pipe yet: find the host or launch it, then keep retrying until it answers, exits or we stop.
                    if(!await WaitForHostAsync(ct).ConfigureAwait(false))return;
                    continue;
                }
                // Served even when a stop began while this connect finished: the stop's StopRequest then goes through this connection.
                SetProblem(null);
                if(CurrentProcess is null&&launcher.FindRunning() is {} found&&!Adopt(found)) {stream.Dispose();return;}
                var end=await ServeAsync(stream,ct).ConfigureAwait(false);
                if(end==ServeEnd.VersionMismatch) {SetProblem(VersionProblem);return;}
                if(IsStopping)return;
                if(end==ServeEnd.WrongElevation)
                {
                    // Deliberate restart, not a crash: not counted in the backoff, but never faster than its base delay.
                    IDeviceHostProcess? old;lock(gate){old=process;process=null;}
                    old?.Dispose();
                    if(!await Pause(RestartBackoff.Base,ct).ConfigureAwait(false)||!Launch(false))return;
                    continue;
                }
                if(CurrentProcess is {HasExited:true}) {if(!await ExitedAsync(ct).ConfigureAwait(false))return;continue;}
                if(!await Pause(RetryDelay,ct).ConfigureAwait(false))return;
            }
        }
        catch(OperationCanceledException) {}
        catch(Exception) {SetProblem(FailedProblem);}
    }
    async Task<bool> WaitForHostAsync(CancellationToken ct)
    {
        if(IsStopping)return false;
        var p=CurrentProcess;
        if(p is {HasExited:true})return await ExitedAsync(ct).ConfigureAwait(false);
        if(p is null)
        {
            bool startup;lock(gate){startup=startupPending;startupPending=false;}
            if(launcher.FindRunning() is {} found) {if(!Adopt(found))return false;}
            else if(!Launch(startup))return false;
        }
        return await Pause(RetryDelay,ct).ConfigureAwait(false);
    }
    /// <summary>The tracked process exited while we were not stopping: pace the relaunch, or give up after the fifth exit in ten minutes.</summary>
    async Task<bool> ExitedAsync(CancellationToken ct)
    {
        IDeviceHostProcess? old;lock(gate){old=process;process=null;}
        old?.Dispose();
        if(backoff.RecordFailure(time.GetUtcNow()) is not {} delay) {SetProblem(GaveUpProblem);return false;}
        return await Pause(delay,ct).ConfigureAwait(false)&&Launch(false);
    }
    bool Launch(bool startup)
    {
        if(IsStopping)return false;
        IDeviceHostProcess started;
        try {started=launcher.Start(openRgbPath,startup);}
        catch(Exception e) when(e is Win32Exception or IOException or InvalidOperationException) {SetProblem(LaunchProblem);return false;}
        return Adopt(started);
    }
    /// <summary>Tracks a process, during a stop too, whose window then covers it. Once disposal began it is killed instead, since nothing would stop it later.</summary>
    bool Adopt(IDeviceHostProcess started)
    {
        bool track;lock(gate){track=!disposing;if(track)process=started;}
        if(track)return true;
        Kill(started);started.Dispose();return false;
    }
    static void Kill(IDeviceHostProcess p) {try {p.Kill();} catch(Exception e) when(e is Win32Exception or InvalidOperationException) {}}
    /// <summary>Reads until the pipe closes, the tracked process exits or the client ends. A host that runs without the window's elevation is stopped here, before the pipe closes.</summary>
    async Task<ServeEnd> ServeAsync(Stream stream,CancellationToken ct)
    {
        var c=new Conn(stream);CancellationToken? stop;Task stopSend=Task.CompletedTask;
        lock(gate){conn=c;stop=ClaimStop(c);}
        try
        {
            using var serve=CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task exited=CurrentProcess?.WaitForExitAsync(serve.Token)??Task.Delay(Timeout.Infinite,serve.Token);
            var read=ReadAsync(c,serve.Token);
            // A stop that began before this connection was registered could not reach the host: its StopRequest goes now.
            if(stop is {} window)stopSend=SendStopAsync(c,window);else _=RefreshAsync(ct);
            await Task.WhenAny(read,exited).ConfigureAwait(false);
            serve.Cancel();
            try {await exited.ConfigureAwait(false);} catch(Exception) {}
            var end=await read.ConfigureAwait(false);
            if(end==ServeEnd.WrongElevation&&!IsStopping)await StopHostAsync(c).ConfigureAwait(false);
            return end;
        }
        finally
        {
            List<TaskCompletionSource<Reply>> orphaned;bool cleared;
            lock(gate){c.Dead=true;if(conn==c)conn=null;orphaned=[..c.Pending.Values];c.Pending.Clear();cleared=state is not null;state=null;}
            foreach(var p in orphaned)p.TrySetResult(NotConnected);
            stream.Dispose();
            await stopSend.ConfigureAwait(false);
            // The host is gone or out of reach, so its last state no longer describes it: the page can tell a relaunch from a running host.
            if(cleared)RaiseChanged();
        }
    }
    async Task RefreshAsync(CancellationToken ct) {try {await SendAsync(new GetStateRequest(),ct).ConfigureAwait(false);} catch(Exception) {}}
    /// <summary>Under the lock: the stop window's token when the caller is the one to send the running stop's single StopRequest on <paramref name="c"/>, else null.</summary>
    CancellationToken? ClaimStop(Conn? c) {if(c is null||c.Dead||stopWindow is null||stopSent)return null;stopSent=true;return stopWindow.Token;}
    /// <summary>Writes StopRequest under the connection's write lock, within <paramref name="limit"/>. No reply is awaited: the caller waits for the exit instead.</summary>
    async Task SendStopAsync(Conn c,CancellationToken limit)
    {
        bool locked=false;
        try
        {
            await c.Write.WaitAsync(limit).ConfigureAwait(false);locked=true;
            await DeviceHostProtocol.WriteAsync(c.Stream,new StopRequest{Id=Interlocked.Increment(ref nextId)},limit).ConfigureAwait(false);
        }
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException) {}
        finally {if(locked)c.Write.Release();}
    }
    /// <summary>The elevation restart's stop, by <see cref="StopAsync"/>'s rules: one 5 s window from now covers the StopRequest write and the wait for the exit, then a kill.</summary>
    async Task StopHostAsync(Conn c)
    {
        using var window=new CancellationTokenSource(ExitTimeout,time);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(window.Token,lifetime.Token);
        await SendStopAsync(c,limit.Token).ConfigureAwait(false);
        await KillAfterAsync(CurrentProcess,limit.Token).ConfigureAwait(false);
    }
    /// <summary>Waits for the process to exit until <paramref name="limit"/> ends, then kills it; never while the client is being disposed.</summary>
    async Task KillAfterAsync(IDeviceHostProcess? p,CancellationToken limit)
    {
        if(p is null)return;
        try {await p.WaitForExitAsync(limit).ConfigureAwait(false);}
        catch(OperationCanceledException) when(!lifetime.IsCancellationRequested) {Kill(p);}
        catch(Exception) {}
    }
    async Task<ServeEnd> ReadAsync(Conn c,CancellationToken token)
    {
        try
        {
            while(await DeviceHostProtocol.ReadAsync(c.Stream,token).ConfigureAwait(false) is {} message)
            {
                if(message.Version!=DeviceHostProtocol.Version)return ServeEnd.VersionMismatch;
                if(message is Reply reply)Complete(c,reply.Id,reply);
                else if(message is StateEvent update)
                {
                    bool first;lock(gate){state=update.State;first=!c.Checked;c.Checked=true;}
                    if(update.Id!=0)Complete(c,update.Id,new Reply(true,null));
                    RaiseChanged();
                    if(first&&windowElevated&&!update.State.Elevated)return ServeEnd.WrongElevation;
                }
            }
            return ServeEnd.Closed;
        }
        catch(InvalidDataException) {return ServeEnd.VersionMismatch;}
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException) {return ServeEnd.Closed;}
    }
    void Complete(Conn c,long id,Reply reply)
    {
        TaskCompletionSource<Reply>? waiting;
        lock(gate){if(!c.Pending.Remove(id,out waiting))return;}
        waiting.TrySetResult(reply);
    }
    /// <summary>Sends a request and returns the host's answer. Connection problems come back as a failed <see cref="Reply"/>; only a cancelled <paramref name="token"/> throws.</summary>
    public async Task<Reply> SendAsync(HostMessage request,CancellationToken token)
    {
        var waiting=new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id=Interlocked.Increment(ref nextId);Conn? c;
        lock(gate){c=conn;if(c is null||c.Dead)return NotConnected;c.Pending[id]=waiting;}
        using var timeout=new CancellationTokenSource(ReplyTimeout,time);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        bool locked=false;
        try
        {
            try
            {
                await c.Write.WaitAsync(linked.Token).ConfigureAwait(false);locked=true;
                await DeviceHostProtocol.WriteAsync(c.Stream,request with {Id=id},linked.Token).ConfigureAwait(false);
            }
            catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // A write that failed or was cut short may have left half a frame on the pipe; closing it makes the read loop reconnect.
                if(locked)c.Stream.Dispose();
                if(token.IsCancellationRequested)throw;
                return e is OperationCanceledException?NoAnswer:NotConnected;
            }
            finally {if(locked)c.Write.Release();}
            try {return await waiting.Task.WaitAsync(linked.Token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(!token.IsCancellationRequested) {return NoAnswer;}
        }
        finally {lock(gate)c.Pending.Remove(id);}
    }
    /// <summary>
    /// Stops the host. A 5 s window starts now: it covers writing StopRequest, whose reply is not awaited, and waiting for the host to
    /// exit; a host still running when it closes is killed with its process tree. Safe to call again; the second call shares the first.
    /// </summary>
    public Task StopAsync()
    {
        var window=new CancellationTokenSource(ExitTimeout,time);bool mine=false;Task? running;
        lock(gate){running=stopTask;if(running is null&&!disposing){mine=stopping=true;stopWindow=window;running=stopTask=Task.Run(()=>StopCoreAsync(window));}}
        if(!mine)window.Dispose();
        return running??Task.CompletedTask;
    }
    async Task StopCoreAsync(CancellationTokenSource window)
    {
        try
        {
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(window.Token,lifetime.Token);
            pacing.Cancel();
            Conn? c;Task? running;bool send;
            lock(gate){c=conn;send=ClaimStop(c) is not null;running=loop;}
            if(send)await SendStopAsync(c!,limit.Token).ConfigureAwait(false);
            // The loop ends once the host exits or closes its pipe. Until then it may still adopt a host whose connect just finished and send it the StopRequest.
            if(running is not null)try {await running.WaitAsync(limit.Token).ConfigureAwait(false);} catch(Exception) {}
            var waited=CurrentProcess;
            await KillAfterAsync(waited,limit.Token).ConfigureAwait(false);
            lifetime.Cancel();
            if(running is not null)try {await running.ConfigureAwait(false);} catch(Exception) {}
            // A host adopted after that wait, when the window closed while the loop still ran, would outlive the client.
            IDeviceHostProcess? late;bool keep;lock(gate){late=process;keep=disposing;}
            if(!keep&&late is not null&&late!=waited&&!late.HasExited)Kill(late);
        }
        finally {lock(gate)stopWindow=null;window.Dispose();}
    }
    /// <summary>Tears everything down without asking the host to stop; callers wanting the host gone call <see cref="StopAsync"/> first. Safe to call again: a later call waits for the first.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? stop,running;bool first;
        lock(gate){first=!disposing;disposing=stopping=true;stop=stopTask;running=loop;}
        if(!first) {await disposed.Task.ConfigureAwait(false);return;}
        try
        {
            pacing.Cancel();lifetime.Cancel();
            if(stop is not null)try {await stop.ConfigureAwait(false);} catch(Exception) {}
            if(running is not null)try {await running.ConfigureAwait(false);} catch(Exception) {}
            IDeviceHostProcess? tracked;lock(gate){tracked=process;process=null;}
            tracked?.Dispose();
            startToken.Dispose();pacing.Dispose();lifetime.Dispose();
        }
        finally {disposed.TrySetResult();}
    }
}
/// <summary>Starts and finds the real DeadWare.Devices.exe beside this program.</summary>
internal sealed class DeviceHostLauncher : IDeviceHostLauncher
{
    internal static string HostPath=>Path.Combine(AppContext.BaseDirectory,"DeadWare.Devices.exe");
    sealed class HostProcess(Process process):IDeviceHostProcess
    {
        public int Id=>process.Id;
        public bool HasExited {get {try {return process.HasExited;} catch(Exception e) when(e is InvalidOperationException or Win32Exception) {return true;}}}
        public Task WaitForExitAsync(CancellationToken token)=>process.WaitForExitAsync(token);
        // The whole tree: a killed host never ran its own shutdown, so the OpenRGB it started would keep the devices and block the next host.
        public void Kill() {try {process.Kill(entireProcessTree:true);} catch(Exception e) when(e is InvalidOperationException or Win32Exception) {}}
        public void Dispose()=>process.Dispose();
    }
    public IDeviceHostProcess Start(string openRgbPath,bool startup)
    {
        var info=new ProcessStartInfo(HostPath){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory};
        info.ArgumentList.Add("--openrgb");info.ArgumentList.Add(openRgbPath);
        if(startup)info.ArgumentList.Add("--startup");
        return new HostProcess(Process.Start(info)??throw new InvalidOperationException("The lighting helper did not start."));
    }
    /// <summary>A DeadWare.Devices process of this Windows session running this program's own copy, or null.</summary>
    public IDeviceHostProcess? FindRunning()
    {
        using var self=Process.GetCurrentProcess();
        IDeviceHostProcess? match=null;
        foreach(var candidate in Process.GetProcessesByName("DeadWare.Devices"))
        {
            bool ours=match is null&&candidate.SessionId==self.SessionId&&string.Equals(NativeProcessIdentity.ImagePath(candidate.Id),HostPath,StringComparison.OrdinalIgnoreCase);
            if(ours)match=new HostProcess(candidate);else candidate.Dispose();
        }
        return match;
    }
}
/// <summary>Connects to this user's lighting host pipe and proves the server is our own DeadWare.Devices.exe.</summary>
internal static class DeviceHostPipe
{
    public static Task<Stream> ConnectAsync(CancellationToken token)=>ConnectAsync(DeviceHostProtocol.PipeName(StartupRegistration.CurrentSid),token);
    /// <summary>Connects to the named pipe; a test serves its own name, which never collides with a real host's.</summary>
    internal static async Task<Stream> ConnectAsync(string pipeName,CancellationToken token)
    {
        // Identification, not Impersonation: whoever answers the pipe may learn who we are but never act as us.
        var stream=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        try
        {
            await stream.ConnectAsync(2000,token).ConfigureAwait(false);
            if(!GetNamedPipeServerProcessId(stream.SafePipeHandle,out uint pid)||!string.Equals(NativeProcessIdentity.ImagePath((int)pid),DeviceHostLauncher.HostPath,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pipe is not served by DeadWare.Devices.exe.");
            return stream;
        }
        catch {stream.Dispose();throw;}
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
