using System.Globalization;

namespace DeadWare.Core;

/// <summary>The page a traffic strip sits on: it decides the empty texts, the legend's wording and which slots are hatched.</summary>
public enum TrafficStripKind { Connections, UsageHistory }

public enum TrafficSlotState { NoCapture, NotSaved, NoTraffic, Full, Partial }

/// <summary>One series' filled area over runs of adjacent slots in the same state; <see cref="Full"/> runs are drawn stronger.</summary>
public sealed record TrafficStripBand(int Series, bool Full, IReadOnlyList<PlotPoint[]> Polygons);

/// <summary>What the pointer is over: a listed slot, or the begun minutes that are not saved yet; with the chip's lines and anchor.</summary>
public sealed record TrafficStripHover(int? Index, bool NotYet, IReadOnlyList<string> Lines, double X, double Y);

/// <summary>One legend entry: the heading (Series −1) or a series with its application (null for Other).</summary>
public sealed record TrafficLegendRow(string Text, string? Tip, int Series, TrafficStripApplication? Application);

/// <summary>
/// A short stacked chart of the six applications with the most traffic, plus Other, as average rates per slot. Unrecorded time is
/// hatched and never drawn as zero; the minute not yet saved stays empty and says why. This holds the data and the geometry; a view draws it.
/// </summary>
public sealed class TrafficStripModel(TrafficStripKind page)
{
    public const double LeftMargin = 12, RightMargin = 8, TopMargin = 10, BottomMargin = 24;
    public const string NoSavedTraffic = "No saved traffic", NotRecordedReason = "Not recorded: in Don't record these apps";

    private DateTimeOffset dueUntil, now;
    private TrafficSlotState[] states = [];
    private string[]?[] reasons = [];
    private double[][]? rates;
    private int seriesCount;
    private readonly List<(double X, double Tolerance)> targets = [];
    private double[] stackTops = [];
    private (double Left, double Right)? notYetTarget;
    private DateTimeOffset notYetStart, notYetEnd;

    public TrafficStrip? Strip { get; private set; }
    /// <summary>The text drawn in place of a chart (reading, unavailable, invalid interval), or null.</summary>
    public string? Message { get; private set; }
    /// <summary>The text drawn over an empty chart, or null.</summary>
    public string? EmptyText { get; private set; }
    /// <summary>The label of the band over [origin, RetainedFrom): "Cleared" after an all-apps clear, otherwise "Not kept"; null without a band.</summary>
    public string? BandLabel { get; private set; }
    /// <summary>The shown strip is filtered to an application in Don't record these apps, so its recorded zero-byte slots are Not saved.</summary>
    public bool FilterNotRecorded { get; private set; }
    public int SeriesCount => seriesCount;

    // Geometry from the last Layout.
    public PlotRect Plot { get; private set; }
    public double Maximum { get; private set; } = 1;
    public IReadOnlyList<TrafficStripBand> Bands { get; private set; } = [];
    /// <summary>Hairlines between stacked series, where slots are wide enough to show them: (left, right, y).</summary>
    public IReadOnlyList<(double Left, double Right, double Y)> Separators { get; private set; } = [];
    /// <summary>Baseline runs under recorded slots: (left, right) at the plot's bottom.</summary>
    public IReadOnlyList<(double Left, double Right)> Baseline { get; private set; } = [];
    /// <summary>Hatched spans: due whole minutes that no drawn slot holds.</summary>
    public IReadOnlyList<PlotRect> Hatch { get; private set; } = [];
    public PlotRect? NotKept { get; private set; }
    /// <summary>False when the last layout was too small to draw.</summary>
    public bool Drawn { get; private set; }

