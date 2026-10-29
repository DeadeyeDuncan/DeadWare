using System.Net;

namespace DeadWare.Core;

/// <summary>Bounded observations, not socket lifetimes. All snapshots and breaks share one gate.</summary>
public sealed class ConnectionLedger
{
    private sealed class Counter(DateTimeOffset timestamp)
    {
        public DateTimeOffset First = timestamp, Last = timestamp;
        public long Down, Up, PreviousDown, PreviousUp, Events;
        public void Add(DateTimeOffset timestamp, int bytes, bool send)
        {
            if (timestamp < First) First = timestamp;
            if (timestamp > Last) Last = timestamp;
            if (send) Up = Sum(Up, bytes); else Down = Sum(Down, bytes);
            Events = Sum(Events, 1);
        }
    }

    private readonly object gate = new();
    private readonly int maximumPeers, maximumDeltaKeys;
    private readonly Dictionary<ConnectionPeer, Counter> peers = new();
    private readonly Dictionary<(DateTimeOffset Minute, ConnectionPeer Peer), Counter> pending = new();
    private DateTimeOffset? lastTimestamp;
    private double? lastMonotonic;
    private string segment = Guid.NewGuid().ToString("N");
    private long sequence, droppedEvents, lostEvents, unattributedEvents;
    private bool incomplete;

    public ConnectionLedger(int maximumPeers = 8192, int maximumDeltaKeys = 32768)
    {
        if (maximumPeers <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPeers));
        if (maximumDeltaKeys <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDeltaKeys));
        this.maximumPeers = maximumPeers;
        this.maximumDeltaKeys = maximumDeltaKeys;
    }

    public void Record(ConnectionPeer peer, DateTimeOffset timestamp, int bytes, bool send)
    {
        lock (gate)
        {
            // The generous per-observation size cap permits offloaded network buffers,
            // while rejecting corrupt signed/implausibly large ETW sizes.
            if (peer?.Process is null || peer.Process.Pid < 0)
            {
                unattributedEvents = Sum(unattributedEvents, 1);
                Drop(1);
                return;
            }
            if (bytes is < 0 or > 16 * 1024 * 1024 || peer.LocalPort is < 0 or > 65535 ||
                peer.RemotePort is < 0 or > 65535 || !Enum.IsDefined(peer.Protocol) ||
                peer.Process.Generation < 0 || string.IsNullOrEmpty(peer.Process.Name) || peer.Process.Name.Length > 260 ||
                peer.LocalAddress is null || peer.LocalAddress.Length > 64 ||
                peer.RemoteAddress is null || peer.RemoteAddress.Length > 64 ||
                !IPAddress.TryParse(peer.LocalAddress, out var local) ||
                !IPAddress.TryParse(peer.RemoteAddress, out var remote) ||
                (lastTimestamp.HasValue && Math.Abs((timestamp - lastTimestamp.Value).TotalSeconds) > 60))
            {
                Drop(1);
                return;
            }
            timestamp = timestamp.ToUniversalTime();
            peer = peer with { LocalAddress = NetworkAddress.Normalize(local).ToString(), RemoteAddress = NetworkAddress.Normalize(remote).ToString() };
            var minute = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0, TimeSpan.Zero);
            var key = (minute, peer);
            bool knownPeer = peers.TryGetValue(peer, out var live);
            bool knownDelta = pending.TryGetValue(key, out var delta);
            if ((!knownPeer && peers.Count >= maximumPeers) || (!knownDelta && pending.Count >= maximumDeltaKeys))
            {
                Drop(1);
                return;
            }
            if (!knownPeer) peers[peer] = live = new(timestamp);
            if (!knownDelta) pending[key] = delta = new(timestamp);
            live!.Add(timestamp, bytes, send);
            delta!.Add(timestamp, bytes, send);
        }
    }

    public ConnectionSample Take(DateTimeOffset timestamp, double monotonicSeconds)
    {
        lock (gate)
        {
            timestamp = timestamp.ToUniversalTime();
            double elapsed = lastMonotonic.HasValue ? monotonicSeconds - lastMonotonic.Value : double.NaN;
            double wallElapsed = lastTimestamp.HasValue ? (timestamp - lastTimestamp.Value).TotalSeconds : double.NaN;
            bool available = double.IsFinite(monotonicSeconds);
            if (!available || (lastMonotonic.HasValue &&
                (!double.IsFinite(elapsed) || elapsed <= 0 || elapsed > 60 || wallElapsed <= 0 || Math.Abs(wallElapsed - elapsed) > 2)))
                BreakCore(0);

            // Individual observations are aggregated to bound memory. A corrupt future
            // timestamp invalidates that peer's pending aggregates; report every discarded event.
            var unsafePeers = pending.Where(p => (p.Value.Last - timestamp).TotalSeconds > 2 ||
                (timestamp - p.Value.First).TotalSeconds > 60).Select(p => p.Key.Peer).ToHashSet();
            foreach (var key in pending.Keys.Where(k => unsafePeers.Contains(k.Peer)).ToArray())
            {
                Drop(pending[key].Events);
                pending.Remove(key);
            }
            foreach (var peer in unsafePeers) peers.Remove(peer);
            foreach (var peer in peers.Where(p => (timestamp - p.Value.Last).TotalSeconds >= 60).Select(p => p.Key).ToArray())
                peers.Remove(peer);

            bool hasInterval = lastTimestamp.HasValue && lastMonotonic.HasValue;
            var live = peers.Select(p => new ConnectionTraffic(p.Key, p.Value.First, p.Value.Last, p.Value.Down, p.Value.Up,
                hasInterval ? Math.Min(double.MaxValue, (p.Value.Down - p.Value.PreviousDown) / elapsed) : 0,
                hasInterval ? Math.Min(double.MaxValue, (p.Value.Up - p.Value.PreviousUp) / elapsed) : 0)).ToArray();
            var deltas = pending.Select(p => new ConnectionDelta(p.Key.Minute, p.Key.Peer, p.Value.Down, p.Value.Up, p.Value.First, p.Value.Last)).ToArray();
            var sample = new ConnectionSample(timestamp, monotonicSeconds, sequence = Sum(sequence, 1), segment,
                Array.AsReadOnly(live), Array.AsReadOnly(deltas), hasInterval ? lastTimestamp : null,
                available, !incomplete, droppedEvents, lostEvents, unattributedEvents,
                !available ? "Collection clock unavailable" : incomplete ? "Observation gap; some events were lost or dropped" : "Observed network peers");
            foreach (var counter in peers.Values)
            {
                counter.PreviousDown = counter.Down;
                counter.PreviousUp = counter.Up;
            }
            pending.Clear();
            lastTimestamp = available ? timestamp : null;
            lastMonotonic = available ? monotonicSeconds : null;
            incomplete = false;
            return sample;
        }
    }

    public void Break(long lostEvents = 0)
    {
        lock (gate) BreakCore(lostEvents);
    }

    private void BreakCore(long loss)
    {
        foreach (var counter in pending.Values) Drop(counter.Events);
        pending.Clear();
        peers.Clear();
        lastTimestamp = null;
        lastMonotonic = null;
        segment = Guid.NewGuid().ToString("N");
        lostEvents = Sum(lostEvents, Math.Max(0, loss));
        incomplete = true;
    }

    private void Drop(long count) { droppedEvents = Sum(droppedEvents, count); incomplete = true; }
    private static long Sum(long value, long increment) => value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
