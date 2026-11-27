using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HubFlowTests
{
    static readonly DateTimeOffset Clicked=new(2026,11,26,12,0,0,TimeSpan.Zero);
    static HubView Viewing(HubReading? read)=>new("off",Read:read);
    static HostState State(string hubState,params string?[] routes)
    {
        var devices=routes.Select((r,i)=>new DeviceView("d"+i,"D",1,PaintKind.Direct,null,null,false,[],Hub:r)).ToList();
        return new HostState(HostStatus.Running,"",false,[],devices,new LightingSettings(),Hub:new HubView(hubState));
    }

    [Fact] public void A_reading_older_than_the_click_is_not_fresh()
    {
        var before=new HubReading(true,Clicked.AddSeconds(-1)); var after=new HubReading(true,Clicked.AddSeconds(1));
        Assert.Null(HubFlow.Fresh(Viewing(before),Clicked));
        Assert.Same(after,HubFlow.Fresh(Viewing(after),Clicked));
        Assert.Null(HubFlow.Fresh(Viewing(null),Clicked));
        Assert.Null(HubFlow.Fresh(null,Clicked));
    }
    [Theory]
    [InlineData("off",true,true)]
    [InlineData("off",false,false)]
    [InlineData("switching",true,false)]
    [InlineData("gaveBack",true,false)]
    [InlineData("driving",true,false)]
    [InlineData("letGo",true,false)]
    public void Back_through_OpenRGB_needs_the_state_off_and_the_hub_listed(string hubState,bool listed,bool expected)
    {
        var state=listed?State(hubState,"openrgb"):State(hubState,null,"driven");
        Assert.Equal(expected,HubFlow.BackThroughOpenRgb(state));
        Assert.False(HubFlow.BackThroughOpenRgb(null));
    }
    [Theory]
    [InlineData("off",false,false)]
    [InlineData("driving",false,true)]
    [InlineData("letGo",false,true)]
    [InlineData("gaveBack",false,false)]
    [InlineData("gaveBack",true,false)]
    [InlineData("switching",true,true)]
    [InlineData("switching",false,false)]
    public void The_switch_follows_the_state(string hubState,bool turningOn,bool expected)=>Assert.Equal(expected,HubFlow.SwitchOn(new HubView(hubState),turningOn));
    [Fact] public void No_state_shows_the_switch_off()=>Assert.False(HubFlow.SwitchOn(null,true));
    [Theory]
    [InlineData("gaveBack",true)]
    [InlineData("off",false)]
    [InlineData("driving",false)]
    [InlineData("letGo",false)]
    [InlineData("switching",false)]
    public void Only_gave_back_retries_first(string hubState,bool expected)
    {
        Assert.Equal(expected,HubFlow.RetryFirst(new HubView(hubState)));
        Assert.False(HubFlow.RetryFirst(null));
    }
}
