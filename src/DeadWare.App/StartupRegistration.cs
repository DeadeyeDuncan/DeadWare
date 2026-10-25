using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DeadWare.Core;
namespace DeadWare.App;

internal sealed record StartupRegistrationStatus(string TaskName,bool Registered,bool Owned,bool Enabled,bool NeedsRepair,string? Executable,string Message,string? Error=null,string? TaskXml=null)
{
    public bool Ready=>Registered&&Owned&&Enabled&&!NeedsRepair&&Error is null;
}
internal sealed record StartupConfigurationResult(bool Succeeded,bool Cancelled,int HelperExitCode,string Message,StartupRegistrationStatus Registration);

internal static class StartupRegistration
{
    public static string CurrentSid {get {using var identity=WindowsIdentity.GetCurrent();return identity.User?.Value??throw new InvalidOperationException("Windows user identity unavailable.");}}
    public static bool IsAdministrator {get {using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
    public static string Executable=>Environment.ProcessPath??throw new InvalidOperationException("Executable path unavailable.");
    private static void Release(object? value) {if(value is not null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    private static object Connect()
    {
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")??throw new InvalidOperationException("Windows Task Scheduler is unavailable."))!;
        try {service.Connect();return service;}catch {Release(service);throw;}
    }
    private static object? GetTask(dynamic folder,string name)
    {
        try {return folder.GetTask(name);}
        // COM's missing-file HRESULT may be projected as FileNotFoundException by .NET.
        catch(Exception e) when(e.HResult==unchecked((int)0x80070002)) {return null;}
    }
    public static StartupRegistrationStatus Query()
    {
        string sid=CurrentSid,name=StartupTaskPolicy.TaskName(sid);object? service=null,folder=null,task=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder("\\");task=GetTask(folder,name);
            if(task is null)return new(name,false,false,false,false,null,"Off — DeadWare will not start at sign-in.");
            string xml=((dynamic)task).Xml;bool owned=StartupTaskPolicy.IsOwned(xml,sid),enabled=((dynamic)task).Enabled;
            string? executable=StartupTaskPolicy.Executable(xml);bool repair=owned&&!StartupTaskPolicy.IsCurrent(xml,sid,Executable);
            string message=!owned?"A task with this name belongs to something else. DeadWare will not change it.":repair?"Startup needs repair: its executable location or task settings differ. Choose Repair startup to use this copy of DeadWare.":enabled?"On — starts as administrator when you sign in.":"Off — the registered startup task is disabled.";
            return new(name,true,owned,enabled,repair,executable,message,TaskXml:xml);
        }
        catch(Exception e) {return new(name,false,false,false,false,null,"Startup status could not be read.",e.Message);}
        finally {Release(task);Release(folder);Release(service);}
    }
    // Called only by the explicitly elevated helper (or an already elevated UI).
    private static int Apply(bool enable,string expectedSid)
    {
        try {StartupTaskPolicy.RequireSameUser(expectedSid,CurrentSid);}catch(InvalidOperationException) {return 2;}
        if(!IsAdministrator)return 5;
        string name=StartupTaskPolicy.TaskName(expectedSid);object? service=null,folder=null,existing=null,registered=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder("\\");existing=GetTask(folder,name);
            if(existing is not null&&!StartupTaskPolicy.IsOwned((string)((dynamic)existing).Xml,expectedSid))return 3;
            if(enable)
            {
                // CREATE for absence prevents an unchecked overwrite if another task appears.
                // UPDATE is allowed only after the ownership + principal check above.
                registered=((dynamic)folder).RegisterTask(name,StartupTaskPolicy.CreateXml(expectedSid,Executable),existing is null?2:4,expectedSid,null,3,null);
            }
            else if(existing is not null)((dynamic)folder).DeleteTask(name,0);
            return 0;
        }
        catch(Exception e) {return e.HResult;}
        finally {Release(registered);Release(existing);Release(folder);Release(service);}
    }
    public static async Task<StartupConfigurationResult> ConfigureAsync(bool enable)
    {
        int code;string sid=CurrentSid;
        try
        {
            if(IsAdministrator)code=await Task.Run(()=>Apply(enable,sid));
            else
            {
                var start=new ProcessStartInfo(Executable) {UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
                start.ArgumentList.Add("--startup-apply");start.ArgumentList.Add(enable?"enable":"disable");start.ArgumentList.Add("--expected-user");start.ArgumentList.Add(sid);
                using var child=Process.Start(start)??throw new InvalidOperationException("Windows did not start the configuration helper.");
                await child.WaitForExitAsync();code=child.ExitCode;
            }
        }
        catch(Win32Exception e) when(e.NativeErrorCode==1223) {code=1223;}
        catch(Exception e) {code=e.HResult;}
        var status=await Task.Run(Query);
        bool succeeded=code==0&&status.Error is null&&(enable?status.Ready:!status.Registered);
        string message=succeeded?(enable?"Startup enabled for this executable.":"Startup disabled."):code switch
        {
            1223=>"Administrator approval was cancelled. Startup was not changed.",
            2=>"Windows used a different administrator account. Startup was not changed; use the original user's administrator account.",
            3=>"The existing task is not owned by DeadWare for this user. It was not changed.",
            5=>"Administrator approval is required to change startup.",
            _=>"Windows could not confirm the startup change"+(code==0?".":$" (0x{code:X8}).")+" "+status.Message+(status.Error is null?"":" "+status.Error)
        };
        return new(succeeded,code==1223,code,message,status);
    }
    public static async Task<int?> HandleCommandLineAsync(string[] args)
    {
        if(!args.Any(a=>a is "--startup-status" or "--startup-config" or "--startup-apply"))return null;
        string? output=null;
        string Required(string option)
        {
            int index=Array.IndexOf(args,option);
            return index>=0&&index+1<args.Length?args[index+1]:throw new ArgumentException("Missing value for "+option);
        }
        bool Enable(string option)=>Required(option) switch {"enable"=>true,"disable"=>false,_=>throw new ArgumentException("Use enable or disable.")};
        try
        {
            if(args.Contains("--startup-apply"))return Apply(Enable("--startup-apply"),Required("--expected-user"));
            output=Path.GetFullPath(Required(args.Contains("--startup-status")?"--startup-status":"--result"));
            object report;int code;
            if(args.Contains("--startup-status")) {var status=await Task.Run(Query);report=status;code=status.Error is null?0:1;}
            else {var result=await ConfigureAsync(Enable("--startup-config"));report=result;code=result.Succeeded?0:result.Cancelled?1223:1;}
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));return code;
        }
        catch(Exception e)
        {
            if(output is not null)try {await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new {Error=e.Message}));}catch(Exception) { }
            return 1;
        }
    }
}
