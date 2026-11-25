using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4, everyday: what the signals told the host, and the lights-off rule's evaluation on the host loop.
// Press, SetLocked, SetSuspended and SetResumed are called on the watcher thread and are lock-free: they only count or set. Everything
// else runs under the host's gate, on the loop or in a request. The reason is worked out from the last loop turn's readings (Observe), so
// it changes only on a loop turn, where its edges are logged and a state is raised. Quiet is the one reading taken at each use: whatever
// paints or connects asks it, so nothing is written from the suspend signal on, even before the loop's next turn.
internal sealed class EverydayState
{
    // A press this soon after an idle spell ended only brings the lights back (ruling P9): holding Ctrl or Alt ends the spell before the
    // shortcut's letter arrives, and a loop turn may fall in between.
    public static readonly TimeSpan IdleGrace=TimeSpan.FromSeconds(2);
    readonly HostLog log;
    readonly TimeProvider time;
    int presses,suspends,resumes;
    volatile bool locked;
    // Task 5, sleep and wake: power is Awake, Asleep, or the timestamp of the first resume signal since the last suspend or wake. The
    // watcher sets it lock-free; the wake's end is taken on the loop by compare-and-swap, so a suspend that arrives meanwhile is never lost.
    // A resume with no suspend before it (Modern Standby can do this) starts the wait the same way; a second one restarts nothing.
    const long Awake=long.MinValue,Asleep=long.MinValue+1;
    long power=Awake;
    // Under the host's gate.
    long idleMs;
    bool idleSpell,lockSeen,lastOn,primed,waking;
    long? idleEndedAt;
    string? lastReason;
    public EverydayState(HostLog log,TimeProvider time){this.log=log;this.time=time;}

    public void Press()=>Interlocked.Increment(ref presses);
    public void SetLocked(bool locked)=>this.locked=locked;
    public void SetSuspended(){Interlocked.Exchange(ref power,Asleep);Interlocked.Increment(ref suspends);}
    public void SetResumed()
    {
        var now=time.GetTimestamp();
        for(var p=Volatile.Read(ref power);p is Awake or Asleep;p=Volatile.Read(ref power))
            if(Interlocked.CompareExchange(ref power,now,p)==p) break;
        Interlocked.Increment(ref resumes);
    }
    public int TakePresses()=>Interlocked.Exchange(ref presses,0);
    public bool Locked=>locked;
    // Suspended, or waking and the wait not yet over: nothing paints or connects.
    public bool Quiet=>Volatile.Read(ref power)!=Awake;
    // The suspend and resume signals since the last call.
    public (int Suspends,int Resumes) TakePower()=>(Interlocked.Exchange(ref suspends,0),Interlocked.Exchange(ref resumes,0));
    // True once: on the first call at least delay after the first resume signal, which ends the quiet.
    public bool WakeDue(TimeSpan delay)
    {
        var p=Volatile.Read(ref power);
        if(p is Awake or Asleep||time.GetElapsedTime(p)<delay) return false;
        return Interlocked.CompareExchange(ref power,Awake,p)==p;
    }

    // Why the lights are off beyond the switch now, from the last readings.
    public string? Reason(LightingSettings s)=>LightsOff.Reason(s,idleMs,lockSeen,waking);

    // One loop turn's readings: idle time only while the idle rule is on (never read otherwise), the lock flag and the quiet. Spell and
    // lock edges are logged; the end of a spell is remembered for the press rule.
    public void Observe(LightingSettings s,IInputClock? input)
    {
        waking=Quiet;
        idleMs=input is not null&&s.Everyday.IdleOff?input.IdleMs():0;
        var idle=s.Everyday.IdleOff&&idleMs>=s.Everyday.IdleMinutes*60_000L;
        if(idle!=idleSpell)
        {
            idleSpell=idle;
            if(idle) log.Write($"idle spell started ({s.Everyday.IdleMinutes} min)");
            else{idleEndedAt=time.GetTimestamp();log.Write("idle spell ended");}
        }
        var l=locked;
        if(l!=lockSeen){lockSeen=l;log.Write(l?"locked":"unlocked");}
    }

    // The switch after one press, and whether the press only ended an idle spell. Such a press uses up the spell's end, so a second press
    // toggles.
    public bool PressTarget(LightingSettings s,out bool back)
    {
        var justEnded=Reason(s)==LightsOff.Idle||idleEndedAt is{} at&&time.GetElapsedTime(at)<IdleGrace;
        var target=LightsOff.ShortcutTarget(s.LightsOn,justEnded);
        back=s.LightsOn&&target;
        if(back){idleEndedAt=null;if(idleSpell){idleSpell=false;idleMs=0;log.Write("idle spell ended");}}
        return target;
    }

    // Whether the reason or the switch changed since the last call; the first call only takes the starting point.
    public bool Changed(LightingSettings s)
    {
        var reason=Reason(s);
        if(primed&&reason==lastReason&&s.LightsOn==lastOn) return false;
        var first=!primed;
        primed=true;lastReason=reason;lastOn=s.LightsOn;
        return !first;
    }
}
