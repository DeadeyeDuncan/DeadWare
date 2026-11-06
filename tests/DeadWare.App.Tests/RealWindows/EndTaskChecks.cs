using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>End task on real processes on GitHub's elevated Windows runner (spec, Testing). Only processes the test started are ever ended:
/// Windows' own processes go through End task with a confirm hook that answers No.</summary>
[Collection("RealWindows")]
public sealed class EndTaskChecks
{
    private const string HelperName="deadware-probe-helper.exe";
    private const string AlreadyExited="Ended 0 · not ended 0 · already exited 1 · still running 0";
    private const uint ProcessTerminate=0x0001;
    private const int ErrorAccessDenied=5;
    private static readonly string[] RequiredWindowsNames=["csrss.exe","lsass.exe","services.exe","svchost.exe"],WindowsNamesWhenRunning=["dwm.exe","MsMpEng.exe"];
    // The refusal rules. "Denied by Windows" or "needs administrator" would mean a fact went unread.
    private static readonly string[] RuleRefusals=["Not ended: 1 system process","Not ended: 1 critical process","Not ended: 1 protected process","Not ended: 1 Windows' own process"];

    /// <summary>The confirmation hook: records each question and answers as scripted.</summary>
    private sealed class Confirm(bool answer)
    {
        private readonly List<string> questions=[];
        public IReadOnlyList<string> Questions {get {lock(questions)return [..questions];}}
        public bool Ask(string question,string? summary) {lock(questions)questions.Add(question);return answer;}
    }
    private static ProcessActions Actions(IProcessControl control,Confirm confirm)=>
        new(control,confirm.Ask,_=>throw new InvalidOperationException("End task never reveals."),Environment.ProcessId,MeasurementRecorder.Elevated);

