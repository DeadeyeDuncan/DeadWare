using System.Collections.Concurrent;
using DeadWare.App;
using DeadWare.Core;
using Xunit;
namespace DeadWare.App.Tests;
[Collection(TestHostThreadPool.TimingCollection)]
public class ConnectionServiceTests
{
    private static readonly DateTimeOffset Epoch=new(2026,10,29,12,0,0,TimeSpan.Zero);
    private static readonly ConnectionPeer Peer=new(new(10,1,"fixture"),"192.168.1.2",100,"8.8.8.8",443,ConnectionProtocol.Tcp);
    private static Reading Reading(long sequence,int seconds,long bytes=10,bool complete=true,DateTimeOffset? coverage=null,string segment="segment")
    {
        var time=Epoch.AddSeconds(seconds);
        var traffic=new ConnectionTraffic(Peer,time,time,bytes,0,bytes,0);
        var delta=new ConnectionDelta(new(time.Year,time.Month,time.Day,time.Hour,time.Minute,0,TimeSpan.Zero),Peer,bytes,0,time,time);
        return new(new(time,seconds,new Dictionary<string,double?>()),[],[],[],"CPU","","","",false)
        {Connections=new(time,seconds,sequence,segment,[traffic],bytes>0?[delta]:[],coverage,true,complete,0,0,0,"")};
    }
    /// <summary>A clock the writer can read while the test moves it, without torn reads.</summary>
    private sealed class TestClock
    {
        private long seconds;
        public void Set(int value)=>Volatile.Write(ref seconds,value);
        public DateTimeOffset Read()=>Epoch.AddSeconds(Volatile.Read(ref seconds));
    }
    /// <summary>Each append's records and coverage, copied when the writer makes it.</summary>
    private sealed class Appends
    {
        private readonly object sync=new();
        private readonly List<(IReadOnlyList<ConnectionHistoryRecord> Records,IReadOnlyList<ConnectionCoverage> Coverage)> calls=[];
        public void Add(IReadOnlyList<ConnectionHistoryRecord> records,IReadOnlyList<ConnectionCoverage> coverage) {lock(sync)calls.Add((records.ToArray(),coverage.ToArray()));}
        public (IReadOnlyList<ConnectionHistoryRecord> Records,IReadOnlyList<ConnectionCoverage> Coverage)[] All {get {lock(sync)return calls.ToArray();}}
        public (IReadOnlyList<ConnectionHistoryRecord> Records,IReadOnlyList<ConnectionCoverage> Coverage)[] WithRecords=>All.Where(c=>c.Records.Count>0).ToArray();
        /// <summary>Waits up to 15 s (or the given timeout) for the nth append that carries records, and returns it.</summary>
        public async Task<(IReadOnlyList<ConnectionHistoryRecord> Records,IReadOnlyList<ConnectionCoverage> Coverage)> WaitFor(int n,TimeSpan? timeout=null)
        {
            var deadline=DateTime.UtcNow+(timeout??TimeSpan.FromSeconds(15));
            while(WithRecords.Length<n)
            {
                if(DateTime.UtcNow>deadline)Assert.Fail($"Expected {n} appends with records before the timeout; saw {WithRecords.Length}.");
                await Task.Delay(5);
            }
            return WithRecords[n-1];
        }
    }
    /// <summary>One-second readings at Epoch + from … to, each covering from the previous second; a reading at + 1 s covers nothing.</summary>
    private static void RecordSeconds(ConnectionService service,TestClock clock,int from,int to)
    {
        for(int s=from;s<=to;s++){clock.Set(s);service.TryRecord(Reading(s,s,coverage:s==1?null:Epoch.AddSeconds(s-1)));}
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
        // The 30 and 40 merge into one generation-2 row of the same minute.
        Assert.Equal(new long[]{10,70},saved.Select(r=>r.Delta.ReceivedBytes));
        Assert.Equal(new GeoPoint(1,2),saved[0].Origin);Assert.Equal(new GeoPoint(5,6),saved[1].Origin);
        Assert.NotEqual(saved[0].BatchId,saved[1].BatchId);Assert.Equal(ConnectionMinuteFolder.BatchId("segment",Epoch,2,1),saved[1].BatchId);Assert.False(saved[1].Complete);
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
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));// an upper bound for a slow runner, not a timing property
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
        var service=Service((r,_)=>saved.TrySetResult(r[0]),new Resolver(_=>{entered.Set();release.Wait();return null;}),new(FlushInterval:TimeSpan.FromMilliseconds(30),EnrichmentBudget:TimeSpan.FromMilliseconds(5)),()=>Epoch.AddSeconds(100));
        service.Start();service.TryRecord(Reading(1,1));Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try {Assert.Null((await saved.Task.WaitAsync(TimeSpan.FromSeconds(2))).Location);await service.StopAsync();}
        finally {release.Set();}
    }
    [Fact] public async Task Failed_storage_retries_keep_the_original_payload_when_lookup_later_changes()
    {
        using var release=new ManualResetEventSlim();var first=new TaskCompletionSource<ConnectionHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry=new TaskCompletionSource<ConnectionHistoryRecord>(TaskCreationOptions.RunContinuationsAsynchronously);int attempts=0;
        var place=new ConnectionLocation("GB","United Kingdom",null,"London",new(51,0),"fixture");
        var service=Service((r,_)=>{if(Interlocked.Increment(ref attempts)==1){first.SetResult(r[0]);throw new System.IO.IOException();}retry.TrySetResult(r[0]);},new Resolver(_=>{release.Wait();return place;}),new(FlushInterval:TimeSpan.FromMilliseconds(80),EnrichmentBudget:TimeSpan.Zero),()=>Epoch.AddSeconds(100));
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
        string path=System.IO.Path.Combine(directory,"connections.db");var service=new ConnectionService(path,new Resolver(_=>null),()=>Epoch.AddSeconds(100));
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
        var clock=new TestClock();clock.Set(10);
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),new ConnectionGeolocation(database),clock.Read,new(FlushInterval:TimeSpan.FromMilliseconds(30)));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddMinutes(1));
        try
        {
            service.Configure(true,new(1,2));service.Start();service.TryRecord(Reading(1,1,10));clock.Set(100);
            await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==10);
            string replacement=database+".new";await System.IO.File.WriteAllBytesAsync(replacement,ConnectionMmdbFixture.Create(record:ConnectionMmdbFixture.Record(city:"Replacement")));System.IO.File.Move(replacement,database,true);
            // Minute 0 is saved, so the second reading opens a late unit: the Replacement location and origin (3, 4), written by R4.
            await service.ReloadGeolocationAsync();service.Configure(true,new(3,4));service.TryRecord(Reading(2,2,20));
            await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==30);await service.StopAsync();
            var rows=(await service.ReadViewAsync(query)).Rows;
            Assert.Equal(new GeoPoint(1,2),Assert.Single(rows,r=>r.Location?.City=="Example City").Origin);
            Assert.Equal(new GeoPoint(3,4),Assert.Single(rows,r=>r.Location?.City=="Replacement").Origin);
            Assert.NotNull(service.DatabaseDate);Assert.Contains("DB-IP",service.LookupStatus);
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    private const System.Reflection.BindingFlags NonPublicInstance=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    /// <summary>An uninitialized UiSession holding the collector, the connection service and fixture history services under root, with a fixture ETW capture.</summary>
    private static (UiSession Session,EtwNetwork Capture) FixtureSession(string root,Collector collector,ConnectionService service)
    {
        var session=(UiSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        void Set(string name,object value)=>typeof(UiSession).GetField("<"+name+">k__BackingField",NonPublicInstance)!.SetValue(session,value);
        var capture=new EtwNetwork(_=>new(11,1,"fixture"));typeof(Collector).GetField("etw",NonPublicInstance)!.SetValue(collector,capture);
        Set("Collector",collector);Set("Connections",service);Set("Cooling",new CoolingHistoryService(System.IO.Path.Combine(root,"cooling.db"),"fixture","setup"));
        Set("GraphHistory",new GraphHistoryService(System.IO.Path.Combine(root,"graphs.db"),"fixture",new SampleHistory()));Set("Battery",new BatteryHistoryService(System.IO.Path.Combine(root,"battery.db"),"fixture"));Set("Usage",new UsageHistoryService(System.IO.Path.Combine(root,"usage.db"),"fixture"));Set("Alerts",new AlertService(System.IO.Path.Combine(root,"alerts.db"),"fixture",new UsageHistoryService(System.IO.Path.Combine(root,"usage-alerts.db"),"fixture"),()=>null));
        return (session,capture);
    }
    private static Task StopCollector(UiSession session,TimeSpan providers,TimeSpan connections,TimeSpan history)
        =>(Task)typeof(UiSession).GetMethod("StopCollectorAsync",NonPublicInstance)!.Invoke(session,[providers,connections,history])!;
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Session_normal_quit_keeps_final_subscription_until_the_endpoint_drain_is_persisted(bool failingFinalSubscriber)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));var service=new ConnectionService(System.IO.Path.Combine(root,"connections.db"));
        var (session,capture)=FixtureSession(root,collector,service);
        collector.SampleAvailable+=service.TryRecord;collector.FinalConnectionsAvailable+=service.TryRecord;
        if(failingFinalSubscriber)collector.FinalConnectionsAvailable+=_=>throw new InvalidOperationException("late subscriber failure");
        DateTimeOffset now=DateTimeOffset.UtcNow;
        try
        {
            service.Start();capture.TakeSnapshot(now,collector.ElapsedSeconds);
            capture.RecordEndpoint(11,now,71,false,System.Net.IPAddress.Parse("8.8.8.8"),443,System.Net.IPAddress.Loopback,123,ConnectionProtocol.Tcp);
            var stop=StopCollector(session,TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(20));
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
        using var release=new ManualResetEventSlim();var flushed=new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service=Service((r,_)=>{if(r.Count>0)flushed.TrySetResult(r[0].Delta.ReceivedBytes);},new Resolver(_=>{release.Wait();return null;}),new(FlushInterval:TimeSpan.FromMilliseconds(20),EnrichmentBudget:TimeSpan.FromMilliseconds(200)),()=>Epoch.AddSeconds(100));
        service.Start();for(int i=1;i<=5;i++)service.TryRecord(Reading(i,i));
        // R4 froze the minute at a tick while publications were still queued: fewer than all five 10-byte readings.
        try {Assert.InRange(await flushed.Task.WaitAsync(TimeSpan.FromSeconds(3)),10,40);}
        finally {await service.StopAsync();release.Set();}
    }
    [Fact] public async Task Capture_loss_counters_stay_visible_in_the_shared_live_status()
    {
        var service=Service((_,_)=>{});service.Start();
        service.TryRecord(Reading(1,1).Connections! with {DroppedEvents=3,LostEvents=2,Complete=false});
        Assert.Contains("3 dropped",service.Live.Status);Assert.Contains("2 lost",service.Live.Status);await service.StopAsync();
    }
    [Fact] public async Task Lookup_worker_survives_a_database_that_fails_to_load_at_start()
    {
        // A faulting first Reload used to take the worker down: lookups never resolved and ReloadGeolocationAsync never completed.
        var resolver=new ConnectionGeolocation("unused",_=>throw new System.IO.InvalidDataException("Expected a DB-IP City Lite MMDB database."));
        var service=Service((_,_)=>{},resolver);service.Start();
        await service.ReloadGeolocationAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("could not be read",service.LookupStatus);
        service.TryRecord(Reading(1,1));await service.ReloadGeolocationAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(Assert.Single(service.Live.Connections).Location);await service.StopAsync();
    }
    [Fact] public async Task Destinations_export_writes_a_new_utf8_file_without_a_bom_and_never_overwrites()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));string path=System.IO.Path.Combine(root,"history.db");
        var store=new ConnectionHistoryStore(path);store.Initialize();
        store.Append([new("saved","saved",Reading(1,1).Connections!.Deltas[0],null,null,true)],[],Epoch.AddSeconds(70));
        var service=new ConnectionService(path,new Resolver(_=>null),()=>Epoch.AddSeconds(100));string target=System.IO.Path.Combine(root,"destinations.csv");
        try
        {
            await service.ExportDestinationsCsvAsync(target,new(Epoch,Epoch.AddMinutes(2)),DestinationScope.Internet);
            byte[] bytes=await System.IO.File.ReadAllBytesAsync(target);
            Assert.False(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF,"The export starts with a UTF-8 BOM.");
            string[] lines=System.Text.Encoding.UTF8.GetString(bytes).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete",lines[0]);
            Assert.StartsWith("\"8.8.8.8\",\"Internet\",",Assert.Single(lines.Skip(1)));
            // The Local scope leaves only the header.
            string local=System.IO.Path.Combine(root,"local.csv");await service.ExportDestinationsCsvAsync(local,new(Epoch,Epoch.AddMinutes(2)),DestinationScope.Local);
            Assert.Single((await System.IO.File.ReadAllTextAsync(local)).Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries));
            await Assert.ThrowsAsync<System.IO.IOException>(()=>service.ExportDestinationsCsvAsync(target,new(Epoch,Epoch.AddMinutes(2)),DestinationScope.All));
            Assert.Equal(bytes,await System.IO.File.ReadAllBytesAsync(target));
        }
        finally {await service.StopAsync();System.IO.Directory.Delete(root,true);}
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
        var service=new ConnectionService(System.IO.Path.Combine(root,"history.db"),new Resolver(_=>null),()=>Epoch.AddSeconds(Volatile.Read(ref seconds)),new(QueueCapacity:stopImmediately?120:2,FlushInterval:TimeSpan.FromMilliseconds(20),EnrichmentBudget:TimeSpan.Zero));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddSeconds(110));
        try
        {
            await service.ReadViewAsync(query);service.Start();service.TryRecord(Reading(1,100,oldBytes,coverage:Epoch.AddSeconds(99)));
            await Until(()=>service.PendingPublications==0);Volatile.Write(ref seconds,30);
            // 68 s past the minute-1 unit's end, beyond the hold: R2 freezes it and the same step retires it.
            service.TryRecord(Reading(2,30,30,false,Epoch.AddSeconds(29),"new"));
            if(stopImmediately)await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            else
            {
                await Until(()=>service.DroppedObservations==1);Volatile.Write(ref seconds,95);
                await UntilAsync(async()=>(await service.ReadViewAsync(query)).Summary.ReceivedBytes==30);
            }
            await service.StopAsync();var history=await service.ReadViewAsync(query);var row=Assert.Single(history.Rows);
            Assert.Equal(30,row.ReceivedBytes);Assert.Equal(Epoch,row.Minute);Assert.Equal(Epoch.AddSeconds(30),row.FirstSeen);Assert.Equal(row.FirstSeen,row.LastSeen);Assert.False(row.Complete);
            Assert.Contains(history.Gaps,g=>g.From<=Epoch.AddSeconds(99)&&g.To>=Epoch.AddSeconds(100));
            Assert.DoesNotContain(history.Coverage,c=>c.Complete&&c.From<Epoch.AddSeconds(30)&&c.To>Epoch.AddSeconds(29));
            Assert.Equal(1,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);Assert.Contains("history has gaps",service.StorageStatus);Assert.Contains("saved locally",service.StorageStatus);
        }
        finally {await service.StopAsync();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Clock_rollback_discards_whole_attempted_batch_without_changing_saved_receipts_or_retrying_its_safe_subset()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));var store=new ConnectionHistoryStore(System.IO.Path.Combine(root,"history.db"));store.Initialize();int seconds=160;
        using var release=new ManualResetEventSlim();var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);ConnectionHistoryRecord[] original=[];ConnectionCoverage[] originalCoverage=[];int calls=0;
        var service=Service((r,c)=>
        {
            store.Append(r,c,Epoch.AddSeconds(Volatile.Read(ref seconds)));
            if(r.Count>0&&Interlocked.Increment(ref calls)==1){original=r.ToArray();originalCoverage=c.ToArray();attempted.SetResult();release.Wait();throw new System.IO.IOException("Commit succeeded before acknowledgement failed.");}
        },options:new(FlushInterval:TimeSpan.FromMilliseconds(100),EnrichmentBudget:TimeSpan.Zero),clock:()=>Epoch.AddSeconds(Volatile.Read(ref seconds)));
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddSeconds(110));
        try
        {
            // Two minute-1 deltas: Peer at + 100 s and Peer on port 80 at + 70 s. R4 freezes the unit at the first tick.
            var old=Reading(1,100,10,coverage:Epoch.AddSeconds(99)).Connections!;var other=Reading(1,70,5).Connections!.Deltas[0] with {Peer=Peer with {RemotePort=80}};
            service.Start();service.TryRecord(old with {Deltas=[old.Deltas[0],other]});await attempted.Task.WaitAsync(TimeSpan.FromSeconds(15));// a loaded CI runner needs more than 3 s to start the writer
            // After the step back the unit is 68 s ahead: the next attempt retires it whole, and R6 writes the new unit.
            Volatile.Write(ref seconds,30);service.TryRecord(Reading(2,30,30,false,segment:"new"));release.Set();
            await Until(()=>service.DroppedObservations==2);await service.StopAsync();
            Assert.Equal(45,store.Read(query).Summary.ReceivedBytes);
            Assert.Equal(2,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
            Assert.Equal(new long[]{5,10},store.Read(query).Rows.Where(r=>r.Minute==Epoch.AddMinutes(1)).Select(r=>r.ReceivedBytes).Order());
            // The saved original receipt remains valid: no subset, retimed record or changed
            // completeness/location was retried under its immutable BatchId.
            store.Append(original,originalCoverage,Epoch.AddSeconds(100));Assert.Equal(45,store.Read(query).Summary.ReceivedBytes);
            Assert.Equal(new[]{Epoch.AddSeconds(100),Epoch.AddSeconds(70)},original.Select(r=>r.Delta.LastSeen));Assert.All(original,r=>Assert.True(r.Complete));
            Assert.Contains(store.Read(query).Gaps,g=>g.From<=Epoch.AddSeconds(30)&&g.To>=Epoch.AddSeconds(31));
        }
        finally {release.Set();await service.StopAsync();System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Open_minute_is_written_once_when_it_closes_and_live_is_unchanged()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,clock:clock.Read);service.Start();
        try
        {
            for(int s=1;s<=62;s++)
            {
                RecordSeconds(service,clock,s,s);
                var live=service.Live;Assert.Equal(Epoch.AddSeconds(s),live.Timestamp);Assert.Equal(10,Assert.Single(live.Connections).Traffic.ReceivedBytes);
            }
            await Task.Delay(200);Assert.Empty(calls.WithRecords);
            // CoverageFrom + 62 s closes minute 0 (R1): one summed record and one complete run [+1 s, +60 s).
            RecordSeconds(service,clock,63,63);var append=await calls.WaitFor(1);var record=Assert.Single(append.Records);
            Assert.Equal((Epoch,590L,true),(record.Delta.Minute,record.Delta.ReceivedBytes,record.Complete));
            var run=Assert.Single(append.Coverage,c=>c.From<Epoch.AddMinutes(1));
            Assert.Equal((Epoch.AddSeconds(1),Epoch.AddMinutes(1),true),(run.From,run.To,run.Complete));
            Assert.Equal(10,Assert.Single(service.Live.Connections).Traffic.ReceivedBytes);
        }
        finally {await service.StopAsync();}
    }
    [Fact] public async Task Stop_writes_the_open_minute()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,clock:clock.Read);service.Start();
        RecordSeconds(service,clock,1,30);await service.StopAsync();
        var append=Assert.Single(calls.WithRecords);var record=Assert.Single(append.Records);
        Assert.Equal((Epoch,300L),(record.Delta.Minute,record.Delta.ReceivedBytes));
        var run=Assert.Single(append.Coverage);Assert.Equal((Epoch.AddSeconds(1),Epoch.AddSeconds(30),true),(run.From,run.To,run.Complete));
        Assert.Equal(0,service.BufferedDeltas);Assert.Equal(0,service.DroppedObservations);
    }
    [Fact] public async Task Session_ending_stop_writes_the_open_minute()
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-connections-"+Guid.NewGuid().ToString("N"));var clock=new TestClock();
        var collector=new Collector(new CollectorOptions(HardwareEnabled:false));var service=new ConnectionService(System.IO.Path.Combine(root,"connections.db"),new Resolver(_=>null),clock.Read);
        var (session,capture)=FixtureSession(root,collector,service);
        try
        {
            service.Start();capture.TakeSnapshot(DateTimeOffset.UtcNow,collector.ElapsedSeconds);RecordSeconds(service,clock,1,30);
            // The session-ending budget: 3 s for providers, the connections stop after 1.5 s at the latest, 3 s for history.
            var stop=StopCollector(session,TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(1.5),TimeSpan.FromSeconds(3));
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            var row=Assert.Single((await service.ReadViewAsync(new(Epoch,Epoch.AddMinutes(1)))).Rows);Assert.Equal((Epoch,300L),(row.Minute,row.ReceivedBytes));
        }
        finally {await collector.DisposeAsync();await service.StopAsync();if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);}
    }
    [Fact] public async Task Pause_writes_the_open_minute_at_the_next_tick_and_resume_uses_a_new_batch()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,options:new(FlushInterval:TimeSpan.FromMilliseconds(20)),clock:clock.Read);service.Start();
        try
        {
            RecordSeconds(service,clock,1,10);await Until(()=>service.PendingPublications==0);
            service.Configure(false,null);
            var paused=Assert.Single((await calls.WaitFor(1,TimeSpan.FromSeconds(1))).Records);
            Assert.Equal((Epoch,100L,ConnectionMinuteFolder.BatchId("segment",Epoch,0,0)),(paused.Delta.Minute,paused.Delta.ReceivedBytes,paused.BatchId));
            service.Configure(true,null);RecordSeconds(service,clock,11,20);await service.StopAsync();
            Assert.Equal(2,calls.WithRecords.Length);var resumed=Assert.Single(calls.WithRecords[1].Records);
            // The same minute again, under a new BatchId in the resumed generation (2).
            Assert.Equal((Epoch,100L,ConnectionMinuteFolder.BatchId("segment",Epoch,2,1)),(resumed.Delta.Minute,resumed.Delta.ReceivedBytes,resumed.BatchId));
            Assert.Equal(0,service.BufferedDeltas);
        }
        finally {await service.StopAsync();}
    }
    [Fact] public async Task Failed_write_retries_identical_units_and_new_data_uses_new_batches()
    {
        var clock=new TestClock();var calls=new Appends();int failures=0;
        var service=Service((r,c)=>{calls.Add(r,c);if(r.Count>0&&Interlocked.Increment(ref failures)==1)throw new System.IO.IOException();},
            options:new(FlushInterval:TimeSpan.FromMilliseconds(50)),clock:clock.Read);
        service.Start();
        try
        {
            RecordSeconds(service,clock,1,63);
            var failed=await calls.WaitFor(1);Assert.Equal((Epoch,590L),(Assert.Single(failed.Records).Delta.Minute,failed.Records[0].Delta.ReceivedBytes));
            var firstIds=failed.Records.Select(r=>r.BatchId).Concat(failed.Coverage.Select(c=>c.BatchId)).ToHashSet(StringComparer.Ordinal);
            // A late 7-byte delta for minute 0 after the failure: it opens a new unit, which R1 freezes at once.
            clock.Set(64);var late=Reading(64,64,7,coverage:Epoch.AddSeconds(63)).Connections!;
            service.TryRecord(late with {Deltas=[late.Deltas[0] with {Minute=Epoch,FirstSeen=Epoch.AddSeconds(59),LastSeen=Epoch.AddSeconds(59)}]});
            var retry=await calls.WaitFor(2);
            Assert.Equal(failed.Records,retry.Records.Where(r=>firstIds.Contains(r.BatchId)));
            Assert.Equal(failed.Coverage,retry.Coverage.Where(c=>firstIds.Contains(c.BatchId)));
            await service.StopAsync();
            var later=calls.WithRecords.Skip(1).SelectMany(c=>c.Records).ToArray();
            var lateRecord=Assert.Single(later,r=>r.Delta.ReceivedBytes==7);Assert.Equal(Epoch,lateRecord.Delta.Minute);Assert.DoesNotContain(lateRecord.BatchId,firstIds);
            Assert.All(later.Where(r=>r.Delta.Minute==Epoch.AddMinutes(1)),r=>Assert.DoesNotContain(r.BatchId,firstIds));
            Assert.Equal(0,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);Assert.Contains("saved locally",service.StorageStatus);
        }
        finally {await service.StopAsync();}
    }
    [Fact] public async Task Pressure_freezes_open_units_at_half_the_delta_budget()
    {
        var calls=new Appends();var service=Service(calls.Add,options:new(MaximumBufferedDeltas:8),clock:()=>Epoch.AddSeconds(1));service.Start();
        try
        {
            // Four distinct peers in one reading: Held reaches 4, half the budget, so R5 freezes the open unit with no R1 and no tick.
            var reading=Reading(1,1).Connections!;var delta=reading.Deltas[0];
            service.TryRecord(reading with {Deltas=[..Enumerable.Range(0,4).Select(i=>delta with {Peer=Peer with {RemotePort=1000+i}})]});
            var append=await calls.WaitFor(1,TimeSpan.FromSeconds(5));
            Assert.Equal(new[]{1000,1001,1002,1003},append.Records.Select(r=>r.Delta.Peer.RemotePort));Assert.Single(append.Records.Select(r=>r.BatchId).Distinct());
            await Until(()=>service.BufferedDeltas==0);
        }
        finally {await service.StopAsync();}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Small_clock_rollback_holds_a_frozen_minute_until_the_clock_passes_it(bool stopAt95)
    {
        var clock=new TestClock();clock.Set(100);var calls=new Appends();
        var service=Service(calls.Add,options:new(FlushInterval:TimeSpan.FromMilliseconds(20)),clock:clock.Read);service.Start();
        try
        {
            service.TryRecord(Reading(1,100,coverage:Epoch.AddSeconds(99)));await Until(()=>service.PendingPublications==0);
            // The step back: R2 freezes minute 1, which is 48 s ahead of the clock, so it is held, not retired.
            clock.Set(50);service.TryRecord(Reading(2,50,segment:"new"));await Until(()=>service.PendingPublications==0);
            await Task.Delay(200);Assert.Empty(calls.WithRecords);Assert.Equal(0,service.DroppedObservations);
            // R4 writes minute 0 while minute 1 is still 3 s ahead.
            clock.Set(95);var minute0=Assert.Single((await calls.WaitFor(1)).Records);
            Assert.Equal((Epoch,Epoch.AddSeconds(50),"new"),(minute0.Delta.Minute,minute0.Delta.LastSeen,minute0.Segment));
            if(stopAt95)
            {
                await service.StopAsync();Assert.Single(calls.WithRecords);Assert.Equal(1,service.DroppedObservations);
            }
            else
            {
                await Task.Delay(200);Assert.Single(calls.WithRecords);
                clock.Set(100);var later=await calls.WaitFor(2);var record=Assert.Single(later.Records);
                Assert.Equal((ConnectionMinuteFolder.BatchId("segment",Epoch.AddMinutes(1),0,0),Epoch.AddMinutes(1),10L,Epoch.AddSeconds(100),true),
                    (record.BatchId,record.Delta.Minute,record.Delta.ReceivedBytes,record.Delta.LastSeen,record.Complete));
                var run=Assert.Single(later.Coverage);Assert.Equal((Epoch.AddSeconds(99),Epoch.AddSeconds(100),true),(run.From,run.To,run.Complete));
                await service.StopAsync();Assert.Equal(0,service.DroppedObservations);
            }
            Assert.Equal(0,service.BufferedDeltas);
        }
        finally {await service.StopAsync();}
    }
    /// <summary>One sample at Epoch + seconds with a 10-byte delta per named app, covering from the previous second (nothing at + 1 s).</summary>
    private static ConnectionSample Apps(int seconds,params string[] names)
    {
        var time=Epoch.AddSeconds(seconds);var minute=new DateTimeOffset(time.Year,time.Month,time.Day,time.Hour,time.Minute,0,TimeSpan.Zero);
        var peers=names.Select((name,i)=>Peer with {Process=new(20+i,1,name)}).ToArray();
        return new(time,seconds,seconds,"segment",[..peers.Select(p=>new ConnectionTraffic(p,time,time,10,0,10,0))],
            [..peers.Select(p=>new ConnectionDelta(minute,p,10,0,time,time))],seconds==1?null:Epoch.AddSeconds(seconds-1),true,true,0,0,0,"");
    }
    private static void RecordApps(ConnectionService service,TestClock clock,int from,int to,params string[] names)
    {
        for(int s=from;s<=to;s++){clock.Set(s);service.TryRecord(Apps(s,names));}
    }
    [Fact] public async Task Excluded_deltas_leave_buffered_balanced()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,clock:clock.Read);
        service.SetExclusions(AppExclusions.Keys(["A.EXE"]));service.Start();
        try
        {
            // Minute 0 closes by capture at + 63 s (R1); minute 1 is written by the final write.
            RecordApps(service,clock,1,63,"a.exe","b.exe");await calls.WaitFor(1);await service.StopAsync();
            var records=calls.All.SelectMany(c=>c.Records).ToArray();var coverage=calls.All.SelectMany(c=>c.Coverage).ToArray();
            Assert.DoesNotContain(records,r=>AppKey.Of(r.Delta.Peer.Process.Name)=="A.EXE");
            Assert.Equal(0,service.DroppedObservations);
            var b=Assert.Single(records,r=>r.Delta.Minute==Epoch);
            Assert.Equal(("b.exe",590L,true),(b.Delta.Peer.Process.Name,b.Delta.ReceivedBytes,b.Complete));
            Assert.NotEmpty(coverage);Assert.All(coverage,c=>Assert.True(c.Complete));
            Assert.Equal(0,service.BufferedDeltas);
        }
        finally {await service.StopAsync();}
    }
    [Fact] public async Task Excluded_app_shows_live_and_is_never_appended()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,clock:clock.Read);
        var control=Service((_,_)=>{},clock:clock.Read);
        service.SetExclusions(AppExclusions.Keys(["a.exe"]));service.Start();control.Start();
        try
        {
            for(int s=1;s<=30;s++)
            {
                clock.Set(s);service.TryRecord(Apps(s,"a.exe","b.exe"));control.TryRecord(Apps(s,"a.exe","b.exe"));
                var live=service.Live;
                Assert.Contains(live.Connections,c=>c.Traffic.Peer.Process.Name=="a.exe");
                Assert.Equal(control.Live.Connections.Select(c=>c.Traffic),live.Connections.Select(c=>c.Traffic));
                Assert.Equal(control.Live.Complete,live.Complete);Assert.True(live.Complete);
            }
            await service.StopAsync();await control.StopAsync();
            var records=calls.All.SelectMany(c=>c.Records).ToArray();
            Assert.DoesNotContain(records,r=>r.Delta.Peer.Process.Name=="a.exe");
            Assert.Equal(("b.exe",300L),(Assert.Single(records).Delta.Peer.Process.Name,records[0].Delta.ReceivedBytes));
            Assert.Equal(control.DroppedObservations,service.DroppedObservations);Assert.Equal(0,service.DroppedObservations);
            Assert.Equal(control.Live.Complete,service.Live.Complete);
        }
        finally {await service.StopAsync();await control.StopAsync();}
    }
    [Fact] public async Task Retried_batch_keeps_its_payload_when_the_list_changes()
    {
        var clock=new TestClock();var calls=new Appends();int failures=0;using var changed=new ManualResetEventSlim();
        var service=Service((r,c)=>
            {
                calls.Add(r,c);
                // The R1 write fails only after the list has changed, so the retry follows the change.
                if(r.Count>0&&Interlocked.Increment(ref failures)==1){changed.Wait(TimeSpan.FromSeconds(10));throw new System.IO.IOException();}
            },options:new(FlushInterval:TimeSpan.FromMilliseconds(50)),clock:clock.Read);
        service.Start();
        try
        {
            RecordApps(service,clock,1,63,"a.exe","b.exe");
            var failed=await calls.WaitFor(1);
            Assert.Contains(failed.Records,r=>r.Delta.Peer.Process.Name=="b.exe");
            service.SetExclusions(AppExclusions.Keys(["B.EXE"]));changed.Set();
            var ids=failed.Records.Select(r=>r.BatchId).Concat(failed.Coverage.Select(c=>c.BatchId)).ToHashSet(StringComparer.Ordinal);
            var retry=await calls.WaitFor(2);
            Assert.Equal(failed.Records,retry.Records.Where(r=>ids.Contains(r.BatchId)));
            Assert.Equal(failed.Coverage,retry.Coverage.Where(c=>ids.Contains(c.BatchId)));
            Assert.Contains(retry.Records,r=>ids.Contains(r.BatchId)&&r.Delta.Peer.Process.Name=="b.exe");
            // Minute 1: b.exe keeps the + 60 … + 63 s merged before the change; its + 64 s delta is not saved.
            RecordApps(service,clock,64,64,"a.exe","b.exe");await service.StopAsync();
            var minute1=calls.All.Skip(1).SelectMany(c=>c.Records).Where(r=>r.Delta.Minute==Epoch.AddMinutes(1)).ToArray();
            Assert.Equal(40L,Assert.Single(minute1,r=>r.Delta.Peer.Process.Name=="b.exe").Delta.ReceivedBytes);
            Assert.Equal(50L,Assert.Single(minute1,r=>r.Delta.Peer.Process.Name=="a.exe").Delta.ReceivedBytes);
            Assert.Equal(0,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
        }
        finally {changed.Set();await service.StopAsync();}
    }
    [Fact] public async Task Excluding_an_app_mid_minute_keeps_what_was_merged_before()
    {
        var clock=new TestClock();var calls=new Appends();var service=Service(calls.Add,clock:clock.Read);service.Start();
        try
        {
            RecordApps(service,clock,1,30,"a.exe");
            // Every publication merged: one entry and one coverage run held, nothing queued or in flight (+ 30 s would add 1).
            await Until(()=>service.PendingPublications==0&&service.BufferedDeltas==2);
            service.SetExclusions(AppExclusions.Keys(["A.EXE"]));
            RecordApps(service,clock,31,62,"a.exe");await service.StopAsync();
            var records=calls.All.SelectMany(c=>c.Records).ToArray();
            var minute0=Assert.Single(records,r=>r.Delta.Minute==Epoch);
            Assert.Equal(("a.exe",300L,Epoch.AddSeconds(30)),(minute0.Delta.Peer.Process.Name,minute0.Delta.ReceivedBytes,minute0.Delta.LastSeen));
            Assert.DoesNotContain(records,r=>r.Delta.Minute==Epoch.AddMinutes(1));
            Assert.Contains(calls.All.SelectMany(c=>c.Coverage),c=>c.From>=Epoch.AddMinutes(1));
            Assert.Equal(0,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
        }
        finally {await service.StopAsync();}
    }
    /// <summary>Appends through the real store of a wrapAppend service, keeping each call's payload, maintenance bound and whether the store's clear was pending.</summary>
    private sealed class ClearAppends
    {
        private readonly object sync=new();
        private readonly List<(ConnectionHistoryRecord[] Records,ConnectionCoverage[] Coverage,int Maintenance,bool Pending)> calls=[];
        /// <summary>Asked on each call that carries records; true throws IOException before the store sees it.</summary>
        public Func<bool> Fail=()=>false;
        public Func<bool> Pending=()=>false;
        public ConnectionAppend Wrap(ConnectionAppend inner)=>(records,coverage,now,maintenance)=>
        {
            lock(sync)calls.Add((records.ToArray(),coverage.ToArray(),maintenance,Pending()));
            if(records.Count>0&&Fail())throw new System.IO.IOException();
            inner(records,coverage,now,maintenance);
        };
        public (ConnectionHistoryRecord[] Records,ConnectionCoverage[] Coverage,int Maintenance,bool Pending)[] All {get {lock(sync)return calls.ToArray();}}
        public (ConnectionHistoryRecord[] Records,ConnectionCoverage[] Coverage,int Maintenance,bool Pending)[] WithRecords=>All.Where(c=>c.Records.Length>0).ToArray();
        public async Task WaitFor(int n)
        {
            var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(15);
            while(WithRecords.Length<n)
            {
                if(DateTime.UtcNow>deadline)Assert.Fail($"Expected {n} appends with records before the timeout; saw {WithRecords.Length}.");
                await Task.Delay(5);
            }
        }
    }
    private static (ConnectionService Service,string Root) ClearService(ClearAppends appends,TestClock clock,ConnectionServiceOptions? options=null)
    {
        string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-clear-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
        var service=new ConnectionService(System.IO.Path.Combine(root,"connections.db"),new Resolver(_=>null),clock.Read,appends.Wrap,options??new(FlushInterval:TimeSpan.FromMinutes(10)));
        appends.Pending=()=>service.PendingClear is not null;
        return (service,root);
    }
    private static async Task UntilClear(ConnectionService service,HistoryClearPhase phase)
    {
        var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(15);
        while(service.ClearState.Phase!=phase)
        {
            if(DateTime.UtcNow>deadline)Assert.Fail($"Expected the clear to reach {phase}; it is {service.ClearState}.");
            await Task.Delay(5);
        }
    }
    private static async Task Dispose(ConnectionService service,string root)
    {
        await service.StopAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);
    }
    private static long Count(string file,string sql)
    {
        using var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());
        db.Open();using var command=db.CreateCommand();command.CommandText=sql;return Convert.ToInt64(command.ExecuteScalar());
    }
    [Fact] public async Task Clear_retires_held_in_scope_units_whole()
    {
        var clock=new TestClock();clock.Set(100);var appends=new ClearAppends();var observed=new ConcurrentQueue<int>();
        var (service,root)=ClearService(appends,clock,new(FlushInterval:TimeSpan.FromMilliseconds(20)));
        service.maintenanceObserved=observed.Enqueue;
        long droppedAtFirstStep=-1;
        service.ClearStepOverride=_=>{Interlocked.CompareExchange(ref droppedAtFirstStep,service.DroppedObservations,-1);return new(1,1,true);};
        service.Start();
        try
        {
            // Minute 1: a.exe in "segment"; then the clock steps back to + 50 s and b.exe arrives in "other" (R2 freezes the a.exe unit 48 s ahead).
            service.TryRecord(Apps(100,"a.exe"));await Until(()=>service.PendingPublications==0);
            clock.Set(50);service.TryRecord(Apps(101,"b.exe") with {Segment="other"});await Until(()=>service.PendingPublications==0);
            // R2 freezes the b.exe-only unit 49 s ahead. Both are held by the clock check.
            service.TryRecord(Apps(50) with {Sequence=200,Segment="new"});await Until(()=>service.PendingPublications==0);
            await Task.Delay(200);Assert.Empty(appends.WithRecords);Assert.Equal(0,service.DroppedObservations);
            var clear=HistoryClear.Create("A.EXE",allTime:true,Epoch.AddSeconds(100));Assert.Equal(Epoch.AddMinutes(2),clear.Before);
            service.Clear(clear);await UntilClear(service,HistoryClearPhase.Done);
            // The a.exe unit (one observation) was retired whole before the first step.
            Assert.Equal(1,Interlocked.Read(ref droppedAtFirstStep));Assert.Equal(1,service.DroppedObservations);
            // The clock passes the b.exe unit: a tick sends it unchanged.
            clock.Set(101);
            await UntilAsync(()=>Task.FromResult(appends.WithRecords.Any(c=>c.Records.Any(r=>r.Delta.Peer.Process.Name=="b.exe"))));
            var b=Assert.Single(appends.WithRecords.SelectMany(c=>c.Records),r=>r.Delta.Peer.Process.Name=="b.exe");
            Assert.Equal((ConnectionMinuteFolder.BatchId("other",Epoch.AddMinutes(1),0,1),Epoch.AddMinutes(1),10L,Epoch.AddSeconds(101)),(b.BatchId,b.Delta.Minute,b.Delta.ReceivedBytes,b.Delta.LastSeen));
            // An open c.exe unit for the final write.
            service.TryRecord(Apps(101,"c.exe") with {Sequence=300,Segment="new"});await Until(()=>service.PendingPublications==0);
            await service.StopAsync();
            var calls=appends.All;int last=Array.FindIndex(calls,c=>c.Records.Any(r=>r.Delta.Peer.Process.Name=="c.exe"));
            Assert.True(last>=0);
            Assert.All(calls[last..],c=>Assert.Equal(0,c.Maintenance));Assert.All(calls[..last],c=>Assert.Equal(64,c.Maintenance));
            Assert.Equal(64,observed.First());Assert.Equal(0,observed.Last());
            Assert.DoesNotContain(calls.SelectMany(c=>c.Records),r=>r.Delta.Peer.Process.Name=="a.exe");
            Assert.Equal(1,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
        }
        finally {await Dispose(service,root);}
    }
    [Fact] public async Task Connection_clear_flushes_first_and_drops_the_rest_of_the_minute()
    {
        var query=new ConnectionHistoryQuery(Epoch,Epoch.AddMinutes(3));
        // All time, one app: the frozen minute 0 (its R1 write failed) is appended after the marker and before the delete;
        // a.exe's open minute 1 is forgotten and the rest of it dropped; minute 2 (at Before) is saved.
        {
            var clock=new TestClock();var appends=new ClearAppends();int failures=0;var observed=new ConcurrentQueue<int>();
            appends.Fail=()=>Interlocked.Increment(ref failures)==1;
            var (service,root)=ClearService(appends,clock);service.maintenanceObserved=observed.Enqueue;service.Start();
            try
            {
                RecordApps(service,clock,1,63,"a.exe","b.exe");await appends.WaitFor(1);await Until(()=>service.PendingPublications==0);
                var clear=HistoryClear.Create("a.exe",allTime:true,Epoch.AddSeconds(63));Assert.Equal(Epoch.AddMinutes(2),clear.Before);
                service.Clear(clear);await UntilClear(service,HistoryClearPhase.Done);
                var flushed=appends.WithRecords[1];
                Assert.True(flushed.Pending);Assert.Contains(flushed.Records,r=>r.Delta.Peer.Process.Name=="a.exe"&&r.Delta.Minute==Epoch);
                Assert.Null(service.PendingClear);Assert.False(service.ClearState.Resumed);
                RecordApps(service,clock,64,125,"a.exe","b.exe");await service.StopAsync();
                var appended=appends.All.SelectMany(c=>c.Records).ToArray();
                Assert.DoesNotContain(appended,r=>r.Delta.Peer.Process.Name=="a.exe"&&r.Delta.Minute==Epoch.AddMinutes(1));
                Assert.Equal(60L,Assert.Single(appended,r=>r.Delta.Peer.Process.Name=="a.exe"&&r.Delta.Minute==Epoch.AddMinutes(2)).Delta.ReceivedBytes);
                var rows=(await service.ReadViewAsync(query)).Rows;
                Assert.Equal([(Epoch.AddMinutes(2),60L)],rows.Where(r=>r.Process.Name=="a.exe").Select(r=>(r.Minute,r.ReceivedBytes)));
                Assert.Equal([(Epoch,590L),(Epoch.AddMinutes(1),600L),(Epoch.AddMinutes(2),60L)],rows.Where(r=>r.Process.Name=="b.exe").Select(r=>(r.Minute,r.ReceivedBytes)).OrderBy(r=>r.Minute));
                // InitializeAsync and the write attempts pass 64; the final write (minute 2) passes 0.
                var calls=appends.All;int final=Array.FindIndex(calls,c=>c.Records.Any(r=>r.Delta.Minute==Epoch.AddMinutes(2)));
                Assert.True(final>=0);
                Assert.All(calls[..final],c=>Assert.Equal(64,c.Maintenance));Assert.All(calls[final..],c=>Assert.Equal(0,c.Maintenance));
                Assert.Equal(64,observed.First());Assert.Equal(0,observed.Last());
                Assert.Equal(0,service.DroppedObservations);Assert.Equal(0,service.BufferedDeltas);
            }
            finally {await Dispose(service,root);}
        }
        // Older than 7 days: the open units are left alone.
        {
            var clock=new TestClock();var appends=new ClearAppends();var (service,root)=ClearService(appends,clock);service.Start();
            try
            {
                RecordApps(service,clock,1,30,"a.exe");await Until(()=>service.PendingPublications==0&&service.BufferedDeltas==2);
                service.Clear(HistoryClear.Create("a.exe",allTime:false,Epoch.AddSeconds(30)));await UntilClear(service,HistoryClearPhase.Done);
                Assert.Equal(2,service.BufferedDeltas);Assert.Empty(appends.WithRecords);
                await service.StopAsync();
                var record=Assert.Single(appends.All.SelectMany(c=>c.Records));Assert.Equal(("a.exe",300L),(record.Delta.Peer.Process.Name,record.Delta.ReceivedBytes));
            }
            finally {await Dispose(service,root);}
        }
        // The clear's write throws: Failed with the marker saved; Try again resumes without repeating Forget.
        {
            var clock=new TestClock();var appends=new ClearAppends();int failing=1;appends.Fail=()=>Volatile.Read(ref failing)==1;
            var (service,root)=ClearService(appends,clock);service.Start();
            try
            {
                RecordApps(service,clock,1,63,"a.exe","b.exe");await appends.WaitFor(1);await Until(()=>service.PendingPublications==0);
                service.Clear(HistoryClear.Create("A.EXE",allTime:true,Epoch.AddSeconds(63)));await UntilClear(service,HistoryClearPhase.Failed);
                var state=service.ClearState;
                Assert.Equal(("IOException",true),(state.Error,state.MarkerSaved));Assert.NotNull(service.PendingClear);Assert.True(service.CanRetryClear);
                Assert.EndsWith(" · Clearing history failed (IOException); it resumes when DeadWare next starts.",service.StorageStatus);
                // a.exe at Before and after, merged after the first run.
                RecordApps(service,clock,120,121,"a.exe","b.exe");await Until(()=>service.PendingPublications==0);
                Volatile.Write(ref failing,0);service.RetryClear();await UntilClear(service,HistoryClearPhase.Done);
                Assert.Null(service.PendingClear);Assert.False(service.CanRetryClear);
                await service.StopAsync();
                Assert.Equal(20L,Assert.Single(appends.All.SelectMany(c=>c.Records),r=>r.Delta.Peer.Process.Name=="a.exe"&&r.Delta.Minute==Epoch.AddMinutes(2)).Delta.ReceivedBytes);
                var rows=(await service.ReadViewAsync(query)).Rows;
                Assert.Equal([(Epoch.AddMinutes(2),20L)],rows.Where(r=>r.Process.Name=="a.exe").Select(r=>(r.Minute,r.ReceivedBytes)));
                Assert.Contains(rows,r=>r.Process.Name=="b.exe"&&r.Minute==Epoch);
            }
            finally {await Dispose(service,root);}
        }
        // A marker that cannot be saved (the metadata table padded to the 65,536-byte cap after InitializeAsync) clears nothing.
        {
            string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-clear-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(root);
            string file=System.IO.Path.Combine(root,"connections.db");
            ConnectionHistoryRecord Row(string name)=>new("b-"+name,"segment",new(Epoch,Peer with {Process=new(30,1,name)},10,0,Epoch.AddSeconds(1),Epoch.AddSeconds(2)),null,null,true);
            var seed=new ConnectionHistoryStore(file,65_536);seed.Initialize();seed.Append([Row("a.exe"),Row("b.exe")],[],Epoch.AddMinutes(3));
            var clock=new TestClock();clock.Set(180);
            var service=new ConnectionService(file,new Resolver(_=>null),clock.Read,append=>append,new(FlushInterval:TimeSpan.FromMinutes(10)),65_536);
            try
            {
                Assert.Equal(2,(await service.ReadViewAsync(query)).Rows.Count);
                using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString()))
                {
                    db.Open();using var command=db.CreateCommand();
                    command.CommandText="PRAGMA page_size";long pageSize=(long)command.ExecuteScalar()!;
                    command.CommandText=$"PRAGMA max_page_count={65_536/pageSize}";command.ExecuteNonQuery();
                    int pad=0;
                    try {for(;;pad++){command.CommandText=$"INSERT INTO metadata VALUES('pad-{pad:D5}',0)";command.ExecuteNonQuery();}}
                    catch(Microsoft.Data.Sqlite.SqliteException error) when(error.SqliteErrorCode==13) { }
                    Assert.True(pad>0);
                }
                long traffic=Count(file,"SELECT count(*) FROM traffic");Assert.Equal(2,traffic);
                service.Start();service.Clear(HistoryClear.Create("A.EXE",allTime:true,Epoch.AddSeconds(180)));
                await UntilClear(service,HistoryClearPhase.Failed);
                var state=service.ClearState;
                Assert.Equal(("SqliteException",false),(state.Error,state.MarkerSaved));
                Assert.Null(service.PendingClear);Assert.True(service.CanRetryClear);
                Assert.EndsWith(" · Clearing history failed (SqliteException); not cleared.",service.StorageStatus);
                Assert.Equal(traffic,Count(file,"SELECT count(*) FROM traffic"));
                Assert.Equal(0,Count(file,"SELECT count(*) FROM metadata WHERE substr(key,1,6)='clear-'"));
            }
            finally {await Dispose(service,root);}
        }
    }
}
