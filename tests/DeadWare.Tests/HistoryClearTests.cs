using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class HistoryClearTests
{
    [Fact]
    public void Clear_boundaries()
    {
        var t = new DateTimeOffset(2026, 11, 7, 10, 31, 45, TimeSpan.Zero);
        var older = HistoryClear.Create("Chrome.exe", allTime: false, t);
        Assert.Equal(("CHROME.EXE", new DateTimeOffset(2026, 10, 31, 10, 31, 0, TimeSpan.Zero), t, false), (older.AppKey, older.Before, older.Requested, older.AllTime)); Assert.Equal(older.Before, older.AlertCutoff);
        var all = HistoryClear.Create(null, allTime: true, t); Assert.Null(all.AppKey); Assert.Equal(new DateTimeOffset(2026, 11, 7, 10, 32, 0, TimeSpan.Zero), all.Before); Assert.True(all.AllTime); Assert.Equal(t, all.AlertCutoff);
        Assert.True(older.Covers("chrome.EXE", older.Before.AddMinutes(-1))); Assert.False(older.Covers("chrome.exe", older.Before)); Assert.False(older.Covers("other.exe", older.Before.AddMinutes(-1)));
    }

    // Task 9: cleared time on both strips.
    private static readonly DateTimeOffset Epoch = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly ConnectionLocation Place = new("US", "United States", "Illinois", "Chicago", new(41, -87), "fixture");

    private static ConnectionHistoryRecord Row(int minute, string name = "browser") =>
        new("b" + minute + name, "segment", new(Epoch.AddMinutes(minute), new(new(7, 1, name), "192.168.1.234", 49123, "8.8.8.8", 443, ConnectionProtocol.Tcp), 10, 20,
            Epoch.AddMinutes(minute).AddSeconds(1), Epoch.AddMinutes(minute).AddSeconds(2)), Place, new(40, -80), true);

    private static ConnectionHistoryStore Connections(string file)
    {
        var store = new ConnectionHistoryStore(file); store.Initialize();
        store.Append([.. Enumerable.Range(0, 20).Select(m => Row(m))], [], Epoch.AddMinutes(21));
        return store;
    }

    private static void Finish(ConnectionHistoryStore store) { while (!store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) { } }
    private static void Finish(UsageHistoryStore store) { while (!store.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) { } }

    private static TrafficStrip Strip(ConnectionHistoryStore store, int fromMinute = 0, int toMinute = 20)
        => store.Read(new(Epoch.AddMinutes(fromMinute), Epoch.AddMinutes(toMinute))).Strip!;

    private static long Meta(string file, string key)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = $"SELECT value FROM metadata WHERE key='{key}'";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Sql(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Fact]
    public void Strip_marks_time_a_clear_removed()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeadWare-strip-clear-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Connections: an all-apps Older than 7 days clear.
            string one = Path.Combine(root, "one", "connections.db");
            var store = Connections(one);
            var initial = Strip(store); Assert.Null(initial.RetainedFrom); Assert.False(initial.RetainedByClear); Assert.Null(initial.AppClearedBefore);
            var older = HistoryClear.Create(null, allTime: false, Epoch.AddMinutes(10).AddDays(7));
            Assert.Equal(Epoch.AddMinutes(10), older.Before);
            store.BeginClear(older);
            var cleared = Strip(store);
            Assert.Equal(older.Before, cleared.RetainedFrom); Assert.True(cleared.RetainedByClear); Assert.False(cleared.CapacityShortened); Assert.Null(cleared.AppClearedBefore);
            Finish(store);
            Assert.True(Strip(store).RetainedByClear);
            // A one-app clear after the origin and after RetainedFrom sets AppClearedBefore; before the origin it does not.
            store.BeginClear(new("BROWSER", Epoch.AddMinutes(12), Epoch.AddMinutes(12))); Finish(store);
            var app = Strip(store);
            Assert.Equal(Epoch.AddMinutes(12), app.AppClearedBefore); Assert.True(app.RetainedByClear);
            Assert.Null(Strip(store, 12).AppClearedBefore); Assert.Null(Strip(store, 13).AppClearedBefore);
            // Once the 30-day expiry passes Before, the retained start is expiry's again.
            store.Append([], [], older.Before.AddDays(30).AddMinutes(1));
            var expired = Strip(store);
            Assert.Equal(older.Before.AddMinutes(1), expired.RetainedFrom); Assert.False(expired.RetainedByClear);

            // With the capacity floor raised above the clear's Before, the retained start is the floor's.
            string two = Path.Combine(root, "two", "connections.db");
            var shortened = Connections(two);
            shortened.BeginClear(new(null, Epoch.AddMinutes(10), Epoch.AddMinutes(10)));
            Sql(two, $"UPDATE metadata SET value={Epoch.AddMinutes(14).UtcTicks} WHERE key='floor'");
            var floor = Strip(shortened);
            Assert.Equal(Epoch.AddMinutes(15), floor.RetainedFrom); Assert.True(floor.CapacityShortened); Assert.False(floor.RetainedByClear);

            // Two clears of each kind: the marks keep the later Before, and a reopen still initializes.
            string three = Path.Combine(root, "three", "connections.db");
            var twice = Connections(three);
            twice.BeginClear(new(null, Epoch.AddMinutes(15), Epoch.AddMinutes(15))); Finish(twice);
            twice.BeginClear(new(null, Epoch.AddMinutes(10), Epoch.AddMinutes(10))); Finish(twice);
            twice.BeginClear(new("BROWSER", Epoch.AddMinutes(12), Epoch.AddMinutes(12))); Finish(twice);
            twice.BeginClear(new("BROWSER", Epoch.AddMinutes(8), Epoch.AddMinutes(8))); Finish(twice);
            Assert.Equal(Epoch.AddMinutes(15).UtcTicks, Meta(three, "cleared-all")); Assert.Equal(Epoch.AddMinutes(12).UtcTicks, Meta(three, "cleared-one"));
            var reopened = new ConnectionHistoryStore(three); reopened.Initialize(); Assert.Null(reopened.PendingClear);
            var latest = Strip(reopened);
            Assert.Equal(Epoch.AddMinutes(15), latest.RetainedFrom); Assert.True(latest.RetainedByClear);
            // cleared-one (12) is not after RetainedFrom (15).
            Assert.Null(latest.AppClearedBefore);

            // Usage history.
            var day = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            string usagePath = Path.Combine(root, "usage", "usage-history.db"), marks = Path.ChangeExtension(usagePath, ".cleared.json");
            var usage = new UsageHistoryStore(usagePath); usage.Initialize();
            UsageMinuteBatch Batch(int minute) => new(day.AddMinutes(minute), [new(UsageMetrics.Cpu, null, day.AddMinutes(minute), 10, 10, 10, 60, true, "seg")],
                [new("a.exe", day.AddMinutes(minute), 100, 10, "seg"), new("b.exe", day.AddMinutes(minute), 50, 5, "seg")], false, "seg");
            usage.Append("m", [.. Enumerable.Range(0, 60).Select(Batch)], day.AddDays(1));
            var none = usage.ReadApplicationSeries("m", day, day.AddHours(1), 60);
            Assert.Equal(60, none.Slots.Count); Assert.Null(none.RetainedFrom); Assert.False(none.RetainedByClear); Assert.Null(none.AppClearedBefore);
            Assert.Equal(UsageClearedMarks.None, usage.ClearedMarks);
            usage.BeginClear(new(null, day.AddMinutes(20), day.AddMinutes(20))); Finish(usage);
            // A clock set back writes application rows before AllAppsBefore again; metric minutes before it were never deleted. Neither is in a slot.
            usage.Append("m", [Batch(5)], day.AddDays(1));
            var all = usage.ReadApplicationSeries("m", day, day.AddHours(1), 60);
            Assert.Equal(day.AddMinutes(20), all.RetainedFrom); Assert.True(all.RetainedByClear); Assert.Null(all.AppClearedBefore);
            Assert.Equal(40, all.Slots.Count); Assert.All(all.Slots, slot => Assert.True(slot.Start >= day.AddMinutes(20)));
            // The totals stay the Applications table's, which counts the rewritten minute.
            Assert.Equal(usage.ReadApplications("m", day, day.AddHours(1)).Applications.Sum(a => a.Received), all.Applications.Sum(a => a.Received) + all.OtherReceived);
            // A range that starts at or after AllAppsBefore has no cleared start.
            var later = usage.ReadApplicationSeries("m", day.AddMinutes(20), day.AddHours(1), 40);
            Assert.Null(later.RetainedFrom); Assert.False(later.RetainedByClear); Assert.Equal(40, later.Slots.Count);
            usage.BeginClear(new("A.EXE", day.AddMinutes(40), day.AddMinutes(40))); Finish(usage);
            var oneApp = usage.ReadApplicationSeries("m", day, day.AddHours(1), 60);
            Assert.Equal(day.AddMinutes(40), oneApp.AppClearedBefore); Assert.True(oneApp.RetainedByClear);
            Assert.Null(usage.ReadApplicationSeries("m", day.AddMinutes(40), day.AddHours(1), 20).AppClearedBefore);
            Assert.Equal(new UsageClearedMarks(day.AddMinutes(20), day.AddMinutes(40)), UsageClearedMarks.Read(marks));
            // A one-app time not after RetainedFrom is not shown, and RetainedFrom is the first whole minute at or after AllAppsBefore.
            File.WriteAllText(marks, "{\"Version\":1,\"AllAppsBefore\":\"2026-11-02T00:20:30Z\",\"OneAppBefore\":\"2026-11-02T00:21:00Z\"}");
            var parsed = new UsageHistoryStore(usagePath); parsed.Initialize();
            var rounded = parsed.ReadApplicationSeries("m", day, day.AddHours(1), 60);
            Assert.Equal(day.AddMinutes(21), rounded.RetainedFrom); Assert.True(rounded.RetainedByClear); Assert.Null(rounded.AppClearedBefore);
            Assert.All(rounded.Slots, slot => Assert.True(slot.Start >= day.AddMinutes(21)));
            // An invalid or missing file reads as no clear.
            foreach (Action broken in new Action[] { () => File.WriteAllText(marks, "{"), () => File.Delete(marks) })
            {
                broken();
                var plain = new UsageHistoryStore(usagePath); plain.Initialize();
                Assert.Equal(UsageClearedMarks.None, plain.ClearedMarks); Assert.Equal(UsageClearedMarks.None, UsageClearedMarks.Read(marks));
                var series = plain.ReadApplicationSeries("m", day, day.AddHours(1), 60);
                Assert.Null(series.RetainedFrom); Assert.False(series.RetainedByClear); Assert.Null(series.AppClearedBefore);
                Assert.Contains(series.Slots, slot => slot.Start == day.AddMinutes(5));
            }
            // With marks: the larger of old and new per kind.
            var marked = new UsageClearedMarks(day.AddMinutes(20), null).With(new("X", day.AddMinutes(30), day)).With(new(null, day.AddMinutes(10), day));
            Assert.Equal(new UsageClearedMarks(day.AddMinutes(20), day.AddMinutes(30)), marked);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Task 10: the retention wait.
    private static long Count(string file, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Retention_expiry_waits_while_a_marker_exists()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeadWare-retention-wait-" + Guid.NewGuid().ToString("N"));
        try
        {
            var day = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
            long oldest = day.AddDays(1).ToUnixTimeMilliseconds();

            // Usage history: rows on its oldest day, then an Append 401 days later while a marker exists.
            string usagePath = Path.Combine(root, "usage", "usage-history.db");
            var usage = new UsageHistoryStore(usagePath); usage.Initialize();
            UsageMinuteBatch Batch(DateTimeOffset minute) => new(minute, [new(UsageMetrics.Cpu, null, minute, 10, 10, 10, 60, true, "seg")],
                [new("a.exe", minute, 100, 10, "seg")], false, "seg");
            usage.Append("m", [Batch(day), Batch(day.AddMinutes(1))], day.AddDays(1));
            var later = day.AddDays(401);
            usage.BeginClear(new("ZZZ.EXE", later, later));
            usage.Append("m", [Batch(later)], later);
            Assert.Equal(2, Count(usagePath, $"SELECT COUNT(*) FROM metric_minutes WHERE minute<{oldest}"));
            Assert.Equal(2, Count(usagePath, $"SELECT COUNT(*) FROM application_minutes WHERE minute<{oldest}"));
            while (!usage.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) { }
            Assert.False(usage.ClearPending);
            usage.Append("m", [Batch(later.AddMinutes(1))], later.AddMinutes(1));
            Assert.Equal(0, Count(usagePath, $"SELECT COUNT(*) FROM metric_minutes WHERE minute<{oldest}"));
            Assert.Equal(0, Count(usagePath, $"SELECT COUNT(*) FROM application_minutes WHERE minute<{oldest}"));
            Assert.Equal(2, Count(usagePath, "SELECT COUNT(*) FROM metric_minutes"));

            // Application resources: its Expire and Append 31 days after its oldest day delete nothing while a marker exists.
            string processPath = Path.Combine(root, "process", "process-history.db");
            var process = new ProcessHistoryStore(processPath); process.Initialize();
            ProcessMinuteBatch Minute(DateTimeOffset minute) => new(minute, 60, 4, true, "seg", [new("a.exe", 1, 0, 0, 0, false)]);
            process.Append("m", [Minute(day), Minute(day.AddMinutes(1))], day.AddHours(1));
            var month = day.AddDays(31);
            process.BeginClear(new("ZZZ.EXE", month, month));
            process.Expire(month);
            Assert.Equal(2, Count(processPath, $"SELECT COUNT(*) FROM minutes WHERE minute<{oldest}"));
            process.Append("m", [Minute(month)], month);
            Assert.Equal(2, Count(processPath, $"SELECT COUNT(*) FROM minutes WHERE minute<{oldest}"));
            Assert.Equal(2, Count(processPath, $"SELECT COUNT(*) FROM app_minutes WHERE minute<{oldest}"));
            // The last step deletes the marker, then expires with the step's now.
            while (!process.ClearStep(TimeSpan.FromMilliseconds(250), month).Finished) { }
            Assert.False(process.ClearPending);
            Assert.Equal(0, Count(processPath, $"SELECT COUNT(*) FROM minutes WHERE minute<{oldest}"));
            Assert.Equal(0, Count(processPath, $"SELECT COUNT(*) FROM app_minutes WHERE minute<{oldest}"));
            Assert.Equal(1, Count(processPath, "SELECT COUNT(*) FROM minutes"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Task 11: the checks across all four stores.
    private const string Machine = "M";
    private static readonly DateTimeOffset Asked = new(2026, 11, 7, 12, 0, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset OldMinute = UsageMetrics.Floor(Asked.AddDays(-8)), RecentMinute = UsageMetrics.Floor(Asked.AddHours(-1));
    private static int receipt;

    private static void Seam(object store, string field, object? value)
        => store.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(store, value);

    /// <summary>The four history stores in one folder, each initialized.</summary>
    private sealed class FourStores : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "DeadWare-four-stores-" + Guid.NewGuid().ToString("N"));
        public string ConnectionsPath => Path.Combine(Root, "connections.db");
        public string UsagePath => Path.Combine(Root, "usage-history.db");
        public string ProcessPath => Path.Combine(Root, "process-history.db");
        public string AlertsPath => Path.Combine(Root, "alerts.db");
        public string[] Databases => [ConnectionsPath, UsagePath, ProcessPath, AlertsPath];
        public readonly ConnectionHistoryStore Connections;
        public readonly UsageHistoryStore Usage;
        public readonly ProcessHistoryStore Process;
        public readonly AlertStore Alerts;
        private int episode;

        public FourStores()
        {
            Directory.CreateDirectory(Root);
            Connections = new(ConnectionsPath); Connections.Initialize();
            Usage = new(UsagePath); Usage.Initialize();
            Process = new(ProcessPath); Process.Initialize();
            Alerts = new(AlertsPath); Alerts.Initialize();
        }

        /// <summary>One row for each (name, minute) in every store; in alerts.db a budget episode. Names of one key go in different minutes.</summary>
        public void Fill(params (string Name, DateTimeOffset Minute)[] rows)
        {
            Connections.Append([.. rows.Select(r =>
            {
                int n = Interlocked.Increment(ref receipt);
                return new ConnectionHistoryRecord("k" + n, "segment", new(r.Minute, new(new(7, n, r.Name), "192.168.1.234", 49123, "8.8.8.8", 443, ConnectionProtocol.Tcp), 10, 20,
                    r.Minute.AddSeconds(1), r.Minute.AddSeconds(2)), Place, new(40, -80), true);
            })], [], Asked);
            var minutes = rows.GroupBy(r => r.Minute).OrderBy(g => g.Key).ToList();
            Usage.Append(Machine, [.. minutes.Select(g => new UsageMinuteBatch(g.Key, [new(UsageMetrics.Cpu, null, g.Key, 10, 10, 10, 60, true, "seg")],
                [.. g.Select(r => new UsageApplicationMinute(r.Name, g.Key, 100, 10, "seg"))], false, "seg"))], Asked);
            Process.Append(Machine, [.. minutes.Select(g => new ProcessMinuteBatch(g.Key, 60, 4, true, "seg", [.. g.Select(r => new ProcessAppMinute(r.Name, 1, 0, 0, 0, false))]))], Asked);
            Alerts.Append(Machine, [.. rows.Select(r => new AlertEpisode("e" + ++episode, "rule-budget", AlertKind.ApplicationDaily, $"{r.Name} ≥ 2.0 GB in a day",
                r.Minute, r.Minute.AddMinutes(1), 3e9, false, r.Name))], Asked);
        }

        /// <summary>A first-use episode for <paramref name="name"/> with its known row.</summary>
        public void FirstUse(string name, DateTimeOffset minute)
            => Alerts.Append(Machine, [new("fu" + ++episode, FirstNetworkUse.RuleId, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary(name), minute, minute, 0, false, name)], Asked);

        public void Begin(HistoryClear clear)
        { Connections.BeginClear(clear); Usage.BeginClear(clear); Process.BeginClear(clear); Alerts.BeginClear(Machine, clear); }

        public HistoryClearProgress[] Step() => [Connections.ClearStep(TimeSpan.FromMilliseconds(250)), Usage.ClearStep(TimeSpan.FromMilliseconds(250)),
            Process.ClearStep(TimeSpan.FromMilliseconds(250), Asked), Alerts.ClearStep(Machine, TimeSpan.FromMilliseconds(250))];

        public void Finish()
        {
            for (int steps = 1; !Connections.ClearStep(TimeSpan.FromMilliseconds(250)).Finished; steps++) Assert.InRange(steps, 1, 1000);
            for (int steps = 1; !Usage.ClearStep(TimeSpan.FromMilliseconds(250)).Finished; steps++) Assert.InRange(steps, 1, 1000);
            for (int steps = 1; !Process.ClearStep(TimeSpan.FromMilliseconds(250), Asked).Finished; steps++) Assert.InRange(steps, 1, 1000);
            for (int steps = 1; !Alerts.ClearStep(Machine, TimeSpan.FromMilliseconds(250)).Finished; steps++) Assert.InRange(steps, 1, 1000);
        }

        public void AssertNoMarkers()
        {
            Assert.Null(Connections.PendingClear); Assert.Equal(0, Count(ConnectionsPath, "SELECT count(*) FROM metadata WHERE substr(key,1,6)='clear-'"));
            Assert.False(Usage.ClearPending); Assert.False(Process.ClearPending);
            Assert.Null(Alerts.PendingClear); Assert.Equal(0, Count(AlertsPath, "SELECT count(*) FROM pending_clear"));
            Assert.Empty(Directory.GetFiles(Root, "*.clear.json")); Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
        }

        /// <summary>Every table's row count in every store.</summary>
        public long[] Counts() =>
        [
            .. new[] { "traffic", "coverage", "dedup", "manifests" }.Select(t => Count(ConnectionsPath, $"SELECT count(*) FROM {t}")),
            .. new[] { "metric_minutes", "application_minutes", "capture_gaps" }.Select(t => Count(UsagePath, $"SELECT count(*) FROM {t}")),
            .. new[] { "names", "minutes", "app_minutes" }.Select(t => Count(ProcessPath, $"SELECT count(*) FROM {t}")),
            .. new[] { "episodes", "episode_apps", "known_apps", "alert_metadata" }.Select(t => Count(AlertsPath, $"SELECT count(*) FROM {t}")),
        ];

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [Fact]
    public void Clear_marker_that_cannot_be_saved_clears_nothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeadWare-marker-full-" + Guid.NewGuid().ToString("N"));
        try
        {
            // connections.db at its fixture cap: metadata rows no build reads fill the metadata table's last leaf and the freelist.
            string file = Path.Combine(root, "connections.db");
            var store = new ConnectionHistoryStore(file, 65_536); store.Initialize();
            store.Append([Row(1, "a.exe"), Row(2, "b.exe")], [], Epoch.AddMinutes(3));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "PRAGMA page_size"; long pageSize = (long)command.ExecuteScalar()!;
                command.CommandText = $"PRAGMA max_page_count={65_536 / pageSize}"; command.ExecuteNonQuery();
                int pad = 0;
                try { for (; ; pad++) { command.CommandText = $"INSERT INTO metadata VALUES('pad-{pad:D5}',0)"; command.ExecuteNonQuery(); } }
                catch (SqliteException error) when (error.SqliteErrorCode == 13) { }
                Assert.True(pad > 0);
            }
            long traffic = Count(file, "SELECT count(*) FROM traffic"), metadata = Count(file, "SELECT count(*) FROM metadata"), expiry = Meta(file, "expiry");
            foreach (var clear in new[] { new HistoryClear("A.EXE", Epoch.AddMinutes(10), Epoch.AddMinutes(10)), new HistoryClear(null, Epoch.AddMinutes(10), Epoch.AddMinutes(10)) })
            {
                Assert.ThrowsAny<Exception>(() => store.BeginClear(clear));
                Assert.Null(store.PendingClear);
                Assert.Equal(0, Count(file, "SELECT count(*) FROM metadata WHERE substr(key,1,6)='clear-'"));
                Assert.Equal(traffic, Count(file, "SELECT count(*) FROM traffic")); Assert.Equal(metadata, Count(file, "SELECT count(*) FROM metadata"));
                Assert.Equal(expiry, Meta(file, "expiry"));
            }

            // Usage history with its sidecar unwritable: a directory at the marker's .tmp path.
            string usagePath = Path.Combine(root, "usage", "usage-history.db");
            var usage = new UsageHistoryStore(usagePath); usage.Initialize();
            var day = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            usage.Append("m", [.. Enumerable.Range(0, 5).Select(i => new UsageMinuteBatch(day.AddMinutes(i), [], [new("a.exe", day.AddMinutes(i), 100, 10, "seg")], false, "seg"))], day.AddDays(1));
            string marker = Path.ChangeExtension(usagePath, ".clear.json");
            Directory.CreateDirectory(marker + ".tmp");
            foreach (var clear in new[] { new HistoryClear("A.EXE", day.AddHours(1), day.AddHours(1)), new HistoryClear(null, day.AddHours(1), day.AddHours(1)) })
            {
                Assert.ThrowsAny<Exception>(() => usage.BeginClear(clear));
                Assert.False(usage.ClearPending); Assert.False(File.Exists(marker));
                Assert.Equal(5, Count(usagePath, "SELECT count(*) FROM application_minutes"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Cleared_names_leave_no_bytes_in_any_store_file()
    {
        using var stores = new FourStores();
        stores.Fill(("ZQXSECRET.exe", OldMinute), ("other.exe", OldMinute), ("ZQXSECRET.exe", RecentMinute));
        stores.FirstUse("ZQXSECRET.exe", RecentMinute.AddSeconds(5));
        stores.Begin(HistoryClear.Create("ZQXSECRET.exe", allTime: true, Asked));
        // Each store's write connections opened while its marker exists.
        var secure = new List<(string Store, long Value)>();
        void Watch(object store, string name, Func<bool> pending) => Seam(store, "clearConnectionOpened", (Action<SqliteConnection>)(db =>
        {
            if (!pending()) return;
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA secure_delete"; secure.Add((name, (long)command.ExecuteScalar()!));
        }));
        Watch(stores.Connections, "connections", () => stores.Connections.PendingClear is not null);
        Watch(stores.Usage, "usage", () => stores.Usage.ClearPending);
        Watch(stores.Process, "process", () => stores.Process.ClearPending);
        Watch(stores.Alerts, "alerts", () => stores.Alerts.PendingClear is not null);
        stores.Finish();
        stores.AssertNoMarkers();
        Assert.Equal(["alerts", "connections", "process", "usage"], secure.Select(s => s.Store).Distinct().Order(StringComparer.Ordinal));
        Assert.All(secure, s => Assert.Equal(1, s.Value));
        foreach (var database in stores.Databases) Sql(database, "PRAGMA wal_checkpoint(TRUNCATE)");
        SqliteConnection.ClearAllPools();
        var files = stores.Databases.SelectMany(d => new[] { d, d + "-wal" }).Append(Path.Combine(stores.Root, "usage-history.cleared.json")).Where(File.Exists).ToList();
        Assert.Contains(Path.Combine(stores.Root, "usage-history.cleared.json"), files);
        foreach (var path in files)
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            { bytes = new byte[stream.Length]; stream.ReadExactly(bytes); }
            foreach (var secret in new[] { "ZQXSECRET", "zqxsecret" })
                Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(secret)) < 0, $"{Path.GetFileName(path)} still holds {secret}.");
        }
        // The other app's rows remain.
        Assert.Equal(1, Count(stores.ConnectionsPath, "SELECT count(*) FROM traffic WHERE name='other.exe'"));
        Assert.Equal(1, Count(stores.AlertsPath, "SELECT count(*) FROM episodes"));
    }

    [Fact]
    public void One_app_clear_matches_non_ascii_case_in_every_store()
    {
        using var stores = new FourStores();
        // One batch cannot hold two apps of one key, so the two spellings are saved in different minutes.
        stores.Fill(("ärger.exe", OldMinute), ("other.exe", OldMinute), ("Ärger.exe", RecentMinute), ("other.exe", RecentMinute));
        stores.FirstUse("Ärger.exe", RecentMinute.AddSeconds(5)); stores.FirstUse("other.exe", RecentMinute.AddSeconds(6));
        Assert.Equal(1, Count(stores.ProcessPath, "SELECT count(*) FROM names WHERE upper='ÄRGER.EXE' AND display='ärger.exe'"));
        Assert.Equal(1, Count(stores.AlertsPath, "SELECT count(*) FROM known_apps WHERE app_key='ÄRGER.EXE'"));
        var clear = HistoryClear.Create("ÄRGER.EXE", allTime: true, Asked);
        Assert.Equal("ÄRGER.EXE", clear.AppKey);
        stores.Begin(clear); stores.Finish(); stores.AssertNoMarkers();
        Assert.Equal(0, Count(stores.ConnectionsPath, "SELECT count(*) FROM traffic WHERE name IN('ärger.exe','Ärger.exe')"));
        Assert.Equal(2, Count(stores.ConnectionsPath, "SELECT count(*) FROM traffic WHERE name='other.exe'"));
        Assert.Equal(0, Count(stores.UsagePath, "SELECT count(*) FROM application_minutes WHERE name IN('ärger.exe','Ärger.exe')"));
        Assert.Equal(2, Count(stores.UsagePath, "SELECT count(*) FROM application_minutes WHERE name='other.exe'"));
        Assert.Equal(0, Count(stores.ProcessPath, "SELECT count(*) FROM names WHERE upper='ÄRGER.EXE' OR display IN('ärger.exe','Ärger.exe')"));
        Assert.Equal(0, Count(stores.ProcessPath, "SELECT count(*) FROM app_minutes WHERE name_id NOT IN(SELECT id FROM names)"));
        Assert.Equal(2, Count(stores.ProcessPath, "SELECT count(*) FROM app_minutes a JOIN names n ON n.id=a.name_id WHERE n.upper='OTHER.EXE'"));
        Assert.Equal(0, Count(stores.AlertsPath, "SELECT count(*) FROM episodes WHERE summary LIKE '%rger.exe%'"));
        Assert.Equal(0, Count(stores.AlertsPath, "SELECT count(*) FROM episode_apps WHERE name IN('ärger.exe','Ärger.exe')"));
        Assert.Equal(0, Count(stores.AlertsPath, "SELECT count(*) FROM known_apps WHERE app_key='ÄRGER.EXE'"));
        Assert.Equal(3, Count(stores.AlertsPath, "SELECT count(*) FROM episodes"));
        Assert.Equal(3, Count(stores.AlertsPath, "SELECT count(*) FROM episode_apps WHERE name='other.exe'"));
        Assert.Equal(1, Count(stores.AlertsPath, "SELECT count(*) FROM known_apps WHERE app_key='OTHER.EXE'"));
    }

    [Fact]
    public void Clearing_an_app_with_no_saved_history_finishes_at_once()
    {
        using var stores = new FourStores();
        stores.Fill(("other.exe", OldMinute), ("b.exe", OldMinute), ("other.exe", RecentMinute));
        stores.FirstUse("other.exe", OldMinute.AddSeconds(5));
        var counts = stores.Counts();
        Assert.All(counts.Where((_, i) => i is 0 or 5 or 9 or 10), count => Assert.True(count > 0));
        stores.Begin(HistoryClear.Create("chrom.exe", allTime: false, Asked));
        var first = stores.Step();
        Assert.All(first, progress => Assert.True(progress.Finished));
        Assert.All(first, progress => Assert.Equal(progress.Total is null ? null : 100, progress.Percent));
        Assert.Null(first[3].Total);
        stores.AssertNoMarkers();
        Assert.Equal(counts, stores.Counts());
    }
}
