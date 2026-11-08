using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>The page a traffic strip sits on: it decides the empty texts, the legend's wording and which slots are hatched.</summary>
internal enum TrafficStripPage { Connections, UsageHistory }

/// <summary>A short stacked chart of the six applications with the most traffic, plus Other, as average rates per slot. Unrecorded
/// time is hatched and never drawn as zero; the minute not yet saved stays empty and says why. Drawn the way <see cref="UsageHistoryChart"/> is.</summary>
internal sealed class TrafficStripChart : FrameworkElement
{
    private const double LeftMargin = 12, RightMargin = 8, TopMargin = 10, BottomMargin = 24;
    private const double FullOpacity = 0.85, PartialOpacity = 0.45, NotKeptOpacity = 0.35;
    private enum SlotState { NoCapture, NotSaved, NoTraffic, Full, Partial }
    internal const string NoSavedTraffic = "No saved traffic", NotRecordedReason = "Not recorded: in Don't record these apps";

    private readonly TrafficStripPage page;
    private TrafficStrip? strip;
    private DateTimeOffset dueUntil, now;
    private string? message, emptyText;
    private SlotState[] states = [];
    // Per slot: a Not saved slot's reason lines, otherwise null.
    private string[]?[] reasons = [];
    // Per slot: each drawn series' rate (B/s), and the stacked total; null when the slot has no recorded time.
    private double[][]? rates;
    private int seriesCount, version;

    internal TrafficStripChart(TrafficStripPage page)
    {
        this.page = page;
        Height = 130; MinHeight = 120; ClipToBounds = true;
        AutomationProperties.SetName(this, "Traffic by application");
    }

    internal TrafficStrip? Strip => strip;
    /// <summary>The label of the band over [origin, RetainedFrom): "Cleared" when an all-apps clear set the retained start, otherwise
    /// "Not kept"; null when there is no band.</summary>
    internal string? BandLabel { get; private set; }
    /// <summary>The shown strip is filtered to an application in Don't record these apps, so its recorded zero-byte slots are Not saved.</summary>
    internal bool FilterNotRecorded { get; private set; }

    /// <summary>Shows a strip. Due until is clamped to the strip's end; now places the begun not-yet minutes. With
    /// <paramref name="filterNotRecorded"/> the strip is filtered to an application in Don't record these apps.</summary>
    internal void SetData(TrafficStrip data, DateTimeOffset due, DateTimeOffset current, bool filterNotRecorded = false)
    {
        strip = data; dueUntil = due < data.To ? due : data.To; now = current; message = null; FilterNotRecorded = filterNotRecorded;
        int apps = data.Applications.Count;
        seriesCount = apps + (TrafficStrips.Bytes(data.OtherReceived, data.OtherSent, data.Measure) > 0 ? 1 : 0);
        states = new SlotState[data.Slots.Count];
        reasons = new string[data.Slots.Count][];
        rates = new double[data.Slots.Count][];
        string? clearedReason = data.AppClearedBefore is { } cleared ? $"Some application history before {cleared.ToLocalTime().ToString("g")} was cleared" : null;
        for (int i = 0; i < data.Slots.Count; i++)
        {
            var slot = data.Slots[i];
            long total = SlotTotal(slot, apps, data.Measure);
            bool beforeClear = data.AppClearedBefore is { } before && slot.Start < before;
            states[i] = page == TrafficStripPage.UsageHistory && slot.RecordedSeconds == 0 ? SlotState.NoCapture
                : slot.RecordedSeconds > 0 && total == 0 && (beforeClear || filterNotRecorded) ? SlotState.NotSaved
                : slot.RecordedSeconds > 0 && total == 0 ? SlotState.NoTraffic
                : TrafficStrips.IsFull(slot) ? SlotState.Full : SlotState.Partial;
            if (states[i] == SlotState.NotSaved)
                reasons[i] = beforeClear && filterNotRecorded ? [clearedReason!, NotRecordedReason] : beforeClear ? [clearedReason!] : [NotRecordedReason];
            rates[i] = Enumerable.Range(0, seriesCount).Select(s => TrafficStrips.Rate(slot, s, data.Measure) ?? 0).ToArray();
        }
        // On both pages the band covers [origin, RetainedFrom); a RetainedFrom past To (a range wholly before it) fills the plot.
        bool wholly = data.RetainedFrom is { } retained && retained >= data.To;
        BandLabel = data.RetainedFrom is { } start && start > TrafficStrips.CeilingMinute(data.From) ? data.RetainedByClear ? "Cleared" : "Not kept" : null;
        emptyText = wholly ? null
            : page == TrafficStripPage.Connections ? data.Slots.Count == 0 ? "No saved history in this interval" : null
            : data.Slots.Count == 0 ? "No history in this range yet"
            : data.Slots.All(s => s.RecordedSeconds == 0) ? "No per-application capture in this range" : null;
        Changed();
    }

