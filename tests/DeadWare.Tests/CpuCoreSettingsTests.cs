using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class CpuCoreSettingsTests
{
    [Fact] public void Legacy_settings_keep_per_core_off()
        =>Assert.False(SettingsCodec.Read("{\"ShowCpuPumpSpeed\":true}").Settings.ShowCpuCoreUsage);

    [Theory][InlineData(false)][InlineData(true)]
    public void Per_core_preference_roundtrips(bool enabled)
    {
        var loaded=SettingsCodec.Read($"{{\"ShowCpuCoreUsage\":{enabled.ToString().ToLowerInvariant()}}}");
        Assert.Equal(enabled,loaded.Settings.ShowCpuCoreUsage);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.Equal(enabled,json.RootElement.GetProperty("ShowCpuCoreUsage").GetBoolean());
    }
}
