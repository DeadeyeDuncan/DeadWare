using DeadWare.Core;
using System.Text.Json;
using Xunit;

namespace DeadWare.Tests;

public class ConnectionWidgetSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"WidgetGraphs\":null}")]
    public void Legacy_graph_choices_keep_the_map_disabled(string json)
    {
        var settings=SettingsCodec.Read(json).Settings;
        Assert.Equal(WidgetGraphSelection.Kinds,settings.WidgetGraphs);
        Assert.False(settings.WidgetConnectionsMap);
        using var saved=JsonDocument.Parse(SettingsCodec.Write(settings));
        Assert.True(saved.RootElement.TryGetProperty("WidgetConnectionsNorthAmerica",out var region));
        Assert.False(region.GetBoolean());
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void Region_selection_roundtrips_independently_of_map_visibility(bool map,bool northAmerica)
    {
        var json=$"{{\"WidgetConnectionsMap\":{map.ToString().ToLowerInvariant()},\"WidgetConnectionsNorthAmerica\":{northAmerica.ToString().ToLowerInvariant()},\"WidgetGraphs\":[]}}";
        var settings=SettingsCodec.Read(json).Settings;
        using var saved=JsonDocument.Parse(SettingsCodec.Write(settings));
        Assert.Equal(map,settings.WidgetConnectionsMap);Assert.Empty(settings.WidgetGraphs);
        Assert.True(saved.RootElement.TryGetProperty("WidgetConnectionsNorthAmerica",out var region));
        Assert.Equal(northAmerica,region.GetBoolean());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Map_selection_is_independent_of_every_graph_subset(int count)
    {
        foreach(bool map in new[]{false,true})
        {
            var graphs=WidgetGraphSelection.Kinds.Take(count).ToList();
            var settings=SettingsCodec.Read(SettingsCodec.Write(new UserSettings {WidgetGraphs=graphs,WidgetConnectionsMap=map})).Settings;
            Assert.Equal(graphs,settings.WidgetGraphs);
            Assert.Equal(map,settings.WidgetConnectionsMap);
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void Disabled_map_preserves_existing_window_sizes(int count)
    {
        Assert.Equal(WidgetGraphSelection.MinimumHeight(count),WidgetGraphSelection.MinimumHeight(count,false));
        Assert.Equal(WidgetGraphSelection.DefaultHeight(count),WidgetGraphSelection.DefaultHeight(count,false));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Map_adds_one_150_dip_row_without_squeezing_graphs(int count)
    {
        Assert.Equal(WidgetGraphSelection.MinimumHeight(count)+150,WidgetGraphSelection.MinimumHeight(count,true));
        Assert.Equal(WidgetGraphSelection.DefaultHeight(count)+150,WidgetGraphSelection.DefaultHeight(count,true));
    }

    [Fact]
    public void Map_alone_has_a_compact_window_size()
    {
        Assert.Equal(210,WidgetGraphSelection.MinimumHeight(0,true));
        Assert.Equal(240,WidgetGraphSelection.DefaultHeight(0,true));
    }
}
