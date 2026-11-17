using System.IO.Pipes;
using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;
namespace DeadWare.App.Tests;
public class DeviceHostClientTests
{
    const string Exe=@"C:\OpenRGB\OpenRGB.exe";
    static readonly HostState Sample=new(HostStatus.Running,"ok",false,[],[],new LightingSettings());
    static readonly TimeSpan Step=TimeSpan.FromMilliseconds(250);
    sealed class FakeProcess:IDeviceHostProcess
    {
        readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id {get;init;}=1;public volatile bool Killed;
        public bool HasExited=>exit.Task.IsCompleted;
        public Task WaitForExitAsync(CancellationToken token)=>exit.Task.WaitAsync(token);
        public void Kill() {Killed=true;exit.TrySetResult();}
        public void Exit()=>exit.TrySetResult();
        public void Dispose() {}
    }
    sealed class FakeLauncher:IDeviceHostLauncher
    {
        public readonly List<(string Path,bool Startup)> Starts=new();
        public readonly List<FakeProcess> Processes=new();
        public FakeProcess? Running;
        public IDeviceHostProcess Start(string openRgbPath,bool startup) {lock(Starts){Starts.Add((openRgbPath,startup));var p=new FakeProcess{Id=Starts.Count+100};Processes.Add(p);return p;}}
        public IDeviceHostProcess? FindRunning()=>Running;
        public int Launched {get{lock(Starts)return Starts.Count;}}
    }
    /// <summary>A scripted host: each connection is a real test-named pipe, answered the way DeviceHost answers.</summary>
    sealed class FakeHost
    {
        public readonly List<HostMessage> Received=new();
        public Func<HostMessage,HostMessage[]> Answer=m=>m is GetStateRequest?[new StateEvent(Sample){Id=m.Id}]:[new Reply(true,null){Id=m.Id},new StateEvent(Sample)];
        public NamedPipeServerStream? Server;public int Connections;
        public bool Saw<T>() {lock(Received)return Received.Any(m=>m is T);}
        public async Task<Stream> Connect(CancellationToken token)
        {
            string name="DeadWare.Devices.Tests."+Guid.NewGuid().ToString("N");
            var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(token),client.ConnectAsync(token));
            Server=server;Interlocked.Increment(ref Connections);
            _=Task.Run(async ()=>
            {
                try
                {
                    while(await DeviceHostProtocol.ReadAsync(server,CancellationToken.None) is {} m)
                    {lock(Received)Received.Add(m);foreach(var r in Answer(m))await DeviceHostProtocol.WriteAsync(server,r,CancellationToken.None);}
                }
                catch(Exception) {}
            });
            return client;
        }
        public Task Push(HostMessage m)=>DeviceHostProtocol.WriteAsync(Server!,m,CancellationToken.None);
    }
    readonly FakeTimeProvider time=new();readonly FakeLauncher launcher=new();readonly FakeHost host=new();
    DeviceHostClient Make(Func<CancellationToken,Task<Stream>>? connect=null,bool elevated=false)=>new(Exe,launcher,connect??host.Connect,time,elevated);
    // Waits for a condition; unless told not to, it advances the fake clock a step per tick so timers run.
    async Task Until(Func<bool> condition,bool advance=true)
    {
        for(int i=0;i<6000&&!condition();i++){if(advance)time.Advance(Step);await Task.Delay(1);}
        Assert.True(condition(),"condition not reached");
    }
    Func<CancellationToken,Task<Stream>> NoPipeUntil(Func<bool> up)=>token=>up()?host.Connect(token):Task.FromException<Stream>(new IOException("no pipe"));

    [Fact] public async Task Starts_host_when_none_running_and_connects()
    {
        bool up=false;await using var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(true,CancellationToken.None);
        await Until(()=>launcher.Launched==1);
        Assert.Equal((Exe,true),launcher.Starts[0]);
        up=true;await Until(()=>client.State is not null);
        Assert.Equal(HostStatus.Running,client.State!.Status);Assert.Null(client.Problem);Assert.Equal(1,launcher.Launched);
        var reply=await client.SendAsync(new SetLightsOnRequest(false),CancellationToken.None);
        Assert.True(reply.Ok);
        lock(host.Received)Assert.IsType<SetLightsOnRequest>(host.Received.Last(m=>m is SetLightsOnRequest));
    }
    [Fact] public async Task Connects_to_running_host_without_launching()
    {
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        Assert.Equal(0,launcher.Launched);Assert.Equal(1,host.Connections);
    }
    [Fact] public async Task Found_running_host_is_waited_for_not_launched()
    {
        launcher.Running=new FakeProcess{Id=7};bool up=false;await using var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(false,CancellationToken.None);
        for(int i=0;i<20;i++){time.Advance(Step);await Task.Delay(1);}
        up=true;await Until(()=>client.State is not null);
        Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task Unexpected_host_exit_relaunches_after_four_seconds()
    {
        bool up=false;await using var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>launcher.Launched==1);
        up=true;await Until(()=>client.State is not null);
        var before=time.GetUtcNow();
        launcher.Processes[0].Exit();
        await Until(()=>launcher.Launched==2);
        var waited=time.GetUtcNow()-before;
        Assert.True(waited>=TimeSpan.FromSeconds(4),$"relaunched after {waited}");
        Assert.True(waited<=TimeSpan.FromSeconds(6),$"relaunched after {waited}");
        Assert.False(launcher.Starts[1].Startup);
    }
    [Fact] public async Task Fifth_exit_in_ten_minutes_sets_Problem_and_stops_relaunching()
    {
        await using var client=Make(_=>Task.FromException<Stream>(new IOException("no pipe")));
        await client.StartAsync(false,CancellationToken.None);
        for(int i=0;i<5;i++)
        {
            await Until(()=>launcher.Launched==i+1);
            launcher.Processes[i].Exit();
        }
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper stopped 5 times in 10 minutes. Turn lighting off and on in Settings to try again.",client.Problem);
        for(int i=0;i<40;i++){time.Advance(TimeSpan.FromSeconds(10));await Task.Delay(1);}
        Assert.Equal(5,launcher.Launched);
    }
    [Fact] public async Task Stop_sends_StopRequest_and_waits_up_to_five_seconds_then_kills()
    {
        var process=new FakeProcess();launcher.Running=process;
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var stop=client.StopAsync();
        await Until(()=>host.Saw<StopRequest>(),advance:false);
        await Task.Delay(100);
        time.Advance(TimeSpan.FromSeconds(4));await Task.Delay(50);
        Assert.False(process.Killed);Assert.False(stop.IsCompleted);
        await Until(()=>stop.IsCompleted);
        await stop;
        Assert.True(process.Killed);
        await client.StopAsync();
    }
    [Fact] public async Task Stop_does_not_kill_a_host_that_exits()
    {
        var process=new FakeProcess();launcher.Running=process;
        host.Answer=m=>{if(m is StopRequest)process.Exit();return [new Reply(true,null){Id=m.Id}];};
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Connections==1);
        await client.StopAsync();
        Assert.False(process.Killed);Assert.Equal(0,launcher.Launched);Assert.True(host.Saw<StopRequest>());
    }
    [Fact] public async Task Access_denied_on_connect_reports_elevation_problem_and_does_not_launch()
    {
        await using var client=Make(_=>Task.FromException<Stream>(new UnauthorizedAccessException()));
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper is running as administrator. Restart DeadWare as administrator to control it.",client.Problem);
        for(int i=0;i<10;i++){time.Advance(TimeSpan.FromSeconds(5));await Task.Delay(1);}
        Assert.Equal(0,launcher.Launched);
        var reply=await client.SendAsync(new RescanRequest(),CancellationToken.None);
        Assert.False(reply.Ok);Assert.Equal("The lighting helper isn't connected.",reply.Error);
        await client.StopAsync();
    }
    [Fact] public async Task Unverified_pipe_server_reports_problem_and_does_not_launch()
    {
        await using var client=Make(_=>Task.FromException<Stream>(new InvalidDataException("wrong server")));
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("Another program is using the lighting helper's connection, so DeadWare won't send it commands.",client.Problem);
        for(int i=0;i<10;i++){time.Advance(TimeSpan.FromSeconds(5));await Task.Delay(1);}
        Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task Version_mismatch_sets_Problem_and_closes_without_relaunching()
    {
        host.Answer=m=>[new Reply(true,null){Id=m.Id,Version=2}];
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper is a different version. Restart DeadWare.",client.Problem);
        for(int i=0;i<10;i++){time.Advance(TimeSpan.FromSeconds(5));await Task.Delay(1);}
        Assert.Equal(1,host.Connections);Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task SendAsync_times_out_without_throwing_and_state_events_raise_Changed()
    {
        host.Answer=m=>[];
        await using var client=Make();int changed=0;client.Changed+=()=>Interlocked.Increment(ref changed);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Saw<GetStateRequest>());
        var send=client.SendAsync(new RescanRequest(),CancellationToken.None);
        await Until(()=>send.IsCompleted);
        Assert.Equal("The lighting helper didn't answer.",(await send).Error);
        await host.Push(new StateEvent(Sample));
        await Until(()=>client.State is not null&&Volatile.Read(ref changed)>=1,advance:false);
    }
    [Fact] public async Task GetState_completes_when_its_state_event_arrives()
    {
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Saw<GetStateRequest>());
        var reply=await client.SendAsync(new GetStateRequest(),CancellationToken.None);
        Assert.True(reply.Ok);Assert.NotNull(client.State);
    }
    [Fact] public async Task Pipe_closed_while_host_alive_reconnects_without_launching()
    {
        launcher.Running=new FakeProcess();
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        host.Server!.Dispose();
        await Until(()=>host.Connections==2);
        Assert.Equal(0,launcher.Launched);Assert.Null(client.Problem);
    }
    [Fact] public async Task Elevated_window_restarts_non_elevated_host()
    {
        var old=new FakeProcess();launcher.Running=old;
        await using var client=Make(elevated:true);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Saw<StopRequest>());
        Assert.False(old.Killed);
        await Until(()=>old.Killed);
        await Until(()=>launcher.Launched==1);
        Assert.False(launcher.Starts[0].Startup);Assert.Null(client.Problem);
    }
    [Fact] public async Task Non_elevated_window_leaves_non_elevated_host_alone()
    {
        launcher.Running=new FakeProcess();
        await using var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        for(int i=0;i<40;i++){time.Advance(Step);await Task.Delay(1);}
        Assert.False(host.Saw<StopRequest>());Assert.Equal(0,launcher.Launched);
    }
}
