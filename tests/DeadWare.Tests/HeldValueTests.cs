using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HeldValueTests
{
    [Fact] public void Nothing_asked_shows_the_state()
    {
        var h=new HeldValue<string>();
        Assert.False(h.Held);
        Assert.Equal("state",h.Current("state"));
    }

    [Fact] public void An_ask_shows_at_once_even_a_null()
    {
        var h=new HeldValue<string>();
        h.Ask("mine");
        Assert.True(h.Held);
        Assert.Equal("mine",h.Current("state"));
        h.Ask(null);
        Assert.Null(h.Current("state"));
    }

    [Fact] public void Held_until_the_reply_and_a_later_state()
    {
        var h=new HeldValue<string>();
        h.Ask("mine");h.Sent();
        h.Settle(shown:5,waiting:false);
        Assert.True(h.Held); // on its way
        h.Replied(ok:true,statesBefore:7);
        h.Settle(shown:7,waiting:false);
        Assert.True(h.Held); // the state at the reply doesn't carry it yet
        h.Settle(shown:8,waiting:false);
        Assert.False(h.Held);
        Assert.Equal("state",h.Current("state"));
    }

    [Fact] public void Something_waiting_or_a_second_send_holds_it()
    {
        var h=new HeldValue<string>();
        h.Ask("a");h.Sent();h.Ask("b");h.Sent();
        h.Replied(true,3);
        h.Settle(9,false);
        Assert.True(h.Held);
        h.Replied(true,4);
        h.Settle(9,waiting:true);
        Assert.True(h.Held);
        h.Settle(9,false);
        Assert.False(h.Held);
    }

    [Fact] public void The_latest_reply_counts_for_the_settle()
    {
        var h=new HeldValue<string>();
        h.Ask("a");h.Sent();h.Sent();
        h.Replied(true,10);h.Replied(true,4);
        h.Settle(10,false);
        Assert.True(h.Held);
        h.Settle(11,false);
        Assert.False(h.Held);
    }

    [Fact] public void A_refusal_drops_it_at_once()
    {
        var h=new HeldValue<string>();
        h.Ask("mine");h.Sent();
        h.Replied(ok:false,statesBefore:0);
        Assert.False(h.Held);
        Assert.Equal("state",h.Current("state"));
    }

    [Fact] public void Reset_forgets_everything()
    {
        var h=new HeldValue<string>();
        h.Ask("a");h.Sent();h.Replied(true,50);
        h.Reset();
        Assert.False(h.Held);
        h.Ask("b");h.Sent();h.Replied(true,1);
        h.Settle(2,false);
        Assert.False(h.Held); // the old reply's count is gone
    }
}
