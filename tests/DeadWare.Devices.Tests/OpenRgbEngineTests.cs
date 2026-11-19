using System.Diagnostics;
using DeadWare.Core;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

public sealed class OpenRgbEngineTests : IAsyncLifetime
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-eng-"+Guid.NewGuid().ToString("N"));
    readonly string exe;
    readonly FakeTimeProvider time=new();
    readonly FakeRunner runner=new();
    readonly HostLog log;
    bool portUp=true;
    int probes;
    readonly List<OpenRgbEngine> engines=new();
    public OpenRgbEngineTests(){Directory.CreateDirectory(dir);exe=Path.Combine(dir,"OpenRGB.exe");File.WriteAllText(exe,"");log=new HostLog(Path.Combine(dir,"logs"));}
    public Task InitializeAsync()=>Task.CompletedTask;
    // Every engine a test made is disposed here under a real-time cap, so a stuck supervisor fails that test instead of hanging the run.
    public async Task DisposeAsync()
    {
        try{foreach(var e in engines) await e.DisposeAsync().AsTask().WaitAsync(RealTimeCap);}
        finally{log.Dispose();try{Directory.Delete(dir,true);}catch{}}
    }
    OpenRgbEngine Track(OpenRgbEngine e){engines.Add(e);return e;}

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
        public ProcessStartInfo? Info;
        public IRunningProcess Start(ProcessStartInfo info){lock(Started){Info=info;var p=new FakeProcess(100+Started.Count);Started.Add(p);return p;}}
        public int Count{get{lock(Started)return Started.Count;}}
        public FakeProcess At(int i){lock(Started)return Started[i];}
    }

    OpenRgbEngine Engine()=>Track(new(exe,@"C:\cfg\openrgb",runner,(_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(portUp);},time,log));
    async Task Advance(TimeSpan total,TimeSpan step)
    {
        for(var t=TimeSpan.Zero;t<total;t+=step){time.Advance(step);await Task.Delay(5);}
    }

    // Ruling AN: the SDK server listens on this PC only. OpenRGB 1.0rc3 binds 0.0.0.0 (every network interface) unless told otherwise,
    // and its SDK has no authentication; the client and the port probe both use 127.0.0.1.
    [Fact]
    public void Arguments_match_spec()=>Assert.Equal(["--server","--server-host","127.0.0.1","--server-port","6744","--noautoconnect","--config",@"C:\cfg\openrgb"],OpenRgbEngine.Arguments(@"C:\cfg\openrgb"));

    [Fact]
    public async Task Missing_exe_fails_without_starting()
    {
        var missing=Path.Combine(dir,"nope","OpenRGB.exe");
        var e=Track(new OpenRgbEngine(missing,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal($"OpenRGB.exe was not found at {missing}. Choose OpenRGB.exe in Settings → Lighting.",e.FailureReason);
        Assert.Equal(0,runner.Count);
    }

    [Fact]
    public async Task Start_runs_with_spec_arguments_and_reports_running()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Running,e.Status);
        Assert.Equal(100,e.ProcessId);
        Assert.Equal(OpenRgbEngine.Arguments(@"C:\cfg\openrgb"),runner.Info!.ArgumentList.ToArray());
    }

    // F4 item 1 (whole-branch review D14): OpenRGB 1.0rc3 uses <APPDATA>\OpenRGB before it reads --config. It loads OpenRGB.json from there
    // and makes, rotates and opens its log where that file says. So OpenRGB starts with APPDATA set to the config folder's parent, whose
    // OpenRGB folder is the config folder itself, since Windows names ignore case.
    [Fact]
    public async Task OpenRGB_starts_with_APPDATA_at_the_config_folders_parent()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        var appData=runner.Info!.Environment["APPDATA"];
        Assert.Equal(@"C:\cfg",appData);
        Assert.Equal(@"C:\cfg\openrgb",Path.Combine(appData!,"OpenRGB"),ignoreCase:true);
    }

    // Its guard: for a config folder of any other name, <APPDATA>\OpenRGB would be another folder, so OpenRGB isn't started, and the reason
    // says why.
    [Fact]
    public async Task A_config_folder_not_named_openrgb_is_refused_without_starting()
    {
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\settings",runner,(_,_)=>Task.FromResult(true),time,log));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal(@"OpenRGB's settings folder for DeadWare, C:\cfg\settings, isn't named openrgb, so DeadWare didn't start OpenRGB: OpenRGB would read settings and keep its log in another folder.",e.FailureReason);
        await Advance(TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(1));
        Assert.Equal(0,runner.Count);
    }

    [Fact]
    public async Task Port_never_opening_within_thirty_seconds_kills_and_backs_off()
    {
        portUp=false;
        var e=Engine();
        var start=e.StartAsync(CancellationToken.None);
        await Until(()=>Volatile.Read(ref probes)>=1); // the 30 s deadline exists before the first probe runs
        await Advance(TimeSpan.FromSeconds(29),TimeSpan.FromMilliseconds(500));
        Assert.False(runner.At(0).Killed);
        await Advance(TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(500));
        await Until(()=>e.Status==EngineStatus.Restarting);
        Assert.True(runner.At(0).Killed);
        await start.WaitAsync(RealTimeCap);
        await Advance(TimeSpan.FromMilliseconds(3500),TimeSpan.FromMilliseconds(100));
        Assert.Equal(1,runner.Count);
        await Advance(TimeSpan.FromMilliseconds(600),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==2);
    }

    [Fact]
    public async Task Unexpected_exit_restarts_after_four_seconds_then_eight()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        runner.At(0).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await Advance(TimeSpan.FromMilliseconds(3900),TimeSpan.FromMilliseconds(100));
        Assert.Equal(1,runner.Count);
        await Advance(TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==2&&e.Status==EngineStatus.Running);
        runner.At(1).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await Advance(TimeSpan.FromMilliseconds(7900),TimeSpan.FromMilliseconds(100));
        Assert.Equal(2,runner.Count);
        await Advance(TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==3);
    }

    [Fact]
    public async Task Five_exits_in_ten_minutes_fail_with_reason()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        for(int i=0;i<4;i++)
        {
            await Until(()=>runner.Count==i+1&&e.Status==EngineStatus.Running);
            runner.At(i).Exit();
            await Until(()=>e.Status==EngineStatus.Restarting);
            await Advance(TimeSpan.FromSeconds(61),TimeSpan.FromSeconds(1));
        }
        await Until(()=>runner.Count==5&&e.Status==EngineStatus.Running);
        runner.At(4).Exit();
        await Until(()=>e.Status==EngineStatus.Failed);
        Assert.Equal("OpenRGB stopped 5 times in 10 minutes. Turn lighting off and on in Settings to try again.",e.FailureReason);
        Assert.Equal(5,runner.Count);
    }

    [Fact]
    public async Task Stop_kills_tree_and_does_not_restart()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        await e.StopAsync().WaitAsync(RealTimeCap);
        Assert.True(runner.At(0).Killed);
        Assert.Equal(EngineStatus.Stopped,e.Status);
        await Advance(TimeSpan.FromSeconds(30),TimeSpan.FromSeconds(1));
        Assert.Equal(1,runner.Count);
    }

    [Fact]
    public async Task Concurrent_starts_spawn_exactly_one_process()
    {
        var e=Engine();
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>e.StartAsync(CancellationToken.None)))).WaitAsync(RealTimeCap);
        await Task.Delay(50);
        Assert.Equal(1,runner.Count);
    }

    // Ruling AM: an elevated host never runs an OpenRGB.exe whose folder programs without administrator rights can change. The refusal is
    // final for this engine (no relaunch, not counted as a crash) and gives the plain remedy.
    [Fact]
    public async Task Refused_openrgb_folder_fails_without_starting_or_relaunching()
    {
        string? checkedPath=null;
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,
            folderCheck:path=>{checkedPath=path;return new DeadWare.UpdateCore.LocationProtection(false,@"C:\Users\Public can be changed by S-1-5-32-545.");}));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal(OpenRgbEngine.UnprotectedFolderText,e.FailureReason);
        Assert.Equal(exe,checkedPath);
        await Advance(TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(1));
        Assert.Equal(0,runner.Count);
        Assert.Equal(EngineStatus.Failed,e.Status);
    }

    // Polish item 8 (B12): a folder the check couldn't judge (here a link) isn't called changeable: the refusal says it can't be checked,
    // gives the reason and keeps the remedy.
    [Fact]
    public async Task Unjudged_openrgb_folder_is_refused_with_the_cant_check_text()
    {
        const string reason=@"C:\Tools\OpenRGB is a link or junction.";
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,folderCheck:_=>new DeadWare.UpdateCore.LocationProtection(false,reason)));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal(@"DeadWare can't check the OpenRGB folder, so it won't run OpenRGB as administrator: C:\Tools\OpenRGB is a link or junction. Copy OpenRGB to C:\Program Files\OpenRGB and choose it in Settings → Lighting.",e.FailureReason);
        Assert.Equal(0,runner.Count);
    }

    // A check that throws couldn't judge the folder either.
    [Fact]
    public async Task Failing_folder_check_is_refused_with_the_cant_check_text()
    {
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,folderCheck:_=>throw new UnauthorizedAccessException("Access is denied.")));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.StartsWith("DeadWare can't check the OpenRGB folder, so it won't run OpenRGB as administrator: Access is denied.",e.FailureReason);
        Assert.Equal(0,runner.Count);
    }

    // The non-elevated path: no check at all, or a folder that passes it, starts OpenRGB as before.
    [Fact]
    public async Task Protected_openrgb_folder_starts_as_before()
    {
        var checks=0;
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,folderCheck:_=>{checks++;return new DeadWare.UpdateCore.LocationProtection(true);}));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Running,e.Status);
        Assert.Equal(1,runner.Count);
        Assert.Equal(1,checks);
    }

    // Ruling AR: OpenRGB's settings are prepared before every start, the first and each relaunch.
    [Fact]
    public async Task Config_is_prepared_before_every_start()
    {
        var prepared=new List<int>();
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,prepareConfig:d=>{Assert.Equal(@"C:\cfg\openrgb",d);lock(prepared)prepared.Add(runner.Count);}));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        runner.At(0).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await Advance(TimeSpan.FromSeconds(4),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==2&&e.Status==EngineStatus.Running);
        lock(prepared)Assert.Equal([0,1],prepared); // each time before the process started
    }

    // A settings file that can't be written means OpenRGB isn't started: a plain, final reason, not five crash restarts.
    [Fact]
    public async Task Config_that_cant_be_written_fails_without_starting()
    {
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,prepareConfig:_=>throw new UnauthorizedAccessException("denied")));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal(OpenRgbEngine.ConfigFailedText(@"C:\cfg\openrgb"),e.FailureReason);
        await Advance(TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(1));
        Assert.Equal(0,runner.Count);
    }

    // Task 7: a Stop that lands in the relaunch delay (as the host's conflict stop can) cancels the relaunch: OpenRGB never starts beside
    // the program that caused the stop.
    [Fact]
    public async Task Stop_during_the_restart_delay_cancels_the_relaunch()
    {
        var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        runner.At(0).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await e.StopAsync().WaitAsync(RealTimeCap);
        await Advance(TimeSpan.FromSeconds(40),TimeSpan.FromSeconds(1));
        Assert.Equal(1,runner.Count);
        Assert.Equal(EngineStatus.Stopped,e.Status);
    }

    // B5: a Stop that lands while the engine checks OpenRGB's folder or writes its settings, the work just before a launch, starts nothing:
    // the engine looks at the stop again after them, so OpenRGB never runs, even for a moment, beside the program that caused the stop.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_during_the_checks_before_a_launch_starts_nothing(bool duringFolderCheck)
    {
        using var inside=new SemaphoreSlim(0);
        using var release=new ManualResetEventSlim(false);
        void Park(){inside.Release();release.Wait(RealTimeCap);}
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,
            folderCheck:_=>{if(duringFolderCheck) Park();return new DeadWare.UpdateCore.LocationProtection(true);},
            prepareConfig:_=>{if(!duringFolderCheck) Park();}));
        var start=e.StartAsync(CancellationToken.None);
        Assert.True(await inside.WaitAsync(RealTimeCap));
        var stop=e.StopAsync(); // marks the stop at once, then waits for the supervisor still inside the check
        release.Set();
        await stop.WaitAsync(RealTimeCap);
        await start.WaitAsync(RealTimeCap);
        Assert.Equal(0,runner.Count);
        Assert.Equal(EngineStatus.Stopped,e.Status);
    }

    [Fact]
    public async Task Stop_during_the_port_wait_kills_it_and_relaunches_nothing()
    {
        portUp=false;
        var e=Engine();
        var start=e.StartAsync(CancellationToken.None);
        await Until(()=>Volatile.Read(ref probes)>=1);
        await e.StopAsync().WaitAsync(RealTimeCap);
        await start.WaitAsync(RealTimeCap);
        Assert.True(runner.At(0).Killed);
        Assert.Equal(EngineStatus.Stopped,e.Status);
        await Advance(TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(1));
        Assert.Equal(1,runner.Count);
    }

    // B11: an OpenRGB that exits while its port is awaited is noticed when the probe interval ends, before any further probe, so it is
    // never reported Running; the relaunch waits exactly the 4 s backoff, armed before Restarting is reported. The clock knows its timers,
    // so each step moves it only once the engine waits on the timer meant.
    [Fact]
    public async Task Exit_during_the_port_wait_restarts_after_the_backoff()
    {
        var clock=new Clock();
        var up=false;var probed=0;
        var seen=new List<EngineStatus>();
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>{Interlocked.Increment(ref probed);return Task.FromResult(Volatile.Read(ref up));},clock,log));
        e.Changed+=()=>{lock(seen)seen.Add(e.Status);};
        var start=e.StartAsync(CancellationToken.None);
        await Until(()=>clock.Waiting(OpenRgbEngine.PortProbe)); // the first probe found the port shut; the next waits a probe interval
        runner.At(0).Exit();
        Volatile.Write(ref up,true); // a probe of the exited process would now call it running
        var probes=Volatile.Read(ref probed);
        clock.Advance(OpenRgbEngine.PortProbe);
        await Until(()=>e.Status==EngineStatus.Restarting);
        await start.WaitAsync(RealTimeCap); // the first attempt resolved as a restart
        Assert.Equal(probes,Volatile.Read(ref probed)); // the exited process was not probed again
        lock(seen)Assert.DoesNotContain(EngineStatus.Running,seen);
        Assert.True(clock.Waiting(RestartBackoff.Base)); // the relaunch waits on a timer of exactly the backoff
        clock.Advance(RestartBackoff.Base-TimeSpan.FromTicks(1));
        await Task.Delay(100);
        Assert.Equal(1,runner.Count);
        clock.Advance(TimeSpan.FromTicks(1));
        await Until(()=>runner.Count==2&&e.Status==EngineStatus.Running);
    }

    // B11: a second Start while one runs starts no second supervisor. One would be queued to the thread pool, so the check waits a settle
    // before it counts launches and settings writes (a second supervisor writes the settings before it starts anything).
    [Fact]
    public async Task Second_start_while_running_starts_nothing_more()
    {
        var prepared=0;
        var e=Track(new OpenRgbEngine(exe,@"C:\cfg\openrgb",runner,(_,_)=>Task.FromResult(true),time,log,prepareConfig:_=>Interlocked.Increment(ref prepared)));
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        await Task.Delay(100);
        Assert.Equal(1,Volatile.Read(ref prepared));
        Assert.Equal(1,runner.Count);
        Assert.Equal(EngineStatus.Running,e.Status);
    }

    [Fact]
    public async Task Throwing_changed_handler_does_not_stop_restarts()
    {
        var e=Engine();
        e.Changed+=()=>throw new InvalidOperationException("pipe broke");
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        runner.At(0).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await Advance(TimeSpan.FromSeconds(4),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==2&&e.Status==EngineStatus.Running);
    }
}
