using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Everything one refresh of the page shows, so tests can apply a fixture without a store.</summary>
internal sealed record AlertsPageData(IReadOnlyList<AlertRule> Rules, bool Notifications, AlertLogView Log, string Status);

internal sealed class AlertsPage : ScrollViewer
{
    private sealed record Choice<T>(string Label, T Value) { public override string ToString() => Label; }
    private static readonly Choice<AlertKind>[] Kinds =
    [
        new("Device metric", AlertKind.Metric), new("Network rate", AlertKind.NetworkRate),
        new("Application traffic per day", AlertKind.ApplicationDaily), new("Battery level", AlertKind.Battery),
    ];
    private static readonly Choice<string>[] MetricTargets =
    [
        new("CPU usage", UsageMetrics.Cpu), new("CPU temperature", UsageMetrics.CpuTemperature), new("GPU usage", UsageMetrics.Gpu),
        new("GPU temperature", UsageMetrics.GpuTemperature), new("RAM usage", UsageMetrics.Memory),
    ];
    private static readonly Choice<string>[] NetworkTargets = [new("Download", UsageMetrics.Download), new("Upload", UsageMetrics.Upload)];
    private readonly UiSession session;
    private readonly ComboBox kind = new() { MinWidth = 200 }, target = new() { MinWidth = 160 }, duration = new() { MinWidth = 110 }, logRange = new() { MinWidth = 110 };
    private readonly TextBox application = new() { Width = 180 }, threshold = new() { Width = 90 };
    private readonly TextBlock unit = Ui.Text("", 12, "Muted"), suggestion = Ui.Text("", 12, "Muted"), editorError = Ui.Text("", 12, "Warning");
    private readonly TextBlock rulesEmpty = Ui.Text("No rules yet. Add one above; it watches the readings DeadWare already records.", 12, "Muted");
    private readonly TextBlock logSummary = Ui.Text("", 12, "Muted"), status = Ui.Text("", 12, "Muted"), actionStatus = Ui.Text("", 12, "Warning");
    private readonly StackPanel rules = new(), episodes = new();
    private readonly CheckBox notifications = new() { Content = "Show tray notifications for alerts" };
    private readonly Button add;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private AlertsPageData? data;
    private int revision, suggestionRevision;
    private bool refreshing, refreshAgain, syncing;
    // The log read and whether the page is showing, replaced by the checks (assigned in the constructor, since a field initializer cannot read this).
    private Func<bool> showing;
    private Func<DateTimeOffset, DateTimeOffset, Task<AlertLogView>> readLog;
    private int LogDays => (logRange.SelectedItem as Choice<int>)?.Value ?? 1;

