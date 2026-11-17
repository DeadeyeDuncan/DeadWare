using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class SettingsCodecLightingTests
{
    [Fact] public void SettingsCodec_lighting_defaults_off()
    {
        var settings=SettingsCodec.Read("{}").Settings;
        Assert.False(settings.LightingEnabled);Assert.Null(settings.OpenRgbPath);
    }
    [Theory]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe",true)]
    [InlineData(@"C:\Tools\OpenRGB\notepad.exe",false)]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe.bak",false)]
    [InlineData("C:\\Tools\\Open\nRGB\\OpenRGB.exe",false)]
    public void SettingsCodec_drops_unsafe_openrgb_path(string path,bool kept)
    {
        var settings=SettingsCodec.Read(SettingsCodec.Write(new UserSettings{LightingEnabled=true,OpenRgbPath=path})).Settings;
        Assert.True(settings.LightingEnabled);
        Assert.Equal(kept?path:null,settings.OpenRgbPath);
    }
    [Fact] public void SettingsCodec_drops_overlong_openrgb_path()
    {
        string path=@"C:\"+new string('a',260)+@"\OpenRGB.exe";
        Assert.Null(SettingsCodec.Read(SettingsCodec.Write(new UserSettings{OpenRgbPath=path})).Settings.OpenRgbPath);
    }
}
