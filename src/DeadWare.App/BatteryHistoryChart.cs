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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 180 || h < 180 || view is null) return;
        const double left = 12, right = 90, top = 18, bottom = 90;
        double width = w - left - right, height = h - top - bottom;
        double seconds = Math.Max(60, (view.To - view.From).TotalSeconds);
        double X(DateTimeOffset time) => left + width * Math.Clamp((time - view.From).TotalSeconds / seconds, 0, 1);
        double Y(double value) => top + height * (1 - Math.Clamp(value, 0, 100) / 100);
        for (int i = 0; i <= 4; i++)
        {
            double y = Y(i * 25);
            dc.DrawLine(new Pen(Theme.Brush("Line"), 1) { DashStyle = DashStyles.Dot }, new(left, y), new(left + width, y));
            Label(dc, $"{i * 25}%", left + width + 8, y - 7);
        }
        BatteryHistoryBucket? previous = null;
        foreach (var bucket in view.Buckets)
        {
            double x = X(bucket.Start + (bucket.End - bucket.Start) / 2);
            if (bucket.Average is { } average)
            {
                var charge = ChargePen(bucket);
                if (previous is not null && CanConnect(previous, bucket))
                    // The earlier observation's state lasts until the next recorded change.
                    dc.DrawLine(ChargePen(previous), new(X(previous.Start), Y(previous.Average!.Value)), new(x, Y(average)));
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
        new Typeface("Segoe UI"), 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
