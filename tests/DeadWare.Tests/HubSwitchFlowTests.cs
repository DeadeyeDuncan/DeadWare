using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Tests;

public class HubSwitchFlowTests
{
    readonly FakeTimeProvider clock=new();
    HubSwitchFlow Flow()=>new(clock);
    static HostState State(string hubState,string? problem=null,HubReading? read=null,string? route=null)
    {
        var devices=route is null?new List<DeviceView>():[new DeviceView("d0","D",1,PaintKind.Direct,null,null,false,[],Hub:route)];
        return new HostState(HostStatus.Running,"",false,[],devices,new LightingSettings(),Hub:new HubView(hubState,Problem:problem,Read:read));
    }
    // A switch click with its read accepted: the flow now waits for the dialog's reading.
    HubSwitchFlow Waiting(out HostState off)
    {
        var flow=Flow();off=State("off");
        flow.Click(off,true);flow.Reply(off,true,null);
        return flow;
    }

    [Fact] public void Switching_on_asks_for_a_reading_first()
    {
        var show=Flow().Click(State("off"),true);
        Assert.Equal(HubSend.Read,show.Send);
        Assert.True(show.On);Assert.False(show.Enabled);Assert.Null(show.Red);Assert.Null(show.Ask);
    }
    [Fact] public void A_refused_reading_shows_why_and_puts_the_switch_back()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);
        var show=flow.Reply(off,false,"hub busy");
        Assert.Equal("DeadWare is still switching. Try again in a moment.",show.Red);
        Assert.False(show.On);Assert.True(show.Enabled);Assert.Equal(HubSend.None,show.Send);
    }
    [Fact] public void A_fresh_reading_opens_the_dialog_once()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);flow.Reply(off,true,null);
        var reading=new HubReading(true,clock.GetUtcNow().AddSeconds(1),new HubReadings([new HubPartReading(1,"pump","Pump",2000,30.0)]));
        clock.Advance(TimeSpan.FromSeconds(1.5));
        var asked=flow.Look(State("off",read:reading));
        Assert.NotNull(asked.Ask);Assert.Same(reading,asked.Ask!.Read);Assert.False(asked.Enabled);Assert.True(asked.On);
        Assert.Null(flow.Look(State("off",read:reading)).Ask);
    }
    [Fact] public void A_reading_taken_before_the_click_is_waited_past()
    {
        var flow=Flow();var off=State("off");
        var old=new HubReading(true,clock.GetUtcNow().AddSeconds(-1));
        flow.Click(off,true);flow.Reply(off,true,null);
        Assert.Null(flow.Look(State("off",read:old)).Ask);
    }
    [Fact] public void No_reading_in_3_s_opens_the_dialog_without_one()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromMilliseconds(2999));
        Assert.Null(flow.Look(off).Ask);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var shown=flow.Look(off);
        Assert.NotNull(shown.Ask);Assert.Null(shown.Ask!.Read);
        Assert.Null(flow.Look(off).Ask);
    }
    [Fact] public void A_reading_with_a_blocker_shows_the_blocked_line_and_no_dialog()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);flow.Reply(off,true,null);
        var blocked=new HubReading(false,clock.GetUtcNow().AddSeconds(1),null,"unknownPart","type 0x1A, model 0x01");
        clock.Advance(TimeSpan.FromSeconds(1.5));
        var shown=flow.Look(State("off",read:blocked));
        Assert.Equal("DeadWare can't take the hub yet: the hub has a part DeadWare doesn't know (type 0x1A, model 0x01). See below for what to do, then try again.",shown.Red);
        Assert.Null(shown.Ask);Assert.False(shown.On);Assert.True(shown.Enabled);
    }
    [Fact] public void Cancel_puts_the_switch_back_and_sends_nothing()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.NotNull(flow.Look(off).Ask);
        var shown=flow.Answer(off,false);
        Assert.Equal(HubSend.None,shown.Send);Assert.False(shown.On);Assert.True(shown.Enabled);Assert.Null(shown.Red);
        Assert.Equal(HubSend.None,flow.Look(off).Send);
    }
    [Fact] public void Take_over_sends_driven_and_a_refusal_shows()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromSeconds(3));flow.Look(off);
        var sent=flow.Answer(off,true);
        Assert.Equal(HubSend.Driven,sent.Send);Assert.True(sent.On);Assert.False(sent.Enabled);
        var refused=flow.Reply(off,false,"hub stale");
        Assert.Equal("That reading was more than two minutes old. Click the switch again.",refused.Red);
        Assert.False(refused.On);Assert.True(refused.Enabled);Assert.Equal(HubSend.None,refused.Send);
    }
    [Fact] public void An_accepted_take_over_goes_idle_and_lets_the_state_speak()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromSeconds(3));flow.Look(off);
        flow.Answer(off,true);
        var shown=flow.Reply(State("switching"),true,null);
        Assert.True(shown.On);Assert.False(shown.Enabled);Assert.Null(shown.Red);Assert.Equal(HubSend.None,shown.Send);
        var driving=flow.Look(State("driving"));
        Assert.True(driving.On);Assert.True(driving.Enabled);
    }
    [Fact] public void Switching_off_sends_openrgb_with_no_dialog()
    {
        var flow=Flow();var driving=State("driving");
        var sent=flow.Click(driving,false);
        Assert.Equal(HubSend.OpenRgb,sent.Send);Assert.Null(sent.Ask);Assert.False(sent.Enabled);Assert.False(sent.On);
        var refused=flow.Reply(driving,false,"hub busy");
        Assert.Equal("DeadWare is still switching. Try again in a moment.",refused.Red);
        Assert.True(refused.On);Assert.True(refused.Enabled);
    }
    [Fact] public void Back_to_OpenRGB_sends_openrgb()
    {
        var sent=Flow().Back(State("gaveBack"));
        Assert.Equal(HubSend.OpenRgb,sent.Send);Assert.Null(sent.Ask);
    }
    [Fact] public void Try_again_waits_for_the_hub_through_OpenRGB_then_reads()
    {
        var flow=Flow();var gaveBack=State("gaveBack");
        Assert.Equal(HubSend.OpenRgb,flow.Retry(gaveBack).Send);
        var accepted=flow.Reply(gaveBack,true,null);
        Assert.Equal(HubSend.None,accepted.Send);Assert.True(accepted.On);Assert.False(accepted.Enabled);
        Assert.Equal(HubSend.None,flow.Look(State("off")).Send);
        Assert.Equal(HubSend.None,flow.Look(State("off",route:"driven")).Send);
        Assert.Equal(HubSend.Read,flow.Look(State("off",route:"openrgb")).Send);
        Assert.Equal(HubSend.None,flow.Look(State("off",route:"openrgb")).Send);
    }
    [Fact] public void After_the_way_back_the_read_goes_on_as_switching_on_does()
    {
        var flow=Flow();var gaveBack=State("gaveBack");
        flow.Retry(gaveBack);flow.Reply(gaveBack,true,null);
        clock.Advance(TimeSpan.FromSeconds(20));
        var back=State("off",route:"openrgb");
        Assert.Equal(HubSend.Read,flow.Look(back).Send);
        flow.Reply(back,true,null);
        var reading=new HubReading(true,clock.GetUtcNow().AddSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Same(reading,flow.Look(State("off",read:reading,route:"openrgb")).Ask!.Read);
    }
    [Fact] public void Try_again_gives_up_after_60_s()
    {
        var flow=Flow();var gaveBack=State("gaveBack");
        flow.Retry(gaveBack);flow.Reply(gaveBack,true,null);
        var off=State("off");
        clock.Advance(TimeSpan.FromMilliseconds(59999));
        Assert.Null(flow.Look(off).Red);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var shown=flow.Look(off);
        Assert.Equal("The hub has not come back through OpenRGB yet. Click the switch when its lights are back.",shown.Red);
        Assert.True(shown.Enabled);Assert.False(shown.On);Assert.Equal(HubSend.None,shown.Send);
    }
    [Fact] public void The_switch_turned_on_in_gave_back_retries_first()
    {
        var shown=Flow().Click(State("gaveBack"),true);
        Assert.Equal(HubSend.OpenRgb,shown.Send);Assert.True(shown.On);
    }
    [Fact] public void A_take_over_that_did_not_finish_shows_why_until_the_next_click()
    {
        var flow=Flow();var interrupted=State("off","icue");
        Assert.Equal("The take-over did not finish (iCUE is running). The switch is off and the hub's lights stay with OpenRGB.",flow.Look(interrupted).Red);
        Assert.Null(flow.Click(interrupted,true).Red);
    }
    [Fact] public void The_flows_own_line_wins_over_the_problem_and_a_click_clears_it()
    {
        var flow=Flow();var interrupted=State("off","icue");
        flow.Click(interrupted,true);
        var shown=flow.Reply(interrupted,false,"hub busy");
        Assert.Equal("DeadWare is still switching. Try again in a moment.",shown.Red);
        Assert.Equal("DeadWare is still switching. Try again in a moment.",flow.Look(interrupted).Red);
        Assert.Null(flow.Click(interrupted,true).Red);
    }
    [Fact] public void The_switch_is_disabled_while_switching_and_without_a_state()
    {
        var flow=Flow();
        Assert.False(flow.Look(State("switching")).Enabled);
        var none=flow.Look(null);
        Assert.False(none.Enabled);Assert.False(none.On);
    }
    [Fact] public void A_click_while_a_sequence_runs_changes_nothing()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);
        var again=flow.Click(off,true);
        Assert.Equal(HubSend.None,again.Send);Assert.True(again.On);Assert.False(again.Enabled);
    }
    [Fact] public void A_click_that_matches_the_state_asks_nothing()
    {
        var flow=Flow();
        Assert.Equal(HubSend.None,flow.Click(State("driving"),true).Send);
        Assert.Equal(HubSend.None,flow.Click(State("off"),false).Send);
        Assert.Equal(HubSend.None,flow.Click(State("switching"),true).Send);
        Assert.Equal(HubSend.None,flow.Click(null,true).Send);
        Assert.Equal(HubSend.None,flow.Back(State("driving")).Send);
        Assert.Equal(HubSend.None,flow.Retry(State("off")).Send);
        Assert.Equal(HubSend.None,flow.Answer(State("off"),true).Send);
    }
    const string StaleLine="That reading was more than two minutes old. Click the switch again.";
    // The reading is taken 1 s after the click and is shown (or refused) 2 min later: exactly 2 min old is shown, 2 min and 1 ms is stale.
    [Theory]
    [InlineData(0,true)]
    [InlineData(1,false)]
    public void A_reading_older_than_two_minutes_is_not_shown_as_just_now(int extraMs,bool opens)
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);flow.Reply(off,true,null);
        var reading=new HubReading(true,clock.GetUtcNow().AddSeconds(1),new HubReadings([new HubPartReading(13,"pump","Pump",2716)]));
        clock.Advance(TimeSpan.FromSeconds(1)+TimeSpan.FromMinutes(2)+TimeSpan.FromMilliseconds(extraMs));
        var shown=flow.Look(State("off",read:reading));
        if(opens){Assert.Same(reading,shown.Ask!.Read);Assert.Null(shown.Red);}
        else{Assert.Null(shown.Ask);Assert.Equal(StaleLine,shown.Red);Assert.False(shown.On);Assert.True(shown.Enabled);}
    }
    // The dialog is open for a while: Take over on a reading that went stale meanwhile is not sent.
    [Theory]
    [InlineData(0,HubSend.Driven)]
    [InlineData(1,HubSend.None)]
    public void Take_over_is_not_sent_on_a_reading_that_went_stale_while_the_dialog_was_open(int extraMs,HubSend sent)
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);flow.Reply(off,true,null);
        var reading=new HubReading(true,clock.GetUtcNow().AddSeconds(1),new HubReadings([new HubPartReading(13,"pump","Pump",2716)]));
        clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.NotNull(flow.Look(State("off",read:reading)).Ask);
        clock.Advance(TimeSpan.FromMinutes(2)-TimeSpan.FromSeconds(0.5)+TimeSpan.FromMilliseconds(extraMs));
        var answer=flow.Answer(off,true);
        Assert.Equal(sent,answer.Send);
        if(sent==HubSend.None){Assert.Equal(StaleLine,answer.Red);Assert.False(answer.On);Assert.True(answer.Enabled);}
    }
    [Fact] public void A_wait_left_unwatched_past_its_limit_ends_without_a_dialog()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromMilliseconds(3001));
        var shown=flow.Look(off);
        Assert.Null(shown.Ask);Assert.Null(shown.Red);Assert.False(shown.On);Assert.True(shown.Enabled);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(flow.Look(off).Ask);
    }
    [Fact] public void A_lost_helper_during_the_wait_ends_it_with_the_not_connected_line()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromSeconds(1));
        var shown=flow.Look(null);
        Assert.Equal("The lighting helper isn't connected.",shown.Red);
        Assert.Null(shown.Ask);Assert.False(shown.On);Assert.False(shown.Enabled);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(flow.Look(off).Ask);
    }
    [Fact] public void A_lost_helper_while_waiting_for_the_way_back_ends_it_too()
    {
        var flow=Flow();var gaveBack=State("gaveBack");
        flow.Retry(gaveBack);flow.Reply(gaveBack,true,null);
        Assert.Equal("The lighting helper isn't connected.",flow.Look(null).Red);
        Assert.Equal(HubSend.None,flow.Look(State("off",route:"openrgb")).Send);
    }
    [Fact] public void A_take_over_nobody_answered_is_watched_not_refused()
    {
        var flow=Waiting(out var off);
        clock.Advance(TimeSpan.FromSeconds(3));flow.Look(off);
        flow.Answer(off,true);
        var shown=flow.Reply(State("switching"),false,"The lighting helper didn't answer.");
        Assert.Null(shown.Red);Assert.True(shown.On);Assert.Equal(HubSend.None,shown.Send);
        Assert.True(flow.Look(State("switching")).On);
        Assert.False(flow.Look(State("switching")).Enabled);
        Assert.True(flow.Look(State("driving")).On);
    }
    [Fact] public void A_read_nobody_answered_still_waits_for_the_reading()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);
        var shown=flow.Reply(off,false,"The lighting helper didn't answer.");
        Assert.Null(shown.Red);Assert.True(shown.On);Assert.False(shown.Enabled);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.NotNull(flow.Look(off).Ask);
    }
    [Fact] public void The_dialog_shows_a_reading_only_when_it_is_a_good_one()
    {
        var good=new HubReading(true,clock.GetUtcNow(),new HubReadings([new HubPartReading(13,"pump","Pump",2716)]));
        Assert.Same(good,new HubAsk(good).Shown);
        Assert.Null(new HubAsk(null).Shown);
        Assert.Null(new HubAsk(new HubReading(false,clock.GetUtcNow())).Shown);
        Assert.Null(new HubAsk(new HubReading(true,clock.GetUtcNow())).Shown);
    }
    [Fact] public void A_reply_with_no_text_still_gives_a_line()
    {
        var flow=Flow();var off=State("off");
        flow.Click(off,true);
        Assert.False(string.IsNullOrEmpty(flow.Reply(off,false,null).Red));
    }
}
