using System.Diagnostics;
using System.IO.Pipes;
using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;
namespace DeadWare.App.Tests;
/// <summary>
/// Fake time moves only where a test says so, and only once the client is known to wait on the timer the test means to fire
/// (<see cref="Elapse"/>). Every other wait polls in real time and fails the test after <see cref="Bound"/> instead of hanging the run.
/// </summary>
public class DeviceHostClientTests:IAsyncLifetime
{
    const string Exe=@"C:\OpenRGB\OpenRGB.exe";
    static readonly HostState Sample=new(HostStatus.Running,"ok",false,[],[],new LightingSettings());
    static readonly TimeSpan Retry=TimeSpan.FromMilliseconds(500),Window=TimeSpan.FromSeconds(5),Tick=TimeSpan.FromTicks(1),Bound=TimeSpan.FromSeconds(10);
    sealed class FakeProcess:IDeviceHostProcess
    {
        readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id {get;init;}=1;public volatile bool Killed;
        /// <summary>The tree kill fails part-way, as .NET reports it: an AggregateException, and the process keeps running.</summary>
        public bool KillFails {get;init;}
        public bool HasExited=>exit.Task.IsCompleted;
        public Task WaitForExitAsync(CancellationToken token)=>exit.Task.WaitAsync(token);
        public void Kill() {Killed=true;if(KillFails)throw new AggregateException("Not all processes in the process tree could be terminated.",new System.ComponentModel.Win32Exception(5));exit.TrySetResult();}
        public void Exit()=>exit.TrySetResult();
        public void Dispose() {}
    }
    sealed class FakeLauncher:IDeviceHostLauncher
    {
        readonly List<(string Path,bool Startup)> starts=new();readonly List<FakeProcess> processes=new();int searches;
        public FakeProcess? Running;
        public IDeviceHostProcess Start(string openRgbPath,bool startup) {lock(starts){starts.Add((openRgbPath,startup));var p=new FakeProcess{Id=starts.Count+100};processes.Add(p);return p;}}
        public IDeviceHostProcess? FindRunning() {Interlocked.Increment(ref searches);return Running;}
        public int Launched {get{lock(starts)return starts.Count;}}
        public int Searches=>Volatile.Read(ref searches);
        public (string Path,bool Startup) StartAt(int index) {lock(starts)return starts[index];}
        public FakeProcess Process(int index) {lock(starts)return processes[index];}
    }
    /// <summary>A fake clock that knows which timers are waiting and when each is due, so a test can wait until the client waits.</summary>
    sealed class Clock:FakeTimeProvider
    {
        sealed class Waiter(Clock clock):ITimer
        {
            readonly object sync=new();DateTimeOffset? due;bool done;
            public ITimer? Inner;
            public DateTimeOffset? Due {get {lock(sync)return done?null:due;}}
            public bool Done {get {lock(sync)return done;}}
            public void Arm(DateTimeOffset? at) {lock(sync)due=at;}
            public void Fired(TimeSpan period) {lock(sync){if(period==Timeout.InfiniteTimeSpan)done=true;else due+=period;}}
            public bool Change(TimeSpan dueTime,TimeSpan period)
            {
                var before=clock.GetUtcNow();bool changed=Inner!.Change(dueTime,period);
                lock(sync){done=false;due=before==clock.GetUtcNow()?At(before,dueTime):null;}
                return changed;
            }
            public void Dispose() {lock(sync)done=true;Inner?.Dispose();}
            public ValueTask DisposeAsync() {Dispose();return ValueTask.CompletedTask;}
        }
        readonly List<Waiter> waiters=new();
        static DateTimeOffset? At(DateTimeOffset now,TimeSpan dueTime)=>dueTime==Timeout.InfiniteTimeSpan?null:now+dueTime;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
        {
            var waiter=new Waiter(this);var before=GetUtcNow();
            waiter.Inner=base.CreateTimer(s=>{waiter.Fired(period);callback(s);},state,dueTime,period);
            // Should the clock move during the call, the due time is unknown and the timer never counts as waiting.
            waiter.Arm(before==GetUtcNow()?At(before,dueTime):null);
            lock(waiters)waiters.Add(waiter);
            return waiter;
        }
        /// <summary>True while a timer is due exactly <paramref name="delay"/> from now: something started waiting that long since the clock last moved.</summary>
        public bool Waiting(TimeSpan delay)
        {
            var at=GetUtcNow()+delay;
            lock(waiters){waiters.RemoveAll(w=>w.Done);return waiters.Any(w=>w.Due==at);}
        }
    }
    /// <summary>A scripted host: each connection is a real test-named pipe, answered the way DeviceHost answers.</summary>
    sealed class FakeHost
    {
        public static HostMessage[] Answers(HostMessage m)=>m is GetStateRequest?[new StateEvent(Sample){Id=m.Id}]:[new Reply(true,null){Id=m.Id},new StateEvent(Sample)];
        public readonly List<HostMessage> Received=new();
        public Func<HostMessage,HostMessage[]> Answer=Answers;
        readonly List<Stream> pipes=new();
        public volatile NamedPipeServerStream? Server;public int Connections;
        public bool Saw<T>() {lock(Received)return Received.Any(m=>m is T);}
        public int Count<T>() {lock(Received)return Received.Count(m=>m is T);}
        public async Task<Stream> Connect(CancellationToken token)
        {
            string name="DeadWare.Devices.Tests."+Guid.NewGuid().ToString("N");
            var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
            var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
            lock(pipes){pipes.Add(server);pipes.Add(client);}
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
        public void Close() {lock(pipes)foreach(var pipe in pipes)pipe.Dispose();}
    }
    readonly Clock time=new();readonly FakeLauncher launcher=new();readonly FakeHost host=new();readonly List<DeviceHostClient> clients=new();
    DeviceHostClient Make(Func<CancellationToken,Task<Stream>>? connect=null,bool elevated=false)
    {var client=new DeviceHostClient(Exe,launcher,connect??host.Connect,time,elevated);clients.Add(client);return client;}
    public Task InitializeAsync()=>Task.CompletedTask;
    /// <summary>Disposes every client within <see cref="Bound"/>, so a teardown stuck on the fake clock fails its test instead of hanging the run.</summary>
    public async Task DisposeAsync() {try {foreach(var client in clients)await client.DisposeAsync().AsTask().WaitAsync(Bound);} finally {host.Close();}}
    /// <summary>Polls in real time, never moving the fake clock, until the condition holds; fails the test after <see cref="Bound"/>.</summary>
    static async Task Until(Func<bool> condition,string what="condition")
    {
        var waited=Stopwatch.StartNew();
        while(!condition()){Assert.True(waited.Elapsed<Bound,$"{what} not reached within {Bound.TotalSeconds} s");await Task.Delay(1);}
    }
    /// <summary>Waits until the client waits on a timer due exactly <paramref name="delay"/> from now, then moves the clock by that much so the timer fires.</summary>
    async Task Elapse(TimeSpan delay) {await Until(()=>time.Waiting(delay),$"a {delay} wait");time.Advance(delay);}
    /// <summary>Gives anything a timer fired too early would have run the chance to show before a test asserts that nothing happened.</summary>
    static Task Settle()=>Task.Delay(50);
    Func<CancellationToken,Task<Stream>> NoPipeUntil(Func<bool> up)=>token=>up()?host.Connect(token):Task.FromException<Stream>(new IOException("no pipe"));

