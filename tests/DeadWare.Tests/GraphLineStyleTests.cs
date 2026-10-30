using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class GraphLineStyleTests
{
    [Fact] public void Styles_roundtrip_independently_of_colors_and_other_traces()
    {
        var settings=new UserSettings {GraphStyles=new(){{"cpu",new("dotted",true)},{"upload",new("solid")},{"gpufan",new("dashed",true,2,2.5)},{"gputemp",new("solid",false,.5,0)}},GraphColors=new(){{"cpu","#AABBCC"},{"gpufan","#B69CFF"}}};
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(new("dotted",true),loaded.GraphStyles["cpu"]);Assert.Equal(new("solid"),loaded.GraphStyles["upload"]);
        Assert.False(loaded.GraphStyles.ContainsKey("gpu"));Assert.Equal("#AABBCC",loaded.GraphColors["cpu"]);
        Assert.Equal(new("dashed",true,2,2.5),loaded.GraphStyles["gpufan"]);Assert.Equal("#B69CFF",loaded.GraphColors["gpufan"]);
        Assert.Equal(new("solid",false,.5,0),loaded.GraphStyles["gputemp"]);
    }
    [Theory][InlineData("{}")][InlineData("{\"GraphStyles\":null}")]
    public void Old_settings_keep_default_line_styles(string json)=>Assert.Empty(SettingsCodec.Read(json).Settings.GraphStyles);
    [Fact] public void Invalid_styles_are_normalized_without_losing_other_settings()
    {
        var loaded=SettingsCodec.Read("""{"HistorySeconds":3600,"GraphStyles":{"cpu":{"Pattern":"oops","Glow":true},"gpu":null,"unknown":{"Pattern":"dashed"},"memory":{"Pattern":"default"}}} """).Settings;
        Assert.Equal(3600,loaded.HistorySeconds);Assert.Equal(new("default",true),Assert.Single(loaded.GraphStyles).Value);
    }
    [Fact] public void Stroke_controls_roundtrip_and_old_styles_keep_original_strength()
    {
        var settings=SettingsCodec.Read("""{"GraphStyles":{"cpu":{"Pattern":"dotted","Glow":true,"Thickness":2.5,"GlowStrength":2},"upload":{"Pattern":"solid","Glow":true}}}""").Settings;
        using var saved=System.Text.Json.JsonDocument.Parse(SettingsCodec.Write(settings));
        var styles=saved.RootElement.GetProperty("GraphStyles");
        Assert.Equal(2.5,styles.GetProperty("cpu").GetProperty("Thickness").GetDouble());
        Assert.Equal(2,styles.GetProperty("cpu").GetProperty("GlowStrength").GetDouble());
        Assert.Equal(1,styles.GetProperty("upload").GetProperty("Thickness").GetDouble());
        Assert.Equal(1,styles.GetProperty("upload").GetProperty("GlowStrength").GetDouble());
    }
    [Fact] public void Stroke_controls_bound_invalid_values()
    {
        Assert.Equal(new GraphLineStyle("solid",true,.5,3),GraphLineStyles.Normalize(new GraphLineStyle("solid",true,-2,100)));
        Assert.Equal(new GraphLineStyle("default",false,3,0),GraphLineStyles.Normalize(new GraphLineStyle("oops",false,100,-1)));
        Assert.Equal(new GraphLineStyle(),GraphLineStyles.Normalize(new GraphLineStyle(Thickness:double.NaN,GlowStrength:double.PositiveInfinity)));
    }
    [Fact] public void Custom_thickness_and_disabled_glow_strength_are_preserved()
    {
        var settings=new UserSettings {GraphStyles=new(){{"cpu",new(Thickness:2,GlowStrength:2.5)}}};
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(settings.GraphStyles["cpu"],loaded.GraphStyles["cpu"]);
    }
}
