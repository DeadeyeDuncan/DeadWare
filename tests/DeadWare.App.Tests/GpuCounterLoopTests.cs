using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class GpuCounterLoopTests : IAsyncLifetime
{
    /// <summary>
    /// A GPU counter source whose read carries the time passed in as MonotonicSeconds and the gap since its previous read as IntervalSeconds
    /// (null for the first after open). It answers at once, except that with <c>block</c> every call, and with <c>blockFrom:k</c> every call
    /// from the kth on, waits for <see cref="Release"/>; <see cref="Entered"/> completes when the first of them enters. With <c>throwAt:k</c>
    /// the kth call throws instead of answering.
    /// </summary>
    private sealed class FakeGpuSource(bool block=false,int blockFrom=0,int throwAt=0) : IGpuCounterSource
    {
        private readonly object gate=new();
        private readonly List<double> calls=[];
        private readonly int firstBlocked=block?1:blockFrom;
        private readonly ManualResetEventSlim released=new();
        private readonly TaskCompletionSource entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double previous,lastReadAt=double.NaN;
        private int reads,closes,inFlight,maxConcurrent;
        private bool open,lastReadOpened;
        /// <summary>Calls that have returned or thrown.</summary>
        public int Reads {get{lock(gate)return reads;}}
        public int Closes {get{lock(gate)return closes;}}
        /// <summary>The most calls in flight at once.</summary>
        public int MaxConcurrent {get{lock(gate)return maxConcurrent;}}
        public bool IsOpen {get{lock(gate)return open;}}
        public bool LastReadOpened {get{lock(gate)return lastReadOpened;}}
        /// <summary>The time passed to the last call that returned.</summary>
        public double LastReadAt {get{lock(gate)return lastReadAt;}}
        public Task Entered=>entered.Task;
        /// <summary>The gaps between the times passed to successive calls.</summary>
        public IReadOnlyList<double> CallGaps() {lock(gate)return calls.Zip(calls.Skip(1),(a,b)=>b-a).ToArray();}
        public void Release()=>released.Set();
        public async Task WaitForReads(int count)
        {
            var sw=Stopwatch.StartNew();
            while(Reads<count)
            {
                if(sw.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException($"{Reads} of {count} GPU reads");
                await Task.Delay(10);
            }
        }
        public GpuCounterRead Read(double monotonicSeconds,double pollingIntervalSeconds)
        {
            int call;double? interval;bool opened;
            lock(gate)
            {
                calls.Add(monotonicSeconds);call=calls.Count;
                maxConcurrent=Math.Max(maxConcurrent,++inFlight);
                opened=!open;open=true;
                interval=opened?null:monotonicSeconds-previous;
                previous=monotonicSeconds;
            }
            // Bounded so a test that fails before Release cannot park the thread for good; the class cleanup releases too.
            if(firstBlocked>0&&call>=firstBlocked) {entered.TrySetResult();released.Wait(TimeSpan.FromSeconds(30));}
            lock(gate) {inFlight--;reads++;lastReadAt=monotonicSeconds;lastReadOpened=opened;}
            if(call==throwAt)throw new InvalidOperationException("fake GPU read");
            var utc=DateTimeOffset.UnixEpoch.AddSeconds(monotonicSeconds);
            return new(ProcessColumnState.Ready,utc,monotonicSeconds,interval is {} gap?utc.AddSeconds(-gap):null,interval,[],[],[],new Dictionary<long,GpuAdapterName>(),0,0);
        }
        public void Close() {lock(gate){closes++;open=false;}}
    }

    private readonly List<(GpuCounterLoop Loop,FakeGpuSource Fake)> started=[];
    public Task InitializeAsync()=>Task.CompletedTask;
    /// <summary>Releases each fake before disposing its loop, so no test leaves a read parked.</summary>
    public async Task DisposeAsync() {foreach(var (loop,fake) in started){fake.Release();await loop.DisposeAsync();}}
    /// <summary>Starts a loop over <paramref name="source"/> on a Stopwatch clock.</summary>
    private (GpuCounterLoop Loop,FakeGpuSource Fake) Loop(Func<double>? interval=null,Func<bool>? enabled=null,FakeGpuSource? source=null)
    {
        source??=new FakeGpuSource();
        var clock=Stopwatch.StartNew();
        var loop=new GpuCounterLoop(source,()=>clock.Elapsed.TotalSeconds,interval??(()=>0.5),enabled??(()=>true));
        started.Add((loop,source));
        loop.Start();
        return (loop,source);
    }
    /// <summary>Takes from <paramref name="loop"/> every 10 ms until a take matches, for at most 15 s; each take before it is used up as a poll's would be.</summary>
    private static async Task<GpuCounterTake> TakeWhere(GpuCounterLoop loop,Func<GpuCounterTake,bool> match)
    {
        var sw=Stopwatch.StartNew();
        while(true)
        {
            if(loop.Take() is {} take&&match(take))return take;
            if(sw.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("No matching GPU take.");
            await Task.Delay(10);
        }
    }

    [Fact] public async Task Period_is_at_least_one_second_and_follows_slower_polling()
    {
        double interval=0.5;var (_,fake)=Loop(()=>interval);await fake.WaitForReads(3);
        Assert.All(fake.CallGaps(),g=>Assert.True(g>=0.95,$"gap {g:0.00} s"));
        interval=2;int n=fake.Reads;await fake.WaitForReads(n+2);
        Assert.All(fake.CallGaps().Skip(n),g=>Assert.True(g>=1.95,$"gap {g:0.00} s"));
    }
    [Fact] public async Task A_read_is_taken_fresh_once_then_again_without_credit()
    {
        // The second call parks, so the first read is the latest completed one when Take runs.
        var (loop,fake)=Loop(source:new FakeGpuSource(blockFrom:2));await fake.Entered;
        var first=loop.Take()!;Assert.True(first.Fresh);Assert.True(first.Read.Credit);Assert.NotNull(first.ReadMilliseconds);
        var again=loop.Take()!;Assert.False(again.Fresh);Assert.False(again.Read.Credit);Assert.Null(again.ReadMilliseconds);
        Assert.Equal(first.Read.MonotonicSeconds,again.Read.MonotonicSeconds);
        fake.Release();
    }
    [Fact] public async Task A_take_carries_when_its_read_returned()
    {
        // The first read parks for 300 ms, so it returns well after it started (its MonotonicSeconds); a poll ages a loop read from the return.
        var fake=new FakeGpuSource(block:true);var (loop,_)=Loop(source:fake);
        await fake.Entered;await Task.Delay(300);fake.Release();
        var first=await TakeWhere(loop,_=>true);Assert.True(first.Fresh);
        double took=first.ReturnedSeconds-first.Read.MonotonicSeconds;Assert.True(took>=0.25,$"returned {took:0.000} s after the read started");
        Assert.Equal(first.Read.MonotonicSeconds+first.ReadMilliseconds!.Value/1000,first.ReturnedSeconds,6);
        // Taken again without credit, the same read keeps its return time.
        var again=loop.Take()!;Assert.False(again.Fresh);Assert.Equal(first.ReturnedSeconds,again.ReturnedSeconds);
    }
    [Fact] public async Task A_throwing_read_is_kept_as_unavailable_and_the_loop_reads_on()
    {
        // Once, a throw ended the loop unobserved, and every later poll joined no read: "first sample" for good.
        var (loop,fake)=Loop(source:new FakeGpuSource(throwAt:1));
        var failed=await TakeWhere(loop,_=>true);
        Assert.True(failed.Fresh);Assert.Equal(new ProcessColumnState(false,GpuCounterStatus.Unavailable),failed.Read.State);
        await fake.WaitForReads(2);
        var next=await TakeWhere(loop,t=>t.Read.State.Available);Assert.True(next.Fresh);Assert.False(loop.Stopped.IsCompleted);
    }
    [Fact] public async Task A_read_passed_over_is_never_returned()
    {
        // The fourth call parks, so reads 1 and 2 were replaced before any Take and read 3 is the latest.
        var (loop,fake)=Loop(source:new FakeGpuSource(blockFrom:4));await fake.Entered;
        var taken=loop.Take()!;Assert.True(taken.Fresh);Assert.Equal(fake.LastReadAt,taken.Read.MonotonicSeconds);
        fake.Release();
    }
    [Fact] public async Task A_disabled_loop_does_not_read_and_closes_the_query()
    {
        bool on=true;var (loop,fake)=Loop(enabled:()=>on);await fake.WaitForReads(2);on=false;await Task.Delay(1500);
        int reads=fake.Reads;await Task.Delay(1500);Assert.Equal(reads,fake.Reads);Assert.False(fake.IsOpen);Assert.Null(loop.Take());
    }
    [Fact] public async Task Dispose_completes_within_the_hardware_budget_and_closes_after_the_read()
    {
        var fake=new FakeGpuSource(block:true);var (loop,_)=Loop(source:fake);await fake.Entered;
        var sw=Stopwatch.StartNew();await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(sw.Elapsed<TimeSpan.FromSeconds(5.5),$"Dispose took {sw.Elapsed}");Assert.Equal(0,fake.Closes);Assert.False(loop.Stopped.IsCompleted);
        fake.Release();await loop.Stopped.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,fake.Closes);Assert.Equal(1,fake.MaxConcurrent);
        // A second disposal (Task 5: monitoring off, the worker's finally, DisposeCoreAsync) returns at once and never throws.
        await loop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));Assert.Equal(1,fake.Closes);
    }
    [Fact] public async Task Interval_changes_keep_each_read_fresh_once()   // Review Focus 1
    {
        double interval=0.5,before=0.5;var (loop,fake)=Loop(()=>interval);var fresh=new List<double>();   // before: the interval in effect until the latest change
        async Task Poll(int count)
        {
            for(int i=0;i<count;i++)
            {
                if(loop.Take() is {Fresh:true} t)
                {
                    fresh.Add(t.Read.MonotonicSeconds);
                    Assert.True(t.Read.IntervalSeconds is null||t.Read.IntervalSeconds<=PollingPolicy.FreshnessSeconds(Math.Max(before,interval)),$"interval {t.Read.IntervalSeconds:0.00} s");
                }
                await Task.Delay(TimeSpan.FromSeconds(interval));
            }
        }
        await Poll(6);before=interval;interval=2;await Poll(3);before=interval;interval=0.5;await Poll(8);
        Assert.Equal(fresh.Count,fresh.Distinct().Count());Assert.True(fresh.Count>=5);
        // The period is back to max(1 s, 0.5 s).
        var last=fake.CallGaps().Last();Assert.True(last<1.9,$"last gap {last:0.00} s");
    }
    [Fact] public async Task The_first_read_waits_one_period_after_start()
    {
        // The poll that starts the loop has just read, so an immediate loop read would read more often than every-poll reading.
        var (loop,fake)=Loop();await Task.Delay(500);Assert.Equal(0,fake.Reads);Assert.Null(loop.Take());
        await fake.WaitForReads(1);Assert.True(fake.LastReadAt>=0.95,$"first read at {fake.LastReadAt:0.00} s");
    }
    [Fact] public async Task A_shorter_interval_takes_effect_during_a_long_wait()
    {
        // The first read returns into a 10 s period; 1.5 s later, past Period(0.5) but well inside Period(10), polling drops to 0.5 s.
        double interval=0.5;var fake=new FakeGpuSource(block:true);Loop(()=>interval,source:fake);
        await fake.Entered;interval=10;fake.Release();await fake.WaitForReads(1);
        await Task.Delay(1500);int reads=fake.Reads;var sw=Stopwatch.StartNew();interval=0.5;await fake.WaitForReads(reads+1);
        Assert.True(sw.Elapsed<TimeSpan.FromSeconds(GpuCounterLoop.Period(0.5)+0.5+0.1),$"next read {sw.Elapsed.TotalSeconds:0.00} s after the switch");
        Assert.Equal(1,reads);
    }
    [Fact] public async Task A_take_after_the_loop_stops_is_null()
    {
        var (loop,fake)=Loop();await fake.WaitForReads(1);Assert.NotNull(loop.Take());
        await loop.DisposeAsync();Assert.True(loop.Stopped.IsCompleted);Assert.Null(loop.Take());
    }
}
