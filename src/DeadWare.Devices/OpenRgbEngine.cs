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
    volatile bool quitting;
    bool started;
    // F4 item 3 (whole-branch review D8, D9): the restart history a conflict stop keeps and a full stop clears, with the backoff's count.
    // resumeAt: the earliest the next launch may start, armed with each restart delay. gaveUp: the backoff gave up, so a start fails at once.
    // F5 item 3b (whole-branch review R4-D2): resumeAt is a time.GetTimestamp() value, so a step of the wall clock doesn't move it.
    long? resumeAt;
    bool gaveUp;
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
    // portGuard: an elevated host's OpenRgbPortLock.Ensure (Ruling BG), run before every launch: null when OpenRGB's SDK port is locked to
    // the host, else the refusal. Null for a host without administrator rights, which adds no lock.
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
                if(PortRefusal() is {} denied){Set(EngineStatus.Failed,denied);return;}
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
                // F4 item 3 (whole-branch review D8): every OpenRGB that was launched and ended counts, one a stop ended included, so a program
                // that comes and goes can't cycle OpenRGB faster or longer than crashes can. A full stop clears the count after it.
                if(quitting&&proc is null) break;
                var now=time.GetUtcNow();
                var delay=backoff.RecordFailure(now);
                lock(gate){if(delay is null) gaveUp=true;else resumeAt=time.GetTimestamp()+(long)(delay.Value.Ticks*(double)time.TimestampFrequency/TimeSpan.TicksPerSecond);}
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

    // F5 item 3b (whole-branch review R4-D2): what is left until a deadline on the monotonic clock (a time.GetTimestamp() value), never
    // more than the backoff's cap.
    internal static TimeSpan Left(TimeProvider time,long due)
    {
        var left=time.GetElapsedTime(time.GetTimestamp(),due);
        return left>RestartBackoff.Cap?RestartBackoff.Cap:left;
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
    // confirm the lock either, so it gets the lock's refusal with the exception's code and message.
    string? PortRefusal()
    {
        if(portGuard is null) return null;
        string? refusal;
        try{refusal=portGuard();}
        catch(Exception e){refusal=OpenRgbPortLock.RefusalText(e is Win32Exception w?w.NativeErrorCode:e.HResult,e.Message);}
        if(refusal is not null) log.Write("openrgb port couldn't be locked: "+refusal);
        return refusal;
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

    // keepHistory: a conflict stop (F4 item 3, whole-branch review D8): the OpenRGB it ends counts towards the give-up, and the next start
    // waits out the delay that armed and fails at once after a give-up. A full stop, the host stopping for lighting turned off or Quit,
    // clears all of that.
    public async Task StopAsync(bool keepHistory=false)
    {
        quitting=true;
        Task? sup;
        IRunningProcess? proc;
        lock(gate){sup=supervisor;proc=current;}
        try{cts.Cancel();}catch(ObjectDisposedException){}
        try{proc?.KillTree();}catch{}
        if(sup is not null){try{await sup.ConfigureAwait(false);}catch{}}
        // Re-arm so the host can start the engine again after a conflict clears; disposal cancels the fresh token below.
        lock(gate)
        {
            started=false;supervisor=null;cts.Dispose();cts=new();firstAttempt=new(TaskCreationOptions.RunContinuationsAsynchronously);quitting=false;
            if(!keepHistory){backoff.Reset();resumeAt=null;gaveUp=false;}
        }
        Set(EngineStatus.Stopped,null);
    }
    public async ValueTask DisposeAsync(){await StopAsync().ConfigureAwait(false);lock(gate)cts.Dispose();}

    // Real port probe: one TCP connect to 127.0.0.1.
    public static async Task<bool> ProbePort(int port,CancellationToken token)
    {
        try{using var client=new TcpClient();await client.ConnectAsync(System.Net.IPAddress.Loopback,port,token).ConfigureAwait(false);return true;}
        catch(Exception e) when(e is SocketException or IOException){return false;}
    }
}
