using System.Runtime.InteropServices;
using DeadWare.Core;
using Microsoft.Win32;
namespace DeadWare.App;

/// <summary>The iCUE and DeadLight facts Settings → Lighting shows (Lighting 5, Task 12). "Running" is the helper's own rule, so the lines and the block agree.</summary>
internal interface IHubFactReader{ICueFacts ICue(IReadOnlyList<LightingConflict> conflicts,IReadOnlyList<RunningProcess> processes);DeadLightFacts DeadLight(IReadOnlyList<LightingConflict> conflicts);}

/// <summary>Reads the facts from Windows, read-only and without administrator rights: the three Corsair services' registry keys, the Run keys and
/// Task Manager's StartupApproved values, and the Task Scheduler task \DeadLight. It writes nothing, opens every registry key read-only, and changes no
/// service, entry or task. Each fact is read in its own try; an exception becomes that fact's error text.</summary>
internal sealed class WindowsHubFacts:IHubFactReader
{
    private const string ServicesKey=@"SYSTEM\CurrentControlSet\Services\",RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run",ApprovedKey=@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const int TaskActionExec=0;
    public ICueFacts ICue(IReadOnlyList<LightingConflict> conflicts,IReadOnlyList<RunningProcess> processes)=>ICueFrom(conflicts,()=>ReadServices(processes),ReadStartup);
    public DeadLightFacts DeadLight(IReadOnlyList<LightingConflict> conflicts)=>DeadLightFrom(conflicts,ReadTask);

    /// <summary>iCUE runs when the helper's scan says so (<see cref="ConflictKind.ICue"/>); the services and the start-up entries are each read in their own try.</summary>
    internal static ICueFacts ICueFrom(IReadOnlyList<LightingConflict> conflicts,Func<IReadOnlyList<ServiceFact>> services,Func<IReadOnlyList<StartupEntry>> startup)
    {
        IReadOnlyList<ServiceFact>? serviceFacts=null;IReadOnlyList<StartupEntry>? entries=null;string? servicesError=null,startupError=null;
        try{serviceFacts=services();}catch(Exception e){servicesError=e.Message;}
        try{entries=startup();}catch(Exception e){startupError=e.Message;}
        return new(conflicts.Any(c=>c.Kind==ConflictKind.ICue),serviceFacts,entries,servicesError,startupError);
    }
    /// <summary>DeadLight runs when the helper's scan says so (<see cref="ConflictKind.DeadLight"/>); the task is read in its own try.</summary>
    internal static DeadLightFacts DeadLightFrom(IReadOnlyList<LightingConflict> conflicts,Func<DeadLightTask?> task)
    {
        bool running=conflicts.Any(c=>c.Kind==ConflictKind.DeadLight);
        try{return new(running,task());}catch(Exception e){return new(running,null,e.Message);}
    }

