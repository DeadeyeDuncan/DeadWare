using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ConnectionSettingsTests
{
    [Fact] public void Old_settings_enable_recording_without_enabling_the_widget_map()
    {
        var settings=SettingsCodec.Read("{}").Settings;
        Assert.True(settings.ConnectionsRecording);Assert.True(settings.ConnectionsAnimation);
        Assert.False(settings.WidgetConnectionsMap);Assert.Null(settings.ConnectionsHomeCityId);
    }
    [Fact] public void Connection_preferences_round_trip_and_oversized_city_identifiers_are_cleared()
    {
        var settings=new UserSettings{ConnectionsRecording=false,WidgetConnectionsMap=true,ConnectionsAnimation=false,ConnectionsHomeCityId="london"};
        var saved=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.False(saved.ConnectionsRecording);Assert.True(saved.WidgetConnectionsMap);Assert.False(saved.ConnectionsAnimation);Assert.Equal("london",saved.ConnectionsHomeCityId);
        settings.ConnectionsHomeCityId=new string('a',1024);
        Assert.Null(SettingsCodec.Read(SettingsCodec.Write(settings)).Settings.ConnectionsHomeCityId);
    }
}
