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

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Overview_has_seven_cards_in_four_rows();
            Storage_card_draws_read_and_write_and_per_drive_lines();
            Storage_card_without_drives_shows_no_drives_detected();
            Storage_hover_lists_drives_by_rate();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Overview_has_seven_cards_in_four_rows()
    {
        var session = Session([Hdd, Nvme]);
        var page = new OverviewPage(session, new TextBlock());
        page.Measure(new Size(900, 1100)); page.Arrange(new Rect(0, 0, 900, 1100)); page.UpdateLayout();
        var grid = Descendants(page).OfType<UniformGrid>().First();
        Assert.Equal(4, grid.Rows); Assert.Equal(2, grid.Columns);
        var kinds = grid.Children.OfType<FrameworkElement>().Where(c => c.GetType().Name == "ChartCard")
            .Select(c => (string)c.GetType().GetField("kind", Fields)!.GetValue(c)!).ToArray();
        Assert.Equal(new[] { "cpu", "gpu", "cputemp", "gputemp", "memory", "network", "storage" }, kinds);
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
