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
}
