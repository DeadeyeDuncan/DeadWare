using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        CheckChartColumns(session, now);
        CheckPageLayout(session, now);
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight", "Blackout" })
        {
            Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
            CheckChart(theme, now, output);
            CheckFractionalBucketsConnect(now);
            CheckByteUnit(now);
            CheckSelection(now);
            foreach (bool empty in new[] { true, false })
            {
                var page = (FrameworkElement)Activator.CreateInstance(PageType, All, null, [session, true], null)!;
                PageType.GetMethod("Apply", All)!.Invoke(page, [Data(now, empty)]);
                PageType.GetMethod("ApplyStrip", All)!.Invoke(page, [StripFor(now, empty), now]);
                double width = empty ? 620 : 1050;
                Layout(page, width, 1100);
                var hint = Assert.Single(Descendants(page).OfType<TextBlock>(), t => t.Text.StartsWith("Points average each slot's recorded minutes;", StringComparison.Ordinal));
                Assert.Equal(TextWrapping.Wrap, hint.TextWrapping); Assert.Contains("or press Enter on it", hint.Text);
                Assert.Equal("Each point is the average of the recorded minutes in its slot; the shaded band spans their minimum and maximum. Minutes with no readings stay empty and partial slots are not connected. Statistics use the stored minute values for the whole period. Dates use local time. Click a CPU, RAM or GPU point, or press Enter on it, to list its busiest apps under Application resources.", hint.ToolTip);
                if (empty) Assert.True(hint.ActualHeight > 2 * hint.FontSize, "The caption wraps at 620 DIP instead of clipping its keyboard guidance.");
                var retention = Assert.Single(Descendants(page).OfType<Expander>()); Assert.False(retention.IsExpanded);
                retention.IsExpanded = true; Layout(page, width, 1100);
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
                CheckClearHistoryEntry(page);
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
                    Assert.Contains("3 recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out.", texts);
                    Assert.Contains("15 applications", text);
                    var search = (TextBox)PageType.GetField("search", All)!.GetValue(page)!;
                    search.Text = "steam";
                    var filtered = ((System.Collections.IEnumerable)table.ItemsSource!).Cast<object>().ToArray();
                    Assert.Equal("steam.exe", RowName(Assert.Single(filtered)));
                    Assert.Contains("1 of 15 applications match", Text(page));
                    search.Text = "";
                    // The strip takes the share bar's place: the period header, the six applications and Other, each with its swatch.
                    var legend = ((TrafficStripView)PageType.GetField("strip", All)!.GetValue(page)!).Legend;
                    Assert.Equal(8, legend.Count);
                    Assert.Equal("Totals for this period", legend[0].Text); Assert.Null(legend[0].Swatch);
                    string[] names = StripFor(now).Applications.Select(a => a.Name).ToArray();
                    for (int i = 0; i < 6; i++) { Assert.StartsWith(names[i] + " · ", legend[i + 1].Text); Assert.Same(Theme.StripBrush(i), legend[i + 1].Swatch); }
                    Assert.StartsWith("Other (9 more) · ", legend[7].Text);
                    Assert.Equal(((SolidColorBrush)Theme.Brush("Muted")).Color, ((SolidColorBrush)legend[7].Swatch!).Color);
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
        // The strip's reads finish on the UI thread, as they do in the app.
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            CheckStripListsSlot(session, now);
            CheckStripSharesTheCursor(session, now);
            CheckStripClearPaths(session, now);
            CheckStripReadsOnItsOwn(session, now);
            CheckStripClearedTime(session, now);
            CheckHistoryClearRereads(session);
            CheckShowInConnections(session, now);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        ApplicationResourcesUiChecks.Run(app);
    }

    private static void CheckChartColumns(UiSession session, DateTimeOffset now)
    {
        var page = new UsageHistoryPage(session); page.Apply((UsagePageData)Data(now, false));
        var original = new[] { "cpu", "cputemp", "gpu", "gputemp", "memory", "network" }.Select(page.Chart).ToArray();
        foreach (var (width, columns) in new[] { (1300, 3), (1050, 2), (1099, 2), (1100, 3), (1300, 3), (900, 2), (1300, 3) })
        {
            Layout(page, width, 1100);
            var grid = Assert.Single(Descendants(page).OfType<UniformGrid>(), g => g.Children.Count == 6);
            Assert.Equal(columns, grid.Columns);
            Assert.Equal(original, grid.Children.Cast<StackPanel>().Select(cell => Assert.Single(cell.Children.OfType<UsageHistoryChart>())));
            Assert.All(original, chart => Assert.Equal(170, chart.Height));
        }
    }

    private static void CheckPageLayout(UiSession session, DateTimeOffset now)
    {
        foreach (bool showOpenWindow in new[] { true, false })
        {
            var page = new UsageHistoryPage(session, showOpenWindow); page.Apply((UsagePageData)Data(now, false)); Layout(page, 1300, 800);
            var body = Assert.IsType<StackPanel>(page.Content); var cards = body.Children.Cast<UIElement>().Select(Assert.IsType<Border>).ToArray();
            Assert.Equal(4, cards.Length); Assert.Equal(new Thickness(12, 6, 12, 6), cards[0].Padding); Assert.Equal(new Thickness(0, 0, 0, 10), cards[0].Margin);
            Assert.Contains(Field<ComboBox>(page, "range"), Descendants(cards[0])); Assert.Contains("Device", Text(cards[1])); Assert.Same(page.Tabs, cards[2].Child);
            Assert.Equal(new Thickness(16), cards[2].Padding); Assert.Equal(new Thickness(0, 0, 0, 12), cards[2].Margin);
            var status = Field<TextBlock>(page, "actionStatus"); Assert.Contains(status, Descendants(cards[0]));
            status.Text = "Export saved: fixture.csv"; Layout(page, 1300, 800); Assert.Contains(status.Text, Text(cards[0])); Assert.True(status.ActualHeight > 0);
            Assert.Equal(showOpenWindow ? 1 : 0, Descendants(cards[0]).OfType<Button>().Count(b => Equals(b.Content, "Open in window ↗")));
            Assert.Same(page.ApplicationsTab, page.Tabs.SelectedItem); Assert.Equal(2, page.Tabs.Items.Count);
            Assert.Contains(Field<DataGrid>(page, "applications"), Descendants(cards[2])); Assert.DoesNotContain(Field<ApplicationResourcesCard>(page, "resources"), Descendants(cards[2]));
            page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout();
            Assert.Contains(Field<ApplicationResourcesCard>(page, "resources"), Descendants(cards[2])); Assert.DoesNotContain(Field<DataGrid>(page, "applications"), Descendants(cards[2]));
            var retention = Assert.Single(Descendants(cards[3]).OfType<Expander>()); Assert.Equal("Retention, privacy and clearing", retention.Header); Assert.False(retention.IsExpanded);
            CheckClearHistoryEntry(page);
        }
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

    // ReadMetric's Week grid at 1,500 points: 403,200 ms (6.72 min) from now − 7 d (a whole minute). A bucket holds 6 or 7 whole-minute starts,
    // and a fully recorded 6-start bucket must connect.
    private static void CheckFractionalBucketsConnect(DateTimeOffset now)
    {
        var from = now.AddDays(-7);
        UsageHistoryBucket B(int k, long minutes) => new(from.AddMilliseconds(403_200L * k), from.AddMilliseconds(403_200L * (k + 1)), 50, 40, 60, minutes, minutes, minutes * 60);
        Assert.Equal(6, TrafficStrips.MinuteStarts(B(3, 0).Start, B(3, 0).End)); Assert.Equal(7, TrafficStrips.MinuteStarts(B(2, 0).Start, B(2, 0).End));
        Assert.True(UsageHistoryChart.IsFull(B(3, 6), 403_200)); Assert.True(UsageHistoryChart.CanConnect(B(2, 7), B(3, 6), 403_200));
        Assert.False(UsageHistoryChart.IsFull(B(3, 5), 403_200)); Assert.False(UsageHistoryChart.CanConnect(B(2, 7), B(3, 5), 403_200));
        Assert.False(UsageHistoryChart.IsFull(B(2, 6), 403_200)); Assert.True(UsageHistoryChart.IsFull(B(2, 7), 403_200));
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

    /// <summary>The slim card keeps recording and storage visible; its expander ends with Don't record and Clear history.</summary>
    internal static void CheckClearHistoryEntry(FrameworkElement page)
    {
        var keep = Descendants(page).OfType<StackPanel>().Single(panel => panel.Children.Count > 0 && panel.Children[0] is TextBlock { Text: "Keep a longer record" });
        foreach (string name in new[] { "recording", "recordingStatus", "storage" })
        {
            var child = Field<FrameworkElement>((UsageHistoryPage)page, name);
            Assert.Contains(child, keep.Children.Cast<UIElement>()); Assert.Equal(Visibility.Visible, child.Visibility);
        }
        var expander = Assert.Single(keep.Children.OfType<Expander>());
        var children = Assert.IsType<StackPanel>(expander.Content).Children.Cast<UIElement>().ToList();
        Assert.Equal("Clear history…", Assert.IsType<Button>(children[^1]).Content);
        Assert.Equal("Apps in Don't record these apps are not saved here; the Device charts still include their traffic.", Assert.IsType<TextBlock>(children[^2]).Text);
        Assert.True(ToolTipService.GetShowOnDisabled(children[^1]));
    }

    private const string LoadingSlot = "Loading this slot…", SlotUnavailable = "History unavailable for this slot.", EmptySlot = "No application traffic recorded in this slot.";
    private static readonly string[] Moments = ["cpu", "memory", "gpu"];

    /// <summary>A month page with Data and StripFor applied and laid out at 1050 × 1100. Its slot reads are queued, never answered.</summary>
    private static (UsageHistoryPage Page, TrafficStripChart Chart, List<(DateTimeOffset From, DateTimeOffset To)> SlotReads) Build(UiSession session, DateTimeOffset now)
    {
        var page = new UsageHistoryPage(session);
        var slotReads = new List<(DateTimeOffset From, DateTimeOffset To)>();
        SetField(page, "readSlotApplications", (Func<DateTimeOffset, DateTimeOffset, Task<UsageApplicationView?>>)((from, to) =>
            { slotReads.Add((from, to)); return new TaskCompletionSource<UsageApplicationView?>().Task; }));
        page.Apply((UsagePageData)Data(now, false)); page.ApplyStrip(StripFor(now), now);
        Layout(page, 1050, 1100);
        return (page, Field<TrafficStripView>(page, "strip").Chart, slotReads);
    }

    private static void CheckStripListsSlot(UiSession session, DateTimeOffset now)
    {
        var (page, chart, slotReads) = Build(session, now);
        var slots = StripFor(now).Slots; var summary = Field<TextBlock>(page, "applicationSummary"); var gap = Field<TextBlock>(page, "gapNotice");
        var resources = Field<ApplicationResourcesCard>(page, "resources");
        void Listed(int index)
        {
            Assert.Same(page.ApplicationsTab, page.Tabs.SelectedItem);
            var slot = slots[index];
            Assert.Equal<(DateTimeOffset, DateTimeOffset)?>((slot.Start, slot.End), ApplicationSlot(page));
            Assert.Equal("Applications · " + ApplicationResourcesCard.SlotSpan(TrafficStrips.CeilingMinute(slot.Start), TrafficStrips.CeilingMinute(slot.End), true),
                Field<TextBlock>(page, "applicationsHeading").Text);
            Assert.Equal(Visibility.Visible, Field<Button>(page, "applicationsClear").Visibility);
            Assert.Equal(LoadingSlot, summary.Text); Assert.Empty(Rows(page));
            Assert.Equal((slot.Start, slot.End), slotReads[^1]);
            // One cursor: the CPU, RAM and GPU hairlines mark the slot's time (Data's grid is the strip's), and the strip keeps its selection.
            foreach (string kind in Moments) Assert.Equal(index, page.Chart(kind).SelectedIndex);
            Assert.Equal(index, chart.SelectedIndex);
        }
        // A click lists the slot; offscreen nothing takes keyboard focus, so any Focus() call would show in the scope.
        var scope = FocusManager.GetFocusScope(chart); FocusManager.SetFocusedElement(scope, null);
        ClickSlot(chart, 3);
        Listed(3); Assert.Single(slotReads);
        Assert.Null(FocusManager.GetFocusedElement(scope));
        Assert.Null(resources.Slot);
        // The slot's read shows its rows, its summary and its gap notice; an empty slot and an unreadable one say so.
        page.ApplySlotApplications(SlotView(slots[3]));
        Assert.Equal(["chrome.exe", UsageMetrics.UnknownName, "Other applications (2 more)"], Rows(page).Select(RowName));
        Assert.StartsWith("4 applications · ", summary.Text);
        Assert.Equal("1 recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out.", gap.Text);
        Assert.Equal(Visibility.Visible, gap.Visibility);
        page.ApplySlotApplications(new UsageApplicationView(slots[3].Start, slots[3].End, [], 0, 0, 0, 0, 28, null, 0));
        Assert.Equal(EmptySlot, summary.Text); Assert.Empty(Rows(page));
        page.ApplySlotApplications(null);
        Assert.Equal(SlotUnavailable, summary.Text); Assert.Empty(Rows(page)); Assert.Equal(Visibility.Collapsed, gap.Visibility);
        // Enter on the selected slot lists it the same way.
        Click(Field<Button>(page, "applicationsClear"));
        chart.SelectedIndex = 5; Assert.True(chart.HandleKey(Key.Enter));
        Listed(5); Assert.Equal(2, slotReads.Count);
        Assert.Null(FocusManager.GetFocusedElement(scope));
        // A Moments-chart activation lists the card's slot and leaves the Applications slot; a strip activation leaves the card's.
        var cpu = page.Chart("cpu"); cpu.SelectedIndex = 7; Assert.True(cpu.HandleKey(Key.Enter));
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        var bucket = (start: slots[7].Start, end: slots[7].End);
        Assert.Equal<(DateTimeOffset, DateTimeOffset)?>(bucket, resources.Slot); Assert.Equal<(DateTimeOffset, DateTimeOffset)?>((slots[5].Start, slots[5].End), ApplicationSlot(page));
        page.Tabs.SelectedItem = page.ApplicationsTab; page.UpdateLayout(); ClickSlot(chart, 3);
        Listed(3); Assert.Equal<(DateTimeOffset, DateTimeOffset)?>(bucket, resources.Slot);
        // At a window's height the table sits below the fold; activation scrolls it into view, and the refresh never scrolls.
        Click(Field<Button>(page, "applicationsClear"));
        var window = new Size(1050, 800); var grid = Field<DataGrid>(page, "applications");
        page.ScrollToTop(); Layout(page, window.Width, window.Height);
        Assert.Equal(0, page.VerticalOffset);
        Assert.True(Top(grid, page) > page.ViewportHeight, "The Applications table starts below the fold.");
        page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout();
        typeof(UsageHistoryPage).GetMethod("ShowApplicationsSlot", All)!.Invoke(page, [3]);
        Assert.Same(page.ApplicationsTab, page.Tabs.SelectedItem);
        WaitFor(() => { Layout(page, window.Width, window.Height); return page.VerticalOffset > 0; });
        Assert.InRange(Top(grid, page), -0.5, page.ViewportHeight - 1);
        page.ScrollToTop(); Layout(page, window.Width, window.Height);
        Assert.Equal(0, page.VerticalOffset);
        page.Apply((UsagePageData)Data(now, false)); page.ApplyStrip(StripFor(now), now); Pump(); Layout(page, window.Width, window.Height);
        Assert.Equal(0, page.VerticalOffset);
        Listed(3);
    }

    private static void CheckStripSharesTheCursor(UiSession session, DateTimeOffset now)
    {
        var (page, chart, slotReads) = Build(session, now);
        var resources = Field<ApplicationResourcesCard>(page, "resources");
        // Left on the strip with nothing selected goes to its last slot and moves the Moments charts by time; nothing is listed.
        Assert.True(chart.HandleKey(Key.Left));
        Assert.Equal(1499, chart.SelectedIndex);
        foreach (string kind in Moments) Assert.Equal(1499, page.Chart(kind).SelectedIndex);
        Assert.Null(ApplicationSlot(page)); Assert.Null(resources.Slot); Assert.Empty(slotReads);
        // Left on the CPU chart moves the strip.
        Assert.True(page.Chart("cpu").HandleKey(Key.Left));
        Assert.Equal(1498, chart.SelectedIndex); Assert.Equal(1498, page.Chart("memory").SelectedIndex);
        Assert.Null(ApplicationSlot(page)); Assert.Null(resources.Slot); Assert.Empty(slotReads);
        // A refresh keeps the hairline at its time, also when the strip's slots move.
        page.Apply((UsagePageData)Data(now, false));
        Assert.Equal(1498, chart.SelectedIndex);
        var strip = StripFor(now);
        page.ApplyStrip(strip with { Slots = [.. strip.Slots.Skip(1)] }, now);
        Assert.Equal(1497, chart.SelectedIndex);
        Assert.Equal(1498, page.Chart("cpu").SelectedIndex);
    }

    private static void CheckStripClearPaths(UiSession session, DateTimeOffset now)
    {
        var (page, chart, _) = Build(session, now);
        var resources = Field<ApplicationResourcesCard>(page, "resources");
        var search = Field<TextBox>(page, "search"); var applicationsClear = Field<Button>(page, "applicationsClear");
        void ListBoth()
        {
            var cpu = page.Chart("cpu"); cpu.SelectedIndex = 7; Assert.True(cpu.HandleKey(Key.Enter));
            Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem); page.Tabs.SelectedItem = page.ApplicationsTab; page.UpdateLayout();
            ClickSlot(chart, 3);
            Assert.NotNull(resources.Slot); Assert.NotNull(ApplicationSlot(page)); Assert.NotNull(Marked(page));
        }
        void Cleared(string path)
        {
            Assert.True(ApplicationSlot(page) is null, $"{path} left the Applications slot listed.");
            Assert.True(resources.Slot is null, $"{path} left the card's slot listed.");
            Assert.True(Marked(page) is null, $"{path} left the cursor.");
            Assert.True(chart.SelectedIndex is null, $"{path} left the strip's hairline.");
            foreach (string kind in Moments) Assert.True(page.Chart(kind).SelectedIndex is null, $"{path} left the {kind} hairline.");
            Assert.Equal("Applications", Field<TextBlock>(page, "applicationsHeading").Text);
            Assert.Equal(Visibility.Collapsed, applicationsClear.Visibility);
            Assert.Equal(4, Rows(page).Count);
        }
        ListBoth(); Press(chart, Key.Escape); Cleared("Esc");
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout(); Press(page.Chart("cpu"), Key.Escape); Cleared("Esc on Resources");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        ListBoth(); Click(applicationsClear); Cleared("The Applications Clear");
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout(); Click(Descendants(resources).OfType<Button>().Single(b => Equals(b.Content, "Clear"))); Cleared("The card's Clear");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout();
        var range = Field<ComboBox>(page, "range"); range.SelectedIndex = 1; Cleared("A range change");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout(); Click(Descendants(page).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Earlier period")); Cleared("◀ Earlier");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout(); Click(Descendants(page).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Later period")); Cleared("Later ▶");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        ListBoth(); page.Tabs.SelectedItem = page.ResourcesTab; page.UpdateLayout(); page.ShowDay(now.AddDays(-2)); Cleared("ShowDay");
        Assert.Same(page.ResourcesTab, page.Tabs.SelectedItem);
        // The Applications Clear collapses once pressed: holding the focus, it hands it to the search box below it.
        ListBoth();
        var scope = FocusManager.GetFocusScope(applicationsClear);
        FocusManager.SetFocusedElement(scope, applicationsClear);
        Click(applicationsClear);
        Assert.Same(search, FocusManager.GetFocusedElement(scope));
        FocusManager.SetFocusedElement(scope, null);
    }

    /// <summary>Cleared time on Usage history's strip: the Cleared band and its line, no hatching inside it, and Not saved slots before a one-app clear.</summary>
    private static void CheckStripClearedTime(UiSession session, DateTimeOffset now)
    {
        var (page, chart, _) = Build(session, now);
        var view = Field<TrafficStripView>(page, "strip");
        var from = now.AddHours(-1);
        TrafficStrip Strip(Func<int, bool> zero, int first = 0) => new(from, now, 60_000, TrafficMeasure.Total, [new("chrome.exe", "CHROME.EXE", 1000, 500)], 0, 0, 0,
            Enumerable.Range(first, 60 - first).Select(k => new TrafficStripSlot(from.AddMinutes(k), from.AddMinutes(k + 1), 1, 60, false, [zero(k) ? 0 : 100L, 0], [zero(k) ? 0 : 50L, 0])).ToArray());
        Point At(int k) => new(chart.PlotRect.Left + chart.PlotRect.Width * (k + .5) / 60, chart.PlotRect.Top + chart.PlotRect.Height / 2);
        // An all-apps clear: the band labelled Cleared over [from, RetainedFrom), its line first, and nothing hatched inside it.
        var retained = from.AddMinutes(10);
        page.ApplyStrip(Strip(_ => false, 10) with { RetainedFrom = retained, RetainedByClear = true }, now);
        Layout(page, 1050, 1100); chart.SlotAt(new Point());
        Assert.Equal("Cleared", chart.BandLabel);
        Assert.NotNull(chart.NotKept); var band = chart.NotKept!.Value;
        Assert.Equal(chart.PlotRect.Left, band.Left, 6); Assert.Equal(chart.PlotRect.Left + chart.PlotRect.Width / 6, band.Right, 3);
        Assert.Equal($"Cleared before {retained.ToLocalTime().ToString("g")}", view.NoteLines[0]);
        Assert.Single(view.NoteLines);
        Assert.True(chart.Hatch is null || Enumerable.Range(0, 10).All(k => !chart.Hatch.FillContains(At(k))), "no minute inside the Cleared band is hatched");
        // A range wholly before the cleared start (RetainedFrom past To) is one Cleared band over the whole plot, with nothing hatched.
        page.ApplyStrip(Strip(_ => false, 60) with { RetainedFrom = now.AddMinutes(5), RetainedByClear = true }, now);
        Layout(page, 1050, 1100); chart.SlotAt(new Point());
        Assert.Equal(chart.PlotRect, chart.NotKept); Assert.Null(chart.Hatch); Assert.Equal("Cleared", chart.BandLabel);
        // A one-app clear: its line, and a zero-byte slot before it Not saved (hatched, its reason in the chip), one after it No traffic.
        var oneApp = from.AddMinutes(30); string g = oneApp.ToLocalTime().ToString("g");
        page.ApplyStrip(Strip(k => k is 20 or 40) with { AppClearedBefore = oneApp }, now);
        Layout(page, 1050, 1100); chart.SlotAt(new Point());
        Assert.Null(chart.BandLabel); Assert.Null(chart.NotKept);
        Assert.Equal([$"Some application history before {g} was cleared."], view.NoteLines);
        chart.HoverAt(At(20)); Assert.Equal([chart.SlotSpan(20), "No saved traffic", $"Some application history before {g} was cleared"], chart.ChipLines);
        Assert.True(chart.Hatch!.FillContains(At(20)), "a Not saved slot is hatched");
        chart.HoverAt(At(40)); Assert.Equal([chart.SlotSpan(40), "No traffic"], chart.ChipLines);
        Assert.False(chart.Hatch.FillContains(At(40)), "a No traffic slot is not hatched");
        chart.HoverAt(null);
        // Both: the Cleared line, then the one-app line.
        page.ApplyStrip(Strip(_ => false, 10) with { RetainedFrom = retained, RetainedByClear = true, AppClearedBefore = oneApp }, now);
        Assert.Equal([$"Cleared before {retained.ToLocalTime().ToString("g")}", $"Some application history before {g} was cleared."], view.NoteLines);
    }

    private static void CheckStripReadsOnItsOwn(UiSession session, DateTimeOffset now)
    {
        var page = new UsageHistoryPage(session);
        var reads = new List<(DateTimeOffset From, DateTimeOffset To, int Points, TaskCompletionSource<TrafficStrip?> Reply)>();
        SetField(page, "readStrip", (Func<DateTimeOffset, DateTimeOffset, int, Task<TrafficStrip?>>)((from, to, points) =>
            { var reply = new TaskCompletionSource<TrafficStrip?>(); reads.Add((from, to, points, reply)); return reply.Task; }));
        SetField(page, "showing", (Func<bool>)(() => true));
        Layout(page, 1050, 1100);
        var chart = Field<TrafficStripView>(page, "strip").Chart; var cpu = page.Chart("cpu");
        int points = (int)Math.Clamp(cpu.ActualWidth > 0 ? cpu.ActualWidth : 600, 60, 1500);
        // The Device charts and the table never wait for the strip.
        var first = LoadAsync(page);
        Assert.Single(reads);
        WaitFor(() => first.IsCompleted);
        Assert.False(reads[0].Reply.Task.IsCompleted);
        Assert.Equal((first.Result.From, first.Result.To, points), (reads[0].From, reads[0].To, reads[0].Points));
        page.Apply(first.Result);
        Assert.Same(first.Result, Field<UsagePageData>(page, "data"));
        Assert.Same(first.Result.Metrics[UsageMetrics.Cpu], Assert.Single(Traces(cpu)).View);
        Assert.Equal("No application traffic recorded in this period.", Field<TextBlock>(page, "applicationSummary").Text);
        Assert.Null(chart.Strip);
        // Each load starts its own strip read; of two, only the newer is shown, whichever answers first.
        var second = LoadAsync(page);
        Assert.Equal(2, reads.Count);
        WaitFor(() => second.IsCompleted);
        TrafficStrip For(int read) => new(reads[read].From, reads[read].To, 60_000, TrafficMeasure.Total, [], 0, 0, 0, []);
        var newer = For(1); var older = For(0);
        reads[1].Reply.SetResult(newer); Pump();
        Assert.Same(newer, chart.Strip);
        reads[0].Reply.SetResult(older); Pump();
        Assert.Same(newer, chart.Strip);
        page.ApplyStrip(null, now);
        Assert.Null(chart.Strip); Assert.Equal("History unavailable", typeof(TrafficStripChart).GetField("message", All)!.GetValue(chart));
        // A day ending now: its last minute is not due yet, so it is left unhatched for "Not saved yet".
        var from = now.AddDays(-1);
        var day = new TrafficStrip(from, now, 60_000, TrafficMeasure.Total, [], 0, 0, 0,
            [new TrafficStripSlot(from, from.AddMinutes(1), 1, 60, false, [0], [0])]);
        page.ApplyStrip(day, now); Layout(page, 1050, 1100); chart.SlotAt(new Point());
        var plot = chart.PlotRect;
        Assert.NotNull(chart.Hatch);
        Assert.Equal(plot.Left + plot.Width * (1439d / 1440), chart.Hatch!.Bounds.Right, 3);
        Assert.True(chart.Hatch.Bounds.Right < plot.Right);
        var third = LoadAsync(page);
        Assert.Equal(3, reads.Count);
        WaitFor(() => third.IsCompleted);
        // A range change or Refresh drops the strip read still in flight for the old period. (Hidden here, so the refresh reads nothing.)
        SetField(page, "showing", (Func<bool>)(() => false));
        typeof(UsageHistoryPage).GetMethod("RequestRefresh", All)!.Invoke(page, []);
        SetField(page, "showing", (Func<bool>)(() => true));
        reads[2].Reply.SetResult(For(2)); Pump();
        Assert.Same(day, chart.Strip);
        var fourth = LoadAsync(page);
        Assert.Equal(4, reads.Count);
        WaitFor(() => fourth.IsCompleted);
        reads[3].Reply.SetResult(null); Pump();
        Assert.Null(chart.Strip); Assert.Equal("History unavailable", typeof(TrafficStripChart).GetField("message", All)!.GetValue(chart));
        // A listed slot: an older slot read answering after a newer one is dropped, and a load reads the listed slot again.
        var slotReads = new List<(DateTimeOffset From, DateTimeOffset To, TaskCompletionSource<UsageApplicationView?> Reply)>();
        SetField(page, "readSlotApplications", (Func<DateTimeOffset, DateTimeOffset, Task<UsageApplicationView?>>)((from, to) =>
            { var reply = new TaskCompletionSource<UsageApplicationView?>(); slotReads.Add((from, to, reply)); return reply.Task; }));
        var slots = StripFor(now).Slots; page.ApplyStrip(StripFor(now), now);
        void ListSlot(int index) => typeof(UsageHistoryPage).GetMethod("ShowApplicationsSlot", All)!.Invoke(page, [index]);
        var summary = Field<TextBlock>(page, "applicationSummary");
        ListSlot(3); ListSlot(5); Assert.Equal(2, slotReads.Count); Assert.Equal((slots[5].Start, slots[5].End), (slotReads[1].From, slotReads[1].To));
        slotReads[1].Reply.SetResult(SlotView(slots[5])); Pump();
        Assert.StartsWith("4 applications · ", summary.Text);
        slotReads[0].Reply.SetResult(new UsageApplicationView(slots[3].Start, slots[3].End, [], 0, 0, 0, 0, 28, null, 0)); Pump();
        Assert.StartsWith("4 applications · ", summary.Text);
        var fifth = LoadAsync(page);
        WaitFor(() => fifth.IsCompleted);
        Assert.Equal(3, slotReads.Count); Assert.Equal((slots[5].Start, slots[5].End), (slotReads[2].From, slotReads[2].To));
        slotReads[2].Reply.SetResult(new UsageApplicationView(slots[5].Start, slots[5].End, [], 0, 0, 0, 0, 28, null, 0)); Pump();
        Assert.Equal(EmptySlot, summary.Text);
        reads[^1].Reply.SetResult(null); Pump();
    }

    /// <summary>A Usage history or Application resources clear starting or ending refreshes the loaded page; others do not, nor does an unloaded page.</summary>
    private static void CheckHistoryClearRereads(UiSession session)
    {
        var page = new UsageHistoryPage(session);
        int reads = 0;
        SetField(page, "readStrip", (Func<DateTimeOffset, DateTimeOffset, int, Task<TrafficStrip?>>)((_, _, _) => { reads++; return Task.FromResult<TrafficStrip?>(null); }));
        SetField(page, "showing", (Func<bool>)(() => true));
        Layout(page, 1050, 1100);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        bool Idle() => !(bool)typeof(UsageHistoryPage).GetField("refreshing", All)!.GetValue(page)!;
        void Raise(HistoryClearStore store) => ((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged", All)!.GetValue(session))?.Invoke(store);
        WaitFor(Idle); int count = reads;
        Raise(HistoryClearStore.Usage);
        WaitFor(() => reads == count + 1 && Idle()); Assert.Equal(count + 1, reads);
        Raise(HistoryClearStore.ProcessHistory);
        WaitFor(() => reads == count + 2 && Idle()); Assert.Equal(count + 2, reads);
        Raise(HistoryClearStore.Connections); Raise(HistoryClearStore.Alerts); Pump();
        Assert.Equal(count + 2, reads);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Raise(HistoryClearStore.Usage); Raise(HistoryClearStore.ProcessHistory); Pump();
        Assert.Equal(count + 2, reads);
    }

    private static void CheckShowInConnections(UiSession session, DateTimeOffset now)
    {
        var (page, chart, _) = Build(session, now);
        var button = Field<Button>(page, "showInConnections"); var search = Field<TextBox>(page, "search"); var table = Field<DataGrid>(page, "applications");
        var slots = StripFor(now).Slots;
        ConnectionsHistoryRequest? request = null;
        Action<ConnectionsHistoryRequest> capture = r => request = r;
        void Clock(DateTimeOffset time) => typeof(UsageHistoryPage).GetField("clock", All)!.SetValue(page, (Func<DateTimeOffset>)(() => time));
        void Select(string name) => table.SelectedItem = Rows(page).Single(r => RowName(r) == name);
        ConnectionsHistoryRequest Requested() { request = null; Click(button); return request ?? throw new Xunit.Sdk.XunitException("No Connections request."); }
        session.ConnectionsHistoryRequested += capture;
        try
        {
            Assert.Equal(DataGridSelectionMode.Single, table.SelectionMode);
            Assert.True(ToolTipService.GetShowOnDisabled(button));
            // The period gives its whole minutes.
            Clock(now);
            var shifted = (UsagePageData)Data(now.AddSeconds(20), false);
            page.Apply(shifted);
            Assert.True(button.IsEnabled); Assert.Equal("Opens Connections History for this period.", button.ToolTip);
            Assert.Equal(new ConnectionsHistoryRequest(TrafficStrips.CeilingMinute(shifted.From), TrafficStrips.CeilingMinute(shifted.To), null), Requested());
            Assert.Equal(new ConnectionsHistoryRequest(now.AddDays(-30).AddMinutes(1), now.AddMinutes(1), null), Requested());
            page.Apply((UsagePageData)Data(now, false));
            // A selected application is carried by its name; the Other row carries none.
            Select("chrome.exe");
            Assert.Equal("chrome.exe", Field<string?>(page, "selectedApplication"));
            Assert.Equal("Opens Connections History for this period. Only the selected application is shown.", button.ToolTip);
            Assert.Equal(new ConnectionsHistoryRequest(now.AddDays(-30), now, "chrome.exe"), Requested());
            Select("Other applications (12 more)");
            Assert.Equal("Opens Connections History for this period.", button.ToolTip);
            Assert.Null(Requested().Application);
            // The selection survives a refresh that replaces the rows; a search that hides its row clears it.
            Select("chrome.exe");
            page.Apply((UsagePageData)Data(now, false));
            Assert.Equal("chrome.exe", Field<string?>(page, "selectedApplication")); Assert.Equal("chrome.exe", RowName(table.SelectedItem!));
            Assert.Equal("chrome.exe", Requested().Application);
            search.Text = "steam";
            Assert.Null(Field<string?>(page, "selectedApplication"));
            search.Text = "";
            Assert.Null(Field<string?>(page, "selectedApplication")); Assert.Null(table.SelectedItem);
            Assert.Null(Requested().Application);
            // A listed slot gives its whole minutes; "Unknown application" carries no application.
            ClickSlot(chart, 3); page.ApplySlotApplications(SlotView(slots[3]));
            Assert.Equal("Opens Connections History for the listed slot.", button.ToolTip);
            Assert.Equal(new ConnectionsHistoryRequest(TrafficStrips.CeilingMinute(slots[3].Start), TrafficStrips.CeilingMinute(slots[3].End), null), Requested());
            Select(UsageMetrics.UnknownName);
            Assert.Null(Requested().Application);
            Select("chrome.exe");
            Assert.Equal("Opens Connections History for the listed slot. Only the selected application is shown.", button.ToolTip);
            Assert.Equal("chrome.exe", Requested().Application);
            Click(Field<Button>(page, "applicationsClear"));
            Assert.Equal("chrome.exe", Field<string?>(page, "selectedApplication"));
            // Older than Connections keeps: disabled, with the tooltip saying why.
            Clock(now.AddDays(40)); page.Apply((UsagePageData)Data(now, false));
            Assert.False(button.IsEnabled); Assert.Equal("Connections keeps up to 30 days; this period is older.", button.ToolTip);
            // A day clipped from the 30-day period says so.
            Clock(now.AddDays(1)); page.Apply((UsagePageData)Data(now, false));
            Assert.True(button.IsEnabled);
            Assert.Equal("Opens Connections History for this period. Only the selected application is shown. Connections keeps up to 30 days, so earlier minutes are left out.", button.ToolTip);
            Assert.Equal(new ConnectionsHistoryRequest(now.AddDays(-29), now, "chrome.exe"), Requested());
        }
        finally { session.ConnectionsHistoryRequested -= capture; }
    }

    // Data's 1,500-slot month grid: the six applications with the most traffic and nine others, the table's fifteen.
    private static TrafficStrip StripFor(DateTimeOffset now, bool empty = false)
    {
        var from = now.AddDays(-30);
        if (empty) return new(from, now, 1_728_000, TrafficMeasure.Total, [], 0, 0, 0, []);
        string[] names = ["chrome.exe", "steam.exe", "=cmd.exe", "Code.exe", "Teams.exe", "Spotify.exe"];
        var applications = names.Select((name, i) => new TrafficStripApplication(name, name.ToUpperInvariant(), 6_000_000_000L >> i, 600_000_000L >> i)).ToArray();
        var slots = Enumerable.Range(0, 1500).Select(i => new TrafficStripSlot(from.AddMilliseconds(i * 1_728_000L), from.AddMilliseconds((i + 1) * 1_728_000L), 28, 28 * 60, false,
            Enumerable.Range(0, 7).Select(s => 4_000_000L >> s).ToArray(), Enumerable.Range(0, 7).Select(s => 400_000L >> s).ToArray())).ToArray();
        return new(from, now, 1_728_000, TrafficMeasure.Total, applications, 300_000_000, 20_000_000, 9, slots);
    }
    /// <summary>One listed slot's applications: chrome.exe, "Unknown application" and two others, with one capture-gap minute.</summary>
    private static UsageApplicationView SlotView(TrafficStripSlot slot) => new(slot.Start, slot.End,
        [new("chrome.exe", 40_000_000, 4_000_000, 28), new(UsageMetrics.UnknownName, 1_000_000, 500_000, 3)], 2, 300_000, 20_000, 1, 28, null, 0);
    private static Task<UsagePageData> LoadAsync(UsageHistoryPage page) => (Task<UsagePageData>)typeof(UsageHistoryPage).GetMethod("LoadAsync", All)!.Invoke(page, [])!;
    private static (DateTimeOffset, DateTimeOffset)? ApplicationSlot(UsageHistoryPage page)
        => ((DateTimeOffset, DateTimeOffset)?)typeof(UsageHistoryPage).GetField("applicationSlot", All)!.GetValue(page);
    private static DateTimeOffset? Marked(UsageHistoryPage page) => (DateTimeOffset?)typeof(UsageHistoryPage).GetField("marked", All)!.GetValue(page);
    private static T Field<T>(UsageHistoryPage page, string name) => (T)typeof(UsageHistoryPage).GetField(name, All)!.GetValue(page)!;
    private static void SetField(UsageHistoryPage page, string name, object value) => typeof(UsageHistoryPage).GetField(name, All)!.SetValue(page, value);
    private static List<object> Rows(UsageHistoryPage page)
        => ((System.Collections.IEnumerable?)Field<DataGrid>(page, "applications").ItemsSource ?? Array.Empty<object>()).Cast<object>().ToList();
    private static IReadOnlyList<UsageTrace> Traces(UsageHistoryChart chart) => (IReadOnlyList<UsageTrace>)ChartType.GetField("traces", All)!.GetValue(chart)!;
    /// <summary>Clicks the strip where slot <paramref name="index"/> is drawn, through its own click handling (offscreen pages have no input source).</summary>
    private static void ClickSlot(TrafficStripChart chart, int index)
    {
        var root = Root(chart);
        var page = Assert.IsType<UsageHistoryPage>(root); Assert.Same(page.ApplicationsTab, page.Tabs.SelectedItem);
        Layout(root, root.RenderSize.Width > 0 ? root.RenderSize.Width : 1050, root.RenderSize.Height > 0 ? root.RenderSize.Height : 1100);
        chart.SlotAt(new Point());
        var plot = chart.PlotRect; double y = plot.Top + plot.Height / 2;
        double? x = null;
        for (double at = plot.Left; at <= plot.Right && x is null; at += 0.05) if (chart.SlotAt(new(at, y)) == index) x = at;
        Assert.True(x.HasValue, $"No point on the strip selects slot {index}.");
        chart.HandleClick(new(x.Value, y));
        Assert.Equal(index, chart.SelectedIndex);
    }
    private static FrameworkElement Root(DependencyObject element)
    {
        while (VisualTreeHelper.GetParent(element) is { } parent) element = parent;
        return (FrameworkElement)element;
    }
    private static double Top(FrameworkElement element, UsageHistoryPage page) => element.TransformToAncestor(page).Transform(new Point()).Y;
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    /// <summary>Presses a key on an element as the keyboard does: the preview first, then the key itself unless it was handled.</summary>
    private static void Press(UIElement target, Key key)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get => null!; set { } }
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed => false;
    }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    /// <summary>Pumps the dispatcher until <paramref name="done"/> holds, for up to 5 s.</summary>
    private static void WaitFor(Func<bool> done)
    {
        if (!done())
        {
            var frame = new DispatcherFrame(); var deadline = System.Diagnostics.Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { if (done() || deadline.Elapsed >= TimeSpan.FromSeconds(5)) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        Assert.True(done(), "The Usage history page did not get there within 5 s.");
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
