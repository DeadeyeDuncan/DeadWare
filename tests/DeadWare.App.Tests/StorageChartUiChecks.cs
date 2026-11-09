using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
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
            Overview_breakpoints_use_page_width_with_and_without_scrollbars();
            Overview_refits_when_only_the_viewport_height_changes();
            Overview_footer_owns_spacing_and_refits_when_memory_wraps();
            Overview_adds_a_card_per_drive_and_follows_the_drive_set();
            Overview_populated_cards_stay_readable_at_breakpoints();
            Overview_reflow_preserves_cards_and_popout_destinations();
            Overview_drive_card_is_titled_with_the_model_and_pops_out_that_drive();
            Overview_card_stats_sit_beside_value();
            Storage_card_draws_read_and_write_and_per_drive_lines();
            Storage_card_traces_removable_drives_too();
            Storage_card_without_drives_shows_no_drives_detected();
            Storage_card_subtitle_holds_the_busiest_drive();
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

    private static OverviewPage Page(UiSession session, double width = 900, double height = 1100, TextBlock? memory = null)
    {
        var page = new OverviewPage(session, memory ?? Ui.Text("Memory history", 11, "Muted"));
        Layout(page, width, height);
        return page;
    }

    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }

    private static void Layout(OverviewPage page, double width, double height)
    {
        page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height));
        for (int i = 0; i < 3; i++) { page.UpdateLayout(); Pump(); }
        page.UpdateLayout();
    }

    private static Border Footer(OverviewPage page) => Assert.IsType<Border>(Assert.IsType<StackPanel>(page.Content).Children[1]);
    private static ChartCard[] CardElements(OverviewPage page) => Cards(page).Grid.Children.OfType<ChartCard>().ToArray();

    private static void AssertFit(OverviewPage page, int columns, int rows, bool scrolls)
    {
        var grid = Cards(page).Grid; var footer = Footer(page);
        Assert.Equal(columns, grid.Columns); Assert.Equal(rows, grid.Rows);
        Assert.Equal(Math.Max(rows * 270, Math.Floor(page.ViewportHeight - footer.ActualHeight - footer.Margin.Top - footer.Margin.Bottom)), grid.Height);
        Assert.Equal(scrolls ? Visibility.Visible : Visibility.Collapsed, page.ComputedVerticalScrollBarVisibility);
        Assert.Equal(scrolls, page.ScrollableHeight > 0);
        var before = (grid.Columns, grid.Rows, grid.Height, page.ViewportHeight, page.ExtentHeight, page.ComputedVerticalScrollBarVisibility);
        for (int i = 0; i < 2; i++)
        {
            page.UpdateLayout(); Pump(); page.UpdateLayout();
            Assert.Equal(before, (grid.Columns, grid.Rows, grid.Height, page.ViewportHeight, page.ExtentHeight, page.ComputedVerticalScrollBarVisibility));
        }
    }

    private static void Overview_breakpoints_use_page_width_with_and_without_scrollbars()
    {
        foreach (var (width, columns, rows) in new[] { (899, 1, 7), (900, 2, 4), (1499, 2, 4), (1500, 3, 3) })
        {
            var page = Page(Session([]), width, 700);
            // Using ViewportWidth instead would move the 900/1500 cases below their breakpoints when the scrollbar appears.
            Assert.Equal(width, page.ActualWidth); Assert.True(page.ViewportWidth < page.ActualWidth);
            AssertFit(page, columns, rows, true);
            Layout(page, width, 2600); AssertFit(page, columns, rows, false);
            Layout(page, width, 1100); AssertFit(page, columns, rows, width != 1500);
        }
    }

    private static void Overview_refits_when_only_the_viewport_height_changes()
    {
        var page = Page(Session([]), 1500); var before = CardElements(page);
        AssertFit(page, 3, 3, false);
        Layout(page, 1500, 700); AssertFit(page, 3, 3, true);
        Layout(page, 1500, 1100); AssertFit(page, 3, 3, false);
        Assert.Equal(before, CardElements(page));
    }

    private static void Overview_footer_owns_spacing_and_refits_when_memory_wraps()
    {
        var memory = Ui.Text("Memory history", 11, "Muted"); var page = Page(Session([]), 1500, 1100, memory);
        Layout(page, 899, 1100); Layout(page, 1500, 1100);
        var stack = Assert.IsType<StackPanel>(page.Content); Assert.Equal(2, stack.Children.Count);
        Assert.Same(Cards(page).Grid, stack.Children[0]); var footer = Footer(page);
        Assert.Equal(new Thickness(9, 8, 9, 12), footer.Margin); Assert.Equal(new Thickness(12, 6, 12, 6), footer.Padding);
        var lines = Assert.IsType<StackPanel>(footer.Child); Assert.Equal(2, lines.Children.Count); Assert.Same(memory, lines.Children[0]);
        Assert.Equal(new Thickness(0), memory.Margin);
        var note = Assert.IsType<TextBlock>(lines.Children[1]); Assert.Equal("— indicates an unavailable reading.", note.Text);
        Assert.Equal(new Thickness(0, 3, 0, 0), note.Margin); Assert.Equal(11, note.FontSize);
        AssertFit(page, 3, 3, false);
        double gridHeight = Cards(page).Grid.Height, footerHeight = footer.ActualHeight, memoryHeight = memory.ActualHeight;
        memory.Text = string.Join(" ", Enumerable.Repeat("Memory history uses 128 MB.", 12));
        page.UpdateLayout(); Pump(); page.UpdateLayout(); Pump(); page.UpdateLayout();
        Assert.True(memory.ActualHeight > memoryHeight, "The shell's memory text must wrap inside the footer.");
        Assert.True(Cards(page).Grid.Height < gridHeight);
        Assert.InRange(Math.Abs((gridHeight - Cards(page).Grid.Height) - (footer.ActualHeight - footerHeight)), 0, 1);
        AssertFit(page, 3, 3, false);
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
        foreach (var (width, height, columns, rows) in new[] { (900, 1100, 2, new[] { 5, 5, 4 }), (1500, 1000, 3, new[] { 4, 3, 3 }), (899, 2500, 1, new[] { 10, 9, 7 }) })
        {
            var session = Session([Nvme, Usb, Hdd]); var page = Page(session, width, height); var original = CardElements(page);
            var (grid, kinds, drives) = Cards(page);
            Assert.Equal(FixedKinds.Concat(["storage", "storage", "storage"]), kinds);
            Assert.Equal<string?>([null, null, null, null, null, null, null, "HDD1", "NVME1", "Stick#2"], drives.AsEnumerable());
            AssertFit(page, columns, rows[0], true);
            // Mutations cross the scroll threshold without resizing, and retained drive cards keep their history/state.
            SetDrives(session, [Nvme, Hdd]); typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
            Layout(page, width, height); (grid, kinds, drives) = Cards(page);
            Assert.Equal(FixedKinds.Concat(["storage", "storage"]), kinds);
            Assert.Equal<string?>([null, null, null, null, null, null, null, "HDD1", "NVME1"], drives.AsEnumerable());
            Assert.Equal(original.Take(9), CardElements(page)); AssertFit(page, columns, rows[1], width == 900);
            // Duplicate ids (trimmed serials can repeat): [S] → [S,S] gives the second entry its own card; [S,S] → [S] keeps the first.
            var twin = Nvme with { Index = 9 }; SetDrives(session, [Nvme, Hdd, twin]); typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
            Layout(page, width, height); var withTwin = CardElements(page); Assert.Equal(10, withTwin.Length); Assert.Equal(original.Take(9), withTwin.Take(9)); Assert.NotSame(withTwin[8], withTwin[9]);
            SetDrives(session, [Nvme, Hdd]); typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []); Layout(page, width, height);
            Assert.Equal(original.Take(9), CardElements(page));
            var before = CardElements(page); typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
            Layout(page, width, height); Assert.Equal(before, CardElements(page)); AssertFit(page, columns, rows[1], width == 900);
            SetDrives(session, []); typeof(OverviewPage).GetMethod("SyncDrives", Fields)!.Invoke(page, []);
            Layout(page, width, height); Assert.Equal(FixedKinds, Cards(page).Kinds);
            Assert.Equal(original.Take(7), CardElements(page)); AssertFit(page, columns, rows[2], width == 900);
        }
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

    private static void Overview_populated_cards_stay_readable_at_breakpoints()
    {
        var longDrive = Nvme with { Model = string.Join(" ", Enumerable.Repeat("Very long NVMe drive model", 6)) };
        foreach (int width in new[] { 899, 900, 1499, 1500 })
        {
            var page = Page(Session([longDrive]), width);
            foreach (var card in CardElements(page))
            {
                typeof(ChartCard).GetMethod("UpdateReadings", Fields)!.Invoke(card, []);
                string kind = (string)typeof(ChartCard).GetField("kind", Fields)!.GetValue(card)!;
                typeof(ChartCard).GetMethod("UpdateStatistics", Fields)!.Invoke(card, [kind is "network" or "storage" ? new Summary(100_000, 5_000_000, 1_500_000) : new Summary(12, 87, 49)]);
            }
            Layout(page, width, 1100);
            foreach (var card in CardElements(page))
            {
                T Field<T>(string name) => (T)typeof(ChartCard).GetField(name, Fields)!.GetValue(card)!;
                string kind = Field<string>("kind");
                void Inside(FrameworkElement element)
                {
                    var bounds = new Rect(element.TranslatePoint(new Point(), card), new Size(element.ActualWidth, element.ActualHeight));
                    Assert.True(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= card.ActualWidth + .5 && bounds.Bottom <= card.ActualHeight + .5,
                        $"{width} {kind} {element.GetType().Name} {bounds} outside card {card.ActualWidth}x{card.ActualHeight}");
                }
                foreach (var control in Descendants(card).OfType<FrameworkElement>().Where(e => e is Button || e.GetType().Name == "GraphColorPicker")) Inside(control);
                var title = Field<TextBlock>("title"); Inside(title);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming); Assert.Equal(title.Text, title.ToolTip);
                if (Field<string?>("driveId") is not null)
                {
                    Assert.Equal(longDrive.Model, title.Text);
                    var fullTitle = new TextBlock { Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
                    fullTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Assert.True(fullTitle.DesiredSize.Width > title.ActualWidth);
                }
                var stats = Field<TextBlock[]>("overviewStats"); Assert.All(stats, s => { Assert.NotEqual("—", s.Text); Inside(s); });
                Inside((FrameworkElement)((FrameworkElement)stats[0].Parent).Parent);
                var chart = Field<HistoryChart>("chart"); Inside(chart);
                Assert.True(chart.ActualHeight > 0 && chart.ActualHeight >= .4 * card.ActualHeight, $"{width} {kind} chart {chart.ActualHeight} / card {card.ActualHeight}");
                // The seeded values, not just the unit: an unavailable reading still prints "left RPM".
                if (kind == "cputemp") { var pump = Field<TextBlock>("pump"); Assert.Equal(Visibility.Visible, pump.Visibility); Assert.Contains("2400", pump.Text); Inside(pump); }
                if (kind == "gputemp") { var fan = Field<TextBlock>("fan"); Assert.Equal(Visibility.Visible, fan.Visibility); Assert.Contains("1500", fan.Text); Inside(fan); }
                // Primary reading and subtitle: present, inside the card, and not shrunk below legibility by the reading's Viewbox.
                var subtitle = Field<TextBlock>("subtitle"); Inside(subtitle);
                if (kind == "network")
                {
                    // The network card replaces the single reading with two Viewbox-scaled rate readings; each must stay legible.
                    foreach (var box in Descendants(card).OfType<Viewbox>()) { Inside(box); var reading = (FrameworkElement)box.Child; var rendered = reading.TransformToAncestor(card).TransformBounds(new Rect(0, 0, reading.ActualWidth, reading.ActualHeight)); Assert.True(rendered.Height >= 18, $"{width} network rate reading rendered {rendered.Height} DIP tall"); }
                }
                else
                {
                    var value = Field<TextBlock>("value"); Assert.NotEqual("—", value.Text); Inside(value);
                    var scaled = value.TransformToAncestor(card).TransformBounds(new Rect(0, 0, value.ActualWidth, value.ActualHeight));
                    Assert.True(scaled.Height >= 18, $"{width} {kind} reading rendered {scaled.Height} DIP tall");
                    // Stacked boxes may share an edge; only a real overlap fails.
                    Assert.True(scaled.Bottom <= subtitle.TranslatePoint(new Point(), card).Y + .5, $"{width} {kind} reading {scaled.Bottom} overlaps subtitle at {subtitle.TranslatePoint(new Point(), card).Y}");
                }
            }
        }
    }

    private static void Overview_reflow_preserves_cards_and_popout_destinations()
    {
        var session = Session([Hdd, Nvme]); var page = Page(session, 1500); var original = CardElements(page);
        AssertFit(page, 3, 3, false);
        Layout(page, 899, 1100); AssertFit(page, 1, 9, true); Assert.Equal(original, CardElements(page));
        Layout(page, 1500, 1100); AssertFit(page, 3, 3, false); Assert.Equal(original, CardElements(page));
        typeof(UiSession).GetField("captureWindows", Fields)!.SetValue(session, new Dictionary<Window, Action>());
        Window? shown = null; session.ShowWindow = w => shown = w;
        foreach (var (index, tooltip, title, drive) in new[] { (6, "Pop out " + Ui.Title("storage"), "DeadWare · " + Ui.Title("storage"), (string?)null), (8, "Pop out this drive", "Disk · " + Nvme.Model, Nvme.Id) })
        {
            shown = null;
            Descendants(original[index]).OfType<Button>().Single(b => b.ToolTip as string == tooltip).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            try
            {
                var window = Assert.IsType<GraphWindow>(shown); Assert.Equal(title, window.Title);
                Assert.Equal(drive, Assert.IsType<CompactGraph>(window.Content).DriveId);
            }
            finally { shown?.Close(); }
        }
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
        // The per-drive lines are tints of the disk read colour (never the CPU core palette), and the axis is a bytes/s scale.
        var pens = Drawings(drawing).Select(d => d.Pen?.Brush).OfType<SolidColorBrush>().Select(b => b.Color).ToHashSet();
        Assert.Contains(Theme.DriveBrush(0, 2).Color, pens); Assert.Contains(Theme.DriveBrush(1, 2).Color, pens);
        Assert.Equal(ThemeTokens.CoreTint(((SolidColorBrush)Theme.Brush("Graph/diskread")).Color, 0, 2, SystemParameters.HighContrast), Theme.DriveBrush(0, 2).Color);
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

    private static void Storage_card_subtitle_holds_the_busiest_drive()
    {
        // The named drive changes only after another drive has led for BusiestHold polls; an idle tie keeps the current one.
        var session = Session([Hdd, Nvme]); var card = Card(session);
        string Subtitle() => ((TextBlock)card.GetType().GetField("subtitle", Fields)!.GetValue(card)!).Text;
        void Update() => typeof(ChartCard).GetMethod("UpdateReadings", Fields)!.Invoke(card, []);
        Assert.Equal("Fast NVMe · 2 drives", Subtitle());
        SetRates(session, hdd: 9_000_000, nvme: 1_000);
        for (int i = 0; i < ChartCard.BusiestHold - 1; i++) { Update(); Assert.Equal("Fast NVMe · 2 drives", Subtitle()); }
        Update(); Assert.Equal("Slow HDD · 2 drives", Subtitle());
        SetRates(session, hdd: 1_000, nvme: 9_000_000);
        for (int i = 0; i < ChartCard.BusiestHold; i++) Update();
        Assert.Equal("Fast NVMe · 2 drives", Subtitle());
        SetRates(session, hdd: 0, nvme: 0);
        for (int i = 0; i < ChartCard.BusiestHold + 1; i++) { Update(); Assert.Equal("Fast NVMe · 2 drives", Subtitle()); }
    }

    private static void SetRates(UiSession session, double hdd, double nvme)
    {
        var collector = session.Collector;
        var latest = (Reading)collector.GetType().GetField("latest", Fields)!.GetValue(collector)!;
        var values = new Dictionary<string, double?>(latest.Sample.Values) { ["storage/HDD1/read"] = hdd, ["storage/HDD1/write"] = 0, ["storage/NVME1/read"] = nvme, ["storage/NVME1/write"] = 0 };
        collector.GetType().GetField("latest", Fields)!.SetValue(collector, latest with { Sample = latest.Sample with { Values = values } });
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
            var values = new Dictionary<string, double?>
            {
                ["cpu/usage"] = 42, ["cpu/temperature"] = 64, ["cpu/pump"] = 2400,
                ["gpu/usage"] = 57, ["gpu/temperature"] = 68, ["gpu/fan"] = 1500,
                ["memory/usage"] = 48, ["memory/used"] = 8_000_000_000, ["memory/total"] = 16_000_000_000,
                ["network/download"] = 5_100_000, ["network/upload"] = 1_000_000
            };
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
        Set(collector, "latest", new Reading(history.Read()[^1], [new("gpu", "Fixture GPU")], [], [], "Fixture CPU", "", "", "", false) { Storage = new(drives, [], "") });
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session, "<Collector>k__BackingField", collector);
        Set(session, "<Settings>k__BackingField", new UserSettings { HistorySeconds = 60, SelectedGpuId = "gpu", ShowGpuFanSpeed = true, ShowCpuPumpSpeed = true });
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
