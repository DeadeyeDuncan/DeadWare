using System.Globalization;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>
/// Recent alert episodes with their actions, the rule editor with what recorded history says about a draft rule,
/// the rules themselves, and the apps the first network use rule keeps quiet.
/// </summary>
internal sealed class AlertsPage : Page
{
    private static readonly (string Label, AlertKind Value)[] Kinds =
    [
        ("Device metric", AlertKind.Metric), ("Network rate", AlertKind.NetworkRate),
        ("Application traffic per day", AlertKind.ApplicationDaily), ("Battery level", AlertKind.Battery),
        ("First network use by an app", AlertKind.FirstNetworkUse),
    ];
    private static readonly (string Label, string Value)[] MetricTargets =
    [
        ("CPU usage", UsageMetrics.Cpu), ("CPU temperature", UsageMetrics.CpuTemperature), ("GPU usage", UsageMetrics.Gpu),
        ("GPU temperature", UsageMetrics.GpuTemperature), ("RAM usage", UsageMetrics.Memory),
    ];
    private static readonly (string Label, string Value)[] NetworkTargets = [("Download", UsageMetrics.Download), ("Upload", UsageMetrics.Upload)];
    private static readonly (string Label, int Days)[] Ranges = [("Day", 1), ("Week", 7), ("Month", 30)];
    private const string NotCounted = "Not counted: this app is in Don't record these apps.";
    /// <summary>Connections history's retention: newer first-use episodes open there, older ones in Usage history.</summary>
    private static readonly TimeSpan ConnectionsRetention = TimeSpan.FromDays(30);

    private readonly MonitorSession session;
    private readonly ComboBox kind = new() { MinWidth = 220 }, target = new() { MinWidth = 180 }, duration = new() { MinWidth = 130 }, logRange = new() { MinWidth = 110 };
    private readonly TextBox application = new() { Width = 200 }, threshold = new() { Width = 100 };
    private readonly TextBlock unit = Muted(""), suggestion = Muted(""), notCounted = Muted(""), editorError = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock rulesEmpty = Muted("No rules yet. Add one above; it watches the readings DeadWare already records.");
    private readonly TextBlock logSummary = Muted(""), status = Muted("");
    private readonly InfoBar actionStatus = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
    private readonly StackPanel rules = new() { Spacing = 6 }, episodes = new() { Spacing = 10 }, ignoredList = new() { Spacing = 6 }, editor = new() { Spacing = 8 };
    private readonly FrameworkElement targetField, applicationField, thresholdField, durationField;
    private readonly SelectorBarItem rulesTab = new() { Text = "Rules" }, ignoredTab = new() { Text = "Don't alert" };
    private readonly CheckBox notifications = new() { Content = "Show tray notifications for alerts" };
    private readonly QueueTimer timer;
    private (string Label, string Value)[] targets = MetricTargets;
    private bool loaded, refreshing, refreshAgain, syncing, logRead;
    private int revision, suggestionRevision;

