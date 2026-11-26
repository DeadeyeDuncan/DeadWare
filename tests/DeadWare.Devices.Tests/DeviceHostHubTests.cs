using System.Text.Json.Nodes;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// Lighting 5, Task 9: the host's side of the hub, against FakeHub: holding, dark, sleep and wake, conflicts, the detector at every launch,
// the hand-back at the stop and the hub's state member. Mode driven comes from a version 5 file. No test reaches a real OpenRGB.json:
// HostOptions.ConfigDir is "" unless a test gives a temp folder of its own.
public sealed partial class DeviceHostTests
{
    readonly List<bool> launchDetector=new();
    Action? beforeLaunch;
    List<bool> Launches(){lock(launchDetector)return launchDetector.ToList();}
    void SaveHub(string hub)=>File.WriteAllText(lightingPath,"{\"Version\":5,\"Hub\":"+hub+"}");
    void SaveMode(string mode)=>SaveHub("{\"Mode\":\""+mode+"\"}");
    const string FourParts="[{\"Channel\":1,\"Type\":15,\"Model\":0},{\"Channel\":2,\"Type\":15,\"Model\":0},{\"Channel\":3,\"Type\":15,\"Model\":0},{\"Channel\":13,\"Type\":17,\"Model\":2}]";
    static readonly IReadOnlyList<HubPart> OwnersParts=
    [
        new(1,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(2,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(3,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(13,0x11,0x02,"iCUE LINK TITAN 360",20,true,"pump"),
    ];
    static readonly LightingConflict ICue=new(ConflictKind.ICue,"iCUE is running.",true);
    static readonly LightingConflict LegacyService=new(ConflictKind.LegacyCorsairService,"The Corsair service, Corsair.Service.exe, is running. It doesn't block lighting.",false);
    string FileText()=>File.Exists(lightingPath)?File.ReadAllText(lightingPath):"";
    static (string? Reason,string? Detail) LastAllow(FakeHub hub)=>hub.Allows is{Count:>0} a?a[^1]:("none","none");
    bool Allowed(FakeHub hub)=>hub.Allows.Count>0&&LastAllow(hub).Reason is null;
    List<HostState> Recorded(DeviceHost h){var states=new List<HostState>();h.StateChanged+=s=>{lock(states)states.Add(s);};return states;}
    static List<T> Copy<T>(List<T> list){lock(list)return list.ToList();}
    async Task StopHost(DeviceHost h){Assert.True((await Send(h,new StopRequest())).Ok);await run!.WaitAsync(RealTimeCap);}
    // RunFor in 20 s pieces: one AdvanceUntil may move fake time at most FakeTimeLimit (30 s).
    async Task RunLong(TimeSpan span){for(var left=span;left>TimeSpan.Zero;left-=TimeSpan.FromSeconds(20)) await RunFor(left<TimeSpan.FromSeconds(20)?left:TimeSpan.FromSeconds(20),DeviceHost.Tick);}

    // FakeHub seen through a recorder: each Allow also notes whether the newest fake OpenRGB still runs and how many Resumes came before it.
    sealed class Watched(FakeHub inner,Func<bool> running):IHub
    {
        readonly List<(string? Reason,bool Running,int Resumes)> seen=new();
        public List<(string? Reason,bool Running,int Resumes)> Seen{get{lock(seen)return seen.ToList();}}
        public void Post(Rgb[] zones)=>inner.Post(zones);
        public void SetDark(bool dark)=>inner.SetDark(dark);
        public void Allow(string? forbidden,string? detail=null){lock(seen)seen.Add((forbidden,running(),inner.Resumes));inner.Allow(forbidden,detail);}
        public void Quiet()=>inner.Quiet();
        public void Resume()=>inner.Resume();
        public void Recheck()=>inner.Recheck();
        public void RequestReading()=>inner.RequestReading();
        public Task LetGoAsync()=>inner.LetGoAsync();
        public HubSnapshot Snapshot=>inner.Snapshot;
        public event Action? Changed{add=>inner.Changed+=value;remove=>inner.Changed-=value;}
    }
    bool NewestRuns()=>runner.Count>0&&!runner.At(runner.Count-1).HasExited;

    // A host whose conflict scan the test writes, for a scan that fails.
    DeviceHost BuildScanned(IHub hub,Func<int?,IReadOnlyList<LightingConflict>> scan)
    {
        var engine=new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log);
        var options=new HostOptions(exe,"",lightingPath,Path.Combine(dir,"logs"),false,"S-1-5-21-1",1);
        return host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,new Scanner(scan),time,log,hub:hub);
    }

    // Review Focus 2: with the switch off the hub is never allowed and DeadWare does nothing with it.
    [Fact]
    public async Task With_mode_openrgb_the_hub_is_never_allowed()
    {
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=3);
        Assert.NotEmpty(hub.Allows);
        Assert.All(hub.Allows,a=>Assert.Equal("off",a.Reason));
        Assert.Empty(hub.Posts);
        Assert.Equal("off",h.State.Hub!.State);
    }