    [ElevatedGitHubFact] public async Task Windows_processes_are_refused_with_a_No_answer()
    {
        var control=new NativeProcessControl();
        var confirm=new Confirm(answer:false);
        var actions=Actions(control,confirm);
        var targets=Scan().Where(p=>p.Pid==4||RequiredWindowsNames.Concat(WindowsNamesWhenRunning).Contains(p.Name,StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.Contains(targets,p=>p.Pid==4);
        foreach(string name in RequiredWindowsNames)Assert.Contains(targets,p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        var statuses=new List<(ProcessScanEntry Target,string Status)>();
        foreach(var target in targets)statuses.Add((target,await actions.EndAsync(target.Name,[(target.Pid,target.Generation)],wholeApp:false)));
        Assert.Empty(confirm.Questions);
        Assert.Empty(statuses.Where(s=>!RuleRefusals.Contains(s.Status)&&s.Status!=AlreadyExited).Select(s=>$"{s.Target.Name} (PID {s.Target.Pid}): {s.Status}").ToList());
        // A process that exited between the scan and End task is reported as already exited; the next scan must show it gone.
        var exited=statuses.Where(s=>s.Status==AlreadyExited).Select(s=>s.Target).ToList();
        if(exited.Count>0)
        {
            var after=Scan();
            foreach(var gone in exited)Assert.DoesNotContain(after,p=>p.Pid==gone.Pid&&p.Generation==gone.Generation);
        }
        Assert.Equal("Not ended: 1 system process",statuses.Single(s=>s.Target.Pid==4).Status);
        foreach(var (csrss,status) in statuses.Where(s=>s.Target.Name.Equals("csrss.exe",StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal("Not ended: 1 critical process",status);
            // The facts End task classified, read through its own open: the handle is only closed.
            var open=control.OpenForTerminate(csrss.Pid,enableDebugPrivilege:true);
            EndTaskFacts facts;
            using(open.Handle)facts=control.ReadFacts(csrss.Pid,open);
            Assert.Equal(csrss.Generation,facts.CreationTicks);
            Assert.True(facts.Critical==true&&facts.Protected==true,$"csrss.exe (PID {csrss.Pid}): {facts}");
        }
    }

    [ElevatedGitHubFact] public async Task LocalSystem_helper_is_allowed_and_ended_only_with_the_debug_privilege()
    {
        var control=new NativeProcessControl();
        var confirm=new Confirm(answer:true);
        string folder=Path.Combine(Path.GetTempPath(),$"DeadWare-EndTask-{Guid.NewGuid():N}");
        string task=$"DeadWare-Probe-{Guid.NewGuid():N}";
        SafeProcessHandle? watch=null;
        try
        {
            Directory.CreateDirectory(folder);
            string helper=CopyPing(folder);
            await Schtasks(expectSuccess:true,"/Create","/TN",task,"/SC","ONCE","/ST","23:59","/RU","SYSTEM","/TR",$"\"{helper}\" -n 600 127.0.0.1","/F");
            await Schtasks(expectSuccess:true,"/Run","/TN",task);
            var found=await FindHelper(control,folder);
            watch=found.Open.Handle;
            Assert.True(watch is not null,"End task's open, with the debug privilege enabled for it, was denied.");
            Assert.Equal("S-1-5-18",found.Facts.TokenUserSid);
            var verdict=EndTaskPolicy.Classify(found.Facts,found.Generation,control.WindowsFolder,MeasurementRecorder.Elevated);
            Assert.True(verdict==EndTaskVerdict.CanEnd,$"The LocalSystem helper classified {verdict}: {found.Facts}");
            string status;
            using(new DisabledDebugPrivilege())
            {
                using(var plain=OpenProcess(ProcessTerminate,false,found.Pid))
                {
                    int error=Marshal.GetLastPInvokeError();
                    Assert.True(plain.IsInvalid,"A plain terminate open of the LocalSystem helper succeeded with the debug privilege disabled.");
                    Assert.Equal(ErrorAccessDenied,error);
                }
                status=await Actions(control,confirm).EndAsync(HelperName,[(found.Pid,found.Generation)],wholeApp:false);
            }
            Assert.Equal("Ended 1 · not ended 0 · already exited 0 · still running 0",status);
            Assert.Equal([$"End {HelperName} (PID {found.Pid})? Unsaved work in it will be lost."],confirm.Questions);
            control.WaitAll([watch],TimeSpan.FromSeconds(5));
            Assert.True(control.IsSignalled(watch),"The LocalSystem helper is still running.");
        }
        finally
        {
            // The helper is ended only through the handle whose image path End task's open confirmed.
            if(watch is not null)
            {
                if(!control.IsSignalled(watch)&&control.Terminate(watch))control.WaitAll([watch],TimeSpan.FromSeconds(5));
                watch.Dispose();
            }
            await DeleteTask(task);
            DeleteFolder(folder);
        }
    }

    [ElevatedGitHubFact] public async Task Own_child_processes_end()
    {
        var control=new NativeProcessControl();
        var children=new List<Process>();
        try
        {
            var single=StartPing(children);
            var confirm=new Confirm(answer:true);
            Assert.Equal("Ended 1 · not ended 0 · already exited 0 · still running 0",await Actions(control,confirm).EndAsync("PING.EXE",[Member(single)],wholeApp:false));
            Assert.Equal([$"End PING.EXE (PID {single.Id})? Unsaved work in it will be lost."],confirm.Questions);
            Assert.True(single.WaitForExit(5000),$"Child PID {single.Id} is still running.");

            Process[] app=[StartPing(children),StartPing(children),StartPing(children)];
            var asked=new Confirm(answer:true);
            Assert.Equal("Ended 3 · not ended 0 · already exited 0 · still running 0",await Actions(control,asked).EndAsync("PING.EXE",[..app.Select(Member)],wholeApp:true));
            Assert.Equal(["End PING.EXE (3 processes)? Unsaved work in it will be lost."],asked.Questions);
            foreach(var child in app)Assert.True(child.WaitForExit(5000),$"Child PID {child.Id} is still running.");
        }
        finally
        {
            foreach(var child in children)
            {
                try {if(!child.HasExited)child.Kill();}
                catch(Exception) {}
                child.Dispose();
            }
        }
    }

    private static IReadOnlyList<ProcessScanEntry> Scan()
    {
        var source=new WindowsProcesses();
        var scan=source.Scan(Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,1);
        Assert.True(scan is not null,$"Process scan failed: {source.Failure}");
        return scan!.Processes;
    }

    /// <summary>Copies Windows' ping into <paramref name="folder"/> as the helper, with its language resources under the new name so the copy can load its messages.</summary>
    private static string CopyPing(string folder)
    {
        string system=Environment.SystemDirectory,helper=Path.Combine(folder,HelperName);
        File.Copy(Path.Combine(system,"PING.EXE"),helper);
        string language=CultureInfo.InstalledUICulture.Name,mui=Path.Combine(system,language,"ping.exe.mui");
        if(File.Exists(mui))
        {
            Directory.CreateDirectory(Path.Combine(folder,language));
            File.Copy(mui,Path.Combine(folder,language,HelperName+".mui"));
        }
        return helper;
    }

    /// <summary>Runs Windows' schtasks; with <paramref name="expectSuccess"/> a non-zero exit fails the check with schtasks' own output.</summary>
    private static async Task Schtasks(bool expectSuccess,params string[] arguments)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var schtasks=Process.Start(start)!;
        var output=Task.WhenAll(schtasks.StandardOutput.ReadToEndAsync(),schtasks.StandardError.ReadToEndAsync());
        await schtasks.WaitForExitAsync();
        string text=string.Concat(await output);
        if(expectSuccess)Assert.True(schtasks.ExitCode==0,$"schtasks {arguments[0]} exited with {schtasks.ExitCode}: {text}");
    }

    /// <summary>Ends the task's helper if it is running, including one that started but was never found, then deletes the task. Never throws, so the folder is still removed.</summary>
    private static async Task DeleteTask(string task)
    {
        string[][] steps=[["/End","/TN",task],["/Delete","/TN",task,"/F"]];
        foreach(var step in steps)
        {
            try {await Schtasks(expectSuccess:false,step);}
            catch(Exception) {}
        }
    }

    /// <summary>Removes the helper's folder, retrying while its image is still locked just after the helper exits. Never throws.</summary>
    private static void DeleteFolder(string folder)
    {
        for(int attempt=0;attempt<25&&Directory.Exists(folder);attempt++)
        {
            try {Directory.Delete(folder,recursive:true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {Thread.Sleep(200);}
        }
    }

    /// <summary>Waits for the task's helper, found by name and confirmed by its image path in the test's own folder, and returns End task's own open of it with the facts read on that open.</summary>
    private static async Task<(int Pid,long Generation,ProcessOpen Open,EndTaskFacts Facts)> FindHelper(NativeProcessControl control,string folder)
    {
        // The folder's own name, not its full path: the temp path may be in 8.3 form and the image path in long form, or the reverse.
        string tail=@"\"+Path.GetFileName(folder)+@"\"+HelperName;
        var waited=Stopwatch.StartNew();
        while(waited.Elapsed<TimeSpan.FromSeconds(30))
        {
            foreach(int pid in ProcessIds(Path.GetFileNameWithoutExtension(HelperName)))
            {
                var open=control.OpenForTerminate(pid,enableDebugPrivilege:true);
                var facts=control.ReadFacts(pid,open);
                if(facts.CreationTicks is long generation&&facts.ImagePath?.EndsWith(tail,StringComparison.OrdinalIgnoreCase)==true)return (pid,generation,open,facts);
                open.Handle?.Dispose();
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("The LocalSystem helper was not running 30 s after schtasks /Run.");
    }
    /// <summary>System.Diagnostics.Process may enable SeDebugPrivilege when first used (spec), so the check uses it only before it disables the privilege.</summary>
    private static int[] ProcessIds(string name)
    {
        var processes=Process.GetProcessesByName(name);
        try {return [..processes.Select(p=>p.Id)];}
        finally {foreach(var process in processes)process.Dispose();}
    }

    /// <summary>Starts a ping child of the test that runs for ten minutes unless ended, and adds it to <paramref name="children"/> for cleanup.</summary>
    private static Process StartPing(List<Process> children)
    {
        var child=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"PING.EXE"),"-n 600 127.0.0.1") {UseShellExecute=false,CreateNoWindow=true})!;
        children.Add(child);
        return child;
    }
    private static (int Pid,long Generation) Member(Process child)=>
        NativeProcessIdentity.Times(child.SafeHandle) is {Exited:false} times?(child.Id,times.Generation):throw new InvalidOperationException($"Child PID {child.Id} is not running.");

    /// <summary>SeDebugPrivilege disabled on the test process's own token until disposed; disposing restores exactly the PreviousState AdjustTokenPrivileges returned.</summary>
    private sealed class DisabledDebugPrivilege:IDisposable
    {
        private const uint TokenAdjustPrivileges=0x0020,TokenQuery=0x0008;
        private const int ErrorNotAllAssigned=1300;
        private readonly SafeAccessTokenHandle token;
        private TokenPrivileges previous;
        public DisabledDebugPrivilege()
        {
            if(!OpenProcessToken(GetCurrentProcess(),TokenAdjustPrivileges|TokenQuery,out token))throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                if(!LookupPrivilegeValue(null,"SeDebugPrivilege",out var luid))throw new Win32Exception(Marshal.GetLastPInvokeError());
                var disabled=new TokenPrivileges {Count=1,Luid=luid,Attributes=0};
                bool adjusted=AdjustTokenPrivileges(token,false,ref disabled,(uint)Marshal.SizeOf<TokenPrivileges>(),out previous,out _);
                int error=Marshal.GetLastPInvokeError();
                if(!adjusted)throw new Win32Exception(error);
                // Success with ERROR_NOT_ALL_ASSIGNED: the token does not hold the privilege, nothing changed, and the check cannot run.
                if(error==ErrorNotAllAssigned)throw new InvalidOperationException("The test's token does not hold SeDebugPrivilege.");
            }
            catch {token.Dispose();throw;}
        }
        public void Dispose()
        {
            using(token)
            {
                // PreviousState lists only the privileges the change altered: none when SeDebugPrivilege was already disabled.
                if(previous.Count>0&&!AdjustTokenPrivileges(token,false,ref previous,0,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges {public uint Count;public Luid Luid;public uint Attributes;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="LookupPrivilegeValueW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,[MarshalAs(UnmanagedType.Bool)]bool disableAll,ref TokenPrivileges newState,uint bufferLength,out TokenPrivileges previousState,out uint returnLength);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,[MarshalAs(UnmanagedType.Bool)]bool disableAll,ref TokenPrivileges newState,uint bufferLength,IntPtr previousState,IntPtr returnLength);
}
