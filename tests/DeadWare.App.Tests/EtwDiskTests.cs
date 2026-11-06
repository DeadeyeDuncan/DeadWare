using System.Diagnostics;
using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// A disk trace that only counts its lifecycle. <paramref name="startGate"/> (or <paramref name="startDelay"/>) holds
/// <see cref="Start"/> and <paramref name="stopGate"/> holds <see cref="StopAsync"/>, so tests can queue requests behind them.
/// The same instance serves every factory call, so starts and stops add up across restarts.
/// </summary>
internal sealed class FakeTrace(TimeSpan startDelay=default,ManualResetEventSlim? startGate=null,ManualResetEventSlim? stopGate=null) : IDiskTrace
{
    private readonly object gate=new();
    private readonly List<string> log=[];
    private readonly TaskCompletionSource entered=new(TaskCreationOptions.RunContinuationsAsynchronously),stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int starts,stops,factories;
    private bool running;
    private DiskTake result=new(DiskTraceState.Running,"",false,false,[],0,0);
    public int Starts {get{lock(gate)return starts;}}
    public int Stops {get{lock(gate)return stops;}}
    public int Factories {get{lock(gate)return factories;}}
    public bool Running {get{lock(gate)return running;}}
    public IReadOnlyList<string> Log {get{lock(gate)return log.ToArray();}}
    /// <summary>Completes when the first <see cref="Start"/> begins.</summary>
    public Task Entered=>entered.Task;
    /// <summary>Completes with the first stop.</summary>
    public Task Stopped=>stopped.Task;
    /// <summary>What every <see cref="Take"/> returns.</summary>
    public DiskTake Result {get{lock(gate)return result;}set{lock(gate)result=value;}}
    public IDiskTrace Create() {lock(gate)factories++;return this;}
    public void Start()
    {
        entered.TrySetResult();
        if(startGate is not null)Assert.True(startGate.Wait(TimeSpan.FromSeconds(10)));
        else if(startDelay>TimeSpan.Zero)Thread.Sleep(startDelay);
        lock(gate){starts++;running=true;log.Add("start");}
    }
    public Task StopAsync()
    {
        if(stopGate is not null)Assert.True(stopGate.Wait(TimeSpan.FromSeconds(10)));
        lock(gate){stops++;running=false;log.Add("stop");}
        stopped.TrySetResult();
        return Task.CompletedTask;
    }
    public DiskTake Take(double now,IReadOnlyDictionary<int,ProcessIdentity> index)=>Result;
    public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
}

internal static class DiskTraceTesting
{
    /// <summary>Awaits the controller's serialized lifecycle work until no further step was queued behind it.</summary>
    public static async Task Drain(DiskTraceController controller)
    {
        for(int i=0;i<100;i++)
        {
            var idle=controller.Idle;
            await idle.WaitAsync(TimeSpan.FromSeconds(10));
            if(ReferenceEquals(idle,controller.Idle))return;
        }
        throw new TimeoutException("The disk lifecycle kept queuing work.");
    }
    public static Task Drain(Collector collector)=>Drain((DiskTraceController)typeof(Collector).GetField("disk",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(collector)!);
}

[Collection(TestHostThreadPool.TimingCollection)]
public class EtwDiskTests
{
    private static readonly IReadOnlyDictionary<int,ProcessIdentity> Index=new Dictionary<int,ProcessIdentity>();
    private static EtwDisk Disk(Func<int,ProcessIdentity?>? resolve=null,Func<long>? readLoss=null,Action? stopNative=null)=>new(resolve??(pid=>new(pid,10,"a.exe")),readLoss,stopNative);
    private static DiskTake Take(EtwDisk d,double at)=>d.Take(at,Index);
    private static DiskTake RunningTake(params DiskProcessRate[] rates)=>new(DiskTraceState.Running,"",false,true,rates,0,0);

