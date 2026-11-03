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
            Page_lists_one_row_per_drive_with_badge();
            Selecting_a_row_fills_detail_with_volumes();
            Live_only_drive_shows_identity_note();
            Sensors_off_status_is_shown();
            Open_in_window_pops_out_selected_drive();
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
        var page = Page(Session([Stick, Ssd], [Failing], ""));
        Assert.Equal("live-1", page.SelectedDriveId);
        Click(page.Rows.Children.OfType<Button>().ElementAt(1));
        Assert.Equal("S1", page.SelectedDriveId);
        page.UpdateLayout();
        var table = Texts(page.Volumes);
        Assert.Contains("LETTER", table); Assert.Contains("LABEL", table); Assert.Contains("USED", table); Assert.Contains("FREE", table);
        Assert.Contains("C", table); Assert.Contains("D", table); Assert.Contains("System", table);
        Assert.Contains(Ui.Bytes(300 * Gb), table); Assert.Contains(Ui.Bytes(200 * Gb), table);
        Assert.DoesNotContain("E", table);
        var smart = Texts(page.Detail);
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
        page.UpdateLayout();
        var detail = Texts(page.Detail);
        Assert.Contains(detail, t => t.Contains("no serial number"));
        Assert.Contains("Power-on hours · —", detail);
    }

    private static void Sensors_off_status_is_shown()
    {
        var off = new StorageHealth("S1", "Unknown", null, null, null, null, null, "Unavailable · storage sensors off");
        var page = Page(Session([Ssd], [off], ""));
        Assert.Contains("storage sensors off", page.Status.Text);
        var reported = Page(Session([Ssd], [Failing], "Storage enumeration failed"));
        Assert.Equal("Storage enumeration failed", reported.Status.Text);
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

    private static StoragePage Page(UiSession session)
    {
        var page = new StoragePage(session);
        page.Measure(new Size(1000, 1400)); page.Arrange(new Rect(0, 0, 1000, 1400)); page.UpdateLayout();
        return page;
    }

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
