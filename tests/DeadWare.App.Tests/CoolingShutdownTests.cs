using System.Diagnostics;
using System.IO;
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
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation=new CancellationTokenSource();
        // Never construct/start a hardware collector, Application or Window. Only exercise the stop path.
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"cancellation",cancellation);Set(collector,"worker",blocked.Task);
        Set(collector,"etw",Activator.CreateInstance(typeof(Collector).Assembly.GetType("DeadWare.App.EtwNetwork")!,[false])!);
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);Set(session,"<Cooling>k__BackingField",history);Set(session,"<GraphHistory>k__BackingField",graphs);
        try
        {
            history.Start();history.TryRecord(ThermalProjectionTests.Reading(ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60)));
            graphs.Start();
            var graphReading=new Reading(new Snapshot(DateTimeOffset.UtcNow,1,new Dictionary<string,double?>{{"network/download",100},{"network/upload",0}}),[],[],[],"Processor","","","",false);
            graphs.TryRecord(graphReading);
            var watch=Stopwatch.StartNew();
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding",flags)!.Invoke(session,null);
            Assert.InRange(watch.Elapsed.TotalSeconds,2.8,4.5);
            Assert.True(cancellation.IsCancellationRequested);Assert.True(history.StopAsync().IsCompleted);
            Assert.True(graphs.StopAsync().IsCompleted);
            var savedGraphs=new GraphHistoryStore(Path.Combine(root,"graphs.db"));savedGraphs.Initialize();
            Assert.Single(savedGraphs.Read(DateTimeOffset.UtcNow));
            Assert.Single((await history.ReadViewAsync(null,null,1)).Devices);
            var stop=(Task)typeof(UiSession).GetField("collectorStop",flags)!.GetValue(session)!;
            blocked.SetResult();await stop.WaitAsync(TimeSpan.FromSeconds(3));
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding",flags)!.Invoke(session,null);
            Assert.Same(stop,typeof(UiSession).GetField("collectorStop",flags)!.GetValue(session));
        }
        finally {blocked.TrySetResult();await Task.WhenAll(history.StopAsync(),graphs.StopAsync());if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
