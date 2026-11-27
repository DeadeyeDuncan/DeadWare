using DeadWare.Core;

namespace DeadWare.Devices;

internal sealed record HostOptions(string OpenRgbPath,string ConfigDir,string LightingPath,string LogDir,bool Startup,string Sid,int Session);

// ended (fix wave 4, A): the engine's last exited OpenRGB, never counted as another one; endedListed: Windows still lists it.
internal interface IConflictScanner { IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid,(int Id,DateTime Started)? ended,out bool endedListed); }

// Orchestrates the host: settings, conflict gate, OpenRGB engine, device painting and the window's requests.
// Concurrency: two locks. gate guards every touch of settings, the device service, the session flag, the status and the preview state;
// previewGate serialises raising preview frames against ClientGone. Order: previewGate then gate, never the reverse. PreviewChanged
// subscribers run under previewGate (not gate), and nothing calls the pipe server while holding gate.
// Callers are the run loop, pipe requests and engine events on the supervisor thread. Their work under the lock is synchronous and
// includes OpenRGB SDK calls, which OpenRGB.NET leaves unbounded: BoundedSession cuts each one off at BoundedSession.CallBound (3 s) and
// drops the session (Ruling AO). One hold of the lock can chain several such calls, each with its own bound: a connect is Connect,
// ReadControllers, for a Node Pro a resize and a second read, then a paint of one or more writes per device. So an OpenRGB that answers
// slowly but within the bound can keep the lock for several bounds in a row; a dead one costs one bound, after which every call fails at
// once until a connect succeeds. RunAsync's finally and the conflict stop take the lock before they drop the session, so they wait out a
// call that hangs until its bound. The session is also dropped, outside the lock, whenever OpenRGB stops, so a call waiting on a dead
// OpenRGB fails at once. Engine start/stop, conflict scans and StateChanged subscribers run OUTSIDE the lock, so the lock never nests
// with the engine's own lock the wrong way round and a slow subscriber cannot stall painting.
internal sealed class DeviceHost : IAsyncDisposable
{
    // FastTick: the paint tick while something animates with the lights on (a desk effect, or a painted device's keyboard mode) and the
    // host is Running with a session; Tick otherwise. SlowTick's work keeps its cadence at either.
    public static readonly TimeSpan Tick=TimeSpan.FromMilliseconds(500),SlowTick=TimeSpan.FromSeconds(5),StartupDelay=TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FastTick=TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan RescanSettle=TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ConnectDelayMin=TimeSpan.FromSeconds(2),ConnectDelayMax=TimeSpan.FromSeconds(30);
    public const string ScanFailedText="DeadWare couldn't check for conflicting programs; it will try again shortly.";
    // Polish round 2, item 8: the host runs on defaults and saves nothing, and the file is read again only when a host starts (Initialize
    // runs once per host process): after lighting is turned off and on, after Quit and a new start, or after the window relaunches a host
    // that stopped. A restart of DeadWare's window alone leaves a running host as it is, and the file may fail again, so the note promises
    // no fix.
    public const string UnreadableNote="Your lighting settings file couldn't be read, so lighting uses its defaults and DeadWare won't save lighting changes. It tries the file again when the lighting helper next starts, such as when you turn lighting off and on.";
    // B3/B14/B17/B24 and F2 item 1: an elevated host refuses lighting.json when its path goes through a link or junction (SafeFiles.NoLinks
    // checks the file with its folders, so "the file's path" names both: round 2, item 7), when the file it opened has another final path,
    // as any other redirection gives, and when Windows can't give that final path. A restart changes none of them, so the note promises no
    // fix from one.
    public const string LinkedNote="Your lighting settings file's path goes through a link, or leads somewhere DeadWare can't confirm, so DeadWare won't save lighting changes while it runs as administrator. Lighting uses its defaults.";
    public const string QuarantineNote="Your lighting settings file was damaged, so DeadWare set it aside and started with defaults.";
    public const string LostText="Lost the connection to OpenRGB; reconnecting.";
    public const string NewerNote="Your lighting settings were saved by a newer DeadWare, so this version won't save changes to them.";
    // B7: a newer file whose values this build can't accept: its defaults are painted, and the file is left for the build that wrote it.
    public const string NewerDefaultsNote="Your lighting settings were saved by a newer DeadWare and this version can't use them, so it uses its defaults and won't change the file.";
    readonly HostOptions options;
    readonly LightingStore store;
    readonly OpenRgbEngine engine;
    readonly IOpenRgbSession session;
    readonly LightingDeviceService service;
    readonly IConflictScanner scanner;
    readonly TimeProvider time;
    readonly HostLog log;
    readonly string? startupFailure;
    // epoch: when the host was made; effects are painted at the milliseconds since (Now).
    readonly long epoch;
    readonly CancellationTokenSource stopCts=new();
    readonly object gate=new();
    LightingSettings settings=new();
    // conflictsChecked: a conflict scan has completed since the host started (polish item 2); it stays set.
    bool saveAllowed=true,engineActive,sessionOk,rescanPending,disposed,loaded,conflictsChecked;
    // lastArrangeError: why the last rescan's auto-arrange wasn't adopted, so the same reason is logged once.
    string? note,lastConflictKey,lastSessionError,lastScanError,lastArrangeError;
    readonly object raiseGate=new();
    long nextConnectAt;
    // starting: OpenRGB's start while it is pending (fix wave 1, F2); only the run loop touches it.
    Task? starting;
    TimeSpan connectDelay=ConnectDelayMin;
    HostStatus status=HostStatus.Starting;
    string message="";
    IReadOnlyList<LightingConflict> conflicts=[];
    public event Action<HostState>? StateChanged;
    // Lighting 3b, the key-press rule: keys is the source of key presses (the low-level hook in the process; a fake in tests; none, never
    // watched). It runs only while the host is Running with a session and some keyboard needs key presses (Watch). The gate turns its events
    // into LightPress records and is the only thing the source is given. A press wakes the loop through wake, a TaskCompletionSource whose
    // continuations run on the thread pool, never on the hook's thread; the loop makes a fresh one each turn. Never cancel a token from the
    // press callback: the cancellation would run the loop's continuation, the paint tick and its OpenRGB calls, on the hook thread.
    // watchGate orders Watch, its refresh and the stop at the end: raiseGate, then watchGate, then gate. watchStarted, watchLogged (how many
    // keyboards the last "watching" line named, 0 once "not watching" was logged or a start failed), watchFailLogged and refreshAt are
    // guarded by watchGate; keyHook (the state's KeyHook) and namesLogged (keyboards whose light names were logged since the device list
    // last changed) by gate.
    public static readonly TimeSpan KeyRefresh=TimeSpan.FromMinutes(5);
    const string StartRefused="Windows didn't install the keyboard hook";
    readonly IKeyPressSource? keys;
    readonly KeyPressGate presses;
    TaskCompletionSource wake=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object watchGate=new();
    bool watchStarted,watchFailLogged,keyHook;
    int watchLogged;
    long refreshAt;
    readonly HashSet<string> namesLogged=new();
    // Preview frames (Task 7; step 3a adds device modes and per-light colours for key-map devices): while the window has subscribed and
    // something animates (LightingPaint.Animating), the paint tick takes the colours before
    // brightness at most once per PreviewGap of the host's time and raises them when they differ from the last frame sent. previewOn, the
    // last frame and its time are guarded by gate; previewGeneration changes with every subscribe and ClientGone, so a frame taken under an
    // older subscription is never raised. Frames are raised outside gate under previewGate, which ClientGone also takes, so once it returns
    // no frame follows.
    // Lighting 3b: painted keys and layers aren't in the state's colours, so a painted desk that doesn't move still needs a frame. previewDue
    // is set, while subscribed, by the subscription and by every raised state; the next paint tick then sends one frame whatever animates,
    // however soon after the last and even when it equals it, if the window would draw it (LightingPaint.ShowsLights: lights on and some
    // painted device with paint or layers). So with nothing painted the frames are 3a's, and none goes while the lights are off.
    public static readonly TimeSpan PreviewGap=TimeSpan.FromMilliseconds(100);
    public event Action<PreviewEvent>? PreviewChanged;
    readonly object previewGate=new();
    bool previewOn,previewDue;
    // wbr-1 D1: the last preview tick found something moving. When that stops (a key press's fade or ring ends, with nothing else moving),
    // the next tick sends one closing frame like a due one, so the window, which keeps drawing the last frame while it shows painted
    // lights, doesn't keep the press lit.
    bool previewMoving;
    int previewGeneration;
    double? lastPreviewMs;
    PreviewEvent? lastPreview;
    // The window is gone: its subscription ends. Called by the pipe server outside its own locks.
    public void ClientGone(){lock(previewGate)lock(gate){previewOn=false;previewDue=false;previewGeneration++;lastPreview=null;lastPreviewMs=null;}}

