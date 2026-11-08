namespace DeadWare.Core;

/// <summary>
/// The first network use policy: learning, known apps, episodes, balloon groups and the changes to save.
/// Pure, lock-free and I/O-free; owned by the alert worker alone.
/// </summary>
public sealed class FirstUseTracker
{
    private readonly TimeZoneInfo zone;
    private readonly TimeSpan groupWindow;

    private string? ruleId;
    private bool ruleEnabled;
    private HashSet<string> ignored = new(StringComparer.Ordinal);
    private HashSet<string> excluded = new(StringComparer.Ordinal);

    private bool restored;
    private DateTimeOffset? seededUsage, seededConnections, learningStart, learningEnd, floor;

    private readonly Dictionary<string, Known> known = new(StringComparer.Ordinal);
    private readonly SortedSet<(long Last, string Key)> byLast = new(LastComparer.Instance);

    // Pending changes, one per key, in the order each key's first untaken change was recorded.
    private readonly LinkedList<Change> order = new();
    private readonly Dictionary<string, LinkedListNode<Change>> pending = new(StringComparer.Ordinal);
    private long sequence;
    private Meta metaSeededUsage, metaSeededConnections, metaLearningStart, metaLearningEnd;

    private readonly List<Group> groups = [];

    public FirstUseTracker(TimeZoneInfo zone, TimeSpan? groupWindow = null)
    {
        this.zone = zone ?? throw new ArgumentNullException(nameof(zone));
        this.groupWindow = groupWindow ?? FirstNetworkUse.GroupWindow;
    }

    public FirstUseStatus Status => new(restored, seededUsage is not null && seededConnections is not null, learningStart, learningEnd, restored ? known.Count : null);

    public bool HasChanges => order.Count > 0 || metaSeededUsage.Pending || metaSeededConnections.Pending || metaLearningStart.Pending || metaLearningEnd.Pending;

    /// <summary>The due time of the earliest waiting group, or null when none is open.</summary>
    public DateTimeOffset? GroupDue => groups.Count > 0 ? groups[0].Due : null;

    public bool NeedsSeed(FirstUseSource source) => (source == FirstUseSource.Usage ? seededUsage : seededConnections) is null;

    /// <summary>The FirstNetworkUse rule (or none) and the Don't alert and Don't record lists, as names or keys.</summary>
    public void Configure(AlertRule? rule, IEnumerable<string> ignored, IEnumerable<string> excluded)
    {
        ruleId = rule?.Id;
        ruleEnabled = rule?.Enabled == true;
        this.ignored = new HashSet<string>((ignored ?? []).Select(AppKey.Of), StringComparer.Ordinal);
        this.excluded = new HashSet<string>((excluded ?? []).Select(AppKey.Of), StringComparer.Ordinal);
    }

    public void Restore(FirstUseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        seededUsage = state.SeededUsage; seededConnections = state.SeededConnections;
        learningStart = state.LearningStart; learningEnd = state.LearningEnd;
        known.Clear(); byLast.Clear(); order.Clear(); pending.Clear(); groups.Clear();
        metaSeededUsage = metaSeededConnections = metaLearningStart = metaLearningEnd = default;
        floor = null;
        foreach (var row in state.Known ?? [])
        {
            if (row is null || known.ContainsKey(row.Key)) continue;
            Add(row.Key, row.First, row.Last);
        }
        restored = true;
    }

    /// <summary>Merges one store's names. Null means the read failed: the store stays unseeded. Never alerts and never forgets a known key.</summary>
    public void Seed(FirstUseSource source, IReadOnlyList<ApplicationSeen>? names, DateTimeOffset now)
    {
        if (names is null || !NeedsSeed(source)) return;
        var cutoff = now - TimeSpan.FromDays(FirstNetworkUse.ForgetDays);
        var merged = new Dictionary<string, (DateTimeOffset First, DateTimeOffset Last)>(StringComparer.Ordinal);
        foreach (var seen in names)
        {
            if (seen is null || !FirstNetworkUse.IsLearnable(seen.Name)) continue;
            string key = AppKey.Of(seen.Name);
            if (excluded.Contains(key)) continue;
            var first = UsageMetrics.Floor(seen.First < seen.Last ? seen.First : seen.Last);
            var last = UsageMetrics.Floor(seen.First < seen.Last ? seen.Last : seen.First);
            merged[key] = merged.TryGetValue(key, out var m) ? (Min(m.First, first), Max(m.Last, last)) : (first, last);
        }
        foreach (var (key, row) in merged.Where(p => p.Value.Last >= cutoff)
                     .OrderByDescending(p => p.Value.Last).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (known.TryGetValue(key, out var k))
            {
                var first = Min(k.First, row.First); var last = Max(k.Last, row.Last);
                if (first == k.First && last == k.Last) continue;
                Update(key, k, first, last);
                RecordUpsert(key);
            }
            else if (known.Count < FirstNetworkUse.MaximumKnown)
            {
                Add(key, row.First, row.Last);
                RecordUpsert(key);
            }
        }
        if (source == FirstUseSource.Usage) { seededUsage = now; metaSeededUsage = new(now, sequence, true); }
        else { seededConnections = now; metaSeededConnections = new(now, sequence, true); }
    }

