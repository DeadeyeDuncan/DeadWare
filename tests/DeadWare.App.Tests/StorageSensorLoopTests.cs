using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class StorageSensorLoopTests
{
    private static readonly TimeSpan Period=TimeSpan.FromMilliseconds(50);
    private static SmartSummary Ok=>new(true,false,90,40,70,0,0,0,100,50,false);
    private static StorageDrive Drive(string? serial,string model,int index)=>
        new(StorageProjection.DriveId(serial,model,index),serial,model,index,"NVMe",1000,false,[]);

    private sealed class FakeHealth(Func<IReadOnlyList<(string Serial,string Model,int? Index,SmartSummary Summary)>> read) : IStorageHealthSource
    {
        public IReadOnlyList<(string Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout)=>read();
        public void Dispose() {}
    }
    private sealed class FakeFast(IReadOnlyList<StorageDrive> drives) : IStorageFastSource
    {
        public IReadOnlyList<StorageDrive> Drives(double now)=>drives;
        public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)=>error=null;
    }
    private static async Task<IReadOnlyList<StorageHealth>> FirstHealth(StorageSensorLoop loop)
    {
        loop.Start();
        var sw=Stopwatch.StartNew();
        while(loop.Latest.Count==0&&sw.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
        return loop.Latest;
    }

    [Fact] public async Task Health_is_matched_by_serial_then_index_then_model()
    {
        IReadOnlyList<StorageDrive> drives=[Drive("S1","A",0),Drive(null,"B",1),Drive(null,"C",2),Drive(null,"C",3)];
        await using var loop=new StorageSensorLoop(new FakeHealth(()=>[("S1","A",null,Ok),(null!,"B",1,Ok),(null!,"C",null,Ok)]),Period,()=>drives);
        var h=await FirstHealth(loop);
        Assert.Equal("S1",h[0].DriveId);Assert.Equal("B#1",h[1].DriveId);Assert.StartsWith("unmatched:",h[2].DriveId);
    }

    [Fact] public async Task Slow_source_does_not_block_collector_samples()
    {
        var fast=new FakeFast([Drive("S1","A",0)]);
        var slow=new FakeHealth(()=>{Thread.Sleep(5000);return [];});
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5),fast,slow,Period);
        int count=0;var three=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=_=>{if(Interlocked.Increment(ref count)>=3)three.TrySetResult();};
        collector.Start();
        await three.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(count>=3);
    }

    [Fact] public async Task Timeout_marks_only_that_drive()
    {
        IReadOnlyList<StorageDrive> drives=[Drive("S1","A",0),Drive("S2","B",1)];
        await using var loop=new StorageSensorLoop(new FakeHealth(()=>[("S1","A",0,Ok),("S2","B",1,StorageSensorLoop.TimedOut)]),Period,()=>drives);
        var h=await FirstHealth(loop);
        Assert.Equal("Unavailable · sensor timeout",h.Single(x=>x.DriveId=="S2").Unavailable);Assert.Equal("OK",h.Single(x=>x.DriveId=="S1").Badge);
    }

    [Fact] public async Task Sensors_off_publishes_off_reason()
    {
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5,StorageSensors:false),new FakeFast([Drive("S1","A",0),Drive("S2","B",1)]),null);
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=r=>tcs.TrySetResult(r);
        collector.Start();
        var r=await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2,r.Storage.Health.Count);
        Assert.All(r.Storage.Health,h=>Assert.Equal("Unavailable · storage sensors off",h.Unavailable));
    }

    [Fact] public async Task Dispose_completes_within_hardware_budget()
    {
        var gate=new ManualResetEventSlim();var entered=new ManualResetEventSlim();
        var blocked=new FakeHealth(()=>{entered.Set();gate.Wait();return [];});
        var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5),new FakeFast([Drive("S1","A",0)]),blocked,Period);
        collector.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var sw=Stopwatch.StartNew();
        await collector.DisposeAsync();
        sw.Stop();gate.Set();
        Assert.True(sw.Elapsed<TimeSpan.FromSeconds(5.5),$"Dispose took {sw.Elapsed}");
    }
}
