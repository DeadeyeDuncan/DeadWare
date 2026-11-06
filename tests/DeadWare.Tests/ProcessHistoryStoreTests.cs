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

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "deadware-process-history-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public Fixture() { Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "process-history.db"); }

        public ProcessHistoryStore Open(long maximumBytes = ProcessHistoryStore.MaximumDatabaseBytes)
        {
            var store = new ProcessHistoryStore(Path, maximumBytes); store.Initialize(); return store;
        }

        public T Query<T>(string sql, Func<SqliteDataReader, T> read)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader(); return read(reader);
        }

        public List<DateTime> Days() => Query("SELECT DISTINCT minute/86400000 FROM minutes ORDER BY 1", reader =>
        {
            var days = new List<DateTime>();
            while (reader.Read()) days.Add(DateTime.UnixEpoch.AddDays(reader.GetInt64(0)));
            return days;
        });

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void One_day_at_the_cap_stays_under_four_mebibytes()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        long big = long.MaxValue / 4;
        var apps = Enumerable.Range(0, ProcessHistory.MaximumApplications).Select(i => App($"application-{i:00}.exe", big, big, big, big))
            .Append(Else(big, big, big, big)).ToArray();
        for (int start = 0; start < 1440; start += 60)
            store.Append("m", Enumerable.Range(start, 60).Select(i => Batch(Day.AddMinutes(i), apps: apps)).ToList(), Day.AddDays(1));
        Assert.True(store.StorageBytes < 4 * 1024 * 1024, $"One day used {store.StorageBytes:N0} bytes.");
    }

    [Fact]
    public void Cap_and_retention_constants()
    {
        Assert.Equal(128L * 1024 * 1024, ProcessHistoryStore.MaximumDatabaseBytes);
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
        var view = store.ReadApplications("m", Day, Day.AddHours(1), ProcessRank.CpuTime);
        Assert.Equal(["Explorer.EXE", "Éclair.exe"], view.Rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, view.Rows.Single(r => r.Name == "Explorer.EXE").MinutesListed);
    }

    [Fact]
    public void Restart_within_a_minute_keeps_the_first_segment_and_clears_complete()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, "seg-a", apps: [App("a.exe", 10)])], Now);
        store.Append("m", [Batch(Day, "seg-b", apps: [App("a.exe", 99), App("b.exe", 5)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime);
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
        Assert.Equal(10, store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime).Rows.Single().CpuSeconds * 1e7, 3);
        Assert.Equal(1L, fixture.Query("SELECT complete FROM minutes", r => { r.Read(); return r.GetInt64(0); }));
    }

    [Theory]
    [InlineData("newer")]
    [InlineData("foreign")]
    [InlineData("corrupt")]
    public void Unreadable_files_are_preserved_and_reported(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "corrupt") File.WriteAllBytes(fixture.Path, Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 7)).ToArray());
        else
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = kind == "newer"
                    ? $"PRAGMA application_id={ProcessHistoryStore.ApplicationId}; PRAGMA user_version=2; CREATE TABLE x(y)"
                    : "PRAGMA application_id=1234; PRAGMA user_version=1; CREATE TABLE x(y)";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
        }
        byte[] before = File.ReadAllBytes(fixture.Path);
        var store = new ProcessHistoryStore(fixture.Path);
        Assert.Throws<InvalidDataException>(store.Initialize);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void Reopen_preserves_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("a.exe", 10)])], Now);
        var view = fixture.Open().ReadApplications("m", Day, Day.AddHours(1), ProcessRank.CpuTime);
        Assert.Equal("a.exe", Assert.Single(view.Rows).Name);
        Assert.Equal(Day, view.EarliestRetained); Assert.True(view.StorageBytes > 0);
    }

    [Fact]
    public void Whole_day_eviction_never_evicts_today()
    {
        using var fixture = new Fixture(); var store = fixture.Open(300 * 1024);
        var apps = Enumerable.Range(0, 10).Select(i => App($"application-{i}.exe", 1000 + i, 2000 + i, 3000 + i, 4000 + i)).ToArray();
        var today = Day.AddDays(6);
        for (int d = 0; d <= 6; d++)
            store.Append("m", Enumerable.Range(0, 120).Select(i => Batch(Day.AddDays(d).AddMinutes(i), apps: apps)).ToList(), today.AddHours(13));
        var days = fixture.Days();
        Assert.Contains(today.UtcDateTime.Date, days);
        Assert.DoesNotContain(Day.UtcDateTime.Date, days);
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
    public void Reads_reject_more_than_31_days()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-32), Now, ProcessRank.CpuTime));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-32), Now, 100));
        Assert.Empty(store.ReadApplications("m", Now.AddDays(-31), Now, ProcessRank.CpuTime).Rows);
    }

    [Fact]
    public void Averages_follow_the_definitions()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        double gib = 1024d * 1024 * 1024;
        store.Append("m", [Batch(Day, processors: 8, apps: [App("other.exe", 1)]),
            Batch(Day.AddMinutes(1), processors: 8, apps: [App("a.exe", 48_0000000, memory: gib * 60)])], Now);
        var view = store.ReadApplications("m", Day, Day.AddMinutes(2), ProcessRank.CpuTime);
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
        var view = store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime);
        Assert.True(view.EverythingElse.EverythingElse);
        Assert.Equal(ProcessHistory.EverythingElse, view.EverythingElse.Name);
        Assert.Equal(7, view.EverythingElse.CpuSeconds, 6);
        Assert.Equal((5L, 6L), (view.EverythingElse.IoRead, view.EverythingElse.IoWritten));
        Assert.DoesNotContain(view.Rows, r => r.EverythingElse);
        Assert.Equal(view.Rows.Sum(r => r.CpuSeconds) + view.EverythingElse.CpuSeconds, view.RankTotal, 6);
        Assert.Equal(10, view.RankTotal, 6);
    }

    [Fact]
    public void Series_buckets_like_ReadMetric_and_leaves_unlisted_minutes_as_gaps()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [0, 1, 3].Select(i => Batch(Day.AddMinutes(i), processors: 2, apps: [App("a.exe", (i + 1) * 12_000_000)])).Append(Batch(Day.AddMinutes(2), apps: [App("b.exe", 1)])).ToList(), Now);
        var s = store.ReadSeries("m", "A.EXE", ProcessRank.CpuTime, Day, Day.AddMinutes(4), 2);
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
        var memory = store.ReadSeries("m", "a.exe", ProcessRank.AverageMemory, Day, Day.AddMinutes(2), 1);
        var io = store.ReadSeries("m", "a.exe", ProcessRank.Io, Day, Day.AddMinutes(2), 1);
        Assert.Equal(6600d / 90, Assert.Single(memory.Buckets).Average, 6);
        Assert.Equal((20.0, 100.0), (memory.Buckets[0].Minimum, memory.Buckets[0].Maximum), new TupleComparer());
        Assert.Equal(1500d / 90, Assert.Single(io.Buckets).Average, 6);
        Assert.Equal((10.0, 20.0), (io.Buckets[0].Minimum, io.Buckets[0].Maximum), new TupleComparer());
        Assert.Equal(1, io.Buckets[0].CompleteMinutes);
        Assert.Empty(store.ReadSeries("m", "unknown.exe", ProcessRank.Io, Day, Day.AddMinutes(2), 1).Buckets);
    }

    [Fact]
    public void Read_applications_includes_minutes_starting_in_a_non_aligned_slot()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [1, 2, 4, 8].Select((cpu, i) => Batch(At(12, i, 0), apps: [App("a.exe", cpu * 10_000_000L)])).ToList(), Now);
        var view = store.ReadApplications("m", At(12, 0, 30), At(12, 2, 30), ProcessRank.CpuTime);
        Assert.Equal(2, view.RecordedMinutes);
        Assert.Equal(6, view.Rows.Single(r => r.Name == "a.exe").CpuSeconds, 6);
        Assert.Equal(At(12, 1, 0), view.FirstMinute);
        Assert.Equal(At(12, 3, 0), view.LastMinuteEnd);
    }

    [Fact]
    public void An_empty_period_has_no_rows_and_no_bounds()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        var view = store.ReadApplications("m", Day, Day.AddHours(1), ProcessRank.Io);
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
        Assert.Equal(expected, store.ReadApplications("m", Day, Day.AddMinutes(1), rank).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Ties_sort_by_name_and_limit_truncates_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Day, apps: [App("c.exe", 5), App("a.exe", 5), App("b.exe", 5)])], Now);
        Assert.Equal(["a.exe", "b.exe"], store.ReadApplications("m", Day, Day.AddMinutes(1), ProcessRank.CpuTime, 2).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Machines_are_partitioned()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("one", [Batch(Day, apps: [App("a.exe", 5)])], Now);
        Assert.Empty(store.ReadApplications("two", Day, Day.AddMinutes(1), ProcessRank.CpuTime).Rows);
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
    public void Limit_above_1000_is_rejected()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-1), Now, ProcessRank.CpuTime, 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplications("m", Now.AddDays(-1), Now, ProcessRank.CpuTime, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-1), Now, 4001));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-1), Now, 0));
    }

    [Fact]
    public void Series_points_are_capped_at_1000()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        store.Append("m", [Batch(Now.AddDays(-1), apps: [App("a.exe", 5)])], Now);
        var view = store.ReadSeries("m", "a.exe", ProcessRank.Io, Now.AddDays(-30), Now, 4000);
        Assert.True(view.Buckets.Count <= 1000);
        Assert.True(view.BucketMilliseconds >= (long)Math.Ceiling(TimeSpan.FromDays(30).TotalMilliseconds / 1000));
    }

    private sealed class TupleComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;
        public int GetHashCode((double, double) value) => 0;
    }
}
