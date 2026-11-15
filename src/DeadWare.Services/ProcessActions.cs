using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.App;

/// <summary>The outcome of End task's terminate open: a handle, or why there is none.</summary>
internal sealed record ProcessOpen(SafeProcessHandle? Handle,bool Denied,bool Exited);

/// <summary>The native calls behind End task and Open file location. Paths are read on demand and never kept.</summary>
internal interface IProcessControl
{
    /// <summary>The folder <c>GetSystemWindowsDirectoryW</c> returns, read once; "" when it could not be read.</summary>
    string WindowsFolder {get;}
    ProcessOpen OpenForTerminate(int pid,bool enableDebugPrivilege);
    /// <summary>The facts on <paramref name="open"/>'s handle, or after a denied open on a facts-only handle. A fact that cannot be read is null; TerminateOpened is true exactly when <paramref name="open"/> has a handle.</summary>
    EndTaskFacts ReadFacts(int pid,ProcessOpen open);
    bool Terminate(SafeProcessHandle handle);
    bool IsSignalled(SafeProcessHandle handle);
    /// <summary>Waits up to <paramref name="timeout"/> in total for every handle to signal.</summary>
    void WaitAll(IReadOnlyList<SafeProcessHandle> handles,TimeSpan timeout);
    /// <summary>The running process's Generation; null only when the process is gone (the PID no longer exists, or the process has exited).
    /// Throws <see cref="System.ComponentModel.Win32Exception"/> when a running process's creation time cannot be read, so it is never mistaken for an exited one.</summary>
    long? CreationTicks(int pid);
    string? ImagePath(int pid);
    /// <summary>The attributes of the file at <paramref name="path"/>, read and thrown as <see cref="File.GetAttributes(string)"/> reads and throws them.
    /// <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> means the file is gone; <see cref="UnauthorizedAccessException"/> means Windows denies access. <c>File.Exists</c> reads both as false.</summary>
    FileAttributes Attributes(string path);
}

/// <summary>End task's and Open file location's Windows calls (spec, Actions). Only the terminate open may enable the debug privilege, and only on its own short-lived thread.</summary>
internal sealed class NativeProcessControl : IProcessControl
{
    private const uint ProcessTerminate=0x0001,ProcessQueryLimitedInformation=0x1000,Synchronize=0x100000;
    private const int ErrorInvalidParameter=87,SecurityImpersonation=2,ProcessProtectionLevelInfo=7,TokenUser=1;
    private const uint ProtectionLevelNone=0xFFFFFFFE,TokenAdjustPrivileges=0x0020,TokenQuery=0x0008,SePrivilegeEnabled=0x2,WaitObject0=0;
    private string? windowsFolder;

    public string WindowsFolder=>windowsFolder??=ReadWindowsFolder();

    public ProcessOpen OpenForTerminate(int pid,bool enableDebugPrivilege)
    {
        if(!enableDebugPrivilege)return Open(pid);
        ProcessOpen? opened=null;Exception? failure=null;
        var thread=new Thread(()=>{try{opened=OpenWithDebugPrivilege(pid);}catch(Exception e){failure=e;}}){IsBackground=true,Name="DeadWare End task open"};
        thread.Start();thread.Join();
        if(failure is not null)ExceptionDispatchInfo.Throw(failure);
        return opened!;
    }
    /// <summary>A failed terminate open: a PID that no longer exists has exited; every other error is a denial, so only the facts-only read can show a refusal and nothing is ended.</summary>
    internal static ProcessOpen OpenFailed(int win32Error)=>win32Error==ErrorInvalidParameter?new(null,false,true):new(null,true,false);
    private static ProcessOpen Open(int pid)
    {
        var handle=OpenProcess(ProcessTerminate|ProcessQueryLimitedInformation|Synchronize,false,pid);
        if(!handle.IsInvalid)return new(handle,false,false);
        int error=Marshal.GetLastPInvokeError();
        handle.Dispose();
        return OpenFailed(error);
    }
    /// <summary>Runs on a thread of its own: SeDebugPrivilege is enabled on that thread's impersonation token for this one open, and the thread reverts before it ends.
    /// When the privilege cannot be enabled the open goes ahead without it; Windows then decides, and a denied open is reported, never ended.</summary>
    private static ProcessOpen OpenWithDebugPrivilege(int pid)
    {
        try
        {
            if(ImpersonateSelf(SecurityImpersonation))EnableDebugPrivilege();
            return Open(pid);
        }
        finally{RevertToSelf();}
    }
    private static void EnableDebugPrivilege()
    {
        if(!OpenThreadToken(GetCurrentThread(),TokenAdjustPrivileges|TokenQuery,false,out var token))return;
        using(token)
        {
            if(!LookupPrivilegeValue(null,"SeDebugPrivilege",out var luid))return;
            var privileges=new TokenPrivileges{Count=1,Luid=luid,Attributes=SePrivilegeEnabled};
            AdjustTokenPrivileges(token,false,ref privileges,0,IntPtr.Zero,IntPtr.Zero);
        }
    }

