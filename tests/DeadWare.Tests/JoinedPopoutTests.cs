using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;

public class JoinedPopoutTests
{
    [Fact] public void ExistingSettingsKeepSeparatePopoutsByDefault()
    {
        Assert.False(new UserSettings().JoinCpuGpuPopouts);
        Assert.False(SettingsCodec.Read("{}").Settings.JoinCpuGpuPopouts);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaunchChoiceSurvivesSettingsRoundtrip(bool joined)
    {
        var settings=new UserSettings {JoinCpuGpuPopouts=joined,WidgetAlwaysOnTop=false};
        var roundtrip=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(joined,roundtrip.JoinCpuGpuPopouts);
        Assert.False(roundtrip.WidgetAlwaysOnTop);
    }
    [Theory]
    [InlineData("cpu",false,"cpu")]
    [InlineData("gpu",false,"gpu")]
    [InlineData("cputemp",false,"cputemp")]
    [InlineData("gputemp",false,"gputemp")]
    [InlineData("cpu",true,"usage")]
    [InlineData("gpu",true,"usage")]
    [InlineData("cputemp",true,"temperature")]
    [InlineData("gputemp",true,"temperature")]
    public void NewDeviceGraphLaunchesFollowChoice(string requested,bool joined,string expected)
        =>Assert.Equal(expected,PopoutGraphMode.Resolve(requested,joined));

    [Theory]
    [InlineData("usage")]
    [InlineData("temperature")]
    [InlineData("memory")]
    [InlineData("network")]
    [InlineData("widget")]
    [InlineData("batteryhistory")]
    [InlineData("unknown")]
    [InlineData("CPU")]
    public void OtherLaunchTargetsStayUnchanged(string requested)
    {
        Assert.Equal(requested,PopoutGraphMode.Resolve(requested,false));
        Assert.Equal(requested,PopoutGraphMode.Resolve(requested,true));
    }
    [Fact] public void JoinedWindowTypesSurviveRoundtripAndInvalidTypesAreFiltered()
    {
        var read=SettingsCodec.Read("""{"JoinCpuGpuPopouts":true,"OpenWindows":["usage","temperature","usage",null,"unknown","cpu","widget"]}""");
        var roundtrip=SettingsCodec.Read(SettingsCodec.Write(read.Settings)).Settings;
        Assert.Equal(new[]{"usage","temperature","cpu","widget"},roundtrip.OpenWindows);
    }
    [Fact] public void MatchingLaunchesShareOneWindowAndClosingForgetsItsExactKey()
    {
        var state=new DetachedWindowState([]);
        state.Open(PopoutGraphMode.Resolve("cpu",true));
        state.Open(PopoutGraphMode.Resolve("gpu",true));
        state.Open(PopoutGraphMode.Resolve("cputemp",true));
        state.Open(PopoutGraphMode.Resolve("gputemp",true));
        Assert.Equal(new[]{"usage","temperature"},state.Remembered);
        state.Close("usage");
        var restored=new DetachedWindowState(state.Remembered);
        Assert.Equal(new[]{"temperature"},restored.Remembered);
    }
    [Fact] public void RememberedSoloAndJoinedWindowsRemainIndependent()
    {
        var state=new DetachedWindowState(["cpu","gputemp","usage","temperature"]);
        Assert.Equal(new[]{"cpu","gputemp","usage","temperature"},state.Remembered);
        state.Close("usage");
        Assert.Equal(new[]{"cpu","gputemp","temperature"},state.Remembered);
        state.BeginShutdown();state.Close("temperature");state.Open("usage");
        Assert.Equal(new[]{"cpu","gputemp","temperature"},state.Remembered);
    }
}
