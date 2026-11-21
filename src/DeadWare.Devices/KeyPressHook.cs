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
    const uint Quit=0x0012;                  // WM_QUIT
    const uint Extended=0x01,LowerInjected=0x02,Injected=0x10,Up=0x80; // KBDLLHOOKSTRUCT flags
    static readonly TimeSpan StartWait=TimeSpan.FromSeconds(2),StopWait=TimeSpan.FromSeconds(2);

    // One run of the hook thread. The thread writes only its own Loop, so a run that was stopped can't mark a later one installed.
    sealed class Loop
    {
        public volatile uint ThreadId;
        public volatile bool Hooked;
        public readonly ManualResetEventSlim Ready=new();
        public Thread? Thread;
    }
    readonly object sync=new();
    // Kept for the hook's whole life: Windows calls this delegate, so it must never be collected while a hook may use it.
    readonly HookProc proc;
    Loop? loop;
    KeyPressGate? gate;
    public KeyPressHook(){proc=Callback;}

    // False from the moment Stop is called, and when the thread's message loop ends for any reason.
    public bool Installed=>Volatile.Read(ref loop) is{Hooked:true};

    // Starts the thread and waits up to StartWait for it to say whether Windows installed the hook. False when it didn't.
    public bool Start(KeyPressGate gate)
    {
        lock(sync)
        {
            if(Volatile.Read(ref loop) is{Hooked:true}) return true;
            StopLocked();
            Volatile.Write(ref this.gate,gate);
            var l=new Loop();
            l.Thread=new Thread(()=>Pump(l)){IsBackground=true,Name="Key presses"};
            Volatile.Write(ref loop,l);
            l.Thread.Start();
            if(l.Ready.Wait(StartWait)&&l.Hooked) return true;
            StopLocked();
            return false;
        }
    }

    public void Stop(){lock(sync)StopLocked();}
    public void Dispose()=>Stop();

    // Installed turns false at once; WM_QUIT ends the thread's GetMessage loop, which then removes the hook. The quit is posted again until
    // the thread ends or StopWait passes, since a thread that hasn't reached its loop yet may have no message queue to post to.
    void StopLocked()
    {
        var l=Interlocked.Exchange(ref loop,null);
        if(l?.Thread is not{} t) return;
        var until=Environment.TickCount64+(long)StopWait.TotalMilliseconds;
        while(!t.Join(0))
        {
            if(l.ThreadId!=0) PostThreadMessageW(l.ThreadId,Quit,UIntPtr.Zero,IntPtr.Zero);
            if(t.Join(50)||Environment.TickCount64>=until) break;
        }
    }

    // The hook thread: install, report, then pump messages (Windows calls the hook from inside GetMessage) until WM_QUIT or an error.
    void Pump(Loop l)
    {
        l.ThreadId=GetCurrentThreadId();
        var hook=SetWindowsHookExW(KeyboardLowLevel,proc,GetModuleHandleW(null),0);
        if(hook==IntPtr.Zero){l.Ready.Set();return;}
        l.Hooked=true;
        l.Ready.Set();
        try{while(GetMessageW(out _,IntPtr.Zero,0,0)>0){}}
        finally{l.Hooked=false;UnhookWindowsHookEx(hook);}
    }

    // Only the scan code and the flags are read; the event is always passed on, and nothing is ever blocked, changed or sent.
    IntPtr Callback(int nCode,IntPtr wParam,IntPtr lParam)
    {
        if(nCode<0) return CallNextHookEx(IntPtr.Zero,nCode,wParam,lParam);
        try
        {
            var e=Marshal.PtrToStructure<KeyEvent>(lParam);
            Volatile.Read(ref gate)?.OnKey((int)e.ScanCode,(e.Flags&Extended)!=0,(e.Flags&(Injected|LowerInjected))!=0,(e.Flags&Up)!=0);
        }
        catch{}
        return CallNextHookEx(IntPtr.Zero,nCode,wParam,lParam);
    }

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