    // Ruling R4: until lighting has connected once in this run the reason is starting; then holding is allowed.
    [Fact]
    public async Task With_mode_driven_holding_waits_for_the_first_connect()
    {
        SaveMode("driven");
        var port=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector(),portOpen:(_,_)=>port.Task);Start(h);
        try
        {
            await Until(()=>runner.Count==1);
            await AdvanceUntil(()=>hub.Allows.Count>0);
            await AdvanceUntil(()=>Volatile.Read(ref scans)>=2);
            Assert.Equal(HostStatus.Starting,h.State.Status);
            Assert.Equal(new (string?,string?)[]{("starting",null)},hub.Allows);
            Assert.Equal("letGo",h.State.Hub!.State);
            Assert.Equal("starting",h.State.Hub.Reason);
        }
        finally{port.TrySetResult(true);}
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running&&Allowed(hub));
        Assert.Equal(new (string?,string?)[]{("starting",null),(null,null)},hub.Allows);
    }

    // Review Focus 6: an OpenRGB crash and its restart keep the hub.
    [Fact]
    public async Task The_hub_is_kept_while_OpenRGB_restarts()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        var allows=hub.Allows.Count;
        runner.At(0).Exit();
        await Until(()=>h.State.Status==HostStatus.Waiting);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(HostStatus.Waiting,h.State.Status);
        Assert.Equal(allows,hub.Allows.Count);
        Assert.Null(LastAllow(hub).Reason);
        Assert.Equal(0,hub.LetGos);
        Assert.Equal("driving",h.State.Hub!.State);
    }

    // Review Focus 4: a program that blocks lighting forbids holding before OpenRGB is stopped; a stop for a failed scan forbids with noLighting.
    [Fact]
    public async Task A_blocking_conflict_forbids_before_OpenRGB_is_stopped()
    {
        SaveMode("driven");
        var fake=new FakeHub();
        var hub=new Watched(fake,NewestRuns);
        var breaks=false;
        var h=BuildScanned(hub,pid=>
        {
            Interlocked.Increment(ref scans);
            if(Volatile.Read(ref breaks)) throw new InvalidOperationException("scan broke");
            var found=LightingConflicts.Find(runner.Live(),pid).ToList();
            lock(conflicts)found.AddRange(conflicts);
            return found;
        });
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(fake));
        lock(conflicts)conflicts.Add(DeadLight);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(0).Killed);
        Assert.Contains(("deadlight",true,0),hub.Seen);
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>runner.Count==2&&h.State.Status==HostStatus.Running&&Allowed(fake),limit:TimeSpan.FromSeconds(60));
        var from=hub.Seen.Count;
        Volatile.Write(ref breaks,true);
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(1).Killed);
        Assert.Equal(("noLighting",true,0),hub.Seen.Skip(from).First());
    }

    // The legacy Corsair.Service doesn't block lighting, but the hub is let go while it runs.
    [Fact]
    public async Task The_legacy_Corsair_service_lets_go_and_lighting_runs()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        lock(conflicts)conflicts.Add(LegacyService);
        await AdvanceUntil(()=>LastAllow(hub).Reason=="service");
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.False(runner.At(0).Killed);
        Assert.Equal(1,runner.Count);
        lock(conflicts)conflicts.Clear();
        await AdvanceUntil(()=>Allowed(hub));
        Assert.Equal(1,runner.Count);
    }

    // Review Focus 6: dark is not a let-go.
    [Fact]
    public async Task Lights_off_idle_and_lock_set_dark_and_keep_hold()
    {
        var saved=new LightingSettings();saved.Hub.Mode="driven";saved.Everyday.IdleOff=true;saved.Everyday.IdleMinutes=1;saved.Everyday.LockOff=true;
        new LightingStore(lightingPath).Save(saved);
        var signals=new FakeSignals();var input=new FakeInput();
        var hub=new FakeHub();
        var h=Build(signals:signals,input:input,hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        var allows=hub.Allows.Count;
        Assert.False(hub.Darks[^1]);
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.True(hub.Darks[^1]);
        signals.Press();
        await Until(()=>h.State.Settings.LightsOn);
        Assert.False(hub.Darks[^1]);
        input.Idle=60_000;
        await AdvanceUntil(()=>h.State.LightsOffReason==LightsOff.Idle);
        Assert.True(hub.Darks[^1]);
        input.Idle=0;
        await AdvanceUntil(()=>h.State.LightsOffReason is null);
        Assert.False(hub.Darks[^1]);
        signals.Lock(true);
        await Until(()=>h.State.LightsOffReason==LightsOff.Locked);
        Assert.True(hub.Darks[^1]);
        signals.Lock(false);
        await Until(()=>h.State.LightsOffReason is null);
        Assert.False(hub.Darks[^1]);
        await RunFor(TimeSpan.FromSeconds(1),DeviceHost.Tick);
        Assert.Equal(allows,hub.Allows.Count);
        Assert.Null(LastAllow(hub).Reason);
    }

    [Fact]
    public async Task A_press_while_OpenRGB_restarts_still_darkens_the_hub()
    {
        SaveMode("driven");
        var signals=new FakeSignals();var input=new FakeInput();
        var hub=new FakeHub();
        var h=Build(signals:signals,input:input,hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        runner.At(0).Exit();
        await Until(()=>h.State.Status==HostStatus.Waiting);
        signals.Press();
        await Until(()=>!h.State.Settings.LightsOn);
        Assert.Equal(HostStatus.Waiting,h.State.Status);
        Assert.True(hub.Darks[^1]);
    }

    // Quiet at the suspend signal itself, before any loop turn; Resume at the wake's end, in the turn that stops forbidding with waking.
    [Fact]
    public async Task Suspend_goes_quiet_and_the_wakes_end_resumes()
    {
        SaveMode("driven");
        var signals=new FakeSignals();var input=new FakeInput();
        var fake=new FakeHub();
        var hub=new Watched(fake,NewestRuns);
        var h=Build(signals:signals,input:input,hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(fake));
        signals.Suspend();
        Assert.Equal(1,fake.Quiets);
        await AdvanceUntil(()=>LastAllow(fake).Reason=="waking");
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(1,fake.Quiets);
        var resumedAt=time.GetUtcNow();
        signals.Resume();
        await Until(()=>LogLines(" resume").Length>=1);
        await RunFor(TimeSpan.FromSeconds(9),DeviceHost.Tick);
        Assert.Equal(0,fake.Resumes);
        Assert.Equal("waking",LastAllow(fake).Reason);
        await AdvanceUntil(()=>fake.Resumes==1);
        Assert.Equal(TimeSpan.FromSeconds(10),time.GetUtcNow()-resumedAt);
        await Until(()=>Allowed(fake));
        Assert.Equal(1,fake.Quiets);
        var seen=hub.Seen;
        var lifted=seen.FindLastIndex(a=>a.Reason=="waking")+1;
        Assert.Equal((null,true,1),seen[lifted]);
    }

    // The window ends the helper 5 s after Stop, so the hand-back comes before OpenRGB's stop.
    [Fact]
    public async Task Stop_hands_the_hub_back_before_OpenRGB_is_stopped()
    {
        SaveMode("driven");
        var hub=new FakeHub{HoldLetGo=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        Assert.True((await Send(h,new StopRequest())).Ok);
        await Until(()=>hub.LetGos==1);
        Assert.Equal("noLighting",LastAllow(hub).Reason);
        await Task.Delay(100);
        Assert.False(runner.At(0).HasExited);
        Assert.False(run!.IsCompleted);
        hub.HoldLetGo!.SetResult();
        await run.WaitAsync(RealTimeCap);
        Assert.True(runner.At(0).Killed);
    }

    [Fact]
    public async Task A_hand_back_that_never_finishes_is_left_after_1_5_s()
    {
        SaveMode("driven");
        var hub=new FakeHub{HoldLetGo=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.True((await Send(h,new StopRequest())).Ok);
        await Until(()=>hub.LetGos==1);
        Assert.False(run!.IsCompleted);
        await Elapse(TimeSpan.FromMilliseconds(1500));
        await run.WaitAsync(RealTimeCap);
        Assert.Single(LogLines("hub hand-back did not finish in time"));
        Assert.True(runner.At(0).Killed);
    }

    // So a start by 0.22 finds the hub: the key goes back to true at every controlled stop, whatever the mode.
    // With configDir "" (folder false) nothing is written anywhere: not the temp folder's file, nor the relative OpenRGB.json "" would name.
    [Theory]
    [InlineData("driven",true)]
    [InlineData("openrgb",true)]
    [InlineData("driven",false)]
    [InlineData("openrgb",false)]
    public async Task Stop_writes_the_detector_on_in_every_mode(string mode,bool folder)
    {
        SaveMode(mode);
        var cfg=Path.Combine(dir,"openrgb-cfg");Directory.CreateDirectory(cfg);
        var file=Path.Combine(cfg,"OpenRGB.json");
        File.WriteAllText(file,"{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":false}}}");
        var relative=Path.Combine(Environment.CurrentDirectory,"OpenRGB.json");
        var wasThere=File.Exists(relative);
        var h=Build(hub:new FakeHub(),detector:new HubDetector(),configDir:folder?cfg:"");Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await StopHost(h);
        var key=(bool)JsonNode.Parse(File.ReadAllText(file))!["Detectors"]!["detectors"]!["Corsair iCUE Link System Hub"]!;
        if(folder)
        {
            Assert.True(key);
            Assert.Single(LogLines("openrgb hub detector: on (at stop)"));
            return;
        }
        Assert.False(key);
        Assert.Empty(LogLines("openrgb hub detector"));
        Assert.Equal(wasThere,File.Exists(relative));
        Assert.Single(LogLines("host stopped"));
    }

    [Fact]
    public async Task The_detector_at_the_stop_makes_no_file_and_needs_a_folder()
    {
        var cfg=Path.Combine(dir,"openrgb-empty");Directory.CreateDirectory(cfg);
        var h=Build(hub:new FakeHub(),detector:new HubDetector(),configDir:cfg);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await StopHost(h);
        Assert.Empty(Directory.GetFileSystemEntries(cfg));
        Assert.Empty(LogLines("openrgb hub detector"));
    }

    // Ruling P3: the detector is synced from the saved mode before the first launch of the run.
    [Theory]
    [InlineData("driven",false)]
    [InlineData("openrgb",true)]
    public async Task The_detector_follows_the_mode_at_each_launch(string mode,bool on)
    {
        if(mode!="openrgb") SaveMode(mode);
        var h=Build(hub:new FakeHub(),detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal([on],Launches());
    }

    [Fact]
    public async Task A_driven_start_launches_without_the_detector_and_never_restarts()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await RunLong(TimeSpan.FromSeconds(60));
        Assert.Equal([false],Launches());
        Assert.Equal(1,runner.Count);
        Assert.Empty(LogLines("OpenRGB still has the hub"));
        Assert.Null(LastAllow(hub).Reason);
    }

    // Review Focus 4: an OpenRGB launched with the detector on is never held beside, whatever it has listed; the one restart follows.
    [Fact]
    public async Task An_OpenRGB_launched_with_the_detector_on_is_never_held_beside()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var detector=new HubDetector();
        var launches=0;
        List<(string? Reason,string? Detail)>? atSecond=null;
        var connectsAtSecond=0;
        Volatile.Write(ref beforeLaunch,()=>{if(++launches==1) detector.On=true;else if(launches==2){atSecond=hub.Allows;connectsAtSecond=session.Connects;}});
        var h=Build(hub:hub,detector:detector);Start(h);
        await AdvanceUntil(()=>Launches().Count==2,limit:TimeSpan.FromSeconds(60));
        Assert.NotNull(atSecond);
        Assert.Equal(1,connectsAtSecond); // the first OpenRGB was Running, connected, and still never held beside
        Assert.All(atSecond!,a=>Assert.NotNull(a.Reason));
        Assert.Equal("openRgbHasIt",atSecond![^1].Reason);
        Assert.Equal([true,false],Launches());
        Assert.Single(LogLines("OpenRGB still has the hub: restarting it without"));
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running&&Allowed(hub));
        await RunLong(TimeSpan.FromSeconds(30));
        Assert.Equal(2,runner.Count);
    }

    // The worker's stopped-pump hand-back while the saved mode is driven ends in mode stopped.
    [Fact]
    public async Task A_stopped_pump_ends_in_stopped()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        hub.Snapshot=hub.Snapshot with{State="letGo",SoftwareAt=0,PumpStopped=true};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack");
        Assert.Contains("\"Mode\": \"stopped\"",FileText());
        Assert.Contains("\"Kind\": \"pumpStopped\"",FileText());
        Assert.Equal("off",LastAllow(hub).Reason);
        Assert.Single(LogLines("hub mode settled: driven -> stopped (the pump read 0 RPM)"));
        Assert.Equal("pumpStopped",h.State.Hub!.Stop!.Kind);
    }

    // Ruling P17: the stopped-pump branch acts once per PumpStopped snapshot, so a save that fails is logged once and stopped stays in memory.
    [Fact]
    public async Task A_stopped_pump_whose_save_fails_logs_once()
    {
        SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var hub=new FakeHub();
        var store=new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full"));
        var h=Build(hub:hub,detector:new HubDetector(),store:store);Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        hub.Snapshot=hub.Snapshot with{State="letGo",SoftwareAt=0,PumpStopped=true};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack");
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Single(LogLines("saving the hub mode failed"));
        Assert.Equal("gaveBack",h.State.Hub!.State);
        Assert.Equal("off",LastAllow(hub).Reason);
        Assert.Equal("stopped",h.State.Settings.Hub.Mode);
    }

    // Review Focus 4, the owner's rig: OpenRGB lists the hub although launched without the detector; one restart, then let go for the run.
    [Fact]
    public async Task OpenRGB_still_listing_the_hub_gets_one_restart()
    {
        UseOwnerRig();
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await AdvanceUntil(()=>hub.Allows.Any(a=>a.Reason=="openRgbHasIt"));
        Assert.DoesNotContain(hub.Allows,a=>a.Reason is null);
        await AdvanceUntil(()=>runner.Count==2,limit:TimeSpan.FromSeconds(60));
        Assert.InRange(runner.StartedAt(1)-runner.StartedAt(0),TimeSpan.FromSeconds(4),TimeSpan.FromSeconds(9));
        await AdvanceUntil(()=>LastAllow(hub)==("openRgbHasIt","restartSpent"));
        await RunLong(TimeSpan.FromSeconds(60));
        Assert.Equal(2,runner.Count);
        Assert.Single(LogLines("OpenRGB still has the hub: restarting it without"));
        Assert.Equal(("openRgbHasIt","restartSpent"),LastAllow(hub));
        Assert.DoesNotContain(hub.Allows,a=>a.Reason is null);
        Assert.Equal([false,false],Launches());
    }

    [Theory]
    [InlineData("driving")]
    [InlineData("letGo")]
    [InlineData("gaveBack")]
    public async Task The_state_follows_mode_and_snapshot(string expected)
    {
        if(expected=="gaveBack") SaveHub("{\"Mode\":\"stopped\",\"Stop\":{\"Kind\":\"slower\",\"Part\":\"pump\",\"Number\":1,\"Before\":2716,\"After\":2444}}");
        else SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        if(expected=="gaveBack")
        {
            await AdvanceUntil(()=>LastAllow(hub).Reason=="off");
            var view=h.State.Hub!;
            Assert.Equal("gaveBack",view.State);
            Assert.Equal(("slower","pump",1,2716,2444),(view.Stop!.Kind,view.Stop.Part,view.Stop.Number,view.Stop.Before,view.Stop.After));
            return;
        }
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        if(expected=="driving"){Assert.Null(h.State.Hub!.Reason);return;}
        hub.Snapshot=hub.Snapshot with{State="letGo",Reason="silent",SoftwareAt=0};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="letGo");
        Assert.Equal("silent",h.State.Hub!.Reason);
    }

    // Ruling P33 (fix round 1, M5): between the first connect and the worker's first take the let-go reason is starting; once the worker has
    // held the hub, a let-go with no reason of its own reads as one.
    [Fact]
    public async Task Between_the_first_connect_and_the_first_take_the_reason_is_starting()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        Assert.Equal("off",hub.Snapshot.State);
        Assert.Equal(("letGo","starting"),(h.State.Hub!.State,h.State.Hub.Reason));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        hub.Snapshot=hub.Snapshot with{State="letGo",SoftwareAt=0};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="letGo");
        Assert.Null(h.State.Hub!.Reason);
    }

    // Ruling R9: the worker's hub is listed under its fixed key once the snapshot changes, and a paint after that reaches the worker.
    [Fact]
    public async Task A_changed_snapshot_lists_the_hub()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        Assert.DoesNotContain(h.State.Devices,d=>d.Key=="linkhub");
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Devices.Any(d=>d.Key=="linkhub"&&d.Hub=="driven"));
        await AdvanceUntil(()=>hub.Posts.Count>0);
        Assert.Equal(4,hub.Posts[^1].Length);
    }

    // Ruling P14: a hub on its own stays in the device list across a restart, from its saved parts, and its tuning works.
    [Fact]
    public async Task A_hub_on_its_own_after_a_restart_is_listed_from_its_saved_parts()
    {
        SaveHub("{\"Mode\":\"stopped\",\"Stop\":{\"Kind\":\"pumpStopped\",\"Part\":\"pump\",\"Number\":1},\"Parts\":"+FourParts+"}");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        var view=h.State.Devices.Single(d=>d.Key=="linkhub");
        Assert.Equal("own",view.Hub);
        Assert.Equal(4,view.Zones.Count);
        Assert.True((await Send(h,new SetDeviceTuningRequest("linkhub",Brightness:70))).Ok);
        Assert.Equal(70,h.State.Devices.Single(d=>d.Key=="linkhub").Brightness);
    }

    [Fact]
    public async Task New_parts_in_mode_driven_are_saved_once()
    {
        SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        IReadOnlyList<HubPart> five=[OwnersParts[0],OwnersParts[1],OwnersParts[2],new(4,0x01,0x00,"iCUE LINK QX RGB",34,false,"fan"),OwnersParts[3]];
        hub.Snapshot=new HubSnapshot("driving",Parts:five,ReadingsAt:time.GetTimestamp(),SoftwareAt:time.GetTimestamp());
        hub.Raise();
        await AdvanceUntil(()=>new LightingStore(lightingPath).Load().Settings.Hub.Parts?.Count==5);
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        var text=FileText();
        Assert.Contains("\"Channel\": 4",text);
        Assert.Contains("\"Type\": 1,",text);
        var parts=new LightingStore(lightingPath).Load().Settings.Hub.Parts!;
        Assert.Equal([(1,15,0),(2,15,0),(3,15,0),(4,1,0),(13,17,2)],parts.Select(p=>(p.Channel,p.Type,p.Model)));
        Assert.Single(LogLines("hub parts saved"));
    }

    // The dialog's reading in mode openrgb leaves the file alone.
    [Fact]
    public async Task A_reading_in_mode_openrgb_saves_no_parts()
    {
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        var at=new DateTimeOffset(2026,11,26,12,0,0,TimeSpan.Zero);
        hub.Snapshot=HubSnapshot.Idle with{Parts=OwnersParts,Read=new HubReading(true,at)};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.Read?.At==at);
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Empty(LogLines("hub parts saved"));
    }

    // Ruling P18: a second reading with unchanged parts reaches the window, which gets states only as pushed events.
    [Fact]
    public async Task Each_reading_raises_a_state_with_its_result()
    {
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());
        var states=Recorded(h);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        var first=new DateTimeOffset(2026,11,26,12,0,0,TimeSpan.Zero);var second=first.AddSeconds(5);
        hub.Snapshot=HubSnapshot.Idle with{Parts=OwnersParts,Read=new HubReading(true,first)};
        hub.Raise();
        await Until(()=>Copy(states).Any(s=>s.Hub?.Read?.At==first));
        hub.Snapshot=hub.Snapshot with{Read=new HubReading(true,second)};
        hub.Raise();
        await Until(()=>Copy(states).Any(s=>s.Hub?.Read?.At==second));
        Assert.Equal([first,second],Copy(states).Select(s=>s.Hub?.Read?.At).OfType<DateTimeOffset>().Distinct());
    }

    [Fact]
    public async Task A_new_let_go_reason_raises_a_state()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());
        var states=Recorded(h);
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        var from=Copy(states).Count;
        hub.Snapshot=new HubSnapshot("letGo","silent");
        hub.Raise();
        await Until(()=>Copy(states).Any(s=>s.Hub?.Reason=="silent"));
        hub.Snapshot=new HubSnapshot("letGo","fallsBack");
        hub.Raise();
        await Until(()=>Copy(states).Any(s=>s.Hub?.Reason=="fallsBack"));
        Assert.Equal(["silent","fallsBack"],Copy(states).Skip(from).Where(s=>s.Hub?.State=="letGo"&&s.Hub.Reason is not null).Select(s=>s.Hub!.Reason!).Distinct());
    }

    // Readings go out as their own event, once per round, with their age on the host's clock, and only while the hub is driven.
    [Fact]
    public async Task Each_reading_round_goes_out_once_with_its_age()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());
        var events=new List<HubReadingsEvent>();
        h.HubReadingsChanged+=e=>{lock(events)events.Add(e);};
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await Until(()=>Copy(events).Count==1);
        Assert.Equal(0,Copy(events)[0].Readings.AgeMs);
        Assert.Equal(2774,Copy(events)[0].Readings.Parts.Single(p=>p.Kind=="pump").Rpm);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Single(Copy(events));
        hub.Snapshot=hub.Snapshot with{ReadingsAt=time.GetTimestamp()-(long)(1.2*time.TimestampFrequency)};
        hub.Raise();
        await Until(()=>Copy(events).Count==2);
        Assert.Equal(1200,Copy(events)[1].Readings.AgeMs);
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(2,Copy(events).Count);
        hub.Snapshot=hub.Snapshot with{State="letGo",Reason="silent",SoftwareAt=0,ReadingsAt=time.GetTimestamp()};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="letGo");
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal(2,Copy(events).Count);
    }

    // Ruling P24: while OpenRGB's start is pending the loop scans nothing, so the hub step scans by itself on slow turns in mode driven.
    [Fact]
    public async Task While_a_start_is_pending_the_hub_step_scans_by_itself()
    {
        SaveMode("driven");
        var port=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector(),portOpen:(_,_)=>port.Task);Start(h);
        try
        {
            await Until(()=>runner.Count==1);
            var scanned=Volatile.Read(ref scans);
            await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+2);
            Assert.Equal("starting",LastAllow(hub).Reason);
            lock(conflicts)conflicts.Add(ICue);
            await AdvanceUntil(()=>LastAllow(hub).Reason=="icue");
            Assert.Equal(HostStatus.Starting,h.State.Status);
        }
        finally{port.TrySetResult(true);}
        // Fix round 1, I1: the own scan's iCUE stands once the start has resolved, until the loop's own scan replaces it: no Allow(null)
        // until a later scan finds iCUE gone.
        var from=hub.Allows.FindLastIndex(a=>a.Reason=="icue");
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        await Until(()=>runner.At(0).Killed);
        var cleared=hub.Allows.Count;
        lock(conflicts)conflicts.Remove(ICue);
        await AdvanceUntil(()=>Allowed(hub),limit:TimeSpan.FromSeconds(60));
        Assert.DoesNotContain(hub.Allows.Skip(from).Take(cleared-from),a=>a.Reason is null);
        Assert.Equal("icue",hub.Allows[cleared-1].Reason);
    }

    // Ruling R30 (spec "Conflicts": a take runs a fresh scan first): after a forbid, holding is allowed only once a fresh scan of the hub step
    // finds nothing. Here iCUE starts after the loop's last scan and before lighting connects, as a launcher may at sign-in.
    [Fact]
    public async Task A_take_waits_for_a_fresh_scan()
    {
        SaveMode("driven");
        var port=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector(),portOpen:(_,_)=>port.Task);Start(h);
        try
        {
            await Until(()=>runner.Count==1);
            await AdvanceUntil(()=>hub.Allows.Count>0);
            Assert.Equal(1,Volatile.Read(ref scans)); // only the loop's first scan, before iCUE: no slow turn yet
            lock(conflicts)conflicts.Add(ICue);
        }
        finally{port.TrySetResult(true);}
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>hub.Allows.Count>=2);
        Assert.Equal(new (string?,string?)[]{("starting",null),("icue",null)},hub.Allows);
        Assert.Equal(2,Volatile.Read(ref scans));
        await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);
        lock(conflicts)conflicts.Remove(ICue);
        await AdvanceUntil(()=>Allowed(hub),limit:TimeSpan.FromSeconds(60));
        Assert.Equal(new (string?,string?)[]{("starting",null),("icue",null),(null,null)},hub.Allows);
    }

    // R30's scan is for a take only: a hub already allowed adds no scan to any turn, only the loop's slow ones (every 5 s).
    [Fact]
    public async Task An_allowed_hub_adds_no_scans_per_turn()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        var before=Volatile.Read(ref scans);
        await RunFor(TimeSpan.FromSeconds(20),DeviceHost.Tick);
        Assert.InRange(Volatile.Read(ref scans)-before,3,5);
        Assert.Null(LastAllow(hub).Reason);
    }

    // Ruling P24: a pending start is when OpenRGB is launched, and the new process would count as another OpenRGB to a scan begun before it.
    [Fact]
    public async Task A_scan_that_raced_a_relaunch_is_dropped()
    {
        SaveMode("driven");
        var hub=new FakeHub();
        var launch=NewHold();
        var launched=0;
        Volatile.Write(ref beforeLaunch,()=>{if(Interlocked.Increment(ref launched)==1) launch.Park();});
        var h=Build(hub:hub,detector:new HubDetector());Start(h);
        await Until(()=>launch.Parked);
        Assert.Equal(0,runner.Count);
        var scan=NewHold();
        Volatile.Write(ref scanHold,scan);
        await AdvanceUntil(()=>scan.Parked);
        Volatile.Write(ref scanHold,null);
        launch.Release();
        await Until(()=>h.State.Status==HostStatus.Running);
        scan.Release();
        var scanned=Volatile.Read(ref scans);
        await AdvanceUntil(()=>Volatile.Read(ref scans)>=scanned+2&&Allowed(hub));
        Assert.DoesNotContain(hub.Allows,a=>a.Reason=="otherOpenRgb");
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.False(runner.At(0).Killed);
    }
}
