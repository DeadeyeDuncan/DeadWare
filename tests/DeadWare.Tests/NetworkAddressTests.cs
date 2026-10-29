using System.Net;
using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class NetworkAddressTests
{
    [Fact] public void Receive_and_send_choose_remote_source_and_destination()
    {
        var identity = new ProcessIdentity(42, 100, "app");
        var local = IPAddress.Parse("192.168.1.5");
        var remote = IPAddress.Parse("8.8.8.8");
        var received = NetworkAddress.FromEndpoints(identity, remote, 443, local, 5000, ConnectionProtocol.Tcp, false);
        var sent = NetworkAddress.FromEndpoints(identity, local, 5000, remote, 443, ConnectionProtocol.Tcp, true);
        Assert.Equal(sent, received);
        Assert.Equal("8.8.8.8", received.RemoteAddress);
        Assert.Equal(443, received.RemotePort);
        Assert.Equal("192.168.1.5", received.LocalAddress);
        Assert.Equal(5000, received.LocalPort);
        Assert.Same(identity, received.Process);
    }

    [Fact] public void Mapped_ipv6_matches_ipv4()
    {
        Assert.Equal(IPAddress.Parse("8.8.8.8"), NetworkAddress.Normalize(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.Equal(NetworkAddressKind.Local, NetworkAddress.Classify(IPAddress.Parse("::ffff:192.168.1.1")));
        Assert.Equal(NetworkAddressKind.Public, NetworkAddress.Classify(IPAddress.Parse("8.8.8.8")));
        Assert.Equal(NetworkAddressKind.Public, NetworkAddress.Classify(IPAddress.Parse("2606:4700:4700::1111")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.31.196.1")]
    [InlineData("192.52.193.1")]
    [InlineData("192.175.48.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("198.18.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fdff::1")]
    [InlineData("ff02::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:20::1")]
    [InlineData("3fff::1")]
    [InlineData("64:ff9b::1")]
    [InlineData("2620:4f:8000::1")]
    public void Special_addresses_never_classify_public(string address) =>
        Assert.NotEqual(NetworkAddressKind.Public, NetworkAddress.Classify(IPAddress.Parse(address)));
}
