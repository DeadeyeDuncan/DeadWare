using System.Runtime.InteropServices;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public sealed class WindowsBatteryTests
{
    private const uint PowerSettingChange = 0x8013;

    private static IntPtr Setting(Guid guid, int length, int level)
    {
        // A native POWERBROADCAST_SETTING as Windows hands it to the callback: GUID, DWORD DataLength, DWORD data.
        var memory = Marshal.AllocHGlobal(24);
        Marshal.Copy(guid.ToByteArray(), 0, memory, 16);
        Marshal.WriteInt32(memory, 16, length); Marshal.WriteInt32(memory, 20, level);
        return memory;
    }

    [Fact]
    public void Power_setting_callback_reads_the_energy_saver_level_at_offset_20()
    {
        IntPtr standard = Setting(EnergySaver.StatusSetting, 4, 1), high = Setting(EnergySaver.StatusSetting, 4, 2),
            legacy = Setting(new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5"), 4, 1), empty = Setting(EnergySaver.StatusSetting, 0, 2);
        try
        {
            Assert.Equal(1, EnergySaverStatus.ReadSetting(PowerSettingChange, standard));
            Assert.Equal(2, EnergySaverStatus.ReadSetting(PowerSettingChange, high));
            Assert.Null(EnergySaverStatus.ReadSetting(0x8012, standard));            // not a power-setting change
            Assert.Null(EnergySaverStatus.ReadSetting(PowerSettingChange, legacy));  // GUID_POWER_SAVING_STATUS, not Energy saver
            Assert.Null(EnergySaverStatus.ReadSetting(PowerSettingChange, empty));   // no DWORD of data
            Assert.Null(EnergySaverStatus.ReadSetting(PowerSettingChange, IntPtr.Zero));
        }
        finally { foreach (var memory in new[] { standard, high, legacy, empty }) Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void Reading_the_battery_subscribes_once_and_never_throws()
    {
        // The runner may have no battery and may predate Energy saver: the level is then absent and the legacy flag decides.
        var first = WindowsBattery.Read(); var second = WindowsBattery.Read();
        Assert.NotNull(first); Assert.NotNull(second);
        Assert.True(EnergySaverStatus.Current is null or >= 0 and <= 2, $"Energy saver level {EnergySaverStatus.Current}");
    }
}
