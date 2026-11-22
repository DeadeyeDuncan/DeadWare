using System.Net.NetworkInformation;
using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class NetworkRateUnitTests
{
    [Fact] public void Missing_or_invalid_unit_reads_as_bytes_without_an_error()
    {
        foreach(var json in new[]{"{}","{\"NetworkRateUnit\":null}","{\"NetworkRateUnit\":\"Bogus\"}","{\"NetworkRateUnit\":\"bits\"}"})
        {
            var read=SettingsCodec.Read(json);
            Assert.Equal(NetworkRateUnits.Bytes,read.Settings.NetworkRateUnit);Assert.False(read.Settings.ExcludeVirtualAdapters);Assert.Null(read.Error);
        }
        Assert.Equal(NetworkRateUnit.BytesPerSecond,NetworkRateUnits.Parse(new UserSettings().NetworkRateUnit));
    }
    [Fact] public void Unit_and_virtual_adapter_choices_round_trip()
    {
        foreach(var unit in NetworkRateUnits.All)
        {
            var saved=SettingsCodec.Read(SettingsCodec.Write(new UserSettings{NetworkRateUnit=unit,ExcludeVirtualAdapters=true})).Settings;
            Assert.Equal(unit,saved.NetworkRateUnit);Assert.True(saved.ExcludeVirtualAdapters);
        }
        Assert.Equal(NetworkRateUnit.BitsPerSecond,NetworkRateUnits.Parse(NetworkRateUnits.Bits));
    }
    [Fact] public void Bits_formatting_multiplies_by_eight_and_steps_at_thousands()
    {
        Assert.Equal("—",NetworkRateUnits.Format(null,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("—",NetworkRateUnits.Format(double.NaN,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("0.0 bps",NetworkRateUnits.Format(0,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("800.0 bps",NetworkRateUnits.Format(100,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("1.0 Kbps",NetworkRateUnits.Format(125,NetworkRateUnit.BitsPerSecond));
        // 999,999 bps stays in Kbps; one more bit per second steps to Mbps.
        Assert.Equal("1000.0 Kbps",NetworkRateUnits.Format(999_999/8d,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("1.0 Mbps",NetworkRateUnits.Format(125_000,NetworkRateUnit.BitsPerSecond));
        // Steam's 503 Mbps is DeadWare's 62.9 MB/s.
        Assert.Equal("503.0 Mbps",NetworkRateUnits.Format(62_875_000,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("62.9 MB/s",NetworkRateUnits.Format(62_875_000,NetworkRateUnit.BytesPerSecond));
        Assert.Equal("1.0 Gbps",NetworkRateUnits.Format(125_000_000,NetworkRateUnit.BitsPerSecond));
        Assert.Equal("8000.0 Tbps",NetworkRateUnits.Format(1e15,NetworkRateUnit.BitsPerSecond));
    }
    [Fact] public void Bytes_formatting_matches_the_existing_display()
    {
        Assert.Equal("—",NetworkRateUnits.Format(null,NetworkRateUnit.BytesPerSecond));
        Assert.Equal("999.0 B/s",NetworkRateUnits.Format(999,NetworkRateUnit.BytesPerSecond));
        Assert.Equal("1.0 KB/s",NetworkRateUnits.Format(1000,NetworkRateUnit.BytesPerSecond));
        Assert.Equal("257.0 MB/s",NetworkRateUnits.Format(257_000_000,NetworkRateUnit.BytesPerSecond));
    }
    [Fact] public void Virtual_adapters_are_recognized_by_type_and_name()
    {
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,"vEthernet (WSL)","Hyper-V Virtual Ethernet Adapter"));
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,"Ethernet 3","TAP-Windows Adapter V9"));
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Tunnel,"Tunnel","Microsoft Teredo Tunneling Adapter"));
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,"Ethernet 2","WireGuard Tunnel"));
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,"Docker","vEthernet (nat)"));
        Assert.True(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ppp,"VPN Connection",null));
        Assert.False(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,"Ethernet","Intel(R) Ethernet Controller I225-V"));
        Assert.False(VirtualAdapters.IsVirtual(NetworkInterfaceType.Wireless80211,"Wi-Fi","Intel(R) Wi-Fi 6E AX211 160MHz"));
        Assert.False(VirtualAdapters.IsVirtual(NetworkInterfaceType.Ethernet,null,null));
    }
}
