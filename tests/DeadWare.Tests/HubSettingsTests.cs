using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HubSettingsTests
{
    const string Invalid="The saved hub mode isn't one DeadWare knows.";
    static HubSettings Driven(params (int c,int t,int m)[] parts)=>new(){Mode="driven",Parts=parts.Select(p=>new HubSavedPart{Channel=p.c,Type=p.t,Model=p.m}).ToList()};
    static HubSettings Stopped(Action<HubStop> edit)
    {
        var stop=new HubStop{Kind="slower",Part="pump",Number=1,Before=2716,After=2010};
        edit(stop);
        return new(){Mode="stopped",Stop=stop};
    }

    [Fact]
    public void The_default_mode_is_openrgb_and_counts_as_default()
    {
        var h=new HubSettings();
        Assert.Equal("openrgb",h.Mode);Assert.Null(h.Stop);Assert.Null(h.Parts);
        Assert.True(Hubs.IsDefault(h));Assert.Null(Hubs.Validate(h));
        Assert.False(Hubs.IsDefault(new(){Mode="driven"}));
    }
    [Theory]
    [InlineData("openrgb",true)]
    [InlineData("driven",true)]
    [InlineData("stopped",true)]
    [InlineData("Driven",false)]
    [InlineData("",false)]
    [InlineData(null,false)]
    [InlineData("leaveAlone",false)]
    public void Only_the_three_modes_are_known(string? mode,bool known)=>Assert.Equal(known,HubModes.Known(mode));
    [Fact]
    public void Stopped_needs_a_stop_and_the_other_modes_refuse_one()
    {
        Assert.Equal(Invalid,Hubs.Validate(new(){Mode="stopped"}));
        Assert.Equal(Invalid,Hubs.Validate(new(){Mode="driven",Stop=new()}));
        Assert.Equal(Invalid,Hubs.Validate(new(){Mode="openrgb",Stop=new()}));
        Assert.Null(Hubs.Validate(Stopped(_=>{})));
    }
    [Theory]
    [InlineData("kind")]
    [InlineData("part")]
    [InlineData("number0")]
    [InlineData("number65")]
    [InlineData("before-1")]
    [InlineData("after100001")]
    public void A_stop_outside_its_ranges_is_refused(string what)
    {
        var h=Stopped(s=>
        {
            switch(what)
            {
                case "kind":s.Kind="faster";break;
                case "part":s.Part="radiator";break;
                case "number0":s.Number=0;break;
                case "number65":s.Number=65;break;
                case "before-1":s.Before=-1;break;
                case "after100001":s.After=100001;break;
            }
        });
        Assert.Equal(Invalid,Hubs.Validate(h));
    }
    [Fact]
    public void A_stop_at_its_edges_is_accepted()
    {
        Assert.Null(Hubs.Validate(Stopped(s=>{s.Kind="noReading";s.Part="fan";s.Number=64;s.Before=null;s.After=null;})));
        Assert.Null(Hubs.Validate(Stopped(s=>{s.Kind="pumpStopped";s.Before=0;s.After=100000;})));
    }
    [Fact]
    public void Clone_copies_the_stop_and_shares_nothing()
    {
        var h=Stopped(_=>{});h.Parts=new(){new(){Channel=1,Type=15,Model=0},new(){Channel=13,Type=17,Model=2}};
        var c=Hubs.Clone(h);
        Assert.Equal("stopped",c.Mode);Assert.Equal("slower",c.Stop!.Kind);Assert.Equal("pump",c.Stop.Part);Assert.Equal(1,c.Stop.Number);
        Assert.Equal(2716,c.Stop.Before);Assert.Equal(2010,c.Stop.After);
        Assert.NotSame(h.Stop,c.Stop);Assert.NotSame(h.Parts,c.Parts);Assert.NotSame(h.Parts[0],c.Parts![0]);Assert.NotSame(h.Parts[1],c.Parts[1]);
        h.Stop!.Number=9;h.Parts[1].Model=7;h.Parts.RemoveAt(0);
        Assert.Equal(1,c.Stop.Number);Assert.Equal(2,c.Parts.Count);Assert.Equal(2,c.Parts[1].Model);
        Assert.Null(Hubs.Clone(new()).Stop);Assert.Null(Hubs.Clone(new()).Parts);
    }
    [Theory]
    [InlineData("count65")]
    [InlineData("channel0")]
    [InlineData("channel256")]
    [InlineData("same")]
    [InlineData("descending")]
    [InlineData("type-1")]
    [InlineData("type256")]
    [InlineData("model-1")]
    [InlineData("model256")]
    public void Parts_outside_their_ranges_are_refused(string what)
    {
        var h=what switch
        {
            "count65"=>new HubSettings{Mode="driven",Parts=Enumerable.Range(1,65).Select(i=>new HubSavedPart{Channel=i}).ToList()},
            "channel0"=>Driven((0,15,0)),
            "channel256"=>Driven((256,15,0)),
            "same"=>Driven((3,15,0),(3,15,0)),
            "descending"=>Driven((3,15,0),(2,15,0)),
            "type-1"=>Driven((1,-1,0)),
            "type256"=>Driven((1,256,0)),
            "model-1"=>Driven((1,15,-1)),
            _=>Driven((1,15,256)),
        };
        Assert.Equal(Invalid,Hubs.Validate(h));
    }
    [Fact]
    public void Parts_at_their_edges_and_the_owners_four_are_accepted()
    {
        Assert.Null(Hubs.Validate(Driven((1,15,0),(2,15,0),(3,15,0),(13,17,2))));
        Assert.Null(Hubs.Validate(Driven((255,255,255))));
        Assert.Null(Hubs.Validate(new HubSettings{Mode="driven",Parts=Enumerable.Range(1,64).Select(i=>new HubSavedPart{Channel=i}).ToList()}));
        Assert.Null(Hubs.Validate(new(){Mode="driven",Parts=null}));
    }
    [Fact]
    public void Parts_in_mode_openrgb_are_refused()
    {
        var h=new HubSettings{Mode="openrgb",Parts=new(){new(){Channel=1,Type=15,Model=0}}};
        Assert.Equal(Invalid,Hubs.Validate(h));
        Assert.False(Hubs.IsDefault(h));
    }
}
