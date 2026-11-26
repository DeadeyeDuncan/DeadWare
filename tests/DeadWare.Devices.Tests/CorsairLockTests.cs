using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5: the lock other Corsair software shares. Every test makes its lock under a fresh Local\DeadWareTest-<guid> name; none makes or
// opens the real one. Enter and Exit are called on the thread a test means, never across an await.
public sealed class CorsairLockTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-corsairlock-"+Guid.NewGuid().ToString("N"));
    readonly string name=@"Local\DeadWareTest-"+Guid.NewGuid().ToString("N");
    readonly HostLog log;

    public CorsairLockTests(){log=new HostLog(Path.Combine(dir,"logs"));}

    public void Dispose()
    {
        log.Dispose();
        try{Directory.Delete(dir,true);}catch{}
    }

    int LogCount(string text){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllText(p).Split(text).Length-1:0;}

    // Runs f on a thread of its own and waits for that thread to end; what f throws is thrown here (on a bare thread it would end the test run).
    static T OnAnotherThread<T>(Func<T> f)
    {
        T result=default!;
        ExceptionDispatchInfo? thrown=null;
        var t=new Thread(()=>{try{result=f();}catch(Exception e){thrown=ExceptionDispatchInfo.Capture(e);}});
        t.Start();t.Join();
        thrown?.Throw();
        return result;
    }

    // Another thread enters and lets go at once: true when the lock was free.
    static bool FreeForAnother(CorsairLock l)=>OnAnotherThread(()=>{var ok=l.Enter(0);if(ok) l.Exit();return ok;});

    [Fact]
    public void Enter_and_exit_on_one_thread()
    {
        using var l=new CorsairLock(log,name);
        Assert.True(l.Enter(0));
        Assert.Null(l.Problem);
        l.Exit();
        Assert.True(FreeForAnother(l));
        Assert.True(l.Enter(0));
        l.Exit();
    }

    [Fact]
    public void A_second_thread_is_refused_while_it_is_held()
    {
        using var l=new CorsairLock(log,name);
        Assert.True(l.Enter(0));
        Assert.False(OnAnotherThread(()=>l.Enter(0)));
        l.Exit();
        Assert.True(FreeForAnother(l));
    }

    [Fact]
    public void A_lock_left_by_a_thread_that_ended_counts_as_entered()
    {
        using var l=new CorsairLock(log,name);
        Assert.True(OnAnotherThread(()=>l.Enter(0)));      // that thread ends still holding it
        Assert.True(l.Enter(0));
        Assert.Equal(1,LogCount("Corsair lock was left held by a program that ended"));
        Assert.False(OnAnotherThread(()=>l.Enter(0)));     // the thread that caught it owns it now
        l.Exit();
        Assert.True(FreeForAnother(l));
        Assert.Equal(1,LogCount("Corsair lock was left held by a program that ended"));
    }

    [Fact]
    public void A_name_that_cannot_be_made_reports_a_problem()
    {
        using var l=new CorsairLock(log,@"Global\bad\name");
        Assert.False(l.Enter(0));
        Assert.NotNull(l.Problem);
        Assert.False(l.Enter(250));
        // Asked before any Enter, Problem makes the lock first: the worker can tell it cannot be made before it opens the hub.
        using var m=new CorsairLock(log,@"Global\bad\name");
        Assert.NotNull(m.Problem);
        Assert.False(m.Enter(0));
    }

    // Fix round 1, M1: the lock never throws. With no mutex (it could not be made) or after Dispose, Exit does nothing; here the lock is disposed
    // while held, on a thread of its own that then ends.
    [Fact]
    public void Exit_with_no_mutex_or_after_Dispose_does_nothing()
    {
        using var bad=new CorsairLock(log,@"Global\bad\name");
        Assert.False(bad.Enter(0));
        bad.Exit();
        var l=new CorsairLock(log,name);
        Assert.True(OnAnotherThread(()=>{var ok=l.Enter(0);l.Dispose();l.Exit();return ok;}));
        Assert.NotNull(l.Problem);
        using var again=new CorsairLock(log,name);
        Assert.True(again.Enter(0));                                         // free again once that thread ended
        again.Exit();
    }

    [Fact]
    public void Enter_after_Dispose_is_refused_and_says_why()
    {
        var l=new CorsairLock(log,name);
        Assert.True(l.Enter(0));
        l.Exit();
        l.Dispose();
        Assert.False(l.Enter(0));
        Assert.NotNull(l.Problem);
        l.Exit();
    }

    // The rule other users' programs need: Everyone (S-1-1-0) may Synchronize and Modify (0x100001), and that is what the lock grants when it makes the mutex.
    [Fact]
    public void The_lock_it_makes_lets_every_user_take_it()
    {
        using var l=new CorsairLock(log,name);
        Assert.True(l.Enter(0));
        l.Exit();
        using var made=MutexAcl.OpenExisting(name,MutexRights.ReadPermissions);
        var rules=made.GetAccessControl().GetAccessRules(true,false,typeof(SecurityIdentifier)).Cast<MutexAccessRule>().ToList();
        Assert.Contains(rules,r=>r.IdentityReference.Value=="S-1-1-0"&&r.AccessControlType==AccessControlType.Allow&&(int)r.MutexRights==0x100001);
    }

    // Another program made the mutex granting every user only Synchronize and Modify, so making it with full rights is refused; the lock opens it instead.
    [Fact]
    public void A_lock_another_program_made_is_opened()
    {
        var security=new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier("S-1-1-0"),(MutexRights)0x100001,AccessControlType.Allow));
        using var other=MutexAcl.Create(false,name,out var created,security);
        Assert.True(created);
        using var l=new CorsairLock(log,name);
        Assert.True(l.Enter(0));
        Assert.Null(l.Problem);
        Assert.False(OnAnotherThread(()=>other.WaitOne(0)));   // the same mutex
        l.Exit();
        Assert.True(OnAnotherThread(()=>{var ok=other.WaitOne(0);if(ok) other.ReleaseMutex();return ok;}));
    }
}