    [Fact] public async Task Starts_host_when_none_running_and_connects()
    {
        bool up=false;var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(true,CancellationToken.None);
        await Until(()=>launcher.Launched==1);
        Assert.Equal((Exe,true),launcher.StartAt(0));
        up=true;await Elapse(Retry);
        await Until(()=>client.State is not null);
        Assert.Equal(HostStatus.Running,client.State!.Status);Assert.Null(client.Problem);Assert.Equal(1,launcher.Launched);
        var reply=await client.SendAsync(new SetLightsOnRequest(false),CancellationToken.None).WaitAsync(Bound);
        Assert.True(reply.Ok);
        lock(host.Received)Assert.Contains(host.Received,m=>m is SetLightsOnRequest{On:false});
    }
    [Fact] public async Task Connects_to_running_host_without_launching()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        Assert.Equal(0,launcher.Launched);Assert.Equal(1,host.Connections);
    }
    [Fact] public async Task Found_running_host_is_waited_for_not_launched()
    {
        var found=new FakeProcess{Id=7};launcher.Running=found;bool up=false;var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(false,CancellationToken.None);
        await Elapse(Retry);await Elapse(Retry);
        Assert.Equal(1,launcher.Searches);Assert.Equal(0,launcher.Launched);
        up=true;await Elapse(Retry);
        await Until(()=>client.State is not null);
        // One search, while the pipe was down: the host it found stayed tracked, so connecting needed no second search.
        Assert.Equal(1,launcher.Searches);Assert.Equal(0,launcher.Launched);
        // Tracked, so its exit is an unexpected exit, relaunched after the backoff.
        found.Exit();
        await Elapse(RestartBackoff.Base);
        await Until(()=>launcher.Launched==1);
    }
    [Fact] public async Task Unexpected_host_exit_relaunches_after_four_seconds()
    {
        bool up=false;var client=Make(NoPipeUntil(()=>up));
        await client.StartAsync(true,CancellationToken.None);
        await Until(()=>launcher.Launched==1);
        up=true;await Elapse(Retry);
        await Until(()=>client.State is not null);
        launcher.Process(0).Exit();
        await Until(()=>time.Waiting(RestartBackoff.Base),"the backoff wait");
        // The dead host's state is gone, so the page can tell a relaunch from a running host.
        Assert.Null(client.State);
        time.Advance(RestartBackoff.Base-Tick);await Settle();
        Assert.Equal(1,launcher.Launched);
        time.Advance(Tick);
        await Until(()=>launcher.Launched==2);
        // Only the sign-in launch passes --startup; a relaunch after a crash never does.
        Assert.True(launcher.StartAt(0).Startup);Assert.False(launcher.StartAt(1).Startup);
    }
    [Fact] public async Task Fifth_exit_in_ten_minutes_sets_Problem_and_stops_relaunching()
    {
        var client=Make(_=>Task.FromException<Stream>(new IOException("no pipe")));
        await client.StartAsync(false,CancellationToken.None);
        TimeSpan[] backoff=[TimeSpan.FromSeconds(4),TimeSpan.FromSeconds(8),TimeSpan.FromSeconds(16),TimeSpan.FromSeconds(32)];
        for(int i=0;i<5;i++)
        {
            await Until(()=>launcher.Launched==i+1);
            launcher.Process(i).Exit();
            await Elapse(Retry);
            if(i<4)await Elapse(backoff[i]);
        }
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper stopped 5 times in 10 minutes. Turn lighting off and on in Settings to try again.",client.Problem);
        time.Advance(TimeSpan.FromMinutes(10));await Settle();
        Assert.Equal(5,launcher.Launched);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task Stop_sends_StopRequest_and_waits_up_to_five_seconds_then_kills(bool answersStop)
    {
        // The host never exits; without answersStop it is hung and never answers the StopRequest either.
        var process=new FakeProcess();launcher.Running=process;
        if(!answersStop)host.Answer=m=>m is StopRequest?[]:FakeHost.Answers(m);
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var stop=client.StopAsync();
        await Until(()=>host.Saw<StopRequest>());
        // One 5 s window from the moment the stop began, answered or not.
        time.Advance(Window-Tick);await Settle();
        Assert.False(process.Killed);Assert.False(stop.IsCompleted);
        time.Advance(Tick);
        await stop.WaitAsync(Bound);
        Assert.True(process.Killed);
        await client.StopAsync().WaitAsync(Bound);
        Assert.Equal(1,host.Count<StopRequest>());
    }
    /// <summary>
    /// A hung host whose tree kill fails part-way, as when the host's job is already ending its OpenRGB: Process.Kill(entireProcessTree:true)
    /// throws AggregateException. The stop still completes, its cached task never faults, and the client can be disposed.
    /// </summary>
    [Fact] public async Task Stop_completes_when_the_tree_kill_fails_part_way()
    {
        var process=new FakeProcess{KillFails=true};launcher.Running=process;
        host.Answer=m=>m is StopRequest?[]:FakeHost.Answers(m);
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var stop=client.StopAsync();
        await Until(()=>host.Saw<StopRequest>());
        time.Advance(Window);
        await stop.WaitAsync(Bound);
        Assert.True(process.Killed);
        Assert.False(stop.IsFaulted);
        await client.StopAsync().WaitAsync(Bound);
        await client.DisposeAsync().AsTask().WaitAsync(Bound);
    }
    [Fact] public async Task Stop_does_not_kill_a_host_that_exits()
    {
        var process=new FakeProcess();launcher.Running=process;
        host.Answer=m=>{if(m is StopRequest)process.Exit();return [new Reply(true,null){Id=m.Id}];};
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        // The client asks for state only after adopting the host and registering the connection, so the stop can reach the host.
        await Until(()=>host.Saw<GetStateRequest>());
        await client.StopAsync().WaitAsync(Bound);
        Assert.False(process.Killed);Assert.Equal(0,launcher.Launched);Assert.Equal(1,host.Count<StopRequest>());
    }
    [Fact] public async Task Stop_racing_a_just_completed_connect_still_sends_StopRequest()
    {
        var process=new FakeProcess();launcher.Running=process;
        host.Answer=m=>{if(m is StopRequest)process.Exit();return [new Reply(true,null){Id=m.Id}];};
        var stopping=new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);DeviceHostClient? client=null;
        // The stop begins once the pipe connected, before the client could adopt the host or register the connection.
        client=Make(async token=>{var stream=await host.Connect(token);stopping.TrySetResult(client!.StopAsync());return stream;});
        await client.StartAsync(false,CancellationToken.None);
        await (await stopping.Task.WaitAsync(Bound)).WaitAsync(Bound);
        Assert.Equal(1,host.Count<StopRequest>());Assert.False(process.Killed);Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task Access_denied_on_connect_reports_elevation_problem_and_does_not_launch()
    {
        var client=Make(_=>Task.FromException<Stream>(new UnauthorizedAccessException()));
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper is running as administrator. Restart DeadWare as administrator to control it.",client.Problem);
        time.Advance(TimeSpan.FromMinutes(1));await Settle();
        Assert.Equal(0,launcher.Launched);
        var reply=await client.SendAsync(new RescanRequest(),CancellationToken.None).WaitAsync(Bound);
        Assert.False(reply.Ok);Assert.Equal("The lighting helper isn't connected.",reply.Error);
        await client.StopAsync().WaitAsync(Bound);
    }
    [Fact] public async Task Unverified_pipe_server_reports_problem_and_does_not_launch()
    {
        var client=Make(_=>Task.FromException<Stream>(new InvalidDataException("wrong server")));
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("Another program is using the lighting helper's connection, so DeadWare won't send it commands.",client.Problem);
        time.Advance(TimeSpan.FromMinutes(1));await Settle();
        Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task Version_mismatch_sets_Problem_and_closes_without_relaunching()
    {
        host.Answer=m=>[new Reply(true,null){Id=m.Id,Version=2}];
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Problem is not null);
        Assert.Equal("The lighting helper is a different version. Restart DeadWare.",client.Problem);
        time.Advance(TimeSpan.FromMinutes(1));await Settle();
        Assert.Equal(1,host.Connections);Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task SendAsync_times_out_without_throwing_and_state_events_raise_Changed()
    {
        host.Answer=m=>[];
        var client=Make();int changed=0;client.Changed+=()=>Interlocked.Increment(ref changed);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Saw<GetStateRequest>());
        var send=client.SendAsync(new RescanRequest(),CancellationToken.None);
        await Until(()=>host.Saw<RescanRequest>());
        time.Advance(Window-Tick);await Settle();
        Assert.False(send.IsCompleted);
        time.Advance(Tick);
        Assert.Equal("The lighting helper didn't answer.",(await send.WaitAsync(Bound)).Error);
        Assert.Equal(0,Volatile.Read(ref changed));
        await host.Push(new StateEvent(Sample));
        await Until(()=>client.State is not null&&Volatile.Read(ref changed)==1);
    }
    [Fact] public async Task GetState_completes_when_its_state_event_arrives()
    {
        host.Answer=m=>m is GetStateRequest?[new StateEvent(Sample with {Message="state "+m.Id}){Id=m.Id}]:FakeHost.Answers(m);
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var reply=await client.SendAsync(new GetStateRequest(),CancellationToken.None).WaitAsync(Bound);
        Assert.True(reply.Ok);
        HostMessage asked;lock(host.Received)asked=host.Received.Last(m=>m is GetStateRequest);
        // The state that answered this request, not the one from connecting.
        Assert.Equal(2,host.Count<GetStateRequest>());Assert.Equal("state "+asked.Id,client.State!.Message);
    }
    [Fact] public async Task Pipe_closed_while_host_alive_reconnects_without_launching()
    {
        launcher.Running=new FakeProcess();
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        // Five drops, each reconnected after the 500 ms retry: a drop counted as a failure would wait out a 4 s backoff, and the fifth would give up.
        for(int drop=1;drop<=5;drop++)
        {
            host.Server!.Dispose();
            await Until(()=>client.State is null,"the dropped host's state cleared");
            await Elapse(Retry);
            await Until(()=>host.Connections==drop+1&&client.State is not null);
        }
        Assert.Equal(0,launcher.Launched);Assert.Null(client.Problem);
    }
    [Fact] public async Task Elevated_window_restarts_non_elevated_host()
    {
        var old=new FakeProcess();launcher.Running=old;
        var client=Make(elevated:true);
        await client.StartAsync(true,CancellationToken.None);
        await Until(()=>host.Saw<StopRequest>());
        // The restart's stop has the same 5 s window: the old host answers but never exits, so it is killed when the window closes.
        time.Advance(Window-Tick);await Settle();
        Assert.False(old.Killed);
        time.Advance(Tick);
        await Until(()=>old.Killed);
        await Elapse(RestartBackoff.Base);
        await Until(()=>launcher.Launched==1);
        // A deliberate restart is a fresh launch, never a sign-in one.
        Assert.False(launcher.StartAt(0).Startup);Assert.Null(client.Problem);
    }
    [Fact] public async Task Non_elevated_window_leaves_non_elevated_host_alone()
    {
        launcher.Running=new FakeProcess();
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        time.Advance(TimeSpan.FromSeconds(10));await Settle();
        Assert.False(host.Saw<StopRequest>());Assert.Equal(0,launcher.Launched);
    }
    [Fact] public async Task DisposeAsync_twice_and_StopAsync_after_it_are_harmless()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        await Task.WhenAll(client.DisposeAsync().AsTask(),client.DisposeAsync().AsTask()).WaitAsync(Bound);
        await client.DisposeAsync().AsTask().WaitAsync(Bound);
        await client.StopAsync().WaitAsync(Bound);
        // Disposing never asks the host to stop.
        Assert.False(host.Saw<StopRequest>());
    }
    [Fact] public async Task A_throwing_Changed_subscriber_does_not_end_the_connection()
    {
        var client=Make();int calls=0;
        client.Changed+=()=>throw new InvalidOperationException("subscriber failed");
        client.Changed+=()=>Interlocked.Increment(ref calls);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null&&Volatile.Read(ref calls)>=1,"the second subscriber called");
        var reply=await client.SendAsync(new SetLightsOnRequest(true),CancellationToken.None).WaitAsync(Bound);
        await Settle();
        Assert.True(reply.Ok);Assert.Null(client.Problem);
    }
    [Fact] public async Task Real_connect_rejects_a_pipe_server_that_is_not_DeadWare_Devices()
    {
        string name="DeadWare.Devices.Tests."+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var accepted=server.WaitForConnectionAsync();
        // This test process serves the pipe, so the identity check finds the test host's image, not DeadWare.Devices.exe.
        await Assert.ThrowsAsync<InvalidDataException>(()=>DeviceHostPipe.ConnectAsync(name,CancellationToken.None)).WaitAsync(Bound);
        await accepted.WaitAsync(Bound);
        // The rejected stream was disposed: the server sees the client gone.
        Assert.Equal(0,await server.ReadAsync(new byte[1]).AsTask().WaitAsync(Bound));
    }
}
