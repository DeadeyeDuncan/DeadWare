using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightsOffTests
{
    static LightingSettings On(Action<EverydaySettings>? e=null)
    {
        var s=new LightingSettings{LightsOn=true};
        e?.Invoke(s.Everyday);
        return s;
    }
    [Fact] public void Defaults_never_turn_lights_off()
        =>Assert.Null(LightsOff.Reason(On(),long.MaxValue,true,false));
    [Fact] public void Waking_then_locked_then_idle_is_the_order()
    {
        var s=On(e=>{e.LockOff=true;e.IdleOff=true;e.IdleMinutes=1;});
        Assert.Equal(LightsOff.Waking,LightsOff.Reason(s,10*60_000,true,true));
        Assert.Equal(LightsOff.Locked,LightsOff.Reason(s,10*60_000,true,false));
        Assert.Equal(LightsOff.Idle,LightsOff.Reason(s,10*60_000,false,false));
    }
    [Theory] [InlineData(119_999L,null)] [InlineData(120_000L,"idle")]
    public void Idle_starts_at_exactly_the_chosen_minutes(long ms,string? want)
        =>Assert.Equal(want,LightsOff.Reason(On(e=>{e.IdleOff=true;e.IdleMinutes=2;}),ms,false,false));
    [Fact] public void Lock_needs_its_rule_on()
    {
        Assert.Null(LightsOff.Reason(On(e=>e.LockOff=false),0,true,false));
        Assert.Equal(LightsOff.Locked,LightsOff.Reason(On(e=>e.LockOff=true),0,true,false));
    }
    [Fact] public void Idle_needs_its_rule_on()
    {
        Assert.Null(LightsOff.Reason(On(e=>{e.IdleOff=false;e.IdleMinutes=1;}),10*60_000,false,false));
        Assert.Equal(LightsOff.Idle,LightsOff.Reason(On(e=>{e.IdleOff=true;e.IdleMinutes=1;}),10*60_000,false,false));
    }
    [Fact] public void The_switch_off_gives_no_reason()
    {
        var s=On(e=>{e.LockOff=true;e.IdleOff=true;e.IdleMinutes=1;});
        s.LightsOn=false;
        Assert.Null(LightsOff.Reason(s,10*60_000,true,true));
    }
    [Fact] public void Effective_needs_the_switch_and_no_reason()
    {
        var s=On();
        Assert.True(LightsOff.Effective(s,null));
        Assert.False(LightsOff.Effective(s,LightsOff.Idle));
        s.LightsOn=false;
        Assert.False(LightsOff.Effective(s,null));
    }
    [Fact] public void A_press_during_an_idle_spell_only_brings_lights_back()
        =>Assert.True(LightsOff.ShortcutTarget(true,true));
    [Fact] public void A_press_while_locked_or_waking_flips_the_switch()
        =>Assert.False(LightsOff.ShortcutTarget(true,false));
    [Fact] public void Five_presses_alternate()
    {
        var on=true;var seen=new List<bool>();
        for(var i=0;i<5;i++){on=LightsOff.ShortcutTarget(on,false);seen.Add(on);}
        Assert.Equal([false,true,false,true,false],seen);
    }
}
