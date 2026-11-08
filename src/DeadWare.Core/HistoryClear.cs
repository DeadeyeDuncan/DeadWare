using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

/// <summary>A request to clear history older than <see cref="Before"/>, for one application or for all.</summary>
public sealed record HistoryClear(string? AppKey, DateTimeOffset Before, DateTimeOffset Requested)
{
    /// <summary>One clear step runs delete transactions until this has passed; the boundary rule lasts at most <see cref="BoundaryLimit"/>.</summary>
    public static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(250), BoundaryLimit = TimeSpan.FromSeconds(120);

    public static HistoryClear Create(string? application, bool allTime, DateTimeOffset requested)
    {
        var floor = UsageMetrics.Floor(requested);
        return new(application is null ? null : Core.AppKey.Of(application), allTime ? floor.AddMinutes(1) : floor.AddDays(-7), requested);
    }

    public bool AllTime => Before > Requested;
    public DateTimeOffset AlertCutoff => Before < Requested ? Before : Requested;

    public bool Covers(string? name, DateTimeOffset minute)
        => minute < Before && (AppKey is null || string.Equals(Core.AppKey.Of(name), AppKey, StringComparison.Ordinal));
}

/// <summary>Where a service's clear stands.</summary>
public enum HistoryClearPhase { None, Queued, Clearing, Done, Failed, Unavailable }

/// <summary>The four stores a clear reaches, in the progress block's order.</summary>
public enum HistoryClearStore { Connections, Usage, ProcessHistory, Alerts }

/// <summary>A service's clear state.</summary>
/// <param name="Percent">The store's percent while clearing, or null when it shows none.</param>
/// <param name="Error">The failure's exception type name, or null.</param>
/// <param name="MarkerSaved">True once the store's durable marker is saved, until the clear is done.</param>
/// <param name="Resumed">True for a clear resumed from a marker found at startup.</param>
public sealed record HistoryClearState(HistoryClearPhase Phase, int? Percent, string? Error, bool MarkerSaved, bool Resumed)
{
    public static HistoryClearState None { get; } = new(HistoryClearPhase.None, null, null, false, false);
}

/// <summary>Fixed copy for clears. Status text holds counts and exception type names only.</summary>
public static class HistoryClearCopy
{
    /// <summary>The suffix each service's status gains: while clearing, after a failure with or without the marker, or "" otherwise.</summary>
    /// <param name="alertLog">The alert log shows no percent.</param>
    public static string StatusSuffix(HistoryClearState state, bool alertLog)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Phase switch
        {
            HistoryClearPhase.Clearing when alertLog || state.Percent is null => " · Clearing history.",
            HistoryClearPhase.Clearing => string.Create(CultureInfo.InvariantCulture, $" · Clearing history ({state.Percent} %)."),
            HistoryClearPhase.Failed when state.MarkerSaved => $" · Clearing history failed ({state.Error}); it resumes when DeadWare next starts.",
            HistoryClearPhase.Failed => $" · Clearing history failed ({state.Error}); not cleared.",
            _ => "",
        };
    }
}

/// <summary>Where one store's clear stands after a step.</summary>
/// <param name="Done">Work done so far, in the store's own unit.</param>
/// <param name="Total">The work taken at the first step, or null when the store shows no percent.</param>
/// <param name="Finished">True once the store's marker is removed: by the last transaction (connections.db, alerts.db), or by deleting
/// the sidecar file right after the last delete commits (usage-history.db, process-history.db).</param>
public sealed record HistoryClearProgress(long Done, long? Total, bool Finished)
{
    /// <summary>Null without a total; 100 when finished; otherwise ⌊100 × Done ÷ Total⌋ held between 0 and 99 (0 for a zero total).</summary>
    public int? Percent
    {
        get
        {
            if (Total is not { } total) return null;
            if (Finished) return 100;
            if (total <= 0) return 0;
            Int128 percent = (Int128)Done * 100 / total;
            return percent < 0 ? 0 : percent > 99 ? 99 : (int)percent;
        }
    }
}

