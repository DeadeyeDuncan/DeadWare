using System.Diagnostics;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 4: the everyday watcher with its real hidden window. No global shortcut is ever registered here: only SetShortcut(null,...),
// which clears, so these are safe on any PC.
public sealed class EverydayWatcherTests
{
    [Fact]
    public void It_starts_and_stops_without_a_problem()
    {
        var w=new EverydayWatcher(new HostLog(null));
        Assert.Null(w.Problem);
        var sw=Stopwatch.StartNew();
        w.Dispose();
        Assert.True(sw.Elapsed<TimeSpan.FromSeconds(5),$"Dispose took {sw.Elapsed}");
    }

    [Fact]
    public void Clearing_the_shortcut_answers_true()
    {
        using var w=new EverydayWatcher(new HostLog(null));
        Assert.True(w.SetShortcut(null,TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void After_dispose_it_answers_null()
    {
        var w=new EverydayWatcher(new HostLog(null));
        w.Dispose();
        Assert.Null(w.SetShortcut(null,TimeSpan.FromSeconds(1)));
    }

    // Fix round 1, I1: a request that got no answer in time is withdrawn, so the thread never applies it later. The request is a clear
    // (null), so nothing ever reaches a real global-key registration; whether the thread took it is read from the test seam.
    [Fact]
    public void A_request_that_timed_out_is_never_applied_later()
    {
        using var w=new EverydayWatcher(new HostLog(null));
        using var gate=new ManualResetEventSlim();
        using var handled=new ManualResetEventSlim();
        var took=new List<bool>();
        var first=true;
        w.BeforeTake=()=>{if(first){first=false;gate.Wait(TimeSpan.FromSeconds(5));}};
        w.AfterTake=t=>{lock(took)took.Add(t);handled.Set();};
        Assert.Null(w.SetShortcut(null,TimeSpan.FromMilliseconds(100)));
        gate.Set();
        Assert.True(handled.Wait(TimeSpan.FromSeconds(5)));
        lock(took) Assert.Equal([false],took);
        Assert.True(w.SetShortcut(null,TimeSpan.FromSeconds(1)));
    }

    // Fix round 1, I1: a request the thread took just after the caller's wait ran out answers its real result, not null.
    [Fact]
    public void A_request_taken_late_answers_its_real_result()
    {
        using var w=new EverydayWatcher(new HostLog(null));
        w.AfterTake=t=>{if(t) Thread.Sleep(800);};
        Assert.True(w.SetShortcut(null,TimeSpan.FromMilliseconds(500)));
    }

    // Fix round 1, M3, ruling P15: wParam 4 suspend, 7 resume from suspend, 0x12 automatic resume.
    [Theory]
    [InlineData(false,4,true,true)]       // suspend raised
    [InlineData(true,4,false,true)]       // a second suspend is not
    [InlineData(false,7,false,false)]     // resume from suspend with no suspend since the last wake: ignored
    [InlineData(true,7,true,false)]       // resume from suspend after a suspend: raised
    [InlineData(false,0x12,true,false)]   // automatic resume: always raised
    [InlineData(true,0x12,true,false)]
    [InlineData(true,10,false,true)]      // anything else changes nothing
    [InlineData(false,10,false,false)]
    public void Power_follows_the_sleep_rule(bool suspended,int e,bool raise,bool after)
        =>Assert.Equal((raise,after),EverydayWatcher.Power(suspended,e));

    [Fact]
    public void Suspend_twice_then_both_resumes_raise_once_each()
    {
        var s=false;var raised=new List<int>();
        foreach(var e in new[]{4,4,0x12,7})
        {
            var (r,now)=EverydayWatcher.Power(s,e);s=now;
            if(r) raised.Add(e);
        }
        Assert.Equal([4,0x12],raised);
    }
}

public sealed class InputClockTests
{
    [Fact]
    public void Idle_time_is_not_negative_and_the_pointer_reads()
    {
        var c=new InputClock();
        Assert.True(c.IdleMs()>=0);
        _=c.Pointer();
    }
}
