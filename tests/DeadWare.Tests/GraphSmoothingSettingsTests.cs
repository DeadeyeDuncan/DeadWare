using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class GraphSmoothingSettingsTests
{
    [Fact] public void Legacy_settings_keep_long_range_smoothing_enabled()
    {
        var loaded=SettingsCodec.Read("""{"Theme":"Blackout","HistorySeconds":3600}""");
        Assert.True(new UserSettings().GraphSmoothing);Assert.True(loaded.Settings.GraphSmoothing);
        Assert.Equal("Blackout",loaded.Settings.Theme);Assert.Equal(3600,loaded.Settings.HistorySeconds);Assert.Null(loaded.Error);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Explicit_smoothing_preference_survives_settings_roundtrip(bool enabled)
    {
        var settings=new UserSettings {GraphSmoothing=enabled,GraphStyles=new(){{"cpu",new("dotted",true,1.5,2)}},HistorySeconds=10800};
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings));
        Assert.Equal(enabled,loaded.Settings.GraphSmoothing);Assert.Equal(10800,loaded.Settings.HistorySeconds);
        Assert.Equal(settings.GraphStyles["cpu"],loaded.Settings.GraphStyles["cpu"]);Assert.Null(loaded.Error);
    }
}
