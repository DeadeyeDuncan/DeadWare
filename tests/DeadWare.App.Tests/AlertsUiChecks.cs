using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Alerts layout, editor and hand-off checks; temporary stores exercise acknowledgements and an offscreen native host verifies focus.</summary>
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
        CheckLayout(session, now);
        CheckNarrowEpisodes(session, now);
        CheckActionStatus(session, now);
        CheckEditorRoundTrip(session, now);
        CheckHostedLifecycle(session);
        CheckIgnoredTabs(session, now);
        CheckWheel(session, now);
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
                Select(page, false, width, 900);
                var bitmap = new RenderTargetBitmap((int)width, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                string text = Text(page);
                Assert.Equal("Rules", Tab(page, false).Header); Assert.Contains("Recent alerts", text); Assert.Contains("Show tray notifications for alerts", text);
                Assert.Contains("Every episode is logged in Recent alerts whether or not notifications are on.", text);
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

    private static void CheckLayout(UiSession session, DateTimeOffset now)
    {
        var page = new AlertsPage(session); Layout(page, 1050, 700);
        var body = Assert.IsType<Grid>(page.Content);
        Assert.Equal(3, body.RowDefinitions.Count);
        Assert.True(body.RowDefinitions[1].Height.IsStar);
        Assert.True(body.RowDefinitions[0].Height.IsAuto); Assert.True(body.RowDefinitions[2].Height.IsAuto);
        Assert.Equal(new Thickness(6, 10, 6, 10), page.Margin); Assert.Equal(new Thickness(), body.Margin);
        var logCard = Field<Border>(page, "logCard"); var tabbedCard = Field<Border>(page, "tabbedCard"); var statusCard = Field<Border>(page, "statusCard");
        var scroll = Field<ScrollViewer>(page, "episodeScroll"); var layout = Field<Grid>(page, "logLayout");
        Assert.Same(body, logCard.Parent); Assert.Same(body, tabbedCard.Parent); Assert.Same(body, statusCard.Parent);
        Assert.Equal((0, 1, 2), (Grid.GetRow(statusCard), Grid.GetRow(logCard), Grid.GetRow(tabbedCard)));
        Assert.Equal(Visibility.Collapsed, statusCard.Visibility); Assert.Equal(new Thickness(12, 6, 12, 6), statusCard.Padding);
        Assert.Equal(new Thickness(0, 0, 0, 10), statusCard.Margin); Assert.Equal(new Thickness(0, 0, 0, 10), logCard.Margin);
        Assert.Same(Field<TextBlock>(page, "actionStatus"), statusCard.Child);
        Assert.Same(page.FindResource("Warning"), Field<TextBlock>(page, "actionStatus").Foreground);
        Assert.Equal(TextWrapping.Wrap, Field<TextBlock>(page, "actionStatus").TextWrapping);
        Assert.Equal(360, logCard.MinHeight); Assert.True(logCard.ActualHeight >= 360);
        Assert.Equal(5, layout.RowDefinitions.Count); Assert.True(layout.RowDefinitions[2].Height.IsStar);
        Assert.Same(Field<StackPanel>(page, "episodes"), scroll.Content); Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility); Assert.Equal(new Thickness(0, 6, 0, 0), scroll.Margin);
        Assert.Equal(new Thickness(), Field<StackPanel>(page, "episodes").Margin);
        var heading = Assert.Single(layout.Children.OfType<DockPanel>(), child => Grid.GetRow(child) == 0);
        var toolbar = Assert.IsType<WrapPanel>(heading.Children[0]); Assert.Equal(Dock.Right, DockPanel.GetDock(toolbar));
        Assert.Contains(Field<ComboBox>(page, "logRange"), toolbar.Children.Cast<UIElement>());
        Assert.Equal("Recent alerts", Assert.IsType<TextBlock>(heading.Children[1]).Text);
        Assert.Same(Tab(page, false), Tabs(page).SelectedItem); Assert.Equal("Rules", Tab(page, false).Header);
        Assert.Same(Tabs(page), tabbedCard.Child); Assert.Same(Field<StackPanel>(page, "ignoredList"), Tab(page, true).Content);
        Assert.Equal(TimeSpan.FromSeconds(30), Field<DispatcherTimer>(page, "timer").Interval);
        page.Apply(Fixture(now, 2, 40)); Select(page, true, 1050, 700);
        Assert.True(double.IsFinite(body.Height)); Assert.InRange(body.Height, 370, page.ViewportHeight + 1);
        Assert.InRange(page.ScrollableHeight, 0, 1); Assert.True(scroll.ScrollableHeight > 0);
        Assert.InRange(scroll.ActualHeight + scroll.Margin.Top, layout.RowDefinitions[2].ActualHeight - 1, layout.RowDefinitions[2].ActualHeight + 1);
        double chrome = layout.Children.OfType<FrameworkElement>().Where(child => !ReferenceEquals(child, scroll)).Sum(child => child.DesiredSize.Height);
        Assert.InRange(body.RowDefinitions[1].MinHeight, Math.Max(370, chrome + 32 + 240 + 10) - 1, Math.Max(370, chrome + 32 + 240 + 10) + 1);
        double small = scroll.ViewportHeight; Layout(page, 1050, 1000); Assert.True(scroll.ViewportHeight > small + 250);
        Assert.InRange(body.Height, 370, page.ViewportHeight + 1);
        Layout(page, 1050, 700); Select(page, false, 1050, 700); page.Apply(Fixture(now, 20, 40)); Layout(page, 1050, 700);
        double many = body.Height; Assert.True(tabbedCard.ActualHeight > page.ViewportHeight); Assert.True(many > page.ViewportHeight);
        page.Apply(Fixture(now, 2, 40)); Layout(page, 1050, 700); Assert.True(body.Height < many - 200);
        page.Apply(Fixture(now, 20, 40)); Layout(page, 1050, 700); many = body.Height;
        Select(page, true, 1050, 700); Assert.True(body.Height < many - 200); Assert.InRange(body.Height, 370, page.ViewportHeight + 1);
        Select(page, false, 1050, 700); Assert.InRange(body.Height, many - 1, many + 1);
    }

    /// <summary>At minimum width every long first-use summary and all three actions remain reachable; replacing rows keeps scroll and keyboard focus.</summary>
    private static void CheckNarrowEpisodes(UiSession session, DateTimeOffset now)
    {
        var saved = session.Alerts; string folder = Path.Combine(Path.GetTempPath(), "deadware-alerts-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var firstUse = AlertRules.DefaultFirstNetworkUse(now.AddDays(-10));
        var list = Enumerable.Range(0, 40).Select(i => new AlertEpisode("narrow-" + i, firstUse.Id, AlertKind.FirstNetworkUse,
            $"Fixture application {i}: " + string.Join(" ", Enumerable.Repeat("A long first network use summary keeps all of its words readable beside the episode actions.", 3)),
            now.AddMinutes(-i - 1), now.AddMinutes(-i - 1), 0, false, $"fixture{i}.exe")).ToArray();
        string path = Path.Combine(folder, "alerts.db"); var store = new AlertStore(path); store.Initialize(); store.Append("fixture", list, now);
        var service = new AlertService(path, "fixture", session.Usage, () => null); Set(session, "<Alerts>k__BackingField", service);
        var page = new AlertsPage(session); Set(page, "clock", (Func<DateTimeOffset>)(() => now)); Set(page, "showing", (Func<bool>)(() => false));
        var host = new Window { Content = page, Width = 620, Height = 700, Left = -32000, Top = -32000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            host.Show(); host.Activate(); host.UpdateLayout(); Pump();
            Assert.True(Field<DispatcherTimer>(page, "timer").IsEnabled);
            AlertLogView Log() => store.Read("fixture", now.AddDays(-7), now.AddDays(1));
            AlertsPageData Data(AlertLogView log) => new([firstUse], false, log, "Fixture saved locally.", IgnoredApps: []);
            int reads = 0; Set(page, "readLog", (Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>>)((_, _) => { reads++; return Task.FromResult(Log()); }));
            Set(page, "showing", (Func<bool>)(() => true)); page.Apply(Data(Log())); Tabs(page).SelectedItem = Tab(page, false); Settle(host);
            { var timer = Field<DispatcherTimer>(page, "timer"); Assert.True(timer.IsEnabled); timer.Stop(); }// the 30 s refresh must not replace rows mid-assertion on a slow run
            var scroll = Field<ScrollViewer>(page, "episodeScroll"); var rows = Field<StackPanel>(page, "episodes");
            Assert.True(scroll.ScrollableHeight > 0); Assert.NotNull(PresentationSource.FromVisual(page));
            foreach (var row in rows.Children.OfType<Panel>())
            {
                var buttons = Descendants(row).OfType<Button>().ToArray();
                Assert.Equal(new[] { "View in Connections", "Don't alert for this app", "Acknowledge" }, buttons.Select(button => button.Content?.ToString()));
                foreach (var element in Descendants(row).OfType<FrameworkElement>().Where(element => element is Button or TextBlock)) Reach(scroll, element, host);
            }
            var bitmap = new RenderTargetBitmap(620, 700, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
            string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
            if (output is not null) { Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, "alerts-populated-620.png")); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
            var selectedRow = (Panel)rows.Children[18]; var acknowledge = Descendants(selectedRow).OfType<Button>().Single(button => Equals(button.Content, "Acknowledge"));
            // Scroll the page so the log card starts above the viewport: restoring focus to the list must not pull the page back up.
            page.ScrollToVerticalOffset(100); Settle(host); Assert.True(page.VerticalOffset > 0);
            Reach(scroll, acknowledge, host); Assert.True(acknowledge.Focus()); Settle(host); double offset = scroll.VerticalOffset, pageOffset = page.VerticalOffset;
            Assert.True(offset > 0); int before = reads; Dispatcher.CurrentDispatcher.Invoke(() => acknowledge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Until(() => reads > before && !Field<bool>(page, "refreshing")); Settle(host);
            Assert.True(Log().Episodes.Single(episode => episode.Id == "narrow-18").Acknowledged);
            Assert.InRange(scroll.VerticalOffset, Math.Min(offset, scroll.ScrollableHeight) - 1, Math.Min(offset, scroll.ScrollableHeight) + 1);
            Assert.InRange(page.VerticalOffset, pageOffset - 1, pageOffset + 1);
            AssertUsableFocus(scroll);
            // A later refresh shortens the list, so the retained offset must clamp to the new end without losing focus.
            scroll.ScrollToBottom(); Settle(host); offset = scroll.VerticalOffset;
            page.Apply(Data(new(now.AddDays(-7), now.AddDays(1), list.Take(2).ToArray(), 2, 4096))); Settle(host);
            Assert.InRange(scroll.VerticalOffset, Math.Min(offset, scroll.ScrollableHeight) - 1, Math.Min(offset, scroll.ScrollableHeight) + 1); AssertUsableFocus(scroll);
        }
        finally { host.Content = null; host.Close(); Set(session, "<Alerts>k__BackingField", saved); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static void AssertUsableFocus(ScrollViewer scroll)
    {
        var focused = Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement);
        Assert.True(focused.IsEnabled && focused.IsVisible && focused.Focusable);
        Assert.True(ReferenceEquals(focused, scroll) || focused is Button && Descendants(scroll).Contains(focused), "Focus must remain on the list or a current episode action after its old row is removed.");
    }

    private static void Reach(ScrollViewer scroll, FrameworkElement element, Window host)
    {
        var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().First(presenter => ReferenceEquals(presenter.TemplatedParent, scroll));
        var bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + bounds.Top); Settle(host);
        bounds = element.TransformToAncestor(viewport).TransformBounds(new Rect(element.RenderSize));
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, "Episode text and actions must have a usable size.");
        Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
            $"{element.GetType().Name} {(element is Button button ? button.Content : (element as TextBlock)?.Text)} must fit or scroll into the episode viewport: {bounds}, viewport {viewport.RenderSize}.");
    }

    private static void CheckActionStatus(UiSession session, DateTimeOffset now)
    {
        var saved = session.Alerts; string folder = Path.Combine(Path.GetTempPath(), "deadware-alert-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            // A directory in place of the database is deterministically unwritable as a database, even for an elevated test process.
            Set(session, "<Alerts>k__BackingField", new AlertService(folder, "fixture", session.Usage, () => null));
            var page = new AlertsPage(session); var data = Fixture(now, 20, 3); page.Apply(data); Select(page, false, 1050, 700);
            int reads = 0; Set(page, "showing", (Func<bool>)(() => true));
            Set(page, "readLog", (Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>>)((_, _) => { reads++; return Task.FromResult(data.Log); }));
            var card = Field<Border>(page, "statusCard"); Assert.Equal(Visibility.Collapsed, card.Visibility);
            page.ScrollToVerticalOffset(30); Layout(page, 1050, 700); double before = page.VerticalOffset;
            Wait(UiTask(page, "AcknowledgeAsync", data.Log.Episodes[0].Id)); Layout(page, 1050, 700);
            Assert.True(session.Alerts.Failed); Assert.Equal(1, reads); Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.StartsWith("The episode could not be marked as acknowledged. ", Field<TextBlock>(page, "actionStatus").Text);
            Assert.Contains(session.Alerts.Status, Field<TextBlock>(page, "actionStatus").Text);
            Assert.True(before > 0); Assert.Equal(before, page.VerticalOffset); Assert.Same(Tab(page, false), Tabs(page).SelectedItem);
            string path = Path.Combine(folder, "recovered.db"); var store = new AlertStore(path); store.Initialize(); store.Append("fixture", data.Log.Episodes, now);
            Set(session, "<Alerts>k__BackingField", new AlertService(path, "fixture", session.Usage, () => null));
            Wait(UiTask(page, "AcknowledgeAsync", data.Log.Episodes[0].Id)); Layout(page, 1050, 700);
            Assert.Equal(2, reads); Assert.Equal("", Field<TextBlock>(page, "actionStatus").Text); Assert.Equal(Visibility.Collapsed, card.Visibility);
            Assert.True(store.Read("fixture", now.AddDays(-7), now.AddDays(1)).Episodes.Single(episode => episode.Id == data.Log.Episodes[0].Id).Acknowledged);
        }
        finally { Set(session, "<Alerts>k__BackingField", saved); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static void CheckEditorRoundTrip(UiSession session, DateTimeOffset now)
    {
        var saved = session.Settings.Alerts; var savedService = session.Alerts;
        string folder = Path.Combine(Path.GetTempPath(), "deadware-alert-suggestion-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var rule = Fixture(now, 1, 0).Rules[0]; session.Settings.Alerts = [rule]; var page = new AlertsPage(session);
        Set(page, "showing", (Func<bool>)(() => false)); page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Select(page, false, 1050, 700);
        try
        {
            string[] names = ["kind", "target", "application", "threshold", "duration", "editorError", "suggestion", "add", "notifications"];
            var controls = names.Select(name => Field<FrameworkElement>(page, name)).ToArray();
            Descendants(page).OfType<Button>().Single(button => Equals(button.Content, "Edit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(session.Settings.Alerts, savedRule => savedRule.Id == rule.Id);
            var threshold = Field<TextBox>(page, "threshold"); var error = Field<TextBlock>(page, "editorError");
            threshold.Text = "invalid"; Field<Button>(page, "add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains("number", error.Text);
            threshold.Text = "85"; string errorText = error.Text;
            page.ApplySuggestion(rule, new(new(92, 61, .021, 10080), new(95, 60, .018, 43200))); string suggestion = Field<TextBlock>(page, "suggestion").Text;
            var draft = page.Draft(out _)!; Select(page, true, 1050, 700);
            Assert.DoesNotContain(controls[0], Descendants(page)); Assert.Contains(Field<StackPanel>(page, "ignoredList"), Descendants(page));
            Select(page, false, 1050, 700);
            for (int i = 0; i < names.Length; i++) { Assert.Same(controls[i], Field<FrameworkElement>(page, names[i])); Assert.Contains(controls[i], Descendants(page)); }
            var returned = page.Draft(out _)!; Assert.Equal((draft.Kind, draft.Target, draft.Threshold, draft.Minutes), (returned.Kind, returned.Target, returned.Threshold, returned.Minutes));
            Assert.Equal(errorText, error.Text); Assert.Equal(suggestion, Field<TextBlock>(page, "suggestion").Text);
            // Delay the real history service at initialization, so the page's actual asynchronous suggestion completes on the inactive tab.
            var usage = new UsageHistoryService(Path.Combine(folder, "usage.db"), "fixture"); Field<UsageHistoryStore>(usage, "store").Initialize();
            Set(session, "<Alerts>k__BackingField", new AlertService(Path.Combine(folder, "alerts.db"), "fixture", usage, () => null));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Set(usage, "initialization", gate.Task);
            int revision = Field<int>(page, "suggestionRevision");
            var pending = UiTask(page, "SuggestAsync", draft, revision);
            Select(page, true, 1050, 700); Assert.False(pending.IsCompleted); gate.SetResult(); Wait(pending);
            Assert.Same(Tab(page, true), Tabs(page).SelectedItem); Select(page, false, 1050, 700);
            Assert.Contains("Recorded so far", Field<TextBlock>(page, "suggestion").Text); Assert.Contains("no recorded minutes", Field<TextBlock>(page, "suggestion").Text);
            Assert.Equal(errorText, error.Text);
            var staleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Set(usage, "initialization", staleGate.Task);
            var stale = UiTask(page, "SuggestAsync", draft, Field<int>(page, "suggestionRevision"));
            threshold.Text = "86"; page.ApplySuggestion(rule, new(new(99, 70, .03, 100), new(99, 70, .03, 100))); string latest = Field<TextBlock>(page, "suggestion").Text;
            staleGate.SetResult(); Wait(stale); Assert.Equal(latest, Field<TextBlock>(page, "suggestion").Text);
        }
        finally
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); session.Settings.Alerts = saved; Set(session, "<Alerts>k__BackingField", savedService);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true);
        }
    }

    /// <summary>Real tab unload/load preserves a normally scheduled suggestion and the draft, without detaching the page's session subscriptions.</summary>
    private static void CheckHostedLifecycle(UiSession session)
    {
        var savedService = session.Alerts; bool savedNotifications = session.Settings.AlertNotifications;
        string folder = Path.Combine(Path.GetTempPath(), "deadware-alert-lifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Window { Width = 1050, Height = 800, Left = -32000, Top = -32000, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        try
        {
            // AlertsPage has no suggestion delegate seam: hold its real history read pending instead.
            var usage = new UsageHistoryService(Path.Combine(folder, "usage.db"), "fixture"); Field<UsageHistoryStore>(usage, "store").Initialize();
            Set(usage, "initialization", gate.Task);
            int suggestionWindows = 0;
            Set(session, "<Alerts>k__BackingField", new AlertService(Path.Combine(folder, "alerts.db"), "fixture", usage,
                () => { Interlocked.Increment(ref suggestionWindows); return "/gpu/0"; }));
            var page = new AlertsPage(session); host.Content = page;
            int reads = 0;
            Set(page, "readLog", (Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>>)((from, to) => { reads++; return Task.FromResult(new AlertLogView(from, to, [], 0, 0)); }));
            var editor = Assert.IsAssignableFrom<FrameworkElement>(Tab(page, false).Content);
            var loadedStates = new List<bool>();
            editor.Loaded += (_, _) => loadedStates.Add(editor.IsLoaded);
            editor.Unloaded += (_, _) => loadedStates.Add(editor.IsLoaded);
            host.Show(); Settle(host);
            Assert.True(page.IsLoaded); Assert.True(editor.IsLoaded); Assert.Equal(new[] { true }, loadedStates);
            var timer = Field<DispatcherTimer>(page, "timer"); Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval); Assert.True(timer.IsEnabled); timer.Stop();
            Assert.Same(Tab(page, false), Tabs(page).SelectedItem);
            // GPU selection makes the service's delegate count the normal suggestion entering each history window.
            Field<ComboBox>(page, "target").SelectedIndex = 2;
            var threshold = Field<TextBox>(page, "threshold"); var error = Field<TextBlock>(page, "editorError"); var suggestion = Field<TextBlock>(page, "suggestion");
            threshold.Text = "invalid"; Field<Button>(page, "add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Contains("number", error.Text);
            string errorText = error.Text; const string draftText = " 85 ";
            Assert.Equal(0, Volatile.Read(ref suggestionWindows));
            Dispatcher.CurrentDispatcher.Invoke(() => threshold.Text = draftText);
            Assert.Equal(1, Volatile.Read(ref suggestionWindows)); Assert.False(gate.Task.IsCompleted); Assert.Equal("", suggestion.Text);
            Tabs(page).SelectedItem = Tab(page, true); Settle(host);
            Assert.True(page.IsLoaded); Assert.False(editor.IsLoaded); Assert.Equal(new[] { true, false }, loadedStates);
            Assert.False(gate.Task.IsCompleted); Assert.Equal("", suggestion.Text);
            gate.SetResult(); Until(() => suggestion.Text.StartsWith("Recorded so far", StringComparison.Ordinal));
            Assert.Equal(2, Volatile.Read(ref suggestionWindows)); Assert.False(usage.Failed);
            Assert.False(editor.IsLoaded); Assert.Same(Tab(page, true), Tabs(page).SelectedItem);
            string completedSuggestion = suggestion.Text;
            Assert.Contains("Last 7 days: no recorded minutes.", completedSuggestion); Assert.Contains("Last 30 days: no recorded minutes.", completedSuggestion);
            Tabs(page).SelectedItem = Tab(page, false); Settle(host);
            Assert.True(editor.IsLoaded); Assert.Equal(new[] { true, false, true }, loadedStates);
            Assert.True(suggestion.IsVisible); Assert.Equal(completedSuggestion, suggestion.Text);
            Assert.Equal(draftText, threshold.Text); Assert.Equal(errorText, error.Text);
            var notifications = Field<CheckBox>(page, "notifications");
            foreach (bool ignored in new[] { false, true })
            {
                Tabs(page).SelectedItem = Tab(page, ignored); Settle(host); var selected = Tabs(page).SelectedItem;
                int before = reads;
                ((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged", All)!.GetValue(session))?.Invoke(HistoryClearStore.Alerts);
                Settle(host); Assert.Equal(before + 1, reads); Assert.Same(selected, Tabs(page).SelectedItem);
                bool changed = !session.Settings.AlertNotifications; Assert.NotEqual((bool?)changed, notifications.IsChecked);
                session.Settings.AlertNotifications = changed;
                ((Action?)typeof(UiSession).GetField("Changed", All)!.GetValue(session))?.Invoke();
                Assert.Equal(changed, notifications.IsChecked); Assert.Same(selected, Tabs(page).SelectedItem);
            }
        }
        finally
        {
            gate.TrySetCanceled(); host.Content = null; host.Close(); session.Settings.AlertNotifications = savedNotifications; Set(session, "<Alerts>k__BackingField", savedService);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true);
        }
    }

    private static void CheckIgnoredTabs(UiSession session, DateTimeOffset now)
    {
        var saved = session.Settings.FirstUseIgnoredApps; session.Settings.FirstUseIgnoredApps = [];
        var page = new AlertsPage(session); Set(page, "showing", (Func<bool>)(() => false)); page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        try
        {
            Select(page, false, 1050, 700); Assert.Equal("Don't alert", Tab(page, true).Header);
            session.IgnoreFirstUse("zebra.exe"); session.IgnoreFirstUse("Alpha.exe"); Layout(page, 1050, 700);
            Assert.Same(Tab(page, false), Tabs(page).SelectedItem); Assert.Equal("Don't alert · 2", Tab(page, true).Header);
            Assert.DoesNotContain(Field<StackPanel>(page, "ignoredList"), Descendants(page)); Select(page, true, 1050, 700);
            Assert.Contains("Not alerted, even when new again:", Text(page)); Assert.Contains("Alpha.exe", Text(page)); Assert.Contains("zebra.exe", Text(page));
            foreach (string name in new[] { "Alpha.exe", "zebra.exe" })
            {
                Descendants(page).OfType<Button>().Single(button => AutomationProperties.GetName(button) == $"Alert again for {name}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(page, 1050, 700);
            }
            Assert.Equal("Don't alert", Tab(page, true).Header); Assert.Equal(Visibility.Visible, Field<StackPanel>(page, "ignoredList").Visibility);
            Assert.Contains("No apps are kept quiet. Don't alert for this app on an episode adds one.", Text(page));
            Assert.DoesNotContain(Descendants(page).OfType<Button>(), button => Equals(button.Content, "Alert again"));
        }
        finally { page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); session.Settings.FirstUseIgnoredApps = saved; }
    }

    private static void CheckWheel(UiSession session, DateTimeOffset now)
    {
        var page = new AlertsPage(session); page.Apply(Fixture(now, 20, 1)); Select(page, false, 1050, 500);
        var scroll = Field<ScrollViewer>(page, "episodeScroll"); Assert.True(page.ScrollableHeight > 0); Assert.InRange(scroll.ScrollableHeight, 0, .5);
        Wheel(Field<StackPanel>(page, "episodes").Children[0], -120); Layout(page, 1050, 500); Assert.True(page.VerticalOffset > 0);
        page.Apply(Fixture(now, 20, 40)); Layout(page, 1050, 500); page.ScrollToTop(); scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); Layout(page, 1050, 500);
        double middle = scroll.VerticalOffset; Assert.True(middle > 0); Assert.True(middle < scroll.ScrollableHeight);
        Wheel(Field<StackPanel>(page, "episodes").Children[10], -120); Layout(page, 1050, 500);
        Assert.True(scroll.VerticalOffset > middle); Assert.Equal(0, page.VerticalOffset);
        scroll.ScrollToBottom(); Layout(page, 1050, 500); double bottom = scroll.VerticalOffset;
        Wheel(Field<StackPanel>(page, "episodes").Children[^1], -120); Layout(page, 1050, 500); Assert.True(page.VerticalOffset > 0); Assert.Equal(bottom, scroll.VerticalOffset);
        page.ScrollToVerticalOffset(120); scroll.ScrollToTop(); Layout(page, 1050, 500); double outer = page.VerticalOffset;
        Wheel(Field<StackPanel>(page, "episodes").Children[0], 120); Layout(page, 1050, 500); Assert.True(page.VerticalOffset < outer); Assert.Equal(0, scroll.VerticalOffset);
    }

    private static void Wheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent }; target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
        Pump();
    }

    private static AlertsPageData Fixture(DateTimeOffset now, int ruleCount, int episodeCount)
    {
        var rules = Enumerable.Range(0, ruleCount).Select(i => new AlertRule("layout-rule-" + i, AlertKind.Metric, UsageMetrics.CpuTemperature, 85, 15, true, now)).ToArray();
        var episodes = Enumerable.Range(0, episodeCount).Select(i => new AlertEpisode("layout-episode-" + i, "layout-rule-0", AlertKind.Metric, "CPU temperature fixture " + i, now.AddMinutes(-i - 1), null, 91)).ToArray();
        return new(rules, false, new(now.AddDays(-7), now.AddDays(1), episodes, episodes.Length, 4096), "Fixture saved locally.", IgnoredApps: []);
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, All)!.GetValue(value)!;
    private static void Set(object value, string name, object? field) => value.GetType().GetField(name, All)!.SetValue(value, field);
    private static TabControl Tabs(AlertsPage page) => (TabControl)typeof(AlertsPage).GetProperty("Tabs", All)!.GetValue(page)!;
    private static TabItem Tab(AlertsPage page, bool ignored) => (TabItem)typeof(AlertsPage).GetProperty(ignored ? "IgnoredTab" : "RulesTab", All)!.GetValue(page)!;
    private static void Select(AlertsPage page, bool ignored, double width, double height) { Tabs(page).SelectedItem = Tab(page, ignored); Layout(page, width, height); }
    private static void Settle(Window host) { host.UpdateLayout(); Pump(); host.UpdateLayout(); Pump(); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Until(Func<bool> done)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(10); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; }; timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(done(), "The Alerts action did not complete within 10 seconds."); Pump();
    }
    private static void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }
    private static Task UiTask(AlertsPage page, string method, params object[] args) => Dispatcher.CurrentDispatcher.Invoke(() => (Task)typeof(AlertsPage).GetMethod(method, All)!.Invoke(page, args)!);

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
                    Select(page, false, 1050, 900);
                    var lines = Texts(page);
                    Assert.Contains(state.Line, lines);
                    Assert.Single(lines, l => states.Any(s => s.Line == l));
                    var buttons = Descendants(page).OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
                    Assert.Contains("Remove", buttons); Assert.DoesNotContain("Edit", buttons);
                }

                // The editor: the kind hides Reading, Executable, At or above and For, parses no threshold and refuses a second rule.
                settings.Alerts = [fu, appRule];
                {
                    var page = NewPage(); Select(page, false, 900, 600);
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
                    Select(page, false, 1050, 900);
                    Assert.DoesNotContain(notCounted, Texts(page));
                    settings.ExcludedApps = ["chrome.exe"];
                    page.Apply(new([appRule], true, none, "Fixture saved locally."));
                    Select(page, false, 1050, 900);
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
                    Assert.Equal(Visibility.Visible, Field<Border>(page, "statusCard").Visibility);
                }
                // The Not alerted list shows also with no first-use rule, sorted, each line with Alert again.
                settings.FirstUseIgnoredApps = ["zoom.exe", "chrome.exe", "Brave.exe"];
                {
                    var page = NewPage();
                    page.Apply(new([], true, none, "Fixture saved locally.", IgnoredApps: settings.FirstUseIgnoredApps));
                    Select(page, true, 1050, 900);
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
        foreach (bool ignored in new[] { false, true })
        {
            Select(page, ignored, 1050, 700); var selected = Tabs(page).SelectedItem;
            Raise(HistoryClearStore.Alerts); Assert.Equal(++count, reads); Assert.Same(selected, Tabs(page).SelectedItem);
            Raise(HistoryClearStore.Alerts); Assert.Equal(++count, reads); Assert.Same(selected, Tabs(page).SelectedItem);
            Raise(HistoryClearStore.Connections); Raise(HistoryClearStore.Usage); Raise(HistoryClearStore.ProcessHistory); Assert.Equal(count, reads);
        }
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Raise(HistoryClearStore.Alerts); Assert.Equal(count, reads); Assert.False(Field<DispatcherTimer>(page, "timer").IsEnabled);
    }

    private static void CheckEditor(UiSession session)
    {
        var page = new AlertsPage(session);
        Select(page, false, 900, 600);
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
    { element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); Pump(); element.Measure(new(width, height)); element.Arrange(new(0, 0, width, height)); element.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(t => t.Text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
