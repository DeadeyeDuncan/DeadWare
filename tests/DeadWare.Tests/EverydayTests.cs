using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class EverydayTests
{
    [Fact]
    public void The_default_shortcut_reads_Ctrl_Alt_L()=>Assert.Equal("Ctrl + Alt + L",Shortcuts.Text(Shortcuts.Default));

    [Fact]
    public void No_shortcut_reads_None()=>Assert.Equal("None",Shortcuts.Text(null));

    [Fact]
    public void Modifiers_read_in_the_order_Ctrl_Alt_Shift_Win()=>Assert.Equal("Ctrl + Alt + Shift + Win + F5",Shortcuts.Text(new(Shortcuts.Win|Shortcuts.Shift|Shortcuts.Alt|Shortcuts.Ctrl,0x74)));

    [Theory]
    [InlineData(Shortcuts.Shift,0x4C,false)]
    [InlineData(0,0x4C,false)]
    [InlineData(Shortcuts.Ctrl,0x5B,false)]
    [InlineData(Shortcuts.Ctrl|16,0x4C,false)]
    [InlineData(Shortcuts.Win,0x87,true)]
    [InlineData(Shortcuts.Ctrl|Shortcuts.Alt,0x4C,true)]
    public void A_shortcut_needs_Ctrl_Alt_or_Win_and_a_table_key(int modifiers,int key,bool valid)=>Assert.Equal(valid,Shortcuts.Valid(new(modifiers,key)));

    [Fact]
    public void Taken_text_names_the_keys()=>Assert.Equal("Another program already uses Ctrl + Alt + P. Choose different keys.",Shortcuts.TakenText(new(Shortcuts.Ctrl|Shortcuts.Alt,0x50)));

    [Fact]
    public void The_key_table_names_its_keys()
    {
        Assert.Equal("A",Shortcuts.KeyName(0x41));Assert.Equal("Z",Shortcuts.KeyName(0x5A));Assert.Equal("0",Shortcuts.KeyName(0x30));
        Assert.Equal("F1",Shortcuts.KeyName(0x70));Assert.Equal("F24",Shortcuts.KeyName(0x87));Assert.Equal("Page Down",Shortcuts.KeyName(0x22));
        Assert.Null(Shortcuts.KeyName(0x5B));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(61)]
    public void Idle_minutes_off_the_list_are_refused(int minutes)=>Assert.Equal(HostReplyCodes.BadEveryday,Everydays.Validate(new EverydaySettings{IdleMinutes=minutes}));

    [Fact]
    public void Every_idle_choice_is_valid()
    {
        foreach(var m in Everydays.IdleChoices) Assert.Null(Everydays.Validate(new EverydaySettings{IdleMinutes=m}));
    }

    [Fact]
    public void An_everyday_with_a_bad_shortcut_is_not_valid()=>Assert.Equal(HostReplyCodes.BadEveryday,Everydays.Validate(new EverydaySettings{Shortcut=new(Shortcuts.Shift,0x4C)}));

    [Theory]
    [InlineData(null,true)]
    [InlineData(0,true)]
    [InlineData(20,true)]
    [InlineData(4,false)]
    [InlineData(21,false)]
    [InlineData(-1,false)]
    public void Sleep_minutes_must_be_on_the_list_or_unset(int? minutes,bool valid)=>Assert.Equal(valid,MouseSleep.Valid(minutes));

    [Fact]
    public void Everyday_defaults_are_defaults_and_a_clone_is_deep()
    {
        var e=new EverydaySettings();
        Assert.True(Everydays.IsDefault(e));
        var c=Everydays.Clone(e);c.IdleOff=true;
        Assert.False(e.IdleOff);Assert.False(Everydays.IsDefault(c));
        Assert.False(Everydays.IsDefault(new EverydaySettings{Shortcut=null}));
        Assert.False(Everydays.IsDefault(new EverydaySettings{IdleMinutes=30}));
    }
}
