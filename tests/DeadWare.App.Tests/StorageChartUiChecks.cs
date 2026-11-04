using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks for the Overview Disk card; run from the shared STA UI test with the real styles loaded.</summary>
internal static class StorageChartUiChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly StorageDrive Hdd = new("HDD1", "HDD1", "Slow HDD", 0, "SATA", 1000, false, []);
    private static readonly StorageDrive Nvme = new("NVME1", "NVME1", "Fast NVMe", 1, "NVMe", 1000, false, []);
    /// <summary>A removable stick without a serial: live-only, never persisted, still present on the overview.</summary>
    private static readonly StorageDrive Usb = new("Stick#2", null, "USB Stick", 2, "USB", 1000, true, []);

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Overview_has_seven_cards_in_four_rows();
            Overview_adds_a_card_per_drive_and_follows_the_drive_set();
            Overview_drive_card_is_titled_with_the_model_and_pops_out_that_drive();
            Overview_card_stats_sit_beside_value();
            Storage_card_draws_read_and_write_and_per_drive_lines();
            Storage_card_traces_removable_drives_too();
            Storage_card_without_drives_shows_no_drives_detected();
            Storage_hover_lists_drives_by_rate();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static readonly string[] FixedKinds = ["cpu", "gpu", "cputemp", "gputemp", "memory", "network", "storage"];

    private static (UniformGrid Grid, string[] Kinds, string?[] Drives) Cards(OverviewPage page)
    {
        var grid = Descendants(page).OfType<UniformGrid>().First();
        var cards = grid.Children.OfType<FrameworkElement>().Where(c => c.GetType().Name == "ChartCard").ToArray();
        return (grid, cards.Select(c => (string)c.GetType().GetField("kind", Fields)!.GetValue(c)!).ToArray(),
            cards.Select(c => (string?)c.GetType().GetField("driveId", Fields)!.GetValue(c)).ToArray());
    }

    private static OverviewPage Page(UiSession session)
    {
        var page = new OverviewPage(session, new TextBlock());
        page.Measure(new Size(900, 1100)); page.Arrange(new Rect(0, 0, 900, 1100)); page.UpdateLayout();
        return page;
    }

    private static void Overview_has_seven_cards_in_four_rows()
    {
        var (grid, kinds, drives) = Cards(Page(Session([])));
        Assert.Equal(4, grid.Rows); Assert.Equal(2, grid.Columns); Assert.Equal(4 * OverviewPage.RowHeight, grid.Height);
        Assert.Equal(FixedKinds, kinds);
        Assert.All(drives, Assert.Null);
    }

    private static void Overview_adds_a_card_per_drive_and_follows_the_drive_set()
    {
        // Drives follow the seven fixed cards by disk index, removable ones included; the grid grows a row for the ninth card.
        var session = Session([Nvme, Usb, Hdd]);
        var page = Page(session);
        var (grid, kinds, drives) = Cards(page);
        Assert.Equal(5, grid.Rows); Assert.Equal(5 * OverviewPage.RowHeight, grid.Height);
        Assert.Equal(FixedKinds.Concat(["storage", "storage", "storage"]), kinds);
        Assert.Equal([null, null, null, null, null, null, null, "HDD1", "NVME1", "Stick#2"], drives);
        // Unplugging the stick drops its card; the same set again rebuilds nothing.
        FrameworkElement[] CardElements() => Descendants(page).OfType<FrameworkElement>().Where(c => c.GetType().Name == "ChartCard").ToArray();
        SetDrives(session, [Hdd, Nvme]);
        typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
        (grid, kinds, drives) = Cards(page);
        Assert.Equal(5, grid.Rows); Assert.Equal(9, kinds.Length);
        Assert.Equal([null, null, null, null, null, null, null, "HDD1", "NVME1"], drives);
        var before = CardElements();
        typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
        Assert.Equal(before, CardElements());
        SetDrives(session, []);
        typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
        (grid, kinds, _) = Cards(page);
        Assert.Equal(4, grid.Rows); Assert.Equal(FixedKinds, kinds);
        Assert.Equal(1, OverviewPage.Rows(1)); Assert.Equal(4, OverviewPage.Rows(8)); Assert.Equal(5, OverviewPage.Rows(9));
    }

    private static void Overview_drive_card_is_titled_with_the_model_and_pops_out_that_drive()
    {
        var session = Session([Hdd, Usb]);
        Window? shown = null;
        session.ShowWindow = w => shown = w;
        var card = new ChartCard(session, "storage", true, true, "Stick#2");
        card.Measure(new Size(460, 300)); card.Arrange(new Rect(0, 0, 460, 300)); card.UpdateLayout();
        typeof(ChartCard).GetMethod("UpdateReadings", Fields)!.Invoke(card, []);
        Assert.Equal("USB Stick", ((TextBlock)typeof(ChartCard).GetField("title", Fields)!.GetValue(card)!).Text);
        Assert.Equal("USB · 1.0 KB · removable · ↑ 10.0 KB/s write", ((TextBlock)typeof(ChartCard).GetField("subtitle", Fields)!.GetValue(card)!).Text);
        Assert.Equal("50.0 KB/s", ((TextBlock)typeof(ChartCard).GetField("value", Fields)!.GetValue(card)!).Text);
        // No colour picker of its own: the drive shares the Disk card's read/write colours.
        Assert.DoesNotContain(Descendants(card), d => d.GetType().Name == "GraphColorPicker");
        var pop = Descendants(card).OfType<Button>().Single(b => (b.ToolTip as string) == "Pop out this drive");
        pop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var window = Assert.IsType<GraphWindow>(shown);
        Assert.Equal("Disk · USB Stick", window.Title);
        window.Close();
        // The total Disk card keeps its picker and its own pop-out.
        var total = new ChartCard(session, "storage", true, true);
        Assert.Contains(Descendants(total), d => d.GetType().Name == "GraphColorPicker");
        Assert.Contains(Descendants(total).OfType<Button>(), b => (b.ToolTip as string) == "Pop out " + Ui.Title("storage"));
    }

    private static void Overview_card_stats_sit_beside_value()
    {
        // Overview cards have no MIN/AVG/MAX footer row: the three stats sit on one line beside the 28px value
        // (under the two rate columns on the network card), and the chart row is the only star row.
        var session = Session([Hdd, Nvme]);
        foreach (string kind in new[] { "storage", "network" })
        {
            var card = new ChartCard(session, kind, false, true);
            card.Measure(new Size(480, 300)); card.Arrange(new Rect(0, 0, 480, 300)); card.UpdateLayout();
            Rect Bounds(FrameworkElement e) => new(e.TranslatePoint(new Point(0, 0), card), new Size(e.ActualWidth, e.ActualHeight));
            var rows = Descendants(card).OfType<Grid>().First(g => g.RowDefinitions.Count > 0).RowDefinitions;
            Assert.Equal(3, rows.Count); Assert.Equal(GridUnitType.Star, rows[2].Height.GridUnitType);
            var value = (TextBlock)card.GetType().GetField("value", Fields)!.GetValue(card)!;
            var subtitle = (TextBlock)card.GetType().GetField("subtitle", Fields)!.GetValue(card)!;
            var chart = (FrameworkElement)card.GetType().GetField("chart", Fields)!.GetValue(card)!;
            var stats = (TextBlock[])card.GetType().GetField("overviewStats", Fields)!.GetValue(card)!;
            Assert.Equal(3, stats.Length);
            Assert.Single(stats.Select(s => ((FrameworkElement)s.Parent).Parent).Distinct());
            Assert.True(chart.ActualHeight >= 120, $"{kind} chart {chart.ActualHeight}");
            string prefix = kind == "network" ? "↓ " : "";
            foreach (string label in new[] { "MIN", "AVG", "MAX" })
                Assert.Contains(Descendants(card).OfType<TextBlock>(), t => t.Text == prefix + label && t.FontSize < 11);
            var bounds = stats.Select(Bounds).ToArray();
            Assert.All(bounds, b => Assert.True(b.Width > 0 && b.Height > 0));
            Assert.All(bounds, b => Assert.True(Math.Abs(b.Bottom - bounds[0].Bottom) < .5, $"{kind} stats not on one line"));
            Assert.True(bounds[0].Right <= bounds[1].Left && bounds[1].Right <= bounds[2].Left);
            if (kind == "network")
            {
                var rates = Descendants(card).OfType<Grid>().First(g => g.ColumnDefinitions.Count == 2 && g.RowDefinitions.Count == 0);
                Assert.True(bounds[0].Top >= Bounds(rates).Bottom - .5, $"stats {bounds[0].Top} rates {Bounds(rates).Bottom}");
                Assert.True(bounds[0].Bottom <= Bounds(subtitle).Top + .5, $"stats {bounds[0].Bottom} subtitle {Bounds(subtitle).Top}");
            }
            else
            {
                Assert.Equal(28, value.FontSize);
                var v = Bounds(value);
                Assert.All(bounds, b => Assert.True(b.Top >= v.Top - .5 && b.Bottom <= v.Bottom + .5 && b.Left >= v.Right, $"{kind} stat {b} value {v}"));
            }
        }
    }

    private static void Storage_card_draws_read_and_write_and_per_drive_lines()
    {
        var session = Session([Hdd, Nvme]);
        var chart = Chart(session);
        var drawing = Draw(chart);
        var cache = chart.GetType().GetField("cache", Fields)!.GetValue(chart)!;
        var specs = ((Array)cache.GetType().GetProperty("Specs")!.GetValue(cache)!).Cast<object>().ToArray();
        string Metric(object spec) => (string)spec.GetType().GetProperty("Metric")!.GetValue(spec)!;
        bool Core(object spec) => (bool)spec.GetType().GetProperty("Core")!.GetValue(spec)!;
        Assert.Equal(4, specs.Length);
        Assert.Equal(new[] { "storage/total/read", "storage/total/write" }, specs.Where(s => !Core(s)).Select(Metric));
        Assert.Equal(new[] { "storage/HDD1/read", "storage/NVME1/read" }, specs.Where(Core).Select(Metric));
        // The per-drive lines are drawn with the core palette, and the axis is a bytes/s scale.
        var pens = Drawings(drawing).Select(d => d.Pen?.Brush).OfType<SolidColorBrush>().Select(b => b.Color).ToHashSet();
        Assert.Contains(Theme.CoreBrush(0, 2).Color, pens); Assert.Contains(Theme.CoreBrush(1, 2).Color, pens);
        Assert.Contains(Labels(drawing), l => l.EndsWith("MB/s", StringComparison.Ordinal));
    }

    private static void Storage_card_traces_removable_drives_too()
    {
        var session = Session([Hdd, Nvme, Usb]);
        var chart = Chart(session);
        Draw(chart);
        var cache = chart.GetType().GetField("cache", Fields)!.GetValue(chart)!;
        var specs = ((Array)cache.GetType().GetProperty("Specs")!.GetValue(cache)!).Cast<object>().ToArray();
        string? DriveId(object spec) => (string?)spec.GetType().GetProperty("DriveId")!.GetValue(spec);
        Assert.Equal(["HDD1", "NVME1", "Stick#2"], specs.Select(DriveId).OfType<string>());
        var plot = chart.PlotRect;
        chart.SetHover(new Point(plot.Right - 1, plot.Top + plot.Height / 2));
        Draw(chart);
        var lines = ((List<(string Text, Brush Color)>)chart.GetType().GetField("hoverLines", Fields)!.GetValue(chart)!).Select(l => l.Text).ToList();
        Assert.Contains("USB Stick · 50.0 KB/s ↓ 10.0 KB/s ↑", lines);
    }

    private static void Storage_card_without_drives_shows_no_drives_detected()
    {
        var empty = Card(Session([]));
        Assert.Contains("No drives detected", ((TextBlock)empty.GetType().GetField("subtitle", Fields)!.GetValue(empty)!).Text);
        var busy = Card(Session([Hdd, Nvme]));
        Assert.Equal("Fast NVMe · 2 drives", ((TextBlock)busy.GetType().GetField("subtitle", Fields)!.GetValue(busy)!).Text);
    }

    private static void Storage_hover_lists_drives_by_rate()
    {
        var session = Session([Hdd, Nvme]);
        var chart = Chart(session);
        Draw(chart);
        var plot = chart.PlotRect;
        chart.SetHover(new Point(plot.Right - 1, plot.Top + plot.Height / 2));
        Assert.True(chart.HoverIndex >= 0);
        Draw(chart);
        var lines = ((List<(string Text, Brush Color)>)chart.GetType().GetField("hoverLines", Fields)!.GetValue(chart)!).Select(l => l.Text).ToList();
        int nvme = lines.FindIndex(l => l == "Fast NVMe · 5.0 MB/s ↓ 1.0 MB/s ↑");
        int hdd = lines.FindIndex(l => l == "Slow HDD · 100.0 KB/s ↓ 0.0 B/s ↑");
        Assert.True(nvme >= 0, string.Join(" | ", lines)); Assert.True(hdd > nvme, string.Join(" | ", lines));
        Assert.Contains("5.1 MB/s", lines); Assert.Contains("1.0 MB/s", lines);
    }

    private static HistoryChart Chart(UiSession session)
    {
        var chart = new HistoryChart(session, "storage");
        chart.Measure(new Size(620, 240)); chart.Arrange(new Rect(0, 0, 620, 240)); chart.UpdateLayout();
        return chart;
    }

    private static FrameworkElement Card(UiSession session)
    {
        var card = new ChartCard(session, "storage", false, true);
        card.Measure(new Size(460, 300)); card.Arrange(new Rect(0, 0, 460, 300)); card.UpdateLayout();
        typeof(ChartCard).GetMethod("UpdateReadings", Fields)!.Invoke(card, []);
        return card;
    }

    private static UiSession Session(IReadOnlyList<StorageDrive> drives)
    {
        void Set(object target, string field, object value) => target.GetType().GetField(field, Fields)!.SetValue(target, value);
        var history = new SampleHistory();
        var epoch = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        for (int second = -60; second <= 0; second++)
        {
            var values = new Dictionary<string, double?>();
            if (drives.Count > 0)
            {
                values["storage/total/read"] = 5_100_000; values["storage/total/write"] = 1_000_000;
                values["storage/HDD1/read"] = 100_000; values["storage/HDD1/write"] = 0;
                values["storage/NVME1/read"] = 5_000_000; values["storage/NVME1/write"] = 1_000_000;
                foreach (var other in drives.Where(d => d.Id is not "HDD1" and not "NVME1"))
                { values[StorageMetrics.Key(other.Id, "read")] = 50_000; values[StorageMetrics.Key(other.Id, "write")] = 10_000; }
            }
            history.Add(new(epoch.AddSeconds(second), second, values));
        }
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector, "clock", new Stopwatch()); Set(collector, "<History>k__BackingField", history);
        Set(collector, "latest", new Reading(history.Read()[^1], [], [], [], "Fixture CPU", "", "", "", false) { Storage = new(drives, [], "") });
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session, "<Collector>k__BackingField", collector);
        Set(session, "<Settings>k__BackingField", new UserSettings { HistorySeconds = 60 });
        return session;
    }

    private static void SetDrives(UiSession session, IReadOnlyList<StorageDrive> drives)
    {
        var collector = session.Collector;
        var latest = (Reading)collector.GetType().GetField("latest", Fields)!.GetValue(collector)!;
        collector.GetType().GetField("latest", Fields)!.SetValue(collector, latest with { Storage = new(drives, [], "") });
    }

    private static DrawingGroup Draw(HistoryChart chart)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            typeof(HistoryChart).GetMethod("OnRender", Fields)!.Invoke(chart, [context]);
        return drawing;
    }

    private static IEnumerable<GeometryDrawing> Drawings(Drawing value) => value is GeometryDrawing g ? [g]
        : value is DrawingGroup group ? group.Children.SelectMany(Drawings) : [];

    private static IEnumerable<string> Labels(Drawing value) => value is GlyphRunDrawing glyph ? [new string(glyph.GlyphRun.Characters.ToArray())]
        : value is DrawingGroup group ? group.Children.SelectMany(Labels) : [];

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
