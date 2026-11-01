using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class CoolingShutdownTests
{
    [Fact]
    public async Task Session_ending_drains_history_even_when_hardware_worker_is_stuck()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        static void Set(object target,string name,object value)=>target.GetType().GetField(name,flags)!.SetValue(target,value);
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        var history=new CoolingHistoryService(Path.Combine(root,"history.db"),"fixture","setup");
        var graphs=new GraphHistoryService(Path.Combine(root,"graphs.db"),"fixture",new SampleHistory());
        var battery=new BatteryHistoryService(Path.Combine(root,"battery.db"),"fixture");
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation=new CancellationTokenSource();
        // Never construct/start a hardware collector, Application or Window. Only exercise the stop path.
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"cancellation",cancellation);Set(collector,"worker",blocked.Task);
        Set(collector,"etwStopGate",new object());Set(collector,"publicationGate",new object());Set(collector,"clock",Stopwatch.StartNew());Set(collector,"options",new CollectorOptions(EtwEnabled:false));
        Set(collector,"etw",Activator.CreateInstance(typeof(Collector).Assembly.GetType("DeadWare.App.EtwNetwork")!,[false])!);
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);Set(session,"<Cooling>k__BackingField",history);Set(session,"<GraphHistory>k__BackingField",graphs);
        Set(session,"<Battery>k__BackingField",battery);
        try
        {
            history.Start();history.TryRecord(ThermalProjectionTests.Reading(ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60)));
            graphs.Start();
            var graphReading=new Reading(new Snapshot(DateTimeOffset.UtcNow,1,new Dictionary<string,double?>{{"network/download",100},{"network/upload",0}}),[],[],[],"Processor","","","",false);
            graphs.TryRecord(graphReading);
            battery.Start();battery.TryRecord(graphReading with {Battery=new(60,BatteryPowerState.OnBattery,true)});
            var watch=Stopwatch.StartNew();
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding",flags)!.Invoke(session,null);
            Assert.InRange(watch.Elapsed.TotalSeconds,2.8,4.5);
            Assert.True(cancellation.IsCancellationRequested);Assert.True(history.StopAsync().IsCompleted);
            Assert.True(graphs.StopAsync().IsCompleted);
            Assert.True(battery.StopAsync().IsCompleted);
            Assert.Equal(1,(await battery.ReadViewAsync(0)).TotalObservations);
            var savedGraphs=new GraphHistoryStore(Path.Combine(root,"graphs.db"));savedGraphs.Initialize();
            Assert.Single(savedGraphs.Read(DateTimeOffset.UtcNow));
            Assert.Single((await history.ReadViewAsync(null,null,1)).Devices);
            var stop=(Task)typeof(UiSession).GetField("collectorStop",flags)!.GetValue(session)!;
            blocked.SetResult();await stop.WaitAsync(TimeSpan.FromSeconds(3));
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding",flags)!.Invoke(session,null);
            Assert.Same(stop,typeof(UiSession).GetField("collectorStop",flags)!.GetValue(session));
        }
        finally {blocked.TrySetResult();await Task.WhenAll(history.StopAsync(),graphs.StopAsync(),battery.StopAsync());if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static void SetField(object target,string name,object value)=>target.GetType().GetField(name,Flags)!.SetValue(target,value);
    private static string Root()=>Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
    private static Task Stop(UiSession session,TimeSpan deadline)=>(Task)typeof(UiSession).GetMethod("StopCollectorAsync",Flags)!.Invoke(session,[deadline])!;
    private static async Task<long> SavedBytes(ConnectionService connections,DateTimeOffset now)=>(await connections.ReadViewAsync(new(now.AddMinutes(-1),now.AddMinutes(1)))).Summary.ReceivedBytes;
    private static void Observe(EtwNetwork capture,DateTimeOffset now,double seconds)
    {capture.TakeSnapshot(now,seconds);capture.RecordEndpoint(11,now,71,false,IPAddress.Parse("8.8.8.8"),443,IPAddress.Loopback,123,ConnectionProtocol.Tcp);}
    /// <summary>A session wired like the real one whose hardware worker never finishes. Never starts hardware or ETW.</summary>
    private static (UiSession Session,Collector Collector,ConnectionService Connections,TaskCompletionSource Worker) StuckSession(string root,EtwNetwork capture)
    {
        var worker=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        SetField(collector,"cancellation",new CancellationTokenSource());SetField(collector,"worker",worker.Task);SetField(collector,"etwStopGate",new object());
        SetField(collector,"publicationGate",new object());SetField(collector,"clock",Stopwatch.StartNew());SetField(collector,"options",new CollectorOptions());SetField(collector,"etw",capture);
        var connections=new ConnectionService(Path.Combine(root,"connections.db"));
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        SetField(session,"<Collector>k__BackingField",collector);SetField(session,"<Connections>k__BackingField",connections);
        SetField(session,"<Cooling>k__BackingField",new CoolingHistoryService(Path.Combine(root,"cooling.db"),"fixture","setup"));
        SetField(session,"<GraphHistory>k__BackingField",new GraphHistoryService(Path.Combine(root,"graphs.db"),"fixture",new SampleHistory()));
        SetField(session,"<Battery>k__BackingField",new BatteryHistoryService(Path.Combine(root,"battery.db"),"fixture"));
        SetField(session,"diagnostics",new LayoutDiagnostics(Path.Combine(root,"trace.jsonl"),Path.Combine(root,"settings.json")));
        collector.SampleAvailable+=connections.TryRecord;collector.FinalConnectionsAvailable+=connections.TryRecord;
        return (session,collector,connections,worker);
    }
    [Fact]
    public async Task Normal_quit_is_bounded_and_publishes_final_connections_while_hardware_worker_is_stuck()
    {
        string root=Root();var capture=new EtwNetwork(_=>new(11,1,"fixture"));
        var (session,collector,connections,worker)=StuckSession(root,capture);var now=DateTimeOffset.UtcNow;
        try
        {
            connections.Start();await SavedBytes(connections,now);Observe(capture,now,collector.ElapsedSeconds);
            var watch=Stopwatch.StartNew();
            await Stop(session,TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(watch.Elapsed.TotalSeconds,0.8,3);Assert.False(worker.Task.IsCompleted);
            // The final ETW drain and the connection flush never waited for the hardware worker.
            Assert.True(connections.StopAsync().IsCompleted);Assert.Equal(71,await SavedBytes(connections,now));
            Assert.Contains("collector_stop_timeout",File.ReadAllText(Path.Combine(root,"trace.jsonl")));
        }
        finally {worker.TrySetResult();await connections.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Hung_native_capture_stop_still_flushes_buffered_connections_within_the_deadline(bool sessionEnding)
    {
        string root=Root();using var release=new ManualResetEventSlim();
        var capture=new EtwNetwork(_=>new(11,1,"fixture"),stopNative:()=>release.Wait());
        var (session,collector,connections,worker)=StuckSession(root,capture);var now=DateTimeOffset.UtcNow;
        try
        {
            double seconds=collector.ElapsedSeconds;connections.Start();await SavedBytes(connections,now);Observe(capture,now,seconds);
            connections.TryRecord(capture.TakeSnapshot(now.AddSeconds(1),seconds+1).Connections);
            var watch=Stopwatch.StartNew();
            if(sessionEnding)
            {
                typeof(UiSession).GetMethod("StopCollectorForSessionEnding",Flags)!.Invoke(session,null);
                Assert.InRange(watch.Elapsed.TotalSeconds,2.8,4.5);
            }
            else
            {
                await Stop(session,TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.InRange(watch.Elapsed.TotalSeconds,0.8,3);
            }
            Assert.True(connections.StopAsync().IsCompleted);Assert.Equal(71,await SavedBytes(connections,now));
            // Native stop is still blocked: concurrent callers share its task without waiting on it.
            var calls=await Task.WhenAll(Task.Factory.StartNew(collector.StopNetworkCaptureAsync),Task.Factory.StartNew(collector.StopNetworkCaptureAsync)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(calls[0],calls[1]);Assert.False(calls[0].IsCompleted);
        }
        finally {release.Set();worker.TrySetResult();await connections.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public async Task Sign_out_still_flushes_connections_when_the_final_drain_faults()
    {
        string root=Root();var capture=new EtwNetwork(_=>new(11,1,"fixture"));
        var (session,collector,connections,worker)=StuckSession(root,capture);var now=DateTimeOffset.UtcNow;
        collector.FinalConnectionsAvailable+=_=>throw new InvalidOperationException("late subscriber failure");worker.SetResult();
        try
        {
            connections.Start();await SavedBytes(connections,now);Observe(capture,now,collector.ElapsedSeconds);
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding",Flags)!.Invoke(session,null);
            Assert.True(connections.StopAsync().IsCompleted);Assert.Equal(71,await SavedBytes(connections,now));
            Assert.Contains("session_end_collector_stop_failed",File.ReadAllText(Path.Combine(root,"trace.jsonl")));
        }
        finally {await connections.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public async Task Worker_sample_taken_before_stop_reaches_connection_history_before_the_final_drain()
    {
        string root=Root();var collector=new Collector(new CollectorOptions(HardwareEnabled:false));
        var capture=new EtwNetwork(_=>new(11,1,"fixture"));
        // A stopped capture ignores Start, so the real worker never opens a kernel session, yet still serves snapshots.
        await capture.StopAsync();SetField(collector,"etw",capture);
        var connections=new ConnectionService(Path.Combine(root,"connections.db"));
        using var parked=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        // The first subscriber holds the worker between its ETW snapshot and the connection service's delivery.
        collector.SampleAvailable+=_=>{parked.Set();release.Wait();};
        collector.SampleAvailable+=connections.TryRecord;collector.FinalConnectionsAvailable+=connections.TryRecord;
        var now=DateTimeOffset.UtcNow;
        try
        {
            connections.Start();await SavedBytes(connections,now);Observe(capture,now,collector.ElapsedSeconds);
            collector.Start();Assert.True(parked.Wait(TimeSpan.FromSeconds(10)));
            var stop=collector.StopNetworkCaptureAsync();
            await Task.WhenAny(stop,Task.Delay(300));release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));await collector.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await connections.StopAsync();Assert.Equal(71,await SavedBytes(connections,now));
        }
        finally {release.Set();await collector.DisposeAsync();await connections.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
