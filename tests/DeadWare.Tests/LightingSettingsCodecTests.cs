using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightingSettingsCodecTests
{
    [Fact]
    public void Read_of_empty_object_gives_defaults()
    {
        var r = LightingSettingsCodec.Read("{}");
        Assert.True(r.Settings.LightsOn);
        Assert.Equal(100, r.Settings.Brightness);
        Assert.Equal("#4682B4", r.Settings.DefaultColor);
        Assert.Null(r.Error);
        Assert.False(r.Quarantine);
    }

    [Fact]
    public void Write_then_Read_round_trips()
    {
        var s = new LightingSettings { LightsOn = false, Brightness = 40, DefaultColor = "#112233" };
        s.ZoneColors["gpu:fan"] = new ZoneColorEntry { Color = "#FF0000", ZoneName = "Fan" };
        var r = LightingSettingsCodec.Read(LightingSettingsCodec.Write(s));
        Assert.False(r.Quarantine);
        Assert.False(r.Settings.LightsOn);
        Assert.Equal(40, r.Settings.Brightness);
        Assert.Equal("#112233", r.Settings.DefaultColor);
        var e = Assert.Single(r.Settings.ZoneColors);
        Assert.Equal("gpu:fan", e.Key);
        Assert.Equal("#FF0000", e.Value.Color);
        Assert.Equal("Fan", e.Value.ZoneName);
    }

    [Fact]
    public void Unparseable_json_asks_for_quarantine()
    {
        var r = LightingSettingsCodec.Read("{");
        Assert.True(r.Quarantine);
        Assert.NotNull(r.Error);
        Assert.Equal(100, r.Settings.Brightness);
    }

    [Fact]
    public void Out_of_range_brightness_asks_for_quarantine() => Assert.True(LightingSettingsCodec.Read("{\"Brightness\":101}").Quarantine);

    [Fact]
    public void Bad_colour_asks_for_quarantine() => Assert.True(LightingSettingsCodec.Read("{\"DefaultColor\":\"#12345G\"}").Quarantine);

    [Fact]
    public void Too_many_zone_entries_asks_for_quarantine()
    {
        var s = new LightingSettings();
        for (int i = 0; i < LightingSettingsCodec.MaxZoneEntries + 1; i++) s.ZoneColors["z" + i] = new ZoneColorEntry();
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.True(LightingSettingsCodec.Read(json).Quarantine);
    }

    [Fact]
    public void Overlong_key_asks_for_quarantine()
    {
        var s = new LightingSettings();
        s.ZoneColors[new string('k', LightingSettingsCodec.MaxKeyLength + 1)] = new ZoneColorEntry();
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.True(LightingSettingsCodec.Read(json).Quarantine);
    }

    [Fact]
    public void Write_refuses_invalid_settings() => Assert.Throws<InvalidDataException>(() => LightingSettingsCodec.Write(new LightingSettings { Brightness = -1 }));

    [Fact]
    public void Rgb_scale_rounds()
    {
        var c = new Rgb(70, 130, 180);
        Assert.Equal(new Rgb(35, 65, 90), c.Scale(50));
        Assert.Equal(Rgb.Black, c.Scale(0));
        Assert.Equal(c, c.Scale(150));
    }

    [Fact]
    public void Rgb_parse_and_format()
    {
        Assert.True(Rgb.TryParse("#4682b4", out var c));
        Assert.Equal("#4682B4", c.ToHex());
        Assert.False(Rgb.TryParse("4682B4", out _));
    }
}
