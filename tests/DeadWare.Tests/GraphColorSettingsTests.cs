using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class GraphColorSettingsTests
{
    [Fact] public void Disk_colour_kinds_exist(){ Assert.Contains("diskread",GraphColorSettings.Kinds);Assert.Contains("diskwrite",GraphColorSettings.Kinds);}
    [Fact] public void Core_keys_are_zero_based_plain_decimal(){ Assert.Equal("cpucore/0",GraphColorSettings.CoreKey(0));Assert.Equal("cpucore/63",GraphColorSettings.CoreKey(63));}
    [Theory][InlineData("cpucore/0",0)][InlineData("cpucore/5",5)][InlineData("cpucore/63",63)]
    public void Valid_core_keys_parse(string key,int expected){ Assert.True(GraphColorSettings.TryCoreIndex(key,out int index));Assert.Equal(expected,index);Assert.True(GraphColorSettings.IsKind(key));}
    [Theory][InlineData("cpucore/00")][InlineData("cpucore/64")][InlineData("cpucore/-1")][InlineData("cpucore/x")][InlineData("cpucore/٣")][InlineData("cpucore/")][InlineData("cpucore/+1")][InlineData("cpucore/ 1")][InlineData("cpucore/123")][InlineData("CpuCore/1")][InlineData("cpu")][InlineData("")][InlineData(null)]
    public void Invalid_core_keys_are_rejected(string? key){ Assert.False(GraphColorSettings.TryCoreIndex(key,out int index));Assert.Equal(0,index);}
    [Fact] public void IsKind_accepts_every_fixed_kind_and_rejects_bad_core_keys()
    {
        Assert.All(GraphColorSettings.Kinds,kind=>Assert.True(GraphColorSettings.IsKind(kind)));
        Assert.False(GraphColorSettings.IsKind("cpucore/64"));Assert.False(GraphColorSettings.IsKind("unknown"));Assert.False(GraphColorSettings.IsKind(null));
        Assert.DoesNotContain("cpucore/0",GraphColorSettings.Kinds);
    }
    [Fact] public void Normalize_keeps_valid_core_colors_and_drops_invalid_ones()
    {
        var colors=GraphColorSettings.Normalize(new Dictionary<string,string>{{"cpucore/5","#abcdef"},{"cpucore/64","#123456"},{"cpucore/00","#123456"},{"cpu","#000000"}});
        Assert.Equal(2,colors.Count);Assert.Equal("#ABCDEF",colors["cpucore/5"]);Assert.Equal("#000000",colors["cpu"]);
    }
    [Fact] public void Core_colors_and_styles_roundtrip_through_settings_codec()
    {
        var settings=new UserSettings {GraphColors=new(){{"cpucore/3","#ABCDEF"},{"cpu","#123456"}},GraphStyles=new(){{"cpucore/3",new("dashed",true,2,1.5)}}};
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal("#ABCDEF",loaded.GraphColors["cpucore/3"]);Assert.Equal("#123456",loaded.GraphColors["cpu"]);
        Assert.Equal(new GraphLineStyle("dashed",true,2,1.5),loaded.GraphStyles["cpucore/3"]);
    }

    [Fact]
    public void Every_series_keeps_its_independent_color_after_reopening_settings()
    {
        var settings=SettingsCodec.Read("""
            {"Theme":"Neon","GraphColors":{"cpu":"#123456","cputemp":"#234567","gpu":"#345678","gputemp":"#456789","gpufan":"#89ABCD","memory":"#56789A","download":"#6789AB","upload":"#789ABC"}}
            """).Settings;
        var reopened=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(8,reopened.GraphColors.Count);
        Assert.Equal("#123456",reopened.GraphColors["cpu"]);
        Assert.Equal("#234567",reopened.GraphColors["cputemp"]);
        Assert.Equal("#789ABC",reopened.GraphColors["upload"]);
        Assert.Equal("#89ABCD",reopened.GraphColors["gpufan"]);
        Assert.Equal("#456789",reopened.GraphColors["gputemp"]);
        Assert.Equal("Neon",reopened.Theme);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"GraphColors\":null}")]
    public void Older_or_null_choices_use_theme_defaults(string json)
        =>Assert.Empty(SettingsCodec.Read(json).Settings.GraphColors);

    [Fact]
    public void Invalid_colors_are_discarded_without_losing_other_preferences()
    {
        var settings=SettingsCodec.Read("""
            {"HistorySeconds":300,"Theme":"Daylight","GraphColors":{"cpu":"#aabbcc","gpu":"red","cputemp":"#00112233","gputemp":null,"memory":"#12XZ56","unknown":"#123456","upload":"#FFFFFF"}}
            """).Settings;
        Assert.Equal(2,settings.GraphColors.Count);
        Assert.Equal("#AABBCC",settings.GraphColors["cpu"]);
        Assert.Equal("#FFFFFF",settings.GraphColors["upload"]);
        Assert.Equal(300,settings.HistorySeconds);
        Assert.Equal("Daylight",settings.Theme);
    }
}
