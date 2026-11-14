using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>Actual-time thermal history drawn with Win2D from <see cref="ThermalChartModel"/>, with a hover readout.</summary>
internal sealed class ThermalChartView : UserControl
{
    private const double Left = 12, Right = 8, Top = 15, Bottom = 32;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat RightLabelFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };

    private readonly CanvasControl canvas = new();
    private IReadOnlyList<ThermalWindow> windows = [];
    private string metric = "temperature";
    private DateTimeOffset from = DateTimeOffset.UtcNow.AddDays(-1), to = DateTimeOffset.UtcNow;
    private ThermalChartFrame? frame;
    private Point? pointer;

    /// <summary>Draws the line as the live graphs' smoothed curve; the page sets it from the Graph smoothing setting.</summary>
    public bool Smoothed { get; set; }

    public ThermalChartView()
    {
        canvas.Draw += Draw;
        var layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(canvas);
        layers.PointerMoved += (_, e) => { pointer = e.GetCurrentPoint(layers).Position; canvas.Invalidate(); };
        layers.PointerExited += (_, _) => { pointer = null; canvas.Invalidate(); };
        Content = layers;
        MinHeight = 180;
        AutomationProperties.SetName(this, "Thermal history graph");
        SizeChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => canvas.Invalidate();
    }

    public void SetData(IReadOnlyList<ThermalWindow> data, string selectedMetric, DateTimeOffset start, DateTimeOffset end)
    {
        windows = data; metric = selectedMetric; from = start; to = end;
        Rebuild();
    }

    private void Rebuild()
    {
        double width = ActualWidth - Left - Right, height = ActualHeight - Top - Bottom;
        frame = width < 30 || height < 30 || to <= from ? null : ThermalChartModel.Build(windows, metric, from, to, new ChartPlot(Left, Top, width, height));
        canvas.Invalidate();
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current) return;
        var session = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(170, c, c, c), line = Color.FromArgb(32, c, c, c);
        Color accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var plot = current.Plot;
        float left = (float)plot.Left, right = (float)(plot.Left + plot.Width), top = (float)plot.Top, bottom = (float)(plot.Top + plot.Height);
        session.FillRectangle(left, top, right - left, bottom - top, Color.FromArgb(10, c, c, c));
        foreach (var value in current.Lines)
        {
            session.DrawLine(left, (float)value.Y, right, (float)value.Y, line, 1);
            session.DrawText(value.Label, left + 4, (float)value.Y - (value.Y <= plot.Top + 1 ? -1 : 15), ink, LabelFormat);
        }
        session.DrawText(current.FromLabel, left, bottom + 12, ink, LabelFormat);
        session.DrawText(current.ToLabel, right, bottom + 12, ink, RightLabelFormat);
        if (!current.HasReadings)
        {
            session.DrawText("No available readings in this range", left + 12, (top + bottom) / 2, ink, LabelFormat);
            return;
        }

        Color spread = Color.FromArgb(64, accent.R, accent.G, accent.B);
        foreach (var point in current.Points)
            if (point.Spread is { } range) session.DrawLine((float)point.X, (float)range.Top, (float)point.X, (float)range.Bottom, spread, 1);
        using (var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round })
        {
            foreach (var run in current.Runs)
            {
                if (run.Count < 2) continue;
                using var builder = new CanvasPathBuilder(sender);
                builder.BeginFigure((float)current.Points[run[0]].X, (float)current.Points[run[0]].Y);
                HistoryChartView.AddRun(builder, [.. run.Select(index => new ChartPoint(current.Points[index].X, current.Points[index].Y))], Smoothed);
                builder.EndFigure(CanvasFigureLoop.Open);
                using var geometry = CanvasGeometry.CreatePath(builder);
                session.DrawGeometry(geometry, accent, 1.8f, style);
            }
        }
        float radius = (float)current.MarkerRadius;
        foreach (var point in current.Points) session.FillCircle((float)point.X, (float)point.Y, radius, accent);

        if (pointer is not { } position || ThermalChartModel.Hover(current, position.X, position.Y) is not { } hover) return;
        session.DrawLine((float)hover.X, top, (float)hover.X, bottom, ink, 1);
        session.FillCircle((float)hover.X, (float)hover.Y, 3, accent);
        (string Text, Color Color)[] rows = [(hover.Time, ink), (hover.Text, accent)];
        float width = 0;
        foreach (var row in rows)
        {
            using var layout = new CanvasTextLayout(sender, row.Text, LabelFormat, 0, 0);
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        width += 12;
        float height = rows.Length * 16 + 12;
        float x = (float)hover.X + 10;
        if (x + width > right) x = (float)hover.X - 10 - width;
        float chipTop = (float)Math.Clamp(hover.Y - height / 2, top, Math.Max(top, bottom - height));
        session.FillRoundedRectangle(x, chipTop, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        session.DrawRoundedRectangle(x, chipTop, width, height, 4, 4, line, 1);
        for (int i = 0; i < rows.Length; i++) session.DrawText(rows[i].Text, x + 6, chipTop + 6 + i * 16, rows[i].Color, LabelFormat);
    }
}

