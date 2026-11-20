using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class SettingsCodecLightingTests
{
    /// <summary>A path of exactly <paramref name="length"/> characters ending in OpenRGB.exe.</summary>
    static string PathOf(int length)=>@"C:\"+new string('a',length-@"C:\".Length-@"\OpenRGB.exe".Length)+@"\OpenRGB.exe";
    [Fact] public void SettingsCodec_lighting_defaults_off()
    {
        var settings=SettingsCodec.Read("{}").Settings;
        Assert.False(settings.LightingEnabled);Assert.Null(settings.OpenRgbPath);
    }
    [Theory]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe",true)]
    [InlineData(@"C:\Tools\OpenRGB\openrgb.EXE",true)]
    [InlineData(@"C:\Tools\OpenRGB\notepad.exe",false)]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe.bak",false)]
    [InlineData("C:\\Tools\\Open\nRGB\\OpenRGB.exe",false)]
    public void SettingsCodec_drops_unsafe_openrgb_path(string path,bool kept)
    {
        var settings=SettingsCodec.Read(SettingsCodec.Write(new UserSettings{LightingEnabled=true,OpenRgbPath=path})).Settings;
        Assert.True(settings.LightingEnabled);
        Assert.Equal(kept?path:null,settings.OpenRgbPath);
    }
    [Theory][InlineData(260,true)][InlineData(261,false)]
    public void SettingsCodec_keeps_openrgb_paths_up_to_260_characters(int length,bool kept)
    {
        string path=PathOf(length);
        Assert.Equal(length,path.Length);
        Assert.Equal(kept?path:null,SettingsCodec.Read(SettingsCodec.Write(new UserSettings{OpenRgbPath=path})).Settings.OpenRgbPath);
    }
    [Theory]
    [InlineData(null,false)]
    [InlineData("",false)]
    [InlineData("   ",false)]
    [InlineData(@"C:\Tools\OpenRGB\OpenRGB.exe",true)]
    [InlineData(@"C:\Tools\OpenRGB\openrgb.EXE",true)]
    [InlineData(@"C:\Tools\OpenRGB\notepad.exe",false)]
    [InlineData("C:\\Tools\\Open\tRGB\\OpenRGB.exe",false)]
    public void IsSafeOpenRgbPath_is_the_rule_the_codec_applies(string? path,bool safe)
    {
        Assert.Equal(safe,SettingsCodec.IsSafeOpenRgbPath(path));
        Assert.Equal(safe?path:null,SettingsCodec.Read(SettingsCodec.Write(new UserSettings{OpenRgbPath=path})).Settings.OpenRgbPath);
    }
    [Fact] public void IsSafeOpenRgbPath_allows_260_characters_not_261()
    {
        Assert.True(SettingsCodec.IsSafeOpenRgbPath(PathOf(260)));Assert.False(SettingsCodec.IsSafeOpenRgbPath(PathOf(261)));
    }
}
