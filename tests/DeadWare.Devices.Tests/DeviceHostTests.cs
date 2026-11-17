using System.Diagnostics;
using DeadWare.Core;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class DeviceHostTests : IAsyncLifetime
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-host-"+Guid.NewGuid().ToString("N"));
    readonly string exe,lightingPath;
    readonly FakeTimeProvider time=new();
    readonly FakeRunner runner=new();
    readonly FakeOpenRgbSession session=FakeOpenRgbSession.FromFixture();
    readonly HostLog log;
    readonly List<LightingConflict> conflicts=new();
    int scans;
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
    static readonly LightingConflict DeadLight=new(ConflictKind.DeadLight,"DeadLight is running.",true);

    DeviceHost Build(bool startup=false,string? exePath=null)
    {
        var engine=new OpenRgbEngine(exePath??exe,@"C:\cfg",runner,(_,_)=>Task.FromResult(true),time,log);
        var scanner=new Scanner(_=>{Interlocked.Increment(ref scans);lock(conflicts)return conflicts.ToList();});
        var options=new HostOptions(exePath??exe,@"C:\cfg",lightingPath,Path.Combine(dir,"logs"),startup,"S-1-5-21-1",1);
        return host=new DeviceHost(options,new LightingStore(lightingPath),engine,()=>session,scanner,time,log);
    }
    void Start(DeviceHost h)=>run=h.RunAsync(CancellationToken.None);
    static async Task Until(Func<bool> cond){for(int i=0;i<500&&!cond();i++) await Task.Delay(10);Assert.True(cond(),"condition not reached");}
    async Task Advance(TimeSpan total,TimeSpan? step=null)
    {
        var s=step??TimeSpan.FromMilliseconds(500);
        for(var t=TimeSpan.Zero;t<total;t+=s){time.Advance(s);await Task.Delay(5);}
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
        await Advance(TimeSpan.FromSeconds(5));
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(1,runner.Count);
    }

    [Fact]
    public async Task Conflict_appearing_while_running_stops_engine_and_blocks()
    {
        var h=await Running();
        lock(conflicts)conflicts.Add(DeadLight);
        await Advance(TimeSpan.FromSeconds(5));
        await Until(()=>h.State.Status==HostStatus.Blocked);
        Assert.True(runner.At(0).Killed);
        var leds=session.Leds.Count;
        await Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(leds,session.Leds.Count); // nothing is painted while blocked
        lock(conflicts)conflicts.Clear();
        await Advance(TimeSpan.FromSeconds(5));
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(2,runner.Count);
    }

    [Fact]
    public async Task Startup_flag_waits_ten_seconds_before_scanning()
    {
        var h=Build(startup:true);Start(h);
        await Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0,Volatile.Read(ref scans));
        Assert.Equal(0,runner.Count);
        await Advance(TimeSpan.FromSeconds(1));
        await Until(()=>Volatile.Read(ref scans)>0);
        await Until(()=>h.State.Status==HostStatus.Running);
    }

    [Fact]
    public async Task Missing_openrgb_reports_OpenRgbMissing()
    {
        var h=Build(exePath:Path.Combine(dir,"nope","OpenRGB.exe"));Start(h);
        await Until(()=>h.State.Status==HostStatus.OpenRgbMissing);
        Assert.Contains("not found",h.State.Message);
        Assert.Equal(0,runner.Count);
    }

    [Fact]
    public async Task Engine_restart_resets_reconnects_rescans_and_repaints()
    {
        var h=await Running();
        await Advance(TimeSpan.FromSeconds(1));
        var connects=session.Connects;var reads=session.Reads;var leds=session.Leds.Count;
        Assert.Equal(1,connects);
        runner.At(0).Exit();
        await Until(()=>h.State.Status==HostStatus.Waiting);
        await Advance(TimeSpan.FromSeconds(5));
        await Until(()=>h.State.Status==HostStatus.Running);
        Assert.Equal(2,runner.Count);
        Assert.Equal(connects+1,session.Connects);
        Assert.True(session.Reads>reads);
        await Advance(TimeSpan.FromSeconds(1));
        Assert.True(session.Leds.Count>leds); // Reset forgot what was sent, so the new process is painted afresh
    }

    [Fact]
    public async Task Engine_failure_reports_OpenRgbFailed_with_reason()
    {
        var h=await Running();
        for(var i=0;i<6&&h.State.Status!=HostStatus.OpenRgbFailed;i++)
        {
            await Until(()=>runner.Count>i);
            runner.At(i).Exit();
            await Advance(TimeSpan.FromSeconds(65),TimeSpan.FromSeconds(1));
        }
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
        await Advance(TimeSpan.FromSeconds(1));
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
    public async Task Stop_kills_engine_and_ends_RunAsync()
    {
        var h=await Running();
        var reply=await Send(h,new StopRequest{Id=3});
        Assert.True(reply.Ok);Assert.Equal(3,reply.Id);
        await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runner.At(0).Killed);
    }
}