/// <summary>
/// Cooling Health: recorded thermal history per device and cooling setup, the assessment against a saved reference,
/// references, warnings, and the recording options. Persistent history is read only while the page is shown.
/// </summary>
internal sealed class CoolingPage : Page
{
    private static readonly (string Label, int Days)[] Ranges = [("Day", 1), ("Week", 7), ("Month", 30)];
    private static readonly int[] DetailChoices = [7, 30], SummaryChoices = [30, 90];

    private readonly MonitorSession session;
    private readonly ComboBox device = new() { MinWidth = 260, MaxWidth = 570 }, range = new() { MinWidth = 100 }, metric = new() { MinWidth = 150 };
    private readonly ComboBox references = new() { MinWidth = 230, MaxWidth = 550 }, detailDays = new() { MinWidth = 110 }, summaryDays = new() { MinWidth = 110 };
    private readonly TextBox referenceFrom = new() { Width = 170 }, referenceTo = new() { Width = 170 }, referenceLabel = new() { Width = 230, MaxLength = 80 };
    private readonly CheckBox recording = new() { Content = "Record thermal history on this PC" }, notifications = new() { Content = "Notify me about sustained cooling changes" };
    private readonly TextBlock recordingStatus = Muted("History starts when monitoring begins."), storage = Muted("");
    private readonly TextBlock assessmentHeading = new() { Text = "Learning", FontSize = 21, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock assessmentText = new() { Text = "Save a reference period before comparing cooling performance.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock comparison = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap }, coverage = Muted(""), chartSummary = Muted(""), referenceStatus = Muted("No reference saved yet.");
    private readonly TextBlock actionStatus = Caution(), recordingWarning = Caution();
    private readonly StackPanel warningRows = new() { Spacing = 6 }, assessmentPanel = new() { Spacing = 8 };
    private readonly ThermalChartView chart = new() { Height = 300 };
    private readonly Grid historyLayout = new() { ColumnSpacing = 12 };
    private readonly Border narrowAssessmentCard;
    private readonly SelectorBarItem referenceTab = new() { Text = "Reference" }, warningsTab = new() { Text = "Warnings" }, setupTab = new() { Text = "Recording & setup" };
    private readonly Button saveReference = new() { Content = "Save this period as a reference" }, useReference = new() { Content = "Use reference" };
    private readonly Button export = new() { Content = "Export CSV…" }, newSetup = new() { Content = "Start a new cooling setup" };
    private readonly QueueTimer refreshTimer;
    private (string Label, ThermalDevice Device)[] devices = [];
    private ThermalReference[] referenceChoices = [];
    private CoolingView? view;
    private bool loaded, syncing, refreshing, refreshAgain, operationBusy;
    private bool? assessmentBeside;
    private int requestRevision;
    private DateTimeOffset displayedFrom, displayedTo;

    public CoolingPage(MonitorSession session)
    {
        this.session = session;
        refreshTimer = new QueueTimer(DispatcherQueue) { Interval = TimeSpan.FromSeconds(15) };
        ToolTipService.SetToolTip(device, "Choose a CPU/GPU and its recorded cooling setup. This does not change the live GPU selection.");
        AutomationProperties.SetName(device, "Cooling history device and setup");
        AutomationProperties.SetName(range, "Thermal history time range");
        AutomationProperties.SetName(metric, "Thermal history metric");
        foreach (var (label, _) in Ranges) range.Items.Add(label);
        range.SelectedIndex = 0;
        foreach (var (label, _) in ThermalChartModel.Metrics) metric.Items.Add(label);
        metric.SelectedIndex = 0;

        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => _ = RefreshAsync();
        export.Click += (_, _) => _ = ExportAsync();
        var deviceCaption = Muted("Device & cooling setup"); deviceCaption.VerticalAlignment = VerticalAlignment.Center;
        var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { deviceCaption, device, refresh, export } };
        recordingWarning.Visibility = Visibility.Collapsed; actionStatus.Visibility = Visibility.Collapsed;
        var controlsCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { Scroll(controlRow), recordingWarning, actionStatus } });

