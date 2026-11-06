using System.ComponentModel;
using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;

namespace DeadWare.App.Tests;

/// <summary>How a kept process behaves once End task's Yes is answered.</summary>
internal enum FakeEnding
{
    /// <summary>Terminate succeeds and the handle signals.</summary>
    Ends,
    /// <summary>Terminate succeeds but the handle never signals.</summary>
    StaysRunning,
    /// <summary>Terminate fails because the process exited meanwhile, so the handle signals.</summary>
    FailsAndExits,
    /// <summary>The process exited after it was classified and before the box was answered: its handle is already signalled.</summary>
    ExitedBeforeBox,
}

/// <summary>Scripted processes for End task and Open file location, shared by the action tests and the Processes page checks.
/// Every PID opens for termination and reads as endable (creation ticks 1, an image outside the Windows folder, a user SID) and signals once terminated, unless its script says otherwise.
/// Records the opens, terminations, waits and handle closes; handles are fake, never-owned <see cref="SafeProcessHandle"/>s, and a handle counts as closed once disposed.</summary>
internal sealed class FakeProcessControl : IProcessControl
{
    public const string Windows=@"C:\Windows";
    public const string AppPath=@"C:\Program Files\App\app.exe";
    public const string UserSid="S-1-5-21-1004336348-1177238915-682003330-1001";

    /// <summary>One PID's script. The defaults read as an endable process of the signed-in user.</summary>
    internal sealed class Script
    {
        /// <summary>The Win32 error the terminate open fails with, mapped by <see cref="NativeProcessControl.OpenFailed"/>; null opens it.</summary>
        public int? OpenError;
        /// <summary>Whether the facts-only open after a denied terminate open succeeds.</summary>
        public bool FactsOpened=true;
        public long? CreationTicks=1;
        public bool? Critical=false,Protected=false;
        public string? ImagePath=AppPath;
        public string? TokenUserSid=UserSid;
        public FakeEnding Ending=FakeEnding.Ends;
        /// <summary>The Win32 error <see cref="FakeProcessControl.CreationTicks"/> throws: the process runs but its creation time cannot be read. End task's facts read is unaffected.</summary>
        public int? CreationTicksError;
        /// <summary>The creation ticks the PID reads once its image path has been read: the PID was reused meanwhile.</summary>
        public long? CreationTicksAfterPathRead;
    }
    private sealed class Handle(int pid,SafeProcessHandle value,bool signalled)
    {
        public int Pid=>pid;
        public SafeProcessHandle Value=>value;
        public bool Signalled=signalled;
    }

    private readonly object gate=new();
    private readonly Dictionary<int,Script> scripts=[];
    private readonly List<(int Pid,bool Debug)> opened=[];
    private readonly List<Handle> handles=[];
    private readonly List<int> terminated=[];
    private int waits;
    private TimeSpan? waitTimeout;

    public string WindowsFolder=>Windows;
    /// <summary>Thrown from <see cref="ReadFacts"/> when set.</summary>
    public Exception? ThrowOnFacts {get;set;}
    /// <summary>Thrown from <see cref="Attributes"/> when set; otherwise the real attributes are read, so a temp file is there and a path in a missing folder is gone.</summary>
    public Exception? ThrowOnAttributes {get;set;}
    public FileAttributes Attributes(string path)=>ThrowOnAttributes is {} e?throw e:File.GetAttributes(path);
    /// <summary>The script of <paramref name="pid"/>, created with the defaults on first use.</summary>
    public Script this[int pid] {get {lock(gate)return scripts.TryGetValue(pid,out var script)?script:scripts[pid]=new();}}
    public void OpenError(int pid,int error)=>this[pid].OpenError=error;

    public IReadOnlyList<(int Pid,bool Debug)> Opened {get {lock(gate)return [..opened];}}
    /// <summary>PIDs whose handle has been closed, in the order the handles were opened.</summary>
    public IReadOnlyList<int> Closed {get {lock(gate)return [..handles.Where(h=>h.Value.IsClosed).Select(h=>h.Pid)];}}
    /// <summary>PIDs whose handle is still open.</summary>
    public IReadOnlyList<int> StillOpen {get {lock(gate)return [..handles.Where(h=>!h.Value.IsClosed).Select(h=>h.Pid)];}}
    public IReadOnlyList<int> Terminated {get {lock(gate)return [..terminated];}}
    public int Waits {get {lock(gate)return waits;}}
    public TimeSpan? WaitTimeout {get {lock(gate)return waitTimeout;}}

    public ProcessOpen OpenForTerminate(int pid,bool enableDebugPrivilege)
    {
        var script=this[pid];
        lock(gate)
        {
            opened.Add((pid,enableDebugPrivilege));
            if(script.OpenError is int error)return NativeProcessControl.OpenFailed(error);
            var handle=new Handle(pid,new SafeProcessHandle(1000+handles.Count,ownsHandle:false),script.Ending==FakeEnding.ExitedBeforeBox);
            handles.Add(handle);
            return new(handle.Value,false,false);
        }
    }
    /// <summary>Reads like <see cref="NativeProcessControl.ReadFacts"/>: an exited open reads nothing, an open handle reads every fact, and a denied open reads them only when the facts-only open succeeds.</summary>
    public EndTaskFacts ReadFacts(int pid,ProcessOpen open)
    {
        if(ThrowOnFacts is {} failure)throw failure;
        if(open.Exited)return new(true,false,false,null,null,null,null,null);
        var script=this[pid];
        bool terminateOpened=open.Handle is not null;
        return terminateOpened||script.FactsOpened
            ?new(false,terminateOpened,true,script.CreationTicks,script.Critical,script.Protected,script.ImagePath,script.TokenUserSid)
            :new(false,false,false,null,null,null,null,null);
    }
    public bool Terminate(SafeProcessHandle handle)
    {
        var kept=Find(handle);
        lock(gate)
        {
            terminated.Add(kept.Pid);
            var ending=this[kept.Pid].Ending;
            if(ending is FakeEnding.Ends or FakeEnding.FailsAndExits)kept.Signalled=true;
            return ending!=FakeEnding.FailsAndExits;
        }
    }
    public bool IsSignalled(SafeProcessHandle handle){var kept=Find(handle);lock(gate)return kept.Signalled;}
    public void WaitAll(IReadOnlyList<SafeProcessHandle> handles,TimeSpan timeout)
    {
        foreach(var handle in handles)Find(handle);
        lock(gate){waits++;waitTimeout=timeout;}
    }
    public long? CreationTicks(int pid)
    {
        var script=this[pid];
        lock(gate)return script.CreationTicksError is int error?throw new Win32Exception(error):script.CreationTicks;
    }
    public string? ImagePath(int pid)
    {
        var script=this[pid];
        lock(gate)
        {
            if(script.CreationTicksAfterPathRead is long reused)script.CreationTicks=reused;
            return script.ImagePath;
        }
    }

    private Handle Find(SafeProcessHandle handle)
    {
        lock(gate)
        {
            var kept=handles.Single(h=>ReferenceEquals(h.Value,handle));
            ObjectDisposedException.ThrowIf(kept.Value.IsClosed,kept.Value);
            return kept;
        }
    }
}
