using System.Security.AccessControl;
using System.Security.Principal;

namespace DeadWare.Devices;

// Lighting 5: the named mutex Corsair software and OpenRGB take around each of their own sequences on the hub. It is made on first use (an Enter,
// or a look at Problem, so the worker can tell it cannot be made before it opens the hub) with one allow rule for Everyone (Synchronize and
// Modify), so other users' programs can take it too; when another program made it without granting full rights, it is opened instead. The
// .NET 10 overloads that take wait-handle options are never used: their default limits the lock to the current user. Making it is tried once: if
// neither way works, Enter is false for the run, Problem says why and nothing throws. The mutex is recursive per thread and owned by the thread
// that entered it, so Enter and Exit belong on one thread.
internal sealed class CorsairLock:IHubLock,IDisposable
{
    public const string Name=@"Global\CorsairLinkReadWriteGuardMutex";
    readonly HostLog log;
    readonly string name;
    readonly object gate=new();
    Mutex? mutex;
    string? problem;
    bool tried,disposed;

    public CorsairLock(HostLog log,string name=Name){this.log=log;this.name=name;}

    public string? Problem{get{Made();lock(gate)return problem;}}

    // WaitOne within the limit. A mutex whose holder ended without letting go counts as entered: the thread that caught it owns it now. A lock
    // disposed before or under an Enter (the helper ending while the worker is stuck) refuses it and says why; it never throws.
    public bool Enter(int limitMs)
    {
        var m=Made();
        if(m is null) return false;
        try{return m.WaitOne(limitMs);}
        catch(AbandonedMutexException){log.Write("Corsair lock was left held by a program that ended");return true;}
        catch(ObjectDisposedException){lock(gate)problem??=ClosedText;return false;}
    }

    // Nothing to let go with no mutex (it could not be made) or once disposed.
    public void Exit()
    {
        Mutex? m;
        lock(gate) m=mutex;
        if(m is null) return;
        try{m.ReleaseMutex();}
        catch(ObjectDisposedException){}
    }

    // Closes this handle; the disposed mutex is kept only so a later Enter or Exit sees it disposed. Disposing while the worker holds the lock
    // does not let it go: Windows keeps it owned by the worker's thread until that thread or the process ends, and then treats it as abandoned.
    public void Dispose(){lock(gate){disposed=true;problem??=ClosedText;mutex?.Dispose();}}

    const string ClosedText="the Corsair lock was closed";

    Mutex? Made()
    {
        lock(gate)
        {
            if(disposed||tried) return mutex;
            tried=true;
            try{mutex=Make(name);}
            catch(Exception e){problem=e.Message;}
            return mutex;
        }
    }

    static Mutex Make(string name)
    {
        var security=new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),MutexRights.Synchronize|MutexRights.Modify,AccessControlType.Allow));
        try{return MutexAcl.Create(false,name,out _,security);}
        catch(UnauthorizedAccessException){return Mutex.OpenExisting(name);}
    }
}
