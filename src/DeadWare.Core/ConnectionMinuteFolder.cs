using System.Globalization;

namespace DeadWare.Core;

/// <summary>Looks up the location cache. A hit with a null location is a cached null.</summary>
public delegate bool ConnectionLocationLookup(string address, out ConnectionLocation? location);

/// <summary>One frozen, immutable minute of one segment and recording generation, sent whole under one BatchId.</summary>
public sealed record ConnectionMinuteUnit(string BatchId, string Segment, long Generation, DateTimeOffset Minute,
    IReadOnlyList<ConnectionHistoryRecord> Records, IReadOnlyList<ConnectionCoverage> Coverage, long Observations)
{
    public int Size => Records.Count + Coverage.Count;
}

/// <summary>The units one write attempt sends, and the units the clock check retired.</summary>
public sealed record ConnectionSendable(IReadOnlyList<ConnectionMinuteUnit> Send, IReadOnlyList<ConnectionMinuteUnit> Retired);

/// <summary>Merges accepted connection deltas and coverage into one row per app and destination per minute.
/// A pure policy object: no timers, threads or storage. The location cache is read only through the lookup.</summary>
public sealed class ConnectionMinuteFolder
{
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2), StallLimit = TimeSpan.FromSeconds(90),
        ClockHold = TimeSpan.FromSeconds(60), FutureGuard = TimeSpan.FromSeconds(2);
    public const int MaximumRecords = 32768, MaximumCoverage = 32768;

    private readonly record struct UnitKey(string Segment, long Generation, long MinuteTicks);
    private readonly record struct EntryKey(int Pid, long ProcessGeneration, string Name, string RemoteAddress, int RemotePort,
        ConnectionProtocol Protocol, GeoPoint? Origin);

    private sealed class Entry(ConnectionDelta first, GeoPoint? origin)
    {
        public readonly ConnectionPeer Peer = first.Peer;
        public readonly GeoPoint? Origin = origin;
        public long Received, Sent, Observations;
        public DateTimeOffset FirstSeen = first.FirstSeen, LastSeen = first.LastSeen;
        public bool Complete = true;
        public ConnectionLocation? LastLocation;
    }

    private sealed class OpenUnit(UnitKey key)
    {
        public readonly UnitKey Key = key;
        public readonly Dictionary<EntryKey, Entry> Entries = new();
        public readonly List<Entry> Order = [];
        public readonly List<(DateTimeOffset From, DateTimeOffset To, bool Complete)> Runs = [];
        public DateTimeOffset Minute => new(Key.MinuteTicks, TimeSpan.Zero);
    }

    private readonly ConnectionLocationLookup lookup;
    private readonly int pressureThreshold;
    private readonly Dictionary<UnitKey, OpenUnit> open = new();
    private readonly List<ConnectionMinuteUnit> waiting = [];
    private long serial;

    public ConnectionMinuteFolder(ConnectionLocationLookup lookup, int pressureThreshold)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentOutOfRangeException.ThrowIfLessThan(pressureThreshold, 1);
        this.lookup = lookup; this.pressureThreshold = pressureThreshold;
    }

    public static string BatchId(string segment, DateTimeOffset minute, long generation, long serial)
        => string.Create(CultureInfo.InvariantCulture, $"{segment}:m{minute.UtcTicks}:{generation}:{serial}");

    /// <summary>Open entries plus open coverage runs.</summary>
    public int Held { get; private set; }

    /// <summary>The sum of Size over frozen units not yet sent or retired.</summary>
    public int Waiting { get; private set; }

    /// <summary>Merges one publication's accepted deltas and coverage, then applies R1, R2 and R5. Returns the units frozen.</summary>
    public int Merge(ConnectionSample sample, long generation, GeoPoint? origin, bool complete, IReadOnlyList<ConnectionDelta> accepted)
    {
        ArgumentNullException.ThrowIfNull(sample); ArgumentNullException.ThrowIfNull(accepted);
        foreach (var delta in accepted)
        {
            var unit = Unit(new(sample.Segment, generation, delta.Minute.UtcTicks));
            var peer = delta.Peer;
            var key = new EntryKey(peer.Process.Pid, peer.Process.Generation, peer.Process.Name, peer.RemoteAddress, peer.RemotePort, peer.Protocol, origin);
            if (!unit.Entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(delta, origin);
                unit.Entries.Add(key, entry); unit.Order.Add(entry); Held++;
            }
            entry.Received = Add(entry.Received, delta.ReceivedBytes);
            entry.Sent = Add(entry.Sent, delta.SentBytes);
            if (delta.FirstSeen < entry.FirstSeen) entry.FirstSeen = delta.FirstSeen;
            if (delta.LastSeen > entry.LastSeen) entry.LastSeen = delta.LastSeen;
            entry.Complete &= complete;
            entry.Observations++;
            if (lookup(peer.RemoteAddress, out var location)) entry.LastLocation = location;
        }
        // Coverage pieces as the service computes them: CoverageFrom before Timestamp, at most 60 s, split at UTC minutes.
        if (sample.CoverageFrom is { } coverageFrom && coverageFrom < sample.Timestamp && (sample.Timestamp - coverageFrom).TotalSeconds <= 60)
        {
            var from = coverageFrom.ToUniversalTime(); var to = sample.Timestamp.ToUniversalTime();
            while (from < to)
            {
                long minute = from.UtcTicks - from.UtcTicks % TimeSpan.TicksPerMinute;
                var boundary = new DateTimeOffset(minute, TimeSpan.Zero).AddMinutes(1);
                var end = boundary < to ? boundary : to;
                var runs = Unit(new(sample.Segment, generation, minute)).Runs;
                if (runs.Count > 0 && runs[^1].To == from && runs[^1].Complete == complete) runs[^1] = (runs[^1].From, end, complete);
                else { runs.Add((from, end, complete)); Held++; }
                from = end;
            }
        }
        int frozen = 0;
        if (sample.CoverageFrom is { } closeFrom) // R1: closed by capture.
            frozen += Freeze(u => u.Key.Generation == generation && string.Equals(u.Key.Segment, sample.Segment, StringComparison.Ordinal)
                && u.Key.MinuteTicks <= closeFrom.UtcTicks - (TimeSpan.FromMinutes(1) + CloseGrace).Ticks);
        // R2: the segment or generation ended.
        frozen += Freeze(u => u.Key.Generation != generation || !string.Equals(u.Key.Segment, sample.Segment, StringComparison.Ordinal));
        if (Held >= pressureThreshold) frozen += FreezeAll(); // R5: pressure.
        return frozen;
    }

    /// <summary>R3 (recording generation changed), then R4 (capture stalled).</summary>
    public int Tick(DateTimeOffset now, long currentGeneration)
        => Freeze(u => u.Key.Generation != currentGeneration) + Freeze(u => u.Key.MinuteTicks <= now.UtcTicks - StallLimit.Ticks);

    /// <summary>R6: freezes every open unit.</summary>
    public int FreezeAll() => Freeze(_ => true);

    /// <summary>Walks waiting units in (Minute, serial) order: sends whole units within the limits, holds units up to
    /// ClockHold ahead of the clock (unless final) and retires the rest.</summary>
    public ConnectionSendable Sendable(DateTimeOffset now, bool final, int maximumRecords = MaximumRecords, int maximumCoverage = MaximumCoverage)
    {
        var send = new List<ConnectionMinuteUnit>(); var retired = new List<ConnectionMinuteUnit>();
        int records = 0, coverage = 0;
        bool full = false; // After the first sendable unit that does not fit, take no more sendables but keep retiring.
        for (int i = 0; i < waiting.Count; i++)
        {
            var unit = waiting[i];
            long excess = Latest(unit) - now.UtcTicks - FutureGuard.Ticks;
            if (excess <= 0)
            {
                if (full) continue;
                if (send.Count > 0 && (records + unit.Records.Count > maximumRecords || coverage + unit.Coverage.Count > maximumCoverage)) { full = true; continue; }
                send.Add(unit); records += unit.Records.Count; coverage += unit.Coverage.Count;
            }
            else if (excess <= ClockHold.Ticks && !final) continue;
            else { retired.Add(unit); waiting.RemoveAt(i--); Waiting -= unit.Size; }
        }
        return new(send.AsReadOnly(), retired.AsReadOnly());
    }

    /// <summary>Removes the listed units from waiting.</summary>
    public void Sent(IReadOnlyList<ConnectionMinuteUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var sent = new HashSet<ConnectionMinuteUnit>(units, ReferenceEqualityComparer.Instance);
        foreach (var unit in waiting) if (sent.Contains(unit)) Waiting -= unit.Size;
        waiting.RemoveAll(sent.Contains);
    }

    /// <summary>Removes and returns every waiting unit.</summary>
    public IReadOnlyList<ConnectionMinuteUnit> DropWaiting()
    {
        var dropped = waiting.ToArray();
        waiting.Clear(); Waiting = 0;
        return Array.AsReadOnly(dropped);
    }

    private OpenUnit Unit(UnitKey key)
    {
        if (!open.TryGetValue(key, out var unit)) open.Add(key, unit = new OpenUnit(key));
        return unit;
    }

    private int Freeze(Func<OpenUnit, bool> predicate)
    {
        var closing = open.Values.Where(predicate).OrderBy(u => u.Key.MinuteTicks)
            .ThenBy(u => u.Key.Segment, StringComparer.Ordinal).ThenBy(u => u.Key.Generation).ToArray();
        foreach (var unit in closing)
        {
            open.Remove(unit.Key);
            Held -= unit.Order.Count + unit.Runs.Count;
            var frozen = Frozen(unit);
            // Kept in (Minute, serial) order; serials only grow, so insert after every unit of an equal or earlier minute.
            int index = waiting.FindLastIndex(w => w.Minute.UtcTicks <= unit.Key.MinuteTicks) + 1;
            waiting.Insert(index, frozen); Waiting += frozen.Size;
        }
        return closing.Length;
    }

    private ConnectionMinuteUnit Frozen(OpenUnit unit)
    {
        var minute = unit.Minute;
        string batch = BatchId(unit.Key.Segment, minute, unit.Key.Generation, serial++);
        var records = unit.Order.Select(entry =>
        {
            var location = lookup(entry.Peer.RemoteAddress, out var cached) ? cached : entry.LastLocation;
            var delta = new ConnectionDelta(minute, entry.Peer, entry.Received, entry.Sent, entry.FirstSeen, entry.LastSeen);
            return new ConnectionHistoryRecord(batch, unit.Key.Segment, delta, location, entry.Origin, entry.Complete);
        }).ToArray();
        var coverage = unit.Runs.Select(run => new ConnectionCoverage(batch, unit.Key.Segment, run.From, run.To, run.Complete)).ToArray();
        return new(batch, unit.Key.Segment, unit.Key.Generation, minute, Array.AsReadOnly(records), Array.AsReadOnly(coverage),
            unit.Order.Sum(entry => entry.Observations));
    }

    private static long Latest(ConnectionMinuteUnit unit)
        => unit.Records.Select(r => r.Delta.LastSeen.UtcTicks).Concat(unit.Coverage.Select(c => c.To.UtcTicks)).Max();

    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