/// <summary>What connections.db keeps of a clear request: the app key (null for all apps) and <c>Before</c>.</summary>
public sealed record PendingConnectionClear(string? AppKey, DateTimeOffset Before);

/// <summary>The outcome of one call of a store's per-transaction delegate, as <see cref="HistoryClearStep.Run"/> reads it.</summary>
/// <param name="Committed">True when the call committed a delete transaction (the marker's removal included); false for a read-only call.</param>
/// <param name="Rows">Rows the committed transaction deleted, passed to the store's <c>clearCommitted</c> seam.</param>
/// <param name="Finished">True when the call found nothing left and removed the marker. It may arrive with <see cref="Committed"/> false:
/// a sidecar store finishes by deleting its marker file, without a transaction.</param>
public readonly record struct HistoryClearTransaction(bool Committed, int Rows, bool Finished);

/// <summary>
/// The clear-step machinery the four history stores share: the step budget loop, the yield to readers and the TRUNCATE
/// checkpoint after each delete transaction, plus <see cref="SecureDelete"/> for write connections while a marker exists.
/// </summary>
/// <remarks>
/// <para>A store keeps one instance per clear (create it when the marker is written or read at startup, drop it when the
/// clear finishes), because the instance remembers since when the store has been yielding. Each store step opens its own
/// write connection, takes the store's own lock, and calls <see cref="Run"/> with:</para>
/// <list type="bullet">
/// <item><description><c>clock</c>: the store's <c>clearClock</c> seam (a monotonic elapsed time; <see cref="MonotonicClock"/> by default);</description></item>
/// <item><description><c>guardJournal</c>: the store's existing 8 MiB journal guard, run before each transaction;</description></item>
/// <item><description><c>transaction</c>: one delete transaction in the store's own SQL and transaction-size rule (its chunk
/// size and any halving under its budget), returning a <see cref="HistoryClearTransaction"/>; a call may also do a read-only
/// round and commit nothing. The call that finds nothing left removes the marker and reports Finished: in its transaction for a
/// marker kept in the database, or by deleting the sidecar file (Finished with Committed false);</description></item>
/// <item><description><c>committed</c>: the store's <c>clearCommitted</c> seam, invoked with the deleted row count after each
/// commit and before its TRUNCATE checkpoint, so a reader of the <c>-wal</c> there sees the transaction's frames.</description></item>
/// </list>
/// <para>The store builds its own <see cref="HistoryClearProgress"/> from the result. Tasks for usage-history.db,
/// process-history.db and alerts.db call it the same way; only the delegate and the marker's home differ.</para>
/// </remarks>
public sealed class HistoryClearStep
{
    /// <summary>A step ends early while the <c>-wal</c> stays above this after a TRUNCATE checkpoint attempt.</summary>
    public const long YieldJournalBytes = 4L * 1024 * 1024;
    /// <summary>Yielding this long without a committed transaction fails the step with <see cref="IOException"/>.</summary>
    public static readonly TimeSpan YieldLimit = TimeSpan.FromSeconds(60);
    private static readonly long Origin = Stopwatch.GetTimestamp();
    /// <summary>The default <c>clearClock</c>: monotonic time elapsed since the process loaded this type.</summary>
    public static TimeSpan MonotonicClock() => Stopwatch.GetElapsedTime(Origin);

    private readonly string journal;
    private TimeSpan? yieldingSince;

    /// <param name="databasePath">The store's database file; its <c>-wal</c> is checked before each transaction.</param>
    public HistoryClearStep(string databasePath) => journal = Path.GetFullPath(databasePath) + "-wal";

