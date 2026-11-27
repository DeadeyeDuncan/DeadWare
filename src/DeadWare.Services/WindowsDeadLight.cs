using System.IO;
using System.Runtime.InteropServices;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.App;

/// <summary>Retire DeadLight's Windows calls (Lighting 5, Task 12), used only through <see cref="DeadLightRetire.Run"/>, which calls nothing here unless
/// DeadWare runs as administrator; the changing calls also refuse on their own without it. It switches the task \DeadLight off (never deletes it),
/// stops that task's running instance, and ends a process only through one handle on which its path was read and matched first. It deletes nothing,
/// writes no file or registry value and never uses <c>Process.MainModule</c>.</summary>
internal sealed class WindowsDeadLight:IDeadLightSystem
{
    private const uint ProcessQueryLimitedInformation=0x1000,ProcessTerminate=0x0001,Synchronize=0x00100000,WaitObject0=0;
    private const int ErrorInvalidParameter=87,MaxPackageJson=1<<20;
    private static readonly int TaskNotRunning=unchecked((int)0x8004130B);

    public DeadLightTask? ReadTask()=>WindowsHubFacts.ReadTask();
    /// <summary>Sets the registered task's Enabled to false; the task stays, so the owner can switch it back on in Task Scheduler.</summary>
    public void DisableTask()=>WithTask(task=>task.Enabled=false);
    /// <summary>IRegisteredTask.Stop(0): ends the task's running instance; a task with none running is not a failure.</summary>
    public void EndTask()=>WithTask(task=>{try{task.Stop(0);}catch(Exception e) when(e.HResult==TaskNotRunning){}});
    public IReadOnlyList<RunningProcess> Processes()=>LightingConflicts.RunningProcesses();
    public string? ReadText(string path)
    {
        try{var file=new FileInfo(path);return file.Exists&&file.Length<=MaxPackageJson?File.ReadAllText(path):null;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException){return null;}
    }
    /// <summary>Opens the process once, reads its image path on that handle and ends it through the same handle only when the path is <paramref name="path"/>
    /// (case ignored). While the handle is open the id cannot pass to another process, so the check and the end apply to the same one.</summary>
    public bool End(int pid,string path)
    {
        if(!Environment.IsPrivilegedProcess) return false;
        using var process=OpenProcess(ProcessQueryLimitedInformation|ProcessTerminate|Synchronize,false,pid);
        if(process.IsInvalid) return false;
        return NativeProcessIdentity.ImagePath(process) is {} actual&&string.Equals(actual,path,StringComparison.OrdinalIgnoreCase)&&TerminateProcess(process,1);
    }
    /// <summary>Waits, within one deadline for all, until every process is gone: one that no longer exists is gone; one that cannot be opened to wait on is not.</summary>
    public bool WaitGone(IReadOnlyList<int> pids,TimeSpan limit)
    {
        long deadline=Environment.TickCount64+(long)limit.TotalMilliseconds;
        foreach(var pid in pids)
        {
            using var process=OpenProcess(Synchronize,false,pid);
            if(process.IsInvalid)
            {
                if(Marshal.GetLastPInvokeError()==ErrorInvalidParameter) continue;
                return false;
            }
            if(WaitForSingleObject(process,(uint)Math.Max(0,deadline-Environment.TickCount64))!=WaitObject0) return false;
        }
        return true;
    }
    // The task \DeadLight in the root folder, changed by `change` and released. A missing task throws, and the result line reports it.
    private static void WithTask(Action<dynamic> change)
    {
        if(!Environment.IsPrivilegedProcess) throw new InvalidOperationException("DeadWare is not running as administrator.");
        object? service=null,folder=null,task=null;
        try
        {
            service=WindowsHubFacts.Connect();folder=((dynamic)service).GetFolder("\\");task=((dynamic)folder).GetTask("DeadLight");
            change(task!);
        }
        finally{WindowsHubFacts.Release(task);WindowsHubFacts.Release(folder);WindowsHubFacts.Release(service);}
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
}
