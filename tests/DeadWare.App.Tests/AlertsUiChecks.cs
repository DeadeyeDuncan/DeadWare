using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
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
        CheckFirstUse(session);
    }

    /// <summary>First network use on the Alerts page in the three themes: the nine rule lines, the rule row, the editor, the log rows,
    /// the Don't alert list and the Not counted note.</summary>
    private static void CheckFirstUse(UiSession session)
    {
        var settings = session.Settings;
        var saved = (settings.Alerts, settings.FirstUseIgnoredApps, settings.ExcludedApps);
        var failed = typeof(AlertService).GetField("failed", All)!;
        var culture = CultureInfo.CurrentCulture;
        var zone = TimeZoneInfo.Local;
        var fu = AlertRules.DefaultFirstNetworkUse(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var appRule = settings.Alerts.Single(r => r.Kind == AlertKind.ApplicationDaily);
        DateTimeOffset Local(int day, int hour = 0) { var t = new DateTime(2026, 11, day, hour, 0, 0); return new DateTimeOffset(t, zone.GetUtcOffset(t)); }
        var now = Local(3, 12);
        static FirstUseStatus St(DateTimeOffset? start, DateTimeOffset? end, int? known, bool restored = true) => new(restored, restored, start, end, known);
        var none = new AlertLogView(now.AddDays(-7), now, [], 0, 0);
        const string notCounted = "Not counted: this app is in Don't record these apps.";
        string[] Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToArray();
        // The direct form: the learning date is the day before the end (Thursday 00:00), in the given zone and culture.
        var utcEnd = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("Learning quietly through Wednesday 4 November · 12 apps known.", FirstNetworkUse.RuleLine(false, St(utcEnd.AddDays(-3), utcEnd, 12), true, true, false,
            utcEnd.AddDays(-1), TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-GB")));
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            foreach (string theme in new[] { "Midnight", "Daylight", "Blackout" })
            {
                Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
                AlertsPage NewPage()
                {
                    var created = new AlertsPage(session);
                    typeof(AlertsPage).GetField("clock", All)!.SetValue(created, (Func<DateTimeOffset>)(() => now));
                    return created;
                }
                var start = Local(2, 9); var end = Local(5);
                var learned = St(start.AddDays(-10), end.AddDays(-10), 12);
                var states = new (bool Failed, bool Enabled, FirstUseStatus Status, bool Elevated, bool Capture, string Line)[]
                {
                    (true, true, learned, true, true, "Paused: the alert log is unavailable."),
                    (false, true, St(null, null, null, false), true, true, "Starting: reading the known-app list."),
                    (false, false, St(start, end, 1), true, true, "Switched off: apps are learned without alerts · 1 app known."),
                    (false, true, St(null, null, 12), false, false, "Waiting for per-app capture, which runs while DeadWare runs as administrator. Learning starts then."),
                    (false, true, St(null, null, 12), true, false, "Waiting for per-app capture to start. Learning starts then."),
                    (false, true, St(start, end, 12), true, true, "Learning quietly through Wednesday 4 November · 12 apps known."),
                    (false, true, learned, true, true, "Watching for new apps · 12 apps known."),
                    (false, true, learned, false, false, "Not watching: per-app capture needs DeadWare to run as administrator · 12 apps known."),
                    (false, true, learned, true, false, "Not watching: per-app capture is not running · 12 apps known."),
                };
                foreach (var state in states)
                {
                    var page = NewPage();
                    failed.SetValue(session.Alerts, state.Failed);
                    try { page.Apply(new([state.Enabled ? fu : fu with { Enabled = false }], true, none, "Fixture saved locally.", FirstUse: state.Status, Elevated: state.Elevated, CaptureAvailable: state.Capture)); }
                    finally { failed.SetValue(session.Alerts, false); }
                    Layout(page, 1050, 900);
                    var lines = Texts(page);
                    Assert.Contains(state.Line, lines);
                    Assert.Single(lines, l => states.Any(s => s.Line == l));
                    var buttons = Descendants(page).OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
                    Assert.Contains("Remove", buttons); Assert.DoesNotContain("Edit", buttons);
                }

                // The editor: the kind hides Reading, Executable, At or above and For, parses no threshold and refuses a second rule.
                settings.Alerts = [fu, appRule];
                {
                    var page = NewPage(); Layout(page, 900, 600);
                    var kind = (ComboBox)typeof(AlertsPage).GetField("kind", All)!.GetValue(page)!;
                    var target = (ComboBox)typeof(AlertsPage).GetField("target", All)!.GetValue(page)!;
                    var threshold = (TextBox)typeof(AlertsPage).GetField("threshold", All)!.GetValue(page)!;
                    var application = (TextBox)typeof(AlertsPage).GetField("application", All)!.GetValue(page)!;
                    var duration = (ComboBox)typeof(AlertsPage).GetField("duration", All)!.GetValue(page)!;
                    var suggestion = (TextBlock)typeof(AlertsPage).GetField("suggestion", All)!.GetValue(page)!;
                    var editorError = (TextBlock)typeof(AlertsPage).GetField("editorError", All)!.GetValue(page)!;
                    var add = (Button)typeof(AlertsPage).GetField("add", All)!.GetValue(page)!;
                    kind.SelectedIndex = 4; threshold.Text = "";
                    Assert.Equal("First network use by an app", kind.SelectedItem!.ToString());
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)target.Parent).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)application.Parent).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)((FrameworkElement)threshold.Parent).Parent).Visibility);
                    Assert.Equal(Visibility.Collapsed, ((FrameworkElement)duration.Parent).Visibility);
                    Assert.Equal("New apps are learned quietly for 3 calendar days after per-app capture first runs; after that each app's first network use is logged once.", suggestion.Text);
                    var draft = page.Draft(out string error)!;
                    Assert.Equal((AlertKind.FirstNetworkUse, "", ""), (draft.Kind, draft.Target, error));
                    add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("A first network use rule already exists.", editorError.Text);
                    Assert.Equal(2, settings.Alerts.Count);
                    // Not counted, in the editor, while the Executable text is excluded.
                    var note = (TextBlock)typeof(AlertsPage).GetField("notCounted", All)!.GetValue(page)!;
                    kind.SelectedIndex = 2; application.Text = "chrome.exe";
                    Assert.Equal("", note.Text);
                    settings.ExcludedApps = ["chrome.exe"];
                    application.Text = "Chrome.exe";
                    Assert.Equal(notCounted, note.Text); Assert.Equal(Visibility.Visible, note.Visibility);
                    application.Text = "Steam.exe";
                    Assert.Equal("", note.Text); Assert.Equal(Visibility.Collapsed, note.Visibility);
                    settings.ExcludedApps = [];
                }

                // Not counted under an ApplicationDaily rule whose target is excluded.
                {
                    var page = NewPage();
                    page.Apply(new([appRule], true, none, "Fixture saved locally."));
                    Layout(page, 1050, 900);
                    Assert.DoesNotContain(notCounted, Texts(page));
                    settings.ExcludedApps = ["chrome.exe"];
                    page.Apply(new([appRule], true, none, "Fixture saved locally."));
                    Layout(page, 1050, 900);
                    Assert.Contains(notCounted, Texts(page));
                    settings.ExcludedApps = [];
                }

                // Log rows.
                var chrome = new AlertEpisode("fu-chrome", fu.Id, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary("chrome.exe"), now.AddDays(-29).AddSeconds(37), now.AddDays(-29).AddSeconds(37), 1, false, "chrome.exe");
                var steam = new AlertEpisode("fu-steam", fu.Id, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary("Steam.exe"), now.AddDays(-31), now.AddDays(-31), 1, true, "Steam.exe");
                var discord = new AlertEpisode("fu-discord", fu.Id, AlertKind.FirstNetworkUse, FirstNetworkUse.Summary("Discord.exe"), now.AddDays(-2), now.AddDays(-2), 1, true, "Discord.exe");
                settings.FirstUseIgnoredApps = ["Discord.exe"];
                {
                    var page = NewPage();
                    page.Apply(new([fu], true, new(now.AddDays(-30), now, [chrome, steam, discord], 3, 4096), "Fixture saved locally.", FirstUse: learned, Elevated: true, CaptureAvailable: true, IgnoredApps: settings.FirstUseIgnoredApps));
                    Layout(page, 1050, 900);
                    var bitmap = new RenderTargetBitmap(1050, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                    var rows = (StackPanel)typeof(AlertsPage).GetField("episodes", All)!.GetValue(page)!;
                    DockPanel Row(AlertEpisode episode) => rows.Children.OfType<DockPanel>().Single(d => Descendants(d).OfType<TextBlock>().Any(t => t.Text == episode.Summary));
                    string[] Buttons(DockPanel row) => Descendants(row).OfType<Button>().Select(b => b.Content?.ToString() ?? "").ToArray();
                    var chromeRow = Row(chrome);
                    Assert.Equal(new[] { "View in Connections", "Don't alert for this app", "Acknowledge" }, Buttons(chromeRow));
                    Assert.Contains($"{chrome.Start.ToLocalTime():g} · first network use", Texts(chromeRow));
                    var title = Descendants(chromeRow).OfType<TextBlock>().First(t => t.Text == chrome.Summary);
                    Assert.Same(page.FindResource("Ink"), title.Foreground);
                    var dontAlert = Descendants(chromeRow).OfType<Button>().Single(b => b.Content?.ToString() == "Don't alert for this app");
                    Assert.Equal("Don't alert for chrome.exe", AutomationProperties.GetName(dontAlert));
                    ConnectionsHistoryRequest? request = null; void Requested(ConnectionsHistoryRequest r) => request = r;
                    session.ConnectionsHistoryRequested += Requested;
                    Descendants(chromeRow).OfType<Button>().Single(b => b.Content?.ToString() == "View in Connections").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    session.ConnectionsHistoryRequested -= Requested;
                    var minute = new DateTimeOffset(chrome.Start.Ticks - chrome.Start.Ticks % TimeSpan.TicksPerMinute, chrome.Start.Offset);
                    Assert.Equal(new ConnectionsHistoryRequest(minute.AddMinutes(-5), minute.AddMinutes(10), "chrome.exe"), request);
                    var steamRow = Row(steam);
                    Assert.Equal(new[] { "View in Usage history", "Don't alert for this app" }, Buttons(steamRow));
                    Assert.Contains($"{steam.Start.ToLocalTime():g} · first network use · acknowledged", Texts(steamRow));
                    var discordRow = Row(discord);
                    Assert.Equal(new[] { "View in Connections" }, Buttons(discordRow));
                    Assert.Contains($"{discord.Start.ToLocalTime():g} · first network use · acknowledged · on the Don't alert list", Texts(discordRow));
                    Assert.DoesNotContain(Texts(rows), t => t.Contains("peak", StringComparison.Ordinal));
                    // Don't alert for an acknowledged app adds it and leaves the episode alone.
                    Descendants(steamRow).OfType<Button>().Single(b => b.Content?.ToString() == "Don't alert for this app").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(new[] { "Discord.exe", "Steam.exe" }, settings.FirstUseIgnoredApps);
                }
                // With 256 listed, the button stays enabled and a click changes nothing but the action status.
                settings.FirstUseIgnoredApps = Enumerable.Range(0, 256).Select(i => $"app{i}.exe").ToList();
                {
                    var page = NewPage();
                    page.Apply(new([fu], true, new(now.AddDays(-30), now, [chrome], 1, 4096), "Fixture saved locally.", FirstUse: learned, Elevated: true, CaptureAvailable: true, IgnoredApps: settings.FirstUseIgnoredApps));
                    Layout(page, 1050, 900);
                    var button = Descendants(page).OfType<Button>().Single(b => b.Content?.ToString() == "Don't alert for this app");
                    Assert.True(button.IsEnabled);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(256, settings.FirstUseIgnoredApps.Count); Assert.DoesNotContain("chrome.exe", settings.FirstUseIgnoredApps);
                    var actionStatus = (TextBlock)typeof(AlertsPage).GetField("actionStatus", All)!.GetValue(page)!;
                    Assert.Equal("The Don't alert list holds 256 apps; remove one first.", actionStatus.Text);
                }
                // The Not alerted list shows also with no first-use rule, sorted, each line with Alert again.
                settings.FirstUseIgnoredApps = ["zoom.exe", "chrome.exe", "Brave.exe"];
                {
                    var page = NewPage();
                    page.Apply(new([], true, none, "Fixture saved locally.", IgnoredApps: settings.FirstUseIgnoredApps));
                    Layout(page, 1050, 900);
                    var lines = Texts(page).ToList();
                    int heading = lines.IndexOf("Not alerted, even when new again:");
                    Assert.True(heading >= 0);
                    Assert.Equal(new[] { "Brave.exe", "chrome.exe", "zoom.exe" }, lines.Skip(heading + 1).Where(l => l != "Alert again").Take(3));
                    var again = Descendants(page).OfType<Button>().Where(b => b.Content?.ToString() == "Alert again").ToArray();
                    Assert.Equal(new[] { "Alert again for Brave.exe", "Alert again for chrome.exe", "Alert again for zoom.exe" }, again.Select(b => AutomationProperties.GetName(b)));
                    again[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(new[] { "zoom.exe", "Brave.exe" }, settings.FirstUseIgnoredApps);
                }
                settings.FirstUseIgnoredApps = [];
                settings.Alerts = saved.Alerts;
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            failed.SetValue(session.Alerts, false);
            (settings.Alerts, settings.FirstUseIgnoredApps, settings.ExcludedApps) = saved;
            Assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, ["Midnight"]);
        }
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
