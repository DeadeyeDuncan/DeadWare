using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Clear history…: the window's choices, confirmation, progress view, keyboard and theme, offscreen on an unstarted session.</summary>
internal static class HistoryClearUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Invalid = "Enter an app's executable name, such as chrome.exe.";
    private const string ClearedNote = "Freed space is reused for new history; the files don't shrink. Exported CSV files and Windows notification history are not cleared.";

    internal static void Run(Application app)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        typeof(Collector).GetField("clock", All)!.SetValue(collector, new System.Diagnostics.Stopwatch());
        typeof(Collector).GetField("latest", All)!.SetValue(collector, new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()), [], [], [], "CPU", "", "", "", false));
        void Set(string field, object value) => typeof(UiSession).GetField(field, All)!.SetValue(session, value);
        Set("<Collector>k__BackingField", collector);
        Set("<Settings>k__BackingField", new UserSettings());
        Set("layout", new DetachedWindowState([]));
        var usage = new UsageHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture");
        var alerts = new AlertService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture", usage, () => null);
        Set("<Usage>k__BackingField", usage);
        Set("<Alerts>k__BackingField", alerts);
        string[] themes = ["Midnight", "Daylight", "Blackout"];
        try
        {
            for (int index = 0; index < themes.Length; index++)
            {
                Theme.Apply(themes[index]);
                CheckChoices(session);
                CheckConfirmation(session);
                CheckSubmit(session);
                CheckProgress(session);
                CheckKeyboard(session);
                CheckTheme(app, session, themes[(index + 1) % themes.Length]);
                Theme.Apply(themes[index]);
            }
        }
        finally { usage.ClearState = HistoryClearState.None; alerts.ClearState = HistoryClearState.None; Theme.Apply("Midnight"); }
    }

    private static void AssertNone(UiSession session)
    {
        Assert.Equal(HistoryClearPhase.None, session.Usage.ClearState.Phase);
        Assert.Equal(HistoryClearPhase.None, session.Alerts.ClearState.Phase);
    }

    private static void Reset(UiSession session)
    {
        session.Usage.ClearState = HistoryClearState.None; session.Alerts.ClearState = HistoryClearState.None;
    }

    /// <summary>Defaults, copy and validation.</summary>
    private static void CheckChoices(UiSession session)
    {
        var window = new ClearHistoryWindow(session);
        Assert.Equal("Clear history", window.Title);
        Assert.Equal(480, window.Width); Assert.Equal(SizeToContent.Height, window.SizeToContent);
        Assert.True(window.OneApp.IsChecked); Assert.NotEqual(true, window.AllApps.IsChecked);
        Assert.True(window.OlderThan7Days.IsChecked); Assert.NotEqual(true, window.AllTime.IsChecked);
        Assert.Equal(["One app", "All apps", "Older than 7 days", "All time"], new[] { window.OneApp, window.AllApps, window.OlderThan7Days, window.AllTime }.Select(r => r.Content as string));
        Assert.Equal("Clear history", window.ClearButton.Content); Assert.Equal("Cancel", window.CancelButton.Content);
        Assert.False(window.ClearButton.IsEnabled);
        Assert.True(window.AppBox.IsEditable); Assert.Equal("App to clear", AutomationProperties.GetName(window.AppBox));
        Assert.Equal(Visibility.Collapsed, window.Validation.Visibility);
        Assert.Equal(Visibility.Collapsed, window.Progress.Visibility);
        var texts = LogicalTexts((DependencyObject)window.Content).ToArray();
        foreach (string expected in new[]
        {
            "Clear saved history", "Apps", "Range",
            "Clears the chosen apps' saved records from Connections history, Usage history's per-app totals, Application resources and the alert log's application budget and first-use alerts. Clearing one app moves its Application resources numbers into Everything else; clearing all apps removes those minutes.",
            "Kept: device-wide history (CPU, GPU, memory, temperatures, network totals, battery and Cooling Health), other alerts in the log, your alert rules, and the Don't record and Don't alert lists.",
            "Not cleared: CSV files you exported, and Windows notification history, which can keep alert balloons that name apps. DeadWare can't reach them; delete them yourself.",
            "Freed space is reused for new history; the files don't shrink.",
            "Clearing doesn't stop recording; add the app to Don't record these apps for that.",
        }) Assert.Contains(expected, texts);
        window.AppBox.Text = "a*";
        Assert.Equal(Visibility.Visible, window.Validation.Visibility); Assert.Equal(Invalid, window.Validation.Text);
        Assert.False(window.ClearButton.IsEnabled);
        window.AppBox.Text = "chrome.exe";
        Assert.Equal(Visibility.Collapsed, window.Validation.Visibility); Assert.True(window.ClearButton.IsEnabled);
        window.AppBox.Text = "a*"; Assert.False(window.ClearButton.IsEnabled);
        window.AllApps.IsChecked = true;
        Assert.True(window.ClearButton.IsEnabled); Assert.Equal(Visibility.Collapsed, window.Validation.Visibility);
        window.Close();
        AssertNone(session);
    }

    /// <summary>The four confirmation texts through the Confirm hook; No sends nothing.</summary>
    private static void CheckConfirmation(UiSession session)
    {
        foreach (bool allApps in new[] { false, true })
            foreach (bool allTime in new[] { false, true })
            {
                var window = new ClearHistoryWindow(session);
                string? asked = null; Window? owner = null;
                window.Confirm = (o, text) => { owner = o; asked = text; return false; };
                if (allApps) window.AllApps.IsChecked = true; else window.AppBox.Text = "Chrome.exe  ";
                if (allTime) window.AllTime.IsChecked = true;
                var first = DateTimeOffset.UtcNow;
                Assert.False(window.Submit());
                var last = DateTimeOffset.UtcNow;
                string Expected(DateTimeOffset t)
                {
                    var floor = UsageMetrics.Floor(t);
                    string before = (allTime ? floor.AddMinutes(1) : floor.AddDays(-7)).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                    return (allApps, allTime) switch
                    {
                        (false, true) => "Clear all saved history for Chrome.exe? This can't be undone.",
                        (false, false) => $"Clear Chrome.exe's saved history from before {before}? This can't be undone.",
                        (true, true) => "Clear every app's saved history? Device-wide history is kept. This can't be undone.",
                        (true, false) => $"Clear every app's saved history from before {before}? Device-wide history is kept. This can't be undone.",
                    };
                }
                Assert.Same(window, owner);
                Assert.Contains(asked, new[] { Expected(first), Expected(last) });
                AssertNone(session);
                Assert.Equal(Visibility.Collapsed, window.Progress.Visibility);
                window.Close();
            }
    }

    /// <summary>Yes after the session began ending, or after the window closed, sends nothing; Yes sends and shows the progress view with
    /// focus on Close, which closes the window while the clear keeps running.</summary>
    private static void CheckSubmit(UiSession session)
    {
        var layoutField = typeof(UiSession).GetField("layout", All)!;
        var ending = new ClearHistoryWindow(session); ending.AllApps.IsChecked = true;
        ending.Confirm = (_, _) => { ((DetachedWindowState)layoutField.GetValue(session)!).BeginShutdown(); return true; };
        Assert.False(ending.Submit());
        AssertNone(session);
        layoutField.SetValue(session, new DetachedWindowState([]));
        ending.Close();

        var closing = new ClearHistoryWindow(session); closing.AllApps.IsChecked = true;
        closing.Confirm = (owner, _) => { owner.Close(); return true; };
        Assert.False(closing.Submit());
        AssertNone(session);

        var window = new ClearHistoryWindow(session); window.AllApps.IsChecked = true;
        bool closed = false; window.Closed += (_, _) => closed = true;
        window.Confirm = (_, _) => true;
        Assert.True(window.Submit());
        Assert.Equal(HistoryClearPhase.Queued, session.Usage.ClearState.Phase);
        Assert.Equal(HistoryClearPhase.Queued, session.Alerts.ClearState.Phase);
        Assert.Equal(Visibility.Visible, window.Progress.Visibility);
        Assert.Equal(Visibility.Collapsed, window.ClearButton.Visibility);
        Assert.Equal("Clearing history…", window.Progress.Header.Text);
        Assert.Equal(["Usage history · waiting", "Alert log · waiting"], window.Progress.Lines.Select(t => t.Text));
        var close = Assert.IsType<Button>(window.Progress.Close);
        Assert.Equal("Close", close.Content); Assert.True(close.IsCancel);
        Assert.Same(close, FocusManager.GetFocusedElement(window));
        Click(close);
        Assert.True(closed);
        Assert.Equal(HistoryClearPhase.Queued, session.Usage.ClearState.Phase);
        Assert.Equal(HistoryClearPhase.Queued, session.Alerts.ClearState.Phase);
        Reset(session);
    }

    private static HistoryClearState S(HistoryClearPhase phase, int? percent = null, string? error = null, bool saved = false, bool resumed = false)
        => new(phase, percent, error, saved, resumed);

    /// <summary>Every line form, the 99 % cap, each header and summary, Try again and the live setting, driven through Apply.</summary>
    private static void CheckProgress(UiSession session)
    {
        var view = new HistoryClearProgressView(session, closeButton: false);
        Assert.Null(view.Close);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(view.Header));
        string[] Lines() => view.Lines.Select(t => t.Text).ToArray();
        HistoryClearLine[] running =
        [
            new(HistoryClearStore.Connections, S(HistoryClearPhase.Queued), false),
            new(HistoryClearStore.Usage, S(HistoryClearPhase.Clearing, 37), false),
            new(HistoryClearStore.ProcessHistory, S(HistoryClearPhase.Done, 100), false),
            new(HistoryClearStore.Alerts, S(HistoryClearPhase.Failed, null, "IOException", saved: true), false),
        ];
        view.Apply(running);
        Assert.Equal(["Connections history · waiting", "Usage history · clearing (37 %)", "Application resources · done",
            "Alert log · failed (IOException); it resumes when DeadWare next starts"], Lines());
        Assert.Equal("Clearing history…", view.Header.Text);
        Assert.Equal(Visibility.Collapsed, view.Summary.Visibility);
        Assert.Equal(Visibility.Collapsed, view.TryAgain.Visibility);
        // An unchanged list keeps its lines (no re-render, so nothing loses focus every second).
        var kept = view.Lines[0];
        view.Apply(running.Select(line => line with { }).ToArray());
        Assert.Same(kept, view.Lines[0]);

        view.Apply([new(HistoryClearStore.Usage, S(HistoryClearPhase.Clearing, new HistoryClearProgress(999, 1000, false).Percent), false),
            new(HistoryClearStore.Alerts, S(HistoryClearPhase.Clearing), false)]);
        Assert.Equal(["Usage history · clearing (99 %)", "Alert log · clearing"], Lines());

        view.Apply([new(HistoryClearStore.Connections, S(HistoryClearPhase.Failed, null, "IOException"), true),
            new(HistoryClearStore.Usage, S(HistoryClearPhase.Unavailable, null, "IOException"), false)]);
        Assert.Equal(["Connections history · failed (IOException); not cleared", "Usage history · unavailable (IOException); not cleared"], Lines());
        Assert.Equal(Visibility.Visible, view.TryAgain.Visibility); Assert.Equal("Try again", view.TryAgain.Content);

        view.Apply([new(HistoryClearStore.Usage, S(HistoryClearPhase.Clearing, 5, null, saved: true, resumed: true), false)]);
        Assert.Equal("Finishing a clear that was interrupted…", view.Header.Text);
        Assert.Equal(Visibility.Collapsed, view.TryAgain.Visibility);

        var done = S(HistoryClearPhase.Done, 100);
        view.Apply([new(HistoryClearStore.Connections, done, false), new(HistoryClearStore.Usage, done, false),
            new(HistoryClearStore.ProcessHistory, done, false), new(HistoryClearStore.Alerts, done, false)]);
        Assert.Equal("History cleared.", view.Header.Text);
        Assert.Equal(Visibility.Visible, view.Summary.Visibility); Assert.Equal(ClearedNote, view.Summary.Text);

        view.Apply([new(HistoryClearStore.Connections, done, false), new(HistoryClearStore.Usage, done, false),
            new(HistoryClearStore.ProcessHistory, S(HistoryClearPhase.Failed, null, "IOException", saved: true), false),
            new(HistoryClearStore.Alerts, S(HistoryClearPhase.Failed, null, "IOException"), false)]);
        Assert.Equal("History partly cleared.", view.Header.Text);
        Assert.Equal("2 of 4 finished. Failed histories resume when DeadWare next starts. Histories marked not cleared need Clear history again.", view.Summary.Text);

        // A resumed clear counts its own lines.
        view.Apply([new(HistoryClearStore.Usage, S(HistoryClearPhase.Done, 100, null, false, true), false),
            new(HistoryClearStore.Alerts, S(HistoryClearPhase.Failed, null, "IOException", true, true), false)]);
        Assert.Equal("1 of 2 finished. Failed histories resume when DeadWare next starts.", view.Summary.Text);
        view.Apply([]);
        Assert.Empty(view.Lines);
    }

    /// <summary>Tab order, no default button, Esc on Cancel, and Enter in the box starting nothing.</summary>
    private static void CheckKeyboard(UiSession session)
    {
        var window = new ClearHistoryWindow(session);
        var focusable = Focusable((DependencyObject)window.Content).ToArray();
        Assert.Equal(new Control[] { window.OneApp, window.AllApps, window.AppBox, window.OlderThan7Days, window.AllTime, window.ClearButton, window.CancelButton }, focusable);
        Assert.All(focusable, control => Assert.Equal(int.MaxValue, KeyboardNavigation.GetTabIndex(control)));
        Assert.False(window.ClearButton.IsDefault); Assert.True(window.CancelButton.IsCancel);
        bool asked = false; window.Confirm = (_, _) => { asked = true; return true; };
        window.AppBox.Text = "chrome.exe"; Assert.True(window.ClearButton.IsEnabled);
        Press(window.AppBox, Key.Enter);
        Assert.False(asked);
        AssertNone(session);
        window.Close();
    }

    /// <summary>A theme change while the window is open restyles it.</summary>
    private static void CheckTheme(Application app, UiSession session, string next)
    {
        var window = new ClearHistoryWindow(session);
        var heading = LogicalElements((DependencyObject)window.Content).OfType<TextBlock>().First(t => t.Text == "Clear saved history");
        var help = LogicalElements((DependencyObject)window.Content).OfType<TextBlock>().First(t => t.Text.StartsWith("Kept: ", StringComparison.Ordinal));
        Theme.Apply(next);
        Assert.Same(app.Resources["WindowSurface"], window.Background);
        Assert.Same(app.Resources["Ink"], window.Foreground);
        Assert.Same(app.Resources["Heading"], heading.Foreground);
        Assert.Same(app.Resources["Muted"], help.Foreground);
        Assert.Same(app.Resources["Warning"], window.Validation.Foreground);
        window.Close();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<DependencyObject> LogicalElements(DependencyObject root)
    {
        yield return root;
        if (root is UIElement { Visibility: not Visibility.Visible }) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in LogicalElements(child)) yield return found;
    }

    private static IEnumerable<string> LogicalTexts(DependencyObject root)
        => LogicalElements(root).OfType<TextBlock>().Select(t => t.Text);

    /// <summary>The shown controls a Tab can reach, in tree order; a control's own content is not searched.</summary>
    private static IEnumerable<Control> Focusable(DependencyObject root)
    {
        if (root is UIElement { Visibility: not Visibility.Visible }) yield break;
        if (root is Control control)
        {
            if (control.Focusable && control.IsTabStop) yield return control;
            yield break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Focusable(child)) yield return found;
    }

    /// <summary>Presses a key on an element as the keyboard does: the preview first, then the key itself unless it was handled.</summary>
    private static void Press(UIElement target, Key key)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    /// <summary>The input source a key event must name; it is never added to the sources, and offscreen nothing reads it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get => null!; set { } }
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed => false;
    }
}
