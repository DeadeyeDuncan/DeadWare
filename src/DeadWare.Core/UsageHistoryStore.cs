using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

/// <summary>A fixed-width display slot. Minutes below the slot's width mean recorded gaps inside it.</summary>
public sealed record UsageHistoryBucket(DateTimeOffset Start, DateTimeOffset End, double Average, double Minimum, double Maximum,
    long Minutes, long CompleteMinutes, long Samples);

public sealed record UsageHistoryView(DateTimeOffset From, DateTimeOffset To, long BucketMilliseconds, IReadOnlyList<UsageHistoryBucket> Buckets,
    Summary Statistics, long Minutes, DateTimeOffset? EarliestRetained, long StorageBytes);

public sealed record UsageApplicationTotal(string Name, long Received, long Sent, long Minutes);

/// <summary>Per-name totals for a range. Names beyond the limit are folded into the Other counters.</summary>
public sealed record UsageApplicationView(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<UsageApplicationTotal> Applications,
    long OtherApplications, long OtherReceived, long OtherSent, long CaptureGapMinutes, long RecordedMinutes,
    DateTimeOffset? EarliestRetained, long StorageBytes);

/// <summary>Long-term usage minutes. Synchronous calls belong to one background owner; never call from the collector or UI thread.</summary>
public sealed class UsageHistoryStore
{
    public const long MaximumDatabaseBytes = 256L * 1024 * 1024;
    public const int RetentionDays = 400;
    public const int MaximumBatchRows = 4096;
    public const int MaximumApplications = 200;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    private const long MinuteMilliseconds = 60_000, DayMilliseconds = 86_400_000, FutureToleranceMilliseconds = 5 * MinuteMilliseconds;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private bool initialized;

