using System.Diagnostics;
using System.IO;
using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class GraphHistoryServiceTests
{
    [Fact]
    public async Task Quit_flushes_graphs_and_restart_restores_old_values_without_replacing_live_sample()
    {
        using var fixture=new Fixture();
        var origin=DateTimeOffset.UtcNow.AddMinutes(-2);
        var old=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        Assert.False(Directory.Exists(fixture.Root));
        var first=Reading(origin,50,0,60,1000,20);
        var second=Reading(origin.AddSeconds(1),51,null,61,2000,0);
        old.Start();old.TryRecord(first with {Sample=first.Sample with {PollingIntervalSeconds=10}});
        old.TryRecord(second with {Sample=second.Sample with {PollingIntervalSeconds=0.5}});
        await old.StopAsync();
        var history=new SampleHistory();var current=Reading(DateTimeOffset.UtcNow,2,40,65,3000,30);
        history.Add(current.Sample);
        var reopened=new GraphHistoryService(fixture.Path,"machine",history);
        reopened.Start();reopened.TryRecord(current);await reopened.StopAsync();
        var cpu=history.Series("cpu/usage",2,21600);
        Assert.Equal(new double?[]{0,null,40},cpu.Select(p=>p.Value));
        Assert.Equal(new double?[]{1000,2000,3000},history.Series("network/download",2,21600).Select(p=>p.Value));
        Assert.Equal(new double?[]{20,0,30},history.Series("network/upload",2,21600).Select(p=>p.Value));
        Assert.True(cpu[^1].BreakBefore);
        Assert.Null(history.Read()[^1].SessionId);
        Assert.Equal(new double[]{10,0.5,1},history.Read().Select(sample=>sample.PollingIntervalSeconds));
        Assert.DoesNotContain("private-process",File.ReadAllText(fixture.Path));
    }

    [Fact]
    public async Task Empty_first_publication_restores_network_then_delayed_hardware_metadata_restores_cpu()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-2);
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        first.Start();first.TryRecord(Reading(now.AddSeconds(-10),10,25,60,123,45));await first.StopAsync();
        var history=new SampleHistory();var second=new GraphHistoryService(fixture.Path,"machine",history);
        var empty=Reading(now,1,50,65,999,30) with {Sensors=[],Gpus=[],CpuName="Processor"};
        history.Add(empty.Sample);second.Start();second.TryRecord(empty);
        var metadata=Reading(now.AddSeconds(1),2,60,66,1000,31);history.Add(metadata.Sample);second.TryRecord(metadata);
        await second.StopAsync();
        var restored=history.Read().Where(s=>s.SessionId is not null).ToArray();
        Assert.Single(restored);Assert.Equal(25,restored[0].Values["cpu/usage"]);
        Assert.Equal(123,restored[0].Values["network/download"]);
    }

    [Fact]
    public async Task Distinct_gpu_models_restore_after_enumeration_changes_but_replacements_do_not()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-2);
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        first.Start();first.TryRecord(WithGpu(Reading(now.AddSeconds(-10),10),"/gpu/0","Model A",17));await first.StopAsync();
        var history=new SampleHistory();var second=new GraphHistoryService(fixture.Path,"machine",history);
        second.Start();second.TryRecord(WithGpu(Reading(now,1),"/gpu/1","Model A",40));await second.StopAsync();
        Assert.Equal(17,Assert.Single(history.Series("/gpu/1/usage",1,21600)).Value);
        var different=new SampleHistory();var third=new GraphHistoryService(fixture.Path,"machine",different);
        third.Start();third.TryRecord(WithGpu(Reading(now.AddSeconds(1),1),"/gpu/0","Replacement",70));await third.StopAsync();
        Assert.All(different.Series("/gpu/0/usage",1,21600),p=>Assert.Null(p.Value));
    }

    [Fact]
    public void Ambiguous_same_model_gpus_are_not_projected_and_other_sensor_or_process_data_is_excluded()
    {
        var reading=WithGpu(Reading(DateTimeOffset.UtcNow,1),"/gpu/0","Same model",17);
        reading=reading with {Gpus=[..reading.Gpus,new("/gpu/1","Same model")],Sensors=[..reading.Sensors,new("private-extra","Same model","GPU Core","%",50,"/gpu/1","GpuAmd","Load","Available")]};
        var mapping=GraphHistoryProjection.Mappings(reading,"machine");
        Assert.DoesNotContain(mapping.Values,key=>key.StartsWith("/gpu/"));
        Assert.Equal(6,mapping.Count);
        Assert.DoesNotContain(mapping.Keys,key=>key.Contains("Test CPU")||key.Contains("private"));
    }

    [Fact]
    public async Task Periodic_flush_saves_pending_data_without_another_publication()
    {
        using var fixture=new Fixture();
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory(),flushInterval:TimeSpan.FromMilliseconds(50));
        try
        {
            service.Start();service.TryRecord(Reading(DateTimeOffset.UtcNow,1));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(File.Exists(fixture.Path))
                {
                    try {var store=new GraphHistoryStore(fixture.Path);store.Initialize();if(store.Read(DateTimeOffset.UtcNow).Count==1)break;}
                    catch(Microsoft.Data.Sqlite.SqliteException) { }
                }
                await Task.Delay(20,timeout.Token);
            }
            Assert.DoesNotContain("unavailable",service.Status,StringComparison.OrdinalIgnoreCase);
        }
        finally {await service.StopAsync();}
    }

    [Fact]
    public async Task Future_records_do_not_restore_and_clock_changes_create_new_saved_segments()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow;
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();service.TryRecord(Reading(now.AddMinutes(-2),1));service.TryRecord(Reading(now.AddMinutes(-1),2));
        service.TryRecord(Reading(now.AddMinutes(10),3));await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();var records=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(2,records.Count);Assert.NotEqual(records[0].SessionId,records[1].SessionId);
        var history=new SampleHistory();var reopened=new GraphHistoryService(fixture.Path,"machine",history);
        reopened.Start();reopened.TryRecord(Reading(now,1));await reopened.StopAsync();
        Assert.All(history.Read(),sample=>Assert.True(sample.MonotonicSeconds<1));
    }

    [Fact]
    public async Task Queue_is_bounded_nonblocking_and_faults_preserve_the_cache()
    {
        using var fixture=new Fixture();Directory.CreateDirectory(fixture.Root);File.WriteAllText(fixture.Path,"preserve-corrupt-cache");
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();var reading=Reading(DateTimeOffset.UtcNow,1);var watch=Stopwatch.StartNew();
        for(int i=0;i<100000;i++)service.TryRecord(reading);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));Assert.InRange(service.PendingSamples,0,120);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("unavailable",service.Status,StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve-corrupt-cache",File.ReadAllText(fixture.Path));
        Assert.Equal(0,service.PendingSamples);
    }

    [Fact]
    public async Task Quick_restart_marks_a_gap_and_a_different_machine_cannot_inherit_readings()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow;
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        first.Start();first.TryRecord(Reading(now.AddMilliseconds(-500),100,12));await first.StopAsync();
        var history=new SampleHistory();var live=Reading(now,1,34);history.Add(live.Sample);
        var second=new GraphHistoryService(fixture.Path,"machine",history);
        second.Start();second.TryRecord(live);await second.StopAsync();
        var points=history.Series("cpu/usage",1,60);
        Assert.Equal(new double?[]{12,34},points.Select(p=>p.Value));Assert.True(points[1].BreakBefore);
        var different=new SampleHistory();var other=new GraphHistoryService(fixture.Path,"another-machine",different);
        other.Start();other.TryRecord(live);await other.StopAsync();Assert.Empty(different.Read());
    }

    [Fact]
    public async Task Valid_bursts_drop_overflow_instead_of_blocking_or_growing_the_queue()
    {
        using var fixture=new Fixture();var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();var reading=Reading(DateTimeOffset.UtcNow,1);var watch=Stopwatch.StartNew();
        for(int i=0;i<100000;i++)service.TryRecord(reading);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));Assert.InRange(service.PendingSamples,0,120);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.DroppedSamples>0);Assert.Contains("gaps",service.Status);
        Assert.DoesNotContain("unavailable",service.Status,StringComparison.OrdinalIgnoreCase);
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();Assert.Single(store.Read(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Queued_frames_before_overflow_do_not_consume_the_next_accepted_frames_gap()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddMinutes(-3);
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        // Hold the worker back without adding a production-only test hook. These calls
        // still exercise the real bounded channel and TryRecord overflow path.
        typeof(GraphHistoryService).GetField("accepting",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(service,true);
        for(int i=1;i<=120;i++)service.TryRecord(Reading(origin.AddSeconds(i),i,download:i));
        service.TryRecord(Reading(origin.AddSeconds(121),121,download:121));
        Assert.Equal(120,service.PendingSamples);Assert.Equal(1,service.DroppedSamples);
        service.Start();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(service.PendingSamples>0)await Task.Delay(1,timeout.Token);
        service.TryRecord(Reading(origin.AddSeconds(122),122,download:122));
        await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();var saved=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(121,saved.Count);
        Assert.NotEqual(saved.Single(r=>r.MonotonicSeconds==120).SessionId,saved.Single(r=>r.MonotonicSeconds==122).SessionId);
        var history=new SampleHistory();var current=Reading(origin.AddSeconds(124),1,download:124);history.Add(current.Sample);
        var reopened=new GraphHistoryService(fixture.Path,"machine",history);
        reopened.Start();reopened.TryRecord(current);await reopened.StopAsync();
        Assert.True(history.Series("network/download",1,21600).Single(p=>p.Value==122).BreakBefore);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("nonfinite")]
    public async Task A_rejected_frame_splits_the_next_saved_segment(string reason)
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddMinutes(-1);
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();service.TryRecord(Reading(origin,1));
        service.TryRecord(reason switch
        {
            "future"=>Reading(DateTimeOffset.UtcNow.AddMinutes(1),2),
            "expired"=>Reading(DateTimeOffset.UtcNow.AddHours(-7),2),
            _=>Reading(origin.AddSeconds(1),double.NaN)
        });
        service.TryRecord(Reading(origin.AddSeconds(2),3));await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();var saved=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(2,saved.Count);Assert.NotEqual(saved[0].SessionId,saved[1].SessionId);
    }

    [Fact]
    public async Task Duplicate_publications_do_not_create_a_false_segment_boundary()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddMinutes(-1);
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();var first=Reading(origin,1);service.TryRecord(first);service.TryRecord(first);
        service.TryRecord(Reading(origin.AddSeconds(1),2));await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();var saved=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(2,saved.Count);Assert.Equal(saved[0].SessionId,saved[1].SessionId);
    }

    [Fact]
    public void Ram_projection_is_machine_wide_without_hardware_metadata_and_excludes_raw_totals()
    {
        var reading=WithMemory(Reading(DateTimeOffset.UtcNow,1),8,16);
        var mapping=GraphHistoryProjection.Mappings(reading,"machine");
        var memory=Assert.Single(mapping,pair=>pair.Value=="memory/usage");
        Assert.Equal(3,mapping.Count);
        Assert.DoesNotContain(mapping.Values,key=>key is "memory/used" or "memory/total");
        Assert.DoesNotContain(mapping.Keys,key=>key.Contains("machine")||key.Contains("Test CPU"));
        Assert.NotEqual(memory.Key,Assert.Single(GraphHistoryProjection.Mappings(reading,"another-machine"),pair=>pair.Value=="memory/usage").Key);
    }

    [Fact]
    public async Task Ram_history_flushes_and_restores_unknown_zero_cadence_and_restart_gaps()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddMinutes(-2);
        var old=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        var first=WithMemory(Reading(origin,0),8,16);
        var second=WithMemory(Reading(origin.AddSeconds(10),10),null,16);
        var third=WithMemory(Reading(origin.AddSeconds(10.5),10.5),0,32);
        old.Start();old.TryRecord(first with {Sample=first.Sample with {PollingIntervalSeconds=10}});
        old.TryRecord(second with {Sample=second.Sample with {PollingIntervalSeconds=0.5}});
        old.TryRecord(third with {Sample=third.Sample with {PollingIntervalSeconds=0.5}});
        await old.StopAsync();
        var history=new SampleHistory();var current=WithMemory(Reading(DateTimeOffset.UtcNow,1),8,32);
        history.Add(current.Sample);
        var reopened=new GraphHistoryService(fixture.Path,"machine",history);
        reopened.Start();reopened.TryRecord(current);await reopened.StopAsync();
        var points=history.Series("memory/usage",1,21600);
        Assert.Equal(new double?[]{50,null,0,25},points.Select(point=>point.Value));
        Assert.Equal(new double[]{10,0.5,0.5,1},points.Select(point=>point.PollingIntervalSeconds));
        Assert.True(points[^1].BreakBefore);
        Assert.Null(history.Read()[^1].SessionId);
        Assert.Equal(25,history.Summarize("memory/usage",1,21600).Average);
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        Assert.All(store.Read(DateTimeOffset.UtcNow),row=>Assert.Equal(3,row.Values.Count));
    }

    [Fact]
    public async Task Legacy_rows_without_ram_remain_unknown_instead_of_backfilling_current_usage()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow;
        var current=WithMemory(Reading(now,1),8,16);
        var networkKey=Assert.Single(GraphHistoryProjection.Mappings(current,"machine"),pair=>pair.Value=="network/download").Key;
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        store.Append([new("legacy",now.AddSeconds(-5),50,new Dictionary<string,double?>{[networkKey]=100})],now);
        var history=new SampleHistory();history.Add(current.Sample);
        var service=new GraphHistoryService(fixture.Path,"machine",history);
        service.Start();service.TryRecord(current);await service.StopAsync();
        var points=history.Series("memory/usage",1,60);
        Assert.Equal(new double?[]{null,50},points.Select(point=>point.Value));
        Assert.True(points[^1].BreakBefore);
        Assert.Equal(50,history.Summarize("memory/usage",1,60).Average);
    }

    [Fact] public void Core_metrics_join_the_unchanged_cpu_identity_with_short_keys()
    {
        var reading=Reading(DateTimeOffset.UtcNow,1) with {CpuCoreCount=50};
        var mappings=GraphHistoryProjection.Mappings(reading,"machine");
        string identity=Assert.Single(mappings,p=>p.Value=="cpu/usage").Key[..^"/usage".Length];
        Assert.Equal("cpu/core/47/usage",mappings[identity[..12]+"/c47"]);
        Assert.DoesNotContain(mappings.Values,v=>v=="cpu/core/48/usage");
        Assert.Equal(identity,Assert.Single(GraphHistoryProjection.Mappings(reading with {CpuCoreCount=0},"machine"),p=>p.Value=="cpu/usage").Key[..^"/usage".Length]);
        Assert.True(mappings.Count<=64);
    }
    [Fact] public async Task Core_values_are_written_at_the_cadence_and_never_exceed_the_record_limit()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddSeconds(-30);
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        service.Start();
        for(int i=0;i<=20;i++)service.TryRecord(WithCores(Reading(origin.AddSeconds(i*0.5),i+1),64));
        await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        var records=store.Read(DateTimeOffset.UtcNow).OrderBy(r=>r.Timestamp).ToArray();
        Assert.Equal(21,records.Length);
        Assert.All(records,r=>Assert.True(r.Values.Count<=64));
        Assert.All(records,r=>Assert.Contains(r.Values.Keys,k=>!k.Contains("/c")));
        var withCores=records.Where(r=>r.Values.Keys.Any(k=>k.Contains("/c"))).ToArray();
        Assert.Equal(6,withCores.Length);
        Assert.Equal(origin.AddSeconds(2).UtcDateTime,withCores[1].Timestamp.UtcDateTime);
        Assert.Contains(withCores[1].Values,p=>p.Key.EndsWith("/c47")&&p.Value is not null);
        for(int i=1;i<withCores.Length;i++)Assert.True((withCores[i].Timestamp-withCores[i-1].Timestamp).TotalSeconds>=2);
        Assert.True(withCores.Length<records.Length);
    }
    [Fact] public async Task Restore_returns_only_the_cores_present_in_the_current_reading()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-2);
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());
        first.Start();first.TryRecord(WithCores(Reading(now.AddSeconds(-10),10),16));await first.StopAsync();
        var history=new SampleHistory();var live=WithCores(Reading(now,1),8);history.Add(live.Sample);
        var second=new GraphHistoryService(fixture.Path,"machine",history);
        second.Start();second.TryRecord(live);await second.StopAsync();
        var restored=Assert.Single(history.Read().Where(s=>s.SessionId is not null));
        Assert.True(restored.Values.ContainsKey("cpu/core/7/usage"));
        Assert.False(restored.Values.ContainsKey("cpu/core/8/usage"));
        Assert.DoesNotContain("unavailable",second.Status);
    }

    [Fact] public void Many_gpus_leave_the_record_within_the_value_limit_and_keep_cpu_usage()
    {
        var reading=WithCores(Reading(DateTimeOffset.UtcNow,1),64);
        var gpus=Enumerable.Range(0,8).Select(i=>new Device($"/gpu/{i}",$"Model {i}")).ToArray();
        reading=reading with {Gpus=gpus,Sensors=[..reading.Sensors,..gpus.Select(g=>new SensorReading(g.Id+"/load",g.Name,"GPU Core","%",1,g.Id,"GpuAmd","Load","Available"))]};
        var mappings=GraphHistoryProjection.Mappings(reading,"machine");
        Assert.True(mappings.Count<=64);Assert.Contains("cpu/usage",mappings.Values);
    }
    [Fact] public async Task Backwards_clock_step_still_carries_core_keys()
    {
        using var fixture=new Fixture();var now=DateTimeOffset.UtcNow.AddSeconds(-5);
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());service.Start();
        service.TryRecord(WithCores(Reading(now,1),4));service.TryRecord(WithCores(Reading(now.AddHours(-1),2),4));
        await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        var records=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(2,records.Count);Assert.All(records,r=>Assert.Contains(r.Values.Keys,k=>k.Contains("/c")));
    }

    private static async Task<SampleHistory> RestoreCores(Fixture fixture,IReadOnlyList<DateTimeOffset> stamps,int cores)
    {
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());first.Start();
        for(int i=0;i<stamps.Count;i++)first.TryRecord(WithCores(Reading(stamps[i],i+1),cores));
        await first.StopAsync();
        var history=new SampleHistory();var live=WithCores(Reading(DateTimeOffset.UtcNow,1),cores);history.Add(live.Sample);
        var second=new GraphHistoryService(fixture.Path,"machine",history);second.Start();second.TryRecord(live);await second.StopAsync();
        return history;
    }
    [Fact] public async Task Restored_core_history_fills_cadence_thinned_records_without_gaps()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddSeconds(-30);
        var history=await RestoreCores(fixture,Enumerable.Range(0,21).Select(i=>origin.AddSeconds(i*.5)).ToArray(),64);
        double end=history.Read().Where(s=>s.SessionId is not null).Max(s=>s.MonotonicSeconds);
        var restored=history.Series("cpu/core/0/usage",1,21600).Where(p=>p.MonotonicSeconds<=end).ToArray();
        Assert.Equal(21,restored.Length);Assert.All(restored,p=>Assert.NotNull(p.Value));
        var ranges=history.ChartRanges("cpu/core/0/usage",1,21600,2000).Where(r=>r.EndSeconds<=end).ToArray();
        Assert.NotEmpty(ranges);Assert.All(ranges,r=>Assert.NotNull(r.Average));
        Assert.All(history.Read().Where(s=>s.SessionId is not null),s=>Assert.True(s.Values.ContainsKey("cpu/usage")));
    }
    [Fact] public async Task Restore_does_not_fill_cores_across_a_gap_longer_than_the_cadence()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddSeconds(-30);
        // A record 5 s after the last core reading is beyond cadence + 0.5 s, so it stays a gap.
        var first=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());first.Start();
        first.TryRecord(WithCores(Reading(origin,1),64));first.TryRecord(Reading(origin.AddSeconds(5),6));
        await first.StopAsync();
        var history=new SampleHistory();var live=WithCores(Reading(DateTimeOffset.UtcNow,1),64);history.Add(live.Sample);
        var second=new GraphHistoryService(fixture.Path,"machine",history);second.Start();second.TryRecord(live);await second.StopAsync();
        var restored=history.Read().Where(s=>s.SessionId is not null).OrderBy(s=>s.MonotonicSeconds).ToArray();
        Assert.Equal(2,restored.Length);
        Assert.True(restored[0].Values.ContainsKey("cpu/core/0/usage"));
        Assert.False(restored[1].Values.ContainsKey("cpu/core/0/usage"));
        Assert.Null(history.Series("cpu/core/0/usage",1,21600)[1].Value);
    }
    [Fact] public async Task One_second_polling_with_jitter_writes_cores_on_every_record()
    {
        using var fixture=new Fixture();var origin=DateTimeOffset.UtcNow.AddSeconds(-30);
        double[] steps={.98,1.02,.99,1.01,.98,1.0,.985,1.02};var stamps=new List<DateTimeOffset>{origin};
        foreach(double step in steps)stamps.Add(stamps[^1].AddSeconds(step));
        var service=new GraphHistoryService(fixture.Path,"machine",new SampleHistory());service.Start();
        for(int i=0;i<stamps.Count;i++)service.TryRecord(WithCores(Reading(stamps[i],i+1),16));
        await service.StopAsync();
        var store=new GraphHistoryStore(fixture.Path);store.Initialize();
        var records=store.Read(DateTimeOffset.UtcNow);
        Assert.Equal(stamps.Count,records.Count);
        Assert.All(records,r=>Assert.Contains(r.Values.Keys,k=>k.Contains("/c")));
    }
    private static Reading WithCores(Reading reading,int count)
    {
        var values=reading.Sample.Values.ToDictionary(p=>p.Key,p=>p.Value);
        for(int i=0;i<count;i++)values[CpuCoreUsage.Key(i)]=i+reading.Sample.MonotonicSeconds;
        return reading with {Sample=reading.Sample with {Values=values},CpuCoreCount=count};
    }
    private static Reading Reading(DateTimeOffset time,double mono,double? cpu=40,double? temperature=60,double? download=100,double? upload=20)=>new(
        new(time,mono,new Dictionary<string,double?>{{"cpu/usage",cpu},{"cpu/temperature",temperature},{"network/download",download},{"network/upload",upload},{"private-extra",999}}),
        [],[new("cpu-load","Test CPU","CPU Total","%",cpu,"/cpu/0","Cpu","Load","Available")],
        [new(5,1,"private-process",0,0,10,10)],"Test CPU","","","",false);
    private static Reading WithGpu(Reading reading,string id,string model,double value)
    {
        var values=reading.Sample.Values.ToDictionary(p=>p.Key,p=>p.Value);values[id+"/usage"]=value;values[id+"/temperature"]=55;
        return reading with {Sample=reading.Sample with {Values=values},Gpus=[new(id,model)],Sensors=[..reading.Sensors,new(id+"/load",model,"GPU Core","%",value,id,"GpuAmd","Load","Available")]};
    }
    private static Reading WithMemory(Reading reading,double? used,double? total)
    {
        var values=reading.Sample.Values.ToDictionary(pair=>pair.Key,pair=>pair.Value);
        values["memory/used"]=used;values["memory/total"]=total;values["memory/usage"]=MemoryUsage.Percentage(used,total);
        return reading with {Sample=reading.Sample with {Values=values},Sensors=[],Gpus=[]};
    }
    private sealed class Fixture:IDisposable
    {
        public string Root {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        public string Path=>System.IO.Path.Combine(Root,"graph.db");
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}