    public AlertsPage(UiSession session)
    {
        this.session = session;
        showing = () => IsLoaded && IsVisible;
        readLog = (from, to) => session.Alerts.ReadEpisodesAsync(from, to);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var body = new StackPanel { Margin = new(6, 10, 6, 10) }; Content = body;

        var editor = new StackPanel(); editor.Children.Add(Ui.Heading("Rules", 16));
        var intro = Ui.Text("A rule fires once per episode when the recorded value is at or above the threshold for the chosen minutes, and re-arms after the value stays below it. Application budgets fire once per day. Battery rules fire as soon as the charge is at or below the level while on battery.", 12, "Muted");
        intro.Margin = new(0, 6, 0, 10); editor.Children.Add(intro);
        var row = new WrapPanel();
        kind.ItemsSource = Kinds; kind.SelectedIndex = 0; AutomationProperties.SetName(kind, "Alert kind"); row.Children.Add(Labeled("Watch", kind));
        target.ItemsSource = MetricTargets; target.SelectedIndex = 1; AutomationProperties.SetName(target, "Alert target"); row.Children.Add(Labeled("Reading", target));
        AutomationProperties.SetName(application, "Application name"); row.Children.Add(Labeled("Executable", application));
        AutomationProperties.SetName(threshold, "Alert threshold"); row.Children.Add(Labeled("At or above", Ui.Row(threshold, unit)));
        duration.ItemsSource = AlertRules.Durations.Select(d => new Choice<int>(d == 1 ? "1 minute" : $"{d} minutes", d)).ToArray(); duration.SelectedIndex = 1;
        AutomationProperties.SetName(duration, "Alert duration"); row.Children.Add(Labeled("For", duration));
        add = Ui.Button("Add rule", AddRule); add.VerticalAlignment = VerticalAlignment.Bottom; add.Margin = new(0, 0, 12, 8); row.Children.Add(add);
        editor.Children.Add(row);
        suggestion.Margin = new(0, 4, 0, 4); editor.Children.Add(suggestion); editor.Children.Add(editorError);
        rules.Margin = new(0, 10, 0, 0); editor.Children.Add(rules); editor.Children.Add(rulesEmpty);
        notifications.Margin = new(0, 14, 0, 0); AutomationProperties.SetName(notifications, "Alert notifications"); editor.Children.Add(notifications);
        editor.Children.Add(Ui.Text("Every episode is logged below whether or not notifications are on. Alerts report that a threshold you chose was crossed in observed readings; they are not a hardware diagnosis.", 11, "Muted"));
        body.Children.Add(Card(editor));

        var log = new StackPanel(); log.Children.Add(Ui.Heading("Recent alerts", 16));
        var controls = new WrapPanel { Margin = new(0, 8, 0, 6) };
        logRange.ItemsSource = new[] { new Choice<int>("Day", 1), new("Week", 7), new("Month", 30) }; logRange.SelectedIndex = 1;
        AutomationProperties.SetName(logRange, "Alert log range"); controls.Children.Add(logRange);
        controls.Children.Add(Ui.Button("Refresh", () => RequestRefresh())); log.Children.Add(controls);
        log.Children.Add(logSummary); episodes.Margin = new(0, 6, 0, 0); log.Children.Add(episodes);
        status.Margin = new(0, 10, 0, 0); log.Children.Add(status);
        log.Children.Add(Ui.Text("Episodes stay in alerts.db in this Windows profile for up to 400 days and are never uploaded. Acknowledging marks an episode as seen; it does not stop later episodes.", 11, "Muted"));
        body.Children.Add(Card(log)); body.Children.Add(actionStatus);

        kind.SelectionChanged += (_, _) => { UpdateEditor(); RequestSuggestion(); };
        target.SelectionChanged += (_, _) => RequestSuggestion();
        application.TextChanged += (_, _) => RequestSuggestion();
        threshold.TextChanged += (_, _) => RequestSuggestion();
        notifications.Checked += (_, _) => { if (!syncing) session.SetAlertNotifications(true); };
        notifications.Unchecked += (_, _) => { if (!syncing) session.SetAlertNotifications(false); };
        logRange.SelectionChanged += (_, _) => RequestRefresh();
        timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += SyncSettings; session.HistoryClearChanged += HistoryClearChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= SyncSettings; session.HistoryClearChanged -= HistoryClearChanged; timer.Stop(); revision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility();
        UpdateEditor(); SyncSettings();
    }
    private static StackPanel Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new(0, 0, 12, 8) };
        var text = Ui.Text(label, 11, "Muted"); text.Margin = new(0, 0, 0, 2); panel.Children.Add(text); panel.Children.Add(control); return panel;
    }
    private static Border Card(UIElement content) { var card = Ui.Panel(content, new(16)); card.Margin = new(0, 0, 0, 12); return card; }
    private AlertKind SelectedKind => (kind.SelectedItem as Choice<AlertKind>)?.Value ?? AlertKind.Metric;
    private void UpdateEditor()
    {
        var selected = SelectedKind;
        target.ItemsSource = selected == AlertKind.NetworkRate ? NetworkTargets : MetricTargets;
        if (target.SelectedIndex < 0) target.SelectedIndex = 0;
        ((FrameworkElement)target.Parent).Visibility = selected is AlertKind.Metric or AlertKind.NetworkRate ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)application.Parent).Visibility = selected == AlertKind.ApplicationDaily ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)duration.Parent).Visibility = selected is AlertKind.Metric or AlertKind.NetworkRate ? Visibility.Visible : Visibility.Collapsed;
        unit.Text = selected switch
        {
            AlertKind.Metric => AlertRules.IsTemperature((target.SelectedItem as Choice<string>)?.Value ?? "") ? "°C" : "%",
            AlertKind.NetworkRate => "MB/s", AlertKind.ApplicationDaily => "MB per day", _ => "% while on battery",
        };
        editorError.Text = "";
    }
    /// <summary>The rule the editor currently describes, or null with a reason.</summary>
    internal AlertRule? Draft(out string error)
    {
        error = "";
        var selected = SelectedKind;
        if (!double.TryParse(threshold.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(threshold.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        { error = "Enter a number for the threshold."; return null; }
        string targetId = selected switch
        {
            AlertKind.Metric or AlertKind.NetworkRate => (target.SelectedItem as Choice<string>)?.Value ?? "",
            AlertKind.ApplicationDaily => application.Text, _ => "",
        };
        double scaled = selected is AlertKind.NetworkRate or AlertKind.ApplicationDaily ? value * 1_000_000 : value;
        int minutes = (duration.SelectedItem as Choice<int>)?.Value ?? 5;
        var rule = AlertRules.Normalize(new AlertRule(Guid.NewGuid().ToString("N"), selected, targetId, scaled, minutes, true, DateTimeOffset.UtcNow));
        if (rule is null) { error = selected == AlertKind.ApplicationDaily ? "Enter the executable name, such as chrome.exe." : "The threshold is out of range."; return null; }
        return rule;
    }
    private void AddRule()
    {
        if (Draft(out string error) is not { } rule) { editorError.Text = error; return; }
        if (session.Settings.Alerts.Count >= AlertRules.MaximumRules) { editorError.Text = $"At most {AlertRules.MaximumRules} rules are kept."; return; }
        editorError.Text = "";
        session.SetAlertRules([.. session.Settings.Alerts, rule]);
        threshold.Text = "";
    }
    private void RemoveRule(string id) => session.SetAlertRules(session.Settings.Alerts.Where(r => r.Id != id).ToArray());
    private void ToggleRule(string id, bool enabled) => session.SetAlertRules(session.Settings.Alerts.Select(r => r.Id == id ? r with { Enabled = enabled } : r).ToArray());
    private void EditRule(AlertRule rule)
    {
        syncing = true;
        try
        {
            kind.SelectedItem = Kinds.First(k => k.Value == rule.Kind); UpdateEditor();
            if (rule.Kind is AlertKind.Metric or AlertKind.NetworkRate)
                target.SelectedItem = ((Choice<string>[])target.ItemsSource).First(t => t.Value == rule.Target);
            application.Text = rule.Kind == AlertKind.ApplicationDaily ? rule.Target : "";
            threshold.Text = rule.Kind is AlertKind.NetworkRate or AlertKind.ApplicationDaily ? (rule.Threshold / 1_000_000).ToString("0.###", CultureInfo.CurrentCulture) : rule.Threshold.ToString("0", CultureInfo.CurrentCulture);
            duration.SelectedItem = ((Choice<int>[])duration.ItemsSource).FirstOrDefault(d => d.Value == rule.Minutes) ?? duration.Items[1];
        }
        finally { syncing = false; }
        RemoveRule(rule.Id); RequestSuggestion();
    }
    private void SyncSettings()
    {
        syncing = true;
        try { notifications.IsChecked = session.Settings.AlertNotifications; }
        finally { syncing = false; }
        RenderRules(session.Settings.Alerts);
        if (data is null) status.Text = session.Alerts.Status;
    }
    private void RenderRules(IReadOnlyList<AlertRule> list)
    {
        rules.Children.Clear();
        rulesEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rule in list)
        {
            var line = new DockPanel { Margin = new(0, 0, 0, 6) };
            var remove = Ui.Button("Remove", () => RemoveRule(rule.Id)); remove.Margin = new(6, 0, 0, 0); DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
            var edit = Ui.Button("Edit", () => EditRule(rule)); edit.Margin = new(6, 0, 0, 0); DockPanel.SetDock(edit, Dock.Right); line.Children.Add(edit);
            var enabled = new CheckBox { Content = AlertRules.Describe(rule), IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center, Tag = rule.Id };
            enabled.Checked += (_, _) => { if (!syncing) ToggleRule(rule.Id, true); };
            enabled.Unchecked += (_, _) => { if (!syncing) ToggleRule(rule.Id, false); };
            line.Children.Add(enabled); rules.Children.Add(line);
        }
    }
    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { timer.Start(); SyncSettings(); _ = RefreshAsync(); RequestSuggestion(); }
        else { timer.Stop(); revision++; }
    }
    /// <summary>An alert-log clear started or ended: the log is read again.</summary>
    private void HistoryClearChanged(HistoryClearStore store) { if (store == HistoryClearStore.Alerts) RequestRefresh(); }
    private void RequestRefresh() { revision++; _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        if (!showing()) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var to = DateTimeOffset.UtcNow; var from = to.AddDays(-LogDays);
            var log = await readLog(from, to);
            if (requested != revision || !showing()) { refreshAgain = true; return; }
            Apply(new(session.Settings.Alerts, session.Settings.AlertNotifications, log, session.Alerts.Status));
        }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (showing()) _ = RefreshAsync(); }
        }
    }
    private void RequestSuggestion()
    {
        if (syncing) return;
        int requested = ++suggestionRevision;
        if (Draft(out _) is not { } rule) { suggestion.Text = ""; return; }
        if (rule.Kind == AlertKind.Battery) { suggestion.Text = "Battery rules have no recorded history to compare with."; return; }
        if (!IsLoaded) return;
        _ = SuggestAsync(rule, requested);
    }
    private async Task SuggestAsync(AlertRule rule, int requested)
    {
        try
        {
            var result = await session.Alerts.SuggestAsync(rule);
            if (requested == suggestionRevision) ApplySuggestion(rule, result);
        }
        catch (Exception) { if (requested == suggestionRevision) suggestion.Text = "Recorded history is unavailable right now."; }
    }
    internal void ApplySuggestion(AlertRule rule, AlertSuggestion result)
    {
        string Window(string name, AlertWindowStats stats, int days)
        {
            if (stats.Maximum is null) return $"{name}: no recorded minutes.";
            string highest = AlertRules.FormatValue(rule, stats.Maximum.Value), average = AlertRules.FormatValue(rule, stats.Average ?? 0);
            string share = stats.ShareAtOrAbove is { } fraction ? $", at or above {AlertRules.FormatThreshold(rule)} in {fraction * 100:0.#}% of {stats.Minutes:N0} minutes" : "";
            return rule.Kind == AlertKind.ApplicationDaily ? $"{name}: {highest} in total, {average} per day on average."
                : $"{name}: highest minute {highest}, average {average}{share}.";
        }
        suggestion.Text = $"Recorded so far · {Window("Last 7 days", result.Week, 7)} {Window("Last 30 days", result.Month, 30)}";
    }
    internal void Apply(AlertsPageData result)
    {
        data = result;
        syncing = true;
        try { notifications.IsChecked = result.Notifications; }
        finally { syncing = false; }
        RenderRules(result.Rules); status.Text = result.Status + $" Storage: {Ui.Bytes(result.Log.StorageBytes)}.";
        episodes.Children.Clear();
        var rulesById = result.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        logSummary.Text = result.Log.Total == 0 ? "No alerts in this period."
            : result.Log.Total > result.Log.Episodes.Count ? $"{result.Log.Episodes.Count} of {result.Log.Total:N0} episodes shown, newest first." : $"{result.Log.Total:N0} episode{(result.Log.Total == 1 ? "" : "s")}, newest first.";
        foreach (var episode in result.Log.Episodes)
        {
            string peak = rulesById.TryGetValue(episode.RuleId, out var rule) ? AlertRules.FormatValue(rule, episode.Peak) : episode.Peak.ToString("0.#", CultureInfo.CurrentCulture);
            string when = episode.End is { } end ? $"{episode.Start.ToLocalTime():g} – {end.ToLocalTime():t}" : $"{episode.Start.ToLocalTime():g} – ongoing";
            var line = new DockPanel { Margin = new(0, 0, 0, 8) };
            var view = Ui.Button("View in Usage history", () => session.RequestUsageDay(episode.Start)); view.Margin = new(6, 0, 0, 0); DockPanel.SetDock(view, Dock.Right); line.Children.Add(view);
            if (!episode.Acknowledged)
            {
                var acknowledge = Ui.Button("Acknowledge", () => _ = AcknowledgeAsync(episode.Id)); acknowledge.Margin = new(6, 0, 0, 0);
                DockPanel.SetDock(acknowledge, Dock.Right); line.Children.Add(acknowledge);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Text(episode.Summary, 13, episode.End is null ? "Warning" : "Ink"));
            text.Children.Add(Ui.Text($"{when} · peak {peak}{(episode.Acknowledged ? " · acknowledged" : episode.End is null ? " · open" : "")}", 11, "Muted"));
            line.Children.Add(text); episodes.Children.Add(line);
        }
    }
    private async Task AcknowledgeAsync(string id)
    {
        actionStatus.Text = "";
        if (!await session.Alerts.AcknowledgeAsync(id)) actionStatus.Text = "The episode could not be marked as acknowledged. " + session.Alerts.Status;
        RequestRefresh();
    }
}
