using DeadWare.Core;

namespace DeadWare.Devices.Tests;

// Lighting 4: the watcher's signals, raised by the test on its own thread as the watcher raises them on its own. Every shortcut the host
// sets is recorded; Refuse answers for Windows (true registered, false refused, null no answer in time), true when unset.
sealed class FakeSignals:IEverydaySignals
{
    readonly object sync=new();
    readonly List<Shortcut?> shortcuts=new();
    readonly List<TimeSpan> waits=new();
    public event Action? ShortcutPressed;
    public event Action<bool>? LockChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    public volatile Func<Shortcut?,bool?>? Refuse;
    public volatile string? Trouble;
    public string? Problem=>Trouble;
    public List<Shortcut?> Shortcuts{get{lock(sync)return shortcuts.ToList();}}
    public List<TimeSpan> Waits{get{lock(sync)return waits.ToList();}}
    public bool? SetShortcut(Shortcut? shortcut,TimeSpan wait){lock(sync){shortcuts.Add(shortcut);waits.Add(wait);}return Refuse is{} f?f(shortcut):true;}
    public void Press()=>ShortcutPressed?.Invoke();
    public void Lock(bool locked)=>LockChanged?.Invoke(locked);
    public void Suspend()=>Suspending?.Invoke();
    public void Resume()=>Resumed?.Invoke();
    public void Dispose(){}
}

// Idle time and the pointer, set by the test; IdleCalls counts the idle reads.
sealed class FakeInput:IInputClock
{
    long idle;
    int idleCalls;
    public long Idle{get=>Interlocked.Read(ref idle);set=>Interlocked.Exchange(ref idle,value);}
    public volatile Tuple<int,int> At=Tuple.Create(0,0);
    public int IdleCalls=>Volatile.Read(ref idleCalls);
    public long IdleMs(){Interlocked.Increment(ref idleCalls);return Idle;}
    public (int X,int Y) Pointer(){var a=At;return (a.Item1,a.Item2);}
}
