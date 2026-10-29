using System.Net;
using System.Net.Sockets;

namespace DeadWare.Core;

public static class NetworkAddress
{
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static NetworkAddressKind Classify(IPAddress address)
    {
        address = Normalize(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int a = bytes[0], b = bytes[1], c = bytes[2];
            if (a == 10 || a == 127 || (a == 172 && b is >= 16 and <= 31) ||
                (a == 192 && b == 168) || (a == 169 && b == 254))
                return NetworkAddressKind.Local;
            if (a == 0 || a >= 224 || (a == 100 && b is >= 64 and <= 127) ||
                (a == 192 && b == 0 && c is 0 or 2) || (a == 192 && b == 88 && c == 99) ||
                (a == 192 && ((b == 31 && c == 196) || (b == 52 && c == 193) || (b == 175 && c == 48))) ||
                (a == 198 && b is 18 or 19) || (a == 198 && b == 51 && c == 100) ||
                (a == 203 && b == 0 && c == 113))
                return NetworkAddressKind.Special;
            return NetworkAddressKind.Public;
        }
        if (IPAddress.IsLoopback(address) || (bytes[0] & 0xfe) == 0xfc ||
            (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80))
            return NetworkAddressKind.Local;
        // Only global unicast can be located. Exclude IETF protocol assignments,
        // documentation and 6to4 instead of inventing a destination for embedded addresses.
        if ((bytes[0] & 0xe0) != 0x20 ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) ||
            (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) ||
            (bytes[0] == 0x20 && bytes[1] == 0x02) ||
            (bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0) ||
            (bytes[0] == 0x26 && bytes[1] == 0x20 && bytes[2] == 0 && bytes[3] == 0x4f && bytes[4] == 0x80 && bytes[5] == 0))
            return NetworkAddressKind.Special;
        return NetworkAddressKind.Public;
    }
    public static ConnectionPeer FromEndpoints(ProcessIdentity process, IPAddress sourceAddress,
        int sourcePort, IPAddress destinationAddress, int destinationPort,
        ConnectionProtocol protocol, bool send) => send
            ? new(process, Normalize(sourceAddress).ToString(), sourcePort,
                Normalize(destinationAddress).ToString(), destinationPort, protocol)
            : new(process, Normalize(destinationAddress).ToString(), destinationPort,
                Normalize(sourceAddress).ToString(), sourcePort, protocol);
}
