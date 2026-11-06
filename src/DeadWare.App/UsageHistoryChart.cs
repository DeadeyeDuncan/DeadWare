using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>One series on the usage chart: aligned buckets drawn as an average line over a min/max band.</summary>
internal sealed record UsageTrace(UsageHistoryView View, string ColorKey, bool Dashed, string Label);

/// <summary>Average line plus min/max band per bucket. Missing slots and partial buckets never get a line drawn across them.</summary>
internal sealed class UsageHistoryChart : FrameworkElement
{
    private IReadOnlyList<UsageTrace> traces = [];
    private string unit = "%";
    private double? fixedMaximum = 100;
    public UsageHistoryChart()
    {
        ClipToBounds = true; MinHeight = 150;
        System.Windows.Automation.AutomationProperties.SetName(this, "Usage history chart");
    }
    public void SetData(IReadOnlyList<UsageTrace> series, string unitLabel, double? maximum)
    {
        traces = series; unit = unitLabel; fixedMaximum = maximum;
        System.Windows.Automation.AutomationProperties.SetName(this, string.Join(" and ", series.Select(s => s.Label)) + " history");
        InvalidateVisual();
    }
    /// <summary>A bucket counts as full when every minute of its slot was recorded; only adjacent full buckets connect.</summary>
    internal static bool IsFull(UsageHistoryBucket bucket, long bucketMilliseconds)
        => bucket.Minutes >= Math.Max(1, (bucket.End - bucket.Start).TotalMilliseconds / 60_000 - 0.5);
    internal static bool CanConnect(UsageHistoryBucket a, UsageHistoryBucket b, long bucketMilliseconds)
        => IsFull(a, bucketMilliseconds) && IsFull(b, bucketMilliseconds) && b.Start == a.End;

    /// <summary>When true, % values under 10 keep one decimal ("0.4%"): an app's share of all logical processors is often under 1 %.
    /// Off by default, so the Device charts keep whole percents.</summary>
    public bool TenthsBelowTenPercent { get; init; }

