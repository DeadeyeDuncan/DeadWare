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
            capped.Append("tablet", Enumerable.Range(0, 300).Select(i => Episode($"c{batch}-{i}", Day.AddDays(-20).AddMinutes(batch * 300 + i), Day.AddDays(-20).AddMinutes(batch * 300 + i + 1))).ToArray(), now);
        var kept = capped.Read("tablet", Day.AddDays(-60), now, 5000);
        Assert.Contains(kept.Episodes, e => e.Id == "open-0");
        Assert.DoesNotContain(kept.Episodes, e => e.Id == "c0-0");
        Assert.Contains(kept.Episodes, e => e.Id == "c7-299");
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

    private const string M = "DESKTOP-ABCDEFG";
    private static DateTimeOffset Minute0 { get; } = new(DateTimeOffset.UtcNow.AddHours(-1).UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    private static AlertEpisode Fu(string name, DateTimeOffset start, string? id = null)
        => new(id ?? "fu-" + Guid.NewGuid().ToString("N")[..16], FirstNetworkUse.RuleId, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary(name), start, start, 0, false, name);
    private static KnownApplication Kn(string key, DateTimeOffset first, DateTimeOffset last) => new(key, first, last);
    private static FirstUseChanges Ch(IReadOnlyList<KnownApplication> upserts, IReadOnlyList<string> deletes, FirstUseMetadata metadata) => new(upserts, deletes, metadata);
    private static readonly FirstUseMetadata Empty = new(null, null, null, null);

    private const string V016Ddl = """
        CREATE TABLE IF NOT EXISTS episodes(
            machine TEXT NOT NULL, id TEXT NOT NULL, rule TEXT NOT NULL, kind INTEGER NOT NULL, summary TEXT NOT NULL,
            start INTEGER NOT NULL, finish INTEGER, peak REAL NOT NULL, acknowledged INTEGER NOT NULL CHECK(acknowledged IN(0,1)),
            PRIMARY KEY(machine,id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS episodes_time ON episodes(machine,start);
        PRAGMA user_version=1;
        """;
    private static SqliteConnection Raw(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
    }
    private static void Run(string path, string sql) { using var db = Raw(path); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    private static object? Scalar(string path, string sql) { using var db = Raw(path); using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private static long Count(string path, string sql) => Convert.ToInt64(Scalar(path, sql));
    private static string V016(string path, int closed, int open)
    {
        using (var db = Raw(path))
        {
            using var tx = db.BeginTransaction();
            using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = V016Ddl; c.ExecuteNonQuery();
            for (int i = 0; i < closed + open; i++)
            {
                c.CommandText = $"INSERT INTO episodes VALUES('{M}','x{i}','rule-1',0,'CPU usage',{1_700_000_000_000L + i * 60_000L},{(i < closed ? (1_700_000_000_000L + i * 60_000L + 1000).ToString() : "NULL")},95,0)";
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return (string)Scalar(path, "SELECT sql FROM sqlite_master WHERE name='episodes'")!;
    }
    /// <summary>A 0.16 file filled with episodes until SQLITE_FULL at the capacity's page count.</summary>
    private static long FillV016(string path, long capBytes, bool closed)
    {
        long n = 0;
        using (var db = Raw(path))
        {
            using (var c0 = db.CreateCommand()) { c0.CommandText = V016Ddl + "PRAGMA synchronous=OFF; PRAGMA max_page_count=" + capBytes / 4096; c0.ExecuteNonQuery(); }
            using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
            string pad = new('s', 480);
            try
            {
                while (true)
                {
                    c.CommandText = $"INSERT INTO episodes VALUES('{M}','x{n}','rule-1',0,'{pad}',{1_700_000_000_000L + n * 60_000L},{(closed ? (1_700_000_000_000L + n * 60_000L + 1000).ToString() : "NULL")},95,0)";
                    c.ExecuteNonQuery(); n++;
                }
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 13) { }
            tx.Commit();
        }
        return n;
    }
    private static readonly string[] FirstUseTables = ["known_apps", "episode_apps", "alert_metadata", "pending_clear"];
    private static string Cjk(int i) => string.Concat(i.ToString("D5").Select(c => (char)(0x4E00 + c - '0'))).PadRight(256, '中');

    [Fact]
    public void A_version_1_file_gains_the_first_use_tables_without_a_version_change()
    {
        using var fixture = new Fixture(); string before = V016(fixture.Path, 1, 1);
        fixture.Open();
        foreach (var table in FirstUseTables) Assert.Equal(1, Count(fixture.Path, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"));
        Assert.Equal(1, Count(fixture.Path, "PRAGMA user_version"));
        Assert.Equal(before, Scalar(fixture.Path, "SELECT sql FROM sqlite_master WHERE name='episodes'"));
    }

    [Fact]
    public void Initialize_at_the_cap_evicts_closed_episodes_and_retries_the_first_use_tables_once()
    {
        using var fixture = new Fixture(); long n = FillV016(fixture.Path, 2 * 1024 * 1024, closed: true);
        Assert.True(n > 100);
        fixture.Open(2 * 1024 * 1024);
        foreach (var table in FirstUseTables) Assert.Equal(1, Count(fixture.Path, $"SELECT COUNT(*) FROM sqlite_master WHERE name='{table}'"));
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM episodes WHERE id='x0'"));
        Assert.Equal(n - n / 10, Count(fixture.Path, "SELECT COUNT(*) FROM episodes"));
        using var open = new Fixture(); FillV016(open.Path, 2 * 1024 * 1024, closed: false);
        Assert.ThrowsAny<Exception>(() => open.Open(2 * 1024 * 1024));
        foreach (var table in FirstUseTables) Assert.Equal(0, Count(open.Path, $"SELECT COUNT(*) FROM sqlite_master WHERE name='{table}'"));
    }

    [Fact]
    public void First_use_episodes_save_closed_with_their_link_and_known_row_in_one_transaction()
    {
        using var fixture = new Fixture(); var s = fixture.Open(); var t = Minute0;
        s.Append(M, [Fu("chrome.exe", t)], t);
        var e = Assert.Single(s.Read(M, t.AddDays(-1), t.AddDays(1)).Episodes); Assert.Equal(("chrome.exe", AlertKind.FirstNetworkUse), (e.Application, e.Kind));
        Assert.Empty(s.Open(M)); var k = Assert.Single(s.ReadFirstUse(M).Known); Assert.Equal(("CHROME.EXE", t, t), (k.Key, k.First, k.Last));
        Assert.ThrowsAny<ArgumentException>(() => s.Append(M, [Fu("b.exe", t)], t, Ch([Kn("bad", t, t)], [], Empty)));
        Assert.DoesNotContain(s.ReadFirstUse(M).Known, x => x.Key == "B.EXE");
        Assert.Single(s.Read(M, t.AddDays(-1), t.AddDays(1)).Episodes);
    }

    [Fact]
    public void Known_rows_upsert_min_first_and_max_last_deletes_apply_first_and_metadata_is_written_once()
    {
        using var fixture = new Fixture(); var s = fixture.Open(); var t = Minute0;
        s.Append(M, [], t, Ch([Kn("A.EXE", t, t.AddMinutes(5))], [], new(null, null, t, t.AddDays(3))));
        s.Append(M, [], t, Ch([Kn("A.EXE", t.AddMinutes(-2), t.AddMinutes(1))], [], new(t, null, t.AddDays(1), t.AddDays(4))));
        var st = s.ReadFirstUse(M);
        Assert.Equal((t.AddMinutes(-2), t.AddMinutes(5)), (st.Known[0].First, st.Known[0].Last));
        Assert.Equal((t, t.AddDays(3), t), (st.LearningStart, st.LearningEnd, st.SeededUsage));
        Assert.Null(st.SeededConnections);
        s.Append(M, [], t, Ch([Kn("A.EXE", t, t)], ["A.EXE"], Empty)); Assert.Single(s.ReadFirstUse(M).Known);
        Assert.Empty(s.ReadFirstUse("OTHER").Known);
    }

    [Fact]
    public void Retention_and_eviction_remove_links_and_known_rows_unseen_for_400_days_and_initialize_sweeps_orphans()
    {
        using var fixture = new Fixture(); var s = fixture.Open(); var t0 = Minute0;
        s.Append(M, [Fu("old.exe", t0)], t0, Ch([Kn("STALE.EXE", t0, t0), Kn("FRESH.EXE", t0, t0.AddDays(390))], [], Empty));
        Assert.Equal(1, Count(fixture.Path, "SELECT COUNT(*) FROM episode_apps"));
        var now = t0.AddDays(401);
        s.Append(M, [Fu("new.exe", now)], now);
        Assert.Equal("new.exe", Assert.Single(s.Read(M, t0.AddDays(-1), now.AddDays(1)).Episodes).Application);
        Assert.Equal(1, Count(fixture.Path, "SELECT COUNT(*) FROM episode_apps"));
        Assert.Equal(["FRESH.EXE", "NEW.EXE"], s.ReadFirstUse(M).Known.Select(k => k.Key).Order(StringComparer.Ordinal));
        // The cap: evicted episodes lose their links; known and metadata rows stay.
        using var small = new Fixture(); var capped = small.Open(65_536); var t = Minute0; var later = t.AddDays(30);
        capped.Append(M, [Fu("keep.exe", t)], later, Ch([], [], new(t, null, null, null)));
        string summary = new('s', 500);
        for (int batch = 0; batch < 6; batch++)
            capped.Append(M, Enumerable.Range(0, 60).Select(i => new AlertEpisode($"d{batch}-{i}", "rule-2", AlertKind.ApplicationDaily, summary,
                t.AddMinutes(10 + batch * 60 + i), t.AddMinutes(11 + batch * 60 + i), 1, false, $"app{batch}-{i}.exe")).ToArray(), later);
        Assert.Equal(0, Count(small.Path, "SELECT COUNT(*) FROM episodes WHERE kind=4"));
        Assert.Equal(0, Count(small.Path, "SELECT COUNT(*) FROM episode_apps WHERE name='keep.exe'"));
        Assert.Equal(0, Count(small.Path, "SELECT COUNT(*) FROM episode_apps WHERE NOT EXISTS(SELECT 1 FROM episodes e WHERE e.machine=episode_apps.machine AND e.id=episode_apps.episode)"));
        var state = capped.ReadFirstUse(M); Assert.Equal("KEEP.EXE", Assert.Single(state.Known).Key); Assert.Equal(t, state.SeededUsage);
        // Orphans left by 0.16's retention are swept at the next Initialize.
        Run(fixture.Path, $"INSERT INTO episode_apps VALUES('{M}','ghost','ghost.exe')");
        Assert.Equal(2, Count(fixture.Path, "SELECT COUNT(*) FROM episode_apps"));
        fixture.Open();
        Assert.Equal(1, Count(fixture.Path, "SELECT COUNT(*) FROM episode_apps"));
    }

    [Fact]
    public void Invalid_first_use_rows_are_rejected_before_any_write()
    {
        using var fixture = new Fixture(); var s = fixture.Open(); var t = Minute0; var ok = Episode("ok", t);
        void Rejected(IReadOnlyList<AlertEpisode> episodes, FirstUseChanges? changes)
        { Assert.ThrowsAny<ArgumentException>(() => s.Append(M, [ok, .. episodes], t, changes)); }
        foreach (var key in new[] { "chrome.exe", "PID 4", "A\u0001.EXE" }) Rejected([], Ch([Kn(key, t, t)], [], Empty));
        Rejected([], Ch([], ["chrome.exe"], Empty));
        Rejected([], Ch([Kn("A.EXE", t.AddMinutes(1), t)], [], Empty));
        Rejected([], Ch([Kn("A.EXE", t.AddSeconds(30), t.AddSeconds(30))], [], Empty));
        Rejected([], Ch([], [], new(null, null, t.AddDays(1), t)));
        Rejected([Fu("a.exe", t) with { Application = null }], null);
        Rejected([Fu("a.exe", t) with { Application = "PID 4" }], null);
        Rejected([Fu("a.exe", t) with { End = t.AddMinutes(1) }], null);
        Rejected([Fu("a.exe", t) with { Peak = 1 }], null);
        Rejected([Fu("a.exe", t) with { Application = " chrome.exe" }], null);
        Rejected([], Ch([.. Enumerable.Range(0, 1025).Select(i => Kn("K" + i, t, t))], [], Empty));
        Rejected([], Ch([], [.. Enumerable.Range(0, 1025).Select(i => "K" + i)], Empty));
        Assert.Equal(0, s.Read(M, t.AddDays(-1), t.AddDays(1)).Total); Assert.Empty(s.ReadFirstUse(M).Known);
        s.Append(M, [Episode("k2", t) with { Kind = AlertKind.ApplicationDaily, Application = "PID 4" }], t);
        Assert.Equal("PID 4", Assert.Single(s.Read(M, t.AddDays(-1), t.AddDays(1)).Episodes).Application);
    }

    [Fact]
    public void Eight_thousand_known_apps_of_256_cjk_characters_stay_under_8_MiB()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var t = Minute0;
        for (int chunk = 0; chunk < 8; chunk++)
            store.Append(M, [], t, Ch([.. Enumerable.Range(chunk * 1024, 1024).Select(i => Kn(Cjk(i), t, t))], [], Empty));
        Assert.Equal(8192, store.ReadFirstUse(M).Known.Count);
        Assert.True(store.StorageBytes < 8L * 1024 * 1024);
    }

    [Fact]
    public void Eight_thousand_alerted_apps_of_256_cjk_characters_stay_under_32_MiB()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var t = Minute0; var now = t.AddDays(10);
        for (int chunk = 0; chunk < 8; chunk++)
            store.Append(M, Enumerable.Range(chunk * 1024, 1024).Select(i => Fu(Cjk(i), t.AddMinutes(i), "fu-" + i)).ToArray(), now);
        Assert.Equal(8192, store.ReadFirstUse(M).Known.Count);
        Assert.True(store.StorageBytes < 32L * 1024 * 1024);
    }

    [Fact]
    public void Alert_store_creates_new_tables_at_the_cap()
    {
        using var fixture = new Fixture(); long n = FillV016(fixture.Path, 2 * 1024 * 1024, closed: true);
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM sqlite_master WHERE name='episode_apps'"));
        fixture.Open(2 * 1024 * 1024);
        Assert.Equal(4, Count(fixture.Path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN('known_apps','episode_apps','alert_metadata','pending_clear')"));
        Assert.Equal(n - n / 10, Count(fixture.Path, "SELECT COUNT(*) FROM episodes"));
        Assert.Equal(0, Count(fixture.Path, "SELECT COUNT(*) FROM episode_apps"));
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
