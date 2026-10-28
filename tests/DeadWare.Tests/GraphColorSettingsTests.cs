using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class GraphColorSettingsTests
{
    [Fact]
    public void Every_series_keeps_its_independent_color_after_reopening_settings()
    {
        var settings=SettingsCodec.Read("""
            {"Theme":"Neon","GraphColors":{"cpu":"#123456","cputemp":"#234567","gpu":"#345678","gputemp":"#456789","memory":"#56789A","download":"#6789AB","upload":"#789ABC"}}
            """).Settings;
        var reopened=SettingsCodec.Read(SettingsCodec.Write(settings)).Settings;
        Assert.Equal(7,reopened.GraphColors.Count);
        Assert.Equal("#123456",reopened.GraphColors["cpu"]);
        Assert.Equal("#234567",reopened.GraphColors["cputemp"]);
        Assert.Equal("#789ABC",reopened.GraphColors["upload"]);
        Assert.Equal("Neon",reopened.Theme);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"GraphColors\":null}")]
    public void Older_or_null_choices_use_theme_defaults(string json)
        =>Assert.Empty(SettingsCodec.Read(json).Settings.GraphColors);

    [Fact]
    public void Invalid_colors_are_discarded_without_losing_other_preferences()
    {
        var settings=SettingsCodec.Read("""
            {"HistorySeconds":300,"Theme":"Daylight","GraphColors":{"cpu":"#aabbcc","gpu":"red","cputemp":"#00112233","gputemp":null,"memory":"#12XZ56","unknown":"#123456","upload":"#FFFFFF"}}
            """).Settings;
        Assert.Equal(2,settings.GraphColors.Count);
        Assert.Equal("#AABBCC",settings.GraphColors["cpu"]);
        Assert.Equal("#FFFFFF",settings.GraphColors["upload"]);
        Assert.Equal(300,settings.HistorySeconds);
        Assert.Equal("Daylight",settings.Theme);
    }
}
