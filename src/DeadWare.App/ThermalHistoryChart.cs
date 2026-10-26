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
        return Covered(previous) && Covered(next) &&
            previous.Device.Id == next.Device.Id && previous.Device.ConfigurationId == next.Device.ConfigurationId &&
            previous.SessionId == next.SessionId && previous.ResolutionSeconds == next.ResolutionSeconds &&
            next.Start >= previous.End && (next.Start - previous.End).TotalSeconds <= 4 &&
            next.StartMonotonic >= previous.StartMonotonic + previous.DurationSeconds &&
            next.StartMonotonic - previous.StartMonotonic - previous.DurationSeconds <= 4;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth < 180 || ActualHeight < 100 || to <= from) return;
        const double left = 12, top = 15, bottom = 32;
        var valid = windows.Where(w => w.End >= from && w.Start <= to)
            .Where(w => Metric(w, metric).Average is double mean && double.IsFinite(mean)).ToArray();
        double maximum = metric == "load" ? 100 : Math.Max(metric == "temperature" ? 40 : 1,
            valid.Select(w => Metric(w, metric).Maximum ?? Metric(w, metric).Average!.Value).DefaultIfEmpty(0).Max() * 1.12);
        string Axis(double value) => $"{value:0.#} {Unit(metric)}";
        double right = Math.Max(56, Text(Axis(maximum)).Width + 14);
        double width = ActualWidth - left - right, height = ActualHeight - top - bottom;
        if (width < 30 || height < 30) return;
        for (int i = 0; i <= 2; i++)
        {
            double value = maximum * i / 2, y = top + height * (1 - value / maximum);
            drawing.DrawLine(new Pen(Theme.Brush("Line"), 1) { DashStyle = DashStyles.Dash }, new(left, y), new(left + width, y));
            drawing.DrawText(Text(Axis(value)), new(left + width + 8, y - 7));
        }
        var startLabel = Text(from.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture));
        var endLabel = Text(to.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture));
        drawing.DrawText(startLabel, new(left, top + height + 12));
        drawing.DrawText(endLabel, new(left + width - endLabel.Width, top + height + 12));
        if (valid.Length == 0)
        {
            drawing.DrawText(Text("No available readings in this range"), new(left + 12, top + height / 2));
            return;
        }

        var color = Theme.Brush("Accent");
        var seriesPen = new Pen(color, 1.8);
        var spread = color.Clone(); spread.Opacity = .25;
        var spreadPen = new Pen(spread, 1);
        ThermalWindow? previous = null;
        Point? previousPoint = null;
        double Y(double value) => top + height * (1 - Math.Clamp(value / maximum, 0, 1));
        foreach (var window in windows.Where(w => w.End >= from && w.Start <= to))
        {
            var aggregate = Metric(window, metric);
            if (aggregate.Average is not double value || !double.IsFinite(value) || aggregate.Count <= 0)
            {
                previous = null; previousPoint = null; continue;
            }
            double middle = ((window.Start + (window.End - window.Start) / 2) - from).TotalSeconds;
            var point = new Point(left + width * Math.Clamp(middle / (to - from).TotalSeconds, 0, 1), Y(value));
            if (aggregate.Minimum is double minimum && aggregate.Maximum is double upper && double.IsFinite(minimum) && double.IsFinite(upper))
                drawing.DrawLine(spreadPen, new(point.X, Y(minimum)), new(point.X, Y(upper)));
            if (previous is not null && previousPoint is Point last && CanConnect(previous, window, metric))
                drawing.DrawLine(seriesPen, last, point);
            drawing.DrawEllipse(color, null, point, valid.Length > 500 ? 1 : 1.8, valid.Length > 500 ? 1 : 1.8);
            previous = window; previousPoint = point;
        }
    }

    private FormattedText Text(string value) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 11, Theme.Brush("Muted"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
