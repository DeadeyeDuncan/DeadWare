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

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 160 || h < 120) return;
        const double left = 12, right = 78, top = 12, bottom = 30;
        double width = w - left - right, height = h - top - bottom;
        var plot = new Rect(left, top, width, height);
        GraphDepth.Surface(dc, plot);
        var first = traces.FirstOrDefault();
        if (first is null) return;
        var from = first.View.From; var to = first.View.To;
        double seconds = Math.Max(60, (to - from).TotalSeconds);
        double maximum = fixedMaximum ?? Math.Max(1, traces.SelectMany(t => t.View.Buckets).Select(b => b.Maximum).DefaultIfEmpty(0).Max() * 1.15);
        if (fixedMaximum is null && unit == "°C") maximum = Math.Max(maximum, 40);
        double X(DateTimeOffset time) => left + width * Math.Clamp((time - from).TotalSeconds / seconds, 0, 1);
        double Y(double value) => top + height * (1 - Math.Clamp(value / maximum, 0, 1));
        var grid = new Pen(Theme.Brush("Line"), 1) { DashStyle = DashStyles.Dot };
        for (int i = 0; i <= 4; i++)
        {
            double value = maximum * i / 4, y = Y(value);
            dc.DrawLine(grid, new(left, y), new(left + width, y));
            Label(dc, Format(value), left + width + 8, y - 7);
        }
        bool any = false;
        foreach (var trace in traces)
        {
            var color = Theme.Brush(trace.ColorKey);
            var band = color.Clone(); band.Opacity = 0.18; band.Freeze();
            var pen = new Pen(color, 1.8) { DashStyle = trace.Dashed ? DashStyles.Dash : DashStyles.Solid, DashCap = PenLineCap.Round };
            long bucketWidth = trace.View.BucketMilliseconds;
            UsageHistoryBucket? previous = null;
            foreach (var bucket in trace.View.Buckets)
            {
                any = true;
                double x0 = X(bucket.Start), x1 = Math.Max(x0 + 1, X(bucket.End)), mid = (x0 + x1) / 2;
                dc.DrawRectangle(band, null, new Rect(x0, Y(bucket.Maximum), x1 - x0, Math.Max(1, Y(bucket.Minimum) - Y(bucket.Maximum))));
                if (previous is not null && CanConnect(previous, bucket, bucketWidth))
                {
                    var start = new Point((X(previous.Start) + X(previous.End)) / 2, Y(previous.Average));
                    var end = new Point(mid, Y(bucket.Average));
                    GraphDepth.LineShadow(dc, start, end, pen);
                    dc.DrawLine(pen, start, end);
                }
                else dc.DrawEllipse(color, null, new(mid, Y(bucket.Average)), 1.6, 1.6);
                previous = bucket;
            }
        }
        string DateLabel(DateTimeOffset time) => (to - from).TotalDays <= 2
            ? time.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)
            : time.ToLocalTime().ToString("MMM d yyyy", CultureInfo.CurrentCulture);
        Label(dc, DateLabel(from), left, top + height + 8);
        string finish = DateLabel(to);
        Label(dc, finish, left + width - Formatted(finish).Width, top + height + 8);
        if (!any) Label(dc, "No history in this range yet", left + 10, top + height / 2 - 7);
    }
    private string Format(double value) => unit switch
    {
        "%" => $"{value:0}%",
        "°C" => $"{value:0}°C",
        "B/s" => Ui.Bytes(value, true),
        _ => $"{value:0.#} {unit}",
    };
    private void Label(DrawingContext dc, string text, double x, double y) => dc.DrawText(Formatted(text), new(x, y));
    private FormattedText Formatted(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Theme.ChartTypeface, 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
