using System.Globalization;

namespace DeadWare.Core;

public enum TrafficMeasure { Total, Received, Sent }

public sealed record TrafficStripApplication(string Name, string Key, long Received, long Sent);

/// <summary>One listed slot. Received and Sent hold one entry per application in TrafficStrip.Applications, then Other: Applications.Count + 1 entries.</summary>
public sealed record TrafficStripSlot(DateTimeOffset Start, DateTimeOffset End, long Minutes, long RecordedSeconds, bool Incomplete,
    IReadOnlyList<long> Received, IReadOnlyList<long> Sent);

public sealed record TrafficStrip(DateTimeOffset From, DateTimeOffset To, long BucketMilliseconds, TrafficMeasure Measure,
    IReadOnlyList<TrafficStripApplication> Applications, long OtherReceived, long OtherSent, long OtherApplications,
    IReadOnlyList<TrafficStripSlot> Slots, DateTimeOffset? RetainedFrom = null, bool CapacityShortened = false);

public static class TrafficStrips
{
    public const int MaximumApplications = 6, MaximumSlots = 1500, ConnectionsRetentionDays = 30;
    private static readonly long[] BucketChoices = [1, 2, 5, 10, 15, 30];

    /// <summary>The first whole UTC minute at or after the time.</summary>
    public static DateTimeOffset CeilingMinute(DateTimeOffset time)
        => new((time.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>The first of 1, 2, 5, 10, 15 or 30 minutes that gives at most MaximumSlots slots from the first whole minute at or after from; otherwise ⌈minutes ÷ MaximumSlots⌉.</summary>
    public static long ConnectionsBucketMinutes(DateTimeOffset from, DateTimeOffset to)
    {
        long minutes = WholeMinutesFromOrigin(from, to);
        foreach (var b in BucketChoices) if (CeilingDivide(minutes, b) <= MaximumSlots) return b;
        return CeilingDivide(minutes, MaximumSlots);
    }

    public static int ConnectionsSlotCount(DateTimeOffset from, DateTimeOffset to)
        => (int)CeilingDivide(WholeMinutesFromOrigin(from, to), ConnectionsBucketMinutes(from, to));

    /// <summary>Slot index: [origin + index·b, min(origin + (index + 1)·b, to)), with origin the first whole minute at or after from.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) ConnectionsSlot(DateTimeOffset from, DateTimeOffset to, long index)
    {
        var origin = CeilingMinute(from);
        var bucket = TimeSpan.FromTicks(ConnectionsBucketMinutes(from, to) * TimeSpan.TicksPerMinute);
        var start = origin + index * bucket;
        var end = start + bucket;
        return (start, end < to ? end : to);
    }

    /// <summary>The number of whole minutes that start in [start, end).</summary>
    public static long MinuteStarts(DateTimeOffset start, DateTimeOffset end)
        => (CeilingMinute(end).UtcTicks - CeilingMinute(start).UtcTicks) / TimeSpan.TicksPerMinute;

    /// <summary>Not incomplete, and RecordedSeconds ≥ 60 × max(1, n − 0.5) with n the whole minutes that start in the slot.</summary>
    public static bool IsFull(TrafficStripSlot slot)
        => !slot.Incomplete && slot.RecordedSeconds >= 30 * Math.Max(2, 2 * MinuteStarts(slot.Start, slot.End) - 1);

    public static DateTimeOffset DueUntil(DateTimeOffset to, DateTimeOffset now, TimeSpan saveDelay)
    {
        var saved = now - saveDelay;
        return saved < to ? saved : to;
    }

    /// <summary>The series' measure bytes in the slot; the last series is Other. Total saturates at long.MaxValue.</summary>
    public static long Bytes(TrafficStripSlot slot, int series, TrafficMeasure measure) => measure switch
    {
        TrafficMeasure.Received => slot.Received[series],
        TrafficMeasure.Sent => slot.Sent[series],
        _ => slot.Received[series] > long.MaxValue - slot.Sent[series] ? long.MaxValue : slot.Received[series] + slot.Sent[series],
    };

    /// <summary>Average B/s over the slot's recorded time; null when nothing was recorded.</summary>
    public static double? Rate(TrafficStripSlot slot, int series, TrafficMeasure measure)
        => slot.RecordedSeconds == 0 ? null : (double)Bytes(slot, series, measure) / slot.RecordedSeconds;

    /// <summary>The listed slot whose [Start, End) holds the time, or null.</summary>
    public static int? SlotAt(TrafficStrip strip, DateTimeOffset time)
    {
        int low = 0, high = strip.Slots.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var slot = strip.Slots[middle];
            if (time < slot.Start) high = middle - 1;
            else if (time >= slot.End) low = middle + 1;
            else return middle;
        }
        return null;
    }

    public static string SlotLengthText(long bucketMilliseconds)
        => bucketMilliseconds < 3_600_000
            ? (bucketMilliseconds / 60_000d).ToString("0.#", CultureInfo.CurrentCulture) + "-minute"
            : (bucketMilliseconds / 3_600_000d).ToString("0.#", CultureInfo.CurrentCulture) + "-hour";

    /// <summary>[max(CeilingMinute(from), CeilingMinute(now − 30 d)), CeilingMinute(to)), or null when that is empty.</summary>
    public static (DateTimeOffset From, DateTimeOffset To)? ConnectionsTarget(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var start = CeilingMinute(from);
        var retained = CeilingMinute(now.AddDays(-ConnectionsRetentionDays));
        if (retained > start) start = retained;
        var end = CeilingMinute(to);
        return start < end ? (start, end) : null;
    }

    private static long WholeMinutesFromOrigin(DateTimeOffset from, DateTimeOffset to)
        => Math.Max(0, CeilingDivide(to.UtcTicks - CeilingMinute(from).UtcTicks, TimeSpan.TicksPerMinute));

    private static long CeilingDivide(long value, long divisor) => value <= 0 ? 0 : (value - 1) / divisor + 1;
}
