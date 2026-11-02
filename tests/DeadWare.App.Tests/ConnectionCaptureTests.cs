using System.Net;
using System.Text.Json;
using DeadWare.App;
using DeadWare.Core;
using Xunit;
namespace DeadWare.App.Tests;
public class ConnectionCaptureTests
{
    private static readonly DateTimeOffset Time=new(2026,10,29,12,0,0,TimeSpan.Zero);
    [Theory]
    [InlineData(ConnectionProtocol.Tcp,false,false)] [InlineData(ConnectionProtocol.Tcp,true,false)]
    [InlineData(ConnectionProtocol.Tcp,false,true)] [InlineData(ConnectionProtocol.Tcp,true,true)]
    [InlineData(ConnectionProtocol.Udp,false,false)] [InlineData(ConnectionProtocol.Udp,true,false)]
    [InlineData(ConnectionProtocol.Udp,false,true)] [InlineData(ConnectionProtocol.Udp,true,true)]
    public void Eight_endpoint_variants_publish_the_accepted_identity(ConnectionProtocol protocol,bool send,bool ipv6)
    {
        var identity=new ProcessIdentity(11,Time.AddHours(-1).Ticks,"fixture");
        var capture=new EtwNetwork(_=>identity);
        var local=IPAddress.Parse(ipv6?"::ffff:192.168.1.2":"192.168.1.2");
        var remote=IPAddress.Parse(ipv6?"2001:4860:4860::8888":"8.8.8.8");
        capture.RecordEndpoint(11,Time,20,send,send?local:remote,send?101:443,send?remote:local,send?443:101,protocol);
        var snapshot=capture.TakeSnapshot(Time,1);
        var peer=Assert.Single(snapshot.Connections.Deltas).Peer;
        Assert.Equal(identity,peer.Process);Assert.Equal("192.168.1.2",peer.LocalAddress);
        Assert.Equal(ipv6?"2001:4860:4860::8888":"8.8.8.8",peer.RemoteAddress);
        Assert.Equal(101,peer.LocalPort);Assert.Equal(443,peer.RemotePort);Assert.Equal(protocol,peer.Protocol);
        Assert.Equal(identity.Generation,Assert.Single(snapshot.Processes).Generation);
    }
    [Fact] public void Loss_is_incremental_once_and_rejected_identity_is_visible()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));
        capture.TakeSnapshot(Time,1);capture.ObserveLoss(3);
        capture.RecordEndpoint(11,Time.AddSeconds(1),25,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        var first=capture.TakeSnapshot(Time.AddSeconds(1),2);
        capture.ObserveLoss(3);var second=capture.TakeSnapshot(Time.AddSeconds(2),3);
        Assert.Equal(3,second.Connections.LostEvents);Assert.Equal(first.Connections.Segment,second.Connections.Segment);
        Assert.Equal(25,Assert.Single(second.Connections.Live).SentBytes);
        capture.RecordEndpoint(11,Time.AddSeconds(-1),25,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        var rejected=capture.TakeSnapshot(Time.AddSeconds(3),4).Connections;
        Assert.Equal(1,rejected.UnattributedEvents);Assert.False(rejected.Complete);
    }
    [Fact] public async Task Loss_waits_for_the_accepted_process_and_endpoint_decision()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var capture=new EtwNetwork(_=>{entered.Set();release.Wait();return new(11,Time.Ticks,"fixture");});
        var record=Task.Run(()=>capture.RecordEndpoint(11,Time,25,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var loss=Task.Run(()=>capture.ObserveLoss(1));release.Set();await Task.WhenAll(record,loss);
        var snapshot=capture.TakeSnapshot(Time.AddSeconds(1),2);
        Assert.Empty(snapshot.Processes);Assert.Empty(snapshot.Connections.Live);Assert.Empty(snapshot.Connections.Deltas);
        Assert.Equal(1,snapshot.Connections.LostEvents);
    }
    [Fact] public void Diagnostic_json_keeps_legacy_reading_but_excludes_all_endpoints()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));
        capture.RecordEndpoint(11,Time,25,true,IPAddress.Loopback,123,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        var reading=new Reading(new(Time,1,new Dictionary<string,double?>()),[],[],[],"CPU","hardware","network","etw",false){Connections=capture.TakeSnapshot(Time,1).Connections};
        string json=JsonSerializer.Serialize(reading);
        Assert.Contains("CPU",json);Assert.DoesNotContain("Connections",json);Assert.DoesNotContain("8.8.8.8",json);Assert.DoesNotContain("LocalAddress",json);
    }
    [Fact] public async Task Collector_stop_drains_the_last_event_once_without_another_sensor_publication()
    {
        var now=DateTimeOffset.UtcNow;
        var capture=new EtwNetwork(_=>new(11,now.AddMinutes(-1).UtcTicks,"fixture"));
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        typeof(Collector).GetField("etw",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(collector,capture);
        int ordinary=0;var final=new List<ConnectionSample>();
        collector.SampleAvailable+=_=>ordinary++;
        collector.FinalConnectionsAvailable+=final.Add;
        capture.TakeSnapshot(now,0);
        capture.RecordEndpoint(11,now,71,false,IPAddress.Parse("8.8.8.8"),443,IPAddress.Loopback,123,ConnectionProtocol.Tcp);
        await collector.DisposeAsync();
        Assert.Equal(0,ordinary);Assert.Equal(71,Assert.Single(Assert.Single(final).Deltas).ReceivedBytes);
        Assert.True(final[0].Available);
    }
    [Fact] public void Disabled_capture_discards_unsafe_coverage_and_marks_unavailable()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));
        capture.TakeSnapshot(Time,1);
        capture.RecordEndpoint(11,Time.AddSeconds(1),25,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        var sample=capture.TakeSnapshot(Time.AddSeconds(1),2,available:false).Connections;
        Assert.False(sample.Available);Assert.False(sample.Complete);Assert.Null(sample.CoverageFrom);Assert.Empty(sample.Deltas);Assert.Equal(1,sample.DroppedEvents);
    }
    [Fact] public async Task Final_drain_cannot_make_unstarted_capture_available()
    {
        var capture=new EtwNetwork(false);await capture.StopAsync();
        Assert.False(capture.TakeSnapshot(Time,1).Connections.Available);
    }
    [Fact] public async Task Final_drain_cannot_restore_a_previously_failed_stream()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));
        typeof(EtwNetwork).GetField("active",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(capture,false);
        await capture.StopAsync();Assert.False(capture.TakeSnapshot(Time,1).Connections.Available);
    }
    [Fact] public async Task Final_drain_preserves_bytes_without_querying_the_stopped_native_session()
    {
        bool stopped=false;int queries=0;
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"),()=>{queries++;if(stopped)throw new InvalidOperationException("Native session no longer exists");return 0;});
        capture.TakeSnapshot(Time,1);capture.RecordEndpoint(11,Time.AddSeconds(1),71,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        await capture.StopAsync();stopped=true;
        var final=capture.TakeSnapshot(Time.AddSeconds(1),2).Connections;
        Assert.Equal(71,Assert.Single(final.Deltas).SentBytes);Assert.True(final.Available);Assert.Equal(2,queries);
        Assert.False(final.Complete);Assert.Contains("stop-time loss not verified",final.Status);
    }
    [Fact] public async Task Collector_final_publication_waits_for_an_inflight_ordinary_publication()
    {
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        var capture=new EtwNetwork(_=>new(11,DateTimeOffset.UtcNow.AddHours(-1).Ticks,"fixture"));
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var worker=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(Collector).GetField("etw",flags)!.SetValue(collector,capture);typeof(Collector).GetField("worker",flags)!.SetValue(collector,worker.Task);
        int finals=0;collector.FinalConnectionsAvailable+=_=>Interlocked.Increment(ref finals);
        var publication=typeof(Collector).GetField("publicationGate",flags)!.GetValue(collector)!;Task stop;
        Monitor.Enter(publication);
        try {stop=collector.DisposeAsync().AsTask();Thread.Sleep(100);Assert.Equal(0,Volatile.Read(ref finals));}
        finally {Monitor.Exit(publication);}
        // Only the in-flight publication delays the final drain; the stuck hardware worker does not.
        try {Assert.True(SpinWait.SpinUntil(()=>Volatile.Read(ref finals)==1,TimeSpan.FromSeconds(3)));Assert.False(stop.IsCompleted);}
        finally {worker.SetResult();await stop;}
        Assert.Equal(1,finals);
    }
    [Fact] public async Task Ordinary_poll_racing_stop_does_not_discard_final_observations()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));capture.TakeSnapshot(Time,1);
        capture.RecordEndpoint(11,Time.AddSeconds(1),71,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp);
        await capture.StopAsync();var ordinary=capture.TakeSnapshot(Time.AddSeconds(1),2).Connections;
        var final=capture.TakeSnapshot(Time.AddSeconds(2),3).Connections;
        Assert.Equal(71,ordinary.Deltas.Sum(d=>d.SentBytes)+final.Deltas.Sum(d=>d.SentBytes));Assert.False(ordinary.Complete);
    }
    [Fact] public async Task Native_stop_failure_disposes_owned_resources_and_publishes_unavailable_terminal_sample()
    {
        int disposals=0;var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"),stopNative:()=>throw new InvalidOperationException("native stop failed"),disposeNative:()=>Interlocked.Increment(ref disposals));
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        typeof(Collector).GetField("etw",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(collector,capture);
        var terminal=new List<ConnectionSample>();collector.FinalConnectionsAvailable+=terminal.Add;
        var first=collector.DisposeAsync().AsTask();var second=collector.DisposeAsync().AsTask();
        try {await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(3));}catch(InvalidOperationException) { }
        Assert.Equal(1,disposals);Assert.False(Assert.Single(terminal).Available);Assert.False(terminal[0].Complete);Assert.Contains("shutdown unavailable",capture.Status);
        await collector.DisposeAsync();Assert.Equal(1,disposals);
    }
    [Fact] public async Task Repeated_and_overlapping_collector_disposal_share_one_drain_and_completion()
    {
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));int finals=0,disposals=0;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"),stopNative:()=>{entered.Set();release.Wait();},disposeNative:()=>Interlocked.Increment(ref disposals));
        typeof(Collector).GetField("etw",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(collector,capture);
        collector.FinalConnectionsAvailable+=_=>Interlocked.Increment(ref finals);
        var first=collector.DisposeAsync().AsTask();Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));var second=collector.DisposeAsync().AsTask();
        release.Set();await Task.WhenAll(first,second);await collector.DisposeAsync();
        Assert.Same(first,second);Assert.Equal(1,finals);Assert.Equal(1,disposals);
    }
    [Fact] public async Task Failing_final_subscriber_still_disposes_resources_and_settles_repeated_shutdown()
    {
        int disposals=0;var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"),disposeNative:()=>Interlocked.Increment(ref disposals));
        typeof(Collector).GetField("etw",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(collector,capture);
        collector.FinalConnectionsAvailable+=_=>throw new InvalidOperationException("subscriber failed");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>collector.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>collector.DisposeAsync().AsTask());Assert.Equal(1,disposals);
    }
    [Fact] public async Task Reset_cannot_clear_process_bytes_while_endpoint_commit_is_waiting()
    {
        var capture=new EtwNetwork(_=>new(11,Time.Ticks,"fixture"));
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var endpoints=typeof(EtwNetwork).GetField("connections",flags)!.GetValue(capture)!;
        var endpointGate=typeof(ConnectionLedger).GetField("gate",flags)!.GetValue(endpoints)!;
        var attribution=(ProcessAttribution)typeof(EtwNetwork).GetField("attribution",flags)!.GetValue(capture)!;
        Task record,loss;using var started=new ManualResetEventSlim();
        lock(endpointGate)
        {
            record=Task.Run(()=>capture.RecordEndpoint(11,Time,25,true,IPAddress.Loopback,1,IPAddress.Parse("8.8.8.8"),443,ConnectionProtocol.Tcp));
            Assert.True(SpinWait.SpinUntil(()=>attribution.Take(1).Count==1,TimeSpan.FromSeconds(3)));
            loss=Task.Run(()=>{started.Set();capture.ObserveLoss(1);});Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
            // Give the reset a chance to enter while the endpoint ledger is deliberately blocked.
            Assert.False(SpinWait.SpinUntil(()=>attribution.Take(2).Count==0,TimeSpan.FromMilliseconds(100)));
        }
        await Task.WhenAll(record,loss);
        var snapshot=capture.TakeSnapshot(Time.AddSeconds(1),3);Assert.Empty(snapshot.Processes);Assert.Empty(snapshot.Connections.Deltas);
    }
}
