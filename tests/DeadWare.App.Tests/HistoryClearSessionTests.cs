using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>The session side of Clear history…: one clear at a time, Try again, unavailable stores, the re-read event and the quit deadlines.</summary>
[Collection(TestHostThreadPool.TimingCollection)]
public class HistoryClearSessionTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static string Root() { string root = Path.Combine(Path.GetTempPath(), "DeadWare-clear-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }

    /// <summary>An uninitialized session holding real services on temp files, as CoolingShutdownTests builds one, with Settings that exclude one app.
    /// <paramref name="usagePath"/> replaces Usage history's file (a directory there stops its worker).</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root;
        public readonly UiSession Session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        public readonly ConnectionService Connections;
        public readonly UsageHistoryService Usage, AlertUsage;
        public readonly ProcessHistoryService ProcessHistory;
        public readonly AlertService Alerts;
        public Fixture(string? usagePath = null, ConnectionService? connections = null, string? root = null)
        {
            Root = root ?? HistoryClearSessionTests.Root();
            Connections = connections ?? new ConnectionService(Path.Combine(Root, "connections.db"));
            Usage = new UsageHistoryService(usagePath ?? Path.Combine(Root, "usage-history.db"), "fixture");
            ProcessHistory = new ProcessHistoryService(Path.Combine(Root, "process-history.db"), "fixture");
            AlertUsage = new UsageHistoryService(Path.Combine(Root, "usage-alerts.db"), "fixture");
            Alerts = new AlertService(Path.Combine(Root, "alerts.db"), "fixture", AlertUsage, () => null);
            SetField(Session, "<Settings>k__BackingField", new UserSettings { ExcludedApps = ["Steam.exe"] });
            SetField(Session, "<Connections>k__BackingField", Connections); SetField(Session, "<Usage>k__BackingField", Usage);
            SetField(Session, "<ProcessHistory>k__BackingField", ProcessHistory); SetField(Session, "<Alerts>k__BackingField", Alerts);
        }
        public void Start() { Connections.Start(); Usage.Start(); ProcessHistory.Start(); Alerts.Start(); }
        public IEnumerable<HistoryClearState> States => [Connections.ClearState, Usage.ClearState, ProcessHistory.ClearState, Alerts.ClearState];
        public async ValueTask DisposeAsync()
        {
            Connections.ClearStepOverride = null; Usage.ClearStepOverride = null; ProcessHistory.ClearStepOverride = null; Alerts.ClearStepOverride = null;
            await Task.WhenAll(Connections.StopAsync(), Usage.StopAsync(), ProcessHistory.StopAsync(), Alerts.StopAsync(), AlertUsage.StopAsync()).WaitAsync(TimeSpan.FromSeconds(30));
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what + ".");
            await Task.Delay(5);
        }
    }
    private static void UntilNow(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for " + what + ".");
            Thread.Sleep(5);
        }
    }
    private static HistoryClear AllAppsAllTime() => HistoryClear.Create(null, allTime: true, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ClearHistory_refuses_a_second_request_and_reports_a_stopped_worker_as_unavailable()
    {
        // The Don't record list answers from the loaded settings, by key, and never throws.
        {
            var bare = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            SetField(bare, "<Settings>k__BackingField", new UserSettings { ExcludedApps = ["Steam.exe"] });
            Assert.True(bare.IsExcluded("STEAM.EXE")); Assert.False(bare.IsExcluded("chrome.exe")); Assert.False(bare.IsExcluded(null));
            Assert.Contains(AppKey.Of("steam.exe"), bare.ExcludedKeys);
            // A fixture session without services refuses nothing and has no lines.
            Assert.False(bare.ClearActive); Assert.False(bare.ClearWaitingForRestart); Assert.Empty(bare.ClearLines());
        }
        await using (var fixture = new Fixture())
        {
            var session = fixture.Session; fixture.Start();
            // One clear at a time: a second request while any store is Queued or Clearing is refused; once all are Done a new one starts.
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            Assert.True(session.ClearActive);
            Assert.False(session.ClearHistory(AllAppsAllTime()));
            Assert.Equal([HistoryClearStore.Connections, HistoryClearStore.Usage, HistoryClearStore.ProcessHistory, HistoryClearStore.Alerts], session.ClearLines().Select(l => l.Store));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "the first clear to finish");
            Assert.False(session.ClearActive); Assert.All(session.ClearLines(), l => Assert.False(l.CanRetry));
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "the second clear to finish");

            // A Usage step fails once: Failed with MarkerSaved true while its worker runs. A new clear is refused, and Try again finishes it.
            fixture.Usage.ClearStepOverride = _ => { fixture.Usage.ClearStepOverride = null; throw new IOException("fixture step failure"); };
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.Usage.ClearState.Phase == HistoryClearPhase.Failed && fixture.States.Count(s => s.Phase == HistoryClearPhase.Done) == 3, "the Usage step to fail");
            Assert.Equal(new HistoryClearState(HistoryClearPhase.Failed, null, "IOException", true, false), fixture.Usage.ClearState);
            Assert.True(session.ClearActive); Assert.False(session.ClearWaitingForRestart);
            Assert.False(session.ClearHistory(AllAppsAllTime()));
            Assert.True(Assert.Single(session.ClearLines(), l => l.Store == HistoryClearStore.Usage).CanRetry);
            Assert.Equal(HistoryClearPhase.Done, fixture.Alerts.ClearState.Phase);
            session.RetryClear(HistoryClearStore.Usage);
            await Until(() => fixture.Usage.ClearState.Phase == HistoryClearPhase.Done, "Try again to finish the Usage clear");
            Assert.False(session.ClearActive);
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "the clear after Try again to finish");

            // A failed Usage read with its worker running sets Failed, but the store is not unavailable: the request runs.
            typeof(UsageHistoryService).GetMethod("Fail", Flags)!.Invoke(fixture.Usage, [new IOException("fixture read failure")]);
            Assert.True(fixture.Usage.Failed);
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            Assert.NotEqual(HistoryClearPhase.Unavailable, fixture.Usage.ClearState.Phase);
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "the clear with a failed read to finish");

            // A failure without a saved marker does not hold a new clear back.
            fixture.Alerts.ClearState = new(HistoryClearPhase.Failed, null, "SqliteException", false, false);
            Assert.False(session.ClearActive);
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "the clear after a not-cleared failure to finish");
        }
        // The Usage worker stopped by a failure: its request is Unavailable with that failure's type, and nothing is written for it.
        string broken = Path.Combine(Path.GetTempPath(), "DeadWare-clear-session-broken-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(broken);
        try
        {
            await using var fixture = new Fixture(usagePath: broken);
            var session = fixture.Session; fixture.Start();
            await Until(() => fixture.Usage.Failed && !fixture.Usage.CanRetryClear, "the Usage worker to stop");
            await Until(() => (Task)typeof(UsageHistoryService).GetField("worker", Flags)!.GetValue(fixture.Usage)! is { IsCompleted: true }, "the Usage worker task to end");
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            var state = fixture.Usage.ClearState;
            Assert.Equal(HistoryClearPhase.Unavailable, state.Phase); Assert.NotNull(state.Error); Assert.False(state.MarkerSaved);
            Assert.Contains($"({state.Error})", fixture.Usage.Status);
            await Until(() => new[] { fixture.Connections.ClearState, fixture.ProcessHistory.ClearState, fixture.Alerts.ClearState }.All(s => s.Phase == HistoryClearPhase.Done), "the other stores to finish");
            Assert.False(File.Exists(Path.ChangeExtension(broken, ".clear.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(broken));
            Assert.False(session.ClearActive); Assert.False(session.ClearWaitingForRestart);
            Assert.False(Assert.Single(session.ClearLines(), l => l.Store == HistoryClearStore.Usage).CanRetry);
            // A store whose worker stopped with its marker saved waits for the next start: refused, and not offered Try again.
            fixture.Usage.ClearState = new(HistoryClearPhase.Failed, null, "IOException", true, false);
            Assert.True(session.ClearActive); Assert.True(session.ClearWaitingForRestart);
            Assert.False(session.ClearHistory(AllAppsAllTime()));
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(broken, true); } catch (IOException) { } }
    }

    [Fact]
    public void HistoryClearChanged_rereads_the_open_pages()
    {
        var fixture = new Fixture();
        var gates = new Dictionary<HistoryClearStore, ManualResetEventSlim>();
        try
        {
            var session = fixture.Session;
            var raised = new Dictionary<HistoryClearStore, int>();
            session.HistoryClearChanged += store => raised[store] = raised.GetValueOrDefault(store) + 1;
            Func<TimeSpan, HistoryClearProgress> Gated(HistoryClearStore store)
            {
                var gate = gates[store] = new ManualResetEventSlim();
                return _ => { gate.Wait(TimeSpan.FromSeconds(30)); return new(1, 1, true); };
            }
            fixture.Connections.ClearStepOverride = Gated(HistoryClearStore.Connections);
            fixture.Usage.ClearStepOverride = Gated(HistoryClearStore.Usage);
            fixture.ProcessHistory.ClearStepOverride = Gated(HistoryClearStore.ProcessHistory);
            fixture.Alerts.ClearStepOverride = Gated(HistoryClearStore.Alerts);
            fixture.Start();
            session.CheckClearStates(); Assert.Empty(raised);
            Assert.True(session.ClearHistory(AllAppsAllTime()));
            // Each store's step waits at its gate, so each is Clearing.
            UntilNow(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Clearing), "every store to start clearing");
            session.CheckClearStates();
            Assert.Equal(4, raised.Count); Assert.All(raised.Values, count => Assert.Equal(1, count));
            session.CheckClearStates(); Assert.All(raised.Values, count => Assert.Equal(1, count));
            // Connections leaves Clearing first: only it is raised again.
            gates[HistoryClearStore.Connections].Set();
            UntilNow(() => fixture.Connections.ClearState.Phase == HistoryClearPhase.Done, "the Connections clear to finish");
            session.CheckClearStates();
            Assert.Equal(2, raised[HistoryClearStore.Connections]);
            Assert.Equal([1, 1, 1], new[] { HistoryClearStore.Usage, HistoryClearStore.ProcessHistory, HistoryClearStore.Alerts }.Select(s => raised[s]));
            foreach (var gate in gates.Values) gate.Set();
            UntilNow(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Done), "every clear to finish");
            session.CheckClearStates();
            Assert.All(raised.Values, count => Assert.Equal(2, count));
            session.CheckClearStates();
            Assert.All(raised.Values, count => Assert.Equal(2, count));
        }
        finally { foreach (var gate in gates.Values) gate.Set(); fixture.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    /// <summary>The CoolingShutdownTests.StuckSession wiring, with the collector's worker already finished, ProcessHistory set and the trace.</summary>
    private static Collector QuitCollector(Fixture fixture)
    {
        var worker = new TaskCompletionSource(); worker.SetResult();
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        SetField(collector, "cancellation", new CancellationTokenSource()); SetField(collector, "worker", worker.Task); SetField(collector, "etwStopGate", new object());
        SetField(collector, "publicationGate", new object()); SetField(collector, "clock", Stopwatch.StartNew()); SetField(collector, "options", new CollectorOptions());
        SetField(collector, "etw", new EtwNetwork(_ => new(11, 1, "fixture")));
        var session = fixture.Session; string root = fixture.Root;
        SetField(session, "<Collector>k__BackingField", collector);
        SetField(session, "<Cooling>k__BackingField", new CoolingHistoryService(Path.Combine(root, "cooling.db"), "fixture", "setup"));
        SetField(session, "<GraphHistory>k__BackingField", new GraphHistoryService(Path.Combine(root, "graphs.db"), "fixture", new SampleHistory()));
        SetField(session, "<Battery>k__BackingField", new BatteryHistoryService(Path.Combine(root, "battery.db"), "fixture"));
        SetField(session, "diagnostics", new LayoutDiagnostics(Path.Combine(root, "trace.jsonl"), Path.Combine(root, "settings.json")));
        collector.SampleAvailable += fixture.Connections.TryRecord; collector.FinalConnectionsAvailable += fixture.Connections.TryRecord;
        return collector;
    }
    /// <summary>Every service's step sleeps 200 ms and never finishes, so the clears outlast the stop.</summary>
    private static void SlowSteps(Fixture fixture)
    {
        static HistoryClearProgress Slow(TimeSpan _) { Thread.Sleep(200); return new(0, 10, false); }
        fixture.Connections.ClearStepOverride = Slow; fixture.Usage.ClearStepOverride = Slow; fixture.ProcessHistory.ClearStepOverride = Slow; fixture.Alerts.ClearStepOverride = Slow;
    }

    [Fact]
    public async Task Quit_during_a_clear_meets_the_history_deadline()
    {
        // Quit: StopCollectorAsync with Quit's deadlines (5 s providers, 5 s final drain, 20 s history), as ShutdownAsync calls it.
        await using (var fixture = new Fixture())
        {
            QuitCollector(fixture); SlowSteps(fixture); fixture.Start();
            Assert.True(fixture.Session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Clearing && s.MarkerSaved), "every store to start clearing");
            var watch = Stopwatch.StartNew();
            var stop = (Task)typeof(UiSession).GetMethod("StopCollectorAsync", Flags)!.Invoke(fixture.Session, [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)])!;
            await stop.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Quit took {watch.Elapsed}.");
            Assert.True(fixture.Connections.StopAsync().IsCompleted); Assert.True(fixture.Usage.StopAsync().IsCompleted);
            Assert.True(fixture.ProcessHistory.StopAsync().IsCompleted); Assert.True(fixture.Alerts.StopAsync().IsCompleted);
            Assert.DoesNotContain("collector_stop_timeout", File.ReadAllText(Path.Combine(fixture.Root, "trace.jsonl")));
            // The clears stay unfinished; their markers resume at the next start.
            Assert.True(File.Exists(Path.Combine(fixture.Root, "usage-history.clear.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Root, "process-history.clear.json")));
        }
        // Sign-out: a hidden Connections backlog (an all-apps clear raised expiry over rows routine maintenance has not purged) and slow steps.
        {
            string root = Root(); string path = Path.Combine(root, "connections.db");
            var now = DateTimeOffset.UtcNow; var first = UsageMetrics.Floor(now).AddHours(-3);
            var seed = new ConnectionHistoryStore(path); seed.Initialize();
            seed.Append([.. Enumerable.Range(0, 6000).Select(n => new ConnectionHistoryRecord("seed" + n, "segment",
                new(first.AddMinutes(n % 120), new(new(7, n, "app" + n % 40 + ".exe"), "192.168.1.234", 49123, "8.8." + n / 250 % 250 + "." + n % 250, 443, ConnectionProtocol.Tcp), 10, 20,
                    first.AddMinutes(n % 120).AddSeconds(1), first.AddMinutes(n % 120).AddSeconds(2)), null, null, true))], [], now);
            SqliteConnection.ClearAllPools();
            long seeded = Count(path, "SELECT COUNT(*) FROM traffic");
            Assert.True(seeded >= 6000, $"seeded {seeded} rows");
            var maintenance = new List<(bool Stopping, int Bound)>(); bool stopping = false; var sync = new object();
            var connections = new ConnectionService(path, new NoLocation(), () => DateTimeOffset.UtcNow,
                inner => (records, coverage, at, bound) => { lock (sync) maintenance.Add((Volatile.Read(ref stopping), bound)); inner(records, coverage, at, bound); });
            await using var fixture = new Fixture(connections: connections, root: root);
            QuitCollector(fixture); SlowSteps(fixture); fixture.Start();
            Assert.True(fixture.Session.ClearHistory(AllAppsAllTime()));
            await Until(() => fixture.States.All(s => s.Phase == HistoryClearPhase.Clearing && s.MarkerSaved) && connections.PendingClear is not null, "every store to start clearing");
            // The rows are hidden at once but still in the file: nothing has purged them so far.
            Assert.Equal(0, (await connections.ReadViewAsync(new(first, now.AddMinutes(1)))).Rows.Count);
            Assert.Equal(seeded, Count(path, "SELECT COUNT(*) FROM traffic"));
            Volatile.Write(ref stopping, true);
            var watch = Stopwatch.StartNew();
            typeof(UiSession).GetMethod("StopCollectorForSessionEnding", Flags)!.Invoke(fixture.Session, null);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Sign-out took {watch.Elapsed}.");
            Assert.True(connections.StopAsync().IsCompleted); Assert.True(fixture.Usage.StopAsync().IsCompleted);
            Assert.True(fixture.ProcessHistory.StopAsync().IsCompleted); Assert.True(fixture.Alerts.StopAsync().IsCompleted);
            string trace = File.ReadAllText(Path.Combine(root, "trace.jsonl"));
            Assert.Contains("session_end_collector_stopped", trace);
            Assert.DoesNotContain("\"collector_stop_timeout\"", trace); Assert.DoesNotContain("session_end_collector_stop_timeout", trace);
            // The final write runs no routine maintenance, so the hidden backlog never holds sign-out up.
            lock (sync) Assert.All(maintenance.Where(m => m.Stopping), m => Assert.Equal(0, m.Bound));
            Assert.Equal(seeded, Count(path, "SELECT COUNT(*) FROM traffic"));
        }
    }
    private sealed class NoLocation : IConnectionGeolocation { public ConnectionLocation? Lookup(string address) => null; }
    private static long Count(string database, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
