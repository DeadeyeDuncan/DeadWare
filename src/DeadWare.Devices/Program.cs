using System.Diagnostics;
using System.Security.Principal;
using DeadWare.Core;

namespace DeadWare.Devices;

internal static class Program
{
    // The host's kill-on-close job (Ruling Z), held in a static field for the life of the process and never disposed: Windows closes the
    // handle when the host ends, however it ends, and that ends OpenRGB with it.
    static ProcessJob? job;

    // Exit codes: 0 after Stop, 1 unexpected failure, 2 bad arguments, 3 another host already runs in this session, 4 the pipe could not be created.
    private static async Task<int> Main(string[] args)
    {
        string? openRgb=null;var startup=false;
        for(var i=0;i<args.Length;i++)
        {
            if(args[i]=="--openrgb"&&i+1<args.Length) openRgb=args[++i];
            else if(args[i]=="--startup") startup=true;
            else return 2;
        }
        if(string.IsNullOrWhiteSpace(openRgb)) return 2;

        string openRgbPath;
        try{openRgbPath=Path.GetFullPath(openRgb);}catch(Exception){return 2;}

        var sid=WindowsIdentity.GetCurrent().User!.Value;
        var session=Process.GetCurrentProcess().SessionId;
        using var mutex=new Mutex(true,DeviceHostProtocol.InstanceName(sid,session),out var created);
        if(!created) return 3;

        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare");
        var lightingPath=Path.Combine(root,"lighting.json");
        // Ruling AT and B1/B20: elevated, the log lives in protected %ProgramData%\DeadWare\logs\<user SID>, which only this user and
        // administrators can read, or nowhere if that folder can't be prepared; the host then runs on without a log (polish item 7). That
        // folder is made only here, privately: the log never makes it again (polish round 2, item 6).
        string? logDir;
        try{logDir=ProtectedFolders.LogDirectory(sid);}catch(Exception){logDir=null;}
        using var log=new HostLog(logDir,createFolder:!Environment.IsPrivilegedProcess);
        try
        {
            // A refused folder's message names the folder at fault, InstallLocation's reason and the remedy, and reaches the window as the
            // host's state, so the reason is shown even when the same refusal left the host without a log (polish item 7).
            var configDir="";string? failure=null;
            try{configDir=ProtectedFolders.OpenRgbConfigDirectory();}
            catch(Exception e)
            {
                failure=e is UnprotectedFolderException?e.Message:$"DeadWare couldn't prepare OpenRGB's settings folder, so it won't start OpenRGB: {e.Message}";
                log.Write("openrgb config folder unavailable: "+e.Message);
            }
            try{job=ProcessJob.Create();}
            catch(Exception e){log.Write("no job object, so OpenRGB may outlive a host that crashes: "+e.Message);}
            var options=new HostOptions(openRgbPath,configDir,lightingPath,logDir??"",startup,sid,session);
            var engine=new OpenRgbEngine(options.OpenRgbPath,configDir,new SystemProcessRunner(job,log),OpenRgbEngine.ProbePort,TimeProvider.System,log,ProtectedFolders.OpenRgbFolderCheck(Environment.IsPrivilegedProcess),dir=>OpenRgbConfig.Prepare(dir,log));
            await using var host=new DeviceHost(options,new LightingStore(lightingPath,guardLinks:Environment.IsPrivilegedProcess),engine,()=>new OpenRgbSession(),new ProcessConflictScanner(),TimeProvider.System,log,failure);
            await using var server=new HostPipeServer(DeviceHostProtocol.PipeName(sid),host.HandleAsync,log);
            host.Initialize(); // settings are loaded before the pipe accepts a single request
            try{server.Open();}
            catch(Exception e){log.Write("pipe could not be created: "+e.Message);return 4;}
            host.StateChanged+=state=>_=server.SendAsync(new StateEvent(state));
            using var pipeCts=new CancellationTokenSource();
            using var hostCts=new CancellationTokenSource();
            var serving=server.RunAsync(pipeCts.Token);
            var running=host.RunAsync(hostCts.Token);
            var first=await Task.WhenAny(running,serving).ConfigureAwait(false);
            if(first==serving&&!running.IsCompleted)
            {
                // The pipe died: the window can no longer reach us. Exit non-zero so its supervisor restarts the host.
                log.Write("pipe server ended unexpectedly: "+(serving.Exception?.GetBaseException().Message??"no error"));
                hostCts.Cancel();
                try{await running.ConfigureAwait(false);}catch(OperationCanceledException){}
                return 1;
            }
            await running.ConfigureAwait(false);
            await server.WaitForStopReplyAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            pipeCts.Cancel();
            try{await serving.ConfigureAwait(false);}catch(OperationCanceledException){}
            return 0;
        }
        catch(Exception e)
        {
            log.Write("host crashed: "+e);
            return 1;
        }
    }
}