    /// <summary>Shows a strip. Due until is clamped to the strip's end; now places the begun not-yet minutes.</summary>
    public void SetData(TrafficStrip data, DateTimeOffset due, DateTimeOffset current, bool filterNotRecorded = false)
    {
        Strip = data; dueUntil = due < data.To ? due : data.To; now = current; Message = null; FilterNotRecorded = filterNotRecorded;
        int apps = data.Applications.Count;
        seriesCount = apps + (TrafficStrips.Bytes(data.OtherReceived, data.OtherSent, data.Measure) > 0 ? 1 : 0);
        states = new TrafficSlotState[data.Slots.Count];
        reasons = new string[data.Slots.Count][];
        rates = new double[data.Slots.Count][];
        string? clearedReason = data.AppClearedBefore is { } cleared ? $"Some application history before {cleared.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} was cleared" : null;
        for (int i = 0; i < data.Slots.Count; i++)
        {
            var slot = data.Slots[i];
            long total = SlotTotal(slot, apps, data.Measure);
            bool beforeClear = data.AppClearedBefore is { } before && slot.Start < before;
            states[i] = page == TrafficStripKind.UsageHistory && slot.RecordedSeconds == 0 ? TrafficSlotState.NoCapture
                : slot.RecordedSeconds > 0 && total == 0 && (beforeClear || filterNotRecorded) ? TrafficSlotState.NotSaved
                : slot.RecordedSeconds > 0 && total == 0 ? TrafficSlotState.NoTraffic
                : TrafficStrips.IsFull(slot) ? TrafficSlotState.Full : TrafficSlotState.Partial;
            if (states[i] == TrafficSlotState.NotSaved)
                reasons[i] = beforeClear && filterNotRecorded ? [clearedReason!, NotRecordedReason] : beforeClear ? [clearedReason!] : [NotRecordedReason];
            int index = i;
            rates[i] = Enumerable.Range(0, seriesCount).Select(s => TrafficStrips.Rate(data.Slots[index], s, data.Measure) ?? 0).ToArray();
        }
        bool wholly = data.RetainedFrom is { } retained && retained >= data.To;
        BandLabel = data.RetainedFrom is { } start && start > TrafficStrips.CeilingMinute(data.From) ? data.RetainedByClear ? "Cleared" : "Not kept" : null;
        EmptyText = wholly ? null
            : page == TrafficStripKind.Connections ? data.Slots.Count == 0 ? "No saved history in this interval" : null
            : data.Slots.Count == 0 ? "No history in this range yet"
            : data.Slots.All(s => s.RecordedSeconds == 0) ? "No per-application capture in this range" : null;
    }

    /// <summary>Clears the data and shows a text in place of the chart.</summary>
    public void SetText(string text)
    {
        Strip = null; Message = text; EmptyText = null; states = []; reasons = []; rates = null; seriesCount = 0; BandLabel = null; FilterNotRecorded = false;
    }

    public TrafficSlotState State(int index) => states[index];

    /// <summary>A Not saved slot's reason lines (the chip's lines after "No saved traffic"); null for any other slot.</summary>
    public IReadOnlyList<string>? NotSavedReasons(int index) => index >= 0 && index < reasons.Length ? reasons[index] : null;

