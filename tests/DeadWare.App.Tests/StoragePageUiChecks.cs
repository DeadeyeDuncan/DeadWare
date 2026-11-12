using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Offscreen checks for the Storage page; run from the shared STA UI test with the real styles loaded.</summary>
internal static class StoragePageUiChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const long Gb = 1_000_000_000;
    private static readonly StorageDrive Ssd = new("S1", "S1", "Fast NVMe", 0, "NVMe", 1000 * Gb, false,
        [new("C", "System", 500 * Gb, 200 * Gb), new("D", "Data", 500 * Gb, 300 * Gb)]);
    private static readonly StorageDrive Stick = new("live-1", null, "USB Stick", 1, "USB", 64 * Gb, true,
        [new("E", "STICK", 64 * Gb, 60 * Gb)]) { IdentityNote = "no serial number" };
    private static readonly StorageHealth Failing = new("S1", "Failing", 41, 87, 13, 1200, 63, null);

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Narrow_layout_places_drives_above_detail();
            Page_lists_one_row_per_drive_with_badge();
            Selecting_a_row_fills_detail_with_volumes();
            Live_only_drive_shows_identity_note();
            Sensors_off_status_is_shown();
            Open_in_window_pops_out_selected_drive();
            Overview_disk_card_pops_out();
            Nine_drive_note_is_shown();
            Refresh_keeps_row_instances_and_focus();
            Exactly_one_row_shows_selection();
            Wide_layout_gives_chart_spare_height_and_scrolls_drives();
            Resizing_keeps_buttons_selection_and_chart();
            Tabs_default_to_volumes_and_show_health_on_selection();
            Empty_page_and_removed_last_drive_show_chart_placeholder();
            New_rows_use_current_density_and_volumes_keep_empty_state();
            Wheel_over_drives_scrolls_the_page_when_the_list_cannot();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static void Page_lists_one_row_per_drive_with_badge()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""));
        var rows = page.Rows.Children.OfType<Button>().ToArray();
        Assert.Equal(2, rows.Length);
        var first = Texts(rows[0]);
        Assert.Contains("Fast NVMe · NVMe · " + Ui.Bytes(1000 * Gb), first);
        Assert.Contains("41.0°C · Failing · Life 87%", first);
        Assert.Contains($"{Ui.Bytes(500 * Gb)} free of {Ui.Bytes(1000 * Gb)}", first);
        Assert.Contains(Descendants(rows[0]).OfType<ProgressBar>(), bar => Math.Abs(bar.Value - .5) < 1e-9);
        // A drive without a health entry reads dashes.
        Assert.Contains("— · — · Life —", Texts(rows[1]));
        Assert.Equal("S1", page.SelectedDriveId);
    }

    private static void Selecting_a_row_fills_detail_with_volumes()
    {
        // Rows follow drive Index, not enumeration order.
        var page = Page(Session([Stick, Ssd], [Failing], ""));
        Assert.Equal("S1", page.SelectedDriveId);
        Click(page.Rows.Children.OfType<Button>().ElementAt(1));
        Assert.Equal("live-1", page.SelectedDriveId);
        Click(page.Rows.Children.OfType<Button>().ElementAt(0));
        Assert.Equal("S1", page.SelectedDriveId);
        page.Tabs.SelectedItem = page.VolumesTab;
        page.UpdateLayout();
        var table = Texts(page.Volumes);
        Assert.Contains("LETTER", table); Assert.Contains("LABEL", table); Assert.Contains("USED", table); Assert.Contains("FREE", table);
        Assert.Contains("C", table); Assert.Contains("D", table); Assert.Contains("System", table);
        Assert.Contains(Ui.Bytes(300 * Gb), table); Assert.Contains(Ui.Bytes(200 * Gb), table);
        Assert.DoesNotContain("E", table);
        page.Tabs.SelectedItem = page.HealthTab;
        page.UpdateLayout();
        var smart = Texts(page.Smart);
        Assert.Contains("Power-on hours · 1200", smart); Assert.Contains("Highest temperature · 63.0°C", smart); Assert.Contains("Remaining life · 87%", smart);
        // The detail chart follows the selected drive only.
        var card = Descendants(page.Detail).OfType<ChartCard>().Single();
        Assert.Equal("S1", card.GetType().GetField("driveId", Fields)!.GetValue(card));
        // Refreshing keeps the selection by id.
        typeof(StoragePage).GetMethod("Refresh", Fields)!.Invoke(page, []);
        Assert.Equal("S1", page.SelectedDriveId);
    }

    private static void Live_only_drive_shows_identity_note()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""));
        var row = Texts(page.Rows.Children.OfType<Button>().ElementAt(1));
        Assert.Contains(row, t => t.Contains("no serial number") && t.Contains("removable"));
        Click(page.Rows.Children.OfType<Button>().ElementAt(1));
        page.Tabs.SelectedItem = page.HealthTab;
        page.UpdateLayout();
        var detail = Texts(page.Smart);
        Assert.Contains(detail, t => t.Contains("no serial number"));
        Assert.Contains("Power-on hours · —", detail);
    }

    private static void Sensors_off_status_is_shown()
    {
        var off = new StorageHealth("S1", "Unknown", null, null, null, null, null, "Unavailable · storage sensors off");
        var page = Page(Session([Ssd], [off], ""));
        Assert.Contains("storage sensors off", page.Status.Text);
        page.Tabs.SelectedItem = page.HealthTab;
        page.UpdateLayout();
        Assert.DoesNotContain(off.Unavailable!, Texts(page.Smart));
        var reported = Page(Session([Ssd], [Failing], "Storage enumeration failed"));
        Assert.Equal("Storage enumeration failed", reported.Status.Text);
        var separate = Page(Session([Ssd], [off], "Storage enumeration failed"));
        separate.Tabs.SelectedItem = separate.HealthTab;
        separate.UpdateLayout();
        Assert.Contains(off.Unavailable!, Texts(separate.Smart));
    }

    private static void Open_in_window_pops_out_selected_drive()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""));
        Click(page.Rows.Children.OfType<Button>().ElementAt(1));
        Window? shown = null;
        page.ShowWindow = w => shown = w;
        var open = Descendants(page).OfType<Button>().Single(b => b.Content as string == "Open in window ↗");
        Click(open);
        Assert.NotNull(shown);
        Assert.Equal("Disk · USB Stick", shown!.Title);
        var graph = Assert.IsType<CompactGraph>(shown.Content);
        Assert.Equal("live-1", graph.DriveId);
        Assert.Equal(page.SelectedDriveId, graph.DriveId);
        shown.Close();
    }

    private static void Overview_disk_card_pops_out()
    {
        var session = Session([Ssd], [Failing], "");
        session.GetType().GetField("captureWindows", Fields)!.SetValue(session, new Dictionary<Window, Action>());
        Window? shown = null;
        session.ShowWindow = w => shown = w;
        var card = new ChartCard(session, "storage", true, true);
        card.Measure(new Size(600, 300)); card.Arrange(new Rect(0, 0, 600, 300)); card.UpdateLayout();
        var pop = Descendants(card).OfType<Button>().Single(b => (b.ToolTip as string)?.StartsWith("Pop out", StringComparison.Ordinal) == true);
        Click(pop);
        Assert.IsType<GraphWindow>(shown);
        Assert.Equal("DeadWare · " + Ui.Title("storage"), shown!.Title);
        shown.Close();
    }

    private static void Nine_drive_note_is_shown()
    {
        var reading = new Reading(new(DateTimeOffset.Now, 0, new Dictionary<string, double?>()), [], [], [], "", "8 sensors · Standard user · Drives beyond eight are not recorded.", "", "", false);
        Assert.Equal("Drives beyond eight are not recorded.", StoragePage.StatusText(reading));
        var nine = Enumerable.Range(0, 9).Select(i => Ssd with { Id = "S" + i, Serial = "S" + i, Index = i }).ToList();
        var page = Page(Session(nine, [], "Drives beyond eight are not recorded."));
        Assert.Contains("Drives beyond eight are not recorded.", page.Status.Text);
    }

    private static void Refresh_keeps_row_instances_and_focus()
    {
        // The host test forbids native windows, so focus itself cannot be taken; keeping the same
        // button instances across polls is what preserves it (rows are never torn down).
        var page = Page(Session([Ssd, Stick], [Failing], ""));
        var before = page.Rows.Children.OfType<Button>().ToArray();
        Refresh(page); Refresh(page);
        var after = page.Rows.Children.OfType<Button>().ToArray();
        Assert.Equal(2, after.Length);
        Assert.Same(before[0], after[0]); Assert.Same(before[1], after[1]);
        Click(before[1]);
        Assert.Same(before[1], page.Rows.Children.OfType<Button>().ElementAt(1));
    }

    private static void Exactly_one_row_shows_selection()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""));
        var rows = page.Rows.Children.OfType<Button>().ToArray();
        void Expect(int index)
        {
            Assert.Single(rows, r => Equals(r.Tag, "active"));
            Assert.Equal("active", rows[index].Tag);
            var accent = ((SolidColorBrush)Application.Current.Resources["Accent"]).Color;
            Assert.Equal(accent, ((SolidColorBrush)rows[index].BorderBrush).Color);
            Assert.All(rows.Where((_, i) => i != index), r => Assert.NotEqual(accent, (r.BorderBrush as SolidColorBrush)?.Color));
        }
        Expect(0);
        Click(rows[1]);
        Expect(1);
    }

    private static void Refresh(StoragePage page) => typeof(StoragePage).GetMethod("Refresh", Fields)!.Invoke(page, []);

    private static void Narrow_layout_places_drives_above_detail()
    {
        foreach (double width in new[] { 1000d, 900d })
        {
            var page = Page(Session([Ssd, Stick], [Failing], ""), width);
            var body = Assert.IsType<Grid>(Assert.IsType<ScrollViewer>(page.Content).Content);
            var panel = body.Children.OfType<Grid>().Single(child => Descendants(child).Contains(page.Rows));
            Assert.Equal(1, Grid.GetRow(panel));
            Assert.Equal(2, Grid.GetRow(page.Detail));
            Assert.True(body.Children.IndexOf(panel) < body.Children.IndexOf(page.Detail));
            Assert.True(panel.TranslatePoint(new Point(), body).Y + panel.ActualHeight <= page.Detail.TranslatePoint(new Point(), body).Y);
            Assert.All(page.Rows.Children.OfType<Button>(), row =>
            {
                Assert.Equal(new Thickness(12, 8, 12, 8), row.Padding);
                Assert.Equal(new Thickness(0, 0, 0, 6), row.Margin);
            });
        }
    }

    private static void Wide_layout_gives_chart_spare_height_and_scrolls_drives()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""), 1300, 700);
        var body = Assert.IsType<Grid>(Assert.IsType<ScrollViewer>(page.Content).Content);
        var detail = Assert.IsType<Grid>(page.Detail.Child);
        var panel = detail.Children.OfType<Grid>().Single(child => Grid.GetColumn(child) == 1);
        Assert.Equal(280, detail.ColumnDefinitions[1].Width.Value);
        Assert.InRange(panel.ActualWidth, 271.9, 272.1);
        Assert.Contains("DRIVES", Texts(panel));
        var scroll = Descendants(panel).OfType<ScrollViewer>().Single();
        Assert.Same(page.Rows, scroll.Content);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.True(page.Detail.ActualHeight >= 360);
        Assert.Equal(370, body.RowDefinitions[2].MinHeight);
        var chart = Descendants(page.Detail).OfType<ChartCard>().Single();
        Assert.True(chart.ActualHeight > 260);
        Assert.True(double.IsNaN(chart.Height));
        Assert.Equal(0, chart.MinHeight);
        var chartColumn = Assert.IsType<Grid>(chart.Parent);
        Assert.True(chartColumn.RowDefinitions[Grid.GetRow(chart)].Height.IsStar);
        double before = chart.ActualHeight;
        Layout(page, 1300, 1000);
        Assert.True(chart.ActualHeight > before + 250);
        Assert.Equal(Visibility.Collapsed, page.Status.Visibility);

        var drives = Enumerable.Range(0, 12).Select(i => Ssd with { Id = "S" + i, Index = i }).ToArray();
        var many = Page(Session(drives, [], "Drives beyond eight are not recorded."), 1300, 700);
        var viewport = Assert.IsType<ScrollViewer>(many.Content);
        var manyBody = Assert.IsType<Grid>(viewport.Content);
        var side = Descendants(many.Detail).OfType<ScrollViewer>().Single(s => ReferenceEquals(s.Content, many.Rows));
        Assert.True(double.IsFinite(manyBody.Height));
        Assert.InRange(manyBody.Height, 370, 700);
        Assert.True(many.Detail.ActualHeight >= 360);
        Assert.True(side.ScrollableHeight > 0);
        Assert.InRange(viewport.ScrollableHeight, 0, 1);
        side.ScrollToBottom(); Pump(); many.UpdateLayout();
        Assert.True(side.VerticalOffset > 0);
    }

    private static void Wheel_over_drives_scrolls_the_page_when_the_list_cannot()
    {
        // Narrow: the drive list has nothing to scroll, so a wheel over a drive row must reach the page viewport.
        var drives = Enumerable.Range(0, 12).Select(i => Ssd with { Id = "S" + i, Index = i }).ToArray();
        var page = Page(Session(drives, [], ""), 900, 500);
        var viewport = Assert.IsType<ScrollViewer>(page.Content);
        Assert.True(viewport.ScrollableHeight > 0);
        var inner = Descendants(page).OfType<ScrollViewer>().Single(s => ReferenceEquals(s.Content, page.Rows));
        Assert.InRange(inner.ScrollableHeight, 0, .5);
        page.Rows.Children.OfType<Button>().First().RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Pump(); page.UpdateLayout();
        Assert.True(viewport.VerticalOffset > 0, "The wheel over a drive row scrolls the page.");
        // Wide: the list scrolls itself first; a wheel at its top going up falls through to the page.
        var wide = Page(Session(drives, [], ""), 1300, 500);
        var wideViewport = Assert.IsType<ScrollViewer>(wide.Content);
        var side = Descendants(wide.Detail).OfType<ScrollViewer>().Single(s => ReferenceEquals(s.Content, wide.Rows));
        Assert.True(side.ScrollableHeight > 0);
        wide.Rows.Children.OfType<Button>().First().RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
        Pump(); wide.UpdateLayout();
        Assert.True(side.VerticalOffset > 0 || wideViewport.VerticalOffset == 0, "A list that can scroll keeps the wheel.");
    }

    private static void Resizing_keeps_buttons_selection_and_chart()
    {
        var page = Page(Session([Ssd, Stick], [Failing], ""), 1300, 700);
        var rows = page.Rows.Children.OfType<Button>().ToArray();
        Click(rows[1]);
        var chart = Descendants(page.Detail).OfType<ChartCard>().Single();
        // The page owns 12 DIP of horizontal outer margin; 1111 / 1112 offer 1099 / 1100 to the page itself.
        foreach (var (width, beside) in new[] { (1300d, true), (900d, false), (1300d, true), (1111d, false), (1112d, true) })
        {
            Layout(page, width, 700);
            Refresh(page);
            Layout(page, width, 700);
            Assert.Equal("live-1", page.SelectedDriveId);
            Assert.Same(chart, Descendants(page.Detail).OfType<ChartCard>().Single());
            Assert.Same(rows[0], page.Rows.Children[0]); Assert.Same(rows[1], page.Rows.Children[1]);
            var detail = Assert.IsType<Grid>(page.Detail.Child);
            Assert.Equal(beside ? 280 : 0, detail.ColumnDefinitions[1].Width.Value);
            Assert.All(rows, row =>
            {
                Assert.Equal(beside ? new Thickness(10, 6, 10, 6) : new Thickness(12, 8, 12, 8), row.Padding);
                Assert.Equal(new Thickness(0, 0, 0, beside ? 4 : 6), row.Margin);
            });
            if (!beside)
            {
                var body = Assert.IsType<Grid>(Assert.IsType<ScrollViewer>(page.Content).Content);
                var panel = body.Children.OfType<Grid>().Single(child => Descendants(child).Contains(page.Rows));
                Assert.Equal(1, Grid.GetRow(panel));
                Assert.True(body.Children.IndexOf(panel) < body.Children.IndexOf(page.Detail));
            }
        }
    }

    private static void Tabs_default_to_volumes_and_show_health_on_selection()
    {
        var page = Page(Session([Ssd], [Failing], ""));
        Assert.Same(page.VolumesTab, page.Tabs.SelectedItem);
        Assert.Equal("Volumes", page.VolumesTab.Header); Assert.Equal("Health", page.HealthTab.Header);
        Assert.Contains(page.Volumes, Descendants(page)); Assert.DoesNotContain(page.Smart, Descendants(page));
        Assert.Equal(new[] { 1d, 2, 1, 1 }, page.Volumes.ColumnDefinitions.Select(column => column.Width.Value).ToArray());
        page.Tabs.SelectedItem = page.HealthTab;
        page.UpdateLayout();
        Assert.Contains(page.Smart, Descendants(page)); Assert.DoesNotContain(page.Volumes, Descendants(page));
        Assert.Contains("Power-on hours · 1200", Texts(page.Smart));
        Assert.Contains("Highest temperature · 63.0°C", Texts(page.Smart));
        Assert.Contains("Remaining life · 87%", Texts(page.Smart));
    }

    private static void Empty_page_and_removed_last_drive_show_chart_placeholder()
    {
        var session = Session([], [], "");
        var page = Page(session);
        var placeholder = Descendants(page.Detail).OfType<TextBlock>().Single(text => text.Text == "Select a drive.");
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.Contains("No drives detected", Texts(page.Rows));
        SetStorage(session, [Ssd], [Failing]); Refresh(page); Layout(page, 1000, 1400);
        Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
        Assert.Single(Descendants(page.Detail).OfType<ChartCard>());
        SetStorage(session, [], []); Refresh(page); Layout(page, 1000, 1400);
        Assert.Null(page.SelectedDriveId);
        Assert.Same(placeholder, Descendants(page.Detail).OfType<TextBlock>().Single(text => text.Text == "Select a drive."));
        Assert.Equal(Visibility.Visible, placeholder.Visibility);
        Assert.Empty(Descendants(page.Detail).OfType<ChartCard>());
        page.Tabs.SelectedItem = page.HealthTab;
        page.UpdateLayout();
        Assert.Empty(Texts(page.Smart));
    }

    private static void New_rows_use_current_density_and_volumes_keep_empty_state()
    {
        var session = Session([Ssd], [Failing], "");
        var page = Page(session, 1300, 700);
        SetStorage(session, [Ssd, Stick with { Volumes = [] }], [Failing]); Refresh(page); Layout(page, 1300, 700);
        var added = page.Rows.Children.OfType<Button>().Last();
        Assert.Equal(new Thickness(10, 6, 10, 6), added.Padding);
        Assert.Equal(new Thickness(0, 0, 0, 4), added.Margin);
        Assert.Contains("No volumes", Texts(added));
        Assert.Contains("USB Stick", System.Windows.Automation.AutomationProperties.GetName(added));
        Click(added);
        page.Tabs.SelectedItem = page.VolumesTab;
        page.UpdateLayout();
        Assert.Contains("No mounted volumes", Texts(page.Volumes));
    }

    private static void SetStorage(UiSession session, IReadOnlyList<StorageDrive> drives, IReadOnlyList<StorageHealth> health)
    {
        var collector = session.Collector;
        collector.GetType().GetField("latest", Fields)!.SetValue(collector, session.Current! with { Storage = new(drives, health, "") });
    }

    private static StoragePage Page(UiSession session, double width = 1000, double height = 1400)
    {
        var page = new StoragePage(session);
        Layout(page, width, height);
        return page;
    }

    private static void Layout(StoragePage page, double width, double height)
    {
        page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); page.UpdateLayout();
        Pump();
        page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); page.UpdateLayout();
    }

    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static List<string> Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();

    private static UiSession Session(IReadOnlyList<StorageDrive> drives, IReadOnlyList<StorageHealth> health, string status)
    {
        void Set(object target, string field, object value) => target.GetType().GetField(field, Fields)!.SetValue(target, value);
        var history = new SampleHistory();
        var epoch = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        for (int second = -10; second <= 0; second++)
            history.Add(new(epoch.AddSeconds(second), second, new Dictionary<string, double?> { ["storage/S1/read"] = 1000, ["storage/S1/write"] = 500 }));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector, "clock", new Stopwatch()); Set(collector, "<History>k__BackingField", history);
        Set(collector, "latest", new Reading(history.Read()[^1], [], [], [], "Fixture CPU", "", "", "", false) { Storage = new(drives, health, status) });
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session, "<Collector>k__BackingField", collector);
        Set(session, "<Settings>k__BackingField", new UserSettings { HistorySeconds = 60 });
        return session;
    }

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
