using System.Buffers.Binary;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class BatteryHistoryTests
{
    [Theory]
    [InlineData(0, 1, 86, 0, BatteryPowerState.OnBattery, 86, false)]
    [InlineData(1, 9, 40, 1, BatteryPowerState.Charging, 40, true)]
    [InlineData(1, 1, 100, 0, BatteryPowerState.PluggedIn, 100, false)]
    [InlineData(0, 4, 0, 1, BatteryPowerState.OnBattery, 0, true)]
    public void Windows_flags_keep_charge_power_and_saver(byte ac, byte flags, byte percent, byte saver,
        BatteryPowerState state, double expected, bool saving)
    {
        var result = BatteryReading.FromWindows(ac, flags, percent, saver);
        Assert.Equal(expected, result.Percentage); Assert.Equal(state, result.PowerState);
        Assert.Equal(saving, result.SaverEnabled);
    }

    [Theory]
    [InlineData((byte)0, 1, true)]   // Energy saver standard: Windows 11 24H2+ leaves the legacy flag at 0
    [InlineData((byte)0, 2, true)]   // high savings
    [InlineData((byte)1, 2, true)]
    [InlineData((byte)0, 0, false)]
    [InlineData((byte)1, 0, true)]   // the legacy flag still counts when it reports on
    [InlineData((byte)255, 0, false)] // a reported level settles an unknown legacy flag
    [InlineData((byte)255, 1, true)]
    public void Energy_saver_level_decides_saver_where_the_legacy_flag_stays_off(byte legacy, int energySaver, bool saving)
        => Assert.Equal(saving, BatteryReading.FromWindows(0, 1, 60, legacy, energySaver).SaverEnabled);

    [Fact]
    public void Without_an_energy_saver_level_the_legacy_flag_decides_alone()
    {
        Assert.False(BatteryReading.FromWindows(0, 1, 60, 0, null).SaverEnabled);
        Assert.True(BatteryReading.FromWindows(0, 1, 60, 1, null).SaverEnabled);
        Assert.Null(BatteryReading.FromWindows(0, 1, 60, 3, null).SaverEnabled);
    }

    [Fact]
    public void Energy_saver_setting_is_read_from_its_own_guid_and_dword()
    {
        // POWERBROADCAST_SETTING: the setting GUID, a DWORD DataLength, then the data (here the ENERGY_SAVER_STATUS DWORD).
        static byte[] Setting(Guid guid, uint length, uint level)
        {
            var bytes = new byte[24]; Assert.True(guid.TryWriteBytes(bytes));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), level);
            return bytes;
        }
        Assert.Equal(new Guid("550E8400-E29B-41D4-A716-446655440000"), EnergySaver.StatusSetting);
        Assert.Equal(0, EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 4, 0)));
        Assert.Equal(1, EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 4, 1)));
        Assert.Equal(2, EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 4, 2)));
        Assert.Null(EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 4, 3)));   // unknown level
        Assert.Null(EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 2, 1)));   // no DWORD of data
        Assert.Null(EnergySaver.Parse(Setting(new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5"), 4, 1))); // legacy battery-saver setting
        Assert.Null(EnergySaver.Parse(Setting(EnergySaver.StatusSetting, 4, 1).AsSpan(0, 23)));
    }

    [Fact]
    public void Unknown_and_absent_values_never_become_full_charge_or_saver_off()
    {
        var unknown = BatteryReading.FromWindows(255, 255, 255, 255);
        Assert.Null(unknown.Percentage); Assert.Null(unknown.SaverEnabled);
        Assert.Equal(BatteryPowerState.Unknown, unknown.PowerState);
        var absent = BatteryReading.FromWindows(1, 128, 100, 0);
        Assert.Null(absent.Percentage); Assert.Equal(BatteryPowerState.NoBattery, absent.PowerState);
        Assert.Null(BatteryReading.FromWindows(0, 1, 101, 3).Percentage);
        Assert.Null(BatteryReading.FromWindows(0, 1, 50, 3).SaverEnabled);
    }

    [Fact]
    public void Cadence_is_one_minute_but_power_and_saver_transitions_are_immediate()
    {
        var recorder = new BatteryRecorder(); var start = DateTimeOffset.UtcNow;
        var onBattery = new BatteryReading(80, BatteryPowerState.OnBattery, false);
        Assert.NotNull(recorder.Observe(start, 0, onBattery));
        for (int i = 1; i < 60; i++) Assert.Null(recorder.Observe(start.AddSeconds(i), i, onBattery with { Percentage = 79 }));
        Assert.Equal(79, recorder.Observe(start.AddSeconds(60), 60, onBattery with { Percentage = 79 })!.Reading.Percentage);
        Assert.NotNull(recorder.Observe(start.AddSeconds(61), 61, onBattery with { SaverEnabled = true }));
        Assert.NotNull(recorder.Observe(start.AddSeconds(62), 62, onBattery with { PowerState = BatteryPowerState.Charging }));
    }

    [Fact]
    public void Sleep_clock_jump_loss_and_restart_create_distinct_segments()
    {
        var recorder = new BatteryRecorder(); var start = DateTimeOffset.UtcNow;
        var reading = new BatteryReading(80, BatteryPowerState.OnBattery, false);
        var first = recorder.Observe(start, 0, reading)!;
        var sleep = recorder.Observe(start.AddMinutes(20), 1200, reading)!;
        Assert.NotEqual(first.Segment, sleep.Segment);
        var clockJump = recorder.Observe(start.AddHours(1), 1201, reading)!;
        Assert.NotEqual(sleep.Segment, clockJump.Segment);
        recorder.Break();
        var loss = recorder.Observe(start.AddHours(1).AddSeconds(1), 1202, reading)!;
        Assert.NotEqual(clockJump.Segment, loss.Segment);
        Assert.NotEqual(loss.Segment, new BatteryRecorder().Observe(start, 0, reading)!.Segment);
    }

    [Fact]
    public void Unavailable_transition_is_saved_and_desktops_do_not_accumulate_empty_minutes()
    {
        var recorder = new BatteryRecorder(); var start = DateTimeOffset.UtcNow;
        var absent = new BatteryReading(null, BatteryPowerState.NoBattery, false);
        Assert.Null(recorder.Observe(start, 0, absent));
        Assert.Null(recorder.Observe(start.AddMinutes(1), 60, absent));
        Assert.NotNull(recorder.Observe(start.AddSeconds(61), 61, new(80, BatteryPowerState.OnBattery, false)));
        Assert.Null(recorder.Observe(start.AddSeconds(61), 61, new(50, BatteryPowerState.OnBattery, true)));
        Assert.NotNull(recorder.Observe(start.AddSeconds(62), 62, BatteryReading.Unavailable));
        Assert.Null(recorder.Observe(start.AddSeconds(63), 63, BatteryReading.Unavailable));
        var resumed = recorder.Observe(start.AddSeconds(64), 64, new(79, BatteryPowerState.OnBattery, null));
        Assert.NotNull(resumed);
    }
}