    private double Seconds => Strip is null ? 1 : Math.Max(1, (Strip.To - Strip.From).TotalSeconds);
    public double X(DateTimeOffset time) => Plot.X + Plot.Width * Math.Clamp((time - Strip!.From).TotalSeconds / Seconds, 0, 1);
    public double Y(double value) => Plot.Y + Plot.Height * (1 - Math.Clamp(value / Maximum, 0, 1));
    public DateTimeOffset TimeAt(double x) => Strip!.From + TimeSpan.FromSeconds(Seconds * Math.Clamp((x - Plot.X) / Math.Max(1, Plot.Width), 0, 1));
    private static DateTimeOffset FloorMinute(DateTimeOffset time) => new(time.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>Every series' measure bytes in the slot (the applications, then Other), saturating like TrafficStrips.Bytes.</summary>
    private static long SlotTotal(TrafficStripSlot slot, int apps, TrafficMeasure measure)
    {
        long total = 0;
        for (int s = 0; s <= apps; s++) total = TrafficStrips.Bytes(total, TrafficStrips.Bytes(slot, s, measure), TrafficMeasure.Total);
        return total;
    }

    private bool Drawable(int index) => states[index] is TrafficSlotState.Full or TrafficSlotState.Partial && Strip!.Slots[index].RecordedSeconds > 0;

    /// <summary>The x of a listed slot's middle, for the selection hairline; null when the index is not a listed slot.</summary>
    public double? SlotMiddle(int index) => index >= 0 && index < targets.Count ? targets[index].X : null;

    /// <summary>Builds the geometry for a size. Call after the data or the size changes.</summary>
    public void Layout(double width, double height)
    {
        Plot = new PlotRect(LeftMargin, TopMargin, Math.Max(0, width - LeftMargin - RightMargin), Math.Max(0, height - TopMargin - BottomMargin));
        targets.Clear(); stackTops = []; notYetTarget = null; Maximum = 1;
        Bands = []; Separators = []; Baseline = []; Hatch = []; NotKept = null;
        Drawn = width >= 160 && height >= 100;
        if (Strip is not { } data || rates is null || !Drawn) return;
        var plot = Plot; var slots = data.Slots;
        stackTops = rates.Select(r => r.Sum()).ToArray();
        double highest = 0;
        for (int i = 0; i < slots.Count; i++) if (Drawable(i)) highest = Math.Max(highest, stackTops[i]);
        Maximum = Math.Max(1, highest * 1.15);
        var x0 = new double[slots.Count]; var x1 = new double[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            x0[i] = X(slots[i].Start); x1[i] = X(slots[i].End);
            targets.Add(((x0[i] + x1[i]) / 2, Math.Max(1, (x1[i] - x0[i]) / 2)));
        }

        // Runs of adjacent drawable slots in the same state; any change of state, a gap or an undrawn slot breaks a run.
        var runs = new List<(int First, int Last)>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (!Drawable(i)) continue;
            if (runs.Count > 0 && runs[^1].Last == i - 1 && slots[i - 1].End == slots[i].Start && states[i - 1] == states[i]) runs[^1] = (runs[^1].First, i);
            else runs.Add((i, i));
        }
        double Lower(int slot, int series) { double sum = 0; for (int s = 0; s < series; s++) sum += rates[slot][s]; return sum; }
        double Upper(int slot, int series) => Lower(slot, series) + rates[slot][series];
        var bands = new List<TrafficStripBand>();
        for (int series = 0; series < seriesCount; series++)
            foreach (bool full in new[] { true, false })
            {
                var polygons = new List<PlotPoint[]>(); bool any = false;
                foreach (var (first, last) in runs)
                {
                    if ((states[first] == TrafficSlotState.Full) != full) continue;
                    for (int i = first; i <= last && !any; i++) any |= rates[i][series] > 0;
                    var points = new List<PlotPoint>((last - first + 1) * 4);
                    for (int i = first; i <= last; i++) { double y = Y(Upper(i, series)); points.Add(new(x0[i], y)); points.Add(new(x1[i], y)); }
                    for (int i = last; i >= first; i--) { double y = Y(Lower(i, series)); points.Add(new(x1[i], y)); points.Add(new(x0[i], y)); }
                    polygons.Add([.. points]);
                }
                if (any) bands.Add(new(series, full, polygons));
            }
        Bands = bands;

        // Separators only where slots are at least 3 px wide, so a stroke never paints over sub-pixel fills.
        if (data.BucketMilliseconds / 1000d / Seconds * plot.Width >= 3)
        {
            var separators = new List<(double, double, double)>();
            for (int series = 0; series < seriesCount - 1; series++)
                for (int i = 0; i < slots.Count; i++)
                {
                    if (!Drawable(i)) continue;
                    double top = Y(Upper(i, series));
                    if (Y(Lower(i, series)) - top < 3) continue;
                    separators.Add((x0[i], x1[i], top));
                }
            Separators = separators;
        }

        var baseline = new List<(double, double)>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].RecordedSeconds <= 0) continue;
            int last = i;
            while (last + 1 < slots.Count && slots[last + 1].RecordedSeconds > 0 && slots[last + 1].Start == slots[last].End) last++;
            baseline.Add((x0[i], x1[last]));
            i = last;
        }
        Baseline = baseline;

        var origin = TrafficStrips.CeilingMinute(data.From);
        DateTimeOffset? retained = data.RetainedFrom;
        if (retained is { } kept && kept > origin)
            NotKept = kept >= data.To ? plot : new PlotRect(X(origin), plot.Y, Math.Max(0, X(kept) - X(origin)), plot.Height);

        // Hatching: the due whole minutes from the retained start that no drawn slot holds. A Usage slot without capture and a Not
        // saved slot are hatched too.
        var hatchStart = retained is { } r && r > origin ? TrafficStrips.CeilingMinute(r) : origin;
        var hatchEnd = FloorMinute(dueUntil);
        var hatch = new List<PlotRect>();
        void Add(DateTimeOffset from, DateTimeOffset to)
        {
            if (from < hatchStart) from = hatchStart;
            if (to > hatchEnd) to = hatchEnd;
            if (from >= to) return;
            double left = X(from), right = X(to);
            hatch.Add(new(left, plot.Y, right - left, plot.Height));
        }
        var cursor = hatchStart;
        for (int i = 0; i < slots.Count; i++)
        {
            if (states[i] is TrafficSlotState.NoCapture or TrafficSlotState.NotSaved) continue;
            DateTimeOffset first = TrafficStrips.CeilingMinute(slots[i].Start), end = TrafficStrips.CeilingMinute(slots[i].End);
            if (end <= first) continue;
            Add(cursor, first);
            if (end > cursor) cursor = end;
        }
        Add(cursor, hatchEnd);
        Hatch = hatch;

        // The begun not-yet minutes: from the first minute that is not due to the end of the minute that holds now.
        notYetStart = FloorMinute(dueUntil) < origin ? origin : FloorMinute(dueUntil);
        var nowEnd = FloorMinute(now).AddMinutes(1);
        notYetEnd = nowEnd < data.To ? nowEnd : data.To;
        if (notYetStart < notYetEnd) notYetTarget = (X(notYetStart), X(notYetEnd));
    }

    /// <summary>The listed slot under a point, or null.</summary>
    public int? SlotAt(PlotPoint point)
    {
        if (!Plot.Contains(point)) return null;
        int best = -1; double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < targets.Count; i++)
        {
            double distance = Math.Abs(targets[i].X - point.X);
            if (distance <= targets[i].Tolerance && distance < bestDistance) { best = i; bestDistance = distance; }
        }
        return best >= 0 ? best : null;
    }

    /// <summary>What a pointer position is over, with its chip; null over nothing.</summary>
    public TrafficStripHover? Hover(PlotPoint? point)
    {
        if (point is not { } p || Strip is null) return null;
        if (SlotAt(p) is { } index)
        {
            double y = Drawable(index) ? Y(stackTops[index]) : Plot.Y + Plot.Height / 2;
            return new(index, false, SlotChip(index), targets[index].X, y);
        }
        if (Plot.Contains(p) && notYetTarget is { } span && p.X >= span.Left && p.X < span.Right)
            return new(null, true, [Span(notYetStart, notYetEnd), "Not saved yet", "Minutes are saved after they end"], (span.Left + span.Right) / 2, Plot.Y + Plot.Height / 2);
        return null;
    }

    private string[] SlotChip(int index)
    {
        var data = Strip!; var slot = data.Slots[index];
        var lines = new List<string> { Span(slot.Start, slot.End) };
        if (states[index] == TrafficSlotState.NoCapture) { lines.Add("No per-application capture"); return [.. lines]; }
        if (states[index] == TrafficSlotState.NotSaved) { lines.Add(NoSavedTraffic); lines.AddRange(reasons[index]!); return [.. lines]; }
        if (states[index] == TrafficSlotState.NoTraffic) lines.Add("No traffic");
        else if (slot.RecordedSeconds > 0)
        {
            long total = SlotTotal(slot, data.Applications.Count, data.Measure);
            lines.Add($"{HistoryChartModel.Bytes(total)} · {HistoryChartModel.Bytes((double)total / slot.RecordedSeconds, true)} average");
        }
        if (!TrafficStrips.IsFull(slot))
        {
            long recorded = (long)Math.Round(slot.RecordedSeconds / 60d, MidpointRounding.AwayFromZero);
            lines.Add($"Recorded {recorded} of {TrafficStrips.MinuteStarts(slot.Start, slot.End)} min");
            if (slot.Incomplete) lines.Add("Incomplete capture");
        }
        return [.. lines];
    }

    /// <summary>A slot's span in local time ("03:13–03:16"), with the date when the strip spans more than a day.</summary>
    public string SlotSpan(int index) => Span(Strip!.Slots[index].Start, Strip.Slots[index].End);

    private string Span(DateTimeOffset start, DateTimeOffset end)
    {
        bool withDate = Strip is { } data && data.To - data.From > TimeSpan.FromDays(1);
        DateTime first = TrafficStrips.CeilingMinute(start).ToLocalTime().DateTime, last = TrafficStrips.CeilingMinute(end).ToLocalTime().DateTime;
        static string Format(DateTime time, bool date) => time.ToString(date ? "MMM d HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
        return $"{Format(first, withDate)}–{Format(last, withDate && last.Date != first.Date)}";
    }

    /// <summary>The time a click at x activates in a slot: under the pointer, clamped into the slot.</summary>
    public DateTimeOffset ClickTime(int index, double x)
    {
        var slot = Strip!.Slots[index]; var at = TimeAt(x);
        if (at < slot.Start) at = slot.Start;
        if (at >= slot.End) at = slot.End.AddTicks(-1);
        return at;
    }

    /// <summary>
    /// Where Left or Right moves the selection: from a selection to its neighbour (null at the ends), from no selection to the listed
    /// slot before or after the mark, or to the last or first slot without a mark. Null also when there are no slots.
    /// </summary>
    public int? Step(bool left, int? selectedIndex, DateTimeOffset? mark)
    {
        int count = Strip?.Slots.Count ?? 0;
        if (count == 0) return null;
        var slots = Strip!.Slots;
        if (selectedIndex is { } s)
        {
            int selected = Math.Clamp(s, 0, count - 1);
            return left ? selected > 0 ? selected - 1 : null : selected < count - 1 ? selected + 1 : null;
        }
        if (mark is { } time)
        {
            if (left) { int last = -1; for (int i = 0; i < count; i++) if (slots[i].End <= time) last = i; return last >= 0 ? last : null; }
            for (int i = 0; i < count; i++) if (slots[i].Start > time) return i;
            return null;
        }
        return left ? count - 1 : 0;
    }

    /// <summary>The y-axis labels at 0, half and the maximum.</summary>
    public IReadOnlyList<(double Y, string Label)> AxisLabels() =>
        new[] { 0d, .5, 1 }.Select(f => (Y(Maximum * f), HistoryChartModel.Bytes(Maximum * f, true))).ToArray();

    /// <summary>The labels under the plot's two ends.</summary>
    public (string From, string To) DateLabels()
    {
        var data = Strip!;
        string Label(DateTimeOffset time) => (data.To - data.From).TotalDays <= 2
            ? time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : time.ToLocalTime().ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        return (Label(data.From), Label(data.To));
    }

    /// <summary>The caption over the chart.</summary>
    public string Caption() => Strip is not { } strip ? "Traffic by application"
        : $"Traffic by application · {TrafficStrips.SlotLengthText(strip.BucketMilliseconds)} slots"
            + strip.Measure switch { TrafficMeasure.Received => " · received", TrafficMeasure.Sent => " · sent", _ => "" };

    /// <summary>The lines under the legend: the retained-start line when set, the one-app clear's line, then the not-recorded filter's line.</summary>
    public IReadOnlyList<string> NoteLines()
    {
        var lines = new List<string>();
        if (Strip is not { } strip) return lines;
        if (strip.RetainedFrom is { } retained)
            lines.Add((strip.RetainedByClear ? "Cleared before " : "Not kept before ") + retained.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                + (strip.CapacityShortened ? " · Capacity shortened retention" : ""));
        if (strip.AppClearedBefore is { } cleared) lines.Add($"Some application history before {cleared.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} was cleared.");
        if (FilterNotRecorded) lines.Add("Not recorded: this app is in Don't record these apps.");
        return lines;
    }

    /// <summary>The legend: a heading, then one entry per series. With a shown slot the entries give that slot's bytes; otherwise the interval's totals and shares.</summary>
    public IReadOnlyList<TrafficLegendRow> LegendRows(int? shownIndex)
    {
        var rows = new List<TrafficLegendRow>();
        if (Strip is not { } strip) return rows;
        var measure = strip.Measure;
        int apps = strip.Applications.Count;
        long otherTotal = TrafficStrips.Bytes(strip.OtherReceived, strip.OtherSent, measure);
        double denominator = strip.Applications.Sum(a => (double)TrafficStrips.Bytes(a.Received, a.Sent, measure)) + otherTotal;
        bool connections = page == TrafficStripKind.Connections;
        if (denominator <= 0)
        {
            rows.Add(new(connections ? "No application traffic recorded in this interval." : "No application traffic recorded in this period.", null, -1, null));
            return rows;
        }
        int? shown = shownIndex is { } candidate && candidate >= 0 && candidate < strip.Slots.Count ? candidate : null;
        var slot = shown is { } index ? strip.Slots[index] : null;
        rows.Add(new(shown is { } k ? SlotSpan(k) : connections ? "Totals for this interval" : "Totals for this period", null, -1, null));
        for (int series = 0; series < apps + (otherTotal > 0 ? 1 : 0); series++)
        {
            var app = series < apps ? strip.Applications[series] : null;
            string name = app?.Name ?? $"Other ({strip.OtherApplications:N0} more)";
            long received = app?.Received ?? strip.OtherReceived, sent = app?.Sent ?? strip.OtherSent;
            string text, tip;
            if (slot is null)
            {
                long bytes = TrafficStrips.Bytes(received, sent, measure);
                text = $"{name} · {HistoryChartModel.Bytes(bytes)} · {(bytes / denominator * 100).ToString("0.#", CultureInfo.CurrentCulture)}%";
                tip = $"{name} · ↓ {HistoryChartModel.Bytes(received)} · ↑ {HistoryChartModel.Bytes(sent)}";
            }
            else if (NotSavedReasons(shown!.Value) is { } why)
            {
                text = $"{name} · —";
                tip = string.Join("\n", why);
            }
            else if (slot.RecordedSeconds == 0)
            {
                text = $"{name} · —";
                tip = page == TrafficStripKind.UsageHistory ? "No per-application capture in this slot" : "Under a second recorded in this slot";
            }
            else
            {
                text = $"{name} · {HistoryChartModel.Bytes(TrafficStrips.Bytes(slot, series, measure))}";
                tip = $"{name} · ↓ {HistoryChartModel.Bytes(slot.Received[series])} · ↑ {HistoryChartModel.Bytes(slot.Sent[series])}";
            }
            rows.Add(new(text, tip, series, app));
        }
        return rows;
    }
}
