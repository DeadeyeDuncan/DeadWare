using System.Globalization;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class BatteryHistoryStoreTests
{
    [Fact]
    public void Reopen_preserves_oldest_years_and_partitions_machines()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var old = DateTimeOffset.UtcNow.AddYears(-12); var now = DateTimeOffset.UtcNow;
        store.Append("tablet", [Sample(old, 0, 100), Sample(now, 1, 25)]);
        store.Append("desktop", [Sample(now, 0, 50)]);
        var reopened = fixture.Open(); var view = reopened.Read("tablet", null, now.AddSeconds(1));
        Assert.Equal(2, view.TotalObservations); Assert.Equal(old.ToUnixTimeMilliseconds(), view.FirstRecorded!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(2, view.Buckets.Sum(b => b.Samples)); Assert.Contains(view.Buckets, b => b.Maximum == 100);
        Assert.Equal(1, reopened.Read("desktop", null, now.AddSeconds(1)).TotalObservations);
        Assert.True(view.StorageBytes > 0);
    }

    [Fact]
    public void Whole_history_is_bounded_and_keeps_extremes_charging_saver_and_unknowns()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var start = DateTimeOffset.UtcNow.AddYears(-8);
        for (int batch = 0; batch < 40; batch++)
            store.Append("tablet", Enumerable.Range(batch * 500, 500).Select(i => Sample(start.AddHours(i), i,
                i % 101, i % 2 == 0 ? BatteryPowerState.Charging : BatteryPowerState.OnBattery,
                i % 3 == 0 ? null : i % 3 == 1)).ToArray());
        var view = store.Read("tablet", null, DateTimeOffset.UtcNow, 100);
        Assert.InRange(view.Buckets.Count, 1, 100); Assert.Equal(20_000, view.Buckets.Sum(b => b.Samples));
        Assert.Equal(0, view.Buckets.Min(b => b.Minimum)); Assert.Equal(100, view.Buckets.Max(b => b.Maximum));
        Assert.Equal(10_000, view.Buckets.Sum(b => b.ChargingSamples));
        Assert.Equal(6_667, view.Buckets.Sum(b => b.SaverSamples));
        Assert.Equal(13_333, view.Buckets.Sum(b => b.SaverKnownSamples));
    }

    [Fact]
    public void Csv_streams_original_observations_with_utc_and_nullable_saver()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var stamp = DateTimeOffset.UtcNow.AddYears(-3);
        store.Append("tablet", [Sample(stamp, 0, 0, BatteryPowerState.OnBattery, true),
            Sample(stamp.AddMinutes(1), 60, null, BatteryPowerState.Unknown, null)]);
        store.Append("other", [Sample(stamp, 0, 99)]);
        using var writer = new StringWriter(CultureInfo.GetCultureInfo("fr-FR"));
        store.ExportCsv("tablet", writer, null, stamp.AddDays(1));
        string csv = writer.ToString();
        Assert.Contains("timestamp_utc,percentage,power_state,battery_saver,segment", csv);
        Assert.Contains(",0,OnBattery,on,", csv); Assert.Contains(",,Unknown,unknown,", csv);
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(",99,", csv);
    }

    [Fact]
    public void Invalid_batch_cannot_partly_write_and_repeated_samples_are_idempotent()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = DateTimeOffset.UtcNow;
        var sample = Sample(now, 0, 50); store.Append("tablet", [sample]); store.Append("tablet", [sample]);
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Sample(now.AddSeconds(1), 1, 60), Sample(now.AddSeconds(2), 2, double.NaN)]));
        Assert.Equal(1, store.Read("tablet", null, now.AddSeconds(3)).TotalObservations);
    }

    [Fact]
    public void Newer_database_is_preserved_and_locked_writer_reports_failure()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        using (var db = new SqliteConnection($"Data Source={fixture.Path};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "BEGIN IMMEDIATE"; command.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => store.Append("tablet", [Sample(DateTimeOffset.UtcNow, 0, 50)]));
            command.CommandText = "ROLLBACK; PRAGMA user_version=99"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new BatteryHistoryStore(fixture.Path).Initialize());
        Assert.True(File.Exists(fixture.Path));
    }

    private static BatterySample Sample(DateTimeOffset stamp, double seconds, double? percent,
        BatteryPowerState state = BatteryPowerState.OnBattery, bool? saver = false) => new(stamp, seconds, new(percent, state, saver), "fixture");

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-battery-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "battery.db");
        public BatteryHistoryStore Open() { var store = new BatteryHistoryStore(Path); store.Initialize(); return store; }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
