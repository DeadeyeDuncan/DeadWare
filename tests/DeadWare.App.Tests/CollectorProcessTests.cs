using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Scripted scans of two processes whose CPU time and I/O rise on every call, so any two scans in a row yield rates.</summary>
internal sealed class FakeProcesses(int processorCount=8) : IProcessSource
{
    private int calls;
    public int Calls=>Volatile.Read(ref calls);
    public int ProcessorCount=>processorCount;
    public string Status=>"";
    public string Failure=>"";
    public ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds)
    {
        long n=Interlocked.Increment(ref calls);
        ProcessScanEntry[] entries=
        [
            new(100,1,"alpha.exe",1,4,10_000_000,n*500_000,n*500_000,n*4096,n*1024),
            new(200,1,"beta.exe",1,2,20_000_000,n*100_000,0,0,0),
        ];
        return new ProcessScan(DateTimeOffset.UtcNow,monotonicSeconds,pollingIntervalSeconds,entries,6,WindowsProcesses.InitialBufferBytes);
    }
}
/// <summary>A process source whose every scan throws <see cref="InvalidOperationException"/>.</summary>
internal sealed class ThrowingProcesses : IProcessSource
{
    public int ProcessorCount=>8;
    public string Status=>"";
    public string Failure=>"";
    public ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds)=>throw new InvalidOperationException();
}
/// <summary>Storage whose drive enumeration throws, so every collector pass lands on its failed path.</summary>
internal sealed class ThrowingStorage : IStorageFastSource
{
    public IReadOnlyList<StorageDrive> Drives(double now)=>throw new NotSupportedException();
    public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)=>error=null;
}

