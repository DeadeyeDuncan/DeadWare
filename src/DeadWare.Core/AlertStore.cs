using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

public sealed record AlertLogView(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AlertEpisode> Episodes, long Total, long StorageBytes);

/// <summary>Local alert episode log. Synchronous calls belong to one background owner; never call from the collector or UI thread.</summary>
public sealed class AlertStore
{
    public const long MaximumDatabaseBytes = 64L * 1024 * 1024;
    public const int RetentionDays = 400;
    public const int MaximumBatch = 1024;
    public const int MaximumRows = 500;
    private const long MaximumJournalBytes = 8L * 1024 * 1024;
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS episodes(
            machine TEXT NOT NULL, id TEXT NOT NULL, rule TEXT NOT NULL, kind INTEGER NOT NULL, summary TEXT NOT NULL,
            start INTEGER NOT NULL, finish INTEGER, peak REAL NOT NULL, acknowledged INTEGER NOT NULL CHECK(acknowledged IN(0,1)),
            PRIMARY KEY(machine,id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS episodes_time ON episodes(machine,start);
        CREATE TABLE IF NOT EXISTS known_apps(
            machine TEXT NOT NULL, app_key TEXT NOT NULL,
            first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL CHECK(last_seen>=first_seen),
            PRIMARY KEY(machine,app_key)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS episode_apps(
            machine TEXT NOT NULL, episode TEXT NOT NULL, name TEXT NOT NULL,
            PRIMARY KEY(machine,episode)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS alert_metadata(
            machine TEXT NOT NULL, key TEXT NOT NULL, value INTEGER NOT NULL,
            PRIMARY KEY(machine,key)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS pending_clear(id INTEGER PRIMARY KEY CHECK(id=1), app_key TEXT, before INTEGER NOT NULL, requested INTEGER NOT NULL);
        PRAGMA user_version=1;
        """;
    private const string KnownUpsert = """
        INSERT INTO known_apps(machine,app_key,first_seen,last_seen) VALUES($machine,$key,$first,$last)
        ON CONFLICT(machine,app_key) DO UPDATE SET first_seen=min(first_seen,excluded.first_seen), last_seen=max(last_seen,excluded.last_seen)
        """;
    public const string SeedUsageKey = "first_use_seed_usage", SeedConnectionsKey = "first_use_seed_connections",
        LearningStartKey = "first_use_learning_start", LearningEndKey = "first_use_learning_end";
    /// <summary>At most this many in-scope episodes, with their links, are deleted in one clear transaction.</summary>
    public const int ClearEpisodeChunk = 256;
    /// <summary>At most this many other machine keys' known rows are deleted in one clear transaction.</summary>
    public const int ClearKnownChunk = 1024;
    private readonly string path;
    private readonly long maximumDatabaseBytes;
    private bool initialized;
    private readonly object clearGate = new();
    private HistoryClear? pending;
    private ClearState? clearing;
    // Test seams, set by reflection; always these defaults in the app.
    private Func<TimeSpan> clearClock = HistoryClearStep.MonotonicClock;
    private Action<int>? clearCommitted = null;
    private Action<SqliteConnection>? clearConnectionOpened = null;

    public AlertStore(string path, long maximumDatabaseBytes = MaximumDatabaseBytes)
    {
        if (maximumDatabaseBytes is < 65536 or > MaximumDatabaseBytes) throw new ArgumentOutOfRangeException(nameof(maximumDatabaseBytes));
        this.path = Path.GetFullPath(path); this.maximumDatabaseBytes = maximumDatabaseBytes;
    }

    public long StorageBytes => new[] { path, path + "-wal", path + "-shm" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(create: true, configure: false);
        long version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version is not (0 or 1)) throw new InvalidDataException("The alert log belongs to a newer DeadWare version.");
        Configure(db);
        Execute(db, "PRAGMA journal_mode=WAL");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                Execute(db, Schema, transaction);
                transaction.Commit();
                break;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                // The file is at its cap and a new table needs a page: drop the oldest tenth of the closed episodes and retry once.
                if (attempt > 0 || !EvictOldestClosed(db)) throw new IOException("The alert log is full and could not be upgraded.", error);
            }
        }
        var marker = ReadMarker(db);
        lock (clearGate) { pending = marker; clearing = marker is null ? null : new(path); }
        if (marker is not null) HistoryClearStep.SecureDelete(db);
        using (var transaction = db.BeginTransaction())
        {
            Execute(db, "DELETE FROM episode_apps WHERE NOT EXISTS(SELECT 1 FROM episodes e WHERE e.machine=episode_apps.machine AND e.id=episode_apps.episode)", transaction);
            transaction.Commit();
        }
        Checkpoint(db); initialized = true;
    }

    /// <summary>Inserts new episodes and updates the end, peak and summary of known ones. Acknowledgement is never cleared by an update.
    /// The first-use changes are saved in the same transaction.</summary>
    public void Append(string machine, IReadOnlyList<AlertEpisode> episodes, DateTimeOffset now, FirstUseChanges? firstUse = null)
    {
        EnsureInitialized(); ValidateMachine(machine); ArgumentNullException.ThrowIfNull(episodes);
        if (episodes.Count > MaximumBatch) throw new ArgumentException($"Alert log batches are limited to {MaximumBatch} episodes.", nameof(episodes));
        foreach (var episode in episodes) Validate(episode);
        if (firstUse is not null) Validate(firstUse);
        bool changes = firstUse is { } f && (f.Upserts.Count > 0 || f.Deletes.Count > 0 || Metadata(f.Metadata).Count > 0);
        if (episodes.Count == 0 && !changes) return;
        long cutoff = now.ToUnixTimeMilliseconds() - RetentionDays * 86_400_000L;
        using var db = Open();
        while (true)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                Execute(db, "DELETE FROM episode_apps WHERE (machine,episode) IN (SELECT machine,id FROM episodes WHERE start<$cutoff)", transaction, ("$cutoff", cutoff));
                Execute(db, "DELETE FROM episodes WHERE start<$cutoff", transaction, ("$cutoff", cutoff));
                Execute(db, "DELETE FROM known_apps WHERE last_seen<$cutoff", transaction, ("$cutoff", cutoff));
                foreach (var episode in episodes)
                {
                    if (episode.Start.ToUnixTimeMilliseconds() < cutoff) continue;
                    Execute(db, """
                        INSERT INTO episodes(machine,id,rule,kind,summary,start,finish,peak,acknowledged)
                        VALUES($machine,$id,$rule,$kind,$summary,$start,$finish,$peak,$acknowledged)
                        ON CONFLICT(machine,id) DO UPDATE SET finish=excluded.finish, peak=excluded.peak, summary=excluded.summary,
                            acknowledged=MAX(acknowledged,excluded.acknowledged)
                        """, transaction, ("$machine", machine), ("$id", episode.Id), ("$rule", episode.RuleId), ("$kind", (int)episode.Kind),
                        ("$summary", episode.Summary), ("$start", episode.Start.ToUnixTimeMilliseconds()),
                        ("$finish", episode.End is { } end ? end.ToUnixTimeMilliseconds() : DBNull.Value), ("$peak", episode.Peak),
                        ("$acknowledged", episode.Acknowledged ? 1 : 0));
                    if (episode.Application is null) continue;
                    Execute(db, "INSERT INTO episode_apps(machine,episode,name) VALUES($machine,$id,$name) ON CONFLICT DO NOTHING", transaction,
                        ("$machine", machine), ("$id", episode.Id), ("$name", episode.Application));
                    if (episode.Kind == AlertKind.FirstNetworkUse)
                    {
                        long minute = Minute(episode.Start.ToUnixTimeMilliseconds());
                        Execute(db, KnownUpsert, transaction, ("$machine", machine), ("$key", AppKey.Of(episode.Application)), ("$first", minute), ("$last", minute));
                    }
                }
                if (firstUse is not null)
                {
                    foreach (var key in firstUse.Deletes)
                        Execute(db, "DELETE FROM known_apps WHERE machine=$machine AND app_key=$key", transaction, ("$machine", machine), ("$key", key));
                    foreach (var known in firstUse.Upserts)
                        Execute(db, KnownUpsert, transaction, ("$machine", machine), ("$key", known.Key),
                            ("$first", known.First.ToUnixTimeMilliseconds()), ("$last", known.Last.ToUnixTimeMilliseconds()));
                    foreach (var (name, value) in Metadata(firstUse.Metadata))
                        Execute(db, "INSERT INTO alert_metadata(machine,key,value) VALUES($machine,$key,$value) ON CONFLICT DO NOTHING", transaction,
                            ("$machine", machine), ("$key", name), ("$value", value));
                }
                transaction.Commit();
                break;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13)
            {
                // The file reached its cap: drop the oldest tenth of the closed episodes (with their links) and retry; open ones are never dropped.
                if (!EvictOldestClosed(db)) throw new IOException("The alert log is full of open episodes; the batch was not saved.", error);
            }
        }
        Checkpoint(db);
    }

    /// <summary>The seed markers, learning dates and the most recently seen known apps (at most 8,192).</summary>
    public FirstUseState ReadFirstUse(string machine)
    {
        EnsureInitialized(); ValidateMachine(machine);
        using var db = Open(readOnly: true);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var command = Command(db, "SELECT key,value FROM alert_metadata WHERE machine=$machine", null, ("$machine", machine)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) values[reader.GetString(0)] = reader.GetInt64(1);
        DateTimeOffset? At(string key) => values.TryGetValue(key, out long v) ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
        var known = new List<KnownApplication>();
        using (var command = Command(db, "SELECT app_key,first_seen,last_seen FROM known_apps WHERE machine=$machine ORDER BY last_seen DESC,app_key LIMIT $limit",
            null, ("$machine", machine), ("$limit", FirstNetworkUse.MaximumKnown)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                known.Add(new(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        return new(At(SeedUsageKey), At(SeedConnectionsKey), At(LearningStartKey), At(LearningEndKey), known);
    }

    private static List<(string Name, long Value)> Metadata(FirstUseMetadata m)
    {
        var list = new List<(string, long)>();
        void Add(string name, DateTimeOffset? value) { if (value is { } v) list.Add((name, v.ToUnixTimeMilliseconds())); }
        Add(SeedUsageKey, m.SeededUsage); Add(SeedConnectionsKey, m.SeededConnections); Add(LearningStartKey, m.LearningStart); Add(LearningEndKey, m.LearningEnd);
        return list;
    }
    private static long Minute(long milliseconds) => milliseconds - ((milliseconds % 60_000) + 60_000) % 60_000;

    /// <summary>Episodes that overlap the range, newest first, bounded by <paramref name="maxRows"/>.</summary>
    public AlertLogView Read(string machine, DateTimeOffset from, DateTimeOffset to, int maxRows = MaximumRows)
    {
        EnsureInitialized(); ValidateMachine(machine);
        if (from >= to) throw new ArgumentOutOfRangeException(nameof(from));
        if (maxRows is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maxRows));
        using var db = Open(readOnly: true);
        (string, object)[] args = [("$machine", machine), ("$from", from.ToUnixTimeMilliseconds()), ("$to", to.ToUnixTimeMilliseconds())];
        const string Where = "WHERE e.machine=$machine AND e.start<$to AND (e.finish IS NULL OR e.finish>=$from)";
        var episodes = new List<AlertEpisode>();
        using (var command = Command(db, $"SELECT e.id,e.rule,e.kind,e.summary,e.start,e.finish,e.peak,e.acknowledged,a.name FROM episodes e LEFT JOIN episode_apps a ON a.machine=e.machine AND a.episode=e.id {Where} ORDER BY e.start DESC,e.id LIMIT $limit", null, [.. args, ("$limit", maxRows)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) episodes.Add(ReadEpisode(reader));
        long total = Convert.ToInt64(ScalarWith(db, $"SELECT COUNT(*) FROM episodes e {Where}", args), CultureInfo.InvariantCulture);
        return new(from, to, episodes, total, StorageBytes);
    }

    /// <summary>Episodes without an end, oldest first, for restoring evaluator state at startup.</summary>
    public IReadOnlyList<AlertEpisode> Open(string machine)
    {
        EnsureInitialized(); ValidateMachine(machine);
        using var db = Open(readOnly: true);
        using var command = Command(db, "SELECT e.id,e.rule,e.kind,e.summary,e.start,e.finish,e.peak,e.acknowledged,a.name FROM episodes e LEFT JOIN episode_apps a ON a.machine=e.machine AND a.episode=e.id WHERE e.machine=$machine AND e.finish IS NULL ORDER BY e.start,e.id LIMIT $limit",
            null, ("$machine", machine), ("$limit", AlertRules.MaximumRules));
        using var reader = command.ExecuteReader();
        var result = new List<AlertEpisode>();
        while (reader.Read()) result.Add(ReadEpisode(reader));
        return result;
    }

    public bool Acknowledge(string machine, string id)
    {
        EnsureInitialized(); ValidateMachine(machine); ValidateId(id);
        using var db = Open();
        int changed = Execute(db, "UPDATE episodes SET acknowledged=1 WHERE machine=$machine AND id=$id", null, ("$machine", machine), ("$id", id));
        Checkpoint(db);
        return changed > 0;
    }

    /// <summary>The clear in progress: from the <c>pending_clear</c> row's write until the last clear transaction deletes it, and from
    /// <see cref="Initialize"/> while the row exists.</summary>
    public HistoryClear? PendingClear { get { lock (clearGate) return pending; } }

    /// <summary>A clear's in-memory state. A resumed clear scans again from the start.</summary>
    private sealed class ClearState(string path)
    {
        public readonly HistoryClearStep Step = new(path);
        public long Done;
    }

    private static HistoryClear? ReadMarker(SqliteConnection db)
    {
        using var command = Command(db, "SELECT app_key,before,requested FROM pending_clear WHERE id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string? key = reader.IsDBNull(0) ? null : reader.GetString(0);
        long before = reader.GetInt64(1), requested = reader.GetInt64(2);
        static bool Valid(long ms) => ms >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && ms <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        // A row no build could have written is no request; BeginClear replaces it.
        if (key is { Length: 0 } || !Valid(before) || !Valid(requested)) return null;
        return new(key, DateTimeOffset.FromUnixTimeMilliseconds(before), DateTimeOffset.FromUnixTimeMilliseconds(requested));
    }

    // The request as pending_clear holds it: the key and whole UTC milliseconds.
    private static (string? Key, long Before, long Requested) Saved(HistoryClear clear)
        => (clear.AppKey, clear.Before.ToUnixTimeMilliseconds(), clear.Requested.ToUnixTimeMilliseconds());

    /// <summary>
    /// Records a clear in one transaction: the <c>pending_clear</c> row and, for All time, the deletion of <paramref name="machine"/>'s
    /// in-scope <c>known_apps</c> rows (the key's row for one app, every row for all apps). A failure (SQLITE_FULL at the cap, a full disk,
    /// access denied) throws and writes nothing. An equal pending request commits nothing and deletes no known row; a different one throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public void BeginClear(string machine, HistoryClear clear)
    {
        EnsureInitialized(); ValidateMachine(machine); ArgumentNullException.ThrowIfNull(clear);
        if (clear.AppKey is { Length: 0 }) throw new ArgumentException("An alert log clear names an application key, or none for all apps.", nameof(clear));
        lock (clearGate)
        {
            if (pending is not null)
            {
                if (Saved(pending) == Saved(clear)) return;
                throw new InvalidOperationException("Another alert log clear is in progress.");
            }
            var (key, before, requested) = Saved(clear);
            object keyValue = (object?)key ?? DBNull.Value;
            using var db = Open();
            // The known rows deleted below hold app keys, so this connection overwrites them although the marker is not saved yet.
            HistoryClearStep.SecureDelete(db);
            using (var transaction = db.BeginTransaction())
            {
                // A row no build could read as a request is replaced.
                Execute(db, "DELETE FROM pending_clear", transaction);
                Execute(db, "INSERT INTO pending_clear(id,app_key,before,requested) VALUES(1,$key,$before,$requested)", transaction,
                    ("$key", keyValue), ("$before", before), ("$requested", requested));
                if (clear.AllTime)
                    Execute(db, "DELETE FROM known_apps WHERE machine=$machine AND ($key IS NULL OR app_key=$key)", transaction,
                        ("$machine", machine), ("$key", keyValue));
                transaction.Commit();
            }
            pending = clear; clearing = new(path);
            Checkpoint(db);
        }
    }

    /// <summary>
    /// Runs the pending clear's delete transactions until <paramref name="budget"/> has passed on the monotonic clock (see
    /// <see cref="HistoryClearStep.Run"/>). In scope are closed episodes that start before <see cref="HistoryClear.AlertCutoff"/>, on
    /// every machine key: first-use episodes found through their <c>episode_apps</c> link, and ApplicationDaily episodes, whose app is the
    /// text before the last " ≥ " of a summary that ends " in a day" (or their link). Each transaction deletes at most
    /// <see cref="ClearEpisodeChunk"/> episodes with their links; then, for All time, at most <see cref="ClearKnownChunk"/> in-scope known
    /// rows of machine keys other than <paramref name="machine"/>. The call that finds nothing left deletes the marker in its transaction.
    /// <c>Total</c> is null (the alert log shows no percent) and <c>Done</c> counts the rows deleted. Throws
    /// <see cref="InvalidOperationException"/> when no clear is pending.
    /// </summary>
    public HistoryClearProgress ClearStep(string machine, TimeSpan budget)
    {
        EnsureInitialized(); ValidateMachine(machine);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        lock (clearGate)
        {
            if (pending is not { } request || clearing is not { } state) throw new InvalidOperationException("No alert log clear is pending.");
            bool finished;
            using (var db = Open())
                finished = state.Step.Run(db, budget, clearClock, () => GuardJournal(db), () => ClearTransaction(db, machine, request, state), clearCommitted);
            if (finished) { pending = null; clearing = null; }
            return new(state.Done, null, finished);
        }
    }

    // A first-use or ApplicationDaily episode whose link names the app (any app for all apps).
    private const string LinkedTo = "e.kind IN(2,4) AND EXISTS(SELECT 1 FROM episode_apps a WHERE a.machine=e.machine AND a.episode=e.id AND ($key IS NULL OR app_key(a.name)=$key))";
    // An ApplicationDaily episode whose summary names the app; daily_app is DailyApp, so the match is AppKey.Of's.
    private const string NamedIn = "e.kind=2 AND ($key IS NULL AND daily_app(e.summary) IS NOT NULL OR daily_app(e.summary)=$key)";
    private const string InScope = "SELECT e.machine,e.id FROM episodes e WHERE e.finish IS NOT NULL AND e.start<$cutoff AND (" + LinkedTo + " OR " + NamedIn + ") ORDER BY e.machine,e.id LIMIT $limit";

    /// <summary>One clear transaction. It scans and decides in the same call, so a step that allows one call still finishes.</summary>
    private HistoryClearTransaction ClearTransaction(SqliteConnection db, string machine, HistoryClear request, ClearState state)
    {
        object key = (object?)request.AppKey ?? DBNull.Value;
        using var transaction = db.BeginTransaction();
        var episodes = new List<(string Machine, string Id)>();
        using (var select = Command(db, InScope, transaction, ("$key", key), ("$cutoff", request.AlertCutoff.ToUnixTimeMilliseconds()), ("$limit", ClearEpisodeChunk)))
        using (var reader = select.ExecuteReader())
            while (reader.Read()) episodes.Add((reader.GetString(0), reader.GetString(1)));
        int rows = episodes.Count;
        foreach (var (owner, id) in episodes)
        {
            Execute(db, "DELETE FROM episode_apps WHERE machine=$machine AND episode=$id", transaction, ("$machine", owner), ("$id", id));
            Execute(db, "DELETE FROM episodes WHERE machine=$machine AND id=$id", transaction, ("$machine", owner), ("$id", id));
        }
        // Then, for All time, other machine keys' known rows. The current machine's went with the marker's transaction, and a row
        // written there since (a cleared app used again) is kept.
        if (rows == 0 && request.AllTime)
            rows = Execute(db, "DELETE FROM known_apps WHERE (machine,app_key) IN(SELECT machine,app_key FROM known_apps " +
                "WHERE machine<>$machine AND ($key IS NULL OR app_key=$key) ORDER BY machine,app_key LIMIT $limit)", transaction, ("$machine", machine), ("$key", key), ("$limit", ClearKnownChunk));
        if (rows == 0)
        {
            Execute(db, "DELETE FROM pending_clear", transaction);
            transaction.Commit();
            return new(true, 0, true);
        }
        transaction.Commit();
        state.Done += rows;
        return new(true, rows, false);
    }

    /// <summary>The app key of an ApplicationDaily summary: <see cref="AppKey.Of"/> of the text before the last " ≥ " of a summary that
    /// ends " in a day" (<see cref="AlertRules.Describe"/>); null for any other summary.</summary>
    internal static string? DailyApp(string? summary)
    {
        const string Suffix = " in a day", Separator = " ≥ ";
        if (summary is null || !summary.EndsWith(Suffix, StringComparison.Ordinal)) return null;
        int at = summary.LastIndexOf(Separator, StringComparison.Ordinal);
        return at <= 0 ? null : AppKey.Of(summary[..at]);
    }

    private static AlertEpisode ReadEpisode(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), (AlertKind)reader.GetInt32(2),
        reader.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), reader.GetDouble(6), reader.GetInt64(7) == 1,
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static bool EvictOldestClosed(SqliteConnection db)
    {
        if (Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='episodes'"), CultureInfo.InvariantCulture) == 0) return false;
        long closed = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM episodes WHERE finish IS NOT NULL"), CultureInfo.InvariantCulture);
        if (closed == 0) return false;
        long count = Math.Max(1, closed / 10);
        bool links = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='episode_apps'"), CultureInfo.InvariantCulture) > 0;
        const string Oldest = "SELECT machine,id FROM episodes WHERE finish IS NOT NULL ORDER BY start,id LIMIT $count";
        using var transaction = db.BeginTransaction();
        if (links) Execute(db, $"DELETE FROM episode_apps WHERE (machine,episode) IN ({Oldest})", transaction, ("$count", count));
        Execute(db, $"DELETE FROM episodes WHERE (machine,id) IN ({Oldest})", transaction, ("$count", count));
        transaction.Commit();
        return true;
    }

    private static void Validate(AlertEpisode episode)
    {
        if (episode is null) throw new ArgumentException("Alert episode is missing.");
        ValidateId(episode.Id); ValidateId(episode.RuleId);
        if (!Enum.IsDefined(episode.Kind) || episode.Summary is null || episode.Summary.Length > 512 || episode.Summary.Any(char.IsControl) ||
            !double.IsFinite(episode.Peak) || episode.End is { } end && end < episode.Start)
            throw new ArgumentException("Invalid alert episode.", nameof(episode));
        if (episode.Application is { } app && (app != UsageMetrics.NormalizeName(app) || AppKey.Of(app) == AppKey.Of(UsageMetrics.UnknownName)))
            throw new ArgumentException("Invalid alert application.", nameof(episode));
        if (episode.Kind == AlertKind.FirstNetworkUse && (episode.Application is null || !FirstNetworkUse.IsLearnable(episode.Application) ||
            episode.End != episode.Start || episode.Peak != 0))
            throw new ArgumentException("Invalid first network use episode.", nameof(episode));
    }
    private static void Validate(FirstUseChanges changes)
    {
        if (changes.Upserts is null || changes.Deletes is null || changes.Metadata is null ||
            changes.Upserts.Count > FirstNetworkUse.MaximumChanges || changes.Deletes.Count > FirstNetworkUse.MaximumChanges)
            throw new ArgumentException("Invalid first use changes.", nameof(changes));
        static bool Valid(string? key) => key is not null && key == AppKey.Of(key) && FirstNetworkUse.IsLearnable(key);
        static bool Whole(DateTimeOffset time) => time.ToUnixTimeMilliseconds() % 60_000 == 0;
        foreach (var known in changes.Upserts)
            if (known is null || !Valid(known.Key) || !Whole(known.First) || !Whole(known.Last) || known.First > known.Last)
                throw new ArgumentException("Invalid known application.", nameof(changes));
        foreach (var key in changes.Deletes)
            if (!Valid(key)) throw new ArgumentException("Invalid known application key.", nameof(changes));
        if (changes.Metadata is { LearningStart: { } start, LearningEnd: { } end } && end <= start)
            throw new ArgumentException("The learning end must follow its start.", nameof(changes));
    }
    private static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid alert identifier.", nameof(id));
    }
    private static void ValidateMachine(string machine)
    { if (string.IsNullOrWhiteSpace(machine) || machine.Length > 256) throw new ArgumentException("Invalid machine key.", nameof(machine)); }
    private void EnsureInitialized()
    { if (!initialized) throw new InvalidOperationException("Alert log storage has not been initialized."); }
    private SqliteConnection Open(bool readOnly = false, bool create = false, bool configure = true)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumDatabaseBytes)
            throw new IOException("The alert log exceeds its size limit; the file was preserved.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = 1 }.ToString());
        try
        {
            db.Open();
            db.CreateFunction<string, string>("app_key", AppKey.Of, isDeterministic: true);
            db.CreateFunction<string?, string?>("daily_app", DailyApp, isDeterministic: true);
            if (!readOnly && configure)
            {
                Configure(db);
                // While a clear marker exists, every write connection overwrites what it deletes.
                if (PendingClear is not null) HistoryClearStep.SecureDelete(db);
                clearConnectionOpened?.Invoke(db);
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private void Configure(SqliteConnection db)
    {
        GuardJournal(db);
        long pageSize = Convert.ToInt64(Scalar(db, "PRAGMA page_size"), CultureInfo.InvariantCulture);
        Execute(db, "PRAGMA max_page_count=" + (maximumDatabaseBytes / pageSize).ToString(CultureInfo.InvariantCulture));
        Execute(db, "PRAGMA journal_size_limit=8388608; PRAGMA wal_autocheckpoint=256; PRAGMA synchronous=FULL;");
    }
    /// <summary>The 8 MiB journal bound: a <c>-wal</c> still over it after a TRUNCATE checkpoint stops the write.</summary>
    private void GuardJournal(SqliteConnection db)
    {
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > MaximumJournalBytes)
        {
            Checkpoint(db);
            if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > MaximumJournalBytes)
                throw new IOException("The alert log journal is waiting for a reader; recording has stopped to bound disk use.");
        }
    }
    private static void Checkpoint(SqliteConnection db)
    {
        try { Execute(db, "PRAGMA wal_checkpoint(TRUNCATE)"); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 1; command.Transaction = transaction;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] args)
    { using var command = Command(db, sql, transaction, args); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var command = Command(db, sql); return command.ExecuteScalar(); }
    private static object? ScalarWith(SqliteConnection db, string sql, (string Name, object Value)[] args)
    { using var command = Command(db, sql, null, args); return command.ExecuteScalar(); }
}
