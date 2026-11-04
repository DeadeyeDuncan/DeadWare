using System.Net;

namespace DeadWare.Core;

/// <summary>The local-network share (LAN, loopback, link-local peers) of the per-app capture's live rates.</summary>
public static class NetworkTrafficSplit
{
    /// <summary>
    /// Sums the live peers whose remote address classifies as local; unparsable addresses are not local.
    /// Returns nulls when the capture is unavailable. Each direction is clamped to the adapter total when
    /// one is known, since ETW and the adapter counters are sampled on different cadences.
    /// </summary>
    public static (double? Down, double? Up) Local(ConnectionSample sample, double? totalDown = null, double? totalUp = null)
    {
        if (!sample.Available) return (null, null);
        double down = 0, up = 0;
        foreach (var traffic in sample.Live)
        {
            if (!IPAddress.TryParse(traffic.Peer.RemoteAddress, out var address) ||
                NetworkAddress.Classify(address) != NetworkAddressKind.Local) continue;
            down += traffic.DownloadBytesPerSecond;
            up += traffic.UploadBytesPerSecond;
        }
        return (Clamp(down, totalDown), Clamp(up, totalUp));
    }

    private static double Clamp(double local, double? total)
        => total is { } limit && double.IsFinite(limit) ? Math.Min(local, Math.Max(0, limit)) : local;
}
