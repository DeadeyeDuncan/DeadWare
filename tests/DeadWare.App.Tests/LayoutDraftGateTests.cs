using Xunit;
namespace DeadWare.App.Tests;

// Fix wave W6: the desk's draft-release rule, moved out of LightingPage. The pipe writes a request's reply before the state that request
// caused, so the draft goes only once a state counted above the last Ok reply's StatesBefore is shown with nothing on its way.
public class LayoutDraftGateTests
{
    [Fact]
    public void Releases_once_a_newer_state_than_the_last_Ok_is_seen_and_only_once()
    {
        var gate=new LayoutDraftGate();
        var t=gate.Sent();
        Assert.True(gate.Holding);
        Assert.False(gate.ShouldRelease(9,false)); // its reply hasn't come
        Assert.Equal(LayoutReplied.Last,gate.Replied(t,true,5));
        Assert.True(gate.Holding);
        Assert.False(gate.ShouldRelease(5,false)); // the state its request caused is still to come
        Assert.True(gate.ShouldRelease(6,false));
        Assert.False(gate.Holding);
        Assert.False(gate.ShouldRelease(7,false));
    }

    [Fact]
    public void Two_sends_hold_until_both_reply()
    {
        var gate=new LayoutDraftGate();
        var a=gate.Sent();var b=gate.Sent();
        Assert.Equal(LayoutReplied.Waiting,gate.Replied(a,true,3));
        Assert.False(gate.ShouldRelease(100,false));
        Assert.True(gate.Holding);
        Assert.Equal(LayoutReplied.Last,gate.Replied(b,true,4));
        Assert.False(gate.ShouldRelease(4,false));
        Assert.True(gate.ShouldRelease(5,false));
    }

    [Fact]
    public void A_send_after_the_last_Ok_holds_again_and_moves_the_mark()
    {
        var gate=new LayoutDraftGate();
        Assert.Equal(LayoutReplied.Last,gate.Replied(gate.Sent(),true,3));
        var next=gate.Sent();
        Assert.False(gate.ShouldRelease(10,false));
        Assert.Equal(LayoutReplied.Last,gate.Replied(next,true,10));
        Assert.False(gate.ShouldRelease(10,false));
        Assert.True(gate.ShouldRelease(11,false));
    }

    [Fact]
    public void A_refusal_makes_the_replies_sent_before_it_stale()
    {
        var gate=new LayoutDraftGate();
        var a=gate.Sent();var b=gate.Sent();
        Assert.Equal(LayoutReplied.Refused,gate.Replied(a,false,3));
        Assert.False(gate.Holding);
        Assert.Equal(LayoutReplied.Stale,gate.Replied(b,true,4));
        Assert.False(gate.ShouldRelease(100,false));
        // A send after the refusal counts as usual.
        Assert.Equal(LayoutReplied.Last,gate.Replied(gate.Sent(),true,7));
        Assert.True(gate.ShouldRelease(8,false));
    }

    [Fact]
    public void Drop_makes_every_outstanding_ticket_stale()
    {
        var gate=new LayoutDraftGate();
        var a=gate.Sent();var b=gate.Sent();
        gate.Drop();
        Assert.False(gate.Holding);
        Assert.Equal(LayoutReplied.Stale,gate.Replied(a,true,1));
        Assert.Equal(LayoutReplied.Stale,gate.Replied(b,false,2));
        Assert.False(gate.ShouldRelease(100,false));
    }

    [Fact]
    public void Drop_forgets_an_awaited_release()
    {
        var gate=new LayoutDraftGate();
        Assert.Equal(LayoutReplied.Last,gate.Replied(gate.Sent(),true,2));
        gate.Drop();
        Assert.False(gate.Holding);
        Assert.False(gate.ShouldRelease(100,false));
    }

    [Fact]
    public void A_nudge_waiting_to_be_sent_blocks_the_release()
    {
        var gate=new LayoutDraftGate();
        Assert.Equal(LayoutReplied.Last,gate.Replied(gate.Sent(),true,2));
        Assert.False(gate.ShouldRelease(3,true));
        Assert.True(gate.Holding);
        Assert.True(gate.ShouldRelease(3,false));
    }
}
