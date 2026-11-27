using System.Diagnostics;
using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5: the hub worker's shell and its watchdog. HubWatch is judged look by look. The shell tests run a real HubCore over the fakes of Task 4
// on the worker's own thread, with the core's clock frozen (fake time), and wait only on what the fakes record, within RealTimeCap. Each ends, after
// the worker has stopped, with Settled(): the fake lock is not held and every Enter had its Exit, and reading Held fails the test for any check the
// fakes keep that the core's own catch swallowed. endHelper only counts here: the watchdog calls it on a timer thread, where a failed assert would
// end the test run.
public sealed class HubWorkerTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-hubworker-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    readonly FakeTimeProvider time=new();
    readonly FakeHubLock corsairLock=new();
    readonly FakeHubTransport fake;
    readonly HubCore core;
    readonly ManualResetEventSlim hang=new(false),stuck=new(false),entered=new(false);
    int ended;
    const string StuckLine="hub: a USB call was stuck; its handle was closed",
        EndLine="the hub's USB call is stuck; ending the lighting helper so other programs get the Corsair lock back";
    static readonly Rgb A=new(10,20,30),B=new(1,2,3),C=new(4,5,6),D=new(7,8,9);
    static readonly Rgb[] Four=[A,B,C,D];
    static readonly Rgb[] FourLit=[..Enumerable.Repeat(A,8),..Enumerable.Repeat(B,8),..Enumerable.Repeat(C,8),..Enumerable.Repeat(D,20)];
    static readonly string[] TakeFrames=["02 13","05 01 01 21","0D 01 21","05 01 01 21","01 03 00 02",
        "05 01 01 36","0D 01 36","08 01","05 01 01 36","05 01 01 21","0D 01 21","08 01","05 01 01 21","05 01 01 17","0D 01 17","08 01","05 01 01 17"];

    public HubWorkerTests()
    {
        log=new HostLog(Path.Combine(dir,"logs"));
        fake=new(time,corsairLock);
        core=new(fake,corsairLock,time,log);
    }

    public void Dispose()
    {
        hang.Set();stuck.Set();
        core.Abort();
        log.Dispose();
        try{Directory.Delete(dir,true);}catch{}
    }

    HubWorker Worker()=>new(core,log,()=>Interlocked.Increment(ref ended));
    static async Task Stop(HubWorker worker)=>await Task.Run(worker.Dispose).WaitAsync(Waits.RealTimeCap);
    void Settled(){Assert.False(corsairLock.Held);Assert.Equal(corsairLock.Enters,corsairLock.Exits);}
    List<string> Commands()=>fake.Commands.Select(c=>string.Join(" ",c.Select(x=>x.ToString("X2")))).ToList();
    string[] Lines(){var p=Path.Combine(dir,"logs","devices.log");return File.Exists(p)?File.ReadAllLines(p).Select(l=>l[(l.IndexOf(' ')+1)..]).ToArray():[];}
    int LogCount(string line)=>Lines().Count(l=>l==line);
    static TimeSpan S(double seconds)=>TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_watch_closes_at_1_5_s_and_ends_3_s_later()
    {
        var watch=new HubWatch();
        Assert.Equal(HubWatchAct.None,watch.Look(S(1.4)));
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.5)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(1.75)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(4.4)));
        Assert.Equal(HubWatchAct.End,watch.Look(S(4.5)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(4.75)));
    }

    // The last two looks: after a null look, a time no shorter than the last one seen is still a new transaction.
    [Fact]
    public void A_finished_transaction_resets_the_watch()
    {
        var watch=new HubWatch();
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.6)));
        Assert.Equal(HubWatchAct.None,watch.Look(null));
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.5)));
        Assert.Equal(HubWatchAct.None,watch.Look(null));
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.6)));
    }

    [Fact]
    public void A_shorter_time_is_a_new_transaction()
    {
        var watch=new HubWatch();
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.6)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(0.2)));
        Assert.Equal(HubWatchAct.Close,watch.Look(S(1.5)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(4.4)));
    }

    // Lighting 5 (extra, the choice the brief leaves open): a first look already past 4.5 s (a late timer) closes first; the end comes at the next look.
    [Fact]
    public void A_late_first_look_closes_before_it_ends()
    {
        var watch=new HubWatch();
        Assert.Equal(HubWatchAct.Close,watch.Look(S(4.6)));
        Assert.Equal(HubWatchAct.End,watch.Look(S(4.85)));
        Assert.Equal(HubWatchAct.None,watch.Look(S(5.1)));
    }

    // The watch alone, then the worker between transactions: the core holds the hub, its port is open and no locked unit is under way (BusySince 0).
    // Look after look passes and nothing is closed or ended.
    [Fact]
    public async Task No_transaction_is_never_closed()
    {
        var watch=new HubWatch();
        for(int k=0;k<40;k++) Assert.Equal(HubWatchAct.None,watch.Look(null));
        core.Allow(null);
        var worker=Worker();
        try
        {
            await Waits.Until(()=>core.Snapshot.State=="driving");
            await Waits.Until(()=>core.BusySince==0);
            int looks=worker.Looks;
            await Waits.Until(()=>worker.Looks>=looks+4);                    // about 1 s of looks, none at a transaction
        }
        finally{await Stop(worker);}
        Assert.Equal(1,fake.Opens);
        Assert.Equal(0,fake.Closes);
        Assert.DoesNotContain(StuckLine,Lines());
        Assert.Equal(0,Volatile.Read(ref ended));
        Settled();
    }

    [Fact]
    public async Task The_shell_steps_the_core_and_stops()
    {
        core.Allow(null);
        var worker=Worker();
        try{await Waits.Until(()=>fake.Commands.Count>=17&&core.Snapshot.State=="driving");}
        finally{await Stop(worker);}
        Assert.False(worker.Alive);
        int after=fake.Writes.Count;
        core.Post(Four);                                                     // nobody steps the core any more
        Assert.Equal(TakeFrames,Commands());
        Assert.Equal(after,fake.Writes.Count);
        Assert.Equal(0,Volatile.Read(ref ended));
        Settled();
    }

    // The core asks to wait 30 s (the retry after a take that got no reply); Resume's nudge ends that wait, and the retake paints the posted frame
    // within RealTimeCap (15 s). The brief's version (a Post after the take, while the core asks to wait 3 s) passes without the nudge too, since
    // 3 s < RealTimeCap: see the report.
    [Fact]
    public async Task A_nudge_ends_the_wait()
    {
        Assert.True(HubCore.RetryMin>Waits.RealTimeCap);                    // the test's own premise (review M4): else the timed wake passes it too
        fake.Silent=true;
        core.Allow(null);
        var worker=Worker();
        try
        {
            await Waits.Until(()=>core.Snapshot.Reason=="silent");
            Assert.Equal(1,fake.Opens);
            fake.Silent=false;
            core.Post(Four);
            core.Resume();
            await Waits.Until(()=>fake.LastColours is not null);
        }
        finally{await Stop(worker);}
        Assert.Equal(FourLit,fake.LastColours);
        Assert.Equal("driving",core.Snapshot.State);
        Assert.Equal(2,fake.Opens);
        Assert.Equal(0,Volatile.Read(ref ended));
        Settled();
    }

    // Review I1 (a): a read held inside a locked unit (a hub call that does not return) is closed by the watchdog 1.5 s into the unit. The step ends
    // as a failure (letGo, silent) and lets go of the lock before the test releases anything; the stuck line is logged once; the helper is not ended.
    [Fact]
    public async Task The_watchdog_closes_a_stuck_call()
    {
        core.Allow(null);
        var worker=Worker();
        try
        {
            await Waits.Until(()=>core.Snapshot.State=="driving");
            fake.HangRead=hang;fake.ReadEntered=entered;
            // The watchdog runs on real time (amendment 2), so this test cannot use fake time. It pins a lower bound only, which a slow machine
            // cannot break: the clock starts before the unit does, so it reads at least what the watchdog read when it closed.
            var clock=Stopwatch.StartNew();
            core.Post(Four);                                                 // the paint's unit, held at its first read
            Assert.True(entered.Wait(Waits.RealTimeCap));
            await Waits.Until(()=>core.Snapshot.State=="letGo");             // about 1.5 to 1.75 s of real time
            Assert.True(clock.Elapsed>=TimeSpan.FromMilliseconds(1500),$"closed after {clock.Elapsed.TotalMilliseconds:0} ms");
            Assert.Equal("silent",core.Snapshot.Reason);
            Assert.Equal(0L,core.BusySince);
            Assert.False(corsairLock.Held);
            Assert.Equal(1,fake.Closes);
        }
        finally{hang.Set();await Stop(worker);}
        Assert.False(worker.Alive);                                          // the loop got back to its wait, so the step returned
        Assert.Equal(1,LogCount(StuckLine));
        Assert.Equal(0,LogCount(EndLine));
        Assert.Equal(0,Volatile.Read(ref ended));
        Settled();
    }

    // Review I1 (b): a call that closing the handle does not end (StuckRead ignores the port's Dispose). The watchdog closes at 1.5 s; at 4.5 s it
    // logs the end line, after the stuck line, and ends the helper, once. Released, the call ends as a failure.
    [Fact]
    public async Task A_call_the_close_does_not_end_ends_the_helper()
    {
        core.Allow(null);
        var worker=Worker();
        try
        {
            await Waits.Until(()=>core.Snapshot.State=="driving");
            fake.StuckRead=stuck;fake.ReadEntered=entered;
            // Real time, as above (amendment 2): a lower bound only, with the clock started before the unit.
            var clock=Stopwatch.StartNew();
            core.Post(Four);                                                 // the paint's unit, stuck at its first read
            Assert.True(entered.Wait(Waits.RealTimeCap));
            await Waits.Until(()=>Volatile.Read(ref ended)>0);               // about 4.5 to 4.75 s of real time
            Assert.True(clock.Elapsed>=TimeSpan.FromMilliseconds(4500),$"ended after {clock.Elapsed.TotalMilliseconds:0} ms");
            var lines=Lines();
            Assert.Equal(1,lines.Count(l=>l==StuckLine));
            Assert.Equal(1,lines.Count(l=>l==EndLine));
            Assert.True(Array.IndexOf(lines,StuckLine)<Array.IndexOf(lines,EndLine));
            Assert.Equal(1,fake.Closes);
            stuck.Set();
            await Waits.Until(()=>core.Snapshot.State=="letGo");
        }
        finally{stuck.Set();await Stop(worker);}
        Assert.Equal("silent",core.Snapshot.Reason);
        Assert.Equal(1,LogCount(EndLine));
        Assert.Equal(1,Volatile.Read(ref ended));
        Settled();
    }

    // Review M1: unit X is closed and ends, unit Y begins with no look between them, and at the next look Y is older than X was at its last. Y still
    // gets its own Close. The looks are the test's (Tick), on the real clock; the worker's timer never fires.
    [Fact]
    public async Task A_new_unit_between_two_looks_gets_its_own_close()
    {
        core.Allow(null);
        var worker=new HubWorker(core,log,()=>Interlocked.Increment(ref ended),Timeout.InfiniteTimeSpan);
        try
        {
            await Waits.Until(()=>core.Snapshot.State=="driving");
            fake.HangRead=hang;fake.ReadEntered=entered;
            core.Post(Four);                                                 // unit X: the paint, held at its first read
            Assert.True(entered.Wait(Waits.RealTimeCap));
            var seenX=TimeSpan.Zero;                                         // at least X's age at the look that closed it
            await Waits.Until(()=>{var x=core.BusySince;worker.Tick();seenX=Stopwatch.GetElapsedTime(x);return fake.Closes==1;});
            await Waits.Until(()=>core.Snapshot.State=="letGo");             // X ended as a failure; no look since its Close
            entered.Reset();
            core.Resume();                                                   // unit Y: the retake, held at its first read
            Assert.True(entered.Wait(Waits.RealTimeCap));
            await Waits.Until(()=>Stopwatch.GetElapsedTime(core.BusySince)>seenX);
            worker.Tick();
            Assert.Equal(2,fake.Closes);
            await Waits.Until(()=>core.Snapshot.State=="letGo");
        }
        finally{hang.Set();await Stop(worker);}
        Assert.Equal(2,fake.Opens);
        Assert.Equal(2,LogCount(StuckLine));
        Assert.Equal(0,Volatile.Read(ref ended));
        Settled();
    }
}
