using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class CpuPumpSettingsTests
{
    [Fact]
    public void Legacy_settings_keep_the_pump_overlay_disabled()
    {
        var loaded=SettingsCodec.Read("{\"Theme\":\"Ember\",\"ShowGpuFanSpeed\":true}");
        Assert.False(loaded.Settings.ShowCpuPumpSpeed);Assert.True(loaded.Settings.ShowGpuFanSpeed);
        Assert.Null(loaded.Error);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Explicit_pump_preference_roundtrips_independently_of_the_gpu_fan(bool enabled)
    {
        var loaded=SettingsCodec.Read($"{{\"ShowCpuPumpSpeed\":{enabled.ToString().ToLowerInvariant()}}}");
        Assert.Equal(enabled,loaded.Settings.ShowCpuPumpSpeed);Assert.False(loaded.Settings.ShowGpuFanSpeed);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.Equal(enabled,json.RootElement.GetProperty("ShowCpuPumpSpeed").GetBoolean());
    }

    [Fact]
    public void Pump_color_and_line_style_are_saved_like_other_graph_series()
    {
        var settings=new UserSettings {GraphColors=new(){{"cpupump","#3366FF"}},GraphStyles=new(){{"cpupump",new("dashed",true,1.5,2)}}};
        var reopened=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal("#3366FF",reopened.GraphColors["cpupump"]);
        Assert.Equal(new("dashed",true,1.5,2),reopened.GraphStyles["cpupump"]);
    }
}
