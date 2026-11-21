using DeadWare.Core;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 3b, Task 6: the gate between the hook and the paint stage (Review Focus 4). Scan codes are Windows set-1: A=0x1E, W=0x11,
// Control=0x1D (extended for the right-hand one).
public sealed class KeyPressGateTests
{
    double now;
    int pressed;
    KeyPressGate Gate()=>new(()=>now,()=>pressed++);
    static readonly IReadOnlyList<string> Board=["Key: A","Key: W","Key: Left Control","Key: Right Control","Key 4"];
    static (string,IReadOnlyList<string>) Kb(string key,IReadOnlyList<string> names)=>(key,names);
    const int A=0x1E,W=0x11,Ctrl=0x1D,Q=0x10;
    static void Tap(KeyPressGate g,int scan,bool ext=false){g.OnKey(scan,ext,false,false);g.OnKey(scan,ext,false,true);}

    [Fact]
    public void A_press_becomes_light_numbers_for_each_prepared_keyboard()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board),Kb("kb2",["Key: W","A"])]);
        now=10;
        g.OnKey(A,false,false,false);
        var live=g.Live();
        Assert.Equal(2,live.Count);
        Assert.Equal("kb1",live[0].DeviceKey);Assert.Equal([0],live[0].Lights);Assert.Equal(10,live[0].AtMs);
        Assert.Equal("kb2",live[1].DeviceKey);Assert.Equal([1],live[1].Lights);
        Assert.Equal(1,pressed);
    }

    [Fact]
    public void A_key_with_no_light_leaves_nothing_and_is_not_remembered_as_held()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(Q,false,false,false); // Q has no light on this keyboard
        Assert.Empty(g.Live());
        Assert.Equal(0,pressed);
        // A later table that maps Q: its next down counts, so the first was never remembered as held.
        g.Prepare([Kb("kb1",["Key: Q"])]);
        now=1;
        g.OnKey(Q,false,false,false);
        Assert.Single(g.Live());
    }

    [Fact]
    public void A_held_key_counts_once_until_released()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        for(var i=0;i<3;i++){now+=30;g.OnKey(A,false,false,false);}
        now+=30;g.OnKey(A,false,false,true);
        now+=30;g.OnKey(A,false,false,false);
        Assert.Equal(2,g.Live().Count);
        Assert.Equal(2,pressed);
    }

    [Fact]
    public void A_key_whose_release_was_missed_counts_again_two_seconds_after_its_last_repeat()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(A,false,false,false);
        now=1500;g.OnKey(A,false,false,false); // a repeat: noted, not counted
        now=3400;g.OnKey(A,false,false,false); // 1.9 s after the last repeat: still held
        Assert.Equal(1,pressed);
        now=5400;g.OnKey(A,false,false,false); // 2 s after the last sighting: counts again
        Assert.Equal(2,pressed);
        Assert.Equal(5400,g.Live().Last().AtMs);
    }

    [Fact]
    public void Software_sent_keys_are_ignored_before_anything_else()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(A,false,true,false);
        Assert.Empty(g.Live());
        Assert.Equal(0,pressed);
        g.OnKey(A,false,false,false); // not marked held by the software-sent one
        Assert.Single(g.Live());
        g.OnKey(A,false,true,true);   // a software-sent up doesn't release it either
        now=100;g.OnKey(A,false,false,false);
        Assert.Single(g.Live());
    }

    [Fact]
    public void Presses_expire_after_three_seconds()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        Tap(g,A);
        now=2999;Assert.Single(g.Live());
        now=3000;Assert.Empty(g.Live());
    }

    [Fact]
    public void At_most_32_live_presses_newest_kept()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        for(var i=0;i<40;i++){now=i;Tap(g,A);}
        var live=g.Live();
        Assert.Equal(KeyPressGate.MaxLive,live.Count);
        Assert.Equal(8,live[0].AtMs);Assert.Equal(39,live[^1].AtMs);
    }

    [Fact]
    public void Preparing_nothing_clears_presses_and_held_keys()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(A,false,false,false);
        g.Prepare([]);
        Assert.Empty(g.Live());
        g.OnKey(A,false,false,false); // no table: nothing
        Assert.Empty(g.Live());
        g.Prepare([Kb("kb1",Board)]);
        now=10;g.OnKey(A,false,false,false); // not held any more: counts
        Assert.Single(g.Live());
    }

    [Fact]
    public void Released_forgets_held_keys_and_keeps_presses()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(A,false,false,false);
        g.Released();
        Assert.Single(g.Live());
        now=10;g.OnKey(A,false,false,false);
        Assert.Equal(2,g.Live().Count);
        g.Clear();
        Assert.Empty(g.Live());
        now=20;g.OnKey(A,false,false,false);
        Assert.Single(g.Live());
    }

    [Fact]
    public void OnKey_never_throws()
    {
        new KeyPressGate(()=>0).OnKey(A,false,false,false);
        var g=new KeyPressGate(()=>now,()=>throw new InvalidOperationException("boom"));
        g.OnKey(-1,false,false,false);
        g.OnKey(int.MaxValue,true,false,true);
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(A,false,false,false);
        Assert.Single(g.Live());
        var bad=new KeyPressGate(()=>throw new InvalidOperationException("clock"));
        bad.Prepare([Kb("kb1",Board)]);
        bad.OnKey(A,false,false,false);
    }

    [Fact]
    public void Left_and_right_keys_of_one_scan_are_held_apart()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board)]);
        g.OnKey(Ctrl,false,false,false);
        g.OnKey(Ctrl,true,false,false);
        var live=g.Live();
        Assert.Equal(2,live.Count);
        Assert.Equal([2],live[0].Lights);Assert.Equal([3],live[1].Lights);
        g.OnKey(Ctrl,true,false,true);  // right up: left stays held
        now=10;g.OnKey(Ctrl,false,false,false);
        now=20;g.OnKey(Ctrl,true,false,false);
        Assert.Equal(3,g.Live().Count);
    }

    [Fact]
    public async Task OnKey_from_two_threads_keeps_the_list_bounded()
    {
        var clock=0L;
        var g=new KeyPressGate(()=>Interlocked.Increment(ref clock)/1000.0);
        g.Prepare([Kb("kb1",Board)]);
        Task Run(int scan)=>Task.Run(()=>{for(var i=0;i<10_000;i++){g.OnKey(scan,false,false,i%2==1);if(i%100==0)Assert.InRange(g.Live().Count,0,KeyPressGate.MaxLive);}});
        await Task.WhenAll(Run(A),Run(W));
        var live=g.Live();
        Assert.InRange(live.Count,1,KeyPressGate.MaxLive);
        Assert.Equal(live.OrderBy(p=>p.AtMs).Select(p=>p.AtMs),live.Select(p=>p.AtMs));
    }

    // Fix round 1, M1: the table is built in Prepare, so a key with no light, or a held key's repeat, costs no name matching and no
    // allocation on the hook's thread.
    [Fact]
    public void An_unmapped_key_or_a_held_repeat_allocates_nothing()
    {
        var g=Gate();
        g.Prepare([Kb("kb1",Board),Kb("kb2",Board)]);
        g.OnKey(A,false,false,false);
        g.OnKey(Q,false,false,false);g.OnKey(A,false,false,false); // warm up both paths
        var before=GC.GetAllocatedBytesForCurrentThread();
        for(var i=0;i<1000;i++){g.OnKey(Q,false,false,false);g.OnKey(Q,false,false,true);g.OnKey(A,false,false,false);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        Assert.Equal(1,pressed);
    }

    // Fix round 1, M8: preparing the same table again (as every raised state does) while someone types drops no key event: every down
    // after an up counts.
    [Fact]
    public async Task Preparing_the_same_table_while_typing_drops_nothing()
    {
        var clock=0L;var count=0;
        var g=new KeyPressGate(()=>Interlocked.Increment(ref clock),()=>Interlocked.Increment(ref count));
        g.Prepare([Kb("kb1",Board)]);
        using var stop=new CancellationTokenSource();
        var preparing=Task.Run(()=>{while(!stop.IsCancellationRequested) g.Prepare([Kb("kb1",Board.ToArray())]);});
        for(var i=0;i<20_000;i++){g.OnKey(A,false,false,false);g.OnKey(A,false,false,true);}
        stop.Cancel();await preparing;
        Assert.Equal(20_000,Volatile.Read(ref count));
    }
}
