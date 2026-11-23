using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

// Lighting 4, Task 10: the tray menu's lights line.
public class TrayLightsTests
{
    static HostState State(HostStatus status,bool on,string? reason=null)=>new(status,"",false,[],[],new LightingSettings{LightsOn=on},LightsOffReason:reason);

    [Fact]
    public void Lighting_disabled_is_unavailable()=>Assert.Equal((TrayLights.Unavailable,false,false),TrayLights.Line(false,State(HostStatus.Running,true),null));
    [Fact]
    public void No_state_is_unavailable()=>Assert.Equal((TrayLights.Unavailable,false,false),TrayLights.Line(true,null,null));
    [Fact]
    public void A_client_problem_is_unavailable()=>Assert.Equal((TrayLights.Unavailable,false,false),TrayLights.Line(true,State(HostStatus.Running,true),"pipe refused"));
    // Fix wave 1, F6 (spec line 126): a connected helper takes the switch whatever OpenRGB is doing, as the page's switch does, so the line
    // is live while OpenRGB starts, waits, is blocked, missing or failed.
    [Theory]
    [InlineData(HostStatus.Starting)]
    [InlineData(HostStatus.Waiting)]
    [InlineData(HostStatus.Blocked)]
    [InlineData(HostStatus.OpenRgbMissing)]
    [InlineData(HostStatus.OpenRgbFailed)]
    public void A_connected_host_that_is_not_running_is_live(HostStatus status)
    {
        Assert.Equal((TrayLights.Off,true,false),TrayLights.Line(true,State(status,true),null));
        Assert.Equal((TrayLights.On,true,true),TrayLights.Line(true,State(status,false),null));
    }
    [Fact]
    public void Running_with_the_switch_on_offers_off()=>Assert.Equal((TrayLights.Off,true,false),TrayLights.Line(true,State(HostStatus.Running,true),null));
    [Fact]
    public void Running_with_the_switch_off_offers_on()=>Assert.Equal((TrayLights.On,true,true),TrayLights.Line(true,State(HostStatus.Running,false),null));
    [Fact]
    public void The_label_follows_the_switch_during_an_idle_spell()=>Assert.Equal((TrayLights.Off,true,false),TrayLights.Line(true,State(HostStatus.Running,true,LightsOff.Idle),null));
}
