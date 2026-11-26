using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

public sealed partial class DeviceHostTests : IAsyncLifetime
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-host-"+Guid.NewGuid().ToString("N"));
    readonly string exe,lightingPath;
    readonly Clock time=new();
    readonly FakeRunner runner;
    // Another PC, with no desk picture, unless a test makes it the owner's before it builds the host (UseOwnerRig).
    FakeOpenRgbSession session=FakeOpenRgbSession.FromFixture();
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
        runner=new(time);
        Directory.CreateDirectory(dir);
        exe=Path.Combine(dir,"OpenRGB.exe");File.WriteAllText(exe,"");
        lightingPath=Path.Combine(dir,"lighting.json");
        log=new HostLog(Path.Combine(dir,"logs"));
    }
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync()
    {
        lock(holds)foreach(var hold in holds)hold.Release(); // nothing a test parked outlives it
        // Capped in real time: a stuck supervisor fails the test that left it instead of hanging the run until the CI job times out.
        if(host is not null){try{await host.HandleAsync(new StopRequest());if(run is not null)await run.WaitAsync(TimeSpan.FromSeconds(5));}catch{}await host.DisposeAsync().AsTask().WaitAsync(RealTimeCap);}
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
    // Records when, in fake time, each OpenRGB was started.
    sealed class FakeRunner(TimeProvider time):IProcessRunner
    {
        public readonly List<FakeProcess> Started=new();
        readonly List<DateTimeOffset> startedAt=new();
        public IRunningProcess Start(ProcessStartInfo info){lock(Started){var p=new FakeProcess(100+Started.Count);Started.Add(p);startedAt.Add(time.GetUtcNow());return p;}}
        public int Count{get{lock(Started)return Started.Count;}}
        public FakeProcess At(int i){lock(Started)return Started[i];}
        public DateTimeOffset StartedAt(int i){lock(Started)return startedAt[i];}
        // The OpenRGB processes still running, as a real scan would list them.
        public IReadOnlyList<RunningProcess> Live(){lock(Started)return Started.Where(p=>!p.HasExited).Select(p=>new RunningProcess(p.Id,"OpenRGB",null)).ToList();}
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
    // The scanner sees the fake OpenRGB processes that are still running, through the real classification (F15): one whose id isn't the
    // pid the host passes counts as another OpenRGB. So a host that stopped passing its own pid, or kept a scan that raced a relaunch,
    // would block itself. The test's own conflicts are added after a park, which a test can hold the loop in.
    DeviceHost Build(bool startup=false,string? exePath=null,Action<OpenRgbEngine>? beforeHost=null,Func<string,DeadWare.UpdateCore.LocationProtection>? folderCheck=null,string? startupFailure=null,LightingStore? store=null,HostLog? hostLog=null,IKeyPressSource? keys=null,IEverydaySignals? signals=null,IInputClock? input=null,AeroxMouse? mouse=null,Func<int,CancellationToken,Task<bool>>? portOpen=null)
    {
        var log=hostLog??this.log;
        var engine=new OpenRgbEngine(exePath??exe,@"C:\cfg\openrgb",runner,portOpen??((_,_)=>Task.FromResult(true)),time,log,folderCheck);
        beforeHost?.Invoke(engine);
        var scanner=new Scanner(pid=>
        {
            Interlocked.Increment(ref scans);
            Volatile.Read(ref scanHold)?.Park();
            var found=LightingConflicts.Find(runner.Live(),pid).ToList();
            lock(conflicts)found.AddRange(conflicts);
            return found;
        });
        var options=new HostOptions(exePath??exe,@"C:\cfg\openrgb",lightingPath,Path.Combine(dir,"logs"),startup,"S-1-5-21-1",1);
        return host=new DeviceHost(options,store??new LightingStore(lightingPath),engine,()=>session,scanner,time,log,startupFailure,keys:keys,signals:signals,input:input,mouse:mouse);
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
    // tick: the loop's tick, DeviceHost.FastTick while an effect runs with the lights on; the step defaults to it.
    async Task<TimeSpan> AdvanceUntil(Func<bool> cond,TimeSpan? step=null,TimeSpan? limit=null,TimeSpan? tick=null,[CallerArgumentExpression(nameof(cond))] string what="")
    {
        var t=tick??DeviceHost.Tick;
        var s=step??t;
        var max=limit??FakeTimeLimit;
        var moved=TimeSpan.Zero;
        var idle=Stopwatch.StartNew();
        while(!cond())
        {
            if(moved<max&&time.Waiting(t)){time.Advance(s);moved+=s;idle.Restart();}
            else if(idle.Elapsed>RealTimeCap)
                Assert.Fail(moved>=max
                    ?$"condition not reached within the {max.TotalSeconds:0.#} s fake-time limit: {what}"
                    :$"the host loop did not wait on its tick for {RealTimeCap.TotalSeconds:0} s (after {moved.TotalSeconds:0.#} s of fake time): {what}");
            await Task.Delay(1);
        }
        return moved;
    }
    async Task<DeviceHost> Running(LightingStore? store=null)
    {
        var h=Build(store:store);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        return h;
    }
    static async Task<Reply> Send(DeviceHost h,HostMessage m)=>Assert.IsType<Reply>(await h.HandleAsync(m));
    Rgb[] LastLeds(int index)=>session.Leds.Last(l=>l.Index==index).Colors;
    // A store that counts its saves, which write as the real one does.
    int saves;
    LightingStore CountingStore()=>new(lightingPath,write:(stream,json)=>{Interlocked.Increment(ref saves);using var w=new StreamWriter(stream,leaveOpen:true);w.Write(json);});
    // A device OpenRGB reports from now on. The list is replaced, never changed in place, since the host reads it on its own thread.
    void AddDevice(string serial,string name,int zones)
        =>session.Controllers=[..session.Controllers,new OpenRgbController(session.Controllers.Count,name,3,serial,$"HID: {serial}",Enumerable.Range(0,zones).Select(z=>new LightingZone(z,$"Zone {z}",4)).ToList(),["Direct"],0)];
    // The distinct frames sent to the device at index since the from-th LED write.
    List<string> Frames(int index,int from)=>session.Leds.Skip(from).Where(l=>l.Index==index).Select(l=>string.Join(",",l.Colors)).Distinct().ToList();
    static readonly Rgb SteelBlue=new(0x46,0x82,0xB4);
    int Apex=>session.Controllers.FindIndex(c=>c.Name=="SteelSeries Apex Pro");
    static readonly string ApexZone=LightingKeys.Zone(LightingKeys.Device("SERIAL-4",@"HID: \\?\HID#FAKE-1"),0);

    [Fact]
    public async Task Blocking_conflict_reports_Blocked_and_never_starts_engine()
    {
        conflicts.Add(DeadLight);
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        Assert.Contains("DeadLight is running.",h.State.Message);
        // Re-scans have run while DeadLight runs (a fixed advance could end before any did), and none started OpenRGB.
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=3);
        Assert.Equal(0,runner.Count);
        Assert.Equal(HostStatus.Blocked,h.State.Status);
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
        var leds=session.LedCount;
        var scanned=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+2); // ticks and re-scans have run while blocked
        Assert.Equal(leds,session.LedCount); // nothing is painted while blocked
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(2,runner.Count);
    }

    // F4 item 3 (whole-branch review D8): a program that comes and goes can't cycle OpenRGB without bound. Each conflict stop counts as an
    // exit, the relaunch once the conflict clears waits out the backoff, and the fifth stop within 10 minutes gives up, which the clean
    // scans that follow don't undo.
    [Fact]
    public async Task A_conflict_that_comes_and_goes_reaches_the_give_up_and_stops_relaunching()
    {
        var h=await Running();
        for(var i=0;i<RestartBackoff.MaxFailures;i++)
        {
            lock(conflicts)conflicts.Add(DeadLight);
            await AdvanceUntil(()=>runner.At(i).Killed);
            lock(conflicts)conflicts.Clear();
            if(i<RestartBackoff.MaxFailures-1) await AdvanceUntil(()=>runner.Count==i+2&&h.State.Status==HostStatus.Running,limit:TimeSpan.FromSeconds(60));
        }
        await AdvanceUntil(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal(OpenRgbEngine.GaveUpText,h.State.Message);
        var scanned=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+6); // half a minute of clean scans
        Assert.Equal(RestartBackoff.MaxFailures,runner.Count);
        Assert.Equal(HostStatus.OpenRgbFailed,h.State.Status);
    }

    // F4 item 3 (whole-branch review D9): the relaunch after a conflict stop waits the backoff counted from the stop itself, so it never comes
    // sooner than 4 s after the stop, nor so after the previous start, however long the scan or a wait for the lock held the stop up. Here the
    // scan that finds the conflict takes 4.5 s of fake time, so the next scan, timed from the start of that one, comes 0.5 s after the stop.
    [Fact]
    public async Task A_relaunch_after_a_conflict_stop_comes_no_sooner_than_4_s_after_it()
    {
        var h=await Running();
        var scan=NewHold();
        Volatile.Write(ref scanHold,scan);
        await AdvanceUntil(()=>scan.Parked); // the next scan is held in the scanner
        Volatile.Write(ref scanHold,null);
        lock(conflicts)conflicts.Add(DeadLight);
        time.Advance(TimeSpan.FromMilliseconds(4500));
        scan.Release();
        await Until(()=>runner.At(0).Killed);
        var stopped=time.GetUtcNow();
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>runner.Count==2&&h.State.Status==HostStatus.Running);
        Assert.InRange(runner.StartedAt(1)-stopped,RestartBackoff.Base,RestartBackoff.Base+DeviceHost.SlowTick);
        Assert.True(runner.StartedAt(1)-runner.StartedAt(0)>=RestartBackoff.Base);
    }

    // F4 item 3 (whole-branch review D8): after the engine gives up, here on five crashes, a conflict that comes and goes doesn't relaunch
    // OpenRGB: only turning lighting off and on does, as the give-up text says.
    [Fact]
    public async Task A_give_up_is_not_undone_by_a_conflict_that_clears()
    {
        var h=await Running();
        for(var i=0;i<RestartBackoff.MaxFailures-1;i++)
        {
            runner.At(i).Exit();
            await Until(()=>h.State.Status==HostStatus.Waiting); // the engine arms its relaunch delay before it reports Restarting
            await AdvanceUntil(()=>runner.Count==i+2&&h.State.Status==HostStatus.Running,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(65));
        }
        runner.At(RestartBackoff.MaxFailures-1).Exit();
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        lock(conflicts)conflicts.Add(DeadLight);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal(OpenRgbEngine.GaveUpText,h.State.Message);
        var scanned=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+4);
        Assert.Equal(RestartBackoff.MaxFailures,runner.Count);
        Assert.Equal(HostStatus.OpenRgbFailed,h.State.Status);
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

    // Ruling AQ through the host: on connecting, the Node Pro's empty channel 1 is sized before its Rainbow Wave is first sent, so the
    // effect has LEDs to run on from the start.
    [Fact]
    public async Task Node_Pro_is_sized_before_its_effect_is_first_sent()
    {
        session.SizeZone(6,0,0);
        await Running();
        Assert.Equal(["resize 6/0/48","mode 6/1"],session.Calls.Where(c=>c.StartsWith("resize")||c.StartsWith("mode 6/")).ToList());
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
    public async Task Clone_keeps_everyday_and_mouse()
    {
        File.WriteAllText(lightingPath,"{\"Version\":4,\"Everyday\":{\"Shortcut\":null,\"IdleOff\":true,\"IdleMinutes\":30,\"LockOff\":true},\"Mouse\":{\"SleepMinutes\":5}}");
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        var state=h.State.Settings;
        Assert.Null(state.Everyday.Shortcut);Assert.True(state.Everyday.IdleOff);Assert.Equal(30,state.Everyday.IdleMinutes);Assert.True(state.Everyday.LockOff);
        Assert.Equal(5,state.Mouse.SleepMinutes);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Null(saved.Everyday.Shortcut);Assert.True(saved.Everyday.IdleOff);Assert.Equal(30,saved.Everyday.IdleMinutes);Assert.True(saved.Everyday.LockOff);
        Assert.Equal(5,saved.Mouse.SleepMinutes);Assert.Equal(4,saved.Version);
    }

    [Fact]
    public async Task Clone_keeps_hub()
    {
        File.WriteAllText(lightingPath,"{\"Version\":5,\"Hub\":{\"Mode\":\"driven\"}}");
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.Equal("driven",h.State.Settings.Hub.Mode);
        var text=File.ReadAllText(lightingPath);
        Assert.Contains("\"Mode\": \"driven\"",text);Assert.Contains("\"Version\": 5",text);
    }

    [Fact]
    public async Task Other_changes_keep_device_tuning()
    {
        File.WriteAllText(lightingPath,"{\"Version\":2,\"Devices\":{\"k|1\":{\"Brightness\":70}}}");
        var h=await Running();
        Assert.Equal(70,h.State.Settings.Devices["k|1"].Brightness);
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        Assert.Equal(70,h.State.Settings.Devices["k|1"].Brightness);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Equal(70,saved.Devices["k|1"].Brightness);Assert.Equal(2,saved.Version);
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

    // Polish round 2, item 8: the note says what happens and promises no fix. The host runs on defaults and saves nothing, and only a host
    // that starts later reads the file again (lighting turned off and on, say); one that keeps running never does.
    [Fact]
    public async Task Unreadable_lighting_json_runs_with_defaults_but_never_saves()
    {
        File.WriteAllText(lightingPath,"{\"Brightness\":10}");
        var hold=new FileStream(lightingPath,FileMode.Open,FileAccess.Read,FileShare.None);
        var h=Build();
        try
        {
            Start(h);
            await Until(()=>h.State.Status==HostStatus.Running);
            Assert.Contains(DeviceHost.UnreadableNote,h.State.Message);
            Assert.Contains("defaults",DeviceHost.UnreadableNote);
            Assert.Contains("won't save",DeviceHost.UnreadableNote);
            Assert.Contains("when the lighting helper next starts",DeviceHost.UnreadableNote);
            Assert.DoesNotContain("restart",DeviceHost.UnreadableNote,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(100,h.State.Settings.Brightness);
            Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
            Assert.Equal(30,h.State.Settings.Brightness);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(lightingPath)!,Path.GetFileName(lightingPath)+".*.tmp"));
        }
        finally{hold.Dispose();}
        // Readable now, but this host read it when it started; it keeps its defaults and still saves nothing.
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.Contains(DeviceHost.UnreadableNote,h.State.Message);
        Assert.Equal("{\"Brightness\":10}",File.ReadAllText(lightingPath));
        // The next host to start reads it.
        Assert.True((await Send(h,new StopRequest())).Ok);
        await run!.WaitAsync(RealTimeCap);
        await h.DisposeAsync();
        var next=Build();
        next.Initialize();
        Assert.Equal(10,next.State.Settings.Brightness);
        Assert.DoesNotContain(DeviceHost.UnreadableNote,next.State.Message);
    }

    // Ruling AS: lighting.json from a newer DeadWare (after a rollback, say) is used as far as this build understands it, but never
    // rewritten, so the newer build's settings survive; the page says why changes don't stick.
    [Fact]
    public async Task Newer_lighting_json_is_used_but_never_saved()
    {
        const string json="{\"Version\":6,\"Brightness\":40,\"DefaultColor\":\"#112233\",\"Layout\":{\"Desk\":\"L\"}}";
        File.WriteAllText(lightingPath,json);
        var h=await Running();
        Assert.Equal(40,h.State.Settings.Brightness);
        Assert.Contains(DeviceHost.NewerNote,h.State.Message);
        Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
        Assert.Equal(30,h.State.Settings.Brightness);
        Assert.Equal(json,File.ReadAllText(lightingPath));
        Assert.Empty(Directory.GetFiles(dir,Path.GetFileName(lightingPath)+".*.tmp"));
        Assert.Empty(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
    }

    // B7: a newer file whose values this build can't accept is left alone and defaults are painted, and the page says both.
    [Fact]
    public async Task Newer_lighting_json_this_build_cannot_use_runs_on_defaults_and_says_so()
    {
        const string json="{\"Version\":6,\"Brightness\":500,\"DefaultColor\":\"#112233\"}";
        File.WriteAllText(lightingPath,json);
        var h=await Running();
        Assert.Contains(DeviceHost.NewerDefaultsNote,h.State.Message);
        Assert.DoesNotContain(DeviceHost.NewerNote,h.State.Message);
        Assert.Equal(100,h.State.Settings.Brightness);
        Assert.Equal("#4682B4",h.State.Settings.DefaultColor);
        Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
        Assert.Equal(json,File.ReadAllText(lightingPath));
        Assert.Empty(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
    }

    // B3/B14/B17/B24: an elevated host refuses lighting.json behind a link on every start, so its note says why and promises nothing from
    // a restart: it runs on defaults and saves nothing. The note speaks of the file's path, which covers the file as well as its folders
    // (polish round 2, item 7), since SafeFiles.NoLinks refuses a lighting.json that is itself a link too; and of a path that leads somewhere
    // DeadWare can't confirm, since the final-path check also refuses a redirection and a final path Windows can't give (F2 item 1).
    [Fact]
    public async Task Linked_lighting_json_runs_on_defaults_and_says_why()
    {
        var target=Path.Combine(dir,"target");Directory.CreateDirectory(target);
        var link=Path.Combine(dir,"link");
        using(var mklink=Process.Start(new ProcessStartInfo("cmd.exe"){ArgumentList={"/c","mklink","/J",link,target},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!)
        {mklink.StandardOutput.ReadToEnd();mklink.WaitForExit();Assert.Equal(0,mklink.ExitCode);}
        try
        {
            File.WriteAllText(Path.Combine(target,"lighting.json"),"{\"Brightness\":10}");
            var h=Build(store:new LightingStore(Path.Combine(link,"lighting.json"),guardLinks:true));Start(h);
            await Until(()=>h.State.Status==HostStatus.Running);
            Assert.Contains(DeviceHost.LinkedNote,h.State.Message);
            Assert.StartsWith("Your lighting settings file's path goes through a link, or leads somewhere DeadWare can't confirm, ",DeviceHost.LinkedNote);
            Assert.DoesNotContain("restart",h.State.Message,StringComparison.OrdinalIgnoreCase);
            Assert.Equal(100,h.State.Settings.Brightness);
            Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
            Assert.Equal("{\"Brightness\":10}",File.ReadAllText(Path.Combine(target,"lighting.json")));
        }
        finally{Directory.Delete(link);}
    }

    // F3 item 7 (whole-branch review D2): a lighting.json over 1 MiB is set aside unread, as damaged, so the host starts on defaults with the
    // damaged-file note and saves the next change, exactly as for invalid JSON, and its log line says the file was larger than 1 MiB.
    [Fact]
    public async Task A_lighting_json_over_1_MiB_is_set_aside_and_the_next_change_is_saved()
    {
        const string json="{\"Brightness\":40}";
        File.WriteAllText(lightingPath,json+new string(' ',1_048_577-json.Length));
        var h=await Running();
        Assert.Equal(100,h.State.Settings.Brightness);
        Assert.Contains(DeviceHost.QuarantineNote,h.State.Message);
        Assert.Contains("lighting.json was invalid and was set aside: Lighting settings are larger than 1 MiB (1048577 bytes).",File.ReadAllText(Path.Combine(dir,"logs","devices.log")));
        Assert.Single(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
        Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
        Assert.Equal(30,new LightingStore(lightingPath).Load().Settings.Brightness);
    }

    // The settings' written form, as Write makes it for lighting.json, measured in UTF-8.
    static long Written(LightingSettings s)=>System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(s,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    static async Task<HostMessage?> RoundTrip(HostMessage m)
    {
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,m,default);
        ms.Position=0;
        return await DeviceHostProtocol.ReadAsync(ms,default);
    }

    // F4 item 2 (whole-branch review D1): a change whose saved form would pass 1 MiB is refused with a plain error reply, as any invalid
    // change is, so the host never writes a lighting.json that its next start would set aside unread: the live settings stay as they were,
    // and the file too. Here a ReplaceColors of 2,100 entries, each valid, with names of 512 characters: about 1.2 MB written out.
    [Fact]
    public async Task A_change_that_would_save_over_1_MiB_is_refused_and_nothing_is_saved()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        var saved=File.ReadAllText(lightingPath);
        var colors=Enumerable.Range(0,2100).ToDictionary(i=>$"zone-{i:D5}",_=>new ZoneColorEntry{Color="#FF0000",ZoneName=new string('n',LightingSettingsCodec.MaxKeyLength)});
        Assert.True(Written(new LightingSettings{Brightness=40,DefaultColor="#123456",ZoneColors=colors})>LightingSettingsCodec.MaxFileBytes);
        var reply=await Send(h,new ReplaceColorsRequest("#123456",colors){Id=8});
        Assert.False(reply.Ok);Assert.Equal(8,reply.Id);
        Assert.Equal("Lighting settings would be larger than 1 MiB when saved.",reply.Error);
        Assert.Equal("#4682B4",h.State.Settings.DefaultColor);
        Assert.Empty(h.State.Settings.ZoneColors);
        Assert.Equal(40,h.State.Settings.Brightness);
        Assert.Equal(saved,File.ReadAllText(lightingPath));
        Assert.Empty(Directory.GetFiles(dir,"lighting.json.*.tmp"));
    }

    // F4 item 2 (whole-branch review D12): a state message carries the whole settings with the device list, so the pipe takes 4 MiB. The
    // largest settings lighting.json may hold travel in one state message with a large device list: the fixture's devices plus 40 more of
    // 16 zones each. Names here are non-ASCII, which grows six-fold once escaped; lighting.json's cap counts that already, since the file
    // escapes them just as the pipe does, so the settings' compact form is never longer than the file. The window's ReplaceColors that puts
    // them back (its Undo) travels too.
    [Fact]
    public async Task The_largest_settings_and_a_large_device_list_fit_one_state_message()
    {
        for(var i=0;i<40;i++)
            session.Controllers.Add(new OpenRgbController(session.Controllers.Count,"Gerät "+new string('ä',58),0,$"SERIAL-X{i}",$"HID: Ö{i}",
                Enumerable.Range(0,16).Select(z=>new LightingZone(z,"Zone "+new string('ö',59),1)).ToList(),["Direct"],0));
        var h=await Running();
        // Entries with non-ASCII keys and names of the longest length, then entries with ASCII names taking up the rest, to just under the cap.
        var colors=new Dictionary<string,ZoneColorEntry>();
        // Measured on the host's own settings, with the layout auto-arrange gave its devices at the first connect and its effect, with the
        // colours swapped in, as the ReplaceColors below leaves them.
        var basis=h.State.Settings;
        long Size(){basis.DefaultColor="#123456";basis.ZoneColors=colors;return Written(basis);}
        string Key(int i)=>$"zone-{i:D5}-"+new string('ß',LightingSettingsCodec.MaxKeyLength-11);
        var full=new ZoneColorEntry{Color="#FF0000",ZoneName=new string('ü',LightingSettingsCodec.MaxKeyLength)};
        colors[Key(0)]=full;var one=Size();colors[Key(1)]=full;var each=Size()-one;
        for(var i=2;Size()+each<=LightingSettingsCodec.MaxFileBytes;i++) colors[Key(i)]=full;
        for(var i=colors.Count;;i++)
        {
            colors[$"fill-{i:D5}"]=new ZoneColorEntry{Color="#FF0000",ZoneName=""};
            var left=LightingSettingsCodec.MaxFileBytes-Size();
            if(left<0){colors.Remove($"fill-{i:D5}");break;}
            colors[$"fill-{i:D5}"].ZoneName=new string('n',(int)Math.Min(left,LightingSettingsCodec.MaxKeyLength));
        }
        Assert.InRange(Size(),LightingSettingsCodec.MaxFileBytes-200,LightingSettingsCodec.MaxFileBytes);
        var undo=Assert.IsType<ReplaceColorsRequest>(await RoundTrip(new ReplaceColorsRequest("#123456",colors){Id=8}));
        Assert.True((await Send(h,undo)).Ok);
        Assert.Equal(Size(),new FileInfo(lightingPath).Length); // as saved
        var state=Assert.IsType<StateEvent>(await h.HandleAsync(new GetStateRequest{Id=9}));
        var ms=new MemoryStream();
        await DeviceHostProtocol.WriteAsync(ms,state,default);
        Assert.InRange(ms.Length-4,1_048_577,DeviceHostProtocol.MaxMessageBytes); // over 1 MiB, which the pipe once took
        ms.Position=0;
        var back=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(ms,default)).State;
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(back.Settings).Length<=new FileInfo(lightingPath).Length); // compact, escaped alike
        Assert.Equal(48,back.Devices.Count);
        Assert.Equal(colors.Count,back.Settings.ZoneColors.Count);
        Assert.All(colors,e=>Assert.Equal(e.Value.ZoneName,back.Settings.ZoneColors[e.Key].ZoneName));
    }

    // F3 item 6 (whole-branch review D3): a host that may save, since lighting.json wasn't there when it started, but whose save then fails
    // replies "couldn't save" to a change and doesn't make it: the settings it reports, and the colours it paints, stay as they were. The
    // store stands in a write that fails, as a full disk makes it fail, and a put-in-place refused as a linked path, as a junction swapped
    // in while the host runs makes the final-path check refuse it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_change_whose_save_fails_replies_couldnt_save_and_is_not_made(bool linked)
    {
        var store=linked
            ?new LightingStore(lightingPath,place:(_,_)=>throw new LinkedPathException("lighting.json's path led elsewhere"))
            :new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full"));
        var h=Build(store:store);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal("",h.State.Message); // it started normally, with no note
        var steelBlue=new Rgb(0x46,0x82,0xB4);
        Assert.All(LastLeds(0),c=>Assert.Equal(steelBlue,c));
        await Until(()=>time.Waiting(DeviceHost.Tick));
        time.Advance(TimeSpan.FromMilliseconds(250)); // past the RAM's frame cap, short of the next tick, so a change made now would be painted at once
        var reply=await Send(h,new SetAllColorsRequest("#FF0000"){Id=7});
        Assert.False(reply.Ok);Assert.Equal(HostReplyCodes.CouldntSave,reply.Error);Assert.Equal(7,reply.Id);
        Assert.Equal("#4682B4",h.State.Settings.DefaultColor);
        Assert.Equal("#4682B4",h.State.Devices[0].Zones[0].Color);
        Assert.All(LastLeds(0),c=>Assert.Equal(steelBlue,c)); // nothing of the change was painted
        var written=session.LedCount;
        time.Advance(TimeSpan.FromMilliseconds(250)); // the rest of the way to the tick the loop was waiting on
        await AdvanceUntil(()=>session.LedCount>written); // the next paint
        Assert.All(LastLeds(0),c=>Assert.Equal(steelBlue,c));
        // F4 item 4b (whole-branch review D3): each change fails in turn, since each save is refused, so the second does too.
        var second=await Send(h,new SetBrightnessRequest(30){Id=8});
        Assert.False(second.Ok);Assert.Equal(HostReplyCodes.CouldntSave,second.Error);Assert.Equal(8,second.Id);
        Assert.Equal(100,h.State.Settings.Brightness);
        Assert.Equal("#4682B4",h.State.Settings.DefaultColor);
        Assert.False(File.Exists(lightingPath));
        Assert.Empty(Directory.GetFiles(dir,"lighting.json.*.tmp"));
        Assert.Contains("saving lighting.json failed: "+(linked?"lighting.json's path led elsewhere":"the disk is full"),File.ReadAllText(Path.Combine(dir,"logs","devices.log")));
    }

    // F3 item 10 (spec, "Hardware modes and the Lighting Node Pro"): "Direct" is never written into a hardware-defaulted device's saved
    // settings, since that would defeat the fallback by mode name, as DeadLight's comment warns. Colour changes that reach the Node Pro, one
    // for every zone and one for a zone of its own, save no hardware mode for it, and it is never switched to Direct.
    [Fact]
    public async Task Colour_changes_never_save_Direct_for_the_Node_Pro()
    {
        var h=await Running();
        var nodePro=Assert.Single(h.State.Devices,d=>d.Name==LightingDeviceService.NodePro);
        Assert.Equal("Rainbow Wave",nodePro.OwnEffect);
        var channel1=LightingKeys.Zone(nodePro.Key,LightingDeviceService.NodeProZone);
        Assert.True((await Send(h,new SetAllColorsRequest("#FF0000"))).Ok);
        Assert.True((await Send(h,new SetZoneColorRequest(channel1,"#00FF00"))).Ok);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Equal("#FF0000",saved.DefaultColor);Assert.Equal("#00FF00",saved.ZoneColors[channel1].Color); // both changes were saved
        Assert.Empty(saved.HardwareModes);
        Assert.DoesNotContain("Direct",File.ReadAllText(lightingPath));
        Assert.Empty(h.State.Settings.HardwareModes);
        var node=session.Controllers.FindIndex(c=>c.Name==LightingDeviceService.NodePro);
        var direct=session.Controllers[node].Modes.ToList().IndexOf(LightingPaint.DirectMode);
        Assert.DoesNotContain($"mode {node}/{direct}",session.Calls);
        Assert.Equal("Rainbow Wave",Assert.Single(h.State.Devices,d=>d.Name==LightingDeviceService.NodePro).Mode);
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
        // Exactly 2 in these 10 s: retries at 2 s and 6 s, the backoff armed by the first failed connect (K(e)) and doubling. Without K(e)
        // the first tick retries at once (3 attempts); without the doubling it retries every 2 s (5).
        Assert.Equal(2,session.Connects-attempts);
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
        var engine=new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log);
        var options=new HostOptions(exe,@"C:\cfg\openrgb",lightingPath,Path.Combine(dir,"logs"),false,"S-1-5-21-1",1);
        var h=host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,new Scanner(_=>failing?throw new InvalidOperationException("scan broke"):[]),time,log);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        Assert.Equal(DeviceHost.ScanFailedText,h.State.Message);
        // Whole-branch review D5: no scan has completed yet, so the empty list is no all-clear; the first scan that completes marks it.
        Assert.False(h.State.ConflictsChecked);
        Assert.Equal(0,runner.Count);
        failing=false;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        Assert.True(h.State.ConflictsChecked);
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

    // F15 / Ruling I: the host passes its own OpenRGB's pid to the scan, so its own OpenRGB is never a conflict: re-scans keep it running.
    [Fact]
    public async Task Own_openrgb_is_never_a_conflict()
    {
        var h=await Running();
        var scanned=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+3);
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.Empty(h.State.Conflicts);
        Assert.Equal(1,runner.Count);
        Assert.False(runner.At(0).Killed);
    }

    // F39 / Review Focus 3: a device-list change OpenRGB announces before the next tick, then a colour change. The change's immediate paint
    // must reach each device at its new index, never a stale one.
    [Fact]
    public async Task Settings_change_after_a_device_list_change_paints_the_new_indexes()
    {
        var h=await Running();
        await Until(()=>time.Waiting(DeviceHost.Tick));
        time.Advance(TimeSpan.FromMilliseconds(100)); // past the HID frame cap, short of the next tick
        session.Reorder(l=>{var x=l.ToList();var apex=x[3];x.RemoveAt(3);x.Insert(0,apex);return x;}); // the Apex (112 LEDs) moves to index 0
        session.RaiseUpdated();
        var before=session.LedCount;
        Assert.True((await Send(h,new SetAllColorsRequest("#FF0000"))).Ok);
        var written=session.Leds.Skip(before).ToList();
        Assert.Contains(written,w=>w.Index==0&&w.Colors.Length==112);
        // Every frame fits the controller now at its index.
        Assert.All(written,w=>Assert.Equal(session.Controllers[w.Index].Zones.Sum(z=>z.LedCount),w.Colors.Length));
    }

    // Task 9: when an elevated host couldn't prepare OpenRGB's protected config folder, it reports why and never scans or starts OpenRGB,
    // which would otherwise run elevated with a config and plugin folder other programs can change.
    [Fact]
    public async Task Startup_failure_reports_OpenRgbFailed_and_never_starts_openrgb()
    {
        var h=Build(startupFailure:"The folder isn't protected.");Start(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal("The folder isn't protected.",h.State.Message);
        await Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(0,runner.Count);
        Assert.Equal(0,Volatile.Read(ref scans));
        Assert.Equal(HostStatus.OpenRgbFailed,h.State.Status);
        Assert.False(h.State.ConflictsChecked); // it never scanned, so its empty list is no all-clear (polish item 2)
    }

    // Polish item 7: an elevated host whose %ProgramData%\DeadWare was refused has no log either, since the log lives there. It still
    // reports the refusal with its reason and answers the window: nothing else depends on the log.
    [Fact]
    public async Task Host_without_a_log_still_reports_a_refused_folder_and_answers()
    {
        using var nowhere=new HostLog(null);
        var refusal=ProtectedFolders.Refusal(@"C:\ProgramData\DeadWare",@"C:\ProgramData\DeadWare\openrgb",@"C:\ProgramData\DeadWare can be changed by S-1-5-32-545.");
        var h=Build(startupFailure:refusal,hostLog:nowhere);Start(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbFailed);
        Assert.Equal(refusal,h.State.Message);
        Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
        Assert.Equal(30,new LightingStore(lightingPath).Load().Settings.Brightness);
        Assert.IsType<StateEvent>(await h.HandleAsync(new GetStateRequest{Id=4}));
        Assert.False(Directory.Exists(Path.Combine(dir,"logs"))); // nothing was logged anywhere
    }

    // Polish item 2 (Ruling AV, T10): the host's first completed conflict scan marks the state checked, and it stays checked, so Settings can
    // tell an empty list from one that was never looked for.
    [Fact]
    public async Task Conflicts_are_marked_checked_by_the_first_scan_and_stay_checked()
    {
        var failing=false;
        var engine=new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log);
        var options=new HostOptions(exe,@"C:\cfg\openrgb",lightingPath,Path.Combine(dir,"logs"),false,"S-1-5-21-1",1);
        var h=host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,new Scanner(_=>Volatile.Read(ref failing)?throw new InvalidOperationException("scan broke"):[]),time,log);
        h.Initialize();
        Assert.False(h.State.ConflictsChecked);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.True(h.State.ConflictsChecked);
        Volatile.Write(ref failing,true);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        Assert.True(h.State.ConflictsChecked);
    }

    // Task 9 K(d): a scan that keeps failing the same way is logged once, not every 5 s.
    [Fact]
    public async Task Persistent_scan_failure_is_logged_once()
    {
        var engine=new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log);
        var options=new HostOptions(exe,@"C:\cfg\openrgb",lightingPath,Path.Combine(dir,"logs"),false,"S-1-5-21-1",1);
        var h=host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,new Scanner(_=>{Interlocked.Increment(ref scans);throw new InvalidOperationException("scan broke");}),time,log);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Blocked);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=4);
        var text=File.ReadAllText(Path.Combine(dir,"logs","devices.log"));
        Assert.Equal(1,text.Split("conflict scan failed: scan broke").Length-1);
    }

    // Task 9 K(c): states are built and delivered in one order, so the last one a subscriber gets is the newest even when two changes race:
    // a change made while an earlier state is still being delivered is delivered after it.
    [Fact]
    public async Task Racing_changes_deliver_their_states_in_order()
    {
        var h=await Running();
        var first=NewHold();
        var delivered=new List<int>();
        var parked=0;
        h.StateChanged+=s=>{if(s.Settings.Brightness==10&&Interlocked.Exchange(ref parked,1)==0) first.Park();lock(delivered)delivered.Add(s.Settings.Brightness);};
        var ten=Task.Run(()=>h.HandleAsync(new SetBrightnessRequest(10)));
        await Until(()=>first.Parked);
        var twenty=Task.Run(()=>h.HandleAsync(new SetBrightnessRequest(20)));
        await Task.Delay(200); // let the second change reach its delivery, if it isn't made to wait
        first.Release();
        await Task.WhenAll(ten,twenty).WaitAsync(RealTimeCap);
        lock(delivered)Assert.Equal(20,delivered[^1]);
        Assert.Equal(20,h.State.Settings.Brightness);
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

    // Lighting step 2: the loop ticks every 33 ms only while an effect runs with the lights on; otherwise every 500 ms. A tick already armed
    // runs out first, so each switch takes effect from the next tick.
    [Fact]
    public async Task Tick_is_fast_only_while_an_effect_runs_with_lights_on()
    {
        var h=await Running();
        await Until(()=>time.Waiting(DeviceHost.Tick));
        Assert.False(time.Waiting(DeviceHost.FastTick));
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.Tick),tick:DeviceHost.FastTick);
        Assert.False(time.Waiting(DeviceHost.FastTick));
    }

    // Fix wave L1: the fast tick only while something can be painted. With Wave chosen while OpenRGB won't connect the loop waits Tick;
    // once the session is back and Running, FastTick.
    [Fact]
    public async Task Tick_is_slow_while_the_session_is_down_even_with_an_effect()
    {
        session.FailConnect=true;
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Waiting);
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        await RunFor(TimeSpan.FromSeconds(3),DeviceHost.Tick); // stalls if the loop waits FastTick instead
        Assert.False(time.Waiting(DeviceHost.FastTick));
        Assert.Equal(HostStatus.Waiting,h.State.Status);
        session.FailConnect=false;
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
    }

    // With the lights off an effect doesn't run: the loop keeps its 500 ms tick and every frame is black, the same each time.
    [Fact]
    public async Task Tick_is_slow_while_lights_are_off()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        var from=session.LedCount;
        await AdvanceUntil(()=>session.Leds.Skip(from).Count(l=>l.Index==Apex)>=3); // the tick stays Tick, or this stalls
        await Until(()=>time.Waiting(DeviceHost.Tick));
        Assert.False(time.Waiting(DeviceHost.FastTick));
        var written=session.Leds.Skip(from).ToList();
        Assert.All(written,w=>Assert.All(w.Colors,c=>Assert.Equal(Rgb.Black,c)));
        Assert.All(written.GroupBy(w=>w.Index),g=>Assert.Single(g.Select(w=>string.Join(",",w.Colors)).Distinct()));
    }

    // The host paints the effect at the time since it started, so a placed Direct device gets new frames as time passes.
    [Fact]
    public async Task Frames_change_as_time_passes()
    {
        var h=await Running();
        Assert.Contains(ApexZone,h.State.Settings.Layout.Zones.Keys); // placed at the first connect
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var from=session.LedCount;
        await AdvanceUntil(()=>Frames(Apex,from).Count>=2,tick:DeviceHost.FastTick);
    }

    // The 5 s work (conflict scan, rescan) keeps its cadence when the loop ticks every 33 ms: once per 5 s of fake time, not once per tick.
    [Fact]
    public async Task The_five_second_work_keeps_its_cadence_at_the_fast_tick()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var before=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>before,tick:DeviceHost.FastTick);
        var first=time.GetUtcNow();
        var at=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=at+2,tick:DeviceHost.FastTick);
        // Two more scans took two slow ticks, each the first 33 ms tick at or past 5 s: 5.016 s apiece.
        Assert.InRange((time.GetUtcNow()-first).TotalMilliseconds,2*DeviceHost.SlowTick.TotalMilliseconds,2*(DeviceHost.SlowTick+DeviceHost.FastTick).TotalMilliseconds);
        Assert.Equal(at+2,Volatile.Read(ref scans));
    }

    // A device that appears gets points for its zones, saved and in the state, and a rescan that finds no change saves nothing.
    [Fact]
    public async Task A_new_device_is_arranged_and_saved_once()
    {
        var h=await Running(CountingStore());
        Assert.Equal(1,Volatile.Read(ref saves)); // the first connect arranged the fixture's devices and saved them once
        var saved=Volatile.Read(ref saves);
        var key=LightingKeys.Device("SERIAL-NEW","HID: SERIAL-NEW");
        AddDevice("SERIAL-NEW","Fan Hub",3);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(saved+1,Volatile.Read(ref saves));
        var zones=Enumerable.Range(0,3).Select(i=>LightingKeys.Zone(key,i)).ToList();
        var layout=new LightingStore(lightingPath).Load().Settings.Layout;
        Assert.All(zones,z=>Assert.Contains(z,layout.Zones.Keys));
        Assert.Contains(key,layout.Tower.Members);
        var state=Assert.IsType<StateEvent>(await h.HandleAsync(new GetStateRequest{Id=2})).State;
        Assert.All(zones,z=>Assert.Equal(layout.Zones[z],state.Settings.Layout.Zones[z]));
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(saved+1,Volatile.Read(ref saves));
    }

    // A device that comes back under a new key at the same location keeps its saved points: the remap re-keys them before auto-arrange
    // could place the new key elsewhere, and one save covers both.
    [Fact]
    public async Task Remap_runs_before_arrange()
    {
        var h=await Running(CountingStore());
        var old=LightingKeys.Zone(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"),0);
        var point=h.State.Settings.Layout.Zones[old];
        var saved=Volatile.Read(ref saves);
        var list=session.Controllers.ToList();list[0]=list[0] with{Serial=""};session.Controllers=list; // it lost its serial
        Assert.True((await Send(h,new RescanRequest())).Ok);
        var layout=h.State.Settings.Layout;
        Assert.Equal(point,layout.Zones[LightingKeys.Zone(LightingKeys.Device("","I2C: FAKE-1"),0)]);
        Assert.DoesNotContain(old,layout.Zones.Keys);
        Assert.Equal(saved+1,Volatile.Read(ref saves));
        Assert.Equal(point,new LightingStore(lightingPath).Load().Settings.Layout.Zones[LightingKeys.Zone(LightingKeys.Device("","I2C: FAKE-1"),0)]);
    }

    // The points for a newly seen device would make lighting.json larger than 1 MiB: the layout stays as it was, one log line says why (once
    // per reason, however many rescans meet it), and later changes are still accepted, since the live settings remain savable.
    [Fact]
    public async Task A_layout_too_large_to_save_is_not_adopted_at_a_rescan()
    {
        var h=await Running();
        var basis=h.State.Settings;
        var colors=new Dictionary<string,ZoneColorEntry>();
        long Size(){basis.ZoneColors=colors;return Written(basis);}
        for(var i=0;;i++)
        {
            colors[$"fill-{i:D5}"]=new ZoneColorEntry{Color="#FF0000",ZoneName=""};
            var left=LightingSettingsCodec.MaxFileBytes-Size();
            if(left<0){colors.Remove($"fill-{i:D5}");break;}
            colors[$"fill-{i:D5}"].ZoneName=new string('n',(int)Math.Min(left,LightingSettingsCodec.MaxKeyLength));
        }
        Assert.InRange(Size(),LightingSettingsCodec.MaxFileBytes-200,LightingSettingsCodec.MaxFileBytes); // 16 more points take far more
        Assert.True((await Send(h,new ReplaceColorsRequest(basis.DefaultColor,colors))).Ok);
        var layout=h.State.Settings.Layout;
        AddDevice("SERIAL-NEW","Fan Hub",16);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        var after=h.State.Settings.Layout;
        Assert.Equal(layout.Zones,after.Zones);
        Assert.Equal(layout.Tower.Members,after.Tower.Members);
        Assert.Equal(9,h.State.Devices.Count); // the device itself is seen
        AddDevice("SERIAL-NEW2","Fan Hub",16);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(layout.Zones,h.State.Settings.Layout.Zones);
        var text=File.ReadAllText(Path.Combine(dir,"logs","devices.log"));
        Assert.Equal(1,text.Split("desk layout not arranged: Lighting settings would be larger than 1 MiB when saved.").Length-1);
        Assert.True((await Send(h,new SetBrightnessRequest(30))).Ok);
        Assert.Equal(30,new LightingStore(lightingPath).Load().Settings.Brightness);
    }

    [Fact]
    public async Task SetEffect_saves_and_refuses()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetEffectRequest("Breathing",new EffectParams{Speed=10}))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Spectrum",new EffectParams{Speed=20}))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Wave",new EffectParams{Speed=70}))).Ok);
        var effect=new LightingStore(lightingPath).Load().Settings.Effect;
        Assert.Equal("Wave",effect.Kind);Assert.Equal(70,effect.Wave.Speed);
        Assert.Equal(10,effect.Breathing.Speed);Assert.Equal(20,effect.Spectrum.Speed); // a Wave Params leaves the others alone
        Assert.Equal("Wave",h.State.Settings.Effect.Kind);Assert.Equal(70,h.State.Settings.Effect.Wave.Speed);
        var file=File.ReadAllText(lightingPath);
        HostMessage[] refused=
        [
            new SetEffectRequest("Wave",new EffectParams{Speed=101}),
            new SetEffectRequest("Wave",new EffectParams{Colors=Enumerable.Repeat("#FF0000",LightingEffects.MaxColors+1).ToList()}),
            new SetEffectRequest("Wave",new EffectParams{Colors=["red"]}),
            new SetEffectRequest("Plasma",null),
            new SetEffectRequest("1",null), // a kind is known by its name only
            new SetEffectRequest(null!,null),
            new SetEffectRequest("Static",new EffectParams()),
            new SetEffectRequest("Wave",new EffectParams{Colors=null!}),
        ];
        foreach(var m in refused)
        {
            var reply=await Send(h,m);
            Assert.False(reply.Ok);Assert.False(string.IsNullOrEmpty(reply.Error));
            Assert.Equal("Wave",h.State.Settings.Effect.Kind);Assert.Equal(70,h.State.Settings.Effect.Wave.Speed);Assert.Empty(h.State.Settings.Effect.Wave.Colors);
            Assert.Equal(file,File.ReadAllText(lightingPath));
        }
        Assert.True((await Send(h,new SetEffectRequest("Static",null))).Ok);
        Assert.Equal("Static",h.State.Settings.Effect.Kind);
        Assert.Equal(70,h.State.Settings.Effect.Wave.Speed);
        Assert.Equal("Static",new LightingStore(lightingPath).Load().Settings.Effect.Kind);
    }

    [Fact]
    public async Task SetEffect_refused_when_the_save_fails()
    {
        var h=await Running(new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full")));
        var reply=await Send(h,new SetEffectRequest("Wave",new EffectParams{Speed=70}){Id=4});
        Assert.False(reply.Ok);Assert.Equal(HostReplyCodes.CouldntSave,reply.Error);Assert.Equal(4,reply.Id);
        Assert.Equal("Static",h.State.Settings.Effect.Kind);
        Assert.Equal(40,h.State.Settings.Effect.Wave.Speed);
    }

    // An effect chosen while OpenRGB won't connect is saved and answered at once, with no OpenRGB call; the frames after the reconnect are
    // the effect's from the first, so the first connect places the devices before it paints.
    [Fact]
    public async Task SetEffect_while_waiting_is_saved_and_painted_after_reconnect()
    {
        session.FailConnect=true;
        var h=Build();Start(h);
        await Until(()=>h.State.Status==HostStatus.Waiting);
        var (connects,reads,calls,leds)=(session.Connects,session.Reads,session.Calls.Count,session.LedCount);
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        Assert.Equal((connects,reads,calls,leds),(session.Connects,session.Reads,session.Calls.Count,session.LedCount));
        Assert.Equal("Wave",new LightingStore(lightingPath).Load().Settings.Effect.Kind);
        session.FailConnect=false; // nothing can be painted until the reconnect, so the loop keeps Tick until then (fix wave L1)
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Frames(Apex,0).Count>=2,tick:DeviceHost.FastTick);
        Assert.DoesNotContain(string.Join(",",Enumerable.Repeat(SteelBlue,112)),Frames(Apex,0));
    }

    [Fact]
    public async Task SetLayout_places_a_device_the_draft_lacks()
    {
        var h=await Running();
        var a=LightingKeys.Zone(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"),0);
        var b=LightingKeys.Zone(LightingKeys.Device("SERIAL-2","I2C: FAKE-2"),0);
        var draft=new DeskLayout{Zones={[a]=new(0.5,0.5)}};
        Assert.True((await Send(h,new SetLayoutRequest(draft))).Ok);
        var saved=new LightingStore(lightingPath).Load().Settings.Layout;
        Assert.Equal(new DeskPoint(0.5,0.5),saved.Zones[a]);
        Assert.Contains(b,saved.Zones.Keys);
        Assert.Contains(ApexZone,saved.Zones.Keys);
        Assert.Equal(saved.Zones,h.State.Settings.Layout.Zones);
        Assert.Single(draft.Zones); // the request's own layout is left as it came
    }

    [Fact]
    public async Task SetLayout_refuses_an_invalid_layout()
    {
        var h=await Running();
        var layout=h.State.Settings.Layout;
        var file=File.ReadAllText(lightingPath);
        var bad=DeskLayouts.Clone(layout);bad.Zones[ApexZone]=new(2,0.5);
        var reply=await Send(h,new SetLayoutRequest(bad));
        Assert.False(reply.Ok);Assert.Contains("unit square",reply.Error);
        Assert.False((await Send(h,new SetLayoutRequest(null!))).Ok);
        Assert.Equal(layout.Zones,h.State.Settings.Layout.Zones);
        Assert.Equal(file,File.ReadAllText(lightingPath));
    }

    [Fact]
    public async Task SetLayout_keeps_entries_for_absent_devices()
    {
        var h=await Running();
        var draft=DeskLayouts.Clone(h.State.Settings.Layout);
        draft.Zones["ghost|nowhere#0"]=new(0.3,0.3);draft.Tower.Excluded.Add("ghost|nowhere");draft.Unplaced.Add("other|gone");
        Assert.True((await Send(h,new SetLayoutRequest(draft))).Ok);
        var saved=new LightingStore(lightingPath).Load().Settings.Layout;
        Assert.Equal(new DeskPoint(0.3,0.3),saved.Zones["ghost|nowhere#0"]);
        Assert.Contains("ghost|nowhere",saved.Tower.Excluded);
        Assert.Contains("other|gone",saved.Unplaced);
    }

    // Choosing an effect or a layout repaints only: it reads no device list and switches no device's mode.
    [Fact]
    public async Task Changing_effect_or_layout_never_rescans_or_switches_modes()
    {
        var h=await Running();
        await Until(()=>time.Waiting(DeviceHost.Tick)); // the first tick's work is done; nothing moves the clock below
        var (reads,modes,connects)=(session.Reads,session.Modes.Count,session.Connects);
        Assert.True((await Send(h,new SetEffectRequest("Wave",new EffectParams{Speed=90}))).Ok);
        var draft=DeskLayouts.Clone(h.State.Settings.Layout);draft.Zones[ApexZone]=new(0.9,0.1);
        Assert.True((await Send(h,new SetLayoutRequest(draft))).Ok);
        Assert.Equal((reads,modes,connects),(session.Reads,session.Modes.Count,session.Connects));
        Assert.Equal(1,runner.Count);
    }

    // Task 7: the preview frames the host raises, each with the fake time it was raised at.
    List<(DateTimeOffset At,Dictionary<string,string> Colors)> Previews(DeviceHost h)
    {
        var seen=new List<(DateTimeOffset,Dictionary<string,string>)>();
        h.PreviewChanged+=e=>{lock(seen)seen.Add((time.GetUtcNow(),e.Colors));};
        return seen;
    }
    static int CountOf<T>(List<T> list){lock(list)return list.Count;}
    // Steps fake time by at least span, then waits until the loop waits on its next tick, so every tick in the span has done its work.
    async Task RunFor(TimeSpan span,TimeSpan tick)
    {
        var end=time.GetUtcNow()+span;
        await AdvanceUntil(()=>time.GetUtcNow()>=end,tick:tick);
        await Until(()=>time.Waiting(tick));
    }
    static bool SameFrame(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(e=>b.TryGetValue(e.Key,out var v)&&v==e.Value);
    async Task<DeviceHost> RunningWave(EffectParams? p=null)
    {
        var h=await Running();
        Assert.True((await Send(h,new SetEffectRequest("Wave",p))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick)); // the tick armed before the change runs out first
        return h;
    }

    [Fact]
    public async Task No_preview_without_a_subscription()
    {
        var h=await RunningWave();
        var seen=Previews(h);
        var from=session.LedCount;
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.FastTick);
        Assert.True(Frames(Apex,from).Count>=2); // the effect ran
        Assert.Equal(0,CountOf(seen));
    }

    [Fact]
    public async Task Preview_flows_to_a_subscriber_at_most_ten_a_second()
    {
        var h=await RunningWave();
        var seen=Previews(h);
        var states=0;
        h.StateChanged+=_=>Interlocked.Increment(ref states);
        var reply=await Send(h,new SubscribePreviewRequest(true){Id=9});
        Assert.True(reply.Ok);Assert.Equal(9,reply.Id);
        Assert.Equal(0,Volatile.Read(ref states)); // a subscription raises no state
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.FastTick);
        List<(DateTimeOffset At,Dictionary<string,string> Colors)> frames;
        lock(seen)frames=seen.ToList();
        Assert.InRange(frames.Count,2,10);
        Assert.Contains(ApexZone,frames[0].Colors.Keys);
        for(var i=1;i<frames.Count;i++)
        {
            Assert.False(SameFrame(frames[i-1].Colors,frames[i].Colors));
            Assert.True(frames[i].At-frames[i-1].At>=DeviceHost.PreviewGap,$"frames {i-1} and {i} came {(frames[i].At-frames[i-1].At).TotalMilliseconds} ms apart");
        }
    }

    [Fact]
    public async Task No_preview_while_static_or_lights_off()
    {
        var h=await Running();
        var seen=Previews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick); // Static
        Assert.Equal(0,CountOf(seen));
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Wave",null))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick); // Wave with the lights off
        Assert.Equal(0,CountOf(seen));
        Assert.True((await Send(h,new SetLightsOnRequest(true))).Ok); // the subscription was live all along
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        await AdvanceUntil(()=>CountOf(seen)>=1,tick:DeviceHost.FastTick);
    }

    [Fact]
    public async Task Preview_stops_when_the_client_goes()
    {
        var h=await RunningWave();
        var seen=Previews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=2,tick:DeviceHost.FastTick);
        h.ClientGone();
        var n=CountOf(seen);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.FastTick);
        Assert.Equal(n,CountOf(seen));
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>n,tick:DeviceHost.FastTick);
    }

    // A one-colour Wave shows that colour at every placed zone, so the frame is known: the preview carries it as chosen, the LEDs at 50 %.
    [Fact]
    public async Task Preview_is_before_brightness()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(50))).Ok);
        Assert.True((await Send(h,new SetEffectRequest("Wave",new EffectParams{Colors=["#FF0000"]}))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var seen=Previews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1,tick:DeviceHost.FastTick);
        Dictionary<string,string> colors;
        lock(seen)colors=seen[0].Colors;
        Assert.Equal("#FF0000",colors[ApexZone]);
        var dimmed=new Rgb(0xFF,0,0).Scale(50);
        Assert.NotEqual(new Rgb(0xFF,0,0),dimmed);
        Assert.All(LastLeds(Apex),c=>Assert.Equal(dimmed,c));
    }

    // Fix wave W1: while no effect runs the window shows the state's colours, so the host forgets its last frame then, and a one-colour
    // Wave that resumes sends its (unchanged) frame again. Each stop is held over a slow tick, so the tick's own check is what forgets it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_constant_frame_is_sent_again_after_the_effect_stops_and_resumes(bool byLights)
    {
        var h=await RunningWave(new EffectParams{Colors=["#FF0000"]});
        var seen=Previews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1,tick:DeviceHost.FastTick);
        Assert.True((await Send(h,byLights?new SetLightsOnRequest(false):new SetEffectRequest("Static",null))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.Tick),tick:DeviceHost.FastTick); // the armed fast tick runs out first
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Equal(1,CountOf(seen));
        Assert.True((await Send(h,byLights?new SetLightsOnRequest(true):new SetEffectRequest("Wave",null))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        await AdvanceUntil(()=>CountOf(seen)>=2,tick:DeviceHost.FastTick,limit:TimeSpan.FromSeconds(2));
        lock(seen)Assert.True(SameFrame(seen[0].Colors,seen[1].Colors));
    }

    // W5: a frame equal to the last one sent is not sent again: a one-colour Wave sends one frame in a second of ticks, a rainbow more.
    [Fact]
    public async Task A_one_colour_wave_sends_one_frame_a_rainbow_more()
    {
        var h=await RunningWave(new EffectParams{Colors=["#FF0000"]});
        var seen=Previews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.FastTick);
        Assert.Equal(1,CountOf(seen));
        Assert.True((await Send(h,new SetEffectRequest("Wave",new EffectParams()))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.FastTick);
        Assert.True(CountOf(seen)>2,$"{CountOf(seen)} frames");
    }

    // W7: a draft keyed on a device's old key, arriving after a rescan re-keyed that device, is re-keyed too before auto-arrange, so the
    // device takes the draft's point under its new key and gets no fresh cell. The device's own zone colour, already under the new key,
    // doesn't stop that.
    [Fact]
    public async Task SetLayout_remaps_a_stale_draft_before_arranging()
    {
        var h=await Running();
        var k1=LightingKeys.Device("SERIAL-1","I2C: FAKE-1");var k2=LightingKeys.Device("","I2C: FAKE-1");
        var draft=DeskLayouts.Clone(h.State.Settings.Layout);
        var point=draft.Zones[LightingKeys.Zone(k1,0)];
        var list=session.Controllers.ToList();list[0]=list[0] with{Serial=""};session.Controllers=list; // it lost its serial
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(point,h.State.Settings.Layout.Zones[LightingKeys.Zone(k2,0)]);
        Assert.True((await Send(h,new SetZoneColorRequest(LightingKeys.Zone(k2,0),"#00FF00"))).Ok);
        var moved=new DeskPoint(0.5,0.5);Assert.NotEqual(point,moved);
        draft.Zones[LightingKeys.Zone(k1,0)]=moved;
        Assert.True((await Send(h,new SetLayoutRequest(draft))).Ok);
        var saved=new LightingStore(lightingPath).Load().Settings.Layout;
        Assert.Equal(moved,saved.Zones[LightingKeys.Zone(k2,0)]);
        Assert.DoesNotContain(saved.Zones.Keys,k=>k.StartsWith(k1+"#",StringComparison.Ordinal));
        Assert.DoesNotContain(k1,saved.Tower.Members);
        Assert.Equal(saved.Zones,h.State.Settings.Layout.Zones);
    }

    // Lighting desk picture, Task 2: on the owner's rig the device host lays the desk out from its built-in picture.
    const string Board="MSI MAG B650M MORTAR WIFI (MS-7D76)";
    static readonly string HubKey=LightingKeys.Device("SERIAL-6",@"HID: \\?\HID#FAKE-3");
    const string PictureRefused="This PC's layout comes from its built-in picture.",PictureNamed="A window can't choose the desk picture.";
    void UseOwnerRig()=>session=FakeOpenRgbSession.FromFixture(owner:true);
    int Hub=>session.Controllers.FindIndex(c=>c.Name==FakeOpenRgbSession.OwnerHub);
    static PicturePart Part(string id)=>DeskPictures.OwnerRig.Parts.Single(p=>p.Id==id);
    DeskLayout SavedLayout()=>new LightingStore(lightingPath).Load().Settings.Layout;
    int LogCount(string text)=>File.ReadAllText(Path.Combine(dir,"logs","devices.log")).Split(text).Length-1;
    void Without(params string[] names)=>session.Reorder(l=>l.Where(c=>!names.Contains(c.Name)).ToList());

    [Fact]
    public async Task The_picture_replaces_the_layout_at_connect()
    {
        UseOwnerRig();
        var apex=LightingKeys.Device("SERIAL-4",@"HID: \\?\HID#FAKE-1");
        var seed=new DeskLayout();
        seed.Zones[ApexZone]=DeskLayouts.AutoArrange(new DeskLayout(),[new ArrangeDevice(apex,"SteelSeries Apex Pro",[0])]).Zones[ApexZone];
        seed.Zones["gone|x#0"]=new(0.4,0.4);
        seed.Tower.Members.Add(LightingKeys.Device("SERIAL-1","I2C: FAKE-1"));
        seed.Tower.Excluded.Add(LightingKeys.Device("SERIAL-3","I2C: FAKE-3"));
        new LightingStore(lightingPath).Save(new LightingSettings{Layout=seed});
        var h=await Running();
        var saved=SavedLayout();
        Assert.Equal(DeskPictures.OwnerRigName,saved.Picture);
        Assert.True(DeskLayouts.Same(DeskPictures.Layout(DeskPictures.OwnerRig,h.State.Devices),saved));
        Assert.DoesNotContain("gone|x#0",saved.Zones.Keys);
        Assert.Equal(DeskPictures.PointOf(DeskPictures.OwnerRig,Part("keyboard")),saved.Zones[ApexZone]);
        Assert.True(DeskLayouts.Same(saved,h.State.Settings.Layout));
    }

    // The device list changes shape (new indexes) but the picture's layout does not: nothing is saved.
    [Fact]
    public async Task A_rescan_that_changes_nothing_in_the_picture_saves_nothing()
    {
        UseOwnerRig();
        var h=await Running(CountingStore());
        Assert.Equal(1,Volatile.Read(ref saves));
        var layout=h.State.Settings.Layout;
        Assert.Equal(DeskPictures.OwnerRigName,layout.Picture);
        session.Reorder(l=>Enumerable.Reverse(l).ToList());
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(2,LogCount("devices changed:")); // the rescan saw a changed list
        Assert.Equal(1,Volatile.Read(ref saves));
        Assert.True(DeskLayouts.Same(layout,h.State.Settings.Layout));
    }

    // The board alone still holds the saved picture: the hub's zones lose their points, everything else stays.
    [Fact]
    public async Task The_picture_survives_the_hub_going_missing()
    {
        UseOwnerRig();
        var h=await Running(CountingStore());
        var before=h.State.Settings.Layout;
        Assert.Contains(before.Zones.Keys,k=>k.StartsWith(HubKey+"#",StringComparison.Ordinal));
        Without(FakeOpenRgbSession.OwnerHub);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        var after=h.State.Settings.Layout;
        Assert.Equal(DeskPictures.OwnerRigName,after.Picture);
        Assert.DoesNotContain(after.Zones.Keys,k=>k.StartsWith(HubKey+"#",StringComparison.Ordinal));
        var rest=before.Zones.Where(e=>!e.Key.StartsWith(HubKey+"#",StringComparison.Ordinal)).ToDictionary(e=>e.Key,e=>e.Value);
        Assert.Equal(rest,after.Zones);
        Assert.Equal(before.Unplaced,after.Unplaced);
        Assert.True(DeskLayouts.Same(after,SavedLayout()));
    }

    [Fact]
    public async Task The_picture_survives_both_signature_devices_going_missing()
    {
        UseOwnerRig();
        var all=session.Controllers;
        var h=await Running(CountingStore());
        var before=h.State.Settings.Layout;
        Assert.Equal(1,LogCount("desk picture applied")); // the first connect, from no saved picture
        Without(FakeOpenRgbSession.OwnerHub,Board);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        var after=h.State.Settings.Layout;
        Assert.Equal(DeskPictures.OwnerRigName,after.Picture);
        Assert.Empty(after.Tower.Members); // nothing is auto-arranged
        var rest=before.Zones.Where(e=>!e.Key.StartsWith(HubKey+"#",StringComparison.Ordinal)).ToDictionary(e=>e.Key,e=>e.Value);
        Assert.Equal(rest,after.Zones);
        Assert.True(DeskLayouts.Same(DeskPictures.Layout(DeskPictures.OwnerRig,h.State.Devices),after));
        Assert.True(DeskLayouts.Same(after,SavedLayout()));
        session.Controllers=all; // both come back
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.True(DeskLayouts.Same(before,h.State.Settings.Layout));
        Assert.Equal(1,LogCount("desk picture applied"));
        Assert.Equal(1,LogCount("desk picture")); // and no other line about the picture: it was never left, reset or applied again
    }

    // OpenRGB lists devices a few at a time at each start: a saved picture is kept through every partial list, with no log line.
    [Fact]
    public async Task A_partial_device_list_at_start_keeps_a_saved_picture()
    {
        UseOwnerRig();
        var all=session.Controllers;
        var views=all.Select(c=>LightingPaint.View(new LightingSettings(),new LightingDevice(LightingKeys.Device(c.Serial,c.Location),c.Name,c.Type,c.Zones,c.Modes,c.ActiveMode))).ToList();
        new LightingStore(lightingPath).Save(new LightingSettings{Layout=DeskPictures.Layout(DeskPictures.OwnerRig,views)});
        void Only(params string[] names)=>session.Controllers=all.Where(c=>names.Contains(c.Name)).Select((c,i)=>c with{Index=i}).ToList();
        Only("SteelSeries Apex Pro");
        var h=await Running();
        Assert.Single(h.State.Devices);
        Assert.Equal(DeskPictures.OwnerRigName,h.State.Settings.Layout.Picture);
        Only("SteelSeries Apex Pro","Corsair Vengeance RGB DDR5","Gigabyte Radeon RX 6800 XT GAMING OC");
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(4,h.State.Devices.Count); // two RAM sticks share a name
        Assert.Equal(DeskPictures.OwnerRigName,h.State.Settings.Layout.Picture);
        session.Controllers=all;
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.Equal(8,h.State.Devices.Count);
        Assert.True(DeskLayouts.Same(DeskPictures.Layout(DeskPictures.OwnerRig,h.State.Devices),h.State.Settings.Layout));
        Assert.Equal(0,LogCount("desk picture"));
    }

    // A file from another build may name a picture this one doesn't have: reset to the plain desk, the name cleared, saved once. The
    // desk's shape is kept, as Reset positions keeps it: the saved one here is not the shape a new layout starts with.
    [Fact]
    public async Task A_saved_picture_name_this_build_does_not_have_resets_to_the_plain_desk()
    {
        new LightingStore(lightingPath).Save(new LightingSettings{Layout=new DeskLayout{Picture="some-other-picture",Desk=DeskLayouts.Preset(DeskShape.LRight),Zones={[ApexZone]=new(0.9,0.1)}}});
        Assert.NotEqual(DeskShape.LRight,DeskLayouts.ShapeOf(new DeskLayout()));
        var h=await Running(CountingStore());
        var layout=h.State.Settings.Layout;
        Assert.Null(layout.Picture);
        Assert.Equal(DeskShape.LRight,DeskLayouts.ShapeOf(layout));
        Assert.True(DeskLayouts.Same(DeskLayouts.AutoArrange(new DeskLayout{Desk=DeskLayouts.Preset(DeskShape.LRight)},DeskLayouts.Arrangeable(h.State.Devices)),layout));
        Assert.Equal(1,Volatile.Read(ref saves));
        Assert.True(DeskLayouts.Same(layout,SavedLayout()));
    }

    // A second host on the same file and the same devices finds the picture's layout already saved.
    [Fact]
    public async Task A_restart_from_the_saved_picture_saves_nothing()
    {
        UseOwnerRig();
        var first=await Running();
        Assert.Equal(DeskPictures.OwnerRigName,SavedLayout().Picture);
        Assert.True((await Send(first,new StopRequest())).Ok);
        await run!.WaitAsync(RealTimeCap);
        await first.DisposeAsync().AsTask().WaitAsync(RealTimeCap);
        var file=File.ReadAllText(lightingPath);
        var h=await Running(CountingStore());
        Assert.Equal(DeskPictures.OwnerRigName,h.State.Settings.Layout.Picture);
        Assert.Equal(8,h.State.Devices.Count);
        Assert.Equal(0,Volatile.Read(ref saves));
        Assert.Equal(file,File.ReadAllText(lightingPath));
    }

    [Fact]
    public async Task No_picture_on_another_pc()
    {
        var h=await Running();
        var layout=h.State.Settings.Layout;
        Assert.Null(layout.Picture);
        Assert.True(DeskLayouts.Same(DeskLayouts.AutoArrange(new DeskLayout(),DeskLayouts.Arrangeable(h.State.Devices)),layout));
        Assert.True(DeskLayouts.Same(layout,SavedLayout()));
    }

    [Fact]
    public async Task SetLayout_is_refused_while_a_picture_applies()
    {
        UseOwnerRig();
        var h=await Running();
        var layout=h.State.Settings.Layout;
        var file=File.ReadAllText(lightingPath);
        var draft=DeskLayouts.Clone(layout);draft.Picture=null;draft.Zones[ApexZone]=new(0.9,0.1);
        var reply=await Send(h,new SetLayoutRequest(draft));
        Assert.False(reply.Ok);Assert.Equal(PictureRefused,reply.Error);
        Assert.True(DeskLayouts.Same(layout,h.State.Settings.Layout));
        Assert.Equal(file,File.ReadAllText(lightingPath));
        // Missing signature devices don't lift the refusal: the picture is kept.
        Without(FakeOpenRgbSession.OwnerHub,Board);
        Assert.True((await Send(h,new RescanRequest())).Ok);
        reply=await Send(h,new SetLayoutRequest(draft));
        Assert.False(reply.Ok);Assert.Equal(PictureRefused,reply.Error);
    }

    [Fact]
    public async Task SetLayout_refuses_a_layout_that_names_a_picture()
    {
        var h=await Running(CountingStore());
        var layout=h.State.Settings.Layout;
        var saved=Volatile.Read(ref saves);
        var draft=DeskLayouts.Clone(layout);draft.Picture=DeskPictures.OwnerRigName;
        var reply=await Send(h,new SetLayoutRequest(draft));
        Assert.False(reply.Ok);Assert.Equal(PictureNamed,reply.Error);
        Assert.Equal(saved,Volatile.Read(ref saves));
        Assert.Null(h.State.Settings.Layout.Picture);
        Assert.True(DeskLayouts.Same(layout,h.State.Settings.Layout));
        // Where no picture applies, a layout is accepted as before.
        draft.Picture=null;draft.Zones[ApexZone]=new(0.9,0.1);
        Assert.True((await Send(h,new SetLayoutRequest(draft))).Ok);
        Assert.Equal(saved+1,Volatile.Read(ref saves));
        Assert.Equal(new DeskPoint(0.9,0.1),SavedLayout().Zones[ApexZone]);
        Assert.Equal(new DeskPoint(0.9,0.1),h.State.Settings.Layout.Zones[ApexZone]);
    }

    // The effect is painted at the picture's points: each part's first LED is the wave at that part's point, at the frame's effect time,
    // which is the fake time since the host was made (its epoch).
    [Fact]
    public async Task Wave_follows_the_pictures_points()
    {
        UseOwnerRig();
        var start=time.GetUtcNow();
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(100))).Ok);
        var wave=new EffectParams{Colors=["#FF0000","#00FF00","#0000FF"]};
        Assert.True((await Send(h,new SetEffectRequest("Wave",wave))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var from=session.LedCount;
        await AdvanceUntil(()=>session.Leds.Skip(from).Any(l=>l.Index==Hub),tick:DeviceHost.FastTick);
        await Until(()=>time.Waiting(DeviceHost.FastTick)); // that tick's paint is done and the clock has not moved since
        var t=(time.GetUtcNow()-start).TotalMilliseconds;
        var settings=h.State.Settings;
        Assert.True(Rgb.TryParse(settings.DefaultColor,out var zoneColour));
        Assert.Empty(settings.ZoneColors);
        Rgb Expected(string id)=>LightingEffects.Evaluate(EffectKind.Wave,DeskPictures.PointOf(DeskPictures.OwnerRig,Part(id)),t,wave,zoneColour);
        var hub=LastLeds(Hub);
        Rgb[] seen=[hub[0],hub[16],LastLeds(Apex)[0]]; // the hub's zones hold 8, 8, 8 and 20 LEDs: zone 2 starts at 16
        Assert.Equal([Expected("fan1"),Expected("fan3"),Expected("keyboard")],seen);
        Assert.Equal(3,seen.Distinct().Count());
        // Left to right, the three sit in that order along the palette. Where a painted colour lies on the red, green, blue palette is
        // read from the colour itself, not from the effect's own maths: each third blends one colour into the next and leaves the third at 0.
        static double Along(Rgb c)=>c.B==0?c.G/(double)(c.R+c.G)/3:c.R==0?(1+c.B/(double)(c.G+c.B))/3:(2+c.R/(double)(c.B+c.R))/3;
        static double Ahead(Rgb from,Rgb to){var d=Along(to)-Along(from);return d-Math.Floor(d);}
        static double X(string id)=>DeskPictures.PointOf(DeskPictures.OwnerRig,Part(id)).X;
        Assert.All(seen,c=>Assert.Contains((byte)0,new[]{c.R,c.G,c.B}));
        Assert.True(X("fan1")<X("fan3")&&X("fan3")<X("keyboard"));
        const double wavelength=1.6; // Wave's default spread of 50: the palette repeats every 1.6 desk widths
        Assert.Equal((X("fan3")-X("fan1"))/wavelength,Ahead(seen[0],seen[1]),0.01);   // about 0.10 of the palette ahead of fan 1; 8-bit colour reads to well within 0.01
        Assert.Equal((X("keyboard")-X("fan1"))/wavelength,Ahead(seen[0],seen[2]),0.01);   // about 0.29 ahead
    }

    // Step 3a, Task 4: device tuning and keyboard modes through the pipe.
    static readonly string ApexKey=LightingKeys.Device("SERIAL-4",@"HID: \\?\HID#FAKE-1");
    static readonly string DramKey=LightingKeys.Device("SERIAL-1","I2C: FAKE-1");
    static readonly string MouseKey=AeroxIdentity.Key;   // lighting 4: the cable mouse is listed under its fixed key
    static readonly string NodeKey=LightingKeys.Device("SERIAL-7",@"HID: \\?\HID#FAKE-4");
    bool ApexShows(Rgb c){try{return LastLeds(Apex).All(x=>x==c);}catch(InvalidOperationException){return false;}}
    void DropApexMatrix()=>session.Controllers[Apex]=session.Controllers[Apex] with{Zones=session.Controllers[Apex].Zones.Select(z=>z with{Matrix=null}).ToList()};
    // Each preview frame whole, per-light colours included.
    List<PreviewEvent> FullPreviews(DeviceHost h)
    {
        var seen=new List<PreviewEvent>();
        h.PreviewChanged+=e=>{lock(seen)seen.Add(e);};
        return seen;
    }
    static string[] Chunks(string packed)=>Enumerable.Range(0,packed.Length/6).Select(i=>packed.Substring(i*6,6)).ToArray();

    [Fact]
    public async Task Other_changes_keep_paint_layers_and_looks()
    {
        var seeded=new LightingSettings();
        seeded.Devices[ApexKey]=new DeviceTuning
        {
            Keys=new(){[3]=new(){Name="W",Color="#112233"}},
            Layers=[new(){Kind="ripple",Lights=[new(){Index=3,Name="W"}]}],
            Looks=[new(){Name="Gaming",Keys=new(){[4]=new(){Name="A",Color="#445566"}}}],
            Look="Gaming",
        };
        File.WriteAllText(lightingPath,LightingSettingsCodec.Write(seeded));
        var want=JsonSerializer.Serialize(seeded.Devices[ApexKey]);
        var h=await Running();
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,50))).Ok);
        // Compare the paint on copies, with the brightness (which the request changed) cleared; the state's own settings are never touched.
        string Paint(DeviceTuning t){var c=JsonSerializer.Deserialize<DeviceTuning>(JsonSerializer.Serialize(t))!;c.Brightness=null;return JsonSerializer.Serialize(c);}
        var inState=h.State.Settings.Devices[ApexKey];
        Assert.Equal(50,inState.Brightness);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Equal(40,saved.Brightness);Assert.Equal(3,saved.Version);Assert.Equal(50,saved.Devices[ApexKey].Brightness);
        var wantNoBrightness=JsonSerializer.Deserialize<DeviceTuning>(want)!;
        Assert.Equal(Paint(wantNoBrightness),Paint(inState));
        Assert.Equal(Paint(wantNoBrightness),Paint(saved.Devices[ApexKey]));
    }

    [Fact]
    public async Task SetDeviceTuning_saves_and_repaints()
    {
        var h=await Running();
        await AdvanceUntil(()=>ApexShows(SteelBlue));
        var from=session.LedCount;
        var reply=await Send(h,new SetDeviceTuningRequest(ApexKey,50){Id=3});
        Assert.True(reply.Ok);Assert.Equal(3,reply.Id);
        await AdvanceUntil(()=>session.Leds.Skip(from).Any(l=>l.Index==Apex));
        var half=SteelBlue.Scale(50);
        Assert.NotEqual(SteelBlue,half);
        Assert.All(session.Leds.Skip(from).First(l=>l.Index==Apex).Colors,c=>Assert.Equal(half,c));
        Assert.Equal(50,h.State.Settings.Devices[ApexKey].Brightness);
        Assert.Equal(50,Assert.Single(h.State.Devices,d=>d.Key==ApexKey).Brightness);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Equal(50,saved.Devices[ApexKey].Brightness);Assert.Equal(2,saved.Version);
    }

    [Fact]
    public async Task SetDeviceTuning_leaves_unnamed_settings_alone()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,50,new DeviceCorrection{R=150,G=100,B=80}))).Ok);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"breathing",20))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,70))).Ok);
        var t=h.State.Settings.Devices[ApexKey];
        Assert.Equal(70,t.Brightness);
        Assert.Equal((150,100,80),(t.Correction!.R,t.Correction.G,t.Correction.B));
        Assert.Equal(("breathing",20),(t.Mode!.Kind,t.Mode.Speed));
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,null,new DeviceCorrection{R=10,G=20,B=30}))).Ok);
        t=h.State.Settings.Devices[ApexKey];
        Assert.Equal(70,t.Brightness);
        Assert.Equal((10,20,30),(t.Correction!.R,t.Correction.G,t.Correction.B));
        Assert.Equal("breathing",t.Mode!.Kind);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey))).Ok); // names nothing: accepted, and nothing changes
        t=h.State.Settings.Devices[ApexKey];
        Assert.Equal((70,10,"breathing"),(t.Brightness,t.Correction!.R,t.Mode!.Kind));
    }

    [Fact]
    public async Task ResetCorrection_removes_it()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,null,new DeviceCorrection{R=150,G=100,B=80}))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,null,null,true))).Ok);
        Assert.False(h.State.Settings.Devices.ContainsKey(ApexKey)); // nothing else was set, so the entry goes
        Assert.Null(Assert.Single(h.State.Devices,d=>d.Key==ApexKey).Correction);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,60,new DeviceCorrection{R=150,G=100,B=80}))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,null,null,true))).Ok);
        Assert.Null(h.State.Settings.Devices[ApexKey].Correction);
        Assert.Equal(60,h.State.Settings.Devices[ApexKey].Brightness);
        var saved=new LightingStore(lightingPath).Load().Settings;
        Assert.Null(saved.Devices[ApexKey].Correction);Assert.Equal(60,saved.Devices[ApexKey].Brightness);
    }

    [Fact]
    public async Task SetDeviceTuning_out_of_range_replies_the_sentence_and_changes_nothing()
    {
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        Assert.Equal("Device brightness must be 0 to 100.",(await Send(h,new SetDeviceTuningRequest(ApexKey,101))).Error);
        Assert.Equal("Device brightness must be 0 to 100.",(await Send(h,new SetDeviceTuningRequest(ApexKey,-1))).Error);
        Assert.Equal("Colour correction must be 0 to 200.",(await Send(h,new SetDeviceTuningRequest(ApexKey,null,new DeviceCorrection{R=201}))).Error);
        Assert.Equal("Colour correction must be 0 to 200.",(await Send(h,new SetDeviceTuningRequest(ApexKey,50,new DeviceCorrection{B=-1}))).Error);
        Assert.Equal("Mode speed must be 0 to 100.",(await Send(h,new SetDeviceModeRequest(ApexKey,"cycle",101))).Error);
        Assert.Equal("Lighting mode is not known.",(await Send(h,new SetDeviceModeRequest(ApexKey,"Cycle"))).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.Equal(before,Volatile.Read(ref saves));
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,0,new DeviceCorrection{R=200,G=0,B=0}))).Ok); // the bounds themselves are accepted
    }

    [Fact]
    public async Task A_request_for_an_unknown_device_is_refused()
    {
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        var a=await Send(h,new SetDeviceTuningRequest("ghost|nowhere",50){Id=4});
        Assert.False(a.Ok);Assert.Equal(HostReplyCodes.UnknownDevice,a.Error);Assert.Equal(4,a.Id);
        var b=await Send(h,new SetDeviceModeRequest("ghost|nowhere","cycle"));
        Assert.False(b.Ok);Assert.Equal(HostReplyCodes.UnknownDevice,b.Error);
        Assert.Equal(HostReplyCodes.UnknownDevice,(await Send(h,new SetDeviceModeRequest("ghost|nowhere",null))).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.Equal(before,Volatile.Read(ref saves));
    }

    [Fact]
    public async Task An_unplugged_devices_entry_is_kept()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,40))).Ok);
        session.Controllers=session.Controllers.Where(c=>c.Name!="SteelSeries Apex Pro").ToList();
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Assert.DoesNotContain(h.State.Devices,d=>d.Key==ApexKey);
        Assert.Equal(40,h.State.Settings.Devices[ApexKey].Brightness);
        Assert.Equal(40,new LightingStore(lightingPath).Load().Settings.Devices[ApexKey].Brightness);
        Assert.Equal(HostReplyCodes.UnknownDevice,(await Send(h,new SetDeviceTuningRequest(ApexKey,60))).Error);
        Assert.Equal(HostReplyCodes.UnknownDevice,(await Send(h,new SetDeviceModeRequest(ApexKey,"cycle"))).Error);
        Assert.Equal(40,h.State.Settings.Devices[ApexKey].Brightness);
    }

    [Fact]
    public async Task SetDeviceMode_refuses_what_the_device_cant_do()
    {
        DropApexMatrix();
        var h=await Running();
        Assert.Equal("This keyboard doesn't report its key positions.",(await Send(h,new SetDeviceModeRequest(ApexKey,"wave"))).Error);
        Assert.Equal("Lighting modes are for keyboards.",(await Send(h,new SetDeviceModeRequest(MouseKey,"cycle"))).Error);
        Assert.Equal("Lighting modes are for keyboards.",(await Send(h,new SetDeviceModeRequest(MouseKey,"static"))).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"breathing"))).Ok); // a mode it can do
        Assert.True((await Send(h,new SetDeviceModeRequest(MouseKey,null))).Ok); // following the desk is never refused
        Assert.Equal("breathing",Assert.Single(h.State.Settings.Devices).Value.Mode!.Kind);
    }

    [Fact]
    public async Task SetDeviceMode_null_returns_to_the_desk_effect()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"cycle",60))).Ok);
        Assert.Equal(("cycle",60),(h.State.Settings.Devices[ApexKey].Mode!.Kind,h.State.Settings.Devices[ApexKey].Mode!.Speed));
        Assert.Equal("cycle",Assert.Single(h.State.Devices,d=>d.Key==ApexKey).LightingMode!.Kind);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        await AdvanceUntil(()=>{try{return LastLeds(Apex).Any(c=>c!=SteelBlue);}catch(InvalidOperationException){return false;}},tick:DeviceHost.FastTick);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,null))).Ok);
        Assert.False(h.State.Settings.Devices.ContainsKey(ApexKey));
        Assert.Null(Assert.Single(h.State.Devices,d=>d.Key==ApexKey).LightingMode);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.Tick),tick:DeviceHost.FastTick);
        await AdvanceUntil(()=>ApexShows(SteelBlue));
    }

    [Fact]
    public async Task Tuning_a_hardware_device_is_saved_but_sends_no_frame()
    {
        var h=await Running();
        var node=session.Controllers.FindIndex(c=>c.Name==LightingDeviceService.NodePro);
        Assert.Equal(PaintKind.Hardware,Assert.Single(h.State.Devices,d=>d.Key==NodeKey).Kind);
        var modes=session.Modes.Count(m=>m.Index==node);
        Assert.True((await Send(h,new SetDeviceTuningRequest(NodeKey,30,new DeviceCorrection{R=50,G=100,B=100}))).Ok);
        Assert.Equal(30,new LightingStore(lightingPath).Load().Settings.Devices[NodeKey].Brightness);
        var view=Assert.Single(h.State.Devices,d=>d.Key==NodeKey);
        Assert.Equal((30,50),(view.Brightness,view.Correction!.R));
        Assert.Equal(PaintKind.Hardware,view.Kind);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.DoesNotContain(session.Leds,l=>l.Index==node);
        Assert.Equal(modes,session.Modes.Count(m=>m.Index==node)); // its hardware effect isn't sent again either
    }

    [Fact]
    public async Task Tick_is_fast_for_an_animated_keyboard_mode_under_a_static_desk()
    {
        var h=await Running();
        await Until(()=>time.Waiting(DeviceHost.Tick));
        Assert.Equal("Static",h.State.Settings.Effect.Kind);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"static"))).Ok); // a still mode: the tick stays slow
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.False(time.Waiting(DeviceHost.FastTick));
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"breathing"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var from=session.LedCount;
        await AdvanceUntil(()=>Frames(Apex,from).Count>=3,tick:DeviceHost.FastTick);
    }

    [Theory]
    [InlineData("follow")]
    [InlineData("lights off")]
    [InlineData("hardware")]
    public async Task Tick_is_fast_for_an_animated_keyboard_mode_under_a_static_desk_and_slow_again_when_it_stops(string stop)
    {
        // The keyboard has a saved hardware effect it doesn't offer yet, so it is painted until it does.
        File.WriteAllText(lightingPath,"{\"Version\":1,\"HardwareModes\":{"+System.Text.Json.JsonSerializer.Serialize(ApexKey)+":\"Spectrum\"}}");
        var h=await Running();
        Assert.Equal(PaintKind.Direct,Assert.Single(h.State.Devices,d=>d.Key==ApexKey).Kind);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"wave"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        switch(stop)
        {
            case "follow":Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,null))).Ok);break;
            case "lights off":Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);break;
            default:
                session.Controllers[Apex]=session.Controllers[Apex] with{Modes=["Direct","Spectrum"]};
                Assert.True((await Send(h,new RescanRequest())).Ok);
                Assert.Equal(PaintKind.Hardware,Assert.Single(h.State.Devices,d=>d.Key==ApexKey).Kind);
                Assert.Equal("wave",h.State.Settings.Devices[ApexKey].Mode!.Kind); // its mode is kept
                break;
        }
        await AdvanceUntil(()=>time.Waiting(DeviceHost.Tick),tick:DeviceHost.FastTick);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick); // stalls if the loop goes back to FastTick
        Assert.False(time.Waiting(DeviceHost.FastTick));
    }

    [Fact]
    public async Task Preview_flows_for_an_animated_keyboard_mode_under_a_static_desk()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"wave"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        // A second frame comes as the wave moves. (The keyboard's zone colour moves with its first light, so this alone doesn't show that
        // a change in the lights by themselves sends a frame; Same_frame_needs_the_same_lights_too does.)
        await AdvanceUntil(()=>CountOf(seen)>=2,tick:DeviceHost.FastTick);
        List<PreviewEvent> frames;
        lock(seen)frames=seen.ToList();
        Assert.Contains(ApexZone,frames[0].Colors.Keys);
        Assert.Contains(LightingKeys.Zone(DramKey,0),frames[0].Colors.Keys);
        var lights=frames[0].Lights;
        Assert.NotNull(lights);
        var packed=Assert.Single(lights!); // the keyboard alone has a key map; the DRAM and the rest have none
        Assert.Equal(ApexKey,packed.Key);
        Assert.Equal(112*6,packed.Value.Length);
        Assert.Matches("^[0-9A-F]+$",packed.Value);
        Assert.True(Chunks(packed.Value).Distinct().Count()>1); // a rainbow across the keys
        Assert.NotEqual(packed.Value,frames[1].Lights![ApexKey]);
    }

    // Whole-branch review 1, D7: a frame whose zone colours match the last one but whose lights differ is a new frame, and so is one whose
    // lights appear or go; the same colours and lights are not.
    [Fact]
    public void Same_frame_needs_the_same_lights_too()
    {
        static Dictionary<string,string> D(string k,string v)=>new(){[k]=v};
        var colors=D("z|1#0","#102030");
        Assert.True(DeviceHost.SameFrame(new(colors,D("k","AABBCC")),new(D("z|1#0","#102030"),D("k","AABBCC"))));
        Assert.True(DeviceHost.SameFrame(new(colors),new(D("z|1#0","#102030"))));
        Assert.False(DeviceHost.SameFrame(new(colors,D("k","AABBCC")),new(colors,D("k","AABBCD"))));
        Assert.False(DeviceHost.SameFrame(new(colors),new(colors,D("k","AABBCC"))));
        Assert.False(DeviceHost.SameFrame(new(colors,D("k","AABBCC")),new(colors)));
        Assert.False(DeviceHost.SameFrame(new(colors,D("k","AABBCC")),new(D("z|1#0","#102031"),D("k","AABBCC"))));
    }

    // Whole-branch review 1, D6: the packed lights are the frame's own base colours, light 0 first, each as R, G, B. The frame is painted at
    // the host's time when it is raised: the clock moves only while the loop waits on its tick.
    [Fact]
    public async Task Preview_lights_pack_each_light_as_RRGGBB_in_light_order()
    {
        var start=time.GetUtcNow(); // the host's epoch: it is built before the clock moves
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"wave"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var seen=new List<(PreviewEvent Frame,double Ms)>();
        h.PreviewChanged+=e=>{lock(seen)seen.Add((e,(time.GetUtcNow()-start).TotalMilliseconds));};
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>{lock(seen)return seen.Count>=2;},tick:DeviceHost.FastTick);
        List<(PreviewEvent Frame,double Ms)> frames;
        lock(seen)frames=seen.ToList();
        var c=session.Controllers[Apex];
        var device=new LightingDevice(ApexKey,c.Name,c.Type,c.Zones,c.Modes,c.ActiveMode,c.LedNames);
        foreach(var (frame,ms) in frames.Take(2))
        {
            var expected=string.Concat(LightingPaint.Lights(h.State.Settings,device,ms).Select(x=>x.ToHex()[1..]));
            Assert.Equal(112*6,expected.Length);
            Assert.Equal(expected,frame.Lights![ApexKey]);
        }
    }

    [Fact]
    public async Task Preview_lights_are_before_tuning()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,0))).Ok);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"wave"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1,tick:DeviceHost.FastTick);
        string packed;
        lock(seen)packed=seen[0].Lights![ApexKey];
        Assert.All(Chunks(packed),c=>Assert.NotEqual("000000",c));
        Assert.All(LastLeds(Apex),c=>Assert.Equal(Rgb.Black,c)); // the keyboard itself is dark
    }

    // The mode's phase is the host's time times its speed, so a brightness or correction change leaves it where the clock has it.
    [Fact]
    public async Task A_tuning_change_does_not_restart_the_mode()
    {
        var start=time.GetUtcNow(); // the host's epoch: it is built before the clock moves
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"cycle"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        await RunFor(TimeSpan.FromMilliseconds(700),DeviceHost.FastTick);
        // The last tick painted the keyboard just now, so the change's own paint is held by the 33 ms cap and the next tick sends it.
        var from=session.LedCount;
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,50,new DeviceCorrection{R=120,G=80,B=100}))).Ok);
        await Elapse(DeviceHost.FastTick);
        await Until(()=>session.Leds.Skip(from).Any(l=>l.Index==Apex));
        var t=(time.GetUtcNow()-start).TotalMilliseconds;
        Assert.True(t>700);
        var c=session.Controllers[Apex];
        var expected=LightingPaint.Paint(h.State.Settings,new LightingDevice(ApexKey,c.Name,c.Type,c.Zones,c.Modes,c.ActiveMode,c.LedNames),t).Colors;
        Assert.Equal(expected,session.Leds.Skip(from).First(l=>l.Index==Apex).Colors);
    }

    [Fact]
    public async Task Reply_precedes_the_state_broadcast_for_a_device_tuning_over_a_real_pipe()
    {
        var h=Build();
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
        await DeviceHostProtocol.WriteAsync(client,new SetDeviceTuningRequest(ApexKey,40){Id=6},cts.Token);
        var first=Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.True(first.Ok);Assert.Equal(6,first.Id);
        var second=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(0,second.Id);Assert.Equal(40,Assert.Single(second.State.Devices,d=>d.Key==ApexKey).Brightness);
        cts.Cancel();
    }

    // Ruling 1: a keyboard with no key map running a mode gets no per-light colours, but its zone colours move, so frames keep flowing.
    [Fact]
    public async Task Preview_flows_for_a_mode_on_a_keyboard_without_a_key_map()
    {
        DropApexMatrix();
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"cycle"))).Ok);
        await AdvanceUntil(()=>time.Waiting(DeviceHost.FastTick));
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=3,tick:DeviceHost.FastTick);
        List<PreviewEvent> frames;
        lock(seen)frames=seen.ToList();
        Assert.All(frames,f=>Assert.Null(f.Lights));
        Assert.NotEqual(frames[0].Colors[ApexZone],frames[1].Colors[ApexZone]);
    }

    // Ruling 3: entries for absent devices count toward the cap, so a first entry for a present device is refused at 256.
    [Fact]
    public async Task The_257th_device_entry_is_refused()
    {
        var entries=string.Join(",",Enumerable.Range(0,DeviceTunings.MaxEntries).Select(i=>$"\"gone-{i}|HID: gone-{i}\":{{\"Brightness\":50}}"));
        File.WriteAllText(lightingPath,"{\"Version\":2,\"Devices\":{"+entries+"}}");
        var h=await Running(CountingStore());
        Assert.Equal(DeviceTunings.MaxEntries,h.State.Settings.Devices.Count);
        await Until(()=>time.Waiting(DeviceHost.Tick));
        var before=Volatile.Read(ref saves);
        var reply=await Send(h,new SetDeviceTuningRequest(ApexKey,50));
        Assert.False(reply.Ok);Assert.Equal("Too many device entries.",reply.Error);
        Assert.Equal(before,Volatile.Read(ref saves));
        Assert.False(h.State.Settings.Devices.ContainsKey(ApexKey));
        Assert.Equal(DeviceTunings.MaxEntries,new LightingStore(lightingPath).Load().Settings.Devices.Count);
    }
    // Lighting 3b, Task 5: key paint, layers and saved looks through the pipe, on the fixture keyboard (lights "Key 0".."Key 111").
    static PaintedKey PK(string name,string color)=>new(){Name=name,Color=color};
    static Dictionary<int,PaintedKey> Keys(params (int Index,string Color)[] keys)=>keys.ToDictionary(k=>k.Index,k=>PK("Key "+k.Index,k.Color));
    DeviceView ApexView(DeviceHost h)=>Assert.Single(h.State.Devices,d=>d.Key==ApexKey);
    DeviceTuning? ApexEntry(DeviceHost h)=>h.State.Settings.Devices.TryGetValue(ApexKey,out var t)?t:null;
    LightingSettings SavedSettings()=>new LightingStore(lightingPath).Load().Settings;
    static string KeysText(Dictionary<int,PaintedKey>? k)=>k is null?"null":string.Join(",",k.OrderBy(e=>e.Key).Select(e=>$"{e.Key}={e.Value.Name}/{e.Value.Color}"));
    // Read sharing write (and delete, for a roll-over). HostLog appends with FileShare.Read only, so File.ReadAllText's FileShare.Read made
    // the host's write fail while the test read, and HostLog drops a line it can't write (a "suspend" line lost: a 15 s wait that fails);
    // or the read failed while a write was open (IOException).
    string[] LogLines(string contains)
    {
        using var fs=new FileStream(Path.Combine(dir,"logs","devices.log"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var r=new StreamReader(fs);
        return r.ReadToEnd().Split('\n').Where(l=>l.Contains(contains)).ToArray();
    }

    [Fact]
    public async Task Painting_keys_is_saved_replied_then_stated()
    {
        var h=Build();
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
        await DeviceHostProtocol.WriteAsync(client,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(5,"#00FF00"))){Id=7},cts.Token);
        var first=Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.True(first.Ok);Assert.Equal(7,first.Id);
        var second=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal("0=Key 0/#FF0000,5=Key 5/#00FF00",KeysText(Assert.Single(second.State.Devices,d=>d.Key==ApexKey).Paint));
        Assert.Equal("0=Key 0/#FF0000,5=Key 5/#00FF00",KeysText(second.State.Settings.Devices[ApexKey].Keys));
        var saved=SavedSettings();
        Assert.Equal(3,saved.Version);
        Assert.Equal("0=Key 0/#FF0000,5=Key 5/#00FF00",KeysText(saved.Devices[ApexKey].Keys));
        cts.Cancel();
    }

    [Fact]
    public async Task Painting_replaces_the_whole_set()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(1,"#00FF00"))))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((2,"#0000FF"))))).Ok);
        Assert.Equal("2=Key 2/#0000FF",KeysText(ApexEntry(h)!.Keys));
        Assert.Equal("2=Key 2/#0000FF",KeysText(ApexView(h).Paint));
        Assert.Equal("2=Key 2/#0000FF",KeysText(SavedSettings().Devices[ApexKey].Keys));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_empty_set_clears_paint_and_returns_the_file_to_its_earlier_version(bool empty)
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,50))).Ok);
        var before=File.ReadAllText(lightingPath);
        Assert.Equal(2,SavedSettings().Version);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.Equal(3,SavedSettings().Version);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,empty?new():null))).Ok);
        Assert.Equal(before,File.ReadAllText(lightingPath)); // the file 3a wrote, byte for byte
        Assert.Null(ApexEntry(h)!.Keys);Assert.Null(ApexView(h).Paint);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,100))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,null))).Ok);
        Assert.Null(ApexEntry(h)); // nothing left: the entry goes
        Assert.Equal(1,SavedSettings().Version);
    }

    [Fact]
    public async Task A_key_beyond_the_keyboard_is_refused()
    {
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        var a=await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(112,"#00FF00"))){Id=5});
        Assert.False(a.Ok);Assert.Equal(HostReplyCodes.BadKey,a.Error);Assert.Equal(5,a.Id);
        Assert.Equal(HostReplyCodes.BadKey,(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="fade",Lights=[new LayerLight{Index=112,Name="Key 112"}]}]))).Error);
        // The ValidateSetup sentences, through the validation every change goes through.
        Assert.Equal("A colour is invalid.",(await Send(h,new SetDeviceKeysRequest(ApexKey,new(){[0]=PK("Key 0","red")}))).Error);
        Assert.Equal("A light's name must be 1 to 128 characters.",(await Send(h,new SetDeviceKeysRequest(ApexKey,new(){[0]=PK("","#FF0000")}))).Error);
        Assert.Equal("A light's name must be 1 to 128 characters.",(await Send(h,new SetDeviceKeysRequest(ApexKey,new(){[0]=PK(new string('n',129),"#FF0000")}))).Error);
        Assert.Equal("Too many painted keys.",(await Send(h,new SetDeviceKeysRequest(ApexKey,Enumerable.Range(0,DeviceLayers.MaxKeys+1).ToDictionary(i=>i,i=>PK("Key "+i,"#FF0000"))))).Error);
        Assert.Equal("Layer strength must be 0 to 100.",(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="color",Strength=101}]))).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.Equal(before,Volatile.Read(ref saves));
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((111,"#00FF00"))))).Ok); // the last light is on it
    }

    [Fact]
    public async Task A_device_that_is_not_a_paintable_keyboard_is_refused()
    {
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new SetDeviceKeysRequest(DramKey,new(){[0]=PK("LED 0","#FF0000")}))).Error);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new SetDeviceLayersRequest(DramKey,[new DeviceLayer{Kind="color"}]))).Error);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new SaveLookRequest(DramKey,"Gaming"))).Error);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new ChooseLookRequest(DramKey,"Gaming"))).Error);
        Assert.Equal(before,Volatile.Read(ref saves));
        // Clearing, renaming and deleting work on any listed device, so one that lost its names can still be cleaned up.
        Assert.True((await Send(h,new SetDeviceKeysRequest(DramKey,null))).Ok);
        Assert.True((await Send(h,new SetDeviceLayersRequest(DramKey,[]))).Ok);
        Assert.Equal(HostReplyCodes.NoLook,(await Send(h,new RenameLookRequest(DramKey,"Gaming","Work"))).Error);
        Assert.Equal(HostReplyCodes.NoLook,(await Send(h,new DeleteLookRequest(DramKey,"Gaming"))).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.False(Assert.Single(h.State.Devices,d=>d.Key==DramKey).CanPaint);
        Assert.True(ApexView(h).CanPaint);
    }

    [Fact]
    public async Task A_keyboard_that_lost_its_names_can_still_be_cleaned_up()
    {
        var seeded=new LightingSettings();
        seeded.Devices[ApexKey]=new DeviceTuning{Keys=Keys((0,"#FF0000")),Layers=[new DeviceLayer{Kind="color"}],Looks=[new DeviceLook{Name="Gaming"},new DeviceLook{Name="Work"}],Look="Gaming"};
        File.WriteAllText(lightingPath,LightingSettingsCodec.Write(seeded));
        session.Controllers[Apex]=session.Controllers[Apex] with{LedNames=null};
        var h=await Running();
        Assert.False(ApexView(h).CanPaint);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((1,"#FF0000"))))).Error);
        Assert.Equal(HostReplyCodes.NotPaintable,(await Send(h,new ChooseLookRequest(ApexKey,"Work"))).Error);
        Assert.True((await Send(h,new RenameLookRequest(ApexKey,"Work","Office"))).Ok);
        Assert.True((await Send(h,new DeleteLookRequest(ApexKey,"Gaming"))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,null))).Ok);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,null))).Ok);
        var t=ApexEntry(h)!;
        Assert.Null(t.Keys);Assert.Null(t.Layers);Assert.Null(t.Look);Assert.Equal(["Office"],t.Looks!.Select(l=>l.Name));
    }

    [Fact]
    public async Task An_unlisted_device_is_refused()
    {
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        const string ghost="ghost|nowhere";
        HostMessage[] all=[new SetDeviceKeysRequest(ghost,new(){[0]=PK("A","#FF0000")}),new SetDeviceKeysRequest(ghost,null),new SetDeviceLayersRequest(ghost,[new DeviceLayer()]),
            new SetDeviceLayersRequest(ghost,null),new SaveLookRequest(ghost,"A"),new ChooseLookRequest(ghost,"A"),new RenameLookRequest(ghost,"A","B"),new DeleteLookRequest(ghost,"A")];
        foreach(var m in all) Assert.Equal(HostReplyCodes.UnknownDevice,(await Send(h,m)).Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.Equal(before,Volatile.Read(ref saves));
    }

    [Fact]
    public async Task Layers_are_saved_and_shown()
    {
        var h=await Running();
        List<DeviceLayer> layers=
        [
            new(){Kind="color",Color="#102030",Strength=50,Mix="cover",Lights=[new(){Index=0,Name="Key 0"}]},
            new(){Kind="effect",Effect="breathing",Speed=70,Lights=[]},
            new(){Kind="fade",Color="#FFFFFF",DurationMs=1200},
            new(){Kind="ripple",Color="#00FF00",DurationMs=100,Strength=0,Lights=[new(){Index=111,Name="Key 111"},new(){Index=5,Name="Key 5"}]},
        ];
        var want=JsonSerializer.Serialize(layers);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,layers))).Ok);
        layers[0].Color="#FFFFFF"; // the host keeps its own copy
        Assert.Equal(want,JsonSerializer.Serialize(ApexEntry(h)!.Layers));
        Assert.Equal(want,JsonSerializer.Serialize(ApexView(h).Layers));
        Assert.Equal([0,0,0,0],ApexView(h).SkippedLayerLights!);
        var saved=SavedSettings();
        Assert.Equal(3,saved.Version);
        Assert.Equal(want,JsonSerializer.Serialize(saved.Devices[ApexKey].Layers));
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,null))).Ok);
        Assert.Null(ApexEntry(h));Assert.Null(ApexView(h).Layers);
    }

    [Fact]
    public async Task Nine_layers_are_refused()
    {
        var h=await Running();
        var reply=await Send(h,new SetDeviceLayersRequest(ApexKey,Enumerable.Range(0,9).Select(_=>new DeviceLayer{Kind="color"}).ToList()));
        Assert.False(reply.Ok);Assert.Equal(HostReplyCodes.TooManyLayers,reply.Error);
        Assert.Empty(h.State.Settings.Devices);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,Enumerable.Range(0,8).Select(_=>new DeviceLayer{Kind="color"}).ToList()))).Ok);
        Assert.Equal(8,ApexEntry(h)!.Layers!.Count);
    }

    [Fact]
    public async Task An_unknown_layer_kind_is_refused_in_a_request_but_kept_from_a_file()
    {
        var seeded=new LightingSettings();
        seeded.Devices[ApexKey]=new DeviceTuning{Layers=[new DeviceLayer{Kind="morrowind"}]};
        File.WriteAllText(lightingPath,LightingSettingsCodec.Write(seeded));
        var h=await Running(CountingStore());
        var before=Volatile.Read(ref saves);
        Assert.Equal(HostReplyCodes.BadLayer,(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="morrowind"}]))).Error);
        Assert.Equal(HostReplyCodes.BadLayer,(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="color",Effect="plasma"}]))).Error);
        Assert.Equal(HostReplyCodes.BadLayer,(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="color",Mix="multiply"}]))).Error);
        Assert.Equal(before,Volatile.Read(ref saves));
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"Old"))).Ok);
        var saved=SavedSettings().Devices[ApexKey];
        Assert.Equal("morrowind",Assert.Single(saved.Layers!).Kind);
        Assert.Equal("morrowind",Assert.Single(Assert.Single(saved.Looks!).Layers!).Kind);
        Assert.Equal("morrowind",Assert.Single(ApexView(h).Layers!).Kind);
    }

    [Fact]
    public async Task Looks_save_choose_rename_delete()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"breathing",30))).Ok);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="fade"}]))).Ok);
        // Save: copies of the mode, keys and layers, and the look is current.
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"  Gaming  "))).Ok);
        var t=ApexEntry(h)!;var look=Assert.Single(t.Looks!);
        Assert.Equal("Gaming",look.Name);Assert.Equal("Gaming",t.Look);
        Assert.Equal(("breathing",30),(look.Mode!.Kind,look.Mode.Speed));Assert.Equal("0=Key 0/#FF0000",KeysText(look.Keys));Assert.Equal("fade",Assert.Single(look.Layers!).Kind);
        var v=ApexView(h);Assert.Equal(["Gaming"],v.Looks!);Assert.Equal("Gaming",v.Look);Assert.False(v.LookChanged);
        // A change marks the look changed; the look itself keeps what was saved.
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((1,"#00FF00"))))).Ok);
        Assert.Equal("0=Key 0/#FF0000",KeysText(ApexEntry(h)!.Looks![0].Keys));
        v=ApexView(h);Assert.Equal("Gaming",v.Look);Assert.True(v.LookChanged);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,null))).Ok);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,null))).Ok);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"Work"))).Ok);
        v=ApexView(h);Assert.Equal(["Gaming","Work"],v.Looks!);Assert.Equal("Work",v.Look);Assert.False(v.LookChanged);
        // Choose: the keyboard takes copies of the look's, whatever the case of the name.
        Assert.True((await Send(h,new ChooseLookRequest(ApexKey,"gaming"))).Ok);
        t=ApexEntry(h)!;
        Assert.Equal("0=Key 0/#FF0000",KeysText(t.Keys));Assert.Equal(("breathing",30),(t.Mode!.Kind,t.Mode.Speed));Assert.Equal("fade",Assert.Single(t.Layers!).Kind);
        Assert.Equal("Gaming",t.Look);
        v=ApexView(h);Assert.Equal("Gaming",v.Look);Assert.False(v.LookChanged);Assert.Equal("breathing",v.LightingMode!.Kind);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(2,"#0000FF"))))).Ok);
        Assert.Equal("0=Key 0/#FF0000",KeysText(ApexEntry(h)!.Looks![0].Keys)); // choosing copied, so the look didn't change with it
        Assert.True(ApexView(h).LookChanged);
        Assert.True((await Send(h,new ChooseLookRequest(ApexKey,"Work"))).Ok);
        t=ApexEntry(h)!;Assert.Equal("1=Key 1/#00FF00",KeysText(t.Keys));Assert.Null(t.Mode);Assert.Null(t.Layers);Assert.False(ApexView(h).LookChanged);
        // Rename: the current look follows.
        Assert.True((await Send(h,new RenameLookRequest(ApexKey,"work"," Office "))).Ok);
        v=ApexView(h);Assert.Equal(["Gaming","Office"],v.Looks!);Assert.Equal("Office",v.Look);Assert.False(v.LookChanged);
        Assert.True((await Send(h,new RenameLookRequest(ApexKey,"Gaming","Games"))).Ok);
        v=ApexView(h);Assert.Equal(["Games","Office"],v.Looks!);Assert.Equal("Office",v.Look);
        // Delete: another look leaves the current one; the current one clears Look and leaves the keyboard as it is.
        Assert.True((await Send(h,new DeleteLookRequest(ApexKey,"Games"))).Ok);
        v=ApexView(h);Assert.Equal(["Office"],v.Looks!);Assert.Equal("Office",v.Look);
        Assert.True((await Send(h,new DeleteLookRequest(ApexKey,"OFFICE"))).Ok);
        t=ApexEntry(h)!;Assert.Null(t.Looks);Assert.Null(t.Look);Assert.Equal("1=Key 1/#00FF00",KeysText(t.Keys));
        v=ApexView(h);Assert.Null(v.Looks);Assert.Null(v.Look);Assert.False(v.LookChanged);
        var saved=SavedSettings().Devices[ApexKey];
        Assert.Null(saved.Looks);Assert.Null(saved.Look);Assert.Equal("1=Key 1/#00FF00",KeysText(saved.Keys));
    }

    [Fact]
    public async Task Look_edges()
    {
        var h=await Running();
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"A"))).Ok);
        Assert.True((await Send(h,new DeleteLookRequest(ApexKey,"A"))).Ok);
        Assert.Equal(HostReplyCodes.NoLook,(await Send(h,new ChooseLookRequest(ApexKey,"A"))).Error);
        Assert.Equal(HostReplyCodes.NoLook,(await Send(h,new RenameLookRequest(ApexKey,"A","B"))).Error);
        Assert.Equal(HostReplyCodes.NoLook,(await Send(h,new DeleteLookRequest(ApexKey,"A"))).Error);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"A"))).Ok);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"B"))).Ok);
        Assert.Equal(HostReplyCodes.LookTaken,(await Send(h,new RenameLookRequest(ApexKey,"B","a"))).Error);
        Assert.True((await Send(h,new RenameLookRequest(ApexKey,"A","a"))).Ok); // its own name in another case
        Assert.Equal(["a","B"],ApexView(h).Looks!);
        Assert.Equal(HostReplyCodes.LookName,(await Send(h,new RenameLookRequest(ApexKey,"a","   "))).Error);
        Assert.Equal(HostReplyCodes.LookName,(await Send(h,new RenameLookRequest(ApexKey,"a",new string('x',41)))).Error);
        // Save over an existing name only when asked to.
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.Equal(HostReplyCodes.LookTaken,(await Send(h,new SaveLookRequest(ApexKey,"b"))).Error);
        Assert.Null(ApexEntry(h)!.Looks![1].Keys);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"b",true))).Ok);
        var t=ApexEntry(h)!;
        Assert.Equal(2,t.Looks!.Count);Assert.Equal("0=Key 0/#FF0000",KeysText(t.Looks[1].Keys));Assert.Equal(t.Looks[1].Name,t.Look);
        // Names.
        Assert.Equal(HostReplyCodes.LookName,(await Send(h,new SaveLookRequest(ApexKey,"   "))).Error);
        Assert.Equal(HostReplyCodes.LookName,(await Send(h,new SaveLookRequest(ApexKey,""))).Error);
        Assert.Equal(HostReplyCodes.LookName,(await Send(h,new SaveLookRequest(ApexKey,new string('x',41)))).Error);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"  "+new string('x',40)+"  "))).Ok);
        // Twelve saved: a thirteenth is refused, an overwrite isn't.
        for(var i=3;i<12;i++) Assert.True((await Send(h,new SaveLookRequest(ApexKey,"Look "+i))).Ok);
        Assert.Equal(12,ApexEntry(h)!.Looks!.Count);
        Assert.Equal(HostReplyCodes.TooManyLooks,(await Send(h,new SaveLookRequest(ApexKey,"Thirteen"))).Error);
        Assert.Equal(HostReplyCodes.TooManyLooks,(await Send(h,new SaveLookRequest(ApexKey,"Thirteen",true))).Error);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"look 5",true))).Ok);
        Assert.Equal(12,ApexEntry(h)!.Looks!.Count);
        Assert.Equal(12,SavedSettings().Devices[ApexKey].Looks!.Count);
    }

    // A 1 MiB file with room for the painted keys but not for a look holding a second copy of them.
    [Fact]
    public async Task A_save_past_the_size_cap_is_refused_with_no_room_and_nothing_changes()
    {
        var seeded=new LightingSettings();
        seeded.Devices[ApexKey]=new DeviceTuning{Keys=Enumerable.Range(0,112).ToDictionary(i=>i,i=>PK(new string('k',DeviceLayers.MaxLightName),"#FF0000"))};
        var withLook=DeviceTunings.Clone(seeded.Devices);
        withLook[ApexKey].Looks=[new DeviceLook{Name="Big",Keys=DeviceLayers.Clone(seeded.Devices[ApexKey].Keys)}];withLook[ApexKey].Look="Big";
        var look=Written(new LightingSettings{Devices=withLook})-Written(seeded);
        ZoneColorEntry Entry()=>new(){Color="#FF0000",ZoneName=new string('n',LightingSettingsCodec.MaxKeyLength)};
        seeded.ZoneColors["zone-00000"]=Entry();var one=Written(seeded);
        seeded.ZoneColors["zone-00001"]=Entry();var each=Written(seeded)-one;
        var count=(int)((LightingSettingsCodec.MaxFileBytes-look/2-one)/each)+1;
        for(var i=2;i<count;i++) seeded.ZoneColors[$"zone-{i:D5}"]=Entry();
        var withRoom=Written(seeded);
        Assert.InRange(withRoom,LightingSettingsCodec.MaxFileBytes-look,LightingSettingsCodec.MaxFileBytes);
        File.WriteAllText(lightingPath,LightingSettingsCodec.Write(seeded));
        var h=await Running(CountingStore());
        var file=File.ReadAllText(lightingPath);
        var before=Volatile.Read(ref saves);
        var reply=await Send(h,new SaveLookRequest(ApexKey,"Big"){Id=9});
        Assert.False(reply.Ok);Assert.Equal(HostReplyCodes.NoRoom,reply.Error);Assert.Equal(9,reply.Id);
        Assert.Equal(HostReplyCodes.NoRoom,(await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="fade",Lights=Enumerable.Range(0,112).Select(i=>new LayerLight{Index=i,Name=new string('k',DeviceLayers.MaxLightName)}).ToList()}]))).Error);
        Assert.Null(ApexEntry(h)!.Looks);Assert.Null(ApexEntry(h)!.Look);Assert.Null(ApexEntry(h)!.Layers);
        Assert.Equal(before,Volatile.Read(ref saves));
        Assert.Equal(file,File.ReadAllText(lightingPath));
        // Other requests still answer the sentence itself.
        var colors=Enumerable.Range(0,60).ToDictionary(i=>$"more-{i:D5}",_=>Entry());
        foreach(var e in h.State.Settings.ZoneColors) colors[e.Key]=e.Value;
        Assert.Equal(LightingSettingsCodec.WrittenTooLarge,(await Send(h,new ReplaceColorsRequest("#123456",colors))).Error);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,null))).Ok); // clearing makes room
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"Small"))).Ok);
    }

    [Fact]
    public async Task Deleting_the_current_look_keeps_the_keyboard_as_it_is()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((3,"#123456"))))).Ok);
        Assert.True((await Send(h,new SetDeviceModeRequest(ApexKey,"cycle",80))).Ok);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="ripple"}]))).Ok);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"Mine"))).Ok);
        var want=JsonSerializer.Serialize(new{ApexEntry(h)!.Keys,ApexEntry(h)!.Mode,ApexEntry(h)!.Layers});
        Assert.True((await Send(h,new DeleteLookRequest(ApexKey,"Mine"))).Ok);
        var t=ApexEntry(h)!;
        Assert.Null(t.Look);Assert.Null(t.Looks);
        Assert.Equal(want,JsonSerializer.Serialize(new{t.Keys,t.Mode,t.Layers}));
        var saved=SavedSettings().Devices[ApexKey];
        Assert.Equal(want,JsonSerializer.Serialize(new{saved.Keys,saved.Mode,saved.Layers}));
    }

    // Review Focus 1: two lights come back with each other's names. The 5 s rescan sees it (the shape names the lights), a state follows
    // with both keys skipped and both still saved, and the log says so once, not per frame.
    [Fact]
    public async Task A_reordered_keyboard_counts_skipped_keys_and_logs_once()
    {
        var names=Enumerable.Range(0,112).Select(i=>"Key: K"+i).ToArray();
        session.Controllers[Apex]=session.Controllers[Apex] with{LedNames=names};
        var h=await Running();
        var states=new List<HostState>();
        h.StateChanged+=s=>{lock(states)states.Add(s);};
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,new(){[0]=PK(names[0],"#FF0000"),[1]=PK(names[1],"#00FF00"),[7]=PK(names[7],"#0000FF")}))).Ok);
        Assert.Equal(0,ApexView(h).SkippedKeys);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        const string line="SteelSeries Apex Pro: 2 painted or layered keys don't match this keyboard's keys any more and are skipped.";
        Assert.Empty(LogLines("painted or layered keys"));
        var swapped=names.ToArray();(swapped[0],swapped[1])=(swapped[1],swapped[0]);
        session.Controllers[Apex]=session.Controllers[Apex] with{LedNames=swapped};
        lock(states)states.Clear();
        await AdvanceUntil(()=>{lock(states)return states.Any(s=>s.Devices.Any(d=>d.Key==ApexKey&&d.SkippedKeys==2));},limit:TimeSpan.FromSeconds(10));
        Assert.Equal(3,ApexEntry(h)!.Keys!.Count);
        Assert.Equal(3,SavedSettings().Devices[ApexKey].Keys!.Count);
        await Elapse(DeviceHost.Tick);await Elapse(DeviceHost.Tick);
        await Until(()=>time.Waiting(DeviceHost.Tick));
        Assert.Equal([line],LogLines("painted or layered keys").Select(l=>l[(l.IndexOf("SteelSeries",StringComparison.Ordinal))..].TrimEnd('\r')));
        // Light 1's paint goes to the light that is named for it now: it still doesn't match light 1, and the light shows the base colour.
        Assert.Equal(SteelBlue,LastLeds(Apex)[1]);Assert.Equal(new Rgb(0,0,0xFF),LastLeds(Apex)[7]);
    }

    [Fact]
    public async Task A_static_paint_change_sends_one_frame_while_subscribed()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(1,CountOf(seen));
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(1,"#00FF00"))))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=2);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(2,CountOf(seen));
        lock(seen)Assert.Equal(["FF0000","00FF00","4682B4"],Chunks(seen[1].Lights![ApexKey]).Take(3));
        // The same paint again: a state is raised, so a frame goes although the colours are the same. (A static desk forgets its last
        // frame at each tick, so the SameFrame bypass itself is A_paint_change_inside_the_preview_gap_is_still_sent's.)
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"),(1,"#00FF00"))))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=3);
        lock(seen)Assert.True(DeviceHost.SameFrame(seen[1],seen[2]));
    }

    // A one-colour wave keeps the fast tick and sends one frame while nothing changes; a paint change right after that frame, inside its
    // PreviewGap and giving the same colours, still sends one.
    [Fact]
    public async Task A_paint_change_inside_the_preview_gap_is_still_sent()
    {
        var h=await RunningWave(new EffectParams{Colors=["#FF0000"]});
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#00FF00"))))).Ok);
        var seen=new List<(DateTimeOffset At,PreviewEvent Frame)>();
        h.PreviewChanged+=e=>{lock(seen)seen.Add((time.GetUtcNow(),e));};
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1,tick:DeviceHost.FastTick);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#00FF00"))))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=2,tick:DeviceHost.FastTick,limit:TimeSpan.FromSeconds(2));
        lock(seen)
        {
            Assert.True(seen[1].At-seen[0].At<DeviceHost.PreviewGap,$"{(seen[1].At-seen[0].At).TotalMilliseconds} ms apart");
            Assert.True(DeviceHost.SameFrame(seen[0].Frame,seen[1].Frame));
        }
    }

    [Fact]
    public async Task A_static_paint_change_sends_no_frame_when_not_subscribed()
    {
        var h=await Running();
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        Assert.True((await Send(h,new SubscribePreviewRequest(false))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((1,"#FF0000"))))).Ok);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(0,CountOf(seen));
        Assert.False(ApexShows(SteelBlue)); // the keyboard itself is painted all along
    }

    [Fact]
    public async Task A_static_painted_desk_sends_no_frames_on_later_ticks()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[new DeviceLayer{Kind="color",Color="#00FF00",Lights=[new(){Index=4,Name="Key 4"}]}]))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick); // across a 5 s rescan too
        Assert.Equal(1,CountOf(seen));
        Assert.False(time.Waiting(DeviceHost.FastTick)); // and the tick stays slow
    }

    [Fact]
    public async Task Subscribing_sends_a_frame()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((2,"#ABCDEF"))))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        lock(seen)Assert.Equal("ABCDEF",Chunks(seen[0].Lights![ApexKey])[2]);
        h.ClientGone(); // a window that comes back gets one again
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=2);
    }

    [Fact]
    public async Task Preview_lights_carry_painted_keys_and_zone_colours_stay_base()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.True((await Send(h,new SetDeviceTuningRequest(ApexKey,50))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        PreviewEvent frame;
        lock(seen)frame=seen[0];
        var packed=frame.Lights![ApexKey];
        Assert.StartsWith("FF0000",packed);                                     // painted, before tuning
        Assert.Equal(111,Chunks(packed).Skip(1).Count(c=>c=="4682B4"));
        Assert.Equal("#4682B4",frame.Colors[ApexZone]);                        // the zone's colour is the base frame's, not light 0's
        Assert.Equal(new Rgb(0xFF,0,0).Scale(50),LastLeds(Apex)[0]);
    }

    // Controller ruling R4: the frame a state asks for is not sent while the lights are off, so painted colours never reach the window then.
    [Fact]
    public async Task Lights_off_sends_no_painted_preview()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#00FF00"))))).Ok);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(1,CountOf(seen));
        Assert.All(LastLeds(Apex),c=>Assert.Equal(Rgb.Black,c));
        Assert.True((await Send(h,new SetLightsOnRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=2);
        lock(seen)Assert.StartsWith("00FF00",seen[1].Lights![ApexKey]);
    }

    // Controller ruling R8 (a): the frame a state asks for goes only after that state has been handed to StateChanged. The state is held in
    // its handler while ticks run: no frame comes; once it is let go the frame follows at once (b), without a tick.
    [Fact]
    public async Task A_forced_frame_never_precedes_the_state_that_caused_it()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        var hold=NewHold();var parkNext=1;
        h.StateChanged+=_=>{if(Interlocked.Exchange(ref parkNext,0)==1) hold.Park();};
        var sending=Task.Run(()=>h.HandleAsync(new SetDeviceKeysRequest(ApexKey,Keys((0,"#00FF00")))));
        await Until(()=>hold.Parked);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick); // the loop ticks while the state waits in its handler
        Assert.Equal(1,CountOf(seen));
        hold.Release();
        Assert.True(Assert.IsType<Reply>(await sending).Ok);
        await Until(()=>CountOf(seen)>=2);
        lock(seen)Assert.StartsWith("00FF00",seen[1].Lights![ApexKey]);
    }

    // Controller ruling R8 (b): an accepted static change while subscribed is sent at once, not up to a slow tick later.
    [Fact]
    public async Task A_static_change_is_sent_without_waiting_for_a_tick()
    {
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        var at=time.GetUtcNow();
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#00FF00"))))).Ok);
        await Until(()=>CountOf(seen)>=2);
        Assert.Equal(at,time.GetUtcNow());
        lock(seen)Assert.StartsWith("00FF00",seen[1].Lights![ApexKey]);
    }

    // Lighting 3b, Task 6: the key-press hook, through a fake source that keeps the gate it was given, so a test presses keys itself.
    internal sealed class FakeKeys(TimeProvider time):IKeyPressSource
    {
        readonly object sync=new();
        bool installed;
        int starts,stops;
        public volatile bool FailStart;
        public volatile string? Refusal; // Start throws with this, as the real hook does when Windows refuses
        public KeyPressGate? Gate;
        public DateTimeOffset StartedAt;
        public bool Installed{get{lock(sync)return installed;}}
        public int Starts{get{lock(sync)return starts;}}
        public int Stops{get{lock(sync)return stops;}}
        public bool Start(KeyPressGate gate){lock(sync){starts++;Gate=gate;if(Refusal is{} why) throw new InvalidOperationException(why);if(FailStart) return false;installed=true;StartedAt=time.GetUtcNow();return true;}}
        // The hook goes away by itself, as when its thread's message loop ends or Windows removes it.
        public void Die(){lock(sync)installed=false;}
        public void Stop(){lock(sync){stops++;installed=false;}}
        public void Dispose(){}
    }
    const int ScanA=0x1E,ScanW=0x11,ScanCtrl=0x1D,ScanQ=0x10;
    static readonly Rgb PressedRed=new(0xFF,0x82,0xB4); // steel blue with red added at full strength
    // The fixture keyboard's lights 0 to 3 get real key names; the rest keep theirs, which name no key.
    void NameApexKeys()
    {
        string[] keys=["Key: A","Key: W","Key: Left Control","Key: Right Control"];
        session.Controllers[Apex]=session.Controllers[Apex] with{LedNames=Enumerable.Range(0,112).Select(i=>i<keys.Length?keys[i]:"Key "+i).ToArray()};
    }
    static DeviceLayer Ripple(int strength=100,List<LayerLight>? lights=null)=>new(){Kind="ripple",Color="#FF0000",Strength=strength,Lights=lights};
    async Task<(DeviceHost Host,FakeKeys Keys)> Watching(DeviceLayer? layer,bool names=true)
    {
        if(names) NameApexKeys();
        var keys=new FakeKeys(time);
        var h=Build(keys:keys);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        if(layer is not null) Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[layer]))).Ok);
        return (h,keys);
    }
    List<HostState> StatesOf(DeviceHost h){var states=new List<HostState>();h.StateChanged+=s=>{lock(states)states.Add(s);};return states;}
    string LogText()=>File.ReadAllText(Path.Combine(dir,"logs","devices.log"));
    static void Tap(FakeKeys k,int scan,bool extended=false){k.Gate!.OnKey(scan,extended,false,false);k.Gate.OnKey(scan,extended,false,true);}
    // After a press: the loop goes fast, then back to the slow tick once the reaction is over.
    async Task<TimeSpan> UntilReactionEnds()
    {
        await Until(()=>time.Waiting(DeviceHost.FastTick));
        return await AdvanceUntil(()=>time.Waiting(DeviceHost.Tick),tick:DeviceHost.FastTick);
    }

    [Fact]
    public async Task The_hook_starts_with_a_ripple_layer_and_stops_without_one()
    {
        var (h,keys)=await Watching(null);
        Assert.Equal(0,keys.Starts);Assert.False(h.State.KeyHook);
        var states=StatesOf(h);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[Ripple()]))).Ok);
        Assert.Equal(1,keys.Starts);Assert.True(keys.Installed);Assert.True(h.State.KeyHook);
        lock(states)Assert.True(states[^1].KeyHook);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,null))).Ok);
        Assert.Equal(1,keys.Stops);Assert.False(keys.Installed);Assert.False(h.State.KeyHook);
        lock(states)Assert.False(states[^1].KeyHook);
        Assert.Single(LogLines("Key presses: watching, for 1 keyboard(s)."));
        Assert.Single(LogLines("Key presses: not watching."));
    }

    [Fact]
    public async Task The_hook_stops_when_lights_go_off_and_presses_are_cleared()
    {
        var (h,keys)=await Watching(Ripple());
        Tap(keys,ScanA);
        Assert.Single(keys.Gate!.Live());
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        Assert.Equal(1,keys.Stops);Assert.False(h.State.KeyHook);
        Assert.Empty(keys.Gate.Live());
        Assert.True((await Send(h,new SetLightsOnRequest(true))).Ok);
        Assert.Equal(2,keys.Starts);Assert.True(h.State.KeyHook);
    }

    [Fact]
    public async Task The_hook_stops_when_the_host_stops_running()
    {
        var (h,keys)=await Watching(Ripple());
        Assert.True(keys.Installed);
        lock(conflicts)conflicts.Add(DeadLight);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        // The status is set under the gate and the hook is stopped by the Raise that follows it, so a poll of State can land between the
        // two (seen on CI). Every raised state already has the hook stopped; wait for that Raise before asserting.
        await Until(()=>!keys.Installed&&!h.State.KeyHook);
        Assert.False(keys.Installed);Assert.False(h.State.KeyHook);
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running);
        await Until(()=>keys.Installed);
        Assert.Equal(2,keys.Starts);
        Assert.True((await Send(h,new StopRequest())).Ok);
        await run!.WaitAsync(RealTimeCap);
        Assert.False(keys.Installed);
        Assert.True(keys.Stops>=2);
        Assert.Empty(keys.Gate!.Live());
    }

    // A run that ends by cancellation raises no state, so only RunAsync's own stop removes the hook then.
    [Fact]
    public async Task The_hook_stops_when_the_run_is_cancelled()
    {
        NameApexKeys();
        var keys=new FakeKeys(time);
        var h=Build(keys:keys);
        using var cts=new CancellationTokenSource();
        run=h.RunAsync(cts.Token);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[Ripple()]))).Ok);
        Assert.True(keys.Installed);
        cts.Cancel();
        await run.WaitAsync(RealTimeCap);
        Assert.False(keys.Installed);
        Assert.Empty(keys.Gate!.Live());
        Assert.Single(LogLines("Key presses: not watching."));
    }

    [Theory]
    [InlineData("strength 0")]
    [InlineData("no lights")]
    [InlineData("no known keys")]
    public async Task A_layer_that_can_do_nothing_starts_no_hook(string why)
    {
        var layer=why switch{"strength 0"=>Ripple(strength:0),"no lights"=>Ripple(lights:[]),_=>Ripple()};
        var (h,keys)=await Watching(layer,names:why!="no known keys");
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Equal(0,keys.Starts);Assert.False(h.State.KeyHook);
        Assert.Empty(LogLines("Key presses: watching"));
    }

    [Fact]
    public async Task A_keyboard_in_a_hardware_mode_starts_no_hook()
    {
        NameApexKeys();
        session.Controllers[Apex]=session.Controllers[Apex] with{Modes=["Direct","Spectrum"]};
        var seeded=new LightingSettings();
        seeded.HardwareModes[ApexKey]="Spectrum";
        seeded.Devices[ApexKey]=new DeviceTuning{Layers=[Ripple()]};
        File.WriteAllText(lightingPath,LightingSettingsCodec.Write(seeded));
        var (h,keys)=await Watching(null,names:false);
        Assert.Equal(PaintKind.Hardware,ApexView(h).Kind);
        Assert.Single(ApexEntry(h)!.Layers!); // kept
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Equal(0,keys.Starts);Assert.False(h.State.KeyHook);
    }

    [Fact]
    public async Task A_press_runs_the_fast_tick_until_the_reaction_ends()
    {
        var (h,keys)=await Watching(Ripple());
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.False(time.Waiting(DeviceHost.FastTick)); // nobody typing: the slow tick
        keys.Gate!.OnKey(ScanA,false,false,false);
        var moved=await UntilReactionEnds();
        Assert.InRange(moved.TotalMilliseconds,600,600+2*DeviceHost.FastTick.TotalMilliseconds);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick); // stalls if the loop goes back to FastTick
        Assert.False(time.Waiting(DeviceHost.FastTick));
    }

    [Fact]
    public async Task A_press_wakes_the_loop_at_once()
    {
        var (h,keys)=await Watching(Ripple());
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        time.Advance(TimeSpan.FromMilliseconds(40)); // past the device's frame cap, far short of the slow tick
        var from=session.LedCount;
        keys.Gate!.OnKey(ScanA,false,false,false);
        await Until(()=>session.Leds.Skip(from).Any(l=>l.Index==Apex&&l.Colors[0]==PressedRed));
    }

    [ThreadStatic] static bool inOnKey;
    [Fact]
    public async Task A_press_never_runs_the_tick_on_the_callers_thread()
    {
        var (h,keys)=await Watching(Ripple());
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        int painted=0,onCaller=0;
        session.FailLeds=i=>{if(i==Apex){Interlocked.Increment(ref painted);if(inOnKey) Interlocked.Increment(ref onCaller);}return false;};
        time.Advance(TimeSpan.FromMilliseconds(40));
        inOnKey=true;
        try{keys.Gate!.OnKey(ScanA,false,false,false);}
        finally{inOnKey=false;}
        await Until(()=>Volatile.Read(ref painted)>0);
        Assert.Equal(0,Volatile.Read(ref onCaller));
    }

    [Fact]
    public async Task A_press_reaches_the_preview_lights_and_nothing_else()
    {
        var (h,keys)=await Watching(Ripple());
        var states=StatesOf(h);
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick); // a still tick forgets the last frame
        var logBefore=LogText();
        time.Advance(TimeSpan.FromMilliseconds(40));
        Tap(keys,ScanA);
        await Until(()=>{lock(seen)return seen.Any(f=>f.Lights![ApexKey].StartsWith("FF82B4",StringComparison.Ordinal));});
        await UntilReactionEnds();
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Equal(0,CountOf(states));
        Assert.Equal(logBefore,LogText());
    }

    [Fact]
    public async Task A_renamed_keyboard_gets_a_new_press_table()
    {
        var (h,keys)=await Watching(Ripple());
        Tap(keys,ScanA);
        Assert.Equal([0],keys.Gate!.Live()[^1].Lights);
        var swapped=session.Controllers[Apex].LedNames!.ToArray();
        (swapped[0],swapped[1])=(swapped[1],swapped[0]);
        session.Controllers[Apex]=session.Controllers[Apex] with{LedNames=swapped};
        Assert.True((await Send(h,new RescanRequest())).Ok);
        Tap(keys,ScanA);
        var live=keys.Gate.Live();
        Assert.Equal([1],live[^1].Lights); // A is light 1 now; light 0 is W
        Assert.DoesNotContain(live,p=>p.Lights.Contains(0)); // wbr-1 D3: the press made under the old order is gone too
    }

    // wbr-1 D1: when the last live press ends, one closing frame shows the keyboard with the press gone, so the window's picture doesn't
    // keep the last fade or ring lit; later still ticks send nothing.
    [Theory]
    [InlineData("ripple",600)]
    [InlineData("fade",600)]
    [InlineData("ripple",100)]
    [InlineData("fade",100)]
    public async Task The_last_frame_after_a_press_shows_the_keyboard_unpressed(string kind,int ms)
    {
        var (h,keys)=await Watching(new DeviceLayer{Kind=kind,Color="#FF0000",Strength=100,DurationMs=ms});
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick); // a still tick forgets the last frame
        string unpressed;int before;
        lock(seen){unpressed=seen[^1].Lights![ApexKey];before=seen.Count;}
        time.Advance(TimeSpan.FromMilliseconds(40));
        Tap(keys,ScanA);
        await Until(()=>{lock(seen)return seen.Skip(before).Any(f=>f.Lights![ApexKey].StartsWith("FF82B4",StringComparison.Ordinal));});
        await UntilReactionEnds();
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        int after;
        lock(seen){after=seen.Count;Assert.Equal(unpressed,seen[^1].Lights![ApexKey]);}
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick); // across a 5 s rescan too
        Assert.Equal(after,CountOf(seen));
    }

    [Fact]
    public async Task No_closing_frame_after_the_window_unsubscribes()
    {
        var (h,keys)=await Watching(Ripple());
        var seen=FullPreviews(h);
        Assert.True((await Send(h,new SubscribePreviewRequest(true))).Ok);
        await AdvanceUntil(()=>CountOf(seen)>=1);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        time.Advance(TimeSpan.FromMilliseconds(40));
        Tap(keys,ScanA);
        await Until(()=>{lock(seen)return seen.Any(f=>f.Lights![ApexKey].StartsWith("FF82B4",StringComparison.Ordinal));});
        Assert.True((await Send(h,new SubscribePreviewRequest(false))).Ok);
        var n=CountOf(seen);
        await UntilReactionEnds();
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(n,CountOf(seen));
    }

    // wbr-1 D8: overwriting a look replaces it where it is.
    [Fact]
    public async Task Overwriting_a_look_keeps_its_place()
    {
        var h=await Running();
        foreach(var name in new[]{"A","B","C"}) Assert.True((await Send(h,new SaveLookRequest(ApexKey,name))).Ok);
        Assert.True((await Send(h,new SetDeviceKeysRequest(ApexKey,Keys((0,"#FF0000"))))).Ok);
        Assert.True((await Send(h,new SaveLookRequest(ApexKey,"B",true))).Ok);
        var t=ApexEntry(h)!;var looks=t.Looks!;
        Assert.Equal(["A","B","C"],looks.Select(l=>l.Name));
        Assert.Equal("0=Key 0/#FF0000",KeysText(looks[1].Keys));
        Assert.Null(looks[0].Keys);Assert.Null(looks[2].Keys);
        Assert.Equal("B",t.Look);
        Assert.Equal(["A","B","C"],ApexView(h).Looks!);
        Assert.Equal(["A","B","C"],SavedSettings().Devices[ApexKey].Looks!.Select(l=>l.Name));
    }

    [Fact]
    public async Task The_hook_is_refreshed_every_five_minutes_silently()
    {
        var (h,keys)=await Watching(Ripple());
        var states=StatesOf(h);
        var logBefore=LogText();
        var started=keys.StartedAt;
        await AdvanceUntil(()=>time.GetUtcNow()>=started+TimeSpan.FromSeconds(295),step:TimeSpan.FromSeconds(5),limit:TimeSpan.FromMinutes(6));
        await AdvanceUntil(()=>time.GetUtcNow()>=started+TimeSpan.FromSeconds(298.5));
        Assert.Equal(0,keys.Stops);
        keys.Gate!.OnKey(ScanA,false,false,false); // held, and live
        await UntilReactionEnds();
        await AdvanceUntil(()=>keys.Starts>=2,limit:TimeSpan.FromSeconds(5));
        Assert.Equal(1,keys.Stops);Assert.True(keys.Installed);
        Assert.Single(keys.Gate.Live());               // the live press is kept
        keys.Gate.OnKey(ScanA,false,false,false);       // within 2 s of its last sighting, yet counts: held keys were forgotten
        Assert.Equal(2,keys.Gate.Live().Count);
        Assert.Equal(0,CountOf(states));
        Assert.Equal(logBefore,LogText());
    }

    [Fact]
    public async Task A_hook_that_cannot_start_is_reported_and_retried()
    {
        NameApexKeys();
        var keys=new FakeKeys(time){FailStart=true};
        var h=Build(keys:keys);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[Ripple()]))).Ok);
        var tried=keys.Starts;
        Assert.True(tried>=1);Assert.False(h.State.KeyHook);
        Assert.Single(LogLines("Key presses: couldn't watch for key presses ("));
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        Assert.True(keys.Starts>tried);
        Assert.Single(LogLines("Key presses: couldn't watch for key presses ("));
        keys.FailStart=false;
        Assert.True((await Send(h,new SetBrightnessRequest(50))).Ok);
        Assert.True(keys.Installed);Assert.True(h.State.KeyHook);
        Assert.Single(LogLines("Key presses: watching, for 1 keyboard(s)."));
    }

    [Fact]
    public async Task Without_a_key_source_nothing_is_watched()
    {
        NameApexKeys();
        var h=await Running();
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[Ripple()]))).Ok);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.False(h.State.KeyHook);
        Assert.False(time.Waiting(DeviceHost.FastTick));
        Assert.Empty(LogLines("Key presses"));
    }

    [Fact]
    public async Task The_log_never_names_a_pressed_key()
    {
        var (h,keys)=await Watching(Ripple());
        var before=LogText().Length;
        time.Advance(TimeSpan.FromMilliseconds(40));
        Tap(keys,ScanA);Tap(keys,ScanW);Tap(keys,ScanCtrl);Tap(keys,ScanCtrl,true);Tap(keys,ScanQ);
        keys.Gate!.OnKey(ScanA,false,true,false); // software-sent
        await UntilReactionEnds();
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick); // across a rescan
        Assert.True((await Send(h,new SetLightsOnRequest(false))).Ok);
        var added=LogText()[before..].Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>l.TrimEnd('\r')[(l.IndexOf(' ')+1)..]).ToList();
        string[] allowed=[@"^Key presses: watching, for \d+ keyboard\(s\)\.$",@"^Key presses: not watching\.$",@"^Key presses: couldn't watch for key presses \(.*\)\.$"];
        Assert.All(added,l=>Assert.Contains(allowed,a=>System.Text.RegularExpressions.Regex.IsMatch(l,a)));
        Assert.Equal(["Key presses: not watching."],added);
    }

    // Fix round 1, M4: a hook that goes away by itself makes the state truthful at the next tick, and is started again.
    [Fact]
    public async Task A_hook_that_dies_is_noticed_at_the_next_tick()
    {
        var (h,keys)=await Watching(Ripple());
        var states=StatesOf(h);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        keys.FailStart=true;
        keys.Die();
        await AdvanceUntil(()=>{lock(states)return states.Any(s=>!s.KeyHook);},limit:TimeSpan.FromSeconds(1));
        Assert.Equal(2,keys.Starts); // tried again
        Assert.False(h.State.KeyHook);
        Assert.Single(LogLines("Key presses: couldn't watch for key presses ("));
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Single(states); // one state, not one per tick
        keys.FailStart=false;
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok); // a failed start is tried again at the next state
        Assert.Equal(3,keys.Starts);
        Assert.True(keys.Installed);Assert.True(h.State.KeyHook);
    }

    // Fix round 1, M5: when Start throws with Windows' reason, the couldn't-watch line carries it.
    [Fact]
    public async Task A_refused_hook_is_logged_with_the_reason()
    {
        NameApexKeys();
        var keys=new FakeKeys(time){Refusal="Windows refused the keyboard hook: Access is denied. (5)"};
        var h=Build(keys:keys);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.True((await Send(h,new SetDeviceLayersRequest(ApexKey,[Ripple()]))).Ok);
        Assert.Equal(["Key presses: couldn't watch for key presses (Windows refused the keyboard hook: Access is denied. (5))."],
            LogLines("Key presses:").Select(l=>l[(l.IndexOf(' ')+1)..].TrimEnd('\r')));
        Assert.False(h.State.KeyHook);
    }

    // Fix round 1, M6: the light-names line, once, with at most 20 of the unknown names.
    [Fact]
    public async Task The_light_names_line_lists_at_most_twenty_unknown_names()
    {
        var (h,keys)=await Watching(Ripple());
        Assert.True((await Send(h,new SetBrightnessRequest(40))).Ok);
        var want="SteelSeries Apex Pro: 4 of 112 light names are known keys; not known: "+string.Join(", ",Enumerable.Range(4,20).Select(i=>"Key "+i))+" and 88 more.";
        Assert.Equal([want],LogLines("light names are known keys").Select(l=>l[(l.IndexOf(' ')+1)..].TrimEnd('\r')));
    }
}
