using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public sealed record AlertLogView(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AlertEpisode> Episodes, long Total, long StorageBytes);

/// <summary>Local alert episode log. Synchronous calls belong to one background owner; never call from the collector or UI thread.</summary>
public sealed class AlertStore
{
    public const long MaximumDatabaseBytes = 64L * 1024 * 1024;
    public const int RetentionDays = 400;
    public const int MaximumBatch = 1024;
    public const int MaximumRows = 500;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private bool initialized;

    public AlertStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
    {
        if (maximumDatabaseBytes is < 65536 or > MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        this.path = Path.GetFullPath(path); this.maximumDatabaseBytes = maximumDatabaseBytes;
    }

    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(create: true, configure: false);
        long version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is not (0 or 1)) throw new InvalidDataException("The alert log belongs to a newer DeadWare version.");
        Configure(db);
        Execute(db, "PRAGMA journal_mode=WAL");
        using (var transaction = db.BeginTransaction())
        {
            Execute(db, """
                CREATE TABLE IF NOT EXISTS episodes(
                    machine TEXT NOT NULL, id TEXT NOT NULL, rule TEXT NOT NULL, kind INTEGER NOT NULL, summary TEXT NOT NULL,
                    start INTEGER NOT NULL, finish INTEGER, peak REAL NOT NULL, acknowledged INTEGER NOT NULL CHECK(acknowledged IN(0,1)),
                    PRIMARY KEY(machine,id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS episodes_time ON episodes(machine,start);
                PRAGMA user_version=1;
                """, transaction);
            transaction.Commit();
        }
        Checkpoint(db); initialized = true;
    }

    /// <summary>Inserts new episodes and updates the end, peak and summary of known ones. Acknowledgement is never cleared by an update.</summary>
    public void Append(string machine, IReadOnlyList<AlertEpisode> episodes, DateTimeOffset now)
    {
        EnsureInitialized(); ValidateMachine(machine); ArgumentNullException.ThrowIfNull(episodes);
        if (episodes.Count > MaximumBatch) throw new ArgumentException($"Alert log batches are limited to {MaximumBatch} episodes.", nameof(episodes));
        foreach (var episode in episodes) Validate(episode);
        if (episodes.Count == 0) return;
        long cutoff = now.ToUnixTimeMilliseconds() - RetentionDays * 86_400_000L;
        using var db = Open();
        while (true)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                Execute(db, "DELETE FROM episodes WHERE start<$cutoff", transaction, ("$cutoff", cutoff));
                foreach (var episode in episodes)
                {
                    if (episode.Start.ToUnixTimeMilliseconds() < cutoff) continue;
                    Execute(db, """
                        INSERT INTO episodes(machine,id,rule,kind,summary,start,finish,peak,acknowledged)
                        VALUES($machine,$id,$rule,$kind,$summary,$start,$finish,$peak,$acknowledged)
                        ON CONFLICT(machine,id) DO UPDATE SET finish=excluded.finish, peak=excluded.peak, summary=excluded.summary,
                            acknowledged=MAX(acknowledged,excluded.acknowledged)
                        """, transaction, ("$machine", machine), ("$id", episode.Id), ("$rule", episode.RuleId), ("$kind", (int)episode.Kind),
                        ("$summary", episode.Summary), ("$start", episode.Start.ToUnixTimeMilliseconds()),
                        ("$finish", episode.End is { } end ? end.ToUnixTimeMilliseconds() : DBNull.Value), ("$peak", episode.Peak),
                        ("$acknowledged", episode.Acknowledged ? 1 : 0));
                }
                transaction.Commit();
                break;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                // The file reached its cap: drop the oldest tenth of the closed episodes and retry; open ones are never dropped.
                if (!EvictOldestClosed(db)) throw new IOException("The alert log is full of open episodes; the batch was not saved.", error);
            }
        }
        Checkpoint(db);
    }

    /// <summary>Episodes that overlap the range, newest first, bounded by <paramref name="maxRows"/>.</summary>
    public AlertLogView Read(string machine, DateTimeOffset from, DateTimeOffset to, int maxRows = MaximumRows)
    {
        EnsureInitialized(); ValidateMachine(machine);
        if (from >= to) throw new ArgumentOutOfRangeException(nameof(from));
        if (maxRows is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maxRows));
        using var db = Open(readOnly: true);
        (string, object)[] args = [("$machine", machine), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())];
        const string Where = "WHERE machine=$machine AND start<$to AND (finish IS NULL OR finish>=$from)";
        var episodes = new List<AlertEpisode>();
        using (var command = Command(db, $"SELECT id,rule,kind,summary,start,finish,peak,acknowledged FROM episodes {Where} ORDER BY start DESC,id LIMIT $limit", null, [.. args, ("$limit", maxRows)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) episodes.Add(ReadEpisode(reader));
        long total = Convert.ToInt64(ScalarWith(db, $"SELECT COUNT(*) FROM episodes {Where}", args), CultureInfo.InvariantCulture);
        return new(from, to, episodes, total, StorageBytes);
    }

    /// <summary>Episodes without an end, oldest first, for restoring evaluator state at startup.</summary>
    public IReadOnlyList<AlertEpisode> Open(string machine)
    {
        EnsureInitialized(); ValidateMachine(machine);
        using var db = Open(readOnly: true);
        using var command = Command(db, "SELECT id,rule,kind,summary,start,finish,peak,acknowledged FROM episodes WHERE machine=$machine AND finish IS NULL ORDER BY start,id LIMIT $limit",
            null, ("$machine", machine), ("$limit", AlertRules.MaximumRules));
        using var reader = command.ExecuteReader();
        var result = new List<AlertEpisode>();
        while (reader.Read()) result.Add(ReadEpisode(reader));
        return result;
    }

    public bool Acknowledge(string machine, string id)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateId(id);
        using var db = Open();
        int changed = Execute(db, "UPDATE episodes SET acknowledged=1 WHERE machine=$machine AND id=$id", null, ("$machine", machine), ("$id", id));
        Checkpoint(db);
        return changed > 0;
    }

    private static AlertEpisode ReadEpisode(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), (AlertKind)reader.GetInt32(2),
        reader.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), reader.GetDouble(6), reader.GetInt64(7) == 1);

    private static bool EvictOldestClosed(SqliteConnection db)
    {
        long closed = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM episodes WHERE finish IS NOT NULL"), CultureInfo.InvariantCulture);
        if (closed == 0) return false;
        long count = Math.Max(1, closed / 10);
        using var transaction = db.BeginTransaction();
        Execute(db, "DELETE FROM episodes WHERE (machine,id) IN (SELECT machine,id FROM episodes WHERE finish IS NOT NULL ORDER BY start,id LIMIT $count)", transaction, ("$count", count));
        transaction.Commit();
        return true;
    }

    private static void Validate(AlertEpisode episode)
    {
        if (episode is null) throw new ArgumentException("Alert episode is missing.");
        ValidateId(episode.Id); ValidateId(episode.RuleId);
        if (!Enum.IsDefined(episode.Kind) || episode.Summary is null || episode.Summary.Length > 512 || episode.Summary.Any(char.IsControl) ||
            !double.IsFinite(episode.Peak) || episode.End is { } end && end < episode.Start)
            throw new ArgumentException("Invalid alert episode.", nameof(episode));
    }
    private static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid alert identifier.", nameof(id));
    }
    private static void ValidateMachine(string machine)
    { if (string.IsNullOrWhiteSpace(machine) || machine.Length > 256) throw new ArgumentException("Invalid machine key.", nameof(machine)); }
    private void EnsureInitialized()
    { if (!initialized) throw new InvalidOperationException("Alert log storage has not been initialized."); }
    private SqliteConnection Open(bool readOnly = false, bool create = false, bool configure = true)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumDatabaseBytes)
            throw new IOException("The alert log exceeds its size limit; the file was preserved.");
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
                throw new IOException("The alert log journal is waiting for a reader; recording has stopped to bound disk use.");
        }
        long pageSize = Convert.ToInt64(Scalar(db, "PRAGMA page_size"), CultureInfo.InvariantCulture);
        Execute(db, "PRAGMA max_page_count=" + (maximumDatabaseBytes / pageSize).ToString(CultureInfo.InvariantCulture));
        Execute(db, "PRAGMA journal_size_limit=8388608; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL;");
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
    private static object? ScalarWith(SqliteConnection db, string sql, (string Name, object Value)[] args)
    { using var command = Command(db, sql, null, args); return command.ExecuteScalar(); }
}
