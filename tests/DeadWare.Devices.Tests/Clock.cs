using Microsoft.Extensions.Time.Testing;

namespace DeadWare.Devices.Tests;

// Fake time that knows which timers are waiting and how long each was armed for. The host and engine arm their timers on thread-pool
// continuations, so a test that moves the clock before a timer exists skips it; with this clock a test moves it only once the host
// waits on the timer the test means to fire (DeviceHostTests.Elapse and AdvanceUntil).
sealed class Clock:FakeTimeProvider
{
    // A context of its own type: under one, the runtime queues await continuations to the thread pool instead of running them inline.
    sealed class NoInlining:SynchronizationContext{}
    static readonly NoInlining noInlining=new();

    // Moves the clock. The timers it fires complete their tasks on this thread, and the runtime would run the host's await continuations
    // right here, inside the move, wherever the caller has no synchronization context (as an xUnit test does after its first await).
    // Moving it under a context of its own sends them to the thread pool, as real timers do, so they never run on (or park) the test's thread.
    public new void Advance(TimeSpan delta)
    {
        var previous=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(noInlining);
        try{base.Advance(delta);}
        finally{SynchronizationContext.SetSynchronizationContext(previous);}
    }

    sealed class Waiter:ITimer
    {
        readonly Clock clock;
        readonly object sync=new();
        TimeSpan delay;
        DateTimeOffset? due;
        bool dead;
        public ITimer? Inner;
        public Waiter(Clock clock,TimeSpan delay){this.clock=clock;this.delay=delay;}
        public bool Dead{get{lock(sync)return dead;}}
        // Armed for exactly d, not fired, and due at (or, if armed while the clock moved, no later than) at.
        public bool Waits(TimeSpan d,DateTimeOffset at){lock(sync)return !dead&&delay==d&&(due is null||due==at);}
        public void Arm(DateTimeOffset? at){lock(sync)due=at;}
        public void Fired(TimeSpan period){lock(sync){if(period==Timeout.InfiniteTimeSpan)dead=true;else{delay=period;due+=period;}}}
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {
            var before=clock.GetUtcNow();
            var changed=Inner!.Change(dueTime,period);
            lock(sync){dead=false;delay=dueTime;due=clock.Due(before,dueTime);}
            return changed;
        }
        public void Dispose(){lock(sync)dead=true;Inner?.Dispose();}
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
    readonly List<Waiter> waiters=new();

    // The due time of a timer armed for dueTime when the clock read before; unknown (null) if the clock has moved since, or never due.
    DateTimeOffset? Due(DateTimeOffset before,TimeSpan dueTime)=>dueTime==Timeout.InfiniteTimeSpan||before!=GetUtcNow()?null:before+dueTime;

    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {
        var waiter=new Waiter(this,dueTime);
        var before=GetUtcNow();
        waiter.Inner=base.CreateTimer(s=>{waiter.Fired(period);callback(s);},state,dueTime,period);
        waiter.Arm(Due(before,dueTime));
        lock(waiters)waiters.Add(waiter);
        return waiter;
    }

    // True while a timer armed for exactly delay has not fired and is due delay from now: something started waiting that long since the
    // clock last moved. A timer armed while the clock was moving also counts, since it is due no later than that.
    public bool Waiting(TimeSpan delay)
    {
        lock(waiters)
        {
            var at=GetUtcNow()+delay;
            waiters.RemoveAll(w=>w.Dead);
            return waiters.Any(w=>w.Waits(delay,at));
        }
    }
}
