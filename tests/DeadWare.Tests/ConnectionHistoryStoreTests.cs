using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DeadWare.Tests;

public sealed class ConnectionHistoryStoreTests(ITestOutputHelper testOutput) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DeadWare-connections-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "connections.db");
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionLocation Place = new("US", "United States", "Illinois", "Chicago", new(41,-87), "fixture");
    private ConnectionHistoryStore Store(long cap = 268435456, int pages = 16384, string? file = null) { var store = new ConnectionHistoryStore(file ?? Database, cap, maximumGroupedTempPages: pages); store.Initialize(); return store; }
    private static ConnectionHistoryRecord Row(int minute = 0, string batch = "b", long received = 10, long sent = 20, long generation = 1, string name = "browser", string address = "8.8.8.8") =>
        new(batch, "segment", new(Epoch.AddMinutes(minute), new(new(7,generation,name), "192.168.1.234", 49123, address, 443, ConnectionProtocol.Tcp), received, sent, Epoch.AddMinutes(minute).AddSeconds(1), Epoch.AddMinutes(minute).AddSeconds(2)), Place, new(40,-80), true);
    private static ConnectionHistoryQuery Query(int minutes = 10) => new(Epoch, Epoch.AddMinutes(minutes));
    private SqliteConnection Connect(string? file = null) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=file ?? Database, Pooling=false, DefaultTimeout=1 }.ToString()); db.Open(); return db; }
    private static void Sql(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText=sql; cmd.ExecuteNonQuery(); }
    public void Dispose() { if(Directory.Exists(directory)) Directory.Delete(directory,true); }

    [Fact] public void Restart_roundtrip_retains_minute_generation_location_and_origin()
    {
        var store = Store();
        store.Append([Row(), Row(1,"c",generation:2) with { Location=Place with { City="Elsewhere" }, Origin=new(20,30) }], [], Epoch.AddMinutes(2));
        var view = Store().Read(Query());
        Assert.Equal(2,view.TotalRows); Assert.Equal(2,view.Rows.Select(x=>x.Process.Generation).Distinct().Count());
        Assert.Contains(view.Rows,x=>x.Minute==Epoch.AddMinutes(1)&&x.Location!.City=="Elsewhere"&&x.Origin==new GeoPoint(20,30));
        Assert.Equal(20,view.Summary.ReceivedBytes); Assert.True(view.StorageBytes>0);
    }
    [Fact] public void Replayed_batch_does_not_add_bytes_and_new_batches_aggregate()
    {
        var store=Store(); store.Append([Row()],[],Epoch.AddMinutes(1)); store.Append([Row()],[],Epoch.AddMinutes(1));
        store.Append([Row(batch:"c")],[],Epoch.AddMinutes(1));
        Assert.Equal(20,Assert.Single(store.Read(Query()).Rows).ReceivedBytes);
    }
    [Fact] public void Coverage_distinguishes_zero_traffic_incomplete_and_missing_intervals()
    {
        var store=Store(); store.Append([], [new("a","s",Epoch,Epoch.AddSeconds(15),true), new("b","s",Epoch.AddSeconds(15),Epoch.AddSeconds(30),false)],Epoch.AddMinutes(1));
        var view=store.Read(new(Epoch,Epoch.AddMinutes(1)));
        Assert.Empty(view.Rows); Assert.Equal(2,view.Coverage.Count); Assert.False(view.Coverage[1].Complete);
        Assert.Equal(new ConnectionHistoryGap(Epoch.AddSeconds(30),Epoch.AddMinutes(1)),Assert.Single(view.Gaps));
    }
    [Fact] public void Full_range_summary_is_independent_of_page_and_filters_are_half_open()
    {
        var store=Store(); var rows=Enumerable.Range(0,301).Select(i=>Row(i,"b"+i,address:"8.8."+(i/250)+"."+(i%250+1))).ToArray();
        store.Append(rows,[],Epoch.AddMinutes(302));
        var view=store.Read(Query(301)); Assert.Equal(200,view.Rows.Count); Assert.Equal(301,view.TotalRows); Assert.Equal(3010,view.Summary.ReceivedBytes);
        Assert.Single(store.Read(new(Epoch,Epoch.AddMinutes(1),"BROWSER","us",ConnectionDirection.Received)).Rows);
        Assert.Empty(store.Read(Query() with {CountryCode="DE"}).Rows);
        Assert.Equal(300,store.Read(new(Epoch.AddMinutes(1),Epoch.AddMinutes(301), Limit:500)).TotalRows);
    }
    [Fact] public void Csv_neutralizes_formulas_quotes_control_characters_and_omits_local_endpoint()
    {
        var store=Store(); store.Append([Row(name:" =cmd,\"x\"\n")],[],Epoch.AddMinutes(1));
        using var output=new StringWriter(); store.ExportCsv(output,Query() with {Limit=1}); var csv=output.ToString();
        Assert.Contains("' =cmd,\"\"x\"\" ",csv); Assert.DoesNotContain("192.168.1.234",csv); Assert.DoesNotContain("49123",csv);
        using var db=Connect(); using var command=db.CreateCommand(); command.CommandText="SELECT sql FROM sqlite_master WHERE type='table'";
        using var reader=command.ExecuteReader(); while(reader.Read()) { Assert.DoesNotContain("local_address",reader.GetString(0)); Assert.DoesNotContain("local_port",reader.GetString(0)); }
    }
    [Fact] public void Retention_prunes_records_coverage_and_replay_keys_after_thirty_days()
    {
        var store=Store(); store.Append([Row()], [new("b","s",Epoch,Epoch.AddSeconds(15),true)],Epoch.AddMinutes(1));
        store.Append([Row(43201,"fresh")],[],Epoch.AddDays(30).AddMinutes(2));
        var view=store.Read(new(Epoch,Epoch.AddDays(31))); Assert.Single(view.Rows); Assert.Empty(view.Coverage); Assert.Equal(Epoch.AddMinutes(43201),view.EarliestRetainedTime);
    }
    [Fact] public void Cap_prunes_oldest_whole_minutes_and_preserves_recent_data()
    {
        var store=Store(65536);
        for(int i=0;i<160;i++) store.Append([Row(i,"b"+i,name:new string('n',250))],[],Epoch.AddMinutes(161));
        var view=store.Read(Query(161) with {Limit=500}); Assert.InRange(view.TotalRows,1,159); Assert.True(view.EarliestRetainedTime>Epoch); Assert.Contains(view.Rows,x=>x.Minute==Epoch.AddMinutes(159));
        Assert.True(new FileInfo(Database).Length<=65536); Assert.True(store.StorageBytes<=65536+8*1024*1024+32768);
        store.Append([Row(0,"b0",name:new string('n',250))],[],Epoch.AddMinutes(161));
        var replay=store.Read(Query(161) with {Limit=500}); Assert.Equal(view.TotalRows,replay.TotalRows); Assert.Equal(view.Summary,replay.Summary); Assert.Equal(view.EarliestRetainedTime,replay.EarliestRetainedTime);
    }
    [Fact] public void Invalid_batch_is_atomic_and_invalid_intervals_are_rejected()
    {
        var store=Store(); store.Append([Row()],[],Epoch.AddMinutes(1));
        Assert.Throws<ArgumentException>(()=>store.Append([Row(batch:"c"),Row(batch:"d",received:-1)],[],Epoch.AddMinutes(2)));
        Assert.Equal(10,store.Read(Query()).Summary.ReceivedBytes);
        Assert.Throws<ArgumentException>(()=>store.Read(new(Epoch,Epoch)));
        Assert.Throws<ArgumentException>(()=>store.Append(Enumerable.Repeat(Row(),32769).ToArray(),[],Epoch));
    }
    [Fact] public void Future_skew_is_saved_without_becoming_coverage()
    {
        var store=Store(); var row=Row(1); store.Append([row],[],Epoch.AddSeconds(60));
        Assert.Single(store.Read(Query()).Rows); Assert.Empty(store.Read(Query()).Coverage); Assert.Single(store.Read(Query()).Gaps);
    }
    [Theory] [InlineData("corrupt")] [InlineData("newer")] [InlineData("foreign")] public void Unsupported_files_are_left_byte_identical(string kind)
    {
        Directory.CreateDirectory(directory);
        if(kind=="corrupt") File.WriteAllText(Database,"not a sqlite database");
        else { using var db=Connect(); Sql(db,kind=="newer"?"PRAGMA user_version=999":"CREATE TABLE unrelated(x TEXT)"); }
        byte[] before=File.ReadAllBytes(Database); Assert.ThrowsAny<Exception>(()=>Store()); Assert.Equal(before,File.ReadAllBytes(Database));
    }
    [Fact] public void Locked_append_preserves_existing_rows_and_retry_succeeds_once()
    {
        var store=Store(); store.Append([Row()],[],Epoch.AddMinutes(1));
        using(var db=Connect()) { Sql(db,"BEGIN IMMEDIATE"); Assert.Throws<SqliteException>(()=>store.Append([Row(1,"c")],[],Epoch.AddMinutes(2))); Sql(db,"ROLLBACK"); }
        Assert.Single(store.Read(Query()).Rows); store.Append([Row(1,"c")],[],Epoch.AddMinutes(2)); Assert.Equal(2,store.Read(Query()).TotalRows);
    }
    [Fact] public void Multiple_local_endpoints_sum_before_privacy_projection_and_reordered_replay_is_exact()
    {
        var store=Store(); var first=Row(); var second=Row(received:30,sent:40);
        second=second with {Delta=second.Delta with {Peer=second.Delta.Peer with {LocalAddress="10.20.30.40",LocalPort=55555}}};
        store.Append([first,second],[],Epoch.AddMinutes(1)); store.Append([second,first],[],Epoch.AddMinutes(1));
        var row=Assert.Single(store.Read(Query()).Rows); Assert.Equal(40,row.ReceivedBytes); Assert.Equal(60,row.SentBytes);
        using var writer=new StringWriter(); store.ExportCsv(writer,Query()); Assert.DoesNotContain("10.20.30.40",writer.ToString()); Assert.DoesNotContain("55555",writer.ToString());
    }
    [Fact] public void Changed_batch_replay_is_rejected_before_any_new_batch_is_written()
    {
        var store=Store(); store.Append([Row()],[],Epoch.AddMinutes(1));
        Assert.Throws<ArgumentException>(()=>store.Append([Row(1,"new"),Row(received:999)],[],Epoch.AddMinutes(2)));
        Assert.Equal(10,store.Read(Query()).Summary.ReceivedBytes);
        Assert.Throws<ArgumentException>(()=>store.Append([Row() with {Location=Place with {City="Changed"}}],[],Epoch.AddMinutes(2)));
    }
    [Fact] public void Coverage_crossing_a_minute_remains_explicit_and_replay_changes_are_rejected()
    {
        var store=Store(); ConnectionCoverage c=new("b","s",Epoch.AddSeconds(55),Epoch.AddSeconds(70),true);
        store.Append([], [c],Epoch.AddMinutes(2)); store.Append([], [c],Epoch.AddMinutes(2));
        Assert.Equal(new ConnectionHistoryInterval(c.From,c.To,true),Assert.Single(store.Read(Query()).Coverage));
        Assert.Throws<ArgumentException>(()=>store.Append([], [c with {Complete=false}],Epoch.AddMinutes(2)));
    }
    [Fact] public void Saturating_totals_remain_exact_above_double_precision_and_query_rates_use_minute_buckets()
    {
        var store=Store(); store.Append([Row(received:9007199254740993),Row(batch:"c",received:1)],[],Epoch.AddMinutes(1));
        var view=store.Read(new(Epoch,Epoch.AddMinutes(1))); Assert.Equal(9007199254740994,view.Summary.ReceivedBytes);
        store.Append([Row(batch:"d",received:long.MaxValue)],[],Epoch.AddMinutes(1)); view=store.Read(new(Epoch,Epoch.AddMinutes(1)));
        Assert.Equal(long.MaxValue,view.Summary.ReceivedBytes); Assert.Equal(long.MaxValue,Assert.Single(view.Rows).ReceivedBytes);
        Assert.Equal(long.MaxValue/60d,view.Rows[0].DownloadBytesPerSecond);
    }
    [Fact] public void Saved_minute_rates_and_csv_do_not_change_with_enclosing_query_duration()
    {
        var store=Store();var record=Row(received:61440,sent:30720) with {Complete=false};
        store.Append([record with {Delta=record.Delta with {LastSeen=record.Delta.FirstSeen}}],[],Epoch.AddMinutes(1));
        foreach(int minutes in new[]{1,1440,43200})
        {
            var query=Query(minutes);var row=Assert.Single(store.Read(query).Rows);
            Assert.Equal(1024,row.DownloadBytesPerSecond);Assert.Equal(512,row.UploadBytesPerSecond);Assert.False(row.Complete);
            using var output=new StringWriter();store.ExportCsv(output,query);
            var cells=output.ToString().Split(Environment.NewLine)[1].Split(',');Assert.Equal("\"1024\"",cells[11]);Assert.Equal("\"512\"",cells[12]);
        }
        // Selection includes a bucket iff its minute start is inside the half-open query.
        Assert.Empty(store.Read(new(Epoch.AddSeconds(1),Epoch.AddMinutes(1))).Rows);
        Assert.Equal(1024,Assert.Single(store.Read(new(Epoch,Epoch.AddSeconds(1))).Rows).DownloadBytesPerSecond);
    }
    [Fact] public void History_pid_search_matches_existing_schema_v1_rows_without_rewriting_them()
    {
        var store=Store();store.Append([Row()],[],Epoch.AddMinutes(1));
        using(var db=Connect())Sql(db,"UPDATE traffic SET pid=7654321");
        Assert.Single(store.Read(Query() with {Search="7654321"}).Rows);
        using var output=new StringWriter();store.ExportCsv(output,Query() with {Search="7654321"});Assert.Contains("7654321",output.ToString());
        Assert.Empty(store.Read(Query() with {Search="7654322"}).Rows);
    }
    [Fact] public void Marker_representatives_preserve_full_saved_group_identity_and_absolute_page_rank()
    {
        var store=Store();var records=Enumerable.Range(0,202).Select(i=>Row(i,"b"+i) with {Location=Place with {City=i==0?"Old city":"New city",Source=i==0?"Old database":"New database"},Origin=i==0?new(1,2):new(3,4)}).ToArray();
        store.Append(records,[],Epoch.AddMinutes(203));var query=Query(203);var view=store.Read(query);Assert.Equal(2,view.Markers.Count);
        var old=Assert.Single(view.Markers,m=>m.Location.City=="Old city");Assert.Equal(201,old.RowIndex);
        foreach(var marker in view.Markers)
        {
            var row=Assert.Single(store.Read(query with {Offset=(int)marker.RowIndex,Limit=1}).Rows);
            Assert.Equal(row,marker.Representative);Assert.Equal(marker.Location,row.Location);Assert.Equal(marker.Origin,row.Origin);
        }
    }
    [Fact] public void Csv_exports_entire_filter_even_with_page_offset_and_escapes_all_formula_prefixes()
    {
        var store=Store(); var names=new[]{"=formula","+formula","-formula","@formula","\t=hidden","safe,\"quoted\""};
        store.Append(names.Select((name,i)=>Row(i,"b"+i,name:name)).ToArray(),[],Epoch.AddMinutes(10));
        using var output=new StringWriter(); store.ExportCsv(output,Query() with {Limit=1,Offset=3}); string csv=output.ToString();
        Assert.Equal(7,csv.Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries).Length);
        foreach(string prefix in new[]{"=","+","-","@"}) Assert.Contains("\"'"+prefix+"formula\"",csv);
        Assert.Contains("\"' =hidden\"",csv); Assert.Contains("\"safe,\"\"quoted\"\"\"",csv); Assert.DoesNotContain('\t',csv);
    }
    [Fact] public void Csv_keeps_negative_coordinates_and_rates_numeric()
    {
        // Chicago's longitude and the origin's are negative; the formula guard must leave numbers alone.
        var store=Store(); store.Append([Row(name:"-formula")],[],Epoch.AddMinutes(1));
        using var output=new StringWriter(); store.ExportCsv(output,Query()); string csv=output.ToString();
        string row=csv.Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains("\"'-formula\"",row); Assert.Contains("\"-87\"",row); Assert.Contains("\"-80\"",row); Assert.DoesNotContain("\"'-8",row);
    }
    [Fact] public void Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country()
    {
        var store=Store(); var rows=Enumerable.Range(0,300).Select(i=>Row(i,"b"+i,address:"8.9."+(i/250)+"."+(i%250+1)) with {Location=Place with {City="City "+i,Point=new(i%90,i%180)}}).ToList();
        rows.Add(Row(300,"local",address:"192.168.3.5") with {Location=null}); rows.Add(Row(301,"unknown",address:"9.9.9.9") with {Location=null});
        store.Append(rows,[],Epoch.AddMinutes(302)); var view=store.Read(Query(302));
        Assert.Equal(256,view.Markers.Count); Assert.Equal(302,view.Summary.Destinations); Assert.Equal(1,view.Summary.Countries); Assert.Equal(1,view.Summary.Unlocated); Assert.Equal(1,view.Summary.Local); Assert.Equal(3020,view.Summary.ReceivedBytes);
        Assert.Equal(44,view.OmittedMarkers);
    }
    [Fact] public void Additional_schema_objects_are_rejected_without_modifying_existing_database()
    {
        _=Store(); using(var db=Connect()) Sql(db,"CREATE TRIGGER surprise AFTER INSERT ON traffic BEGIN DELETE FROM traffic; END");
        byte[] before=File.ReadAllBytes(Database); Assert.Throws<InvalidDataException>(()=>Store()); Assert.Equal(before,File.ReadAllBytes(Database));
    }
    [Fact] public void Query_normalizes_utc_clamps_page_limits_and_direction_filters_traffic_rows()
    {
        var store=Store(); store.Append([Row(received:0),Row(1,"received",sent:0)],[],Epoch.AddMinutes(3));
        var view=store.Read(new(Epoch.ToOffset(TimeSpan.FromHours(-5)),Epoch.AddMinutes(3).ToOffset(TimeSpan.FromHours(-5)),Direction:ConnectionDirection.Sent,Offset:-1,Limit:900));
        Assert.Equal(TimeSpan.Zero,view.Query.From.Offset); Assert.Equal(500,view.Query.Limit); Assert.Equal(0,view.Query.Offset); Assert.Equal(20,Assert.Single(view.Rows).SentBytes);
        Assert.Single(store.Read(Query() with {Direction=ConnectionDirection.Received,Limit=0}).Rows);
    }
    [Fact] public void Later_chunk_write_failure_preserves_committed_rows_and_retry_never_doubles_them_or_claims_coverage()
    {
        var store=Store(); store.Append([Row(-1,"old")],[],Epoch);
        var rows=Enumerable.Range(0,300).Select(i=>Row(i,"drain")).ToArray(); rows[270]=rows[270] with {Delta=rows[270].Delta with {Peer=rows[270].Delta.Peer with {RemoteAddress="9.9.9.9"}}};
        ConnectionCoverage coverage=new("drain","s",Epoch,Epoch.AddSeconds(15),true);
        // A real SQLite statement failure after the first committed chunk, without a production fault hook.
        using(var db=Connect()) Sql(db,"CREATE TRIGGER fail_later BEFORE INSERT ON traffic WHEN NEW.remote='9.9.9.9' BEGIN SELECT RAISE(ABORT,'test disk write failure'); END");
        Assert.Throws<SqliteException>(()=>store.Append(rows,[coverage],Epoch.AddMinutes(302)));
        var query=new ConnectionHistoryQuery(Epoch.AddMinutes(-1),Epoch.AddMinutes(302),Limit:500); var partial=store.Read(query);
        Assert.Equal(257,partial.TotalRows); Assert.Equal(2570,partial.Summary.ReceivedBytes); Assert.Empty(partial.Coverage); Assert.Single(partial.Gaps);
        using(var db=Connect()) Sql(db,"DROP TRIGGER fail_later");
        store.Append(rows,[coverage],Epoch.AddMinutes(302)); var complete=Store().Read(query);
        Assert.Equal(301,complete.TotalRows); Assert.Equal(3010,complete.Summary.ReceivedBytes); Assert.Single(complete.Coverage);
    }
    [Fact] public void Impossible_capacity_write_reports_failure_and_exposes_intentional_old_retention_eviction()
    {
        var store=Store(65536); store.Append([Row(-1,"old")],[],Epoch);
        var large=Enumerable.Range(0,256).Select(i=>Row(0,"large",address:"8.9.1."+(i%250+1),generation:i+10,name:new string('x',260)) with {Location=Place with {City=new string('c',160),Region=new string('r',160),Country=new string('n',160),Source=new string('s',128)}}).ToArray();
        Assert.ThrowsAny<IOException>(()=>store.Append(large,[],Epoch.AddMinutes(1)));
        var view=store.Read(new(Epoch.AddMinutes(-1),Epoch.AddMinutes(1))); Assert.Equal(0,view.Summary.ReceivedBytes); Assert.Contains("Capacity shortened",view.Status); Assert.Empty(view.Coverage);
    }
    [Fact] public void Pinned_reader_bounds_journal_and_failed_drain_replays_without_duplicate_traffic_or_false_coverage()
    {
        var store=Store(); store.Append([Row(-1,"old")],[],Epoch);
        var rows=Enumerable.Range(0,8192).Select(i=>Row(i/300,"large",address:"8."+(i/60000)+"."+(i/250%240)+"."+(i%250+1),generation:i+10,name:new string('x',260)) with {Location=Place with {City=new string('c',160),Region=new string('r',160),Country=new string('n',160),Source=new string('s',128)}}).ToArray();
        ConnectionCoverage coverage=new("large","s",Epoch,Epoch.AddSeconds(15),true);
        using(var pinned=Connect())
        using(var tx=pinned.BeginTransaction(deferred:true))
        {
            using var command=pinned.CreateCommand(); command.Transaction=tx; command.CommandText="SELECT count(*) FROM traffic"; _=command.ExecuteScalar();
            Assert.Throws<IOException>(()=>store.Append(rows,[coverage],Epoch.AddMinutes(30)));
            var view=store.Read(new(Epoch.AddMinutes(-1),Epoch.AddMinutes(30)));
            Assert.InRange(view.TotalRows,257,8192); Assert.Empty(view.Coverage);
            Assert.True(store.StorageBytes<=268435456+10*1024*1024);
            Assert.InRange(new FileInfo(Database+"-wal").Length,8*1024*1024,10*1024*1024);
        }
        store.Append(rows,[coverage],Epoch.AddMinutes(30)); var complete=Store().Read(new(Epoch.AddMinutes(-1),Epoch.AddMinutes(30)));
        Assert.Equal(8193,complete.TotalRows); Assert.Equal(81930,complete.Summary.ReceivedBytes); Assert.Single(complete.Coverage);
        Assert.True(store.StorageBytes<268435456);
    }
    [Theory] [InlineData(262144)] [InlineData(524288)] [InlineData(1048576)]
    public void Capacity_cannot_evict_an_earlier_committed_chunk_of_the_incoming_minute(long cap)
    {
        var store=Store(cap);
        var rows=Enumerable.Range(0,1500).Select(i=>Row(0,"same-minute",generation:i+1,name:new string('x',100)) with {Location=null,Origin=null}).ToArray();
        ConnectionCoverage coverage=new("same-minute","s",Epoch,Epoch.AddSeconds(15),true);
        Assert.Throws<IOException>(()=>store.Append(rows,[coverage],Epoch.AddMinutes(1)));
        var partial=store.Read(Query() with {Limit=500}); Assert.InRange(partial.TotalRows,256,1499); Assert.Empty(partial.Coverage);
        Assert.Throws<IOException>(()=>store.Append(rows,[coverage],Epoch.AddMinutes(1)));
        var replay=store.Read(Query() with {Limit=500}); Assert.Equal(partial.TotalRows,replay.TotalRows); Assert.Equal(partial.Summary,replay.Summary); Assert.Empty(replay.Coverage);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Duplicate_coverage_is_order_independent_and_incomplete_dominates(bool reverse)
    {
        var store=Store(); ConnectionCoverage complete=new("coverage","s",Epoch,Epoch.AddSeconds(15),true);
        ConnectionCoverage[] intervals=[complete,complete with {Complete=false}]; if(reverse) Array.Reverse(intervals);
        store.Append([],intervals,Epoch.AddMinutes(1));
        Assert.False(Assert.Single(store.Read(Query()).Coverage).Complete);
        Array.Reverse(intervals); store.Append([],intervals,Epoch.AddMinutes(1)); Assert.False(Assert.Single(store.Read(Query()).Coverage).Complete);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Coverage_piece_collision_after_minute_split_is_conservatively_incomplete(bool reverse)
    {
        var store=Store(); ConnectionCoverage complete=new("coverage","s",Epoch.AddSeconds(55),Epoch.AddSeconds(70),true);
        ConnectionCoverage[] intervals=[complete,complete with {From=Epoch.AddMinutes(1),Complete=false}]; if(reverse) Array.Reverse(intervals);
        store.Append([],intervals,Epoch.AddMinutes(2)); var coverage=store.Read(Query()).Coverage;
        Assert.Equal(2,coverage.Count); Assert.Equal(new(Epoch.AddSeconds(55),Epoch.AddMinutes(1),true),coverage[0]);
        Assert.Equal(new(Epoch.AddMinutes(1),Epoch.AddSeconds(70),false),coverage[1]);
        Array.Reverse(intervals); store.Append([],intervals,Epoch.AddMinutes(2)); Assert.Equal(coverage,store.Read(Query()).Coverage);
    }
    [Fact] public void Near_capacity_expiry_with_pinned_reader_bounds_maintenance_wal_and_can_resume()
    {
        Assert.Equal(256L*1024*1024,ConnectionHistoryStore.MaximumDatabaseBytes);
        // Keep production WAL/transaction budgets; scale only fixture capacity and row count for CI.
        // Explicit manual stress mode exercises the original ~235 MB database on this same Fact.
        bool fullStress=Environment.GetEnvironmentVariable("DEADWARE_FULL_STORAGE_STRESS")=="1";
        long cap=fullStress?ConnectionHistoryStore.MaximumDatabaseBytes:64L*1024*1024;
        int rowCount=fullStress?32768:8192;
        var store=Store(cap); var place=Place with {Country=new string('n',160),Region=new string('r',160),City=new string('c',160),Source=new string('s',128)};
        var rows=Enumerable.Range(0,rowCount).Select(i=>Row(0,"expiry",generation:i+1,name:new string('x',260)) with {Location=place}).ToArray();
        for(int batch=0;batch<3;batch++) store.Append(rows.Select(x=>x with {BatchId="expiry"+batch}).ToArray(),[],Epoch.AddMinutes(1));
        store.Append([],[new("old-coverage","s",Epoch,Epoch.AddSeconds(15),true)],Epoch.AddMinutes(1));
        long seededBytes=new FileInfo(Database).Length;
        Assert.InRange(seededBytes,fullStress?200L*1024*1024:50L*1024*1024,cap);
        testOutput.WriteLine($"Near-capacity expiry: mode={(fullStress?"full":"CI")}, cap={cap}, rows={3*rowCount}, database={seededBytes} bytes.");
        using(var pinned=Connect())
        using(var tx=pinned.BeginTransaction(deferred:true))
        {
            using var command=pinned.CreateCommand(); command.Transaction=tx; command.CommandText="SELECT count(*) FROM traffic"; _=command.ExecuteScalar();
            Assert.Throws<IOException>(()=>store.Append([],[],Epoch.AddDays(31)));
            long journalBytes=new FileInfo(Database+"-wal").Length;
            Assert.InRange(journalBytes,8*1024*1024,10*1024*1024);
            testOutput.WriteLine($"Pinned-reader maintenance stopped at WAL={journalBytes} bytes.");
            var hidden=store.Read(Query()); Assert.Empty(hidden.Rows); Assert.Equal(0,hidden.Summary.ReceivedBytes); Assert.Empty(hidden.Markers); Assert.Empty(hidden.Coverage); Assert.Single(hidden.Gaps); Assert.Null(hidden.EarliestRetainedTime);
            using var output=new StringWriter(); store.ExportCsv(output,Query()); Assert.Single(output.ToString().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries));
            using var inspect=Connect(); using var physical=inspect.CreateCommand(); physical.CommandText="SELECT count(*) FROM traffic"; Assert.True((long)physical.ExecuteScalar()!>0);
        }
        store.Append(rows.Select(x=>x with {BatchId="expiry0"}).ToArray(),[],Epoch.AddDays(31)); Assert.Empty(Store(cap).Read(Query()).Rows);
        using var db=Connect(); using var check=db.CreateCommand(); check.CommandText="SELECT (SELECT count(*) FROM traffic)+(SELECT count(*) FROM coverage)+(SELECT count(*) FROM dedup)+(SELECT count(*) FROM manifests)";
        Assert.Equal(0L,check.ExecuteScalar());
    }
    [Fact] public void Logical_capacity_eviction_hides_every_result_path_before_physical_cleanup_finishes()
    {
        var store=Store(262144);
        var old=Enumerable.Range(0,200).Select(i=>Row(0,"old",generation:i+1,name:new string('x',100)) with {Location=null,Origin=null}).ToArray();
        store.Append(old,[new("old","s",Epoch,Epoch.AddSeconds(15),true)],Epoch.AddMinutes(1));
        var incoming=old.Select(x=>x with {BatchId="new",Delta=x.Delta with {Minute=Epoch.AddMinutes(1),FirstSeen=Epoch.AddSeconds(61),LastSeen=Epoch.AddSeconds(62)}}).ToArray();
        ConnectionCoverage coverage=new("new","s",Epoch.AddMinutes(1),Epoch.AddSeconds(75),true);
        using(var db=Connect()) Sql(db,"CREATE TRIGGER fail_cleanup BEFORE DELETE ON traffic WHEN OLD.generation=151 BEGIN SELECT RAISE(ABORT,'cleanup failure'); END");
        Assert.Throws<SqliteException>(()=>store.Append(incoming,[coverage],Epoch.AddMinutes(2)));
        var hidden=store.Read(Query()); Assert.Empty(hidden.Rows); Assert.Equal(0,hidden.TotalRows); Assert.Equal(0,hidden.Summary.ReceivedBytes); Assert.Empty(hidden.Markers); Assert.Empty(hidden.Coverage); Assert.Single(hidden.Gaps); Assert.Null(hidden.EarliestRetainedTime);
        using(var output=new StringWriter()) {store.ExportCsv(output,Query()); Assert.Single(output.ToString().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries));}
        using(var db=Connect()) {using var command=db.CreateCommand(); command.CommandText="SELECT count(*) FROM traffic"; Assert.True((long)command.ExecuteScalar()!>0); Sql(db,"DROP TRIGGER fail_cleanup");}
        store.Append(old,[],Epoch.AddMinutes(2)); Assert.Empty(store.Read(Query()).Rows);
        store.Append(incoming,[coverage],Epoch.AddMinutes(2)); var complete=Store().Read(Query()); Assert.Equal(200,complete.TotalRows); Assert.Equal(Epoch.AddMinutes(1),complete.EarliestRetainedTime); Assert.Single(complete.Coverage);
    }

    private static ConnectionHistoryRecord Peer(string batch, DateTimeOffset minute, string address, ConnectionLocation? location, long received = 10, long sent = 20,
        int pid = 7, string name = "browser", GeoPoint? origin = null, int firstSecond = 1, int lastSecond = 2, ConnectionProtocol protocol = ConnectionProtocol.Tcp) =>
        new(batch, "s", new(minute, new(new(pid,1,name), "192.168.1.234", 49123, address, 443, protocol), received, sent, minute.AddSeconds(firstSecond), minute.AddSeconds(lastSecond)), location, origin ?? new(40,-80), true);
    private long Traffic(string file) { using var db=Connect(file); using var command=db.CreateCommand(); command.CommandText="SELECT count(*) FROM traffic"; return (long)command.ExecuteScalar()!; }
    private static string Csv(ConnectionHistoryStore store, ConnectionHistoryQuery query) { using var output=new StringWriter(); store.ExportCsv(output,query); return output.ToString(); }

    [Fact] public void Per_minute_rows_read_the_same_as_per_poll_rows()
    {
        (string Address, ConnectionLocation? Location)[] pairs=[("8.8.8.8",Place),("192.168.1.5",null),("9.9.9.9",null)];
        string fileA=Path.Combine(directory,"per-poll.db"), fileB=Path.Combine(directory,"per-minute.db");
        var a=Store(file:fileA); var b=Store(file:fileB);
        a.Append([..Enumerable.Range(0,60).SelectMany(i=>pairs.Select(p=>Peer($"s:{i}:1",Epoch,p.Address,p.Location,10,10,firstSecond:i,lastSecond:i)))],
            [..Enumerable.Range(0,60).Select(i=>new ConnectionCoverage($"s:{i}:1","s",Epoch.AddSeconds(i),Epoch.AddSeconds(i+1),true))],Epoch.AddMinutes(2));
        string unit=ConnectionMinuteFolder.BatchId("s",Epoch,1,0);
        b.Append([..pairs.Select(p=>Peer(unit,Epoch,p.Address,p.Location,600,600,firstSecond:0,lastSecond:59))],[new(unit,"s",Epoch,Epoch.AddMinutes(1),true)],Epoch.AddMinutes(2));
        var viewA=a.Read(Query(2)); var viewB=b.Read(Query(2));
        Assert.Equal(3,viewA.TotalRows);
        Assert.Equal(viewA.Rows,viewB.Rows); Assert.Equal(viewA.TotalRows,viewB.TotalRows); Assert.Equal(viewA.Summary,viewB.Summary); Assert.Equal(viewA.Markers,viewB.Markers);
        Assert.Equal(viewA.Coverage,viewB.Coverage); Assert.Equal(viewA.Gaps,viewB.Gaps); Assert.Equal(viewA.EarliestRetainedTime,viewB.EarliestRetainedTime);
        Assert.Equal(Csv(a,Query(2)),Csv(b,Query(2)));
        Assert.Equal((180L,3L),(Traffic(fileA),Traffic(fileB)));
    }

    [Fact] public void Grouped_read_matches_the_three_scan_reference()
    {
        var store=Store(); var records=new List<ConnectionHistoryRecord>();
        // 300 located groups at minutes 3..302; expiry and floor below hide minutes 3..6, leaving 296 (more than 256 markers).
        for(int i=0;i<300;i++)
            records.Add(Peer($"g{i}",Epoch.AddMinutes(3+i),"8.9."+(i/250)+"."+(i%250+1),Place with {City="City "+i,Point=new(i%90,i%180)},
                i%4==0?0:10+i,i%3==0?0:20+i,pid:i==42?4242:7+i%3,name:"app"+(i%5)));
        // Per-poll rows: one pair in minute 310 over five batches, plus a per-minute unit sharing that pair in minute 311.
        for(int k=0;k<5;k++) records.Add(Peer($"p{k}",Epoch.AddMinutes(310),"8.8.8.8",Place,10,10,name:"app2",firstSecond:k,lastSecond:k));
        records.Add(Peer("q0",Epoch.AddMinutes(311),"8.8.8.8",Place,10,10,name:"app2",firstSecond:3,lastSecond:3));
        string unit=ConnectionMinuteFolder.BatchId("s",Epoch.AddMinutes(311),1,0);
        var noPoint=Place with {CountryCode="AU",Country="Australia",City="Sydney",Point=null};
        records.Add(Peer(unit,Epoch.AddMinutes(311),"8.8.8.8",Place,600,600,name:"app2",firstSecond:0,lastSecond:59));
        records.Add(Peer(unit,Epoch.AddMinutes(311),"192.168.3.5",null,600,0,name:"app1"));
        records.Add(Peer(unit,Epoch.AddMinutes(311),"100.64.0.1",null,0,600,name:"app3"));
        records.Add(Peer(unit,Epoch.AddMinutes(311),"239.255.255.250",null,5,5,name:"app4",protocol:ConnectionProtocol.Udp));
        records.Add(Peer(unit,Epoch.AddMinutes(311),"9.9.9.9",null,7,7,pid:4242,name:"app0"));
        records.Add(Peer(unit,Epoch.AddMinutes(311),"1.0.0.1",noPoint,8,8,name:"app2"));
        // Two origins at one location.
        records.Add(Peer("o",Epoch.AddMinutes(320),"8.8.4.4",Place,50,50,name:"app2"));
        records.Add(Peer("o",Epoch.AddMinutes(320),"8.8.4.4",Place,70,0,name:"app2",origin:new(10,10)));
        store.Append(records,[],Epoch.AddMinutes(400));
        using(var db=Connect())
            Sql(db,$"UPDATE metadata SET value={Epoch.AddMinutes(4).UtcTicks} WHERE key='expiry'; UPDATE metadata SET value={Epoch.AddMinutes(6).UtcTicks} WHERE key='floor'");
        var whole=Reference(Query(600)); Assert.Equal(256,whole.Markers.Count); Assert.Equal(296+2-256,whole.OmittedMarkers);
        foreach(var q in new[]{Query(600),Query(600) with {Search="app2"},Query(600) with {Search="4242"},Query(600) with {CountryCode="US"},
            Query(600) with {Direction=ConnectionDirection.Received},Query(600) with {Direction=ConnectionDirection.Sent}})
        foreach(int offset in new[]{0,200,(int)Reference(q).TotalRows+5})
        {
            var mine=store.Read(q with {Offset=offset}); var theirs=Reference(q with {Offset=offset});
            Assert.Equal(theirs.Rows,mine.Rows); Assert.Equal(theirs.TotalRows,mine.TotalRows); Assert.Equal(theirs.Summary,mine.Summary);
            Assert.Equal(theirs.Markers,mine.Markers); Assert.Equal(theirs.OmittedMarkers,mine.OmittedMarkers);
        }
    }

    private sealed record ReferenceView(IReadOnlyList<ConnectionHistoryRow> Rows, long TotalRows, ConnectionHistorySummary Summary, IReadOnlyList<ConnectionHistoryMarker> Markers, long OmittedMarkers);
    // The 0.15 statements, copied verbatim from 75d6d65 (ConnectionHistoryStore.cs :22-35, :342, :345 and :351-369), with kind=0 added inside Unlocated's CASE.
    private const string ReferenceColumns = "minute,pid,generation,name,remote,port,protocol,first_seen,last_seen,received,sent,location,origin,complete";
    private const string ReferenceRowOrder = "minute DESC,pid,generation,name,remote,port,protocol,location,origin";
    private const string ReferenceGrouped = """
        SELECT minute,pid,generation,name,remote,port,protocol,min(first_seen) first_seen,
          max(last_seen) last_seen,sat_sum(received) received,sat_sum(sent) sent,
          location,origin,min(complete) complete,country,kind
        FROM traffic
        WHERE minute >= max($from,coalesce((SELECT value FROM metadata WHERE key='expiry'),0)) AND minute < $to
          AND minute > (SELECT value FROM metadata WHERE key='floor')
          AND ($search='' OR instr(search,$search)>0 OR instr(cast(pid AS TEXT),$search)>0)
          AND ($country='' OR country=$country)
        GROUP BY minute,pid,generation,name,remote,port,protocol,location,origin,country,kind
        HAVING ($direction=0 OR ($direction=1 AND sat_sum(received)>0) OR ($direction=2 AND sat_sum(sent)>0))
        """;
    private ReferenceView Reference(ConnectionHistoryQuery query)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Database, Mode=SqliteOpenMode.ReadOnly, Pooling=false, DefaultTimeout=1 }.ToString());
        db.Open(); db.CreateAggregate<long,long>("sat_sum",0,(sum,value)=>sum>long.MaxValue-value?long.MaxValue:sum+value);
        using var tx=db.BeginTransaction(deferred:true);
        SqliteCommand Command(string sql,bool page=false)
        {
            var command=db.CreateCommand(); command.CommandText=sql; command.Transaction=tx;
            command.Parameters.AddWithValue("$from",query.From.UtcTicks); command.Parameters.AddWithValue("$to",query.To.UtcTicks);
            command.Parameters.AddWithValue("$search",query.Search); command.Parameters.AddWithValue("$country",query.CountryCode); command.Parameters.AddWithValue("$direction",(int)query.Direction);
            if(page) {command.Parameters.AddWithValue("$limit",query.Limit); command.Parameters.AddWithValue("$offset",query.Offset);}
            return command;
        }
        static ConnectionHistoryRow ReadRow(SqliteDataReader r) =>
            new(new(r.GetInt64(0),TimeSpan.Zero),new(r.GetInt32(1),r.GetInt64(2),r.GetString(3)),r.GetString(4),r.GetInt32(5),(ConnectionProtocol)r.GetInt32(6),new(r.GetInt64(7),TimeSpan.Zero),new(r.GetInt64(8),TimeSpan.Zero),
                r.GetInt64(9),r.GetInt64(10),r.GetInt64(9)/60.0,r.GetInt64(10)/60.0,System.Text.Json.JsonSerializer.Deserialize<ConnectionLocation>(r.GetString(11)),System.Text.Json.JsonSerializer.Deserialize<GeoPoint>(r.GetString(12)),r.GetInt32(13)!=0);
        var rows=new List<ConnectionHistoryRow>();
        using(var command=Command("WITH filtered AS ("+ReferenceGrouped+") SELECT "+ReferenceColumns+" FROM filtered ORDER BY "+ReferenceRowOrder+" LIMIT $limit OFFSET $offset",page:true))
        using(var reader=command.ExecuteReader()) while(reader.Read()) rows.Add(ReadRow(reader));
        long total; ConnectionHistorySummary summary;
        static long Value(SqliteDataReader reader,int ordinal) => reader.IsDBNull(ordinal)?0:reader.GetInt64(ordinal);
        using(var command=Command("WITH filtered AS ("+ReferenceGrouped+") SELECT count(*),sat_sum(received),sat_sum(sent),count(DISTINCT nullif(country,'')),count(DISTINCT remote),count(DISTINCT CASE WHEN kind=0 AND (location='null' OR json_extract(location,'$.Point') IS NULL) THEN remote END),count(DISTINCT CASE WHEN kind=1 THEN remote END) FROM filtered"))
        using(var reader=command.ExecuteReader()) {reader.Read(); total=reader.GetInt64(0); summary=new(Value(reader,1),Value(reader,2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6));}
        var markers=new List<ConnectionHistoryMarker>(); long markerCount=0;
        string markerSql=$"""
            WITH filtered AS ({ReferenceGrouped}),
            ranked AS (SELECT *,row_number() OVER (ORDER BY {ReferenceRowOrder})-1 row_index FROM filtered),
            groups AS (
              SELECT location,origin,sat_sum(received) total_received,sat_sum(sent) total_sent,min(row_index) representative
              FROM ranked WHERE location<>'null' AND json_extract(location,'$.Point') IS NOT NULL GROUP BY location,origin),
            markers AS (
              SELECT *,count(*) OVER () marker_count FROM groups
              ORDER BY (cast(total_received AS REAL)+cast(total_sent AS REAL)) DESC,location,origin LIMIT 256)
            SELECT {string.Join(',',ReferenceColumns.Split(',').Select(c=>"r."+c))},m.total_received,m.total_sent,m.representative,m.marker_count
            FROM markers m JOIN ranked r ON r.row_index=m.representative
            ORDER BY (cast(m.total_received AS REAL)+cast(m.total_sent AS REAL)) DESC,m.location,m.origin
            """;
        using(var command=Command(markerSql))
        using(var reader=command.ExecuteReader()) while(reader.Read())
        {
            var representative=ReadRow(reader); markerCount=reader.GetInt64(17);
            markers.Add(new(representative.Location!,representative.Origin,reader.GetInt64(14),reader.GetInt64(15),representative,reader.GetInt64(16)));
        }
        return new(rows,total,summary,markers,Math.Max(0,markerCount-markers.Count));
    }

    [Fact] public void Grouped_read_writes_nothing_to_the_database()
    {
        var store=Store(); store.Append([Row(),Row(1,"c",address:"9.9.9.9"),Row(2,"d",address:"192.168.3.5")],[new("c","s",Epoch,Epoch.AddSeconds(30),true)],Epoch.AddMinutes(3));
        (byte[] Main,byte[]? Wal,long WalLength,long ShmLength,long Storage) Files() =>
            (Bytes(Database),File.Exists(Database+"-wal")?Bytes(Database+"-wal"):null,File.Exists(Database+"-wal")?new FileInfo(Database+"-wal").Length:-1,
             File.Exists(Database+"-shm")?new FileInfo(Database+"-shm").Length:-1,store.StorageBytes);
        // SQLite keeps the files open for writing; Windows refuses File.ReadAllBytes (FileShare.Read) while it does.
        static byte[] Bytes(string path)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var copy=new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
        }
        void Unchanged(int rows)
        {
            var before=Files(); Assert.Equal(rows,store.Read(Query()).TotalRows); var after=Files();
            Assert.Equal(before.Main,after.Main); Assert.Equal(before.Wal,after.Wal); Assert.Equal(before.WalLength,after.WalLength); Assert.Equal(before.ShmLength,after.ShmLength); Assert.Equal(before.Storage,after.Storage);
        }
        // A read-only open after the writer has removed its journal recreates -wal and -shm empty, as 0.15's Read did: no page is written.
        var closed=Files(); Assert.Equal(3,store.Read(Query()).TotalRows); var opened=Files();
        Assert.Null(closed.Wal); Assert.Equal(closed.Main,opened.Main); Assert.Equal(0,opened.WalLength);
        // StorageBytes rises by the recreated -shm alone.
        Assert.Equal(opened.ShmLength-Math.Max(0,closed.ShmLength),opened.Storage-closed.Storage);
        Unchanged(3);
        using var pinned=Connect(); using var tx=pinned.BeginTransaction(deferred:true);
        using(var command=pinned.CreateCommand()) {command.Transaction=tx; command.CommandText="SELECT count(*) FROM traffic"; Assert.Equal(3L,command.ExecuteScalar());}
        // The pinned reader keeps this append's frames in -wal, so the read below runs over a non-empty journal.
        store.Append([Row(3,"e",address:"1.1.1.1")],[],Epoch.AddMinutes(4));
        Assert.True(new FileInfo(Database+"-wal").Length>0);
        Unchanged(4);
    }

    [Fact] public void Grouped_read_over_the_temp_limit_fails_without_a_partial_view()
    {
        var store=Store(pages:16);
        store.Append([..Enumerable.Range(0,2000).Select(i=>Row(i%9,$"b{i}",address:$"20.{i/250}.{i%250}.1"))],[],Epoch.AddMinutes(10));
        store.Append([Row(9,"last")],[],Epoch.AddMinutes(10));
        var error=Assert.Throws<ConnectionHistoryLimitException>(()=>store.Read(Query(10)));
        Assert.Equal("Connection history interval holds too many saved rows to group.",error.Message);
        Assert.Equal(13,Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        Assert.Single(store.Read(new(Epoch.AddMinutes(9),Epoch.AddMinutes(10))).Rows);
        Assert.Equal("maximumGroupedTempPages",Assert.Throws<ArgumentOutOfRangeException>(()=>new ConnectionHistoryStore(Database,maximumGroupedTempPages:15)).ParamName);
        Assert.Equal("maximumGroupedTempPages",Assert.Throws<ArgumentOutOfRangeException>(()=>new ConnectionHistoryStore(Database,maximumGroupedTempPages:16385)).ParamName);
        Assert.Equal(16384,ConnectionHistoryStore.MaximumGroupedTempPages);
    }

    [Fact] public void Unlocated_counts_public_peers_without_a_point_only()
    {
        var store=Store();
        store.Append([Row(0,"a",address:"192.168.3.5") with {Location=null},Row(0,"b",address:"9.9.9.9") with {Location=null},
            Row(0,"c",address:"1.0.0.1") with {Location=Place with {CountryCode="AU",Country="Australia",Point=null}},
            Row(0,"d",address:"100.64.0.1") with {Location=null},Row(0,"e",address:"239.255.255.250") with {Location=null},Row(0,"f",address:"8.8.8.8")],[],Epoch.AddMinutes(1));
        var summary=store.Read(Query()).Summary;
        Assert.Equal((2L,1L,6L),(summary.Unlocated,summary.Local,summary.Destinations));
    }

    // The traffic strip and the exact application filter.
    private static ConnectionHistoryRecord A(string name, long received, long sent, int minute = 0) => Row(minute, $"b-{name}-{minute}", received, sent, name:name);
    private ConnectionHistoryStore? opened;
    // The strip and destination tests' shared store, opened on first use.
    private ConnectionHistoryStore shared => opened ??= Store();
    private TrafficStrip Strip(ConnectionHistoryQuery query) => shared.Read(query).Strip!;
    private void Metadata(long floor, long expiry) { using var db=Connect(); Sql(db,$"UPDATE metadata SET value={floor} WHERE key='floor'; UPDATE metadata SET value={expiry} WHERE key='expiry'"); }
    private static byte[] SharedBytes(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var copy=new MemoryStream(); stream.CopyTo(copy); return copy.ToArray();
    }
    private sealed class StripEquality : IEqualityComparer<TrafficStrip?>
    {
        public bool Equals(TrafficStrip? x, TrafficStrip? y) => x is null || y is null ? x is null && y is null :
            (x.From,x.To,x.BucketMilliseconds,x.Measure,x.OtherReceived,x.OtherSent,x.OtherApplications,x.RetainedFrom,x.CapacityShortened)==
            (y.From,y.To,y.BucketMilliseconds,y.Measure,y.OtherReceived,y.OtherSent,y.OtherApplications,y.RetainedFrom,y.CapacityShortened) &&
            x.Applications.SequenceEqual(y.Applications) && x.Slots.Count==y.Slots.Count && x.Slots.Zip(y.Slots).All(p=>
                (p.First.Start,p.First.End,p.First.Minutes,p.First.RecordedSeconds,p.First.Incomplete)==(p.Second.Start,p.Second.End,p.Second.Minutes,p.Second.RecordedSeconds,p.Second.Incomplete) &&
                p.First.Received.SequenceEqual(p.Second.Received) && p.First.Sent.SequenceEqual(p.Second.Sent));
        public int GetHashCode(TrafficStrip? strip) => strip?.Slots.Count ?? 0;
    }
    private static readonly StripEquality StripComparer = new();

    [Fact] public void Strip_ranks_six_applications_by_the_direction_measure_and_folds_the_rest_into_other()
    {
        shared.Append([A("b.exe",70,11),A("a.exe",80,1),A("c.exe",60,3),A("d.exe",50,4),A("e.exe",40,5),A("f.exe",30,6),A("g.exe",20,7),A("h.exe",10,0),A("z.exe",0,0)],[],Epoch.AddMinutes(2));
        var both=Strip(Query()); Assert.Equal(["a.exe","b.exe","c.exe","d.exe","e.exe","f.exe"],both.Applications.Select(a=>a.Name)); Assert.Equal((30L,7L,2L),(both.OtherReceived,both.OtherSent,both.OtherApplications));
        Assert.Equal(TrafficMeasure.Total,both.Measure);
        Assert.Equal(["a.exe","b.exe","c.exe","d.exe","e.exe","f.exe"],Strip(Query() with {Direction=ConnectionDirection.Received}).Applications.Select(a=>a.Name));
        var sent=Strip(Query() with {Direction=ConnectionDirection.Sent}); Assert.Equal(["b.exe","g.exe","f.exe","e.exe","d.exe","c.exe"],sent.Applications.Select(a=>a.Name));
        Assert.Equal((80L,1L,1L),(sent.OtherReceived,sent.OtherSent,sent.OtherApplications)); Assert.Equal(TrafficMeasure.Sent,sent.Measure);
        foreach(var d in Enum.GetValues<ConnectionDirection>())
        {
            var v=shared.Read(Query() with {Direction=d});
            Assert.Equal(v.Summary.ReceivedBytes,v.Strip!.Applications.Sum(a=>a.Received)+v.Strip.OtherReceived); Assert.Equal(v.Summary.SentBytes,v.Strip.Applications.Sum(a=>a.Sent)+v.Strip.OtherSent);
        }
    }
    [Fact] public void Strip_groups_names_by_application_key_and_shows_the_ordinal_smallest_name()
    {
        shared.Append([A("chrome.exe",10,0),A("Chrome.exe",20,0),A("PID 4",5,0)],[],Epoch.AddMinutes(2));
        Assert.Equal([("Chrome.exe","CHROME.EXE",30L),("PID 4","PID 4",5L)],Strip(Query()).Applications.Select(a=>(a.Name,a.Key,a.Received)));
    }
    [Fact] public void Strip_slots_match_a_reference_fold_of_every_read_row()
    {
        var random=new Random(7);
        string[] names=["a.exe","A.EXE","b.exe","c.exe","C.exe","d.exe","e.exe","f.exe","g.exe"];
        (string Address,ConnectionLocation Location)[] remotes=[("8.8.8.8",Place),("9.9.9.9",Place with {City="Elsewhere",Point=new(30,-90)}),
            ("1.1.1.1",Place with {CountryCode="AU",Country="Australia",Region=null,City="Sydney",Point=new(-33,151)}),("1.0.0.1",Place with {CountryCode="AU",Country="Australia",Region=null,City=null,Point=new(-35,149)})];
        int minutes=3*24*60; var records=new List<ConnectionHistoryRecord>(); var coverage=new List<ConnectionCoverage>();
        for(int m=0;m<minutes;m++)
        {
            var minute=Epoch.AddMinutes(m);
            foreach(int n in Enumerable.Range(0,random.Next(0,3)).Select(_=>random.Next(names.Length)).Distinct())
            {
                var (address,location)=remotes[random.Next(remotes.Length)];
                records.Add(Peer($"m{m}",minute,address,location,random.Next(4)==0?0:random.Next(1,5000),random.Next(4)==0?0:random.Next(1,500),pid:10+n,name:names[n]));
            }
            if(random.Next(10)!=0) coverage.Add(new($"m{m}","s",minute,minute.AddSeconds(random.Next(10)==0?30:60),random.Next(20)!=0));
        }
        shared.Append(records,coverage,Epoch.AddMinutes(minutes+1));
        var interval=new ConnectionHistoryQuery(Epoch,Epoch.AddMinutes(minutes),Limit:500);
        foreach(var query in new[]{interval,interval with {Search="c.exe"},interval with {CountryCode="AU"},interval with {Direction=ConnectionDirection.Received},
            interval with {Direction=ConnectionDirection.Sent},interval with {Application="A.exe"}})
        {
            var first=shared.Read(query); var strip=first.Strip!; long bucket=strip.BucketMilliseconds*TimeSpan.TicksPerMillisecond;
            Assert.Equal(5*60_000L,strip.BucketMilliseconds); Assert.True(first.TotalRows>500);
            var keys=strip.Applications.Select(a=>a.Key).ToList(); int series=keys.Count+1;
            var fold=new Dictionary<long,(long[] Received,long[] Sent)>();
            for(int offset=0;offset<first.TotalRows;offset+=500)
                foreach(var row in shared.Read(query with {Offset=offset}).Rows)
                {
                    long slot=(row.Minute-Epoch).Ticks/bucket; int index=keys.IndexOf(ProcessGrouping.Key(row.Process.Name)); if(index<0) index=keys.Count;
                    if(!fold.TryGetValue(slot,out var sums)) fold[slot]=sums=(new long[series],new long[series]);
                    sums.Received[index]+=row.ReceivedBytes; sums.Sent[index]+=row.SentBytes;
                }
            var listed=strip.Slots.ToDictionary(s=>(s.Start-Epoch).Ticks/bucket);
            Assert.All(fold.Keys,slot=>Assert.True(listed.ContainsKey(slot)));
            foreach(var (slot,s) in listed)
            {
                Assert.Equal(Epoch.AddTicks(slot*bucket),s.Start);
                (long[] Received,long[] Sent) expected=fold.TryGetValue(slot,out var sums)?sums:(new long[series],new long[series]);
                Assert.Equal(expected.Received,s.Received); Assert.Equal(expected.Sent,s.Sent);
            }
        }
    }
    [Fact] public void Strip_recorded_seconds_come_from_coverage_and_rows_without_coverage_are_incomplete()
    {
        ConnectionCoverage C(string batch,int minute,double from,double to,bool complete=true) => new(batch,"s",Epoch.AddMinutes(minute).AddSeconds(from),Epoch.AddMinutes(minute).AddSeconds(to),complete);
        shared.Append([..Enumerable.Range(0,5).Select(m=>A("a.exe",10,10,m))],
            [C("c0",0,0,30),C("c1",1,0,60),C("c2",2,0,40),C("d2",2,20,60),C("c3",3,0,60,false),C("c5",5,0,60),C("c6",6,0,0.7)],Epoch.AddMinutes(8));
        var s=Strip(Query(8)); Assert.Equal([0,1,2,3,4,5,6],s.Slots.Select(x=>(int)(x.Start-Epoch).TotalMinutes));
        Assert.Equal([30L,60L,60L,60L,60L,60L,0L],s.Slots.Select(x=>x.RecordedSeconds)); Assert.Equal([false,false,false,true,true,false,false],s.Slots.Select(x=>x.Incomplete));
        Assert.All(s.Slots,x=>Assert.Equal(1,x.Minutes));
        Assert.Equal(0,TrafficStrips.Bytes(s.Slots[5],s.Applications.Count,TrafficMeasure.Total)+TrafficStrips.Bytes(s.Slots[5],0,TrafficMeasure.Total));
        var other=Strip(Query(8) with {Application="other.exe"}); Assert.DoesNotContain(other.Slots,x=>x.Start==Epoch.AddMinutes(4)); Assert.Contains(other.Slots,x=>x.Start==Epoch.AddMinutes(5));
    }
    [Fact] public void Strip_reports_retained_from_after_expiry_and_capacity_eviction()
    {
        var e=Epoch.AddHours(5);
        shared.Append([..new[]{-150,-30,30,90}.Select(m=>Row(300+m,$"r{m}"))],[..new[]{-150,-30,30,90}.Select(m=>new ConnectionCoverage($"r{m}","s",e.AddMinutes(m),e.AddMinutes(m+1),true))],e.AddHours(2));
        var around=new ConnectionHistoryQuery(e.AddHours(-1),e.AddHours(1));
        Metadata(0,e.UtcTicks); var s=Strip(around); Assert.Equal((e,false),(s.RetainedFrom,s.CapacityShortened)); Assert.Equal([e.AddMinutes(30)],s.Slots.Select(x=>x.Start));
        Metadata(e.AddMinutes(10).UtcTicks,e.UtcTicks); s=Strip(around); Assert.Equal((e.AddMinutes(11),true),(s.RetainedFrom,s.CapacityShortened)); Assert.Equal([e.AddMinutes(30)],s.Slots.Select(x=>x.Start));
        Metadata(e.AddMinutes(-10).UtcTicks,e.UtcTicks); s=Strip(around); Assert.Equal((e,false),(s.RetainedFrom,s.CapacityShortened));
        Assert.Null(Strip(new(e.AddHours(1),e.AddHours(2))).RetainedFrom);
        var old=Strip(new(e.AddHours(-3),e.AddHours(-2))); Assert.True(old.RetainedFrom>=old.To); Assert.Empty(old.Slots);
    }
    [Fact] public void Strip_leaves_out_the_minute_that_holds_the_expiry_instant()
    {
        shared.Append([Row()],[new("b","s",Epoch,Epoch.AddMinutes(1),true)],Epoch.AddMinutes(2));
        var interval=new ConnectionHistoryQuery(Epoch.AddMinutes(-10),Epoch.AddMinutes(10));
        Metadata(0,Epoch.AddSeconds(30).UtcTicks); var s=Strip(interval); Assert.DoesNotContain(s.Slots,x=>x.Start==Epoch); Assert.Equal(Epoch.AddMinutes(1),s.RetainedFrom);
        Metadata(0,Epoch.UtcTicks); s=Strip(interval); var slot=Assert.Single(s.Slots); Assert.Equal(Epoch,slot.Start); Assert.True(TrafficStrips.IsFull(slot)); Assert.Equal(Epoch,s.RetainedFrom);
    }
    [Fact] public void Capacity_shortened_status_matches_the_strip()
    {
        shared.Append([Row(20)],[],Epoch.AddMinutes(21));
        Metadata(Epoch.AddMinutes(5).UtcTicks,Epoch.UtcTicks); var view=shared.Read(Query(30)); Assert.Contains("Capacity shortened retention.",view.Status); Assert.True(view.Strip!.CapacityShortened);
        Metadata(Epoch.AddMinutes(-10).UtcTicks,Epoch.UtcTicks); view=shared.Read(Query(30)); Assert.DoesNotContain("Capacity shortened",view.Status); Assert.False(view.Strip!.CapacityShortened);
    }
    [Fact] public void Application_filter_matches_the_key_exactly_everywhere()
    {
        ConnectionHistoryRecord Named(string name,long received,string address,string city) => Row(0,"b-"+name,received,0,name:name,address:address) with {Location=Place with {City=city}};
        shared.Append([Named("chrome.exe",1,"8.8.8.8","One"),Named("Chrome.exe",2,"8.8.4.4","Two"),Named("chrome",4,"9.9.9.9","Three"),Named("other.exe",8,"1.1.1.1","Four")],[],Epoch.AddMinutes(1));
        var bare=shared.Read(Query() with {Application="chrome"}); Assert.Equal(["chrome"],bare.Rows.Select(r=>r.Process.Name)); Assert.Equal(4,bare.Summary.ReceivedBytes);
        var exe=shared.Read(Query() with {Application="CHROME.EXE"});
        Assert.Equal(["Chrome.exe","chrome.exe"],exe.Rows.Select(r=>r.Process.Name).Order(StringComparer.Ordinal)); Assert.Equal(2,exe.TotalRows); Assert.Equal(3,exe.Summary.ReceivedBytes);
        Assert.Equal(["One","Two"],exe.Markers.Select(m=>m.Location.City!).Order(StringComparer.Ordinal));
        var app=Assert.Single(exe.Strip!.Applications); Assert.Equal(("Chrome.exe","CHROME.EXE",3L),(app.Name,app.Key,app.Received)); Assert.Equal(0,exe.Strip.OtherApplications);
        var lines=Csv(shared,Query() with {Application="CHROME.EXE"}).Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries)[1..];
        Assert.Equal(["\"Chrome.exe\"","\"chrome.exe\""],lines.Select(l=>l.Split(',')[1]).Order(StringComparer.Ordinal));
        Assert.Equal(4,shared.Read(Query() with {Application=""}).TotalRows); Assert.Equal(4,shared.Read(Query() with {Application="   "}).TotalRows);
        Assert.Throws<ArgumentException>(()=>shared.Read(Query() with {Application=new string('a',261)}));
        Assert.Throws<ArgumentException>(()=>shared.Read(Query() with {Application=null!}));
        // A schema v1 file is read with the filter set and never rewritten.
        var legacy=Store(file:Path.Combine(directory,"legacy.db")); legacy.Append([Row()],[],Epoch.AddMinutes(1));
        using(var db=Connect(Path.Combine(directory,"legacy.db"))) Sql(db,"UPDATE traffic SET pid=7654321");
        var before=SharedBytes(Path.Combine(directory,"legacy.db"));
        Assert.Single(legacy.Read(Query() with {Application="Browser"}).Rows);
        Assert.Equal(before,SharedBytes(Path.Combine(directory,"legacy.db")));
    }
    [Fact] public void Strip_is_the_same_for_per_sample_and_merged_minute_rows()
    {
        (string Address, ConnectionLocation? Location)[] pairs=[("8.8.8.8",Place),("192.168.1.5",null),("9.9.9.9",null)];
        var storeA=Store(file:Path.Combine(directory,"per-poll.db")); var storeB=Store(file:Path.Combine(directory,"per-minute.db"));
        storeA.Append([..Enumerable.Range(0,60).SelectMany(i=>pairs.Select(p=>Peer($"s:{i}:1",Epoch,p.Address,p.Location,10,10,firstSecond:i,lastSecond:i)))],
            [..Enumerable.Range(0,60).Select(i=>new ConnectionCoverage($"s:{i}:1","s",Epoch.AddSeconds(i),Epoch.AddSeconds(i+1),true))],Epoch.AddMinutes(2));
        string unit=ConnectionMinuteFolder.BatchId("s",Epoch,1,0);
        storeB.Append([..pairs.Select(p=>Peer(unit,Epoch,p.Address,p.Location,600,600,firstSecond:0,lastSecond:59))],[new(unit,"s",Epoch,Epoch.AddMinutes(1),true)],Epoch.AddMinutes(2));
        Assert.Equal(60,Assert.Single(storeA.Read(Query(2)).Strip!.Slots).RecordedSeconds);
        Assert.Equal(storeA.Read(Query(2)).Strip,storeB.Read(Query(2)).Strip,StripComparer);
    }
    [Fact] public void Application_filter_folds_non_ascii_case_and_never_pattern_matches()
    {
        shared.Append([A("ärger.exe",1,0),A("ÄRGER.EXE",2,0),A("a_b.exe",4,0),A("aXb.exe",8,0),A("50%.exe",16,0),A("500.exe",32,0)],[],Epoch.AddMinutes(2));
        long R(string app)=>shared.Read(Query() with {Application=app}).Summary.ReceivedBytes; Assert.Equal((3L,4L,16L),(R("Ärger.exe"),R("a_b.exe"),R("50%.exe")));
    }

    // Coverage in the minute that holds From (Epoch + 30 s) and traffic in the first whole minute after it.
    private void UnalignedFixture() => shared.Append([A("a.exe",10,10,1)],[new("c0","s",Epoch,Epoch.AddMinutes(1),true)],Epoch.AddDays(3).AddMinutes(2));
    [Fact] public void Strip_one_minute_slots_start_at_the_first_whole_minute_of_an_unaligned_interval()
    {
        UnalignedFixture(); var from=Epoch.AddSeconds(30);
        var strip=Strip(new(from,Epoch.AddMinutes(10))); Assert.Equal(60_000,strip.BucketMilliseconds);
        Assert.All(strip.Slots,x=>Assert.True(x.Start>=TrafficStrips.CeilingMinute(from)));
        Assert.Equal((Epoch.AddMinutes(1),1L),(strip.Slots[0].Start,strip.Slots[0].Minutes));
    }
    [Fact] public void Strip_longer_slots_leave_out_coverage_before_the_first_whole_minute_of_an_unaligned_interval()
    {
        UnalignedFixture(); var from=Epoch.AddSeconds(30);
        var strip=Strip(new(from,from.AddDays(3))); Assert.True(strip.BucketMilliseconds>=120_000);
        var first=strip.Slots[0]; Assert.Equal(Epoch.AddMinutes(1),first.Start);
        Assert.True(first.Minutes<=TrafficStrips.MinuteStarts(first.Start,first.End));
        // Only minute 1 (rows without coverage, 60 s); the piece in the minute that holds From is not in the grid.
        Assert.Equal((1L,60L),(first.Minutes,first.RecordedSeconds));
    }
    [Fact] public void Strip_never_ranks_a_zero_measure_key_even_with_free_places()
    {
        shared.Append([A("a.exe",10,5),A("b.exe",20,6),A("z.exe",0,0)],[],Epoch.AddMinutes(2));
        var both=Strip(Query()); Assert.Equal(["b.exe","a.exe"],both.Applications.Select(a=>a.Name)); Assert.Equal((0L,0L,0L),(both.OtherReceived,both.OtherSent,both.OtherApplications));
        shared.Append([A("h.exe",10,0)],[],Epoch.AddMinutes(2));
        // Under Sent the direction filter drops h.exe's rows before the strip, so it neither ranks nor counts as Other.
        var sent=Strip(Query() with {Direction=ConnectionDirection.Sent}); Assert.Equal(["b.exe","a.exe"],sent.Applications.Select(a=>a.Name)); Assert.Equal((0L,0L,0L),(sent.OtherReceived,sent.OtherSent,sent.OtherApplications));
        Assert.DoesNotContain(Strip(Query()).Applications,a=>a.Name=="z.exe");
    }
    // The Destinations table's History steps and the destinations CSV.
    private static readonly ConnectionLocation Elsewhere = Place with { City="Elsewhere", Point=new(30,-90) };
    private static ConnectionHistoryRecord Hit(string batch, int minute, string address, ConnectionLocation? location, string name = "browser", int port = 443,
        ConnectionProtocol protocol = ConnectionProtocol.Tcp, long received = 10, long sent = 20, bool complete = true, int pid = 7, int firstSecond = 1, int lastSecond = 2) =>
        new(batch, "s", new(Epoch.AddMinutes(minute), new(new(pid,1,name), "192.168.1.234", 49123, address, port, protocol), received, sent,
            Epoch.AddMinutes(minute).AddSeconds(firstSecond), Epoch.AddMinutes(minute).AddSeconds(lastSecond)), location, new(40,-80), complete);
    private static (long,long,long,long,long) Tuple(ConnectionDestinationCounts c) => (c.All,c.Internet,c.Local,c.Special,c.Unlocated);
    private static string[] Lines(string text) => text.Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries);
    private static string DestinationsCsv(ConnectionHistoryStore store, ConnectionHistoryQuery query, DestinationScope scope)
    { using var output=new StringWriter(); store.ExportDestinationsCsv(output,query,scope); return output.ToString(); }

    [Fact] public void History_destinations_match_a_brute_force_grouping()
    {
        var random=new Random(11);
        string[] addresses=[..Enumerable.Range(0,20).Select(i=>$"20.1.0.{i+1}"),..Enumerable.Range(0,20).Select(i=>$"192.168.5.{i+1}"),
            ..Enumerable.Range(0,10).Select(i=>$"100.64.0.{i+1}"),..Enumerable.Range(0,10).Select(i=>$"239.1.1.{i+1}")];
        string[] names=["alpha.exe","Alpha.exe","beta.exe","gamma.exe","DELTA.exe","epsilon.exe"];
        int[] ports=[53,80,123,443,8080];
        ConnectionLocation?[] places=[null,Place,Elsewhere,Place with {CountryCode="AU",Country="Australia",Region=null,City="Sydney",Point=new(-33,151)},
            Place with {CountryCode="DE",Country="Germany",Region=null,City=null,Point=null}];
        // One location per address and minute, so the latest one never ties.
        var located=new Dictionary<(string,int),ConnectionLocation?>();
        var records=new List<ConnectionHistoryRecord>();
        for(int i=0;i<900;i++)
        {
            string address=addresses[random.Next(addresses.Length)]; int minute=random.Next(30);
            if(!located.TryGetValue((address,minute),out var location)) located[(address,minute)]=location=places[random.Next(places.Length)];
            records.Add(Hit($"r{i}",minute,address,location,names[random.Next(names.Length)],ports[random.Next(ports.Length)],(ConnectionProtocol)random.Next(2),
                random.Next(5)==0?0:random.Next(1,100000),random.Next(5)==0?0:random.Next(1,10000),random.Next(8)!=0,pid:10+random.Next(4),firstSecond:random.Next(0,20),lastSecond:random.Next(20,60)));
        }
        shared.Append(records,[],Epoch.AddMinutes(31));
        var list=shared.Read(Query(30)).Destinations!;
        var groups=records.GroupBy(r=>r.Delta.Peer.RemoteAddress).ToDictionary(g=>g.Key,g=>g.ToList());
        Assert.Equal(groups.Count,list.Rows.Count); Assert.Equal(groups.Count,list.Counts.All);
        foreach(var row in list.Rows)
        {
            var g=groups[row.Address]; var kind=NetworkAddress.Describe(row.Address).Kind;
            Assert.Equal((g.Sum(r=>r.Delta.ReceivedBytes),g.Sum(r=>r.Delta.SentBytes)),(row.ReceivedBytes,row.SentBytes));
            Assert.Equal(g.Select(r=>r.Delta.Minute).Distinct().Count(),row.Minutes);
            Assert.Equal((g.Min(r=>r.Delta.FirstSeen),g.Max(r=>r.Delta.LastSeen)),(row.FirstSeen,row.LastSeen));
            var apps=g.GroupBy(r=>ProcessGrouping.Key(r.Delta.Peer.Process.Name)).Select(a=>(a.Key,
                Name:UsageMetrics.NormalizeName(a.OrderByDescending(r=>r.Delta.Minute).ThenBy(r=>r.Delta.Peer.Process.Name,StringComparer.Ordinal).First().Delta.Peer.Process.Name),
                Received:a.Sum(r=>r.Delta.ReceivedBytes),Sent:a.Sum(r=>r.Delta.SentBytes))).ToList();
            Assert.Equal(apps.Count,row.AppCount);
            Assert.Equal(apps.OrderByDescending(a=>a.Received+a.Sent).ThenBy(a=>a.Key,StringComparer.Ordinal).Take(10).Select(a=>new ConnectionDestinationApp(a.Name,a.Received,a.Sent)),row.Apps);
            var hits=g.GroupBy(r=>(r.Delta.Peer.RemotePort,r.Delta.Peer.Protocol)).Select(p=>new ConnectionDestinationPort(p.Key.RemotePort,p.Key.Protocol,p.Sum(r=>r.Delta.ReceivedBytes),p.Sum(r=>r.Delta.SentBytes))).ToList();
            Assert.Equal(hits.Count,row.PortCount);
            Assert.Equal(hits.OrderByDescending(p=>p.ReceivedBytes+p.SentBytes).ThenBy(p=>p.Port).ThenBy(p=>p.Protocol).Take(10),row.Ports);
            Assert.Equal(g.All(r=>r.Complete),row.Complete);
            Assert.Equal(NetworkAddress.Describe(row.Address),row.Class);
            Assert.Equal(kind==NetworkAddressKind.Public&&g.Any(r=>r.Location?.Point is null),row.Unlocated);
            Assert.Equal(g.Where(r=>r.Location is not null).OrderByDescending(r=>r.Delta.Minute).FirstOrDefault()?.Location,row.Location);
            Assert.Null(row.DownloadBytesPerSecond); Assert.Null(row.UploadBytesPerSecond); Assert.Null(row.Connections);
        }
        Assert.Equal(list.Rows.OrderByDescending(r=>(double)r.ReceivedBytes+r.SentBytes).ThenBy(r=>r.Address,StringComparer.Ordinal).Select(r=>r.Address),list.Rows.Select(r=>r.Address));
        Assert.Contains(list.Rows,r=>r.Complete==false); Assert.Contains(list.Rows,r=>r.Unlocated); 
        // alpha.exe and Alpha.exe are one app.
        Assert.Contains(list.Rows,r=>groups[r.Address].Select(x=>x.Delta.Peer.Process.Name).Distinct().Count(n=>n is "alpha.exe" or "Alpha.exe")==2&&r.Apps.Count(a=>a.Name is "alpha.exe" or "Alpha.exe")==1);
    }
    [Fact] public void History_destination_counts_match_the_summary()
    {
        var bounded=Store(file:Path.Combine(directory,"bounded.db"));
        var rows=Enumerable.Range(0,300).Select(i=>Row(i,"b"+i,address:"8.9."+(i/250)+"."+(i%250+1)) with {Location=Place with {City="City "+i,Point=new(i%90,i%180)}}).ToList();
        rows.Add(Row(300,"local",address:"192.168.3.5") with {Location=null}); rows.Add(Row(301,"unknown",address:"9.9.9.9") with {Location=null});
        bounded.Append(rows,[],Epoch.AddMinutes(302)); var view=bounded.Read(Query(302));
        Assert.Equal((302L,301L,1L,0L,1L),Tuple(view.Destinations!.Counts));
        Assert.Equal((view.Summary.Destinations,view.Summary.Local,view.Summary.Unlocated),(view.Destinations.Counts.All,view.Destinations.Counts.Local,view.Destinations.Counts.Unlocated));
        shared.Append([Row(0,"a",address:"192.168.3.5") with {Location=null},Row(0,"b",address:"9.9.9.9") with {Location=null},
            Row(0,"c",address:"1.0.0.1") with {Location=Place with {CountryCode="AU",Country="Australia",Point=null}},
            Row(0,"d",address:"100.64.0.1") with {Location=null},Row(0,"e",address:"239.255.255.250") with {Location=null},Row(0,"f",address:"8.8.8.8")],[],Epoch.AddMinutes(1));
        Assert.Equal((6L,3L,1L,2L,2L),Tuple(shared.Read(Query()).Destinations!.Counts));
    }
    [Fact] public void History_destination_shows_the_latest_saved_location_and_every_point()
    {
        shared.Append([Hit("m0",0,"8.8.8.8",Place),Hit("m1",1,"8.8.8.8",null),Hit("m2",2,"8.8.8.8",Elsewhere),Hit("m3",3,"8.8.8.8",null)],[],Epoch.AddMinutes(5));
        var view=shared.Read(Query()); var d=view.Destinations!.Rows.Single();
        Assert.Equal(Elsewhere,d.Location); Assert.Equal([Elsewhere.Point!,Place.Point!],d.Points); Assert.True(d.Unlocated);
        Assert.Equal(view.Summary.Unlocated,view.Destinations.Counts.Unlocated); Assert.Equal(4,d.Minutes);
    }
    [Fact] public void History_destinations_follow_search_country_direction_and_the_minute()
    {
        var australia=Place with {CountryCode="AU",Country="Australia",Point=new(-33,151)};
        shared.Append([Hit("a",0,"8.8.8.8",Place,"a.exe",received:10,sent:0),Hit("b",1,"1.1.1.1",australia,"b.exe",received:0,sent:5),
            Hit("d",1,"8.8.8.8",Place,"d.exe",received:3,sent:4),Hit("c",2,"192.168.1.9",null,"c.exe",received:7,sent:7)],[],Epoch.AddMinutes(3));
        IEnumerable<(string,long,long)> Shown(ConnectionHistoryQuery query) => shared.Read(query).Destinations!.Rows.Select(r=>(r.Address,r.ReceivedBytes,r.SentBytes)).OrderBy(r=>r.Address,StringComparer.Ordinal);
        Assert.Equal([("1.1.1.1",0L,5L),("192.168.1.9",7L,7L),("8.8.8.8",13L,4L)],Shown(Query()));
        Assert.Equal([("192.168.1.9",7L,7L)],Shown(Query() with {Search="c.exe"}));
        Assert.Equal([("1.1.1.1",0L,5L)],Shown(Query() with {CountryCode="au"}));
        Assert.Equal([("192.168.1.9",7L,7L),("8.8.8.8",13L,4L)],Shown(Query() with {Direction=ConnectionDirection.Received}));
        Assert.Equal([("1.1.1.1",0L,5L),("192.168.1.9",7L,7L),("8.8.8.8",3L,4L)],Shown(Query() with {Direction=ConnectionDirection.Sent}));
        Assert.Equal([("1.1.1.1",0L,5L)],Shown(Query() with {Application="B.EXE"}));
        Assert.Equal([("1.1.1.1",0L,5L),("8.8.8.8",3L,4L)],Shown(new(Epoch.AddMinutes(1),Epoch.AddMinutes(2))));
        var sent=shared.Read(Query() with {Direction=ConnectionDirection.Sent}).Destinations!.Rows.Single(r=>r.Address=="8.8.8.8");
        Assert.Equal((1,"d.exe",1L),(sent.AppCount,sent.Apps[0].Name,sent.Minutes));
    }
    [Fact] public void History_destination_lists_are_bounded()
    {
        var records=new List<ConnectionHistoryRecord>();
        for(int i=0;i<1100;i++)
        {
            records.Add(Hit($"p{i}",i%20,$"20.{i/250}.{i%250}.1",Place));
            records.Add(Hit($"l{i}",i%20,$"10.{i/250}.{i%250}.1",null));
            records.Add(Hit($"s{i}",i%20,$"100.{64+i/250}.{i%250}.1",null));
        }
        for(int a=1;a<=5;a++) for(int i=0;i<15;i++) records.Add(Hit($"h{a}-{i}",i,$"8.8.8.{a}",Place,$"app{i}.exe",1000+i,received:1000+i));
        ConnectionLocation At(int minute) => Place with {City=$"City {minute}",Point=new(minute,minute)};
        for(int m=0;m<12;m++) records.Add(Hit($"g{m}",m,"9.9.9.9",At(m),received:5000));
        shared.Append(records,[],Epoch.AddMinutes(21));
        var l=shared.Read(Query(20)).Destinations!;
        Assert.True(l.Rows.Count<=3000); Assert.All(Enum.GetValues<NetworkAddressKind>(),k=>Assert.True(l.Rows.Count(r=>r.Class.Kind==k)<=1000));
        Assert.Equal((3306L,1106L,1100L,1100L,0L),Tuple(l.Counts));
        Assert.Equal(5,l.Rows.Count(r=>r.AppCount==15));
        Assert.All(l.Rows.Where(r=>r.AppCount==15),r=>Assert.Equal((10,15,10),(r.Apps.Count,r.PortCount,r.Ports.Count)));
        Assert.All(l.Rows.Where(r=>r.AppCount==15),r=>Assert.Equal([..Enumerable.Range(5,10).Reverse().Select(i=>$"app{i}.exe")],r.Apps.Select(x=>x.Name)));
        var many=l.Rows.Single(r=>r.Points.Count>1); Assert.Equal(8,many.Points.Count); Assert.Equal(At(11),many.Location);
        Assert.Equal([..Enumerable.Range(4,8).Reverse().Select(m=>At(m).Point!)],many.Points);
    }
    [Fact] public void Destinations_csv_lists_every_destination_in_scope()
    {
        var records=new List<ConnectionHistoryRecord>();
        string Address(int i) => i<1000?$"20.{i/250}.{i%250}.1":$"10.0.{i-1000}.1";
        for(int i=1;i<1200;i++)
            records.Add(Hit($"r{i}",i%10,Address(i),i<1000?Place:null,i==1?"=formula":"browser",received:i%600+1,sent:0));
        for(int k=0;k<120;k++) records.Add(Hit($"a{k}",k%10,Address(0),Place,$"app{k:000}.exe",received:k+1,sent:0));
        shared.Append(records,[],Epoch.AddMinutes(11));
        var lines=Lines(DestinationsCsv(shared,Query(10),DestinationScope.All));
        Assert.Equal("Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete",lines[0]);
        Assert.Equal(1201,lines.Length);
        var expected=records.GroupBy(r=>r.Delta.Peer.RemoteAddress).OrderByDescending(g=>(double)g.Sum(r=>r.Delta.ReceivedBytes)+g.Sum(r=>r.Delta.SentBytes)).ThenBy(g=>g.Key,StringComparer.Ordinal).Select(g=>"\""+g.Key+"\"");
        string[][] cells=[..lines.Skip(1).Select(line=>line.Split(','))];
        Assert.Equal(expected,cells.Select(c=>c[0]));
        var formula=cells.Single(c=>c[0]=="\"20.0.1.1\"");
        Assert.Equal(["\"20.0.1.1\"","\"Internet\"","\"US\"","\"United States\"","\"Illinois\"","\"Chicago\"","\"41\"","\"-87\"","\"1\"","\"'=formula\"","\"1\"","\"443 Tcp\"","\"2\"","\"0\"","\"1\"",
            "\""+Epoch.AddMinutes(1).AddSeconds(1).ToString("O")+"\"","\""+Epoch.AddMinutes(1).AddSeconds(2).ToString("O")+"\"","\"True\""],formula);
        var busy=cells.Single(c=>c[0]=="\"20.0.0.1\"");
        Assert.Equal("\"120\"",busy[8]);
        Assert.Equal("\""+string.Join("; ",Enumerable.Range(20,100).Reverse().Select(k=>$"app{k:000}.exe"))+"; +20 more\"",busy[9]);
        Assert.Equal("\"7260\"",busy[12]); Assert.Equal("\"10\"",busy[14]);
        var local=Lines(DestinationsCsv(shared,Query(10),DestinationScope.Local));
        Assert.Equal(201,local.Length); Assert.All(local.Skip(1),line=>Assert.StartsWith("\"10.0.",line));
        Assert.All(local.Skip(1),line=>Assert.Equal(["\"Local · private\"","\"\"","\"\"","\"\"","\"\"","\"\"","\"\""],line.Split(',')[1..8]));
        Assert.Equal(1001,Lines(DestinationsCsv(shared,Query(10),DestinationScope.Internet)).Length);
    }
    [Fact] public void Destinations_csv_caps_ports_and_neutralizes_formulas_quotes_and_control_characters()
    {
        var hostile=Place with {CountryCode="@x",Country="+Country",Region="-Region",City="say \"hi\"\r\n=x"};
        shared.Append([..Enumerable.Range(0,1005).Select(p=>Hit($"p{p}",0,"8.8.8.8",Place,port:p+1,protocol:(ConnectionProtocol)(p%2),received:p+1)),
            Hit("hostile",0,"9.9.9.9",hostile,"@evil.exe",received:1)],[],Epoch.AddMinutes(1));
        var lines=Lines(DestinationsCsv(shared,Query(),DestinationScope.All));
        Assert.Equal(3,lines.Length);
        var cells=lines[1].Split(',');
        Assert.Equal("\"1005\"",cells[10]);
        var ports=cells[11].Trim('"').Split("; ");
        Assert.Equal(1001,ports.Length); Assert.Equal("1005 Tcp",ports[0]); Assert.Equal("1004 Udp",ports[1]); Assert.Equal("+5 more",ports[^1]);
        // Every text cell that could start a formula gets a leading quote; control characters become spaces and quotes double.
        var neutralized=lines[2].Split(',');
        Assert.Equal(["\"9.9.9.9\"","\"Internet\"","\"'@x\"","\"'+Country\"","\"'-Region\"","\"'say \"\"hi\"\"  =x\"","\"41\"","\"-87\""],neutralized[..8]);
        Assert.Equal(("\"1\"","\"'@evil.exe\""),(neutralized[8],neutralized[9]));
    }
    [Fact] public void Destinations_csv_over_the_temp_limit_writes_nothing()
    {
        var store=Store(pages:16);store.Append([..Enumerable.Range(0,2000).Select(i=>Row(i%9,$"b{i}",address:$"20.{i/250}.{i%250}.1"))],[],Epoch.AddMinutes(10));var writer=new StringWriter();
        Assert.Throws<ConnectionHistoryLimitException>(()=>store.ExportDestinationsCsv(writer,Query(10),DestinationScope.All));Assert.Equal("",writer.ToString());
    }
    [Fact] public void Destinations_csv_over_the_temp_limit_in_a_page_keeps_what_was_written()
    {
        // 136 pages hold the grouped, destinations and exported tables for 3,000 remotes but not the first page's picked rows.
        // Probed on this SQLite build: 128–140 pages fail inside the first page; a SQLitePCLRaw/e_sqlite3 bump may need a re-probe.
        var store=Store(pages:136);store.Append([..Enumerable.Range(0,3000).Select(i=>Row(i%9,$"b{i}",address:$"20.{i/250}.{i%250}.1"))],[],Epoch.AddMinutes(10));var writer=new StringWriter();
        Assert.Throws<ConnectionHistoryLimitException>(()=>store.ExportDestinationsCsv(writer,Query(10),DestinationScope.All));
        Assert.Equal(["Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete"],Lines(writer.ToString()));
    }

    private static void Seam(ConnectionHistoryStore store,Action action) =>
        typeof(ConnectionHistoryStore).GetField("maintenanceCommitted",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(store,action);
    private long Scalar(string sql) { using var db=Connect(); using var command=db.CreateCommand(); command.CommandText=sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private long Hidden() => Scalar("SELECT count(*) FROM traffic WHERE minute<(SELECT value FROM metadata WHERE key='expiry')");

    [Fact] public void Bounded_maintenance_drains_a_raised_expiry_over_several_appends(){
        var store=Store(); int commits=0; Seam(store,()=>commits++);
        store.Append([..Enumerable.Range(0,1000).Select(i=>Row(0,"b"+i/100,generation:i+1))],[],Epoch.AddMinutes(1)); commits=0;
        store.Append([],[],Epoch.AddDays(31),maintenanceTransactions:2);
        Assert.Equal(2,commits); Assert.Equal(1000-128,Hidden());
        var view=store.Read(Query()); Assert.Empty(view.Rows); Assert.Equal(0,view.Summary.ReceivedBytes); Assert.Empty(view.Markers); Assert.Empty(view.Coverage);
        using(var output=new StringWriter()) {store.ExportCsv(output,Query()); Assert.Single(output.ToString().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries));}
        commits=0; store.Append([],[],Epoch.AddDays(31),maintenanceTransactions:0); Assert.Equal(0,commits); Assert.Equal(1000-128,Hidden());
        store.Append([],[],Epoch.AddDays(31),64);
        Assert.Equal(0,Hidden());
        Assert.Throws<ArgumentOutOfRangeException>(()=>store.Append([],[],Epoch.AddDays(31),-1));
    }
    [Fact] public void Full_file_purges_hidden_rows_before_evicting_a_bucket(){
        var store=Store(262144); int commits=0; Seam(store,()=>commits++);
        var old=Enumerable.Range(0,200).Select(i=>Row(0,"old",generation:i+1,name:new string('x',100)) with {Location=null,Origin=null}).ToArray();
        store.Append(old,[],Epoch.AddMinutes(1));
        int minute=31*1440-1; var incoming=old.Select(x=>x with {BatchId="new",Delta=x.Delta with {Minute=Epoch.AddMinutes(minute),FirstSeen=Epoch.AddMinutes(minute).AddSeconds(1),LastSeen=Epoch.AddMinutes(minute).AddSeconds(2)}}).ToArray();
        store.Append([],[],Epoch.AddDays(31),maintenanceTransactions:0); Assert.Equal(0,commits); Assert.Equal(200,Hidden());
        store.Append(incoming,[],Epoch.AddDays(31),maintenanceTransactions:0);
        Assert.True(commits>=1); Assert.Equal(0,Scalar("SELECT value FROM metadata WHERE key='floor'"));
        Assert.Equal(200,Scalar("SELECT count(*) FROM traffic WHERE batch='new'"));
    }

    // Task 8: clearing connections.db.
    private static readonly DateTimeOffset Before = Epoch.AddMinutes(10);
    private static void ClearSeam(ConnectionHistoryStore store,string field,object? value) =>
        typeof(ConnectionHistoryStore).GetField(field,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(store,value);
    // C(store) runs store.ClearStep(TimeSpan.FromMilliseconds(250)) until Finished and returns the step count.
    private static int C(ConnectionHistoryStore store)
    {
        for(int steps=1;;steps++)
        {
            if(store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) return steps;
            Assert.True(steps<100000);
        }
    }
    private long Meta(string key) => Scalar($"SELECT value FROM metadata WHERE key='{key}'");
    private long ClearMarkers() => Scalar("SELECT count(*) FROM metadata WHERE substr(key,1,6)='clear-'");
    private long Wal() => File.Exists(Database+"-wal") ? new FileInfo(Database+"-wal").Length : 0;

    [Fact] public void One_app_clear_hides_at_once_then_deletes_rows_and_receipts(){
        // Rows before Before: "Chrome.exe" in batches b1 and b2, "CHROME.EXE" in b3, "other.exe" in b3; and a Chrome row at Before (b4).
        var store=Store();
        store.Append([Row(1,"b1",name:"Chrome.exe",address:"1.1.1.1"),Row(2,"b2",name:"Chrome.exe",address:"1.1.1.2"),Row(3,"b3",name:"CHROME.EXE",address:"1.1.1.3"),
            Row(3,"b3",name:"other.exe",address:"2.2.2.2"),Row(10,"b4",name:"chrome.exe",address:"1.1.1.4")],
            [new("cov","s",Epoch.AddMinutes(5),Epoch.AddMinutes(5).AddSeconds(15),true)],Epoch.AddMinutes(11));
        Assert.Equal(Epoch.AddMinutes(1),store.Read(Query(11)).EarliestRetainedTime);
        store.BeginClear(new("CHROME.EXE",Before,Before));
        Assert.Equal(new PendingConnectionClear("CHROME.EXE",Before),store.PendingClear);
        // Read (Rows, Summary, Strip), ExportCsv, ExportDestinationsCsv and EarliestRetainedTime omit the Chrome rows before Before and keep other.exe and the row at Before.
        void Check()
        {
            var view=store.Read(Query(11));
            Assert.Equal(2,view.TotalRows);
            Assert.Equal(["chrome.exe","other.exe"],view.Rows.Select(r=>r.Process.Name).OrderBy(n=>n,StringComparer.Ordinal));
            Assert.Equal(20,view.Summary.ReceivedBytes); Assert.Equal(40,view.Summary.SentBytes);
            Assert.Equal(20,view.Strip!.Applications.Sum(a=>a.Received)+view.Strip.OtherReceived);
            Assert.Equal(20,view.Strip.Slots.Sum(s=>s.Received.Sum()));
            Assert.Equal(["1.1.1.4","2.2.2.2"],view.Destinations!.Rows.Select(d=>d.Address).OrderBy(a=>a,StringComparer.Ordinal));
            Assert.Equal(Epoch.AddMinutes(3),view.EarliestRetainedTime);
            var csv=Csv(store,Query(11));
            Assert.Equal(3,Lines(csv).Length); Assert.DoesNotContain("Chrome.exe",csv); Assert.DoesNotContain("CHROME.EXE",csv);
            Assert.Contains("\"chrome.exe\"",csv); Assert.Contains("\"other.exe\"",csv);
            var destinations=DestinationsCsv(store,Query(11),DestinationScope.All);
            Assert.Equal(3,Lines(destinations).Length);
            foreach(var address in new[]{"1.1.1.1","1.1.1.2","1.1.1.3"}) Assert.DoesNotContain(address,destinations);
            Assert.Contains("\"2.2.2.2\"",destinations); Assert.Contains("\"1.1.1.4\"",destinations);
        }
        Check();
        // The application filter on the cleared key keeps only the row at Before.
        Assert.Equal(Epoch.AddMinutes(10),Assert.Single(store.Read(Query(11) with {Application="chrome.exe"}).Rows).Minute);
        C(store);
        Check();
        // SELECT count(*) of traffic and dedup for the Chrome rows before Before: 0; manifests: b3's remains (other.exe keeps its receipt), b1's and b2's are gone; b4 untouched;
        // coverage count unchanged; PendingClear null; no metadata key starts with 'clear-'; 'cleared-one' == Before ticks.
        Assert.Equal(0,Scalar($"SELECT count(*) FROM traffic WHERE minute<{Before.UtcTicks} AND name IN ('Chrome.exe','CHROME.EXE')"));
        Assert.Equal(0,Scalar("SELECT count(*) FROM dedup WHERE kind=0 AND batch IN ('b1','b2')"));
        Assert.Equal(1,Scalar("SELECT count(*) FROM dedup WHERE kind=0 AND batch='b3'"));
        Assert.Equal(0,Scalar("SELECT count(*) FROM manifests WHERE batch IN ('b1','b2')"));
        Assert.Equal(1,Scalar("SELECT count(*) FROM manifests WHERE batch='b3' AND kind=0"));
        Assert.Equal(1,Scalar("SELECT count(*) FROM traffic WHERE batch='b4'")); Assert.Equal(1,Scalar("SELECT count(*) FROM dedup WHERE batch='b4'")); Assert.Equal(1,Scalar("SELECT count(*) FROM manifests WHERE batch='b4'"));
        Assert.Equal(1,Scalar("SELECT count(*) FROM coverage")); Assert.Equal(1,Scalar("SELECT count(*) FROM dedup WHERE kind=1"));
        Assert.Null(store.PendingClear); Assert.Equal(0,ClearMarkers()); Assert.Equal(Before.UtcTicks,Meta("cleared-one"));
        Assert.Equal(0,Scalar("SELECT count(*) FROM metadata WHERE key='cleared-all'"));
    }
    [Fact] public void All_apps_clear_raises_expiry_and_purges_below_it(){
        var store=Store();
        store.Append([Row(1,"b1"),Row(2,"b2",name:"other.exe"),Row(10,"b3"),Row(12,"b4")],
            [new("c1","s",Epoch.AddMinutes(2),Epoch.AddMinutes(2).AddSeconds(15),true),new("c2","s",Epoch.AddMinutes(12),Epoch.AddMinutes(12).AddSeconds(15),true)],Epoch.AddMinutes(13));
        store.BeginClear(new(null,Before,Before));Assert.Equal(Before.UtcTicks,Meta("expiry"));store.Append([],[],Epoch.AddMinutes(20));Assert.Equal(Before.UtcTicks,Meta("expiry"));
        // Read over [Epoch, Before) is empty; after C(store): traffic, coverage, dedup and manifests below Before are gone; rows at or after Before stay; 'cleared-all' == Before ticks.
        var hidden=store.Read(new(Epoch,Before)); Assert.Empty(hidden.Rows); Assert.Empty(hidden.Coverage); Assert.Equal(0,hidden.Summary.ReceivedBytes);
        Assert.Equal(2,store.Read(Query(20)).TotalRows);
        C(store);
        Assert.Equal(0,Scalar($"SELECT count(*) FROM traffic WHERE minute<{Before.UtcTicks}")); Assert.Equal(0,Scalar($"SELECT count(*) FROM coverage WHERE start<{Before.UtcTicks}"));
        Assert.Equal(0,Scalar($"SELECT count(*) FROM dedup WHERE stamp<{Before.UtcTicks}")); Assert.Equal(0,Scalar("SELECT count(*) FROM manifests WHERE batch IN ('b1','b2','c1')"));
        Assert.Equal(2,Scalar("SELECT count(*) FROM traffic")); Assert.Equal(1,Scalar("SELECT count(*) FROM coverage")); Assert.Equal(3,Scalar("SELECT count(*) FROM manifests"));
        Assert.Equal(Before.UtcTicks,Meta("cleared-all")); Assert.Equal(0,ClearMarkers()); Assert.Null(store.PendingClear);
    }
    [Fact] public void All_apps_steps_delete_in_bounded_transactions_and_report_progress(){
        Assert.Null(new HistoryClearProgress(5,null,false).Percent); Assert.Equal(100,new HistoryClearProgress(3,10,true).Percent);
        Assert.Equal(37,new HistoryClearProgress(379,1000,false).Percent); Assert.Equal(99,new HistoryClearProgress(1000,1000,false).Percent);
        Assert.Equal(0,new HistoryClearProgress(0,0,false).Percent); Assert.Equal(99,new HistoryClearProgress(long.MaxValue,long.MaxValue,false).Percent);
        var store=Store(); var commits=new List<int>(); ClearSeam(store,"clearCommitted",(Action<int>)commits.Add);
        store.Append([..Enumerable.Range(0,1200).Select(i=>Row(i%10,"b"+i/100,generation:i+1))],[new("c","s",Epoch.AddMinutes(1),Epoch.AddMinutes(1).AddSeconds(15),true)],Epoch.AddMinutes(11));
        store.BeginClear(new(null,Before,Before)); store.BeginClear(new(null,Before,Before.AddMinutes(5)));
        Assert.Throws<InvalidOperationException>(()=>store.BeginClear(new(null,Before.AddMinutes(1),Before)));
        Assert.Throws<InvalidOperationException>(()=>store.BeginClear(new("A.EXE",Before,Before)));
        long t=0; ClearSeam(store,"clearClock",(Func<TimeSpan>)(()=>TimeSpan.FromMilliseconds(t++)));
        var first=store.ClearStep(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1201,first.Total); Assert.Equal(512,first.Done); Assert.False(first.Finished); Assert.Equal(42,first.Percent); Assert.Equal([512],commits);
        // The routine purge's deletes count toward Done.
        store.Append([],[],Epoch.AddMinutes(12),maintenanceTransactions:1);
        var second=store.ClearStep(TimeSpan.FromMilliseconds(1)); Assert.Equal(512+64+512,second.Done); Assert.Equal(1201,second.Total);
        var rest=store.ClearStep(TimeSpan.FromSeconds(30));
        Assert.True(rest.Finished); Assert.Equal(1201,rest.Done); Assert.Equal(100,rest.Percent); Assert.Equal([512,512,112,1,0],commits);
        Assert.Equal(0,Scalar("SELECT (SELECT count(*) FROM traffic)+(SELECT count(*) FROM coverage)+(SELECT count(*) FROM dedup)+(SELECT count(*) FROM manifests)"));
        Assert.Throws<InvalidOperationException>(()=>store.ClearStep(TimeSpan.FromMilliseconds(1)));
    }
    [Fact] public void Clear_marker_survives_reopen_and_resumes(){
        // BeginClear(one app) and one step with a 1 ms budget; PendingClear non-null; a new ConnectionHistoryStore on the file: Initialize passes (user_version 1), PendingClear equals
        // (AppKey, Before) of the request, and C(new store) finishes; PendingClear is null after the last step and non-null before it.
        var store=Store();
        store.Append([..Enumerable.Range(0,2000).Select(i=>Row(i%10,"b"+i/100,generation:i+1,name:i%2==0?"a.exe":"b.exe"))],[],Epoch.AddMinutes(11));
        Assert.Null(store.PendingClear);
        store.BeginClear(new("A.EXE",Before,Before));
        long t=0; ClearSeam(store,"clearClock",(Func<TimeSpan>)(()=>TimeSpan.FromMilliseconds(t++)));
        Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(1)).Finished); Assert.NotNull(store.PendingClear);
        Assert.Equal(1,ClearMarkers()); Assert.Equal(Before.UtcTicks,Meta("clear-app:A.EXE"));
        var reopened=new ConnectionHistoryStore(Database); var secure=new List<long>();
        ClearSeam(reopened,"clearConnectionOpened",(Action<SqliteConnection>)(db=>{using var command=db.CreateCommand(); command.CommandText="PRAGMA secure_delete"; secure.Add((long)command.ExecuteScalar()!);}));
        reopened.Initialize(); Assert.Equal(1,Scalar("PRAGMA user_version"));
        Assert.Equal(new PendingConnectionClear("A.EXE",Before),reopened.PendingClear);
        Assert.Equal(1000,reopened.Read(Query(11) with {Limit=500}).TotalRows);
        for(;;)
        {
            Assert.NotNull(reopened.PendingClear);
            if(reopened.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) break;
        }
        Assert.Null(reopened.PendingClear); Assert.NotEmpty(secure); Assert.All(secure,value=>Assert.Equal(1,value));
        Assert.Equal(0,Scalar("SELECT count(*) FROM traffic WHERE name='a.exe'")); Assert.Equal(1000,Scalar("SELECT count(*) FROM traffic WHERE name='b.exe'"));
        secure.Clear(); reopened.Append([],[],Epoch.AddMinutes(11)); Assert.Equal([0L],secure);
    }
    [Fact] public void Clear_steps_stay_inside_the_transaction_and_journal_budgets(){
        // 20,000 rows alternating "a.exe" and "b.exe" before Before. clearCommitted (seam) never sees more than 512. With no reader, the -wal is ≤ 8 MiB after each step and when read
        // inside clearCommitted.
        var store=Store(); var place=Place with {City=new string('c',160),Region=new string('r',160)};
        store.Append([..Enumerable.Range(0,20000).Select(i=>Row(i/2000,"b"+i/1000,generation:i+1,name:i%2==0?"a.exe":"b.exe") with {Location=place})],[],Epoch.AddMinutes(11));
        int largest=0, commits=0; long largestWal=0;
        ClearSeam(store,"clearCommitted",(Action<int>)(rows=>{largest=Math.Max(largest,rows); commits++; largestWal=Math.Max(largestWal,Wal());}));
        store.BeginClear(new("A.EXE",Before,Before));
        while(!store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) Assert.InRange(Wal(),0,8*1024*1024);
        Assert.InRange(largest,1,512); Assert.InRange(largestWal,0,8*1024*1024); Assert.Equal(0,Scalar("SELECT count(*) FROM traffic WHERE name='a.exe'"));
        testOutput.WriteLine($"No reader: {commits} clear transactions, largest {largest} rows, largest WAL {largestWal} bytes.");
        // A pinned reader (a second Pooling=false connection holding a read transaction): the step yields once the -wal passes 4 MiB and the -wal never exceeds 10 MiB; with clearClock
        // advanced past 60 s of yielding, ClearStep throws IOException, and not before. ClearStep(TimeSpan.FromMilliseconds(1)) commits exactly one transaction.
        store.BeginClear(new("B.EXE",Before,Before)); largest=0; largestWal=0; commits=0;
        var now=TimeSpan.Zero; ClearSeam(store,"clearClock",(Func<TimeSpan>)(()=>now));
        using(var pinned=Connect())
        using(var tx=pinned.BeginTransaction(deferred:true))
        {
            using var command=pinned.CreateCommand(); command.Transaction=tx; command.CommandText="SELECT count(*) FROM traffic"; _=command.ExecuteScalar();
            // The clock stands still, so only a yield ends this step.
            Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished);
            Assert.InRange(Wal(),4*1024*1024+1,10*1024*1024); Assert.InRange(largestWal,0,10*1024*1024); Assert.InRange(largest,1,512);
            testOutput.WriteLine($"Pinned reader: yielded after {commits} clear transactions at WAL {Wal()} bytes.");
            int before=commits;
            now=TimeSpan.FromSeconds(59); Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished); Assert.Equal(before,commits);
            now=TimeSpan.FromSeconds(61); Assert.Throws<IOException>(()=>store.ClearStep(TimeSpan.FromMilliseconds(250))); Assert.Equal(before,commits);
            Assert.NotNull(store.PendingClear);
            // Try again on the same instance starts a new 60 s yield window instead of failing at its first yield check.
            Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished); Assert.Equal(before,commits);
            now=TimeSpan.FromSeconds(120); Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished);
            now=TimeSpan.FromSeconds(121); Assert.Throws<IOException>(()=>store.ClearStep(TimeSpan.FromMilliseconds(250))); Assert.Equal(before,commits);
        }
        long t=0; ClearSeam(store,"clearClock",(Func<TimeSpan>)(()=>TimeSpan.FromMilliseconds(t++)));
        commits=0; Assert.False(store.ClearStep(TimeSpan.FromMilliseconds(1)).Finished); Assert.Equal(1,commits);
        var clock=System.Diagnostics.Stopwatch.StartNew(); ClearSeam(store,"clearClock",(Func<TimeSpan>)(()=>clock.Elapsed));
        C(store); Assert.Equal(0,Scalar("SELECT count(*) FROM traffic")); Assert.Null(store.PendingClear);
    }
}
