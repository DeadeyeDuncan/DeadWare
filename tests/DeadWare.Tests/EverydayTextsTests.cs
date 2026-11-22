using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 4, Task 10: the Everyday card's wording and the reason line.
public class EverydayTextsTests
{
    static HostState State(bool on,string? reason)=>new(HostStatus.Running,"",false,[],[],new LightingSettings{LightsOn=on},LightsOffReason:reason);

    [Fact]
    public void Idle_names_the_minutes()
    {
        Assert.Equal("Lights are off because the PC has sat unused for 10 minutes. They come back when you touch the mouse or keyboard.",EverydayTexts.Idle(10));
        Assert.Equal("Lights are off because the PC has sat unused for 1 minute. They come back when you touch the mouse or keyboard.",EverydayTexts.Idle(1));
    }

    [Theory]
    [InlineData(null,10,null)]
    [InlineData("idle",5,"Lights are off because the PC has sat unused for 5 minutes. They come back when you touch the mouse or keyboard.")]
    [InlineData("locked",10,"Lights are off because the PC is locked. They come back when you sign in.")]
    [InlineData("waking",10,"Waking up… lights return in a few seconds.")]
    [InlineData("other",10,null)]
    public void The_reason_line_follows_the_reason(string? reason,int minutes,string? expected)=>Assert.Equal(expected,EverydayTexts.ReasonLine(reason,minutes));

    [Fact]
    public void Lit_is_false_with_a_reason_or_the_switch_off()
    {
        Assert.True(EverydayTexts.Lit(State(true,null)));
        Assert.False(EverydayTexts.Lit(State(true,"idle")));
        Assert.False(EverydayTexts.Lit(State(false,null)));
    }

    [Theory]
    [InlineData(true,true,false,false,0x4B,true)]
    [InlineData(false,false,true,false,0x4B,false)]
    [InlineData(true,false,false,true,0x5B,false)]
    [InlineData(false,false,false,true,0x30,true)]
    public void Keys_become_a_shortcut_or_the_invalid_line(bool ctrl,bool alt,bool shift,bool win,int key,bool ok)
    {
        var s=EverydayTexts.FromKeys(ctrl,alt,shift,win,key,out var error);
        if(ok){Assert.NotNull(s);Assert.Null(error);}
        else{Assert.Null(s);Assert.Equal(Shortcuts.InvalidText,error);}
    }

    [Fact]
    public void Ctrl_Alt_K_is_that_shortcut()
    {
        var s=EverydayTexts.FromKeys(true,true,false,false,0x4B,out _);
        Assert.Equal(new Shortcut(Shortcuts.Ctrl|Shortcuts.Alt,0x4B),s);
        Assert.Equal("Ctrl + Alt + K",Shortcuts.Text(s));
    }
}
