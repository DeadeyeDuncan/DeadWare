using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.Core;
using Microsoft.Win32;

namespace DeadWare.App;

internal sealed class BatteryHistoryPage : ScrollViewer
{
    private sealed record RangeChoice(string Label, int Days) { public override string ToString() => Label; }
    private readonly UiSession session;
    private readonly ComboBox range = new() { MinWidth = 120 };
    private readonly TextBlock percentage = Ui.Text("—", 42, "Accent", font:"MetricFont"), power = Ui.Text("Waiting for battery…", 17);
    private readonly TextBlock summary = Ui.Text("", 12, "Muted"), recordingStatus = Ui.Text("", 12, "Muted"), storage = Ui.Text("", 11, "Muted"), actionStatus = Ui.Text("", 12, "Warning");
    private readonly BatteryHistoryChart chart = new() { Margin = new(0, 12, 0, 6), VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Grid body = new(), current = new(), controlsLayout = new();
    private readonly WrapPanel controls = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border controlsCard, historyCard, settingsCard;
    private bool controlsBeside;
    private readonly CheckBox recording = new() { Content = "Record battery history on this device" };
    private readonly Button export;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private int revision;
    private bool refreshing, refreshAgain, syncing;
    private int Days => (range.SelectedItem as RangeChoice)?.Days ?? 1;

    public BatteryHistoryPage(UiSession session, bool showOpenWindow = true)
    {
        this.session = session;
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Margin = new(6, 10, 6, 10); Content = body;
        foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})body.RowDefinitions.Add(new() {Height=height});body.RowDefinitions[1].MinHeight=370;
        current.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); current.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        percentage.Margin = new(0, 0, 26, 0); current.Children.Add(percentage);
        power.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(power, 1); current.Children.Add(power);
        controlsLayout.ColumnDefinitions.Add(new() {Width=GridLength.Auto});controlsLayout.ColumnDefinitions.Add(new() {Width=new(1,GridUnitType.Star)});
        for(int i=0;i<3;i++)controlsLayout.RowDefinitions.Add(new() {Height=GridLength.Auto});Grid.SetColumnSpan(current,2);controlsLayout.Children.Add(current);
        range.ItemsSource = new[] { new RangeChoice("Day", 1), new("Week", 7), new("Month", 30), new("Year", 365), new("All history", 0) }; range.SelectedIndex = 0;
        AutomationProperties.SetName(range, "Battery history time range"); controls.Children.Add(range);
        controls.Children.Add(Ui.Button("Refresh", () => RequestRefresh()));
        export = Ui.Button("Export CSV…", () => _ = ExportAsync()); controls.Children.Add(export);
        if (showOpenWindow) controls.Children.Add(Ui.Button("Open in window ↗", () => session.OpenDetached("batteryhistory")));
        Grid.SetRow(controls,1);Grid.SetColumnSpan(controls,2);controlsLayout.Children.Add(controls);Grid.SetRow(actionStatus,2);Grid.SetColumnSpan(actionStatus,2);controlsLayout.Children.Add(actionStatus);
        controlsCard=Ui.Panel(controlsLayout,new Thickness(16,12,16,12));controlsCard.Margin=new(0,0,0,10);body.Children.Add(controlsCard);
        var history = new Grid();foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto,GridLength.Auto})history.RowDefinitions.Add(new() {Height=height});
        history.Children.Add(Ui.Heading("Battery charge", 16));Grid.SetRow(chart,1);history.Children.Add(chart);
        var legend = new WrapPanel { Margin = new(0, 0, 0, 8) };
        legend.Children.Add(TraceLegend("Saver off", "Accent", DashStyles.Solid));
        legend.Children.Add(TraceLegend("Saver on", "Warning", DashStyles.Dash));
        legend.Children.Add(TraceLegend("State unknown", "Muted", DashStyles.Dot));
        Grid.SetRow(legend,2);history.Children.Add(legend);Grid.SetRow(summary,3);history.Children.Add(summary);
        var hint = Ui.Text("The charge line changes color and style at recorded saver changes. Hollow dots mean mixed or unknown saver state in a summarized time bucket; vertical ranges show minimum–maximum charge. Green marks: charging. Amber marks: saver enabled; outlined marks: mixed states. Gaps remain unconnected. Dates use local time.", 11, "Muted");
        hint.Margin = new(0, 10, 0, 0);Grid.SetRow(hint,4);history.Children.Add(hint);historyCard=Card(history);historyCard.Margin=new(0,0,0,10);historyCard.MinHeight=360;Grid.SetRow(historyCard,1);body.Children.Add(historyCard);
        var settings = new StackPanel(); settings.Children.Add(Ui.Heading("Keep a lasting record", 16)); recording.Margin = new(0, 12, 0, 8); settings.Children.Add(recording);
        settings.Children.Add(recordingStatus); storage.Margin = new(0, 8, 0, 8); settings.Children.Add(storage);
        var notes=new StackPanel();notes.Children.Add(Ui.Text("One observation per minute, plus charging and energy saver (battery saver) changes, retained indefinitely. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. Summarizing the chart keeps the original observations. Export the selected range as a new CSV file to keep a backup.", 12, "Muted"));
        var local = Ui.Text("History stays in this Windows profile and survives app upgrades. Copy battery-history.db while DeadWare is fully quit to back it up. A device reset or profile removal needs your own backup.", 11, "Muted"); local.Margin = new(0, 8, 0, 0);notes.Children.Add(local);
        settings.Children.Add(new Expander {Header="Retention and backup",IsExpanded=false,Margin=new Thickness(0,10,0,0),Content=notes});settingsCard=Card(settings);Grid.SetRow(settingsCard,2);body.Children.Add(settingsCard);
        controlsCard.SizeChanged+=(_,_)=>InvalidateMeasure();settingsCard.SizeChanged+=(_,_)=>InvalidateMeasure();SizeChanged+=(_,e)=>UpdateControlsLayout(e.NewSize.Width);
        recording.IsChecked = session.Settings.BatteryRecording;
        recording.Checked += (_, _) => ChangeRecording(true); recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => RequestRefresh(); timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += RefreshCurrent; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= RefreshCurrent; timer.Stop(); revision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility(); RefreshCurrent();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        double width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width-SystemParameters.VerticalScrollBarWidth):1000;
        var measure=new Size(width,double.PositiveInfinity);controlsCard.Measure(measure);settingsCard.Measure(measure);historyCard.Measure(measure);
        double historyBudget=Math.Max(370,historyCard.DesiredSize.Height+10);body.RowDefinitions[1].MinHeight=historyBudget;
        double minimum=controlsCard.DesiredSize.Height+settingsCard.DesiredSize.Height+historyBudget;
        body.Height=Math.Max(minimum,double.IsFinite(availableSize.Height)?availableSize.Height:minimum);return base.MeasureOverride(availableSize);
    }
    private void UpdateControlsLayout(double width)
    {
        bool beside=width>=760;if(controlsBeside==beside)return;
        Grid.SetColumnSpan(current,beside?1:2);Grid.SetColumn(controls,beside?1:0);Grid.SetRow(controls,beside?0:1);Grid.SetColumnSpan(controls,beside?1:2);
        controls.HorizontalAlignment=beside?HorizontalAlignment.Right:HorizontalAlignment.Left;controlsBeside=beside;InvalidateMeasure();
    }
    private static StackPanel TraceLegend(string label, string color, DashStyle dash)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 18, 4) };
        var line = new System.Windows.Shapes.Line { X1 = 0, Y1 = 7, X2 = 24, Y2 = 7, Width = 24, Height = 14, StrokeThickness = 2,
            StrokeDashArray = dash.Dashes, StrokeDashCap = PenLineCap.Round, Margin = new(0, 0, 6, 0) };
        line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, color);
        item.Children.Add(line); item.Children.Add(Ui.Text(label, 11, color)); return item;
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