    /// <summary>Clears the data and draws the text the way "No history in this range yet" is drawn.</summary>
    internal void SetText(string text)
    {
        strip = null; message = text; emptyText = null; states = []; reasons = []; rates = null; seriesCount = 0; BandLabel = null; FilterNotRecorded = false;
        Changed();
    }

    /// <summary>A Not saved slot's reason lines (the chip's lines after "No saved traffic"); null for any other slot.</summary>
    internal IReadOnlyList<string>? NotSavedReasons(int index) => index >= 0 && index < reasons.Length ? reasons[index] : null;

    private void Changed()
    {
        version++;
        if (HoverIndex is not null || hoverNotYet) { HoverIndex = null; hoverNotYet = false; ChipLines = []; HoverChanged?.Invoke(); ShownSlotChanged?.Invoke(); }
        InvalidateVisual();
    }

    private bool selectable, keyboardFocused;
    private int? selectedIndex;
    private DateTimeOffset? markTime;
    /// <summary>When true the chart takes focus, a click or Enter activates a slot and Left/Right move the selection, as on
    /// <see cref="UsageHistoryChart"/>. While it has keyboard focus it draws an Accent frame around the plot.</summary>
    internal bool Selectable
    {
        get => selectable;
        set { if (selectable == value) return; selectable = value; Focusable = value; FocusVisualStyle = null; InvalidateVisual(); }
    }
    /// <summary>Index into the strip's slots drawn with an Accent hairline at its middle when <see cref="MarkTime"/> is unset.</summary>
    internal int? SelectedIndex
    {
        get => selectedIndex;
        set { if (selectedIndex == value) return; selectedIndex = value; InvalidateVisual(); ShownSlotChanged?.Invoke(); }
    }
    /// <summary>When set, the Accent hairline is drawn here, and Left/Right from no selection go to the listed slots around it.</summary>
    internal DateTimeOffset? MarkTime
    {
        get => markTime;
        set { if (markTime == value) return; markTime = value; InvalidateVisual(); }
    }
    /// <summary>Raised on a click (at the time under the pointer, clamped into the slot) and on Enter (at the slot's Start).</summary>
    internal event Action<int, DateTimeOffset>? SlotActivated;
    /// <summary>Raised with the new index when Left or Right moves the selection. At the ends the selection stays and nothing is raised.</summary>
    internal event Action<int>? SelectionMoved;
    /// <summary>Raised when the hover target changes, the begun not-yet minutes included.</summary>
    internal event Action? HoverChanged;
    /// <summary>Raised when the slot the legend describes (hovered, otherwise selected) may have changed.</summary>
    internal event Action? ShownSlotChanged;

    /// <summary>The hovered listed slot, or null (also while the begun not-yet minutes are hovered).</summary>
    internal int? HoverIndex { get; private set; }
    /// <summary>The slot the legend describes: the hovered one, otherwise the selected one.</summary>
    internal int? ShownIndex => HoverIndex ?? (selectedIndex is { } index && index >= 0 && index < (strip?.Slots.Count ?? 0) ? index : null);
    internal Rect PlotRect { get; private set; }
    internal IReadOnlyList<string> ChipLines { get; private set; } = [];
    internal IReadOnlyList<(int Series, bool Full, StreamGeometry Geometry)> Bands { get; private set; } = [];
    internal IReadOnlyList<StreamGeometry> Separators { get; private set; } = [];
    internal Geometry? Hatch { get; private set; }
    internal Geometry? Baseline { get; private set; }
    internal Rect? NotKept { get; private set; }

    private bool hoverNotYet;

    // Size-dependent layout, rebuilt when the data or the size changes and never on hover.
    private (int Version, double Width, double Height)? built;
    private readonly List<(double X, double Tolerance)> targets = [];
    private double[] stackTops = [];
    private (double Left, double Right)? notYetTarget;
    private DateTimeOffset notYetStart, notYetEnd;
    private double maximum = 1;
    private GuidelineSet? guides;