[Collection(TestHostThreadPool.TimingCollection)]
public class CollectorProcessTests : IAsyncLifetime
{
    private sealed class ProcessorCountThrows : IProcessSource
    {
        public int ProcessorCount=>throw new InvalidCastException();
        public string Status=>"";
        public string Failure=>"";
        public ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds)=>throw new InvalidOperationException();
    }
    /// <summary>A native failure: every scan returns null with a fixed failure and a status the test paces.</summary>
    private sealed class FailingProcesses(string status) : IProcessSource
    {
        public const string Text="Process scan unavailable (NTSTATUS 0xC0000001)";
        public int ProcessorCount=>8;
        public string Status=>status;
        public string Failure=>Text;
        public ProcessScan? Scan(double monotonicSeconds,double pollingIntervalSeconds)=>null;
    }

    private readonly List<Collector> started=[];
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync() {foreach(var c in started)await c.DisposeAsync();}

    private static CollectorOptions Isolated(CollectorOptions options)=>options with {HardwareEnabled=false,EtwEnabled=false,PollingIntervalSeconds=0.5};
    private Collector Build(CollectorOptions options,IProcessSource source,IStorageFastSource? storage=null)
    {
        var c=new Collector(Isolated(options),storage,processes:source);started.Add(c);return c;
    }
    private (Collector Collector,FakeProcesses Fake) Start(CollectorOptions options)
    {
        var fake=new FakeProcesses();var c=Build(options,fake);c.Start();return (c,fake);
    }
    /// <summary>The first <paramref name="count"/> readings a collector publishes, subscribed before it starts.</summary>
    private async Task<IReadOnlyList<Reading>> Readings(Collector c,int count)
    {
        var list=new List<Reading>();
        var tcs=new TaskCompletionSource<IReadOnlyList<Reading>>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.SampleAvailable+=r=>{lock(list){if(list.Count<count){list.Add(r);if(list.Count==count)tcs.TrySetResult(list.ToArray());}}};
        c.Start();
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private async Task<(Reading Reading,FakeProcesses Fake)> Nth(CollectorOptions options,int n)
    {
        var fake=new FakeProcesses();var readings=await Readings(Build(options,fake),n);return (readings[n-1],fake);
    }
    private Task<(Reading Reading,FakeProcesses Fake)> Second(CollectorOptions options)=>Nth(options,2);
    private Task<(Reading Reading,FakeProcesses Fake)> Third(CollectorOptions options)=>Nth(options,3);
    private async Task<Reading> FirstWith(IProcessSource source,CollectorOptions? options=null)
    {
        options??=new(ProcessMonitoring:true);
        return (await Readings(Build(options,source),1))[0];
    }
    private async Task<Reading> FirstWithThrowingStorage(CollectorOptions options)=>(await Readings(Build(options,new FakeProcesses(),new ThrowingStorage()),1))[0];
    private static async Task<Reading> NextWhere(Collector c,Func<Reading,bool> match)
    {
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Reading r) {if(match(r))tcs.TrySetResult(r);}
        c.SampleAvailable+=Handler;
        try
        {
            if(c.Latest is {} latest&&match(latest))tcs.TrySetResult(latest);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally {c.SampleAvailable-=Handler;}
    }
    private static Task<Reading> NextReadingAfter(Collector c,Reading previous)=>NextWhere(c,r=>r.Sample.MonotonicSeconds>previous.Sample.MonotonicSeconds);

    [Fact] public async Task Constructor_seeds_the_switch_and_it_changes_without_starting()
    {
        await using var on=new Collector(Isolated(new(ProcessMonitoring:true)),null,processes:new FakeProcesses());
        Assert.True(on.ProcessMonitoring);
        on.SetProcessMonitoring(false);Assert.False(on.ProcessMonitoring);
        await using var off=new Collector(Isolated(new()),null,processes:new FakeProcesses());
        Assert.False(off.ProcessMonitoring);
        off.SetProcessMonitoring(true);Assert.True(off.ProcessMonitoring);
        Assert.Equal("Not started",off.Stage);
    }
    [Fact] public async Task Monitoring_is_off_by_default_and_never_scans()
    {
        var (r,fake)=await Third(new());
        Assert.Equal(ProcessActivityKind.Off,r.ProcessActivity.Kind);Assert.Equal(0,fake.Calls);
    }
    [Fact] public async Task Fake_scan_publishes_activity_and_keeps_device_metrics()
    {
        var (r,_)=await Second(new(ProcessMonitoring:true));
        Assert.Equal(ProcessActivityKind.Available,r.ProcessActivity.Kind);Assert.NotEmpty(r.ProcessActivity.Processes);
        Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));
        Assert.Equal(8,r.ProcessActivity.ProcessorCount);Assert.Equal(0.5,r.ProcessActivity.IntervalSeconds);Assert.Equal(2,r.ProcessActivity.TotalProcesses);
        // The second scan rates the first: alpha's CPU time rose 1,000,000 ticks (0.1 s) over about 0.5 s on 8 processors.
        var alpha=Assert.Single(r.ProcessActivity.Processes,p=>p.Pid==100);
        Assert.Equal(ProcessGap.None,alpha.RateGap);Assert.NotNull(alpha.CpuPercent);Assert.InRange(alpha.CpuPercent!.Value,0.1,10);
        Assert.NotNull(r.ProcessActivity.DeltaSeconds);
        // ETW is off here, so the network join names the capture's status instead of publishing silent zeros.
        Assert.False(r.ProcessActivity.Network.Available);Assert.Equal(r.EtwStatus,r.ProcessActivity.Network.Status);Assert.NotEqual("",r.ProcessActivity.Network.Status);
        Assert.All(r.ProcessActivity.Processes,p=>{Assert.Null(p.DownloadPerSecond);Assert.Null(p.UploadPerSecond);});
        Assert.False(r.ProcessActivity.Disk.Available);Assert.NotEqual("",r.ProcessActivity.Disk.Status);
    }
    [Fact] public async Task Scan_timings_appear_only_when_the_scan_runs()
    {
        var (on,_)=await Second(new(ProcessMonitoring:true,DiagnosticTimings:true));
        Assert.True(on.ProviderMilliseconds!.ContainsKey("windows/processes"));Assert.True(on.ProviderMilliseconds.ContainsKey("processes/join"));
        var (off,_)=await Second(new(DiagnosticTimings:true));
        Assert.False(off.ProviderMilliseconds!.ContainsKey("windows/processes"));Assert.False(off.ProviderMilliseconds.ContainsKey("processes/join"));
    }
    [Fact] public async Task Throwing_source_publishes_unavailable_and_keeps_cpu_and_memory()
    {
        var r=await FirstWith(new ThrowingProcesses());
        Assert.Equal(ProcessActivityKind.Unavailable,r.ProcessActivity.Kind);Assert.Equal("InvalidOperationException",r.ProcessActivity.Scan.Status);
        Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));
        Assert.DoesNotContain("Collection failed",r.HardwareStatus);
    }
    [Fact] public async Task Failed_scan_publishes_its_failure_at_once_and_paces_only_the_hardware_note()
    {
        var early=await FirstWith(new FailingProcesses(""));
        Assert.Equal(ProcessActivityKind.Unavailable,early.ProcessActivity.Kind);Assert.Equal(FailingProcesses.Text,early.ProcessActivity.Scan.Status);
        Assert.DoesNotContain("Process scan unavailable",early.HardwareStatus);
        var late=await FirstWith(new FailingProcesses(FailingProcesses.Text));
        Assert.Equal(FailingProcesses.Text,late.ProcessActivity.Scan.Status);
        Assert.Contains(" · "+FailingProcesses.Text,late.HardwareStatus);
    }
    [Fact] public async Task Switching_off_takes_effect_within_one_poll_and_clears_baselines()
    {
        var (c,fake)=Start(new(ProcessMonitoring:true));
        var rated=await NextWhere(c,r=>r.ProcessActivity.Processes.Any(p=>p.CpuPercent is not null));
        c.SetProcessMonitoring(false);int callsAtOff=fake.Calls;
        var next=await NextReadingAfter(c,rated);
        Assert.Equal(ProcessActivityKind.Off,next.ProcessActivity.Kind);Assert.False(c.ProcessMonitoring);
        var later=await NextReadingAfter(c,next);
        Assert.Equal(ProcessActivityKind.Off,later.ProcessActivity.Kind);Assert.True(fake.Calls<=callsAtOff+1);
        c.SetProcessMonitoring(true);
        var afterOn=await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Available&&r.Sample.MonotonicSeconds>later.Sample.MonotonicSeconds);
        Assert.NotEmpty(afterOn.ProcessActivity.Processes);
        Assert.All(afterOn.ProcessActivity.Processes,p=>{Assert.Equal(ProcessGap.FirstSample,p.RateGap);Assert.Null(p.CpuPercent);});
    }
    [Fact] public async Task Stopped_path_publishes_unavailable_or_off()
    {
        var on=await FirstWith(new ProcessorCountThrows(),new(ProcessMonitoring:true));
        Assert.StartsWith("Collector stopped (InvalidCastException)",on.HardwareStatus);
        Assert.Equal(ProcessActivityKind.Unavailable,on.ProcessActivity.Kind);Assert.Equal("InvalidCastException",on.ProcessActivity.Scan.Status);
        var off=await FirstWith(new ProcessorCountThrows(),new());
        Assert.StartsWith("Collector stopped",off.HardwareStatus);Assert.Equal(ProcessActivityKind.Off,off.ProcessActivity.Kind);
    }
    [Fact] public async Task Failed_pass_publishes_unavailable_with_the_exception_type()
    {
        var r=await FirstWithThrowingStorage(new(ProcessMonitoring:true));
        Assert.StartsWith("Collection failed (NotSupportedException)",r.HardwareStatus);
        Assert.Equal(ProcessActivityKind.Unavailable,r.ProcessActivity.Kind);Assert.Equal("NotSupportedException",r.ProcessActivity.Scan.Status);
    }
    [Fact] public async Task Failed_pass_with_monitoring_off_publishes_off()
    {
        var r=await FirstWithThrowingStorage(new());
        Assert.StartsWith("Collection failed",r.HardwareStatus);Assert.Equal(ProcessActivityKind.Off,r.ProcessActivity.Kind);
    }
    [Fact] public async Task Sequence_increases_per_published_activity()
    {
        var readings=await Readings(Build(new(ProcessMonitoring:true),new FakeProcesses()),2);
        var (first,second)=(readings[0],readings[1]);
        Assert.Equal(ProcessActivityKind.Available,first.ProcessActivity.Kind);Assert.Equal(ProcessActivityKind.Available,second.ProcessActivity.Kind);
        Assert.True(second.ProcessActivity.Sequence>first.ProcessActivity.Sequence);
    }
}
