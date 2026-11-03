using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Actual-time thermal history. Missing values and collection boundaries break the trace.</summary>
internal sealed class ThermalHistoryChart : FrameworkElement
{
    private IReadOnlyList<ThermalWindow> windows = [];
    private string metric = "temperature";
    private DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow;

    public ThermalHistoryChart()
    {
        ClipToBounds = true;
        MinHeight = 180;
        System.Windows.Automation.AutomationProperties.SetName(this, "Thermal history graph");
    }

    public void SetData(IReadOnlyList<ThermalWindow> data, string selectedMetric, DateTimeOffset start, DateTimeOffset end)
    {
        windows = data.OrderBy(w => w.Start).ToArray();
        metric = selectedMetric;
        from = start;
        to = end;
        InvalidateVisual();
    }

    internal static MetricAggregate Metric(ThermalWindow window, string name) => name switch
    {
        "load" => window.Load,
        "power" => window.Power,
        "clock" => window.Clock,
        "fan" => window.FanRpm,
        _ => window.Temperature
    };

    internal static string Unit(string name) => name switch
    {
        "load" => "%",
        "power" => "W",
        "clock" => "MHz",
        "fan" => "RPM",
        _ => "°C"
    };

    internal static bool CanConnect(ThermalWindow previous, ThermalWindow next, string name)
    {
        bool Covered(ThermalWindow window)
        {
            var value = Metric(window, name);
            return value.Count > 0 && value.Count == window.SampleCount &&
                value.Average is double average && double.IsFinite(average) &&
                window.DurationSeconds >= Math.Max(1, (window.End - window.Start).TotalSeconds * .9);
        }
        double maximumGap=Math.Max(4,Math.Max(PollingPolicy.Normalize(previous.PollingIntervalSeconds),PollingPolicy.Normalize(next.PollingIntervalSeconds))+3);
        return Covered(previous) && Covered(next) &&
            previous.Device.Id == next.Device.Id && previous.Device.ConfigurationId == next.Device.ConfigurationId &&
            previous.SessionId == next.SessionId && previous.ResolutionSeconds == next.ResolutionSeconds &&
            next.Start >= previous.End && (next.Start - previous.End).TotalSeconds <= maximumGap &&
            next.StartMonotonic >= previous.StartMonotonic + previous.DurationSeconds &&
            next.StartMonotonic - previous.StartMonotonic - previous.DurationSeconds <= maximumGap &&
            Math.Abs((next.Start-previous.End).TotalSeconds-(next.StartMonotonic-previous.StartMonotonic-previous.DurationSeconds))<=3;
    }

