using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Fixed percentage scale; ambiguous long-range buckets remain independent ranges and state marks.</summary>
internal sealed class BatteryHistoryChart : FrameworkElement
{
    private BatteryHistoryView? view;
    public BatteryHistoryChart()
    {
        ClipToBounds = true; MinHeight = 220;
        System.Windows.Automation.AutomationProperties.SetName(this, "Battery percentage, charging and battery saver history");
    }
    public void SetData(BatteryHistoryView data) { view = data; InvalidateVisual(); }
    internal static bool CanConnect(BatteryHistoryBucket a, BatteryHistoryBucket b) =>
        a.Samples == 1 && b.Samples == 1 && a.ValidSamples == 1 && b.ValidSamples == 1 &&
        a.Average.HasValue && b.Average.HasValue && a.Segment is not null && a.Segment == b.Segment &&
        b.Start > a.End && b.Start - a.End <= TimeSpan.FromSeconds(90);

    internal Rect PlotRect { get; private set; }
    /// <summary>Index into the view's buckets of the bucket under the pointer, or -1.</summary>
    internal int HoverIndex { get; private set; } = -1;
    private Point? hover;
    private readonly List<(double X, double Tolerance)> targets = [];
    private readonly List<int> targetBuckets = [];
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e) { base.OnMouseMove(e); SetHover(e.GetPosition(this)); }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { base.OnMouseLeave(e); SetHover(null); }
    internal void SetHover(Point? position)
    {
        hover = position; int previous = HoverIndex; ResolveHover();
        if (HoverIndex != previous) InvalidateVisual();
    }
    private void ResolveHover() { int target = ChartFrame.Nearest(targets, hover, PlotRect); HoverIndex = target < 0 ? -1 : targetBuckets[target]; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 180 || h < 180 || view is null) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const double left = 12, right = 90, top = 18, bottom = 90;
        double width = w - left - right, height = h - top - bottom;
        double seconds = Math.Max(60, (view.To - view.From).TotalSeconds);
        double X(DateTimeOffset time) => left + width * Math.Clamp((time - view.From).TotalSeconds / seconds, 0, 1);
        double Y(double value) => top + height * (1 - Math.Clamp(value, 0, 100) / 100);
        var plot = new Rect(left, top, width, height); PlotRect = plot;
        var typeface = Theme.ChartTypeface; double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        GraphDepth.Surface(dc, plot);
        ChartFrame.Grid(dc, plot, Enumerable.Range(0, 5).Select(i => (Y(i * 25), $"{i * 25}%")).ToArray(), [], typeface, pixelsPerDip, false, null, nowLabel: null);
        targets.Clear(); targetBuckets.Clear();
        BatteryHistoryBucket? previous = null;
        for (int index = 0; index < view.Buckets.Count; index++)
        {
            var bucket = view.Buckets[index];
            double x = X(bucket.Start + (bucket.End - bucket.Start) / 2);
            if (bucket.Average is { } average)
            {
                targets.Add((x, Math.Max(6, (X(bucket.End) - X(bucket.Start)) / 2))); targetBuckets.Add(index);
                var charge = ChargePen(bucket);
                if (previous is not null && CanConnect(previous, bucket))
                {
                    // The earlier observation's state lasts until the next recorded change.
                    var previousPen = ChargePen(previous);
                    var start = new Point(X(previous.Start), Y(previous.Average!.Value));
                    var end = new Point(x, Y(average));
                    dc.DrawLine(previousPen, start, end);
                }
                if (bucket.Minimum is { } minimum && bucket.Maximum is { } maximum)
                    dc.DrawLine(new Pen(charge.Brush, 1), new(x, Y(minimum)), new(x, Y(maximum)));
                bool certain = bucket.SaverKnownSamples == bucket.Samples && (bucket.SaverSamples == 0 || bucket.SaverSamples == bucket.Samples);
                dc.DrawEllipse(certain ? charge.Brush : null, certain ? null : new Pen(charge.Brush, 1.3), new(x, Y(average)), 2, 2);
            }
            Mark(dc, bucket.ChargingSamples, bucket.Samples, x, top + height + 34, "Cpu");
            Mark(dc, bucket.SaverSamples, bucket.Samples, x, top + height + 52, "Warning");
            // Unknown percentage cannot be bridged even if state metadata is available.
            previous = bucket;
        }
        string DateLabel(DateTimeOffset time) => (view.To - view.From).TotalDays <= 2
            ? time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : time.ToLocalTime().ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        Label(dc, DateLabel(view.From), left, top + height + 8);
        string finish = DateLabel(view.To);
        Label(dc, finish, left + width - Formatted(finish).Width, top + height + 8);
        Label(dc, "Charging", left + width + 8, top + height + 30);
        Label(dc, "Saver", left + width + 8, top + height + 48);
        if (!view.Buckets.Any(b => b.ValidSamples > 0)) Label(dc, "No charge readings in this range yet", left + 10, top + height / 2);
        ResolveHover();
        if (HoverIndex >= 0 && view.Buckets[HoverIndex].Average is { } hovered)
        {
            var bucket = view.Buckets[HoverIndex];
            double x = X(bucket.Start + (bucket.End - bucket.Start) / 2), y = Y(hovered);
            var hairline = new Pen(Theme.Brush("Hairline"), 1); hairline.Freeze();
            dc.DrawLine(hairline, new(x, top), new(x, top + height));
            var brush = ChargePen(bucket).Brush;
            dc.DrawEllipse(brush, null, new(x, y), 3, 3);
            ChartFrame.Chip(dc, new(x, y), plot, [(bucket.Start.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture), Theme.Brush("Muted")), ($"{hovered:0.#}%", brush)], typeface, pixelsPerDip, false);
        }
    }
    private static Pen ChargePen(BatteryHistoryBucket bucket)
    {
        bool known = bucket.SaverKnownSamples == bucket.Samples;
        return new Pen(Theme.Brush(!known ? "Muted" : bucket.SaverSamples > 0 ? "Warning" : "Accent"), 1.8)
        { DashStyle = !known ? DashStyles.Dot : bucket.SaverSamples > 0 ? DashStyles.Dash : DashStyles.Solid };
    }
    private static void Mark(DrawingContext dc, long enabled, long count, double x, double y, string color)
    {
        if (enabled == 0) return;
        var brush = Theme.Brush(color);
        dc.DrawRectangle(enabled == count ? brush : null, new Pen(brush, 1), new Rect(x - 2, y, 4, 7));
    }
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(Formatted(text), new(x, y));
    private FormattedText Formatted(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Theme.ChartTypeface, 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