    public EndTaskFacts ReadFacts(int pid,ProcessOpen open)
    {
        if(open.Exited)return Exited;
        if(open.Handle is {} handle)return Read(handle,terminateOpened:true);
        // The terminate open was denied: reopen for the facts alone, without the privilege, so a refused process shows its reason.
        using var query=OpenProcess(ProcessQueryLimitedInformation,false,pid);
        if(!query.IsInvalid)return Read(query,terminateOpened:false);
        return Marshal.GetLastPInvokeError()==ErrorInvalidParameter?Exited:new(false,false,false,null,null,null,null,null);
    }
    private static readonly EndTaskFacts Exited=new(true,false,false,null,null,null,null,null);
    private static EndTaskFacts Read(SafeProcessHandle process,bool terminateOpened)
    {
        var times=NativeProcessIdentity.Times(process);
        bool? critical=IsProcessCritical(process,out bool isCritical)?isCritical:null;
        bool? isProtected=GetProcessInformation(process,ProcessProtectionLevelInfo,out uint level,sizeof(uint))?level!=ProtectionLevelNone:null;
        return new(times?.Exited==true,terminateOpened,true,times?.Generation,critical,isProtected,NativeProcessIdentity.ImagePath(process),TokenUserSid(process));
    }
    private static string? TokenUserSid(SafeProcessHandle process)
    {
        if(!OpenProcessToken(process,TokenQuery,out var token))return null;
        using(token)
        {
            GetTokenInformation(token,TokenUser,IntPtr.Zero,0,out int needed);
            if(needed<=0)return null;
            IntPtr user=Marshal.AllocHGlobal(needed);
            try{return GetTokenInformation(token,TokenUser,user,needed,out _)?new SecurityIdentifier(Marshal.ReadIntPtr(user)).Value:null;}
            finally{Marshal.FreeHGlobal(user);}
        }
    }