    public IReadOnlyList<AlertEpisode> Observe(DateTimeOffset at, IReadOnlyList<NetworkUse> uses, DateTimeOffset now)
    {
        if (ruleId is null) return [];
        if (learningStart is null)
        {
            learningStart = at; learningEnd = FirstNetworkUse.LearningEnd(at, zone);
            metaLearningStart = new(learningStart, sequence, true);
            metaLearningEnd = new(learningEnd, sequence, true);
        }
        if (uses is null || uses.Count == 0) return [];
        List<AlertEpisode>? episodes = null;
        bool alerting = ruleEnabled && at >= learningEnd;
        foreach (var use in uses)
        {
            if (use is null || !FirstNetworkUse.IsLearnable(use.Name)) continue;
            string key = AppKey.Of(use.Name);
            if (excluded.Contains(key)) continue;
            var minute = UsageMetrics.Floor(use.FirstSeen);
            if (known.TryGetValue(key, out var k))
            {
                if (minute > k.Last) { Update(key, k, k.First, minute); RecordUpsert(key); }
                continue;
            }
            if (known.Count >= FirstNetworkUse.MaximumKnown) Evict();
            Add(key, minute, minute);
            if (!alerting || ignored.Contains(key)) { RecordUpsert(key); continue; }

            // The episode carries the known row (Append writes it), so the key has no separate change.
            DropPending(key);
            string name = UsageMetrics.NormalizeName(use.Name);
            var start = use.FirstSeen < at ? use.FirstSeen : at;
            if (floor is { } f && start < f) start = f;
            var episode = new AlertEpisode(Guid.NewGuid().ToString("N"), ruleId, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary(name), start, start, 0,
                Application: name);
            (episodes ??= []).Add(episode);
            Join(new Entry(key, name, start), now);
        }
        return episodes ?? (IReadOnlyList<AlertEpisode>)[];
    }

    /// <summary>Closes the earliest group when it is due; null when none is due, none of its entries remain, or no switched-on rule exists.</summary>
    public FirstUseGroup? TakeDueGroup(DateTimeOffset now)
    {
        if (groups.Count == 0 || now < groups[0].Due) return null;
        var group = groups[0]; groups.RemoveAt(0);
        if (ruleId is null || !ruleEnabled) return null;
        var names = new List<string>(Math.Min(group.Entries.Count, FirstNetworkUse.MaximumGroupNames));
        DateTimeOffset? first = null; int count = 0;
        foreach (var entry in group.Entries)
        {
            if (ignored.Contains(entry.Key) || excluded.Contains(entry.Key) || !known.ContainsKey(entry.Key)) continue;
            count++;
            if (names.Count < FirstNetworkUse.MaximumGroupNames) names.Add(entry.Name);
            if (first is null || entry.Start < first) first = entry.Start;
        }
        return count == 0 ? null : new FirstUseGroup(first!.Value, names, count);
    }

    /// <summary>Forgets keys last seen before now − 400 days and drops their pending upserts; Append's retention deletes their rows.</summary>
    public void Expire(DateTimeOffset now)
    {
        long cutoff = (now - TimeSpan.FromDays(FirstNetworkUse.ForgetDays)).UtcTicks;
        while (byLast.Count > 0 && byLast.Min.Last < cutoff)
        {
            var (_, key) = byLast.Min;
            Remove(key);
            DropUpsert(key);
        }
    }