    private bool selectable, keyboardFocused;
    private int? selectedIndex;
    /// <summary>When true the chart takes focus, a click or Enter activates a bucket and Left/Right move <see cref="SelectedIndex"/>. Off by default.
    /// While it has keyboard focus it draws an Accent frame in place of the default focus rectangle.</summary>
    public bool Selectable
    {
        get => selectable;
        set { if (selectable == value) return; selectable = value; Focusable = value; FocusVisualStyle = null; InvalidateVisual(); }
    }
    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        keyboardFocused = (bool)e.NewValue;
        if (selectable) InvalidateVisual();
    }
    /// <summary>Index into the first trace's buckets drawn with an accent hairline; null draws nothing. Ignored unless <see cref="Selectable"/>.</summary>
    public int? SelectedIndex
    {
        get => selectedIndex;
        set { if (selectedIndex == value) return; selectedIndex = value; InvalidateVisual(); }
    }
    /// <summary>Raised with the bucket index when a bucket is clicked or Enter is pressed on the selection.</summary>
    public event Action<int>? BucketActivated;
    /// <summary>Index of the bucket under a point from the last render, or null when the point is over no bucket.</summary>
    internal int? BucketAt(Point point) => ChartFrame.Nearest(targets, point, PlotRect) is >= 0 and var index ? index : null;
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!selectable) return;
        // Handled, so an enclosing ScrollViewer does not take the focus back and Left, Right and Enter stay the chart's.
        Focus(); HandleClick(e.GetPosition(this)); e.Handled = true;
    }
    internal void HandleClick(Point point)
    {
        if (!selectable || BucketAt(point) is not { } index) return;
        SelectedIndex = index; BucketActivated?.Invoke(index);
    }
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (selectable && HandleKey(e.Key)) e.Handled = true;
    }
    internal bool HandleKey(System.Windows.Input.Key key)
    {
        int count = traces.FirstOrDefault()?.View.Buckets.Count ?? 0;
        if (!selectable || count == 0) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Left: SelectedIndex = selectedIndex is { } l ? Math.Max(0, Math.Min(l, count) - 1) : count - 1; return true;
            case System.Windows.Input.Key.Right: SelectedIndex = selectedIndex is { } r ? Math.Min(count - 1, r + 1) : 0; return true;
            case System.Windows.Input.Key.Enter when selectedIndex is { } index && index < count: BucketActivated?.Invoke(index); return true;
            default: return false;
        }
    }

    internal Rect PlotRect { get; private set; }
    /// <summary>Index into the first trace's buckets of the bucket under the pointer, or -1.</summary>
    internal int HoverIndex { get; private set; } = -1;
    private Point? hover;
    private readonly List<(double X, double Tolerance)> targets = [];
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e) { base.OnMouseMove(e); SetHover(e.GetPosition(this)); }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { base.OnMouseLeave(e); SetHover(null); }
    internal void SetHover(Point? position)
    {
        hover = position; int previous = HoverIndex; HoverIndex = ChartFrame.Nearest(targets, hover, PlotRect);
        if (HoverIndex != previous) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 160 || h < 120) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        // A Tab stop shows keyboard focus with an Accent frame, as the app's other focusable controls do.
        if (selectable && keyboardFocused)
        {
            var focus = new Pen(Theme.Brush("Accent"), 1); focus.Freeze();
            dc.DrawRoundedRectangle(null, focus, new Rect(0.5, 0.5, w - 1, h - 1), 7, 7);
        }
        const double left = 12, right = 8, top = 12, bottom = 30;
        double width = w - left - right, height = h - top - bottom;
        var plot = new Rect(left, top, width, height); PlotRect = plot;
        var typeface = Theme.ChartTypeface; double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        GraphDepth.Surface(dc, plot);
        var first = traces.FirstOrDefault();
        if (first is null) { targets.Clear(); HoverIndex = -1; return; }
        var from = first.View.From; var to = first.View.To;
        double seconds = Math.Max(60, (to - from).TotalSeconds);
        double maximum = fixedMaximum ?? Math.Max(1, traces.SelectMany(t => t.View.Buckets).Select(b => b.Maximum).DefaultIfEmpty(0).Max() * 1.15);
        if (fixedMaximum is null && unit == "°C") maximum = Math.Max(maximum, 40);
        double X(DateTimeOffset time) => left + width * Math.Clamp((time - from).TotalSeconds / seconds, 0, 1);
        double Y(double value) => top + height * (1 - Math.Clamp(value / maximum, 0, 1));
        ChartFrame.Grid(dc, plot, Enumerable.Range(0, 5).Select(i => (Y(maximum * i / 4), Format(maximum * i / 4))).ToArray(), [], typeface, pixelsPerDip, false, null, nowLabel: null);
        targets.Clear();
        bool any = false;
        foreach (var trace in traces)
        {
            var color = Theme.Brush(trace.ColorKey);
            var band = color.Clone(); band.Opacity = 0.18; band.Freeze();
            var pen = new Pen(color, 1.8) { DashStyle = trace.Dashed ? DashStyles.Dash : DashStyles.Solid, DashCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            long bucketWidth = trace.View.BucketMilliseconds;
            var runs = new List<IReadOnlyList<Point>>(); List<Point>? run = null;
            UsageHistoryBucket? previous = null;
            foreach (var bucket in trace.View.Buckets)
            {
                any = true;
                double x0 = X(bucket.Start), x1 = Math.Max(x0 + 1, X(bucket.End)), mid = (x0 + x1) / 2;
                // Only the bucket's own width counts, so a missing neighbour never borrows a value.
                if (ReferenceEquals(trace, first)) targets.Add((mid, Math.Max(1, (x1 - x0) / 2)));
                dc.DrawRectangle(band, null, new Rect(x0, Y(bucket.Maximum), x1 - x0, Math.Max(1, Y(bucket.Minimum) - Y(bucket.Maximum))));
                var point = new Point(mid, Y(bucket.Average));
                if (run is null || previous is null || !CanConnect(previous, bucket, bucketWidth)) { run = new(); runs.Add(run); }
                run.Add(point);
                previous = bucket;
            }
            // Runs of adjacent full buckets become one spline each; a lone bucket stays a dot.
            foreach (var lone in runs.Where(r => r.Count == 1)) dc.DrawEllipse(color, null, lone[0], 1.6, 1.6);
            var path = ChartFrame.Spline(runs);
            if (!path.IsEmpty())
            {
                var copy = PathGeometry.CreateFromGeometry(path); copy.Freeze();
                GraphDepth.UnderGlow(dc, copy, trace.ColorKey, 1.8);
                dc.DrawGeometry(null, pen, path);
            }
        }
        string DateLabel(DateTimeOffset time) => (to - from).TotalDays <= 2
            ? time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : time.ToLocalTime().ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        Label(dc, DateLabel(from), left, top + height + 8);
        string finish = DateLabel(to);
        Label(dc, finish, left + width - Formatted(finish).Width, top + height + 8);
        if (!any) Label(dc, "No history in this range yet", left + 10, top + height / 2 - 7);
        if (selectable && selectedIndex is { } picked && picked >= 0 && picked < targets.Count)
        {
            var accent = new Pen(Theme.Brush("Accent"), 1); accent.Freeze();
            dc.DrawLine(accent, new(targets[picked].X, top), new(targets[picked].X, top + height));
        }
        HoverIndex = ChartFrame.Nearest(targets, hover, plot);
        if (HoverIndex >= 0 && HoverIndex < first.View.Buckets.Count)
        {
            var bucket = first.View.Buckets[HoverIndex];
            double x = targets[HoverIndex].X;
            var hairline = new Pen(Theme.Brush("Hairline"), 1); hairline.Freeze();
            dc.DrawLine(hairline, new(x, top), new(x, top + height));
            var lines = new List<(string Text, Brush Color)> { (DateLabel(bucket.Start), Theme.Brush("Muted")) };
            Point? anchor = null;
            foreach (var trace in traces)
            {
                var match = trace.View.Buckets.FirstOrDefault(b => b.Start == bucket.Start);
                if (match is null) continue;
                var brush = Theme.Brush(trace.ColorKey); var point = new Point(x, Y(match.Average));
                dc.DrawEllipse(brush, null, point, 3, 3); anchor ??= point;
                lines.Add((Format(match.Average), brush));
            }
            if (anchor is { } at) ChartFrame.Chip(dc, at, plot, lines, typeface, pixelsPerDip, false);
        }
    }
    internal string Format(double value) => unit switch
    {
        "%" => TenthsBelowTenPercent && Math.Abs(value) < 10 ? $"{value:0.#}%" : $"{value:0}%",
        "°C" => $"{value:0}°C",
        "B/s" => Ui.Bytes(value, true),
        "B" => Ui.Bytes(value),
        _ => $"{value:0.#} {unit}",
    };
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(Formatted(text), new(x, y));
    private FormattedText Formatted(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Theme.ChartTypeface, 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
