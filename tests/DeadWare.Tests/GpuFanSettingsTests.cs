using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class GpuFanSettingsTests
{
    [Fact]
    public void Legacy_settings_keep_the_fan_overlay_disabled()
    {
        var loaded=SettingsCodec.Read("{\"Theme\":\"Ember\",\"HistorySeconds\":300}");
        Assert.False(loaded.Settings.ShowGpuFanSpeed);
        Assert.Equal("Ember",loaded.Settings.Theme);Assert.Equal(300,loaded.Settings.HistorySeconds);
        Assert.Null(loaded.Error);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Explicit_fan_preference_roundtrips_in_settings(bool enabled)
    {
        var loaded=SettingsCodec.Read($"{{\"ShowGpuFanSpeed\":{enabled.ToString().ToLowerInvariant()},\"SelectedGpuId\":\"gpu-b\"}}");
        Assert.Equal(enabled,loaded.Settings.ShowGpuFanSpeed);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.True(json.RootElement.TryGetProperty("ShowGpuFanSpeed",out var saved));
        Assert.Equal(enabled,saved.GetBoolean());
        Assert.Equal("gpu-b",loaded.Settings.SelectedGpuId);Assert.Null(loaded.Error);
    }
}
