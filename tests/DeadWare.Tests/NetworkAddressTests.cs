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
    public static TheoryData<string, string> ClassTable() => new()
    {
        { "127.0.0.0", "Local · loopback" }, { "127.255.255.255", "Local · loopback" }, { "::1", "Local · loopback" },
        { "169.254.0.0", "Local · link-local" }, { "169.254.255.255", "Local · link-local" },
        { "fe80::", "Local · link-local" }, { "febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Local · link-local" },
        { "10.0.0.0", "Local · private" }, { "10.255.255.255", "Local · private" },
        { "172.16.0.0", "Local · private" }, { "172.31.255.255", "Local · private" },
        { "192.168.0.0", "Local · private" }, { "192.168.255.255", "Local · private" },
        { "fc00::", "Local · private" }, { "fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Local · private" },
        { "::ffff:10.0.0.1", "Local · private" },
        { "0.0.0.0", "Special · unspecified" }, { "::", "Special · unspecified" },
        { "0.0.0.1", "Special · this network" }, { "0.1.2.3", "Special · this network" }, { "0.255.255.255", "Special · this network" },
        { "100.64.0.0", "Special · shared (CGNAT)" }, { "100.127.255.255", "Special · shared (CGNAT)" },
        { "224.0.0.0", "Special · multicast" }, { "239.255.255.255", "Special · multicast" },
        { "ff00::", "Special · multicast" }, { "ff02::1", "Special · multicast" }, { "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Special · multicast" },
        { "255.255.255.255", "Special · broadcast" },
        { "192.0.2.0", "Special · documentation" }, { "192.0.2.255", "Special · documentation" },
        { "198.51.100.0", "Special · documentation" }, { "198.51.100.255", "Special · documentation" },
        { "203.0.113.0", "Special · documentation" }, { "203.0.113.255", "Special · documentation" },
        { "2001:db8::", "Special · documentation" }, { "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", "Special · documentation" },
        { "3fff::", "Special · documentation" }, { "3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", "Special · documentation" },
        { "198.18.0.0", "Special · benchmarking" }, { "198.19.255.255", "Special · benchmarking" },
        { "192.0.0.0", "Special · IETF protocol" }, { "192.0.0.255", "Special · IETF protocol" },
        { "2001::", "Special · IETF protocol" }, { "2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff", "Special · IETF protocol" },
        { "192.88.99.0", "Special · 6to4" }, { "192.88.99.255", "Special · 6to4" },
        { "2002::", "Special · 6to4" }, { "2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Special · 6to4" },
        { "192.31.196.0", "Special · reserved" }, { "192.31.196.255", "Special · reserved" },
        { "192.52.193.0", "Special · reserved" }, { "192.52.193.255", "Special · reserved" },
        { "192.175.48.0", "Special · reserved" }, { "192.175.48.255", "Special · reserved" },
        { "240.0.0.0", "Special · reserved" }, { "255.255.255.254", "Special · reserved" },
        { "2620:4f:8000::", "Special · reserved" }, { "2620:4f:8000:ffff:ffff:ffff:ffff:ffff", "Special · reserved" },
        { "1fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Special · reserved" }, { "4000::", "Special · reserved" },
        { "fec0::1", "Special · reserved" }, { "64:ff9b::1", "Special · reserved" },
        { "8.8.8.8", "Internet" }, { "::ffff:8.8.8.8", "Internet" }, { "1.0.0.0", "Internet" }, { "9.255.255.255", "Internet" },
        { "11.0.0.0", "Internet" }, { "126.255.255.255", "Internet" }, { "128.0.0.0", "Internet" }, { "169.253.255.255", "Internet" },
        { "172.15.255.255", "Internet" }, { "172.32.0.0", "Internet" }, { "192.167.255.255", "Internet" }, { "192.169.0.0", "Internet" },
        { "100.63.255.255", "Internet" }, { "100.128.0.0", "Internet" }, { "192.0.1.255", "Internet" }, { "192.0.3.0", "Internet" },
        { "198.17.255.255", "Internet" }, { "198.20.0.0", "Internet" }, { "223.255.255.255", "Internet" },
        { "2000::", "Internet" }, { "2001:200::", "Internet" }, { "2001:db9::", "Internet" }, { "2003::", "Internet" },
        { "2620:4f:7fff:ffff:ffff:ffff:ffff:ffff", "Internet" }, { "2620:4f:8001::", "Internet" }, { "3fff:1000::", "Internet" },
        { "3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", "Internet" }, { "2606:4700:4700::1111", "Internet" },
    };

    [Theory, MemberData(nameof(ClassTable))]
    public void Describe_labels_every_classified_range(string address, string label)
    {
        var ip = IPAddress.Parse(address);
        var described = NetworkAddress.Describe(ip);
        Assert.Equal(label, described.Label);
        Assert.Equal(NetworkAddress.Classify(ip), described.Kind);
        Assert.Equal(described, NetworkAddress.Describe(address));
    }

    [Fact] public void Unparseable_destination_text_is_special_unrecognized() =>
        Assert.Equal(new NetworkAddressClass(NetworkAddressKind.Special, "Special · unrecognized"), NetworkAddress.Describe("not-an-ip"));
}