    /// <summary>
    /// Runs delete transactions on <paramref name="db"/> until <paramref name="budget"/> has passed on <paramref name="clock"/>,
    /// always at least one call of <paramref name="transaction"/> unless the step yields. Before each call it runs
    /// <paramref name="guardJournal"/>, then the yield check: when the <c>-wal</c> is over <see cref="YieldJournalBytes"/> after a
    /// TRUNCATE checkpoint, the step returns early without failing; once it has yielded for <see cref="YieldLimit"/> without a
    /// committed transaction, it throws <see cref="IOException"/> and starts a new yield window for a later call. Every committed transaction is followed by
    /// <paramref name="committed"/> and then <c>PRAGMA wal_checkpoint(TRUNCATE)</c>.
    /// </summary>
    /// <returns>True when the clear finished (the delegate reported Finished, with or without a committed transaction); false when the
    /// budget passed or the step yielded.</returns>
    public bool Run(SqliteConnection db, TimeSpan budget, Func<TimeSpan> clock, Action guardJournal, Func<HistoryClearTransaction> transaction, Action<int>? committed)
    {
        ArgumentNullException.ThrowIfNull(db); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(guardJournal); ArgumentNullException.ThrowIfNull(transaction);
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);
        var start = clock();
        do
        {
            guardJournal();
            if (Yield(db, clock)) return false;
            var result = transaction();
            if (result.Committed)
            {
                yieldingSince = null;
                committed?.Invoke(result.Rows);
                Checkpoint(db);
            }
            if (result.Finished) return true;
        }
        while (clock() - start < budget);
        return false;
    }

    private bool Yield(SqliteConnection db, Func<TimeSpan> clock)
    {
        if (JournalBytes() <= YieldJournalBytes) return false;
        Checkpoint(db);
        if (JournalBytes() <= YieldJournalBytes) return false;
        var now = clock();
        yieldingSince ??= now;
        if (now - yieldingSince.Value >= YieldLimit)
        {
            // A later Try again on this instance starts a new yield window instead of failing at its first check.
            yieldingSince = null;
            throw new IOException("A history clear waited 60 seconds for a reader to release the journal.");
        }
        return true;
    }

    // FileInfo.Length reads the size of a file SQLite holds open.
    private long JournalBytes() => File.Exists(journal) ? new FileInfo(journal).Length : 0;

    /// <summary><c>PRAGMA wal_checkpoint(TRUNCATE)</c>; a busy or locked database (a reader holding the journal) is not an error.</summary>
    public static void Checkpoint(SqliteConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        try { using var command = db.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { }
    }

    /// <summary>Turns on <c>PRAGMA secure_delete</c> for a write connection opened while the store holds a clear marker, and checks it took.</summary>
    public static void SecureDelete(SqliteConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON"; command.ExecuteNonQuery();
        command.CommandText = "PRAGMA secure_delete";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new IOException("History storage could not turn on secure deletion.");
    }
}

/// <summary>
/// The sidecar clear marker of usage-history.db and process-history.db:
/// <c>{"Version":1,"AppKey":&lt;string or null&gt;,"Before":&lt;ISO 8601 UTC&gt;,"Requested":&lt;ISO 8601 UTC&gt;}</c>.
/// </summary>
public static class HistoryClearMarker
{
    /// <summary>Writes the marker to <c>path + ".tmp"</c>, flushes it to disk and moves it over <paramref name="path"/>. On failure the <c>.tmp</c> is removed and the error passes through.</summary>
    public static void Write(string path, HistoryClear clear)
    {
        ArgumentException.ThrowIfNullOrEmpty(path); ArgumentNullException.ThrowIfNull(clear);
        if (clear.AppKey is { Length: 0 }) throw new ArgumentException("A history clear names an application key, or none for all apps.", nameof(clear));
        SidecarJson.Write(path, writer =>
        {
            writer.WriteNumber("Version", 1);
            if (clear.AppKey is null) writer.WriteNull("AppKey"); else writer.WriteString("AppKey", clear.AppKey);
            writer.WriteString("Before", SidecarJson.Stamp(clear.Before));
            writer.WriteString("Requested", SidecarJson.Stamp(clear.Requested));
        });
    }

