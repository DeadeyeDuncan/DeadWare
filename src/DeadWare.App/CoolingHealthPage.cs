using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DeadWare.Core;
using Microsoft.Win32;

namespace DeadWare.App;

/// <summary>Persistent history is queried only while this page is visible, never by the UI sampling timer.</summary>
internal sealed class CoolingHealthPage : ScrollViewer
{
    private sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private readonly UiSession session;
    private readonly ComboBox device = new() { MinWidth = 260, MaxWidth = 570, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox range = new() { MinWidth = 100 }, metric = new() { MinWidth = 150 };
    private readonly ComboBox references = new() { MinWidth = 230, MaxWidth = 550 }, detailDays = new(), summaryDays = new();
    private readonly TextBox referenceFrom = new() { Width = 170 }, referenceTo = new() { Width = 170 }, referenceLabel = new() { Width = 230, MaxLength = 80 };
    private readonly CheckBox recording = new() { Content = "Record thermal history on this PC" }, notifications = new() { Content = "Notify me about sustained cooling changes" };
    private readonly TextBlock recordingStatus = Ui.Text("History starts when monitoring begins.", 12, "Muted"), storage = Ui.Text("", 11, "Muted");
    private readonly TextBlock assessmentHeading = Ui.Heading("Learning", 21), assessmentText = Ui.Text("Save a reference period before comparing cooling performance.", 13);
    private readonly TextBlock comparison = Ui.Text("", 15, font:"MetricFont"), coverage = Ui.Text("", 11, "Muted"), chartSummary = Ui.Text("", 11, "Muted"), referenceStatus = Ui.Text("No reference saved yet.", 12, "Muted");
    private readonly TextBlock actionStatus = Ui.Text("", 12, "Warning");
    /// <summary>A recording or read failure, mirrored into the controls card so it shows whichever lower tab is selected.</summary>
    private readonly TextBlock recordingWarning = Ui.Text("", 12, "Warning");
    private readonly StackPanel warningRows = new();
    private readonly ThermalHistoryChart chart = new() { Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Stretch };
    private readonly Grid body = new(), historyLayout = new(), chartColumn = new(), assessmentPanel = new();
    private readonly Border controlsCard, historyCard, narrowAssessmentCard, lowerCard;
    private readonly TabControl tabs = new();
    private readonly TabItem referenceTab = new() { Header = "Reference" }, warningsTab = new() { Header = "Warnings" }, setupTab = new() { Header = "Recording & setup" };
    private bool assessmentBeside;
    internal TabControl Tabs => tabs;
    internal TabItem ReferenceTab => referenceTab;
    internal TabItem WarningsTab => warningsTab;
    internal TabItem SetupTab => setupTab;
    private readonly Button saveReference, useReference, export, newSetup;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CoolingView? view;
    private bool syncing, refreshing, refreshAgain, operationBusy;
    private int requestRevision;
    private DateTimeOffset displayedFrom, displayedTo;

    public CoolingHealthPage(UiSession session)
    {
        this.session = session;
        Background = System.Windows.Media.Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Margin = new Thickness(6, 10, 6, 10);
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})body.RowDefinitions.Add(new() {Height=height});
        body.RowDefinitions[2].MinHeight=370;
        Content = body;
        device.ToolTip = "Choose a CPU/GPU and its recorded cooling setup. This does not change the live GPU selection.";
        SetAccessibleName(device, "Cooling history device and setup"); SetAccessibleName(range, "Thermal history time range"); SetAccessibleName(metric, "Thermal history metric");
        range.ItemsSource = new[] { new Choice<int>("Day", 1), new("Week", 7), new("Month", 30) }; range.SelectedIndex = 0;
        metric.ItemsSource = new[] { new Choice<string>("Temperature", "temperature"), new("Usage", "load"), new("Package power", "power"), new("Clock", "clock"), new("Fan speed", "fan") }; metric.SelectedIndex = 0;
        var controls = new StackPanel();var controlRow = new WrapPanel();
        var deviceCaption=Ui.Text("Device & cooling setup",11,"Muted");deviceCaption.Margin=new(0,0,8,0);controlRow.Children.Add(deviceCaption);controlRow.Children.Add(device);
        controlRow.Children.Add(Ui.Button("Refresh", () => _ = RefreshAsync()));
        export = Ui.Button("Export CSV…", () => _ = ExportAsync());controlRow.Children.Add(export);controls.Children.Add(controlRow);
        recordingWarning.Margin = new(3, 8, 3, 0);recordingWarning.Visibility = Visibility.Collapsed;recordingWarning.TextWrapping = TextWrapping.Wrap;controls.Children.Add(recordingWarning);actionStatus.Margin = new(3, 8, 3, 10);actionStatus.Visibility = Visibility.Collapsed;controls.Children.Add(actionStatus);
        controlsCard=Ui.Panel(controls,new Thickness(12,6,12,6));controlsCard.Margin=new(0,0,0,10);body.Children.Add(controlsCard);
        var historyHeading = new DockPanel();var historyButtons = new WrapPanel();historyButtons.Children.Add(range);historyButtons.Children.Add(metric);
        DockPanel.SetDock(historyButtons,Dock.Right);historyHeading.Children.Add(historyButtons);historyHeading.Children.Add(Ui.Heading("Thermal history",16));
        foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})chartColumn.RowDefinitions.Add(new() {Height=height});
        chartColumn.Children.Add(historyHeading);Grid.SetRow(chart,1);chartColumn.Children.Add(chart);Grid.SetRow(chartSummary,2);chartColumn.Children.Add(chartSummary);
        var chartNote = Ui.Text("Dots show recorded averages; faint vertical lines show min–max. Gaps stay unconnected. Dates and times use this PC's local time.", 11, "Muted");chartNote.Margin=new(0,8,0,0);Grid.SetRow(chartNote,3);chartColumn.Children.Add(chartNote);
        historyLayout.RowDefinitions.Add(new() {Height=new(1,GridUnitType.Star)});historyLayout.ColumnDefinitions.Add(new() {Width=new(1,GridUnitType.Star)});historyLayout.ColumnDefinitions.Add(new() {Width=new(0)});historyLayout.Children.Add(chartColumn);
        historyCard=Card(historyLayout);historyCard.Margin=new(0,10,0,0);historyCard.MinHeight=360;Grid.SetRow(historyCard,2);body.Children.Add(historyCard);

        var assessment = new StackPanel(); assessment.Children.Add(assessmentHeading); assessmentText.Margin = new(0, 8, 0, 8); assessment.Children.Add(assessmentText);
        comparison.Margin = new(0, 3, 0, 9); assessment.Children.Add(comparison); assessment.Children.Add(coverage);
        var limit = Ui.Text("This compares cooling behavior, not hardware life expectancy. Room temperature, workload, mounting, dust and fan settings can explain a change. Pump, coolant and flow diagnosis requires confirmed sensors.", 11, "Muted");limit.Margin=new(0,12,0,0);assessment.Children.Add(limit);
        assessmentPanel.RowDefinitions.Add(new() {Height=GridLength.Auto});assessmentPanel.RowDefinitions.Add(new() {Height=GridLength.Auto});assessmentPanel.Children.Add(Ui.Caption("Cooling assessment"));
        var assessmentScroll=new ScrollViewer {Content=assessment,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        assessmentScroll.PreviewMouseWheel+=(_,e)=>
        {
            if(e.Handled||e.Delta==0)return;
            bool canScroll=e.Delta<0?assessmentScroll.VerticalOffset+.01<assessmentScroll.ScrollableHeight:assessmentScroll.VerticalOffset>.01;
            if(canScroll)return;
            e.Handled=true;RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice,e.Timestamp,e.Delta) {RoutedEvent=MouseWheelEvent});
        };
        Grid.SetRow(assessmentScroll,1);assessmentPanel.Children.Add(assessmentScroll);narrowAssessmentCard=Card(assessmentPanel);Grid.SetRow(narrowAssessmentCard,1);body.Children.Insert(body.Children.IndexOf(historyCard),narrowAssessmentCard);

        var reference = new StackPanel(); reference.Children.Add(Ui.Heading("Your fixed reference", 16)); referenceStatus.Margin = new(0, 8, 0, 8); reference.Children.Add(referenceStatus);
        SetAccessibleName(references, "Saved cooling reference");
        useReference = Ui.Button("Use reference", () => _ = UseReferenceAsync());
        var savedReferences = new WrapPanel(); savedReferences.Children.Add(references); savedReferences.Children.Add(useReference); reference.Children.Add(savedReferences);
        var referenceEditor = new StackPanel();
        var referenceHint = Ui.Text("Choose up to seven days when this setup was believed to be working normally. Saving needs at least 30 eligible minutes with temperature, load and power. Warm-up, missing readings and changing loads are excluded. New readings never overwrite a reference.", 12, "Muted"); referenceHint.Margin = new(0, 8, 0, 10); referenceEditor.Children.Add(referenceHint);
        var shortcuts = new WrapPanel();
        foreach (int hours in new[] { 1, 24, 168 }) { int h = hours; shortcuts.Children.Add(Ui.Button(hours == 1 ? "Last hour" : hours == 24 ? "Last day" : "Last week", () => SetReferencePeriod(h))); }
        referenceEditor.Children.Add(shortcuts);
        SetAccessibleName(referenceFrom, "Reference start date and time"); SetAccessibleName(referenceTo, "Reference end date and time"); SetAccessibleName(referenceLabel, "Reference name");
        referenceFrom.ToolTip = referenceTo.ToolTip = "Local time, yyyy-MM-dd HH:mm";
        var period = new WrapPanel(); period.Children.Add(Field("From (local)", referenceFrom)); period.Children.Add(Field("To (local)", referenceTo)); period.Children.Add(Field("Reference name", referenceLabel)); referenceEditor.Children.Add(period);
        referenceEditor.Children.Add(Ui.Text("Date format: yyyy-MM-dd HH:mm. Detailed history must still be retained for the selected period.", 11, "Muted"));
        saveReference = Ui.Button("Save this period as a reference", () => _ = SaveReferenceAsync()); saveReference.HorizontalAlignment = HorizontalAlignment.Left; saveReference.Margin = new(0, 12, 0, 2); referenceEditor.Children.Add(saveReference);
        var referenceExpander = new Expander { Header = "Save a new reference", Content = referenceEditor, IsExpanded = false, Margin = new Thickness(0, 10, 0, 0) };reference.Children.Add(referenceExpander);

        var settings = new StackPanel();
        recording.Margin = new(0, 12, 0, 8); notifications.Margin = new(0, 8, 0, 8); settings.Children.Add(recording); settings.Children.Add(recordingStatus); settings.Children.Add(notifications);
        settings.Children.Add(Ui.Text("Notifications require ten consecutive comparable minutes. A warning needs at least a 5°C rise above the matched mean and 3°C above its 95th percentile. Acknowledgment and a 24-hour cooldown prevent repeated alerts. These are conservative comparison rules, not manufacturer limits.", 11, "Muted"));
        detailDays.ItemsSource = new[] { new Choice<int>("7 days", 7), new("30 days", 30) }; summaryDays.ItemsSource = new[] { new Choice<int>("30 days", 30), new("90 days", 90) };
        SetAccessibleName(detailDays, "Detailed history retention"); SetAccessibleName(summaryDays, "Summary history retention");
        var retention = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) }; retention.Children.Add(Field("Minute details", detailDays)); retention.Children.Add(Field("Hourly summaries", summaryDays)); settings.Children.Add(retention); settings.Children.Add(storage);
        newSetup = Ui.Button("Start a new cooling setup", () => _ = NewSetupAsync()); newSetup.HorizontalAlignment = HorizontalAlignment.Left; newSetup.Margin = new(0, 14, 0, 6); settings.Children.Add(newSetup);
        settings.Children.Add(Ui.Text("Use after cleaning, changing the cooler, fan curve or power limits. Earlier history and references stay available under the previous setup; new comparisons require a fresh reference.", 11, "Muted"));
        referenceTab.Content=reference;warningsTab.Content=warningRows;setupTab.Content=settings;tabs.Items.Add(referenceTab);tabs.Items.Add(warningsTab);tabs.Items.Add(setupTab);tabs.SelectedItem=referenceTab;
        lowerCard=Card(tabs);Grid.SetRow(lowerCard,3);body.Children.Add(lowerCard);
        controlsCard.SizeChanged+=(_,_)=>InvalidateMeasure();assessmentPanel.SizeChanged+=(_,_)=>{if(!assessmentBeside)InvalidateMeasure();};narrowAssessmentCard.SizeChanged+=(_,_)=>InvalidateMeasure();lowerCard.SizeChanged+=(_,_)=>InvalidateMeasure();
        tabs.SelectionChanged+=(_,e)=>{if(e.OriginalSource!=tabs)return;InvalidateMeasure();};SizeChanged+=(_,e)=>UpdateAssessmentPlacement(e.NewSize.Width);

        syncing = true;
        recording.IsChecked = session.Settings.CoolingRecording; notifications.IsChecked = session.Settings.CoolingNotifications;
        detailDays.SelectedIndex = session.Settings.CoolingDetailedDays == 7 ? 0 : 1;
        summaryDays.SelectedIndex = session.Settings.CoolingSummaryDays == 30 ? 0 : 1;
        syncing = false; SetReferencePeriod(24); referenceLabel.Text = "My reference";
        device.SelectionChanged += (_, _) => { if (!syncing) RequestRefresh(); };
        range.SelectionChanged += (_, _) => { if (!syncing) RequestRefresh(); };
        metric.SelectionChanged += (_, _) => RenderChart();
        recording.Checked += (_, _) => ApplySettings(); recording.Unchecked += (_, _) => ApplySettings();
        notifications.Checked += (_, _) => ApplySettings(); notifications.Unchecked += (_, _) => ApplySettings();
        detailDays.SelectionChanged += (_, _) => ApplySettings(); summaryDays.SelectionChanged += (_, _) => ApplySettings();
        refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += ThemeChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= ThemeChanged; refreshTimer.Stop(); requestRevision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility();
        UpdateButtons();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width-SystemParameters.VerticalScrollBarWidth):1000;
        var measure=new Size(width,double.PositiveInfinity);controlsCard.Measure(measure);lowerCard.Measure(measure);
        if(!assessmentBeside)narrowAssessmentCard.Measure(measure);
        double minimum=controlsCard.DesiredSize.Height+(assessmentBeside?0:narrowAssessmentCard.DesiredSize.Height)+lowerCard.DesiredSize.Height+370;
        body.Height=Math.Max(minimum,double.IsFinite(availableSize.Height)?availableSize.Height:minimum);
        return base.MeasureOverride(availableSize);
    }
    private void UpdateAssessmentPlacement(double width)
    {
        bool beside=width>=1100;if(assessmentBeside==beside)return;
        if(beside){body.Children.Remove(narrowAssessmentCard);narrowAssessmentCard.Child=null;}
        else historyLayout.Children.Remove(assessmentPanel);
        assessmentPanel.ClearValue(Grid.ColumnProperty);assessmentPanel.Margin=beside?new(8,0,0,0):new(0);assessmentPanel.RowDefinitions[1].Height=beside?new(1,GridUnitType.Star):GridLength.Auto;
        historyLayout.ColumnDefinitions[1].Width=new(beside?280:0);
        if(beside){Grid.SetColumn(assessmentPanel,1);historyLayout.Children.Add(assessmentPanel);}
        else {narrowAssessmentCard.Child=assessmentPanel;body.Children.Insert(body.Children.IndexOf(historyCard),narrowAssessmentCard);}
        assessmentBeside=beside;InvalidateMeasure();
    }

    /// <summary>The Recording &amp; setup tab shows every status; a failure also shows in the controls card, which every tab leaves visible.</summary>
    private void SetRecordingStatus(string text)
    {
        recordingStatus.Text = text;
        bool failed = text.StartsWith("History recording unavailable", StringComparison.Ordinal) || text.StartsWith("History could not be read", StringComparison.Ordinal);
        recordingWarning.Text = failed ? text : ""; recordingWarning.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }
    private static void SetAccessibleName(DependencyObject control, string name) => AutomationProperties.SetName(control, name);
    private static Border Card(UIElement child) { var card = Ui.Panel(child, new Thickness(16)); card.Margin = new(0, 0, 0, 12); return card; }
    private static StackPanel Field(string label, FrameworkElement input)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 5, 12, 5) }; stack.Children.Add(Ui.Text(label, 11, "Muted")); input.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(input); return stack;
    }
    private void ThemeChanged() { if (IsVisible) chart.InvalidateVisual(); }
    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { refreshTimer.Start(); _ = RefreshAsync(); }
        else { refreshTimer.Stop(); requestRevision++; }
    }
    private int Days => (range.SelectedItem as Choice<int>)?.Value ?? 1;
    private ThermalDevice? SelectedDevice => (device.SelectedItem as Choice<ThermalDevice>)?.Value;
    private void RequestRefresh() { requestRevision++; _ = RefreshAsync(); }

    private async Task RefreshAsync()
    {
        if (!IsLoaded || !IsVisible) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        int revision = requestRevision;
        var selected = SelectedDevice;
        try
        {
            var result = await session.Cooling.ReadViewAsync(selected?.Id, selected?.ConfigurationId, Days);
            if (revision != requestRevision || !IsLoaded || !IsVisible) { refreshAgain = true; return; }
            ApplyView(result);
        }
        catch (Exception error) { SetRecordingStatus("History could not be read: " + error.Message); }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (IsLoaded && IsVisible) _ = RefreshAsync(); }
        }
    }

    // Kept separate from async fetching so an offscreen fixture can exercise real content without a collector.
    internal void ApplyView(CoolingView result)
    {
        view = result;
        syncing = true;
        // Devices arrive most recently recorded first; a repeated label is an older sensor map of the same hardware.
        var seen = new HashSet<string>();
        var devices = result.Devices.Select((d, n) => (d, n)).OrderBy(x => x.d.Kind switch { "CPU" => 0, "GPU" => 1, "Storage" => 2, _ => 3 })
            .ThenBy(x => x.d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.n).Select(x => x.d).Select(d => (Label: DeviceLabel(d), Device: d))
            .Select(d => new Choice<ThermalDevice>(seen.Add(d.Label) ? d.Label : $"{d.Label} · older sensor map {d.Device.Id[..Math.Min(6, d.Device.Id.Length)]}", d.Device)).ToArray();
        device.ItemsSource = devices;
        device.SelectedItem = devices.FirstOrDefault(c => c.Value.Id == result.SelectedDevice?.Id && c.Value.ConfigurationId == result.SelectedDevice?.ConfigurationId);
        var priorReference = (references.SelectedItem as Choice<ThermalReference>)?.Value.Id;
        var choices = result.References.Select(r => new Choice<ThermalReference>($"{r.Label} · {r.Created.ToLocalTime():MMM d HH:mm}", r)).ToArray();
        references.ItemsSource = choices;
        references.SelectedItem = choices.FirstOrDefault(c => c.Value.Id == priorReference) ?? choices.FirstOrDefault(c => c.Value.Id == result.ActiveReference?.Id) ?? choices.FirstOrDefault();
        syncing = false;
        SetRecordingStatus(result.Status);
        storage.Text = $"Local storage: {Ui.Bytes(result.StorageBytes)} · Database limit 256 MiB plus journal. Reducing retention removes older history; saved references are retained.";
        assessmentHeading.Text = result.Assessment.State;
        assessmentHeading.SetResourceReference(TextBlock.ForegroundProperty, result.Assessment.ShouldWarn ? "Warning" : "Accent");
        assessmentText.Text = result.Assessment.Message;
        comparison.Text = result.Assessment.CurrentTemperature.HasValue
            ? $"Current {Ui.Number(result.Assessment.CurrentTemperature, "°C")}    Reference {Ui.Number(result.Assessment.ReferenceTemperature, "°C")}    Difference {Signed(result.Assessment.Delta)}"
            : "Waiting for comparable temperature, load and power readings.";
        coverage.Text = $"Matched recent minutes: {result.Assessment.MatchedWindows} / {result.Assessment.RequiredWindows}" +
            (result.Assessment.ReferenceMinimum.HasValue ? $" · Matched reference range {Ui.Number(result.Assessment.ReferenceMinimum, "°C")}–{Ui.Number(result.Assessment.ReferenceMaximum, "°C")}" : "") +
            "\nReference comparisons apply only to matched operating conditions, not every point in the history graph.";
        referenceStatus.Text = result.ActiveReference is { } reference
            ? $"Active: {reference.Label} · {reference.Windows.Count:N0} eligible minutes\n{reference.Start.ToLocalTime():g} to {reference.End.ToLocalTime():g} · saved {reference.Created.ToLocalTime():g}"
            : "No reference for this device and setup. Allow history to accumulate, then save a period you trust.";
        RenderChart(); RenderWarnings(); UpdateButtons();
    }

    private string DeviceLabel(ThermalDevice value)
    {
        string setup = value.ConfigurationId == session.Settings.CoolingConfigurationId ? "current setup" : "previous setup " + value.ConfigurationId[..Math.Min(6, value.ConfigurationId.Length)];
        return $"{value.Kind} · {value.Name} · {setup}";
    }
    private static string Signed(double? value) => value.HasValue ? value.Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + "°C" : "—";

    private void RenderChart()
    {
        if (view is null) return;
        displayedTo = DateTimeOffset.UtcNow; displayedFrom = displayedTo.AddDays(-Days);
        string selectedMetric = (metric.SelectedItem as Choice<string>)?.Value ?? "temperature";
        chart.SetData(view.History, selectedMetric, displayedFrom, displayedTo);
        var values = view.History.Select(w => ThermalHistoryChart.Metric(w, selectedMetric)).Where(v => v.Count > 0 && v.Average.HasValue).ToArray();
        long validCount = values.Sum(v => (long)v.Count);
        int eligible = view.History.Count(w => w.Eligible && w.ResolutionSeconds == 60);
        string resolution = string.Join(" / ", view.History.Select(w => w.ResolutionSeconds).Distinct().Order().Select(s => s < 3600 ? $"{s / 60d:0.#} min" : $"{s / 3600d:0.#} hour"));
        string unit = ThermalHistoryChart.Unit(selectedMetric);
        chartSummary.Text = values.Length == 0 ? "No valid samples for this metric. Unsupported sensors remain unavailable."
            : $"MIN {Ui.Number(values.Min(v => v.Minimum), " " + unit)}    AVG {Ui.Number(values.Sum(v => v.Average!.Value * v.Count) / validCount, " " + unit)}    MAX {Ui.Number(values.Max(v => v.Maximum), " " + unit)}\n" +
                $"{validCount:N0} valid samples · {view.History.Count:N0} plotted windows · {resolution} resolution · {eligible:N0} eligible minute windows in this view";
    }

    private void RenderWarnings()
    {
        warningRows.Children.Clear();
        var warnings = view?.Warnings ?? [];
        int unacknowledged=warnings.Count(w=>!w.Acknowledged);warningsTab.Header=unacknowledged==0?"Warnings":$"Warnings · {unacknowledged} need review";
        if (warnings.Count == 0) warningRows.Children.Add(Ui.Text("No warnings recorded for this device and setup. This does not certify hardware health.", 12, "Muted"));
        foreach (var warning in warnings.OrderByDescending(w => w.Created).Take(20))
        {
            var row = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
            row.Children.Add(Ui.Text(warning.Created.ToLocalTime().ToString("g") + (warning.Acknowledged ? " · Acknowledged" : " · Needs review"), 11, "Muted"));
            row.Children.Add(Ui.Text(warning.Message, 12, "Warning"));
            if (!warning.Acknowledged)
            {
                var acknowledge = Ui.Button("Acknowledge", () => _ = RunOperationAsync(async () => { await session.Cooling.AcknowledgeAsync(warning.Id); return "Warning acknowledged."; }));
                acknowledge.HorizontalAlignment = HorizontalAlignment.Left; acknowledge.IsEnabled = !operationBusy; row.Children.Add(acknowledge);
            }
            warningRows.Children.Add(row);
        }
        if (warnings.Count > 20) warningRows.Children.Add(Ui.Text("Showing the latest 20 warnings.", 11, "Muted"));
    }

    private void SetReferencePeriod(int hours)
    {
        var end = DateTimeOffset.Now;
        referenceFrom.Text = end.AddHours(-hours).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        referenceTo.Text = end.ToString("yyyy-MM-dd HH:mm");
    }
    private static DateTimeOffset ParseLocal(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new ArgumentException("Enter a local date and time as yyyy-MM-dd HH:mm, for example 2026-10-25 18:30.");
        if (TimeZoneInfo.Local.IsInvalidTime(value) || TimeZoneInfo.Local.IsAmbiguousTime(value))
            throw new ArgumentException("That local time is skipped or repeated by daylight saving. Choose a time outside the clock change.");
        return new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value));
    }
    private Task SaveReferenceAsync() => RunOperationAsync(async () =>
    {
        var selected = SelectedDevice ?? throw new InvalidOperationException("Choose a recorded device first.");
        var start = ParseLocal(referenceFrom.Text); var end = ParseLocal(referenceTo.Text);
        if (end <= start || end - start > TimeSpan.FromDays(7)) throw new ArgumentException("Choose a reference period longer than zero and no longer than seven days.");
        if (end > DateTimeOffset.Now.AddMinutes(1)) throw new ArgumentException("The reference period cannot end in the future.");
        if (string.IsNullOrWhiteSpace(referenceLabel.Text)) throw new ArgumentException("Give this reference a name.");
        return await session.Cooling.CreateReferenceAsync(selected, start, end, referenceLabel.Text.Trim());
    });
    private Task UseReferenceAsync() => RunOperationAsync(async () =>
    {
        var chosen = (references.SelectedItem as Choice<ThermalReference>)?.Value ?? throw new InvalidOperationException("Choose a saved reference first.");
        await session.Cooling.ActivateReferenceAsync(chosen.Id); return "Reference activated. Its saved readings remain unchanged.";
    });
    private Task NewSetupAsync() => RunOperationAsync(() =>
    {
        session.NewCoolingSetup(); syncing = true; device.SelectedItem = null; syncing = false;
        return Task.FromResult("New cooling setup started. Earlier history is preserved; a new reference is needed for future comparisons.");
    });
    private async Task ExportAsync()
    {
        if (SelectedDevice is not { } selected || operationBusy) return;
        var dialog = new SaveFileDialog { Title = "Export thermal history to a new CSV file", Filter = "CSV files (*.csv)|*.csv", FileName = $"DeadWare-{DateTime.Now:yyyy-MM-dd-HHmmss}-thermal.csv", AddExtension = true, DefaultExt = ".csv", OverwritePrompt = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        // The store deliberately protects existing files; choose a new name to avoid deleting a file outside the history store.
        await RunOperationAsync(async () =>
        {
            if (System.IO.File.Exists(dialog.FileName)) throw new InvalidOperationException("That file already exists. Export again with a new file name; existing files are kept unchanged.");
            int count = await session.Cooling.ExportAsync(selected, displayedFrom, displayedTo, dialog.FileName);
            return $"Exported {count:N0} history windows to {dialog.FileName}.";
        });
    }
    private async Task RunOperationAsync(Func<Task<string>> operation)
    {
        if (operationBusy) return;
        operationBusy = true; actionStatus.Visibility = Visibility.Visible; actionStatus.Text = "Working…"; UpdateButtons(); RenderWarnings();
        try { actionStatus.Text = await operation(); requestRevision++; await RefreshAsync(); }
        catch (Exception error) { actionStatus.Visibility = Visibility.Visible; actionStatus.Text = error.Message; }
        finally { operationBusy = false; UpdateButtons(); RenderWarnings(); actionStatus.BringIntoView(); }
    }
    private void ApplySettings()
    {
        if (syncing) return;
        try
        {
            session.SetCoolingOptions(recording.IsChecked == true, notifications.IsChecked == true,
                (detailDays.SelectedItem as Choice<int>)?.Value ?? 30, (summaryDays.SelectedItem as Choice<int>)?.Value ?? 90);
            RequestRefresh();
        }
        catch (Exception error) { actionStatus.Visibility = Visibility.Visible; actionStatus.Text = error.Message; actionStatus.BringIntoView(); }
    }
    private void UpdateButtons()
    {
        bool selected = SelectedDevice is not null;
        saveReference.IsEnabled = selected && !operationBusy;
        useReference.IsEnabled = references.SelectedItem is not null && !operationBusy;
        export.IsEnabled = selected && !operationBusy;
        newSetup.IsEnabled = !operationBusy;
        device.IsEnabled = !operationBusy; references.IsEnabled = !operationBusy;
        referenceFrom.IsEnabled = !operationBusy; referenceTo.IsEnabled = !operationBusy; referenceLabel.IsEnabled = !operationBusy;
    }
}
