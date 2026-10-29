using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public sealed class ConnectionHistoryStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DeadWare-connections-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "connections.db");
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionLocation Place = new("US", "United States", "Illinois", "Chicago", new(41,-87), "fixture");
    private ConnectionHistoryStore Store(long cap = 268435456) { var store = new ConnectionHistoryStore(Database, cap); store.Initialize(); return store; }
    private static ConnectionHistoryRecord Row(int minute = 0, string batch = "b", long received = 10, long sent = 20, long generation = 1, string name = "browser", string address = "8.8.8.8") =>
        new(batch, "segment", new(Epoch.AddMinutes(minute), new(new(7,generation,name), "192.168.1.234", 49123, address, 443, ConnectionProtocol.Tcp), received, sent, Epoch.AddMinutes(minute).AddSeconds(1), Epoch.AddMinutes(minute).AddSeconds(2)), Place, new(40,-80), true);
    private static ConnectionHistoryQuery Query(int minutes = 10) => new(Epoch, Epoch.AddMinutes(minutes));
    private SqliteConnection Connect() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Database, Pooling=false, DefaultTimeout=1 }.ToString()); db.Open(); return db; }
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
    [Fact] public void Saturating_totals_remain_exact_above_double_precision_and_query_rates_use_full_interval()
    {
        var store=Store(); store.Append([Row(received:9007199254740993),Row(batch:"c",received:1)],[],Epoch.AddMinutes(1));
        var view=store.Read(new(Epoch,Epoch.AddMinutes(1))); Assert.Equal(9007199254740994,view.Summary.ReceivedBytes);
        store.Append([Row(batch:"d",received:long.MaxValue)],[],Epoch.AddMinutes(1)); view=store.Read(new(Epoch,Epoch.AddMinutes(1)));
        Assert.Equal(long.MaxValue,view.Summary.ReceivedBytes); Assert.Equal(long.MaxValue,Assert.Single(view.Rows).ReceivedBytes);
        Assert.Equal(long.MaxValue/60d,view.Rows[0].DownloadBytesPerSecond);
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
    [Fact] public void Markers_are_bounded_while_full_summaries_include_local_unlocated_and_every_country()
    {
        var store=Store(); var rows=Enumerable.Range(0,300).Select(i=>Row(i,"b"+i,address:"8.9."+(i/250)+"."+(i%250+1)) with {Location=Place with {City="City "+i,Point=new(i%90,i%180)}}).ToList();
        rows.Add(Row(300,"local",address:"192.168.3.5") with {Location=null}); rows.Add(Row(301,"unknown",address:"9.9.9.9") with {Location=null});
        store.Append(rows,[],Epoch.AddMinutes(302)); var view=store.Read(Query(302));
        Assert.Equal(256,view.Markers.Count); Assert.Equal(302,view.Summary.Destinations); Assert.Equal(1,view.Summary.Countries); Assert.Equal(2,view.Summary.Unlocated); Assert.Equal(1,view.Summary.Local); Assert.Equal(3020,view.Summary.ReceivedBytes);
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
    [Fact] public void Impossible_capacity_write_rolls_back_pruning_and_does_not_lose_known_rows()
    {
        var store=Store(65536); store.Append([Row(-1,"old")],[],Epoch);
        var large=Enumerable.Range(0,256).Select(i=>Row(0,"large",address:"8.9.1."+(i%250+1),generation:i+10,name:new string('x',260)) with {Location=Place with {City=new string('c',160),Region=new string('r',160),Country=new string('n',160),Source=new string('s',128)}}).ToArray();
        Assert.ThrowsAny<IOException>(()=>store.Append(large,[],Epoch.AddMinutes(1)));
        Assert.Equal(10,store.Read(new(Epoch.AddMinutes(-1),Epoch.AddMinutes(1))).Summary.ReceivedBytes);
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
}
