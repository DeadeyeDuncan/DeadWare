using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

/// <summary>Local transactional history. Call synchronous methods on background threads, never the collector.</summary>
public sealed class ThermalStore
{
    public const long MaximumDatabaseBytes = 256L * 1024 * 1024;
    private const int SchemaVersion = 2;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private volatile bool initialized;

    public ThermalStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
    {
        if (maximumDatabaseBytes is < 65_536 or > MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        this.path = Path.GetFullPath(path); this.maximumDatabaseBytes = maximumDatabaseBytes;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(false, true);
        long version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is not (0 or 1 or SchemaVersion)) throw new InvalidDataException("This history database belongs to a newer DeadWare version.");
        Execute(db, "PRAGMA journal_mode=WAL");
        using var transaction = db.BeginTransaction();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS devices(device TEXT NOT NULL, configuration TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(device,configuration));
            CREATE TABLE IF NOT EXISTS windows(device TEXT NOT NULL, configuration TEXT NOT NULL, session TEXT NOT NULL,
                monotonic REAL NOT NULL, start INTEGER NOT NULL, finish INTEGER NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(device,configuration,session,monotonic));
            CREATE INDEX IF NOT EXISTS windows_time ON windows(device,configuration,start);
            CREATE TABLE IF NOT EXISTS summaries(id INTEGER PRIMARY KEY, device TEXT NOT NULL, configuration TEXT NOT NULL,
                session TEXT NOT NULL, hour INTEGER NOT NULL, start INTEGER NOT NULL, finish INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS summaries_time ON summaries(device,configuration,start);
            CREATE INDEX IF NOT EXISTS summaries_bucket ON summaries(device,configuration,session,hour);
            CREATE TABLE IF NOT EXISTS reference_periods(id TEXT PRIMARY KEY, device TEXT NOT NULL, configuration TEXT NOT NULL,
                created INTEGER NOT NULL, active INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL, header TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS reference_device ON reference_periods(device,configuration,created);
            CREATE TABLE IF NOT EXISTS warnings(id TEXT PRIMARY KEY, device TEXT NOT NULL, configuration TEXT NOT NULL,
                created INTEGER NOT NULL, acknowledged INTEGER NOT NULL DEFAULT 0, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS warnings_device ON warnings(device,configuration,created);
            """, transaction);
        if (version == 1)
        {
            Execute(db, "ALTER TABLE reference_periods ADD COLUMN header TEXT NOT NULL DEFAULT ''", transaction);
            var headers = new List<(string Id, string Header)>();
            using (var command = Command(db, "SELECT id,payload FROM reference_periods", transaction))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) headers.Add((reader.GetString(0), EncodeReferenceHeader(DecodeReference(reader.GetString(1)))));
            foreach (var header in headers)
                Execute(db, "UPDATE reference_periods SET header=$header WHERE id=$id", transaction, ("$header", header.Header), ("$id", header.Id));
        }
        Execute(db, "PRAGMA user_version=2", transaction);
        transaction.Commit();
        initialized = true;
    }

    public void Append(IReadOnlyList<ThermalWindow> windows, DateTimeOffset now, int detailedDays = 30, int summaryDays = 90)
    {
        EnsureInitialized();
        if (detailedDays is not (7 or 30) || summaryDays is not (30 or 90) || summaryDays < detailedDays)
            throw new ArgumentOutOfRangeException(nameof(detailedDays), "Retention must be 7/30 detailed days and 30/90 summary days.");
        if (windows.Count > 100_000) throw new ArgumentException("History batch is too large.", nameof(windows));
        using var db = Open();
        using (var transaction = db.BeginTransaction())
        {
            // Whole-hour expiry avoids an overlapping partial hourly summary at the detail boundary.
            long detailCutoff = Hour(now.AddDays(-detailedDays));
            long summaryCutoff = Hour(now.AddDays(-summaryDays));
            Execute(db, "DELETE FROM windows WHERE start < $cutoff", transaction, ("$cutoff", detailCutoff));
            Execute(db, "DELETE FROM summaries WHERE finish < $cutoff", transaction, ("$cutoff", summaryCutoff));
            foreach (var window in windows.OrderBy(w => w.Start).ThenBy(w => w.StartMonotonic))
            {
                ValidateWindow(window);
                // Append accepts current detailed observations, not a replay/import of expired rows.
                if (Milliseconds(window.Start) < detailCutoff) continue;
                PutDevice(db, transaction, window.Device);
                int inserted = Execute(db, """
                    INSERT INTO windows(device,configuration,session,monotonic,start,finish,payload)
                    VALUES($device,$configuration,$session,$monotonic,$start,$finish,$payload)
                    ON CONFLICT(device,configuration,session,monotonic) DO NOTHING
                    """, transaction, ("$device", window.Device.Id), ("$configuration", window.Device.ConfigurationId),
                    ("$session", window.SessionId), ("$monotonic", window.StartMonotonic),
                    ("$start", Milliseconds(window.Start)), ("$finish", Milliseconds(window.End)), ("$payload", EncodeWindow(window)));
                if (inserted > 0) AddToSummary(db, transaction, window);
            }
            Execute(db, "DELETE FROM windows WHERE start < $cutoff", transaction, ("$cutoff", detailCutoff));
            transaction.Commit();
        }
        Checkpoint(db);
    }

    public IReadOnlyList<ThermalDevice> Devices()
    {
        EnsureInitialized(); using var db = Open(true);
        return ReadPayloads<ThermalDevice>(db, "SELECT payload FROM devices ORDER BY device,configuration LIMIT 512");
    }

    public IReadOnlyList<ThermalWindow> Read(string deviceId, string configurationId, DateTimeOffset from,
        DateTimeOffset to, int maxPoints = 2000, bool detailedOnly = false)
    {
        EnsureInitialized(); ValidateRange(from, to);
        if (maxPoints is < 1 or > 100_081) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        using var db = Open(true);
        if (detailedOnly)
        {
            var rows = ReadPayloads<ThermalWindow>(db, """
                SELECT payload FROM (SELECT payload,start FROM windows WHERE device=$device AND configuration=$configuration
                    AND start >= $from AND finish <= $to ORDER BY start DESC LIMIT $limit) ORDER BY start
                """, ("$device", deviceId), ("$configuration", configurationId), ("$from", Milliseconds(from)),
                ("$to", Milliseconds(to)), ("$limit", maxPoints));
            var device = FindDevice(db, deviceId, configurationId);
            return rows.Select(row => Hydrate(row, device)).ToArray();
        }
        double bucketSeconds = Math.Max(60, Math.Ceiling((to - from).TotalSeconds / maxPoints / 60) * 60);
        var result = new Queue<ThermalWindow>();
        ThermalWindow? pending = null;
        foreach (var window in EnumerateHistory(db, deviceId, configurationId, from, to))
        {
            bool sameBucket = pending is not null && Math.Floor((pending.Start - from).TotalSeconds / bucketSeconds) == Math.Floor((window.Start - from).TotalSeconds / bucketSeconds);
            if (pending is not null && sameBucket && Contiguous(pending, window) && BaseQuality(pending.Quality) == BaseQuality(window.Quality))
                pending = Merge(pending, window, Math.Max(60, (int)bucketSeconds), "Aggregated view; " + BaseQuality(pending.Quality));
            else
            {
                if (pending is not null) EnqueueBounded(result, pending, maxPoints);
                pending = window;
            }
        }
        if (pending is not null) EnqueueBounded(result, pending, maxPoints);
        return result.ToArray();
    }

    public void SaveReference(ThermalReference reference)
    {
        EnsureInitialized(); ValidateReference(reference);
        using var db = Open(); using var transaction = db.BeginTransaction();
        if (Scalar(db, "SELECT id FROM reference_periods WHERE id=$id", transaction, ("$id", reference.Id)) is not null)
            throw new InvalidOperationException("Saved references are immutable. Save a new reference instead.");
        PutDevice(db, transaction, reference.Windows[0].Device);
        Execute(db, "UPDATE reference_periods SET active=0 WHERE device=$device AND configuration=$configuration", transaction,
            ("$device", reference.DeviceId), ("$configuration", reference.ConfigurationId));
        Execute(db, """
            INSERT INTO reference_periods(id,device,configuration,created,active,payload,header)
            VALUES($id,$device,$configuration,$created,1,$payload,$header)
            """, transaction, ("$id", reference.Id), ("$device", reference.DeviceId), ("$configuration", reference.ConfigurationId),
            ("$created", Milliseconds(reference.Created)), ("$payload", EncodeReference(reference)), ("$header", EncodeReferenceHeader(reference)));
        Execute(db, """
            DELETE FROM reference_periods WHERE device=$device AND configuration=$configuration AND id NOT IN
            (SELECT id FROM reference_periods WHERE device=$device AND configuration=$configuration ORDER BY active DESC,created DESC,id DESC LIMIT 10)
            """, transaction, ("$device", reference.DeviceId), ("$configuration", reference.ConfigurationId));
        transaction.Commit(); Checkpoint(db);
    }

    public IReadOnlyList<ThermalReference> References(string deviceId, string configurationId)
    {
        EnsureInitialized(); using var db = Open(true);
        return ReadPayloads<ThermalReference>(db, "SELECT payload FROM reference_periods WHERE device=$device AND configuration=$configuration ORDER BY created DESC,id DESC LIMIT 10",
            ("$device", deviceId), ("$configuration", configurationId));
    }

    /// <summary>Lightweight selector metadata only: Windows is deliberately empty. No compressed history is read or decoded.</summary>
    public IReadOnlyList<ThermalReference> ReferenceHeaders(string deviceId, string configurationId)
    {
        EnsureInitialized(); using var db = Open(true);
        return ReadPayloads<ThermalReference>(db, "SELECT header FROM reference_periods WHERE device=$device AND configuration=$configuration ORDER BY created DESC,id DESC LIMIT 10",
            ("$device", deviceId), ("$configuration", configurationId));
    }

    public ThermalReference? ActiveReference(string deviceId, string configurationId)
    {
        EnsureInitialized(); using var db = Open(true);
        return ReadPayloads<ThermalReference>(db, "SELECT payload FROM reference_periods WHERE device=$device AND configuration=$configuration AND active=1 LIMIT 1",
            ("$device", deviceId), ("$configuration", configurationId)).FirstOrDefault();
    }

    public void ActivateReference(string id)
    {
        EnsureInitialized(); using var db = Open(); using var transaction = db.BeginTransaction();
        var reference = ReadPayloads<ThermalReference>(db, "SELECT payload FROM reference_periods WHERE id=$id", transaction, ("$id", id)).SingleOrDefault()
            ?? throw new InvalidOperationException("The reference is no longer available.");
        Execute(db, "UPDATE reference_periods SET active=0 WHERE device=$device AND configuration=$configuration", transaction,
            ("$device", reference.DeviceId), ("$configuration", reference.ConfigurationId));
        Execute(db, "UPDATE reference_periods SET active=1 WHERE id=$id", transaction, ("$id", id));
        transaction.Commit();
    }

    public void AddWarning(CoolingWarning warning)
    {
        EnsureInitialized();
        if (!double.IsFinite(warning.Delta)) throw new ArgumentException("Warning delta must be finite.", nameof(warning));
        using var db = Open(); using var transaction = db.BeginTransaction();
        Execute(db, """
            INSERT INTO warnings(id,device,configuration,created,acknowledged,payload) VALUES($id,$device,$configuration,$created,$ack,$payload)
            ON CONFLICT(id) DO NOTHING
            """, transaction, ("$id", warning.Id), ("$device", warning.DeviceId), ("$configuration", warning.ConfigurationId),
            ("$created", Milliseconds(warning.Created)), ("$ack", warning.Acknowledged ? 1 : 0), ("$payload", JsonSerializer.Serialize(warning)));
        Execute(db, "DELETE FROM warnings WHERE id NOT IN (SELECT id FROM warnings ORDER BY created DESC,id DESC LIMIT 1000)", transaction);
        transaction.Commit();
    }

    public IReadOnlyList<CoolingWarning> Warnings(string deviceId, string configurationId)
    {
        EnsureInitialized(); using var db = Open(true);
        using var command = Command(db, "SELECT payload,acknowledged FROM warnings WHERE device=$device AND configuration=$configuration ORDER BY created DESC,id DESC LIMIT 1000", null,
            ("$device", deviceId), ("$configuration", configurationId));
        using var reader = command.ExecuteReader(); var results = new List<CoolingWarning>();
        while (reader.Read()) results.Add(Deserialize<CoolingWarning>(reader.GetString(0)) with { Acknowledged = reader.GetInt32(1) != 0 });
        return results;
    }

    public void AcknowledgeWarning(string id)
    {
        EnsureInitialized(); using var db = Open();
        Execute(db, "UPDATE warnings SET acknowledged=1 WHERE id=$id", null, ("$id", id));
    }

    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }.Sum(file => File.Exists(file) ? new FileInfo(file).Length : 0);

    public int ExportCsv(string deviceId, string configurationId, DateTimeOffset from, DateTimeOffset to, string outputPath)
    {
        EnsureInitialized(); ValidateRange(from, to);
        using var db = Open(true);
        using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine("start_utc,end_utc,device,kind,configuration,resolution_seconds,duration_seconds,samples,temperature_count,temperature_min_c,temperature_mean_c,temperature_max_c,load_mean_percent,power_mean_w,clock_mean_mhz,fan_mean_rpm,eligible,quality");
        int count = 0;
        foreach (var row in EnumerateHistory(db, deviceId, configurationId, from, to))
        {
            writer.WriteLine(string.Join(',', new[] { row.Start.UtcDateTime.ToString("O"), row.End.UtcDateTime.ToString("O"),
                Csv(row.Device.Name), Csv(row.Device.Kind), Csv(row.Device.ConfigurationId), Invariant(row.ResolutionSeconds),
                Invariant(row.DurationSeconds), Invariant(row.SampleCount), Invariant(row.Temperature.Count), Invariant(row.Temperature.Minimum),
                Invariant(row.Temperature.Average), Invariant(row.Temperature.Maximum), Invariant(row.Load.Average), Invariant(row.Power.Average),
                Invariant(row.Clock.Average), Invariant(row.FanRpm.Average), row.Eligible ? "true" : "false", Csv(row.Quality) }));
            count++;
        }
        return count;
    }

    private static IEnumerable<ThermalWindow> EnumerateHistory(SqliteConnection db, string deviceId, string configurationId, DateTimeOffset from, DateTimeOffset to)
    {
        var device = FindDevice(db, deviceId, configurationId);
        using var command = Command(db, """
            SELECT payload FROM (
                SELECT start,payload FROM windows WHERE device=$device AND configuration=$configuration AND start >= $from AND finish <= $to
                UNION ALL
                SELECT s.start,s.payload FROM summaries s WHERE s.device=$device AND s.configuration=$configuration AND s.start >= $from AND s.finish <= $to
                AND NOT EXISTS(SELECT 1 FROM windows w WHERE w.device=s.device AND w.configuration=s.configuration AND w.session=s.session AND w.start <= s.finish AND w.finish >= s.start)
            ) ORDER BY start
            """, null, ("$device", deviceId), ("$configuration", configurationId), ("$from", Milliseconds(from)), ("$to", Milliseconds(to)));
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return Hydrate(Deserialize<ThermalWindow>(reader.GetString(0)), device);
    }

    private static void AddToSummary(SqliteConnection db, SqliteTransaction transaction, ThermalWindow window)
    {
        long hour = Hour(window.Start); long? id = null; ThermalWindow? prior = null;
        using (var command = Command(db, "SELECT id,payload FROM summaries WHERE device=$device AND configuration=$configuration AND session=$session AND hour=$hour ORDER BY finish DESC,id DESC LIMIT 1",
            transaction, ("$device", window.Device.Id), ("$configuration", window.Device.ConfigurationId), ("$session", window.SessionId), ("$hour", hour)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { id = reader.GetInt64(0); prior = Deserialize<ThermalWindow>(reader.GetString(1)); }
        string quality = "Hourly summary; " + BaseQuality(window.Quality);
        if (prior is not null && Contiguous(prior, window) && BaseQuality(prior.Quality) == BaseQuality(window.Quality))
        {
            var merged = Merge(prior, window, 3600, quality);
            Execute(db, "UPDATE summaries SET finish=$finish,payload=$payload WHERE id=$id", transaction,
                ("$finish", Milliseconds(merged.End)), ("$payload", EncodeWindow(merged)), ("$id", id!));
        }
        else
        {
            var summary = window with { ResolutionSeconds = 3600, Eligible = false, Quality = quality };
            Execute(db, "INSERT INTO summaries(device,configuration,session,hour,start,finish,payload) VALUES($device,$configuration,$session,$hour,$start,$finish,$payload)", transaction,
                ("$device", window.Device.Id), ("$configuration", window.Device.ConfigurationId), ("$session", window.SessionId), ("$hour", hour),
                ("$start", Milliseconds(window.Start)), ("$finish", Milliseconds(window.End)), ("$payload", EncodeWindow(summary)));
        }
    }

    private static bool Contiguous(ThermalWindow a, ThermalWindow b) => a.Device.Id == b.Device.Id && a.Device.ConfigurationId == b.Device.ConfigurationId &&
        a.SessionId == b.SessionId && b.Start > a.Start && (b.Start - a.End).TotalSeconds is >= 0 and <= 4 &&
        b.StartMonotonic - (a.StartMonotonic + a.DurationSeconds) is >= 0 and <= 4;

    private static ThermalWindow Merge(ThermalWindow a, ThermalWindow b, int resolution, string quality) => a with
    {
        End = b.End, DurationSeconds = b.StartMonotonic + b.DurationSeconds - a.StartMonotonic,
        SampleCount = checked(a.SampleCount + b.SampleCount), Temperature = Combine(a.Temperature, b.Temperature),
        Load = Combine(a.Load, b.Load), Power = Combine(a.Power, b.Power), Clock = Combine(a.Clock, b.Clock), FanRpm = Combine(a.FanRpm, b.FanRpm),
        Eligible = false, Quality = quality, ResolutionSeconds = resolution
    };
    private static MetricAggregate Combine(MetricAggregate a, MetricAggregate b)
    {
        if (a.Count == 0) return b; if (b.Count == 0) return a;
        int count = checked(a.Count + b.Count);
        return new(count, Math.Min(a.Minimum!.Value, b.Minimum!.Value), Math.Max(a.Maximum!.Value, b.Maximum!.Value),
            (a.Average!.Value * a.Count + b.Average!.Value * b.Count) / count);
    }
    private static string BaseQuality(string quality) => quality.Replace("Hourly summary; ", "", StringComparison.Ordinal).Replace("Aggregated view; ", "", StringComparison.Ordinal);
    // Sensor provenance belongs to the device/mapping identity, not to every minute payload.
    private static string EncodeWindow(ThermalWindow window) => JsonSerializer.Serialize(window with { Device = window.Device with { SensorMap = "" } });
    private static ThermalDevice? FindDevice(SqliteConnection db, string id, string configuration) =>
        ReadPayloads<ThermalDevice>(db, "SELECT payload FROM devices WHERE device=$device AND configuration=$configuration", ("$device", id), ("$configuration", configuration)).SingleOrDefault();
    private static ThermalWindow Hydrate(ThermalWindow window, ThermalDevice? device) => window with
    { Device = device ?? throw new InvalidDataException("History device metadata is missing.") };
    private static void EnqueueBounded(Queue<ThermalWindow> result, ThermalWindow window, int maxPoints)
    { result.Enqueue(window); while (result.Count > maxPoints) result.Dequeue(); }

    private static void ValidateWindow(ThermalWindow window)
    {
        if (window.ResolutionSeconds != 60 || string.IsNullOrWhiteSpace(window.Device.Id) || string.IsNullOrWhiteSpace(window.Device.ConfigurationId) || string.IsNullOrWhiteSpace(window.SessionId) ||
            !double.IsFinite(window.StartMonotonic) || !double.IsFinite(window.DurationSeconds) || window.DurationSeconds < 0 || window.DurationSeconds > 65 || window.End < window.Start || window.SampleCount is < 1 or > 10_000)
            throw new ArgumentException("Invalid detailed thermal window.", nameof(window));
        foreach (var metric in new[] { window.Temperature, window.Load, window.Power, window.Clock, window.FanRpm })
        {
            if (metric.Count < 0 || metric.Count > window.SampleCount ||
                (metric.Count == 0 && (metric.Minimum.HasValue || metric.Maximum.HasValue || metric.Average.HasValue)) ||
                (metric.Count > 0 && (!metric.Minimum.HasValue || !metric.Maximum.HasValue || !metric.Average.HasValue ||
                    !double.IsFinite(metric.Minimum.Value) || !double.IsFinite(metric.Maximum.Value) || !double.IsFinite(metric.Average.Value) ||
                    metric.Minimum > metric.Average || metric.Average > metric.Maximum)))
                throw new ArgumentException("Invalid metric counts or values.", nameof(window));
        }
    }
    private static void ValidateReference(ThermalReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Id) || reference.Label.Length > 120 || reference.End <= reference.Start ||
            reference.End - reference.Start > TimeSpan.FromDays(7) || reference.Windows.Count is < 30 or > 10_080)
            throw new ArgumentException("A reference needs 30 or more eligible minute windows within seven days.", nameof(reference));
        var distinct = new HashSet<(string, double)>();
        foreach (var row in reference.Windows)
        {
            ValidateWindow(row);
            if (!row.Eligible || row.Device.Id != reference.DeviceId || row.Device.ConfigurationId != reference.ConfigurationId ||
                row.Start < reference.Start || row.End > reference.End || !distinct.Add((row.SessionId, row.StartMonotonic)))
                throw new ArgumentException("Reference windows must be eligible, unique and belong to the selected device, setup and period.", nameof(reference));
        }
    }
    private static void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    { if (to <= from || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("History range must be between zero and 366 days."); }
    private void EnsureInitialized() { if (!initialized) throw new InvalidOperationException("History storage has not been initialized."); }
    private SqliteConnection Open(bool readOnly = false, bool create = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            db.Open();
            if (!readOnly)
            {
                // A long-lived export may pin the WAL. Stop accepting writes rather than grow it indefinitely.
                if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 32L * 1024 * 1024)
                {
                    Checkpoint(db);
                    if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 32L * 1024 * 1024)
                        throw new IOException("History journal is waiting for an active reader. Close the export/query and retry recording.");
                }
                long pageSize = Convert.ToInt64(Scalar(db, "PRAGMA page_size"), CultureInfo.InvariantCulture);
                Execute(db, "PRAGMA max_page_count=" + (maximumDatabaseBytes / pageSize).ToString(CultureInfo.InvariantCulture));
                Execute(db, "PRAGMA journal_size_limit=4194304; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL;");
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void PutDevice(SqliteConnection db, SqliteTransaction transaction, ThermalDevice device) => Execute(db,
        "INSERT INTO devices(device,configuration,payload) VALUES($device,$configuration,$payload) ON CONFLICT(device,configuration) DO UPDATE SET payload=excluded.payload", transaction,
        ("$device", device.Id), ("$configuration", device.ConfigurationId), ("$payload", JsonSerializer.Serialize(device)));
    private static void Checkpoint(SqliteConnection db)
    {
        try { Execute(db, "PRAGMA wal_checkpoint(TRUNCATE)"); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { /* A concurrent reader keeps its consistent snapshot; next write retries checkpoint. */ }
    }
    private static long Milliseconds(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static long Hour(DateTimeOffset time) => time.ToUnixTimeMilliseconds() / 3_600_000 * 3_600_000;
    private static string Invariant(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Csv(string value) => "\"" + (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
    private static string EncodeReferenceHeader(ThermalReference reference) => JsonSerializer.Serialize(reference with { Windows = [] });
    private static string EncodeReference(ThermalReference reference)
    {
        // One frozen metadata copy is enough. Compression also bounds the cost of ten long references.
        var compact = reference with { Windows = reference.Windows.Select((row, index) => index == 0 ? row : row with
            { Device = row.Device with { SensorMap = "" } }).ToArray() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(compact);
        if (bytes.Length > 32 * 1024 * 1024) throw new ArgumentException("The selected reference contains too much sensor metadata.", nameof(reference));
        using var buffer = new MemoryStream();
        using (var compressor = new BrotliStream(buffer, CompressionLevel.Fastest, true)) compressor.Write(bytes);
        return "br1:" + Convert.ToBase64String(buffer.ToArray());
    }
    private static ThermalReference DecodeReference(string value)
    {
        ThermalReference reference;
        if (value.StartsWith("br1:", StringComparison.Ordinal))
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(value[4..]));
            using var decompressor = new BrotliStream(compressed, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = decompressor.Read(buffer)) > 0)
            {
                if (expanded.Length + read > 32 * 1024 * 1024) throw new InvalidDataException("The saved reference is too large to read safely.");
                expanded.Write(buffer, 0, read);
            }
            reference = JsonSerializer.Deserialize<ThermalReference>(expanded.ToArray()) ?? throw new InvalidDataException("The saved reference is empty.");
            if (reference.Windows.Count == 0) throw new InvalidDataException("The saved reference has no readings.");
            var device = reference.Windows[0].Device;
            reference = reference with { Windows = reference.Windows.Select(row => row with { Device = device }).ToArray() };
        }
        else reference = JsonSerializer.Deserialize<ThermalReference>(value) ?? throw new InvalidDataException("The saved reference is empty.");
        return reference;
    }
    private static T Deserialize<T>(string value) => typeof(T) == typeof(ThermalReference) ? (T)(object)DecodeReference(value) :
        JsonSerializer.Deserialize<T>(value) ?? throw new InvalidDataException("History contains an empty record.");
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction, params (string Name, object Value)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.CommandTimeout = 1;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    { using var command = Command(db, sql, transaction, args); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    { using var command = Command(db, sql, transaction, args); return command.ExecuteScalar(); }
    private static IReadOnlyList<T> ReadPayloads<T>(SqliteConnection db, string sql, params (string Name, object Value)[] args) => ReadPayloads<T>(db, sql, null, args);
    private static IReadOnlyList<T> ReadPayloads<T>(SqliteConnection db, string sql, SqliteTransaction? transaction, params (string Name, object Value)[] args)
    {
        using var command = Command(db, sql, transaction, args); using var reader = command.ExecuteReader(); var rows = new List<T>();
        while (reader.Read()) rows.Add(Deserialize<T>(reader.GetString(0))); return rows;
    }
}
