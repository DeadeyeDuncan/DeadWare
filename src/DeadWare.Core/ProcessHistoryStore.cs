using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public enum ProcessRank { CpuTime, AverageMemory, Io, Gpu }

/// <summary>One application's totals for a period. <see cref="MemoryByteSeconds"/> is its Σ(memory × Δt) over the minutes it was listed.
/// The GPU members are null when no minute in the period holds GPU data for the application (never 0): <see cref="GpuSeconds"/> is its
/// GPU-seconds, <see cref="AverageGpuPercent"/> is 100 × those ÷ <see cref="ProcessResourceView.GpuCoveredSeconds"/> (a sum over processes, so it
/// can exceed 100) and <see cref="AverageGpuMemoryBytes"/> is its GPU memory while listed in minutes with GPU data.</summary>
public sealed record ProcessResourceRow(string Name, double CpuSeconds, double AverageCpuPercent, double AverageMemoryBytes,
    long IoRead, long IoWritten, long MinutesListed, bool EverythingElse, double MemoryByteSeconds)
{
    public double? GpuSeconds { get; init; }
    public double? AverageGpuPercent { get; init; }
    public double? AverageGpuMemoryBytes { get; init; }

    /// <summary>This row's part of <see cref="ProcessResourceView.RankTotal"/> for the same rank: CPU seconds, Σ(memory × Δt), I/O read + written or GPU-seconds.</summary>
    public double PartOf(ProcessRank rank) => rank switch
    {
        ProcessRank.CpuTime => CpuSeconds,
        ProcessRank.AverageMemory => MemoryByteSeconds,
        ProcessRank.Gpu => GpuSeconds ?? 0,
        _ => (double)IoRead + IoWritten,
    };
}

/// <summary>A period's ranking. <see cref="Rows"/> holds the top applications read; <see cref="ApplicationCount"/> is how many distinct
/// applications (Everything else aside) the period holds, so it exceeds the rows' count when the read's limit cut the list.</summary>
public sealed record ProcessResourceView(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ProcessResourceRow> Rows, long ApplicationCount,
    ProcessResourceRow EverythingElse, double RankTotal, long RecordedMinutes, DateTimeOffset? FirstMinute, DateTimeOffset? LastMinuteEnd,
    DateTimeOffset? EarliestRetained, long StorageBytes)
{
    /// <summary>The GPU-covered seconds of the period's recorded minutes: the divisor of every Avg GPU. 0 when none holds GPU data.</summary>
    public double GpuCoveredSeconds { get; init; }
}

