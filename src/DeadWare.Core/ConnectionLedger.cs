using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;

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

    /// <summary>A peer with its value hash worked out once, so the per-event lookups hash three integers instead of three strings.
    /// Equality is still the peer's value equality; a canonical peer from the endpoint cache usually matches by reference.</summary>
    private readonly struct PeerKey(ConnectionPeer peer, int hash) : IEquatable<PeerKey>
    {
        public readonly ConnectionPeer Peer = peer;
        public readonly int Hash = hash;
        public PeerKey(ConnectionPeer peer) : this(peer, peer.GetHashCode()) { }
        public bool Equals(PeerKey other) => Hash == other.Hash && (ReferenceEquals(Peer, other.Peer) || Peer.Equals(other.Peer));
        public override bool Equals(object? obj) => obj is PeerKey other && Equals(other);
        public override int GetHashCode() => Hash;
    }

    /// <summary>
    /// One ETW observation's raw endpoint: the process identity object, then each side's address bytes, scope and port, local side
    /// first, and the protocol. IPv4-mapped IPv6 is folded to IPv4 here exactly as <see cref="NetworkAddress.Normalize"/> folds it, and an
    /// IPv6 address keeps its scope, which its text carries; so each key always yields the same canonical peer.
    /// </summary>
    private readonly struct EndpointKey : IEquatable<EndpointKey>
    {
        private readonly ProcessIdentity process;
        private readonly ulong localHigh, localLow, remoteHigh, remoteLow;
        private readonly uint localScope, remoteScope;
        private readonly int localPort, remotePort, flags;

        private EndpointKey(ProcessIdentity process, ulong localHigh, ulong localLow, uint localScope, int localPort, ulong remoteHigh, ulong remoteLow, uint remoteScope, int remotePort, int flags)
        {
            this.process = process; this.localHigh = localHigh; this.localLow = localLow; this.localScope = localScope; this.localPort = localPort;
            this.remoteHigh = remoteHigh; this.remoteLow = remoteLow; this.remoteScope = remoteScope; this.remotePort = remotePort; this.flags = flags;
        }

        public static bool TryCreate(ProcessIdentity process, IPAddress? local, int localPort, IPAddress? remote, int remotePort, ConnectionProtocol protocol, out EndpointKey key)
        {
            key = default;
            if (!TryAddress(local, out var lh, out var ll, out var ls, out bool l4) || !TryAddress(remote, out var rh, out var rl, out var rs, out bool r4)) return false;
            key = new(process, lh, ll, ls, localPort, rh, rl, rs, remotePort, (int)protocol << 2 | (l4 ? 1 : 0) | (r4 ? 2 : 0));
            return true;
        }

        private static bool TryAddress(IPAddress? address, out ulong high, out ulong low, out uint scope, out bool v4)
        {
            high = low = 0; scope = 0; v4 = false;
            if (address is null) return false;
            Span<byte> bytes = stackalloc byte[16];
            if (!address.TryWriteBytes(bytes, out int written)) return false;
            if (written == 4) { v4 = true; low = BinaryPrimitives.ReadUInt32BigEndian(bytes); return true; }
            if (written != 16) return false;
            if (address.IsIPv4MappedToIPv6) { v4 = true; low = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]); return true; }
            high = BinaryPrimitives.ReadUInt64BigEndian(bytes); low = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
            scope = unchecked((uint)address.ScopeId);
            return true;
        }

        public bool Equals(EndpointKey other) => ReferenceEquals(process, other.process) && localHigh == other.localHigh && localLow == other.localLow &&
            remoteHigh == other.remoteHigh && remoteLow == other.remoteLow && localScope == other.localScope && remoteScope == other.remoteScope &&
            localPort == other.localPort && remotePort == other.remotePort && flags == other.flags;
        public override bool Equals(object? obj) => obj is EndpointKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(process),
            HashCode.Combine(localHigh, localLow, localScope, localPort), HashCode.Combine(remoteHigh, remoteLow, remoteScope, remotePort), flags);
    }

    private readonly object gate = new();
    private readonly int maximumPeers, maximumDeltaKeys;
    private readonly Dictionary<PeerKey, Counter> peers = new();
    private readonly Dictionary<(DateTimeOffset Minute, PeerKey Peer), Counter> pending = new();
    // Canonical peers by raw endpoint, so an observation of a known peer builds no strings; entries are values only, never counters.
    private readonly Dictionary<EndpointKey, PeerKey> endpoints = new();
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
            if (peer?.Process is null || peer.Process.Pid < 0)
            {
                unattributedEvents = Sum(unattributedEvents, 1);
                Drop(1);
                return;
            }
            if (Canonical(peer) is not { } canonical || !Acceptable(timestamp, bytes))
            {
                Drop(1);
                return;
            }
            RecordCore(new PeerKey(canonical), timestamp, bytes, send);
        }
    }

    /// <summary>
    /// One ETW endpoint observation, equivalent to <c>Record(NetworkAddress.FromEndpoints(process, source, sourcePort, destination,
    /// destinationPort, protocol, send), timestamp, bytes, send)</c>; but a peer seen before is found by its raw address bytes, so its
    /// strings are built and checked once per peer instead of on every event.
    /// </summary>
    public void RecordEndpoint(ProcessIdentity process, IPAddress source, int sourcePort, IPAddress destination, int destinationPort,
        ConnectionProtocol protocol, bool send, DateTimeOffset timestamp, int bytes)
    {
        lock (gate)
        {
            var (local, localPort, remote, remotePort) = send ? (source, sourcePort, destination, destinationPort) : (destination, destinationPort, source, sourcePort);
            // An unattributed observation or an address the key cannot hold takes the general path, which has the same outcome.
            if (process is not { Pid: >= 0 } || !EndpointKey.TryCreate(process, local, localPort, remote, remotePort, protocol, out var endpoint))
            {
                Record(NetworkAddress.FromEndpoints(process!, source, sourcePort, destination, destinationPort, protocol, send), timestamp, bytes, send);
                return;
            }
            if (!endpoints.TryGetValue(endpoint, out var key))
            {
                if (Canonical(NetworkAddress.FromEndpoints(process, source, sourcePort, destination, destinationPort, protocol, send)) is not { } peer)
                {
                    Drop(1);
                    return;
                }
                key = new PeerKey(peer);
                // Bounded, like the peers: past it a new peer is still recorded, only not remembered.
                if (endpoints.Count < 2 * maximumPeers) endpoints[endpoint] = key;
            }
            if (!Acceptable(timestamp, bytes))
            {
                Drop(1);
                return;
            }
            RecordCore(key, timestamp, bytes, send);
        }
    }

    /// <summary>The peer as the ledger stores it: its addresses re-parsed and normalized (IPv4-mapped IPv6 as IPv4), or null when a
    /// field is out of range. Depends on the peer alone, so the endpoint cache may keep the result.</summary>
    private static ConnectionPeer? Canonical(ConnectionPeer peer)
    {
        if (peer.LocalPort is < 0 or > 65535 || peer.RemotePort is < 0 or > 65535 || !Enum.IsDefined(peer.Protocol) ||
            peer.Process.Generation < 0 || string.IsNullOrEmpty(peer.Process.Name) || peer.Process.Name.Length > 260 ||
            peer.LocalAddress is null || peer.LocalAddress.Length > 64 ||
            peer.RemoteAddress is null || peer.RemoteAddress.Length > 64 ||
            !IPAddress.TryParse(peer.LocalAddress, out var local) ||
            !IPAddress.TryParse(peer.RemoteAddress, out var remote))
            return null;
        return peer with { LocalAddress = NetworkAddress.Normalize(local).ToString(), RemoteAddress = NetworkAddress.Normalize(remote).ToString() };
    }

    /// <summary>The per-observation checks. The generous size cap permits offloaded network buffers, while rejecting corrupt
    /// signed/implausibly large ETW sizes; an observation over a minute from the last snapshot is not trusted.</summary>
    private bool Acceptable(DateTimeOffset timestamp, int bytes) =>
        bytes is >= 0 and <= 16 * 1024 * 1024 && !(lastTimestamp.HasValue && Math.Abs((timestamp - lastTimestamp.Value).TotalSeconds) > 60);

    private void RecordCore(PeerKey peer, DateTimeOffset timestamp, int bytes, bool send)
    {
        timestamp = timestamp.ToUniversalTime();
        // The minute holding the event, from ticks: the same instant as building it from the UTC date parts, without the calendar work.
        var minute = new DateTimeOffset(timestamp.UtcTicks - timestamp.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
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
            // Remembered endpoints of peers that expired are dropped once they clearly outnumber the live peers.
            if (endpoints.Count > peers.Count + 1024)
                foreach (var stale in endpoints.Where(p => !peers.ContainsKey(p.Value)).Select(p => p.Key).ToArray())
                    endpoints.Remove(stale);

            bool hasInterval = lastTimestamp.HasValue && lastMonotonic.HasValue;
            var live = peers.Select(p => new ConnectionTraffic(p.Key.Peer, p.Value.First, p.Value.Last, p.Value.Down, p.Value.Up,
                hasInterval ? Math.Min(double.MaxValue, (p.Value.Down - p.Value.PreviousDown) / elapsed) : 0,
                hasInterval ? Math.Min(double.MaxValue, (p.Value.Up - p.Value.PreviousUp) / elapsed) : 0)).ToArray();
            var deltas = pending.Select(p => new ConnectionDelta(p.Key.Minute, p.Key.Peer.Peer, p.Value.Down, p.Value.Up, p.Value.First, p.Value.Last)).ToArray();
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
        endpoints.Clear();
        lastTimestamp = null;
        lastMonotonic = null;
        segment = Guid.NewGuid().ToString("N");
        lostEvents = Sum(lostEvents, Math.Max(0, loss));
        incomplete = true;
    }

    private void Drop(long count) { droppedEvents = Sum(droppedEvents, count); incomplete = true; }
    private static long Sum(long value, long increment) => value > long.MaxValue - increment ? long.MaxValue : value + increment;
}
