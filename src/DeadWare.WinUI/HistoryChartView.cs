using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition;
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
    // Time labels slide with the traces, so they are placed where they belong and not on the nearest whole pixel: snapped,
    // each drawing lands them up to half a pixel off and they tremble against the trace.
    private static readonly CanvasTextFormat TickFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Center, Options = CanvasDrawTextOptions.NoPixelSnap };

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
    private readonly CanvasControl axes = new(), traces = new(), ticks = new(), readout = new();
    private Point? pointer;
    private readonly RectangleGeometry clip = new();
    // The trace canvas is wider than the chart by what it slides, and sits in a host that clips it to the plot. The clip
    // has to be on the host: one on the canvas itself would slide with it.
    private readonly Canvas traceHost = new(), tickHost = new();
    // The time labels ride the same slide as the traces but end sooner, so they pass under "Now" and not through it.
    private readonly RectangleGeometry tickClip = new();
    // Where the sliding layers stand while the pointer holds them still, so the readout lands on what is under it.
    private float held;
    private HistoryChartFrame? frame;
    private HistoryChartRequest? pending;
    private bool building;
    private double frameNow, frameSeconds, frameWidth, frameLead;
    // The frame the trace layer last drew, which is what the slide has to follow until the next one is on screen.
    private bool drawnScrolls;
    private double drawnNow, drawnSeconds, drawnWidth, drawnLead;
    // The value at the top of the axis in the drawing on screen, and what it stood for: a change of scale is eased in.
    private double drawnMaximum;
    private (string Kind, string? Gpu, bool Compare, int Seconds, string? Drive) drawnScale;
    private ChartPlot plot;

    public HistoryChartView(MonitorSession session, string kind, bool compare = false)
    {
        this.session = session;
        this.kind = kind;
        this.compare = compare;
        axes.Draw += DrawAxes;
        traces.Draw += DrawTraces;
        traceHost.Clip = clip;
        traceHost.Children.Add(traces);
        ElementCompositionPreview.SetIsTranslationEnabled(traces, true);
        ticks.Draw += DrawTicks;
        tickHost.Clip = tickClip;
        tickHost.Children.Add(ticks);
        ElementCompositionPreview.SetIsTranslationEnabled(ticks, true);
        readout.Draw += DrawReadout;
        // A transparent background, so the pointer reaches the chart everywhere and not only over drawn pixels.
        var layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(axes);
        layers.Children.Add(traceHost);
        layers.Children.Add(tickHost);
        layers.Children.Add(readout);
        layers.PointerMoved += (_, e) => { bool entered = pointer is null; pointer = e.GetCurrentPoint(layers).Position; if (entered) Scroll(); readout.Invalidate(); };
        layers.PointerExited += (_, _) => { pointer = null; Scroll(); readout.Invalidate(); };
        Content = layers;
        MinHeight = 120;
        this.WhileShowing(() => { session.Changed += Refresh; Refresh(); }, () => session.Changed -= Refresh);
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
        double lead = Lead(settings.PollingIntervalSeconds);
        // Room on the right for the samples that have arrived but not yet slid into the plot.
        double beyond = plot.Width > 0 && session.HistorySeconds > 0 ? Math.Ceiling(plot.Width / session.HistorySeconds * lead) + 4 : 0;
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            (traces.Width, traces.Height) = (ActualWidth + beyond, ActualHeight);
            (ticks.Width, ticks.Height) = (ActualWidth + beyond, ActualHeight);
        }
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
        // Six hours of per-core history is far too much to walk on the UI thread every second for every chart: that starves
        // input and the compositor alike. Build off-thread and keep only the newest request, as the WPF chart does.
        pending = new HistoryChartRequest(session.Collector.History, kind, session.SelectedGpuId, compare,
            session.HistorySeconds, session.Collector.ElapsedSeconds, DateTimeOffset.Now, plot, TimeZoneInfo.Local, Options: options, Lead: lead);
        if (!building) _ = BuildLatestAsync();
    }

    private async Task BuildLatestAsync()
    {
        building = true;
        try
        {
            while (pending is { } request)
            {
                pending = null;
                HistoryChartFrame built;
                try { built = await Task.Run(() => HistoryChartModel.Build(request)); }
                catch (Exception error) { App.Note($"A {kind} chart could not be built: {error.Message}"); continue; }
                frame = built;
                frameNow = request.Now;
                frameSeconds = request.Seconds;
                frameWidth = request.Plot.Width;
                frameLead = request.Lead;
                StatisticsReady?.Invoke(built.Statistics);
                // The clips keep the sliding traces inside the plot, and the time labels under it and short of "Now".
                bool room = plot.Width > 0 && plot.Height > 0;
                clip.Rect = room ? new Rect(plot.Left, 0, plot.Width, plot.Top + plot.Height + 1) : new Rect(0, 0, 0, 0);
                double labels = plot.Width - NowWidth - 6;
                tickClip.Rect = room && labels > 0 ? new Rect(plot.Left, plot.Top + plot.Height, labels, ActualHeight - plot.Top - plot.Height) : new Rect(0, 0, 0, 0);
                axes.Invalidate();
                traces.Invalidate();
                ticks.Invalidate();
                readout.Invalidate();
            }
        }
        finally { building = false; }
    }

    /// <summary>
    /// How far behind the present the plot's right edge sits, in seconds: two samples at up to one a second. The newest
    /// sample is then drawn beyond the edge and slides into view, so the trace never ends short of the edge and waits for
    /// the next reading; and the curve into a sample is still out of sight when the sample after it arrives and bends it.
    /// </summary>
    private static double Lead(double pollingSeconds) => 2 * Math.Min(1, pollingSeconds > 0 ? pollingSeconds : 1);

    /// <summary>
    /// Slides the trace layer left at the chart's pace. Each drawing starts its own run from where that drawing already
    /// stands (the time since its moment) to the point where its last sample reaches the edge, and holds there if the next
    /// reading is late. The run and the drawing are committed together, so the first frame that shows the new drawing shows
    /// it in the right place. An earlier version followed one clock that ran for the life of the app; the compositor stops
    /// that clock while the window is covered or hidden, and every chart then stood still between samples until restart.
    /// Under the pointer the layer holds still, so the readout stays on its sample.
    /// </summary>
    private void Scroll()
    {
        var layers = new[] { traces, ticks };
        var visuals = layers.Select(layer => ElementCompositionPreview.GetElementVisual(layer)).ToArray();
        void Hold(Vector3 at)
        {
            held = at.X;
            for (int i = 0; i < layers.Length; i++) { visuals[i].StopAnimation("Translation"); layers[i].Translation = at; }
        }
        if (!drawnScrolls || !(drawnLead > 0) || !(drawnSeconds > 0) || !(drawnWidth > 0)) { Hold(Vector3.Zero); return; }
        double speed = drawnWidth / drawnSeconds;
        double elapsed = Math.Clamp(session.Collector.ElapsedSeconds - drawnNow, 0, drawnLead);
        var from = new Vector3((float)(-speed * elapsed), 0, 0);
        Hold(from);
        if (pointer is not null || drawnLead - elapsed < 0.001) return;
        var compositor = visuals[0].Compositor;
        var run = compositor.CreateVector3KeyFrameAnimation();
        run.InsertKeyFrame(0, from);
        run.InsertKeyFrame(1, new Vector3((float)(-speed * drawnLead), 0, 0), compositor.CreateLinearEasingFunction());
        run.Duration = TimeSpan.FromSeconds(drawnLead - elapsed);
        foreach (var visual in visuals) visual.StartAnimation("Translation", run);
    }

    /// <summary>
    /// Eases a change of the value axis. A network or disk chart's axis follows its highest reading, so it shifts a little
    /// with most samples; redrawn at the new scale in one step, the whole trace twitches. The new drawing instead starts
    /// stretched to the height the old one had and settles over a quarter of a second. A change of more than three times,
    /// a different chart (range, device, drive) and a chart under the pointer are shown at once.
    /// </summary>
    private void Rescale(HistoryChartFrame current)
    {
        var scale = (kind, session.SelectedGpuId, compare, (int)drawnSeconds, DriveId);
        double before = drawnMaximum, after = current.Maximum;
        bool same = scale == drawnScale;
        (drawnMaximum, drawnScale) = (after, scale);
        var visual = ElementCompositionPreview.GetElementVisual(traces);
        visual.CenterPoint = new Vector3(0, (float)(plot.Top + plot.Height), 0);
        double ratio = before > 0 ? after / before : 1;
        if (!same || pointer is not null || !current.HasReadings || !(after > 0) || Math.Abs(ratio - 1) < 0.002 || ratio > 3 || ratio < 1 / 3d)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            return;
        }
        var settle = visual.Compositor.CreateVector3KeyFrameAnimation();
        settle.InsertKeyFrame(0, new Vector3(1, (float)ratio, 1));
        settle.InsertKeyFrame(1, Vector3.One, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.2f, 1)));
        settle.Duration = TimeSpan.FromMilliseconds(250);
        visual.StartAnimation("Scale", settle);
    }

    /// <summary>The width of the "Now" label, which the sliding time labels stop short of.</summary>
    private static double NowWidth => nowWidth ??= TextWidth(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), "Now", LabelFormat);
    private static double? nowWidth;

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

    /// <summary>
    /// The saved line style a trace is drawn with. A core line takes the CPU line's thickness, times its own when it has a
    /// style of its own; drive lines have none.
    /// </summary>
    private GraphLineStyle StyleOf(ChartTrace trace)
    {
        var styles = session.Settings.GraphStyles;
        if (trace.Role == ChartTraceRole.Drive) return new();
        if (trace.Role != ChartTraceRole.Core) return styles.GetValueOrDefault(trace.ColorKey, new());
        double cpu = styles.TryGetValue("cpu", out var whole) ? whole.Thickness : 1;
        return styles.TryGetValue(trace.ColorKey, out var own) ? own with { Thickness = cpu * own.Thickness } : new GraphLineStyle(Thickness: cpu);
    }

    /// <summary>High contrast draws no glow, as in the WPF charts.</summary>
    private static bool HighContrast => accessibility.HighContrast;
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings accessibility = new();

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
        // The layers stand `held` to the left of where they were drawn, so the pointer is that much further along them.
        if (position.X < plot.Left || position.X > plot.Left + plot.Width) return;
        if (HistoryChartModel.Hover(current, Math.Min(position.X - held, plot.Left + plot.Width), position.Y, session.Settings.PollingIntervalSeconds) is not { } found) return;
        var hover = found with { X = found.X + held, Lines = found.Lines.Select(value => value.Dot is { } dot ? value with { Dot = dot with { X = dot.X + held } } : value).ToList() };
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
            using var layout = new CanvasTextLayout(sender, row.Text, LabelFormat, 0, 0) { WordWrapping = CanvasWordWrapping.NoWrap };
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        // A chip taller than the chart (a CPU chart lists its busiest cores) would be cut off at the bottom: it then
        // continues in further columns, the time staying at the top of the first.
        float room = (float)sender.ActualHeight - 2;
        int perColumn = Math.Max(2, (int)((room - padding * 2) / rowHeight));
        int columns = (rows.Count + perColumn - 1) / perColumn;
        float column = width + 12;
        width = columns * column - 12 + padding * 2;
        float height = Math.Min(rows.Count, perColumn) * rowHeight + padding * 2;
        // The chip sits to the right of the line, or to its left when it would leave the plot.
        float x = (float)hover.X + 10;
        if (x + width > plot.Left + plot.Width) x = Math.Max(0, (float)hover.X - 10 - width);
        float y = Math.Clamp((float)position.Y - height / 2, 1, Math.Max(1, room - height));
        drawing.FillRoundedRectangle(x, y, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        drawing.DrawRoundedRectangle(x, y, width, height, 4, 4, line, 1);
        for (int i = 0; i < rows.Count; i++)
            drawing.DrawText(rows[i].Text, x + padding + i / perColumn * column, y + padding + i % perColumn * rowHeight, rows[i].Color, LabelFormat);
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
        session.DrawText("Now", (float)(plot.Left + plot.Width), (float)(plot.Top + plot.Height + 4), ink, RightLabelFormat);
        if (!current.HasReadings)
            session.DrawText(this.session.Current is null ? "Waiting for first sample…" : "No available readings in this range", (float)plot.Left + 15, (float)(plot.Top + plot.Height / 2 + 2), ink, LabelFormat);
    }

    private static double TextWidth(Microsoft.Graphics.Canvas.ICanvasResourceCreator creator, string text, CanvasTextFormat format)
    {
        using var layout = new CanvasTextLayout(creator, text, format, 0, 0) { WordWrapping = CanvasWordWrapping.NoWrap };
        return layout.LayoutBounds.Width;
    }

    /// <summary>The time labels, every one of them: the clip on their host hides those still to come and those under "Now".</summary>
    private void DrawTicks(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current) return;
        var (ink, _, _) = Inks();
        foreach (var tick in current.Ticks) args.DrawingSession.DrawText(tick.Label, (float)tick.X, (float)(plot.Top + plot.Height) + 4, ink, TickFormat);
    }

    private void DrawTraces(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Interlocked.Increment(ref FramesDrawn);
        if (frame is not { } current) return;
        (drawnScrolls, drawnNow, drawnSeconds, drawnWidth, drawnLead) = (current.HasReadings, frameNow, frameSeconds, frameWidth, frameLead);
        Scroll();
        Rescale(current);
        var session = args.DrawingSession;
        float baseline = (float)(plot.Top + plot.Height);
        using var dashes = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash, LineJoin = CanvasLineJoin.Round };
        using var dots = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dot, LineJoin = CanvasLineJoin.Round };
        using var round = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
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
                // The line style saved for this trace in Settings: its pattern, thickness and glow, as the WPF charts draw them.
                var style = StyleOf(trace);
                float weight = (float)((thin ? 1 : 2) * style.Thickness);
                var pattern = style.Pattern switch { "solid" => null, "dashed" => dashes, "dotted" => dots, _ => trace.Dotted ? dots : trace.Dashed ? dashes : null };
                if (style.Glow && style.GlowStrength > 0 && !HighContrast)
                {
                    var halo = Color.FromArgb((byte)Math.Clamp(color.A * .16 * style.GlowStrength, 0, 255), color.R, color.G, color.B);
                    session.DrawGeometry(stroke, halo, (float)(weight + 4 * style.GlowStrength), round);
                }
                if (pattern is null) session.DrawGeometry(stroke, color, weight, round);
                else session.DrawGeometry(stroke, color, weight, pattern);
            }
        }
    }

    /// <summary>Continues a figure from the run's first point through the rest: as a monotone curve, or straight segments.</summary>
    /// <summary>Continues a figure begun at the run's first point through the rest of it: the monotone spline when curved, straight
    /// segments otherwise. The thermal and usage charts share it, so Graph smoothing looks the same everywhere.</summary>
    internal static void AddRun(CanvasPathBuilder builder, IReadOnlyList<ChartPoint> run, bool curved)
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
