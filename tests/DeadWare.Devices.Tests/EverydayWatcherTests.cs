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
