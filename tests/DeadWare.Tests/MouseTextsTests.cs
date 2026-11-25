using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 4, Task 10: the mouse card's wording, one row per line of the spec's table.
public class MouseTextsTests
{
    static MouseView M(string c,int? battery=null,bool? charging=null,bool stale=false,int? sleep=null,bool sent=false)=>new(c,battery,charging,stale,sleep,sent);

    [Theory]
    [InlineData(MouseConnections.Wireless,85,null,false,"Wireless · Battery 85%","85%","On battery. Read while you use the mouse.")]
    [InlineData(MouseConnections.Wireless,null,null,false,"Wireless","Battery unknown","On battery. Read while you use the mouse.")]
    [InlineData(MouseConnections.Cable,60,true,false,"On its cable · Battery 60% · Charging","60%","Charging. Read every 10 seconds on the cable.")]
    [InlineData(MouseConnections.Cable,100,true,false,"On its cable · Battery 100% · Charging","100%","Fully charged.")]
    [InlineData(MouseConnections.Cable,60,false,false,"On its cable · Battery 60%","60%","Not charging.")]
    [InlineData(MouseConnections.Cable,null,true,false,"On its cable · Charging","Battery unknown","Charging. Read every 10 seconds on the cable.")]
    [InlineData(MouseConnections.Receiver,85,null,true,"Receiver plugged in · mouse asleep or out of reach","85% · last known","Last known reading. Move the mouse to refresh it.")]
    [InlineData(MouseConnections.Receiver,null,null,true,"Receiver plugged in · mouse asleep or out of reach","Battery unknown","Last known reading. Move the mouse to refresh it.")]
    // Fix wave 1, F4: a reading under two minutes old from a mouse that stopped answering is last known too (the row follows answering).
    [InlineData(MouseConnections.Receiver,85,null,false,"Receiver plugged in · mouse asleep or out of reach","85% · last known","Last known reading. Move the mouse to refresh it.")]
    [InlineData(MouseConnections.Receiver,null,null,false,"Receiver plugged in · mouse asleep or out of reach","Battery unknown","Last known reading. Move the mouse to refresh it.")]
    [InlineData(MouseConnections.None,null,null,false,"Not connected","Battery unknown","Plug in the receiver or the cable.")]
    [InlineData(MouseConnections.None,85,null,true,"Not connected","Battery unknown","Plug in the receiver or the cable.")]
    public void The_table_rows(string connection,int? battery,bool? charging,bool stale,string sub,string line,string hint)
    {
        var m=M(connection,battery,charging,stale);
        Assert.Equal(sub,MouseTexts.SubLine(m));
        Assert.Equal(line,MouseTexts.BatteryLine(m));
        Assert.Equal(hint,MouseTexts.Hint(m));
    }

    [Theory]
    [InlineData(null,"Leave as the mouse is")]
    [InlineData(0,"Never")]
    [InlineData(1,"1 minute")]
    [InlineData(2,"2 minutes")]
    [InlineData(20,"20 minutes")]
    public void Sleep_choices(int? minutes,string text)=>Assert.Equal(text,MouseTexts.SleepChoice(minutes));

    [Theory]
    [InlineData(MouseConnections.Wireless,null,false,"DeadWare has not changed the mouse's own sleep time.")]
    [InlineData(MouseConnections.Wireless,5,true,"Saved and sent to the mouse.")]
    [InlineData(MouseConnections.Cable,5,false,"Saved. It is sent over the wireless receiver, so it reaches the mouse next time you use it wireless.")]
    [InlineData(MouseConnections.Receiver,5,false,"Saved. Waiting for the mouse.")]
    [InlineData(MouseConnections.None,0,false,"Saved. Waiting for the mouse.")]
    public void Sleep_status(string connection,int? minutes,bool sent,string text)=>Assert.Equal(text,MouseTexts.SleepStatus(M(connection,sleep:minutes,sent:sent)));

    [Theory]
    [InlineData(null,false)]
    [InlineData(0,false)]
    [InlineData(1,true)]
    [InlineData(20,true)]
    public void The_note_shows_for_a_chosen_time(int? minutes,bool shown)=>Assert.Equal(shown,MouseTexts.ShowNote(M(MouseConnections.Wireless,sleep:minutes)));

    [Theory]
    [InlineData(MouseConnections.None,true)]
    [InlineData(MouseConnections.Receiver,false)]
    [InlineData(MouseConnections.Cable,false)]
    [InlineData(MouseConnections.Wireless,false)]
    public void Only_a_missing_mouse_is_dimmed_and_unlit(string connection,bool absent)
    {
        Assert.Equal(absent,MouseTexts.Dimmed(M(connection)));
        Assert.Equal(absent,MouseTexts.Unlit(new DeviceView("k","n",0,PaintKind.Direct,null,null,false,[],Mouse:M(connection))));
    }

    [Fact]
    public void A_device_without_mouse_data_is_not_unlit()=>Assert.False(MouseTexts.Unlit(new DeviceView("k","n",0,PaintKind.Direct,null,null,false,[])));
}
