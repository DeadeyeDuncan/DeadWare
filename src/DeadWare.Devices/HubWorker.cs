using System.Diagnostics;
namespace DeadWare.Devices;

// Lighting 5: the hub worker's shell. One background thread ("Hub") steps the core and waits for as long as the core asks, or until a call on the
// core nudges it, so every exchange with the hub and every enter and exit of the Corsair lock happen on that thread. Beside it a watchdog guards
// what the core cannot: a USB call that never returns while the shared lock is held (the hub also carries the pump and the radiator fans, and
// other Corsair software waits on that lock). 1.5 s into one locked unit it closes the hub's handle, which ends the call; 3 s later, if that unit
// is still under way, it ends the helper, so Windows frees the lock. The watchdog is its own timer on the real clock, never the host's loop or
// clock. Nothing here sends anything to the hub: the host hands it back before the shell stops.
internal enum HubWatchAct{None,Close,End}

// The watchdog's judgement, look by look, of how long the locked unit under way has been running; null: none is.
internal sealed class HubWatch
{
    TimeSpan? last;
    bool closed,ended;

    // Close at the first look at StuckAfter or more, End at the first look at StuckAfter+EndAfter or more, each once per unit. A null look, or a
    // time shorter than the last one seen (a new unit began between two looks), starts the watch again. A first look already past both gives
    // Close; End follows at the next look.
    public HubWatchAct Look(TimeSpan? busyFor)
    {
        if(busyFor is null||busyFor<last){closed=false;ended=false;}
        last=busyFor;
        if(busyFor is not TimeSpan b) return HubWatchAct.None;
        if(!closed)
        {
            if(b<HubWorker.StuckAfter) return HubWatchAct.None;
            closed=true;
            return HubWatchAct.Close;
        }
        if(ended||b<HubWorker.StuckAfter+HubWorker.EndAfter) return HubWatchAct.None;
        ended=true;
        return HubWatchAct.End;
    }
}

internal sealed class HubWorker:IDisposable
{
    public const int ExitCode=5;
    public const string EndText="the hub's USB call is stuck; ending the lighting helper so other programs get the Corsair lock back";
    public static readonly TimeSpan WatchEvery=TimeSpan.FromMilliseconds(250),StuckAfter=TimeSpan.FromMilliseconds(1500),EndAfter=TimeSpan.FromSeconds(3);
    static readonly TimeSpan JoinLimit=TimeSpan.FromSeconds(2),FaultPause=TimeSpan.FromSeconds(1);

    readonly HubCore core;
    readonly HostLog log;
    readonly Action endHelper;
    readonly ManualResetEventSlim signal=new(),stopping=new();
    readonly Thread thread;
    readonly Timer timer;
    readonly object watching=new();
    readonly HubWatch watch=new();                                        // under watching
    readonly HashSet<string> faults=new();                                // the loop's thread only
    bool watchOff;                                                        // under watching
    long seen;                                                            // under watching: the BusySince of the last look
    int looks,disposed;

    // Starts the thread and the watchdog's timer.
    public HubWorker(HubCore core,HostLog log,Action endHelper):this(core,log,endHelper,WatchEvery){}

    // Tests only: watchEvery Timeout.InfiniteTimeSpan leaves the looks to Tick.
    internal HubWorker(HubCore core,HostLog log,Action endHelper,TimeSpan watchEvery)
    {
        this.core=core;this.log=log;this.endHelper=endHelper;
        core.Nudge=signal.Set;
        thread=new Thread(Loop){IsBackground=true,Name="Hub"};
        timer=new Timer(Watch,null,watchEvery,watchEvery);
        thread.Start();
    }

    // Test seams: whether the thread still runs, how many looks the watchdog has made, and one look now.
    internal bool Alive=>thread.IsAlive;
    internal int Looks=>Volatile.Read(ref looks);
    internal void Tick()=>Watch(null);

    // Stops the loop and waits for it at most JoinLimit while the watchdog still watches (a call stuck now is closed at 1.5 s, which lets the
    // thread end), then stops the watchdog.
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0) return;
        stopping.Set();signal.Set();
        if(!thread.Join(JoinLimit)) log.Write("hub: the worker's thread did not end within 2 s");
        lock(watching) watchOff=true;
        timer.Dispose();
    }

    // A nudge during a step sets the signal, so the wait after it returns at once. The signal is reset only after the wait and before the next
    // step copies its inputs, and Dispose sets stopping before the signal, so neither a nudge nor the stop is lost.
    void Loop()
    {
        while(!stopping.IsSet)
        {
            TimeSpan wait;
            try{wait=core.Step();}
            catch(Exception e){wait=Guard(e);}
            signal.Wait(wait);
            signal.Reset();
        }
    }

    // The last guard: Step lets no exception out, so this is a fault of this code. The port is closed first, so none stays open behind it; each
    // line is logged once; the loop carries on after FaultPause, which only a stop cuts short. The exception's type is logged, never its message,
    // which can carry a device path.
    TimeSpan Guard(Exception e)
    {
        core.Abort();
        var line=$"hub: the worker's loop failed ({e.GetType().Name})";
        if(faults.Add(line)) log.Write(line);
        stopping.Wait(FaultPause);
        return TimeSpan.Zero;
    }

    // Each tick: how long the unit under way has run, on the real clock, judged under the watchdog's lock (so looks are made in order); the act
    // outside it, so an Abort that blocks leaves the next ticks free to end the helper. Abort only if the same unit is still under way. A unit is
    // known by its BusySince: when that changed since the last look, a null look first starts the watch again, so a unit that began after a late
    // tick is never judged as the one before it.
    void Watch(object? _)
    {
        long since;HubWatchAct act;
        lock(watching)
        {
            if(watchOff) return;
            since=core.BusySince;
            if(since!=seen){watch.Look(null);seen=since;}
            act=watch.Look(since==0?null:Stopwatch.GetElapsedTime(since));
            looks++;
        }
        if(act==HubWatchAct.Close){if(since!=0&&core.BusySince==since) core.Abort();}
        else if(act==HubWatchAct.End){log.Write(EndText);endHelper();}
    }
}
