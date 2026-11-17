using System.Diagnostics;
using System.Security.Principal;
using DeadWare.Core;

namespace DeadWare.Devices;

internal static class Program
{
    // Exit codes: 0 after Stop, 1 unexpected failure, 2 bad arguments, 3 another host already runs in this session.
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

        var sid=WindowsIdentity.GetCurrent().User!.Value;
        var session=Process.GetCurrentProcess().SessionId;
        using var mutex=new Mutex(true,DeviceHostProtocol.InstanceName(sid,session),out var created);
        if(!created) return 3;

        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare");
        var logDir=Path.Combine(root,"logs");
        var lightingPath=Path.Combine(root,"lighting.json");
        using var log=new HostLog(logDir);
        try
        {
            var configDir="";string? failure=null;
            try{configDir=ProtectedFolders.OpenRgbConfigDirectory();}
            catch(Exception e){failure=e.Message;log.Write("openrgb config folder unavailable: "+e.Message);}
            var options=new HostOptions(Path.GetFullPath(openRgb),configDir,lightingPath,logDir,startup,sid,session);
            var engine=new OpenRgbEngine(options.OpenRgbPath,configDir,new SystemProcessRunner(),OpenRgbEngine.ProbePort,TimeProvider.System,log);
            await using var host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>new OpenRgbSession(),new ProcessConflictScanner(),TimeProvider.System,log,failure);
            await using var server=new HostPipeServer(DeviceHostProtocol.PipeName(sid),host.HandleAsync,log);
            host.StateChanged+=state=>_=server.SendAsync(new StateEvent(state));
            using var cts=new CancellationTokenSource();
            var serving=server.RunAsync(cts.Token);
            await host.RunAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false); // let the Stop reply reach the window before the pipe closes
            cts.Cancel();
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
