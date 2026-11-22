using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeadWare.Devices;

// Lighting 3b, the key-press rule: the one class, in the one file, that owns the low-level keyboard hook. It runs on a background thread of
// its own with its own message loop. Its callback reads only the scan code and three flags (extended, sent by software, key up) and hands
// them to KeyPressGate.OnKey, which turns them into light numbers or drops them; then it always passes the event on, unchanged. Nothing
// here keeps, logs or sends anything about a key.
// The Windows functions this file may import are exactly: SetWindowsHookEx, UnhookWindowsHookEx, CallNextHookEx, GetMessage,
// PostThreadMessage, GetModuleHandle, GetCurrentThreadId (KeyPressRuleTests holds the list).
internal sealed class KeyPressHook:IKeyPressSource
{
    const int KeyboardLowLevel=13;           // WH_KEYBOARD_LL
    const uint Nothing=0x0000,Quit=0x0012;   // WM_NULL, WM_QUIT
    const uint Extended=0x01,LowerInjected=0x02,Injected=0x10,Up=0x80; // KBDLLHOOKSTRUCT flags
    static readonly TimeSpan StartWait=TimeSpan.FromSeconds(2),StopWait=TimeSpan.FromSeconds(2);

    // One run of the hook thread. Gate is the run's own and is cleared when the run is stopped, so a thread that outlives its Stop feeds
    // nothing. Stopped is set first thing by StopLocked; the thread checks it after installing and the callback checks it on every event,
    // so a run that was stopped removes its own hook however late it got going. The thread writes only its own Loop.
    sealed class Loop
    {
        public volatile uint ThreadId;
        public volatile bool Hooked,Stopped,Quitting;
        public volatile KeyPressGate? Gate;
        public int Error;
        public readonly ManualResetEventSlim Ready=new();
        public Thread? Thread;
    }
    // The run whose thread this is: set by Pump on the hook thread, read by the callback, which Windows calls on that same thread.
    [ThreadStatic] static Loop? mine;
    readonly object sync=new();
    // Kept for the hook's whole life: Windows calls this delegate, so it must never be collected while a hook may use it.
    readonly HookProc proc;
    Loop? loop;
    public KeyPressHook(){proc=Callback;}

    // False from the moment Stop is called, and when the thread's message loop ends for any reason.
    public bool Installed=>Volatile.Read(ref loop) is{Hooked:true};

    // Starts the thread and waits up to StartWait for it to say whether Windows installed the hook. Throws with Windows' reason when it
    // refused, or when the thread didn't answer in time. On those failure paths it can block its caller for up to about 4 s: StopLocked's
    // StopWait for an earlier thread that won't end, then StartWait.
    public bool Start(KeyPressGate gate)
    {
        lock(sync)
        {
            if(Volatile.Read(ref loop) is{Hooked:true} running){running.Gate=gate;return true;}
            StopLocked();
            var l=new Loop{Gate=gate};
            l.Thread=new Thread(()=>Pump(l)){IsBackground=true,Name="Key presses"};
            Volatile.Write(ref loop,l);
            l.Thread.Start();
            var answered=l.Ready.Wait(StartWait);
            if(answered&&l.Hooked) return true;
            var error=l.Error;
            StopLocked();
            if(!answered) throw new InvalidOperationException("the keyboard hook's thread didn't start in time");
            throw new InvalidOperationException($"Windows refused the keyboard hook: {new Win32Exception(error).Message} ({error})");
        }
    }

    public void Stop(){lock(sync)StopLocked();}
    public void Dispose()=>Stop();

    // Installed turns false at once, the run is marked stopped and loses its gate; WM_QUIT then ends the thread's GetMessage loop, which
    // removes the hook. The quit is posted again until the thread ends or StopWait passes. Should the thread still not have ended (it had
    // not yet got going), it removes its own hook as soon as it installs it (Pump), or at its first key event (Callback), and feeds nothing
    // meanwhile. The run's Ready event is disposed once its thread has ended.
    void StopLocked()
    {
        var l=Interlocked.Exchange(ref loop,null);
        if(l?.Thread is not{} t) return;
        l.Stopped=true;l.Gate=null;
        var until=Environment.TickCount64+(long)StopWait.TotalMilliseconds;
        while(!t.Join(0))
        {
            if(l.ThreadId!=0) PostThreadMessageW(l.ThreadId,Quit,UIntPtr.Zero,IntPtr.Zero);
            if(t.Join(50)||Environment.TickCount64>=until) break;
        }
        if(!t.IsAlive) l.Ready.Dispose();
    }