/// <summary>Per-application minutes (image name and numbers only, never paths). Synchronous calls belong to one background owner;
/// never call from the collector or UI thread.</summary>
public sealed class ProcessHistoryStore(string path, long maximumDatabaseBytes = ProcessHistoryStore.MaximumDatabaseBytes)
{
    public const long MaximumDatabaseBytes = 192L * 1024 * 1024;
    public const int RetentionDays = 30, MaximumReadDays = 31, MaximumRows = 1000;
    public const int ApplicationId = 0x44575052;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    private const long MinuteMilliseconds = 60_000, DayMilliseconds = 86_400_000, FutureToleranceMilliseconds = 5 * MinuteMilliseconds;
    private const int MaximumProcessors = 4096;
    private const double TicksPerSecond = 10_000_000;
    // Saved names never contain '*' (ProcessHistory.SavedName), so no application's key is the reserved one.
    private const string EverythingElseKey = "*EVERYTHING ELSE";
    // Version 2: the GPU columns sit where Upgrade's ADD COLUMN puts them in a version 1 file, so both layouts are the same.
    // A minute without GPU-covered time holds NULL in all three, as upgraded version 1 rows do.
    private const string Schema = """
        CREATE TABLE names(id INTEGER PRIMARY KEY, upper TEXT UNIQUE NOT NULL, display TEXT NOT NULL);
        CREATE TABLE minutes(
            machine TEXT NOT NULL, minute INTEGER NOT NULL, covered REAL NOT NULL, processors INTEGER NOT NULL,
            complete INTEGER NOT NULL CHECK(complete IN(0,1)), segment TEXT NOT NULL, gpu_covered REAL NULL, PRIMARY KEY(machine,minute)) WITHOUT ROWID;
        CREATE INDEX minutes_time ON minutes(minute);
        CREATE TABLE app_minutes(
            machine TEXT NOT NULL, minute INTEGER NOT NULL, name_id INTEGER NOT NULL, cpu INTEGER NOT NULL, io_read INTEGER NOT NULL,
            io_write INTEGER NOT NULL, memory_seconds REAL NOT NULL, everything_else INTEGER NOT NULL CHECK(everything_else IN(0,1)),
            gpu_seconds REAL NULL, gpu_memory_seconds REAL NULL, PRIMARY KEY(machine,minute,name_id)) WITHOUT ROWID;
        """;
    private readonly string path = Path.GetFullPath(path);
    private bool initialized;
    // Test seam, set by reflection: runs after a read's first statement so a test can save a minute mid-read. Always null in the app.
    private Action? afterFirstRead = null;
    // Test seam, set by reflection: runs inside the upgrade transaction just before it commits, so a test can fail it. Always null in the app.
    private Action? beforeUpgradeCommit = null;

    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }
        .Where(File.Exists).Sum(file => new FileInfo(file).Length);

    /// <summary>The oldest minute kept as of <paramref name="now"/>: the start of the UTC day <see cref="RetentionDays"/> days before
    /// today. Saves, expiry and reads all use this one cutoff.</summary>
    public static DateTimeOffset RetentionCutoff(DateTimeOffset now) => Stamp(DayFloor(now.ToUnixTimeMilliseconds()) - RetentionDays * DayMilliseconds);

    public void Initialize()
    {
        if (maximumDatabaseBytes is < 65536 or > MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // An empty file is an interrupted first creation; anything else is inspected read-only before it is touched.
        bool create = !File.Exists(path) || new FileInfo(path).Length == 0;
        long version = create ? 2 : Inspect();
        using var db = Open(create: create);
        Execute(db, "PRAGMA journal_mode=WAL");
        if (create)
        {
            using var transaction = db.BeginTransaction();
            Execute(db, Schema, transaction);
            Execute(db, $"PRAGMA application_id={ApplicationId.ToString(CultureInfo.InvariantCulture)}; PRAGMA user_version=2", transaction);
            transaction.Commit();
        }
        // Only a file Inspect read as version 1 is upgraded, so a version 2 start takes no write lock; Upgrade re-reads the version under its own.
        else if (version == 1) Upgrade(db, beforeUpgradeCommit);
        Checkpoint(db); initialized = true;
    }

    /// <summary>Checks the file read-only before anything writes to it and returns its <c>user_version</c>, 1 or 2.</summary>
    private long Inspect()
    {
        try
        {
            using var db = Open(readOnly: true);
            if (Convert.ToString(Scalar(db, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) != "ok")
                throw new InvalidDataException("Process history is corrupt; the file was preserved.");
            long version = Number(db, "PRAGMA user_version");
            if (version is not (1 or 2) || Number(db, "PRAGMA application_id") != ApplicationId)
                throw new InvalidDataException("Unsupported process history; the file was preserved.");
            // A version 1 file gains the GPU columns in Upgrade; a version 2 file must already have all three.
            if (Number(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN('names','minutes','app_minutes')") != 3 ||
                (version == 2 && (Number(db, "SELECT COUNT(*) FROM pragma_table_info('app_minutes') WHERE name IN('gpu_seconds','gpu_memory_seconds')") != 2 ||
                    Number(db, "SELECT COUNT(*) FROM pragma_table_info('minutes') WHERE name='gpu_covered'") != 1)))
                throw new InvalidDataException("Incompatible process history objects; the file was preserved.");
            return version;
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 11 or 26)
        {
            throw new InvalidDataException("Process history is corrupt; the file was preserved.", error);
        }
    }

    /// <summary>Moves a version 1 file to version 2 in one transaction on the writable connection: the three GPU columns, which leave every
    /// existing row NULL, and the version. Adding a nullable column rewrites only the schema, so the journal stays small. Any failure rolls back
    /// and leaves a valid version 1 file. <paramref name="beforeCommit"/> is the test seam.</summary>
    private static void Upgrade(SqliteConnection db, Action? beforeCommit)
    {
        using var transaction = db.BeginTransaction();
        // Inspect read the version on another connection; another writer may have upgraded the file since.
        if (Convert.ToInt64(ScalarWith(db, "PRAGMA user_version", transaction), CultureInfo.InvariantCulture) != 1) return;
        Execute(db, """
            ALTER TABLE app_minutes ADD COLUMN gpu_seconds REAL NULL;
            ALTER TABLE app_minutes ADD COLUMN gpu_memory_seconds REAL NULL;
            ALTER TABLE minutes ADD COLUMN gpu_covered REAL NULL;
            PRAGMA user_version=2;
            """, transaction);
        beforeCommit?.Invoke();
        transaction.Commit();
    }

    /// <summary>Writes closed minutes. Rows older than the retention window or more than five minutes ahead of now are ignored.
    /// A minute already stored from another segment (a restart inside that minute) keeps its first rows and is marked incomplete;
    /// the same segment repeated is ignored, so a retried batch cannot double count. Expired days leave first, as <see cref="Expire(DateTimeOffset)"/> does.</summary>
    public void Append(string machine, IReadOnlyList<ProcessMinuteBatch> batches, DateTimeOffset now)
    {
        EnsureInitialized(); ValidateMachine(machine); ArgumentNullException.ThrowIfNull(batches);
        foreach (var batch in batches) ValidateBatch(batch);
        if (batches.Count == 0) return;
        long today = DayFloor(now.ToUnixTimeMilliseconds());
        long cutoff = RetentionCutoff(now).ToUnixTimeMilliseconds(), latest = now.ToUnixTimeMilliseconds() + FutureToleranceMilliseconds;
        using var db = Open();
        ExpireByDay(db, cutoff);
        while (true)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                var ids = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var batch in batches)
                {
                    long minute = batch.Minute.ToUnixTimeMilliseconds();
                    if (minute < cutoff || minute > latest) continue;
                    object? existing = ScalarWith(db, "SELECT segment FROM minutes WHERE machine=$machine AND minute=$minute", transaction,
                        ("$machine", machine), ("$minute", minute));
                    if (existing is string first)
                    {
                        if (first != batch.Segment)
                            Execute(db, "UPDATE minutes SET complete=0 WHERE machine=$machine AND minute=$minute", transaction, ("$machine", machine), ("$minute", minute));
                        continue;
                    }
                    // A minute without GPU-covered time stores NULL, never 0, as version 1 minutes read.
                    Execute(db, """
                        INSERT INTO minutes(machine,minute,covered,processors,complete,segment,gpu_covered)
                        VALUES($machine,$minute,$covered,$processors,$complete,$segment,$gpu)
                        """, transaction, ("$machine", machine), ("$minute", minute), ("$covered", batch.CoveredSeconds),
                        ("$processors", batch.ProcessorCount), ("$complete", batch.Complete ? 1 : 0), ("$segment", batch.Segment),
                        ("$gpu", batch.GpuCoveredSeconds == 0 ? DBNull.Value : batch.GpuCoveredSeconds));
                    foreach (var app in batch.Applications)
                    {
                        string key = KeyOf(app);
                        if (!ids.TryGetValue(key, out long id))
                        {
                            Execute(db, "INSERT OR IGNORE INTO names(upper,display) VALUES($upper,$display)", transaction,
                                ("$upper", key), ("$display", app.EverythingElse ? ProcessHistory.EverythingElse : app.Name));
                            id = Convert.ToInt64(ScalarWith(db, "SELECT id FROM names WHERE upper=$upper", transaction, ("$upper", key)), CultureInfo.InvariantCulture);
                            ids[key] = id;
                        }
                        Execute(db, """
                            INSERT INTO app_minutes(machine,minute,name_id,cpu,io_read,io_write,memory_seconds,everything_else,gpu_seconds,gpu_memory_seconds)
                            VALUES($machine,$minute,$name,$cpu,$read,$write,$memory,$other,$gpu,$gpuMemory)
                            """, transaction, ("$machine", machine), ("$minute", minute), ("$name", id), ("$cpu", app.CpuTime),
                            ("$read", app.IoRead), ("$write", app.IoWrite), ("$memory", app.MemoryByteSeconds), ("$other", app.EverythingElse ? 1 : 0),
                            ("$gpu", (object?)app.GpuSeconds ?? DBNull.Value), ("$gpuMemory", (object?)app.GpuMemoryByteSeconds ?? DBNull.Value));
                    }
                }
                transaction.Commit();
                break;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                // The file reached its cap: drop the oldest whole day and retry, never the day being recorded.
                if (!EvictOldestDay(db, today))
                    throw new IOException("Process history is full and only the current day remains; the batch was not saved.", error);
            }
        }
        Checkpoint(db);
    }

    /// <summary>Deletes every minute before <see cref="RetentionCutoff"/>(<paramref name="now"/>), with its application rows, for every
    /// machine. Each UTC day is its own committed transaction followed by a checkpoint, so a long absence never writes one large journal.
    /// The history worker calls this at start and when the UTC day changes, so days leave while recording is off too.</summary>
    public void Expire(DateTimeOffset now)
    {
        EnsureInitialized();
        using var db = Open();
        ExpireByDay(db, RetentionCutoff(now).ToUnixTimeMilliseconds());
    }

    /// <summary>Totals per application for the minutes starting in [from, to). Everything else is kept apart from the ranked rows.
    /// Minutes before <see cref="RetentionCutoff"/>(<paramref name="now"/>) are never read, even before they are expired.</summary>
    public ProcessResourceView ReadApplications(string machine, DateTimeOffset from, DateTimeOffset to, ProcessRank rank, DateTimeOffset now, int limit = 200)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        if (limit is < 1 or > MaximumRows) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!Enum.IsDefined(rank)) throw new ArgumentOutOfRangeException(nameof(rank));
        using var db = Open(readOnly: true);
        // One deferred read transaction: every statement sees the snapshot of the first, even while the worker saves a minute.
        using var transaction = db.BeginTransaction(deferred: true);
        long cutoff = RetentionCutoff(now).ToUnixTimeMilliseconds();
        long fromMs = Math.Max(from.ToUnixTimeMilliseconds(), cutoff), toMs = to.ToUnixTimeMilliseconds();
        (string, object)[] args = [("$machine", machine), ("$from", fromMs), ("$to", toMs)];
        long recorded = 0; double capacity = 0, gpuCovered = 0; long? first = null, last = null;
        using (var command = Command(db, """
            SELECT COUNT(*),MIN(minute),MAX(minute),TOTAL(processors*covered),TOTAL(gpu_covered) FROM minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to
            """, transaction, args))
        using (var reader = command.ExecuteReader())
        {
            reader.Read(); recorded = reader.GetInt64(0);
            if (recorded > 0) { first = reader.GetInt64(1); last = reader.GetInt64(2); }
            capacity = reader.GetDouble(3); gpuCovered = reader.GetDouble(4);
        }
        afterFirstRead?.Invoke();
        string order = rank switch
        {
            ProcessRank.CpuTime => "TOTAL(a.cpu)",
            ProcessRank.AverageMemory => "TOTAL(a.memory_seconds)/TOTAL(m.covered)",
            ProcessRank.Io => "TOTAL(a.io_read)+TOTAL(a.io_write)",
            _ => "TOTAL(a.gpu_seconds)"
        };
        // GPU memory is averaged over the covered time of the minutes that hold it, so a minute without GPU data never dilutes it.
        const string Columns = "TOTAL(a.cpu),TOTAL(a.io_read),TOTAL(a.io_write),TOTAL(a.memory_seconds),COUNT(*),TOTAL(m.covered)," +
            "SUM(a.gpu_seconds),SUM(a.gpu_memory_seconds),TOTAL(CASE WHEN a.gpu_memory_seconds IS NOT NULL THEN m.gpu_covered END)";
        const string From = """
            FROM app_minutes a JOIN minutes m ON m.machine=a.machine AND m.minute=a.minute JOIN names n ON n.id=a.name_id
            WHERE a.machine=$machine AND a.minute>=$from AND a.minute<$to
            """;
        var rows = new List<ProcessResourceRow>(); long applications = 0;
        // The window count sees every group before LIMIT, so the same pass also counts the applications beyond the rows read.
        using (var command = Command(db, $"SELECT n.display,{Columns},COUNT(*) OVER () {From} AND a.everything_else=0 GROUP BY a.name_id ORDER BY {order} DESC,n.display LIMIT $limit",
            transaction, [.. args, ("$limit", limit)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { rows.Add(Row(reader.GetString(0), reader, 1, capacity, gpuCovered, false)); applications = reader.GetInt64(10); }
        ProcessResourceRow other;
        using (var command = Command(db, $"SELECT {Columns} {From} AND a.everything_else=1", transaction, args))
        using (var reader = command.ExecuteReader())
        { reader.Read(); other = Row(ProcessHistory.EverythingElse, reader, 0, capacity, gpuCovered, true); }
        double total;
        using (var command = Command(db, """
            SELECT TOTAL(cpu),TOTAL(io_read)+TOTAL(io_write),TOTAL(memory_seconds),TOTAL(gpu_seconds) FROM app_minutes
            WHERE machine=$machine AND minute>=$from AND minute<$to
            """, transaction, args))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            total = rank switch { ProcessRank.CpuTime => reader.GetDouble(0) / TicksPerSecond, ProcessRank.AverageMemory => reader.GetDouble(2), ProcessRank.Io => reader.GetDouble(1), _ => reader.GetDouble(3) };
        }
        return new(from, to, rows, applications, other, total, recorded, first is { } f ? Stamp(f) : null, last is { } l ? Stamp(l + MinuteMilliseconds) : null,
            Earliest(db, transaction, machine, cutoff), StorageBytes) { GpuCoveredSeconds = gpuCovered };
    }

    /// <summary>One application's metric over time, bucketed like <see cref="UsageHistoryStore.ReadMetric"/>. CPU is a percentage of the
    /// machine's capacity, memory is bytes, I/O is read plus written bytes per second and GPU is a percentage of GPU-covered time. Minutes the
    /// application was not listed (for GPU, listed without GPU data) are gaps.
    /// Minutes before <see cref="RetentionCutoff"/>(<paramref name="now"/>) are never read; the grid still starts at <paramref name="from"/>.</summary>
    public UsageHistoryView ReadSeries(string machine, string name, ProcessRank metric, DateTimeOffset from, DateTimeOffset to, int maxPoints, DateTimeOffset now)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateRange(from, to);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (maxPoints is < 1 or > 4000) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        maxPoints = Math.Min(maxPoints, MaximumRows);
        using var db = Open(readOnly: true);
        using var transaction = db.BeginTransaction(deferred: true);
        long fromMs = from.ToUnixTimeMilliseconds(), toMs = to.ToUnixTimeMilliseconds(), cutoff = RetentionCutoff(now).ToUnixTimeMilliseconds();
        long bucket = Math.Max(MinuteMilliseconds, (long)Math.Ceiling((toMs - fromMs) / (double)maxPoints));
        string key = name.Equals(ProcessHistory.EverythingElse, StringComparison.OrdinalIgnoreCase) ? EverythingElseKey : name.ToUpperInvariant();
        (string, object)[] args = [("$machine", machine), ("$key", key), ("$from", fromMs), ("$since", Math.Max(fromMs, cutoff)), ("$to", toMs)];
        // Σ value, Σ weight and the per-minute value; the average is the ratio of the sums.
        (string Sum, string Weight, string Each) = metric switch
        {
            ProcessRank.CpuTime => ($"TOTAL(a.cpu)/{TicksPerSecond.ToString("R", CultureInfo.InvariantCulture)}*100", "TOTAL(m.processors*m.covered)",
                $"100.0*a.cpu/{TicksPerSecond.ToString("R", CultureInfo.InvariantCulture)}/(m.processors*m.covered)"),
            ProcessRank.AverageMemory => ("TOTAL(a.memory_seconds)", "TOTAL(m.covered)", "a.memory_seconds/m.covered"),
            ProcessRank.Io => ("TOTAL(a.io_read)+TOTAL(a.io_write)", "TOTAL(m.covered)", "(a.io_read*1.0+a.io_write)/m.covered"),
            _ => ("100.0*TOTAL(a.gpu_seconds)", "TOTAL(m.gpu_covered)", "100.0*a.gpu_seconds/m.gpu_covered")
        };
        string From = $"""
            FROM app_minutes a JOIN minutes m ON m.machine=a.machine AND m.minute=a.minute JOIN names n ON n.id=a.name_id
            WHERE a.machine=$machine AND n.upper=$key AND a.minute>=$since AND a.minute<$to
            {(metric == ProcessRank.Gpu ? "AND a.gpu_seconds IS NOT NULL AND m.gpu_covered>0" : "")}
            """;
        var buckets = new List<UsageHistoryBucket>();
        using (var command = Command(db, $"""
            SELECT (a.minute-$from)/$bucket,{Sum},{Weight},MIN({Each}),MAX({Each}),COUNT(*),SUM(m.complete)
            {From} GROUP BY (a.minute-$from)/$bucket ORDER BY 1
            """, transaction, [.. args, ("$bucket", bucket)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                long start = fromMs + reader.GetInt64(0) * bucket;
                long minutes = reader.GetInt64(5);
                buckets.Add(new(Stamp(start), Stamp(Math.Min(start + bucket, toMs)), reader.GetDouble(1) / reader.GetDouble(2), reader.GetDouble(3),
                    reader.GetDouble(4), minutes, reader.GetInt64(6), minutes));
            }
        afterFirstRead?.Invoke();
        Summary statistics; long listed;
        using (var command = Command(db, $"SELECT {Sum},{Weight},MIN({Each}),MAX({Each}),COUNT(*) {From}", transaction, args))
        using (var reader = command.ExecuteReader())
        {
            reader.Read(); listed = reader.GetInt64(4);
            statistics = listed == 0 ? new(null, null, null) : new(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(0) / reader.GetDouble(1));
        }
        return new(from, to, bucket, buckets, statistics, listed, Earliest(db, transaction, machine, cutoff), StorageBytes);
    }

    private static ProcessResourceRow Row(string name, SqliteDataReader reader, int offset, double capacity, double gpuCovered, bool everythingElse)
    {
        double cpu = reader.GetDouble(offset) / TicksPerSecond, memory = reader.GetDouble(offset + 3), covered = reader.GetDouble(offset + 5);
        double? gpu = reader.IsDBNull(offset + 6) ? null : reader.GetDouble(offset + 6), gpuMemory = reader.IsDBNull(offset + 7) ? null : reader.GetDouble(offset + 7);
        double gpuMemoryCovered = reader.GetDouble(offset + 8);
        return new(name, cpu, capacity > 0 ? 100 * cpu / capacity : 0, covered > 0 ? memory / covered : 0,
            Saturate(reader.GetDouble(offset + 1)), Saturate(reader.GetDouble(offset + 2)), reader.GetInt64(offset + 4), everythingElse, memory)
        {
            GpuSeconds = gpu,
            AverageGpuPercent = gpu is { } seconds && gpuCovered > 0 ? 100 * seconds / gpuCovered : null,
            AverageGpuMemoryBytes = gpuMemory is { } byteSeconds && gpuMemoryCovered > 0 ? byteSeconds / gpuMemoryCovered : null,
        };
    }

    private static long Saturate(double value) => value >= long.MaxValue ? long.MaxValue : (long)value;
    private static string KeyOf(ProcessAppMinute app) => app.EverythingElse ? EverythingElseKey : app.Name.ToUpperInvariant();

    private static void ValidateBatch(ProcessMinuteBatch batch)
    {
        if (batch is null || batch.Applications is null || batch.Minute.UtcTicks % TimeSpan.TicksPerMinute != 0 || !double.IsFinite(batch.CoveredSeconds) ||
            batch.CoveredSeconds <= 0 || batch.ProcessorCount is < 1 or > MaximumProcessors ||
            string.IsNullOrWhiteSpace(batch.Segment) || batch.Segment.Length > 64 || batch.Segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            !double.IsFinite(batch.GpuCoveredSeconds) || batch.GpuCoveredSeconds < 0 ||
            batch.Applications.Count > ProcessHistory.MaximumApplications + 1)
            throw new ArgumentException("Invalid process history batch.", nameof(batch));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in batch.Applications)
        {
            if (app is null || app.CpuTime < 0 || app.IoRead < 0 || app.IoWrite < 0 || !double.IsFinite(app.MemoryByteSeconds) || app.MemoryByteSeconds < 0 ||
                !ValidGpu(app.GpuSeconds, batch.GpuCoveredSeconds) || !ValidGpu(app.GpuMemoryByteSeconds, batch.GpuCoveredSeconds) ||
                (!app.EverythingElse && (app.Name is null || ProcessHistory.SavedName(app.Name) != app.Name)) ||
                !keys.Add(KeyOf(app)))
                throw new ArgumentException("Invalid process history application minute.", nameof(batch));
        }
    }

    /// <summary>An application's GPU value is null, or finite and at least 0 in a minute with GPU-covered time; a minute without any holds only nulls.</summary>
    private static bool ValidGpu(double? value, double gpuCovered) => value is not { } gpu || gpuCovered > 0 && double.IsFinite(gpu) && gpu >= 0;

    /// <summary>Deletes every minute before <paramref name="cutoff"/> and its application rows, for every machine.</summary>
    private static void DeleteMinutes(SqliteConnection db, SqliteTransaction transaction, long cutoff)
    {
        var machines = new List<string>();
        using (var command = Command(db, "SELECT DISTINCT machine FROM minutes", transaction))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) machines.Add(reader.GetString(0));
        foreach (var machine in machines)
        {
            Execute(db, "DELETE FROM app_minutes WHERE machine=$machine AND minute<$cutoff", transaction, ("$machine", machine), ("$cutoff", cutoff));
            Execute(db, "DELETE FROM minutes WHERE machine=$machine AND minute<$cutoff", transaction, ("$machine", machine), ("$cutoff", cutoff));
        }
    }

    /// <summary>Deletes the names no saved minute lists any more. It walks the whole of app_minutes, so it runs once per expiry or eviction.</summary>
    private static void DeleteOrphanedNames(SqliteConnection db, SqliteTransaction transaction)
        => Execute(db, "DELETE FROM names WHERE id NOT IN(SELECT DISTINCT name_id FROM app_minutes)", transaction);

    /// <summary>Deletes every minute before <paramref name="cutoff"/>, oldest UTC day first: one committed transaction per day and a
    /// checkpoint after each, so the journal holds about one day of deletions at a time. The pass that removes the last expired day
    /// also deletes the names no remaining minute lists, in the same transaction: an interruption before it leaves an expired minute,
    /// so the next expiry comes back to that pass, and never leaves expired names behind with nothing left to expire.</summary>
    private static void ExpireByDay(SqliteConnection db, long cutoff)
    {
        while (Scalar(db, "SELECT MIN(minute) FROM minutes") is long oldest && oldest < cutoff)
        {
            using (var transaction = db.BeginTransaction())
            {
                DeleteMinutes(db, transaction, Math.Min(DayFloor(oldest) + DayMilliseconds, cutoff));
                if (ScalarWith(db, "SELECT MIN(minute) FROM minutes", transaction) is not long left || left >= cutoff) DeleteOrphanedNames(db, transaction);
                transaction.Commit();
            }
            Checkpoint(db);
        }
    }

    private static bool EvictOldestDay(SqliteConnection db, long protectedDay)
    {
        object? oldest = Scalar(db, "SELECT MIN(minute) FROM minutes");
        if (oldest is null or DBNull) return false;
        long day = DayFloor(Convert.ToInt64(oldest, CultureInfo.InvariantCulture));
        if (day >= protectedDay) return false;
        using var transaction = db.BeginTransaction();
        DeleteMinutes(db, transaction, day + DayMilliseconds); DeleteOrphanedNames(db, transaction);
        transaction.Commit();
        return true;
    }

    private static DateTimeOffset? Earliest(SqliteConnection db, SqliteTransaction transaction, string machine, long cutoff)
    {
        object? value = ScalarWith(db, "SELECT MIN(minute) FROM minutes WHERE machine=$machine AND minute>=$cutoff", transaction,
            ("$machine", machine), ("$cutoff", cutoff));
        return value is null or DBNull ? null : Stamp(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static long DayFloor(long milliseconds) => milliseconds - ((milliseconds % DayMilliseconds) + DayMilliseconds) % DayMilliseconds;
    private static DateTimeOffset Stamp(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    private static void ValidateMachine(string machine)
    { if (string.IsNullOrWhiteSpace(machine) || machine.Length > 256) throw new ArgumentException("Invalid machine key.", nameof(machine)); }
    private static void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    { if (from >= to || to - from > TimeSpan.FromDays(MaximumReadDays)) throw new ArgumentOutOfRangeException(nameof(from), "Process history ranges must be ascending and at most 31 days."); }
    private void EnsureInitialized()
    { if (!initialized) throw new InvalidOperationException("Process history storage has not been initialized."); }

    private SqliteConnection Open(bool readOnly = false, bool create = false)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumDatabaseBytes)
            throw new IOException("Process history exceeds its size limit; the file was preserved.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            db.Open();
            if (!readOnly) Configure(db);
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
                throw new IOException("Process history journal is waiting for a reader; recording has stopped to bound disk use.");
        }
        long pageSize = Number(db, "PRAGMA page_size");
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
    private static object? ScalarWith(SqliteConnection db, string sql, SqliteTransaction? transaction, params (string Name, object Value)[] args)
    { using var command = Command(db, sql, transaction, args); return command.ExecuteScalar(); }
    private static long Number(SqliteConnection db, string sql) => Convert.ToInt64(Scalar(db, sql), CultureInfo.InvariantCulture);
}
