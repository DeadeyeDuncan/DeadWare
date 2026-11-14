using System.Globalization;

namespace DeadWare.Core;

/// <summary>One plotted window: its average at the window's middle, with its minimum–maximum spread when recorded.</summary>
/// <param name="Window">Index into <see cref="ThermalChartFrame.Windows"/>.</param>
/// <param name="Tolerance">Half the window's width in pixels, at least 6: how far the pointer may be and still hover it.</param>
public sealed record ThermalChartPoint(int Window, double X, double Y, (double Top, double Bottom)? Spread, double Tolerance);

/// <summary>What the pointer is over: where to mark it and the two lines of the readout.</summary>
public sealed record ThermalChartHover(double X, double Y, string Time, string Text);

/// <summary>A thermal history chart ready to draw: axis lines, the points, and the runs of points that may be joined.</summary>
public sealed record ThermalChartFrame(ChartPlot Plot, IReadOnlyList<ThermalWindow> Windows, string Metric, double Maximum,
    IReadOnlyList<(double Y, string Label)> Lines, IReadOnlyList<ThermalChartPoint> Points, IReadOnlyList<IReadOnlyList<int>> Runs,
    string FromLabel, string ToLabel, double MarkerRadius)
{
    public bool HasReadings => Points.Count > 0;
}

/// <summary>Actual-time thermal history. Missing values and collection boundaries break the trace.</summary>
public static class ThermalChartModel
{
    public static readonly (string Label, string Id)[] Metrics =
        [("Temperature", "temperature"), ("Usage", "load"), ("Package power", "power"), ("Clock", "clock"), ("Fan speed", "fan")];

    public static MetricAggregate Metric(ThermalWindow window, string name) => name switch
    {
        "load" => window.Load,
        "power" => window.Power,
        "clock" => window.Clock,
        "fan" => window.FanRpm,
        _ => window.Temperature,
    };

    public static string Unit(string name) => name switch { "load" => "%", "power" => "W", "clock" => "MHz", "fan" => "RPM", _ => "°C" };

    public static string Number(double? value, string unit = "") =>
        value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString("0.0", CultureInfo.CurrentCulture) + unit : "—";

    public static string Signed(double? value) => value.HasValue ? value.Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°C" : "—";

    /// <summary>Two windows join only when both are fully covered and one follows the other without a gap in one recording session.</summary>
    public static bool CanConnect(ThermalWindow previous, ThermalWindow next, string name)
    {
        bool Covered(ThermalWindow window)
        {
            var value = Metric(window, name);
            return value.Count > 0 && value.Count == window.SampleCount &&
                value.Average is double average && double.IsFinite(average) &&
                window.DurationSeconds >= Math.Max(1, (window.End - window.Start).TotalSeconds * .9);
        }
        double maximumGap = Math.Max(4, Math.Max(PollingPolicy.Normalize(previous.PollingIntervalSeconds), PollingPolicy.Normalize(next.PollingIntervalSeconds)) + 3);
        return Covered(previous) && Covered(next) &&
            previous.Device.Id == next.Device.Id && previous.Device.ConfigurationId == next.Device.ConfigurationId &&
            previous.SessionId == next.SessionId && previous.ResolutionSeconds == next.ResolutionSeconds &&
            next.Start >= previous.End && (next.Start - previous.End).TotalSeconds <= maximumGap &&
            next.StartMonotonic >= previous.StartMonotonic + previous.DurationSeconds &&
            next.StartMonotonic - previous.StartMonotonic - previous.DurationSeconds <= maximumGap &&
            Math.Abs((next.Start - previous.End).TotalSeconds - (next.StartMonotonic - previous.StartMonotonic - previous.DurationSeconds)) <= 3;
    }

    public static string AxisLabel(string metric, double value) => $"{value.ToString("0.#", CultureInfo.CurrentCulture)} {Unit(metric)}";