    /// <summary>
    /// The marker's request, or null when the file is absent. Bad JSON, another <c>Version</c> or an invalid field throws
    /// <see cref="InvalidDataException"/>; an <see cref="IOException"/> (or access denied) passes through.
    /// </summary>
    public static HistoryClear? Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (!SidecarJson.IsVersion1(root))
                throw new InvalidDataException("The history clear marker has an unknown version.");
            if (!root.TryGetProperty("AppKey", out var key) || key.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                key.ValueKind == JsonValueKind.String && key.GetString() is not { Length: > 0 })
                throw new InvalidDataException("The history clear marker has an invalid application key.");
            if (!root.TryGetProperty("Before", out var before) || SidecarJson.Time(before) is not { } beforeTime ||
                !root.TryGetProperty("Requested", out var requested) || SidecarJson.Time(requested) is not { } requestedTime)
                throw new InvalidDataException("The history clear marker has an invalid time.");
            return new(key.ValueKind == JsonValueKind.Null ? null : key.GetString(), beforeTime, requestedTime);
        }
        catch (JsonException error) { throw new InvalidDataException("The history clear marker is not valid JSON.", error); }
    }

    /// <summary>Deletes a leftover <c>path + ".tmp"</c> from an interrupted write.</summary>
    public static void DeleteLeftover(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.Delete(path + ".tmp");
    }
}

/// <summary>
/// usage-history.db's cleared marks, kept after a clear for the traffic strip (times only):
/// <c>{"Version":1,"AllAppsBefore":&lt;ISO 8601 UTC or null&gt;,"OneAppBefore":&lt;ISO 8601 UTC or null&gt;}</c>, each the latest <c>Before</c> of that kind.
/// </summary>
public sealed record UsageClearedMarks(DateTimeOffset? AllAppsBefore, DateTimeOffset? OneAppBefore)
{
    public static UsageClearedMarks None { get; } = new(null, null);

    /// <summary>The marks in <paramref name="path"/>; a missing, invalid or unreadable file reads as <see cref="None"/>.</summary>
    public static UsageClearedMarks Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            if (!SidecarJson.IsVersion1(root) || !root.TryGetProperty("AllAppsBefore", out var all) || !root.TryGetProperty("OneAppBefore", out var one))
                return None;
            DateTimeOffset? allTime = null, oneTime = null;
            if (all.ValueKind != JsonValueKind.Null && (allTime = SidecarJson.Time(all)) is null) return None;
            if (one.ValueKind != JsonValueKind.Null && (oneTime = SidecarJson.Time(one)) is null) return None;
            return new(allTime, oneTime);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return None; }
    }

    /// <summary>The marks with <paramref name="clear"/>'s <c>Before</c> taken into its kind: the larger of the old and the new.</summary>
    public UsageClearedMarks With(HistoryClear clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        var before = clear.Before.ToUniversalTime();
        static DateTimeOffset Max(DateTimeOffset? old, DateTimeOffset value) => old is { } kept && kept > value ? kept : value;
        return clear.AppKey is null ? this with { AllAppsBefore = Max(AllAppsBefore, before) } : this with { OneAppBefore = Max(OneAppBefore, before) };
    }

    /// <summary>Writes the marks as the clear marker is written: <c>.tmp</c>, <c>Flush(true)</c>, then a move over <paramref name="path"/>.</summary>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        SidecarJson.Write(path, writer =>
        {
            writer.WriteNumber("Version", 1);
            if (AllAppsBefore is { } all) writer.WriteString("AllAppsBefore", SidecarJson.Stamp(all)); else writer.WriteNull("AllAppsBefore");
            if (OneAppBefore is { } one) writer.WriteString("OneAppBefore", SidecarJson.Stamp(one)); else writer.WriteNull("OneAppBefore");
        });
    }
}

