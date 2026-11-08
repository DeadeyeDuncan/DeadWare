using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DeadWare.Core;

/// <summary>A request to clear history older than <see cref="Before"/>, for one application or for all.</summary>
public sealed record HistoryClear(string? AppKey, DateTimeOffset Before, DateTimeOffset Requested)
{
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

/// <summary>Where one store's clear stands after a step.</summary>
/// <param name="Done">Work done so far, in the store's own unit.</param>
/// <param name="Total">The work taken at the first step, or null when the store shows no percent.</param>
/// <param name="Finished">True once the last transaction removed the store's marker.</param>
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
/// <param name="Finished">True when the call found nothing left and its transaction removed the marker.</param>
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
/// round and commit nothing. The call that finds nothing left removes the marker in its transaction and reports Finished;</description></item>
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
    /// committed transaction, it throws <see cref="IOException"/>. Every committed transaction is followed by
    /// <paramref name="committed"/> and then <c>PRAGMA wal_checkpoint(TRUNCATE)</c>.
    /// </summary>
    /// <returns>True when the clear finished (the delegate reported Finished); false when the budget passed or the step yielded.</returns>
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
            throw new IOException("A history clear waited 60 seconds for a reader to release the journal.");
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