    public bool Terminate(SafeProcessHandle handle)=>TerminateProcess(handle,1);
    public bool IsSignalled(SafeProcessHandle handle)=>WaitForSingleObject(handle,0)==WaitObject0;
    public void WaitAll(IReadOnlyList<SafeProcessHandle> handles,TimeSpan timeout)
    {
        // One deadline for every handle, so the whole wait lasts at most the timeout, however many processes there are.
        long deadline=Environment.TickCount64+(long)timeout.TotalMilliseconds;
        foreach(var handle in handles)WaitForSingleObject(handle,(uint)Math.Max(0,deadline-Environment.TickCount64));
    }
    public long? CreationTicks(int pid)
    {
        using var process=OpenProcess(ProcessQueryLimitedInformation,false,pid);
        if(process.IsInvalid)
        {
            int error=Marshal.GetLastPInvokeError();
            return error==ErrorInvalidParameter?null:throw new Win32Exception(error);
        }
        if(NativeProcessIdentity.Times(process) is not {} times)throw new Win32Exception(Marshal.GetLastPInvokeError());
        return times.Exited?null:times.Generation;
    }
    public string? ImagePath(int pid)
    {
        using var process=OpenProcess(ProcessQueryLimitedInformation,false,pid);
        return process.IsInvalid?null:NativeProcessIdentity.ImagePath(process);
    }
    /// <summary>Kept on <see cref="File.GetAttributes(string)"/> for .NET's long-path handling, which a raw <c>GetFileAttributesW</c> would lose.</summary>
    public FileAttributes Attributes(string path)=>File.GetAttributes(path);
    private static string ReadWindowsFolder()
    {
        var folder=new StringBuilder(260);
        uint length=GetSystemWindowsDirectory(folder,(uint)folder.Capacity);
        // Too small a buffer returns the size needed, terminating null included.
        if(length>=folder.Capacity){folder=new StringBuilder((int)length+1);length=GetSystemWindowsDirectory(folder,(uint)folder.Capacity);}
        return length==0||length>=folder.Capacity?"":folder.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid {public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges {public uint Count;public Luid Luid;public uint Attributes;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessCritical(SafeProcessHandle process,[MarshalAs(UnmanagedType.Bool)]out bool critical);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessInformation(SafeProcessHandle process,int informationClass,out uint information,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="GetSystemWindowsDirectoryW")] private static extern uint GetSystemWindowsDirectory(StringBuilder buffer,uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ImpersonateSelf(int level);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RevertToSelf();
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenThreadToken(IntPtr thread,uint access,[MarshalAs(UnmanagedType.Bool)]bool openAsSelf,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="LookupPrivilegeValueW")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,[MarshalAs(UnmanagedType.Bool)]bool disableAll,ref TokenPrivileges newState,uint bufferLength,IntPtr previousState,IntPtr returnLength);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,IntPtr information,int length,out int returnLength);
}

/// <summary>End task and Open file location (spec, Actions). Every call runs off the UI thread and returns status text; nothing is thrown.
/// The text holds fixed copy and counts; only the confirm question names the app, and paths stay in locals.</summary>
internal sealed class ProcessActions(IProcessControl control,Func<string,string?,bool> confirm,Func<string,bool> reveal,int ownPid,bool elevated)
{
    internal static readonly TimeSpan EndWait=TimeSpan.FromSeconds(2);
    private const string ProcessExited="The process has already exited.",CouldNotOpen="Could not open the file location.",NoLongerThere="The program file is no longer there.";
    /// <summary>How long Open file location waits for the shell before it reports failure, so a hung Explorer never leaves the action pending.</summary>
    internal TimeSpan RevealTimeout {get;init;}=TimeSpan.FromSeconds(10);

    public Task<string> EndAsync(string appName,IReadOnlyList<(int Pid,long Generation)> members,bool wholeApp)=>Task.Run(()=>
    {
        try{return End(appName,members,wholeApp);}
        catch(Win32Exception w){return $"End task failed (Windows error {w.NativeErrorCode})";}
        catch(Exception e){return $"End task failed ({e.GetType().Name})";}
    });

    private string End(string appName,IReadOnlyList<(int Pid,long Generation)> members,bool wholeApp)
    {
        var verdicts=new EndTaskVerdict?[members.Count];
        for(int i=0;i<members.Count;i++)verdicts[i]=EndTaskPolicy.Precheck(members[i].Pid,ownPid);
        // Handles of members that can be ended stay open until termination, so a reused PID is never hit; every other handle closes at once.
        var kept=new List<SafeProcessHandle>();
        try
        {
            for(int i=0;i<members.Count;i++)
            {
                if(verdicts[i] is not null)continue;
                var (pid,generation)=members[i];
                var open=control.OpenForTerminate(pid,elevated);
                bool keep=false;
                try
                {
                    verdicts[i]=EndTaskPolicy.Classify(control.ReadFacts(pid,open),generation,control.WindowsFolder,elevated);
                    // CanEnd needs TerminateOpened, which only an open with a handle reads.
                    keep=verdicts[i]==EndTaskVerdict.CanEnd&&open.Handle is not null;
                    if(keep)kept.Add(open.Handle!);
                }
                finally{if(!keep)open.Handle?.Dispose();}
            }
            var all=verdicts.Select(v=>v!.Value).ToList();
            if(kept.Count==0)return EndTaskPolicy.Refusal(all);
            int running=all.Count(v=>v!=EndTaskVerdict.AlreadyExited);
            if(!confirm(EndTaskPolicy.Question(appName,members[0].Pid,kept.Count,running,wholeApp),EndTaskPolicy.NotEnded(all)))return "";
            return EndKept(kept,notEnded:all.Count(v=>v is not (EndTaskVerdict.CanEnd or EndTaskVerdict.AlreadyExited)),alreadyExited:all.Count(v=>v==EndTaskVerdict.AlreadyExited));
        }
        finally{foreach(var handle in kept)handle.Dispose();}
    }
    /// <summary>After Yes: terminates every kept process still running, waits once for all of them, and counts each member once.</summary>
    private string EndKept(List<SafeProcessHandle> kept,int notEnded,int alreadyExited)
    {
        int ended=0,stillRunning=0;
        var ending=new List<(SafeProcessHandle Handle,bool Terminated)>();
        foreach(var handle in kept)
        {
            // A handle that already signalled belongs to a process that exited before the answer.
            if(control.IsSignalled(handle))alreadyExited++;
            else ending.Add((handle,control.Terminate(handle)));
        }
        if(ending.Count>0)control.WaitAll([..ending.Select(e=>e.Handle)],EndWait);
        foreach(var (handle,terminated) in ending)
        {
            if(!control.IsSignalled(handle))stillRunning++;
            else if(terminated)ended++;
            else alreadyExited++;
        }
        return EndTaskPolicy.Result(ended,notEnded,alreadyExited,stillRunning);
    }