    // Lighting 4, everyday: signals (the watcher: shortcut, lock, sleep; a fake in tests; none, and nothing changes) and input (idle time,
    // read only while the idle rule is on). Their events only set flags in everyday and wake the loop; they never take a lock here. The loop
    // works out why the lights are off beyond the switch (EverydayWork), and everything that paints, previews, watches keys or picks the
    // tick reads Shown: the settings with the lights off while a reason applies. Shown is made at each use, never kept, since a rescan's
    // remap replaces tables on the live settings (ruling P13). shortcutProblem and shortcutRetry are guarded by gate.
    // ShortcutWait: a request waits this long for the watcher's answer; RetryWait: the slow-work retry of the saved shortcut, kept short since it
    // holds gate while painting may run at the fast tick.
    public static readonly TimeSpan ShortcutWait=TimeSpan.FromSeconds(1),RetryWait=TimeSpan.FromMilliseconds(250);
    readonly IEverydaySignals? signals;
    readonly IInputClock? input;
    readonly EverydayState everyday;
    readonly Action onPress,onSuspend,onResume;
    readonly Action<bool> onLock;
    string? shortcutProblem;
    bool shortcutRetry;
    // Task 5, sleep and wake (amendment 4): from the suspend signal until WakeDelay after the first resume signal (everyday.Quiet) nothing
    // paints or connects: not the tick, a change, a rescan request or OpenRGB coming back. The reason is waking. The wake's end is taken on
    // the loop (WakeEndLocked); Woke is raised once after it, outside every lock. wakePaint (under gate): the wake's repaint isn't logged yet.
    public static readonly TimeSpan WakeDelay=TimeSpan.FromSeconds(10);
    internal event Action? Woke;
    bool wakePaint;
    LightingSettings Shown=>everyday.Reason(settings) is null?settings:settings.WithLightsOff();
    // Lighting 4, Task 7: the Aerox 9 on its receiver (none: the receiver is never used). Every call to it is made under gate: Tick each loop
    // turn, Scan in slow work and before a connect's rescan, Must on a change of effective lights or of the mouse's own frame or sleep time
    // (fix wave 1, F3), Reopen at the wake's end.
    // Task 8: Poll (the battery and the view) in TickWork while lighting runs, and the battery's Invalidate at the wake's end.
    readonly AeroxMouse? mouse;
    // Lighting 5, the hub (none: no HubHost, no hub step and HostState.Hub null, as in 0.22). The host's hub state is in hubHost, under gate;
    // the logic is in HubHold, HubHost and HubTakeover, and this class only wires it. Nothing on the loop or under gate waits for the hub:
    // HubWork looks at the worker's snapshot turn by turn, and the one wait is the hand-back of at most HubHost.HandBack at the stop, off the
    // lock. The worker's Changed only wakes the loop. Under gate: connectedOnce (ConnectLocked has succeeded in this run; never cleared), hubWhy (the forbid last
    // sent, or Starting before the first), lastHubView (the hub view of the last raised state), hubStateDue (SetHubModeLocked changed the
    // settings), hubScanFailed (the loop's last scan failed), ownScan (the hub step's own scan, newer than the loop's last one, for the facts
    // only; it stands until CheckConflictsAsync assigns conflicts again).
    // startDue (the loop only): StartOpenRgb came while a start was pending.
    // Task 10: the hub's requests and the take-over are in HubTakeover (under gate, as hubHost); HubWorkAsync drives it and does its work off the
    // lock. settleMode is SetHubModeLocked, the one way the take-over sets the hub mode.
    readonly IHub? hub;
    readonly HubHost? hubHost;
    readonly HubTakeover? takeover;
    readonly Func<HubSettings,string,bool> settleMode;
    bool connectedOnce,hubStateDue,hubScanFailed,startDue;
    (string? Reason,string? Detail) hubWhy=(HubReasons.Starting,null);
    HubView? lastHubView;
    (IReadOnlyList<LightingConflict> Found,bool Failed)? ownScan;
    public event Action<HubReadingsEvent>? HubReadingsChanged;
    const string RestartingText="OpenRGB is restarting. Lighting will resume in a moment.";
    // Fix wave 2, E: a back-out's or switch-off's deliberate stop that could not end OpenRGB; no second OpenRGB is started beside it.
    // Fix wave 3, D1/D2: NotRestarted clears engineActive and sets notRestarted (under gate); while the engine's Lingering says that OpenRGB
    // still runs, the conflict check counts it as ours and keeps this status; once it has gone, whether or not a scan saw it, the check
    // starts OpenRGB as usual.
    internal const string NotRestartedText="OpenRGB could not be restarted: it did not exit when DeadWare stopped it.";
    bool notRestarted;
    // Fix wave 4, A (under gate): the last scan found the engine's exited OpenRGB (OpenRgbEngine.Ended) still listed. It is never another OpenRGB;
    // while it is listed the take-over waits before the take (at most OpenRgbEngine.LingerWindow after the stop), and after notRestarted it is
    // treated as the lingering one is.
    bool leftover;
    // Fix wave 4c (review D1, D3; under gate): leftover counts for the hold only on the take-over's Take step, and for the Restart chore only when the
    // engine's ended OpenRGB is takeStopped, the one the take-over's own stop ended.
    (int Id,DateTime Started)? takeStopped;

    // startupFailure: set when the host could not prepare OpenRGB's config folder; it reports OpenRgbFailed and never starts OpenRGB.
    // callClock: the clock of the bound on OpenRGB calls; real time unless a test supplies one.
    // hub, detector (lighting 5): the hub's worker and OpenRGB's hub detector as Program's launches read it.
    public DeviceHost(HostOptions options,LightingStore store,OpenRgbEngine engine,Func<IOpenRgbSession> sessionFactory,IConflictScanner conflicts,TimeProvider time,HostLog log,string? startupFailure=null,TimeProvider? callClock=null,IKeyPressSource? keys=null,IEverydaySignals? signals=null,IInputClock? input=null,AeroxMouse? mouse=null,IHub? hub=null,HubDetector? detector=null)
    {
        this.options=options;this.store=store;this.engine=engine;scanner=conflicts;this.time=time;this.log=log;this.startupFailure=startupFailure;
        epoch=time.GetTimestamp();
        this.keys=keys;presses=new(Now,Wake);
        this.signals=signals;this.input=input;everyday=new(log,time);
        // Lighting 5: the hub goes quiet from the suspend signal itself (safe from any thread), not at the loop's next turn, which may be
        // inside an awaited engine stop for up to 5 s when the PC suspends.
        onPress=()=>{everyday.Press();Wake();};onLock=l=>{everyday.SetLocked(l);Wake();};onSuspend=()=>{hub?.Quiet();everyday.SetSuspended();Wake();};onResume=()=>{everyday.SetResumed();Wake();};
        if(signals is not null){signals.ShortcutPressed+=onPress;signals.LockChanged+=onLock;signals.Suspending+=onSuspend;signals.Resumed+=onResume;}
        session=new BoundedSession(sessionFactory(),callClock);
        this.mouse=mouse;
        this.hub=hub;
        service=new(session,time,log,mouse,hub);
        if(hub is not null){hubHost=new(hub,detector,time,log);takeover=new(hubHost,time,log);hub.Changed+=Wake;}
        settleMode=SetHubModeLocked;
        session.DeviceListUpdated+=OnDeviceListUpdated;
        engine.Changed+=OnEngineChanged;
    }

    public HostState State{get{lock(gate)return BuildLocked();}}
    HostState BuildLocked()
    {
        var text=note is null?message:message.Length==0?note:message+" "+note;
        return new(status,text,Environment.IsPrivilegedProcess,conflicts,service.Views(Shown),Clone(settings),conflictsChecked,keyHook,
            everyday.Reason(settings),shortcutProblem,signals?.Problem,hubHost?.View(settings,hubWhy));
    }
    static LightingSettings Clone(LightingSettings s)=>new()
    {
        Version=s.Version,LightsOn=s.LightsOn,Brightness=s.Brightness,DefaultColor=s.DefaultColor,
        ZoneColors=(s.ZoneColors??new()).ToDictionary(e=>e.Key,e=>new ZoneColorEntry{Color=e.Value.Color,ZoneName=e.Value.ZoneName}),
        HardwareModes=new(s.HardwareModes??new()),
        Effect=LightingEffects.Clone(s.Effect),Layout=DeskLayouts.Clone(s.Layout),Devices=DeviceTunings.Clone(s.Devices),
        Everyday=Everydays.Clone(s.Everyday),Mouse=new(){SleepMinutes=s.Mouse.SleepMinutes},Hub=Hubs.Clone(s.Hub),
    };
    double Now()=>time.GetElapsedTime(epoch).TotalMilliseconds;
    void Raise()
    {
        // Build and deliver under one gate: concurrent raises reach subscribers in build order, so the last delivered state is the newest.
        // Every state follows a change of settings, devices or status, so the keyboards to watch are worked out first and the state says
        // whether the hook is installed. Controller ruling R8: the frame the state asks for (previewDue) is asked for only after the state
        // has been handed on, so it can't reach the pipe first, and the loop is woken for it at once rather than at its next tick.
        lock(raiseGate)
        {
            Watch();
            HostState state;
            lock(gate){state=BuildLocked();lastHubView=state.Hub;}
            try{StateChanged?.Invoke(state);}catch(Exception e){log.Write("StateChanged handler failed: "+e.Message);}
            bool due;
            lock(gate){due=previewOn;if(due)previewDue=true;}
            if(due) Wake();
        }
    }
    void Wake()=>Volatile.Read(ref wake).TrySetResult();

    // The keyboards whose key presses matter: lights on, painted, a fade or ripple layer of some strength over at least one light
    // (LightingLayers.NeedsKeys), and at least one light named for a known key. Each such keyboard's light names are logged once per
    // device-list change: facts about the device, never about what was pressed. At most MaxNamesLogged unknown names are listed.
    List<(string DeviceKey,IReadOnlyList<string> LightNames)> WatchedLocked()
    {
        var r=new List<(string,IReadOnlyList<string>)>();
        var shown=Shown;
        foreach(var d in service.Devices)
        {
            if(d.LedNames is not{Count:>0} names||!LightingLayers.NeedsKeys(shown,d)) continue;
            var unknown=KeyPressTable.Unrecognised(names);
            if(namesLogged.Add(d.Key)) log.Write($"{d.Name}: {names.Count-unknown.Count} of {names.Count} light names are known keys; not known: {UnknownText(unknown)}.");
            if(unknown.Count<names.Count) r.Add((d.Key,names));
        }
        return r;
    }

    const int MaxNamesLogged=20;
    static string UnknownText(IReadOnlyList<string> unknown)=>unknown.Count==0?"none"
        :string.Join(", ",unknown.Take(MaxNamesLogged))+(unknown.Count>MaxNamesLogged?$" and {unknown.Count-MaxNamesLogged} more":"");
    static void StopQuietly(IKeyPressSource keys){try{keys.Stop();}catch(Exception){}}

