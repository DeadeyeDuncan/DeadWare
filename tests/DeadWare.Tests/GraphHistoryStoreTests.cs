using System.Diagnostics;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DeadWare.Tests;

public sealed class GraphHistoryStoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DeadWare-graph-store-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private string Database => Path.Combine(directory, "live-graph-history.db");
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private GraphHistoryStore Store(long cap = 64L * 1024 * 1024)
    {
        var store = new GraphHistoryStore(Database, cap); store.Initialize(); return store;
    }
    private static GraphHistoryRecord Row(double seconds, double? value = 40, string session = "run-1") =>
        new(session, Epoch.AddSeconds(seconds), seconds, new Dictionary<string, double?> { ["metric"] = value });
    private SqliteConnection Connect()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open(); return db;
    }
    private static void Execute(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    [Fact] public void ConstructorDoesNotTouchDisk()
    {
        _ = new GraphHistoryStore(Database);
        Assert.False(Directory.Exists(directory));
    }

    [Fact] public void Cadence_roundtrips_without_changing_the_legacy_payload_schema_or_metric_keys()
    {
        var store = Store();
        store.Append([Row(0), Row(10) with { PollingIntervalSeconds = 10 }], Epoch.AddSeconds(20));
        var rows = Store().Read(Epoch.AddSeconds(20));
        Assert.Equal(new double[] { 1, 10 }, rows.Select(row => row.PollingIntervalSeconds));
        Assert.All(rows, row => Assert.Equal(new[] { "metric" }, row.Values.Keys));
        using var db = Connect(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact] public void ReopenPreservesExactTimesSessionIdentityUnknownAndZero()
    {
        var time = Epoch.AddTicks(1234567).ToOffset(TimeSpan.FromHours(-5));
        var values = new Dictionary<string, double?> { ["temperature"] = 62.5, ["download"] = 0, ["upload"] = null };
        Store().Append([new("run-1", time, 0.1234567, values)], Epoch.AddMinutes(1));
        values["temperature"] = 900;
        var store = Store(); var row = Assert.Single(store.Read(Epoch.AddMinutes(1)));
        Assert.Equal(time, row.Timestamp); Assert.Equal(TimeSpan.Zero, row.Timestamp.Offset);
        Assert.Equal(0.1234567, row.MonotonicSeconds); Assert.Equal("run-1", row.SessionId);
        Assert.Equal(62.5, row.Values["temperature"]); Assert.Equal(0, row.Values["download"]); Assert.Null(row.Values["upload"]);
        Assert.True(store.StorageBytes > 0);
    }

    [Fact] public void DuplicateAppendIsIdempotentAndDifferentSessionsMayResetMonotonicTime()
    {
        var store = Store(); store.Append([Row(0)], Epoch.AddSeconds(10));
        store.Append([Row(0, 99), Row(0, 70, "run-2") with { Timestamp = Epoch.AddSeconds(5) }], Epoch.AddSeconds(10));
        var rows = Store().Read(Epoch.AddSeconds(10));
        Assert.Equal(2, rows.Count); Assert.Equal(new[] { "run-1", "run-2" }, rows.Select(x => x.SessionId));
        Assert.Equal(40, rows[0].Values["metric"]); Assert.Equal(70, rows[1].Values["metric"]);
    }

    [Fact] public void SixHourCutoffIsInclusiveAndFutureOrExpiredInputIsNotStored()
    {
        var store = Store(); store.Append([Row(-21601), Row(-21600), Row(-1, 0), Row(1)], Epoch);
        Assert.Equal(new[] { -21600d, -1d }, store.Read(Epoch).Select(x => x.MonotonicSeconds));
        store.Append([], Epoch.AddSeconds(1));
        Assert.Equal(-1, Assert.Single(store.Read(Epoch.AddSeconds(1))).MonotonicSeconds);
        Assert.Empty(store.Read(Epoch.AddHours(7)));
    }

    [Fact] public void CountCapKeepsNewest43201RowsEvenWhenSamplesArriveFasterThanTheSupportedMinimum()
    {
        var store = Store();
        var rows = Enumerable.Range(0, 44000).Select(i => Row(i / 10d)).ToArray();
        foreach (var batch in rows.Chunk(120)) store.Append(batch, Epoch.AddSeconds(4400));
        var read = Store().Read(Epoch.AddSeconds(4400));
        Assert.Equal(43201, read.Count); Assert.Equal(79.9, read[0].MonotonicSeconds); Assert.Equal(4399.9, read[^1].MonotonicSeconds);
    }

    [Fact] public void Half_second_polling_preserves_six_hours_including_ram_within_the_unchanged_storage_limit()
    {
        var store = Store();
        var values = new Dictionary<string,double?>
        {
            [new string('a',64)+"/usage"] = 50.123456789,
            [new string('a',64)+"/temperature"] = 62.25,
            [new string('b',64)+"/usage"] = 21.9733,
            [new string('b',64)+"/temperature"] = 44.2,
            [new string('c',64)+"/download"] = 1234567.89012345,
            [new string('c',64)+"/upload"] = 987654.32,
            [new string('d',64)+"/usage"] = 49.55
        };
        var rows = Enumerable.Range(0, 43201).Select(index => Row(index / 2d,session:new string('s',32)) with
            { Values = values, PollingIntervalSeconds = 0.5 }).ToArray();
        foreach (var batch in rows.Chunk(120)) store.Append(batch, Epoch.AddHours(6));
        var restored = Store().Read(Epoch.AddHours(6));
        Assert.Equal(43201, restored.Count);
        Assert.Equal(0, restored[0].MonotonicSeconds); Assert.Equal(21600, restored[^1].MonotonicSeconds);
        Assert.All(restored, row => Assert.Equal(0.5, row.PollingIntervalSeconds));
        Assert.All(restored, row => Assert.Equal(7,row.Values.Count));
        Assert.InRange(store.StorageBytes, 1, GraphHistoryStore.MaximumDatabaseBytes);
        output.WriteLine($"43,201 half-second samples with seven graph metrics including RAM: {store.StorageBytes:N0} bytes / {GraphHistoryStore.MaximumDatabaseBytes:N0} byte limit.");
    }

    [Fact] public void InvalidInputIsRejectedBeforeChangingPreviouslySavedHistory()
    {
        var store = Store(); store.Append([Row(0)], Epoch.AddSeconds(10));
        var invalid = new[] {
            Row(1) with { SessionId = "" }, Row(1) with { SessionId = new string('s', 129) },
            Row(1) with { MonotonicSeconds = double.NaN }, Row(1) with { MonotonicSeconds = double.PositiveInfinity },
            Row(1, double.NaN), Row(1, double.NegativeInfinity),
            Row(1) with { PollingIntervalSeconds = 3 },
            Row(1) with { Values = new Dictionary<string,double?> { [new string('k', 257)] = 1 } },
            Row(1) with { Values = Enumerable.Range(0,65).ToDictionary(i => i.ToString(), _ => (double?)1) }
        };
        foreach (var record in invalid) Assert.Throws<ArgumentException>(() => store.Append([Row(2), record], Epoch.AddSeconds(10)));
        Assert.Throws<ArgumentException>(() => store.Append(Enumerable.Repeat(Row(2), 121).ToArray(), Epoch.AddSeconds(10)));
        Assert.Single(store.Read(Epoch.AddSeconds(10)));
    }

    [Fact] public void CorruptDatabaseIsPreserved()
    {
        Directory.CreateDirectory(directory); byte[] original = "broken graph cache"u8.ToArray(); File.WriteAllBytes(Database, original);
        Assert.Throws<SqliteException>(() => Store());
        Assert.Equal(original, File.ReadAllBytes(Database));
    }

    [Fact] public void NewerSchemaIsRejectedWithoutResettingData()
    {
        Store().Append([Row(0)], Epoch.AddSeconds(10));
        using (var db = Connect()) Execute(db, "PRAGMA user_version=2");
        Assert.Throws<InvalidDataException>(() => Store());
        using var check = Connect(); using var command = check.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM graph_samples";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact] public void MalformedSavedPayloadIsRejectedAndPreserved()
    {
        var store = Store(); store.Append([Row(0)], Epoch.AddSeconds(10));
        using (var db = Connect()) Execute(db, "UPDATE graph_samples SET payload='{broken'");
        Assert.Throws<InvalidDataException>(() => store.Read(Epoch.AddSeconds(10)));
        using var check = Connect(); using var command = check.CreateCommand(); command.CommandText = "SELECT payload FROM graph_samples";
        Assert.Equal("{broken", command.ExecuteScalar());
    }

    [Fact] public void BusyWriterTimesOutAndKeepsCommittedHistory()
    {
        var store = Store(); store.Append([Row(0)], Epoch.AddSeconds(10));
        using (var db = Connect())
        using (var transaction = db.BeginTransaction())
        {
            var watch = Stopwatch.StartNew();
            var error = Assert.Throws<SqliteException>(() => store.Append([Row(1)], Epoch.AddSeconds(10)));
            Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 }); Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 4);
            transaction.Rollback();
        }
        Assert.Single(store.Read(Epoch.AddSeconds(10)));
    }

    [Fact] public void DatabaseFullRollsBackTheWholeBatchAndKeepsPreviousRows()
    {
        var store = Store(65536); store.Append([Row(0)], Epoch.AddSeconds(120));
        var values = Enumerable.Range(0, 64).ToDictionary(i => new string('x', 240) + i, _ => (double?)1);
        var batch = Enumerable.Range(1, 120).Select(i => Row(i) with { Values = values }).ToArray();
        var error = Assert.Throws<SqliteException>(() => store.Append(batch, Epoch.AddSeconds(120)));
        Assert.Equal(13, error.SqliteErrorCode);
        Assert.Equal(0, Assert.Single(store.Read(Epoch.AddSeconds(120))).MonotonicSeconds);
        Assert.True(new FileInfo(Database).Length <= 65536);
    }
}