    public UsageHistoryStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
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
        if (version is not (0 or 1)) throw new InvalidDataException("Usage history belongs to a newer DeadWare version.");
        Configure(db);
        Execute(db, "PRAGMA journal_mode=WAL");
        using (var transaction = db.BeginTransaction())
        {
            Execute(db, """
                CREATE TABLE IF NOT EXISTS metric_minutes(
                    machine TEXT NOT NULL, metric TEXT NOT NULL, hardware TEXT NOT NULL, minute INTEGER NOT NULL,
                    average REAL NOT NULL, minimum REAL NOT NULL, maximum REAL NOT NULL,
                    samples INTEGER NOT NULL CHECK(samples>0), complete INTEGER NOT NULL CHECK(complete IN(0,1)),
                    segment TEXT NOT NULL, PRIMARY KEY(machine,metric,hardware,minute)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS metric_minutes_time ON metric_minutes(machine,minute);
                CREATE TABLE IF NOT EXISTS application_minutes(
                    machine TEXT NOT NULL, name TEXT NOT NULL, minute INTEGER NOT NULL,
                    received INTEGER NOT NULL CHECK(received>=0), sent INTEGER NOT NULL CHECK(sent>=0),
                    segment TEXT NOT NULL, PRIMARY KEY(machine,name,minute)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS application_minutes_time ON application_minutes(machine,minute);
                CREATE TABLE IF NOT EXISTS capture_gaps(machine TEXT NOT NULL, minute INTEGER NOT NULL, PRIMARY KEY(machine,minute)) WITHOUT ROWID;
                PRAGMA user_version=1;
                """, transaction);
            transaction.Commit();
        }
        Checkpoint(db); initialized = true;
    }

    /// <summary>Writes closed minutes. Rows older than the retention window or more than five minutes ahead of now are ignored.
    /// A minute already recorded from another segment (a restart inside that minute) is merged and marked incomplete; the same
    /// segment repeated is ignored, so a retried batch cannot double count.</summary>
    public void Append(string machine, IReadOnlyList<UsageMinuteBatch> batches, DateTimeOffset now)
    {
        EnsureInitialized(); ValidateMachine(machine); ArgumentNullException.ThrowIfNull(batches);
        int rows = 0;
        foreach (var batch in batches)
        {
            if (batch is null) throw new ArgumentException("Usage history batch is missing.", nameof(batches));
            ValidateSegment(batch.Segment);
            foreach (var metric in batch.Metrics)
            {
                if (metric is null || !UsageMetrics.IsKnown(metric.Metric) || metric.Minute != batch.Minute || metric.Samples <= 0 ||
                    !double.IsFinite(metric.Average) || !double.IsFinite(metric.Minimum) || !double.IsFinite(metric.Maximum) ||
                    metric.Minimum > metric.Average || metric.Average > metric.Maximum ||
                    UsageMetrics.UsesHardwareId(metric.Metric) != !string.IsNullOrEmpty(metric.HardwareId) ||
                    metric.HardwareId is { Length: > 256 } || metric.Segment != batch.Segment)
                    throw new ArgumentException("Invalid usage metric minute.", nameof(batches));
                rows++;
            }
            foreach (var application in batch.Applications)
            {
                if (application is null || application.Minute != batch.Minute || application.Received < 0 || application.Sent < 0 ||
                    application.Received + application.Sent == 0 || application.Name != UsageMetrics.NormalizeName(application.Name) ||
                    application.Segment != batch.Segment)
                    throw new ArgumentException("Invalid usage application minute.", nameof(batches));
                rows++;
            }
            if (batch.CaptureGap) rows++;
        }
        if (rows > MaximumBatchRows) throw new ArgumentException($"Usage history batches are limited to {MaximumBatchRows} rows.", nameof(batches));
        if (rows == 0) return;
        long cutoff = DayFloor(now.ToUnixTimeMilliseconds()) - RetentionDays * DayMilliseconds;
        long latest = now.ToUnixTimeMilliseconds() + FutureToleranceMilliseconds;
        using var db = Open();
        while (true)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                Expire(db, transaction, cutoff);
                foreach (var batch in batches)
                {
                    long minute = batch.Minute.ToUnixTimeMilliseconds();
                    if (minute < cutoff || minute > latest) continue;
                    foreach (var metric in batch.Metrics)
                        Execute(db, """
                            INSERT INTO metric_minutes(machine,metric,hardware,minute,average,minimum,maximum,samples,complete,segment)
                            VALUES($machine,$metric,$hardware,$minute,$average,$minimum,$maximum,$samples,$complete,$segment)
                            ON CONFLICT(machine,metric,hardware,minute) DO UPDATE SET
                                average=(average*samples+excluded.average*excluded.samples)/(samples+excluded.samples),
                                minimum=MIN(minimum,excluded.minimum), maximum=MAX(maximum,excluded.maximum),
                                samples=samples+excluded.samples, complete=0
                            WHERE excluded.segment<>segment
                            """, transaction, ("$machine", machine), ("$metric", metric.Metric), ("$hardware", metric.HardwareId ?? ""),
                            ("$minute", minute), ("$average", metric.Average), ("$minimum", metric.Minimum), ("$maximum", metric.Maximum),
                            ("$samples", metric.Samples), ("$complete", metric.Complete ? 1 : 0), ("$segment", metric.Segment));
                    foreach (var application in batch.Applications)
                        Execute(db, """
                            INSERT INTO application_minutes(machine,name,minute,received,sent,segment)
                            VALUES($machine,$name,$minute,$received,$sent,$segment)
                            ON CONFLICT(machine,name,minute) DO UPDATE SET
                                received=received+excluded.received, sent=sent+excluded.sent
                            WHERE excluded.segment<>segment
                            """, transaction, ("$machine", machine), ("$name", application.Name), ("$minute", minute),
                            ("$received", application.Received), ("$sent", application.Sent), ("$segment", application.Segment));
                    if (batch.CaptureGap)
                        Execute(db, "INSERT OR IGNORE INTO capture_gaps(machine,minute) VALUES($machine,$minute)", transaction,
                            ("$machine", machine), ("$minute", minute));
                }
                transaction.Commit();
                break;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                // The file reached its cap: drop the oldest whole day and retry, never the day being recorded.
                if (!EvictOldestDay(db, DayFloor(now.ToUnixTimeMilliseconds())))
                    throw new IOException("Usage history is full and only the current day remains; the batch was not saved.", error);
            }
        }
        Checkpoint(db);
    }

    public UsageHistoryView ReadMetric(string machine, string metric, string? hardwareId, DateTimeOffset from, DateTimeOffset to, int maxPoints = 1500)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        if (!UsageMetrics.IsKnown(metric) || UsageMetrics.UsesHardwareId(metric) != !string.IsNullOrEmpty(hardwareId))
            throw new ArgumentException("Unknown usage metric or hardware selection.", nameof(metric));
        if (maxPoints is < 1 or > 4000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        using var db = Open(readOnly: true);
        long fromMs = from.ToUnixTimeMilliseconds(), toMs = to.ToUnixTimeMilliseconds();
        long bucket = Math.Max(MinuteMilliseconds, (long)Math.Ceiling((toMs - fromMs) / (double)maxPoints));
        const string Where = "WHERE machine=$machine AND metric=$metric AND hardware=$hardware AND minute>=$from AND minute<$to";
        (string, object)[] args = [("$machine", machine), ("$metric", metric), ("$hardware", hardwareId ?? ""), ("$from", fromMs), ("$to", toMs)];
        var buckets = new List<UsageHistoryBucket>();
        using (var command = Command(db, $"""
            SELECT (minute-$from)/$bucket,SUM(average*samples)/SUM(samples),MIN(minimum),MAX(maximum),COUNT(*),SUM(complete),SUM(samples)
            FROM metric_minutes {Where} GROUP BY (minute-$from)/$bucket ORDER BY 1
            """, null, [.. args, ("$bucket", bucket)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                long start = fromMs + reader.GetInt64(0) * bucket;
                buckets.Add(new(Stamp(start), Stamp(Math.Min(start + bucket, toMs)), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
            }
        Summary statistics; long minutes;
        using (var command = Command(db, $"SELECT MIN(minimum),MAX(maximum),SUM(average*samples)/SUM(samples),COUNT(*) FROM metric_minutes {Where}", null, args))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            statistics = new(reader.IsDBNull(0) ? null : reader.GetDouble(0), reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2));
            minutes = reader.GetInt64(3);
        }
        return new(from, to, bucket, buckets, statistics, minutes, Earliest(db, machine), StorageBytes);
    }

    public UsageApplicationView ReadApplications(string machine, DateTimeOffset from, DateTimeOffset to, int maxNames = MaximumApplications)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        if (maxNames is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxNames));
        using var db = Open(readOnly: true);
        (string, object)[] args = [("$machine", machine), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())];
        var applications = new List<UsageApplicationTotal>();
        using (var command = Command(db, """
            SELECT name,SUM(received),SUM(sent),COUNT(*) FROM application_minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to GROUP BY name ORDER BY SUM(received)+SUM(sent) DESC,name LIMIT $limit
            """, null, [.. args, ("$limit", maxNames)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) applications.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        long names, received, sent;
        using (var command = Command(db, """
            SELECT COUNT(DISTINCT name),COALESCE(SUM(received),0),COALESCE(SUM(sent),0) FROM application_minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to
            """, null, args))
        using (var reader = command.ExecuteReader())
        { reader.Read(); names = reader.GetInt64(0); received = reader.GetInt64(1); sent = reader.GetInt64(2); }
        long gaps = Convert.ToInt64(ScalarWith(db, "SELECT COUNT(*) FROM capture_gaps WHERE machine=$machine AND minute>=$from AND minute<$to", args), CultureInfo.InvariantCulture);
        long recorded = Convert.ToInt64(ScalarWith(db, "SELECT COUNT(DISTINCT minute) FROM metric_minutes WHERE machine=$machine AND minute>=$from AND minute<$to", args), CultureInfo.InvariantCulture);
        return new(from, to, applications, names - applications.Count, received - applications.Sum(a => a.Received),
            sent - applications.Sum(a => a.Sent), gaps, recorded, Earliest(db, machine), StorageBytes);
    }

    /// <summary>One application's stored minutes in a range, oldest first, with the segment that first wrote each row.</summary>
    public IReadOnlyList<UsageApplicationMinute> ReadApplicationMinutes(string machine, string name, DateTimeOffset from, DateTimeOffset to)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var db = Open(readOnly: true);
        var rows = new List<UsageApplicationMinute>();
        using var command = Command(db, """
            SELECT name,minute,received,sent,segment FROM application_minutes
            WHERE machine=$machine AND name=$name COLLATE NOCASE AND minute>=$from AND minute<$to ORDER BY minute
            """, null, [("$machine", machine), ("$name", name), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4)));
        return rows;
    }

    /// <summary>How many recorded minutes of a metric averaged at or above a value, for threshold suggestions.</summary>
    public (long AtOrAbove, long Total) ShareAtOrAbove(string machine, string metric, string? hardwareId, DateTimeOffset from, DateTimeOffset to, double threshold)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        if (!UsageMetrics.IsKnown(metric) || UsageMetrics.UsesHardwareId(metric) != !string.IsNullOrEmpty(hardwareId) || !double.IsFinite(threshold))
            throw new ArgumentException("Unknown usage metric or hardware selection.", nameof(metric));
        using var db = Open(readOnly: true);
        using var command = Command(db, """
            SELECT COALESCE(SUM(CASE WHEN average>=$threshold THEN 1 ELSE 0 END),0),COUNT(*) FROM metric_minutes
            WHERE machine=$machine AND metric=$metric AND hardware=$hardware AND minute>=$from AND minute<$to
            """, null, ("$threshold", threshold), ("$machine", machine), ("$metric", metric), ("$hardware", hardwareId ?? ""),
            ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public DateTimeOffset? EarliestRetained(string machine)
    {
        EnsureInitialized(); ValidateMachine(machine);
        using var db = Open(readOnly: true);
        return Earliest(db, machine);
    }

    public void ExportMetricsCsv(string machine, TextWriter writer, DateTimeOffset from, DateTimeOffset to)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to); ArgumentNullException.ThrowIfNull(writer);
        using var db = Open(readOnly: true);
        using var command = Command(db, """
            SELECT minute,metric,hardware,average,minimum,maximum,samples,complete,segment FROM metric_minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to ORDER BY minute,metric,hardware
            """, null, ("$machine", machine), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
        writer.WriteLine("minute_utc,metric,hardware_id,average,minimum,maximum,samples,complete,segment");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            writer.WriteLine(string.Join(',', new object?[] { Stamp(reader.GetInt64(0)).ToString("O"), reader.GetString(1), reader.GetString(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetInt64(6), reader.GetInt64(7) == 1, reader.GetString(8) }.Select(Csv)));
    }

    public void ExportApplicationsCsv(string machine, TextWriter writer, DateTimeOffset from, DateTimeOffset to)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to); ArgumentNullException.ThrowIfNull(writer);
        using var db = Open(readOnly: true);
        using var command = Command(db, """
            SELECT name,SUM(received),SUM(sent),COUNT(*) FROM application_minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to GROUP BY name ORDER BY SUM(received)+SUM(sent) DESC,name
            """, null, ("$machine", machine), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds()));
        writer.WriteLine("application,received_bytes,sent_bytes,minutes_observed");
        using var reader = command.ExecuteReader();
        while (reader.Read())
            writer.WriteLine(string.Join(',', new object?[] { reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3) }.Select(Csv)));
    }

    private static void Expire(SqliteConnection db, SqliteTransaction transaction, long cutoff)
    {
        Execute(db, "DELETE FROM metric_minutes WHERE minute<$cutoff", transaction, ("$cutoff", cutoff));
        Execute(db, "DELETE FROM application_minutes WHERE minute<$cutoff", transaction, ("$cutoff", cutoff));
        Execute(db, "DELETE FROM capture_gaps WHERE minute<$cutoff", transaction, ("$cutoff", cutoff));
    }

    private static bool EvictOldestDay(SqliteConnection db, long protectedDay)
    {
        object? oldest = Scalar(db, """
            SELECT MIN(minute) FROM (SELECT MIN(minute) AS minute FROM metric_minutes
                UNION ALL SELECT MIN(minute) FROM application_minutes UNION ALL SELECT MIN(minute) FROM capture_gaps)
            """);
        if (oldest is null or DBNull) return false;
        long day = DayFloor(Convert.ToInt64(oldest, CultureInfo.InvariantCulture));
        if (day >= protectedDay) return false;
        using var transaction = db.BeginTransaction();
        Expire(db, transaction, day + DayMilliseconds);
        transaction.Commit();
        return true;
    }

    private static DateTimeOffset? Earliest(SqliteConnection db, string machine)
    {
        object? value = ScalarWith(db, """
            SELECT MIN(minute) FROM (SELECT MIN(minute) AS minute FROM metric_minutes WHERE machine=$machine
                UNION ALL SELECT MIN(minute) FROM application_minutes WHERE machine=$machine)
            """, [("$machine", machine)]);
        return value is null or DBNull ? null : Stamp(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static long DayFloor(long milliseconds) => milliseconds - ((milliseconds % DayMilliseconds) + DayMilliseconds) % DayMilliseconds;
    private static DateTimeOffset Stamp(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    private static string Csv(object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Neutralize before quoting, including formulas hidden behind whitespace or control characters.
        bool formula = text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@';
        bool control = text.Any(char.IsControl);
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (formula || control) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private static void ValidateMachine(string machine)
    { if (string.IsNullOrWhiteSpace(machine) || machine.Length > 256) throw new ArgumentException("Invalid machine key.", nameof(machine)); }
    private static void ValidateSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length > 64 || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid usage history segment.", nameof(segment));
    }
    private static void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    { if (from >= to || to - from > TimeSpan.FromDays(RetentionDays + 1)) throw new ArgumentOutOfRangeException(nameof(from), "Usage history ranges must be ascending and within retention."); }
    private void EnsureInitialized()
    { if (!initialized) throw new InvalidOperationException("Usage history storage has not been initialized."); }
    private SqliteConnection Open(bool readOnly = false, bool create = false, bool configure = true)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumDatabaseBytes)
            throw new IOException("Usage history exceeds its size limit; the file was preserved.");
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
                throw new IOException("Usage history journal is waiting for a reader; recording has stopped to bound disk use.");
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
