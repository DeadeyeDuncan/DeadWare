using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class LightingKeysTests
{
    private static LightingSettings With(string deviceKey)
    {
        var s=new LightingSettings();
        s.ZoneColors[LightingKeys.Zone(deviceKey,0)]=new ZoneColorEntry{Color="#010203",ZoneName="Keyboard"};
        s.HardwareModes[deviceKey]="Direct";
        return s;
    }

    [Fact]
    public void Remap_moves_entries_when_serial_drops_at_same_location()
    {
        var s=With("S1|HID:A");
        Assert.True(LightingKeys.Remap(s,["|HID:A"]));
        Assert.Equal("#010203",s.ZoneColors["|HID:A#0"].Color);
        Assert.Equal("Direct",s.HardwareModes["|HID:A"]);
        Assert.DoesNotContain("S1|HID:A#0",s.ZoneColors.Keys);
        Assert.DoesNotContain("S1|HID:A",s.HardwareModes.Keys);
    }

    [Fact]
    public void Remap_moves_entries_when_serial_returns()
    {
        var s=With("|HID:A");
        Assert.True(LightingKeys.Remap(s,["S1|HID:A"]));
        Assert.Equal("#010203",s.ZoneColors["S1|HID:A#0"].Color);
    }

    [Fact]
    public void Remap_returns_false_when_nothing_to_do()
    {
        var s=With("s|HID:A");
        Assert.False(LightingKeys.Remap(s,["s|HID:A"]));
    }

    [Fact]
    public void Remap_refuses_absent_location()
    {
        var s=With("s|HID:A");
        Assert.False(LightingKeys.Remap(s,["t|HID:B"]));
        Assert.Contains("s|HID:A#0",s.ZoneColors.Keys);
    }

    [Fact]
    public void Remap_refuses_location_claimed_by_two_live_devices()
    {
        var s=With("s|HID:A");
        Assert.False(LightingKeys.Remap(s,["x|HID:A","y|HID:A"]));
    }

    [Fact]
    public void Remap_refuses_when_target_already_stored()
    {
        var s=With("old|HID:A");
        s.HardwareModes["new|HID:A"]="Direct";
        s.ZoneColors["new|HID:A#0"]=new ZoneColorEntry{Color="#090909",ZoneName="Keyboard"};
        Assert.False(LightingKeys.Remap(s,["new|HID:A"]));
        Assert.Equal("#010203",s.ZoneColors["old|HID:A#0"].Color);
    }

    [Fact]
    public void Remap_refuses_two_stale_keys_racing_for_one_target()
    {
        var s=With("old|usb");
        s.HardwareModes["older|usb"]="Static";
        Assert.False(LightingKeys.Remap(s,["new|usb"]));
        Assert.Contains("old|usb",s.HardwareModes.Keys);
        Assert.Contains("older|usb",s.HardwareModes.Keys);
    }

    [Fact]
    public void Remap_splits_on_first_pipe_so_pipe_in_serial_cannot_corrupt_location()
    {
        var s=With("a|b|HID:A");
        Assert.False(LightingKeys.Remap(s,["|HID:A"]));
    }

    [Fact]
    public void Remap_keeps_windows_hid_location_hash_segments()
    {
        var loc=@"HID: \\?\HID#VID_1038&PID_1610&MI_01#8&98b0dcc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        var s=With($"6462633629512210576|{loc}");
        Assert.True(LightingKeys.Remap(s,[$"|{loc}"]));
        Assert.Equal("#010203",s.ZoneColors[$"|{loc}#0"].Color);
    }

    [Fact]
    public void LocationOf_splits_on_first_pipe()
    {
        Assert.Equal("HID:x|y",LightingKeys.LocationOf(LightingKeys.Device("a\r\nb","HID:x|y")));
        Assert.Equal("c|HID:x|y",LightingKeys.LocationOf(LightingKeys.Device("a\r\nb|c","HID:x|y")));
    }

    [Fact]
    public void TrySplitZone_keeps_hid_hash_segments()
    {
        Assert.True(LightingKeys.TrySplitZone("S|HID#VID_1B1C&PID_0C3F#b&16#3",out var dev,out var idx));
        Assert.Equal("S|HID#VID_1B1C&PID_0C3F#b&16",dev);
        Assert.Equal(3,idx);
    }

    [Fact]
    public void TrySplitZone_fails_without_numeric_suffix()
    {
        Assert.False(LightingKeys.TrySplitZone("S|HID#VID_1B1C&PID_0C3F#b&16",out _,out _));
    }

    [Fact] public void Remap_moves_layout_entries()
    {
        var s=new LightingSettings();
        s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Tower.Members.Add("SER|loc");s.Layout.Tower.Excluded.Add("SER|loc");s.Layout.Unplaced.Add("SER|loc");
        Assert.True(LightingKeys.Remap(s,["|loc"]));
        Assert.Equal(new DeskPoint(0.4,0.4),s.Layout.Zones["|loc#0"]);Assert.False(s.Layout.Zones.ContainsKey("SER|loc#0"));
        Assert.Equal(new[]{"|loc"},s.Layout.Tower.Members);Assert.Equal(new[]{"|loc"},s.Layout.Tower.Excluded);Assert.Equal(new[]{"|loc"},s.Layout.Unplaced);
    }
    [Fact] public void A_target_that_already_has_layout_entries_is_refused()
    {
        var s=new LightingSettings();s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Zones["|loc#0"]=new(0.6,0.6);
        Assert.False(LightingKeys.Remap(s,["|loc"]));
    }
    [Fact] public void Remap_then_arrange_keeps_a_returning_device_in_place()
    {
        var s=new LightingSettings();s.Layout.Zones["SER|loc#0"]=new(0.4,0.4);s.Layout.Tower.Members.Add("SER|loc");
        LightingKeys.Remap(s,["|loc"]);
        var next=DeskLayouts.AutoArrange(s.Layout,[new("|loc","Fans",[0])]);
        Assert.Same(s.Layout,next);Assert.Equal(new DeskPoint(0.4,0.4),next.Zones["|loc#0"]);
    }
}
