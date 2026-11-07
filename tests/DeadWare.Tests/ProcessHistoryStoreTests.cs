using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class ProcessHistoryStoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddHours(13);

    private static DateTimeOffset At(int hour, int minute, int second) => Day.AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private static ProcessAppMinute App(string name, long cpu = 0, long read = 0, long write = 0, double memory = 0)
        => new(name, cpu, read, write, memory, false);

    private static ProcessAppMinute Else(long cpu = 0, long read = 0, long write = 0, double memory = 0)
        => new(ProcessHistory.EverythingElse, cpu, read, write, memory, true);

    private static ProcessMinuteBatch Batch(DateTimeOffset minute, string segment = "seg-a", double covered = 60, int processors = 4,
        bool complete = true, params ProcessAppMinute[] apps)
        => new(minute, covered, processors, complete, segment, apps);

    private static ProcessMinuteBatch G(DateTimeOffset minute, double covered, ProcessAppMinute[] apps)
        => Batch(minute, apps: apps) with { GpuCoveredSeconds = covered };

    private static ProcessAppMinute Gpu(string name, double seconds, double memory = 0)
        => App(name, 1) with { GpuSeconds = seconds, GpuMemoryByteSeconds = memory };

    private static ProcessResourceView Read(ProcessHistoryStore store, ProcessRank rank, int minutes = 1)
        => store.ReadApplications("m", Day, Day.AddMinutes(minutes), rank, Now);

    // 0.14.0's Schema constant, verbatim (dd4fb74:src/DeadWare.Core/ProcessHistoryStore.cs).
    private const string Version1Schema = """
        CREATE TABLE names(id INTEGER PRIMARY KEY, upper TEXT UNIQUE NOT NULL, display TEXT NOT NULL);
        CREATE TABLE minutes(
            machine TEXT NOT NULL, minute INTEGER NOT NULL, covered REAL NOT NULL, processors INTEGER NOT NULL,
            complete INTEGER NOT NULL CHECK(complete IN(0,1)), segment TEXT NOT NULL, PRIMARY KEY(machine,minute)) WITHOUT ROWID;
        CREATE INDEX minutes_time ON minutes(minute);
        CREATE TABLE app_minutes(
            machine TEXT NOT NULL, minute INTEGER NOT NULL, name_id INTEGER NOT NULL, cpu INTEGER NOT NULL, io_read INTEGER NOT NULL,
            io_write INTEGER NOT NULL, memory_seconds REAL NOT NULL, everything_else INTEGER NOT NULL CHECK(everything_else IN(0,1)),
            PRIMARY KEY(machine,minute,name_id)) WITHOUT ROWID;
        """;

    private static readonly string[] Version2Minutes = ["machine", "minute", "covered", "processors", "complete", "segment", "gpu_covered"];
    private static readonly string[] Version2AppMinutes =
        ["machine", "minute", "name_id", "cpu", "io_read", "io_write", "memory_seconds", "everything_else", "gpu_seconds", "gpu_memory_seconds"];

    /// <summary>A file as 0.14.0 creates it (its schema, application id, version 1 and WAL), holding minute <see cref="Day"/> of machine "m"
    /// with one row per (name, CPU ticks).</summary>
    private static void WriteVersion1(string path, (string Name, long Cpu)[] apps)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            void Run(string sql, params (string Name, object Value)[] args)
            {
                using var command = db.CreateCommand(); command.CommandText = sql;
                foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
                command.ExecuteNonQuery();
            }
            long minute = Day.ToUnixTimeMilliseconds();
            Run("PRAGMA journal_mode=WAL");
            Run(Version1Schema);
            Run($"PRAGMA application_id={ProcessHistoryStore.ApplicationId}; PRAGMA user_version=1");
            Run("INSERT INTO minutes(machine,minute,covered,processors,complete,segment) VALUES('m',$minute,60,4,1,'seg-a')", ("$minute", minute));
            for (int i = 0; i < apps.Length; i++)
            {
                Run("INSERT INTO names(id,upper,display) VALUES($id,$upper,$display)", ("$id", i + 1), ("$upper", apps[i].Name.ToUpperInvariant()), ("$display", apps[i].Name));
                Run("INSERT INTO app_minutes(machine,minute,name_id,cpu,io_read,io_write,memory_seconds,everything_else) VALUES('m',$minute,$id,$cpu,0,0,0,0)",
                    ("$minute", minute), ("$id", i + 1), ("$cpu", apps[i].Cpu));
            }
        }
        SqliteConnection.ClearAllPools();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "deadware-process-history-" + Guid.NewGuid().ToString("N"));
        public string DbPath { get; }
        public Fixture() { Directory.CreateDirectory(directory); DbPath = Path.Combine(directory, "process-history.db"); }

        public ProcessHistoryStore Open(long maximumBytes = ProcessHistoryStore.MaximumDatabaseBytes)
        {
            var store = new ProcessHistoryStore(DbPath, maximumBytes); store.Initialize(); return store;
        }

        public T Query<T>(string sql, Func<SqliteDataReader, T> read)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); return read(reader);
        }

        public void Execute(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        public List<string> Columns(string table) => Query($"SELECT name FROM pragma_table_info('{table}')", reader =>
        {
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(0));
            return columns;
        });

        public List<DateTime> Days() => Query("SELECT DISTINCT minute/86400000 FROM minutes ORDER BY 1", reader =>
        {
            var days = new List<DateTime>();
            while (reader.Read()) days.Add(DateTime.UnixEpoch.AddDays(reader.GetInt64(0)));
            return days;
        });

        public List<DateTimeOffset> Minutes() => Query("SELECT minute FROM minutes ORDER BY 1", reader =>
        {
            var minutes = new List<DateTimeOffset>();
            while (reader.Read()) minutes.Add(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)));
            return minutes;
        });

        public long Count(string sql) => Query(sql, reader => { reader.Read(); return reader.GetInt64(0); });

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void One_day_at_the_cap_stays_under_six_mebibytes()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        long big = long.MaxValue / 4;
        // A fractional part, so SQLite keeps every REAL at its full 8 bytes.
        double Real(int i) => 1.2345678901234567e15 + i;
        var apps = Enumerable.Range(0, ProcessHistory.MaximumApplications)
            .Select(i => App($"application-{i:00}.exe", big, big, big, Real(i)) with { GpuSeconds = Real(i), GpuMemoryByteSeconds = Real(i) })
            .Append(Else(big, big, big, Real(ProcessHistory.MaximumApplications)) with
                { GpuSeconds = Real(ProcessHistory.MaximumApplications), GpuMemoryByteSeconds = Real(ProcessHistory.MaximumApplications) }).ToArray();
        for (int start = 0; start < 1440; start += 60)
            store.Append("DESKTOP-ABCDEFG", Enumerable.Range(start, 60)
                .Select(i => Batch(Day.AddMinutes(i), apps: apps) with { GpuCoveredSeconds = 59.987654321 }).ToList(), Day.AddDays(1));
        Assert.True(store.StorageBytes < 6 * 1024 * 1024, $"One day used {store.StorageBytes:N0} bytes.");
    }

    [Fact]
    public void Cap_and_retention_constants()
    {
        Assert.Equal(192L * 1024 * 1024, ProcessHistoryStore.MaximumDatabaseBytes);
        Assert.Equal(30, ProcessHistoryStore.RetentionDays);
        Assert.Equal(31, ProcessHistoryStore.MaximumReadDays);
        Assert.Equal(1000, ProcessHistoryStore.MaximumRows);
        Assert.Equal(0x44575052, ProcessHistoryStore.ApplicationId);
    }

    [Fact]
    public void Names_are_unique_on_upper_invariant_and_keep_the_first_casing()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        string[] names = ["Explorer.EXE", "explorer.exe", "Éclair.exe", "éCLAIR.EXE"];
        for (int i = 0; i < names.Length; i++) store.Append("m", [Batch(Day.AddMinutes(i), apps: [App(names[i], 10)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddHours(1), ProcessRank.CpuTime, Now);
        Assert.Equal(["Explorer.EXE", "Éclair.exe"], view.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, view.Rows.Single(r => r.Name == "Explorer.EXE").MinutesListed);
    }

    [Fact]
    public void Restart_within_a_minute_keeps_the_first_segment_and_clears_complete()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, "seg-a", apps: [App("a.exe", 10)])], Now);
        store.Append("m", [Batch(Day, "seg-b", apps: [App("a.exe", 99), App("b.exe", 5)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now);
        Assert.Equal(10, view.Rows.Single(r => r.Name == "a.exe").CpuSeconds * 1e7, 3);
        Assert.DoesNotContain(view.Rows, r => r.Name == "b.exe");
        Assert.Equal(0L, fixture.Query("SELECT complete FROM minutes", r => { r.Read(); return r.GetInt64(0); }));
    }

    [Fact]
    public void A_retried_batch_from_the_same_segment_changes_nothing()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var batch = Batch(Day, "seg-a", apps: [App("a.exe", 10)]);
        store.Append("m", [batch], Now); store.Append("m", [batch], Now);
        Assert.Equal(10, store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows.Single().CpuSeconds * 1e7, 3);
        Assert.Equal(1L, fixture.Query("SELECT complete FROM minutes", r => { r.Read(); return r.GetInt64(0); }));
    }

    [Theory]
    [InlineData("newer")]
    [InlineData("foreign")]
    [InlineData("corrupt")]
    public void Unreadable_files_are_preserved_and_reported(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "corrupt") File.WriteAllBytes(fixture.DbPath, Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 7)).ToArray());
        else
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DbPath, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = kind == "newer"
                    ? $"PRAGMA application_id={ProcessHistoryStore.ApplicationId}; PRAGMA user_version=3; CREATE TABLE x(y)"
                    : "PRAGMA application_id=1234; PRAGMA user_version=1; CREATE TABLE x(y)";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
        }
        byte[] before = File.ReadAllBytes(fixture.DbPath);
        var store = new ProcessHistoryStore(fixture.DbPath);
        var error = Assert.Throws<InvalidDataException>(store.Initialize);
        Assert.Equal(kind == "corrupt" ? "Process history is corrupt; the file was preserved." : "Unsupported process history; the file was preserved.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.DbPath));
    }

    [Fact]
    public void A_new_file_is_version_2_with_the_gpu_columns()
    {
        using var fixture = new Fixture(); fixture.Open();
        Assert.Equal(2L, fixture.Count("PRAGMA user_version"));
        Assert.Equal(Version2Minutes, fixture.Columns("minutes"));
        Assert.Equal(Version2AppMinutes, fixture.Columns("app_minutes"));
    }

    [Fact]
    public void A_version_1_file_is_upgraded_in_place_and_keeps_its_rows()
    {
        using var fixture = new Fixture(); WriteVersion1(fixture.DbPath, [("a.exe", 10_000_000)]);
        var store = fixture.Open();
        Assert.Equal(2L, fixture.Count("PRAGMA user_version"));
        // The same columns, in the same order, as a new file.
        Assert.Equal(Version2Minutes, fixture.Columns("minutes"));
        Assert.Equal(Version2AppMinutes, fixture.Columns("app_minutes"));
        var row = store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows.Single();
        Assert.Equal(("a.exe", 1.0), (row.Name, row.CpuSeconds));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM minutes WHERE gpu_covered IS NULL"));
        // ProcessResourceRow's GPU members come in Task 11.
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM app_minutes WHERE gpu_seconds IS NULL AND gpu_memory_seconds IS NULL"));
        // The next start inspects it as version 2 and keeps the minute.
        Assert.Equal("a.exe", fixture.Open().ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows.Single().Name);
    }

    [Fact]
    public void A_failed_upgrade_leaves_a_valid_version_1_file()
    {
        using var fixture = new Fixture(); WriteVersion1(fixture.DbPath, [("a.exe", 10_000_000)]);
        var store = new ProcessHistoryStore(fixture.DbPath);
        typeof(ProcessHistoryStore).GetField("beforeUpgradeCommit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(store, (Action)(() => throw new InvalidOperationException("seam")));
        Assert.Throws<InvalidOperationException>(store.Initialize);
        SqliteConnection.ClearAllPools();
        Assert.Equal(1L, fixture.Count("PRAGMA user_version"));
        Assert.DoesNotContain("gpu_seconds", fixture.Columns("app_minutes"));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM app_minutes"));
        // The next start upgrades it.
        fixture.Open();
        Assert.Equal(2L, fixture.Count("PRAGMA user_version"));
    }

    [Fact]
    public void A_version_2_start_takes_no_write_lock()
    {
        using var fixture = new Fixture(); fixture.Open();
        // Another connection holds the write lock. A version 2 file has nothing to upgrade, so its start never waits for that lock (the upgrade's
        // transaction once took it on every start, and failed as busy here).
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DbPath, Pooling = false }.ToString());
        writer.Open();
        using (var begin = writer.CreateCommand()) { begin.CommandText = "BEGIN IMMEDIATE"; begin.ExecuteNonQuery(); }
        fixture.Open();
        Assert.Equal(2L, fixture.Count("PRAGMA user_version"));
    }

    [Theory]
    [InlineData("gpu_covered")]
    [InlineData("gpu_seconds")]
    [InlineData("gpu_memory_seconds")]
    public void A_version_2_file_missing_a_gpu_column_is_rejected_and_preserved(string missing)
    {
        using var fixture = new Fixture(); WriteVersion1(fixture.DbPath, []);
        foreach (var (table, column) in new[] { ("minutes", "gpu_covered"), ("app_minutes", "gpu_seconds"), ("app_minutes", "gpu_memory_seconds") })
            if (column != missing) fixture.Execute($"ALTER TABLE {table} ADD COLUMN {column} REAL NULL");
        fixture.Execute("PRAGMA user_version=2");
        byte[] before = File.ReadAllBytes(fixture.DbPath);
        var error = Assert.Throws<InvalidDataException>(new ProcessHistoryStore(fixture.DbPath).Initialize);
        Assert.Equal("Incompatible process history objects; the file was preserved.", error.Message);
        Assert.Equal(before, File.ReadAllBytes(fixture.DbPath));
    }

    [Fact]
    public void Reopen_preserves_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("a.exe", 10)])], Now);
        var view = fixture.Open().ReadApplications("m", Day, Day.AddHours(1), ProcessRank.CpuTime, Now);
        Assert.Equal("a.exe", Assert.Single(view.Rows).Name);
        Assert.Equal(Day, view.EarliestRetained); Assert.True(view.StorageBytes > 0);
    }

    [Fact]
    public void Whole_day_eviction_never_evicts_today()
    {
        using var fixture = new Fixture(); var store = fixture.Open(300 * 1024);
        ProcessAppMinute[] Apps(int count) => Enumerable.Range(0, count).Select(i => App($"application-{i}.exe", 1000 + i, 2000 + i, 3000 + i, 4000 + i)).ToArray();
        var today = Day.AddDays(6);
        // Today lists three times as many apps as the older days, so its save must evict more than one older day to fit.
        for (int d = 0; d <= 6; d++)
            store.Append("m", Enumerable.Range(0, 120).Select(i => Batch(Day.AddDays(d).AddMinutes(i * 12), apps: Apps(d == 6 ? 30 : 10))).ToList(), today.AddHours(23).AddMinutes(59));
        var days = fixture.Days();
        Assert.Contains(today.UtcDateTime.Date, days);
        Assert.DoesNotContain(Day.UtcDateTime.Date, days);
        // Eviction drops whole days: every day left still holds all 120 of its minutes.
        var counts = fixture.Query("SELECT minute/86400000, COUNT(*) FROM minutes GROUP BY 1 ORDER BY 1", r =>
        {
            var rows = new List<(DateTime Day, long Minutes)>(); while (r.Read()) rows.Add((DateTime.UnixEpoch.AddDays(r.GetInt64(0)), r.GetInt64(1))); return rows;
        });
        Assert.Equal(days, counts.Select(c => c.Day));
        Assert.All(counts, c => Assert.Equal(120L, c.Minutes));
    }

    [Fact]
    public void Days_older_than_thirty_are_deleted()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("old.exe", 1)]), Batch(Day.AddDays(1), apps: [App("a.exe", 1)])], Day.AddHours(1).AddDays(1));
        var now = Day.AddDays(31).AddHours(1);
        store.Append("m", [Batch(now.AddHours(-1), apps: [App("b.exe", 1)])], now);
        var days = fixture.Days();
        Assert.DoesNotContain(now.AddDays(-31).UtcDateTime.Date, days);
        Assert.Contains(now.AddDays(-30).UtcDateTime.Date, days);
        Assert.Equal(["a.exe", "b.exe"], fixture.Query("SELECT display FROM names ORDER BY display", r =>
        {
            var names = new List<string>(); while (r.Read()) names.Add(r.GetString(0)); return names;
        }));
    }

    [Fact]
    public void Retention_cutoff_is_the_utc_day_thirty_days_before_today()
    {
        Assert.Equal(Day.AddDays(-30), ProcessHistoryStore.RetentionCutoff(Now));
        Assert.Equal(Day.AddDays(-30), ProcessHistoryStore.RetentionCutoff(Day));
        // 18:59:59 at UTC-5 is 23:59:59 UTC the same day; 08:00 at UTC+9 is 23:00 UTC the day before. The cutoff is in UTC.
        Assert.Equal(Day.AddDays(-30), ProcessHistoryStore.RetentionCutoff(new DateTimeOffset(2026, 11, 2, 18, 59, 59, TimeSpan.FromHours(-5))));
        var east = ProcessHistoryStore.RetentionCutoff(new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.FromHours(9)));
        Assert.Equal((Day.AddDays(-31), TimeSpan.Zero), (east, east.Offset));
    }

    [Fact]
    public void Expire_deletes_days_older_than_thirty_without_an_append()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("old.exe", 1), Else(1)]), Batch(Day.AddDays(1), apps: [App("a.exe", 1), Else(1)])],
            Day.AddDays(1).AddHours(1));
        store.Append("other", [Batch(Day, apps: [App("old.exe", 1)])], Day.AddHours(1));
        // Recording stays off: nothing is appended, yet a day more than 30 days old leaves the file for every machine.
        store.Expire(Day.AddDays(31).AddHours(1));
        Assert.Equal([Day.AddDays(1)], fixture.Minutes());
        Assert.Equal(0, fixture.Count($"SELECT COUNT(*) FROM app_minutes WHERE minute<{Day.AddDays(1).ToUnixTimeMilliseconds()}"));
        Assert.Equal(2, fixture.Count("SELECT COUNT(*) FROM app_minutes"));
        Assert.Equal(0, fixture.Count("SELECT COUNT(*) FROM names WHERE display='old.exe'"));
        // Nothing older is left: expiring again changes nothing.
        store.Expire(Day.AddDays(31).AddHours(2));
        Assert.Equal([Day.AddDays(1)], fixture.Minutes());
    }

    [Fact]
    public void Expire_removes_every_expired_day_and_keeps_today()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var today = Day.AddDays(35);
        store.Append("m", [Batch(today, apps: [App("today.exe", 1)])], today.AddHours(1));
        // shared.exe is listed on the first and the last of the six days.
        store.Append("m", Enumerable.Range(0, 6).SelectMany(d => new[] { Batch(Day.AddDays(d), apps: [App($"day-{d}.exe", 1)]),
            Batch(Day.AddDays(d).AddHours(23).AddMinutes(59), apps: d is 0 or 5 ? [App($"day-{d}.exe", 1), App("shared.exe", 1)] : [App($"day-{d}.exe", 1)]) })
            .ToList(), Day.AddDays(6));
        Assert.Equal(7, fixture.Days().Count);
        store.Expire(today.AddHours(2));
        // The cutoff is Day + 5: days 0 to 4 leave whole, day 5 and today stay whole.
        Assert.Equal([Day.AddDays(5), Day.AddDays(5).AddHours(23).AddMinutes(59), today], fixture.Minutes());
        // Every name only the expired days used leaves with them; a name a kept day still lists stays.
        Assert.Equal(["day-5.exe", "shared.exe", "today.exe"], fixture.Query("SELECT display FROM names ORDER BY display", r =>
        {
            var names = new List<string>(); while (r.Read()) names.Add(r.GetString(0)); return names;
        }));
    }

    [Fact]
    public void Reads_skip_minutes_older_than_the_cutoff_before_they_are_expired()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("old.exe", 10_000_000), Else(5_000_000)]),
            Batch(Day.AddDays(1), apps: [App("a.exe", 20_000_000), Else(10_000_000)])], Day.AddDays(1).AddHours(1));
        // No expiry has run since: both minutes are still in the file.
        var now = Day.AddDays(31).AddHours(1);
        Assert.Equal([Day, Day.AddDays(1)], fixture.Minutes());
        var view = store.ReadApplications("m", Day, Day.AddDays(31), ProcessRank.CpuTime, now);
        Assert.Equal(("a.exe", 1L), (Assert.Single(view.Rows).Name, view.ApplicationCount));
        Assert.Equal((1L, (DateTimeOffset?)Day.AddDays(1), (DateTimeOffset?)Day.AddDays(1)), (view.RecordedMinutes, view.FirstMinute, view.EarliestRetained));
        Assert.Equal(1L, view.EverythingElse.MinutesListed);
        Assert.Equal(3, view.RankTotal, 6);
        // A period wholly before the cutoff reads as empty; its earliest retained minute is the first one after the cutoff.
        var expired = store.ReadApplications("m", Day, Day.AddDays(1), ProcessRank.CpuTime, now);
        Assert.Equal((0L, 0, (DateTimeOffset?)Day.AddDays(1)), (expired.RecordedMinutes, expired.Rows.Count, expired.EarliestRetained));
        Assert.Empty(store.ReadSeries("m", "old.exe", ProcessRank.CpuTime, Day, Day.AddDays(2), 2, now).Buckets);
        var series = store.ReadSeries("m", "a.exe", ProcessRank.CpuTime, Day, Day.AddDays(2), 2, now);
        // The grid still starts at the period start, so a bucket lines up with ReadMetric's.
        Assert.Equal(Day.AddDays(1), Assert.Single(series.Buckets).Start);
        Assert.Equal((1L, (DateTimeOffset?)Day.AddDays(1)), (series.Minutes, series.EarliestRetained));
        // Reading as of the earlier day still sees both.
        Assert.Equal(2, store.ReadApplications("m", Day, Day.AddDays(2), ProcessRank.CpuTime, Day.AddDays(1).AddHours(1)).RecordedMinutes);
    }

    [Fact]
    public void Eviction_at_the_minimum_cap_drops_older_days_but_never_today_s_saved_minutes()
    {
        using var fixture = new Fixture(); var store = fixture.Open(65_536);
        var apps = Enumerable.Range(0, ProcessHistory.MaximumApplications).Select(i => App($"application-{i:00}.exe", 1000 + i, 2000 + i, 3000 + i, 4000 + i))
            .Append(Else(1, 2, 3, 4)).ToArray();
        var today = Day.AddDays(1); var now = today.AddHours(23).AddMinutes(59);
        store.Append("m", Enumerable.Range(0, 3).Select(i => Batch(Day.AddMinutes(i), apps: apps)).ToList(), now);
        // Today's minutes are saved one at a time, as the history worker saves them, until the file is full.
        var saved = new List<DateTimeOffset>(); IOException? full = null;
        for (int i = 0; i < 1440 && full is null; i++)
        {
            try { store.Append("m", [Batch(today.AddMinutes(i), apps: apps)], now); saved.Add(today.AddMinutes(i)); }
            catch (IOException error) { full = error; }
        }
        Assert.NotNull(full);
        Assert.Contains("only the current day remains", full.Message);
        // The older day made room first; then only today was left and the batch was refused, never today's saved minutes evicted.
        Assert.Equal([today.UtcDateTime.Date], fixture.Days());
        Assert.True(saved.Count > 3, $"Only {saved.Count} minutes fit.");
        Assert.Equal(saved, fixture.Minutes());
        Assert.Equal(saved.Count * apps.Length, fixture.Count("SELECT COUNT(*) FROM app_minutes"));
        Assert.Throws<IOException>(() => store.Append("m", [Batch(today.AddMinutes(saved.Count), apps: apps)], now));
        Assert.Equal(saved, fixture.Minutes());
    }

    [Fact]
    public void Reads_reject_more_than_31_days()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-32), Now, ProcessRank.CpuTime, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-32), Now, 100, Now));
        Assert.Empty(store.ReadApplications("m", Now.AddDays(-31), Now, ProcessRank.CpuTime, Now).Rows);
    }

    [Fact]
    public void Averages_follow_the_definitions()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        double gib = 1024d * 1024 * 1024;
        store.Append("m", [Batch(Day, processors: 8, apps: [App("other.exe", 1)]),
            Batch(Day.AddMinutes(1), processors: 8, apps: [App("a.exe", 48_0000000, memory: gib * 60)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddMinutes(2), ProcessRank.CpuTime, Now);
        var row = view.Rows.Single(r => r.Name == "a.exe");
        Assert.Equal(100 * 48.0 / (8 * 120), row.AverageCpuPercent, 6);
        Assert.Equal(gib, row.AverageMemoryBytes, 1);
        Assert.Equal(1, row.MinutesListed);
        Assert.Equal(2, view.RecordedMinutes);
    }

    [Fact]
    public void Everything_else_is_present_and_ranked_total_includes_it()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("a.exe", 30_000_000), Else(70_000_000, 5, 6, 7)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now);
        Assert.True(view.EverythingElse.EverythingElse);
        Assert.Equal(ProcessHistory.EverythingElse, view.EverythingElse.Name);
        Assert.Equal(7, view.EverythingElse.CpuSeconds, 6);
        Assert.Equal((5L, 6L), (view.EverythingElse.IoRead, view.EverythingElse.IoWritten));
        Assert.DoesNotContain(view.Rows, r => r.EverythingElse);
        Assert.Equal(view.Rows.Sum(r => r.CpuSeconds) + view.EverythingElse.CpuSeconds, view.RankTotal, 6);
        Assert.Equal(10, view.RankTotal, 6);
    }

    [Fact]
    public void Row_parts_add_up_to_the_rank_total_for_every_rank()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        // The second minute covers only 30 s, so a row's memory part (Σ memory × Δt) is not its average times its minutes.
        store.Append("m", [
            Batch(Day, covered: 60, apps: [App("a.exe", 10_000_000, 1, 2, 600), App("b.exe", 20_000_000, 3, 4, 1200), Else(5_000_000, 5, 6, 300)]),
            Batch(Day.AddMinutes(1), covered: 30, apps: [App("a.exe", 30_000_000, 7, 8, 900), Else(1_000_000, 9, 10, 150)])], Now);
        foreach (var rank in Enum.GetValues<ProcessRank>())
        {
            var view = store.ReadApplications("m", Day, Day.AddMinutes(2), rank, Now);
            Assert.Equal(view.RankTotal, view.Rows.Sum(r => r.PartOf(rank)) + view.EverythingElse.PartOf(rank), 6);
        }
        var a = store.ReadApplications("m", Day, Day.AddMinutes(2), ProcessRank.AverageMemory, Now).Rows.Single(r => r.Name == "a.exe");
        Assert.Equal(1500, a.MemoryByteSeconds, 6);
        Assert.Equal((4.0, 1500.0, 18.0), (a.PartOf(ProcessRank.CpuTime), a.PartOf(ProcessRank.AverageMemory), a.PartOf(ProcessRank.Io)));
        Assert.Equal(1500 / 90.0, a.AverageMemoryBytes, 6);
    }

    [Fact]
    public void Series_buckets_like_ReadMetric_and_leaves_unlisted_minutes_as_gaps()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", new[] { 0, 1, 3 }.Select(i => Batch(Day.AddMinutes(i), processors: 2, apps: [App("a.exe", (i + 1) * 12_000_000)])).Append(Batch(Day.AddMinutes(2), apps: [App("b.exe", 1)])).ToList(), Now);
        var s = store.ReadSeries("m", "A.EXE", ProcessRank.CpuTime, Day, Day.AddMinutes(4), 2, Now);
        Assert.Equal(2, s.Buckets.Count);
        Assert.Equal(2, s.Buckets[0].Minutes);
        Assert.Equal(1, s.Buckets[1].Minutes);
        Assert.Equal(120_000, s.BucketMilliseconds);
        Assert.Equal(3, s.Minutes);
        // Minute 0: 1.2 s of 2 x 60 s = 1 %; minute 1: 2.4 s = 2 %; minute 3: 4.8 s = 4 %.
        Assert.Equal(1.5, s.Buckets[0].Average, 6);
        Assert.Equal((1.0, 2.0), (s.Buckets[0].Minimum, s.Buckets[0].Maximum), new TupleComparer());
        Assert.Equal(Day.AddMinutes(2), s.Buckets[1].Start);
        Assert.Equal(4.0, s.Buckets[1].Average, 6);
        Assert.Equal(7.0 / 3, s.Statistics.Average!.Value, 6);
        Assert.Equal((1.0, 4.0), (s.Statistics.Minimum!.Value, s.Statistics.Maximum!.Value), new TupleComparer());
    }

    [Fact]
    public void Series_memory_and_io_use_covered_seconds()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, covered: 60, apps: [App("a.exe", read: 600, write: 600, memory: 6000)]),
            Batch(Day.AddMinutes(1), covered: 30, complete: false, apps: [App("a.exe", read: 300, write: 0, memory: 600)])], Now);
        var memory = store.ReadSeries("m", "a.exe", ProcessRank.AverageMemory, Day, Day.AddMinutes(2), 1, Now);
        var io = store.ReadSeries("m", "a.exe", ProcessRank.Io, Day, Day.AddMinutes(2), 1, Now);
        Assert.Equal(6600d / 90, Assert.Single(memory.Buckets).Average, 6);
        Assert.Equal((20.0, 100.0), (memory.Buckets[0].Minimum, memory.Buckets[0].Maximum), new TupleComparer());
        Assert.Equal(1500d / 90, Assert.Single(io.Buckets).Average, 6);
        Assert.Equal((10.0, 20.0), (io.Buckets[0].Minimum, io.Buckets[0].Maximum), new TupleComparer());
        Assert.Equal(1, io.Buckets[0].CompleteMinutes);
        Assert.Empty(store.ReadSeries("m", "unknown.exe", ProcessRank.Io, Day, Day.AddMinutes(2), 1, Now).Buckets);
    }

    [Fact]
    public void Read_applications_includes_minutes_starting_in_a_non_aligned_slot()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", new[] { 1, 2, 4, 8 }.Select((cpu, i) => Batch(At(12, i, 0), apps: [App("a.exe", cpu * 10_000_000L)])).ToList(), Now);
        var view = store.ReadApplications("m", At(12, 0, 30), At(12, 2, 30), ProcessRank.CpuTime, Now);
        Assert.Equal(2, view.RecordedMinutes);
        Assert.Equal(6, view.Rows.Single(r => r.Name == "a.exe").CpuSeconds, 6);
        Assert.Equal(At(12, 1, 0), view.FirstMinute);
        Assert.Equal(At(12, 3, 0), view.LastMinuteEnd);
    }

    [Fact]
    public void An_empty_period_has_no_rows_and_no_bounds()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var view = store.ReadApplications("m", Day, Day.AddHours(1), ProcessRank.Io, Now);
        Assert.Empty(view.Rows);
        Assert.Equal((0L, 0.0), (view.RecordedMinutes, view.RankTotal));
        Assert.Null(view.FirstMinute); Assert.Null(view.LastMinuteEnd); Assert.Null(view.EarliestRetained);
    }

    [Theory]
    [InlineData(ProcessRank.CpuTime, new[] { "cpu.exe", "io.exe", "mem.exe" })]
    [InlineData(ProcessRank.AverageMemory, new[] { "mem.exe", "io.exe", "cpu.exe" })]
    [InlineData(ProcessRank.Io, new[] { "io.exe", "cpu.exe", "mem.exe" })]
    public void Rank_orders_rows_by_cpu_time_avg_memory_or_read_plus_written(ProcessRank rank, string[] expected)
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("cpu.exe", 50_0000000, 10, 10, 1e6 * 60), App("mem.exe", 1_0000000, 0, 5, 4e9 * 60),
            App("io.exe", 2_0000000, 600, 400, 2e9 * 60)])], Now);
        Assert.Equal(expected, store.ReadApplications("m", Day, Day.AddMinutes(1), rank, Now).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Ties_sort_by_name_and_limit_truncates_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("c.exe", 5), App("a.exe", 5), App("b.exe", 5)])], Now);
        Assert.Equal(["a.exe", "b.exe"], store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now, 2).Rows.Select(r => r.Name));
    }

    [Fact]
    public void The_view_counts_every_application_in_the_range_beyond_the_rows_read()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        // Five names over three minutes, one of them in two casings; Everything else is not an application.
        store.Append("m", [Batch(Day, apps: [App("a.exe", 5), App("b.exe", 4), Else(9)]), Batch(Day.AddMinutes(1), apps: [App("A.EXE", 3), App("c.exe", 2)]),
            Batch(Day.AddMinutes(2), apps: [App("d.exe", 1), App("e.exe", 1)])], Now);
        var top = store.ReadApplications("m", Day, Day.AddMinutes(3), ProcessRank.CpuTime, Now, 2);
        Assert.Equal((2, 5L), (top.Rows.Count, top.ApplicationCount));
        var all = store.ReadApplications("m", Day, Day.AddMinutes(3), ProcessRank.Io, Now);
        Assert.Equal((5, 5L), (all.Rows.Count, all.ApplicationCount));
        // Only the range counts: the first minute holds a.exe and b.exe.
        Assert.Equal(2L, store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.AverageMemory, Now, 1).ApplicationCount);
        Assert.Equal(0L, store.ReadApplications("m", Day.AddHours(1), Day.AddHours(2), ProcessRank.CpuTime, Now).ApplicationCount);
    }

    [Fact]
    public void Machines_are_partitioned()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("one", [Batch(Day, apps: [App("a.exe", 5)])], Now);
        Assert.Empty(store.ReadApplications("two", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows);
    }

    [Fact]
    public void Invalid_batches_are_rejected_without_writing()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App("a.exe", -1)])], Now));
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App("a.exe"), App("A.EXE")])], Now));
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App("")])], Now));
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day.AddSeconds(5), apps: [App("a.exe")])], Now));
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, covered: 0, apps: [App("a.exe")])], Now));
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App("a.exe", memory: double.NaN)])], Now));
        Assert.Empty(fixture.Days());
    }

    [Fact]
    public void Gpu_values_are_stored_and_a_minute_without_gpu_stores_null()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("a.exe", 5) with { GpuSeconds = 12, GpuMemoryByteSeconds = 6e9 }, Else(1) with { GpuSeconds = 0, GpuMemoryByteSeconds = 0 }])
            with { GpuCoveredSeconds = 60 }, Batch(Day.AddMinutes(1), apps: [App("a.exe", 5)])], Now);
        Assert.Equal(60.0, fixture.Query("SELECT gpu_covered FROM minutes ORDER BY minute", r => { r.Read(); return r.GetDouble(0); }));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM minutes WHERE gpu_covered IS NULL"));
        Assert.Equal((12.0, 6e9), fixture.Query("SELECT gpu_seconds,gpu_memory_seconds FROM app_minutes a JOIN names n ON n.id=a.name_id WHERE n.upper='A.EXE' ORDER BY minute",
            r => { r.Read(); return (r.GetDouble(0), r.GetDouble(1)); }));
        // Everything else's zeros stay zeros; the minute without GPU data stores NULL, never 0.
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM app_minutes WHERE everything_else=1 AND gpu_seconds=0 AND gpu_memory_seconds=0"));
        Assert.Equal(1L, fixture.Count($"SELECT COUNT(*) FROM app_minutes WHERE minute={Day.AddMinutes(1).ToUnixTimeMilliseconds()} AND gpu_seconds IS NULL AND gpu_memory_seconds IS NULL"));
    }

    [Fact]
    public void Invalid_gpu_values_are_rejected_without_writing()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var ok = Batch(Day, apps: [App("a.exe", 5)]);
        foreach (var bad in new[] { ok with { GpuCoveredSeconds = double.NaN }, ok with { GpuCoveredSeconds = -1 },
            ok with { Applications = [App("a.exe", 5) with { GpuSeconds = 1 }] }, ok with { Applications = [App("a.exe", 5) with { GpuMemoryByteSeconds = 1 }] },
            ok with { GpuCoveredSeconds = 60, Applications = [App("a.exe", 5) with { GpuSeconds = -1 }] },
            ok with { GpuCoveredSeconds = 60, Applications = [App("a.exe", 5) with { GpuMemoryByteSeconds = double.PositiveInfinity }] },
            ok with { Applications = [.. Enumerable.Range(0, 41).Select(i => App($"a{i}.exe", 1)), Else(1)] } })
            Assert.Throws<ArgumentException>(() => store.Append("m", [bad], Now));
        Assert.Empty(fixture.Days());
    }

    [Fact]
    public void Unpaired_surrogate_reserved_and_path_shaped_names_are_rejected_without_writing()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        // SQLite's UTF-8 stores both as a\uFFFD.exe: one name id twice in a minute failed the primary key and stopped recording.
        Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App("a\uD800.exe", 5), App("a\uD801.exe", 7), Else(1)])], Now));
        foreach (string name in new[] { "Everything else", "everything ELSE", "*everything else", @"C:\Users\alice\tool.exe", "tools/tool.exe", "Unknown application" })
            Assert.Throws<ArgumentException>(() => store.Append("m", [Batch(Day, apps: [App(name, 5)])], Now));
        Assert.Empty(fixture.Days());
        store.Append("m", [Batch(Day, apps: [App("a\uFFFD.exe", 12), App("\U0001F600.exe", 3), Else(1)])], Now);
        Assert.Equal(["a\uFFFD.exe", "\U0001F600.exe"], store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Saved_names_at_the_length_limit_are_accepted_as_they_are()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        // NormalizeName keeps 256 characters: a cut can end in a space or in half of a surrogate pair.
        var saved = new[] { new string('x', 255) + " tail.exe", new string('x', 255) + "\U0001F600.exe" }.Select(n => ProcessHistory.SavedName(n)!).ToArray();
        Assert.Equal([new string('x', 255), new string('x', 255) + "\uFFFD"], saved);
        Assert.All(saved, s => Assert.Equal(s, ProcessHistory.SavedName(s)));
        store.Append("m", [Batch(Day, apps: [.. saved.Select(s => App(s, 5)), Else(1)])], Now);
        Assert.Equal(2, store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, Now).Rows.Count);
    }

    [Fact]
    public void Limit_above_1000_is_rejected()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-1), Now, ProcessRank.CpuTime, Now, 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-1), Now, ProcessRank.CpuTime, Now, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-1), Now, 4001, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-1), Now, 0, Now));
    }

    [Fact]
    public void Series_points_are_capped_at_1000()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Now.AddDays(-1), apps: [App("a.exe", 5)])], Now);
        var view = store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-30), Now, 4000, Now);
        Assert.True(view.Buckets.Count <= 1000);
        Assert.True(view.BucketMilliseconds >= (long)Math.Ceiling(TimeSpan.FromDays(30).TotalMilliseconds / 1000));
    }

    [Fact]
    public void Each_read_sees_one_snapshot_while_a_minute_is_saved_between_its_statements()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var writer = fixture.Open();
        ProcessMinuteBatch Minute(int index) => Batch(At(12, index, 0), apps: [App("a.exe", 10_000_000), Else(10_000_000)]);
        store.Append("m", [Minute(0)], Now);
        int saved = 0;
        // The history worker can commit a minute while the card reads: save one after each read's first statement.
        typeof(ProcessHistoryStore).GetField("afterFirstRead", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(store, (Action)(() => writer.Append("m", [Minute(++saved)], Now)));
        var view = store.ReadApplications("m", At(12, 0, 0), At(13, 0, 0), ProcessRank.CpuTime, Now);
        Assert.Equal(1, saved);
        Assert.Equal((1L, (DateTimeOffset?)At(12, 1, 0)), (view.RecordedMinutes, view.LastMinuteEnd));
        Assert.Equal((1L, 1L), (Assert.Single(view.Rows).MinutesListed, view.EverythingElse.MinutesListed));
        Assert.Equal(2, view.RankTotal, 6);
        var series = store.ReadSeries("m", "a.exe", ProcessRank.CpuTime, At(12, 0, 0), At(13, 0, 0), 60, Now);
        Assert.Equal(2, saved);
        Assert.Equal(2, series.Buckets.Count);
        Assert.Equal(2, series.Minutes);
        // The next read sees every saved minute.
        Assert.Equal(3, writer.ReadApplications("m", At(12, 0, 0), At(13, 0, 0), ProcessRank.CpuTime, Now).RecordedMinutes);
    }

    [Fact]
    public void Rank_by_gpu_orders_by_gpu_seconds_and_the_total_includes_everything_else()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [G(Day, 60, [Gpu("a.exe", 10), Gpu("b.exe", 30), App("c.exe", 99) with { GpuSeconds = 0, GpuMemoryByteSeconds = 0 },
            Else(1) with { GpuSeconds = 5, GpuMemoryByteSeconds = 0 }])], Now);
        var v = Read(store, ProcessRank.Gpu);
        Assert.Equal(["b.exe", "a.exe", "c.exe"], v.Rows.Select(r => r.Name));
        Assert.Equal(45, v.RankTotal, 6);
        Assert.Equal(30, v.Rows[0].PartOf(ProcessRank.Gpu), 6);
    }

    [Fact]
    public void Avg_gpu_is_a_share_of_gpu_covered_time_only()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var none = Else(1) with { GpuSeconds = 0, GpuMemoryByteSeconds = 0 };
        store.Append("m", [G(Day, 60, [Gpu("a.exe", 30), Else(1) with { GpuSeconds = 180, GpuMemoryByteSeconds = 0 }]), G(Day.AddMinutes(1), 60, [Gpu("a.exe", 0), none]),
            Batch(Day.AddMinutes(2), apps: [App("a.exe", 1), Else(1)]), Batch(Day.AddMinutes(3), apps: [App("a.exe", 1), Else(1)]), G(Day.AddMinutes(4), 30, [Gpu("b.exe", 6), none])], Now);
        var v = Read(store, ProcessRank.Gpu, 5);
        Assert.Equal(150, v.GpuCoveredSeconds, 6);
        // 100 x 30 / 150: not 25 (/ the 120 s a.exe was listed, or the mean of its minutes' 50 % and 0 %) and not 16.7 (the mean of 50 %, 0 % and 0 %).
        Assert.Equal(20, v.Rows.Single(r => r.Name == "a.exe").AverageGpuPercent!.Value, 6);
        Assert.Equal(4, v.Rows.Single(r => r.Name == "b.exe").AverageGpuPercent!.Value, 6);
        Assert.Equal(120, v.EverythingElse.AverageGpuPercent!.Value, 6);   // unclamped
    }

    [Fact]
    public void Avg_gpu_memory_while_listed_divides_by_the_listed_gpu_minutes()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [G(Day, 60, [Gpu("a.exe", 1, 6e9)]), Batch(Day.AddMinutes(1), apps: [App("a.exe", 1)]), G(Day.AddMinutes(2), 60, [Gpu("b.exe", 1)])], Now);
        Assert.Equal(1e8, Read(store, ProcessRank.Gpu, 3).Rows.Single(r => r.Name == "a.exe").AverageGpuMemoryBytes!.Value, 1);
    }

    [Fact]
    public void Gpu_values_are_null_without_gpu_data()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("a.exe", 5), Else(1)])], Now);
        var v = Read(store, ProcessRank.Gpu); var r = v.Rows.Single();
        Assert.Equal(0, v.GpuCoveredSeconds);
        Assert.Null(r.GpuSeconds); Assert.Null(r.AverageGpuPercent); Assert.Null(r.AverageGpuMemoryBytes);
        Assert.Equal(0, r.PartOf(ProcessRank.Gpu)); Assert.Equal(0, v.RankTotal);
    }

    [Fact]
    public void The_gpu_series_uses_only_minutes_with_gpu_and_leaves_the_rest_as_gaps()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [G(Day, 60, [Gpu("a.exe", 30)]), G(Day.AddMinutes(1), 30, [Gpu("a.exe", 3)]), Batch(Day.AddMinutes(2), apps: [App("a.exe", 1)]),
            G(Day.AddMinutes(3), 60, [Gpu("a.exe", 6)]), G(Day.AddMinutes(4), 60, [Gpu("b.exe", 1)])], Now);
        var s = store.ReadSeries("m", "a.exe", ProcessRank.Gpu, Day, Day.AddMinutes(6), 3, Now);
        Assert.Equal(120_000, s.BucketMilliseconds); Assert.Equal(2, s.Buckets.Count);   // the third bucket has no GPU minute of a.exe
        var two = s.Buckets[0];
        Assert.Equal(2L, two.Minutes); Assert.Equal(100.0 * 33 / 90, two.Average, 6); Assert.Equal((10.0, 50.0), (two.Minimum, two.Maximum));   // sum / sum, not the mean 30
        Assert.Equal((Day.AddMinutes(2), 1L, 10.0), (s.Buckets[1].Start, s.Buckets[1].Minutes, s.Buckets[1].Average));   // minute 2 (no GPU data) is a gap
        Assert.Equal(26, s.Statistics.Average!.Value, 6);   // 100 x 39 / 150
    }

    private sealed class TupleComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;
        public int GetHashCode((double, double) value) => 0;
    }

    [Fact]
    public void A_damaged_table_page_is_reported_and_preserved()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", Enumerable.Range(0, 60).Select(i => Batch(Day.AddMinutes(i), apps: [.. Enumerable.Range(0, 30).Select(a => App($"app-{a}.exe", 10 + a)), Else(1)])).ToList(), Now);
        SqliteConnection.ClearAllPools();
        long page = fixture.Query("PRAGMA page_size", r => { r.Read(); return r.GetInt64(0); });
        using (var file = new FileStream(fixture.DbPath, FileMode.Open, FileAccess.Write))
        {
            file.Seek(file.Length - page, SeekOrigin.Begin);
            file.Write(Enumerable.Repeat((byte)0xA5, (int)page).ToArray());
        }
        byte[] before = File.ReadAllBytes(fixture.DbPath);
        var error = Assert.Throws<InvalidDataException>(new ProcessHistoryStore(fixture.DbPath).Initialize);
        Assert.Null(error.InnerException);   // the integrity_check branch, not the open's error 11/26 catch
        Assert.Equal(before, File.ReadAllBytes(fixture.DbPath));
    }
}