        var historyHeading = new Grid();
        historyHeading.Children.Add(Heading("Thermal history"));
        historyHeading.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { range, metric } });
        var chartColumn = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                historyHeading, chart, chartSummary,
                Muted("Dots show recorded averages; faint vertical lines show min–max. Gaps stay unconnected. Dates and times use this PC's local time."),
            },
        };
        historyLayout.ColumnDefinitions.Add(new ColumnDefinition());
        historyLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        historyLayout.Children.Add(chartColumn);
        var historyCard = Pages.Card(new Border { Padding = new Thickness(8), Child = historyLayout });

        assessmentPanel.Children.Add(Muted("COOLING ASSESSMENT"));
        assessmentPanel.Children.Add(assessmentHeading); assessmentPanel.Children.Add(assessmentText);
        assessmentPanel.Children.Add(comparison); assessmentPanel.Children.Add(coverage);
        assessmentPanel.Children.Add(Muted("This compares cooling behavior, not hardware life expectancy. Room temperature, workload, mounting, dust and fan settings can explain a change. Pump, coolant and flow diagnosis requires confirmed sensors."));
        narrowAssessmentCard = Pages.Card(new Border { Padding = new Thickness(8) });

        // Reference tab.
        AutomationProperties.SetName(references, "Saved cooling reference");
        useReference.Click += (_, _) => _ = UseReferenceAsync();
        var shortcuts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (int hours in new[] { 1, 24, 168 })
        {
            int h = hours;
            var shortcut = new Button { Content = hours == 1 ? "Last hour" : hours == 24 ? "Last day" : "Last week" };
            shortcut.Click += (_, _) => SetReferencePeriod(h);
            shortcuts.Children.Add(shortcut);
        }
        AutomationProperties.SetName(referenceFrom, "Reference start date and time");
        AutomationProperties.SetName(referenceTo, "Reference end date and time");
        AutomationProperties.SetName(referenceLabel, "Reference name");
        ToolTipService.SetToolTip(referenceFrom, "Local time, yyyy-MM-dd HH:mm");
        ToolTipService.SetToolTip(referenceTo, "Local time, yyyy-MM-dd HH:mm");
        saveReference.Click += (_, _) => _ = SaveReferenceAsync();
        var period = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("From (local)", referenceFrom), Field("To (local)", referenceTo), Field("Reference name", referenceLabel) } };
        var referenceEditor = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Muted("Choose up to seven days when this setup was believed to be working normally. Saving needs at least 30 eligible minutes with temperature, load and power. Warm-up, missing readings and changing loads are excluded. New readings never overwrite a reference."),
                shortcuts, Scroll(period),
                Muted("Date format: yyyy-MM-dd HH:mm. Detailed history must still be retained for the selected period."),
                saveReference,
            },
        };
        var reference = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Heading("Your fixed reference"), referenceStatus,
                Scroll(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { references, useReference } }),
                new Expander { Header = "Save a new reference", Content = referenceEditor, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch },
            },
        };

        // Recording & setup tab.
        foreach (int days in DetailChoices) detailDays.Items.Add($"{days} days");
        foreach (int days in SummaryChoices) summaryDays.Items.Add($"{days} days");
        AutomationProperties.SetName(detailDays, "Detailed history retention");
        AutomationProperties.SetName(summaryDays, "Summary history retention");
        newSetup.Click += (_, _) => _ = NewSetupAsync();
        var settings = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                recording, recordingStatus, notifications,
                Muted("Notifications require ten consecutive comparable minutes. A warning needs at least a 5°C rise above the matched mean and 3°C above its 95th percentile. Acknowledgment and a 24-hour cooldown prevent repeated alerts. These are conservative comparison rules, not manufacturer limits."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Minute details", detailDays), Field("Hourly summaries", summaryDays) } },
                storage, newSetup,
                Muted("Use after cleaning, changing the cooler, fan curve or power limits. Earlier history and references stay available under the previous setup; new comparisons require a fresh reference."),
            },
        };

        var tabs = new SelectorBar();
        tabs.Items.Add(referenceTab); tabs.Items.Add(warningsTab); tabs.Items.Add(setupTab);
        tabs.SelectedItem = referenceTab;
        warningRows.Visibility = Visibility.Collapsed; settings.Visibility = Visibility.Collapsed;
        tabs.SelectionChanged += (_, _) =>
        {
            reference.Visibility = tabs.SelectedItem == referenceTab ? Visibility.Visible : Visibility.Collapsed;
            warningRows.Visibility = tabs.SelectedItem == warningsTab ? Visibility.Visible : Visibility.Collapsed;
            settings.Visibility = tabs.SelectedItem == setupTab ? Visibility.Visible : Visibility.Collapsed;
        };
        var lowerCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { tabs, reference, warningRows, settings } });

        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { controlsCard, narrowAssessmentCard, historyCard, lowerCard } } };
        PlaceAssessment(false);
        SizeChanged += (_, e) => PlaceAssessment(e.NewSize.Width >= 1100);

        syncing = true;
        recording.IsChecked = session.Settings.CoolingRecording; notifications.IsChecked = session.Settings.CoolingNotifications;
        detailDays.SelectedIndex = session.Settings.CoolingDetailedDays == 7 ? 0 : 1;
        summaryDays.SelectedIndex = session.Settings.CoolingSummaryDays == 30 ? 0 : 1;
        syncing = false;
        SetReferencePeriod(24); referenceLabel.Text = "My reference";
        device.SelectionChanged += (_, _) => { if (!syncing) RequestRefresh(); };
        range.SelectionChanged += (_, _) => { if (!syncing) RequestRefresh(); };
        metric.SelectionChanged += (_, _) => RenderChart();
        references.SelectionChanged += (_, _) => { if (!syncing) UpdateButtons(); };
        recording.Checked += (_, _) => ApplySettings(); recording.Unchecked += (_, _) => ApplySettings();
        notifications.Checked += (_, _) => ApplySettings(); notifications.Unchecked += (_, _) => ApplySettings();
        detailDays.SelectionChanged += (_, _) => ApplySettings(); summaryDays.SelectionChanged += (_, _) => ApplySettings();
        refreshTimer.Tick += () => _ = RefreshAsync();
        Loaded += (_, _) => { loaded = true; refreshTimer.Start(); _ = RefreshAsync(); };
        Unloaded += (_, _) => { loaded = false; refreshTimer.Stop(); requestRevision++; };
        UpdateButtons();
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    private static TextBlock Caution() => new() { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] };
    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Field(string label, FrameworkElement input) => new() { Spacing = 4, Children = { Muted(label), input } };
    /// <summary>A row of controls that scrolls sideways in a narrow window instead of being cut off.</summary>
    private static ScrollViewer Scroll(UIElement row) => new()
    {
        Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled,
    };

    /// <summary>The assessment sits beside the chart in a wide window and in its own card above it in a narrow one.</summary>
    private void PlaceAssessment(bool beside)
    {
        if (assessmentBeside == beside) return;
        historyLayout.Children.Remove(assessmentPanel);
        ((Border)narrowAssessmentCard.Child).Child = null;
        historyLayout.ColumnDefinitions[1].Width = new GridLength(beside ? 280 : 0);
        if (beside) { Grid.SetColumn(assessmentPanel, 1); historyLayout.Children.Add(assessmentPanel); }
        else ((Border)narrowAssessmentCard.Child).Child = assessmentPanel;
        narrowAssessmentCard.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;
        assessmentBeside = beside;
    }

    /// <summary>The Recording &amp; setup tab shows every status; a failure also shows in the controls card, which every tab leaves visible.</summary>
    private void SetRecordingStatus(string text)
    {
        recordingStatus.Text = text;
        bool failed = text.StartsWith("History recording unavailable", StringComparison.Ordinal) || text.StartsWith("History could not be read", StringComparison.Ordinal);
        recordingWarning.Text = failed ? text : ""; recordingWarning.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private int Days => range.SelectedIndex >= 0 ? Ranges[range.SelectedIndex].Days : 1;
    private string SelectedMetric => metric.SelectedIndex >= 0 ? ThermalChartModel.Metrics[metric.SelectedIndex].Id : "temperature";
    private ThermalDevice? SelectedDevice => device.SelectedIndex >= 0 && device.SelectedIndex < devices.Length ? devices[device.SelectedIndex].Device : null;
    private ThermalReference? SelectedReference => references.SelectedIndex >= 0 && references.SelectedIndex < referenceChoices.Length ? referenceChoices[references.SelectedIndex] : null;
    private void RequestRefresh() { requestRevision++; _ = RefreshAsync(); }

    private async Task RefreshAsync()
    {
        if (!loaded) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        int revision = requestRevision;
        var selected = SelectedDevice;
        try
        {
            var result = await session.Cooling.ReadViewAsync(selected?.Id, selected?.ConfigurationId, Days);
            if (revision != requestRevision || !loaded) { refreshAgain = true; return; }
            ApplyView(result);
        }
        catch (Exception error) { SetRecordingStatus("History could not be read: " + error.Message); }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (loaded) _ = RefreshAsync(); }
        }
    }

    private void ApplyView(CoolingView result)
    {
        view = result;
        syncing = true;
        try
        {
            devices = [.. ThermalChartModel.DeviceChoices(result.Devices, session.Settings.CoolingConfigurationId)];
            device.Items.Clear();
            foreach (var (label, _) in devices) device.Items.Add(label);
            device.SelectedIndex = Array.FindIndex(devices, d => d.Device.Id == result.SelectedDevice?.Id && d.Device.ConfigurationId == result.SelectedDevice?.ConfigurationId);
            string? priorReference = SelectedReference?.Id;
            referenceChoices = [.. result.References];
            references.Items.Clear();
            foreach (var r in referenceChoices) references.Items.Add($"{r.Label} · {r.Created.ToLocalTime():MMM d HH:mm}");
            int chosen = Array.FindIndex(referenceChoices, r => r.Id == priorReference);
            if (chosen < 0) chosen = Array.FindIndex(referenceChoices, r => r.Id == result.ActiveReference?.Id);
            references.SelectedIndex = chosen < 0 && referenceChoices.Length > 0 ? 0 : chosen;
        }
        finally { syncing = false; }
        SetRecordingStatus(result.Status);
        storage.Text = $"Local storage: {HistoryChartModel.Bytes(result.StorageBytes)} · Database limit 256 MiB plus journal. Reducing retention removes older history; saved references are retained.";
        var assessment = result.Assessment;
        assessmentHeading.Text = assessment.State;
        assessmentHeading.Foreground = (Brush)Application.Current.Resources[assessment.ShouldWarn ? "SystemFillColorCautionBrush" : "AccentTextFillColorPrimaryBrush"];
        assessmentText.Text = assessment.Message;
        comparison.Text = assessment.CurrentTemperature.HasValue
            ? $"Current {ThermalChartModel.Number(assessment.CurrentTemperature, "°C")}    Reference {ThermalChartModel.Number(assessment.ReferenceTemperature, "°C")}    Difference {ThermalChartModel.Signed(assessment.Delta)}"
            : "Waiting for comparable temperature, load and power readings.";
        coverage.Text = $"Matched recent minutes: {assessment.MatchedWindows} / {assessment.RequiredWindows}" +
            (assessment.ReferenceMinimum.HasValue ? $" · Matched reference range {ThermalChartModel.Number(assessment.ReferenceMinimum, "°C")}–{ThermalChartModel.Number(assessment.ReferenceMaximum, "°C")}" : "") +
            "\nReference comparisons apply only to matched operating conditions, not every point in the history graph.";
        referenceStatus.Text = result.ActiveReference is { } active
            ? $"Active: {active.Label} · {active.Windows.Count:N0} eligible minutes\n{active.Start.ToLocalTime():g} to {active.End.ToLocalTime():g} · saved {active.Created.ToLocalTime():g}"
            : "No reference for this device and setup. Allow history to accumulate, then save a period you trust.";
        RenderChart(); RenderWarnings(); UpdateButtons();
    }

    private void RenderChart()
    {
        if (view is null) return;
        displayedTo = DateTimeOffset.UtcNow; displayedFrom = displayedTo.AddDays(-Days);
        chart.Smoothed = session.Settings.GraphSmoothing;
        chart.SetData(view.History, SelectedMetric, displayedFrom, displayedTo);
        chartSummary.Text = ThermalChartModel.Summary(view.History, SelectedMetric);
    }

    private void RenderWarnings()
    {
        warningRows.Children.Clear();
        var warnings = view?.Warnings ?? [];
        int unacknowledged = warnings.Count(w => !w.Acknowledged);
        warningsTab.Text = unacknowledged == 0 ? "Warnings" : $"Warnings · {unacknowledged} need review";
        if (warnings.Count == 0) warningRows.Children.Add(Muted("No warnings recorded for this device and setup. This does not certify hardware health."));
        foreach (var warning in warnings.OrderByDescending(w => w.Created).Take(20))
        {
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(Muted(warning.Created.ToLocalTime().ToString("g") + (warning.Acknowledged ? " · Acknowledged" : " · Needs review")));
            var message = Caution(); message.Text = warning.Message; row.Children.Add(message);
            if (!warning.Acknowledged)
            {
                var acknowledge = new Button { Content = "Acknowledge", IsEnabled = !operationBusy };
                acknowledge.Click += (_, _) => _ = RunOperationAsync(async () => { await session.Cooling.AcknowledgeAsync(warning.Id); return "Warning acknowledged."; });
                row.Children.Add(acknowledge);
            }
            warningRows.Children.Add(row);
        }
        if (warnings.Count > 20) warningRows.Children.Add(Muted("Showing the latest 20 warnings."));
    }

    private void SetReferencePeriod(int hours)
    {
        var end = DateTimeOffset.Now;
        referenceFrom.Text = end.AddHours(-hours).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        referenceTo.Text = end.ToString("yyyy-MM-dd HH:mm");
    }

    private Task SaveReferenceAsync() => RunOperationAsync(async () =>
    {
        var selected = SelectedDevice ?? throw new InvalidOperationException("Choose a recorded device first.");
        var start = ThermalChartModel.ParseLocal(referenceFrom.Text); var end = ThermalChartModel.ParseLocal(referenceTo.Text);
        if (end <= start || end - start > TimeSpan.FromDays(7)) throw new ArgumentException("Choose a reference period longer than zero and no longer than seven days.");
        if (end > DateTimeOffset.Now.AddMinutes(1)) throw new ArgumentException("The reference period cannot end in the future.");
        if (string.IsNullOrWhiteSpace(referenceLabel.Text)) throw new ArgumentException("Give this reference a name.");
        return await session.Cooling.CreateReferenceAsync(selected, start, end, referenceLabel.Text.Trim());
    });

    private Task UseReferenceAsync() => RunOperationAsync(async () =>
    {
        var chosen = SelectedReference ?? throw new InvalidOperationException("Choose a saved reference first.");
        await session.Cooling.ActivateReferenceAsync(chosen.Id);
        return "Reference activated. Its saved readings remain unchanged.";
    });

    private Task NewSetupAsync() => RunOperationAsync(() =>
    {
        session.NewCoolingSetup();
        syncing = true; device.SelectedIndex = -1; syncing = false;
        return Task.FromResult("New cooling setup started. Earlier history is preserved; a new reference is needed for future comparisons.");
    });

    private async Task ExportAsync()
    {
        if (SelectedDevice is not { } selected || operationBusy) return;
        Windows.Storage.StorageFile? file;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"DeadWare-{DateTime.Now:yyyy-MM-dd-HHmmss}-thermal" };
            picker.FileTypeChoices.Add("CSV files", [".csv"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            file = await picker.PickSaveFileAsync();
        }
        catch (Exception error) { ShowAction(error.Message); return; }
        if (file is null) return;
        await RunOperationAsync(async () =>
        {
            // The picker leaves an empty file at a new name, and the store only writes to a name that does not exist yet.
            // An existing file with content is kept unchanged, as in the WPF app.
            if (new FileInfo(file.Path) is { Exists: true, Length: 0 }) File.Delete(file.Path);
            if (File.Exists(file.Path)) throw new InvalidOperationException("That file already exists. Export again with a new file name; existing files are kept unchanged.");
            int count = await session.Cooling.ExportAsync(selected, displayedFrom, displayedTo, file.Path);
            return $"Exported {count:N0} history windows to {file.Path}.";
        });
    }

    private void ShowAction(string text)
    {
        actionStatus.Visibility = Visibility.Visible; actionStatus.Text = text;
        actionStatus.StartBringIntoView();
    }

    private async Task RunOperationAsync(Func<Task<string>> operation)
    {
        if (operationBusy) return;
        operationBusy = true; actionStatus.Visibility = Visibility.Visible; actionStatus.Text = "Working…"; UpdateButtons(); RenderWarnings();
        try { actionStatus.Text = await operation(); requestRevision++; await RefreshAsync(); }
        catch (Exception error) { actionStatus.Text = error.Message; }
        finally { operationBusy = false; UpdateButtons(); RenderWarnings(); actionStatus.StartBringIntoView(); }
    }

    private void ApplySettings()
    {
        if (syncing) return;
        try
        {
            session.SetCoolingOptions(recording.IsChecked == true, notifications.IsChecked == true,
                detailDays.SelectedIndex >= 0 ? DetailChoices[detailDays.SelectedIndex] : 30, summaryDays.SelectedIndex >= 0 ? SummaryChoices[summaryDays.SelectedIndex] : 90);
            RequestRefresh();
        }
        catch (Exception error) { ShowAction(error.Message); }
    }

    private void UpdateButtons()
    {
        bool selected = SelectedDevice is not null;
        saveReference.IsEnabled = selected && !operationBusy;
        useReference.IsEnabled = SelectedReference is not null && !operationBusy;
        export.IsEnabled = selected && !operationBusy;
        newSetup.IsEnabled = !operationBusy;
        device.IsEnabled = !operationBusy; references.IsEnabled = !operationBusy;
        referenceFrom.IsEnabled = !operationBusy; referenceTo.IsEnabled = !operationBusy; referenceLabel.IsEnabled = !operationBusy;
    }
}
