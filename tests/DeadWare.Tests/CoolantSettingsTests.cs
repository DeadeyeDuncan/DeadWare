using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class CoolantSettingsTests
{
    [Fact]
    public void The_option_starts_unticked_and_round_trips()
    {
        Assert.False(new UserSettings().ShowCoolantTemperature);
        var legacy=SettingsCodec.Read("{\"Theme\":\"Ember\",\"ShowCpuPumpSpeed\":true}");
        Assert.False(legacy.Settings.ShowCoolantTemperature);Assert.True(legacy.Settings.ShowCpuPumpSpeed);Assert.Null(legacy.Error);
        var loaded=SettingsCodec.Read("{\"ShowCoolantTemperature\":true}");
        Assert.True(loaded.Settings.ShowCoolantTemperature);Assert.False(loaded.Settings.ShowCpuPumpSpeed);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.True(json.RootElement.GetProperty("ShowCoolantTemperature").GetBoolean());
        Assert.True(SettingsCodec.Read(SettingsCodec.Write(loaded.Settings)).Settings.ShowCoolantTemperature);
    }
}
