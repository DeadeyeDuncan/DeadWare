using DeadWare.Core;

namespace DeadWare.Devices;

internal sealed record HostOptions(string OpenRgbPath,string ConfigDir,string LightingPath,string LogDir,bool Startup,string Sid,int Session);

internal interface IConflictScanner { IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid); }

// Orchestrates the host: settings, conflict gate, OpenRGB engine, device painting and the window's requests.
// Concurrency: ONE lock (gate) guards every touch of settings, the device service, the session flag and the status.
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
    public static readonly TimeSpan Tick=TimeSpan.FromMilliseconds(500),SlowTick=TimeSpan.FromSeconds(5),StartupDelay=TimeSpan.FromSeconds(10);
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
    readonly CancellationTokenSource stopCts=new();
    readonly object gate=new();
    LightingSettings settings=new();
    // conflictsChecked: a conflict scan has completed since the host started (polish item 2); it stays set.
    bool saveAllowed=true,engineActive,sessionOk,rescanPending,disposed,loaded,conflictsChecked;
    string? note,lastConflictKey,lastSessionError,lastScanError;
    readonly object raiseGate=new();
    long nextConnectAt;
    TimeSpan connectDelay=ConnectDelayMin;
    HostStatus status=HostStatus.Starting;
    string message="";
    IReadOnlyList<LightingConflict> conflicts=[];
    public event Action<HostState>? StateChanged;

    // startupFailure: set when the host could not prepare OpenRGB's config folder; it reports OpenRgbFailed and never starts OpenRGB.
    // callClock: the clock of the bound on OpenRGB calls; real time unless a test supplies one.
    public DeviceHost(HostOptions options,LightingStore store,OpenRgbEngine engine,Func<IOpenRgbSession> sessionFactory,IConflictScanner conflicts,TimeProvider time,HostLog log,string? startupFailure=null,TimeProvider? callClock=null)
    {
        this.options=options;this.store=store;this.engine=engine;scanner=conflicts;this.time=time;this.log=log;this.startupFailure=startupFailure;
        session=new BoundedSession(sessionFactory(),callClock);
        service=new(session,time,log);
        session.DeviceListUpdated+=OnDeviceListUpdated;
        engine.Changed+=OnEngineChanged;
    }

    public HostState State{get{lock(gate)return BuildLocked();}}
    HostState BuildLocked()
    {
        var text=note is null?message:message.Length==0?note:message+" "+note;
        return new(status,text,Environment.IsPrivilegedProcess,conflicts,service.Views(settings),Clone(settings),conflictsChecked);
    }
    static LightingSettings Clone(LightingSettings s)=>new()
    {
        Version=s.Version,LightsOn=s.LightsOn,Brightness=s.Brightness,DefaultColor=s.DefaultColor,
        ZoneColors=(s.ZoneColors??new()).ToDictionary(e=>e.Key,e=>new ZoneColorEntry{Color=e.Value.Color,ZoneName=e.Value.ZoneName}),
        HardwareModes=new(s.HardwareModes??new()),
    };
    void Raise()
    {
        // Build and deliver under one gate: concurrent raises reach subscribers in build order, so the last delivered state is the newest.
        lock(raiseGate)
        {
            HostState state;
            lock(gate)state=BuildLocked();
            try{StateChanged?.Invoke(state);}catch(Exception e){log.Write("StateChanged handler failed: "+e.Message);}
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,stopCts.Token);
        var ct=linked.Token;
        log.Write($"host start (openrgb {options.OpenRgbPath}, startup {options.Startup})");
        Initialize();
        Raise();
        try
        {
            if(startupFailure is not null)
            {
                lock(gate){status=HostStatus.OpenRgbFailed;message=startupFailure;}
                log.Write("host cannot start openrgb: "+startupFailure);
                Raise();
                await Task.Delay(Timeout.InfiniteTimeSpan,ct).ConfigureAwait(false);
                return;
            }
            if(options.Startup) await Task.Delay(StartupDelay,time,ct).ConfigureAwait(false);
            var lastSlow=time.GetTimestamp();
            await CheckConflictsAsync(ct).ConfigureAwait(false);
            while(true)
            {
                await Task.Delay(Tick,time,ct).ConfigureAwait(false);
                var now=time.GetTimestamp();
                var slow=time.GetElapsedTime(lastSlow,now)>=SlowTick;
                if(slow){lastSlow=now;await CheckConflictsAsync(ct).ConfigureAwait(false);}
                TickWork(slow);
            }
        }
        catch(OperationCanceledException){}
        finally
        {
            lock(gate){status=HostStatus.Stopping;engineActive=false;}
            log.Write("host stopping");
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
        }
    }

    // Ruling E: the engine must never run while a blocking conflict exists.
    async Task CheckConflictsAsync(CancellationToken ct)
    {
        IReadOnlyList<LightingConflict> found;
        var scanFailed=false;
        var pidBefore=engine.ProcessId;
        try{found=scanner.Scan(pidBefore);}
        catch(Exception e){if(lastScanError!=e.Message){lastScanError=e.Message;log.Write("conflict scan failed: "+e.Message);}found=[];scanFailed=true;}
        if(!scanFailed) lastScanError=null;
        // OpenRGB relaunched during the scan: the result may call our own new process foreign. Discard it; the next tick scans again.
        if(!scanFailed&&engine.ProcessId!=pidBefore) return;
        var blocking=found.Where(c=>c.BlocksLighting).ToList();
        bool stop=false,start=false,changed=false;
        lock(gate)
        {
            if(status==HostStatus.Stopping) return;
            var key=scanFailed?"scan failed":string.Join("|",found.Select(c=>c.Description));
            if(key!=lastConflictKey)
            {
                lastConflictKey=key;changed=true;
                log.Write(scanFailed?"conflict gate closed: scan failed":found.Count==0?"conflicts cleared":"conflicts: "+string.Join(" ",found.Select(c=>c.Kind+(c.BlocksLighting?" (blocks)":""))));
            }
            conflicts=found;
            if(!scanFailed&&!conflictsChecked){conflictsChecked=true;changed=true;}
            if(blocking.Count>0||scanFailed)
            {
                var text=scanFailed?ScanFailedText:string.Join(" ",blocking.Select(c=>c.Description));
                if(engineActive){engineActive=false;stop=true;sessionOk=false;}
                if(status!=HostStatus.Blocked||message!=text){status=HostStatus.Blocked;message=text;changed=true;}
            }
            else if(!engineActive){engineActive=true;start=true;status=HostStatus.Starting;message="";changed=true;}
        }
        if(changed) Raise();
        // The client goes before the OpenRGB it talks to, so nothing waits on a process being killed. The stop keeps OpenRGB's restart
        // history (F4 item 3, whole-branch review D8, D9): it counts towards the give-up, the start once the gate opens again waits out the
        // backoff from the stop, and after a give-up only lighting turned off and on starts OpenRGB again.
        if(stop){session.Abort();await engine.StopAsync(keepHistory:true).ConfigureAwait(false);}
        if(start) await engine.StartAsync(ct).ConfigureAwait(false);
    }

    void TickWork(bool slow)
    {
        var changed=false;
        lock(gate)
        {
            if(!engineActive||status is HostStatus.Blocked or HostStatus.Stopping||engine.Status!=EngineStatus.Running) return;
            try
            {
                if(!sessionOk)
                {
                    var now=time.GetTimestamp();
                    if(now<nextConnectAt) return;
                    changed=ConnectLocked();
                    if(!sessionOk) ArmBackoff();
                }
                else
                {
                    if(slow||rescanPending){rescanPending=false;changed=RescanLocked();}
                    service.Paint(settings);
                }
            }
            catch(Exception e){changed=FailSession(LostText,e);}
        }
        if(changed) Raise();
    }

    // Ruling F: after OpenRGB (re)starts, forget what was sent, reconnect, rescan, then paint.
    bool ConnectLocked()
    {
        service.Reset();
        try
        {
            session.Connect();
            sessionOk=true;
            RescanLocked();
            service.Paint(settings);
            status=HostStatus.Running;message="";lastSessionError=null;connectDelay=ConnectDelayMin;nextConnectAt=0;
            return true;
        }
        catch(Exception e){return FailSession("Waiting for OpenRGB to accept connections.",e);}
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

    bool RescanLocked()
    {
        var changed=service.Rescan();
        if(!changed) return false;
        log.Write($"devices changed: {service.Devices.Count} device(s)");
        if(LightingKeys.Remap(settings,service.Devices.Select(d=>d.Key).ToList())) SaveQuietly();
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
                case EngineStatus.Running:connectDelay=ConnectDelayMin;nextConnectAt=0;if(!sessionOk) ConnectLocked();if(!sessionOk) ArmBackoff();break;
                case EngineStatus.Restarting:
                    sessionOk=false;status=HostStatus.Waiting;message="OpenRGB is restarting. Lighting will resume in a moment.";break;
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
                try{RescanLocked();service.Paint(settings);raise=true;return Ok(request);}
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
            default:return Fail(request,HostReplyCodes.Unsupported);
        }
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
    HostMessage Mutate(HostMessage request,Func<LightingSettings,string?> edit,out bool raise)
    {
        raise=false;
        var candidate=Clone(settings);
        var error=edit(candidate)??LightingSettingsCodec.Validate(candidate);
        if(error is not null) return Fail(request,error);
        if(saveAllowed)
        {
            try{store.Save(candidate);}
            catch(Exception e){log.Write("saving lighting.json failed: "+e.Message);return Fail(request,HostReplyCodes.CouldntSave);}
        }
        settings=candidate;
        if(engineActive&&sessionOk&&status==HostStatus.Running)
        {
            try
            {
                // OpenRGB announced a device-list change the next tick hasn't read yet (F39): read it first, so this paint reaches each device
                // at its new index, never a stale one. OpenRGB.NET 3.1.1 never raises that event (O21), so with it the 5 s rescan is what
                // finds a change, and a change made in between still meets the old indexes.
                if(rescanPending){rescanPending=false;RescanLocked();}
                service.Paint(settings);
            }
            catch(Exception e){FailSession(LostText,e);}
        }
        raise=true;
        return Ok(request);
    }
    static Reply Ok(HostMessage r)=>new(true,null){Id=r.Id};
    static Reply Fail(HostMessage r,string why)=>new(false,why){Id=r.Id};

    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed) return;disposed=true;}
        try{stopCts.Cancel();}catch(ObjectDisposedException){}
        engine.Changed-=OnEngineChanged;
        session.DeviceListUpdated-=OnDeviceListUpdated;
        await engine.DisposeAsync().ConfigureAwait(false);
        session.Dispose();
    }
}
