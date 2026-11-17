using System.IO.Pipes;
using DeadWare.Core;
using DeadWare.Devices;
using Xunit;

namespace DeadWare.Devices.Tests;

public sealed class HostPipeServerTests : IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"dw-pipe-"+Guid.NewGuid().ToString("N"));
    readonly string name="DeadWare.Devices.Test."+Guid.NewGuid().ToString("N");
    readonly HostLog log;
    readonly CancellationTokenSource cts=new(TimeSpan.FromSeconds(20));
    public HostPipeServerTests(){log=new HostLog(dir);}
    public void Dispose(){cts.Cancel();log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    static HostState Sample()=>new(HostStatus.Running,"",false,[],[],new());
    async Task<NamedPipeClientStream> Connect()
    {
        var c=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        await c.ConnectAsync(5000,cts.Token);
        return c;
    }

    [Fact]
    public async Task Pipe_rejects_wrong_version()
    {
        var handled=0;
        await using var server=new HostPipeServer(name,m=>{handled++;return Task.FromResult<HostMessage>(new Reply(true,null));},log);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Version=2,Id=7},cts.Token);
        var reply=Assert.IsType<Reply>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.False(reply.Ok);
        Assert.Equal("version",reply.Error);
        Assert.Equal(7,reply.Id);
        Assert.Equal(0,handled);
    }

    [Fact]
    public async Task Pipe_round_trips_request_and_state_event()
    {
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new StateEvent(Sample()){Id=m.Id}),log);
        var run=server.RunAsync(cts.Token);
        await using var client=await Connect();
        await DeviceHostProtocol.WriteAsync(client,new GetStateRequest{Id=42},cts.Token);
        var answer=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        Assert.Equal(42,answer.Id);
        Assert.Equal(HostStatus.Running,answer.State.Status);
        var send=server.SendAsync(new StateEvent(Sample()){Id=0}); // the pipe is unbuffered: the write completes when the client reads
        var pushed=Assert.IsType<StateEvent>(await DeviceHostProtocol.ReadAsync(client,cts.Token));
        await send;
        Assert.Equal(0,pushed.Id);
    }

    [Fact]
    public async Task Pipe_serves_the_next_client_after_a_disconnect()
    {
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(new Reply(true,null){Id=m.Id}),log);
        var run=server.RunAsync(cts.Token);
        var first=await Connect();
        await DeviceHostProtocol.WriteAsync(first,new GetStateRequest{Id=1},cts.Token);
        await DeviceHostProtocol.ReadAsync(first,cts.Token);
        await first.DisposeAsync();
        for(var attempt=0;;attempt++)
        {
            try
            {
                await using var second=await Connect();
                await DeviceHostProtocol.WriteAsync(second,new GetStateRequest{Id=2},cts.Token);
                Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(second,cts.Token))!.Id);
                break;
            }
            catch(Exception e) when(attempt<20&&(e is TimeoutException||e is IOException)){await Task.Delay(50);}
        }
        Assert.False(run.IsFaulted);
    }

    [Fact]
    public async Task A_client_that_never_reads_is_dropped_and_the_next_client_is_served()
    {
        var big=new Dictionary<string,ZoneColorEntry>();
        for(var i=0;i<3000;i++) big[$"serial-{i}|location-{i}#0"]=new(){Color="#112233",ZoneName="Zone "+i};
        var settings=new LightingSettings{ZoneColors=big};
        await using var server=new HostPipeServer(name,m=>Task.FromResult<HostMessage>(m.Id==1?new StateEvent(new(HostStatus.Running,"",false,[],[],settings)){Id=1}:new Reply(true,null){Id=m.Id}),log,elevated:false,writeTimeout:TimeSpan.FromMilliseconds(300));
        var run=server.RunAsync(cts.Token);
        await using var stuck=await Connect();
        await DeviceHostProtocol.WriteAsync(stuck,new GetStateRequest{Id=1},cts.Token); // the reply is larger than the pipe buffer and is never read
        await Task.Delay(1000);
        await using var second=await Connect();
        await DeviceHostProtocol.WriteAsync(second,new GetStateRequest{Id=2},cts.Token);
        Assert.Equal(2,(await DeviceHostProtocol.ReadAsync(second,cts.Token))!.Id);
        Assert.False(run.IsCompleted);
    }

    [Fact]
    public void Sddl_denies_network_gives_user_read_write_and_admins_full()
    {
        var plain=HostPipeServer.Sddl("S-1-5-21-1-2-3-1001",false);
        Assert.Equal("D:(D;;GA;;;NU)(A;;GRGW;;;S-1-5-21-1-2-3-1001)(A;;GA;;;BA)",plain);
        Assert.Equal(plain+"S:(ML;;NW;;;HI)",HostPipeServer.Sddl("S-1-5-21-1-2-3-1001",true));
        var ps=new System.IO.Pipes.PipeSecurity();
        ps.SetSecurityDescriptorSddlForm(HostPipeServer.Sddl("S-1-5-21-1-2-3-1001",false));
        Assert.Contains("(D;;",ps.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access));
    }
}