    /// <summary>The service key's DisplayName, or the service's name when it is missing, empty, not text or an indirect "@…" string.</summary>
    internal static string DisplayName(string name,object? value)=>value is string text&&!string.IsNullOrWhiteSpace(text)&&!text.StartsWith('@')?text:name;
    /// <summary>A service runs when a process named after its program runs (<see cref="ICueLines.ProcessName"/>), the helper's test.</summary>
    internal static bool Runs(string service,IReadOnlyList<RunningProcess> processes)
    {
        string program=ICueLines.ProcessName(service);
        return processes.Any(p=>string.Equals(p.Name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?p.Name[..^4]:p.Name,program,StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>One service's fact from its key's values; null when it is not installed and its program does not run. A program that runs without a key
    /// is still shown as running, since the helper blocks on it all the same.</summary>
    internal static ServiceFact? Service(string name,bool installed,object? displayName,object? start,IReadOnlyList<RunningProcess> processes)
    {
        bool running=Runs(name,processes);
        return installed||running?new(DisplayName(name,displayName),running,installed&&ICueLines.StartsWithWindows(start as int?)):null;
    }
    /// <summary>iCUE's entries among one Run key's values, each disabled when its StartupApproved value says so (no value: enabled).</summary>
    internal static IReadOnlyList<StartupEntry> Entries(IEnumerable<KeyValuePair<string,object?>> run,Func<string,object?> approved)
        =>run.Where(v=>ICueLines.IsICueEntry(v.Key,v.Value as string)).Select(v=>new StartupEntry(v.Key,ICueLines.Disabled(approved(v.Key) as byte[]))).ToList();

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt; for each of the three services, read-only in the 64-bit view.</summary>
    private static IReadOnlyList<ServiceFact> ReadServices(IReadOnlyList<RunningProcess> processes)
    {
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        var facts=new List<ServiceFact>();
        foreach(var name in ICueLines.Services)
        {
            using var key=machine.OpenSubKey(ServicesKey+name,writable:false);
            if(Service(name,key!=null,key?.GetValue("DisplayName"),key?.GetValue("Start"),processes) is {} fact) facts.Add(fact);
        }
        return facts;
    }
    /// <summary>The Run keys of HKCU and of HKLM in its 64- and 32-bit views; disabled from StartupApproved\Run in the same hive (Run32 for HKLM's
    /// 32-bit view), read in the 64-bit view. HKCU\Software is not split by view, so HKCU is read once.</summary>
    private static IReadOnlyList<StartupEntry> ReadStartup()
    {
        using var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64);
        using var machine64=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var machine32=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry32);
        return [..ReadRun(user,user,"Run"),..ReadRun(machine64,machine64,"Run"),..ReadRun(machine32,machine64,"Run32")];
    }
    private static IReadOnlyList<StartupEntry> ReadRun(RegistryKey runView,RegistryKey approvedView,string approvedName)
    {
        using var run=runView.OpenSubKey(RunKey,writable:false);
        if(run is null) return [];
        using var approved=approvedView.OpenSubKey(ApprovedKey+approvedName,writable:false);
        return Entries(run.GetValueNames().Select(n=>KeyValuePair.Create(n,run.GetValue(n))),n=>approved?.GetValue(n));
    }

    /// <summary>The task \DeadLight through the Task Scheduler interface, as StartupRegistration reads its own task: null when there is none (HRESULT
    /// 0x80070002); throws when it cannot be read. Reading only: nothing is set, run, stopped or deleted here.</summary>
    internal static DeadLightTask? ReadTask()
    {
        object? service=null,folder=null,task=null;
        try
        {
            service=Connect();folder=((dynamic)service).GetFolder("\\");task=GetTask(folder,"DeadLight");
            return ParseTask(task);
        }
        finally{Release(task);Release(folder);Release(service);}
    }
    /// <summary>A registered task's switch and its first action's program, arguments and working directory (empty texts are absent). An action that is
    /// not a program (TASK_ACTION_EXEC) gives none of the three.</summary>
    internal static DeadLightTask? ParseTask(dynamic? task)
    {
        if((object?)task is null) return null;
        bool enabled=(bool)task.Enabled;
        object? definition=null,actions=null,action=null;
        try
        {
            definition=task.Definition;actions=((dynamic)definition!).Actions;
            if((int)((dynamic)actions!).Count<1) return new(enabled,null,null,null);
            action=((dynamic)actions).Item(1);
            dynamic exec=action!;
            if((int)exec.Type!=TaskActionExec) return new(enabled,null,null,null);
            return new(enabled,Text((object?)exec.Path),Text((object?)exec.Arguments),Text((object?)exec.WorkingDirectory));
        }
        finally{Release(action);Release(actions);Release(definition);}
    }
    private static string? Text(object? value)=>value is string text&&!string.IsNullOrWhiteSpace(text)?text:null;
    internal static object Connect()
    {
        dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")??throw new InvalidOperationException("Windows Task Scheduler is unavailable."))!;
        try{service.Connect();return service;}catch{Release(service);throw;}
    }
    private static object? GetTask(dynamic folder,string name)
    {
        try{return folder.GetTask(name);}
        // COM's missing-file HRESULT may be projected as FileNotFoundException by .NET.
        catch(Exception e) when(e.HResult==unchecked((int)0x80070002)){return null;}
    }
    internal static void Release(object? value){if(value is not null&&Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);}
}
