using System.Collections.Concurrent;
using DeadWare.App;
using DeadWare.Core;
using Xunit;
namespace DeadWare.App.Tests;
public class ConnectionServiceTests
{
    private static readonly DateTimeOffset Epoch=new(2026,10,29,12,0,0,TimeSpan.Zero);
    private static readonly ConnectionPeer Peer=new(new(10,1,"fixture"),"192.168.1.2",100,"8.8.8.8",443,ConnectionProtocol.Tcp);
    private static Reading Reading(long sequence,int seconds,long bytes=10,bool complete=true,DateTimeOffset? coverage=null)
    {
        var time=Epoch.AddSeconds(seconds);
        var traffic=new ConnectionTraffic(Peer,time,time,bytes,0,bytes,0);
        var delta=new ConnectionDelta(new(time.Year,time.Month,time.Day,time.Hour,time.Minute,0,TimeSpan.Zero),Peer,bytes,0,time,time);
        return new(new(time,seconds,new Dictionary<string,double?>()),[],[],[],"CPU","","","",false)
        {Connections=new(time,seconds,sequence,"segment",[traffic],bytes>0?[delta]:[],coverage,true,complete,0,0,0,"")};
    }
    private sealed class Resolver(Func<string,ConnectionLocation?> lookup):IConnectionGeolocation {public ConnectionLocation? Lookup(string address)=>lookup(address);}
    private static ConnectionService Service(Action<IReadOnlyList<ConnectionHistoryRecord>,IReadOnlyList<ConnectionCoverage>> append,IConnectionGeolocation? resolver=null,ConnectionServiceOptions? options=null,Func<DateTimeOffset>? clock=null)=>new(resolver??new Resolver(_=>null),clock??(()=>Epoch.AddSeconds(10)),append,options);
    [Fact] public async Task Shared_live_updates_without_dashboard_or_recording_and_rejects_duplicate_old_publications()
    {
        var saved=new List<ConnectionHistoryRecord>();var service=Service((r,_)=>saved.AddRange(r));service.Configure(false,new(51,0));service.Start();
        service.TryRecord(Reading(2,2,20));var first=service.Live;
        Assert.Equal(20,Assert.Single(first.Connections).Traffic.ReceivedBytes);Assert.Equal(new GeoPoint(51,0),first.Origin);
        service.TryRecord(Reading(2,2,99));service.TryRecord(Reading(1,1,99));Assert.Equal(20,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);
        service.TryRecord(Reading(3,3,0));Assert.Equal(0,Assert.Single(service.Live.Connections).Traffic.DownloadBytesPerSecond);
        await service.StopAsync();Assert.Empty(saved);
    }
    [Fact] public async Task Pause_resume_acceptance_preserves_prior_writes_and_records_a_gap_with_saved_origin()
    {
        var saved=new List<ConnectionHistoryRecord>();var coverage=new List<ConnectionCoverage>();DateTimeOffset clock=Epoch;
        var service=Service((r,c)=>{saved.AddRange(r);coverage.AddRange(c);},clock:()=>clock);service.Start();
        service.Configure(true,new(1,2));service.TryRecord(Reading(1,1));
        clock=Epoch.AddSeconds(2);service.Configure(false,new(3,4));service.TryRecord(Reading(2,2,200));
        clock=Epoch.AddSeconds(3);service.Configure(true,new(5,6));service.TryRecord(Reading(3,4,30,coverage:Epoch.AddSeconds(2)));
        service.TryRecord(Reading(4,5,40,coverage:Epoch.AddSeconds(4)));
        await service.StopAsync();
        Assert.Equal(new long[]{10,30,40},saved.Select(r=>r.Delta.ReceivedBytes));
        Assert.Equal(new GeoPoint(1,2),saved[0].Origin);Assert.Equal(new GeoPoint(5,6),saved[1].Origin);
        Assert.NotEqual(saved[0].BatchId,saved[1].BatchId);Assert.False(saved[1].Complete);
        Assert.DoesNotContain(coverage,c=>c.Complete&&c.From<Epoch.AddSeconds(3)&&c.To>Epoch.AddSeconds(3));
    }
    [Fact] public async Task Coverage_splits_at_utc_minutes_without_retiming_event_deltas()
    {
        var saved=new List<ConnectionHistoryRecord>();var coverage=new List<ConnectionCoverage>();
        var service=Service((r,c)=>{saved.AddRange(r);coverage.AddRange(c);},clock:()=>Epoch.AddSeconds(61));service.Start();
        service.TryRecord(Reading(1,59));service.TryRecord(Reading(2,61,20,coverage:Epoch.AddSeconds(59)));
        await service.StopAsync();
        Assert.Equal(Epoch, saved[0].Delta.Minute);Assert.Equal(Epoch.AddMinutes(1),saved[1].Delta.Minute);
        Assert.Equal(2,coverage.Count);Assert.Equal(Epoch.AddMinutes(1),coverage[0].To);Assert.Equal(Epoch.AddMinutes(1),coverage[1].From);
    }
    [Fact] public async Task Queue_and_delta_budget_are_bounded_and_drops_are_visible_without_blocking_live()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var service=Service((_,_)=>{entered.Set();release.Wait();},options:new(QueueCapacity:2,MaximumBufferedDeltas:3,FlushInterval:TimeSpan.FromMilliseconds(10)));
        service.Start();service.TryRecord(Reading(1,1));Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try {for(int i=2;i<20;i++)service.TryRecord(Reading(i,i,i));Assert.InRange(service.PendingPublications,0,2);Assert.InRange(service.BufferedDeltas,0,3);Assert.True(service.DroppedObservations>0);Assert.Equal(19,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);}
        finally {release.Set();await service.StopAsync();}
    }
    [Fact] public async Task Resolver_failure_and_storage_failure_leave_live_counters_available()
    {
        var service=Service((_,_)=>throw new System.IO.IOException(),new Resolver(_=>throw new InvalidOperationException()));service.Start();
        service.TryRecord(Reading(1,1,123));await service.StopAsync();
        Assert.Equal(123,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);Assert.Contains("unavailable",service.StorageStatus,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task Shutdown_drains_accepted_data_without_waiting_for_stuck_enrichment()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var saved=new List<ConnectionHistoryRecord>();
        var service=Service((r,_)=>saved.AddRange(r),new Resolver(_=>{entered.Set();release.Wait();return null;}),new(EnrichmentBudget:TimeSpan.FromMinutes(1)));
        service.Start();service.TryRecord(Reading(1,1,71));Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try {await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));Assert.Equal(71,Assert.Single(saved).Delta.ReceivedBytes);}
        finally {release.Set();}
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(!predicate())await Task.Delay(5,timeout.Token);
    }
    [Fact] public async Task First_observation_uses_location_available_before_first_flush()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var place=new ConnectionLocation("GB","United Kingdom",null,"London",new(51,0),"fixture");var saved=new List<ConnectionHistoryRecord>();
        var service=Service((r,_)=>saved.AddRange(r),new Resolver(_=>{entered.Set();release.Wait();return place;}),new(EnrichmentBudget:TimeSpan.Zero));
        service.Start();service.TryRecord(Reading(1,1));Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        service.TryRecord(Reading(2,2));await Until(()=>service.PendingPublications==0);
        release.Set();await Until(()=>service.Live.Connections[0].Location is not null);
        await service.StopAsync();Assert.Equal(place,saved[0].Location);
    }
    [Fact] public async Task Normal_flush_records_unknown_when_lookup_is_stuck()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var saved=new TaskCompletionSource<ConnectionHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=Service((r,_)=>saved.TrySetResult(r[0]),new Resolver(_=>{entered.Set();release.Wait();return null;}),new(FlushInterval:TimeSpan.FromMilliseconds(30),EnrichmentBudget:TimeSpan.FromMilliseconds(5)));
        service.Start();service.TryRecord(Reading(1,1));Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try {Assert.Null((await saved.Task.WaitAsync(TimeSpan.FromSeconds(2))).Location);await service.StopAsync();}
        finally {release.Set();}
    }
    [Fact] public async Task Failed_storage_retries_keep_the_original_payload_when_lookup_later_changes()
    {
        using var release=new ManualResetEventSlim();var first=new TaskCompletionSource<ConnectionHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry=new TaskCompletionSource<ConnectionHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);int attempts=0;
        var place=new ConnectionLocation("GB","United Kingdom",null,"London",new(51,0),"fixture");
        var service=Service((r,_)=>{if(Interlocked.Increment(ref attempts)==1){first.SetResult(r[0]);throw new System.IO.IOException();}retry.TrySetResult(r[0]);},new Resolver(_=>{release.Wait();return place;}),new(FlushInterval:TimeSpan.FromMilliseconds(80),EnrichmentBudget:TimeSpan.Zero));
        service.Start();service.TryRecord(Reading(1,1));var original=await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        release.Set();await Until(()=>service.Live.Connections[0].Location is not null);
        Assert.Equal(original,await retry.Task.WaitAsync(TimeSpan.FromSeconds(3)));await service.StopAsync();
    }
    [Fact] public async Task Resume_omits_aggregates_that_include_paused_observations_and_skew_stays_out_of_current_buckets()
    {
        DateTimeOffset clock=Epoch;var saved=new List<ConnectionHistoryRecord>();var service=Service((r,_)=>saved.AddRange(r),clock:()=>clock);service.Start();
        service.Configure(false,null);clock=Epoch.AddSeconds(10);service.Configure(true,null);
        var mixed=Reading(1,11);mixed=mixed with {Connections=mixed.Connections! with {Deltas=[mixed.Connections!.Deltas[0] with {FirstSeen=Epoch.AddSeconds(9)}]}};
        service.TryRecord(mixed);
        var skew=Reading(2,12);skew=skew with {Connections=skew.Connections! with {Deltas=[skew.Connections!.Deltas[0] with {LastSeen=Epoch.AddSeconds(20)}]}};
        service.TryRecord(skew);clock=Epoch.AddSeconds(13);service.TryRecord(Reading(3,13,31));await service.StopAsync();
        Assert.Equal(31,Assert.Single(saved).Delta.ReceivedBytes);Assert.Equal(2,service.DroppedObservations);
    }
    [Fact] public async Task Location_cache_evicts_with_small_limit_and_stale_live_is_marked_unavailable()
    {
        int calls=0;DateTimeOffset clock=Epoch.AddSeconds(1);
        var service=Service((_,_)=>{},new Resolver(_=>{Interlocked.Increment(ref calls);return new("US","United States",null,null,null,"fixture");}),new(MaximumLocations:2),()=>clock);
        service.Configure(false,null);service.Start();
        async Task Observe(long sequence,string address,int expectedCalls)
        {
            var reading=Reading(sequence,(int)sequence);var traffic=reading.Connections!.Live[0];
            service.TryRecord(reading with {Connections=reading.Connections with {Live=[traffic with {Peer=Peer with {RemoteAddress=address}}]}});
            await Until(()=>Volatile.Read(ref calls)>=expectedCalls&&service.Live.Connections[0].Location is not null);
        }
        await Observe(1,"8.8.8.8",1);await Observe(2,"1.1.1.1",2);await Observe(3,"8.8.8.8",2);await Observe(4,"9.9.9.9",3);await Observe(5,"8.8.8.8",4);
        Assert.Equal(4,calls);clock=Epoch.AddMinutes(2);Assert.False(service.Live.Available);Assert.Contains("stale",service.Live.Status);await service.StopAsync();
    }
    [Fact] public async Task Local_database_drains_final_data_and_history_reads_do_not_replace_live()
    {
        string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));
        string path=System.IO.Path.Combine(directory,"connections.db");var service=new ConnectionService(path);
        Assert.False(System.IO.Directory.Exists(directory));
        try
        {
            service.Start();service.Configure(true,new(51,0));
            service.TryRecord(Reading(1,1,10));service.TryRecord(Reading(2,2,71).Connections! with {Complete=false});
            await service.StopAsync();
            var live=service.Live;var query=new ConnectionHistoryQuery(Epoch,Epoch.AddMinutes(2));
            var history=await service.ReadViewAsync(query);
            Assert.Equal(81,history.Summary.ReceivedBytes);Assert.False(Assert.Single(history.Rows).Complete);
            Assert.Equal(live,service.Live);
            string csv=System.IO.Path.Combine(directory,"export.csv");await service.ExportCsvAsync(csv,query);
            string text=await System.IO.File.ReadAllTextAsync(csv);Assert.Contains("8.8.8.8",text);Assert.DoesNotContain("192.168.1.2",text);
        }
        finally {await service.StopAsync();if(System.IO.Directory.Exists(directory))System.IO.Directory.Delete(directory,true);}
    }
    [Fact] public async Task Empty_enabled_maintenance_expires_old_history_without_any_dashboard_or_new_capture()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));string path=System.IO.Path.Combine(root,"history.db");
        var old=Reading(1,1).Connections!;var store=new ConnectionHistoryStore(path);store.Initialize();
        store.Append([new("old","old",old.Deltas[0],null,null,true)],[],Epoch.AddSeconds(2));
        var service=new ConnectionService(path,new Resolver(_=>null),()=>Epoch.AddDays(31),new(FlushInterval:TimeSpan.FromMilliseconds(20)));
        try
        {
            service.Start();await UntilAsync(async()=>(await service.ReadViewAsync(new(Epoch,Epoch.AddMinutes(1)))).TotalRows==0);
            Assert.Contains("saved locally",service.StorageStatus);await service.StopAsync();
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(!await predicate())await Task.Delay(10,timeout.Token);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Paused_recording_expires_history_on_timer_and_paused_relaunch(bool relaunch)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-paused-"+Guid.NewGuid().ToString("N"));string path=System.IO.Path.Combine(root,"history.db");
        var old=Reading(1,1).Connections!;var store=new ConnectionHistoryStore(path);store.Initialize();
        store.Append([new("old","old",old.Deltas[0],new("US","United States",null,"Saved",new(40,-90),"fixture"),null,true)],[new("coverage","old",Epoch,Epoch.AddSeconds(2),true)],Epoch.AddSeconds(2));
        long days=relaunch?31:0;
        var service=new ConnectionService(path,new Resolver(_=>null),()=>Epoch.AddDays(Volatile.Read(ref days)),new(FlushInterval:TimeSpan.FromMilliseconds(20)));
        service.Configure(false,null);
        try
        {
            if(!relaunch){service.Start();Assert.Equal(1,(await service.ReadViewAsync(new(Epoch,Epoch.AddMinutes(1)))).TotalRows);Volatile.Write(ref days,31);}
            // Relaunch reads/exports must apply retention even before its first timer tick.
            await UntilAsync(async()=>(await service.ReadViewAsync(new(Epoch,Epoch.AddMinutes(1)))).TotalRows==0);
            if(relaunch)service.Start();
            service.TryRecord(Reading(2,2,999));
            var view=await service.ReadViewAsync(new(Epoch,Epoch.AddDays(32)));
            Assert.Empty(view.Rows);Assert.Empty(view.Markers);Assert.Empty(view.Coverage);Assert.Null(view.EarliestRetainedTime);
            string csv=System.IO.Path.Combine(root,"expired.csv");await service.ExportCsvAsync(csv,new(Epoch,Epoch.AddDays(32)));
            Assert.Single((await System.IO.File.ReadAllLinesAsync(csv)));Assert.Contains("paused",service.StorageStatus);
            await service.StopAsync();Assert.Empty(store.Read(new(Epoch,Epoch.AddDays(32))).Rows);
            using var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText="SELECT count(*) FROM traffic";Assert.Equal(0L,(long)command.ExecuteScalar()!);
        }
        finally{await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Real_storage_failure_preserves_existing_bytes_and_live_capture()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
        string path=System.IO.Path.Combine(root,"history.db");byte[] corrupt=[1,2,3,4,5];await System.IO.File.WriteAllBytesAsync(path,corrupt);
        var service=new ConnectionService(path);
        try
        {
            service.Start();service.TryRecord(Reading(1,1,42));await service.StopAsync();
            Assert.Equal(42,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);Assert.Contains("unavailable",service.StorageStatus);
            Assert.Equal(corrupt,await System.IO.File.ReadAllBytesAsync(path));Assert.True(service.DroppedObservations>0);
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Later_database_and_origin_updates_do_not_rewrite_saved_geography()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
        string database=System.IO.Path.Combine(root,"locations.mmdb");await System.IO.File.WriteAllBytesAsync(database,ConnectionMmdbFixture.Create());
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),new ConnectionGeolocation(database),()=>Epoch.AddSeconds(10),new(FlushInterval:TimeSpan.FromMilliseconds(30)));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddMinutes(1));
        try
        {
            service.Configure(true,new(1,2));service.Start();service.TryRecord(Reading(1,1,10));
            await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==10);
            string replacement=database+".new";await System.IO.File.WriteAllBytesAsync(replacement,ConnectionMmdbFixture.Create(record:ConnectionMmdbFixture.Record(city:"Replacement")));System.IO.File.Move(replacement,database,true);
            await service.ReloadGeolocationAsync();service.Configure(true,new(3,4));service.TryRecord(Reading(2,2,20));
            await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==30);await service.StopAsync();
            var rows=(await service.ReadViewAsync(query)).Rows;
            Assert.Equal(new GeoPoint(1,2),Assert.Single(rows,r=>r.Location?.City=="Example City").Origin);
            Assert.Equal(new GeoPoint(3,4),Assert.Single(rows,r=>r.Location?.City=="Replacement").Origin);
            Assert.NotNull(service.DatabaseDate);Assert.Contains("DB-IP",service.LookupStatus);
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Session_normal_quit_keeps_final_subscription_until_the_endpoint_drain_is_persisted(bool failingFinalSubscriber)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));var service=new ConnectionService(System.IO.Path.Combine(root,"connections.db"));
        var session=(UiSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        void Set(string name,object value)=>typeof(UiSession).GetField("<"+name+">k__BackingField",flags)!.SetValue(session,value);
        var capture=new EtwNetwork(_=>new(11,1,"fixture"));typeof(Collector).GetField("etw",flags)!.SetValue(collector,capture);
        Set("Collector",collector);Set("Connections",service);Set("Cooling",new CoolingHistoryService(System.IO.Path.Combine(root,"cooling.db"),"fixture","setup"));
        Set("GraphHistory",new GraphHistoryService(System.IO.Path.Combine(root,"graphs.db"),"fixture",new SampleHistory()));Set("Battery",new BatteryHistoryService(System.IO.Path.Combine(root,"battery.db"),"fixture"));
        collector.SampleAvailable+=service.TryRecord;collector.FinalConnectionsAvailable+=service.TryRecord;
        if(failingFinalSubscriber)collector.FinalConnectionsAvailable+=_=>throw new InvalidOperationException("late subscriber failure");
        DateTimeOffset now=DateTimeOffset.UtcNow;
        try
        {
            service.Start();capture.TakeSnapshot(now,collector.ElapsedSeconds);
            capture.RecordEndpoint(11,now,71,false,System.Net.IPAddress.Parse("8.8.8.8"),443,System.Net.IPAddress.Loopback,123,ConnectionProtocol.Tcp);
            var stop=(Task)typeof(UiSession).GetMethod("StopCollectorAsync",flags)!.Invoke(session,[TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(20)])!;
            if(failingFinalSubscriber)await Assert.ThrowsAsync<InvalidOperationException>(()=>stop);else await stop;
            var history=await service.ReadViewAsync(new(now.AddMinutes(-1),now.AddMinutes(1)));Assert.Equal(71,history.Summary.ReceivedBytes);Assert.False(Assert.Single(history.Rows).Complete);
            Assert.True(service.StopAsync().IsCompleted);
        }
        finally
        {
            if(failingFinalSubscriber)await Assert.ThrowsAsync<InvalidOperationException>(()=>collector.DisposeAsync().AsTask());else await collector.DisposeAsync();
            await service.StopAsync();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);
        }
    }
    private sealed class DownloadHandler(byte[] body):System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)
            =>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(body)});
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Explicit_service_download_reloads_after_success_and_keeps_old_reader_after_failure(bool invalid)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
        string database=System.IO.Path.Combine(root,"locations.mmdb");await System.IO.File.WriteAllBytesAsync(database,ConnectionMmdbFixture.Create());
        byte[] bytes=invalid?[1,2,3]:ConnectionMmdbFixture.Create(record:ConnectionMmdbFixture.Record(city:"Downloaded"));
        using var http=new System.Net.Http.HttpClient(new DownloadHandler(ConnectionMmdbFixture.Gzip(bytes)));
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),database);
        typeof(ConnectionService).GetField("downloader",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(service,new ConnectionDatabaseDownload(http));
        try
        {
            service.Start();service.TryRecord(Reading(1,1));await Until(()=>service.Live.Connections[0].Location?.City=="Example City");
            if(invalid)await Assert.ThrowsAsync<MaxMind.Db.InvalidDatabaseException>(()=>service.UpdateLocationDatabaseAsync());else await service.UpdateLocationDatabaseAsync();
            service.TryRecord(Reading(2,2));await Until(()=>service.Live.Connections[0].Location?.City==(invalid?"Example City":"Downloaded"));
            Assert.NotNull(service.DatabaseDate);Assert.Contains("DB-IP",service.LookupStatus);
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Slow_enrichment_cannot_starve_a_due_flush_behind_all_queued_publications()
    {
        using var release=new ManualResetEventSlim();var flushed=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=Service((r,_)=>{if(r.Count>0)flushed.TrySetResult(r.Count);},new Resolver(_=>{release.Wait();return null;}),new(FlushInterval:TimeSpan.FromMilliseconds(20),EnrichmentBudget:TimeSpan.FromMilliseconds(200)));
        service.Start();for(int i=1;i<=5;i++)service.TryRecord(Reading(i,i));
        try {Assert.InRange(await flushed.Task.WaitAsync(TimeSpan.FromSeconds(3)),1,4);}
        finally {await service.StopAsync();release.Set();}
    }
    [Fact] public async Task Capture_loss_counters_stay_visible_in_the_shared_live_status()
    {
        var service=Service((_,_)=>{});service.Start();
        service.TryRecord(Reading(1,1).Connections! with {DroppedEvents=3,LostEvents=2,Complete=false});
        Assert.Contains("3 dropped",service.Live.Status);Assert.Contains("2 lost",service.Live.Status);await service.StopAsync();
    }
    [Fact] public async Task Export_refuses_to_overwrite_an_existing_file_and_preserves_its_bytes()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"));string target=System.IO.Path.Combine(root,"export.csv");byte[] sentinel=[0,1,2,3,255];await System.IO.File.WriteAllBytesAsync(target,sentinel);
        try
        {
            await Assert.ThrowsAsync<System.IO.IOException>(()=>service.ExportCsvAsync(target,new(Epoch,Epoch.AddMinutes(1))));
            Assert.Equal(sentinel,await System.IO.File.ReadAllBytesAsync(target));
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Clock_discontinuity_after_resume_starts_new_capture_time_without_reusing_the_old_pause_boundary()
    {
        DateTimeOffset clock=Epoch;var saved=new List<ConnectionHistoryRecord>();var service=Service((r,_)=>saved.AddRange(r),clock:()=>clock);service.Start();
        service.Configure(false,null);clock=Epoch.AddSeconds(100);service.Configure(true,null);service.TryRecord(Reading(1,101));
        clock=Epoch.AddSeconds(50);service.TryRecord(Reading(2,50,0).Connections! with {Segment="new",Complete=false,Available=false});
        clock=Epoch.AddSeconds(51);service.TryRecord(Reading(3,51,30,coverage:Epoch.AddSeconds(50)).Connections! with {Segment="new"});
        await service.StopAsync();Assert.Equal(30,saved.Sum(r=>r.Delta.ReceivedBytes));Assert.Equal(1,service.DroppedObservations);Assert.Equal(Epoch,saved.Last().Delta.Minute);
    }
    [Fact] public async Task Lost_events_and_sleep_discontinuities_remain_real_database_coverage_gaps()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));var ledger=new ConnectionLedger();
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),new Resolver(_=>null),()=>Epoch.AddSeconds(100));
        try
        {
            service.Start();service.TryRecord(ledger.Take(Epoch,0));ledger.Record(Peer,Epoch.AddSeconds(1),10,false);service.TryRecord(ledger.Take(Epoch.AddSeconds(1),1));
            ledger.Break(2);service.TryRecord(ledger.Take(Epoch.AddSeconds(2),2));service.TryRecord(ledger.Take(Epoch.AddSeconds(3),3));
            service.TryRecord(ledger.Take(Epoch.AddSeconds(90),90));service.TryRecord(ledger.Take(Epoch.AddSeconds(91),91));await service.StopAsync();
            var history=await service.ReadViewAsync(new(Epoch,Epoch.AddSeconds(92)));Assert.Equal(10,history.Summary.ReceivedBytes);
            Assert.Contains(history.Gaps,g=>g.From<=Epoch.AddSeconds(1)&&g.To>=Epoch.AddSeconds(2));
            Assert.Contains(history.Gaps,g=>g.From<=Epoch.AddSeconds(3)&&g.To>=Epoch.AddSeconds(90));
        }
        finally {await service.StopAsync();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
    }
    [Theory][InlineData(10,false)][InlineData(0,false)][InlineData(10,true)]
    public async Task Clock_rollback_retires_unflushed_future_records_and_coverage_without_blocking_fresh_storage(long oldBytes,bool stopImmediately)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));int seconds=100;
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),new Resolver(_=>null),()=>Epoch.AddSeconds(Volatile.Read(ref seconds)),new(QueueCapacity:stopImmediately?120:2,FlushInterval:TimeSpan.FromMinutes(1),EnrichmentBudget:TimeSpan.Zero));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddSeconds(110));
        try
        {
            await service.ReadViewAsync(query);service.Start();service.TryRecord(Reading(1,100,oldBytes,coverage:Epoch.AddSeconds(99)));
            await Until(()=>service.PendingPublications==0);Volatile.Write(ref seconds,50);
            service.TryRecord(Reading(2,50,30,false,Epoch.AddSeconds(49)).Connections! with {Segment="new"});
            if(stopImmediately)await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            else await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==30);
            await service.StopAsync();var history=await service.ReadViewAsync(query);var row=Assert.Single(history.Rows);
            Assert.Equal(30,row.ReceivedBytes);Assert.Equal(Epoch,row.Minute);Assert.Equal(Epoch.AddSeconds(50),row.FirstSeen);Assert.Equal(row.FirstSeen,row.LastSeen);Assert.False(row.Complete);
            Assert.Contains(history.Gaps,g=>g.From<=Epoch.AddSeconds(99)&&g.To>=Epoch.AddSeconds(100));
            Assert.DoesNotContain(history.Coverage,c=>c.Complete&&c.From<Epoch.AddSeconds(50)&&c.To>Epoch.AddSeconds(49));
            Assert.Equal(1,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);Assert.Contains("history has gaps",service.StorageStatus);Assert.Contains("saved locally",service.StorageStatus);
        }
        finally {await service.StopAsync();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Clock_rollback_discards_whole_attempted_batch_without_changing_saved_receipts_or_retrying_its_safe_subset()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));var store=new ConnectionHistoryStore(System.IO.Path.Combine(root,"history.db"));store.Initialize();int seconds=100;
        using var release=new ManualResetEventSlim();var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);ConnectionHistoryRecord[] original=[];ConnectionCoverage[] originalCoverage=[];int calls=0;
        var service=Service((r,c)=>
        {
            store.Append(r,c,Epoch.AddSeconds(Volatile.Read(ref seconds)));
            if(Interlocked.Increment(ref calls)==1){original=r.ToArray();originalCoverage=c.ToArray();attempted.SetResult();release.Wait();throw new System.IO.IOException("Commit succeeded before acknowledgement failed.");}
        },options:new(FlushInterval:TimeSpan.FromMilliseconds(100),EnrichmentBudget:TimeSpan.Zero),clock:()=>Epoch.AddSeconds(Volatile.Read(ref seconds)));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddSeconds(110));
        try
        {
            var old=Reading(1,100,10,coverage:Epoch.AddSeconds(99)).Connections!;
            service.Start();service.TryRecord(old with {Deltas=[old.Deltas[0],Reading(1,50,5).Connections!.Deltas[0]]});await attempted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Volatile.Write(ref seconds,50);service.TryRecord(Reading(2,50,30,false).Connections! with {Segment="new"});release.Set();
            await Until(()=>store.Read(query).Summary.ReceivedBytes==45);await service.StopAsync();
            Assert.Equal(2,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
            Assert.Equal(10,Assert.Single(store.Read(query).Rows,r=>r.Minute==Epoch.AddMinutes(1)).ReceivedBytes);
            // The saved original receipt remains valid: no subset, retimed record or changed
            // completeness/location was retried under its immutable BatchId.
            store.Append(original,originalCoverage,Epoch.AddSeconds(100));Assert.Equal(45,store.Read(query).Summary.ReceivedBytes);
            Assert.Equal(Epoch.AddSeconds(100),original[0].Delta.LastSeen);Assert.True(original[0].Complete);
            Assert.Contains(store.Read(query).Gaps,g=>g.From<=Epoch.AddSeconds(50)&&g.To>=Epoch.AddSeconds(51));
        }
        finally {release.Set();await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
}
