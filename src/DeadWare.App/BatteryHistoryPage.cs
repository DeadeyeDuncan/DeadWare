using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DeadWare.Core;
using Microsoft.Win32;

namespace DeadWare.App;

internal sealed class BatteryHistoryPage : ScrollViewer
{
    private sealed record RangeChoice(string Label, int Days) { public override string ToString() => Label; }
    private readonly UiSession session;
    private readonly ComboBox range = new() { MinWidth = 120 };
    private readonly TextBlock percentage = Ui.Text("—", 42, "Accent"), power = Ui.Text("Waiting for battery…", 17), saver = Ui.Text("Battery saver unknown", 13, "Muted");
    private readonly TextBlock summary = Ui.Text("", 12, "Muted"), recordingStatus = Ui.Text("", 12, "Muted"), storage = Ui.Text("", 11, "Muted"), actionStatus = Ui.Text("", 12, "Warning");
    private readonly BatteryHistoryChart chart = new() { Height = 320, Margin = new(0, 12, 0, 6) };
    private readonly CheckBox recording = new() { Content = "Record battery history on this device" };
    private readonly Button export;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private int revision;
    private bool refreshing, refreshAgain, syncing;
    private int Days => (range.SelectedItem as RangeChoice)?.Days ?? 1;

    public BatteryHistoryPage(UiSession session, bool showOpenWindow = true)
    {
        this.session = session;
        SetResourceReference(BackgroundProperty, "Background");
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var body = new StackPanel { Margin = new(6, 10, 6, 10) }; Content = body;
        var current = new Grid(); current.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); current.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        percentage.Margin = new(0, 0, 26, 0); current.Children.Add(percentage);
        var state = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; state.Children.Add(power); saver.Margin = new(0, 6, 0, 0); state.Children.Add(saver); Grid.SetColumn(state, 1); current.Children.Add(state);
        body.Children.Add(Card(current));
        var controls = new WrapPanel { Margin = new(0, 0, 0, 10) };
        range.ItemsSource = new[] { new RangeChoice("Day", 1), new("Week", 7), new("Month", 30), new("Year", 365), new("All history", 0) }; range.SelectedIndex = 0;
        AutomationProperties.SetName(range, "Battery history time range"); controls.Children.Add(range);
        controls.Children.Add(Ui.Button("Refresh", () => RequestRefresh()));
        export = Ui.Button("Export CSV…", () => _ = ExportAsync()); controls.Children.Add(export);
        if (showOpenWindow) controls.Children.Add(Ui.Button("Open in window ↗", () => session.OpenDetached("batteryhistory")));
        body.Children.Add(controls);
        var history = new StackPanel(); history.Children.Add(Ui.Text("Battery charge", 16)); history.Children.Add(chart); history.Children.Add(summary);
        var hint = Ui.Text("Line and dots: charge. Vertical ranges: minimum–maximum in a summarized time bucket. Green marks: charging. Amber marks: battery saver. Solid marks mean all observations in the bucket reported that state; outlined marks mean mixed states. Gaps remain unconnected. Dates use local time.", 11, "Muted");
        hint.Margin = new(0, 10, 0, 0); history.Children.Add(hint); body.Children.Add(Card(history));
        var settings = new StackPanel(); settings.Children.Add(Ui.Text("Keep a lasting record", 16)); recording.Margin = new(0, 12, 0, 8); settings.Children.Add(recording);
        settings.Children.Add(recordingStatus); storage.Margin = new(0, 8, 0, 8); settings.Children.Add(storage);
        settings.Children.Add(Ui.Text("One observation per minute, plus charging and battery-saver changes, retained indefinitely. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. Summarizing the chart keeps the original observations. Export the selected range as a new CSV file to keep a backup.", 12, "Muted"));
        var local = Ui.Text("History stays in this Windows profile and survives app upgrades. Copy battery-history.db while DeadWare is fully quit to back it up. A device reset or profile removal needs your own backup.", 11, "Muted"); local.Margin = new(0, 8, 0, 0); settings.Children.Add(local);
        body.Children.Add(Card(settings)); body.Children.Add(actionStatus);
        recording.IsChecked = session.Settings.BatteryRecording;
        recording.Checked += (_, _) => ChangeRecording(true); recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => RequestRefresh(); timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += RefreshCurrent; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= RefreshCurrent; timer.Stop(); revision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility(); RefreshCurrent();
    }
    private static Border Card(UIElement content) { var card = Ui.Panel(content, new(16)); card.Margin = new(0, 0, 0, 12); return card; }
    private void ChangeRecording(bool enabled)
    { if (syncing) return; session.SetBatteryRecording(enabled); recordingStatus.Text = session.Battery.Status; }
    private void RefreshCurrent()
    {
        var current = session.Stale ? BatteryReading.Unavailable : session.Current?.Battery ?? BatteryReading.Unavailable;
        percentage.Text = Ui.Number(current.Percentage, " %");
        power.Text = session.Stale ? "Waiting for a fresh reading" : current.PowerState switch
        {
            BatteryPowerState.NoBattery => "No battery detected", BatteryPowerState.Charging => "Charging",
            BatteryPowerState.OnBattery => "On battery", BatteryPowerState.PluggedIn => "Plugged in · not charging", _ => "Battery reading unavailable"
        };
        saver.Text = current.SaverEnabled is { } saving ? $"Battery saver {(saving ? "on" : "off")}" : "Battery saver unknown";
        syncing = true; recording.IsChecked = session.Settings.BatteryRecording; syncing = false;
        recordingStatus.Text = session.Battery.Status; chart.InvalidateVisual();
    }
    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { timer.Start(); RefreshCurrent(); _ = RefreshAsync(); }
        else { timer.Stop(); revision++; }
    }
    private void RequestRefresh() { revision++; _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        if (!IsLoaded || !IsVisible) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var result = await session.Battery.ReadViewAsync(Days);
            if (requested != revision || !IsLoaded || !IsVisible) { refreshAgain = true; return; }
            ApplyView(result);
        }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (IsLoaded && IsVisible) _ = RefreshAsync(); }
        }
    }
    internal void ApplyView(BatteryHistoryView view)
    {
        chart.SetData(view); recordingStatus.Text = view.Status;
        long count = view.Buckets.Sum(b => b.Samples);
        var valid = view.Buckets.Where(b => b.Minimum.HasValue && b.Maximum.HasValue).ToArray();
        summary.Text = valid.Length == 0 ? "No charge observations in this range yet. Saved readings will appear as monitoring continues."
            : $"MIN {Ui.Number(valid.Min(b => b.Minimum), "%")}    MAX {Ui.Number(valid.Max(b => b.Maximum), "%")}    ·    {count:N0} observations in this view";
        storage.Text = $"Local history: {Ui.Bytes(view.StorageBytes)} · {view.TotalObservations:N0} observations retained indefinitely" +
            (view.FirstRecorded is { } first ? $"\nRecorded since {first.ToLocalTime():g}" : "");
    }
    private async Task ExportAsync()
    {
        int days = Days;
        var dialog = new SaveFileDialog { Title = "Export battery history to a new CSV", Filter = "CSV files (*.csv)|*.csv", FileName = $"DeadWare-battery-{DateTime.Now:yyyyMMdd-HHmmss}.csv", OverwritePrompt = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        export.IsEnabled = false; actionStatus.Text = "Exporting saved observations…";
        try { await session.Battery.ExportCsvAsync(dialog.FileName, days); actionStatus.Text = "Export saved: " + dialog.FileName; }
        catch (Exception error) { actionStatus.Text = "Export could not be saved. Choose a new file name. " + error.Message; }
        finally { export.IsEnabled = true; }
    }
}
