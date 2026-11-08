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

/// <summary>Offscreen checks of the Alerts page and the Usage history hand-off. No store, hardware or native window is involved.</summary>
internal static class AlertsUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Assembly = typeof(Collector).Assembly;

    internal static void Run(Application app)
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        typeof(Collector).GetField("clock", All)!.SetValue(collector, new System.Diagnostics.Stopwatch());
        typeof(Collector).GetField("latest", All)!.SetValue(collector, new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()),
            [new("/gpu/0", "Fixture GPU")], [], [], "CPU", "", "", "", false));
        var cpu = new AlertRule("cpu-rule", AlertKind.Metric, UsageMetrics.CpuTemperature, 85, 15, true, now);
        var app1 = new AlertRule("app-rule", AlertKind.ApplicationDaily, "chrome.exe", 2_000_000_000, 1, false, now);
        var settings = new UserSettings { Alerts = [cpu, app1], AlertNotifications = true };
        typeof(UiSession).GetField("<Collector>k__BackingField", All)!.SetValue(session, collector);
        typeof(UiSession).GetField("<Settings>k__BackingField", All)!.SetValue(session, settings);
        var usage = new UsageHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture");
        typeof(UiSession).GetField("<Usage>k__BackingField", All)!.SetValue(session, usage);
        typeof(UiSession).GetField("<Alerts>k__BackingField", All)!.SetValue(session, new AlertService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture", usage, () => null));
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight", "Blackout" })
        {
            Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
            foreach (bool empty in new[] { true, false })
            {
                var page = new AlertsPage(session);
                var episodes = empty ? Array.Empty<AlertEpisode>() :
                [
                    new AlertEpisode("open", "cpu-rule", AlertKind.Metric, AlertRules.Describe(cpu), now.AddMinutes(-20), null, 91),
                    new AlertEpisode("done", "cpu-rule", AlertKind.Metric, AlertRules.Describe(cpu), now.AddHours(-5), now.AddHours(-4), 88, true),
                    new AlertEpisode("gone", "old-rule", AlertKind.NetworkRate, "Upload ≥ 5.0 MB/s for 5 minutes", now.AddDays(-2), now.AddDays(-2).AddMinutes(9), 6_200_000),
                ];
                var log = new AlertLogView(now.AddDays(-7), now, episodes, empty ? 0 : 12, 4096);
                page.Apply(new(empty ? [] : settings.Alerts, !empty, log, "Fixture saved locally."));
                if (!empty) page.ApplySuggestion(cpu, new(new(92, 61, 0.021, 10_080), new(95, 60, 0.018, 43_200)));
                double width = empty ? 620 : 1050;
                Layout(page, width, 900);
                var bitmap = new RenderTargetBitmap((int)width, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                string text = Text(page);
                Assert.Contains("Rules", text); Assert.Contains("Recent alerts", text); Assert.Contains("Show tray notifications for alerts", text);
                var toggle = (CheckBox)typeof(AlertsPage).GetField("notifications", All)!.GetValue(page)!;
                Assert.Equal(!empty, toggle.IsChecked);
                var buttons = Descendants(page).OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
                if (empty)
                {
                    Assert.Contains("No rules yet", text); Assert.Contains("No alerts in this period", text);
                    Assert.DoesNotContain("Acknowledge", buttons); Assert.DoesNotContain("View in Usage history", buttons);
                }
                else
                {
                    Assert.Contains("CPU temperature ≥ 85°C for 15 minutes", text); Assert.Contains("chrome.exe ≥ 2.0 GB in a day", text);
                    var ruleToggles = Descendants(page).OfType<CheckBox>().Where(c => c.Tag is string).ToArray();
                    Assert.Equal(new bool?[] { true, false }, ruleToggles.Select(c => c.IsChecked));
                    Assert.Contains("3 of 12 episodes shown", text);
                    Assert.Contains("peak 91°C · open", text); Assert.Contains("peak 88°C · acknowledged", text); Assert.Contains("peak 6200000", text);
                    Assert.Equal(2, buttons.Count(b => b == "Acknowledge")); Assert.Equal(3, buttons.Count(b => b == "View in Usage history"));
                    Assert.Equal(2, buttons.Count(b => b == "Edit")); Assert.Equal(2, buttons.Count(b => b == "Remove"));
                    Assert.Contains("highest minute 92°C", text); Assert.Contains("2.1% of 10,080 minutes", text);
                    DateTimeOffset? requested = null; session.UsageDayRequested += at => requested = at;
                    Descendants(page).OfType<Button>().First(b => b.Content?.ToString() == "View in Usage history").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(now.AddMinutes(-20), requested);
                    Assert.Contains("Storage: 4.1 KB", text);
                }
                if (output is not null)
                {
                    Directory.CreateDirectory(output);
                    using var stream = File.Create(Path.Combine(output, $"alerts-{theme}-{(empty ? "empty" : "rules")}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                }
            }
        }
        Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, ["Midnight"]);
        CheckEditor(session);
        CheckUsageHandoff(session);
        CheckHistoryClearRereads(session);
    }

    /// <summary>An alert-log clear starting or ending re-reads the loaded page's log; other stores do not, nor does an unloaded page.</summary>
    private static void CheckHistoryClearRereads(UiSession session)
    {
        var page = new AlertsPage(session);
        int reads = 0;
        typeof(AlertsPage).GetField("readLog", All)!.SetValue(page, (Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>>)((from, to) => { reads++; return Task.FromResult(new AlertLogView(from, to, [], 0, 0)); }));
        typeof(AlertsPage).GetField("showing", All)!.SetValue(page, (Func<bool>)(() => true));
        void Raise(HistoryClearStore store) => ((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged", All)!.GetValue(session))?.Invoke(store);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); int count = reads;
        Raise(HistoryClearStore.Alerts); Assert.Equal(count + 1, reads);
        Raise(HistoryClearStore.Connections); Raise(HistoryClearStore.Usage); Raise(HistoryClearStore.ProcessHistory); Assert.Equal(count + 1, reads);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Raise(HistoryClearStore.Alerts); Assert.Equal(count + 1, reads);
    }

    private static void CheckEditor(UiSession session)
    {
        var page = new AlertsPage(session);
        Layout(page, 900, 600);
        var kind = (ComboBox)typeof(AlertsPage).GetField("kind", All)!.GetValue(page)!;
        var target = (ComboBox)typeof(AlertsPage).GetField("target", All)!.GetValue(page)!;
        var threshold = (TextBox)typeof(AlertsPage).GetField("threshold", All)!.GetValue(page)!;
        var application = (TextBox)typeof(AlertsPage).GetField("application", All)!.GetValue(page)!;
        var duration = (ComboBox)typeof(AlertsPage).GetField("duration", All)!.GetValue(page)!;
        Assert.Null(page.Draft(out string error)); Assert.Contains("number", error);
        threshold.Text = "85"; duration.SelectedIndex = 2;
        var draft = page.Draft(out error)!;
        Assert.Equal((AlertKind.Metric, UsageMetrics.CpuTemperature, 85.0, 15), (draft.Kind, draft.Target, draft.Threshold, draft.Minutes));
        kind.SelectedIndex = 1; target.SelectedIndex = 1; threshold.Text = "2.5";
        draft = page.Draft(out error)!;
        Assert.Equal((AlertKind.NetworkRate, UsageMetrics.Upload, 2_500_000.0), (draft.Kind, draft.Target, draft.Threshold));
        kind.SelectedIndex = 2; threshold.Text = "1500";
        Assert.Null(page.Draft(out error)); Assert.Contains("executable", error);
        application.Text = " Steam.exe ";
        draft = page.Draft(out error)!;
        Assert.Equal(("Steam.exe", 1_500_000_000.0, 1), (draft.Target, draft.Threshold, draft.Minutes));
        kind.SelectedIndex = 3; threshold.Text = "20";
        draft = page.Draft(out error)!;
        Assert.Equal((AlertKind.Battery, "", 20.0), (draft.Kind, draft.Target, draft.Threshold));
        var unit = (TextBlock)typeof(AlertsPage).GetField("unit", All)!.GetValue(page)!;
        Assert.Contains("battery", unit.Text);
    }

    private static void CheckUsageHandoff(UiSession session)
    {
        var page = new UsageHistoryPage(session, false);
        Layout(page, 900, 700);
        page.ShowDay(DateTimeOffset.UtcNow.AddDays(-3).AddHours(-1));
        var range = (ComboBox)typeof(UsageHistoryPage).GetField("range", All)!.GetValue(page)!;
        Assert.Equal(0, range.SelectedIndex);
        Assert.Equal(3, (int)typeof(UsageHistoryPage).GetField("offset", All)!.GetValue(page)!);
        page.ShowDay(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(0, (int)typeof(UsageHistoryPage).GetField("offset", All)!.GetValue(page)!);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(t => t.Text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
