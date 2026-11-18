using System.Diagnostics;
using DeadWare.Devices;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

public sealed class OpenRgbEngineTests : IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-eng-"+Guid.NewGuid().ToString("N"));
    readonly string exe;
    readonly FakeTimeProvider time=new();
    readonly FakeRunner runner=new();
    readonly HostLog log;
    bool portUp=true;
    int probes;
    public OpenRgbEngineTests(){Directory.CreateDirectory(dir);exe=Path.Combine(dir,"OpenRGB.exe");File.WriteAllText(exe,"");log=new HostLog(Path.Combine(dir,"logs"));}
    public void Dispose(){log.Dispose();try{Directory.Delete(dir,true);}catch{}}

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

    OpenRgbEngine Engine()=>new(exe,@"C:\cfg",runner,(_,_)=>{Interlocked.Increment(ref probes);return Task.FromResult(portUp);},time,log);
    async Task Advance(TimeSpan total,TimeSpan step)
    {
        for(var t=TimeSpan.Zero;t<total;t+=step){time.Advance(step);await Task.Delay(5);}
    }

    [Fact]
    public void Arguments_match_spec()=>Assert.Equal(["--server","--server-port","6744","--noautoconnect","--config",@"C:\cfg"],OpenRgbEngine.Arguments(@"C:\cfg"));

    [Fact]
    public async Task Missing_exe_fails_without_starting()
    {
        var missing=Path.Combine(dir,"nope","OpenRGB.exe");
        await using var e=new OpenRgbEngine(missing,@"C:\cfg",runner,(_,_)=>Task.FromResult(true),time,log);
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Failed,e.Status);
        Assert.Equal($"OpenRGB.exe was not found at {missing}.",e.FailureReason);
        Assert.Equal(0,runner.Count);
    }

    [Fact]
    public async Task Start_runs_with_spec_arguments_and_reports_running()
    {
        await using var e=Engine();
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        Assert.Equal(EngineStatus.Running,e.Status);
        Assert.Equal(100,e.ProcessId);
        Assert.Equal(OpenRgbEngine.Arguments(@"C:\cfg"),runner.Info!.ArgumentList.ToArray());
    }

    [Fact]
    public async Task Port_never_opening_within_thirty_seconds_kills_and_backs_off()
    {
        portUp=false;
        await using var e=Engine();
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
        await using var e=Engine();
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
        await using var e=Engine();
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
        Assert.Equal("OpenRGB stopped 5 times in 10 minutes.",e.FailureReason);
        Assert.Equal(5,runner.Count);
    }

    [Fact]
    public async Task Stop_kills_tree_and_does_not_restart()
    {
        await using var e=Engine();
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
        await using var e=Engine();
        await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>e.StartAsync(CancellationToken.None)))).WaitAsync(RealTimeCap);
        await Task.Delay(50);
        Assert.Equal(1,runner.Count);
    }

    [Fact]
    public async Task Throwing_changed_handler_does_not_stop_restarts()
    {
        await using var e=Engine();
        e.Changed+=()=>throw new InvalidOperationException("pipe broke");
        await e.StartAsync(CancellationToken.None).WaitAsync(RealTimeCap);
        runner.At(0).Exit();
        await Until(()=>e.Status==EngineStatus.Restarting);
        await Advance(TimeSpan.FromSeconds(4),TimeSpan.FromMilliseconds(100));
        await Until(()=>runner.Count==2&&e.Status==EngineStatus.Running);
    }
}
