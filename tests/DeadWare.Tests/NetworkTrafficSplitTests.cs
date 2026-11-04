using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class NetworkTrafficSplitTests
{
    [Fact] public void Local_peers_sum_and_public_peers_are_left_out()
    {
        var sample=Sample(true,Peer("192.168.1.20",1000,100),Peer("10.0.0.5",500,50),Peer("127.0.0.1",20,10),Peer("fe80::1",5,1),Peer("8.8.8.8",9000,900),Peer("2606:4700::1",1,1));
        Assert.Equal((1525d,161d),NetworkTrafficSplit.Local(sample));
    }
    [Fact] public void Unparsable_and_special_addresses_are_not_local()
    {
        var sample=Sample(true,Peer("not-an-address",100,100),Peer("224.0.0.1",100,100),Peer("",100,100));
        Assert.Equal((0d,0d),NetworkTrafficSplit.Local(sample));
    }
    [Fact] public void Unavailable_capture_publishes_nothing()
        =>Assert.Equal(((double?)null,(double?)null),NetworkTrafficSplit.Local(Sample(false,Peer("192.168.1.20",1000,100))));
    [Fact] public void Local_is_clamped_to_the_adapter_total_per_direction()
    {
        var sample=Sample(true,Peer("192.168.1.20",1000,100));
        Assert.Equal((800d,100d),NetworkTrafficSplit.Local(sample,800,null));
        Assert.Equal((1000d,40d),NetworkTrafficSplit.Local(sample,null,40));
        Assert.Equal((1000d,100d),NetworkTrafficSplit.Local(sample,5000,5000));
    }
    [Fact] public void Available_capture_with_no_peers_is_zero()=>Assert.Equal((0d,0d),NetworkTrafficSplit.Local(Sample(true)));

    private static ConnectionTraffic Peer(string remote,double down,double up)
        =>new(new(new(1,1,"app"),"192.168.1.2",5000,remote,443,ConnectionProtocol.Tcp),DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch,0,0,down,up);
    private static ConnectionSample Sample(bool available,params ConnectionTraffic[] live)
        =>new(DateTimeOffset.UnixEpoch,0,0,"seg",live,[],null,available,true,0,0,0,"");
}
