using System.ComponentModel;
using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4, everyday: one background thread ("Everyday") with a hidden top-level window that is never shown (not a message-only window:
// those get no session or power broadcasts). It delivers the global shortcut (RegisterHotKey, named in this file alone), lock and unlock,
// and sleep and wake. Events are raised on this thread; nothing here takes a host lock. The shortcut is logged as words only.
// The Windows functions this file may import are exactly the list KeyPressRuleTests holds for it.
internal sealed class EverydayWatcher:IEverydaySignals
{
    const uint Hotkey=0x0312,SessionChange=0x02B1,PowerBroadcast=0x0218,Timer=0x0113,SetMessage=0x8000+1,StopMessage=0x8000+2; // WM_HOTKEY, WM_WTSSESSION_CHANGE, WM_POWERBROADCAST, WM_TIMER, WM_APP+1, WM_APP+2
    const int Locked=7,Unlocked=8;                         // WTS_SESSION_LOCK, WTS_SESSION_UNLOCK
    const int Suspend=4,ResumeSuspend=7,ResumeAutomatic=0x12; // PBT_APMSUSPEND, PBT_APMRESUMESUSPEND, PBT_APMRESUMEAUTOMATIC
    const int HotkeyId=1,RetryTimer=1,RetryMs=5000,RetryTries=12;
    const uint NoRepeat=0x4000;                            // MOD_NOREPEAT
    const string Unavailable="Shortcut and lock are unavailable: ";
    static readonly TimeSpan StartWait=TimeSpan.FromSeconds(2),StopWait=TimeSpan.FromSeconds(2);
    static int classes;

    sealed class Request{public Shortcut? Shortcut;public volatile int Result=-1;public readonly ManualResetEventSlim Done=new();}

    readonly HostLog log;
    readonly WindowProc proc;   // kept alive for the window class: Windows calls it for the window's whole life
    readonly string className="DeadWareEveryday."+Interlocked.Increment(ref classes);
    readonly ManualResetEventSlim ready=new();
    readonly object sync=new();
    readonly Thread thread;
    volatile IntPtr window;
    volatile bool stopped;
    volatile string? problem;
    Request? pending;
    // Watcher thread only.
    bool sessionRegistered,suspended;
    int sessionTries;
    IntPtr power;

    public event Action? ShortcutPressed;
    public event Action<bool>? LockChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    public string? Problem=>problem;

    public EverydayWatcher(HostLog log)
    {
        this.log=log;proc=Procedure;
        thread=new Thread(Pump){IsBackground=true,Name="Everyday"};
        thread.Start();
        if(!ready.Wait(StartWait)){problem??=Unavailable+"the watcher's thread didn't start in time.";stopped=true;}
        if(problem is{} p) log.Write(p);
    }

    public bool? SetShortcut(Shortcut? shortcut,TimeSpan wait)
    {
        lock(sync)
        {
            if(stopped||window==IntPtr.Zero) return null;
            var r=new Request{Shortcut=shortcut};
            Volatile.Write(ref pending,r);
            if(!PostMessageW(window,SetMessage,IntPtr.Zero,IntPtr.Zero)) return null;
            if(!r.Done.Wait(wait)) return null;
            return r.Result switch{1=>true,0=>false,_=>null};
        }
    }

    // Marks the watcher stopped, then asks the thread to unregister everything and destroy its window, which ends its message loop. The ask
    // is posted again until the thread ends or StopWait passes.
    public void Dispose()
    {
        lock(sync)
        {
            if(stopped&&!thread.IsAlive) return;
            stopped=true;
            var until=Environment.TickCount64+(long)StopWait.TotalMilliseconds;
            while(!thread.Join(0))
            {
                if(window is var w&&w!=IntPtr.Zero) PostMessageW(w,StopMessage,IntPtr.Zero,IntPtr.Zero);
                if(thread.Join(50)||Environment.TickCount64>=until) break;
            }
        }
    }

    void Pump()
    {
        var module=GetModuleHandleW(null);
        var registered=false;
        try
        {
            var wc=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=Marshal.GetFunctionPointerForDelegate(proc),Instance=module,ClassName=className};
            if(RegisterClassExW(ref wc)==0){Fail("Windows refused the window class");return;}
            registered=true;
            var w=CreateWindowExW(0x80/*WS_EX_TOOLWINDOW*/,className,"",0x80000000/*WS_POPUP*/,0,0,0,0,IntPtr.Zero,IntPtr.Zero,module,IntPtr.Zero);
            if(w==IntPtr.Zero){Fail("Windows refused the hidden window");return;}
            window=w;
            RegisterSession(w);
            if(!sessionRegistered){log.Write($"Lock signals not yet available ({Marshal.GetLastWin32Error()}); trying again every {RetryMs/1000} s");SetTimer(w,(UIntPtr)RetryTimer,RetryMs,IntPtr.Zero);}
            power=RegisterSuspendResumeNotification(w,0/*DEVICE_NOTIFY_WINDOW_HANDLE*/);
            if(power==IntPtr.Zero) log.Write($"Sleep signals: registration refused ({Marshal.GetLastWin32Error()}); relying on the broadcast");
            ready.Set();
            if(stopped) TearDown(w);
            while(GetMessageW(out var m,IntPtr.Zero,0,0)>0) DispatchMessageW(ref m);
        }
        catch(Exception e){problem??=Unavailable+e.Message;log.Write("Everyday watcher stopped: "+e.Message);}
        finally
        {
            if(window is var w&&w!=IntPtr.Zero){TearDown(w);window=IntPtr.Zero;}
            if(registered) UnregisterClassW(className,module);
            stopped=true;
            if(!ready.IsSet) ready.Set();
        }
    }

    void Fail(string what)
    {
        var e=Marshal.GetLastWin32Error();
        problem=$"{Unavailable}{what}: {new Win32Exception(e).Message} ({e}).";
        ready.Set();
    }

    void RegisterSession(IntPtr w)
    {
        sessionTries++;
        sessionRegistered=WTSRegisterSessionNotification(w,0/*NOTIFY_FOR_THIS_SESSION*/);
    }

    // Undoes every registration and destroys the window (its timer goes with it). False results from unregistering what was never
    // registered are ignored. Destroying the window posts nothing, so the loop is ended by a quit of its own.
    void TearDown(IntPtr w)
    {
        if(window==IntPtr.Zero) return;
        window=IntPtr.Zero;
        UnregisterHotKey(w,HotkeyId);
        if(sessionRegistered){WTSUnRegisterSessionNotification(w);sessionRegistered=false;}
        if(power!=IntPtr.Zero){UnregisterSuspendResumeNotification(power);power=IntPtr.Zero;}
        KillTimer(w,(UIntPtr)RetryTimer);
        DestroyWindow(w);
        PostQuitMessage(0);
    }

    IntPtr Procedure(IntPtr w,uint message,IntPtr wParam,IntPtr lParam)
    {
        try
        {
            switch(message)
            {
                case Hotkey:
                    if((int)wParam==HotkeyId) ShortcutPressed?.Invoke();
                    return IntPtr.Zero;
                case SessionChange:
                    if((int)wParam==Locked) LockChanged?.Invoke(true);
                    else if((int)wParam==Unlocked) LockChanged?.Invoke(false);
                    return IntPtr.Zero;
                case PowerBroadcast:
                    OnPower((int)wParam);
                    return 1;
                case Timer when (int)wParam==RetryTimer:
                    if(!sessionRegistered) RegisterSession(w);
                    if(sessionRegistered){KillTimer(w,(UIntPtr)RetryTimer);log.Write("Lock signals available");}
                    else if(sessionTries>=RetryTries){KillTimer(w,(UIntPtr)RetryTimer);log.Write("Lock signals unavailable: gave up trying");}
                    return IntPtr.Zero;
                case SetMessage:
                    if(Interlocked.Exchange(ref pending,null) is{} r) Apply(w,r);
                    return IntPtr.Zero;
                case StopMessage:
                    TearDown(w);
                    return IntPtr.Zero;
            }
        }
        catch(Exception e){log.Write("Everyday watcher: "+e.Message);}
        return DefWindowProcW(w,message,wParam,lParam);
    }

    // Suspend can arrive twice: raised once. A resume from suspend with no suspend since the last wake is ignored; the automatic resume is
    // always raised, and a resume from suspend after it is then ignored.
    void OnPower(int e)
    {
        if(e==Suspend){if(!suspended){suspended=true;Suspending?.Invoke();}}
        else if(e==ResumeAutomatic){suspended=false;Resumed?.Invoke();}
        else if(e==ResumeSuspend&&suspended){suspended=false;Resumed?.Invoke();}
    }

    void Apply(IntPtr w,Request r)
    {
        UnregisterHotKey(w,HotkeyId);
        if(r.Shortcut is not{} s){r.Result=1;log.Write("Shortcut cleared");}
        else if(RegisterHotKey(w,HotkeyId,(uint)s.Modifiers|NoRepeat,(uint)s.Key)){r.Result=1;log.Write("Shortcut set: "+Shortcuts.Text(s));}
        else{r.Result=0;log.Write($"Shortcut refused by Windows: {Shortcuts.Text(s)} ({Marshal.GetLastWin32Error()})");}
        try{r.Done.Set();}catch(ObjectDisposedException){}
    }

#pragma warning disable CS0169,CS0649 // filled by Windows and handed back to it; never read
    // MSG, never read.
    [StructLayout(LayoutKind.Sequential)]
    struct Message{IntPtr window;uint message;UIntPtr w;IntPtr l;uint at;int x,y;uint extra;}
#pragma warning restore CS0169,CS0649
    // WNDCLASSEXW.
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct WindowClass
    {
        public uint Size,Style;
        public IntPtr Procedure;
        public int ClassExtra,WindowExtra;
        public IntPtr Instance,Icon,Cursor,Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    delegate IntPtr WindowProc(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterClassW(string className,IntPtr instance);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle,string className,string name,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetMessageW(out Message message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref Message message);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterHotKey(IntPtr window,int id);
    [DllImport("wtsapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool WTSRegisterSessionNotification(IntPtr window,uint flags);
    [DllImport("wtsapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient,uint flags);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterSuspendResumeNotification(IntPtr handle);
    [DllImport("user32.dll",SetLastError=true)] static extern UIntPtr SetTimer(IntPtr window,UIntPtr id,uint ms,IntPtr callback);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool KillTimer(IntPtr window,UIntPtr id);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
