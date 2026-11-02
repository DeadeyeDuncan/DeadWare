using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class AlertStoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private static AlertEpisode Episode(string id, DateTimeOffset start, DateTimeOffset? end = null, double peak = 95, string rule = "rule-1", bool acknowledged = false)
        => new(id, rule, AlertKind.Metric, "CPU usage ≥ 90% for 5 minutes", start, end, peak, acknowledged);

    [Fact]
    public void Reopen_keeps_episodes_partitions_machines_and_refuses_newer_versions()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(2);
        store.Append("tablet", [Episode("a", Day, Day.AddMinutes(10)), Episode("b", Day.AddHours(1))], now);
        store.Append("desktop", [Episode("c", Day)], now);
        var reopened = fixture.Open();
        var view = reopened.Read("tablet", Day.AddDays(-1), now);
        Assert.Equal(new[] { "b", "a" }, view.Episodes.Select(e => e.Id));
        Assert.Equal(2, view.Total); Assert.True(view.StorageBytes > 0);
        Assert.Null(view.Episodes[0].End); Assert.Equal(Day.AddMinutes(10), view.Episodes[1].End);
        Assert.Equal("b", Assert.Single(reopened.Open("tablet")).Id);
        Assert.Equal("c", Assert.Single(reopened.Read("desktop", Day.AddDays(-1), now).Episodes).Id);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => fixture.Open());
    }

    [Fact]
    public void Updates_write_the_end_and_peak_without_clearing_acknowledgement()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(2);
        var open = Episode("a", Day, peak: 95);
        store.Append("tablet", [open], now);
        Assert.True(store.Acknowledge("tablet", "a"));
        Assert.False(store.Acknowledge("tablet", "missing"));
        Assert.False(store.Acknowledge("desktop", "a"));
        store.Append("tablet", [open with { End = Day.AddMinutes(30), Peak = 101, Summary = "CPU usage ≥ 90% for 5 minutes" }], now);
        var saved = Assert.Single(store.Read("tablet", Day.AddDays(-1), now).Episodes);
        Assert.Equal(Day.AddMinutes(30), saved.End); Assert.Equal(101, saved.Peak); Assert.True(saved.Acknowledged);
        Assert.Empty(store.Open("tablet"));
        store.Append("tablet", [open], now);
        Assert.True(Assert.Single(store.Read("tablet", Day.AddDays(-1), now).Episodes).Acknowledged);
    }

    [Fact]
    public void Reads_select_overlapping_episodes_newest_first_and_bound_the_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddDays(3);
        var episodes = Enumerable.Range(0, 48).Select(i => Episode("e" + i, Day.AddHours(i), Day.AddHours(i).AddMinutes(20))).ToList();
        episodes.Add(Episode("open", Day.AddDays(-5)));
        store.Append("tablet", episodes, now);
        var day = store.Read("tablet", Day.AddDays(1), Day.AddDays(2));
        Assert.Equal(25, day.Total);
        Assert.Equal("e47", day.Episodes[0].Id); Assert.Equal("e24", day.Episodes[^2].Id); Assert.Equal("open", day.Episodes[^1].Id);
        var bounded = store.Read("tablet", Day.AddDays(-10), now, 10);
        Assert.Equal(10, bounded.Episodes.Count); Assert.Equal(49, bounded.Total); Assert.Equal("e47", bounded.Episodes[0].Id);
        Assert.Empty(store.Read("tablet", Day.AddDays(-20), Day.AddDays(-10)).Episodes);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read("tablet", now, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Read("tablet", Day, now, 0));
    }

    [Fact]
    public void Retention_drops_old_episodes_and_the_cap_evicts_the_oldest_closed_ones_first()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day;
        store.Append("tablet", [Episode("ancient", Day.AddDays(-401), Day.AddDays(-401).AddMinutes(5)), Episode("recent", Day.AddDays(-399), Day.AddDays(-399).AddMinutes(5))], now);
        Assert.Equal("recent", Assert.Single(store.Read("tablet", Day.AddDays(-500), now).Episodes).Id);
        store.Append("tablet", [Episode("today", Day)], Day.AddDays(2));
        Assert.Equal("today", Assert.Single(store.Read("tablet", Day.AddDays(-500), Day.AddDays(2)).Episodes).Id);
        using var small = new Fixture(); var capped = small.Open(65_536);
        capped.Append("tablet", [Episode("open-0", Day.AddDays(-30))], now);
        for (int batch = 0; batch < 8; batch++)
            capped.Append("tablet", Enumerable.Range(0, 400).Select(i => Episode($"c{batch}-{i}", Day.AddDays(-20).AddMinutes(batch * 400 + i), Day.AddDays(-20).AddMinutes(batch * 400 + i + 1))).ToArray(), now);
        var kept = capped.Read("tablet", Day.AddDays(-60), now, 5000);
        Assert.Contains(kept.Episodes, e => e.Id == "open-0");
        Assert.DoesNotContain(kept.Episodes, e => e.Id == "c0-0");
        Assert.Contains(kept.Episodes, e => e.Id == "c7-399");
        Assert.InRange(new FileInfo(small.Path).Length, 1, 65_536);
        using var full = new Fixture(); var stuck = full.Open(65_536);
        Assert.Throws<IOException>(() => stuck.Append("tablet", Enumerable.Range(0, 1000).Select(i => Episode("open-" + i, Day.AddMinutes(i))).ToArray(), now));
    }

    [Fact]
    public void Invalid_episodes_are_rejected_before_any_write_and_a_damaged_file_reports()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        var good = Episode("a", Day);
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [good, Episode("bad id", Day)], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Episode("b", Day, Day.AddMinutes(-1))], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [good with { Peak = double.NaN }], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [good with { Summary = "line\nbreak" }], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [good with { Kind = (AlertKind)9 }], now));
        Assert.Throws<ArgumentException>(() => store.Append("", [good], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", Enumerable.Range(0, AlertStore.MaximumBatch + 1).Select(i => Episode("e" + i, Day)).ToArray(), now));
        Assert.Equal(0, store.Read("tablet", Day.AddDays(-1), now).Total);
        store.Append("tablet", [], now);
        Assert.Throws<InvalidOperationException>(() => new AlertStore(fixture.Path).Open("tablet"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertStore(fixture.Path, 1024));
        using var damaged = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(damaged.Path)!);
        File.WriteAllText(damaged.Path, "not a database");
        Assert.ThrowsAny<Exception>(() => damaged.Open());
        Assert.Equal("not a database", File.ReadAllText(damaged.Path));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-alerts-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "alerts.db");
        public AlertStore Open(long maximumBytes = AlertStore.MaximumDatabaseBytes)
        { var store = new AlertStore(Path, maximumBytes); store.Initialize(); return store; }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