    internal Rect PlotRect { get; private set; }
    /// <summary>Index into the ordered windows of the window under the pointer, or -1.</summary>
    internal int HoverIndex { get; private set; } = -1;
    private Point? hover;
    private readonly List<(double X, double Tolerance)> targets = [];
    private readonly List<int> targetWindows = [];
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e) { base.OnMouseMove(e); SetHover(e.GetPosition(this)); }
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { base.OnMouseLeave(e); SetHover(null); }
    internal void SetHover(Point? position)
    {
        hover = position; int previous = HoverIndex; ResolveHover();
        if (HoverIndex != previous) InvalidateVisual();
    }
    private void ResolveHover() { int target = ChartFrame.Nearest(targets, hover, PlotRect); HoverIndex = target < 0 ? -1 : targetWindows[target]; }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 180 || ActualHeight < 100 || to <= from) return;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        const double left = 12, top = 15, bottom = 32, right = 8;
        var valid = windows.Where(w => w.End >= from && w.Start <= to)
            .Where(w => Metric(w, metric).Average is double mean && double.IsFinite(mean)).ToArray();
        double maximum = metric == "load" ? 100 : Math.Max(metric == "temperature" ? 40 : 1,
            valid.Select(w => Metric(w, metric).Maximum ?? Metric(w, metric).Average!.Value).DefaultIfEmpty(0).Max() * 1.12);
        string Axis(double value) => $"{value:0.#} {Unit(metric)}";
        double width = ActualWidth - left - right, height = ActualHeight - top - bottom;
        if (width < 30 || height < 30) return;
        var plot = new Rect(left, top, width, height); PlotRect = plot;
        var typeface = Theme.ChartTypeface; double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        GraphDepth.Surface(drawing, plot);
        ChartFrame.Grid(drawing, plot, Enumerable.Range(0, 3).Select(i => (top + height * (1 - i / 2.0), Axis(maximum * i / 2))).ToArray(), [], typeface, pixelsPerDip, false, null, nowLabel: null);
        var startLabel = Text(from.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture));
        var endLabel = Text(to.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture));
        drawing.DrawText(startLabel, new(left, top + height + 12));
        drawing.DrawText(endLabel, new(left + width - endLabel.Width, top + height + 12));
        targets.Clear(); targetWindows.Clear();
        if (valid.Length == 0)
        {
            HoverIndex = -1;
            drawing.DrawText(Text("No available readings in this range"), new(left + 12, top + height / 2));
            return;
        }

        var color = Theme.Brush("Accent");
        var seriesPen = new Pen(color, 1.8) { LineJoin = PenLineJoin.Round };
        var spread = color.Clone(); spread.Opacity = .25;
        var spreadPen = new Pen(spread, 1);
        ThermalWindow? previous = null;
        double Y(double value) => top + height * (1 - Math.Clamp(value / maximum, 0, 1));
        var runs = new List<IReadOnlyList<Point>>(); List<Point>? run = null;
        var points = new Dictionary<int, Point>();
        for (int index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            if (window.End < from || window.Start > to) continue;
            var aggregate = Metric(window, metric);
            if (aggregate.Average is not double value || !double.IsFinite(value) || aggregate.Count <= 0)
            {
                previous = null; run = null; continue;
            }
            double middle = ((window.Start + (window.End - window.Start) / 2) - from).TotalSeconds;
            var point = new Point(left + width * Math.Clamp(middle / (to - from).TotalSeconds, 0, 1), Y(value));
            points[index] = point;
            targets.Add((point.X, Math.Max(6, width * (window.End - window.Start).TotalSeconds / (to - from).TotalSeconds / 2))); targetWindows.Add(index);
            if (aggregate.Minimum is double minimum && aggregate.Maximum is double upper && double.IsFinite(minimum) && double.IsFinite(upper))
                drawing.DrawLine(spreadPen, new(point.X, Y(minimum)), new(point.X, Y(upper)));
            if (run is null || previous is null || !CanConnect(previous, window, metric)) { run = new(); runs.Add(run); }
            run.Add(point);
            previous = window;
        }
        var path = ChartFrame.Spline(runs);
        if (!path.IsEmpty())
        {
            var copy = PathGeometry.CreateFromGeometry(path); copy.Freeze();
            GraphDepth.UnderGlow(drawing, copy, "Accent", 1.8);
            drawing.DrawGeometry(null, seriesPen, path);
        }
        double markerRadius = valid.Length > 500 ? 1 : 1.8;
        foreach (var point in points.Values) drawing.DrawEllipse(color, null, point, markerRadius, markerRadius);
        ResolveHover();
        if (HoverIndex >= 0 && points.TryGetValue(HoverIndex, out var hovered))
        {
            var window = windows[HoverIndex];
            var hairline = new Pen(Theme.Brush("Hairline"), 1); hairline.Freeze();
            drawing.DrawLine(hairline, new(hovered.X, top), new(hovered.X, top + height));
            drawing.DrawEllipse(color, null, hovered, 3, 3);
            ChartFrame.Chip(drawing, hovered, plot, [(window.Start.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture), Theme.Brush("Muted")), (Axis(Metric(window, metric).Average!.Value), color)], typeface, pixelsPerDip, false);
        }
    }

    private FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        Theme.ChartTypeface, 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
