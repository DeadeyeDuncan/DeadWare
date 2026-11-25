using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4, everyday: the signals the host turns lights off and on by. The events are raised on the watcher's own thread: a handler only
// sets flags and wakes the host loop, and never takes a host lock.
internal interface IEverydaySignals:IDisposable
{
    event Action? ShortcutPressed;
    event Action<bool>? LockChanged;     // true = locked
    event Action? Suspending;
    event Action? Resumed;
    // Why the shortcut, lock and sleep handling are unavailable, or null.
    string? Problem{get;}
    // Sets (or with null clears) the global shortcut. True = registered or cleared; false = Windows refused it; null = no answer within
    // wait, or the watcher is unavailable or disposed.
    bool? SetShortcut(Shortcut? shortcut,TimeSpan wait);
}

// Lighting 4, everyday: how long since the last keyboard or mouse input anywhere, and where the pointer is. Read on the host loop.
internal interface IInputClock{long IdleMs();(int X,int Y) Pointer();}
