using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks of the Usage history page, chart and window key. No store, hardware or native window is involved.</summary>
internal static class UsageHistoryUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Assembly = typeof(Collector).Assembly;
    private static readonly Type PageType = Assembly.GetType("DeadWare.App.UsageHistoryPage")!;
    private static readonly Type ChartType = Assembly.GetType("DeadWare.App.UsageHistoryChart")!;
    private static readonly Type TraceType = Assembly.GetType("DeadWare.App.UsageTrace")!;
    private static readonly Type DataType = Assembly.GetType("DeadWare.App.UsagePageData")!;
    private const string ProcessHistorySentence = " While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days (up to 192 MiB).";
    private const string PrivacyLine = "History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit.";

    internal static void Run(Application app)
    {
        Assert.True(DetachedWindowState.IsAllowed("usagehistory"));
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        typeof(Collector).GetField("clock", All)!.SetValue(collector, new System.Diagnostics.Stopwatch());
        typeof(Collector).GetField("latest", All)!.SetValue(collector, new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()),
            [new("/gpu/0", "Fixture GPU")], [], [], "CPU", "", "", "", false));
        typeof(UiSession).GetField("<Collector>k__BackingField", All)!.SetValue(session, collector);
        typeof(UiSession).GetField("<Settings>k__BackingField", All)!.SetValue(session, new UserSettings());
        typeof(UiSession).GetField("<Usage>k__BackingField", All)!.SetValue(session, new UsageHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture"));
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight", "Blackout" })
        {
            Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
            CheckChart(theme, now, output);
            CheckByteUnit(now);
            CheckSelection(now);
            foreach (bool empty in new[] { true, false })
            {
                var page = (FrameworkElement)Activator.CreateInstance(PageType, All, null, [session, true], null)!;
                PageType.GetMethod("Apply", All)!.Invoke(page, [Data(now, empty)]);
                double width = empty ? 620 : 1050;
                Layout(page, width, 1100);
                var bitmap = new RenderTargetBitmap((int)width, 1100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                Assert.Equal(width, page.ActualWidth);
                string text = Text(page);
                Assert.Contains("CPU usage", text); Assert.Contains("GPU usage · Fixture GPU", text); Assert.Contains("Network download / upload", text);
                Assert.Contains("Applications", text); Assert.Contains("Record usage history on this device", text);
                Assert.Contains("up to 400 days", text);
                var texts = Descendants(page).OfType<TextBlock>().Select(t => t.Text).ToArray();
                string description = Assert.Single(texts, t => t.StartsWith("One row per minute for CPU, GPU, RAM and network", StringComparison.Ordinal));
                Assert.Contains("up to 400 days", description);
                Assert.EndsWith("for 30 days (up to 192 MiB).", description);
                Assert.EndsWith(ProcessHistorySentence, description);
                Assert.Contains(PrivacyLine, texts);
                var selector = (ComboBox)PageType.GetField("range", All)!.GetValue(page)!;
                Assert.Equal(3, selector.Items.Count);
                var table = (DataGrid)PageType.GetField("applications", All)!.GetValue(page)!;
                var rows = ((System.Collections.IEnumerable)table.ItemsSource!).Cast<object>().ToArray();
                if (empty)
                {
                    Assert.Contains("No history in this range yet", DrawnText(page));
                    Assert.Contains("No application traffic recorded", text);
                    Assert.Contains("No minutes recorded yet", text);
                    Assert.Empty(rows);
                    Assert.Contains("No readings in this period", text);
                }
                else
                {
                    Assert.Contains("MIN 10.0%", text); Assert.Contains("MAX 90.0%", text);
                    Assert.Contains("440 minutes", text);
                    Assert.Contains("↓ MIN", text);
                    Assert.Contains("Earliest retained minute", text);
                    Assert.Equal(4, rows.Length);
                    Assert.Contains("Other applications (12 more)", RowName(rows[^1]));
                    Assert.Equal("chrome.exe", RowName(rows[0]));
                    Assert.Contains("3 recorded minutes had no per-application capture", text);
                    Assert.Contains("15 applications", text);
                    var search = (TextBox)PageType.GetField("search", All)!.GetValue(page)!;
                    search.Text = "steam";
                    var filtered = ((System.Collections.IEnumerable)table.ItemsSource!).Cast<object>().ToArray();
                    Assert.Equal("steam.exe", RowName(Assert.Single(filtered)));
                    Assert.Contains("1 of 15 applications match", Text(page));
                    search.Text = "";
                    var shareBar = (Grid)PageType.GetField("shareBar", All)!.GetValue(page)!;
                    Assert.Equal(4, shareBar.Children.Count);
                }
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    using var stream = File.Create(Path.Combine(output, $"usage-{theme}-{(empty ? "empty" : "month")}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                }
            }
        }
        Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, ["Midnight"]);
        ApplicationResourcesUiChecks.Run(app);
    }

    private static void CheckChart(string theme, DateTimeOffset now, string? output)
    {
        var chart = (FrameworkElement)Activator.CreateInstance(ChartType, true)!;
        var start = now.AddHours(-3);
        // Thirty-six 5-minute slots with slot 10 missing and slot 20 only half recorded: the line must break at both.
        var buckets = Enumerable.Range(0, 36).Where(i => i != 10).Select(i => new UsageHistoryBucket(start.AddMinutes(i * 5), start.AddMinutes(i * 5 + 5),
            40 + i % 20, 30 + i % 20, 50 + i % 20, i == 20 ? 2 : 5, i == 20 ? 2 : 5, (i == 20 ? 2 : 5) * 60)).ToArray();
        Assert.True(UsageHistoryChart.IsFull(buckets[0], 300_000)); Assert.False(UsageHistoryChart.IsFull(buckets[19], 300_000));
        Assert.True(UsageHistoryChart.CanConnect(buckets[0], buckets[1], 300_000));
        Assert.False(UsageHistoryChart.CanConnect(buckets[9], buckets[10], 300_000), "a missing slot breaks the line");
        var view = new UsageHistoryView(start, now, 300_000, buckets, new(30, 69, 49.5), 173, start, 4096);
        var trace = Activator.CreateInstance(TraceType, view, "Graph/cpu", false, "CPU usage")!;
        var traces = Array.CreateInstance(TraceType, 1); traces.SetValue(trace, 0);
        ChartType.GetMethod("SetData", All)!.Invoke(chart, [traces, "%", 100d]);
        Layout(chart, 620, 180);
        var drawings = Drawings(VisualTreeHelper.GetDrawing(chart)!).ToArray();
        // The average line is one spline with a figure per run of connected full buckets: the missing slot and the
        // partial slot split 35 buckets into runs of 10, 9, 1 and 15; 31 Bézier curves join the 34 adjacent pairs minus three.
        var average = Assert.Single(drawings, d => d.Geometry is StreamGeometry && d.Pen is not null && d.Pen.Thickness > 1.5);
        var figures = PathGeometry.CreateFromGeometry(average.Geometry).Figures;
        Assert.Equal(3, figures.Count);
        Assert.Equal(31, figures.Sum(figure => figure.Segments.Sum(segment => segment switch { BezierSegment => 1, PolyBezierSegment poly => poly.Points.Count / 3, _ => 0 })));
        Assert.All(drawings.Where(d => d.Geometry is LineGeometry line && line.StartPoint.Y == line.EndPoint.Y && d.Pen is not null), d => Assert.Empty(d.Pen.DashStyle.Dashes));
        // The chart paints a transparent hit-test surface first; the bands are the other filled rectangles.
        Assert.Equal(35, drawings.Count(d => d.Geometry is RectangleGeometry && d.Brush is not null && !ReferenceEquals(d.Brush, Brushes.Transparent) && d.Pen is null));
        Assert.Contains(drawings, d => d.Geometry is EllipseGeometry);
        var plot = (Rect)ChartType.GetProperty("PlotRect", All)!.GetValue(chart)!;
        foreach (var glyph in Glyphs(VisualTreeHelper.GetDrawing(chart)!).Where(g => RunText(g).EndsWith('%')))
            Assert.True(plot.Contains(glyph.Bounds), $"{theme} usage label {RunText(glyph)} at {glyph.Bounds} outside {plot}.");
        // Bucket index 2 covers minutes 10–15 of the 180-minute range; its centre is at 12.5 minutes.
        ChartType.GetMethod("SetHover", All)!.Invoke(chart, [new Point(plot.Left + plot.Width * (12.5 / 180), plot.Top + 20)]);
        Assert.Equal(2, (int)ChartType.GetProperty("HoverIndex", All)!.GetValue(chart)!);
        Layout(chart, 620, 180);
        var hovered = VisualTreeHelper.GetDrawing(chart)!;
        Assert.Contains(Drawings(hovered), d => ReferenceEquals(d.Brush, Application.Current.Resources["GlassPopup"]));
        Assert.Contains(Glyphs(hovered), g => RunText(g) == "42%");
        ChartType.GetMethod("SetHover", All)!.Invoke(chart, [null]);
        Assert.Equal(-1, (int)ChartType.GetProperty("HoverIndex", All)!.GetValue(chart)!);
        Layout(chart, 620, 180);
        // Narrow one-minute buckets with slot 90 missing: hovering the hole shows nothing rather than a neighbour.
        var dense = (FrameworkElement)Activator.CreateInstance(ChartType, true)!;
        var minuteBuckets = Enumerable.Range(0, 180).Where(i => i != 90).Select(i => new UsageHistoryBucket(start.AddMinutes(i), start.AddMinutes(i + 1), 40, 30, 50, 1, 1, 60)).ToArray();
        var denseView = new UsageHistoryView(start, now, 60_000, minuteBuckets, new(30, 50, 40), 179, start, 4096);
        var denseTraces = Array.CreateInstance(TraceType, 1); denseTraces.SetValue(Activator.CreateInstance(TraceType, denseView, "Graph/cpu", false, "CPU usage")!, 0);
        ChartType.GetMethod("SetData", All)!.Invoke(dense, [denseTraces, "%", 100d]);
        Layout(dense, 620, 180);
        var densePlot = (Rect)ChartType.GetProperty("PlotRect", All)!.GetValue(dense)!;
        ChartType.GetMethod("SetHover", All)!.Invoke(dense, [new Point(densePlot.Left + densePlot.Width * (90.5 / 180), densePlot.Top + 20)]);
        Assert.Equal(-1, (int)ChartType.GetProperty("HoverIndex", All)!.GetValue(dense)!);
        ChartType.GetMethod("SetHover", All)!.Invoke(dense, [new Point(densePlot.Left + densePlot.Width * (91.5 / 180), densePlot.Top + 20)]);
        Assert.Equal(90, (int)ChartType.GetProperty("HoverIndex", All)!.GetValue(dense)!);
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap(620, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(chart);
            using var stream = File.Create(Path.Combine(output, $"usage-chart-{theme}.png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
    }

    private static FrameworkElement ChartWith(DateTimeOffset now, string unit, double? maximum, double value)
    {
        var chart = (FrameworkElement)Activator.CreateInstance(ChartType, true)!;
        var start = now.AddHours(-3);
        var buckets = Enumerable.Range(0, 36).Select(i => new UsageHistoryBucket(start.AddMinutes(i * 5), start.AddMinutes(i * 5 + 5), value, value / 2, value * 1.5, 5, 5, 300)).ToArray();
        var view = new UsageHistoryView(start, now, 300_000, buckets, new(value / 2, value * 1.5, value), 180, start, 4096);
        var traces = Array.CreateInstance(TraceType, 1); traces.SetValue(Activator.CreateInstance(TraceType, view, "Graph/cpu", false, "Fixture")!, 0);
        ChartType.GetMethod("SetData", All)!.Invoke(chart, [traces, unit, maximum]);
        Layout(chart, 620, 180);
        return chart;
    }

    private static void CheckByteUnit(DateTimeOffset now)
    {
        // A bytes series with an automatic maximum labels its axis through Ui.Bytes.
        var chart = ChartWith(now, "B", null, 512d * 1024 * 1024);
        var labels = Glyphs(VisualTreeHelper.GetDrawing(chart)!).Select(RunText).ToArray();
        Assert.Contains(labels, t => t.EndsWith("MB") || t.EndsWith("GB"));
        Assert.DoesNotContain(labels, t => t.EndsWith('%'));
    }

    private static void CheckSelection(DateTimeOffset now)
    {
        var accent = Application.Current.Resources["Accent"];
        int Hairlines(FrameworkElement chart) => Drawings(VisualTreeHelper.GetDrawing(chart)!)
            .Count(d => d.Geometry is LineGeometry line && line.StartPoint.X == line.EndPoint.X && ReferenceEquals(d.Pen?.Brush, accent));
        int Count(FrameworkElement chart) => Drawings(VisualTreeHelper.GetDrawing(chart)!).Count();
        int? Selected(FrameworkElement chart) => (int?)ChartType.GetProperty("SelectedIndex")!.GetValue(chart);
        var handleKey = ChartType.GetMethod("HandleKey", All)!;
        var handleClick = ChartType.GetMethod("HandleClick", All)!;

        // Without Selectable the chart draws exactly as before and is not focusable, whatever SelectedIndex says.
        var plain = ChartWith(now, "%", 100d, 40);
        int before = Count(plain);
        Assert.False(plain.Focusable);
        ChartType.GetProperty("SelectedIndex")!.SetValue(plain, 2);
        Layout(plain, 620, 180);
        Assert.Equal(before, Count(plain));
        Assert.Equal(0, Hairlines(plain));
        handleKey.Invoke(plain, [System.Windows.Input.Key.Right]);
        Assert.Equal(2, Selected(plain));

        var chart = ChartWith(now, "%", 100d, 40);
        int activated = -1;
        ChartType.GetEvent("BucketActivated")!.AddEventHandler(chart, new Action<int>(i => activated = i));
        var moved = new List<int>(); ChartType.GetEvent("SelectionMoved")!.AddEventHandler(chart, new Action<int>(moved.Add));
        ChartType.GetProperty("Selectable")!.SetValue(chart, true);
        Assert.True(chart.Focusable);
        Assert.Equal(0, Hairlines(chart));
        ChartType.GetProperty("SelectedIndex")!.SetValue(chart, 2);
        Layout(chart, 620, 180);
        Assert.Equal(1, Hairlines(chart));
        var plot = (Rect)ChartType.GetProperty("PlotRect", All)!.GetValue(chart)!;
        double Centre(int bucket) => plot.Left + plot.Width * ((bucket * 5 + 2.5) / 180);
        var hairline = Assert.Single(Drawings(VisualTreeHelper.GetDrawing(chart)!), d => d.Geometry is LineGeometry line && line.StartPoint.X == line.EndPoint.X && ReferenceEquals(d.Pen?.Brush, accent));
        Assert.Equal(1, hairline.Pen.Thickness);
        Assert.Equal(Centre(2), ((LineGeometry)hairline.Geometry).StartPoint.X, 3);
        handleKey.Invoke(chart, [System.Windows.Input.Key.Right]);
        Assert.Equal(3, Selected(chart));
        handleKey.Invoke(chart, [System.Windows.Input.Key.Enter]);
        Assert.Equal(3, activated);
        handleKey.Invoke(chart, [System.Windows.Input.Key.Left]); handleKey.Invoke(chart, [System.Windows.Input.Key.Left]);
        Assert.Equal(1, Selected(chart));
        handleKey.Invoke(chart, [System.Windows.Input.Key.Left]); handleKey.Invoke(chart, [System.Windows.Input.Key.Left]);
        Assert.Equal(0, Selected(chart));
        ChartType.GetProperty("SelectedIndex")!.SetValue(chart, 35);
        handleKey.Invoke(chart, [System.Windows.Input.Key.Right]);
        Assert.Equal(35, Selected(chart));
        ChartType.GetProperty("SelectedIndex")!.SetValue(chart, null);
        Layout(chart, 620, 180);
        Assert.Equal(0, Hairlines(chart));

        // A click activates the bucket under the pointer; a click outside any bucket does nothing.
        activated = -1;
        var bucketAt = ChartType.GetMethod("BucketAt", All)!;
        Assert.Equal(1, (int?)bucketAt.Invoke(chart, [new Point(Centre(1), plot.Top + 20)]));
        Assert.Null(bucketAt.Invoke(chart, [new Point(2, plot.Top + 20)]));
        handleClick.Invoke(chart, [new Point(Centre(1), plot.Top + 20)]);
        Assert.Equal(1, activated);
        Assert.Equal(1, Selected(chart));
        handleClick.Invoke(chart, [new Point(2, plot.Top + 20)]);
        Assert.Equal(1, activated);
        // Each Left and Right reports its index, even at an edge where it stays (Right at 35); SetValue, Enter and clicks report nothing.
        Assert.Equal([3, 2, 1, 0, 0, 35], moved);
    }

    private static object Data(DateTimeOffset now, bool empty)
    {
        var from = now.AddDays(-30);
        UsageHistoryView Metric(double low, double high)
        {
            if (empty) return new(from, now, 1_728_000, [], new(null, null, null), 0, null, 0);
            var buckets = Enumerable.Range(0, 1500).Select(i => new UsageHistoryBucket(from.AddMilliseconds(i * 1_728_000L), from.AddMilliseconds((i + 1) * 1_728_000L),
                low + (high - low) * (i % 10) / 10, low, high, i % 7 == 0 ? 1 : 28, 28, 28 * 60)).ToArray();
            return new(from, now, 1_728_000, buckets, new(low, high, (low + high) / 2), 1440, from, 20_000_000);
        }
        var metrics = new Dictionary<string, UsageHistoryView>(StringComparer.Ordinal)
        {
            [UsageMetrics.Cpu] = Metric(10, 90), [UsageMetrics.CpuTemperature] = Metric(35, 80), [UsageMetrics.Gpu] = Metric(5, 95),
            [UsageMetrics.GpuTemperature] = Metric(30, 75), [UsageMetrics.Memory] = Metric(40, 70),
            [UsageMetrics.Download] = Metric(1000, 5_000_000), [UsageMetrics.Upload] = Metric(100, 200_000),
        };
        var applications = empty
            ? new UsageApplicationView(from, now, [], 0, 0, 0, 0, 0, null, 0)
            : new UsageApplicationView(from, now, [new("chrome.exe", 5_000_000_000, 400_000_000, 1200), new("steam.exe", 2_000_000_000, 10_000_000, 300),
                new("=cmd.exe", 1_000, 2_000, 1)], 12, 300_000_000, 20_000_000, 3, 1440, from, 20_000_000);
        return Activator.CreateInstance(DataType, from, now, metrics, applications, "Fixture saved locally.", empty ? null : "Fixture GPU")!;
    }
    private static string RowName(object row) => (string)row.GetType().GetProperty("Name")!.GetValue(row)!;
    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(t => t.Text));
    private static string DrawnText(DependencyObject root) => string.Join("\n", Descendants(root).OfType<Visual>()
        .Select(v => VisualTreeHelper.GetDrawing(v)).Where(d => d is not null).SelectMany(d => GlyphText(d!)));
    private static IEnumerable<string> GlyphText(Drawing drawing)
    {
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var text in GlyphText(child)) yield return text;
        else if (drawing is GlyphRunDrawing glyphs && glyphs.GlyphRun?.Characters is { } characters) yield return new string(characters.ToArray());
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static IEnumerable<GeometryDrawing> Drawings(Drawing drawing)
    {
        if (drawing is DrawingGroup group) foreach (var child in group.Children) foreach (var item in Drawings(child)) yield return item;
        else if (drawing is GeometryDrawing geometry) yield return geometry;
    }
    private static string RunText(GlyphRunDrawing glyph) => new(glyph.GlyphRun.Characters.ToArray());
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing) => drawing is GlyphRunDrawing glyph ? [glyph] : drawing is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];
}
