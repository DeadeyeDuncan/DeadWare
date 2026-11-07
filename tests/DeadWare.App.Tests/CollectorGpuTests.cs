using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class CollectorGpuTests : IAsyncLifetime
{
    /// <summary>
    /// Ready reads giving PID 100 "GPU 0 · 3D" at 25 % and 64,000,000 dedicated bytes. Each read's MonotonicSeconds is the time passed in. The first
    /// read after construction or after <see cref="Close"/> opens the query (<see cref="LastReadOpened"/>) and has no previous collection; later reads
    /// carry the gap since the previous read as IntervalSeconds, as GpuCounterLoopTests' fake does. Each read takes <c>delay</c>. After
    /// <see cref="Stall"/>, a read that starts waits for <see cref="Release"/>, and <see cref="Entered"/> completes when the first of them enters.
    /// </summary>
    private sealed class FakeGpuCounters(TimeSpan delay=default) : IGpuCounterSource
    {
        private const long Luid=0xA;
        private static readonly IReadOnlyDictionary<long,GpuAdapterName> Adapters=new Dictionary<long,GpuAdapterName>{[Luid]=new(0,"Fake GPU")};
        private readonly object gate=new();
        private readonly ManualResetEventSlim released=new();
        private readonly TaskCompletionSource entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double previous,lastReadAt=double.NaN;
        private int reads,closes,closesDuringRead,inFlight,maxConcurrent;
        private bool open,lastReadOpened,stalled;
        /// <summary>Reads that have started.</summary>
        public int Reads {get{lock(gate)return reads;}}
        public int Closes {get{lock(gate)return closes;}}
        /// <summary>Closes that arrived while a read was in flight.</summary>
        public int ClosesDuringRead {get{lock(gate)return closesDuringRead;}}
        /// <summary>The most reads in flight at once.</summary>
        public int MaxConcurrent {get{lock(gate)return maxConcurrent;}}
        public bool IsOpen {get{lock(gate)return open;}}
        public bool LastReadOpened {get{lock(gate)return lastReadOpened;}}
        /// <summary>The time passed to the last read that returned.</summary>
        public double LastReadAt {get{lock(gate)return lastReadAt;}}
        public Task Entered=>entered.Task;
        public void Stall() {lock(gate)stalled=true;}
        public void Release()=>released.Set();
        public GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds)
        {
            bool opened,stall;double? interval;
            lock(gate)
            {
                reads++;maxConcurrent=Math.Max(maxConcurrent,++inFlight);
                opened=!open;open=true;
                interval=opened?null:monotonicSeconds-previous;
                previous=monotonicSeconds;stall=stalled;
            }
            try
            {
                // Bounded so a test that fails before Release cannot park the thread for good; the class cleanup releases too.
                if(stall) {entered.TrySetResult();released.Wait(TimeSpan.FromSeconds(30));}
                if(delay>TimeSpan.Zero)Thread.Sleep(delay);
            }
            finally {lock(gate){inFlight--;lastReadOpened=opened;lastReadAt=monotonicSeconds;}}
            var utc=DateTimeOffset.UnixEpoch.AddSeconds(monotonicSeconds);
            GpuEngineItem[] engines=[new(new GpuCounterInstance(100,Luid,0,0,"3D"),true,25)];
            GpuMemoryItem[] dedicated=[new(new GpuCounterInstance(100,Luid,0,null,null),true,64_000_000)];
            return new(ProcessColumnState.Ready,utc,monotonicSeconds,interval is {} gap?utc.AddSeconds(-gap):null,interval,engines,dedicated,[],Adapters,engines.Length+dedicated.Length,0);
        }
        public void Close() {lock(gate){closes++;if(inFlight>0)closesDuringRead++;open=false;}}
    }
    /// <summary>A GPU source whose every read throws <see cref="InvalidOperationException"/>.</summary>
    private sealed class ThrowingGpuCounters : IGpuCounterSource
    {
        public bool LastReadOpened=>false;
        public bool IsOpen=>false;
        public GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds)=>throw new InvalidOperationException();
        public void Close() {}
    }

    private readonly FakeGpuCounters fake=new();
    private readonly List<Collector> started=[];
    private readonly List<FakeGpuCounters> fakes=[];
    public Task InitializeAsync()=>Task.CompletedTask;
    /// <summary>Releases each fake before disposing the collectors, so no test leaves a read parked.</summary>
    public async Task DisposeAsync() {foreach(var f in fakes)f.Release();foreach(var c in started)await c.DisposeAsync();}

    private Collector Build(IGpuCounterSource gpu,CollectorOptions? options)
    {
        var c=new Collector((options??new(ProcessMonitoring:true)) with {HardwareEnabled=false,EtwEnabled=false,PollingIntervalSeconds=0.5},null,processes:new FakeProcesses(),gpuCounters:gpu);
        started.Add(c);if(gpu is FakeGpuCounters f)fakes.Add(f);return c;
    }
    private Collector Start(IGpuCounterSource gpu,CollectorOptions? options=null)
    {
        var c=Build(gpu,options);c.Start();return c;
    }
    /// <summary>The second reading a collector publishes, subscribed before it starts.</summary>
    private async Task<Reading> Second(CollectorOptions options,IGpuCounterSource gpu)
    {
        var c=Build(gpu,options);
        var list=new List<Reading>();
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.SampleAvailable+=r=>{lock(list){if(list.Count<2){list.Add(r);if(list.Count==2)tcs.TrySetResult(r);}}};
        c.Start();
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
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
    /// <summary>Polls every 20 ms and fails at <paramref name="limit"/>.</summary>
    private static async Task WaitFor(Func<bool> condition,TimeSpan limit)
    {
        var watch=Stopwatch.StartNew();
        while(!condition())
        {
            if(watch.Elapsed>limit)Assert.Fail($"The condition did not hold within {limit.TotalSeconds:0.#} s.");
            await Task.Delay(20);
        }
    }
    private static bool Keyed(Reading r)=>r.ProviderMilliseconds!.ContainsKey("windows/gpucounters");

    [Fact] public async Task Gpu_values_join_the_published_activity()
    {
        var r=await NextWhere(Start(fake),r=>r.ProcessActivity.GpuIntervalSeconds is not null);var a=r.ProcessActivity.Processes.Single(p=>p.Pid==100);
        Assert.Equal(25,a.GpuPercent);Assert.Equal("GPU 0 · 3D",a.GpuEngine);Assert.Equal(64_000_000,a.GpuDedicatedBytes);
        Assert.Equal(0,r.ProcessActivity.Processes.Single(p=>p.Pid==200).GpuPercent);Assert.True(r.ProcessActivity.Gpu.Available);
        // Every-poll placement is never stale.
        Assert.False(r.ProcessActivity.GpuStale);
    }
    [Fact] public async Task A_throwing_gpu_source_keeps_cpu_memory_and_the_process_columns()
    {
        var r=await NextWhere(Start(new ThrowingGpuCounters()),r=>r.ProcessActivity.Processes.Any(p=>p.CpuPercent is not null));
        Assert.Equal(ProcessActivityKind.Available,r.ProcessActivity.Kind);Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),r.ProcessActivity.Gpu);
        Assert.All(r.ProcessActivity.Processes,p=>Assert.Null(p.GpuPercent));
        Assert.True(r.Sample.Values.ContainsKey("cpu/usage"));Assert.True(r.Sample.Values.ContainsKey("memory/usage"));
    }
    [Fact] public async Task Turning_monitoring_off_closes_the_query_within_one_poll()
    {
        var c=Start(fake);var on=await NextWhere(c,r=>fake.Reads>=2);c.SetProcessMonitoring(false);
        var off=await NextReadingAfter(c,on);Assert.Equal(ProcessActivityKind.Off,off.ProcessActivity.Kind);Assert.False(fake.IsOpen);
        int reads=fake.Reads;await NextReadingAfter(c,off);Assert.Equal(reads,fake.Reads);
    }
    [Fact] public async Task A_slow_source_moves_to_its_own_loop_where_an_unrefreshed_poll_adds_nothing()
    {
        var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
        var looped=await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
        // In loop placement a keyed poll took a loop read fresh, with credit; a later unkeyed poll joins that read again for display only.
        var fresh=await NextWhere(c,r=>r.Sample.MonotonicSeconds>looped.Sample.MonotonicSeconds&&Keyed(r));
        Assert.NotNull(fresh.ProcessActivity.GpuIntervalSeconds);Assert.NotNull(fresh.ProcessActivity.Processes.Single(p=>p.Pid==100).Deltas!.GpuSeconds);
        var rejoined=await NextWhere(c,r=>r.ProcessActivity.Gpu.Available&&r.ProcessActivity.GpuIntervalSeconds is null&&r.Sample.MonotonicSeconds>fresh.Sample.MonotonicSeconds);
        Assert.Equal(GpuCounterSummary.OwnLoop,rejoined.ProcessSummary!.Gpu!.Placement);Assert.False(rejoined.ProcessActivity.GpuStale);
        Assert.All(rejoined.ProcessActivity.Processes,p=>{Assert.NotNull(p.Deltas);Assert.Null(p.Deltas!.GpuSeconds);});Assert.DoesNotContain("windows/gpucounters",rejoined.ProviderMilliseconds!.Keys);
        // Shown, not credited.
        Assert.Equal(25,rejoined.ProcessActivity.Processes.Single(p=>p.Pid==100).GpuPercent);
    }
    [Fact] public async Task A_loop_read_older_than_two_periods_is_stale_and_still_carried()
    {
        var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
        try
        {
            var looped=await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
            // A fresh take after the latch, so a loop read has completed before the stall.
            var fresh=await NextWhere(c,r=>r.Sample.MonotonicSeconds>looped.Sample.MonotonicSeconds&&Keyed(r));Assert.False(fresh.ProcessActivity.GpuStale);
            // The read in flight is older than 2 × Period(0.5) = 2 s.
            fake.Stall();var stale=await NextWhere(c,r=>r.ProcessActivity.GpuStale);
            // The loop's latest read is the last one that returned; a poll joining it is stale only past two periods, never after one.
            double age=stale.Sample.MonotonicSeconds-fake.LastReadAt;Assert.True(age>2*GpuCounterLoop.Period(0.5),$"stale at {age:0.00} s");
            Assert.True(stale.ProcessActivity.Gpu.Available);Assert.Equal(25,stale.ProcessActivity.Processes.Single(p=>p.Pid==100).GpuPercent);
        }
        finally {fake.Release();}
    }
    [Fact] public async Task The_timing_key_appears_only_when_a_read_ran()
    {
        var r=await Second(new(ProcessMonitoring:true,DiagnosticTimings:true),fake);Assert.Contains("windows/gpucounters",r.ProviderMilliseconds!.Keys);
        Assert.Equal(GpuCounterSummary.EveryPoll,r.ProcessSummary!.Gpu!.Placement);Assert.NotNull(r.ProcessSummary.Gpu.ReadMilliseconds);
        var off=await Second(new(DiagnosticTimings:true),new FakeGpuCounters());Assert.DoesNotContain("windows/gpucounters",off.ProviderMilliseconds!.Keys);Assert.Null(off.ProcessSummary!.Gpu);
    }
    [Fact] public async Task Rapid_toggles_during_a_loop_read_never_read_concurrently()   // Review Focus 2
    {
        var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(300));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
        try
        {
            await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
            // The loop's next read parks in flight until Release.
            fake.Stall();await fake.Entered.WaitAsync(TimeSpan.FromSeconds(10));int reads=fake.Reads;
            c.SetProcessMonitoring(false);c.SetProcessMonitoring(true);c.SetProcessMonitoring(false);
            // The first off poll disposes the loop; the second runs while the disposed loop's read is still in flight.
            var off=await NextReadingAfter(c,await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Off));
            Assert.Equal(ProcessActivityKind.Off,off.ProcessActivity.Kind);
            c.SetProcessMonitoring(true);
            var on=await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Available&&r.Sample.MonotonicSeconds>off.Sample.MonotonicSeconds);
            foreach(var r in new[]{on,await NextReadingAfter(c,on)})
            {
                // The disposed loop still owns the query, so these polls join no read and do not read.
                Assert.Equal(ProcessActivityKind.Available,r.ProcessActivity.Kind);Assert.False(Keyed(r));
                Assert.Equal(GpuCounterSummary.EveryPoll,r.ProcessSummary!.Gpu!.Placement);Assert.All(r.ProcessActivity.Processes,p=>Assert.Null(p.GpuPercent));
            }
            Assert.Equal(reads,fake.Reads);Assert.Equal(0,fake.Closes);
            fake.Release();
            // Not !IsOpen: the next poll's reopen could hide the close inside one 20 ms step.
            await WaitFor(()=>fake.Closes>=1,TimeSpan.FromSeconds(5));
            var back=await NextWhere(c,r=>r.Sample.MonotonicSeconds>on.Sample.MonotonicSeconds&&Keyed(r));
            // The first read after the reopen is a first sample, in every-poll placement.
            Assert.Equal(GpuCounterSummary.EveryPoll,back.ProcessSummary!.Gpu!.Placement);Assert.True(back.ProcessActivity.Gpu.Available);
            Assert.All(back.ProcessActivity.Processes,p=>{Assert.Null(p.GpuPercent);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});
            Assert.Equal(1,fake.MaxConcurrent);Assert.Equal(0,fake.ClosesDuringRead);
            c.SetProcessMonitoring(false);
            await NextReadingAfter(c,await NextWhere(c,r=>r.ProcessActivity.Kind==ProcessActivityKind.Off&&r.Sample.MonotonicSeconds>back.Sample.MonotonicSeconds));
            Assert.False(fake.IsOpen);
        }
        finally {fake.Release();}
    }
    [Fact] public async Task A_worker_that_stops_after_an_error_stops_the_gpu_loop()   // as EtwDiskTests.Worker_that_stops_after_an_error_stops_the_disk_trace
    {
        var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
        try
        {
            await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
            // A loop read is in flight when the worker stops, so its finally must neither wait on that read nor close under it.
            fake.Stall();await fake.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            // Throws from the pass's publication and again from the failed pass's, so the worker leaves through its outer catch.
            int throws=2;c.SampleAvailable+=_=>{if(Interlocked.Decrement(ref throws)>=0)throw new InvalidOperationException("subscriber");};
            await c.Completion.WaitAsync(TimeSpan.FromSeconds(10));Assert.StartsWith("Collector stopped (InvalidOperationException)",c.Latest!.HardwareStatus);
            Assert.Equal(0,fake.Closes);fake.Release();
            await WaitFor(()=>!fake.IsOpen,TimeSpan.FromSeconds(3));int reads=fake.Reads;await Task.Delay(1500);Assert.Equal(reads,fake.Reads);
            Assert.Equal(1,fake.MaxConcurrent);Assert.Equal(0,fake.ClosesDuringRead);
        }
        finally {fake.Release();}
    }
    [Fact] public async Task Disposal_waits_for_the_loop_read_in_flight_and_its_close()
    {
        var fake=new FakeGpuCounters(delay:TimeSpan.FromMilliseconds(30));var c=Start(fake,new(ProcessMonitoring:true,DiagnosticTimings:true));
        try
        {
            await NextWhere(c,r=>r.ProcessSummary?.Gpu?.Placement==GpuCounterSummary.OwnLoop);
            fake.Stall();await fake.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            // The worker ends at once; disposal then waits, within the 5 s hardware budget, for the read to return and the loop to close the query.
            var disposal=c.DisposeAsync().AsTask();await Task.Delay(500);Assert.False(disposal.IsCompleted);Assert.Equal(0,fake.Closes);
            fake.Release();await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(fake.IsOpen);Assert.Equal(1,fake.MaxConcurrent);Assert.Equal(0,fake.ClosesDuringRead);
        }
        finally {fake.Release();}
    }
}