    [Fact] public void Buffer_size_starts_at_the_TraceEvent_default()
    {
        Assert.Equal(Math.Max(64,2*Environment.ProcessorCount),EtwDisk.DefaultBufferSizeMB);
        Assert.Equal(EtwDisk.DefaultBufferSizeMB*ProcessFeatures.DiskBufferMultiplier,EtwDisk.BufferSizeMB);
    }
    [Fact] public void Reads_and_writes_reach_the_disk_ledger()
    {
        var d=Disk(resolve:pid=>new(pid,10,"a.exe"));
        Assert.False(Take(d,at:0).Warm);
        d.RecordIo(8,20,4096,write:true);d.RecordIo(8,21,512,write:false);
        var t=Take(d,at:2.5);
        Assert.Equal(DiskTraceState.Running,t.State);Assert.True(t.Warm);Assert.False(t.Lossy);
        var rate=Assert.Single(t.Rates,r=>r.Pid==8);
        Assert.Equal(10,rate.Generation);Assert.Equal(512/2.5,rate.Read,6);Assert.Equal(4096/2.5,rate.Write,6);
    }
    [Fact] public void Event_before_the_generation_is_not_credited()
    {
        var d=Disk();Take(d,at:0);
        d.RecordIo(8,5,4096,true);
        var t=Take(d,at:2);
        Assert.True(t.Warm);Assert.DoesNotContain(t.Rates,r=>r.Pid==8&&r.Write>0);Assert.Equal(0,t.UnattributedEvents);
    }
    [Fact] public void Unattributed_events_are_counted()
    {
        var d=Disk();Take(d,at:0);
        d.RecordIo(-1,20,4096,true);d.RecordIo(8,20,4096,true);
        var t=Take(d,at:2);
        Assert.Equal(1,t.UnattributedEvents);Assert.Equal(0.5,t.UnattributedShare,6);
        Assert.DoesNotContain(t.Rates,r=>r.Pid==-1);
    }
    [Fact] public void Lossy_take_resets_totals_and_warm_up()
    {
        long loss=0;var d=Disk(readLoss:()=>loss);
        Take(d,at:0);d.RecordIo(8,20,4096,true);
        loss=5;var t=d.Take(3,Index);
        Assert.True(t.Lossy);Assert.Equal("Windows dropped disk events",t.Status);Assert.Empty(t.Rates);Assert.False(t.Warm);
        Assert.False(d.Take(4,Index).Warm);
        // Two seconds after the reset the window is warm again, and the bytes recorded before the loss are gone.
        var after=d.Take(5.5,Index);
        Assert.False(after.Lossy);Assert.True(after.Warm);Assert.DoesNotContain(after.Rates,r=>r.Pid==8);
    }
    [Fact] public async Task Ten_lossy_takes_pause_for_five_minutes()
    {
        Assert.Equal(10,EtwDisk.LossStormTakes);Assert.Equal(TimeSpan.FromMinutes(5),EtwDisk.StormPause);
        long loss=0;var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var d=Disk(readLoss:()=>loss,stopNative:()=>stopped.TrySetResult());
        for(int i=0;i<9;i++){loss+=1;Assert.True(d.Take(i,Index).Lossy);}
        Assert.False(stopped.Task.IsCompleted);
        loss+=1;Assert.Equal(DiskTraceState.Paused,d.Take(9,Index).State);
        var t=d.Take(11,Index);
        Assert.Equal(DiskTraceState.Paused,t.State);Assert.Equal("Disk tracing paused for 5 minutes after Windows dropped events repeatedly",t.Status);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [Fact] public void A_clean_take_restarts_the_loss_storm_count()
    {
        long loss=0;var d=Disk(readLoss:()=>loss);
        for(int i=0;i<9;i++){loss+=1;d.Take(i,Index);}
        Assert.False(d.Take(9,Index).Lossy);
        for(int i=10;i<19;i++){loss+=1;Assert.Equal(DiskTraceState.Running,d.Take(i,Index).State);}
    }
    [Theory][InlineData("DeadWare-Disk-123-abc",123)][InlineData("DeadWare-456-def",456)][InlineData("Other-1-x",null)][InlineData("DeadWare-Disk-x-y",null)]
    public void Orphan_session_names_parse_their_pid(string name,int? pid)=>Assert.Equal(pid,EtwDisk.OrphanPid(name));
    [Fact] public void Only_sessions_of_exited_deadware_are_stopped()
    {
        var stopped=new List<string>();
        EtwDisk.RemoveOrphans(["DeadWare-Disk-1-a","DeadWare-2-b","NT Kernel Logger"],pid=>pid==2,stopped.Add);
        Assert.Equal(["DeadWare-Disk-1-a"],stopped);
    }
    [Fact] public async Task Queued_start_is_dropped_after_stop()
    {
        using var stopGate=new ManualResetEventSlim();
        var fake=new FakeTrace(stopGate:stopGate);var c=new DiskTraceController(fake.Create);
        c.Request(true);await DiskTraceTesting.Drain(c);Assert.True(fake.Running);
        // The stop of the first trace is held, so the second start waits in the queue when disposal closes it.
        c.Request(false);c.Request(true);
        var stop=c.StopAsync();stopGate.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));await DiskTraceTesting.Drain(c);
        Assert.Equal(1,fake.Starts);Assert.Equal(1,fake.Stops);Assert.False(fake.Running);Assert.Equal(1,fake.Factories);
    }
    [Fact] public async Task Running_start_finishes_then_stops()
    {
        using var startGate=new ManualResetEventSlim();
        var fake=new FakeTrace(startGate:startGate);var c=new DiskTraceController(fake.Create);
        c.Request(true);await fake.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        var stop=c.StopAsync();
        Assert.False(stop.IsCompleted);
        startGate.Set();await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["start","stop"],fake.Log);Assert.False(fake.Running);
        c.Request(true);await DiskTraceTesting.Drain(c);
        Assert.Equal(1,fake.Starts);Assert.Equal(DiskTake.Off,c.Take(1,Index));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Rapid_toggles_leave_the_trace_matching_the_last_request(bool last)
    {
        var fake=new FakeTrace(startDelay:TimeSpan.FromMilliseconds(50));var c=new DiskTraceController(fake.Create);
        for(int i=0;i<10;i++){c.Request(true);c.Request(false);}
        if(last)c.Request(true);
        await DiskTraceTesting.Drain(c);
        Assert.Equal(last,fake.Running);Assert.Equal(fake.Starts-(last?1:0),fake.Stops);
        if(last)Assert.Same(fake.Result,c.Take(1,Index));else Assert.Equal(DiskTake.Off,c.Take(1,Index));
        await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fake.Running);Assert.Equal(fake.Starts,fake.Stops);
    }
    [Fact] public async Task Stop_queued_behind_a_slow_start_finishes_within_three_seconds()
    {
        var fake=new FakeTrace(startDelay:TimeSpan.FromMilliseconds(200));var c=new DiskTraceController(fake.Create);
        c.Request(true);await fake.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        var sw=Stopwatch.StartNew();await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(sw.Elapsed<TimeSpan.FromSeconds(3));Assert.Equal(1,fake.Starts);Assert.Equal(1,fake.Stops);Assert.False(fake.Running);
    }
    [Fact] public async Task Take_names_a_reason_while_starting_and_when_off()
    {
        using var startGate=new ManualResetEventSlim();
        var fake=new FakeTrace(startGate:startGate);var c=new DiskTraceController(fake.Create);
        Assert.Equal(DiskTake.Off,c.Take(0,Index));Assert.NotEqual("",DiskTake.Off.Status);
        c.Request(true);await fake.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        var starting=c.Take(1,Index);
        Assert.Equal(DiskTraceState.Starting,starting.State);Assert.Equal("Starting disk tracing…",starting.Status);
        startGate.Set();await DiskTraceTesting.Drain(c);
        Assert.Same(fake.Result,c.Take(2,Index));Assert.NotNull(c.LastOpenMilliseconds);
        c.Request(false);await DiskTraceTesting.Drain(c);
        Assert.Equal(DiskTake.Off,c.Take(3,Index));
    }
    [Fact] public async Task Factory_failure_names_the_exception_type()
    {
        var c=new DiskTraceController(()=>throw new InvalidOperationException());
        c.Request(true);await DiskTraceTesting.Drain(c);
        var t=c.Take(0,Index);
        Assert.Equal(DiskTraceState.Failed,t.State);Assert.Equal("Disk tracing unavailable (InvalidOperationException)",t.Status);
    }
    [Fact] public async Task Paused_trace_is_stopped_and_restarted_after_five_minutes()
    {
        var fake=new FakeTrace();var c=new DiskTraceController(fake.Create);
        c.Request(true);await DiskTraceTesting.Drain(c);
        var paused=new DiskTake(DiskTraceState.Paused,"Disk tracing paused for 5 minutes after Windows dropped events repeatedly",false,false,[],0,0);
        fake.Result=paused;
        Assert.Equal(DiskTraceState.Paused,c.Take(100,Index).State);
        fake.Result=RunningTake();
        await DiskTraceTesting.Drain(c);
        Assert.Equal(1,fake.Stops);Assert.False(fake.Running);
        var during=c.Take(399,Index);
        Assert.Equal(DiskTraceState.Paused,during.State);Assert.Equal(paused.Status,during.Status);
        await DiskTraceTesting.Drain(c);Assert.Equal(1,fake.Starts);
        Assert.Equal(DiskTraceState.Starting,c.Take(400,Index).State);
        await DiskTraceTesting.Drain(c);
        Assert.Equal(2,fake.Starts);Assert.True(fake.Running);Assert.Same(fake.Result,c.Take(401,Index));
        await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.False(fake.Running);
    }

    // Collector wiring. These collectors never open a kernel session: the trace is always the fake.
    private static Collector Build(FakeTrace fake,CollectorOptions options)=>
        new(options with {HardwareEnabled=false,EtwEnabled=false,PollingIntervalSeconds=0.5},null,processes:new FakeProcesses(),diskTrace:fake.Create);
    /// <summary>Starts the collector if needed and returns the <paramref name="count"/>th reading published after the call.</summary>
    private static async Task<Reading> Readings(Collector c,int count)
    {
        int seen=0;var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Reading r) {if(Interlocked.Increment(ref seen)==count)tcs.TrySetResult(r);}
        c.SampleAvailable+=Handler;
        try {c.Start();return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));}
        finally {c.SampleAvailable-=Handler;}
    }
    [Fact] public async Task Monitoring_on_starts_disk_only_when_ProcessDiskTracing_is_set()
    {
        var fake=new FakeTrace();
        await using(var off=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:false)))
        {
            var reading=await Readings(off,3);
            Assert.Equal(DiskTake.Off.Status,reading.ProcessActivity.Disk.Status);Assert.False(reading.ProcessActivity.Disk.Available);
        }
        Assert.Equal(0,fake.Starts);Assert.Equal(0,fake.Factories);
        var gated=new FakeTrace();await using var c=Build(gated,new(ProcessMonitoring:false,ProcessDiskTracing:true));
        await Readings(c,2);await DiskTraceTesting.Drain(c);Assert.Equal(0,gated.Starts);
        c.SetProcessMonitoring(true);await Readings(c,2);await DiskTraceTesting.Drain(c);
        Assert.Equal(1,gated.Starts);Assert.True(gated.Running);Assert.Equal(1,gated.Factories);
    }
    [Fact] public async Task Monitoring_off_stops_the_disk_trace()
    {
        var fake=new FakeTrace();await using var c=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:true));
        await Readings(c,2);await DiskTraceTesting.Drain(c);Assert.True(fake.Running);
        c.SetProcessMonitoring(false);await Readings(c,2);await DiskTraceTesting.Drain(c);
        Assert.Equal(1,fake.Stops);Assert.False(fake.Running);
    }
    [Fact] public async Task Rapid_monitoring_toggles_leave_the_disk_trace_matching_the_last_setting()
    {
        var fake=new FakeTrace(startDelay:TimeSpan.FromMilliseconds(200));await using var c=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:true));
        await Readings(c,1);
        for(int i=0;i<4;i++){c.SetProcessMonitoring(false);await Readings(c,1);c.SetProcessMonitoring(true);await Readings(c,1);}
        c.SetProcessMonitoring(false);await Readings(c,2);await DiskTraceTesting.Drain(c);
        Assert.False(fake.Running);Assert.Equal(fake.Starts,fake.Stops);
        c.SetProcessMonitoring(true);await Readings(c,2);await DiskTraceTesting.Drain(c);
        Assert.True(fake.Running);Assert.Equal(fake.Stops+1,fake.Starts);
        await c.DisposeAsync();
        Assert.False(fake.Running);Assert.Equal(fake.Starts,fake.Stops);
    }
    [Fact] public async Task Disk_take_joins_into_the_published_activity_with_its_timings()
    {
        // FakeProcesses publishes alpha (PID 100) and beta (PID 200), both Generation 1.
        var fake=new FakeTrace {Result=new(DiskTraceState.Running,"",false,true,[new(100,1,512,2048)],0,3)};
        await using var c=Build(fake,new(ProcessMonitoring:true,ProcessDiskTracing:true,DiagnosticTimings:true));
        await Readings(c,1);await DiskTraceTesting.Drain(c);
        var r=await Readings(c,2);
        Assert.True(r.ProcessActivity.Disk.Available);Assert.Equal(0,r.ProcessActivity.DiskMisses);Assert.Equal(3,r.ProcessActivity.UnattributedDiskEvents);
        var alpha=Assert.Single(r.ProcessActivity.Processes,p=>p.Pid==100);Assert.Equal(512,alpha.DiskReadPerSecond);Assert.Equal(2048,alpha.DiskWritePerSecond);
        var beta=Assert.Single(r.ProcessActivity.Processes,p=>p.Pid==200);Assert.Equal(0,beta.DiskReadPerSecond);Assert.Equal(0,beta.DiskWritePerSecond);
        Assert.True(r.ProviderMilliseconds!.ContainsKey("etw/disk"));Assert.True(r.ProviderMilliseconds.ContainsKey("open/etwdisk"));
        await using var off=Build(new FakeTrace(),new(ProcessMonitoring:true,DiagnosticTimings:true));
        var plain=await Readings(off,2);
        Assert.False(plain.ProviderMilliseconds!.ContainsKey("etw/disk"));Assert.False(plain.ProviderMilliseconds.ContainsKey("open/etwdisk"));
    }
}
