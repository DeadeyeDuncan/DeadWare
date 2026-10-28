using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class WidgetGraphSelectionTests
{
    [Fact]
    public void Legacy_settings_default_to_all_graphs_with_independent_lists()
    {
        var first=new UserSettings();var second=new UserSettings();
        Assert.Equal(WidgetGraphSelection.Kinds,first.WidgetGraphs);
        Assert.NotSame(first.WidgetGraphs,second.WidgetGraphs);
        first.WidgetGraphs.Clear();
        Assert.Equal(WidgetGraphSelection.Kinds,second.WidgetGraphs);
        var loaded=SettingsCodec.Read("{}");
        Assert.Equal(WidgetGraphSelection.Kinds,loaded.Settings.WidgetGraphs);
        Assert.Null(loaded.Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public void Every_graph_subset_survives_settings_roundtrip(int mask)
    {
        var expected=WidgetGraphSelection.Kinds.Where((_,index)=>(mask&(1<<index))!=0).ToList();
        var loaded=SettingsCodec.Read(SettingsCodec.Write(new UserSettings {WidgetGraphs=expected}));
        Assert.Equal(expected,loaded.Settings.WidgetGraphs);
        Assert.Null(loaded.Error);
    }

    [Fact]
    public void Normalization_keeps_canonical_supported_unique_graphs_and_explicit_empty()
    {
        Assert.Equal(new[]{"cpu","network"},WidgetGraphSelection.Normalize(["network",null!,"cpu","network","invalid"]));
        Assert.Empty(WidgetGraphSelection.Normalize([]));
        Assert.Empty(WidgetGraphSelection.Normalize(["invalid","CPU"]));
        Assert.Equal(WidgetGraphSelection.Kinds,WidgetGraphSelection.Normalize(null));
    }

    [Theory]
    [InlineData("{\"WidgetGraphs\":null}","cpu,cputemp,memory,network")]
    [InlineData("{\"WidgetGraphs\":[\"network\",\"cpu\"]}","cpu,network")]
    [InlineData("{\"WidgetGraphs\":[\"cpu\",\"cpu\",\"invalid\",null,\"network\"]}","cpu,network")]
    [InlineData("{\"WidgetGraphs\":[\"invalid\"]}","")]
    public void Invalid_or_noncanonical_saved_selections_are_normalized_with_a_notice(string json,string expected)
    {
        var loaded=SettingsCodec.Read(json);
        Assert.Equal(expected.Length==0?[]:expected.Split(','),loaded.Settings.WidgetGraphs);
        Assert.NotNull(loaded.Error);
    }

    [Theory]
    [InlineData(0,180,180)] [InlineData(1,200,240)] [InlineData(2,280,360)]
    [InlineData(3,400,480)] [InlineData(4,520,600)]
    public void Graph_counts_have_readable_minimum_and_default_heights(int count,double minimum,double defaultHeight)
    {
        Assert.Equal(minimum,WidgetGraphSelection.MinimumHeight(count));
        Assert.Equal(defaultHeight,WidgetGraphSelection.DefaultHeight(count));
    }
}
