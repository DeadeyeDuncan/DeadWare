using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

/// <summary>Local minute history. Calls are serialized; perform I/O on a background owner.</summary>
public sealed class ConnectionHistoryStore
{
    public const long MaximumDatabaseBytes = 268435456;
    /// <summary>64 MiB of TEMP pages per grouped read, at the temp database's 4,096-byte pages.</summary>
    public const int MaximumGroupedTempPages = 16384;
    private const long MaximumJournalBytes = 8 * 1024 * 1024;
    private const int ApplicationId = 0x44435748;
    private const int ChunkSize = 256;
    private const int MaintenanceChunkSize = 64;
    private const int MaximumTransactionCacheBytes = 1536 * 1024;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private readonly int maximumGroupedTempPages;
    // Measurement only: set by reflection to time a read with the build's default temp_store.
    internal bool defaultTempStore = false;
    // Measurement only: set by reflection; Read reports the elapsed time of the Destinations steps 1–7.
    internal Action<TimeSpan>? destinationStepsTimed = null;
    private readonly object gate = new();
    private bool initialized;
    private const string Columns = "minute,pid,generation,name,remote,port,protocol,first_seen,last_seen,received,sent,location,origin,complete";
    // Every read path filters traffic through this one predicate: Grouped (ExportCsv) and both TEMP tables in Read.
    private const string GroupedWhere = """
        minute >= max($from,coalesce((SELECT value FROM metadata WHERE key='expiry'),0)) AND minute < $to
          AND minute > (SELECT value FROM metadata WHERE key='floor')
          AND ($search='' OR instr(search,$search)>0 OR instr(cast(pid AS TEXT),$search)>0)
          AND ($country='' OR country=$country)
          AND ($application='' OR app_key(name)=$application)
        """;
    private const string DirectionHaving = "($direction=0 OR ($direction=1 AND sat_sum(received)>0) OR ($direction=2 AND sat_sum(sent)>0))";
    private const string Grouped = $"""
        SELECT minute,pid,generation,name,remote,port,protocol,min(first_seen) first_seen,
          max(last_seen) last_seen,sat_sum(received) received,sat_sum(sent) sent,
          location,origin,min(complete) complete,country,kind
        FROM traffic
        WHERE {GroupedWhere}
        GROUP BY minute,pid,generation,name,remote,port,protocol,location,origin,country,kind
        HAVING {DirectionHaving}
        """;
    // The geo dictionary: ids follow BINARY (location, origin) order, so ordering grouped rows by id equals ordering them by location and origin.
    private const string GeoTable = """
        CREATE TEMP TABLE geo(id INTEGER PRIMARY KEY,location TEXT NOT NULL,origin TEXT NOT NULL,located INTEGER NOT NULL,UNIQUE(location,origin))
        """;
    private const string GeoInsert = $"""
        INSERT INTO temp.geo(id,location,origin,located)
        SELECT row_number() OVER (ORDER BY location,origin),location,origin,location<>'null' AND json_extract(location,'$.Point') IS NOT NULL
        FROM traffic WHERE {GroupedWhere} GROUP BY location,origin
        """;
    private const string GroupedTable = """
        CREATE TEMP TABLE grouped(row_index INTEGER PRIMARY KEY,minute INTEGER NOT NULL,pid INTEGER NOT NULL,generation INTEGER NOT NULL,
          name TEXT NOT NULL,remote TEXT NOT NULL,port INTEGER NOT NULL,protocol INTEGER NOT NULL,first_seen INTEGER NOT NULL,last_seen INTEGER NOT NULL,
          received INTEGER NOT NULL,sent INTEGER NOT NULL,geo INTEGER NOT NULL,complete INTEGER NOT NULL,country TEXT NOT NULL,kind INTEGER NOT NULL)
        """;
    private const string GroupedInsert = $"""
        INSERT INTO temp.grouped(row_index,minute,pid,generation,name,remote,port,protocol,first_seen,last_seen,received,sent,geo,complete,country,kind)
        SELECT row_number() OVER (ORDER BY minute DESC,pid,generation,name,remote,port,protocol,g.id)-1,
          minute,pid,generation,name,remote,port,protocol,min(first_seen),max(last_seen),sat_sum(received),sat_sum(sent),g.id,min(complete),country,kind
        FROM traffic JOIN temp.geo g ON g.location=traffic.location AND g.origin=traffic.origin
        WHERE {GroupedWhere}
        GROUP BY minute,pid,generation,name,remote,port,protocol,g.id,country,kind
        HAVING {DirectionHaving}
        """;
    // Columns' order over a grouped row g and its geo entry x, as ReadRow expects.
    private const string GroupedColumns = "g.minute,g.pid,g.generation,g.name,g.remote,g.port,g.protocol,g.first_seen,g.last_seen,g.received,g.sent,x.location,x.origin,g.complete";
    // The Destinations table's History steps over temp.grouped and temp.geo.
    private const string DestinationsTable = """
        CREATE TEMP TABLE destinations AS
        SELECT g.remote, g.kind,
               sat_sum(g.received) received, sat_sum(g.sent) sent,
               min(g.first_seen) first_seen, max(g.last_seen) last_seen,
               count(DISTINCT g.minute) minutes, min(g.complete) complete,
               count(DISTINCT app_key(g.name)) apps,
               count(DISTINCT g.port*2+g.protocol) ports,
               max(g.kind=0 AND x.located=0) unlocated,
               row_number() OVER (PARTITION BY g.kind
                 ORDER BY cast(sat_sum(g.received) AS REAL)+cast(sat_sum(g.sent) AS REAL) DESC, g.remote) rank
        FROM temp.grouped g JOIN temp.geo x ON x.id=g.geo
        GROUP BY g.remote, g.kind
        """;
    // The shared pass: app_key runs once per picked row. {0} selects the remotes to pick.
    private const string PickedTable = """
        CREATE TEMP TABLE picked AS
        SELECT g.remote, app_key(g.name) app, g.name, g.port, g.protocol,
               g.geo, g.minute, g.received, g.sent
        FROM temp.grouped g
        WHERE g.remote IN ({0})
        """;
    private const string Saturated = "CASE WHEN received>9223372036854775807-sent THEN 9223372036854775807 ELSE received+sent END";
    private const string DestinationColumns = "remote,kind,received,sent,first_seen,last_seen,minutes,complete,apps,ports,unlocated";
    private const string DestinationOrder = "cast(received AS REAL)+cast(sent AS REAL) DESC,remote";
    private const string Schema = """
        CREATE TABLE metadata(key TEXT PRIMARY KEY,value INTEGER NOT NULL);
        INSERT INTO metadata VALUES('floor',0),('expiry',0);
        CREATE TABLE manifests(batch TEXT NOT NULL,kind INTEGER NOT NULL,payload TEXT NOT NULL,
          PRIMARY KEY(batch,kind));
        CREATE TABLE dedup(batch TEXT NOT NULL,kind INTEGER NOT NULL,item TEXT NOT NULL,stamp INTEGER NOT NULL,
          PRIMARY KEY(batch,kind,item));
        CREATE INDEX dedup_time ON dedup(stamp);
        CREATE TABLE traffic(id INTEGER PRIMARY KEY,batch TEXT NOT NULL,item TEXT NOT NULL,
          minute INTEGER NOT NULL,pid INTEGER NOT NULL,generation INTEGER NOT NULL,name TEXT NOT NULL,
          remote TEXT NOT NULL,port INTEGER NOT NULL,protocol INTEGER NOT NULL,
          first_seen INTEGER NOT NULL,last_seen INTEGER NOT NULL,received INTEGER NOT NULL,sent INTEGER NOT NULL,
          location TEXT NOT NULL,origin TEXT NOT NULL,complete INTEGER NOT NULL,country TEXT NOT NULL,
          kind INTEGER NOT NULL,search TEXT NOT NULL);
        CREATE INDEX traffic_time ON traffic(minute);
        CREATE INDEX traffic_country_time ON traffic(country,minute);
        CREATE UNIQUE INDEX traffic_receipt ON traffic(batch,item);
        CREATE TABLE coverage(id INTEGER PRIMARY KEY,batch TEXT NOT NULL,item TEXT NOT NULL,
          stamp INTEGER NOT NULL,start INTEGER NOT NULL,finish INTEGER NOT NULL,complete INTEGER NOT NULL);
        CREATE INDEX coverage_time ON coverage(start,finish);
        CREATE UNIQUE INDEX coverage_receipt ON coverage(batch,item);
        """;

