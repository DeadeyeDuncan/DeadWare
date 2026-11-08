using System.Reflection;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class AppKeyTests
{
    private static readonly string[] Names =
        ["Chrome.EXE", " chrome.exe", "CHROME.EXE ", "chr\u0001ome.exe", "", "Ärger.exe", "a\uD800b.exe", new string('a', 300), new string('b', 255) + " x", "PID 4"];

    private static string Scalar(SqliteConnection db, string sql, string? name = null)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        if (name is not null) command.Parameters.AddWithValue("$n", name);
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Reach(object store, params object[] args)
        => (SqliteConnection)store.GetType().GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, args)!;

    [Fact]
    public void AppKey_folds_case_spelling_and_surrogates()
    {
        Assert.Equal(AppKey.Of("chrome.exe"), AppKey.Of("Chrome.EXE")); Assert.Equal(AppKey.Of("chrome.exe"), AppKey.Of("CHROME.EXE ")); Assert.Equal("CHROME.EXE", AppKey.Of(" Chrome.exe\u0001 "));
        Assert.Equal("A\uFFFDB.EXE", AppKey.Of("a\uD800b.exe")); Assert.Equal(AppKey.Of(ProcessHistory.SavedName("a\uD800b.exe")), AppKey.Of("a\uD800b.exe"));
        Assert.Equal("UNKNOWN APPLICATION", AppKey.Of(null)); Assert.Equal("UNKNOWN APPLICATION", AppKey.Of(""));
        foreach (var n in Names) { Assert.Equal(AppKey.Of(n), ProcessGrouping.Key(n)); Assert.Equal(AppKey.Of(n), AppKey.Of(AppKey.Of(n))); }
        Assert.True(AppKey.IsPidName("pid 4294967295")); Assert.False(AppKey.IsPidName("PID 4.exe")); Assert.False(AppKey.IsPidName("PID 12345678901"));

        var directory = Path.Combine(Path.GetTempPath(), "deadware-appkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // process-history.db: names.upper equals AppKey.Of(display).
            var historyPath = Path.Combine(directory, "process-history.db");
            var history = new ProcessHistoryStore(historyPath); history.Initialize();
            var minute = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
            ProcessAppMinute App(string name) => new(name, 1, 0, 0, 0, false);
            history.Append("m", [new ProcessMinuteBatch(minute, 60, 4, true, "seg", [App(ProcessHistory.SavedName("a\uD800b.exe")!), App("Chrome.EXE"), App("Ärger.exe")])], minute.AddMinutes(2));
            SqliteConnection.ClearAllPools();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = historyPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "SELECT display,upper FROM names";
                using var reader = command.ExecuteReader(); int rows = 0;
                while (reader.Read()) { rows++; Assert.Equal(AppKey.Of(reader.GetString(0)), reader.GetString(1)); }
                Assert.Equal(3, rows);
            }

            // connections.db and alerts.db: the registered app_key function agrees.
            var connections = new ConnectionHistoryStore(Path.Combine(directory, "connections.db")); connections.Initialize();
            var alerts = new AlertStore(Path.Combine(directory, "alerts.db")); alerts.Initialize();
            var opened = new List<SqliteConnection>
            {
                Reach(connections, false, false),
                Reach(alerts, false, false, true),
                Reach(alerts, true, false, true),
            };
            try
            {
                foreach (var db in opened)
                    foreach (var n in Names) Assert.Equal(AppKey.Of(n), Scalar(db, "SELECT app_key($n)", n));
            }
            finally { foreach (var db in opened) db.Dispose(); SqliteConnection.ClearAllPools(); }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public void Exclusions_normalize_drop_invalid_and_duplicates_and_cap_at_64()
    {
        Assert.Empty(AppExclusions.Normalize(null));
        Assert.Equal(["chrome.exe"], AppExclusions.Normalize(["", "  ", "Unknown application", "everything ELSE", @"C:\a.exe", "a/b", "a*", "PID 123", "pid 9", "chrome.exe", "Chrome.exe"]));
        var many = Enumerable.Range(0, 70).Select(i => $"app{i}.exe").ToArray(); Assert.Equal(many.Take(64), AppExclusions.Normalize(many));
        Assert.False(AppExclusions.Accepts("PID 7")); Assert.True(AppExclusions.Accepts("PID 7.exe")); Assert.Equal(64, AppExclusions.MaximumApps);
        Assert.True(AppExclusions.Keys(["chrome.exe", "Steam.exe"]).SetEquals(["CHROME.EXE", "STEAM.EXE"]));
    }

    [Fact]
    public void Excluded_apps_round_trip_and_a_file_without_them_reads_empty()
    {
        var s = new UserSettings(); s.ExcludedApps.AddRange(["chrome.exe", "Steam.exe"]); Assert.Equal(["chrome.exe", "Steam.exe"], SettingsCodec.Read(SettingsCodec.Write(s)).Settings.ExcludedApps);
        foreach (var json in new[] { "{}", "{\"ExcludedApps\":null}", "{\"ExcludedApps\":[\"\",\"a*\",\"x.exe\",\"X.EXE\"]}" }) Assert.Null(SettingsCodec.Read(json).Error);
        Assert.Empty(SettingsCodec.Read("{}").Settings.ExcludedApps); Assert.Equal(["x.exe"], SettingsCodec.Read("{\"ExcludedApps\":[\"\",\"a*\",\"x.exe\",\"X.EXE\"]}").Settings.ExcludedApps);
    }
}
