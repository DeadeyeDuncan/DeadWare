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
    private const long MaximumJournalBytes = 8 * 1024 * 1024;
    private const int ApplicationId = 0x44435748;
    private const int ChunkSize = 256;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private readonly object gate = new();
    private bool initialized;
    private const string Columns = "minute,pid,generation,name,remote,port,protocol,first_seen,last_seen,received,sent,location,origin,complete";
    private const string Grouped = """
        SELECT minute,pid,generation,name,remote,port,protocol,min(first_seen) first_seen,
          max(last_seen) last_seen,sat_sum(received) received,sat_sum(sent) sent,
          location,origin,min(complete) complete,country,kind
        FROM traffic
        WHERE minute >= $from AND minute < $to
          AND ($search='' OR instr(search,$search)>0)
          AND ($country='' OR country=$country)
        GROUP BY minute,pid,generation,name,remote,port,protocol,location,origin,country,kind
        HAVING ($direction=0 OR ($direction=1 AND sat_sum(received)>0) OR ($direction=2 AND sat_sum(sent)>0))
        """;
    private const string Schema = """
        CREATE TABLE metadata(key TEXT PRIMARY KEY,value INTEGER NOT NULL);
        INSERT INTO metadata VALUES('floor',0);
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

    public ConnectionHistoryStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
    {
        if(maximumDatabaseBytes is <65536 or >MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        this.path=Path.GetFullPath(path); this.maximumDatabaseBytes=maximumDatabaseBytes;
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
            var manifests=prepared.GroupBy(x=>x.Record.BatchId).Select(group=>new Manifest(group.Key,0,
                Identity(group.OrderBy(x=>x.Item,StringComparer.Ordinal).Select(x=>new {x.Item,x.Record.Delta.ReceivedBytes,x.Record.Delta.SentBytes,first=x.Record.Delta.FirstSeen.UtcTicks,last=x.Record.Delta.LastSeen.UtcTicks,x.Record.Complete}).ToArray())))
                .Concat(coverage.GroupBy(x=>x.BatchId).Select(group=>new Manifest(group.Key,1,Identity(group.Select(x=>new {x.Segment,from=x.From.UtcTicks,to=x.To.UtcTicks,x.Complete}).Distinct().OrderBy(x=>x.from).ThenBy(x=>x.to).ThenBy(x=>x.Segment,StringComparer.Ordinal).ThenBy(x=>x.Complete).ToArray())))).ToArray();
            var manifestIndex=manifests.ToDictionary(x=>(x.Batch,x.Kind));
            var coveragePieces=coverage.SelectMany(c=>
            {
                long boundary=Minute(c.From.UtcTicks)+TimeSpan.TicksPerMinute;
                return c.To.UtcTicks<=boundary ? new[]{c} : new[]{c with {To=Stamp(boundary)},c with {From=Stamp(boundary)}};
            }).ToArray();
            using var db=Open();
            ValidateManifests(db,manifests);
            // Independent dedup keys let an exact retry finish after a later chunk failed, without duplicating earlier chunks.
            // Coverage is committed only after all traffic chunks succeed, so a failed traffic drain cannot claim coverage.
            for(int offset=0;offset<Math.Max(1,prepared.Length);offset+=ChunkSize)
            {
                var chunk=prepared.Skip(offset).Take(ChunkSize).ToArray();
                var chunkManifests=chunk.Select(x=>x.Record.BatchId).Distinct(StringComparer.Ordinal).Select(batch=>manifestIndex[(batch,0)]).ToArray();
                GuardJournal(db); WriteChunk(db,chunk,[],cutoff,chunkManifests); Checkpoint(db);
            }
            for(int offset=0;offset<coveragePieces.Length;offset+=ChunkSize)
            {
                var chunk=coveragePieces.Skip(offset).Take(ChunkSize).ToArray();
                var chunkManifests=chunk.Select(x=>x.BatchId).Distinct(StringComparer.Ordinal).Select(batch=>manifestIndex[(batch,1)]).ToArray();
                GuardJournal(db); WriteChunk(db,[],chunk,cutoff,chunkManifests); Checkpoint(db);
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
    private void WriteChunk(SqliteConnection db,Prepared[] records,ConnectionCoverage[] coverage,long cutoff,Manifest[] manifests)
    {
        long? attemptedFloor=null;
        for(int pruneCount=0;;pruneCount=pruneCount==0?1:pruneCount*2)
        {
            try
            {
                using var tx=db.BeginTransaction();
                ValidateManifests(db,manifests,tx);
                Execute(db,"DELETE FROM traffic WHERE minute < $cutoff; DELETE FROM coverage WHERE finish <= $cutoff",tx,("$cutoff",cutoff));
                Execute(db,"UPDATE coverage SET start=$cutoff,stamp=$stamp WHERE start<$cutoff",tx,("$cutoff",cutoff),("$stamp",Minute(cutoff)));
                if(pruneCount>0)
                {
                    using var choose=Command(db,"SELECT max(stamp) FROM (SELECT stamp FROM (SELECT minute stamp FROM traffic UNION SELECT stamp FROM coverage) ORDER BY stamp LIMIT $count)",tx,("$count",pruneCount));
                    object? value=choose.ExecuteScalar();
                    if(value is null or DBNull) throw new IOException("A complete minute cannot fit in the connection history capacity; earlier committed chunks were preserved.");
                    long floor=Convert.ToInt64(value,CultureInfo.InvariantCulture);
                    if(attemptedFloor==floor) throw new IOException("A complete minute cannot fit in the connection history capacity; earlier committed chunks were preserved.");
                    attemptedFloor=floor;
                    Execute(db,"DELETE FROM traffic WHERE minute<=$floor; DELETE FROM coverage WHERE stamp<=$floor; DELETE FROM dedup WHERE stamp<=$floor; UPDATE metadata SET value=max(value,$floor) WHERE key='floor'",tx,("$floor",floor));
                }
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
                Execute(db,"""
                    DELETE FROM dedup WHERE (kind=0 AND NOT EXISTS(SELECT 1 FROM traffic WHERE traffic.batch=dedup.batch AND traffic.item=dedup.item))
                        OR (kind=1 AND NOT EXISTS(SELECT 1 FROM coverage WHERE coverage.batch=dedup.batch AND coverage.item=dedup.item));
                    DELETE FROM manifests WHERE NOT EXISTS(SELECT 1 FROM dedup WHERE dedup.batch=manifests.batch AND dedup.kind=manifests.kind);
                    """,tx);
                foreach(var manifest in manifests)
                    Execute(db,"INSERT OR IGNORE INTO manifests SELECT $batch,$kind,$payload WHERE EXISTS(SELECT 1 FROM dedup WHERE batch=$batch AND kind=$kind)",tx,("$batch",manifest.Batch),("$kind",manifest.Kind),("$payload",manifest.Payload));
                tx.Commit(); return;
            }
            catch(SqliteException error) when(error.SqliteErrorCode==13 && pruneCount<65536) { }
            catch(SqliteException error) when(error.SqliteErrorCode==13)
            {throw new IOException("Connection history capacity was exhausted; earlier committed chunks were preserved.",error);}
        }
    }

    public ConnectionHistoryView Read(ConnectionHistoryQuery query)
    {
        lock(gate)
        {
            EnsureInitialized(); query=Normalize(query);
            using var db=Open(readOnly:true); using var tx=db.BeginTransaction(deferred:true);
            var args=Arguments(query);
            var rows=new List<ConnectionHistoryRow>();
            using(var command=Command(db,"WITH filtered AS ("+Grouped+") SELECT "+Columns+" FROM filtered ORDER BY minute DESC,pid,generation,name,remote,port,protocol,location,origin LIMIT $limit OFFSET $offset",tx,args.Concat(new[]{("$limit",(object)query.Limit),("$offset",(object)query.Offset)}).ToArray()))
            using(var reader=command.ExecuteReader()) while(reader.Read()) rows.Add(ReadRow(reader,query));
            long total; ConnectionHistorySummary summary;
            using(var command=Command(db,"WITH filtered AS ("+Grouped+") SELECT count(*),sat_sum(received),sat_sum(sent),count(DISTINCT nullif(country,'')),count(DISTINCT remote),count(DISTINCT CASE WHEN location='null' OR json_extract(location,'$.Point') IS NULL THEN remote END),count(DISTINCT CASE WHEN kind=1 THEN remote END) FROM filtered",tx,args))
            using(var reader=command.ExecuteReader()) {reader.Read(); total=reader.GetInt64(0); summary=new(Value(reader,1),Value(reader,2),reader.GetInt64(3),reader.GetInt64(4),reader.GetInt64(5),reader.GetInt64(6));}
            var markers=new List<ConnectionHistoryMarker>();
            using(var command=Command(db,"WITH filtered AS ("+Grouped+") SELECT location,origin,sat_sum(received) received,sat_sum(sent) sent FROM filtered WHERE location<>'null' AND json_extract(location,'$.Point') IS NOT NULL GROUP BY location,origin ORDER BY (cast(sat_sum(received) AS REAL)+cast(sat_sum(sent) AS REAL)) DESC,location,origin LIMIT 256",tx,args))
            using(var reader=command.ExecuteReader()) while(reader.Read()) markers.Add(new(JsonSerializer.Deserialize<ConnectionLocation>(reader.GetString(0))!,JsonSerializer.Deserialize<GeoPoint>(reader.GetString(1)),reader.GetInt64(2),reader.GetInt64(3)));
            var (coverage,gaps)=ReadCoverage(db,tx,query);
            object? earliest=Scalar(db,"SELECT min(stamp) FROM (SELECT min(minute) stamp FROM traffic UNION ALL SELECT min(start) FROM coverage)",tx);
            long floor=Convert.ToInt64(Scalar(db,"SELECT value FROM metadata WHERE key='floor'",tx),CultureInfo.InvariantCulture);
            string status=floor>0?"Capacity shortened retention. ":"";
            if(gaps.Count>0||coverage.Any(x=>!x.Complete)) status+="History contains recording gaps.";
            else status+="Recorded coverage available.";
            return new(query,rows.AsReadOnly(),total,summary,markers.AsReadOnly(),coverage.AsReadOnly(),gaps.AsReadOnly(),earliest is null or DBNull?null:Stamp(Convert.ToInt64(earliest,CultureInfo.InvariantCulture)),StorageBytes,status);
        }
    }
    private static long Value(SqliteDataReader reader,int ordinal) => reader.IsDBNull(ordinal)?0:reader.GetInt64(ordinal);
    private static DateTimeOffset Stamp(long ticks) => new(ticks,TimeSpan.Zero);
    private static ConnectionHistoryRow ReadRow(SqliteDataReader r,ConnectionHistoryQuery query)
    {
        double seconds=(query.To-query.From).TotalSeconds;
        return new(Stamp(r.GetInt64(0)),new(r.GetInt32(1),r.GetInt64(2),r.GetString(3)),r.GetString(4),r.GetInt32(5),(ConnectionProtocol)r.GetInt32(6),Stamp(r.GetInt64(7)),Stamp(r.GetInt64(8)),r.GetInt64(9),r.GetInt64(10),r.GetInt64(9)/seconds,r.GetInt64(10)/seconds,JsonSerializer.Deserialize<ConnectionLocation>(r.GetString(11)),JsonSerializer.Deserialize<GeoPoint>(r.GetString(12)),r.GetInt32(13)!=0);
    }
    private static ConnectionHistoryQuery Normalize(ConnectionHistoryQuery query)
    {
        if(query is null||query.To<=query.From||query.Search is null||query.Search.Length>260||query.CountryCode is null||query.CountryCode.Length>2||!Enum.IsDefined(query.Direction)) throw Invalid();
        return query with {From=query.From.ToUniversalTime(),To=query.To.ToUniversalTime(),Search=query.Search.Trim().ToLowerInvariant(),CountryCode=query.CountryCode.Trim().ToUpperInvariant(),Offset=Math.Max(0,query.Offset),Limit=Math.Clamp(query.Limit,1,500)};
    }
    private static (string,object)[] Arguments(ConnectionHistoryQuery query) => [("$from",query.From.UtcTicks),("$to",query.To.UtcTicks),("$search",query.Search),("$country",query.CountryCode),("$direction",(int)query.Direction)];
    private static (List<ConnectionHistoryInterval>,List<ConnectionHistoryGap>) ReadCoverage(SqliteConnection db,SqliteTransaction tx,ConnectionHistoryQuery query)
    {
        // Sweep interval edges in SQL order; memory is proportional to the published merged result, not raw samples.
        using var command=Command(db,"""
            SELECT tick,sum(active),sum(incomplete) FROM (
              SELECT max(start,$from) tick,1 active,CASE WHEN complete=0 THEN 1 ELSE 0 END incomplete FROM coverage WHERE finish>$from AND start<$to
              UNION ALL SELECT min(finish,$to),-1,CASE WHEN complete=0 THEN -1 ELSE 0 END FROM coverage WHERE finish>$from AND start<$to
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
                var row=ReadRow(reader,query); var location=row.Location;
                object?[] values=[row.Minute.ToString("O"),row.Process.Name,row.Process.Pid,row.Process.Generation,row.RemoteAddress,row.RemotePort,row.Protocol,row.FirstSeen.ToString("O"),row.LastSeen.ToString("O"),row.ReceivedBytes,row.SentBytes,row.DownloadBytesPerSecond,row.UploadBytesPerSecond,location?.CountryCode,location?.Country,location?.Region,location?.City,location?.Point?.Latitude,location?.Point?.Longitude,location?.Source,row.Origin?.Latitude,row.Origin?.Longitude,row.Complete];
                writer.WriteLine(string.Join(',',values.Select(Csv)));
            }
        }
    }
    private static string Csv(object? value)
    {
        string text=Convert.ToString(value,CultureInfo.InvariantCulture)??"";
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
        var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:create?SqliteOpenMode.ReadWriteCreate:SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=1}.ToString());
        try
        {
            db.Open(); db.CreateAggregate<long,long>("sat_sum",0,(sum,value)=>Add(sum,value));
            if(!readOnly)
            {
                if(File.Exists(path+"-wal")&&new FileInfo(path+"-wal").Length>MaximumJournalBytes)
                {
                    Checkpoint(db);
                    if(File.Exists(path+"-wal")&&new FileInfo(path+"-wal").Length>MaximumJournalBytes) throw new IOException("Connection history journal is waiting for a reader; recording stopped to bound disk use.");
                }
                long pageSize=Number(db,"PRAGMA page_size");
                Execute(db,"PRAGMA max_page_count="+(maximumDatabaseBytes/pageSize).ToString(CultureInfo.InvariantCulture)+"; PRAGMA journal_size_limit=8388608; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL;");
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
