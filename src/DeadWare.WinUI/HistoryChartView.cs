using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// A live history chart drawn with Win2D from <see cref="HistoryChartModel"/>. The value lines and their labels sit on
/// one canvas; the traces and time labels sit on another that the compositor slides left between samples, so the
/// chart scrolls smoothly while being redrawn only once per sample.
/// </summary>
internal sealed class HistoryChartView : UserControl
{
    /// <summary>Trace-layer draws since start-up, across all charts; reported by <c>--exit-after</c> for the smoke check.</summary>
    public static long FramesDrawn;

    private const double Left = 8, Top = 14, Right = 8, Bottom = 22;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat RightLabelFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };
    private static readonly CanvasTextFormat TickFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Center };

    /// <summary>Default trace colours by graph colour key; cpu and download default to the system accent. A colour saved in settings wins.</summary>
    private static readonly Dictionary<string, Color> Palette = new()
    {
        ["gpu"] = Color.FromArgb(255, 0x8E, 0x6C, 0xEF), ["cputemp"] = Color.FromArgb(255, 0xF2, 0x99, 0x4A), ["gputemp"] = Color.FromArgb(255, 0xEB, 0x57, 0x57),
        ["memory"] = Color.FromArgb(255, 0x27, 0xAE, 0x60), ["upload"] = Color.FromArgb(255, 0xF2, 0xC9, 0x4C),
        ["diskread"] = Color.FromArgb(255, 0x2D, 0x9C, 0xDB), ["diskwrite"] = Color.FromArgb(255, 0xBB, 0x6B, 0xD9),
    };

    private readonly MonitorSession session;
    private readonly string kind;
    private readonly bool compare;
    private readonly CanvasControl axes = new(), traces = new(), readout = new();
    private Point? pointer;
    private readonly RectangleGeometry clip = new();
    private HistoryChartFrame? frame;
    private ChartPlot plot;

    public HistoryChartView(MonitorSession session, string kind, bool compare = false)
    {
        this.session = session;
        this.kind = kind;
        this.compare = compare;
        axes.Draw += DrawAxes;
        traces.Draw += DrawTraces;
        traces.Clip = clip;
        ElementCompositionPreview.SetIsTranslationEnabled(traces, true);
        readout.Draw += DrawReadout;
        // A transparent background, so the pointer reaches the chart everywhere and not only over drawn pixels.
        var layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(axes);
        layers.Children.Add(traces);
        layers.Children.Add(readout);
        layers.PointerMoved += (_, e) => { pointer = e.GetCurrentPoint(layers).Position; readout.Invalidate(); };
        layers.PointerExited += (_, _) => { pointer = null; readout.Invalidate(); };
        Content = layers;
        MinHeight = 120;
        Loaded += (_, _) => { session.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => session.Changed -= Refresh;
        SizeChanged += (_, _) => Refresh();
    }

    /// <summary>Limits a storage chart to one drive's read and write; null for the totals with per-drive lines.</summary>
    public string? DriveId { get; init; }

    /// <summary>Keeps the CPU pump line and the per-core lines on a CPU / GPU comparison, as the widget shows them.</summary>
    public bool PumpAndCoresWithDevices { get; init; }

    /// <summary>Raised after each rebuild with the main trace's minimum, maximum and average over the range.</summary>
    public event Action<Summary>? StatisticsReady;

    private void Refresh()
    {
        plot = new ChartPlot(Left, Top, ActualWidth - Left - Right, ActualHeight - Top - Bottom);
        var settings = session.Settings;
        // Every present drive by disk index, as the dashboard traces them.
        var drives = kind == "storage" ? (session.Current?.Storage.Drives ?? []).OrderBy(drive => drive.Index).ToList() : new List<StorageDrive>();
        var options = new HistoryChartOptions(
            GpuFan: settings.ShowGpuFanSpeed,
            CpuPump: settings.ShowCpuPumpSpeed,
            Cores: settings.ShowCpuCoreUsage ? session.Current?.CpuCoreCount ?? 0 : 0,
            Drives: drives.Select(drive => drive.Id).ToList(),
            DriveNames: drives.GroupBy(drive => drive.Id).ToDictionary(group => group.Key, group => group.First().Model),
            PumpWithDevices: PumpAndCoresWithDevices,
            CoresWithDevices: PumpAndCoresWithDevices,
            Smoothed: settings.GraphSmoothing,
            DriveId: DriveId);
        frame = HistoryChartModel.Build(new HistoryChartRequest(session.Collector.History, kind, session.SelectedGpuId, compare,
            session.HistorySeconds, session.Collector.ElapsedSeconds, DateTimeOffset.Now, plot, TimeZoneInfo.Local, Options: options));
        StatisticsReady?.Invoke(frame.Statistics);
        // The clip keeps scrolled traces inside the plot and leaves room below it for the time labels that scroll with them.
        clip.Rect = plot.Width > 0 && plot.Height > 0 ? new Rect(plot.Left, 0, plot.Width, ActualHeight) : new Rect(0, 0, 0, 0);
        axes.Invalidate();
        traces.Invalidate();
        readout.Invalidate();
        if (frame.HasReadings) StartScroll();
    }

    /// <summary>Slides the trace layer left by one polling interval's width over that interval, as the WPF chart does.</summary>
    private void StartScroll()
    {
        double interval = Math.Min(1, session.Settings.PollingIntervalSeconds);
        if (!(interval > 0) || session.HistorySeconds <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(traces);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.Zero);
        animation.InsertKeyFrame(1, new Vector3((float)(-plot.Width / session.HistorySeconds * interval), 0, 0), visual.Compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromSeconds(interval);
        visual.StartAnimation("Translation", animation);
    }

    private (Color Ink, Color Line, Color Surface) Inks()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        return (Color.FromArgb(170, c, c, c), Color.FromArgb(32, c, c, c), Color.FromArgb(10, c, c, c));
    }

    /// <summary>
    /// A trace's colour. Core and drive lines share a base colour (CPU, disk read) and are told apart by a tint that
    /// steps with their index, unless a colour was saved for that core.
    /// </summary>
    private Color TraceColor(ChartTrace trace) => trace.Role switch
    {
        ChartTraceRole.Core => Tinted(trace.ColorKey, "cpu", trace.Index),
        ChartTraceRole.Drive => Tinted(null, "diskread", trace.Index),
        _ => TraceColor(trace.ColorKey),
    };

    private Color Tinted(string? ownKey, string baseKey, int index)
    {
        if (ownKey is not null && session.Settings.GraphColors.ContainsKey(ownKey)) return TraceColor(ownKey);
        Color color = TraceColor(baseKey);
        // Alternate lighter and darker steps, so neighbouring lines differ most.
        double amount = 0.18 + 0.12 * (index / 2 % 4);
        byte target = index % 2 == 0 ? (byte)255 : (byte)0;
        byte Mix(byte channel) => (byte)(channel + (target - channel) * amount);
        return Color.FromArgb(255, Mix(color.R), Mix(color.G), Mix(color.B));
    }

    /// <summary>The colour a graph colour key is drawn in, so a card can tint its reading to match its trace.</summary>
    public Color ColorOf(string key) => TraceColor(key);

    private Color TraceColor(string key)
    {
        if (key == "muted") return Inks().Ink;
        // Saved colours are normalised to "#RRGGBB" when settings load.
        if (session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved))
            return Color.FromArgb(255, Convert.ToByte(saved[1..3], 16), Convert.ToByte(saved[3..5], 16), Convert.ToByte(saved[5..7], 16));
        return Palette.TryGetValue(key, out var color) ? color : (Color)Application.Current.Resources["SystemAccentColor"];
    }

    /// <summary>The hover readout: a line at the nearest sample, a dot on each trace and a chip with the time and values.</summary>
    private void DrawReadout(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current || pointer is not { } position) return;
        if (HistoryChartModel.Hover(current, position.X, position.Y, session.Settings.PollingIntervalSeconds) is not { } hover) return;
        var drawing = args.DrawingSession;
        var (ink, line, _) = Inks();
        bool dark = ActualTheme == ElementTheme.Dark;
        drawing.DrawLine((float)hover.X, (float)plot.Top, (float)hover.X, (float)(plot.Top + plot.Height), ink, 1);
        Color LineColor(ChartHoverLine value) =>
            value.Index < 0 ? TraceColor(value.ColorKey) : value.ColorKey == "diskread" ? Tinted(null, "diskread", value.Index) : Tinted(value.ColorKey, "cpu", value.Index);
        foreach (var value in hover.Lines)
            if (value.Dot is { } dot) drawing.FillCircle((float)dot.X, (float)dot.Y, 3, LineColor(value));

        var rows = new List<(string Text, Color Color)> { (hover.Time, ink) };
        rows.AddRange(hover.Lines.Select(value => (value.Text, LineColor(value))));
        const float rowHeight = 16, padding = 6;
        float width = 0;
        foreach (var row in rows)
        {
            using var layout = new CanvasTextLayout(sender, row.Text, LabelFormat, 0, 0);
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        width += padding * 2;
        float height = rows.Count * rowHeight + padding * 2;
        // The chip sits to the right of the line, or to its left when it would leave the plot.
        float x = (float)hover.X + 10;
        if (x + width > plot.Left + plot.Width) x = (float)hover.X - 10 - width;
        float y = (float)Math.Clamp(position.Y - height / 2, plot.Top, Math.Max(plot.Top, plot.Top + plot.Height - height));
        drawing.FillRoundedRectangle(x, y, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        drawing.DrawRoundedRectangle(x, y, width, height, 4, 4, line, 1);
        for (int i = 0; i < rows.Count; i++) drawing.DrawText(rows[i].Text, x + padding, y + padding + i * rowHeight, rows[i].Color, LabelFormat);
    }

    private void DrawAxes(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current || current.Lines.Count == 0) return;
        var session = args.DrawingSession;
        var (ink, line, surface) = Inks();
        session.FillRectangle((float)plot.Left, (float)plot.Top, (float)plot.Width, (float)plot.Height, surface);
        foreach (var value in current.Lines)
        {
            session.DrawLine((float)plot.Left, (float)value.Y, (float)(plot.Left + plot.Width), (float)value.Y, line, 1);
            session.DrawText(value.Label, (float)(plot.Left + plot.Width - 4), (float)value.Y - 14, ink, RightLabelFormat);
        }
        // Fan and pump lines read against RPM labels on the left of the same value lines.
        foreach (var value in current.RpmLines) session.DrawText(value.Label, (float)plot.Left + 4, (float)value.Y - 14, ink, LabelFormat);
        if (!current.HasReadings)
            session.DrawText(this.session.Current is null ? "Waiting for first sample…" : "No available readings in this range", (float)plot.Left + 15, (float)(plot.Top + plot.Height / 2 - 8), ink, LabelFormat);
    }

    private void DrawTraces(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Interlocked.Increment(ref FramesDrawn);
        if (frame is not { } current) return;
        var session = args.DrawingSession;
        var (ink, _, _) = Inks();
        float baseline = (float)(plot.Top + plot.Height);
        foreach (var tick in current.Ticks) session.DrawText(tick.Label, (float)tick.X, baseline + 4, ink, TickFormat);
        using var dashes = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        using var dots = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dot };
        // Cores and drives sit underneath, then the local share, then the totals and comparison lines.
        static int Layer(ChartTrace trace) => trace.Role switch { ChartTraceRole.Core or ChartTraceRole.Drive => 0, ChartTraceRole.Local => 1, _ => 2 };
        foreach (var trace in current.Traces.Where(trace => !trace.Hidden).OrderBy(Layer))
        {
            Color color = TraceColor(trace);
            bool thin = trace.Role is ChartTraceRole.Core or ChartTraceRole.Drive or ChartTraceRole.Local;
            if (trace.Role == ChartTraceRole.Local) color = Color.FromArgb(140, color.R, color.G, color.B);
            // With smoothing: each column's lowest-to-highest range, faintly, behind its average line.
            foreach (var band in trace.Bands)
                session.FillRectangle((float)band.Left, (float)band.Top, (float)(band.Right - band.Left), (float)Math.Max(1, band.Bottom - band.Top), Color.FromArgb(40, color.R, color.G, color.B));
            foreach (var run in trace.Runs)
            {
                if (run.Count == 1)
                {
                    session.FillCircle((float)run[0].X, (float)run[0].Y, 2, color);
                    continue;
                }
                if (trace.Fill > 0)
                {
                    using var area = new CanvasPathBuilder(sender);
                    area.BeginFigure((float)run[0].X, baseline);
                    area.AddLine((float)run[0].X, (float)run[0].Y);
                    AddRun(area, run, current.Curved);
                    area.AddLine((float)run[^1].X, baseline);
                    area.EndFigure(CanvasFigureLoop.Closed);
                    using var filled = CanvasGeometry.CreatePath(area);
                    session.FillGeometry(filled, Color.FromArgb((byte)(56 * trace.Fill), color.R, color.G, color.B));
                }
                using var path = new CanvasPathBuilder(sender);
                path.BeginFigure((float)run[0].X, (float)run[0].Y);
                AddRun(path, run, current.Curved);
                path.EndFigure(CanvasFigureLoop.Open);
                using var stroke = CanvasGeometry.CreatePath(path);
                float weight = thin ? 1 : 2;
                if (trace.Dotted) session.DrawGeometry(stroke, color, weight, dots);
                else if (trace.Dashed) session.DrawGeometry(stroke, color, weight, dashes);
                else session.DrawGeometry(stroke, color, weight);
            }
        }
    }

    /// <summary>Continues a figure from the run's first point through the rest: as a monotone curve, or straight segments.</summary>
    private static void AddRun(CanvasPathBuilder builder, IReadOnlyList<ChartPoint> run, bool curved)
    {
        if (curved)
        {
            foreach (var segment in MonotoneSpline.Build(run))
                builder.AddCubicBezier(new Vector2((float)segment.Control1.X, (float)segment.Control1.Y), new Vector2((float)segment.Control2.X, (float)segment.Control2.Y), new Vector2((float)segment.End.X, (float)segment.End.Y));
            return;
        }
        for (int i = 1; i < run.Count; i++) builder.AddLine((float)run[i].X, (float)run[i].Y);
    }
}
