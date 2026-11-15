using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public sealed record GraphHistoryRecord(string SessionId, DateTimeOffset Timestamp, double MonotonicSeconds,
    IReadOnlyDictionary<string, double?> Values)
{
    public double PollingIntervalSeconds { get; init; } = 1;
}

/// <summary>Six-hour graph cache. Synchronous calls belong to a single background owner.</summary>
public sealed class GraphHistoryStore
{
    public const long MaximumDatabaseBytes = 64L * 1024 * 1024;
    public const int MaximumRows = HistoryRanges.MaximumSamples;
    // Remain a dictionary payload so previous builds can read the same schema.
    private const string PollingIntervalKey = "$deadware-polling-interval-seconds";
    private const int MaximumPayloadLength = 32768;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    // What a row costs beyond its payload: its own header and its entries in the two indexes, session text included.
    private const int RowOverheadBytes = 160;
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

    /// <summary>Saves a batch. Returns true when the oldest rows were dropped to keep the file inside its size limit.</summary>
    public bool Append(IReadOnlyList<GraphHistoryRecord> records, DateTimeOffset now)
    {
        bool dropped = false;
        EnsureInitialized();
        if (records.Count > 120) throw new ArgumentException("Graph history batches cannot exceed 120 records.", nameof(records));
        // Validate and serialize the complete batch before beginning a transaction. Failed
        // validation cannot prune history or partly persist the caller's batch.
        var prepared = records.Select(record =>
        {
            Validate(record);
            var values = record.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (record.PollingIntervalSeconds != 1) values.Add(PollingIntervalKey, record.PollingIntervalSeconds);
            string payload = JsonSerializer.Serialize(values);
            if (payload.Length > MaximumPayloadLength) throw new ArgumentException("Graph history values exceed the per-record limit.", nameof(records));
            return (Record: record, Payload: payload);
        }).ToArray();
        long cutoff = Cutoff(now), latest = now.UtcTicks;
        using var db = Open();
        using (var transaction = db.BeginTransaction())
        {
            Prune(db, transaction, cutoff, latest);
            dropped = MakeRoom(db, transaction, prepared.Sum(item => Encoding.UTF8.GetByteCount(item.Payload) + RowOverheadBytes), prepared.Length);
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
        return dropped;
    }

    public IReadOnlyList<GraphHistoryRecord> Read(DateTimeOffset now)
    {
        EnsureInitialized();
        using var db = Open(readOnly: true);
        using var command = Command(db, """
            SELECT session,monotonic,stamp,payload,length(payload) FROM
            (SELECT id,session,monotonic,stamp,payload FROM graph_samples
             WHERE stamp >= $cutoff AND stamp <= $now ORDER BY stamp DESC,id DESC LIMIT $limit)
            ORDER BY stamp,id
            """, null, ("$cutoff", Cutoff(now)), ("$now", now.UtcTicks), ("$limit", MaximumRows));
        using var reader = command.ExecuteReader();
        var result = new List<GraphHistoryRecord>();
        while (reader.Read())
        {
            try
            {
                if (reader.GetInt64(4) > MaximumPayloadLength) throw new InvalidDataException("Graph cache contains an oversized record.");
                var values = JsonSerializer.Deserialize<Dictionary<string, double?>>(reader.GetString(3))
                    ?? throw new InvalidDataException("Graph cache contains an empty record.");
                double interval = values.Remove(PollingIntervalKey, out var storedInterval) ? storedInterval ?? double.NaN : 1;
                var record = new GraphHistoryRecord(reader.GetString(0), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), reader.GetDouble(1),
                    new ReadOnlyDictionary<string, double?>(values)) { PollingIntervalSeconds = interval };
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
            !double.IsFinite(record.MonotonicSeconds) || PollingPolicy.Normalize(record.PollingIntervalSeconds) != record.PollingIntervalSeconds ||
            record.Values is null || record.Values.Count > 64 || record.Values.ContainsKey(PollingIntervalKey) ||
            record.Values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256 ||
                pair.Value.HasValue && !double.IsFinite(pair.Value.Value)))
            throw new ArgumentException("Graph history record has invalid identifiers, time or values.", nameof(record));
    }
    private static long Cutoff(DateTimeOffset now) => Math.Max(DateTime.MinValue.Ticks, now.UtcTicks - 21600L * TimeSpan.TicksPerSecond);
    private static void Prune(SqliteConnection db, SqliteTransaction transaction, long cutoff, long now)
    {
        Execute(db, "DELETE FROM graph_samples WHERE stamp < $cutoff OR stamp > $now", transaction, ("$cutoff", cutoff), ("$now", now));
        Execute(db, "DELETE FROM graph_samples WHERE id IN (SELECT id FROM graph_samples ORDER BY stamp DESC,id DESC LIMIT -1 OFFSET $limit)", transaction, ("$limit", MaximumRows));
    }
    /// <summary>
    /// Frees pages for a batch by deleting the oldest rows, inside the batch's transaction. The file may not grow past its
    /// limit, and six hours of records do not always fit in it: 23 long-keyed values every half second fill 64 MiB in under
    /// five hours. Without this the insert fails with "database or disk is full" and recording stops until a restart; with
    /// it the file keeps the newest history that fits. A batch too large for an empty file still fails, and the
    /// transaction's rollback then restores what was deleted here.
    /// </summary>
    private static bool MakeRoom(SqliteConnection db, SqliteTransaction transaction, long incomingBytes, int incomingRows)
    {
        long Pages(string pragma) => Convert.ToInt64(Scalar(db, "PRAGMA " + pragma, transaction), CultureInfo.InvariantCulture);
        long pageSize = Pages("page_size"), limit = Pages("max_page_count");
        // Rows do not pack pages fully and the two indexes grow with them, so ask for half as much again and a few spare.
        long needed = (long)Math.Ceiling(incomingBytes * 1.5 / pageSize) + 4;
        bool dropped = false;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            long used = Pages("page_count"), free = Pages("freelist_count"), available = limit - used + free;
            if (available >= needed) break;
            long rows = Convert.ToInt64(Scalar(db, "SELECT count(*) FROM graph_samples", transaction), CultureInfo.InvariantCulture);
            if (rows == 0) break;
            double bytesPerRow = Math.Max(64, (double)(used - free) * pageSize / rows);
            // A quarter more than the shortfall, so a file at its limit drops rows every few batches, not every batch.
            long drop = Math.Clamp((long)Math.Ceiling((needed - available) * pageSize / bytesPerRow * 1.25) + incomingRows, 1, rows);
            Execute(db, "DELETE FROM graph_samples WHERE id IN (SELECT id FROM graph_samples ORDER BY stamp,id LIMIT $drop)", transaction, ("$drop", drop));
            dropped = true;
        }
        return dropped;
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
    private static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    { using var command = Command(db, sql, transaction); return command.ExecuteScalar(); }
}