    public AlertsPage(MonitorSession session)
    {
        this.session = session;
        timer = new QueueTimer(DispatcherQueue) { Interval = TimeSpan.FromSeconds(30) };

        foreach (var (label, _) in Kinds) kind.Items.Add(label);
        kind.SelectedIndex = 0;
        AutomationProperties.SetName(kind, "Alert kind");
        FillTargets(MetricTargets, 1);
        AutomationProperties.SetName(target, "Alert target");
        AutomationProperties.SetName(application, "Application name");
        AutomationProperties.SetName(threshold, "Alert threshold");
        foreach (int minutes in AlertRules.Durations) duration.Items.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");
        duration.SelectedIndex = Math.Min(1, duration.Items.Count - 1);
        AutomationProperties.SetName(duration, "Alert duration");
        unit.VerticalAlignment = VerticalAlignment.Center;
        var add = new Button { Content = "Add rule", VerticalAlignment = VerticalAlignment.Bottom, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += (_, _) => AddRule();
        targetField = Labeled("Reading", target);
        applicationField = Labeled("Executable", application);
        thresholdField = Labeled("At or above", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { threshold, unit } });
        durationField = Labeled("For", duration);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Labeled("Watch", kind), targetField, applicationField, thresholdField, durationField, add } };

        editor.Children.Add(Muted("A rule fires once per episode when the recorded value is at or above the threshold for the chosen minutes, and re-arms after the value stays below it. Application budgets fire once per day. Battery rules fire as soon as the charge is at or below the level while on battery. A first network use rule logs each app the first time DeadWare sees it use the network, after a quiet 3-day learning period; apps seen within a minute share one notification."));
        editor.Children.Add(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });
        editor.Children.Add(suggestion); editor.Children.Add(notCounted); editor.Children.Add(editorError);
        editor.Children.Add(rules); editor.Children.Add(rulesEmpty);
        AutomationProperties.SetName(notifications, "Alert notifications");
        editor.Children.Add(notifications);
        editor.Children.Add(Muted("Every episode is logged in Recent alerts whether or not notifications are on. Alerts report what was observed against rules you chose; they are not a hardware diagnosis or a security finding."));

        var tabs = new SelectorBar();
        tabs.Items.Add(rulesTab); tabs.Items.Add(ignoredTab);
        tabs.SelectedItem = rulesTab;
        ignoredList.Visibility = Visibility.Collapsed;
        tabs.SelectionChanged += (_, _) =>
        {
            bool showRules = tabs.SelectedItem != ignoredTab;
            editor.Visibility = showRules ? Visibility.Visible : Visibility.Collapsed;
            ignoredList.Visibility = showRules ? Visibility.Collapsed : Visibility.Visible;
        };
        var tabbed = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { tabs, editor, ignoredList } });

        foreach (var (label, _) in Ranges) logRange.Items.Add(label);
        logRange.SelectedIndex = 1;
        AutomationProperties.SetName(logRange, "Alert log range");
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => RequestRefresh();
        var heading = new Grid();
        heading.Children.Add(new TextBlock { Text = "Recent alerts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        heading.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { logRange, refresh } });
        var log = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children =
            {
                heading, logSummary, episodes, status,
                Muted("Episodes stay in alerts.db in this Windows profile for up to 400 days and are never uploaded. Acknowledging marks an episode as seen; it does not stop later episodes. The first network use rule also keeps the names of apps it has seen, with the first and last minute, for up to 400 days after each was last seen."),
            },
        });

        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { actionStatus, log, tabbed } } };

        kind.SelectionChanged += (_, _) => { if (syncing) return; UpdateEditor(); RequestSuggestion(); };
        target.SelectionChanged += (_, _) => { if (syncing) return; UpdateUnit(); RequestSuggestion(); };
        application.TextChanged += (_, _) => { UpdateNotCounted(); RequestSuggestion(); };
        threshold.TextChanged += (_, _) => RequestSuggestion();
        notifications.Checked += (_, _) => { if (!syncing) session.SetAlertNotifications(true); };
        notifications.Unchecked += (_, _) => { if (!syncing) session.SetAlertNotifications(false); };
        logRange.SelectionChanged += (_, _) => RequestRefresh();
        timer.Tick += () => _ = RefreshAsync();
        Loaded += (_, _) =>
        {
            loaded = true;
            session.Changed += SyncSettings; session.HistoryClearChanged += HistoryClearChanged;
            timer.Start(); SyncSettings(); _ = RefreshAsync(); RequestSuggestion();
        };
        Unloaded += (_, _) =>
        {
            loaded = false;
            session.Changed -= SyncSettings; session.HistoryClearChanged -= HistoryClearChanged;
            timer.Stop(); revision++;
        };
        UpdateEditor(); SyncSettings();
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    private static StackPanel Labeled(string label, UIElement control) => new() { Spacing = 2, Children = { Muted(label), control } };

    private static Button Action(string label, Action run)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => run();
        return button;
    }

    private AlertKind SelectedKind => kind.SelectedIndex >= 0 ? Kinds[kind.SelectedIndex].Value : AlertKind.Metric;
    private string SelectedTarget => target.SelectedIndex >= 0 && target.SelectedIndex < targets.Length ? targets[target.SelectedIndex].Value : "";
    private int SelectedMinutes => duration.SelectedIndex >= 0 ? AlertRules.Durations[duration.SelectedIndex] : 5;
    private int LogDays => logRange.SelectedIndex >= 0 ? Ranges[logRange.SelectedIndex].Days : 7;

    private void FillTargets((string Label, string Value)[] list, int selected)
    {
        bool was = syncing; syncing = true;
        try
        {
            targets = list;
            target.Items.Clear();
            foreach (var (label, _) in list) target.Items.Add(label);
            target.SelectedIndex = Math.Min(selected, list.Length - 1);
        }
        finally { syncing = was; }
    }

    private void UpdateEditor()
    {
        var selected = SelectedKind;
        var wanted = selected == AlertKind.NetworkRate ? NetworkTargets : MetricTargets;
        if (!ReferenceEquals(wanted, targets)) FillTargets(wanted, 0);
        bool sampled = selected is AlertKind.Metric or AlertKind.NetworkRate;
        targetField.Visibility = sampled ? Visibility.Visible : Visibility.Collapsed;
        applicationField.Visibility = selected == AlertKind.ApplicationDaily ? Visibility.Visible : Visibility.Collapsed;
        durationField.Visibility = sampled ? Visibility.Visible : Visibility.Collapsed;
        // The first network use rule has nothing to edit: no threshold.
        thresholdField.Visibility = selected == AlertKind.FirstNetworkUse ? Visibility.Collapsed : Visibility.Visible;
        UpdateUnit();
        editorError.Text = "";
        UpdateNotCounted();
    }

    private void UpdateUnit() => unit.Text = SelectedKind switch
    {
        AlertKind.Metric => AlertRules.IsTemperature(SelectedTarget) ? "°C" : "%",
        AlertKind.NetworkRate => "MB/s", AlertKind.ApplicationDaily => "MB per day", AlertKind.Battery => "% while on battery", _ => "",
    };

    /// <summary>The editor's Not counted note, while an application budget's Executable is on the Don't record list.</summary>
    private void UpdateNotCounted()
    {
        bool show = SelectedKind == AlertKind.ApplicationDaily && session.IsExcluded(application.Text);
        notCounted.Text = show ? NotCounted : "";
        notCounted.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rule the editor currently describes, or null with a reason.</summary>
    private AlertRule? Draft(out string error)
    {
        error = "";
        var selected = SelectedKind;
        if (selected == AlertKind.FirstNetworkUse)
            return AlertRules.Normalize(new AlertRule(Guid.NewGuid().ToString("N"), selected, "", 1, 1, true, DateTimeOffset.UtcNow));
        string text = threshold.Text.Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        { error = "Enter a number for the threshold."; return null; }
        string targetId = selected switch
        {
            AlertKind.Metric or AlertKind.NetworkRate => SelectedTarget,
            AlertKind.ApplicationDaily => application.Text, _ => "",
        };
        double scaled = selected is AlertKind.NetworkRate or AlertKind.ApplicationDaily ? value * 1_000_000 : value;
        var rule = AlertRules.Normalize(new AlertRule(Guid.NewGuid().ToString("N"), selected, targetId, scaled, SelectedMinutes, true, DateTimeOffset.UtcNow));
        if (rule is null) { error = selected == AlertKind.ApplicationDaily ? "Enter the executable name, such as chrome.exe." : "The threshold is out of range."; return null; }
        return rule;
    }

    private void AddRule()
    {
        if (Draft(out string error) is not { } rule) { editorError.Text = error; return; }
        if (rule.Kind == AlertKind.FirstNetworkUse && session.Settings.Alerts.Any(r => r.Kind == AlertKind.FirstNetworkUse))
        { editorError.Text = "A first network use rule already exists."; return; }
        if (session.Settings.Alerts.Count >= AlertRules.MaximumRules) { editorError.Text = $"At most {AlertRules.MaximumRules} rules are kept."; return; }
        editorError.Text = "";
        session.SetAlertRules([.. session.Settings.Alerts, rule]);
        threshold.Text = "";
    }

    private void RemoveRule(string id) => session.SetAlertRules(session.Settings.Alerts.Where(r => r.Id != id).ToArray());

    private void ToggleRule(string id, bool enabled) => session.SetAlertRules(session.Settings.Alerts.Select(r => r.Id == id ? r with { Enabled = enabled } : r).ToArray());

    /// <summary>Puts a rule back into the editor and removes it, so Add rule saves the edited one.</summary>
    private void EditRule(AlertRule rule)
    {
        if (rule.Kind == AlertKind.FirstNetworkUse) return;
        syncing = true;
        try
        {
            kind.SelectedIndex = Array.FindIndex(Kinds, k => k.Value == rule.Kind);
            UpdateEditor();
            if (rule.Kind is AlertKind.Metric or AlertKind.NetworkRate)
                target.SelectedIndex = Math.Max(0, Array.FindIndex(targets, t => t.Value == rule.Target));
            UpdateUnit();
            application.Text = rule.Kind == AlertKind.ApplicationDaily ? rule.Target : "";
            threshold.Text = rule.Kind is AlertKind.NetworkRate or AlertKind.ApplicationDaily
                ? (rule.Threshold / 1_000_000).ToString("0.###", CultureInfo.CurrentCulture) : rule.Threshold.ToString("0", CultureInfo.CurrentCulture);
            int minutes = AlertRules.Durations.ToList().IndexOf(rule.Minutes);
            duration.SelectedIndex = minutes >= 0 ? minutes : Math.Min(1, duration.Items.Count - 1);
        }
        finally { syncing = false; }
        RemoveRule(rule.Id); RequestSuggestion();
    }

    private void SyncSettings()
    {
        syncing = true;
        try { notifications.IsChecked = session.Settings.AlertNotifications; }
        finally { syncing = false; }
        RenderRules();
        UpdateNotCounted();
        if (!logRead) status.Text = session.Alerts.Status;
    }

    private void RenderRules()
    {
        var list = session.Settings.Alerts;
        rules.Children.Clear();
        rulesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rule in list)
        {
            var enabled = new CheckBox { Content = AlertRules.Describe(rule), IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
            enabled.Checked += (_, _) => { if (!syncing) ToggleRule(rule.Id, true); };
            enabled.Unchecked += (_, _) => { if (!syncing) ToggleRule(rule.Id, false); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (rule.Kind != AlertKind.FirstNetworkUse) buttons.Children.Add(Action("Edit", () => EditRule(rule)));
            buttons.Children.Add(Action("Remove", () => RemoveRule(rule.Id)));
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(enabled); Grid.SetColumn(buttons, 1); line.Children.Add(buttons);
            rules.Children.Add(line);
            string? note = rule.Kind switch
            {
                AlertKind.FirstNetworkUse => FirstNetworkUse.RuleLine(session.Alerts.Failed, session.Alerts.FirstUse, rule.Enabled, Collector.IsElevated,
                    session.Current?.Connections?.Available == true, DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture),
                AlertKind.ApplicationDaily when session.IsExcluded(rule.Target) => NotCounted,
                _ => null,
            };
            if (note is not null) rules.Children.Add(Muted(note));
        }
        RenderIgnored(session.Settings.FirstUseIgnoredApps);
    }

    /// <summary>The Don't alert tab keeps its count current even while Rules is selected.</summary>
    private void RenderIgnored(IReadOnlyList<string> names)
    {
        ignoredList.Children.Clear();
        ignoredTab.Text = names.Count == 0 ? "Don't alert" : $"Don't alert · {names.Count}";
        if (names.Count == 0) { ignoredList.Children.Add(Muted("No apps are kept quiet. Don't alert for this app on an episode adds one.")); return; }
        ignoredList.Children.Add(Muted("Not alerted, even when new again:"));
        foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string key = AppKey.Of(name);
            var again = Action("Alert again", () => session.UnignoreFirstUse(key));
            AutomationProperties.SetName(again, $"Alert again for {name}");
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(again, 1); line.Children.Add(again);
            ignoredList.Children.Add(line);
        }
    }

    /// <summary>An alert-log clear started or ended: the log is read again.</summary>
    private void HistoryClearChanged(HistoryClearStore store) { if (store == HistoryClearStore.Alerts) RequestRefresh(); }

    private void RequestRefresh() { revision++; _ = RefreshAsync(); }

    private async Task RefreshAsync()
    {
        if (!loaded) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var log = await session.Alerts.ReadEpisodesAsync(to.AddDays(-LogDays), to);
            if (requested != revision || !loaded) { refreshAgain = true; return; }
            ShowLog(log);
        }
        catch (Exception error) { status.Text = $"The alert log could not be read: {error.Message}"; }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (loaded) _ = RefreshAsync(); }
        }
    }

    private void RequestSuggestion()
    {
        if (syncing) return;
        int requested = ++suggestionRevision;
        if (Draft(out _) is not { } rule) { suggestion.Text = ""; return; }
        if (rule.Kind == AlertKind.Battery) { suggestion.Text = "Battery rules have no recorded history to compare with."; return; }
        if (rule.Kind == AlertKind.FirstNetworkUse)
        { suggestion.Text = "New apps are learned quietly for 3 calendar days after per-app capture first runs; after that each app's first network use is logged once."; return; }
        if (!loaded) return;
        _ = SuggestAsync(rule, requested);
    }

    private async Task SuggestAsync(AlertRule rule, int requested)
    {
        try
        {
            var result = await session.Alerts.SuggestAsync(rule);
            if (requested != suggestionRevision) return;
            string Window(string name, AlertWindowStats stats)
            {
                if (stats.Maximum is null) return $"{name}: no recorded minutes.";
                string highest = AlertRules.FormatValue(rule, stats.Maximum.Value), average = AlertRules.FormatValue(rule, stats.Average ?? 0);
                string share = stats.ShareAtOrAbove is { } fraction ? $", at or above {AlertRules.FormatThreshold(rule)} in {fraction * 100:0.#}% of {stats.Minutes:N0} minutes" : "";
                return rule.Kind == AlertKind.ApplicationDaily ? $"{name}: {highest} in total, {average} per day on average."
                    : $"{name}: highest minute {highest}, average {average}{share}.";
            }
            suggestion.Text = $"Recorded so far · {Window("Last 7 days", result.Week)} {Window("Last 30 days", result.Month)}";
        }
        catch (Exception) { if (requested == suggestionRevision) suggestion.Text = "Recorded history is unavailable right now."; }
    }

    private void ShowLog(AlertLogView log)
    {
        logRead = true;
        RenderRules();
        status.Text = session.Alerts.Status + $" Storage: {HistoryChartModel.Bytes(log.StorageBytes)}.";
        episodes.Children.Clear();
        var rulesById = session.Settings.Alerts.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        logSummary.Text = log.Total == 0 ? "No alerts in this period."
            : log.Total > log.Episodes.Count ? $"{log.Episodes.Count} of {log.Total:N0} episodes shown, newest first." : $"{log.Total:N0} episode{(log.Total == 1 ? "" : "s")}, newest first.";
        var ignoredKeys = session.Settings.FirstUseIgnoredApps.Select(AppKey.Of).ToHashSet(StringComparer.Ordinal);
        foreach (var episode in log.Episodes)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            string detail;
            if (episode.Kind == AlertKind.FirstNetworkUse)
            {
                // Never open, no peak; it opens Connections history while that still keeps the minute.
                if (DateTimeOffset.UtcNow - episode.Start <= ConnectionsRetention)
                {
                    var minute = new DateTimeOffset(episode.Start.Ticks - episode.Start.Ticks % TimeSpan.TicksPerMinute, episode.Start.Offset);
                    actions.Children.Add(Action("View in Connections", () => session.RequestConnectionsHistory(minute.AddMinutes(-5), minute.AddMinutes(10), episode.Application)));
                }
                else actions.Children.Add(Action("View in Usage history", () => session.RequestUsageDay(episode.Start)));
                bool listed = !string.IsNullOrEmpty(episode.Application) && ignoredKeys.Contains(AppKey.Of(episode.Application));
                if (!string.IsNullOrEmpty(episode.Application) && !listed)
                {
                    var ignore = Action("Don't alert for this app", () => DontAlert(episode));
                    AutomationProperties.SetName(ignore, $"Don't alert for {episode.Application}");
                    actions.Children.Add(ignore);
                }
                detail = $"{episode.Start.ToLocalTime():g} · first network use{(episode.Acknowledged ? " · acknowledged" : "")}{(listed ? " · on the Don't alert list" : "")}";
            }
            else
            {
                string peak = rulesById.TryGetValue(episode.RuleId, out var rule) ? AlertRules.FormatValue(rule, episode.Peak) : episode.Peak.ToString("0.#", CultureInfo.CurrentCulture);
                string when = episode.End is { } end ? $"{episode.Start.ToLocalTime():g} – {end.ToLocalTime():t}" : $"{episode.Start.ToLocalTime():g} – ongoing";
                actions.Children.Add(Action("View in Usage history", () => session.RequestUsageDay(episode.Start)));
                detail = $"{when} · peak {peak}{(episode.Acknowledged ? " · acknowledged" : episode.End is null ? " · open" : "")}";
            }
            if (!episode.Acknowledged) actions.Children.Add(Action("Acknowledge", () => _ = AcknowledgeAsync(episode.Id)));
            var summary = new TextBlock { Text = episode.Summary, TextWrapping = TextWrapping.Wrap };
            if (episode.Kind != AlertKind.FirstNetworkUse && episode.End is null)
                summary.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            episodes.Children.Add(new StackPanel { Spacing = 4, Children = { summary, Muted(detail), actions } });
        }
    }

    /// <summary>Adds the episode's app to the Don't alert list and acknowledges the episode. A full list changes nothing and says why.</summary>
    private void DontAlert(AlertEpisode episode)
    {
        SetActionStatus("");
        if (session.Settings.FirstUseIgnoredApps.Count >= FirstNetworkUse.MaximumIgnored) { SetActionStatus("The Don't alert list holds 256 apps; remove one first."); return; }
        session.IgnoreFirstUse(episode.Application!);
        if (!episode.Acknowledged) _ = AcknowledgeAsync(episode.Id); else RequestRefresh();
    }

    private async Task AcknowledgeAsync(string id)
    {
        SetActionStatus("");
        try { if (!await session.Alerts.AcknowledgeAsync(id)) SetActionStatus("The episode could not be marked as acknowledged. " + session.Alerts.Status); }
        catch (Exception error) { SetActionStatus("The episode could not be marked as acknowledged. " + error.Message); }
        RequestRefresh();
    }

    private void SetActionStatus(string text) { actionStatus.Message = text; actionStatus.IsOpen = text.Length > 0; }
}