/// <summary>The sidecar files' shared JSON handling: version 1, UTC ISO 8601 times and the durable write.</summary>
internal static class SidecarJson
{
    public static bool IsVersion1(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number &&
           version.TryGetInt32(out int number) && number == 1;

    public static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>An ISO 8601 time as UTC, or null for anything else.</summary>
    public static DateTimeOffset? Time(JsonElement element)
        => element.ValueKind == JsonValueKind.String && element.TryGetDateTimeOffset(out var time) ? time.ToUniversalTime() : null;

    /// <summary>Writes one JSON object to <c>path + ".tmp"</c>, flushes it to disk and moves it over <paramref name="path"/>.</summary>
    public static void Write(string path, Action<Utf8JsonWriter> properties)
    {
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); properties(writer); writer.WriteEndObject();
                }
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}

/// <summary>
/// The in-memory side of a sidecar clear marker (<see cref="HistoryClearMarker"/>), shared by usage-history.db and process-history.db:
/// the pending request, an unreadable or discarded marker, and the store's per-clear <typeparamref name="TState"/>, all under one lock.
/// </summary>
internal sealed class SidecarClear<TState>(string markerPath, Func<TState> newState) where TState : class
{
    private readonly object gate = new();
    private HistoryClear? pending;
    private bool unreadable, discarded;
    private TState? state;

    /// <summary>A clear is in progress: from the marker's write until <see cref="Finish"/>, and from <see cref="ReadAtStartup"/> while a
    /// marker exists, including one that cannot be read.</summary>
    public bool Pending { get { lock (gate) return pending is not null || unreadable; } }
    /// <summary>The clear in progress; null without one or while the marker cannot be read.</summary>
    public HistoryClear? Request { get { lock (gate) return pending; } }
    /// <summary><see cref="ReadAtStartup"/> found a marker with invalid content and deleted it.</summary>
    public bool Discarded { get { lock (gate) return discarded; } }

    /// <summary>Deletes a leftover <c>.tmp</c> and reads the marker, before the store's first write connection opens. A valid marker
    /// becomes the pending request; invalid content is deleted (<see cref="Discarded"/>), and one that cannot even be deleted, or cannot
    /// be read (IOException or access denied), is kept as unreadable.</summary>
    public void ReadAtStartup()
    {
        try { HistoryClearMarker.DeleteLeftover(markerPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        HistoryClear? marker = null; bool isUnreadable = false, isDiscarded = false;
        try { marker = HistoryClearMarker.Read(markerPath); }
        catch (InvalidDataException)
        {
            try { File.Delete(markerPath); isDiscarded = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { isUnreadable = true; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { isUnreadable = true; }
        lock (gate)
        {
            pending = marker; unreadable = isUnreadable; discarded = isDiscarded;
            state = marker is null ? null : newState();
        }
    }

    /// <summary>Writes the marker for <paramref name="clear"/>. An equal pending request writes nothing; a different one, or any request
    /// while an unreadable marker exists, throws <see cref="InvalidOperationException"/> with <paramref name="busyMessage"/>.</summary>
    public void Begin(HistoryClear clear, string busyMessage)
    {
        lock (gate)
        {
            if (pending is not null || unreadable)
            {
                if (pending == clear) return;
                throw new InvalidOperationException(busyMessage);
            }
            HistoryClearMarker.Write(markerPath, clear);
            pending = clear; state = newState();
        }
    }

    /// <summary>The readable pending request and its state; throws <see cref="InvalidOperationException"/> with
    /// <paramref name="noneMessage"/> without one (Task 8's ClearStep contract).</summary>
    public (HistoryClear Request, TState State) Current(string noneMessage)
    {
        lock (gate)
        {
            if (pending is not { } request || state is not { } current) throw new InvalidOperationException(noneMessage);
            return (request, current);
        }
    }

    /// <summary>Deletes the marker file, then forgets the clear. A failed delete throws and keeps the clear pending.</summary>
    public void Finish()
    {
        File.Delete(markerPath);
        lock (gate) { pending = null; state = null; unreadable = false; }
    }
}