    // The hook thread. Its first call into user32 (a WM_NULL posted to itself, which GetMessage later reads and nothing acts on) makes it a
    // GUI thread with a message queue, so the WM_QUIT Stop posts can be delivered from the moment ThreadId is published. Then install;
    // a run already stopped removes the hook again at once. Then pump messages (Windows calls the hook from inside GetMessage) until WM_QUIT
    // or an error.
    void Pump(Loop l)
    {
        mine=l;
        var id=GetCurrentThreadId();
        PostThreadMessageW(id,Nothing,UIntPtr.Zero,IntPtr.Zero);
        l.ThreadId=id;
        var hook=SetWindowsHookExW(KeyboardLowLevel,proc,GetModuleHandleW(null),0);
        if(hook==IntPtr.Zero){l.Error=Marshal.GetLastWin32Error();l.Ready.Set();return;}
        try
        {
            if(l.Stopped) return;
            l.Hooked=true;
            l.Ready.Set();
            while(!l.Stopped&&GetMessageW(out _,IntPtr.Zero,0,0)>0){}
        }
        finally
        {
            l.Hooked=false;
            UnhookWindowsHookEx(hook);
            if(!l.Ready.IsSet) try{l.Ready.Set();}catch(ObjectDisposedException){}
        }
    }

    // Only the scan code and the flags are read; the event is always passed on, and nothing is ever blocked, changed or sent. A run that has
    // been stopped hands nothing on and asks its own loop to quit.
    IntPtr Callback(int nCode,IntPtr wParam,IntPtr lParam)
    {
        if(nCode<0) return CallNextHookEx(IntPtr.Zero,nCode,wParam,lParam);
        try
        {
            var l=mine;
            if(l is null||l.Stopped)
            {
                if(l is{Quitting:false}){l.Quitting=true;PostThreadMessageW(l.ThreadId,Quit,UIntPtr.Zero,IntPtr.Zero);}
            }
            else if(l.Gate is{} gate)
            {
                var e=Marshal.PtrToStructure<KeyEvent>(lParam);
                var (extended,sentBySoftware,up)=Decode(e.Flags);
                gate.OnKey((int)e.ScanCode,extended,sentBySoftware,up);
            }
        }
        catch{}
        return CallNextHookEx(IntPtr.Zero,nCode,wParam,lParam);
    }

    // The three flags the gate is given: extended, sent by software (either injected flag) and key up. Nothing else in the flags is read.
    internal static (bool Extended,bool SentBySoftware,bool Up) Decode(uint flags)=>((flags&Extended)!=0,(flags&(Injected|LowerInjected))!=0,(flags&Up)!=0);

#pragma warning disable CS0169,CS0649 // filled by Windows; the fields that aren't read are padding so they can't be read by name
    // KBDLLHOOKSTRUCT. The first field, the virtual-key code, is padding.
    [StructLayout(LayoutKind.Sequential)]
    readonly struct KeyEvent
    {
        private readonly uint _0;
        public readonly uint ScanCode;
        public readonly uint Flags;
        private readonly uint _1;
        private readonly UIntPtr _2;
    }
    // MSG, never read.
    [StructLayout(LayoutKind.Sequential)]
    struct Message{IntPtr window;uint message;UIntPtr w;IntPtr l;uint at;int x,y;uint extra;}
#pragma warning restore CS0169,CS0649

    delegate IntPtr HookProc(int nCode,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr SetWindowsHookExW(int idHook,HookProc proc,IntPtr module,uint threadId);
    [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int nCode,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetMessageW(out Message message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll",SetLastError=true,CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool PostThreadMessageW(uint threadId,uint message,UIntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
