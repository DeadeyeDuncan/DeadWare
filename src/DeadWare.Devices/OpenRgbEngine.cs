using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using DeadWare.Core;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

internal enum EngineStatus { Starting, Running, Restarting, Failed, Stopped }

// Runs OpenRGB as a child, waits for its SDK port and restarts it at a safe pace (RestartBackoff: never sooner than 4 s; rapid cycles have wedged USB firmware).
internal sealed class OpenRgbEngine : IAsyncDisposable
{
    public const int Port=6744;
    public static readonly TimeSpan PortWait=TimeSpan.FromSeconds(30),PortProbe=TimeSpan.FromMilliseconds(500);
    // F6 item 9 (Ruling BH): how long the supervisor waits for an OpenRGB it killed to exit before it finishes.
    public static readonly TimeSpan ExitWait=TimeSpan.FromSeconds(5);
    // Fix wave 2, D: a port the self-check finds taken less than LingerWindow after this engine ended an OpenRGB is checked again every
    // LingerRetry until LingerWindow after that end (the OpenRGB it killed may still be running down). E: a deliberate stop never ends an
    // OpenRGB younger than YoungStop; it waits out the rest first.
    public static readonly TimeSpan LingerWindow=TimeSpan.FromSeconds(10),LingerRetry=TimeSpan.FromSeconds(1),YoungStop=TimeSpan.FromSeconds(5);
    readonly string exePath,configDir;
    readonly IProcessRunner runner;
    readonly Func<int,CancellationToken,Task<bool>> portOpen;
    readonly TimeProvider time;
    readonly HostLog log;
    readonly RestartBackoff backoff=new();
    CancellationTokenSource cts=new();
    TaskCompletionSource firstAttempt=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    IRunningProcess? current;
    Task? supervisor;
    volatile bool quitting,deliberate;
    // Lighting 5: false once an OpenRGB the supervisor ended had not exited by the end of its wait; StopForRestartAsync's answer.
    bool allGone=true;
    bool started;
    // F4 item 3 (whole-branch review D8, D9): the restart history a conflict stop keeps and a full stop clears, with the backoff's count.
    // resumeAt: the earliest the next launch may start, armed with each restart delay. gaveUp: the backoff gave up, so a start fails at once.
    // F5 item 3b (whole-branch review R4-D2): resumeAt is a time.GetTimestamp() value, so a step of the wall clock doesn't move it.
    long? resumeAt;
    bool gaveUp;
    // Fix wave 2: when the last OpenRGB this engine launched was launched, and when the last one it launched ended (time.GetTimestamp() values);
    // fix wave 3, D5: endedAt only for one this engine ended (a stop or a kill), null after one that exited by itself.
    long? launchedAt,endedAt;
    // Fix wave 2, E: cancelled by a full stop, which ends a deliberate stop's wait for a young OpenRGB at once.
    CancellationTokenSource fullStop=new();
    EngineStatus status=EngineStatus.Stopped;
    int? processId;
    string? failureReason;
    // The refusal of a folder programs without administrator rights can change; one the check couldn't judge gets
    // OpenRgbFolderRefusal's can't-check text with the reason instead (polish item 8).
    public const string UnprotectedFolderText=OpenRgbFolderRefusal.WritableText;
    readonly Func<string,LocationProtection>? folderCheck;
    // folderCheck: an elevated host's check of OpenRGB's folder (Ruling AM), run before every launch; null when the host isn't elevated.
    public static string ConfigFailedText(string configDir)=>$"DeadWare couldn't update OpenRGB's settings file in {configDir}, so it didn't start OpenRGB. Turn lighting off and on in Settings to try again.";
    // The final reasons name their remedy (O16): choosing the same OpenRGB.exe again in Settings retries, as turning lighting off and on does.
    public static string MissingText(string exePath)=>$"OpenRGB.exe was not found at {exePath}. Choose OpenRGB.exe in Settings → Lighting.";
    // F4 item 3: a conflict stop counts towards the give-up as well, and the text says so.
    public static readonly string GaveUpText=$"OpenRGB stopped {RestartBackoff.MaxFailures} times in {(int)RestartBackoff.Window.TotalMinutes} minutes, counting the times DeadWare stopped it for a program that blocks lighting. Turn lighting off and on in Settings to try again.";
    readonly Action<string>? prepareConfig;
    // prepareConfig: writes OpenRGB's settings before every launch (Ruling AR); a launch whose settings can't be written doesn't happen.
    // portGuard: an elevated host's OpenRgbPortLock.Guard (Ruling BG; F6 item 1, Ruling BH), run before every launch: null when OpenRGB's
    // SDK port is locked to the host and its self-check passed, else the refusal. Null for a host without administrator rights, which adds
    // no lock.
    readonly Func<string?>? portGuard;
    public OpenRgbEngine(string exePath,string configDir,IProcessRunner runner,Func<int,CancellationToken,Task<bool>> portOpen,TimeProvider time,HostLog log,Func<string,LocationProtection>? folderCheck=null,Action<string>? prepareConfig=null,Func<string?>? portGuard=null)
    {this.exePath=exePath;this.configDir=configDir;this.runner=runner;this.portOpen=portOpen;this.time=time;this.log=log;this.folderCheck=folderCheck;this.prepareConfig=prepareConfig;this.portGuard=portGuard;}
    // --server-host 127.0.0.1 (Ruling AN): OpenRGB 1.0rc3 otherwise serves its unauthenticated SDK on every network interface. The literal
    // address, not "localhost", which can resolve to ::1 first, while the session and the port probe use IPv4 loopback.
    public const string Host="127.0.0.1";
    public static IReadOnlyList<string> Arguments(string configDir)=>["--server","--server-host",Host,"--server-port",Port.ToString(CultureInfo.InvariantCulture),"--noautoconnect","--config",configDir];
    // F4 item 1 (whole-branch review D14): OpenRGB 1.0rc3 is built before it reads --config, and while it is built it uses <APPDATA>\OpenRGB:
    // it loads OpenRGB.json there and configures its log from that file's LogManager section (ResourceManager.cpp:136-148), which takes an
    // absolute log path, makes the log's folder, deletes old logs of that name and opens the log (LogManager.cpp:65-160). --config later
    // swaps the settings but never moves the log (ResourceManager.cpp:710-717). So OpenRGB starts with APPDATA set to the config folder's
    // parent, whose OpenRGB folder is the config folder itself, since Windows names ignore case: all of that happens in DeadWare's folder,
    // the protected one for an elevated host. Null for a config folder of another name, whose parent's OpenRGB folder would be another
    // folder, so the engine refuses to start (Program always names it openrgb).
    public static string? AppData(string configDir)
        =>string.Equals(Path.GetFileName(configDir),"openrgb",StringComparison.OrdinalIgnoreCase)&&Path.GetDirectoryName(configDir) is {Length:>0} parent?parent:null;
    // F5 item 3a (whole-branch review R4-D15): OpenRGB 1.0rc3 loads GPU vendor DLLs by bare name (atiadlxx.dll, nvapi64.dll), and Qt
    // honours QT_PLUGIN_PATH and QT_QPA_PLATFORM_PLUGIN_PATH. An OpenRGB that inherited the host's environment would search the user's PATH,
    // %LOCALAPPDATA%\Microsoft\WindowsApps included, and an elevated one would run a DLL planted there as administrator. So the child's
    // environment is cleared and given only these variables, copied from the host's, plus APPDATA (the config folder's parent, F4 item 1)
    // and a PATH of OpenRGB's own folder and the system folders, never the inherited PATH. Every host does this, elevated or not.
    internal static readonly string[] AllowedVariables=["SystemRoot","windir","SystemDrive","ComSpec","PATHEXT","OS","PROCESSOR_ARCHITECTURE","NUMBER_OF_PROCESSORS",
        "ProgramData","ProgramFiles","ProgramFiles(x86)","ProgramW6432","CommonProgramFiles","CommonProgramW6432","ALLUSERSPROFILE","USERPROFILE","USERNAME",
        "USERDOMAIN","COMPUTERNAME","TEMP","TMP","LOCALAPPDATA"];
    internal static string SystemPath(string exeFolder)
    {
        var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.Join(';',exeFolder,system,Path.GetDirectoryName(system)??"",Path.Combine(system,"Wbem"));
    }
    internal static void SetEnvironment(ProcessStartInfo info,string exeFolder,string appData)
    {
        var env=info.Environment;
        env.Clear();
        foreach(var name in AllowedVariables)
            if(Environment.GetEnvironmentVariable(name) is {Length:>0} value) env[name]=value;
        env["PATH"]=SystemPath(exeFolder);
        env["APPDATA"]=appData;
    }
    public static string FolderNameText(string configDir)=>$"OpenRGB's settings folder for DeadWare, {configDir}, isn't named openrgb, so DeadWare didn't start OpenRGB: OpenRGB would read settings and keep its log in another folder.";
    public int? ProcessId{get{lock(gate)return processId;}}
    // Fix wave 3, D1/D2: the last OpenRGB this engine ended that had not exited by the end of its wait, kept until it has; Lingering is its
    // id while it still runs, null once it has exited (then it is let go).
    IRunningProcess? lingering;
    public int? Lingering
    {
        get
        {
            IRunningProcess? gone=null;int? id=null;
            lock(gate)
            {
                if(lingering is {} p)
                {
                    bool done;try{done=p.HasExited;}catch{done=true;}
                    if(done){gone=p;lingering=null;}else id=p.Id;
                }
            }
            try{gone?.Dispose();}catch{}
            return id;
        }
    }
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
            var ct=cts.Token;
            if(AppData(configDir) is null){SetLocked(EngineStatus.Failed,FolderNameText(configDir),out pending);firstAttempt.TrySetResult();}
            // F4 item 3: a give-up holds across conflict stops; only a full stop, lighting turned off and on or Quit, clears it.
            else if(gaveUp){SetLocked(EngineStatus.Failed,GaveUpText,out pending);firstAttempt.TrySetResult();}
            else if(!File.Exists(exePath)){SetLocked(EngineStatus.Failed,MissingText(exePath),out pending);firstAttempt.TrySetResult();}
            else supervisor=Task.Run(()=>Supervise(ct));
        }
        Notify(pending);
        if(pending is not null) return Task.CompletedTask;
        return firstAttempt.Task.WaitAsync(token);
    }

    async Task Supervise(CancellationToken ct)
    {
        try
        {
            // F4 item 3 (whole-branch review D9): a start after a conflict stop waits out what is left of the delay that stop armed, as a crash
            // restart waits out its own, so OpenRGB is never relaunched sooner than 4 s after it was stopped. The first attempt resolves as a
            // restart, so the host's loop carries on scanning meanwhile, and a stop in the wait cancels the launch.
            long? at;
            lock(gate)at=resumeAt;
            if(at is {} due&&Left(time,due) is {Ticks:>0} left)
            {
                var pause=Task.Delay(left,time,ct); // registered before the status change so a clock advance can't miss it
                Set(EngineStatus.Restarting,null);
                firstAttempt.TrySetResult();
                await pause.ConfigureAwait(false);
            }
            while(!quitting)
            {
                Set(EngineStatus.Starting,null);
                // Outside the lock: the check walks OpenRGB's whole folder. A refusal is final for this engine and isn't a crash, so it
                // is neither retried nor counted in the backoff.
                if(Refusal() is {} refusal){Set(EngineStatus.Failed,refusal);return;}
                // F5 item 2 (whole-branch review D15, Ruling BG): an elevated host launches OpenRGB only with its SDK port locked to the
                // host, confirmed before each launch; a refusal is final in the same way.
                if(await PortRefusalAsync(ct).ConfigureAwait(false) is {} denied){Set(EngineStatus.Failed,denied);return;}
                if(!Prepared()){Set(EngineStatus.Failed,ConfigFailedText(configDir));return;}
                // B5: the check and the settings write take a while, and a Stop (a conflict's, say) that lands during them starts nothing.
                if(quitting||ct.IsCancellationRequested) break;
                IRunningProcess? proc=null;
                try
                {
                    var info=new ProcessStartInfo(exePath){WorkingDirectory=Path.GetDirectoryName(exePath)??""};
                    foreach(var a in Arguments(configDir)) info.ArgumentList.Add(a);
                    SetEnvironment(info,Path.GetDirectoryName(exePath)??"",AppData(configDir)!); // StartAsync refused a folder for which AppData is null
                    proc=runner.Start(info);
                    lock(gate){current=proc;processId=proc.Id;launchedAt=time.GetTimestamp();}
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
                    if(proc is not null)
                    {
                        // Fix wave 3, D5: one that had exited by itself before any stop or kill was not ended by this engine: no endedAt.
                        bool own;try{own=!quitting&&!ct.IsCancellationRequested&&proc.HasExited;}catch{own=false;}
                        try{proc.KillTree();}catch{}
                        await Exited(proc).ConfigureAwait(false);
                        bool gone;try{gone=proc.HasExited;}catch{gone=false;}
                        // One block: a stop either still sees this process in current (and this false lands after its reset) or sees none.
                        IRunningProcess? old=null;
                        lock(gate){allGone=allGone&&gone;current=null;processId=null;endedAt=own?null:time.GetTimestamp();if(!gone){old=lingering;lingering=proc;}}
                        try{(gone?proc:old)?.Dispose();}catch{}
                    }
                    else lock(gate){current=null;processId=null;}
                }
                // F4 item 3 (whole-branch review D8): every OpenRGB that was launched and ended counts, one a stop ended included, so a program
                // that comes and goes can't cycle OpenRGB faster or longer than crashes can. A full stop clears the count after it.
                if(quitting&&proc is null) break;
                // Lighting 5: a deliberate stop (the hub's take-over) counts nothing, so no number of them reaches the give-up, but it arms
                // the 4 s floor, or keeps a longer delay already armed.
                if(deliberate)
                {
                    lock(gate){var floor=Deadline(RestartBackoff.Base);resumeAt=resumeAt is {} armed&&armed>floor?armed:floor;}
                    break;
                }
                var delay=backoff.RecordFailure(time); // on the monotonic clock, as resumeAt is (F6 item 4, whole-branch review D4)
                lock(gate){if(delay is null) gaveUp=true;else resumeAt=Deadline(delay.Value);}
                if(quitting) break;
                if(delay is null){Set(EngineStatus.Failed,GaveUpText);return;}
                var wait=Task.Delay(delay.Value,time,ct); // registered before the status change so a clock advance can't miss it
                Set(EngineStatus.Restarting,null);
                firstAttempt.TrySetResult();
                await wait.ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}
        finally{firstAttempt.TrySetResult();}
    }

    // The time.GetTimestamp() value a delay from now ends at, in the timestamp units Left reads.
    long Deadline(TimeSpan delay)=>time.GetTimestamp()+(long)(delay.Ticks*(double)time.TimestampFrequency/TimeSpan.TicksPerSecond);

    // F5 item 3b (whole-branch review R4-D2): what is left until a deadline on the monotonic clock (a time.GetTimestamp() value), never
    // more than the backoff's cap.
    internal static TimeSpan Left(TimeProvider time,long due)
    {
        var left=time.GetElapsedTime(time.GetTimestamp(),due);
        return left>RestartBackoff.Cap?RestartBackoff.Cap:left;
    }

    // F6 item 9 (Ruling BH): after a kill, on a stop, a give-up or a failed launch, the supervisor waits for that OpenRGB to exit, at most
    // ExitWait, so StopAsync returns only once it is gone and the next start's self-check never meets it still listening on the port.
    // One that hasn't exited by then is logged and left; the self-check then refuses honestly. The wait has a token of its own: a stop
    // has already cancelled the supervisor's.
    async Task Exited(IRunningProcess proc)
    {
        if(proc.HasExited) return;
        using var bound=new CancellationTokenSource(ExitWait,time);
        try{await proc.WaitForExitAsync(bound.Token).ConfigureAwait(false);}
        catch(OperationCanceledException){log.Write($"openrgb hadn't exited {(int)ExitWait.TotalSeconds} s after it was ended");}
        catch(Exception e){log.Write("openrgb exit couldn't be awaited: "+e.Message);}
    }

    bool Prepared()
    {
        if(prepareConfig is null) return true;
        try{prepareConfig(configDir);return true;}
        catch(Exception e){log.Write("openrgb settings couldn't be written: "+e.Message);return false;}
    }

    // The words for a refused OpenRGB folder, or null when it may be used. They fit the reason (polish item 8): only a folder the check
    // found other programs can change is called changeable, and one it couldn't judge, a check that failed included, is reported as such.
    string? Refusal()
    {
        if(folderCheck is null) return null;
        LocationProtection verdict;
        try{verdict=folderCheck(exePath);}
        catch(Exception e){log.Write("openrgb folder couldn't be checked: "+e.Message);return OpenRgbFolderRefusal.CantCheckText(e.Message);}
        if(verdict.Protected) return null;
        log.Write("openrgb folder refused: "+verdict.Reason);
        return OpenRgbFolderRefusal.Text(verdict.Reason);
    }

    // The port lock's refusal, or null when the lock is in place or the host has none (it isn't elevated). A guard that throws couldn't
    // confirm the lock either, so it gets the lock's refusal with the exception's code and message. Fix wave 2, D: the port taken less than
    // LingerWindow after this engine ended an OpenRGB is checked again every LingerRetry until LingerWindow after that end, with one line
    // when the checks start and one when they end; a stop cancels the wait. Every other refusal is final at once.
    async Task<string?> PortRefusalAsync(CancellationToken ct)
    {
        if(portGuard is null) return null;
        var refusal=Guard();
        long? ended;
        lock(gate)ended=endedAt;
        var taken=OpenRgbPortLock.PortTakenText(Port);
        if(refusal==taken&&ended is long end&&time.GetElapsedTime(end)<LingerWindow)
        {
            log.Write($"openrgb port still in use {Seconds(end)} s after DeadWare ended its OpenRGB; checking again every second");
            // Fix wave 3, D5: a stop during the checks still logs their end.
            try
            {
                while(refusal==taken&&LingerWindow-time.GetElapsedTime(end) is {Ticks:>0} left)
                {
                    await Task.Delay(left<LingerRetry?left:LingerRetry,time,ct).ConfigureAwait(false);
                    refusal=Guard();
                }
            }
            catch(OperationCanceledException){log.Write($"openrgb port checks stopped {Seconds(end)} s after DeadWare ended its OpenRGB");throw;}
            log.Write(refusal is null?$"openrgb port free again {Seconds(end)} s after DeadWare ended its OpenRGB":$"openrgb port still in use {Seconds(end)} s after DeadWare ended its OpenRGB");
        }
        if(refusal is not null) log.Write("openrgb port guard refused: "+refusal);
        return refusal;
    }
    string? Guard()
    {
        try{return portGuard!();}
        catch(Exception e){return OpenRgbPortLock.RefusalText(e is Win32Exception w?w.NativeErrorCode:e.HResult,e.Message);}
    }
    string Seconds(long since)=>((int)time.GetElapsedTime(since).TotalSeconds).ToString(CultureInfo.InvariantCulture);

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

    // keepHistory: a conflict stop (F4 item 3, whole-branch review D8): the OpenRGB it ends counts towards the give-up, and the next start
    // waits out the delay that armed and fails at once after a give-up. A full stop, the host stopping for lighting turned off or Quit,
    // clears all of that.
    public Task StopAsync(bool keepHistory=false)=>Stop(keepHistory,false);

    // Lighting 5: the stop before DeadWare's own driver takes the hub. It counts nothing towards the give-up, arms the 4 s floor for the next
    // launch (or keeps a longer delay already armed) and keeps the history as a conflict stop does. True when no OpenRGB of this engine
    // remains; false when one had not exited by the end of the supervisor's wait, which the caller must treat as a hub still in use.
    // Fix wave 2, E: an OpenRGB launched less than YoungStop ago is ended only once it is YoungStop old (a kill in the middle of its USB
    // detection is the rapid cycle the backoff exists to avoid); the token and a full stop end that wait at once, and the stop goes on.
    public async Task<bool> StopForRestartAsync(CancellationToken token=default)
    {
        TimeSpan young;
        CancellationToken full;
        lock(gate){young=current is not null&&launchedAt is long at?YoungStop-time.GetElapsedTime(at):TimeSpan.Zero;full=fullStop.Token;}
        if(young>TimeSpan.Zero)
        {
            using var cut=CancellationTokenSource.CreateLinkedTokenSource(token,full);
            var wait=Task.Delay(young,time,cut.Token);
            ageing=true;
            try{await wait.ConfigureAwait(false);}
            catch(OperationCanceledException){}
            finally{ageing=false;}
        }
        return await Stop(true,true).ConfigureAwait(false);
    }
    volatile bool ageing;
    // Test seam: true while a deliberate stop waits for a young OpenRGB to reach YoungStop (its wait is armed).
    internal bool Ageing=>ageing;

    async Task<bool> Stop(bool keepHistory,bool isDeliberate)
    {
        if(!keepHistory){CancellationTokenSource ended;lock(gate){ended=fullStop;fullStop=new();}ended.Cancel();}
        deliberate=isDeliberate; // before the cancel: the supervisor reads it once the process has ended
        quitting=true;
        Task? sup;
        IRunningProcess? proc;
        lock(gate){sup=supervisor;proc=current;allGone=true;}
        try{cts.Cancel();}catch(ObjectDisposedException){}
        try{proc?.KillTree();}catch{}
        if(sup is not null){try{await sup.ConfigureAwait(false);}catch{}}
        bool gone;
        // Re-arm so the host can start the engine again after a conflict clears; disposal cancels the fresh token below.
        lock(gate)
        {
            gone=allGone;
            started=false;supervisor=null;cts.Dispose();cts=new();firstAttempt=new(TaskCreationOptions.RunContinuationsAsynchronously);quitting=false;deliberate=false;
            if(!keepHistory){backoff.Reset();resumeAt=null;gaveUp=false;}
        }
        Set(EngineStatus.Stopped,null);
        return gone;
    }
    public async ValueTask DisposeAsync(){await StopAsync().ConfigureAwait(false);IRunningProcess? left;lock(gate){cts.Dispose();left=lingering;lingering=null;}try{left?.Dispose();}catch{}}

    // Real port probe: one TCP connect to 127.0.0.1.
    public static async Task<bool> ProbePort(int port,CancellationToken token)
    {
        try{using var client=new TcpClient();await client.ConnectAsync(System.Net.IPAddress.Loopback,port,token).ConfigureAwait(false);return true;}
        catch(Exception e) when(e is SocketException or IOException){return false;}
    }
}
