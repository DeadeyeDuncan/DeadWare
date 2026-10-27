using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public sealed record BatteryHistoryBucket(DateTimeOffset Start, DateTimeOffset End, double? Average,
    double? Minimum, double? Maximum, long Samples, long ValidSamples, long ChargingSamples,
    long SaverSamples, long SaverKnownSamples, string? Segment);

public sealed record BatteryHistoryView(DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<BatteryHistoryBucket> Buckets, long TotalObservations, DateTimeOffset? FirstRecorded,
    long StorageBytes, string Status = "");

/// <summary>Permanent, sparse battery observations. No retention pruning; callers own background scheduling.</summary>
public sealed class BatteryHistoryStore(string path)
{
    private readonly string path = Path.GetFullPath(path);
    private bool initialized;
    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }
        .Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open();
        long version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is not (0 or 1)) throw new InvalidDataException("Battery history belongs to a newer DeadWare version.");
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA journal_size_limit=8388608");
        using var transaction = db.BeginTransaction();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS battery_samples(
                id INTEGER PRIMARY KEY, machine TEXT NOT NULL, segment TEXT NOT NULL,
                monotonic REAL NOT NULL, stamp INTEGER NOT NULL,
                percentage REAL CHECK(percentage BETWEEN 0 AND 100),
                power INTEGER NOT NULL CHECK(power BETWEEN 0 AND 4), saver INTEGER CHECK(saver IN(0,1)),
                UNIQUE(machine,segment,monotonic));
            CREATE INDEX IF NOT EXISTS battery_samples_time ON battery_samples(machine,stamp);
            PRAGMA user_version=1;
            """, transaction);
        transaction.Commit(); initialized = true;
    }

    public void Append(string machine, IReadOnlyList<BatterySample> samples)
    {
        EnsureInitialized(); ValidateMachine(machine);
        if (samples.Count > 512) throw new ArgumentException("Battery history batches are limited to 512 observations.");
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Segment) || sample.Segment.Length > 64 ||
                sample.Segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                !double.IsFinite(sample.MonotonicSeconds) || !Enum.IsDefined(sample.Reading.PowerState) ||
                sample.Reading.Percentage is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100))
                throw new ArgumentException("Invalid battery observation.", nameof(samples));
        }
        if (samples.Count == 0) return;
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO battery_samples(machine,segment,monotonic,stamp,percentage,power,saver)
            VALUES($machine,$segment,$monotonic,$stamp,$percentage,$power,$saver)
            ON CONFLICT(machine,segment,monotonic) DO NOTHING
            """;
        foreach (string name in new[] { "$machine", "$segment", "$monotonic", "$stamp", "$percentage", "$power", "$saver" })
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        foreach (var sample in samples)
        {
            command.Parameters["$machine"].Value = machine; command.Parameters["$segment"].Value = sample.Segment;
            command.Parameters["$monotonic"].Value = sample.MonotonicSeconds;
            command.Parameters["$stamp"].Value = sample.Timestamp.ToUnixTimeMilliseconds();
            command.Parameters["$percentage"].Value = (object?)sample.Reading.Percentage ?? DBNull.Value;
            command.Parameters["$power"].Value = (int)sample.Reading.PowerState;
            command.Parameters["$saver"].Value = sample.Reading.SaverEnabled is { } saving ? saving ? 1 : 0 : DBNull.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public BatteryHistoryView Read(string machine, DateTimeOffset? from, DateTimeOffset to, int maxPoints = 1500)
    {
        EnsureInitialized(); ValidateMachine(machine);
        if (maxPoints is < 1 or > 4000 || from >= to) throw new ArgumentOutOfRangeException(nameof(maxPoints));
        using var db = Open();
        using var stats = db.CreateCommand();
        stats.CommandText = "SELECT COUNT(*),MIN(stamp) FROM battery_samples WHERE machine=$machine";
        stats.Parameters.AddWithValue("$machine", machine);
        long count; DateTimeOffset? first;
        using (var reader = stats.ExecuteReader())
        { reader.Read(); count = reader.GetInt64(0); first = reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)); }
        var start = from ?? (first is { } recorded && recorded < to ? recorded : to.AddDays(-1));
        // At most maxPoints slots, including an observation exactly at 'to'.
        long startStamp = start.ToUnixTimeMilliseconds(), endStamp = to.ToUnixTimeMilliseconds();
        long bucket = Math.Max(60_000, (long)Math.Ceiling((endStamp - startStamp + 1d) / maxPoints));
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT MIN(stamp),MAX(stamp),AVG(percentage),MIN(percentage),MAX(percentage),COUNT(*),COUNT(percentage),
                SUM(CASE WHEN power=3 THEN 1 ELSE 0 END),
                SUM(CASE WHEN saver=1 THEN 1 ELSE 0 END),COUNT(saver),MIN(segment),MAX(segment)
            FROM battery_samples WHERE machine=$machine AND stamp >= $from AND stamp <= $to
            GROUP BY (stamp-$from)/$bucket ORDER BY MIN(stamp)
            """;
        command.Parameters.AddWithValue("$machine", machine); command.Parameters.AddWithValue("$from", startStamp);
        command.Parameters.AddWithValue("$to", endStamp); command.Parameters.AddWithValue("$bucket", bucket);
        var result = new List<BatteryHistoryBucket>();
        using (var reader = command.ExecuteReader())
        {
            double? Number(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
            while (reader.Read()) result.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), Number(2), Number(3), Number(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                reader.GetString(10) == reader.GetString(11) ? reader.GetString(10) : null));
        }
        return new(start, to, result, count, first, StorageBytes);
    }

    public void ExportCsv(string machine, TextWriter writer, DateTimeOffset? from, DateTimeOffset to)
    {
        EnsureInitialized(); ValidateMachine(machine);
        if (from >= to) throw new ArgumentOutOfRangeException(nameof(from));
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT stamp,percentage,power,saver,segment FROM battery_samples
            WHERE machine=$machine AND stamp >= $from AND stamp <= $to ORDER BY stamp,id
            """;
        command.Parameters.AddWithValue("$machine", machine);
        command.Parameters.AddWithValue("$from", from?.ToUnixTimeMilliseconds() ?? long.MinValue);
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        writer.WriteLine("timestamp_utc,percentage,power_state,battery_saver,segment");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string percentage = reader.IsDBNull(1) ? "" : reader.GetDouble(1).ToString("0.###", CultureInfo.InvariantCulture);
            string saver = reader.IsDBNull(3) ? "unknown" : reader.GetInt32(3) == 1 ? "on" : "off";
            writer.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)):O},{percentage},{(BatteryPowerState)reader.GetInt32(2)},{saver},{reader.GetString(4)}");
        }
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 1 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private void EnsureInitialized() { if (!initialized) throw new InvalidOperationException("Initialize battery history first."); }
    private static void ValidateMachine(string machine)
    { if (string.IsNullOrWhiteSpace(machine) || machine.Length > 256) throw new ArgumentException("Invalid machine key."); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.ExecuteNonQuery(); }
}