    private double Seconds => strip is null ? 1 : Math.Max(1, (strip.To - strip.From).TotalSeconds);
    private double X(DateTimeOffset time) => PlotRect.Left + PlotRect.Width * Math.Clamp((time - strip!.From).TotalSeconds / Seconds, 0, 1);
    private double Y(double value) => PlotRect.Top + PlotRect.Height * (1 - Math.Clamp(value / maximum, 0, 1));
    private DateTimeOffset TimeAt(double x) => strip!.From + TimeSpan.FromSeconds(Seconds * Math.Clamp((x - PlotRect.Left) / Math.Max(1, PlotRect.Width), 0, 1));
    private static DateTimeOffset FloorMinute(DateTimeOffset time) => new(time.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    /// <summary>Every series' measure bytes in the slot (the applications, then Other), saturating like TrafficStrips.Bytes.</summary>
    private static long SlotTotal(TrafficStripSlot slot, int apps, TrafficMeasure measure)
    {
        long total = 0;
        for (int s = 0; s <= apps; s++) total = TrafficStrips.Bytes(total, TrafficStrips.Bytes(slot, s, measure), TrafficMeasure.Total);
        return total;
    }
    private bool Drawable(int index) => states[index] is SlotState.Full or SlotState.Partial && strip!.Slots[index].RecordedSeconds > 0;

    private void EnsureLayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (built == (version, w, h)) return;
        built = (version, w, h);
        PlotRect = new Rect(LeftMargin, TopMargin, Math.Max(0, w - LeftMargin - RightMargin), Math.Max(0, h - TopMargin - BottomMargin));
        targets.Clear(); stackTops = []; notYetTarget = null; maximum = 1; guides = null;
        Bands = []; Separators = []; Hatch = null; Baseline = null; NotKept = null;
        if (strip is not { } data || rates is null || w < 160 || h < 100) return;
        var plot = PlotRect;
        var slots = data.Slots;
        stackTops = rates.Select(r => r.Sum()).ToArray();
        double highest = 0;
        for (int i = 0; i < slots.Count; i++) if (Drawable(i)) highest = Math.Max(highest, stackTops[i]);
        maximum = Math.Max(1, highest * 1.15);
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
        double Lower(int slot, int series) => series == 0 ? 0 : rates[slot].Take(series).Sum();
        double Upper(int slot, int series) => rates[slot].Take(series + 1).Sum();
        var bands = new List<(int, bool, StreamGeometry)>();
        for (int series = 0; series < seriesCount; series++)
            foreach (bool full in new[] { true, false })
            {
                var geometry = new StreamGeometry(); bool any = false;
                using (var context = geometry.Open())
                    foreach (var (first, last) in runs)
                    {
                        if ((states[first] == SlotState.Full) != full) continue;
                        if (!any) for (int i = first; i <= last; i++) any |= rates[i][series] > 0;
                        var points = new List<Point>((last - first + 1) * 4);
                        for (int i = first; i <= last; i++) { double y = Y(Upper(i, series)); points.Add(new(x0[i], y)); points.Add(new(x1[i], y)); }
                        for (int i = last; i >= first; i--) { double y = Y(Lower(i, series)); points.Add(new(x1[i], y)); points.Add(new(x0[i], y)); }
                        context.BeginFigure(points[0], true, true);
                        context.PolyLineTo(points.Skip(1).ToList(), true, false);
                    }
                geometry.Freeze();
                if (any) bands.Add((series, full, geometry));
            }
        Bands = bands;

        var guide = new GuidelineSet();
        guide.GuidelinesY.Add(plot.Bottom + .5);
        // Separators only where slots are at least 3 px wide, so a stroke never paints over sub-pixel fills.
        if (data.BucketMilliseconds / 1000d / Seconds * plot.Width >= 3)
        {
            var separators = new List<StreamGeometry>();
            for (int series = 0; series < seriesCount - 1; series++)
            {
                var geometry = new StreamGeometry(); bool any = false;
                using (var context = geometry.Open())
                    for (int i = 0; i < slots.Count; i++)
                    {
                        if (!Drawable(i)) continue;
                        double top = Y(Upper(i, series));
                        if (Y(Lower(i, series)) - top < 3) continue;
                        context.BeginFigure(new(x0[i], top), false, false);
                        context.LineTo(new(x1[i], top), true, false);
                        guide.GuidelinesY.Add(top + .5);
                        any = true;
                    }
                geometry.Freeze();
                if (any) separators.Add(geometry);
            }
            Separators = separators;
        }
        guide.Freeze(); guides = guide;

        var baseline = new StreamGeometry(); bool anyBaseline = false;
        using (var context = baseline.Open())
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].RecordedSeconds <= 0) continue;
                int last = i;
                while (last + 1 < slots.Count && slots[last + 1].RecordedSeconds > 0 && slots[last + 1].Start == slots[last].End) last++;
                context.BeginFigure(new(x0[i], plot.Bottom), false, false);
                context.LineTo(new(x1[last], plot.Bottom), true, false);
                anyBaseline = true; i = last;
            }
        baseline.Freeze();
        if (anyBaseline) Baseline = baseline;

        var origin = TrafficStrips.CeilingMinute(data.From);
        // The retained start is RetainedFrom on both pages; Usage history sets it only after an all-apps clear.
        DateTimeOffset? retained = data.RetainedFrom;
        if (retained is { } kept && kept > origin)
            NotKept = kept >= data.To ? plot : new Rect(new Point(X(origin), plot.Top), new Point(X(kept), plot.Bottom));

        // Hatching: the due whole minutes from the retained start that no drawn slot holds. A Usage slot without capture and a Not saved
        // slot are hatched too.
        var hatchStart = retained is { } r && r > origin ? TrafficStrips.CeilingMinute(r) : origin;
        var hatchEnd = FloorMinute(dueUntil);
        var hatch = new StreamGeometry(); bool anyHatch = false;
        using (var context = hatch.Open())
        {
            void Add(DateTimeOffset from, DateTimeOffset to)
            {
                if (from < hatchStart) from = hatchStart;
                if (to > hatchEnd) to = hatchEnd;
                if (from >= to) return;
                double left = X(from), right = X(to);
                context.BeginFigure(new(left, plot.Top), true, true);
                context.PolyLineTo([new(right, plot.Top), new(right, plot.Bottom), new(left, plot.Bottom)], false, false);
                anyHatch = true;
            }
            var cursor = hatchStart;
            for (int i = 0; i < slots.Count; i++)
            {
                if (states[i] is SlotState.NoCapture or SlotState.NotSaved) continue;
                DateTimeOffset first = TrafficStrips.CeilingMinute(slots[i].Start), end = TrafficStrips.CeilingMinute(slots[i].End);
                if (end <= first) continue;
                Add(cursor, first);
                if (end > cursor) cursor = end;
            }
            Add(cursor, hatchEnd);
        }
        hatch.Freeze();
        if (anyHatch) Hatch = hatch;

        // The begun not-yet minutes: from the first minute that is not due to the end of the minute that holds now.
        notYetStart = FloorMinute(dueUntil) < origin ? origin : FloorMinute(dueUntil);
        var nowEnd = FloorMinute(now).AddMinutes(1);
        notYetEnd = nowEnd < data.To ? nowEnd : data.To;
        if (notYetStart < notYetEnd) notYetTarget = (X(notYetStart), X(notYetEnd));
    }

    /// <summary>The listed slot under a point, or null.</summary>
    internal int? SlotAt(Point point)
    {
        EnsureLayout();
        return ChartFrame.Nearest(targets, point, PlotRect) is >= 0 and var index ? index : null;
    }

    /// <summary>Mouse moves call this; the checks call it directly. The chip lines follow at once.</summary>
    internal void HoverAt(Point? point)
    {
        EnsureLayout();
        int? index = ChartFrame.Nearest(targets, point, PlotRect) is >= 0 and var nearest ? nearest : null;
        bool notYet = index is null && point is { } p && PlotRect.Contains(p) && notYetTarget is { } span && p.X >= span.Left && p.X < span.Right;
        if (index == HoverIndex && notYet == hoverNotYet) return;
        bool slotChanged = index != HoverIndex;
        HoverIndex = index; hoverNotYet = notYet;
        ChipLines = index is { } slot ? SlotChip(slot) : notYet ? [Span(notYetStart, notYetEnd), "Not saved yet", "Minutes are saved after they end"] : [];
        InvalidateVisual();
        HoverChanged?.Invoke();
        if (slotChanged) ShownSlotChanged?.Invoke();
    }

    private string[] SlotChip(int index)
    {
        var data = strip!; var slot = data.Slots[index];
        var lines = new List<string> { Span(slot.Start, slot.End) };
        if (states[index] == SlotState.NoCapture) { lines.Add("No per-application capture"); return [.. lines]; }
        if (states[index] == SlotState.NotSaved) { lines.Add(NoSavedTraffic); lines.AddRange(reasons[index]!); return [.. lines]; }
        if (states[index] == SlotState.NoTraffic) lines.Add("No traffic");
        else if (slot.RecordedSeconds > 0)
        {
            long total = SlotTotal(slot, data.Applications.Count, data.Measure);
            lines.Add($"{Ui.Bytes(total)} · {Ui.Bytes((double)total / slot.RecordedSeconds, true)} average");
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
    internal string SlotSpan(int index) => Span(strip!.Slots[index].Start, strip.Slots[index].End);
    private string Span(DateTimeOffset start, DateTimeOffset end)
    {
        bool withDate = strip is { } data && data.To - data.From > TimeSpan.FromDays(1);
        DateTime first = TrafficStrips.CeilingMinute(start).ToLocalTime().DateTime, last = TrafficStrips.CeilingMinute(end).ToLocalTime().DateTime;
        static string Format(DateTime time, bool date) => time.ToString(date ? "MMM d HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
        return $"{Format(first, withDate)}–{Format(last, withDate && last.Date != first.Date)}";
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); HoverAt(e.GetPosition(this)); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); HoverAt(null); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!selectable) return;
        // Handled, so an enclosing ScrollViewer does not take the focus back and Left, Right and Enter stay the chart's.
        Focus(); HandleClick(e.GetPosition(this)); e.Handled = true;
    }
    internal void HandleClick(Point point)
    {
        if (!selectable || strip is not { } data || SlotAt(point) is not { } index) return;
        var slot = data.Slots[index];
        var at = TimeAt(point.X);
        if (at < slot.Start) at = slot.Start;
        if (at >= slot.End) at = slot.End.AddTicks(-1);
        SelectedIndex = index; SlotActivated?.Invoke(index, at);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (selectable && HandleKey(e.Key)) e.Handled = true;
    }
    internal bool HandleKey(Key key)
    {
        int count = strip?.Slots.Count ?? 0;
        if (!selectable || count == 0) return false;
        var slots = strip!.Slots;
        int? selected = selectedIndex is { } s ? Math.Clamp(s, 0, count - 1) : null;
        switch (key)
        {
            case Key.Left:
                if (selected is { } l) { if (l > 0) Move(l - 1); }
                else if (markTime is { } mark) { int last = -1; for (int i = 0; i < count; i++) if (slots[i].End <= mark) last = i; if (last >= 0) Move(last); }
                else Move(count - 1);
                return true;
            case Key.Right:
                if (selected is { } r) { if (r < count - 1) Move(r + 1); }
                else if (markTime is { } mark) { for (int i = 0; i < count; i++) if (slots[i].Start > mark) { Move(i); break; } }
                else Move(0);
                return true;
            case Key.Enter when selected is { } index:
                SlotActivated?.Invoke(index, slots[index].Start); return true;
            default: return false;
        }
    }
    private void Move(int index) { SelectedIndex = index; SelectionMoved?.Invoke(index); }
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        keyboardFocused = (bool)e.NewValue;
        if (selectable) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 160 || h < 100) return;
        EnsureLayout();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var plot = PlotRect;
        var typeface = Theme.ChartTypeface; double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        GraphDepth.Surface(dc, plot);
        if (selectable && keyboardFocused)
        {
            var focus = new Pen(Theme.Brush("Accent"), 1); focus.Freeze();
            var frame = plot; frame.Inflate(2, 2);
            dc.DrawRoundedRectangle(null, focus, frame, 6, 6);
        }
        if (message is not null) { Label(dc, message, plot.Left + 10, plot.Top + plot.Height / 2 - 7); return; }
        if (strip is not { } data) return;
        var hairlineBrush = Theme.Brush("Hairline");
        if (NotKept is { } notKept)
        {
            var band = hairlineBrush.Clone(); band.Opacity = NotKeptOpacity; band.Freeze();
            dc.DrawRectangle(band, null, notKept);
            if (notKept.Width >= 60 && BandLabel is { } label) Label(dc, label, notKept.Left + 8, notKept.Top + notKept.Height / 2 - 7);
        }
        if (Hatch is { } hatch) dc.DrawGeometry(HatchBrush(hairlineBrush), null, hatch);
        ChartFrame.Grid(dc, plot, new[] { 0d, .5, 1 }.Select(f => (Y(maximum * f), Ui.Bytes(maximum * f, true))).ToArray(), [], typeface, pixelsPerDip, false, null, nowLabel: null);
        foreach (var (series, full, geometry) in Bands)
        {
            var fill = (series < data.Applications.Count ? Theme.StripBrush(series) : Theme.Brush("Muted")).Clone();
            fill.Opacity = full ? FullOpacity : PartialOpacity; fill.Freeze();
            dc.DrawGeometry(fill, null, geometry);
        }
        if (guides is not null) dc.PushGuidelineSet(guides);
        if (Separators.Count > 0)
        {
            var panel = new Pen(Theme.Brush("Panel"), 1); panel.Freeze();
            foreach (var separator in Separators) dc.DrawGeometry(null, panel, separator);
        }
        var hairline = new Pen(hairlineBrush, 1); hairline.Freeze();
        if (Baseline is { } baseline) dc.DrawGeometry(null, hairline, baseline);
        if (guides is not null) dc.Pop();
        string DateLabel(DateTimeOffset time) => (data.To - data.From).TotalDays <= 2
            ? time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : time.ToLocalTime().ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        Label(dc, DateLabel(data.From), plot.Left, plot.Bottom + 6);
        string finish = DateLabel(data.To);
        Label(dc, finish, plot.Right - Formatted(finish).Width, plot.Bottom + 6);
        if (emptyText is not null) Label(dc, emptyText, plot.Left + 10, plot.Top + plot.Height / 2 - 7);
        if (selectable)
        {
            double? x = markTime is { } mark ? X(mark) : selectedIndex is { } picked && picked >= 0 && picked < targets.Count ? targets[picked].X : null;
            if (x is { } at)
            {
                var accent = new Pen(Theme.Brush("Accent"), 1); accent.Freeze();
                dc.DrawLine(accent, new(at, plot.Top), new(at, plot.Bottom));
            }
        }
        double? hoverX = HoverIndex is { } hovered && hovered < targets.Count ? targets[hovered].X
            : hoverNotYet && notYetTarget is { } span ? (span.Left + span.Right) / 2 : null;
        if (hoverX is { } hx && ChipLines.Count > 0)
        {
            dc.DrawLine(hairline, new(hx, plot.Top), new(hx, plot.Bottom));
            double y = HoverIndex is { } slot && Drawable(slot) ? Y(stackTops[slot]) : plot.Top + plot.Height / 2;
            var ink = Theme.Brush("Ink"); var muted = Theme.Brush("Muted");
            ChartFrame.Chip(dc, new(hx, y), plot, ChipLines.Select((line, i) => (line, i == 0 ? muted : ink)).ToArray(), typeface, pixelsPerDip, false);
        }
    }

    /// <summary>A frozen 6 × 6 DIP tile with one diagonal 1 px Hairline line.</summary>
    private static DrawingBrush HatchBrush(Brush hairline)
    {
        var pen = new Pen(hairline, 1); pen.Freeze();
        var line = new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 6), new Point(6, 0)));
        var tile = new Rect(0, 0, 6, 6);
        var brush = new DrawingBrush(line)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, Viewbox = tile, ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = tile, ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(Formatted(text), new(x, y));
    private FormattedText Formatted(string text) => ChartFrame.Text(text, Theme.ChartTypeface, VisualTreeHelper.GetDpi(this).PixelsPerDip, Theme.Brush("Muted"));
}

