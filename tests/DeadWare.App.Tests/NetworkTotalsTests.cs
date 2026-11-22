using System.Net.NetworkInformation;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class NetworkTotalsTests
{
    /// <summary>A scripted adapter: a read returns the counters set on it, or throws once the adapter is gone, as Windows does.</summary>
    private sealed class FakeAdapter(string id)
    {
        public long Received,Sent;
        public bool Gone;
        public int Reads;
        public NetworkAdapter Adapter=>new(id,()=>{Reads++;if(Gone)throw new NetworkInformationException(2);return (Received,Sent);});
    }
    private const string Note=" · totals include virtual adapters; VPN traffic can be counted twice";

    [Theory]
    [InlineData(OperationalStatus.Up,NetworkInterfaceType.Ethernet,1,true)]
    [InlineData(OperationalStatus.Up,NetworkInterfaceType.Wireless80211,2,true)]
    [InlineData(OperationalStatus.Down,NetworkInterfaceType.Ethernet,1,false)]
    [InlineData(OperationalStatus.Up,NetworkInterfaceType.Loopback,1,false)]
    // A filter layer (WFP, QoS Packet Scheduler) repeats its adapter's counters and holds no address.
    [InlineData(OperationalStatus.Up,NetworkInterfaceType.Ethernet,0,false)]
    public void Counted_adapters_are_up_not_loopback_and_hold_an_address(OperationalStatus status,NetworkInterfaceType type,int addresses,bool expected)
        =>Assert.Equal(expected,NetworkTotals.Counted(status,type,addresses));

    [Fact] public void Totals_sum_each_counted_adapter_once_both_have_a_baseline()
    {
        var a=new FakeAdapter("a");var b=new FakeAdapter("b");
        using var totals=new NetworkTotals(()=>[a.Adapter,b.Adapter]);
        var first=totals.Read(0);
        Assert.Null(first.Down);Assert.Null(first.Up);Assert.Equal("2 active interfaces"+Note+" · waiting for counter baseline / reset",first.Status);
        a.Received=1000;a.Sent=100;b.Received=500;b.Sent=50;
        var second=totals.Read(1);
        Assert.Equal(1500,second.Down);Assert.Equal(150,second.Up);Assert.Equal("2 active interfaces"+Note,second.Status);
    }

    [Fact] public void Virtual_adapters_are_left_out_only_when_asked()
    {
        var a=new FakeAdapter("a");var v=new FakeAdapter("vpn");
        using var totals=new NetworkTotals(()=>[a.Adapter,v.Adapter with {Virtual=true}]);
        totals.Read(0);totals.Read(0,excludeVirtual:true);
        a.Received=1000;a.Sent=100;v.Received=1000;v.Sent=100;
        var all=totals.Read(1);
        Assert.Equal(2000,all.Down);Assert.Equal("2 active interfaces"+Note,all.Status);
        a.Received=2000;a.Sent=200;v.Received=5000;v.Sent=500;
        var physical=totals.Read(2,excludeVirtual:true);
        Assert.Equal(1000,physical.Down);Assert.Equal(100,physical.Up);Assert.Equal("1 active interface · 1 virtual adapter excluded",physical.Status);
    }

    [Fact] public void The_adapter_list_is_kept_between_polls_and_listed_again_every_30_seconds()
    {
        int lists=0;var a=new FakeAdapter("a");
        using var totals=new NetworkTotals(()=>{lists++;return [a.Adapter];});
        totals.Read(0);totals.Read(0.5);totals.Read(29.9);
        // Every poll still reads the counters themselves.
        Assert.Equal(1,lists);Assert.Equal(3,a.Reads);
        totals.Read(30);
        Assert.Equal(2,lists);
    }

    [Fact] public void An_address_change_lists_the_adapters_again_on_the_next_read()
    {
        var a=new FakeAdapter("a");var b=new FakeAdapter("b");IReadOnlyList<NetworkAdapter> listed=[a.Adapter];int lists=0;
        using var totals=new NetworkTotals(()=>{lists++;return listed;});
        totals.Read(0);totals.Read(1);
        // A VPN connects: until Windows reports the change, the kept list still has one adapter.
        listed=[a.Adapter,b.Adapter];
        Assert.Equal("1 active interface"+Note,totals.Read(2).Status);
        totals.MarkChanged();
        var after=totals.Read(3);
        Assert.Equal(2,lists);Assert.StartsWith("2 active interfaces",after.Status);
        // The new adapter has no baseline yet, so the total is unknown for one poll rather than missing its traffic.
        Assert.Null(after.Down);
        Assert.NotNull(totals.Read(4).Down);
    }

    [Fact] public void A_gone_adapter_makes_the_poll_unknown_and_the_next_read_lists_again()
    {
        var a=new FakeAdapter("a");var b=new FakeAdapter("b");IReadOnlyList<NetworkAdapter> listed=[a.Adapter,b.Adapter];int lists=0;
        using var totals=new NetworkTotals(()=>{lists++;return listed;});
        totals.Read(0);Assert.NotNull(totals.Read(1).Down);
        b.Gone=true;listed=[a.Adapter];
        var gone=totals.Read(2);
        Assert.Null(gone.Down);Assert.EndsWith("waiting for counter baseline / reset",gone.Status);Assert.Equal(1,lists);
        a.Received=300;
        var next=totals.Read(3);
        Assert.Equal(2,lists);Assert.Equal(300,next.Down);Assert.Equal("1 active interface"+Note,next.Status);
    }

    [Fact] public void An_adapter_that_leaves_the_list_starts_from_a_new_baseline_when_it_returns()
    {
        var a=new FakeAdapter("a");IReadOnlyList<NetworkAdapter> listed=[a.Adapter];
        using var totals=new NetworkTotals(()=>listed);
        totals.Read(0);a.Received=100;Assert.Equal(100,totals.Read(1).Down);
        listed=[];totals.MarkChanged();
        var none=totals.Read(2);
        Assert.Null(none.Down);Assert.Equal("No active network interface",none.Status);
        listed=[a.Adapter];totals.MarkChanged();a.Received=10_000;
        // Not 9,900 bytes over the last second: the gap's traffic is unknown.
        Assert.Null(totals.Read(3).Down);
        a.Received=10_050;Assert.Equal(50,totals.Read(4).Down);
    }

    [Fact] public void A_failed_listing_is_reported_and_tried_again_on_the_next_poll()
    {
        int lists=0;bool fail=true;
        using var totals=new NetworkTotals(()=>{lists++;if(fail)throw new NetworkInformationException(5);return [];});
        var failed=totals.Read(0);
        Assert.Null(failed.Down);Assert.Equal("Interface counters unavailable (NetworkInformationException)",failed.Status);
        fail=false;
        Assert.Equal("No active network interface",totals.Read(0.5).Status);
        Assert.Equal(2,lists);
    }

    [Fact] public void The_real_adapters_read_their_counters_from_a_kept_list()
    {
        // This machine's own adapters, whatever they are; the listing and the counters are the real Windows calls.
        using var totals=new NetworkTotals();
        totals.Read(0);
        var second=totals.Read(1);
        if(second.Status=="No active network interface"){Assert.Null(second.Down);return;}
        Assert.Matches(@"^\d+ active interfaces? · totals include virtual adapters",second.Status);
        if(!second.Status.Contains("waiting",StringComparison.Ordinal)){Assert.True(second.Down>=0);Assert.True(second.Up>=0);}
    }
}
