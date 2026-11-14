using System.Net.NetworkInformation;
namespace DeadWare.Core;

/// <summary>How a network rate, stored in bytes per second, is shown.</summary>
public enum NetworkRateUnit
{
    /// <summary>Decimal bytes per second: B/s, KB/s, MB/s, GB/s, TB/s.</summary>
    BytesPerSecond,
    /// <summary>Decimal bits per second: bps, Kbps, Mbps, Gbps, Tbps.</summary>
    BitsPerSecond,
}
public static class NetworkRateUnits
{
    public const string Bytes="BytesPerSecond",Bits="BitsPerSecond";
    public static readonly string[] All=[Bytes,Bits];
    /// <summary>An unknown or missing value is the default, bytes per second.</summary>
    public static string Normalize(string? unit)=>unit==Bits?Bits:Bytes;
    public static NetworkRateUnit Parse(string? unit)=>Normalize(unit)==Bits?NetworkRateUnit.BitsPerSecond:NetworkRateUnit.BytesPerSecond;
    public static string Label(string unit)=>Normalize(unit)==Bits?"Bits per second (Mbps)":"Bytes per second (MB/s)";
    private static readonly string[] ByteUnits=["B/s","KB/s","MB/s","GB/s","TB/s"],BitUnits=["bps","Kbps","Mbps","Gbps","Tbps"];
    /// <summary>Formats a stored bytes-per-second value in <paramref name="unit"/> with one decimal and decimal (1000) steps; null or non-finite is "—".</summary>
    public static string Format(double? bytesPerSecond,NetworkRateUnit unit)
    {
        if(!bytesPerSecond.HasValue||!double.IsFinite(bytesPerSecond.Value)) return "—";
        bool bits=unit==NetworkRateUnit.BitsPerSecond;
        double value=bits?bytesPerSecond.Value*8:bytesPerSecond.Value;var units=bits?BitUnits:ByteUnits;int i=0;
        while(value>=1000&&i<units.Length-1){value/=1000;i++;}
        return $"{value:0.0} {units[i]}";
    }
}
/// <summary>Recognizes virtual network adapters (hypervisor switches, VPN tunnels, container bridges) that re-count traffic already seen on a physical adapter.</summary>
public static class VirtualAdapters
{
    private static readonly string[] Markers=["hyper-v","vethernet","wsl","vpn","virtual","tap-","tap adapter","wintun","wireguard","openvpn","tailscale","zerotier","docker","vmware","virtualbox","vbox","npcap","loopback","teredo","isatap","6to4"];
    public static bool IsVirtual(NetworkInterfaceType type,string? name,string? description)
    {
        if(type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Loopback) return true;
        string text=((name??"")+" "+(description??"")).ToLowerInvariant();
        foreach(var marker in Markers) if(text.Contains(marker,StringComparison.Ordinal)) return true;
        return false;
    }
}