    public static ThermalChartFrame Build(IReadOnlyList<ThermalWindow> data, string metric, DateTimeOffset from, DateTimeOffset to, ChartPlot plot, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var windows = data.OrderBy(w => w.Start).ToArray();
        string Stamp(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        var valid = windows.Where(w => w.End >= from && w.Start <= to).Where(w => Metric(w, metric).Average is double mean && double.IsFinite(mean)).ToArray();
        double maximum = metric == "load" ? 100 : Math.Max(metric == "temperature" ? 40 : 1,
            valid.Select(w => Metric(w, metric).Maximum ?? Metric(w, metric).Average!.Value).DefaultIfEmpty(0).Max() * 1.12);
        var lines = Enumerable.Range(0, 3).Select(i => (plot.Top + plot.Height * (1 - i / 2.0), AxisLabel(metric, maximum * i / 2))).ToArray();
        var points = new List<ThermalChartPoint>();
        var runs = new List<IReadOnlyList<int>>();
        double span = (to - from).TotalSeconds;
        if (plot.Width > 0 && plot.Height > 0 && span > 0)
        {
            double Y(double value) => plot.Top + plot.Height * (1 - Math.Clamp(value / maximum, 0, 1));
            ThermalWindow? previous = null; List<int>? run = null;
            for (int index = 0; index < windows.Length; index++)
            {
                var window = windows[index];
                if (window.End < from || window.Start > to) continue;
                var aggregate = Metric(window, metric);
                if (aggregate.Average is not double value || !double.IsFinite(value) || aggregate.Count <= 0) { previous = null; run = null; continue; }
                double middle = ((window.Start + (window.End - window.Start) / 2) - from).TotalSeconds;
                double x = plot.Left + plot.Width * Math.Clamp(middle / span, 0, 1);
                (double, double)? spread = aggregate.Minimum is double minimum && aggregate.Maximum is double upper && double.IsFinite(minimum) && double.IsFinite(upper)
                    ? (Y(upper), Y(minimum)) : null;
                if (run is null || previous is null || !CanConnect(previous, window, metric)) { run = []; runs.Add(run); }
                run.Add(points.Count);
                points.Add(new(index, x, Y(value), spread, Math.Max(6, plot.Width * (window.End - window.Start).TotalSeconds / span / 2)));
                previous = window;
            }
        }
        return new(plot, windows, metric, maximum, lines, points, runs, Stamp(from), Stamp(to), valid.Length > 500 ? 1 : 1.8);
    }

    /// <summary>The nearest point within its tolerance while the pointer is inside the plot, or null.</summary>
    public static ThermalChartHover? Hover(ThermalChartFrame frame, double x, double y, TimeZoneInfo? zone = null)
    {
        var plot = frame.Plot;
        if (x < plot.Left || x > plot.Left + plot.Width || y < plot.Top || y > plot.Top + plot.Height) return null;
        ThermalChartPoint? best = null; double distance = double.MaxValue;
        foreach (var point in frame.Points)
        {
            double d = Math.Abs(point.X - x);
            if (d <= point.Tolerance && d < distance) { best = point; distance = d; }
        }
        if (best is null) return null;
        var window = frame.Windows[best.Window];
        string time = TimeZoneInfo.ConvertTime(window.Start, zone ?? TimeZoneInfo.Local).ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        return new(best.X, best.Y, time, AxisLabel(frame.Metric, Metric(window, frame.Metric).Average!.Value));
    }

    /// <summary>The MIN / AVG / MAX line and the sample counts under the chart.</summary>
    public static string Summary(IReadOnlyList<ThermalWindow> history, string metric)
    {
        var values = history.Select(w => Metric(w, metric)).Where(v => v.Count > 0 && v.Average.HasValue).ToArray();
        if (values.Length == 0) return "No valid samples for this metric. Unsupported sensors remain unavailable.";
        long validCount = values.Sum(v => (long)v.Count);
        int eligible = history.Count(w => w.Eligible && w.ResolutionSeconds == 60);
        string resolution = string.Join(" / ", history.Select(w => w.ResolutionSeconds).Distinct().Order()
            .Select(s => s < 3600 ? $"{(s / 60d).ToString("0.#", CultureInfo.CurrentCulture)} min" : $"{(s / 3600d).ToString("0.#", CultureInfo.CurrentCulture)} hour"));
        string unit = " " + Unit(metric);
        return $"MIN {Number(values.Min(v => v.Minimum), unit)}    AVG {Number(values.Sum(v => v.Average!.Value * v.Count) / validCount, unit)}    MAX {Number(values.Max(v => v.Maximum), unit)}\n" +
            $"{validCount.ToString("N0", CultureInfo.CurrentCulture)} valid samples · {history.Count.ToString("N0", CultureInfo.CurrentCulture)} plotted windows · {resolution} resolution · {eligible.ToString("N0", CultureInfo.CurrentCulture)} eligible minute windows in this view";
    }

    /// <summary>Devices ordered CPU, GPU, storage, then by name; a repeated label is an older sensor map of the same hardware.</summary>
    public static IReadOnlyList<(string Label, ThermalDevice Device)> DeviceChoices(IReadOnlyList<ThermalDevice> devices, string currentConfigurationId)
    {
        var seen = new HashSet<string>();
        string Label(ThermalDevice value)
        {
            string setup = value.ConfigurationId == currentConfigurationId ? "current setup" : "previous setup " + value.ConfigurationId[..Math.Min(6, value.ConfigurationId.Length)];
            return $"{value.Kind} · {value.Name} · {setup}";
        }
        return devices.Select((d, n) => (d, n)).OrderBy(x => x.d.Kind switch { "CPU" => 0, "GPU" => 1, "Storage" => 2, _ => 3 })
            .ThenBy(x => x.d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.n)
            .Select(x => (Label: Label(x.d), Device: x.d))
            .Select(x => (seen.Add(x.Label) ? x.Label : $"{x.Label} · older sensor map {x.Device.Id[..Math.Min(6, x.Device.Id.Length)]}", x.Device)).ToArray();
    }

    /// <summary>A local yyyy-MM-dd HH:mm entry as an instant; clock-change times that do not exist or exist twice are refused.</summary>
    public static DateTimeOffset ParseLocal(string text, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new ArgumentException("Enter a local date and time as yyyy-MM-dd HH:mm, for example 2026-10-25 18:30.");
        if (zone.IsInvalidTime(value) || zone.IsAmbiguousTime(value))
            throw new ArgumentException("That local time is skipped or repeated by daylight saving. Choose a time outside the clock change.");
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }
}
