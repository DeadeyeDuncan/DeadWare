using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// Task 9: the hardware rescan request on its short second connection, against a local listener on an ephemeral port (never OpenRGB's 6744).
public sealed class OpenRgbSessionTests
{
    static readonly TimeSpan Limit=TimeSpan.FromSeconds(1);

    // pushFirst: the server sends 16 bytes of its own before reading and never closes, so the drain ends at its limit; otherwise it reads
    // the request and closes at once, as OpenRGB does when a client half-closes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_rescan_packet_is_the_sixteen_bytes(bool pushFirst)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var keepOpen=new TaskCompletionSource();
        var server=Task.Run(async()=>
        {
            using var c=await listener.AcceptTcpClientAsync();
            var s=c.GetStream();
            if(pushFirst) await s.WriteAsync(new byte[16]);
            var got=new List<byte>();
            var buf=new byte[64];
            int n;
            while((n=await s.ReadAsync(buf))>0) got.AddRange(buf[..n]);   // 0: the client half-closed
            if(pushFirst) await keepOpen.Task.WaitAsync(RealTimeCap);
            return got.ToArray();
        });
        var clock=Stopwatch.StartNew();
        await Task.Run(()=>OpenRgbSession.SendRescan("127.0.0.1",port,Limit,Limit));
        Assert.True(clock.Elapsed<TimeSpan.FromSeconds(3),$"took {clock.Elapsed.TotalSeconds:0.##} s");
        keepOpen.TrySetResult();
        var received=await server.WaitAsync(RealTimeCap);
        Assert.Equal([0x4F,0x52,0x47,0x42,0,0,0,0,0x8C,0,0,0,0,0,0,0],received);
        Assert.Equal(OpenRgbSession.RescanPacket,received);
    }

    [Fact]
    public void A_refused_connection_throws_inside_the_limit()
    {
        var l=new TcpListener(IPAddress.Loopback,0);
        l.Start();
        var port=((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();                                                 // nothing listens there now
        var clock=Stopwatch.StartNew();
        var e=Record.Exception(()=>OpenRgbSession.SendRescan("127.0.0.1",port,Limit,Limit));
        Assert.True(e is SocketException or TimeoutException,e?.ToString()??"nothing thrown");
        Assert.True(clock.Elapsed<Limit+TimeSpan.FromSeconds(2),$"took {clock.Elapsed.TotalSeconds:0.##} s");
    }
}
