using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class CollectorStorageTests
{
    private sealed class FakeStorage(IReadOnlyList<StorageDrive> drives,bool fail) : IStorageFastSource
    {
        public IReadOnlyList<StorageDrive> Drives(double now)=>drives;
        public void Read(IReadOnlyList<StorageDrive> list,Dictionary<string,double?> values,out string? error)
        {
            error=fail?"Disk counters unavailable":null;
            foreach(var d in list)
            {
                values[StorageMetrics.Key(d.Id,"read")]=fail?null:100;
                values[StorageMetrics.Key(d.Id,"write")]=fail?null:50;
                values[StorageMetrics.Key(d.Id,"active")]=fail?null:12;
                foreach(var v in d.Volumes)values[StorageMetrics.VolumeKey(d.Id,v.Letter)]=fail?null:v.FreeBytes;
            }
        }
    }
    private static readonly StorageDrive Drive=new("S1","S1","NVMe A",0,"NVMe",1000,false,[new("C","OS",1000,400)]);
    private static async Task<Reading> First(IStorageFastSource source)
    {
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5),source);
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=r=>tcs.TrySetResult(r);
        collector.Start();
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    [Fact] public async Task Fast_metrics_are_published_per_drive_and_volume()
    {
        var r=await First(new FakeStorage([Drive],false));
        Assert.Equal(100,r.Sample.Values["storage/S1/read"]);Assert.Equal(400,r.Sample.Values["storage/S1/volume/C/free"]);Assert.Single(r.Storage.Drives);
    }
    [Fact] public async Task Counter_failure_publishes_null_and_status()
    {
        var r=await First(new FakeStorage([Drive],true));
        Assert.Null(r.Sample.Values["storage/S1/read"]);Assert.Contains("Disk counters unavailable",r.HardwareStatus);
    }
    [Fact] public async Task No_drives_yields_empty_storage_reading()
    {
        var r=await First(new FakeStorage([],false));
        Assert.Empty(r.Storage.Drives);
    }

    private sealed class FakeHealth : IStorageHealthSource
    {
        public IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout)=>
            [("S1","NVMe A",0,new SmartSummary(true,false,null,41,70,0,0,0,10,55,false))];
        public void Dispose() {}
    }
    [Fact] public async Task Slow_health_is_published_as_drive_keys()
    {
        await using var collector=new Collector(new(HardwareEnabled:false,EtwEnabled:false,PollingIntervalSeconds:0.5),new FakeStorage([Drive],false),new FakeHealth(),TimeSpan.FromMilliseconds(50));
        var tcs=new TaskCompletionSource<Reading>(TaskCreationOptions.RunContinuationsAsynchronously);
        collector.SampleAvailable+=r=>{if(r.Storage.Health.Count>0)tcs.TrySetResult(r);};
        collector.Start();
        var r=await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(41,r.Sample.Values["storage/S1/temperature"]);
        Assert.Null(r.Sample.Values["storage/S1/life"]);
        Assert.Equal(55,r.Sample.Values["storage/S1/used"]);
    }
}
