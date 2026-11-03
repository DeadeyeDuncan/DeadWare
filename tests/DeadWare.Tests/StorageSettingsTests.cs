using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class StorageSettingsTests
{
    [Fact] public void StorageSensors_defaults_on_and_round_trips()
    {
        Assert.True(new UserSettings().StorageSensors);
        Assert.True(SettingsCodec.Read("{}").Settings.StorageSensors);
        var loaded=SettingsCodec.Read("{\"StorageSensors\":false}");
        Assert.False(loaded.Settings.StorageSensors);
        using var json=JsonDocument.Parse(SettingsCodec.Write(loaded.Settings));
        Assert.False(json.RootElement.GetProperty("StorageSensors").GetBoolean());
    }
}