    /// <summary>Forgets one key, or every key when null, for an all-time clear; later episodes start at or after notBefore.</summary>
    public void Forget(string? key, DateTimeOffset notBefore)
    {
        if (key is null)
        {
            known.Clear(); byLast.Clear();
            for (var node = order.First; node is not null;)
            {
                var next = node.Next;
                if (!node.Value.Delete) { pending.Remove(node.Value.Key); order.Remove(node); }
                node = next;
            }
            foreach (var group in groups) group.Entries.Clear();
        }
        else
        {
            key = AppKey.Of(key);
            Remove(key);
            DropUpsert(key);
            foreach (var group in groups) group.Entries.RemoveAll(e => e.Key == key);
        }
        groups.RemoveAll(g => g.Entries.Count == 0);
        floor = notBefore;
    }

    /// <summary>At most maximum upserts and maximum deletes, oldest first, and each metadata value whose earlier changes have all been taken.</summary>
    public FirstUseChanges TakeChanges(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        var upserts = new List<KnownApplication>(); var deletes = new List<string>();
        while (order.First is { } node)
        {
            var change = node.Value;
            if (change.Delete ? deletes.Count >= maximum : upserts.Count >= maximum) break;
            if (change.Delete) deletes.Add(change.Key);
            else { var k = known[change.Key]; upserts.Add(new KnownApplication(change.Key, k.First, k.Last)); }
            order.RemoveFirst(); pending.Remove(change.Key);
        }
        long boundary = order.First?.Value.Sequence ?? long.MaxValue;
        var metadata = new FirstUseMetadata(Take(ref metaSeededUsage, boundary), Take(ref metaSeededConnections, boundary),
            Take(ref metaLearningStart, boundary), Take(ref metaLearningEnd, boundary));
        return new FirstUseChanges(upserts, deletes, metadata);
    }

    private static DateTimeOffset? Take(ref Meta meta, long boundary)
    {
        if (!meta.Pending || meta.Sequence > boundary) return null;
        var value = meta.Value; meta = default;
        return value;
    }

    private void Join(Entry entry, DateTimeOffset now)
    {
        if (groups.Count == 0 || now >= groups[^1].Due) groups.Add(new Group(now + groupWindow));
        groups[^1].Entries.Add(entry);
    }

    private void Evict()
    {
        var (_, key) = byLast.Min;
        Remove(key);
        RecordDelete(key);
    }

    private void Add(string key, DateTimeOffset first, DateTimeOffset last)
    {
        known[key] = new Known(first, last);
        byLast.Add((last.UtcTicks, key));
    }

    private void Update(string key, Known current, DateTimeOffset first, DateTimeOffset last)
    {
        if (last != current.Last) { byLast.Remove((current.Last.UtcTicks, key)); byLast.Add((last.UtcTicks, key)); }
        known[key] = new Known(first, last);
    }

    private void Remove(string key)
    {
        if (known.Remove(key, out var k)) byLast.Remove((k.Last.UtcTicks, key));
    }

    // A key's change keeps its place in the order when replaced, so a metadata value never goes ahead of a key changed before it.
    private void RecordUpsert(string key) => Record(key, delete: false);
    private void RecordDelete(string key) => Record(key, delete: true);

    private void Record(string key, bool delete)
    {
        if (pending.TryGetValue(key, out var node)) node.Value = node.Value with { Delete = delete };
        else pending[key] = order.AddLast(new Change(key, delete, sequence++));
    }

    private void DropUpsert(string key)
    {
        if (pending.TryGetValue(key, out var node) && !node.Value.Delete) { order.Remove(node); pending.Remove(key); }
    }

    private void DropPending(string key)
    {
        if (pending.Remove(key, out var node)) order.Remove(node);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private readonly record struct Known(DateTimeOffset First, DateTimeOffset Last);
    private sealed record Change(string Key, bool Delete, long Sequence);
    private readonly record struct Meta(DateTimeOffset? Value, long Sequence, bool Pending);
    private sealed record Entry(string Key, string Name, DateTimeOffset Start);
    private sealed class Group(DateTimeOffset due)
    {
        public DateTimeOffset Due { get; } = due;
        public List<Entry> Entries { get; } = [];
    }

    private sealed class LastComparer : IComparer<(long Last, string Key)>
    {
        public static readonly LastComparer Instance = new();
        public int Compare((long Last, string Key) x, (long Last, string Key) y)
        {
            int c = x.Last.CompareTo(y.Last);
            return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
        }
    }
}