    // The key-press rule, point 7: the source runs only while the host is Running with a session and some keyboard needs key presses. With
    // none it is stopped and the gate forgets every press and held key; with some the gate gets their table and the source is started if it
    // isn't. A start that fails is logged once and tried again at the next call. refresh (every KeyRefresh while started): stop, forget held
    // keys, start again, silently unless Installed changes. A source that was started but is no longer installed (Windows removed the hook,
    // or its thread ended) counts as not started, so it is started again here. Returns whether Installed changed.
    bool Watch(bool refresh=false)
    {
        if(keys is null) return false;
        lock(watchGate)
        {
            List<(string DeviceKey,IReadOnlyList<string> LightNames)> want;
            bool before;
            lock(gate){before=keyHook;want=engineActive&&sessionOk&&status==HostStatus.Running?WatchedLocked():[];}
            if(want.Count==0)
            {
                if(watchStarted){StopQuietly(keys);watchStarted=false;}
                presses.Prepare([]);
                watchFailLogged=false;
                if(watchLogged>0){watchLogged=0;log.Write("Key presses: not watching.");}
            }
            else
            {
                presses.Prepare(want);
                if(watchStarted&&(refresh||!keys.Installed)){StopQuietly(keys);watchStarted=false;presses.Released();}
                if(!watchStarted)
                {
                    string? why=null;
                    try{watchStarted=keys.Start(presses);}catch(Exception e){watchStarted=false;why=e.Message;}
                    if(watchStarted){watchFailLogged=false;refreshAt=time.GetTimestamp()+(long)(KeyRefresh.TotalSeconds*time.TimestampFrequency);}
                    else
                    {
                        StopQuietly(keys);
                        watchLogged=0;
                        if(!watchFailLogged){watchFailLogged=true;log.Write($"Key presses: couldn't watch for key presses ({why??StartRefused}).");}
                    }
                }
                if(watchStarted&&watchLogged!=want.Count){watchLogged=want.Count;log.Write($"Key presses: watching, for {want.Count} keyboard(s).");}
            }
            var now=keys.Installed;
            lock(gate)keyHook=now;
            return now!=before;
        }
    }
    bool RefreshDue(){if(keys is null) return false;lock(watchGate)return watchStarted&&time.GetTimestamp()>=refreshAt;}
    // The source's Installed differs from what the last state said (fix round 1, M4): a hook whose thread died, or that Windows removed, is
    // noticed at the next tick, and the Raise this brings tries it again and makes the state truthful.
    bool KeyHookStale(){if(keys is null) return false;var now=keys.Installed;lock(gate)return now!=keyHook;}
    void StopWatching()
    {
        if(keys is null) return;
        lock(watchGate)
        {
            StopQuietly(keys);
            watchStarted=false;
            presses.Prepare([]);
            if(watchLogged>0){watchLogged=0;log.Write("Key presses: not watching.");}
            lock(gate)keyHook=false;
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stopCts.Token);
        var ct=linked.Token;
        log.Write($"host start (openrgb {options.OpenRgbPath}, startup {options.Startup})");
        Initialize();
        // Ruling P10: the saved shortcut is registered before the start-up delay and the start-up failure, so it works during both.
        // Lighting 5: the hub's dark once before the loop.
        lock(gate){everyday.Changed(settings);RegisterSavedLocked(ShortcutWait);HubDarkLocked();}
        Raise();
        try
        {
            if(startupFailure is not null)
            {
                lock(gate){status=HostStatus.OpenRgbFailed;message=startupFailure;if(hubHost is not null) HubAllowLocked(false);}
                log.Write("host cannot start openrgb: "+startupFailure);
                Raise();
                // The loop never runs, so a press is applied here, woken by the press itself: it flips and saves the switch (ruling P10).
                var woken=Volatile.Read(ref wake);
                while(true)
                {
                    bool pressed;
                    lock(gate)pressed=PressesLocked();
                    if(pressed) Raise();
                    await woken.Task.WaitAsync(ct).ConfigureAwait(false);
                    woken=new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref wake,woken);
                }
            }
            if(options.Startup) await Task.Delay(StartupDelay,time,ct).ConfigureAwait(false);
            var lastSlow=time.GetTimestamp();
            // Lighting 5 (ruling P3): the first launch comes from this scan, before the loop's first turn, and OpenRGB's settings are written on
            // the supervisor's thread at launch: the detector and the listing follow the saved mode first, so mode driven never launches it on.
            lock(gate)HubSyncLocked();
            await CheckConflictsAsync(ct).ConfigureAwait(false);
            var signal=Volatile.Read(ref wake);
            while(true)
            {
                // Read each turn: an effect chosen or stopped takes effect from the next tick. slow stays timed by elapsed time, so the 5 s
                // work keeps its cadence at either tick. FastTick only while a frame can be painted: something animates, the session is
                // connected and the host is Running (fix wave L1); waiting, blocked or given up, the loop keeps Tick.
                TimeSpan tick;
                // Lighting 3b: a key press still live for a fade or ripple layer moves too, and a press (or a frame a state asks for) ends
                // the wait at once. The next wait's signal is made before this turn's work, so a wake during the work isn't lost. A wait
                // ended by the signal cancels its timer here, on the loop's own thread.
                lock(gate)tick=LightingPaint.Animating(Shown,service.Devices,presses.Live(),Now())&&sessionOk&&status==HostStatus.Running?FastTick:Tick;
                using(var turn=CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var delay=Task.Delay(tick,time,turn.Token);
                    if(await Task.WhenAny(delay,signal.Task).ConfigureAwait(false)!=delay) turn.Cancel();
                }
                ct.ThrowIfCancellationRequested();
                signal=new(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref wake,signal);
                var now=time.GetTimestamp();
                var slow=time.GetElapsedTime(lastSlow,now)>=SlowTick;
                if(mouse is not null) lock(gate)mouse.Tick();
                var raise=EverydayWork(slow,out var suspended,out var woke);
                if(suspended) StopWatching();
                if(woke) RaiseWoke();
                if(raise) Raise();
                // Lighting 5: the hub's turn runs every turn, also while a start is pending.
                await HubWorkAsync(slow,ct).ConfigureAwait(false);
                // Fix wave 1, F2: while OpenRGB's start is pending the everyday part above keeps running each turn; nothing is scanned,
                // started again or painted until it resolves, and a start that threw throws here as it did when it was awaited.
                if(starting is{IsCompleted:true} done){starting=null;await done.ConfigureAwait(false);}
                if(starting is null&&startDue){startDue=false;StartOpenRgb(ct);}
                if(slow) lastSlow=now;
                if(starting is null)
                {
                    if(slow) await CheckConflictsAsync(ct).ConfigureAwait(false);
                    TickWork(slow);
                }
                if(RefreshDue()?Watch(refresh:true):KeyHookStale()) Raise();
            }
        }
        catch(OperationCanceledException){}
        finally
        {
            lock(gate){status=HostStatus.Stopping;engineActive=false;}
            StopWatching();
            log.Write("host stopping");
            // Lighting 5: the hand-back comes first, since the window ends the helper 5 s after Stop and OpenRGB's exit wait alone can take
            // 5 s; then OpenRGB's hub detector goes back on, in every mode, so a start by 0.22 finds the hub.
            if(hubHost is not null)
            {
                lock(gate){hubWhy=(HubReasons.NoLighting,null);hubHost.Allow(HubReasons.NoLighting);}
                await hubHost.LetGoAsync().ConfigureAwait(false);
            }
            OpenRgbConfig.HubDetectorOn(options.ConfigDir,log);
            session.Abort();
            try{await engine.StopAsync().ConfigureAwait(false);}catch(Exception e){log.Write("engine stop failed: "+e.Message);}
            log.Write("host stopped");
        }
    }

    // Loads lighting.json. Idempotent; Program calls it before the pipe accepts requests, so no request ever sees or overwrites unloaded defaults.
    public void Initialize()
    {
        lock(gate)
        {
            if(loaded) return;
            loaded=true;
            try
            {
                var read=store.Load();
                settings=read.Settings;
                if(read.Quarantine){note=QuarantineNote;log.Write("lighting.json was invalid and was set aside: "+read.Error);}
                // Ruling AS: a later DeadWare's file is never rewritten, so its settings survive this build; changes last only while this host
                // process runs, since only a new host process reads the file again (polish round 2, item 8).
                // One whose values this build can't accept gives defaults (B7), and its note says so.
                else if(read.Newer)
                {
                    saveAllowed=false;note=read.Error is null?NewerNote:NewerDefaultsNote;
                    log.Write("lighting.json comes from a newer DeadWare; it won't be saved"+(read.Error is null?"":" (using defaults: "+read.Error+")"));
                }
                if(AdoptLocked(null)|AdoptHubLocked(null)) SaveQuietly();
            }
            catch(LinkedPathException e)
            {
                settings=new();saveAllowed=false;note=LinkedNote;
                log.Write("lighting.json's path goes through a link, is redirected or has a final path Windows can't give; using defaults and saving nothing: "+e.Message);
            }
            catch(Exception e)
            {
                settings=new();saveAllowed=false;note=UnreadableNote;
                log.Write("lighting.json could not be read; saving disabled: "+e.Message);
            }
            // Lighting 5 (ruling P3), on every path out of the try: OpenRGB's hub detector and the hub's listing follow the settings read.
            HubSyncLocked();
        }
    }

    // Ruling E: the engine must never run while a blocking conflict exists.
    async Task CheckConflictsAsync(CancellationToken ct)
    {
        IReadOnlyList<LightingConflict> found;
        var scanFailed=false;
        var pidBefore=engine.ProcessId;
        int? lingering;
        lock(gate)lingering=notRestarted?engine.Lingering:null;
        var listed=false;
        try{found=scanner.Scan(pidBefore??lingering,engine.Ended,out listed);}
        catch(Exception e){if(lastScanError!=e.Message){lastScanError=e.Message;log.Write("conflict scan failed: "+e.Message);}found=[];scanFailed=true;listed=false;}
        if(!scanFailed) lastScanError=null;
        // OpenRGB relaunched during the scan: the result may call our own new process foreign. Discard it; the next tick scans again.
        if(!scanFailed&&engine.ProcessId!=pidBefore) return;
        var blocking=found.Where(c=>c.BlocksLighting).ToList();
        bool stop=false,start=false,changed=false;
        lock(gate)
        {
            if(status==HostStatus.Stopping) return;
            leftover=listed;
            // Fix wave 4, A: after notRestarted, the engine's exited OpenRGB still listed counts as the lingering one does.
            var ours=lingering is not null||notRestarted&&listed;
            if(!ours) notRestarted=false;
            var key=scanFailed?"scan failed":string.Join("|",found.Select(c=>c.Description));
            if(key!=lastConflictKey)
            {
                lastConflictKey=key;changed=true;
                log.Write(scanFailed?"conflict gate closed: scan failed":found.Count==0?"conflicts cleared":"conflicts: "+string.Join(" ",found.Select(c=>c.Kind+(c.BlocksLighting?" (blocks)":""))));
            }
            // The loop's own scan replaces the hub step's (fix round 1, I1): until now that one was the newer.
            conflicts=found;hubScanFailed=scanFailed;ownScan=null;
            if(!scanFailed&&!conflictsChecked){conflictsChecked=true;changed=true;}
            if(blocking.Count>0||scanFailed)
            {
                var text=scanFailed?ScanFailedText:string.Join(" ",blocking.Select(c=>c.Description));
                if(engineActive){engineActive=false;stop=true;sessionOk=false;}
                if(status!=HostStatus.Blocked||message!=text){status=HostStatus.Blocked;message=text;changed=true;}
            }
            else if(ours){if(status!=HostStatus.OpenRgbFailed||message!=NotRestartedText){status=HostStatus.OpenRgbFailed;message=NotRestartedText;changed=true;}}
            else if(!engineActive){engineActive=true;start=true;status=HostStatus.Starting;message="";changed=true;}
            // Lighting 5: the worker lets go before OpenRGB is stopped, with the blocking conflict's reason (noLighting for a failed scan).
            if(stop&&hubHost is not null) HubAllowLocked(false);
        }
        if(changed) Raise();
        // The client goes before the OpenRGB it talks to, so nothing waits on a process being killed. The stop keeps OpenRGB's restart
        // history (F4 item 3, whole-branch review D8, D9): it counts towards the give-up, the start once the gate opens again waits out the
        // backoff from the stop, and after a give-up only lighting turned off and on starts OpenRGB again.
        if(stop){session.Abort();await engine.StopAsync(keepHistory:true).ConfigureAwait(false);}
        // F2 (spec: a press while OpenRGB is still starting is applied at once): the first launch can take up to PortWait, so it isn't
        // awaited here; the loop carries on and wakes once it resolves.
        if(start) Launch(ct);
    }
    void Launch(CancellationToken ct){starting=engine.StartAsync(ct);_=starting.ContinueWith(s=>{_=s.Exception;Wake();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}

    // Lighting 5: the hub's loop step, after EverydayWork and before the starting check, so it also runs while an OpenRGB start is pending.
    // First, off the lock, the hub step's own scan: on slow turns while a start is pending (ruling P24), and before a take, that is when the
    // facts would allow holding after a forbid (ruling R30); a turn whose scan did not stand keeps the forbid. Under gate: the saved parts and
    // the stopped pump follow the snapshot, then the facts decide the Allow (Force wins), the detector and the listing follow the mode, and a
    // state is due whenever the hub view differs from the last one raised (ruling P18). Outside it: the state, the readings event, and the one
    // restart for an OpenRGB that still has the hub (rulings P3, P21, P32), whose stop comes after the Allow that forbids holding, sent in this
    // same turn.
    // Task 10: the take-over's turn comes under gate before the Allow, so a step that forbids (Force, Taking) is sent in the same turn; its Scan
    // step's fresh scan is made first, off the lock (amendment 4); its chore (OpenRGB's stop, start or restart) is done last, off the lock, after
    // this turn's Allow; a stop's answer goes back to it, and a back-out or switch-off that issued its restart ends after that (Issued).
    async Task HubWorkAsync(bool slow,CancellationToken ct)
    {
        if(hubHost is null) return;
        bool fresh;
        // Fix wave 4, A: also every turn of the take while the engine's exited OpenRGB is still listed, so the take goes on once it has gone.
        lock(gate)fresh=(takeover!.Step==HubStep.Scan||takeover.Step==HubStep.Take&&leftover)&&status!=HostStatus.Stopping;
        var scanned=fresh?ScanForHub(always:true):slow&&starting is{IsCompleted:false}&&ScanForHub();
        if(!scanned&&!fresh)
        {
            bool take;
            lock(gate)take=hubHost.Force is null&&!hubHost.Allowing&&HubHold.Why(HubFactsLocked()).Reason is null;
            if(take) scanned=ScanForHub();
        }
        bool raise,restart;
        HubChore chore;
        HubReadingsEvent? readings;
        lock(gate)
        {
            var snap=hubHost.Hub.Snapshot;
            if(hubHost.NewParts(settings,snap) is{} parts) SetHubModeLocked(parts,"hub parts saved");
            if(hubHost.StoppedPump(settings,snap) is{} stopped) SetHubModeLocked(stopped,"hub mode settled: driven -> stopped (the pump read 0 RPM)");
            chore=takeover!.Turn(new(snap,HubFactsLocked(),scanned,settings.Hub.Mode),settleMode);
            var held=HubAllowLocked(scanned);
            HubSyncLocked();
            if(hubHost.Moved(snap,service.Hub)) rescanPending=true;
            if(slow&&hubHost.Mode(settings)==HubModes.Driven&&snap.State!=HubStates.Driving) hubHost.Hub.Recheck();
            raise=hubStateDue||!HubHost.Same(hubHost.View(settings,hubWhy),lastHubView);
            hubStateDue=false;
            readings=hubHost.Readings(snap);
            restart=held.Reason==HubReasons.OpenRgbHasIt&&!hubHost.Restarted&&!hubHost.Switching;
            if(restart){hubHost.Restarted=hubHost.Restarting=true;log.Write("OpenRGB still has the hub: restarting it without");}
        }
        if(raise) Raise();
        if(readings is not null) try{HubReadingsChanged?.Invoke(readings);}catch(Exception e){log.Write("HubReadingsChanged handler failed: "+e.Message);}
        // Fix wave 4, C (review D1): a stop that could not end OpenRGB starts none beside it here either.
        if(restart){if(await StopOpenRgbForRestartAsync(ct).ConfigureAwait(false)) StartOpenRgb(ct);else NotRestarted();}
        if(chore!=HubChore.None&&!ct.IsCancellationRequested)
        {
            if(chore==HubChore.StopOpenRgb)
            {
                var exited=await StopOpenRgbForRestartAsync(ct).ConfigureAwait(false);
                lock(gate){takeover.Stopped(exited);if(exited) takeStopped=engine.Ended;}
            }
            else
            {
                // Ruling P19: an OpenRGB of ours that exists, Starting included (launched, its port not open yet), is stopped first. With none,
                // StartOpenRgb alone; a launch still in its floor wait reads the detector by itself, and the engine starts nothing twice.
                // Fix wave 2, E: a stop that could not end OpenRGB starts none beside it; the status says so. Fix wave 4 (A, and C for review D1):
                // nor does a restart with no OpenRGB of ours running while the one the take-over stopped still lingers or is still listed.
                // Fix wave 4c (review D3): a listed leftover counts only when it is the OpenRGB the take-over itself stopped, never one that
                // ended by itself and that the engine is about to start again.
                bool own;
                lock(gate)own=leftover&&takeStopped is{} stopped&&engine.Ended==stopped;
                if(chore==HubChore.Restart&&engine.ProcessId is not null&&!await StopOpenRgbForRestartAsync(ct).ConfigureAwait(false)) NotRestarted();
                else if(chore==HubChore.Restart&&engine.ProcessId is null&&(engine.Lingering is not null||own)) NotRestarted();
                else StartOpenRgb(ct);
            }
        }
        bool ended;
        lock(gate)ended=takeover.Issued();
        if(ended) Raise();
    }

    // The hub step's own scan, in mode driven, off the lock, for the facts only (rulings P24, R30); it stands until CheckConflictsAsync assigns
    // conflicts again. A pending start is when OpenRGB is launched, and its new process would count as another OpenRGB: a result during which
    // our process id changed is dropped, as CheckConflictsAsync drops it. A scan that throws counts as a failed scan. True when a result stands.
    // always (Task 10): the take-over's Scan step, made in mode openrgb too.
    bool ScanForHub(bool always=false)
    {
        lock(gate)if(!always&&hubHost!.Mode(settings)!=HubModes.Driven) return false;
        var pid=engine.ProcessId;
        IReadOnlyList<LightingConflict> found;
        var failed=false;
        var listed=false;
        try{found=scanner.Scan(pid,engine.Ended,out listed);}catch(Exception){found=[];failed=true;listed=false;}
        if(!failed&&engine.ProcessId!=pid) return false;
        lock(gate){ownScan=(found,failed);leftover=listed;}
        return true;
    }

    // The facts as the host knows them now. LaunchedWithHub also holds while the restart for a detector left on is under way, until the
    // relaunched OpenRGB has connected (HubHost.Restarting), and the restart is spent only once it has.
    HubFacts HubFactsLocked()
    {
        var own=ownScan;
        return new(hubHost!.Mode(settings),status==HostStatus.Stopping,everyday.Quiet,engineActive,connectedOnce,status,own?.Found??conflicts,own?.Failed??hubScanFailed,
            service.OpenRgbListsHub,hubHost.Restarting||engine.ProcessId is not null&&hubHost.LaunchedOn,hubHost.Restarted&&!hubHost.Restarting,
            leftover&&takeover!.Step==HubStep.Take);
    }

    // Sends the forbid (Force wins) and returns the facts' own answer. Ruling R30: after a forbid, or before any Allow in this run, holding is
    // allowed only on a turn whose fresh scan stood (scanned); without one the last forbid is kept for this turn.
    (string? Reason,string? Detail) HubAllowLocked(bool scanned)
    {
        var held=HubHold.Why(HubFactsLocked());
        (string? Reason,string? Detail) why=hubHost!.Force is{} force?(force,hubHost.ForceDetail):held;
        if(why.Reason is null&&!hubHost.Allowing&&!scanned) return held;
        hubWhy=why;
        hubHost.Allow(why.Reason,why.Detail);
        return held;
    }

    void HubSyncLocked(){if(hubHost is null) return;hubHost.SyncDetector(settings);service.Hub=hubHost.Listing(settings);}
    void HubDarkLocked()=>hub?.SetDark(!LightsOff.Effective(settings,everyday.Reason(settings)));

    // How the host itself sets the hub mode and its saved parts (the stopped pump, new parts; Task 10's verdict). Unlike Change it adopts the
    // candidate even when the save throws (ruling P17): the mode then holds for this run, and a later successful save of any change writes it.
    // False when the settings refuse the candidate (fix wave 1, D3): nothing changed, and the take-over backs out.
    bool SetHubModeLocked(HubSettings next,string logLine)
    {
        var candidate=Clone(settings);candidate.Hub=next;
        if(LightingSettingsCodec.Validate(candidate) is{} error){log.Write("hub settings not changed: "+error);return false;}
        if(saveAllowed)
        {
            try{store.Save(candidate);}
            catch(Exception e){log.Write("saving the hub mode failed: "+e.Message);}
        }
        settings=candidate;
        log.Write(logLine);
        hubStateDue=true;
        return true;
    }

    // Lighting 5: OpenRGB stopped on purpose, to start again without the hub. The session is dropped as HardwareRescanLocked does, since
    // OnEngineChanged leaves it set on Stopped, and the status says OpenRGB is restarting; engineActive stays true, so the conflict check
    // starts nothing by itself. The stop can last ExitWait: before every such stop the last Allow sent forbids holding, and the wait ends at once
    // on the stop token (fix wave 1, D10), so a Stop request arriving during it reaches the hand-back without waiting for OpenRGB's exit; the
    // engine's own stop goes on and the host's final engine stop waits for it. False when OpenRGB did not exit.
    async Task<bool> StopOpenRgbForRestartAsync(CancellationToken ct)
    {
        lock(gate){session.Abort();sessionOk=false;service.Reset();ForgetPreview();status=HostStatus.Waiting;message=RestartingText;}
        Raise();
        return await engine.StopForRestartAsync(ct).WaitAsync(ct).ConfigureAwait(false);
    }

    void NotRestarted()
    {
        log.Write("OpenRGB did not exit: DeadWare did not start it again");
        lock(gate){if(status==HostStatus.Stopping) return;status=HostStatus.OpenRgbFailed;message=NotRestartedText;engineActive=false;notRestarted=true;}
        Raise();
    }

    // OpenRGB's start after a deliberate stop, as CheckConflictsAsync starts it. Never over a start that is still pending: the loop issues it on
    // the first turn after that one resolved. Not while lighting is blocked (the conflict check starts it once that clears) or stopping.
    void StartOpenRgb(CancellationToken ct)
    {
        if(ct.IsCancellationRequested) return;
        lock(gate)if(!engineActive||status==HostStatus.Stopping) return;
        if(starting is not null){startDue=true;return;}
        Launch(ct);
    }

    void TickWork(bool slow)
    {
        var changed=false;
        PreviewEvent? frame=null;
        var generation=0;
        lock(gate)
        {
            if(!engineActive||status is HostStatus.Blocked or HostStatus.Stopping||engine.Status!=EngineStatus.Running){ForgetPreview();return;}
            if(everyday.Quiet){ForgetPreview();return;}
            try
            {
                if(!sessionOk)
                {
                    ForgetPreview();
                    var now=time.GetTimestamp();
                    if(now<nextConnectAt) return;
                    changed=ConnectLocked();
                    if(!sessionOk) ArmBackoff();
                }
                else
                {
                    if(slow) mouse?.Scan();
                    if(slow||rescanPending){rescanPending=false;changed=RescanLocked();}
                    // Task 9: the session was dropped on purpose; this tick paints nothing (ruling P3), since a paint now would fail the session.
                    if(slow&&mouse?.CableArrived(service.CableKey is not null)==true&&HardwareRescanLocked()) changed=true;
                    else
                    {
                        var t=Now(); // one reading for the paint and the preview
                        var live=presses.Live();
                        service.Paint(Shown,t,live);
                        PaintedLocked();
                        if(mouse?.Poll(settings.Mouse.SleepMinutes)==true) changed=true;
                        frame=PreviewLocked(t,live);generation=previewGeneration;
                    }
                }
            }
            catch(Exception e){ForgetPreview();changed=FailSession(LostText,e);}
        }
        if(changed) Raise();
        if(frame is not null) RaisePreview(frame,generation);
    }

    // The frame to send at tMs, or null: none subscribed, nothing animating, too soon after the last frame, or the same colours as it.
    // While no frame is produced (nothing animating, or no running session) the window shows the state's colours, so the last frame and
    // its time are forgotten then: the first frame after a resume is sent even when it equals the last one (fix wave W1). A due frame
    // (previewDue) skips all three checks, and so does the closing frame on the first tick after movement stops (previewMoving).
    PreviewEvent? PreviewLocked(double tMs,IReadOnlyList<LightPress> live)
    {
        var shown=Shown;
        var moving=previewOn&&LightingPaint.Animating(shown,service.Devices,live,tMs);
        var due=previewOn&&(previewDue||previewMoving&&!moving)&&LightingPaint.ShowsLights(shown,service.Views(shown));
        previewDue=false;
        if(!due&&!moving){ForgetPreview();return null;}
        previewMoving=moving;
        if(!due&&lastPreviewMs is {} last&&tMs-last<PreviewGap.TotalMilliseconds) return null;
        lastPreviewMs=tMs;
        var frame=new PreviewEvent(service.Preview(shown,tMs,live),service.PreviewLights(shown,tMs,live));
        if(!due&&lastPreview is not null&&SameFrame(lastPreview,frame)) return null;
        lastPreview=frame;
        return frame;
    }
    void ForgetPreview(){lastPreview=null;lastPreviewMs=null;previewMoving=false;}
    internal static bool SameFrame(PreviewEvent a,PreviewEvent b)=>Same(a.Colors,b.Colors)&&(a.Lights is null?b.Lights is null:b.Lights is not null&&Same(a.Lights,b.Lights));
    static bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(e=>b.TryGetValue(e.Key,out var v)&&v==e.Value);

    void RaisePreview(PreviewEvent frame,int generation)
    {
        lock(previewGate)
        {
            lock(gate)if(!previewOn||previewGeneration!=generation) return;
            try{PreviewChanged?.Invoke(frame);}catch(Exception e){log.Write("PreviewChanged handler failed: "+e.Message);}
        }
    }

    // Ruling F: after OpenRGB (re)starts, forget what was sent, reconnect, rescan, then paint.
    bool ConnectLocked()
    {
        service.Reset();
        ForgetPreview();
        try
        {
            session.Connect();
            sessionOk=true;
            mouse?.Scan();
            RescanLocked();
            service.Paint(Shown,Now(),presses.Live());
            status=HostStatus.Running;message="";lastSessionError=null;connectDelay=ConnectDelayMin;nextConnectAt=0;
            // Lighting 5: connected once in this run (never cleared); a restart for a detector left on has run its course.
            connectedOnce=true;
            if(hubHost is not null) hubHost.Restarting=false;
            PaintedLocked();
            return true;
        }
        catch(Exception e){return FailSession("Waiting for OpenRGB to accept connections.",e);}
    }

    // Task 9 (amendments 1 and 2): the mouse cable arrived and OpenRGB doesn't list it. One hardware rescan request, then a deliberate
    // reconnect: OpenRGB rebuilds every controller, so the session is dropped, everything sent is forgotten, and the connect branch waits
    // RescanSettle before the existing connect path reads the list again. The status stays Running. A failed request is logged and leaves
    // the session alone. OpenRGB is never restarted here. Returns whether the session was dropped.
    bool HardwareRescanLocked()
    {
        log.Write("mouse cable seen: asking OpenRGB to look for hardware");
        try{session.RequestHardwareRescan();}
        catch(Exception e){log.Write("hardware rescan request failed: "+e.Message);return false;}
        session.Abort();sessionOk=false;service.Reset();ForgetPreview();
        nextConnectAt=time.GetTimestamp()+(long)(RescanSettle.TotalSeconds*time.TimestampFrequency);
        return true;
    }

    void ArmBackoff()
    {
        nextConnectAt=time.GetTimestamp()+(long)(connectDelay.TotalSeconds*time.TimestampFrequency);
        connectDelay=TimeSpan.FromTicks(Math.Min(connectDelay.Ticks*2,ConnectDelayMax.Ticks));
    }

    // Logs and reports a session failure only when it is new, so a wedged OpenRGB cannot spam the log or the window. The connection is
    // dropped too (Ruling AO): the next tick reconnects with a fresh one, and a dead client's read loop never lingers.
    bool FailSession(string text,Exception e)
    {
        sessionOk=false;
        session.Abort();
        var changed=status!=HostStatus.Waiting||message!=text;
        status=HostStatus.Waiting;message=text;
        if(lastSessionError!=e.Message){lastSessionError=e.Message;changed=true;log.Write("openrgb session error: "+e.Message);}
        return changed;
    }

    // After a change: re-key saved entries for devices back under a new key first, so their points are kept, then give the zones still
    // without a point one. The arranged layout is adopted only when the settings with it stay valid and savable (1 MiB): otherwise the
    // layout stays as it was, the reason is logged once, and the live settings stay savable, so later changes aren't refused. One save
    // covers both. On the owner's rig the layout is the built-in picture's (DeskPictures), not an arranged one, over the devices found so
    // far: a saved picture is never left because devices are missing (OpenRGB lists them a few at a time at each start). Only a saved name
    // this build has no picture for is reset: auto-arranged afresh, its desk shape kept, the name cleared.
    // Lighting 4: the mouse's old cable entries are copied under its fixed key before the re-keying and the arrange (AdoptLocked).
    bool RescanLocked()
    {
        service.MouseSaved=AeroxIdentity.HasSaved(settings);
        var changed=service.Rescan();
        if(!changed) return false;
        namesLogged.Clear();
        log.Write($"devices changed: {service.Devices.Count} device(s)");
        var save=AdoptLocked(service.CableKey);
        save|=AdoptHubLocked(service.HubOldKey);
        save|=LightingKeys.Remap(settings,service.Devices.Select(d=>d.Key).ToList());
        var views=service.Views(settings);
        var picture=DeskPictures.Applies(views.Select(v=>v.Name),settings.Layout.Picture);
        DeskLayout arranged;
        if(picture is not null) arranged=DeskPictures.Layout(picture,views);
        else if(settings.Layout.Picture is not null) arranged=DeskGeometry.Reset(settings.Layout,views);
        else arranged=DeskLayouts.AutoArrange(settings.Layout,DeskLayouts.Arrangeable(views));
        if(ReferenceEquals(arranged,settings.Layout)||DeskLayouts.Same(arranged,settings.Layout)) lastArrangeError=null;
        else
        {
            var candidate=Clone(settings);candidate.Layout=arranged;
            var error=LightingSettingsCodec.Validate(candidate);
            if(error is null)
            {
                if(arranged.Picture!=settings.Layout.Picture) log.Write(arranged.Picture is null?"desk picture not known to this build, plain desk instead: "+settings.Layout.Picture:"desk picture applied: "+arranged.Picture);
                settings=candidate;save=true;lastArrangeError=null;
            }
            else if(error!=lastArrangeError){lastArrangeError=error;log.Write("desk layout not arranged: "+error);}
        }
        if(save) SaveQuietly();
        return true;
    }

    // AeroxIdentity.Adopt on a copy, adopted only when the settings with it stay valid and savable. Returns whether anything was copied.
    bool AdoptLocked(string? cableKey)
    {
        if(!AeroxIdentity.CanAdopt(settings,cableKey)) return false;
        var candidate=Clone(settings);
        if(!AeroxIdentity.Adopt(candidate,cableKey)) return false;
        if(LightingSettingsCodec.Validate(candidate) is{} error){log.Write("mouse colours not copied: "+error);return false;}
        settings=candidate;
        log.Write("mouse settings copied to "+AeroxIdentity.Key);
        return true;
    }

    // HubIdentity.Adopt on a copy, the same shape as AdoptLocked.
    bool AdoptHubLocked(string? oldKey)
    {
        if(!HubIdentity.CanAdopt(settings,oldKey)) return false;
        var candidate=Clone(settings);
        if(!HubIdentity.Adopt(candidate,oldKey)) return false;
        if(LightingSettingsCodec.Validate(candidate) is{} error){log.Write("hub colours not copied: "+error);return false;}
        settings=candidate;
        log.Write("hub settings copied to "+HubIdentity.Key);
        return true;
    }

    void SaveQuietly()
    {
        if(!saveAllowed) return;
        try{store.Save(settings);}catch(Exception e){log.Write("saving lighting.json failed: "+e.Message);}
    }

    void OnDeviceListUpdated(){lock(gate)rescanPending=true;}

    void OnEngineChanged()
    {
        var raise=true;
        // Not running any more (or not yet): drop the client before taking the lock, so a call waiting on that OpenRGB fails now and frees
        // the lock, and a dead client's read loop can't spin on a closed socket (Ruling AO).
        if(engine.Status!=EngineStatus.Running) session.Abort();
        lock(gate)
        {
            if(!engineActive||status==HostStatus.Stopping) return;
            switch(engine.Status)
            {
                // sessionOk is cleared whenever OpenRGB stops, so if it is set here a loop tick has already connected to this process: connect
                // once. The connect is bounded like every OpenRGB call, so the supervisor waits here at most a few seconds.
                // While quiet the connect waits for the wake's end, and the tick after it connects at once (nextConnectAt 0).
                case EngineStatus.Running:
                    connectDelay=ConnectDelayMin;nextConnectAt=0;
                    if(!sessionOk&&!everyday.Quiet){ConnectLocked();if(!sessionOk) ArmBackoff();}
                    break;
                case EngineStatus.Restarting:
                    sessionOk=false;status=HostStatus.Waiting;message=RestartingText;break;
                case EngineStatus.Failed:
                    sessionOk=false;
                    if(!File.Exists(options.OpenRgbPath)){status=HostStatus.OpenRgbMissing;message=engine.FailureReason??"OpenRGB.exe was not found.";}
                    else{status=HostStatus.OpenRgbFailed;message=engine.FailureReason??"OpenRGB failed.";}
                    break;
                default:raise=false;break;
            }
        }
        if(raise) Raise();
    }

    public Task<HostMessage> HandleAsync(HostMessage request)
    {
        HostMessage reply;
        var raise=false;
        lock(gate)reply=loaded||request is StopRequest?Apply(request,out raise):new Reply(false,HostReplyCodes.Starting){Id=request.Id};
        if(raise) Raise();
        if(request is StopRequest&&reply is Reply{Ok:true}) try{stopCts.Cancel();}catch(ObjectDisposedException){}
        return Task.FromResult(reply);
    }

    HostMessage Apply(HostMessage request,out bool raise)
    {
        raise=false;
        switch(request)
        {
            case GetStateRequest:return new StateEvent(BuildLocked()){Id=request.Id};
            case StopRequest:
                status=HostStatus.Stopping;message="";raise=true;
                return Ok(request);
            case RescanRequest:
                if(!engineActive||!sessionOk||status!=HostStatus.Running) return Fail(request,"OpenRGB is not running.");
                // While quiet the rescan is left to the wake's end, which rescans anyway.
                if(everyday.Quiet){rescanPending=true;return Ok(request);}
                try{RescanLocked();service.Paint(Shown,Now(),presses.Live());PaintedLocked();raise=true;return Ok(request);}
                catch(Exception e){FailSession(LostText,e);raise=true;return Fail(request,HostReplyCodes.RescanFailed);}
            case SetLightsOnRequest r:return Mutate(request,s=>{s.LightsOn=r.On;return null;},out raise);
            case SetBrightnessRequest r:return Mutate(request,s=>{if(r.Percent is<0 or>100) return "Brightness must be 0 to 100.";s.Brightness=r.Percent;return null;},out raise);
            case SetAllColorsRequest r:
                return Mutate(request,s=>{if(!Rgb.TryParse(r.Color,out _)) return "Colour must be #RRGGBB.";s.DefaultColor=r.Color;s.ZoneColors.Clear();return null;},out raise);
            case SetZoneColorRequest r:return Mutate(request,s=>SetZone(s,r),out raise);
            case ReplaceColorsRequest r:
                return Mutate(request,s=>
                {
                    if(r.ZoneColors is null) return "ZoneColors is missing.";
                    s.DefaultColor=r.DefaultColor;
                    s.ZoneColors=r.ZoneColors.ToDictionary(e=>e.Key,e=>e.Value is null?null!:new ZoneColorEntry{Color=e.Value.Color,ZoneName=e.Value.ZoneName});
                    return null;
                },out raise);
            case SetEffectRequest r:return Mutate(request,s=>SetEffect(s,r),out raise);
            case SetLayoutRequest r:return Mutate(request,s=>SetLayout(s,r),out raise);
            // Raises no state. A new subscription clears the last frame and its time, so its first frame goes at the next paint tick, as before:
            // unlike a raised state (R8) it doesn't wake the loop, since a test pins that the first frame comes with the device already painted.
            case SubscribePreviewRequest r:previewOn=r.On;previewDue=r.On;previewGeneration++;lastPreview=null;lastPreviewMs=null;return Ok(request);
            case SetDeviceTuningRequest r:return Mutate(request,s=>SetDeviceTuning(s,r),out raise);
            case SetDeviceModeRequest r:return Mutate(request,s=>SetDeviceMode(s,r),out raise);
            case SetDeviceKeysRequest r:return Mutate(request,s=>SetDeviceKeys(s,r),out raise,noRoom:true);
            case SetDeviceLayersRequest r:return Mutate(request,s=>SetDeviceLayers(s,r),out raise,noRoom:true);
            case SaveLookRequest r:return Mutate(request,s=>SaveLook(s,r),out raise,noRoom:true);
            case ChooseLookRequest r:return Mutate(request,s=>ChooseLook(s,r),out raise,noRoom:true);
            case RenameLookRequest r:return Mutate(request,s=>RenameLook(s,r),out raise,noRoom:true);
            case DeleteLookRequest r:return Mutate(request,s=>DeleteLook(s,r),out raise,noRoom:true);
            case SetEverydayRequest r:return SetEveryday(r,out raise);
            // Task 8: Mutate's must-send carries the new value out with the next receiver frame; on the cable it waits.
            case SetMouseSleepRequest r:
                if(!MouseSleep.Valid(r.Minutes)) return Fail(request,HostReplyCodes.BadSleep);
                return Mutate(request,s=>{s.Mouse.SleepMinutes=r.Minutes;return null;},out raise);
            // Lighting 5, Task 10: judged from the last scan; they never scan, wait for the hub or await, and the state they cause follows the reply.
            case ReadHubRequest:return HubRequest(request,t=>t.Read(HubAskLocked()),out raise);
            case SetHubModeRequest r:return HubRequest(request,t=>t.SetMode(r.Mode,HubAskLocked(),r.Shown),out raise);
            default:return Fail(request,HostReplyCodes.Unsupported);
        }
    }

    // A hub request: unsupported without a hub; a state is due when the hub view changed (a mode change began, a problem was cleared).
    HostMessage HubRequest(HostMessage request,Func<HubTakeover,string?> ask,out bool raise)
    {
        raise=false;
        if(takeover is null) return Fail(request,HostReplyCodes.Unsupported);
        var before=hubHost!.View(settings,hubWhy);
        if(ask(takeover) is{} why) return Fail(request,why);
        raise=!HubHost.Same(before,hubHost.View(settings,hubWhy));
        return Ok(request);
    }
    // What a hub request is judged by: the conflicts of the newest scan (the hub step's own while it stands, as in HubFactsLocked) and fields
    // written under gate only, so it is safe on the pipe's thread (Task 9 review, M6).
    HubAsk HubAskLocked()
    {
        var own=ownScan;
        return new(everyday.Quiet,saveAllowed,settings.Hub.Mode,own?.Found??conflicts,own?.Failed??hubScanFailed,engineActive&&sessionOk&&status==HostStatus.Running,
            service.TwoHubs,time.GetUtcNow());
    }

    // Step 3a: the device must be one OpenRGB lists now; an entry for one that isn't connected is kept but can't be changed. The edit goes
    // into the device's entry (made if absent), defaults are dropped and an empty entry removed (Normalize); Mutate's validation then gives
    // the plain-words range sentences. A change that changes nothing is still saved and raises a state.
    LightingDevice? Listed(string? key)=>key is null?null:service.Devices.FirstOrDefault(d=>d.Key==key);
    static DeviceTuning Entry(LightingSettings s,string key)=>s.Devices.TryGetValue(key,out var t)&&t is not null?t:s.Devices[key]=new DeviceTuning();
    string? SetDeviceTuning(LightingSettings s,SetDeviceTuningRequest r)
    {
        if(Listed(r.DeviceKey) is null) return HostReplyCodes.UnknownDevice;
        var t=Entry(s,r.DeviceKey);
        if(r.Brightness is {} b) t.Brightness=b;
        if(r.Correction is {} c) t.Correction=new DeviceCorrection{R=c.R,G=c.G,B=c.B};
        if(r.ResetCorrection) t.Correction=null;
        DeviceTunings.Normalize(s);
        return null;
    }
    // A mode the device can't do is refused in plain words (DeviceModes.CantRun); following the desk (Kind null) never is.
    string? SetDeviceMode(LightingSettings s,SetDeviceModeRequest r)
    {
        if(Listed(r.DeviceKey) is not {} device) return HostReplyCodes.UnknownDevice;
        Entry(s,r.DeviceKey).Mode=r.Kind is null?null:new DeviceMode{Kind=r.Kind,Speed=r.Speed};
        DeviceTunings.Normalize(s);
        return r.Kind is null?null:DeviceModes.CantRun(device,r.Kind);
    }

    // Lighting 3b. Each handler: listed, then (only for one that adds something: paint, layers, a save or a choice) a keyboard DeadWare can
    // paint key by key, then its own checks, then the edit with Normalize; the ranges are Mutate's validation (DeviceLayers.ValidateSetup's
    // sentences). Clearing, renaming and deleting work on any listed device, so a keyboard that lost its names can still be cleaned up.
    string? Paintable(string? key,bool adds,out LightingDevice? device)
    {
        device=Listed(key);
        if(device is null) return HostReplyCodes.UnknownDevice;
        return adds&&!KeyMaps.CanPaint(device)?HostReplyCodes.NotPaintable:null;
    }
    // A set larger than the cap is left to validation's sentence; within it, an index at or beyond the light count isn't on this keyboard.
    string? SetDeviceKeys(LightingSettings s,SetDeviceKeysRequest r)
    {
        if(Paintable(r.DeviceKey,r.Keys is{Count:>0},out var device) is{} why) return why;
        var n=KeyMaps.LightCount(device!);
        if(r.Keys is{} keys&&keys.Count<=DeviceLayers.MaxKeys&&keys.Keys.Any(i=>i>=n)) return HostReplyCodes.BadKey;
        Entry(s,r.DeviceKey).Keys=DeviceLayers.Clone(r.Keys);
        DeviceTunings.Normalize(s);
        return null;
    }
    string? SetDeviceLayers(LightingSettings s,SetDeviceLayersRequest r)
    {
        if(Paintable(r.DeviceKey,r.Layers is{Count:>0},out var device) is{} why) return why;
        if(r.Layers is{} layers)
        {
            if(layers.Count>DeviceLayers.MaxLayers) return HostReplyCodes.TooManyLayers;
            if(DeviceLayers.CheckNew(layers) is not null) return HostReplyCodes.BadLayer;
            var n=KeyMaps.LightCount(device!);
            if(layers.Any(l=>l.Lights is{} g&&g.Count<=DeviceLayers.MaxKeys&&g.Any(x=>x is not null&&x.Index>=n))) return HostReplyCodes.BadKey;
        }
        Entry(s,r.DeviceKey).Layers=DeviceLayers.Clone(r.Layers);
        DeviceTunings.Normalize(s);
        return null;
    }
    static string? LookName(string? name,out string trimmed)
    {
        trimmed=name?.Trim()??"";
        return trimmed.Length is<1 or>DeviceLayers.MaxLookName?HostReplyCodes.LookName:null;
    }
    static DeviceMode? CopyMode(DeviceMode? m)=>m is null?null:new(){Kind=m.Kind,Speed=m.Speed};
    // The keyboard's mode, keys and layers, copied into a new look or over the one of that name (Overwrite, keeping its place); it becomes
    // the current look.
    string? SaveLook(LightingSettings s,SaveLookRequest r)
    {
        if(Paintable(r.DeviceKey,true,out _) is{} why) return why;
        if(LookName(r.Name,out var name) is{} bad) return bad;
        var t=Entry(s,r.DeviceKey);
        var look=new DeviceLook{Name=name,Mode=CopyMode(t.Mode),Keys=DeviceLayers.Clone(t.Keys),Layers=DeviceLayers.Clone(t.Layers)};
        var looks=t.Looks??=[];
        if(DeviceLayers.Find(t,name) is{} old)
        {
            if(!r.Overwrite) return HostReplyCodes.LookTaken;
            looks[looks.IndexOf(old)]=look;
        }
        else if(looks.Count>=DeviceLayers.MaxLooks) return HostReplyCodes.TooManyLooks;
        else looks.Add(look);
        t.Look=name;
        DeviceTunings.Normalize(s);
        return null;
    }
    // The keyboard takes copies of the look's mode, keys and layers; a mode it can't run now is still chosen, and painted as Static.
    string? ChooseLook(LightingSettings s,ChooseLookRequest r)
    {
        if(Paintable(r.DeviceKey,true,out _) is{} why) return why;
        var t=Entry(s,r.DeviceKey);
        if(DeviceLayers.Find(t,r.Name) is not{} look) return HostReplyCodes.NoLook;
        t.Mode=CopyMode(look.Mode);t.Keys=DeviceLayers.Clone(look.Keys);t.Layers=DeviceLayers.Clone(look.Layers);t.Look=look.Name;
        DeviceTunings.Normalize(s);
        return null;
    }
    // Another look of the new name (ignoring case) refuses it; the look itself in another case doesn't. The current look follows.
    string? RenameLook(LightingSettings s,RenameLookRequest r)
    {
        if(Paintable(r.DeviceKey,false,out _) is{} why) return why;
        var t=Entry(s,r.DeviceKey);
        if(DeviceLayers.Find(t,r.Name) is not{} look) return HostReplyCodes.NoLook;
        if(LookName(r.NewName,out var name) is{} bad) return bad;
        if(DeviceLayers.Find(t,name) is{} other&&!ReferenceEquals(other,look)) return HostReplyCodes.LookTaken;
        var current=t.Look is not null&&ReferenceEquals(DeviceLayers.Find(t,t.Look),look);
        look.Name=name;
        if(current) t.Look=name;
        DeviceTunings.Normalize(s);
        return null;
    }
    // The keyboard stays as it is; only the current look's name goes when it named this one.
    string? DeleteLook(LightingSettings s,DeleteLookRequest r)
    {
        if(Paintable(r.DeviceKey,false,out _) is{} why) return why;
        var t=Entry(s,r.DeviceKey);
        if(DeviceLayers.Find(t,r.Name) is not{} look) return HostReplyCodes.NoLook;
        if(t.Look is not null&&ReferenceEquals(DeviceLayers.Find(t,t.Look),look)) t.Look=null;
        t.Looks!.Remove(look);
        DeviceTunings.Normalize(s);
        return null;
    }

    // A kind is known by its exact name only (never a number, as Enum.TryParse would take). Params, when sent, replace that kind's set
    // alone; Static has none. A null colour list is kept so validation refuses it rather than reading it as no colours.
    static string? SetEffect(LightingSettings s,SetEffectRequest r)
    {
        if(r.Kind is null||!Enum.GetNames<EffectKind>().Contains(r.Kind,StringComparer.Ordinal)) return "Effect kind is not known.";
        var kind=Enum.Parse<EffectKind>(r.Kind);
        s.Effect.Kind=r.Kind;
        if(r.Params is not {} q) return null;
        if(kind==EffectKind.Static) return "Static takes no effect settings.";
        var p=new EffectParams{Speed=q.Speed,AngleDeg=q.AngleDeg,Spread=q.Spread,Colors=q.Colors is null?null!:new(q.Colors)};
        if(kind==EffectKind.Wave) s.Effect.Wave=p;else if(kind==EffectKind.Breathing) s.Effect.Breathing=p;else s.Effect.Spectrum=p;
        return null;
    }

    // The window's draft, checked, re-keyed for devices back under a new key, then completed with points for live zones it lacks; entries
    // for absent devices are kept. Remap first, as at a rescan (fix wave W7): a draft made before a rescan re-keyed a device would otherwise
    // give the new key fresh points and strand the old ones. The remap sees the draft alone: the live colours and modes are already under
    // the new keys, and Remap refuses a target it finds stored anywhere.
    // On the owner's rig the layout is the picture's, and only the device host sets a layout's picture.
    string? SetLayout(LightingSettings s,SetLayoutRequest r)
    {
        if(settings.Layout.Picture is not null) return "This PC's layout comes from its built-in picture.";
        if(r.Layout?.Picture is not null) return "A window can't choose the desk picture.";
        if(r.Layout is null) return "Layout is missing.";
        var error=DeskLayouts.Validate(r.Layout);
        if(error is not null) return error;
        var draft=new LightingSettings{Layout=DeskLayouts.Clone(r.Layout)};
        LightingKeys.Remap(draft,service.Devices.Select(d=>d.Key).ToList());
        s.Layout=DeskLayouts.AutoArrange(draft.Layout,DeskLayouts.Arrangeable(service.Views(s)));
        return null;
    }

    string? SetZone(LightingSettings s,SetZoneColorRequest r)
    {
        if(r.Color is null){s.ZoneColors.Remove(r.ZoneKey);return null;}
        if(!Rgb.TryParse(r.Color,out _)) return "Colour must be #RRGGBB.";
        if(!LightingKeys.TrySplitZone(r.ZoneKey,out var deviceKey,out var index)) return HostReplyCodes.UnknownZone;
        var zone=service.Devices.FirstOrDefault(d=>d.Key==deviceKey)?.Zones.FirstOrDefault(z=>z.Index==index);
        if(zone is null) return HostReplyCodes.UnknownZone;
        s.ZoneColors[r.ZoneKey]=new ZoneColorEntry{Color=r.Color,ZoneName=zone.Name};
        return null;
    }

    // Edit a copy, validate, save, and only then adopt it: a rejected or unsavable change leaves the live settings untouched.
    // noRoom (lighting 3b's paint, layer and look requests): a change that would take lighting.json past 1 MiB is answered as NoRoom.
    HostMessage Mutate(HostMessage request,Func<LightingSettings,string?> edit,out bool raise,bool noRoom=false)
    {
        raise=false;
        if(Change(edit,noRoom) is{} error) return Fail(request,error);
        raise=true;
        return Ok(request);
    }
    // Mutate's work, shared with the shortcut press: why the change was refused, or null once it is saved, adopted and painted.
    string? Change(Func<LightingSettings,string?> edit,bool noRoom=false)
    {
        var candidate=Clone(settings);
        var error=edit(candidate)??LightingSettingsCodec.Validate(candidate);
        if(noRoom&&error==LightingSettingsCodec.WrittenTooLarge) error=HostReplyCodes.NoRoom;
        if(error is not null) return error;
        if(saveAllowed)
        {
            try{store.Save(candidate);}
            catch(Exception e){log.Write("saving lighting.json failed: "+e.Message);return HostReplyCodes.CouldntSave;}
        }
        // Fix wave 1, F3: a must-send only for an edit that changes the receiver mouse's frame, or its sleep time (sent with a frame).
        var shownBefore=Shown;
        var sleepBefore=settings.Mouse.SleepMinutes;
        settings=candidate;
        HubDarkLocked();
        if(mouse is not null&&(sleepBefore!=settings.Mouse.SleepMinutes||service.MouseFrameDiffers(shownBefore,Shown,Now()))) mouse.Must();
        if(!LightingPaint.Animating(Shown,service.Devices,presses.Live(),Now())) ForgetPreview();
        if(engineActive&&sessionOk&&status==HostStatus.Running&&!everyday.Quiet)
        {
            try
            {
                // OpenRGB announced a device-list change the next tick hasn't read yet (F39): read it first, so this paint reaches each device
                // at its new index, never a stale one. OpenRGB.NET 3.1.1 never raises that event (O21), so with it the 5 s rescan is what
                // finds a change, and a change made in between still meets the old indexes.
                if(rescanPending){rescanPending=false;RescanLocked();}
                service.Paint(Shown,Now(),presses.Live());
                PaintedLocked();
            }
            catch(Exception e){FailSession(LostText,e);}
        }
        return null;
    }
    // Lighting 4, everyday: one loop turn's work, ahead of TickWork so it is never behind its early return while OpenRGB isn't running.
    // Presses flip the saved switch through Change; idle and lock change only the reason. When the reason or the switch changed, live
    // presses go, and the state that follows (raised by the caller, outside the lock) works out the hook again. Returns whether one is due.
    // Task 5: a suspend clears live presses (suspended: the caller stops the hook, outside the lock); suspend and resume are logged and raise
    // a state; the first turn at least WakeDelay after the first resume ends the wake (woke: the caller raises Woke), before the readings,
    // so the reason is worked out again at once and the TickWork that follows rescans and paints.
    bool EverydayWork(bool slow,out bool suspended,out bool woke)
    {
        suspended=woke=false;
        if(signals is null&&input is null) return false;
        lock(gate)
        {
            var raise=slow&&shortcutRetry&&RegisterSavedLocked(RetryWait);
            var (sleeps,resumes)=everyday.TakePower();
            if(sleeps>0){suspended=true;presses.Clear();log.Write("suspend");raise=true;}
            if(resumes>0){log.Write("resume");raise=true;}
            if(everyday.WakeDue(WakeDelay)){WakeEndLocked();woke=true;raise=true;}
            everyday.Observe(settings,input);
            raise|=PressesLocked();
            // The effective lights changed: the hub's dark follows here too, which runs while OpenRGB is away (a press during a restart).
            if(everyday.Changed(settings)){presses.Clear();mouse?.Must();HubDarkLocked();raise=true;}
            return raise;
        }
    }

    // The wake's end: the one place where everything remembered about every device is forgotten after sleep, as after a reconnect. The
    // next TickWork rescans and paints, or connects when the session was lost across sleep; its first paint is logged (ruling P17).
    // Lighting 5: the hub resumes here, and HubWork stops forbidding with waking in this same loop turn, since quiet has just ended.
    void WakeEndLocked(){service.Reset();mouse?.Reopen();mouse?.Battery.Invalidate();ForgetPreview();rescanPending=true;wakePaint=true;hub?.Resume();}
    void PaintedLocked(){if(!wakePaint) return;wakePaint=false;log.Write($"wake repaint done: {service.Devices.Count} devices");}
    void RaiseWoke(){try{Woke?.Invoke();}catch(Exception e){log.Write("Woke handler failed: "+e.Message);}}

    // Each pending press flips the saved switch, unless it only ends an idle spell (EverydayState.PressTarget), which saves nothing.
    // Returns whether any press came.
    bool PressesLocked()
    {
        var n=everyday.TakePresses();
        for(var i=0;i<n;i++)
        {
            var target=everyday.PressTarget(settings,out var back);
            var error=target==settings.LightsOn?null:Change(s=>{s.LightsOn=target;return null;});
            log.Write("shortcut pressed: "+(error is not null?"not changed ("+error+")":back?"back from idle":target?"lights on":"lights off"));
        }
        return n>0;
    }

    // The saved shortcut, registered at start (ruling P10) and, after no answer in time, again on slow work. Not without a watcher, nor while
    // the watcher reports a problem of its own (the state's EverydayProblem carries that). With no shortcut saved only a retry asks: it
    // clears one Windows may hold from a request answered late (fix wave 1, F5). Returns whether ShortcutProblem changed.
    bool RegisterSavedLocked(TimeSpan wait)
    {
        var retry=shortcutRetry;
        shortcutRetry=false;
        if(signals is null||signals.Problem is not null) return false;
        var saved=settings.Everyday.Shortcut;
        if(saved is null&&!retry) return false;
        var before=shortcutProblem;
        var text=Shortcuts.Text(saved);
        switch(signals.SetShortcut(saved,wait))
        {
            case true:shortcutProblem=null;log.Write(saved is null?"shortcut cleared":"shortcut registered: "+text);break;
            case false:shortcutProblem=saved is null?null:Shortcuts.TakenText(saved);log.Write("shortcut refused: "+text);break;
            default:
                shortcutProblem=HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow);shortcutRetry=true;
                if(before is null) log.Write("shortcut not answered in time: "+text);
                break;
        }
        return shortcutProblem!=before;
    }

    // Ruling P14's refusal of a shortcut change when there is no watcher at all; the real host always has one, so only tests meet it.
    public const string NoWatcherText="Shortcut and lock are unavailable: this lighting helper doesn't watch for them.";
    // setEveryday: checked, then a changed shortcut (or the saved one again while it has a problem) is registered before anything is saved,
    // and the old one is asked for again when Windows refuses the new one, gives no answer in time, or the save fails. Then it is saved as
    // any change. Without a watcher, or with one that reports a problem, a shortcut change is refused in its words while the other rules are
    // still saved (ruling P14). The wait for the watcher's answer (ShortcutWait) is under the lock, as every request is.
    HostMessage SetEveryday(SetEverydayRequest r,out bool raise)
    {
        raise=false;
        if(r.Everyday is null||Everydays.Validate(r.Everyday) is not null) return Fail(r,HostReplyCodes.BadEveryday);
        var old=settings.Everyday.Shortcut;
        var wanted=r.Everyday.Shortcut;
        var rules=Everydays.Clone(r.Everyday);
        string? refused=null;
        var registered=false;
        if(wanted!=old)
        {
            if(signals is null||signals.Problem is not null){refused=signals?.Problem??NoWatcherText;rules.Shortcut=old;}
            else switch(signals.SetShortcut(wanted,ShortcutWait))
            {
                case true:registered=true;break;
                case false:raise=RestoreLocked(old);return Fail(r,wanted is null?HostReplyCodes.ShortcutSlow:Shortcuts.TakenText(wanted));
                // The watcher withdraws a request it didn't take in time, but one it took and answered late may already be applied (fix
                // wave 1, F5): the problem is shown and slow work registers the saved shortcut again, which restores the match either way.
                default:
                {
                    var before=shortcutProblem;
                    shortcutProblem=HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow);shortcutRetry=true;
                    log.Write("shortcut not answered in time: "+Shortcuts.Text(wanted));
                    raise=shortcutProblem!=before;
                    return Fail(r,HostReplyCodes.ShortcutSlow);
                }
            }
        }
        // Fix round 1, I1: the same shortcut while it has a problem is only tried again: true clears the problem, anything else keeps it,
        // and the other rules are saved either way.
        var tried=wanted==old&&shortcutProblem is not null&&RegisterSavedLocked(ShortcutWait);
        var reply=Mutate(r,s=>{s.Everyday=rules;return null;},out raise);
        if(reply is Reply{Ok:false}){raise=tried|(registered&&RestoreLocked(old));return reply;}
        if(registered){shortcutProblem=null;shortcutRetry=false;log.Write(wanted is null?"shortcut cleared":"shortcut registered: "+Shortcuts.Text(wanted));}
        return refused is null?reply:Fail(r,refused);
    }
    // The old shortcut asked for again after the new one was refused or its save failed. When that is refused too, or not answered, the
    // state says so (fix round 1, M1); an unanswered one is retried on slow work. Returns whether ShortcutProblem changed.
    bool RestoreLocked(Shortcut? old)
    {
        var before=shortcutProblem;
        switch(signals!.SetShortcut(old,ShortcutWait))
        {
            case true:break;
            case false:if(old is not null) shortcutProblem=Shortcuts.TakenText(old);log.Write("shortcut refused: "+Shortcuts.Text(old));break;
            default:shortcutProblem=HostReplyCodes.Explain(HostReplyCodes.ShortcutSlow);shortcutRetry=true;log.Write("shortcut not answered in time: "+Shortcuts.Text(old));break;
        }
        return shortcutProblem!=before;
    }

    static Reply Ok(HostMessage r)=>new(true,null){Id=r.Id};
    static Reply Fail(HostMessage r,string why)=>new(false,why){Id=r.Id};

    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed) return;disposed=true;}
        try{stopCts.Cancel();}catch(ObjectDisposedException){}
        if(signals is not null){signals.ShortcutPressed-=onPress;signals.LockChanged-=onLock;signals.Suspending-=onSuspend;signals.Resumed-=onResume;}
        engine.Changed-=OnEngineChanged;
        session.DeviceListUpdated-=OnDeviceListUpdated;
        if(hub is not null) hub.Changed-=Wake;
        await engine.DisposeAsync().ConfigureAwait(false);
        session.Dispose();
    }
}
