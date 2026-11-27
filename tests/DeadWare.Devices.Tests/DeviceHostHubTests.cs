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
        // The stop now waits on the 1.5 s hand-back limit, on fake time that has not moved: OpenRGB's stop can only come after it.
        await Until(()=>time.Waiting(TimeSpan.FromMilliseconds(1500)));
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

    // ---- Lighting 5, Task 10: readHub, setHubMode and the take-over, on the owner's rig. "The reading" is the dialog's: pump 2,716 RPM, fans
    // 1,337, 1,326 and 1,337.
    static readonly HubReadings TheReading=new([new(1,"fan","iCUE LINK RX RGB",1337),new(2,"fan","iCUE LINK RX RGB",1326),new(3,"fan","iCUE LINK RX RGB",1337),
        new(13,"pump","iCUE LINK TITAN 360",2716,43.7)]);
    static readonly int?[] ReadingFans=[1337,1326,1337];
    const string StartedLine="hub mode change: OpenRGB started without the hub",SlowLine="hub hand-back is taking longer than 1.5 s";

    // The dialog's reading in the fake's snapshot, taken age ago; ok false: one that could not be taken.
    void PlaceReading(FakeHub hub,TimeSpan? age=null,bool ok=true)
        =>hub.Snapshot=hub.Snapshot with{Parts=OwnersParts,Read=new HubReading(ok,time.GetUtcNow()-(age??TimeSpan.Zero),ok?TheReading:null)};
    // OpenRGB lists the hub only when it was launched with its hub detector on, as 1.0rc3 does.
    void HubFollowsDetector(HubDetector detector)
    {
        var with=session.Controllers;
        var without=with.Where(c=>c.Name!=FakeOpenRgbSession.OwnerHub).Select((c,i)=>c with{Index=i}).ToList();
        Volatile.Write(ref beforeLaunch,()=>session.Controllers=detector.On?with:without);
    }
    // The owner's rig with FakeHub (seen through wrap when given) and a detector, started. follow: OpenRGB lists the hub only when launched with
    // the detector on.
    (DeviceHost H,FakeHub Hub,HubDetector Detector) OwnerHost(Func<FakeHub,IHub>? wrap=null,LightingStore? store=null,FakeSignals? signals=null,
        string configDir="",bool follow=true,Func<int,CancellationToken,Task<bool>>? portOpen=null)
    {
        UseOwnerRig();
        var hub=new FakeHub();
        var detector=new HubDetector();
        if(follow) HubFollowsDetector(detector);
        var h=Build(hub:wrap is null?hub:wrap(hub),detector:detector,store:store,signals:signals,input:signals is null?null:new FakeInput(),configDir:configDir,portOpen:portOpen);
        Start(h);
        return (h,hub,detector);
    }
    // Once the host runs: the reading placed (or one that could not be taken) and the take-over accepted.
    async Task TakeOver(DeviceHost h,FakeHub hub,bool reading=true)
    {
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub,ok:reading);
        Assert.True((await Send(h,new SetHubModeRequest("driven",hub.Snapshot.Read))).Ok);
    }
    // From the take-over's start to its check: the worker allowed once OpenRGB has stopped, the fake drives at the reading's speeds, and OpenRGB's
    // start is issued. Returns when the fake drove, which is its SoftwareAt.
    async Task<DateTimeOffset> ToCheck(DeviceHost h,FakeHub hub)
    {
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time,2716,ReadingFans);
        var at=time.GetUtcNow();
        await AdvanceUntil(()=>LogLines(StartedLine).Length==1);
        return at;
    }
    // A reading round at after past the take; returns once the host has handled it (its readings event came).
    async Task RoundAt(DeviceHost h,FakeHub hub,DateTimeOffset at,TimeSpan after,int pump,int?[] fans)
    {
        await AdvanceUntil(()=>time.GetUtcNow()-at>=after);
        Assert.Equal(after,time.GetUtcNow()-at);
        var seen=0;
        void Count(HubReadingsEvent _)=>Interlocked.Increment(ref seen);
        h.HubReadingsChanged+=Count;
        try{hub.Round(time,pump,fans);await Until(()=>Volatile.Read(ref seen)>0);}
        finally{h.HubReadingsChanged-=Count;}
    }
    List<int> SavedChannels()=>new LightingStore(lightingPath).Load().Settings.Hub.Parts?.Select(p=>p.Channel).ToList()??[];
    static string Text(string line){var t=line.TrimEnd();return t[(t.IndexOf(' ')+1)..];}

    [Fact]
    public async Task Read_hub_answers_at_once_and_asks_the_worker()
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        var scanned=Volatile.Read(ref scans);
        var answer=h.HandleAsync(new ReadHubRequest());
        Assert.True(answer.IsCompleted);
        Assert.True(Assert.IsType<Reply>(await answer).Ok);
        Assert.Equal(1,hub.ReadingRequests);
        Assert.Equal(scanned,Volatile.Read(ref scans));
        Assert.Single(LogLines("hub reading requested"));
    }

    [Theory]
    [InlineData("icue")]
    [InlineData("driven")]
    [InlineData("suspended")]
    [InlineData("blocked")]
    public async Task Read_hub_is_refused_with_its_reasons(string why)
    {
        var hub=new FakeHub();
        DeviceHost h;
        var signals=new FakeSignals();
        if(why=="blocked")
        {
            h=BuildScanned(hub,_=>{Interlocked.Increment(ref scans);throw new InvalidOperationException("scan broke");});Start(h);
            await Until(()=>h.State.Status==HostStatus.Blocked);
        }
        else
        {
            if(why=="driven") SaveMode("driven");
            (h,hub,_)=OwnerHost(signals:signals);
            await Until(()=>h.State.Status==HostStatus.Running);
            if(why=="icue"){lock(conflicts)conflicts.Add(ICue);await AdvanceUntil(()=>h.State.Status==HostStatus.Blocked);}
            if(why=="suspended") signals.Suspend();
        }
        var reply=await Send(h,new ReadHubRequest());
        Assert.False(reply.Ok);
        switch(why)
        {
            case "icue":Assert.Equal("DeadWare can't take the hub yet: iCUE is running. See below for what to do, then try again.",reply.Error);break;
            case "blocked":Assert.Equal("DeadWare can't take the hub yet: lighting is not running. See below for what to do, then try again.",reply.Error);break;
            default:Assert.Equal("hub busy",reply.Error);break;
        }
        Assert.Equal(0,hub.ReadingRequests);
    }

    [Theory]
    [InlineData(120,true)]
    [InlineData(121,false)]
    [InlineData(-1,false)]
    public async Task A_reading_older_than_two_minutes_is_refused(int seconds,bool accepted)
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        if(seconds>=0) PlaceReading(hub,TimeSpan.FromSeconds(seconds));
        var reply=await Send(h,new SetHubModeRequest("driven",hub.Snapshot.Read));
        Assert.Equal(accepted?(true,(string?)null):(false,"hub stale"),(reply.Ok,reply.Error));
        Assert.Equal(accepted?"switching":"off",h.State.Hub!.State);
    }

    // Change reports success in memory while saves are off, so the hub requests check the save condition themselves.
    [Theory]
    [InlineData("driven")]
    [InlineData("openrgb")]
    public async Task Settings_that_cannot_be_saved_refuse_both_directions(string mode)
    {
        File.WriteAllText(lightingPath,"{\"Version\":6,\"Hub\":{\"Mode\":\""+mode+"\"}}");
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(mode,h.State.Settings.Hub.Mode);
        PlaceReading(hub);
        const string cant="DeadWare can't take the hub yet: DeadWare can't save its lighting settings. See below for what to do, then try again.";
        Assert.Equal(cant,(await Send(h,new SetHubModeRequest("openrgb"))).Error);
        Assert.Equal(cant,(await Send(h,new SetHubModeRequest("driven"))).Error);
        if(mode=="openrgb") Assert.Equal(cant,(await Send(h,new ReadHubRequest())).Error);
        Assert.Equal(mode,h.State.Settings.Hub.Mode);
        Assert.NotEqual("switching",h.State.Hub!.State);
        Assert.Equal(0,hub.ReadingRequests);
    }

    [Fact]
    public async Task A_second_request_while_switching_is_refused()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        Assert.Equal("hub busy",(await Send(h,new SetHubModeRequest("driven"))).Error);
        Assert.Equal("hub busy",(await Send(h,new SetHubModeRequest("openrgb"))).Error);
        Assert.Equal("hub busy",(await Send(h,new ReadHubRequest())).Error);
        Assert.Equal(0,hub.ReadingRequests);
        Assert.Equal("switching",h.State.Hub!.State);
    }

    [Fact]
    public async Task A_mode_change_while_suspended_is_refused()
    {
        var signals=new FakeSignals();
        var (h,hub,_)=OwnerHost(signals:signals);
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub);
        signals.Suspend();
        Assert.Equal("hub busy",(await Send(h,new SetHubModeRequest("driven"))).Error);
        Assert.Equal("off",h.State.Hub!.State);
    }

    [Fact]
    public async Task A_reading_with_a_blocker_refuses_the_take_over()
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        hub.Snapshot=hub.Snapshot with{Read=new HubReading(true,time.GetUtcNow(),null,"unknownPart","type 0x1A, model 0x01")};
        const string refused="DeadWare can't take the hub yet: the hub has a part DeadWare doesn't know (type 0x1A, model 0x01). See below for what to do, then try again.";
        Assert.Equal(refused,(await Send(h,new SetHubModeRequest("driven"))).Error);
        Assert.Single(LogLines("hub mode change refused: "+refused));
        Assert.Equal("off",h.State.Hub!.State);
    }

    // A request runs synchronously on the thread that sends it (HandleAsync answers with a finished task), so a scan on that thread during the
    // call is a scan by the request.
    [Fact]
    public async Task Requests_never_scan()
    {
        var hub=new FakeHub();
        var asking=-1;var scannedAsking=0;
        var h=BuildScanned(hub,pid=>
        {
            Interlocked.Increment(ref scans);
            if(Volatile.Read(ref asking)==Environment.CurrentManagedThreadId) Interlocked.Increment(ref scannedAsking);
            return LightingConflicts.Find(runner.Live(),pid).ToList();
        });
        Start(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub);
        Reply Ask(HostMessage m)
        {
            Volatile.Write(ref asking,Environment.CurrentManagedThreadId);
            try{var t=h.HandleAsync(m);Assert.True(t.IsCompleted);return Assert.IsType<Reply>(t.Result);}
            finally{Volatile.Write(ref asking,-1);}
        }
        var before=Volatile.Read(ref scans);
        Assert.True(Ask(new ReadHubRequest()).Ok);
        Assert.True(Ask(new SetHubModeRequest("driven",hub.Snapshot.Read)).Ok);
        Assert.Equal("hub busy",Ask(new SetHubModeRequest("openrgb")).Error);
        Assert.Equal("bad hub mode",Ask(new SetHubModeRequest("stopped")).Error);
        Assert.Equal(0,Volatile.Read(ref scannedAsking));
        Assert.Equal(before,Volatile.Read(ref scans));
    }

    [Fact]
    public async Task A_take_over_stops_OpenRGB_before_the_take_and_starts_it_without_the_hub()
    {
        Watched? watched=null;
        var (h,hub,_)=OwnerHost(wrap:f=>watched=new Watched(f,NewestRuns));
        var states=Recorded(h);
        await Until(()=>h.State.Status==HostStatus.Running);
        var from=Copy(states).Count;
        await TakeOver(h,hub);
        await AdvanceUntil(()=>Allowed(hub));
        Assert.True(runner.At(0).HasExited);
        Assert.Equal((null,false,1),watched!.Seen.First(a=>a.Reason is null));
        // OpenRGB's 4 s floor passes, and still nothing is launched before the fake drives.
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick);
        Assert.Equal([true],Launches());
        hub.Drive(time,2716,ReadingFans);
        var at=time.GetUtcNow();
        await AdvanceUntil(()=>Launches().Count==2);
        Assert.Equal([true,false],Launches());
        Assert.Equal("switching",h.State.Hub!.State);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2716,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        var seen=Copy(states).Skip(from).Select(s=>s.Hub!.State).ToList();
        var other=seen.FindIndex(s=>s!="switching");
        Assert.True(other>0);
        Assert.Equal("driving",seen[other]);
        Assert.Contains("\"Mode\": \"driven\"",FileText());
    }

    // Ruling P22: the owner asked, so the take does not sit out the retry delay an earlier failed take left.
    [Fact]
    public async Task A_take_over_right_after_a_failed_take_still_takes()
    {
        Watched? watched=null;
        var (h,hub,_)=OwnerHost(wrap:f=>watched=new Watched(f,NewestRuns));
        await Until(()=>h.State.Status==HostStatus.Running);
        hub.Snapshot=hub.Snapshot with{State="letGo",Reason="silent"};
        hub.Raise();
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        await TakeOver(h,hub);
        await AdvanceUntil(()=>Allowed(hub));
        Assert.Equal((null,false,1),watched!.Seen.First(a=>a.Reason is null));
        Assert.Equal(("letGo","silent"),(hub.Snapshot.State,hub.Snapshot.Reason));
        await RunFor(TimeSpan.FromSeconds(2),DeviceHost.Tick);
        Assert.Equal("switching",h.State.Hub!.State);
        hub.Drive(time,2716,ReadingFans);
        await AdvanceUntil(()=>LogLines(StartedLine).Length==1);
        Assert.Equal("switching",h.State.Hub!.State);
        Assert.Null(h.State.Hub.Problem);
    }

    [Fact]
    public async Task A_pass_saves_driven_only_at_the_15_s_mark()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(14),2445,ReadingFans);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Equal("switching",h.State.Hub!.State);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2445,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        var text=FileText();
        Assert.Contains("\"Mode\": \"driven\"",text);
        Assert.Contains("\"Version\": 5",text);
        Assert.Contains("\"Channel\": 13",text);
        Assert.Equal([1,2,3,13],SavedChannels());
        var view=h.State.Hub!;
        Assert.Equal(2716,view.Before!.Parts.Single(p=>p.Kind=="pump").Rpm);
        Assert.NotNull(view.TakenAt);
        Assert.InRange(view.TakenAt!.Value,at,at.AddSeconds(1));
        Assert.Equal("hub check: pump 2,716 -> 2,445 RPM, fan 1 1,337 -> 1,337 RPM, fan 2 1,326 -> 1,326 RPM, fan 3 1,337 -> 1,337 RPM: pass",Text(LogLines("hub check:").Single()));
        Assert.Single(LogLines("hub mode settled: openrgb -> driven"));
        Assert.Null(LastAllow(hub).Reason);
    }

    [Fact]
    public async Task A_slower_pump_gives_back_and_saves_stopped()
    {
        var (h,hub,detector)=OwnerHost();
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2444,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack"&&LastAllow(hub).Reason=="off");
        var text=FileText();
        Assert.Contains("\"Mode\": \"stopped\"",text);
        Assert.Contains("\"Kind\": \"slower\"",text);
        Assert.Contains("\"Before\": 2716",text);
        Assert.Contains("\"After\": 2444",text);
        Assert.Equal([1,2,3,13],SavedChannels());
        var stop=h.State.Hub!.Stop!;
        Assert.Equal(("slower","pump",1,(int?)2716,(int?)2444),(stop.Kind,stop.Part,stop.Number,stop.Before,stop.After));
        Assert.Equal("hub check: pump 2,716 -> 2,444 RPM, fan 1 1,337 -> 1,337 RPM, fan 2 1,326 -> 1,326 RPM, fan 3 1,337 -> 1,337 RPM: fail",Text(LogLines("hub check:").Single()));
        Assert.Single(LogLines("hub mode settled: openrgb -> stopped"));
        await RunFor(TimeSpan.FromSeconds(10),DeviceHost.Tick);
        Assert.Equal([true,false],Launches());
        Assert.Equal(2,runner.Count);
        Assert.False(runner.At(1).Killed);
        Assert.False(detector.On);
    }

    [Fact]
    public async Task A_fan_without_a_reading_fails_the_check()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2716,[1337,null,1337]);
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack");
        var stop=h.State.Hub!.Stop!;
        Assert.Equal(("noReading","fan",2,(int?)1326,(int?)null),(stop.Kind,stop.Part,stop.Number,stop.Before,stop.After));
        Assert.Contains("\"Kind\": \"noReading\"",FileText());
        Assert.Equal("hub check: pump 2,716 -> 2,716 RPM, fan 1 1,337 -> 1,337 RPM, fan 2 1,326 RPM -> no reading, fan 3 1,337 -> 1,337 RPM: fail",Text(LogLines("hub check:").Single()));
    }

    // The check compares against the reading the dialog showed (pump 2,716), not the newer one the snapshot holds (pump 2,000).
    [Theory]
    [InlineData(2445,"driving")]
    [InlineData(2444,"gaveBack")]
    public async Task The_check_compares_against_the_reading_the_dialog_showed(int pumpAfter,string expected)
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        var shown=new HubReading(true,time.GetUtcNow()-TimeSpan.FromSeconds(10),TheReading);
        var newer=new HubReadings([..TheReading.Parts.Where(p=>p.Kind=="fan"),new(13,"pump","iCUE LINK TITAN 360",2000,43.7)]);
        hub.Snapshot=hub.Snapshot with{Parts=OwnersParts,Read=new HubReading(true,time.GetUtcNow(),newer)};
        Assert.True((await Send(h,new SetHubModeRequest("driven",shown))).Ok);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),pumpAfter,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State==expected);
        Assert.Contains("pump 2,716 -> "+pumpAfter.ToString("N0",System.Globalization.CultureInfo.InvariantCulture)+" RPM",Text(LogLines("hub check:").Single()));
    }

    // A dialog that showed no reading means no before reading, whatever the snapshot holds by the time of the click.
    [Fact]
    public async Task A_take_over_after_a_no_reading_dialog_has_no_before_reading()
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub);
        Assert.True((await Send(h,new SetHubModeRequest("driven",null))).Ok);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(14),1000,ReadingFans);
        Assert.DoesNotContain("\"Hub\"",FileText());
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),1000,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        Assert.Contains("\"Mode\": \"driven\"",FileText());
        Assert.Null(h.State.Hub!.Before);
        Assert.Equal("hub check: no before reading, pump 1,000 RPM, fan 1 1,337 RPM, fan 2 1,326 RPM, fan 3 1,337 RPM: pass",Text(LogLines("hub check:").Single()));
    }

    // The window's own reading is held to the same two minutes as the snapshot's.
    [Theory]
    [InlineData(120,true)]
    [InlineData(121,false)]
    public async Task A_shown_reading_older_than_two_minutes_is_refused(int seconds,bool accepted)
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub);
        var shown=new HubReading(true,time.GetUtcNow()-TimeSpan.FromSeconds(seconds),TheReading);
        var reply=await Send(h,new SetHubModeRequest("driven",shown));
        Assert.Equal(accepted?(true,(string?)null):(false,"hub stale"),(reply.Ok,reply.Error));
    }

    [Fact]
    public async Task Without_a_before_reading_driven_is_saved_at_15_s()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub,reading:false);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(14),1000,ReadingFans);
        Assert.DoesNotContain("\"Hub\"",FileText());
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),1000,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        Assert.Contains("\"Mode\": \"driven\"",FileText());
        Assert.Null(h.State.Hub!.Before);
        Assert.Equal("hub check: no before reading, pump 1,000 RPM, fan 1 1,337 RPM, fan 2 1,326 RPM, fan 3 1,337 RPM: pass",Text(LogLines("hub check:").Single()));
    }

    // Ruling P17: the mode is adopted when its save fails, so a pass still drives for this run with the detector off.
    [Fact]
    public async Task A_pass_whose_save_fails_keeps_driven_for_this_run()
    {
        var store=new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full"));
        var (h,hub,detector)=OwnerHost(store:store);
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2716,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Equal("driving",h.State.Hub!.State);
        Assert.Equal("driven",h.State.Settings.Hub.Mode);
        Assert.False(detector.On);
        Assert.Null(LastAllow(hub).Reason);
        Assert.Single(LogLines("saving the hub mode failed"));
        Assert.Equal([true,false],Launches());
    }

    [Fact]
    public async Task A_fail_whose_save_fails_still_gives_back()
    {
        var store=new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full"));
        var (h,hub,_)=OwnerHost(store:store);
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2444,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack"&&LastAllow(hub).Reason=="off");
        Assert.Equal("stopped",h.State.Settings.Hub.Mode);
        Assert.Single(LogLines("saving the hub mode failed"));
    }

    // Fix wave 1, D3: a verdict the settings refuse is an interruption, never dropped while the take-over is marked done. Parts out of channel order
    // stand here for any refusal (a file at the 1 MiB cap among them): the worker lets go first, nothing is saved, the mode stays openrgb, OpenRGB
    // comes back with its detector on and the red line says why.
    [Fact]
    public async Task A_verdict_the_settings_refuse_backs_out_and_saves_nothing()
    {
        Watched? watched=null;
        var (h,hub,detector)=OwnerHost(wrap:f=>watched=new Watched(f,NewestRuns));
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        hub.Snapshot=hub.Snapshot with{Parts=[OwnersParts[3],OwnersParts[0],OwnersParts[1],OwnersParts[2]]};
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2716,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.Problem=="cantSave");
        Assert.Equal("off",h.State.Hub!.State);
        Assert.Single(LogLines("hub settings not changed: "));
        Assert.Single(LogLines("hub take-over interrupted: "));
        Assert.Empty(LogLines("hub mode settled"));
        Assert.Equal("openrgb",h.State.Settings.Hub.Mode);
        Assert.DoesNotContain("\"Hub\"",FileText());
        // The forbid with the reason came before the relaunch, which comes with the detector on.
        var forbid=watched!.Seen.FindIndex(a=>a.Reason=="cantSave");
        Assert.True(forbid>=0);
        Assert.Equal(1,hub.LetGos);
        await AdvanceUntil(()=>Launches().Count==3);
        Assert.Equal([true,false,true],Launches());
        Assert.True(detector.On);
        Assert.NotEqual("driving",hub.Snapshot.State);
    }

    // Ruling P20: a pump that stops during the check is the check's verdict, never an interruption.
    [Fact]
    public async Task A_stopped_pump_during_the_check_saves_stopped()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await AdvanceUntil(()=>time.GetUtcNow()-at>=TimeSpan.FromSeconds(10));
        hub.Snapshot=hub.Snapshot with{State="letGo",SoftwareAt=0,PumpStopped=true};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.State=="gaveBack");
        Assert.Contains("\"Mode\": \"stopped\"",FileText());
        Assert.Contains("\"Kind\": \"pumpStopped\"",FileText());
        Assert.Equal([1,2,3,13],SavedChannels());
        Assert.Null(h.State.Hub!.Problem);
        Assert.Single(LogLines("hub mode settled: openrgb -> stopped (the pump read 0 RPM)"));
        await RunFor(TimeSpan.FromSeconds(10),DeviceHost.Tick);
        Assert.Equal([true,false],Launches());
        Assert.Empty(LogLines("hub take-over interrupted"));
        Assert.Empty(LogLines("hub check:"));
    }

    // Review Focus 3: a take-over cut short saves nothing, ends in openrgb, and brings OpenRGB back with its detector on (ruling R10: not from the
    // Scan step, where nothing was stopped).
    [Theory]
    [InlineData("scan","icue")]
    [InlineData("take silent","silent")]
    [InlineData("take unknownPart","unknownPart")]
    [InlineData("take nothing","silent")]
    [InlineData("check service","service")]
    [InlineData("check noHub","noHub")]
    [InlineData("check openRgbHasIt","openRgbHasIt")]
    public async Task An_interruption_at_each_step_saves_nothing_and_ends_in_openrgb(string row,string problem)
    {
        var (h,hub,detector)=OwnerHost(follow:row!="check openRgbHasIt");
        await TakeOver(h,hub);
        int launches;
        if(row=="scan"){lock(conflicts)conflicts.Add(ICue);launches=1;}
        else if(row.StartsWith("take"))
        {
            await AdvanceUntil(()=>Allowed(hub));
            if(row=="take silent") hub.Snapshot=hub.Snapshot with{State="letGo",Reason="silent"};
            if(row=="take unknownPart") hub.Snapshot=hub.Snapshot with{State="letGo",Reason="unknownPart",Detail="type 0x1A, model 0x01"};
            hub.Raise();
            launches=2;
        }
        else
        {
            // OpenRGB runs without the hub (its second launch).
            await ToCheck(h,hub);
            await AdvanceUntil(()=>Launches().Count==2);
            if(row=="check service") lock(conflicts)conflicts.Add(LegacyService);
            if(row=="check noHub"){hub.Snapshot=hub.Snapshot with{State="letGo",Reason="noHub",SoftwareAt=0};hub.Raise();}
            launches=3;
        }
        await AdvanceUntil(()=>h.State.Hub?.Problem==problem);
        Assert.Equal("off",h.State.Hub!.State);
        Assert.Equal(row=="take unknownPart"?"type 0x1A, model 0x01":null,h.State.Hub.ProblemDetail);
        Assert.Single(LogLines("hub take-over interrupted: "));
        await AdvanceUntil(()=>Launches().Count==launches);
        await RunFor(TimeSpan.FromSeconds(6),DeviceHost.Tick);
        Assert.Equal(launches,Launches().Count);
        Assert.True(Launches()[^1]);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Equal("openrgb",h.State.Settings.Hub.Mode);
        Assert.Equal("off",h.State.Hub!.State);
        Assert.Equal(problem,h.State.Hub.Problem);
        Assert.True(detector.On);
        Assert.NotNull(LastAllow(hub).Reason);
        if(row=="check openRgbHasIt")
        {
            Assert.Empty(LogLines("OpenRGB still has the hub: restarting it without"));
            Assert.DoesNotContain(hub.Allows,a=>a.Detail=="restartSpent");
        }
    }

    // A back-out while OpenRGB's start still waits out its 4 s floor: that pending launch reads the detector by itself, now on, and nothing is
    // launched twice.
    [Fact]
    public async Task A_back_out_while_the_start_waits_its_floor_launches_once_with_the_detector_on()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        await ToCheck(h,hub);
        Assert.Equal([true],Launches());
        hub.Snapshot=hub.Snapshot with{State="letGo",Reason="noHub",SoftwareAt=0};
        hub.Raise();
        await AdvanceUntil(()=>h.State.Hub?.Problem=="noHub");
        await AdvanceUntil(()=>Launches().Count==2);
        await RunFor(TimeSpan.FromSeconds(10),DeviceHost.Tick);
        Assert.Equal([true,true],Launches());
        Assert.Equal(HostStatus.Running,h.State.Status);
        Assert.DoesNotContain("\"Hub\"",FileText());
    }

    // Ruling P4: an OpenRGB that does not exit is a hub still in use.
    [Fact]
    public async Task A_stop_that_cannot_end_OpenRGB_backs_out()
    {
        var (h,hub,_)=OwnerHost();
        try
        {
            await Until(()=>h.State.Status==HostStatus.Running);
            runner.At(0).IgnoresKill=true;
            await TakeOver(h,hub);
            await AdvanceUntil(()=>runner.At(0).Killed);
            await Elapse(TimeSpan.FromSeconds(5));
            await AdvanceUntil(()=>h.State.Hub?.Problem=="noLighting");
            Assert.DoesNotContain(hub.Allows,a=>a.Reason is null);
            Assert.Single(LogLines("hub take-over interrupted: OpenRGB did not exit"));
            Assert.Empty(LogLines("hub mode change: OpenRGB stopped"));
            Assert.DoesNotContain("\"Hub\"",FileText());
            Assert.Equal("off",h.State.Hub!.State);
        }
        finally{runner.At(0).Exit();}
    }

    // Ruling P23: a lock that stays busy skips every round without failing, so the check has an upper limit.
    [Fact]
    public async Task A_check_with_no_reading_round_ends_after_60_s()
    {
        var (h,hub,_)=OwnerHost();
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await AdvanceUntil(()=>time.GetUtcNow()-at>=TimeSpan.FromSeconds(59),limit:TimeSpan.FromSeconds(60));
        Assert.Equal("switching",h.State.Hub!.State);
        Assert.Null(h.State.Hub.Problem);
        await AdvanceUntil(()=>h.State.Hub?.Problem=="silent");
        Assert.InRange(time.GetUtcNow()-at,TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(62));
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Single(LogLines("hub take-over interrupted: the hub stopped answering"));
    }

    // Ruling P25: an OpenRGB launch is a full USB detection, kept out of suspend and the wake's wait.
    [Fact]
    public async Task A_suspend_during_the_check_backs_out_and_restarts_after_the_wake()
    {
        var signals=new FakeSignals();
        var (h,hub,_)=OwnerHost(signals:signals);
        await TakeOver(h,hub);
        await ToCheck(h,hub);
        await AdvanceUntil(()=>Launches().Count==2);
        signals.Suspend();
        await AdvanceUntil(()=>hub.Allows.Any(a=>a.Reason=="asleep")&&hub.Snapshot.State=="letGo");
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Equal(2,Launches().Count);
        signals.Resume();
        await Until(()=>LogLines(" resume").Length>=1);
        await RunFor(TimeSpan.FromSeconds(10),DeviceHost.Tick);
        Assert.Equal(2,Launches().Count);
        await AdvanceUntil(()=>Launches().Count==3);
        Assert.Equal([true,false,true],Launches());
        await AdvanceUntil(()=>h.State.Hub?.Problem=="asleep");
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Equal("off",h.State.Hub!.State);
        Assert.Single(LogLines("hub take-over interrupted: the PC went to sleep"));
    }

    // Ruling P16: switching off and backing out wait for the worker to let go, however long it takes; at 1.5 s they only log.
    [Theory]
    [InlineData("switchOff")]
    [InlineData("backOut")]
    public async Task A_slow_let_go_is_waited_for_and_never_overlapped(string row)
    {
        if(row=="switchOff") SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var (h,hub,detector)=OwnerHost();
        if(row=="switchOff")
        {
            await Until(()=>h.State.Status==HostStatus.Running);
            await AdvanceUntil(()=>Allowed(hub));
            hub.Drive(time);
            await AdvanceUntil(()=>h.State.Hub?.State=="driving");
            hub.AutoLetGo=false;
            Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
            await AdvanceUntil(()=>LastAllow(hub).Reason=="off");
        }
        else
        {
            await TakeOver(h,hub);
            await ToCheck(h,hub);
            await AdvanceUntil(()=>Launches().Count==2);
            hub.AutoLetGo=false;
            lock(conflicts)conflicts.Add(LegacyService);
            await AdvanceUntil(()=>LastAllow(hub).Reason=="service");
        }
        var launches=Launches();
        var count=runner.Count;
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Single(LogLines(SlowLine));
        Assert.Equal(launches,Launches());
        Assert.Equal(count,runner.Count);
        Assert.False(runner.At(count-1).Killed);
        Assert.Equal("driving",hub.Snapshot.State);
        Assert.Equal("switching",h.State.Hub!.State);
        if(row=="switchOff") Assert.Contains("\"Mode\": \"driven\"",FileText());
        else Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.False(detector.On);
        hub.Snapshot=hub.Snapshot with{State="letGo",Reason=row=="switchOff"?"off":"service",SoftwareAt=0};
        hub.Raise();
        await AdvanceUntil(()=>Launches().Count==launches.Count+1);
        Assert.True(Launches()[^1]);
        Assert.True(runner.At(count-1).Killed);
        Assert.DoesNotContain("\"Hub\"",FileText());
        await AdvanceUntil(()=>h.State.Hub?.State=="off");
        Assert.Single(LogLines(SlowLine));
    }

    // Ruling R31 (Task 10 review, M2): a snapshot that has left driving is not enough; backing out and switching off also wait for the worker's
    // let-go, asked for once holding is forbidden, to complete. Until it has: no detector on, no openrgb saved, no OpenRGB started.
    [Fact]
    public async Task A_back_out_waits_for_the_workers_let_go_to_complete()
    {
        var (h,hub,detector)=OwnerHost();
        await TakeOver(h,hub);
        await ToCheck(h,hub);
        await AdvanceUntil(()=>Launches().Count==2);
        hub.HoldLetGo=new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(conflicts)conflicts.Add(LegacyService);
        await AdvanceUntil(()=>hub.Allows.Any(a=>a.Reason=="service")&&hub.Snapshot.State=="letGo");
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Equal("letGo",hub.Snapshot.State);
        Assert.False(detector.On);
        Assert.Equal([true,false],Launches());
        Assert.False(runner.At(1).Killed);
        Assert.Equal("switching",h.State.Hub!.State);
        Assert.Null(h.State.Hub.Problem);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Equal(1,hub.LetGos);
        hub.HoldLetGo!.SetResult();
        await AdvanceUntil(()=>Launches().Count==3);
        Assert.True(Launches()[^1]);
        Assert.True(runner.At(1).Killed);
        await AdvanceUntil(()=>h.State.Hub?.Problem=="service");
        Assert.True(detector.On);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.Equal("openrgb",h.State.Settings.Hub.Mode);
    }

    [Fact]
    public async Task Switching_off_waits_for_the_workers_let_go_to_complete()
    {
        SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var (h,hub,detector)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        hub.HoldLetGo=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
        await AdvanceUntil(()=>hub.Allows.Any(a=>a.Reason=="off")&&hub.Snapshot.State=="letGo");
        await RunFor(TimeSpan.FromSeconds(5),DeviceHost.Tick);
        Assert.Equal("letGo",hub.Snapshot.State);
        Assert.False(detector.On);
        Assert.Contains("\"Mode\": \"driven\"",FileText());
        Assert.Equal("driven",h.State.Settings.Hub.Mode);
        Assert.Equal([false],Launches());
        Assert.False(runner.At(0).Killed);
        Assert.Equal("switching",h.State.Hub!.State);
        Assert.Equal(1,hub.LetGos);
        hub.HoldLetGo!.SetResult();
        await AdvanceUntil(()=>Launches().Count==2);
        Assert.Equal([false,true],Launches());
        Assert.True(runner.At(0).Killed);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.True(detector.On);
        await AdvanceUntil(()=>h.State.Hub?.State=="off");
    }

    // Review Focus 3: a Stop always wins, and waits for nothing but the hand-back.
    [Fact]
    public async Task A_stop_during_a_take_over_is_not_held_up()
    {
        var cfg=Path.Combine(dir,"openrgb-cfg");Directory.CreateDirectory(cfg);
        var file=Path.Combine(cfg,"OpenRGB.json");
        File.WriteAllText(file,"{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":false}}}");
        var (h,hub,_)=OwnerHost(configDir:cfg);
        await TakeOver(h,hub);
        await ToCheck(h,hub);
        await StopHost(h);
        Assert.Equal(1,hub.LetGos);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.True((bool)JsonNode.Parse(File.ReadAllText(file))!["Detectors"]!["detectors"]!["Corsair iCUE Link System Hub"]!);
        Assert.Empty(LogLines("hub mode settled"));
    }

    // Fix wave 1, D10: a Stop while the loop waits for OpenRGB's deliberate stop (an OpenRGB that ignores its kill, whose exit wait is 5 s) is not
    // held up by that wait: the hand-back and the detector come with the fake clock not moved at all, well inside the 1.5 s hand-back limit.
    [Fact]
    public async Task A_stop_while_OpenRGB_is_slow_to_exit_hands_back_at_once()
    {
        var cfg=Path.Combine(dir,"openrgb-cfg");Directory.CreateDirectory(cfg);
        var file=Path.Combine(cfg,"OpenRGB.json");
        File.WriteAllText(file,"{\"Detectors\":{\"detectors\":{\"Corsair iCUE Link System Hub\":false}}}");
        var (h,hub,_)=OwnerHost(configDir:cfg);
        try
        {
            await Until(()=>h.State.Status==HostStatus.Running);
            runner.At(0).IgnoresKill=true;
            await TakeOver(h,hub);
            await AdvanceUntil(()=>runner.At(0).Killed);
            var at=time.GetUtcNow();
            Assert.True((await Send(h,new StopRequest())).Ok);
            await Until(()=>LogLines("host stopping").Length==1);
            await Until(()=>hub.LetGos==1);
            await Until(()=>{try{return File.ReadAllText(file).Contains("true");}catch(IOException){return false;}});
            Assert.True((bool)JsonNode.Parse(File.ReadAllText(file))!["Detectors"]!["detectors"]!["Corsair iCUE Link System Hub"]!);
            Assert.Equal(at,time.GetUtcNow());
            Assert.Equal("noLighting",LastAllow(hub).Reason);
            Assert.DoesNotContain("\"Hub\"",FileText());
        }
        finally{runner.At(0).Exit();}
        await run!.WaitAsync(RealTimeCap);
        Assert.Empty(LogLines("hub mode settled"));
    }

    // A Stop that lands in the middle of a turn of the take-over (here while its fresh scan runs): the machine does nothing more.
    [Fact]
    public async Task A_stop_during_the_take_overs_scan_does_nothing_more()
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        PlaceReading(hub);
        var scan=NewHold();
        Volatile.Write(ref scanHold,scan);
        Assert.True((await Send(h,new SetHubModeRequest("driven",hub.Snapshot.Read))).Ok);
        await AdvanceUntil(()=>scan.Parked);
        Volatile.Write(ref scanHold,null);
        Assert.True((await Send(h,new StopRequest())).Ok);
        scan.Release();
        await run!.WaitAsync(RealTimeCap);
        Assert.Empty(LogLines("hub take-over interrupted"));
        Assert.Empty(LogLines("hub mode change: OpenRGB stopped"));
        Assert.Equal(1,hub.LetGos);
        Assert.Equal([true],Launches());
        Assert.DoesNotContain("\"Hub\"",FileText());
    }

    [Fact]
    public async Task Switching_off_hands_back_saves_openrgb_and_restarts_with_the_detector_on()
    {
        SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var (h,hub,detector)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        Assert.Equal([false],Launches());
        Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
        await AdvanceUntil(()=>h.State.Hub?.State=="off"&&Launches().Count==2);
        Assert.Equal("off",LastAllow(hub).Reason);
        Assert.Equal("letGo",hub.Snapshot.State);
        Assert.Equal([false,true],Launches());
        Assert.True(runner.At(0).Killed);
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.True(detector.On);
        Assert.Single(LogLines("hub mode settled: driven -> openrgb"));
    }

    // Ruling P19: an OpenRGB launched but whose port is not open yet counts as running, and is stopped first.
    [Fact]
    public async Task Switching_off_while_OpenRGB_is_starting_restarts_it()
    {
        SaveMode("driven");
        var port=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (h,hub,_)=OwnerHost(portOpen:(_,ct)=>port.Task.WaitAsync(ct));
        try
        {
            await Until(()=>runner.Count==1);
            await AdvanceUntil(()=>hub.Allows.Count>0);
            Assert.Equal(HostStatus.Starting,h.State.Status);
            Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
            // Stopped by the switch-off within a few turns, not by OpenRGB's own 30 s port wait running out.
            Assert.InRange(await AdvanceUntil(()=>runner.At(0).Killed),TimeSpan.Zero,TimeSpan.FromSeconds(2));
            Assert.InRange(await AdvanceUntil(()=>Launches().Count==2),TimeSpan.Zero,TimeSpan.FromSeconds(5));
            Assert.Equal([false,true],Launches());
            Assert.Empty(LogLines("openrgb port did not open"));
        }
        finally{port.TrySetResult(true);}
        await AdvanceUntil(()=>h.State.Status==HostStatus.Running&&h.State.Hub?.State=="off");
        Assert.DoesNotContain("\"Hub\"",FileText());
    }

    [Fact]
    public async Task Back_to_OpenRGB_from_stopped_does_the_same()
    {
        SaveHub("{\"Mode\":\"stopped\",\"Stop\":{\"Kind\":\"slower\",\"Part\":\"pump\",\"Number\":1,\"Before\":2716,\"After\":2444},\"Parts\":"+FourParts+"}");
        var (h,hub,detector)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal("gaveBack",h.State.Hub!.State);
        Assert.False(detector.On);
        Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
        await AdvanceUntil(()=>h.State.Hub?.State=="off"&&Launches().Count==2);
        Assert.Equal([false,true],Launches());
        Assert.DoesNotContain("\"Hub\"",FileText());
        Assert.True(detector.On);
        Assert.Null(h.State.Hub!.Stop);
        Assert.Single(LogLines("hub mode settled: stopped -> openrgb"));
        Assert.DoesNotContain(hub.Allows,a=>a.Reason is null);
    }

    // Ruling P36 (R8): switching off sets the mode as the verdict does, adopted when its save throws.
    [Fact]
    public async Task Switching_off_with_a_failing_save_still_ends_in_openrgb()
    {
        SaveHub("{\"Mode\":\"driven\",\"Parts\":"+FourParts+"}");
        var store=new LightingStore(lightingPath,write:(_,_)=>throw new IOException("the disk is full"));
        var (h,hub,detector)=OwnerHost(store:store);
        await Until(()=>h.State.Status==HostStatus.Running);
        await AdvanceUntil(()=>Allowed(hub));
        hub.Drive(time);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
        await AdvanceUntil(()=>h.State.Hub?.State=="off"&&Launches().Count==2);
        Assert.Equal("openrgb",h.State.Settings.Hub.Mode);
        Assert.True(Launches()[^1]);
        Assert.True(detector.On);
        var lines=LogLines("hub").Select(Text).ToList();
        var failed=lines.FindIndex(l=>l.StartsWith("saving the hub mode failed: "));
        Assert.True(failed>=0);
        Assert.Single(lines,l=>l.StartsWith("saving the hub mode failed"));
        Assert.Equal(failed+1,lines.IndexOf("hub mode settled: driven -> openrgb"));
    }

    [Fact]
    public async Task Each_mode_change_leaves_its_log_lines()
    {
        var (h,hub,_)=OwnerHost();
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal("bad hub mode",(await Send(h,new SetHubModeRequest("stopped"))).Error);
        Assert.True((await Send(h,new ReadHubRequest())).Ok);
        await TakeOver(h,hub);
        var at=await ToCheck(h,hub);
        await RoundAt(h,hub,at,TimeSpan.FromSeconds(15),2716,ReadingFans);
        await AdvanceUntil(()=>h.State.Hub?.State=="driving");
        Assert.True((await Send(h,new SetHubModeRequest("openrgb"))).Ok);
        await AdvanceUntil(()=>h.State.Hub?.State=="off"&&Launches().Count==3);
        var lines=LogLines("hub ").Select(Text).Where(l=>l.StartsWith("hub mode")||l.StartsWith("hub check")||l.StartsWith("hub reading requested")||l.StartsWith("hub take-over")).ToList();
        Assert.Equal(
        [
            "hub mode change refused: bad hub mode",
            "hub reading requested",
            "hub mode change requested: driven",
            "hub mode change: OpenRGB stopped",
            StartedLine,
            "hub check: pump 2,716 -> 2,716 RPM, fan 1 1,337 -> 1,337 RPM, fan 2 1,326 -> 1,326 RPM, fan 3 1,337 -> 1,337 RPM: pass",
            "hub mode settled: openrgb -> driven",
            "hub mode change requested: openrgb",
            "hub mode settled: driven -> openrgb",
        ],lines);
    }
}