/// <summary>The strip's caption, chart, legend and the lines under it. While loaded it follows theme, graph-colour and high-contrast changes.</summary>
internal sealed class TrafficStripView : StackPanel
{
    private readonly UiSession session;
    private readonly TrafficStripPage page;
    private readonly TextBlock caption, notKept, notes;
    private IReadOnlyList<string> noteLines = [];
    private readonly WrapPanel legend;
    private readonly List<(string Text, string? Tip, Brush? Swatch, bool IsButton)> entries = [];
    // The series behind each legend entry's swatch (−1 for none), so a theme change re-reads the brushes in place,
    // and the entry and its label, so hover and selection update the texts in place.
    private readonly List<(int Series, Border? Swatch, FrameworkElement Entry, TextBlock Label)> swatches = [];
    private bool subscribed;

    internal TrafficStripView(UiSession session, TrafficStripPage page)
    {
        this.session = session; this.page = page;
        caption = Ui.Text("Traffic by application", 11, "Muted"); caption.Margin = new(0, 0, 0, 4);
        Chart = new TrafficStripChart(page);
        legend = new WrapPanel { Margin = new(0, 6, 0, 0) };
        notKept = Ui.Text("", 11, "Muted"); notKept.Margin = new(0, 2, 0, 0); notKept.Visibility = Visibility.Collapsed;
        notes = Ui.Text("", 11, "Muted"); notes.Margin = new(0, 2, 0, 0); notes.Visibility = Visibility.Collapsed;
        Children.Add(caption); Children.Add(Chart); Children.Add(legend); Children.Add(notKept); Children.Add(notes);
        Chart.ShownSlotChanged += UpdateLegend;
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    internal TrafficStripChart Chart { get; }
    internal IReadOnlyList<(string Text, string? Tip, Brush? Swatch, bool IsButton)> Legend => entries;
    internal string Caption => caption.Text;
    /// <summary>The retained-start line: "Not kept before {g}" (with " · Capacity shortened retention"), or "Cleared before {g}" after an
    /// all-apps clear; "" without a retained start.</summary>
    internal string NotKeptLine => notKept.Text;
    /// <summary>Every line under the legend, in order: <see cref="NotKeptLine"/> when set, the one-app clear's line, then the
    /// not-recorded filter's line.</summary>
    internal IReadOnlyList<string> NoteLines => noteLines;
    /// <summary>Raised with the application's key and name when its legend button is clicked (Connections only).</summary>
    internal event Action<string, string>? ApplicationChosen;

    /// <summary>Shows a strip; null shows "History unavailable". With <paramref name="filterNotRecorded"/> the strip is filtered to an
    /// application in Don't record these apps: its recorded zero-byte slots are Not saved and a line under the legend says why.</summary>
    internal void Apply(TrafficStrip? strip, DateTimeOffset dueUntil, DateTimeOffset now, bool filterNotRecorded = false)
    {
        if (strip is null) { ShowText("History unavailable"); return; }
        Chart.SetData(strip, dueUntil, now, filterNotRecorded);
        caption.Text = $"Traffic by application · {TrafficStrips.SlotLengthText(strip.BucketMilliseconds)} slots"
            + strip.Measure switch { TrafficMeasure.Received => " · received", TrafficMeasure.Sent => " · sent", _ => "" };
        string retainedLine = strip.RetainedFrom is { } retained
            ? (strip.RetainedByClear ? "Cleared before " : "Not kept before ") + retained.ToLocalTime().ToString("g")
                + (strip.CapacityShortened ? " · Capacity shortened retention" : "")
            : "";
        var further = new List<string>();
        if (strip.AppClearedBefore is { } cleared) further.Add($"Some application history before {cleared.ToLocalTime().ToString("g")} was cleared.");
        if (filterNotRecorded) further.Add("Not recorded: this app is in Don't record these apps.");
        SetNotes(retainedLine, further);
        RebuildLegend();
    }

    internal void ShowText(string text)
    {
        Chart.SetText(text);
        caption.Text = "Traffic by application";
        SetNotes("", []);
        RebuildLegend();
    }

    /// <summary>Sets the lines under the legend; a line's text and visibility change only when it differs.</summary>
    private void SetNotes(string retainedLine, IReadOnlyList<string> further)
    {
        if (notKept.Text != retainedLine) notKept.Text = retainedLine;
        var visibility = retainedLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notKept.Visibility != visibility) notKept.Visibility = visibility;
        string joined = string.Join("\n", further);
        if (notes.Text != joined) notes.Text = joined;
        visibility = further.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notes.Visibility != visibility) notes.Visibility = visibility;
        noteLines = retainedLine.Length > 0 ? [retainedLine, .. further] : [.. further];
    }

    private void Subscribe()
    {
        if (subscribed) return;
        subscribed = true;
        session.Changed += RefreshSwatches;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
    }
    private void Unsubscribe()
    {
        if (!subscribed) return;
        subscribed = false;
        session.Changed -= RefreshSwatches;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
    }
    // A Windows high-contrast switch re-applies the theme without Changed; Theme queues its Apply at normal priority, so this runs after it.
    private void SystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast) || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (subscribed) RefreshSwatches(); }));
    }

    /// <summary>Re-reads the swatch brushes in place and redraws the chart with the current theme.</summary>
    internal void RefreshSwatches()
    {
        Chart.InvalidateVisual();
        for (int i = 0; i < swatches.Count; i++)
        {
            var (series, swatch, _, _) = swatches[i];
            if (series < 0 || swatch is null) continue;
            var brush = SeriesBrush(series);
            swatch.Background = brush;
            var entry = entries[i]; entry.Swatch = brush; entries[i] = entry;
        }
    }

    private Brush SeriesBrush(int series) => series < (Chart.Strip?.Applications.Count ?? 0) ? Theme.StripBrush(series) : Theme.Brush("Muted");

    /// <summary>Rebuilds the legend's entries; only Apply and ShowText change which entries there are.</summary>
    private void RebuildLegend()
    {
        legend.Children.Clear(); entries.Clear(); swatches.Clear();
        foreach (var (text, tip, series, app) in LegendRows())
            Add(text, tip, series, page == TrafficStripPage.Connections && app is not null, app);
    }

    /// <summary>Hover and selection change only the texts and tooltips: the entries stay, and so does keyboard focus on a legend button.</summary>
    private void UpdateLegend()
    {
        var rows = LegendRows();
        if (rows.Count != swatches.Count) { RebuildLegend(); return; }
        for (int i = 0; i < rows.Count; i++)
        {
            var (text, tip, _, _) = rows[i];
            var (_, _, entry, label) = swatches[i];
            label.Text = text; entry.ToolTip = tip;
            var shown = entries[i]; shown.Text = text; shown.Tip = tip; entries[i] = shown;
        }
    }

    private List<(string Text, string? Tip, int Series, TrafficStripApplication? App)> LegendRows()
    {
        var rows = new List<(string Text, string? Tip, int Series, TrafficStripApplication? App)>();
        if (Chart.Strip is not { } strip) return rows;
        var measure = strip.Measure;
        int apps = strip.Applications.Count;
        long otherTotal = TrafficStrips.Bytes(strip.OtherReceived, strip.OtherSent, measure);
        double denominator = strip.Applications.Sum(a => (double)TrafficStrips.Bytes(a.Received, a.Sent, measure)) + otherTotal;
        if (denominator <= 0)
        {
            rows.Add((page == TrafficStripPage.Connections ? "No application traffic recorded in this interval." : "No application traffic recorded in this period.", null, -1, null));
            return rows;
        }
        int? shown = Chart.ShownIndex;
        var slot = shown is { } index ? strip.Slots[index] : null;
        rows.Add((shown is { } k ? Chart.SlotSpan(k) : page == TrafficStripPage.Connections ? "Totals for this interval" : "Totals for this period", null, -1, null));
        for (int series = 0; series < apps + (otherTotal > 0 ? 1 : 0); series++)
        {
            var app = series < apps ? strip.Applications[series] : null;
            string name = app?.Name ?? $"Other ({strip.OtherApplications:N0} more)";
            long received = app?.Received ?? strip.OtherReceived, sent = app?.Sent ?? strip.OtherSent;
            string text, tip;
            if (slot is null)
            {
                long bytes = TrafficStrips.Bytes(received, sent, measure);
                text = $"{name} · {Ui.Bytes(bytes)} · {(bytes / denominator * 100).ToString("0.#", CultureInfo.CurrentCulture)}%";
                tip = $"{name} · ↓ {Ui.Bytes(received)} · ↑ {Ui.Bytes(sent)}";
            }
            else if (Chart.NotSavedReasons(shown!.Value) is { } why)
            {
                // A Not saved slot: no value, with the chip's reason line as the tooltip.
                text = $"{name} · —";
                tip = string.Join("\n", why);
            }
            else if (slot.RecordedSeconds == 0)
            {
                text = $"{name} · —";
                tip = page == TrafficStripPage.UsageHistory ? "No per-application capture in this slot" : "Under a second recorded in this slot";
            }
            else
            {
                text = $"{name} · {Ui.Bytes(TrafficStrips.Bytes(slot, series, measure))}";
                tip = $"{name} · ↓ {Ui.Bytes(slot.Received[series])} · ↑ {Ui.Bytes(slot.Sent[series])}";
            }
            rows.Add((text, tip, series, app));
        }
        return rows;
    }

    private void Add(string text, string? tip, int series, bool isButton, TrafficStripApplication? app)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, series < 0 ? "Muted" : "Ink");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "BodyFont");
        var content = new DockPanel { LastChildFill = true };
        Brush? brush = null; Border? swatch = null;
        if (series >= 0)
        {
            brush = SeriesBrush(series);
            swatch = new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = brush, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(swatch, Dock.Left); content.Children.Add(swatch);
        }
        content.Children.Add(label);
        FrameworkElement entry;
        if (isButton && app is not null)
        {
            var button = new Button { Content = content, Padding = new(6, 2, 6, 2) };
            AutomationProperties.SetName(button, $"{app.Name}: show only this application");
            button.Click += (_, _) => ApplicationChosen?.Invoke(app.Key, app.Name);
            entry = button;
        }
        else entry = new Border { Child = content, Padding = new(6, 2, 6, 2) };
        entry.MaxWidth = 240; entry.Margin = new(0, 0, 8, 4);
        if (tip is not null) entry.ToolTip = tip;
        legend.Children.Add(entry);
        entries.Add((text, tip, brush, isButton));
        swatches.Add((series, swatch, entry, label));
    }
}
