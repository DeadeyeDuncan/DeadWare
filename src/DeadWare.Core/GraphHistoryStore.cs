using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public sealed record GraphHistoryRecord(string SessionId, DateTimeOffset Timestamp, double MonotonicSeconds,
    IReadOnlyDictionary<string, double?> Values);

/// <summary>Six-hour graph cache. Synchronous calls belong to a single background owner.</summary>
public sealed class GraphHistoryStore
{
    public const long MaximumDatabaseBytes = 64L * 1024 * 1024;
    public const int MaximumRows = 21601;
    private const int MaximumPayloadLength = 32768;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private bool initialized;

    public GraphHistoryStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
    {
        if (maximumDatabaseBytes is < 65536 or > MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        this.path = Path.GetFullPath(path); this.maximumDatabaseBytes = maximumDatabaseBytes;
    }

    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }
        .Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(create: true, configure: false);
        long version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is not (0 or 1)) throw new InvalidDataException("This graph cache belongs to a newer DeadWare version.");
        Configure(db);
        Execute(db, "PRAGMA journal_mode=WAL");
        using (var transaction = db.BeginTransaction())
        {
            Execute(db, """
                CREATE TABLE IF NOT EXISTS graph_samples(
                    id INTEGER PRIMARY KEY, session TEXT NOT NULL, monotonic REAL NOT NULL,
                    stamp INTEGER NOT NULL, payload TEXT NOT NULL, UNIQUE(session,monotonic));
                CREATE INDEX IF NOT EXISTS graph_samples_time ON graph_samples(stamp,id);
                PRAGMA user_version=1;
                """, transaction);
            transaction.Commit();
        }
        Checkpoint(db); initialized = true;
    }

    public void Append(IReadOnlyList<GraphHistoryRecord> records, DateTimeOffset now)
    {
        EnsureInitialized();
        if (records.Count > 120) throw new ArgumentException("Graph history batches cannot exceed 120 records.", nameof(records));
        // Validate and serialize the complete batch before beginning a transaction. Failed
        // validation cannot prune history or partly persist the caller's batch.
        var prepared = records.Select(record =>
        {
            Validate(record);
            string payload = JsonSerializer.Serialize(record.Values);
            if (payload.Length > MaximumPayloadLength) throw new ArgumentException("Graph history values exceed the per-record limit.", nameof(records));
            return (Record: record, Payload: payload);
        }).ToArray();
        long cutoff = Cutoff(now), latest = now.UtcTicks;
        using var db = Open();
        using (var transaction = db.BeginTransaction())
        {
            Prune(db, transaction, cutoff, latest);
            using var insert = Command(db, """
                INSERT INTO graph_samples(session,monotonic,stamp,payload) VALUES($session,$monotonic,$stamp,$payload)
                ON CONFLICT(session,monotonic) DO NOTHING
                """, transaction);
            var session = insert.Parameters.Add("$session", SqliteType.Text);
            var monotonic = insert.Parameters.Add("$monotonic", SqliteType.Real);
            var stamp = insert.Parameters.Add("$stamp", SqliteType.Integer);
            var payload = insert.Parameters.Add("$payload", SqliteType.Text);
            foreach (var item in prepared)
            {
                if (item.Record.Timestamp.UtcTicks < cutoff || item.Record.Timestamp.UtcTicks > latest) continue;
                session.Value = item.Record.SessionId; monotonic.Value = item.Record.MonotonicSeconds;
                stamp.Value = item.Record.Timestamp.UtcTicks; payload.Value = item.Payload;
                insert.ExecuteNonQuery();
            }
            Prune(db, transaction, cutoff, latest);
            transaction.Commit();
        }
        Checkpoint(db);
    }

    public IReadOnlyList<GraphHistoryRecord> Read(DateTimeOffset now)
    {
        EnsureInitialized();
        using var db = Open(readOnly: true);
        using var command = Command(db, """
            SELECT session,monotonic,stamp,payload,length(payload) FROM
            (SELECT id,session,monotonic,stamp,payload FROM graph_samples
             WHERE stamp >= $cutoff AND stamp <= $now ORDER BY stamp DESC,id DESC LIMIT 21601)
            ORDER BY stamp,id
            """, null, ("$cutoff", Cutoff(now)), ("$now", now.UtcTicks));
        using var reader = command.ExecuteReader();
        var result = new List<GraphHistoryRecord>();
        while (reader.Read())
        {
            try
            {
                if (reader.GetInt64(4) > MaximumPayloadLength) throw new InvalidDataException("Graph cache contains an oversized record.");
                var values = JsonSerializer.Deserialize<Dictionary<string, double?>>(reader.GetString(3))
                    ?? throw new InvalidDataException("Graph cache contains an empty record.");
                var record = new GraphHistoryRecord(reader.GetString(0), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), reader.GetDouble(1),
                    new ReadOnlyDictionary<string, double?>(values));
                Validate(record); result.Add(record);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
            { throw new InvalidDataException("Graph cache contains an invalid record; the file was preserved.", error); }
        }
        return result;
    }

    private static void Validate(GraphHistoryRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.SessionId) || record.SessionId.Length > 128 ||
            !double.IsFinite(record.MonotonicSeconds) || record.Values is null || record.Values.Count > 64 ||
            record.Values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 ||
                pair.Value.HasValue && !double.IsFinite(pair.Value.Value)))
            throw new ArgumentException("Graph history record has invalid identifiers, time or values.", nameof(record));
    }
    private static long Cutoff(DateTimeOffset now) => Math.Max(DateTime.MinValue.Ticks, now.UtcTicks - 21600L * TimeSpan.TicksPerSecond);
    private static void Prune(SqliteConnection db, SqliteTransaction transaction, long cutoff, long now)
    {
        Execute(db, "DELETE FROM graph_samples WHERE stamp < $cutoff OR stamp > $now", transaction, ("$cutoff", cutoff), ("$now", now));
        Execute(db, "DELETE FROM graph_samples WHERE id IN (SELECT id FROM graph_samples ORDER BY stamp DESC,id DESC LIMIT -1 OFFSET 21601)", transaction);
    }
    private void EnsureInitialized()
    { if (!initialized) throw new InvalidOperationException("Graph history storage has not been initialized."); }
    private SqliteConnection Open(bool readOnly = false, bool create = false, bool configure = true)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumDatabaseBytes)
            throw new IOException("Graph cache exceeds its size limit; the file was preserved.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            db.Open();
            if (!readOnly && configure) Configure(db);
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private void Configure(SqliteConnection db)
    {
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > MaximumJournalBytes)
        {
            Checkpoint(db);
            if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > MaximumJournalBytes)
                throw new IOException("Graph cache journal is waiting for a reader; recording has stopped to bound disk use.");
        }
        long pageSize = Convert.ToInt64(Scalar(db, "PRAGMA page_size"), CultureInfo.InvariantCulture);
        Execute(db, "PRAGMA max_page_count=" + (maximumDatabaseBytes / pageSize).ToString(CultureInfo.InvariantCulture));
        Execute(db, "PRAGMA journal_size_limit=1048576; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL;");
    }
    private static void Checkpoint(SqliteConnection db)
    {
        try { Execute(db, "PRAGMA wal_checkpoint(TRUNCATE)"); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 1; command.Transaction = transaction;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    { using var command = Command(db, sql, transaction, args); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var command = Command(db, sql); return command.ExecuteScalar(); }
}
