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

    private sealed class FakeHealth(Func<IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)>> read) : IStorageHealthSource
    {
        public IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout)=>read();
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
        await using var loop=new StorageSensorLoop(new FakeHealth(()=>[("S1","A",null,Ok),(null,"B",1,Ok),(null,"C",null,Ok)]),Period,()=>drives);
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

    [Fact] public void Each_drive_and_reading_match_at_most_once()
    {
        IReadOnlyList<StorageDrive> drives=[Drive("S1","A",0),Drive(null,"B",1)];
        var h=StorageSensorLoop.Match([("S1","A",0,Ok),("S1","A",0,Ok),(null,"B",1,Ok),(null,"B",1,Ok),(null,"Z",null,Ok),(null,"Z",null,Ok)],drives);
        Assert.Equal(h.Count,h.Select(x=>x.DriveId).Distinct().Count());
        Assert.Single(h,x=>x.DriveId=="S1");Assert.Single(h,x=>x.DriveId=="B#1");
    }

    [Fact] public void Pending_update_is_not_restarted_and_reports_timeout()
    {
        var updates=new HardwareUpdates();var key=new object();var gate=new ManualResetEventSlim();int starts=0;
        void Update(){Interlocked.Increment(ref starts);gate.Wait();}
        Assert.Null(updates.Run(key,Update,TimeSpan.FromMilliseconds(50)));
        Assert.Null(updates.Run(key,Update,TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1,Volatile.Read(ref starts));
        var outstanding=updates.Outstanding();
        Assert.False(outstanding.IsCompleted);
        gate.Set();
        Assert.True(outstanding.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(updates.Run(key,()=>{},TimeSpan.FromSeconds(5)));
    }

    [Fact] public void Close_between_hardware_refuses_new_updates_and_waits_for_outstanding()
    {
        var updates=new HardwareUpdates();var gate=new ManualResetEventSlim();int secondStarted=0;
        Assert.Null(updates.Run(new object(),()=>gate.Wait(),TimeSpan.FromMilliseconds(50)));
        var closed=updates.Close();
        Assert.Null(updates.Run(new object(),()=>Interlocked.Increment(ref secondStarted),TimeSpan.FromSeconds(1)));
        Assert.Equal(0,Volatile.Read(ref secondStarted));
        Assert.False(closed.Wait(100));
        gate.Set();
        Assert.True(closed.Wait(TimeSpan.FromSeconds(5)));
    }

    private sealed record FakeSmart(bool HasSmart=true,bool StatusBad=false,int? Temperature=38,int? TemperatureWarning=70,int? TemperatureCritical=null,double? Life=90,ulong PowerOnHours=1234,
        IReadOnlyList<SmartAttributeReading>? Readings=null,string? Serial="SN1",int? DriveNumber=0) : ISmartDevice
    {
        public IReadOnlyList<SmartAttributeReading> Attributes=>Readings??[];
    }
    [Fact] public void Smart_summary_reads_counts_limits_hours_and_critical_state()
    {
        var healthy=LibreStorageHealth.Summary(null,null,40,false,false,new FakeSmart(Readings:[new(0x05,100,10,3),new(0xC5,100,0,2),new(0xC6,100,0,1)]));
        Assert.True(healthy.Readable);Assert.False(healthy.CriticalWarning);Assert.Equal(3,healthy.Reallocated);Assert.Equal(2,healthy.Pending);Assert.Equal(1,healthy.Uncorrectable);
        Assert.Equal(70,healthy.MaxTemperature);Assert.Equal(1234,healthy.PowerOnHours);Assert.Equal(38,healthy.Temperature);Assert.Equal(90,healthy.LifePercent);Assert.Equal(40,healthy.UsedPercent);
        // Sensor values win over SMART values; critical limit wins over warning.
        var preferred=LibreStorageHealth.Summary(41,80,null,true,false,new FakeSmart(TemperatureCritical:85));
        Assert.Equal(41,preferred.Temperature);Assert.Equal(80,preferred.LifePercent);Assert.Equal(85,preferred.MaxTemperature);Assert.Equal(0,preferred.Reallocated);
        Assert.True(LibreStorageHealth.Summary(null,null,null,false,false,new FakeSmart(StatusBad:true)).CriticalWarning);
        Assert.True(LibreStorageHealth.Summary(null,null,null,false,false,new FakeSmart(Readings:[new(0x01,5,6,0)])).CriticalWarning);
        Assert.Null(LibreStorageHealth.Summary(null,null,null,false,false,new FakeSmart(PowerOnHours:0)).PowerOnHours);
        Assert.NotEqual("OK",StorageProjection.Health("S",LibreStorageHealth.Summary(null,null,null,false,false,new FakeSmart(StatusBad:true)),null).Badge);
    }
    [Fact] public void Without_smart_the_sensor_fallback_is_kept()
    {
        foreach(var smart in new ISmartDevice?[]{null,new FakeSmart(HasSmart:false)})
        {
            var s=LibreStorageHealth.Summary(42,77,10,true,false,smart);
            Assert.True(s.Readable);Assert.False(s.CriticalWarning);Assert.Equal(42,s.Temperature);Assert.Equal(77,s.LifePercent);Assert.Null(s.MaxTemperature);Assert.Null(s.PowerOnHours);
        }
        Assert.False(LibreStorageHealth.Summary(null,null,null,false,false,null).Readable);
    }
    [Fact] public void Hardware_update_failure_reports_the_exception_type()
    {
        var updates=new HardwareUpdates();
        Assert.False(updates.Run(new object(),()=>throw new UnauthorizedAccessException(),TimeSpan.FromSeconds(5),out var error));
        Assert.Equal("UnauthorizedAccessException",error);
        Assert.True(updates.Run(new object(),()=>{},TimeSpan.FromSeconds(5),out error));Assert.Null(error);
    }
}
