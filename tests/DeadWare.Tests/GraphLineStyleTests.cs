using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class GraphLineStyleTests
{
    [Fact] public void Styles_roundtrip_independently_of_colors_and_other_traces()
    {
        var settings=new UserSettings {GraphStyles=new(){{"cpu",new("dotted",true)},{"upload",new("solid")}},GraphColors=new(){{"cpu","#AABBCC"}}};
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(new("dotted",true),loaded.GraphStyles["cpu"]);Assert.Equal(new("solid"),loaded.GraphStyles["upload"]);
        Assert.False(loaded.GraphStyles.ContainsKey("gpu"));Assert.Equal("#AABBCC",loaded.GraphColors["cpu"]);
    }
    [Theory][InlineData("{}")][InlineData("{\"GraphStyles\":null}")]
    public void Old_settings_keep_default_line_styles(string json)=>Assert.Empty(SettingsCodec.Read(json).Settings.GraphStyles);
    [Fact] public void Invalid_styles_are_normalized_without_losing_other_settings()
    {
        var loaded=SettingsCodec.Read("""{"HistorySeconds":3600,"GraphStyles":{"cpu":{"Pattern":"oops","Glow":true},"gpu":null,"unknown":{"Pattern":"dashed"},"memory":{"Pattern":"default"}}} """).Settings;
        Assert.Equal(3600,loaded.HistorySeconds);Assert.Equal(new("default",true),Assert.Single(loaded.GraphStyles).Value);
    }
}
