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
    DeviceHostClient Make(Func<CancellationToken,Task<Stream>>? connect=null,bool elevated=false,HostLaunchPacer? pacer=null)
    {var client=new DeviceHostClient(Exe,launcher,connect??host.Connect,time,elevated,pacer);clients.Add(client);return client;}
    public Task InitializeAsync()=>Task.CompletedTask;
    /// <summary>Disposes every client within <see cref="Bound"/>, so a teardown stuck on the fake clock fails its test instead of hanging the run.</summary>
    public async Task DisposeAsync() {try {foreach(var client in clients)await client.DisposeAsync().AsTask().WaitAsync(Bound);} finally {host.Close();}}
    /// <summary>Polls in real time, never moving the fake clock, until the condition holds; fails the test after <see cref="Bound"/>.</summary>
    static async Task Until(Func<bool> condition,string what="condition")
    {
        var waited=Stopwatch.StartNew();
        while(!condition()){Assert.True(waited.Elapsed<Bound,$"{what} not reached within {Bound.TotalSeconds} s");await Task.Delay(1);}
    }
    /// <summary>
    /// Waits until the client waits on a timer due exactly <paramref name="delay"/> from now, then moves the clock by that much so the timer fires.
    /// The client may still be reacting when this returns: until a test first yields, xUnit's AsyncTestSyncContext is current, and the client's
    /// continuation then runs on the thread pool instead of inside Advance. So a test changes what the client reads next only once the client
    /// waits again (polish round 3, item 1).
    /// </summary>
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
    /// <summary>Lighting 3b: a key paint request travels whole, its painted keys included, and its reply comes back.</summary>
    [Fact] public async Task Sends_a_key_paint_request_and_gets_its_reply()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var keys=new Dictionary<int,PaintedKey>{[3]=new PaintedKey{Name="Key: W",Color="#FF0000"},[40]=new PaintedKey{Name="Key: A",Color="#00FF00"}};
        var reply=await client.SendAsync(new SetDeviceKeysRequest("kb|1",keys),CancellationToken.None).WaitAsync(Bound);
        Assert.True(reply.Ok);
        SetDeviceKeysRequest sent;
        lock(host.Received)sent=Assert.Single(host.Received.OfType<SetDeviceKeysRequest>());
        Assert.Equal("kb|1",sent.DeviceKey);
        Assert.Equal(["3=Key: W/#FF0000","40=Key: A/#00FF00"],sent.Keys!.OrderBy(e=>e.Key).Select(e=>$"{e.Key}={e.Value.Name}/{e.Value.Color}"));
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
        // Waiting to retry again proves the second retry's connect found no pipe, so the pipe comes up only for the third.
        await Until(()=>time.Waiting(Retry),"the third retry's wait");
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
            // Waiting to retry proves the launch has finished and, for a relaunch, that the client found the new host running: one that
            // exits before that check counts as exited at once, with no retry to elapse.
            await Until(()=>time.Waiting(Retry),"the retry wait after the launch");
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
    /// <summary>
    /// Ruling AP: turning lighting off and on, or choosing another OpenRGB, retires one client and starts another. The new client never
    /// launches a host sooner than 4 s after the old host exited, since OpenRGB restarted faster than USB handles are released can detect
    /// devices but not write to them.
    /// </summary>
    [Fact] public async Task A_new_client_launches_no_sooner_than_four_seconds_after_the_last_host_exited()
    {
        var pacer=new HostLaunchPacer(time);
        var process=new FakeProcess();launcher.Running=process;
        host.Answer=m=>{if(m is StopRequest)process.Exit();return m is GetStateRequest?FakeHost.Answers(m):[new Reply(true,null){Id=m.Id}];};
        var first=Make(pacer:pacer);
        await first.StartAsync(false,CancellationToken.None);
        await Until(()=>first.State is not null);
        await first.StopAsync().WaitAsync(Bound);
        await first.DisposeAsync().AsTask().WaitAsync(Bound);
        Assert.False(process.Killed);
        launcher.Running=null;
        var second=Make(_=>Task.FromException<Stream>(new IOException("no pipe")),pacer:pacer);
        await second.StartAsync(false,CancellationToken.None);
        await Until(()=>time.Waiting(RestartBackoff.Base),"the launch pacing");
        time.Advance(RestartBackoff.Base-Tick);await Settle();
        Assert.Equal(0,launcher.Launched);
        time.Advance(Tick);
        await Until(()=>launcher.Launched==1);
    }
    [Fact] public async Task Stop_during_the_launch_pacing_launches_nothing()
    {
        var pacer=new HostLaunchPacer(time);pacer.HostExited();
        var client=Make(_=>Task.FromException<Stream>(new IOException("no pipe")),pacer:pacer);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>time.Waiting(RestartBackoff.Base),"the launch pacing");
        await client.StopAsync().WaitAsync(Bound);
        time.Advance(TimeSpan.FromMinutes(1));await Settle();
        Assert.Equal(0,launcher.Launched);
    }
    /// <summary>The status line's words for a failed change: a short host code in plain words, and an unanswered change as one whose outcome is unknown.</summary>
    [Fact] public void Failed_change_text_explains_codes_and_an_unanswered_change()
    {
        Assert.Equal("The last change didn't apply: That zone isn't listed any more. Its device may have been unplugged.",DeviceHostClient.FailedChangeText("unknown zone"));
        Assert.Equal("The lighting helper didn't answer, so the last change may not have applied.",DeviceHostClient.FailedChangeText("The lighting helper didn't answer."));
        Assert.Equal("The last change didn't apply.",DeviceHostClient.FailedChangeText(null));
        Assert.Equal("The last change didn't apply: Colour must be #RRGGBB.",DeviceHostClient.FailedChangeText("Colour must be #RRGGBB."));
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
        await host.Push(new StateEvent(Sample)).WaitAsync(Bound); // a client that stopped reading would otherwise hang the run
        await Until(()=>client.State is not null&&Volatile.Read(ref changed)==1);
    }
    [Fact] public async Task Hub_readings_keep_their_age_and_go_with_the_connection()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        Assert.Null(client.Hub);
        await Driving(client);
        await host.Push(new HubReadingsEvent(new HubReadings([new(13,"pump","Pump",2774,43.7)],300))).WaitAsync(Bound);
        await Until(()=>client.Hub is not null);
        var (readings,age)=client.Hub!.Value;
        Assert.Equal(2774,Assert.Single(readings.Parts).Rpm);Assert.Equal(TimeSpan.FromMilliseconds(300),age);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2300),client.Hub!.Value.Age);
        host.Close();
        await Until(()=>client.Hub is null,"the readings gone with the connection");
    }
    // Fix wave 1, D7: the readings go at once when a state says DeadWare is no longer driving the hub, not when they reach 10 s; driving and
    // switching (the take-over's check) keep them.
    [Fact] public async Task Hub_readings_go_when_the_hub_stops_being_driven()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        foreach(var gone in new[]{"letGo","gaveBack","off"})
        {
            await Driving(client);
            await host.Push(new HubReadingsEvent(new HubReadings([new(13,"pump","Pump",2774,43.7)]))).WaitAsync(Bound);
            await Until(()=>client.Hub is not null);
            await host.Push(new StateEvent(Sample with{Hub=new HubView("switching")})).WaitAsync(Bound);
            await Until(()=>client.State?.Hub?.State=="switching");
            Assert.NotNull(client.Hub);
            await host.Push(new StateEvent(Sample with{Hub=new HubView(gone)})).WaitAsync(Bound);
            await Until(()=>client.State?.Hub?.State==gone);
            Assert.Null(client.Hub);
        }
    }
    // Fix wave 2, G (review D1): a readings event that arrives while the state says DeadWare is not driving the hub (a round's readings sent in
    // the same host turn as a give-back, after its state) does not bring the readings back.
    // Fix wave 3, D7: the readings are checked right after they were handled, before anything else could clear them: a preview frame pushed after
    // them on the same pipe marks that point (a later state would clear stored readings itself and prove nothing).
    [Fact] public async Task A_readings_event_after_a_state_that_is_not_driving_is_dropped()
    {
        host.Answer=Subscribe();
        var client=Make();int hub=0,frames=0;client.HubReadingsChanged+=()=>Interlocked.Increment(ref hub);client.Preview+=_=>Interlocked.Increment(ref frames);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>host.Count<SubscribePreviewRequest>()==1);
        int n=0;
        foreach(var gone in new[]{"gaveBack","letGo","off"})
        {
            await host.Push(new StateEvent(Sample with{Hub=new HubView(gone)})).WaitAsync(Bound);
            await host.Push(new HubReadingsEvent(new HubReadings([new(13,"pump","Pump",2774,43.7)]))).WaitAsync(Bound);
            await host.Push(new PreviewEvent(Frame)).WaitAsync(Bound);
            n++;
            await Until(()=>Volatile.Read(ref frames)==n);
            Assert.Equal(gone,client.State?.Hub?.State);
            Assert.Null(client.Hub);
        }
        Assert.Equal(0,Volatile.Read(ref hub));
    }
    // Fix wave 3, D11: readings that arrive while the state says switching (the take-over's check, while the worker drives the hub) are kept and
    // raised.
    [Fact] public async Task A_readings_event_while_switching_is_kept()
    {
        var client=Make();int hub=0;client.HubReadingsChanged+=()=>Interlocked.Increment(ref hub);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        await host.Push(new StateEvent(Sample with{Hub=new HubView("switching")})).WaitAsync(Bound);
        await Until(()=>client.State?.Hub?.State=="switching");
        Assert.Null(client.Hub);
        await host.Push(new HubReadingsEvent(new HubReadings([new(13,"pump","Pump",2716,36.5)]))).WaitAsync(Bound);
        await Until(()=>Volatile.Read(ref hub)==1);
        Assert.Equal(2716,Assert.Single(client.Hub!.Value.Readings.Parts).Rpm);
    }
    async Task Driving(DeviceHostClient client)
    {
        await host.Push(new StateEvent(Sample with{Hub=new HubView("driving")})).WaitAsync(Bound);
        await Until(()=>client.State?.Hub?.State=="driving");
    }
    // A session built without its constructor, holding this client as its lighting client (and, when given, this collector).
    static MonitorSession SessionWith(DeviceHostClient client,Collector? collector=null)
    {
        var session=(MonitorSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MonitorSession));
        typeof(MonitorSession).GetProperty("Lighting",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,client);
        if(collector is not null) typeof(MonitorSession).GetField("<Collector>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,collector);
        return session;
    }
    // Fix wave 1, D5: the session ending's stop reaches a running lighting client: the host is sent its ordinary Stop.
    [Fact] public async Task The_session_ending_stop_sends_the_host_its_stop()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        var stop=SessionWith(client).StopLightingForSessionEnding();
        await Until(()=>host.Saw<StopRequest>(),"the host's Stop");
        Assert.False(stop.IsFaulted);
    }
    // Fix wave 1, D4: the session's tick hands the collector the lighting client's hub readings. Fix wave 2, G (review D5): through the tick's
    // own handler, Ticked, not through FeedHub called by the test. That the constructor subscribes Ticked to the timer is the next test's
    // (fix wave 3, D10).
    [Fact] public async Task The_session_feeds_the_collector_the_hub_readings()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        await Driving(client);
        await host.Push(new HubReadingsEvent(new HubReadings([new(13,"pump","Pump",2774,43.7),new(1,"fan","Fan",1660)]))).WaitAsync(Bound);
        await Until(()=>client.Hub is not null);
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5));
        var session=SessionWith(client,collector);
        typeof(MonitorSession).GetField("<Settings>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(session,new UserSettings{SelectedGpuId="gpu"});
        session.Ticked();
        var seen=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=r=>seen.TrySetResult(r);
        collector.Start();
        var reading=await seen.Task.WaitAsync(Bound);
        Assert.Equal(["Coolant","Pump","Fan 1"],reading.Sensors.Where(s=>s.HardwareType=="CorsairLink").Select(s=>s.Name));
        Assert.Equal(43.7,reading.Sample.Values["cpu/coolant"]);
    }
    // Fix wave 3, D10: the constructor subscribes Ticked to the session timer's Tick (timer.Tick+=Ticked). Running the constructor would read
    // the owner's real settings and open the history databases under the real %LOCALAPPDATA%\DeadWare, so the test reads its compiled body
    // instead: ldfld timer, ldarg.0, ldftn Ticked, newobj Action, callvirt ISessionTimer.add_Tick.
    [Fact] public void The_session_constructor_subscribes_Ticked_to_its_timer()
    {
        const System.Reflection.BindingFlags any=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
        var il=typeof(MonitorSession).GetConstructors().Single().GetMethodBody()!.GetILAsByteArray()!;
        var timer=typeof(MonitorSession).GetField("timer",any)!.MetadataToken;
        var ticked=typeof(MonitorSession).GetMethod("Ticked",any)!.MetadataToken;
        var addTick=typeof(ISessionTimer).GetEvent("Tick")!.AddMethod!.MetadataToken;
        static bool Token(byte[] b,int at,int token)=>at>=0&&at+4<=b.Length&&BitConverter.ToInt32(b,at)==token;
        int found=0;
        for(int i=6;i+16<=il.Length;i++)
            if(il[i]==0xFE&&il[i+1]==0x06&&Token(il,i+2,ticked)&&il[i-1]==0x02&&il[i-6]==0x7B&&Token(il,i-5,timer)&&il[i+6]==0x73&&il[i+11]==0x6F&&Token(il,i+12,addTick)) found++;
        Assert.Equal(1,found);
    }
    [Fact] public async Task A_readings_event_raises_only_its_own_event()
    {
        var client=Make();int changed=0,hub=0;
        client.Changed+=()=>Interlocked.Increment(ref changed);client.HubReadingsChanged+=()=>Interlocked.Increment(ref hub);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null&&Volatile.Read(ref changed)==1);
        await Driving(client);
        await Until(()=>Volatile.Read(ref changed)==2);
        await host.Push(new HubReadingsEvent(new HubReadings([new(1,"fan","Fan",1660)]))).WaitAsync(Bound);
        await Until(()=>Volatile.Read(ref hub)==1);
        Assert.Equal(2,Volatile.Read(ref changed));
        // A state pushed after the readings is read after them, so the readings' own raise has already been counted.
        await host.Push(new StateEvent(Sample)).WaitAsync(Bound);
        await Until(()=>Volatile.Read(ref changed)==3);
        Assert.Equal(1,Volatile.Read(ref hub));
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
    static readonly Dictionary<string,string> Frame=new(){["desk:0"]="#FF0000",["tower:1"]="#00FF80"};
    /// <summary>The host's answers, with a subscription acknowledged by a plain reply and then followed on the same pipe by <paramref name="after"/>, so a test's frame never races the host's own writes.</summary>
    static Func<HostMessage,HostMessage[]> Subscribe(Func<bool,HostMessage[]>? after=null)=>m=>m is SubscribePreviewRequest s?[new Reply(true,null){Id=m.Id},..(after?.Invoke(s.On)??[])]:FakeHost.Answers(m);
    List<HostMessage> Seen() {lock(host.Received)return [..host.Received];}
    [Fact] public async Task A_preview_event_raises_Preview_with_its_colours()
    {
        host.Answer=Subscribe(on=>on?[new PreviewEvent(Frame)]:[]);
        var client=Make();PreviewColors? got=null;client.Preview+=c=>Volatile.Write(ref got,c);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>Volatile.Read(ref got) is not null,"the preview raised");
        Assert.Equal(Frame,got!.Zones);Assert.Null(got.Lights);
    }
    [Fact] public async Task A_preview_with_lights_reaches_the_subscriber()
    {
        var lights=new Dictionary<string,string>{["kb|1"]="FF000000FF00"};
        host.Answer=Subscribe(on=>on?[new PreviewEvent(Frame,lights)]:[]);
        var client=Make();PreviewColors? got=null;client.Preview+=c=>Volatile.Write(ref got,c);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>Volatile.Read(ref got) is not null,"the preview raised");
        Assert.Equal(Frame,got!.Zones);Assert.Equal(lights,got.Lights);
    }
    [Fact] public async Task SetPreview_sends_a_subscribe_request()
    {
        host.Answer=Subscribe();
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);client.SetPreview(true);
        await Until(()=>host.Count<SubscribePreviewRequest>()==1);
        await Settle();
        Assert.Equal(1,host.Count<SubscribePreviewRequest>());
        client.SetPreview(false);
        await Until(()=>host.Count<SubscribePreviewRequest>()==2);
        client.SetPreview(true);
        await Until(()=>host.Count<SubscribePreviewRequest>()==3);
        await Settle();
        Assert.Equal([true,false,true],Seen().OfType<SubscribePreviewRequest>().Select(s=>s.On));
    }
    [Fact] public async Task The_subscription_is_sent_again_after_a_reconnect()
    {
        launcher.Running=new FakeProcess();host.Answer=Subscribe();
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>host.Count<SubscribePreviewRequest>()==1);
        int before=Seen().Count;
        host.Server!.Dispose();
        await Until(()=>client.State is null,"the dropped host's state cleared");
        await Elapse(Retry);
        await Until(()=>host.Connections==2&&host.Count<SubscribePreviewRequest>()==2,"the resubscribe");
        var fresh=Seen().Skip(before).ToList();
        int asked=fresh.FindIndex(m=>m is GetStateRequest),subscribed=fresh.FindIndex(m=>m is SubscribePreviewRequest);
        Assert.True(asked>=0&&subscribed>asked,"the new connection asks for the state before it subscribes");
        Assert.True(((SubscribePreviewRequest)fresh[subscribed]).On);
    }
    [Fact] public async Task SetPreview_before_the_first_connection_is_sent_once_connected()
    {
        host.Answer=Subscribe();
        var client=Make();
        client.SetPreview(true);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>host.Count<SubscribePreviewRequest>()==1);
        await Settle();
        var seen=Seen();
        Assert.Equal(1,host.Count<SubscribePreviewRequest>());
        Assert.True(seen.FindIndex(m=>m is GetStateRequest)<seen.FindIndex(m=>m is SubscribePreviewRequest{On:true}));
    }
    [Fact] public async Task A_preview_event_does_not_change_State_or_raise_Changed()
    {
        host.Answer=Subscribe(on=>on?[new PreviewEvent(Frame)]:[]);
        var client=Make();int changed=0,previews=0;HostState? seen=null;
        client.Changed+=()=>{Volatile.Write(ref seen,client.State);Interlocked.Increment(ref changed);};
        client.Preview+=_=>Interlocked.Increment(ref previews);
        client.SetPreview(true);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>Volatile.Read(ref previews)==1,"the preview raised");
        await Settle();
        Assert.Equal(1,Volatile.Read(ref changed));Assert.NotNull(seen);Assert.Same(seen,client.State);
    }
    [Fact] public async Task A_preview_event_while_not_wanted_is_dropped()
    {
        // A frame the host had already queued arrives just after the unsubscribe's reply.
        host.Answer=Subscribe(on=>on?[]:[new PreviewEvent(Frame)]);
        var client=Make();int previews=0;client.Preview+=_=>Interlocked.Increment(ref previews);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>host.Count<SubscribePreviewRequest>()==1);
        client.SetPreview(false);
        await Until(()=>host.Count<SubscribePreviewRequest>()==2);
        // The pipe is ordered: once this request's answer is read, the stale frame was read before it.
        Assert.True((await client.SendAsync(new GetStateRequest(),CancellationToken.None).WaitAsync(Bound)).Ok);
        await Settle();
        Assert.Equal(0,Volatile.Read(ref previews));
    }
    [Fact] public async Task A_throwing_Preview_subscriber_does_not_end_the_connection()
    {
        host.Answer=Subscribe(on=>on?[new PreviewEvent(Frame)]:[]);
        var client=Make();int calls=0;
        client.Preview+=_=>throw new InvalidOperationException("subscriber failed");
        client.Preview+=_=>Interlocked.Increment(ref calls);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.State is not null);
        client.SetPreview(true);
        await Until(()=>Volatile.Read(ref calls)==1,"the second subscriber called");
        var reply=await client.SendAsync(new SetLightsOnRequest(true),CancellationToken.None).WaitAsync(Bound);
        Assert.True(reply.Ok);Assert.Null(client.Problem);
    }
    [Fact] public async Task SetPreview_never_throws_unconnected_or_after_dispose()
    {
        bool up=false;var client=Make(NoPipeUntil(()=>up));
        client.SetPreview(true);client.SetPreview(false);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>launcher.Launched==1);
        client.SetPreview(true);
        await client.DisposeAsync().AsTask().WaitAsync(Bound);
        client.SetPreview(false);client.SetPreview(true);
        Assert.Equal(0,host.Count<SubscribePreviewRequest>());
    }
    [Fact] public async Task Seq_counts_every_state_and_never_resets_on_reconnect()
    {
        launcher.Running=new FakeProcess();
        var client=Make();
        Assert.Equal(new HostSnapshot(null,0),client.Snapshot());
        await client.StartAsync(false,CancellationToken.None);
        // The connection's GetState answer is the first state.
        await Until(()=>client.Snapshot().Seq==1,"the connection's state");
        await host.Push(new StateEvent(Sample)).WaitAsync(Bound);
        await host.Push(new StateEvent(Sample)).WaitAsync(Bound);
        await Until(()=>client.Snapshot().Seq==3,"two pushed states counted");
        host.Server!.Dispose();
        await Until(()=>client.State is null,"the dropped host's state cleared");
        // Clearing the state stores none, and the count stays.
        Assert.Equal(3,client.Snapshot().Seq);
        await Elapse(Retry);
        await Until(()=>host.Connections==2&&client.State is not null,"the reconnect's state");
        Assert.Equal(4,client.Snapshot().Seq);
    }
    /// <summary>The host's usual order, a reply and at once the state it caused: the reply counts only the states before it, every time.</summary>
    [Fact] public async Task A_tracked_reply_reports_the_states_seen_before_it()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Snapshot().Seq==1,"the connection's state");
        for(int i=0;i<50;i++)
        {
            long before=client.Snapshot().Seq;
            var tracked=await client.SendTrackedAsync(new SetLightsOnRequest(true),CancellationToken.None).WaitAsync(Bound);
            Assert.True(tracked.Reply.Ok);
            Assert.Equal(before,tracked.StatesBefore);
            await Until(()=>client.Snapshot().Seq==tracked.StatesBefore+1,"the state that followed the reply");
        }
    }
    [Fact] public async Task A_tracked_reply_is_exact_when_a_state_precedes_the_reply()
    {
        host.Answer=m=>m is SetLightsOnRequest?[new StateEvent(Sample),new Reply(true,null){Id=m.Id}]:FakeHost.Answers(m);
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Snapshot().Seq==1,"the connection's state");
        for(int i=0;i<20;i++)
        {
            long before=client.Snapshot().Seq;
            var tracked=await client.SendTrackedAsync(new SetLightsOnRequest(true),CancellationToken.None).WaitAsync(Bound);
            Assert.True(tracked.Reply.Ok);
            Assert.Equal(before+1,tracked.StatesBefore);
            Assert.Equal(before+1,client.Snapshot().Seq);
        }
    }
    [Fact] public async Task Snapshot_reads_state_and_seq_together()
    {
        var client=Make();
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Snapshot().Seq==1,"the connection's state");
        // State n<i> is the (1+i)th state stored: a snapshot that pairs it with any other count read the two apart.
        bool done=false;
        var reader=Task.Run(()=>
        {
            var mismatches=new List<string>();
            while(!Volatile.Read(ref done))
            {
                var snap=client.Snapshot();
                long expected=snap.State?.Message is {} m&&m.StartsWith('n')?1+long.Parse(m[1..]):1;
                if(snap.Seq!=expected)mismatches.Add($"{snap.State?.Message} with {snap.Seq}");
            }
            return mismatches;
        });
        for(int i=1;i<=200;i++)await host.Push(new StateEvent(Sample with {Message=$"n{i}"})).WaitAsync(Bound);
        await Until(()=>client.Snapshot().Seq==201,"every pushed state counted");
        Volatile.Write(ref done,true);
        Assert.Empty(await reader.WaitAsync(Bound));
        Assert.Equal("n200",client.Snapshot().State!.Message);
    }
    [Fact] public async Task A_made_up_reply_carries_the_current_seq()
    {
        launcher.Running=new FakeProcess();
        host.Answer=m=>m is RescanRequest?[]:FakeHost.Answers(m);
        var client=Make();
        Assert.Equal(0,(await client.SendTrackedAsync(new RescanRequest(),CancellationToken.None).WaitAsync(Bound)).StatesBefore);
        await client.StartAsync(false,CancellationToken.None);
        await Until(()=>client.Snapshot().Seq==1,"the connection's state");
        // No answer: the reply made up at the timeout carries the states stored while it waited.
        var unanswered=client.SendTrackedAsync(new RescanRequest(),CancellationToken.None);
        await Until(()=>host.Saw<RescanRequest>());
        await host.Push(new StateEvent(Sample)).WaitAsync(Bound);
        await Until(()=>client.Snapshot().Seq==2,"the pushed state counted");
        time.Advance(Window);
        var timedOut=await unanswered.WaitAsync(Bound);
        Assert.Equal((false,"The lighting helper didn't answer.",2L),(timedOut.Reply.Ok,timedOut.Reply.Error,timedOut.StatesBefore));
        // Not connected: the count stays through the drop.
        host.Server!.Dispose();
        await Until(()=>client.State is null,"the dropped host's state cleared");
        var offline=await client.SendTrackedAsync(new RescanRequest(),CancellationToken.None).WaitAsync(Bound);
        Assert.Equal((false,"The lighting helper isn't connected.",2L),(offline.Reply.Ok,offline.Reply.Error,offline.StatesBefore));
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