    public ConnectionHistoryStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes, int maximumGroupedTempPages = MaximumGroupedTempPages)
    {
        if(maximumDatabaseBytes is <65536 or >MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        if(maximumGroupedTempPages is <16 or >MaximumGroupedTempPages) throw new ArgumentOutOfRangeException(nameof(maximumGroupedTempPages));
        this.path=Path.GetFullPath(path); this.maximumDatabaseBytes=maximumDatabaseBytes; this.maximumGroupedTempPages=maximumGroupedTempPages;
    }
    public long StorageBytes => new[] {path,path+"-wal",path+"-shm"}.Where(File.Exists).Sum(file=>new FileInfo(file).Length);

    public void Initialize()
    {
        lock(gate)
        {
            // Inspect existing stores read-only before changing journal mode, schema or configuration.
            if(File.Exists(path))
            {
                using var inspect=Open(readOnly:true);
                if(Convert.ToString(Scalar(inspect,"PRAGMA integrity_check"),CultureInfo.InvariantCulture)!="ok")
                    throw new InvalidDataException("Connection history is corrupt; the file was preserved.");
                long version=Number(inspect,"PRAGMA user_version");
                if(version!=1 || Number(inspect,"PRAGMA application_id")!=ApplicationId) throw new InvalidDataException("Unsupported connection history; the file was preserved.");
                using var expected=new SqliteConnection("Data Source=:memory:"); expected.Open(); Execute(expected,Schema);
                if(!ReadSchema(inspect).SequenceEqual(ReadSchema(expected))) throw new InvalidDataException("Incompatible connection history objects; the file was preserved.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool create=!File.Exists(path);
            using var db=Open(create:create);
            Execute(db,"PRAGMA journal_mode=WAL");
            if(create)
            {
                using var tx=db.BeginTransaction(); Execute(db,Schema,tx);
                Execute(db,"PRAGMA application_id="+ApplicationId.ToString(CultureInfo.InvariantCulture)+"; PRAGMA user_version=1",tx); tx.Commit();
            }
            Checkpoint(db); initialized=true;
        }
    }

    private sealed record Prepared(ConnectionHistoryRecord Record,string Item,string Location,string Origin,string Country,int Kind,string Search);
    private sealed record Manifest(string Batch,int Kind,string Payload);
    private static string Identity(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static Prepared Prepare(ConnectionHistoryRecord record)
    {
        if(record is null || !Identifier(record.BatchId,192) || !Identifier(record.Segment,128) || record.Delta is null) throw Invalid();
        var delta=record.Delta; var peer=delta.Peer;
        if(peer is null || peer.Process is null || peer.Process.Pid<0 || peer.Process.Generation<0 || !Identifier(peer.Process.Name,260) ||
            peer.RemoteAddress is null || peer.RemoteAddress.Length>64 || !IPAddress.TryParse(peer.RemoteAddress,out var address) ||
            peer.RemotePort is <0 or >65535 || !Enum.IsDefined(peer.Protocol) || delta.ReceivedBytes<0 || delta.SentBytes<0 ||
            delta.Minute.UtcTicks%TimeSpan.TicksPerMinute!=0 || delta.FirstSeen>delta.LastSeen ||
            delta.FirstSeen<delta.Minute || delta.LastSeen>=delta.Minute.AddMinutes(1)) throw Invalid();
        ValidateLocation(record.Location); ValidatePoint(record.Origin);
        var normalized=NetworkAddress.Normalize(address).ToString();
        record=record with { Delta=delta with {Peer=peer with {RemoteAddress=normalized}}};
        string location=JsonSerializer.Serialize(record.Location), origin=JsonSerializer.Serialize(record.Origin);
        string item=Identity(new {record.Segment,minute=delta.Minute.UtcTicks,peer.Process,remote=normalized,peer.RemotePort,peer.Protocol,location,origin});
        string search=string.Join(' ',peer.Process.Name,normalized,record.Location?.CountryCode,record.Location?.Country,record.Location?.Region,record.Location?.City).ToLowerInvariant();
        return new(record,item,location,origin,record.Location?.CountryCode.ToUpperInvariant()??"",(int)NetworkAddress.Classify(address),search);
    }
    private static bool Identifier(string? value,int bound) => !string.IsNullOrWhiteSpace(value)&&value.Length<=bound;
    private static bool Text(string? value,int bound) => value is null || value.Length<=bound;
    private static ArgumentException Invalid() => new("Connection history contains an invalid or oversized value.");
    private static void ValidatePoint(GeoPoint? point)
    {
        if(point is not null && (!double.IsFinite(point.Latitude)||!double.IsFinite(point.Longitude)||point.Latitude is <-90 or >90||point.Longitude is <-180 or >180)) throw Invalid();
    }
    private static void ValidateLocation(ConnectionLocation? location)
    {
        if(location is null) return;
        if(!Text(location.CountryCode,2)||location.CountryCode is null||!Text(location.Country,160)||location.Country is null||
            !Text(location.Region,160)||!Text(location.City,160)||!Text(location.Source,128)||location.Source is null) throw Invalid();
        ValidatePoint(location.Point);
    }
    private static long Add(long left,long right) => left>long.MaxValue-right?long.MaxValue:left+right;
    private static long Minute(long ticks) => ticks-ticks%TimeSpan.TicksPerMinute;

    public void Append(IReadOnlyList<ConnectionHistoryRecord> records,IReadOnlyList<ConnectionCoverage> coverage,DateTimeOffset now)
    {
        lock(gate)
        {
            EnsureInitialized();
            if(records is null || coverage is null || records.Count>32768 || coverage.Count>32768) throw Invalid();
            // Validate everything before pruning or writing any chunk. Local endpoints never enter SQL or identity hashes.
            var prepared=records.Select(Prepare).GroupBy(x=>(x.Record.BatchId,x.Item)).Select(group=>
            {
                var first=group.First(); var delta=first.Record.Delta;
                foreach(var next in group.Skip(1)) delta=delta with {ReceivedBytes=Add(delta.ReceivedBytes,next.Record.Delta.ReceivedBytes),SentBytes=Add(delta.SentBytes,next.Record.Delta.SentBytes),FirstSeen=delta.FirstSeen<next.Record.Delta.FirstSeen?delta.FirstSeen:next.Record.Delta.FirstSeen,LastSeen=delta.LastSeen>next.Record.Delta.LastSeen?delta.LastSeen:next.Record.Delta.LastSeen};
                return first with {Record=first.Record with {Delta=delta,Complete=group.All(x=>x.Record.Complete)}};
            }).OrderBy(x=>x.Record.Delta.Minute).ToArray();
            foreach(var interval in coverage)
                if(interval is null || !Identifier(interval.BatchId,192)||!Identifier(interval.Segment,128)||interval.To<=interval.From||interval.To-interval.From>TimeSpan.FromSeconds(60)) throw Invalid();
            long cutoff=Math.Max(0,now.UtcTicks-TimeSpan.TicksPerDay*30L), latest=now.UtcTicks> DateTimeOffset.MaxValue.UtcTicks-TimeSpan.TicksPerSecond*2?DateTimeOffset.MaxValue.UtcTicks:now.UtcTicks+TimeSpan.TicksPerSecond*2;
            if(prepared.Any(x=>x.Record.Delta.LastSeen.UtcTicks>latest)||coverage.Any(x=>x.To.UtcTicks>latest)) throw Invalid();
            var coveragePieces=coverage.SelectMany(c=>
            {
                long boundary=Minute(c.From.UtcTicks)+TimeSpan.TicksPerMinute;
                return c.To.UtcTicks<=boundary ? new[]{c} : new[]{c with {To=Stamp(boundary)},c with {From=Stamp(boundary)}};
            }).GroupBy(c=>(c.BatchId,c.Segment,From:c.From.UtcTicks,To:c.To.UtcTicks))
                .Select(group=>group.First() with {Complete=group.All(c=>c.Complete)})
                .OrderBy(c=>c.From).ThenBy(c=>c.To).ThenBy(c=>c.Segment,StringComparer.Ordinal).ToArray();
            var manifests=prepared.GroupBy(x=>x.Record.BatchId).Select(group=>new Manifest(group.Key,0,
                Identity(group.OrderBy(x=>x.Item,StringComparer.Ordinal).Select(x=>new {x.Item,x.Record.Delta.ReceivedBytes,x.Record.Delta.SentBytes,first=x.Record.Delta.FirstSeen.UtcTicks,last=x.Record.Delta.LastSeen.UtcTicks,x.Record.Complete}).ToArray())))
                .Concat(coveragePieces.GroupBy(x=>x.BatchId).Select(group=>new Manifest(group.Key,1,Identity(group.Select(x=>new {x.Segment,from=x.From.UtcTicks,to=x.To.UtcTicks,x.Complete}).ToArray())))).ToArray();
            var manifestIndex=manifests.ToDictionary(x=>(x.Batch,x.Kind));
            long protectedMinute=prepared.Select(x=>x.Record.Delta.Minute.UtcTicks).Concat(coveragePieces.Select(c=>Minute(c.From.UtcTicks))).DefaultIfEmpty(long.MaxValue).Min();
            using var db=Open();
            ValidateManifests(db,manifests);
            GuardJournal(db);
            using(var expiry=db.BeginTransaction())
            {
                Execute(db,"INSERT INTO metadata VALUES('expiry',$cutoff) ON CONFLICT(key) DO UPDATE SET value=max(value,excluded.value)",expiry,("$cutoff",cutoff));
                CommitBounded(db,expiry);
            }
            Checkpoint(db); Cleanup(db);
            // Independent dedup keys let an exact retry finish after a later chunk failed, without duplicating earlier chunks.
            // Coverage is committed only after all traffic chunks succeed, so a failed traffic drain cannot claim coverage.
            for(int offset=0;offset<Math.Max(1,prepared.Length);offset+=ChunkSize)
            {
                var chunk=prepared.Skip(offset).Take(ChunkSize).ToArray();
                var chunkManifests=chunk.Select(x=>x.Record.BatchId).Distinct(StringComparer.Ordinal).Select(batch=>manifestIndex[(batch,0)]).ToArray();
                GuardJournal(db); WriteChunk(db,chunk,[],cutoff,chunkManifests,protectedMinute); Checkpoint(db);
            }
            for(int offset=0;offset<coveragePieces.Length;offset+=ChunkSize)
            {
                var chunk=coveragePieces.Skip(offset).Take(ChunkSize).ToArray();
                var chunkManifests=chunk.Select(x=>x.BatchId).Distinct(StringComparer.Ordinal).Select(batch=>manifestIndex[(batch,1)]).ToArray();
                GuardJournal(db); WriteChunk(db,[],chunk,cutoff,chunkManifests,protectedMinute); Checkpoint(db);
            }
            GuardJournal(db);
        }
    }
    private static void ValidateManifests(SqliteConnection db,IEnumerable<Manifest> manifests,SqliteTransaction? tx=null)
    {
        foreach(var manifest in manifests)
        {
            using var command=Command(db,"SELECT payload FROM manifests WHERE batch=$batch AND kind=$kind",tx,("$batch",manifest.Batch),("$kind",manifest.Kind));
            if(command.ExecuteScalar() is string existing && existing!=manifest.Payload) throw new ArgumentException("A connection history BatchId was reused with a changed payload.");
        }
    }
    private static IReadOnlyList<string> ReadSchema(SqliteConnection db)
    {
        using var command=Command(db,"SELECT type||':'||name||':'||sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name");
        using var reader=command.ExecuteReader(); var result=new List<string>();
        while(reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    private void WriteChunk(SqliteConnection db,Prepared[] records,ConnectionCoverage[] coverage,long cutoff,Manifest[] manifests,long protectedMinute)
    {
        for(;;)
        {
            try
            {
                using var tx=db.BeginTransaction();
                ValidateManifests(db,manifests,tx);
                long retainedFloor=Convert.ToInt64(Scalar(db,"SELECT value FROM metadata WHERE key='floor'",tx),CultureInfo.InvariantCulture);
                foreach(var item in records)
                {
                    var r=item.Record; var d=r.Delta; var p=d.Peer;
                    if(d.Minute.UtcTicks<cutoff||d.Minute.UtcTicks<=retainedFloor) continue;
                    if(Execute(db,"INSERT OR IGNORE INTO dedup VALUES($batch,0,$item,$stamp)",tx,("$batch",r.BatchId),("$item",item.Item),("$stamp",d.Minute.UtcTicks))==0) continue;
                    Execute(db,"""
                        INSERT INTO traffic(batch,item,minute,pid,generation,name,remote,port,protocol,first_seen,last_seen,received,sent,location,origin,complete,country,kind,search)
                        VALUES($batch,$item,$minute,$pid,$generation,$name,$remote,$port,$protocol,$first,$last,$received,$sent,$location,$origin,$complete,$country,$kind,$search)
                        """,tx,("$batch",r.BatchId),("$item",item.Item),("$minute",d.Minute.UtcTicks),("$pid",p.Process.Pid),("$generation",p.Process.Generation),("$name",p.Process.Name),("$remote",p.RemoteAddress),("$port",p.RemotePort),("$protocol",(int)p.Protocol),("$first",d.FirstSeen.UtcTicks),("$last",d.LastSeen.UtcTicks),("$received",d.ReceivedBytes),("$sent",d.SentBytes),("$location",item.Location),("$origin",item.Origin),("$complete",r.Complete?1:0),("$country",item.Country),("$kind",item.Kind),("$search",item.Search));
                }
                foreach(var c in coverage)
                {
                    long start=Math.Max(c.From.UtcTicks,cutoff), finish=c.To.UtcTicks, stamp=Minute(start);
                    if(finish<=start||stamp<=retainedFloor) continue;
                    string identity=Identity(new{c.Segment,from=c.From.UtcTicks,to=c.To.UtcTicks});
                    if(Execute(db,"INSERT OR IGNORE INTO dedup VALUES($batch,1,$item,$stamp)",tx,("$batch",c.BatchId),("$item",identity),("$stamp",stamp))==0) continue;
                    Execute(db,"INSERT INTO coverage(batch,item,stamp,start,finish,complete) VALUES($batch,$item,$stamp,$from,$to,$complete)",tx,("$batch",c.BatchId),("$item",identity),("$stamp",stamp),("$from",start),("$to",finish),("$complete",c.Complete?1:0));
                }
                foreach(var manifest in manifests)
                    Execute(db,"INSERT OR IGNORE INTO manifests SELECT $batch,$kind,$payload WHERE EXISTS(SELECT 1 FROM dedup WHERE batch=$batch AND kind=$kind)",tx,("$batch",manifest.Batch),("$kind",manifest.Kind),("$payload",manifest.Payload));
                CommitBounded(db,tx); return;
            }
            catch(SqliteException error) when(error.SqliteErrorCode==13)
            {
                if(!EvictOldestBucket(db,protectedMinute)) throw new IOException("The incoming minute cannot fit in connection history; its earlier committed chunks were preserved.",error);
                Cleanup(db);
            }
            catch(TransactionBudgetException error)
            {
                if(records.Length+coverage.Length<=1) throw new IOException("A connection history item exceeds the bounded transaction budget.",error);
                if(records.Length>0)
                {
                    int half=Math.Max(1,records.Length/2);
                    WriteChunk(db,records[..half],[],cutoff,manifests,protectedMinute); Checkpoint(db); GuardJournal(db);
                    WriteChunk(db,records[half..],[],cutoff,manifests,protectedMinute);
                }
                else
                {
                    int half=Math.Max(1,coverage.Length/2);
                    WriteChunk(db,[],coverage[..half],cutoff,manifests,protectedMinute); Checkpoint(db); GuardJournal(db);
                    WriteChunk(db,[],coverage[half..],cutoff,manifests,protectedMinute);
                }
                return;
            }
        }
    }

    private sealed class TransactionBudgetException : Exception;
    private static void CommitBounded(SqliteConnection db,SqliteTransaction tx)
    {
        // With cache_spill disabled, dirty pages stay in this private cache until commit.
        // Its total size bounds dirty page frames, including index/freelist maintenance.
        int result=SQLitePCL.raw.sqlite3_db_status(db.Handle,SQLitePCL.raw.SQLITE_DBSTATUS_CACHE_USED,out int used,out _,0);
        if(result!=SQLitePCL.raw.SQLITE_OK) throw new IOException("Could not verify the connection history transaction budget.");
        if(used>MaximumTransactionCacheBytes) throw new TransactionBudgetException();
        tx.Commit();
    }
    private bool EvictOldestBucket(SqliteConnection db,long protectedMinute)
    {
        GuardJournal(db);
        using var tx=db.BeginTransaction();
        using var choose=Command(db,"""
            SELECT min(stamp) FROM (
              SELECT min(minute) stamp FROM traffic
                WHERE minute>(SELECT value FROM metadata WHERE key='floor') AND minute<$protected
                  AND minute>=coalesce((SELECT value FROM metadata WHERE key='expiry'),0)
              UNION ALL SELECT min(stamp) FROM coverage
                WHERE start>=(SELECT value FROM metadata WHERE key='floor')+$minute AND start<$protected
                  AND finish>coalesce((SELECT value FROM metadata WHERE key='expiry'),0)
            )
            """,tx,("$protected",protectedMinute),("$minute",TimeSpan.TicksPerMinute));
        object? value=choose.ExecuteScalar(); if(value is null or DBNull) return false;
        // Intentional old-bucket eviction is durable even when later physical reclamation is blocked.
        Execute(db,"UPDATE metadata SET value=$floor WHERE key='floor'",tx,("$floor",Convert.ToInt64(value,CultureInfo.InvariantCulture)));
        CommitBounded(db,tx); Checkpoint(db); GuardJournal(db); return true;
    }
    private sealed record Receipt(long Id,string Batch,string Item,int Kind);
    private void Cleanup(SqliteConnection db)
    {
        int count=MaintenanceChunkSize;
        for(;;)
        {
            GuardJournal(db);
            try
            {
                using var tx=db.BeginTransaction();
                var receipts=new List<Receipt>();
                using(var select=Command(db,"""
                    SELECT id,batch,item FROM traffic
                    WHERE minute<max(coalesce((SELECT value FROM metadata WHERE key='expiry'),0),
                        (SELECT value FROM metadata WHERE key='floor')+$minute)
                    ORDER BY minute,id LIMIT $count
                    """,tx,("$count",count),("$minute",TimeSpan.TicksPerMinute)))
                using(var reader=select.ExecuteReader()) while(reader.Read()) receipts.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),0));
                if(receipts.Count==0)
                {
                    using var select=Command(db,"""
                        SELECT id,batch,item FROM coverage
                        WHERE start<max(coalesce((SELECT value FROM metadata WHERE key='expiry'),0),
                            (SELECT value FROM metadata WHERE key='floor')+$minute)
                          AND (finish<=coalesce((SELECT value FROM metadata WHERE key='expiry'),0)
                            OR stamp<=(SELECT value FROM metadata WHERE key='floor'))
                        ORDER BY start,id LIMIT $count
                        """,tx,("$count",count),("$minute",TimeSpan.TicksPerMinute));
                    using var reader=select.ExecuteReader(); while(reader.Read()) receipts.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),1));
                }
                if(receipts.Count==0) return;
                foreach(var receipt in receipts)
                {
                    Execute(db,receipt.Kind==0?"DELETE FROM traffic WHERE id=$id":"DELETE FROM coverage WHERE id=$id",tx,("$id",receipt.Id));
                    Execute(db,"DELETE FROM dedup WHERE batch=$batch AND kind=$kind AND item=$item",tx,("$batch",receipt.Batch),("$kind",receipt.Kind),("$item",receipt.Item));
                }
                foreach(var key in receipts.Select(r=>(r.Batch,r.Kind)).Distinct())
                    Execute(db,"DELETE FROM manifests WHERE batch=$batch AND kind=$kind AND NOT EXISTS(SELECT 1 FROM dedup WHERE batch=$batch AND kind=$kind)",tx,("$batch",key.Batch),("$kind",key.Kind));
                CommitBounded(db,tx);
            }
            catch(TransactionBudgetException error)
            {
                if(count==1) throw new IOException("Connection history maintenance exceeds the bounded transaction budget.",error);
                count=Math.Max(1,count/2); continue;
            }
            Checkpoint(db); GuardJournal(db);
        }
    }

    public ConnectionHistoryView Read(ConnectionHistoryQuery query)
    {
        lock(gate)
        {
            EnsureInitialized(); query=Normalize(query);
            // Group the interval once; the page, summary and markers all read the same TEMP snapshot.
            using var db=OpenGrouped(query,out var transaction); using var tx=transaction;
            var rows=Grouping(()=>
            {
                var page=new List<ConnectionHistoryRow>();
                using var command=Command(db,$"SELECT {GroupedColumns} FROM temp.grouped g JOIN temp.geo x ON x.id=g.geo WHERE row_index>=$offset ORDER BY row_index LIMIT $limit",tx,("$offset",query.Offset),("$limit",query.Limit));
                using var reader=command.ExecuteReader(); while(reader.Read()) page.Add(ReadRow(reader));
                return page;
            });
            var (total,summary)=Grouping(()=>
            {
                // Unlocated counts Public remotes without a point only, as Live does; Local and Special remotes are never unlocated.
                using var command=Command(db,"""
                    SELECT count(*),sat_sum(received),sat_sum(sent),count(DISTINCT nullif(country,'')),count(DISTINCT remote),
                      count(DISTINCT CASE WHEN kind=0 AND x.located=0 THEN remote END),count(DISTINCT CASE WHEN kind=1 THEN remote END)
                    FROM temp.grouped g JOIN temp.geo x ON x.id=g.geo
                    """,tx);
                using var reader=command.ExecuteReader(); reader.Read();
                return (reader.GetInt64(0),new ConnectionHistorySummary(Value(reader,1),Value(reader,2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6)));
            });
            // Rank the same complete grouping used by the table; only the bounded representatives cross the storage/UI boundary.
            var (markers,markerCount)=Grouping(()=>
            {
                var found=new List<ConnectionHistoryMarker>(); long count=0;
                using var command=Command(db,$"""
                    WITH groups AS (
                      SELECT geo,sat_sum(received) total_received,sat_sum(sent) total_sent,min(row_index) representative
                      FROM temp.grouped WHERE geo IN (SELECT id FROM temp.geo WHERE located=1) GROUP BY geo),
                    markers AS (
                      SELECT *,count(*) OVER () marker_count FROM groups
                      ORDER BY (cast(total_received AS REAL)+cast(total_sent AS REAL)) DESC,geo LIMIT 256)
                    SELECT {GroupedColumns},m.total_received,m.total_sent,m.representative,m.marker_count
                    FROM markers m JOIN temp.grouped g ON g.row_index=m.representative JOIN temp.geo x ON x.id=g.geo
                    ORDER BY (cast(m.total_received AS REAL)+cast(m.total_sent AS REAL)) DESC,m.geo
                    """,tx);
                using var reader=command.ExecuteReader(); while(reader.Read())
                {
                    var representative=ReadRow(reader); count=reader.GetInt64(17);
                    found.Add(new(representative.Location!,representative.Origin,reader.GetInt64(14),reader.GetInt64(15),representative,reader.GetInt64(16)));
                }
                return (found,count);
            });
            var (coverage,gaps)=ReadCoverage(db,tx,query);
            object? earliest=Scalar(db,"""
                SELECT min(stamp) FROM (
                  SELECT min(minute) stamp FROM traffic
                    WHERE minute>=coalesce((SELECT value FROM metadata WHERE key='expiry'),0)
                      AND minute>(SELECT value FROM metadata WHERE key='floor')
                  UNION ALL SELECT min(max(start,coalesce((SELECT value FROM metadata WHERE key='expiry'),0))) FROM coverage
                    WHERE finish>coalesce((SELECT value FROM metadata WHERE key='expiry'),0)
                      AND stamp>(SELECT value FROM metadata WHERE key='floor'))
                """,tx);
            var strip=Grouping(()=>ReadStrip(db,tx,query));
            long started=Stopwatch.GetTimestamp();
            var destinations=Grouping(()=>ReadDestinations(db,tx));
            destinationStepsTimed?.Invoke(Stopwatch.GetElapsedTime(started));
            string status=strip.CapacityShortened?"Capacity shortened retention. ":"";
            if(gaps.Count>0||coverage.Any(x=>!x.Complete)) status+="History contains recording gaps.";
            else status+="Recorded coverage available.";
            return new(query,rows.AsReadOnly(),total,summary,markers.AsReadOnly(),coverage.AsReadOnly(),gaps.AsReadOnly(),earliest is null or DBNull?null:Stamp(Convert.ToInt64(earliest,CultureInfo.InvariantCulture)),StorageBytes,status,Math.Max(0,markerCount-markers.Count),
                Strip:strip,Destinations:destinations);
        }
    }
    private sealed class StripSlot(int series)
    {
        public readonly long[] Received=new long[series], Sent=new long[series];
        public readonly HashSet<long> Minutes=[];
        public long CoveredTicks;
        public bool Incomplete;
    }
    /// <summary>The traffic strip over temp.grouped and coverage, in the read's transaction.</summary>
    private static TrafficStrip ReadStrip(SqliteConnection db,SqliteTransaction tx,ConnectionHistoryQuery query)
    {
        var measure=query.Direction switch {ConnectionDirection.Received=>TrafficMeasure.Received,ConnectionDirection.Sent=>TrafficMeasure.Sent,_=>TrafficMeasure.Total};
        long Measure(long received,long sent) => measure switch {TrafficMeasure.Received=>received,TrafficMeasure.Sent=>sent,_=>Add(received,sent)};
        // 1. Grid.
        var origin=TrafficStrips.CeilingMinute(query.From);
        long bucket=TrafficStrips.ConnectionsBucketMinutes(query.From,query.To)*TimeSpan.TicksPerMinute;
        int slotCount=TrafficStrips.ConnectionsSlotCount(query.From,query.To);
        // 2. Totals by stored name, folded to app keys here so app_key never runs per row.
        var keys=new Dictionary<string,string>(StringComparer.Ordinal);
        var totals=new Dictionary<string,(string Name,long Received,long Sent)>(StringComparer.Ordinal);
        using(var command=Command(db,"SELECT name,sat_sum(received),sat_sum(sent) FROM temp.grouped GROUP BY name",tx))
        using(var reader=command.ExecuteReader()) while(reader.Read())
        {
            string name=reader.GetString(0), key=ProcessGrouping.Key(name); keys[name]=key;
            totals[key]=totals.TryGetValue(key,out var total)
                ? (string.CompareOrdinal(name,total.Name)<0?name:total.Name,Add(total.Received,reader.GetInt64(1)),Add(total.Sent,reader.GetInt64(2)))
                : (name,reader.GetInt64(1),reader.GetInt64(2));
        }
        var applications=totals.Select(x=>(x.Key,x.Value,Measure:Measure(x.Value.Received,x.Value.Sent))).Where(x=>x.Measure>0)
            .OrderByDescending(x=>x.Measure).ThenBy(x=>x.Key,StringComparer.Ordinal).Take(TrafficStrips.MaximumApplications)
            .Select(x=>new TrafficStripApplication(x.Value.Name,x.Key,x.Value.Received,x.Value.Sent)).ToList();
        var series=applications.Select((a,i)=>(a.Key,i)).ToDictionary(x=>x.Key,x=>x.i,StringComparer.Ordinal);
        long otherReceived=0,otherSent=0,otherApplications=0;
        foreach(var (key,total) in totals)
        {
            if(series.ContainsKey(key)) continue;
            otherReceived=Add(otherReceived,total.Received); otherSent=Add(otherSent,total.Sent);
            if(total.Received>0||total.Sent>0) otherApplications++;
        }
        // 4. Retained start, from floor and expiry only.
        long floor,expiry;
        using(var command=Command(db,"SELECT (SELECT value FROM metadata WHERE key='floor'),coalesce((SELECT value FROM metadata WHERE key='expiry'),0)",tx))
        using(var reader=command.ExecuteReader()) {reader.Read(); floor=reader.GetInt64(0); expiry=reader.GetInt64(1);}
        long expiryMinute=TrafficStrips.CeilingMinute(Stamp(expiry)).UtcTicks;
        long retained=Math.Max(expiryMinute,floor+TimeSpan.TicksPerMinute);
        var slots=new Dictionary<long,StripSlot>();
        StripSlot SlotAt(long index) {if(!slots.TryGetValue(index,out var slot)) slots[index]=slot=new(applications.Count+1); return slot;}
        StripSlot SlotOf(long minute) => SlotAt((minute-origin.UtcTicks)/bucket);
        if(slotCount>0)
        {
            // 3. Slot bytes by stored name.
            using(var command=Command(db,"SELECT (minute-$origin)/$bucket,name,sat_sum(received),sat_sum(sent),min(complete) FROM temp.grouped GROUP BY 1,name",tx,("$origin",origin.UtcTicks),("$bucket",bucket)))
            using(var reader=command.ExecuteReader()) while(reader.Read())
            {
                var slot=SlotAt(reader.GetInt64(0));
                int at=series.TryGetValue(keys[reader.GetString(1)],out int found)?found:applications.Count;
                slot.Received[at]=Add(slot.Received[at],reader.GetInt64(2)); slot.Sent[at]=Add(slot.Sent[at],reader.GetInt64(3));
                if(reader.GetInt64(4)==0) slot.Incomplete=true;
            }
            // 5. Recorded time per minute: coverage pieces clipped to the interval, capped at a minute.
            var covered=new HashSet<long>();
            using(var command=Command(db,"""
                SELECT stamp,sum(min(finish,$to)-max(start,$from)),min(complete) FROM coverage
                WHERE stamp>$floor AND stamp>=$retained AND stamp>=$origin AND finish>$from AND start<$to GROUP BY stamp
                """,tx,("$floor",floor),("$retained",retained),("$origin",origin.UtcTicks),("$from",query.From.UtcTicks),("$to",query.To.UtcTicks)))
            using(var reader=command.ExecuteReader()) while(reader.Read())
            {
                long minute=reader.GetInt64(0); var slot=SlotOf(minute); covered.Add(minute); slot.Minutes.Add(minute);
                slot.CoveredTicks+=Math.Min(reader.GetInt64(1),TimeSpan.TicksPerMinute);
                if(reader.GetInt64(2)==0) slot.Incomplete=true;
            }
            // A minute with rows but no coverage counts as a whole, incomplete minute.
            using(var command=Command(db,"SELECT minute FROM temp.grouped GROUP BY minute",tx))
            using(var reader=command.ExecuteReader()) while(reader.Read())
            {
                long minute=reader.GetInt64(0); var slot=SlotOf(minute); slot.Minutes.Add(minute);
                if(covered.Contains(minute)) continue;
                slot.CoveredTicks+=TimeSpan.TicksPerMinute; slot.Incomplete=true;
            }
        }
        // 6. Slot fields; slots without a recorded minute are left out.
        var listed=slots.Where(x=>x.Value.Minutes.Count>0).OrderBy(x=>x.Key).Select(x=>
        {
            var (start,end)=TrafficStrips.ConnectionsSlot(query.From,query.To,x.Key);
            return new TrafficStripSlot(start,end,x.Value.Minutes.Count,x.Value.CoveredTicks/TimeSpan.TicksPerSecond,x.Value.Incomplete,x.Value.Received.AsReadOnly(),x.Value.Sent.AsReadOnly());
        }).ToList();
        // 7. and 8.
        DateTimeOffset? retainedFrom=retained>origin.UtcTicks?Stamp(retained):null;
        bool capacityShortened=floor>0&&floor+TimeSpan.TicksPerMinute>expiryMinute;
        return new(query.From,query.To,bucket/TimeSpan.TicksPerMillisecond,measure,applications.AsReadOnly(),otherReceived,otherSent,otherApplications,listed.AsReadOnly(),retainedFrom,capacityShortened);
    }
    private sealed record DestinationTotals(string Remote,int Kind,long Received,long Sent,long FirstSeen,long LastSeen,long Minutes,bool Complete,int Apps,int Ports,bool Unlocated);
    private sealed record DestinationLists(Dictionary<string,List<ConnectionDestinationApp>> Apps,Dictionary<string,List<ConnectionDestinationPort>> Ports,Dictionary<string,List<ConnectionLocation>> Locations);
    /// <summary>The Destinations table's steps 1–7 over temp.grouped, in the read's transaction.</summary>
    private static ConnectionDestinationList ReadDestinations(SqliteConnection db,SqliteTransaction tx)
    {
        // 1. One row per remote with uncapped totals and its rank within its kind.
        Execute(db,DestinationsTable,tx);
        // 2. Exact counts.
        ConnectionDestinationCounts counts;
        using(var command=Command(db,"SELECT count(*),sum(kind=0),sum(kind=1),sum(kind=2),sum(unlocated) FROM temp.destinations",tx))
        using(var reader=command.ExecuteReader()) {reader.Read(); counts=new(reader.GetInt64(0),Value(reader,1),Value(reader,2),Value(reader,3),Value(reader,4));}
        // 3. Each kind's top 1,000, at most 3,000 rows.
        var totals=ReadTotals(db,tx,$"SELECT {DestinationColumns} FROM temp.destinations WHERE rank<=$rank ORDER BY {DestinationOrder}",("$rank",ConnectionDestinations.MaximumRowsPerScope));
        // 4. The shared pass over temp.grouped for those rows; 5.–7. read only temp.picked.
        Execute(db,string.Format(CultureInfo.InvariantCulture,PickedTable,"SELECT remote FROM temp.destinations WHERE rank<=$rank"),tx,("$rank",ConnectionDestinations.MaximumRowsPerScope));
        var lists=ReadLists(db,tx,ConnectionDestinations.MaximumApps,ConnectionDestinations.MaximumPorts,ConnectionDestinations.MaximumPoints);
        return new(totals.Select(t=>Destination(t,lists)).ToList().AsReadOnly(),counts);
    }
    private static List<DestinationTotals> ReadTotals(SqliteConnection db,SqliteTransaction tx,string sql,params (string,object)[] args)
    {
        var totals=new List<DestinationTotals>();
        using var command=Command(db,sql,tx,args);
        using var reader=command.ExecuteReader();
        while(reader.Read())
            totals.Add(new(reader.GetString(0),reader.GetInt32(1),reader.GetInt64(2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6),
                reader.GetInt64(7)==1,reader.GetInt32(8),reader.GetInt32(9),reader.GetInt64(10)==1));
        return totals;
    }
    /// <summary>Steps 5–7 over temp.picked: the busiest apps and ports and the latest locations of every picked remote.</summary>
    private static DestinationLists ReadLists(SqliteConnection db,SqliteTransaction tx,int apps,int ports,int locations)
    {
        var lists=new DestinationLists(new(StringComparer.Ordinal),new(StringComparer.Ordinal),new(StringComparer.Ordinal));
        static List<T> At<T>(Dictionary<string,List<T>> map,string remote) {if(!map.TryGetValue(remote,out var list)) map[remote]=list=[]; return list;}
        // 5. Apps: the latest minute's name, ties the lower BINARY name; busiest first, ties by key.
        using(var command=Command(db,$"""
            WITH totals AS (SELECT remote,app,sat_sum(received) received,sat_sum(sent) sent FROM temp.picked GROUP BY remote,app),
            named AS (SELECT remote,app,name FROM (
              SELECT remote,app,name,row_number() OVER (PARTITION BY remote,app ORDER BY minute DESC,name) n FROM temp.picked) WHERE n=1),
            ranked AS (SELECT remote,app,received,sent,row_number() OVER (PARTITION BY remote ORDER BY {Saturated} DESC,app) r FROM totals)
            SELECT r.remote,n.name,r.received,r.sent FROM ranked r JOIN named n ON n.remote=r.remote AND n.app=r.app
            WHERE r.r<=$cap ORDER BY r.remote,r.r
            """,tx,("$cap",apps)))
        using(var reader=command.ExecuteReader()) while(reader.Read())
            At(lists.Apps,reader.GetString(0)).Add(new(UsageMetrics.NormalizeName(reader.GetString(1)),reader.GetInt64(2),reader.GetInt64(3)));
        // 6. Ports: busiest first, ties by port then protocol.
        using(var command=Command(db,$"""
            SELECT remote,port,protocol,received,sent FROM (
              SELECT remote,port,protocol,received,sent,row_number() OVER (PARTITION BY remote ORDER BY {Saturated} DESC,port,protocol) r FROM (
                SELECT remote,port,protocol,sat_sum(received) received,sat_sum(sent) sent FROM temp.picked GROUP BY remote,port,protocol))
            WHERE r<=$cap ORDER BY remote,r
            """,tx,("$cap",ports)))
        using(var reader=command.ExecuteReader()) while(reader.Read())
            At(lists.Ports,reader.GetString(0)).Add(new(reader.GetInt32(1),(ConnectionProtocol)reader.GetInt32(2),reader.GetInt64(3),reader.GetInt64(4)));
        // 7. Saved locations, latest first, ties the lower location text.
        using(var command=Command(db,"""
            SELECT remote,location FROM (
              SELECT remote,location,row_number() OVER (PARTITION BY remote ORDER BY latest DESC,location) r FROM (
                SELECT p.remote,x.location,max(p.minute) latest FROM temp.picked p JOIN temp.geo x ON x.id=p.geo
                WHERE x.location<>'null' GROUP BY p.remote,x.location))
            WHERE r<=$cap ORDER BY remote,r
            """,tx,("$cap",locations)))
        using(var reader=command.ExecuteReader()) while(reader.Read())
            At(lists.Locations,reader.GetString(0)).Add(JsonSerializer.Deserialize<ConnectionLocation>(reader.GetString(1))!);
        return lists;
    }
    private static ConnectionDestination Destination(DestinationTotals t,DestinationLists lists)
    {
        var locations=lists.Locations.GetValueOrDefault(t.Remote)??[];
        var points=locations.Select(l=>l.Point).OfType<GeoPoint>().Where(ConnectionProjection.IsValid).Distinct().ToList();
        return new(t.Remote,new((NetworkAddressKind)t.Kind,NetworkAddress.Describe(t.Remote).Label),locations.FirstOrDefault(),points.AsReadOnly(),t.Unlocated,
            (lists.Apps.GetValueOrDefault(t.Remote)??[]).AsReadOnly(),t.Apps,(lists.Ports.GetValueOrDefault(t.Remote)??[]).AsReadOnly(),t.Ports,t.Received,t.Sent,
            null,null,null,t.Minutes,Stamp(t.FirstSeen),Stamp(t.LastSeen),t.Complete);
    }
    /// <summary>
    /// Opens a read-only connection with the grouped TEMP limit, begins one deferred transaction and builds temp.geo and
    /// temp.grouped for <paramref name="query"/>. Disposing the unpooled connection drops the TEMP tables.
    /// </summary>
    private SqliteConnection OpenGrouped(ConnectionHistoryQuery query,out SqliteTransaction tx)
    {
        var db=Open(readOnly:true); SqliteTransaction? transaction=null;
        try
        {
            // temp_store goes first: changing it discards the temp database.
            if(!defaultTempStore) Execute(db,"PRAGMA temp_store=MEMORY");
            Execute(db,"PRAGMA temp.max_page_count="+maximumGroupedTempPages.ToString(CultureInfo.InvariantCulture));
            var t=transaction=db.BeginTransaction(deferred:true);
            var args=Arguments(query);
            Grouping(()=>Execute(db,GeoTable,t));
            Grouping(()=>Execute(db,GeoInsert,t,args));
            Grouping(()=>Execute(db,GroupedTable,t));
            Grouping(()=>Execute(db,GroupedInsert,t,args));
            tx=t; return db;
        }
        catch {transaction?.Dispose(); db.Dispose(); throw;}
    }
    /// <summary>Runs a statement over the TEMP tables; SQLITE_FULL there means the interval exceeds the grouped TEMP limit.</summary>
    private static T Grouping<T>(Func<T> statement)
    {
        try {return statement();}
        catch(SqliteException error) when(error.SqliteErrorCode==13) {throw new ConnectionHistoryLimitException(error);}
    }
    private static long Value(SqliteDataReader reader,int ordinal) => reader.IsDBNull(ordinal)?0:reader.GetInt64(ordinal);
    private static DateTimeOffset Stamp(long ticks) => new(ticks,TimeSpan.Zero);
    private static ConnectionHistoryRow ReadRow(SqliteDataReader r)
    {
        // Rows represent whole minute buckets selected by their start, even for sub-minute queries.
        double seconds=60;
        return new(Stamp(r.GetInt64(0)),new(r.GetInt32(1),r.GetInt64(2),r.GetString(3)),r.GetString(4),r.GetInt32(5),(ConnectionProtocol)r.GetInt32(6),Stamp(r.GetInt64(7)),Stamp(r.GetInt64(8)),r.GetInt64(9),r.GetInt64(10),r.GetInt64(9)/seconds,r.GetInt64(10)/seconds,JsonSerializer.Deserialize<ConnectionLocation>(r.GetString(11)),JsonSerializer.Deserialize<GeoPoint>(r.GetString(12)),r.GetInt32(13)!=0);
    }
    private static ConnectionHistoryQuery Normalize(ConnectionHistoryQuery query)
    {
        if(query is null||query.To<=query.From||query.Search is null||query.Search.Length>260||query.CountryCode is null||query.CountryCode.Length>2||!Enum.IsDefined(query.Direction)||
            query.Application is null||query.Application.Length>260) throw Invalid();
        string application=query.Application.Trim();
        return query with {From=query.From.ToUniversalTime(),To=query.To.ToUniversalTime(),Search=query.Search.Trim().ToLowerInvariant(),CountryCode=query.CountryCode.Trim().ToUpperInvariant(),Offset=Math.Max(0,query.Offset),Limit=Math.Clamp(query.Limit,1,500),
            Application=application.Length==0?"":ProcessGrouping.Key(application)};
    }
    private static (string,object)[] Arguments(ConnectionHistoryQuery query) => [("$from",query.From.UtcTicks),("$to",query.To.UtcTicks),("$search",query.Search),("$country",query.CountryCode),("$direction",(int)query.Direction),("$application",query.Application)];
    private static (List<ConnectionHistoryInterval>,List<ConnectionHistoryGap>) ReadCoverage(SqliteConnection db,SqliteTransaction tx,ConnectionHistoryQuery query)
    {
        // Sweep interval edges in SQL order; memory is proportional to the published merged result, not raw samples.
        using var command=Command(db,"""
            SELECT tick,sum(active),sum(incomplete) FROM (
              SELECT max(start,$from,coalesce((SELECT value FROM metadata WHERE key='expiry'),0)) tick,1 active,CASE WHEN complete=0 THEN 1 ELSE 0 END incomplete
                FROM coverage WHERE finish>max($from,coalesce((SELECT value FROM metadata WHERE key='expiry'),0)) AND start<$to
                  AND $to>coalesce((SELECT value FROM metadata WHERE key='expiry'),0) AND stamp>(SELECT value FROM metadata WHERE key='floor')
              UNION ALL SELECT min(finish,$to),-1,CASE WHEN complete=0 THEN -1 ELSE 0 END FROM coverage
                WHERE finish>max($from,coalesce((SELECT value FROM metadata WHERE key='expiry'),0)) AND start<$to
                  AND $to>coalesce((SELECT value FROM metadata WHERE key='expiry'),0) AND stamp>(SELECT value FROM metadata WHERE key='floor')
            ) GROUP BY tick ORDER BY tick
            """,tx,("$from",query.From.UtcTicks),("$to",query.To.UtcTicks));
        using var reader=command.ExecuteReader();
        var intervals=new List<ConnectionHistoryInterval>(); var gaps=new List<ConnectionHistoryGap>();
        long previous=query.From.UtcTicks,active=0,incomplete=0;
        void Slice(long end)
        {
            if(end<=previous) return;
            if(active==0)
            {
                if(gaps.Count>0 && gaps[^1].To.UtcTicks==previous) gaps[^1]=gaps[^1] with {To=Stamp(end)};
                else gaps.Add(new(Stamp(previous),Stamp(end)));
            }
            else
            {
                bool complete=incomplete==0;
                if(intervals.Count>0&&intervals[^1].To.UtcTicks==previous&&intervals[^1].Complete==complete) intervals[^1]=intervals[^1] with {To=Stamp(end)};
                else intervals.Add(new(Stamp(previous),Stamp(end),complete));
            }
            previous=end;
        }
        while(reader.Read()) {Slice(reader.GetInt64(0)); active+=reader.GetInt64(1); incomplete+=reader.GetInt64(2);}
        Slice(query.To.UtcTicks); return(intervals,gaps);
    }

    public void ExportCsv(TextWriter writer,ConnectionHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock(gate)
        {
            EnsureInitialized(); query=Normalize(query);
            using var db=Open(readOnly:true); using var tx=db.BeginTransaction(deferred:true);
            writer.WriteLine("MinuteUtc,Process,Pid,Generation,RemoteAddress,RemotePort,Protocol,FirstSeenUtc,LastSeenUtc,ReceivedBytes,SentBytes,DownloadBytesPerSecond,UploadBytesPerSecond,CountryCode,Country,Region,City,Latitude,Longitude,LocationSource,OriginLatitude,OriginLongitude,Complete");
            using var command=Command(db,"WITH filtered AS ("+Grouped+") SELECT "+Columns+" FROM filtered ORDER BY minute DESC,pid,generation,name,remote,port,protocol,location,origin",tx,Arguments(query));
            using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                var row=ReadRow(reader); var location=row.Location;
                object?[] values=[row.Minute.ToString("O"),row.Process.Name,row.Process.Pid,row.Process.Generation,row.RemoteAddress,row.RemotePort,row.Protocol,row.FirstSeen.ToString("O"),row.LastSeen.ToString("O"),row.ReceivedBytes,row.SentBytes,row.DownloadBytesPerSecond,row.UploadBytesPerSecond,location?.CountryCode,location?.Country,location?.Region,location?.City,location?.Point?.Latitude,location?.Point?.Longitude,location?.Source,row.Origin?.Latitude,row.Origin?.Longitude,row.Complete];
                writer.WriteLine(string.Join(',',values.Select(Csv)));
            }
        }
    }
    /// <summary>
    /// Every destination in <paramref name="scope"/> over the whole interval, uncapped, busiest first. The TEMP build and the
    /// destinations table are made before anything is written, so the TEMP limit leaves the writer empty.
    /// </summary>
    public void ExportDestinationsCsv(TextWriter writer,ConnectionHistoryQuery query,DestinationScope scope)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if(!Enum.IsDefined(scope)) throw Invalid();
        lock(gate)
        {
            EnsureInitialized(); query=Normalize(query);
            using var db=OpenGrouped(query,out var transaction); using var tx=transaction;
            Grouping(()=>Execute(db,DestinationsTable,tx));
            // The scope's export order, numbered so each page of 1,000 is a range.
            Grouping(()=>Execute(db,$"""
                CREATE TEMP TABLE exported(position INTEGER PRIMARY KEY,remote TEXT NOT NULL,kind INTEGER NOT NULL,received INTEGER NOT NULL,sent INTEGER NOT NULL,
                  first_seen INTEGER NOT NULL,last_seen INTEGER NOT NULL,minutes INTEGER NOT NULL,complete INTEGER NOT NULL,apps INTEGER NOT NULL,ports INTEGER NOT NULL,unlocated INTEGER NOT NULL);
                INSERT INTO temp.exported SELECT row_number() OVER (ORDER BY {DestinationOrder})-1,{DestinationColumns} FROM temp.destinations WHERE $kind<0 OR kind=$kind
                """,tx,("$kind",scope switch {DestinationScope.Internet=>0,DestinationScope.Local=>1,_=>-1})));
            writer.WriteLine("Address,Class,CountryCode,Country,Region,City,Latitude,Longitude,Applications,ApplicationNames,Ports,PortList,ReceivedBytes,SentBytes,Minutes,FirstSeenUtc,LastSeenUtc,Complete");
            const int Page=1000;
            for(long start=0;;start+=Page)
            {
                var page=Grouping(()=>ReadTotals(db,tx,$"SELECT {DestinationColumns} FROM temp.exported WHERE position>=$start AND position<$end ORDER BY position",("$start",start),("$end",start+Page)));
                if(page.Count==0) break;
                var lists=Grouping(()=>
                {
                    Execute(db,string.Format(CultureInfo.InvariantCulture,PickedTable,"SELECT remote FROM temp.exported WHERE position>=$start AND position<$end"),tx,("$start",start),("$end",start+Page));
                    var found=ReadLists(db,tx,ConnectionDestinations.ExportMaximumApps,ConnectionDestinations.ExportMaximumPorts,1);
                    Execute(db,"DROP TABLE temp.picked",tx);
                    return found;
                });
                foreach(var row in page.Select(t=>Destination(t,lists)))
                {
                    var location=row.Location;
                    object?[] values=[row.Address,row.Class.Label,location?.CountryCode,location?.Country,location?.Region,location?.City,location?.Point?.Latitude,location?.Point?.Longitude,
                        row.AppCount,Listed(row.Apps.Select(a=>a.Name),row.AppCount),row.PortCount,Listed(row.Ports.Select(p=>string.Create(CultureInfo.InvariantCulture,$"{p.Port} {p.Protocol}")),row.PortCount),
                        row.ReceivedBytes,row.SentBytes,row.Minutes,row.FirstSeen.ToString("O"),row.LastSeen.ToString("O"),row.Complete];
                    writer.WriteLine(string.Join(',',values.Select(Csv)));
                }
                if(page.Count<Page) break;
            }
        }
    }
    /// <summary>The busiest first, joined by "; ", ending "; +{n} more" when the list was capped.</summary>
    private static string Listed(IEnumerable<string> items,int count)
    {
        var shown=items.ToList();
        return count>shown.Count ? string.Join("; ",shown.Append(string.Create(CultureInfo.InvariantCulture,$"+{count-shown.Count} more"))) : string.Join("; ",shown);
    }
    private static string Csv(object? value)
    {
        string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
        // Only text can carry a formula; a negative coordinate or rate keeps its leading minus.
        if(value is not string) return "\""+text+"\"";
        // Neutralize before quoting, including formulas hidden behind whitespace or control characters.
        bool formula=text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@';
        bool control=text.Any(char.IsControl);
        text=new string(text.Select(c=>char.IsControl(c)?' ':c).ToArray());
        if(formula||control) text="'"+text;
        return "\""+text.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
    }
    private void EnsureInitialized() {if(!initialized) throw new InvalidOperationException("Connection history storage has not been initialized.");}
    private SqliteConnection Open(bool readOnly=false,bool create=false)
    {
        if(File.Exists(path)&&new FileInfo(path).Length>maximumDatabaseBytes) throw new IOException("Connection history exceeds its size limit; the file was preserved.");
        var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:create?SqliteOpenMode.ReadWriteCreate:SqliteOpenMode.ReadWrite,Cache=SqliteCacheMode.Private,Pooling=false,DefaultTimeout=1}.ToString());
        try
        {
            db.Open(); db.CreateAggregate<long,long>("sat_sum",0,(sum,value)=>Add(sum,value));
            db.CreateFunction<string,string>("app_key",ProcessGrouping.Key,isDeterministic:true);
            if(!readOnly)
            {
                if(File.Exists(path+"-wal")&&new FileInfo(path+"-wal").Length>MaximumJournalBytes)
                {
                    Checkpoint(db);
                    if(File.Exists(path+"-wal")&&new FileInfo(path+"-wal").Length>MaximumJournalBytes) throw new IOException("Connection history journal is waiting for a reader; recording stopped to bound disk use.");
                }
                long pageSize=Number(db,"PRAGMA page_size");
                Execute(db,"PRAGMA max_page_count="+(maximumDatabaseBytes/pageSize).ToString(CultureInfo.InvariantCulture)+"; PRAGMA journal_size_limit=8388608; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL; PRAGMA cache_size=-256; PRAGMA cache_spill=OFF;");
                if(Number(db,"PRAGMA cache_spill")!=0) throw new IOException("Connection history requires disabled transaction cache spilling.");
            }
            return db;
        }
        catch {db.Dispose();throw;}
    }
    private static void Checkpoint(SqliteConnection db)
    {
        try {Execute(db,"PRAGMA wal_checkpoint(TRUNCATE)");} catch(SqliteException error) when(error.SqliteErrorCode is 5 or 6) { }
    }
    private void GuardJournal(SqliteConnection db)
    {
        if(!File.Exists(path+"-wal")||new FileInfo(path+"-wal").Length<=MaximumJournalBytes) return;
        Checkpoint(db);
        if(File.Exists(path+"-wal")&&new FileInfo(path+"-wal").Length>MaximumJournalBytes)
            throw new IOException("Connection history journal is waiting for a reader; recording stopped to bound disk use.");
    }
    private static SqliteCommand Command(SqliteConnection db,string sql,SqliteTransaction? tx=null,params (string Name,object Value)[] args)
    {
        var command=db.CreateCommand(); command.CommandText=sql; command.Transaction=tx; command.CommandTimeout=1;
        foreach(var arg in args) command.Parameters.AddWithValue(arg.Name,arg.Value); return command;
    }
    private static int Execute(SqliteConnection db,string sql,SqliteTransaction? tx=null,params (string Name,object Value)[] args)
    {using var command=Command(db,sql,tx,args);return command.ExecuteNonQuery();}
    private static object? Scalar(SqliteConnection db,string sql,SqliteTransaction? tx=null)
    {using var command=Command(db,sql,tx);return command.ExecuteScalar();}
    private static long Number(SqliteConnection db,string sql) => Convert.ToInt64(Scalar(db,sql),CultureInfo.InvariantCulture);
}

/// <summary>A History read whose grouped TEMP tables would exceed their page limit; no partial view is returned.</summary>
public sealed class ConnectionHistoryLimitException(Exception inner) : IOException("Connection history interval holds too many saved rows to group.", inner);
