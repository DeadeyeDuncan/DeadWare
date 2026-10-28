using System.Text.Json;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class WidgetPinSettingsTests
{
    private static readonly WindowPlacement WidgetBounds=new(100,200,340,480,false);
    private static readonly WindowPlacement CpuBounds=new(20,30,420,230,true);

    [Fact]
    public void A_new_widget_keeps_the_existing_pinned_default()
    {
        Assert.True(new UserSettings().WidgetAlwaysOnTop);
        var loaded=SettingsCodec.Read("{}");
        Assert.True(loaded.Settings.WidgetAlwaysOnTop);Assert.Null(loaded.Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_preference_migrates_the_saved_widget_pin(bool topmost)
    {
        var loaded=SettingsCodec.Read(Saved(topmost,null,true));
        Assert.Equal(topmost,loaded.Settings.WidgetAlwaysOnTop);
        Assert.Equal(WidgetBounds with {Topmost=topmost},loaded.Settings.Windows["widget"]);
        Assert.Equal(CpuBounds,loaded.Settings.Windows["cpu"]);Assert.Null(loaded.Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Legacy_layout_reset_preserves_the_widget_pin_preference(bool topmost)
    {
        var loaded=SettingsCodec.Read(Saved(topmost,null,false));
        Assert.Equal(topmost,loaded.Settings.WidgetAlwaysOnTop);
        Assert.Empty(loaded.Settings.Windows);
        Assert.Equal(WindowCoordinates.Format,loaded.Settings.WindowPlacementFormat);
        Assert.Contains("Window layout was reset",loaded.Error);
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)]
    [InlineData(true,false)] [InlineData(true,true)]
    public void Explicit_preference_wins_an_older_placement_pin(bool topmost,bool currentFormat)
    {
        var loaded=SettingsCodec.Read(Saved(!topmost,topmost,currentFormat));
        Assert.Equal(topmost,loaded.Settings.WidgetAlwaysOnTop);
        if(currentFormat)Assert.Equal(CpuBounds,loaded.Settings.Windows["cpu"]);
        else Assert.Empty(loaded.Settings.Windows);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Pin_preference_roundtrip_preserves_other_preferences(bool topmost)
    {
        var settings=new UserSettings
        {
            WidgetAlwaysOnTop=topmost,WidgetGraphs=["memory","network"],Theme="Daylight",HistorySeconds=3600,
            PollingIntervalSeconds=2,RestoreOpenWindows=false,SelectedGpuId="gpu-b",
            Windows=new() {["widget"]=WidgetBounds with {Topmost=!topmost},["cpu"]=CpuBounds}
        };
        var loaded=SettingsCodec.Read(SettingsCodec.Write(settings));
        Assert.Equal(topmost,loaded.Settings.WidgetAlwaysOnTop);
        Assert.Equal(settings.WidgetGraphs,loaded.Settings.WidgetGraphs);
        Assert.Equal("Daylight",loaded.Settings.Theme);Assert.Equal(3600,loaded.Settings.HistorySeconds);
        Assert.Equal(2,loaded.Settings.PollingIntervalSeconds);Assert.False(loaded.Settings.RestoreOpenWindows);
        Assert.Equal("gpu-b",loaded.Settings.SelectedGpuId);Assert.Equal(CpuBounds,loaded.Settings.Windows["cpu"]);
        Assert.Equal(settings.Windows["widget"],loaded.Settings.Windows["widget"]);Assert.Null(loaded.Error);
    }

    [Theory]
    [InlineData("{\"Windows\":null}")]
    [InlineData("{\"Windows\":{\"widget\":null}}")]
    public void Missing_valid_widget_placement_uses_the_pinned_default(string json)
    { Assert.True(SettingsCodec.Read(json).Settings.WidgetAlwaysOnTop); }

    [Fact]
    public void Other_windows_do_not_supply_the_widget_pin_default()
    {
        string json=JsonSerializer.Serialize(new {Windows=new Dictionary<string,WindowPlacement> {["cpu"]=CpuBounds with {Topmost=false}}});
        Assert.True(SettingsCodec.Read(json).Settings.WidgetAlwaysOnTop);
    }

    private static string Saved(bool placementPin,bool? explicitPin,bool currentFormat)
    {
        var values=new Dictionary<string,object>
        {
            ["Windows"]=new Dictionary<string,WindowPlacement> {["widget"]=WidgetBounds with {Topmost=placementPin},["cpu"]=CpuBounds}
        };
        if(currentFormat)values[nameof(UserSettings.WindowPlacementFormat)]=WindowCoordinates.Format;
        if(explicitPin.HasValue)values["WidgetAlwaysOnTop"]=explicitPin.Value;
        return JsonSerializer.Serialize(values);
    }
}
