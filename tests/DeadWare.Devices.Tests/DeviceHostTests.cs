using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

public sealed class DeviceHostTests : IAsyncLifetime
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-host-"+Guid.NewGuid().ToString("N"));
    readonly string exe,lightingPath;
    readonly Clock time=new();
    readonly FakeRunner runner=new();
    readonly FakeOpenRgbSession session=FakeOpenRgbSession.FromFixture();
    readonly HostLog log;
    readonly List<LightingConflict> conflicts=new();
    readonly List<Hold> holds=new();
    int scans;
    Hold? scanHold; // once set, conflict scans park the host loop in the scanner until it is released
    readonly string zoneKey=LightingKeys.Zone(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"),0);
    DeviceHost? host;
    Task? run;

    public DeviceHostTests()
    {
        Directory.CreateDirectory(dir);
        exe=Path.Combine(dir,"OpenRGB.exe");File.WriteAllText(exe,"");
        lightingPath=Path.Combine(dir,"lighting.json");
        log=new HostLog(Path.Combine(dir,"logs"));
    }
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync()
    {
        lock(holds)foreach(var hold in holds)hold.Release(); // nothing a test parked outlives it
        if(host is not null){try{await host.HandleAsync(new StopRequest());if(run is not null)await run.WaitAsync(TimeSpan.FromSeconds(5));}catch{}await host.DisposeAsync();}
        log.Dispose();try{Directory.Delete(dir,true);}catch{}
    }

    sealed class FakeProcess(int id):IRunningProcess
    {
        readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Killed;
        public int Id=>id;
        public bool HasExited=>exit.Task.IsCompleted;
        public Task WaitForExitAsync(CancellationToken token)=>exit.Task.WaitAsync(token);
        public void KillTree(){Killed=true;exit.TrySetResult();}
        public void Exit()=>exit.TrySetResult();
        public void Dispose(){}
    }
    sealed class FakeRunner:IProcessRunner
    {
        public readonly List<FakeProcess> Started=new();
        public IRunningProcess Start(ProcessStartInfo info){lock(Started){var p=new FakeProcess(100+Started.Count);Started.Add(p);return p;}}
        public int Count{get{lock(Started)return Started.Count;}}
        public FakeProcess At(int i){lock(Started)return Started[i];}
    }
    sealed class Scanner(Func<int?,IReadOnlyList<LightingConflict>> f):IConflictScanner{public IReadOnlyList<LightingConflict> Scan(int? pid)=>f(pid);}
    // Parks each thread that reaches it until the test releases it. A parked thread gives up after the real-time cap, so a failing test
    // cannot hang the run.
    sealed class Hold
    {
        readonly ManualResetEventSlim open=new(false);
        int parked;
        public bool Parked=>Volatile.Read(ref parked)>0;
        public void Park(){Interlocked.Increment(ref parked);open.Wait(RealTimeCap);}
        public void Release()=>open.Set();
    }
    Hold NewHold(){var hold=new Hold();lock(holds)holds.Add(hold);return hold;}
    static readonly LightingConflict DeadLight=new(ConflictKind.DeadLight,"DeadLight is running.",true);

    // beforeHost gets the engine before the host subscribes to it, so a handler it adds runs ahead of the host's own.
    DeviceHost Build(bool startup=false,string? exePath=null,Action<OpenRgbEngine>? beforeHost=null,Func<string,DeadWare.UpdateCore.LocationProtection>? folderCheck=null)
    {
        var engine=new OpenRgbEngine(exePath??exe,@"C:\cfg",runner,(_,_)=>Task.FromResult(true),time,log,folderCheck);
        beforeHost?.Invoke(engine);
        var scanner=new Scanner(_=>{Interlocked.Increment(ref scans);Volatile.Read(ref scanHold)?.Park();lock(conflicts)return conflicts.ToList();});
        var options=new HostOptions(exePath??exe,@"C:\cfg",lightingPath,Path.Combine(dir,"logs"),startup,"S-1-5-21-1",1);
        return host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,scanner,time,log);
    }
    void Start(DeviceHost h)=>run=h.RunAsync(CancellationToken.None);
    // Fake time AdvanceUntil may move without its condition before it fails, so a host cadence that slows down fails the test instead of
    // being stepped past. A caller that expects a longer wait raises it.
    static readonly TimeSpan FakeTimeLimit=TimeSpan.FromSeconds(30);
    // Moves fake time by exactly total, for checks that something has NOT happened yet. Waiting for an effect after it is only safe when
    // the timer behind that effect was armed before the advance began: the host and engine arm most timers on thread-pool continuations,
    // so on a loaded machine a fixed advance can finish before the timer exists, and then nothing ever fires it. Use Elapse or AdvanceUntil.
    async Task Advance(TimeSpan total,TimeSpan? step=null)
    {
        var s=step??TimeSpan.FromMilliseconds(500);
        for(var t=TimeSpan.Zero;t<total;t+=s){time.Advance(s);await Task.Delay(5);}
    }
    // Moves fake time by exactly delay once the host waits on a timer due that far ahead, so that timer fires and nothing is skipped.
    async Task Elapse(TimeSpan delay)
    {
        var clock=Stopwatch.StartNew();
        while(!time.Waiting(delay))
        {
            if(clock.Elapsed>RealTimeCap) Assert.Fail($"nothing waited {delay.TotalSeconds:0.#} s within {RealTimeCap.TotalSeconds:0} s");
            await Task.Delay(1);
        }
        time.Advance(delay);
    }
    // Steps fake time until the condition holds. The clock moves one step (at least a tick) at a time and only while the host loop waits on
    // its next tick, so the loop has done each tick's work, and armed what comes next, before the clock moves on, however late the thread
    // pool runs it. Fails once limit (FakeTimeLimit unless raised) of fake time has passed without the condition and work still queued has
    // had RealTimeCap to finish, or after RealTimeCap of real time in which the loop never waited on its tick. Returns the fake time moved.
    async Task<TimeSpan> AdvanceUntil(Func<bool> cond,TimeSpan? step=null,TimeSpan? limit=null,[CallerArgumentExpression(nameof(cond))] string what="")
    {
        var s=step??DeviceHost.Tick;
        var max=limit??FakeTimeLimit;
        var moved=TimeSpan.Zero;
        var idle=Stopwatch.StartNew();
        while(!cond())
        {
            if(moved<max&&time.Waiting(DeviceHost.Tick)){time.Advance(s);moved+=s;idle.Restart();}
            else if(idle.Elapsed>RealTimeCap)
                Assert.Fail(moved>=max
                    ?$"condition not reached within the {max.TotalSeconds:0.#} s fake-time limit: {what}"
                    :$"the host loop did not wait on its tick for {RealTimeCap.TotalSeconds:0} s (after {moved.TotalSeconds:0.#} s of fake time): {what}");
            await Task.Delay(1);
        }
        return moved;
    }
    async Task<DeviceHost> Running()
    {
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        return h;
    }
    static async Task<Reply> Send(DeviceHost h,HostMessage m)=>Assert.IsType<Reply>(await h.HandleAsync(m));
    Rgb[] LastLeds(int index)=>session.Leds.Last(l=>l.Index==index).Colors;

    [Fact]
    public async Task Blocking_conflict_reports_Blocked_and_never_starts_engine()
    {
        conflicts.Add(DeadLight);
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        Assert.Contains("DeadLight is running.",h.State.Message);
        await Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(0,runner.Count);
        Assert.Single(h.State.Conflicts);
    }

    [Fact]
    public async Task Blocked_host_starts_engine_once_the_conflict_clears()
    {
        conflicts.Add(DeadLight);
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(1,runner.Count);
    }

    [Fact]
    public async Task Conflict_appearing_while_running_stops_engine_and_blocks()
    {
        var h=await Running();
        lock(conflicts)conflicts.Add(DeadLight);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(0).Killed); // the host publishes Blocked before it stops the engine
        var leds=session.Leds.Count;
        await Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(leds,session.Leds.Count); // nothing is painted while blocked
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(2,runner.Count);
    }

    [Fact]
    public async Task Startup_flag_waits_ten_seconds_before_scanning()
    {
        var h=Build(startup:true);Start(h); // RunAsync arms the startup delay before Start returns, so these fixed advances are exact
        await Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0,Volatile.Read(ref scans));
        Assert.Equal(0,runner.Count);
        await Advance(TimeSpan.FromSeconds(1));
        await Until(()=>Volatile.Read(ref scans)>0);
        await Until(()=>h.State.Status==HostStatus.Running);
    }

    // Ruling AM through the host: an elevated host whose OpenRGB folder is refused reports OpenRgbFailed with the remedy, never starting it.
    [Fact]
    public async Task Refused_openrgb_folder_reports_OpenRgbFailed_with_the_remedy()
    {
        var h=Build(folderCheck:_=>new DeadWare.UpdateCore.LocationProtection(false,"writable"));Start(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal(OpenRgbEngine.UnprotectedFolderText,h.State.Message);
        Assert.Equal(0,runner.Count);
    }

    [Fact]
    public async Task Missing_openrgb_reports_OpenRgbMissing()
    {
        var h=Build(exePath:Path.Combine(dir,"nope","OpenRGB.exe"));Start(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbMissing);
        Assert.Contains("not found",h.State.Message);
        Assert.Equal(0,runner.Count);
    }

    // Ruling F after a crash: the host resets, connects once, rescans and repaints for the relaunched OpenRGB. It can connect two ways:
    // its handler for the engine's Running event, or a loop tick that finds the engine Running while the session is down. Each test holds
    // one of them still so the other goes first, and either way there is exactly one connect.
    [Fact]
    public Task Engine_restart_resets_reconnects_rescans_and_repaints()=>Relaunch(loopFirst:false);

    [Fact]
    public Task Engine_restart_connects_once_when_the_loop_reconnects_first()=>Relaunch(loopFirst:true);

    async Task Relaunch(bool loopFirst)
    {
        session.Controllers[3]=session.Controllers[3] with{ActiveMode=2}; // not in Direct, so the host must switch it
        OpenRgbEngine engine=null!;
        bool Relaunched()=>runner.Count==2&&engine.Status==EngineStatus.Running;
        var hearing=NewHold();
        var heard=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Runs ahead of the host's handler on the engine's supervisor thread: parked, it keeps the host from hearing the relaunch is Running.
        var h=Build(beforeHost:e=>{engine=e;e.Changed+=()=>{if(loopFirst&&Relaunched()) hearing.Park();};});
        engine.Changed+=()=>{if(Relaunched()) heard.TrySetResult();}; // runs after the host's handler: the host has heard
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>session.Leds.Any(l=>l.Index==3));
        var connects=session.Connects;var reads=session.Reads;var leds=session.Leds.Count;
        Assert.Equal(1,session.Modes.Count(m=>m.Index==3));
        var loop=NewHold();
        if(!loopFirst){Volatile.Write(ref scanHold,loop);await AdvanceUntil(()=>loop.Parked);} // the loop parks in its next conflict scan
        runner.At(0).Exit();
        await Until(()=>h.State.Status==HostStatus.Waiting); // the engine arms its relaunch delay before it reports Restarting
        if(loopFirst)
        {
            await AdvanceUntil(()=>hearing.Parked);                    // OpenRGB is back but the host has not heard,
            await AdvanceUntil(()=>h.State.Status==HostStatus.Running); // so a loop tick connects
            hearing.Release();
        }
        else await Elapse(RestartBackoff.Base);                         // the loop is parked, so the host's handler connects
        await heard.Task.WaitAsync(RealTimeCap);
        if(!loopFirst)
        {
            var before=session.Reads;
            loop.Release();                        // the loop drops the scan it began before the relaunch,
            await Until(()=>session.Reads>before); // then its tick finds the session up and only rescans
        }
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.Equal(2,runner.Count);
        Assert.Equal(connects+1,session.Connects);
        Assert.True(session.Reads>reads);
        await AdvanceUntil(()=>session.Leds.Count>leds);
        Assert.Equal(2,session.Modes.Count(m=>m.Index==3)); // only a Reset makes the service issue Direct again for the new process
    }

    [Fact]
    public async Task Engine_failure_reports_OpenRgbFailed_with_reason()
    {
        var h=await Running();
        // Crashes 65 s apart, like a slow crash loop: the fifth comes 260 s after the first, so it fails the engine only because all five
        // fall inside the 10-minute window. Each crash but the last is relaunched after the 4/8/16/32 s backoff.
        for(var i=0;i<RestartBackoff.MaxFailures-1;i++)
        {
            runner.At(i).Exit();
            await Until(()=>h.State.Status==HostStatus.Waiting); // the engine arms its relaunch delay before it reports Restarting
            var moved=await AdvanceUntil(()=>runner.Count>i+1&&h.State.Status==HostStatus.Running,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(65));
            await Advance(TimeSpan.FromSeconds(65)-moved,TimeSpan.FromSeconds(1));
        }
        runner.At(RestartBackoff.MaxFailures-1).Exit();
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Contains("OpenRGB stopped",h.State.Message);
    }

    [Fact]
    public async Task SetAllColors_clears_zone_entries_saves_and_repaints()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetZoneColorRequest(zoneKey,"#00FF00"){Id=1})).Ok);
        var reply=await Send(h,new SetAllColorsRequest("#FF0000"){Id=2});
        Assert.True(reply.Ok);Assert.Equal(2,reply.Id);
        Assert.Empty(h.State.Settings.ZoneColors);
        Assert.Equal("#FF0000",h.State.Settings.DefaultColor);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Equal("#FF0000",saved.DefaultColor);Assert.Empty(saved.ZoneColors);
        await AdvanceUntil(()=>{try{return LastLeds(0).All(c=>c==new Rgb(255,0,0));}catch(InvalidOperationException){return false;}});
        Assert.All(LastLeds(0),c=>Assert.Equal(new Rgb(255,0,0),c));
    }

    [Fact]
    public async Task GetState_answers_with_state_event_carrying_the_request_id()
    {
        var h=await Running();
        var state=Assert.IsType<StateEvent>(await h.HandleAsync(new GetStateRequest{Id=9}));
        Assert.Equal(9,state.Id);
        Assert.Equal(8,state.State.Devices.Count);
    }

    [Fact]
    public async Task SetZoneColor_null_clears_own_colour()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetZoneColorRequest(zoneKey,"#00FF00"))).Ok);
        var entry=Assert.Single(h.State.Settings.ZoneColors);
        Assert.Equal("Corsair DRAM",entry.Value.ZoneName);
        Assert.True(h.State.Devices[0].Zones[0].OwnColor);
        Assert.True((await Send(h,new SetZoneColorRequest(zoneKey,null))).Ok);
        Assert.Empty(h.State.Settings.ZoneColors);
        Assert.False(h.State.Devices[0].Zones[0].OwnColor);
    }

    [Fact]
    public async Task SetZoneColor_unknown_zone_or_device_replies_error()
    {
        var h=await Running();
        var bad=await Send(h,new SetZoneColorRequest(LightingKeys.Zone(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"),9),"#00FF00"));
        Assert.False(bad.Ok);Assert.Equal("unknown zone",bad.Error);
        Assert.False((await Send(h,new SetZoneColorRequest("ghost|nowhere#0","#00FF00"))).Ok);
        Assert.False((await Send(h,new SetZoneColorRequest(zoneKey,"green"))).Ok);
        Assert.Empty(h.State.Settings.ZoneColors);
    }

    [Fact]
    public async Task ReplaceColors_restores_exact_snapshot()
    {
        var h=await Running();
        var snapshot=new Dictionary<string,ZoneColorEntry>{[zoneKey]=new(){Color="#112233",ZoneName="Corsair DRAM"}};
        Assert.True((await Send(h,new SetAllColorsRequest("#FF0000"))).Ok);
        Assert.True((await Send(h,new ReplaceColorsRequest("#445566",snapshot))).Ok);
        Assert.Equal("#445566",h.State.Settings.DefaultColor);
        var e=Assert.Single(h.State.Settings.ZoneColors);
        Assert.Equal(zoneKey,e.Key);Assert.Equal("#112233",e.Value.Color);
        var bad=await Send(h,new ReplaceColorsRequest("nope",snapshot));
        Assert.False(bad.Ok);
        Assert.Equal("#445566",h.State.Settings.DefaultColor);
    }

    [Fact]
    public async Task SetBrightness_out_of_range_replies_error_and_changes_nothing()
    {
        var h=await Running();
        var reply=await Send(h,new SetBrightnessRequest(101));
        Assert.False(reply.Ok);Assert.False(string.IsNullOrEmpty(reply.Error));
        Assert.Equal(100,h.State.Settings.Brightness);
        Assert.False((await Send(h,new SetBrightnessRequest(-1))).Ok);
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.Equal(40,h.State.Settings.Brightness);
    }

    [Fact]
    public async Task Mutation_broadcasts_state_event()
    {
        var h=await Running();
        var seen=new List<HostState>();
        h.StateChanged+=s=>{lock(seen)seen.Add(s);};
        Assert.True((await Send(h,new SetLightsOnRequest(false){Id=5})).Ok);
        lock(seen){Assert.Contains(seen,s=>!s.Settings.LightsOn);}
    }

    [Fact]
    public async Task Unreadable_lighting_json_runs_with_defaults_but_never_saves()
    {
        File.WriteAllText(lightingPath,"{\"Brightness\":10}");
        var hold=new FileStream(lightingPath,FileMode.Open,FileAccess.Read,FileShare.None);
        try
        {
            var h=Build();Start(h);
            await Until(()=>h.State.Status==HostStatus.Running);
            Assert.Contains("couldn't be read, so changes won't be saved until DeadWare restarts.",h.State.Message);
            Assert.Equal(100,h.State.Settings.Brightness);
            Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
            Assert.Equal(30,h.State.Settings.Brightness);
            Assert.False(File.Exists(lightingPath+".tmp"));
        }
        finally{hold.Dispose();}
        Assert.Equal("{\"Brightness\":10}",File.ReadAllText(lightingPath));
    }

    [Fact]
    public async Task Failing_connect_logs_and_broadcasts_once_and_backs_off()
    {
        session.FailConnect=true;
        var seen=new List<HostState>();
        var h=Build();
        h.StateChanged+=s=>{lock(seen)seen.Add(s);};
        Start(h);
        await Until(()=>{lock(seen)return seen.Any(x=>x.Status==HostStatus.Waiting);}); // the one broadcast for this error
        var attempts=session.Connects;
        int events;lock(seen)events=seen.Count;
        // Exactly 10 s, a tick at a time: each step waits until the loop waits on its tick, so a loop that arms its first tick late
        // cannot shift the window.
        for(var i=0;i<20;i++) await Elapse(DeviceHost.Tick);
        await Until(()=>time.Waiting(DeviceHost.Tick)); // the last tick's work is done
        Assert.InRange(session.Connects-attempts,1,6); // 10 s of ticks, backing off 2 s, 4 s, 8 s
        lock(seen)Assert.Equal(events,seen.Count);
        var text=File.ReadAllText(Path.Combine(dir,"logs","devices.log"));
        Assert.Equal(1,text.Split("fake connect failure").Length-1);
        session.FailConnect=false;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
    }

    [Fact]
    public async Task Failing_conflict_scan_fails_closed()
    {
        var failing=true;
        var engine=new OpenRgbEngine(exe,@"C:\cfg",runner,(_,_)=>Task.FromResult(true),time,log);
        var options=new HostOptions(exe,@"C:\cfg",lightingPath,Path.Combine(dir,"logs"),false,"S-1-5-21-1",1);
        var h=host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,new Scanner(_=>failing?throw new InvalidOperationException("scan broke"):[]),time,log);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        Assert.Equal(DeviceHost.ScanFailedText,h.State.Message);
        Assert.Equal(0,runner.Count);
        failing=false;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        failing=true;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(0).Killed); // the host publishes Blocked before it stops the engine
    }

    [Fact]
    public async Task Requests_before_settings_are_loaded_are_refused()
    {
        var h=Build();
        var early=await Send(h,new SetBrightnessRequest(10));
        Assert.False(early.Ok);Assert.Equal("starting",early.Error);
        Assert.False(File.Exists(lightingPath));
        h.Initialize();
        Assert.True((await Send(h,new SetBrightnessRequest(10))).Ok);
    }

    [Fact]
    public async Task Reply_precedes_the_state_broadcast_over_a_real_pipe()
    {
        var h=Build();
        // Recorded from the start: the host raises its Running broadcast just after it sets the status, so waiting for the broadcast itself
        // means no startup broadcast is still on its way to the pipe handler added below, to be written ahead of the reply.
        var states=new List<HostState>();
        h.StateChanged+=s=>{lock(states)states.Add(s);};
        Start(h);
        await Until(()=>{lock(states)return states.Any(s=>s.Status==HostStatus.Running);});
        var pipe="DeadWare.Devices.Test."+Guid.NewGuid().ToString("N");
        await using var server=new HostPipeServer(pipe,h.HandleAsync,log);
        h.StateChanged+=st=>_=server.SendAsync(new StateEvent(st));
        using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serving=server.RunAsync(cts.Token);
        await using var client=new System.IO.Pipes.NamedPipeClientStream(".",pipe,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000,cts.Token);
        await DeviceHostProtocol.WriteAsync(client,new SetBrightnessRequest(40){Id=5},cts.Token);
        var first=Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.True(first.Ok);Assert.Equal(5,first.Id);
        var second=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(0,second.Id);Assert.Equal(40,second.State.Settings.Brightness);
        cts.Cancel();
    }

    // Ruling AO: OpenRGB.NET waits for every reply with no timeout, and the host calls it under its one lock. A call that never returns is
    // cut off at BoundedSession.CallBound (real time) and the session is dropped, so the host answers again within that bound: here a
    // request that queued behind the hung call on the lock.
    [Fact]
    public async Task Hung_OpenRGB_call_cannot_freeze_the_host()
    {
        var h=await Running();
        session.HangWhen=call=>call=="ReadControllers";
        await AdvanceUntil(()=>session.Hanging); // the next slow tick's rescan hangs while it holds the lock
        var state=Assert.IsType<StateEvent>(await Task.Run(()=>h.HandleAsync(new GetStateRequest{Id=4})).WaitAsync(RealTimeCap));
        Assert.Equal(HostStatus.Waiting,state.State.Status);
        Assert.Contains("reconnecting",state.State.Message);
        Assert.True(session.Aborts>=1);
        session.HangWhen=null;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running); // the next tick reconnects
    }

    [Fact]
    public async Task Stop_is_answered_while_an_OpenRGB_call_hangs()
    {
        var h=await Running();
        session.HangWhen=call=>call=="UpdateLeds";
        await AdvanceUntil(()=>session.Hanging); // the next refresh hangs in its first LED write
        var reply=Assert.IsType<Reply>(await Task.Run(()=>h.HandleAsync(new StopRequest{Id=3})).WaitAsync(RealTimeCap));
        Assert.True(reply.Ok);
        await run!.WaitAsync(RealTimeCap);
        Assert.True(runner.At(0).Killed);
    }

    [Fact]
    public async Task Conflict_gate_closes_after_a_hung_OpenRGB_call()
    {
        var h=await Running();
        session.HangWhen=call=>call=="ReadControllers";
        await AdvanceUntil(()=>session.Hanging);
        lock(conflicts)conflicts.Add(DeadLight);
        await Until(()=>!session.Hanging); // cut off at the bound
        session.HangWhen=null;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(0).Killed);
    }

    // An OpenRGB that dies while a call waits on it releases that call at once and is relaunched: supervision never waits on the lock behind it.
    [Fact]
    public async Task OpenRGB_exit_releases_a_hung_call_and_is_relaunched()
    {
        var h=await Running();
        session.HangWhen=call=>call=="ReadControllers";
        await AdvanceUntil(()=>session.Hanging);
        runner.At(0).Exit();
        await Until(()=>!session.Hanging);
        session.HangWhen=null;
        await Until(()=>h.State.Status==HostStatus.Waiting);
        await Elapse(RestartBackoff.Base);
        await AdvanceUntil(()=>runner.Count==2&&h.State.Status==HostStatus.Running);
    }

    // Ruling AO: the OpenRGB.NET client is dropped whenever OpenRGB restarts or stops, so its read loop can't spin on a closed socket.
    [Fact]
    public async Task OpenRGB_restarting_or_stopping_drops_the_client()
    {
        var h=await Running();
        var before=session.Aborts;
        runner.At(0).Exit();
        await Until(()=>h.State.Status==HostStatus.Waiting);
        Assert.True(session.Aborts>before);
        await Elapse(RestartBackoff.Base);
        await AdvanceUntil(()=>runner.Count==2&&h.State.Status==HostStatus.Running);
        before=session.Aborts;
        lock(conflicts)conflicts.Add(DeadLight);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(1).Killed&&session.Aborts>before);
    }

    [Fact]
    public async Task Stop_kills_engine_and_ends_RunAsync()
    {
        var h=await Running();
        var reply=await Send(h,new StopRequest{Id=3});
        Assert.True(reply.Ok);Assert.Equal(3,reply.Id);
        await run!.WaitAsync(RealTimeCap); // no fake time involved: Stop cancels the loop's tick and RunAsync then stops the engine
        Assert.True(runner.At(0).Killed);
    }
}