    public Task<string> OpenLocationAsync(IReadOnlyList<(int Pid,long Generation)> members)=>Task.Run(()=>
    {
        try{return OpenLocation(members);}
        catch(Win32Exception w){return Failed(w);}
        catch(Exception e){return $"Could not open the file location ({e.GetType().Name}).";}
    });
    private static string Failed(Win32Exception w)=>$"Could not open the file location (Windows error {w.NativeErrorCode}).";

    private string OpenLocation(IReadOnlyList<(int Pid,long Generation)> members)
    {
        // A creation time that cannot be read is not an exit: when no member can be confirmed, that failure is reported instead.
        Win32Exception? unreadable=null;
        bool Running((int Pid,long Generation) member)
        {
            try{return control.CreationTicks(member.Pid)==member.Generation;}
            catch(Win32Exception w){unreadable??=w;return false;}
        }
        var running=members.Where(Running).ToList();
        int unreadPaths=0;
        var located=new List<string>();
        foreach(var member in running)
        {
            if(control.ImagePath(member.Pid) is not {} path){unreadPaths++;continue;}
            // Checked again after the read, so a PID reused meanwhile never lends its path.
            if(Running(member))located.Add(path);
        }
        var paths=located.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if(paths.Count>1)return $"runs from {paths.Count} locations; expand to choose one";
        if(paths.Count==0)return unreadable is not null?Failed(unreadable):unreadPaths>0?CouldNotOpen:ProcessExited;
        string only=paths[0];
        // File.Exists reads a denied file as missing, so the attributes are read instead; a folder at the path is not a program file.
        try{if((control.Attributes(only)&FileAttributes.Directory)!=0)return NoLongerThere;}
        catch(Exception e) when(e is FileNotFoundException or DirectoryNotFoundException){return NoLongerThere;}
        catch(UnauthorizedAccessException){return "Could not open the file location (denied by Windows).";}
        return RevealOnSta(only)?"":CouldNotOpen;
    }
    /// <summary>Runs <c>reveal</c> on a short-lived STA thread, as the shell calls require, and rethrows what it threw; false when it has not returned within <see cref="RevealTimeout"/>.</summary>
    private bool RevealOnSta(string path)
    {
        bool revealed=false;Exception? failure=null;
        var thread=new Thread(()=>{try{revealed=reveal(path);}catch(Exception e){failure=e;}}){IsBackground=true,Name="DeadWare Open file location"};
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if(!thread.Join(RevealTimeout))return false;
        if(failure is not null)ExceptionDispatchInfo.Throw(failure);
        return revealed;
    }

    /// <summary>Opens the folder holding <paramref name="path"/> with the file selected; call on an STA thread. Falls back to <c>explorer.exe /select</c>, and returns false when both fail.</summary>
    internal static bool Reveal(string path)
    {
        if(SHParseDisplayName(path,IntPtr.Zero,out IntPtr item,0,out _)==0&&item!=IntPtr.Zero)
        {
            try{if(SHOpenFolderAndSelectItems(item,0,IntPtr.Zero,0)==0)return true;}
            finally{Marshal.FreeCoTaskMem(item);}
        }
        // The full path, so no explorer.exe in a searched folder can stand in for Windows' own.
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if(windows.Length==0)return false;
        try
        {
            using var explorer=Process.Start(new ProcessStartInfo(Path.Combine(windows,"explorer.exe"),$"/select,\"{path}\""){UseShellExecute=false});
            return explorer is not null;
        }
        catch(Win32Exception){return false;}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name,IntPtr bindContext,out IntPtr item,uint attributesIn,out uint attributesOut);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr folder,uint count,IntPtr items,uint flags);
}
