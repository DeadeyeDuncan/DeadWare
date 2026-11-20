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

    // Task 2 boundaries: the comparisons are pinned at the edge, since a slip would quarantine a user's valid file and reset it to defaults.
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Brightness_at_either_end_is_accepted(int brightness)
    {
        var r = LightingSettingsCodec.Read($"{{\"Brightness\":{brightness}}}");
        Assert.False(r.Quarantine);
        Assert.Equal(brightness, r.Settings.Brightness);
    }

    [Fact]
    public void Exactly_the_maximum_number_of_zone_entries_is_accepted()
    {
        var s = new LightingSettings();
        for (int i = 0; i < LightingSettingsCodec.MaxZoneEntries; i++) s.ZoneColors["z" + i] = new ZoneColorEntry();
        for (int i = 0; i < LightingSettingsCodec.MaxZoneEntries; i++) s.HardwareModes["d" + i] = "Rainbow Wave";
        var r = LightingSettingsCodec.Read(System.Text.Json.JsonSerializer.Serialize(s));
        Assert.False(r.Quarantine);
        Assert.Equal(LightingSettingsCodec.MaxZoneEntries, r.Settings.ZoneColors.Count);
        Assert.Equal(LightingSettingsCodec.MaxZoneEntries, r.Settings.HardwareModes.Count);
    }

    [Fact]
    public void Keys_and_names_of_the_maximum_length_are_accepted()
    {
        var s = new LightingSettings();
        var longest = new string('k', LightingSettingsCodec.MaxKeyLength);
        s.ZoneColors[longest] = new ZoneColorEntry { ZoneName = new string('n', LightingSettingsCodec.MaxKeyLength) };
        s.HardwareModes[longest] = new string('m', LightingSettingsCodec.MaxKeyLength);
        Assert.False(LightingSettingsCodec.Read(System.Text.Json.JsonSerializer.Serialize(s)).Quarantine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("1234567")]
    [InlineData("#12345G")]
    public void Rgb_parse_refuses_anything_but_hash_and_six_hex_digits(string? text)
    {
        Assert.False(Rgb.TryParse(text, out var c));
        Assert.Equal(default, c);
    }

    [Fact]
    public void Write_refuses_invalid_settings() => Assert.Throws<InvalidDataException>(() => LightingSettingsCodec.Write(new LightingSettings { Brightness = -1 }));

    // Ruling AS / Task 2: Version 1 is this build's format. 0 or less is invalid and is quarantined; a newer file is a later DeadWare's,
    // read as far as this build understands it but never rewritten or set aside.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Version_below_one_asks_for_quarantine(int version) => Assert.True(LightingSettingsCodec.Read($"{{\"Version\":{version}}}").Quarantine);

    [Fact]
    public void Newer_version_loads_what_it_can_without_quarantine()
    {
        var r = LightingSettingsCodec.Read("{\"Version\":3,\"Brightness\":40,\"Effect\":{\"Name\":\"Wave\"}}");
        Assert.True(r.Newer);
        Assert.False(r.Quarantine);
        Assert.Equal(40, r.Settings.Brightness);
        Assert.Equal(3, r.Settings.Version);
    }

    [Fact]
    public void Newer_version_this_build_cannot_validate_loads_defaults_and_is_not_quarantined()
    {
        var r = LightingSettingsCodec.Read("{\"Version\":3,\"Brightness\":150}");
        Assert.True(r.Newer);
        Assert.False(r.Quarantine);
        Assert.Equal(100, r.Settings.Brightness);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Current_version_is_not_newer() => Assert.False(LightingSettingsCodec.Read("{\"Version\":1}").Newer);

    [Fact]
    public void Write_refuses_a_newer_version() => Assert.Throws<InvalidDataException>(() => LightingSettingsCodec.Write(new LightingSettings { Version = LightingSettings.CurrentVersion + 1 }));

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

    // F4 item 2 (whole-branch review D1, D12 and the false premise P76): settings whose written form, Write's indented JSON in UTF-8, would
    // be longer than MaxFileBytes are invalid, on every read and every write, so the host never saves a lighting.json that its next start
    // would set aside unread. Both sides of the boundary, to the byte.
    const string TooLargeError = "Lighting settings would be larger than 1 MiB when saved.";

    // The written form as Write makes it, measured in UTF-8.
    static long Written(LightingSettings s) => System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    // Valid settings whose written form is exactly bytes long: zone entries with names of the longest length, then names shortened by the
    // bytes left over, one character (and so one byte) at a time.
    static LightingSettings OfWrittenSize(long bytes)
    {
        static LightingSettings With(int count)
        {
            var s = new LightingSettings();
            for (int i = 0; i < count; i++) s.ZoneColors[$"zone-{i:D5}"] = new ZoneColorEntry { Color = "#FF0000", ZoneName = new string('n', LightingSettingsCodec.MaxKeyLength) };
            return s;
        }
        long one = Written(With(1)), each = Written(With(2)) - one;
        var settings = With((int)Math.Ceiling((bytes - (one - each)) / (double)each));
        var over = Written(settings) - bytes;
        foreach (var entry in settings.ZoneColors.Values)
        {
            var cut = (int)Math.Min(over, entry.ZoneName.Length);
            entry.ZoneName = entry.ZoneName[cut..];
            if ((over -= cut) == 0) break;
        }
        Assert.Equal(bytes, Written(settings));
        Assert.True(settings.ZoneColors.Count <= LightingSettingsCodec.MaxZoneEntries);
        return settings;
    }

    [Fact]
    public void Settings_written_at_exactly_1_MiB_are_valid()
    {
        var s = OfWrittenSize(LightingSettingsCodec.MaxFileBytes);
        Assert.Null(LightingSettingsCodec.Validate(s));
        var json = LightingSettingsCodec.Write(s);
        Assert.Equal(LightingSettingsCodec.MaxFileBytes, System.Text.Encoding.UTF8.GetByteCount(json));
        var r = LightingSettingsCodec.Read(json);
        Assert.False(r.Quarantine);
        Assert.Equal(s.ZoneColors.Count, r.Settings.ZoneColors.Count);
    }

    [Fact]
    public void Settings_written_one_byte_over_1_MiB_are_refused_on_write_and_set_aside_on_read()
    {
        var s = OfWrittenSize(LightingSettingsCodec.MaxFileBytes + 1);
        Assert.Equal(TooLargeError, LightingSettingsCodec.Validate(s));
        Assert.Equal(TooLargeError, Assert.Throws<InvalidDataException>(() => LightingSettingsCodec.Write(s)).Message);
        // The same settings as compact JSON, as a hand-edited file might hold them, are under 1 MiB, yet they count as invalid at load,
        // since every read validates and the next change would write them over the cap.
        var compact = System.Text.Json.JsonSerializer.Serialize(s);
        Assert.True(compact.Length < LightingSettingsCodec.MaxFileBytes);
        var r = LightingSettingsCodec.Read(compact);
        Assert.True(r.Quarantine);
        Assert.Equal(TooLargeError, r.Error);
        Assert.Empty(r.Settings.ZoneColors);
    }

    [Fact] public void A_file_without_effect_or_layout_gets_static_and_an_empty_layout()
    {
        var r=LightingSettingsCodec.Read("""{"Version":1,"LightsOn":true,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{}}""");
        Assert.Null(r.Error);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Empty(r.Settings.Layout.Zones);Assert.Equal(DeskShape.LLeft,DeskLayouts.ShapeOf(r.Settings.Layout));
    }
    [Fact] public void Null_effect_and_layout_read_as_defaults()
    {
        var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":null,"Layout":null}""");
        Assert.Null(r.Error);Assert.NotNull(r.Settings.Effect);Assert.NotNull(r.Settings.Layout);
    }
    [Fact] public void An_unknown_effect_and_out_of_range_numbers_keep_the_file()
    {
        var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":{"Kind":"Plasma","Wave":{"Speed":500,"AngleDeg":725,"Spread":-1,"Colors":[]}}}""");
        Assert.False(r.Quarantine);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Equal(100,r.Settings.Effect.Wave.Speed);Assert.Equal(5,r.Settings.Effect.Wave.AngleDeg);Assert.Equal("#112233",r.Settings.DefaultColor);
    }
    [Theory]
    [InlineData("""{"Kind":"Wave","Wave":{"Colors":["red"]}}""")]
    [InlineData("""{"Kind":"Wave","Wave":{"Colors":["#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233"]}}""")]
    [InlineData("""{"Kind":"Wave","Wave":{"Speed":"fast"}}""")]
    public void A_malformed_effect_sets_the_file_aside(string effect)=>Assert.True(LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":"""+effect+"}").Quarantine);
    [Fact] public void A_layout_point_outside_the_square_sets_the_file_aside()=>Assert.True(LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Layout":{"Version":1,"Zones":{"a|1#0":{"X":2,"Y":0}},"Tower":{"At":{"X":0.1,"Y":0.3},"Members":[],"Excluded":[]},"Desk":[],"Unplaced":[]}}""").Quarantine);
    [Fact] public void Effect_and_layout_round_trip()
    {
        var s=new LightingSettings{Effect=new(){Kind="Wave",Wave=new(){Speed=70,AngleDeg=135,Spread=20,Colors={"#FF0000","#0000FF"}}}};
        s.Layout.Zones["a|1#0"]=new(0.25,0.75);s.Layout.Tower.Members.Add("a|1");s.Layout.Unplaced.Add("b|2");
        var back=LightingSettingsCodec.Read(LightingSettingsCodec.Write(s)).Settings;
        Assert.Equal("Wave",back.Effect.Kind);Assert.Equal(135,back.Effect.Wave.AngleDeg);Assert.Equal(new[]{"#FF0000","#0000FF"},back.Effect.Wave.Colors);
        Assert.Equal(new DeskPoint(0.25,0.75),back.Layout.Zones["a|1#0"]);Assert.Equal(new[]{"a|1"},back.Layout.Tower.Members);Assert.Equal(new[]{"b|2"},back.Layout.Unplaced);
    }
    [Fact] public void A_layout_picture_round_trips_and_a_file_without_the_member_reads_as_null()
    {
        var s=new LightingSettings();s.Layout.Picture="b650m-link-hub";
        Assert.Equal("b650m-link-hub",LightingSettingsCodec.Read(LightingSettingsCodec.Write(s)).Settings.Layout.Picture);
        var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Layout":{"Version":1,"Zones":{},"Tower":{"At":{"X":0.1,"Y":0.3},"Members":[],"Excluded":[]},"Desk":[],"Unplaced":[]}}""");
        Assert.False(r.Quarantine);Assert.Null(r.Settings.Layout.Picture);
    }
    [Fact] public void Write_refuses_an_invalid_effect()=>Assert.Throws<InvalidDataException>(()=>LightingSettingsCodec.Write(new LightingSettings{Effect=new(){Kind="Plasma"}}));
    [Fact] public void A_newer_file_with_another_layout_shape_is_kept()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Brightness":40,"DefaultColor":"#112233","Effect":"Plasma","Layout":{"Desk":"L"}}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.Equal(40,r.Settings.Brightness);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Empty(r.Settings.Layout.Zones);
    }
    [Fact] public void A_newer_file_with_a_wrong_typed_brightness_is_kept_on_defaults()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Brightness":"high"}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.NotNull(r.Error);Assert.Equal(100,r.Settings.Brightness);
    }
    [Fact] public void A_newer_file_whose_zone_colours_became_a_list_is_kept_on_defaults()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Brightness":40,"ZoneColors":[]}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.NotNull(r.Error);Assert.Equal(100,r.Settings.Brightness);
    }
    [Fact] public void A_newer_file_with_a_layout_this_build_refuses_keeps_the_rest()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{"a|1#0":{"Color":"#010203","ZoneName":"Z"}},"HardwareModes":{},"Layout":{"Version":2,"Zones":{},"Tower":{"At":{"X":0.1,"Y":0.3},"Members":[],"Excluded":[]},"Desk":[],"Unplaced":[]}}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.Equal(80,r.Settings.Brightness);Assert.Equal("#010203",r.Settings.ZoneColors["a|1#0"].Color);
        Assert.Equal(1,r.Settings.Layout.Version);Assert.Equal(DeskShape.LLeft,DeskLayouts.ShapeOf(r.Settings.Layout));Assert.Equal(new DeskPoint(0.14,0.3),r.Settings.Layout.Tower.At);
    }
    [Fact] public void A_newer_file_with_nine_wave_colours_keeps_the_rest()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Effect":{"Kind":"Wave","Wave":{"Colors":["#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233","#112233"]}}}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.Equal(80,r.Settings.Brightness);Assert.Equal("Static",r.Settings.Effect.Kind);Assert.Empty(r.Settings.Effect.Wave.Colors);
    }
    [Fact] public void A_current_file_with_a_layout_version_this_build_refuses_is_still_set_aside()=>Assert.True(LightingSettingsCodec.Read("""{"Version":1,"Brightness":80,"DefaultColor":"#112233","ZoneColors":{},"HardwareModes":{},"Layout":{"Version":2,"Zones":{},"Tower":{"At":{"X":0.1,"Y":0.3},"Members":[],"Excluded":[]},"Desk":[],"Unplaced":[]}}""").Quarantine);

    [Fact]
    public void A_tuned_file_is_written_as_version_2_and_read_back()
    {
        var s=new LightingSettings();
        s.Devices["k|1"]=new DeviceTuning{Brightness=70,Correction=new(){R=100,G=88,B=112},Mode=new(){Kind="wave",Speed=55}};
        var r=LightingSettingsCodec.Read(LightingSettingsCodec.Write(s));
        Assert.False(r.Newer);Assert.False(r.Quarantine);Assert.Null(r.Error);
        var t=r.Settings.Devices["k|1"];
        Assert.Equal(2,r.Settings.Version);Assert.Equal(70,t.Brightness);
        Assert.Equal(88,t.Correction!.G);Assert.Equal(112,t.Correction.B);Assert.Equal(100,t.Correction.R);
        Assert.Equal("wave",t.Mode!.Kind);Assert.Equal(55,t.Mode.Speed);
    }
    [Fact]
    public void An_untuned_file_is_written_as_version_1()
    {
        Assert.Contains("\"Version\": 1",LightingSettingsCodec.Write(new LightingSettings()));
        var r=LightingSettingsCodec.Read("""{"Version":2,"Devices":{"k":{"Brightness":70}}}""");
        Assert.Equal(2,r.Settings.Version);
        r.Settings.Devices.Clear();
        Assert.Contains("\"Version\": 1",LightingSettingsCodec.Write(r.Settings));
    }
    [Fact]
    public void A_version_1_file_loads_with_an_empty_table()
    {
        var r=LightingSettingsCodec.Read("""{"Version":1,"Brightness":40}""");
        Assert.Empty(r.Settings.Devices);Assert.False(r.Quarantine);Assert.False(r.Newer);
    }
    [Fact]
    public void A_null_table_is_defaulted()
    {
        var r=LightingSettingsCodec.Read("""{"Version":2,"Devices":null}""");
        Assert.NotNull(r.Settings.Devices);Assert.Empty(r.Settings.Devices);Assert.False(r.Quarantine);Assert.Null(r.Error);
    }
    [Fact] public void Current_version_2_is_not_newer()=>Assert.False(LightingSettingsCodec.Read("""{"Version":2}""").Newer);
    [Fact]
    public void A_null_entry_is_quarantined_not_thrown()
    {
        var r=LightingSettingsCodec.Read("""{"Version":2,"Devices":{"k":null}}""");
        Assert.True(r.Quarantine);Assert.NotNull(r.Error);
    }
    [Fact]
    public void A_later_file_with_a_table_of_another_shape_is_still_read()
    {
        var r=LightingSettingsCodec.Read("""{"Version":3,"Devices":7}""");
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.Empty(r.Settings.Devices);
    }
}
