using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// Usage history: the Device charts for a day, week or month with their statistics, the applications' traffic totals, the busiest
/// applications' resources with a chart for a selected one, CSV export and the recording switch. Clicking a CPU, RAM or GPU point lists
/// that moment's busiest apps. The Applications card's traffic strip joins the cursor, and activating one of its slots lists that slot in the table.
/// </summary>
internal sealed class UsagePage : Page
{
    private static readonly (string Label, int Days)[] Ranges = [("Day", 1), ("Week", 7), ("Month", 30)];
    private static readonly (string Kind, string Title, string Unit, double? Maximum)[] Charts =
    [
        ("cpu", "CPU usage", "%", 100), ("cputemp", "CPU temperature", "°C", null),
        ("gpu", "GPU usage", "%", 100), ("gputemp", "GPU temperature", "°C", null),
        ("memory", "RAM usage", "%", 100), ("network", "Network download / upload", "B/s", null),
    ];
    // Click a moment: the CPU, RAM and GPU charts list a slot's busiest apps under Application resources, ranked by their own metric.
    // Every metric is read with the CPU chart's point count, so the three share one slot grid.
    private static readonly (string Kind, string Metric, ProcessRank Rank)[] Moments =
        [("cpu", UsageMetrics.Cpu, ProcessRank.CpuTime), ("memory", UsageMetrics.Memory, ProcessRank.AverageMemory), ("gpu", UsageMetrics.Gpu, ProcessRank.Gpu)];
    private const string OtherKey = "\0";
    private const string ApplicationsTitle = "Applications", LoadingSlot = "Loading this slot…", SlotUnavailable = "History unavailable for this slot.";

    private sealed record PageData(DateTimeOffset From, DateTimeOffset To, IReadOnlyDictionary<string, UsageHistoryView> Metrics,
        UsageApplicationView Applications, string Status, string? GpuName);

    private readonly MonitorSession session;
    private readonly ComboBox range = new() { MinWidth = 110 };
    private readonly TextBlock period = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock applicationSummary = Muted(""), gapNotice = Muted(""), recordingStatus = Muted(""), storage = Muted("");
    private readonly TextBlock actionStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, UsageChartView> charts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> statistics = new(StringComparer.Ordinal), titles = new(StringComparer.Ordinal);
    private readonly List<FrameworkElement> cells = [];
    private readonly Grid grid = new() { ColumnSpacing = 12, RowSpacing = 14 };
    private readonly PickTable applications;
    // The traffic strip in the Applications card; it joins the CPU, RAM and GPU cursor and lists a slot's applications in the table.
    private readonly TrafficStripView strip = new(TrafficStripKind.UsageHistory) { Selectable = true };
    private readonly TextBlock applicationsHeading = new() { Text = ApplicationsTitle, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button applicationsClear = new() { Content = "Clear", Visibility = Visibility.Collapsed };
    private readonly Button openInWindow = new() { Content = "Open in window ↗" };
    // The strip slot the Applications table lists, or null for the period; its last read (null while loading or unreadable) and the
    // text shown meanwhile.
    private (DateTimeOffset From, DateTimeOffset To)? applicationSlot;
    private UsageApplicationView? slotApplications;
    private string? slotPending;
    private int stripRequest, slotRequest;
    private readonly Button showInConnections = new() { Content = "Show in Connections", IsEnabled = false };
    private readonly Button exportMetrics = new() { Content = "Export device CSV…" }, exportApplications = new() { Content = "Export applications CSV…" };
    private readonly Button earlier = new() { Content = "◀ Earlier" }, later = new() { Content = "Later ▶" };
    private readonly TextBox search = new() { Width = 220, PlaceholderText = "Find application" };
    private readonly CheckBox recording = new() { Content = "Record usage history on this device" };
    private readonly ApplicationResourcesView resources;
    private readonly Border resourcesCard;
    private readonly SelectorBar tabs = new();
    private readonly SelectorBarItem applicationsTab = new() { Text = "Applications" }, resourcesTab = new() { Text = "Application resources" };
    private readonly StackPanel applicationsPanel = new() { Spacing = 8 };
    private readonly QueueTimer timer;
    private PageData? data;
    // The selected row's key in the Applications table, kept across rebuilds; null when no row is selected or its row is no longer shown.
    private string? selectedApplication;
    // The moment the CPU, RAM and GPU hairlines mark: the listed slot's middle, or the bucket Left or Right moved to since; null for none.
    private DateTimeOffset? marked;
    private int revision, offset, columns;
    private bool loaded, refreshing, refreshAgain, syncing;

    public UsagePage(MonitorSession session)
    {
        this.session = session;
        timer = new QueueTimer(DispatcherQueue) { Interval = TimeSpan.FromSeconds(60) };

        foreach (var (label, _) in Ranges) range.Items.Add(label);
        range.SelectedIndex = 0;
        AutomationProperties.SetName(range, "Usage history time range");
        AutomationProperties.SetName(earlier, "Earlier period"); AutomationProperties.SetName(later, "Later period");
        earlier.Click += (_, _) => Step(1); later.Click += (_, _) => Step(-1);
        var refresh = new Button { Content = "Refresh" };
        refresh.Click += (_, _) => RequestRefresh();
        exportMetrics.Click += async (_, _) => await ExportAsync(false);
        exportApplications.Click += async (_, _) => await ExportAsync(true);
        openInWindow.Click += (_, _) => App.Popouts?.Open("usagehistory");
        var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { range, earlier, later, period, refresh, exportMetrics, exportApplications, openInWindow } };
        var controls = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children =
            {
                new ScrollViewer { Content = controlRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled },
                actionStatus,
            },
        });

        foreach (var (kind, title, _, _) in Charts)
        {
            var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
            titles[kind] = heading;
            var chart = new UsageChartView(session) { Height = 170 };
            charts[kind] = chart;
            var stats = Muted("");
            statistics[kind] = stats;
            var cell = new StackPanel { Spacing = 6, Children = { heading, chart, stats } };
            cells.Add(cell); grid.Children.Add(cell);
        }
        ArrangeCharts(2);
        var deviceHint = Muted("Points average each slot's recorded minutes; the band spans their minimum and maximum. Click a CPU, RAM or GPU point, or press Enter on it, to list that moment's apps.");
        ToolTipService.SetToolTip(deviceHint, "Each point is the average of the recorded minutes in its slot; the shaded band spans their minimum and maximum. Minutes with no readings stay empty and partial slots are not connected. Statistics use the stored minute values for the whole period. Dates use local time. Click a CPU, RAM or GPU point, or press Enter on it, to list its busiest apps under Application resources.");
        var device = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children = { new TextBlock { Text = "Device", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, grid, deviceHint },
        });

        applications = new PickTable([new("Application", 3), new("Download", 1, true), new("Upload", 1, true), new("Total", 1, true), new("Share", 1, true)],
            "Applications") { MaxHeight = 420 };
        AutomationProperties.SetName(showInConnections, "Show in Connections");
        showInConnections.Click += (_, _) => ShowInConnections();
        AutomationProperties.SetName(search, "Find application in usage history");
        gapNotice.Opacity = 1;
        gapNotice.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        gapNotice.Visibility = Visibility.Collapsed;
        // While a strip slot is listed the heading names it and Clear returns to the period; Show in Connections follows.
        applicationsHeading.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        AutomationProperties.SetName(applicationsClear, "Clear the listed slot");
        applicationsClear.Click += (_, _) => ClearMoment();
        applicationsPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { applicationsHeading, applicationsClear } });
        applicationsPanel.Children.Add(strip);
        applicationsPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { search, showInConnections } });
        applicationsPanel.Children.Add(applicationSummary);
        applicationsPanel.Children.Add(gapNotice);
        applicationsPanel.Children.Add(applications);
        applicationsPanel.Children.Add(Muted("Totals come from observed per-application traffic (ETW) during the period, summed by executable name across its processes. Time without capture is a gap, not zero traffic. The strip stacks the applications with the most traffic, plus Other, as average rates over each slot's captured minutes; click a slot, or press Enter on it, to list it here. Show in Connections opens where the bytes went, for up to the last 30 days."));

        resources = new ApplicationResourcesView(session) { Visibility = Visibility.Collapsed };
        tabs.Items.Add(applicationsTab); tabs.Items.Add(resourcesTab);
        tabs.SelectedItem = applicationsTab;
        tabs.SelectionChanged += (_, _) => ShowTab();
        resourcesCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { tabs, applicationsPanel, resources } });

        AutomationProperties.SetName(recording, "Record usage history");
        var retention = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Muted("One row per minute for CPU, GPU, RAM and network, plus per-application bytes, kept for up to 400 days or 256 MiB, whichever comes first. The six-hour live graphs are separate and unchanged. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days (up to 192 MiB)."),
                Muted("History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit."),
                Muted(HistoryClearCopy.UsageHistoryLine),
                ClearHistoryEntry.Create(session),
            },
        };
        var settings = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children =
            {
                new TextBlock { Text = "Keep a longer record", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                recording, recordingStatus, storage,
                new Expander { Header = "Retention and privacy", Content = retention, HorizontalAlignment = HorizontalAlignment.Stretch },
            },
        });

        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { controls, device, resourcesCard, settings } } };

        foreach (var (kind, _, ranking) in Moments)
        {
            charts[kind].Selectable = true;
            charts[kind].BucketActivated += index => ShowMoment(kind, index, ranking);
            charts[kind].SelectionMoved += index => MoveMoment(kind, index);
        }
        // The strip joins the cursor but is not a Moments chart: moving on it marks the time, and activating a slot lists it in the table.
        strip.SelectionMoved += MoveOnStrip;
        strip.SlotActivated += (index, _) => ShowApplicationsSlot(index);
        applications.SelectionChanged += key => { selectedApplication = key; UpdateShowInConnections(); };
        // ClearSlot always raises SlotCleared, so a range change, Earlier, Later and ShowDay clear the cursor too.
        resources.SlotCleared += () =>
        {
            marked = null;
            foreach (var (kind, _, _) in Moments) charts[kind].SelectedIndex = null;
            strip.SelectedIndex = null; ClearApplicationsSlot();
        };
        // Esc returns Application resources to the period from anywhere on the page, and clears the cursor.
        KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape || (resources.Slot is null && marked is null && applicationSlot is null)) return;
            ClearMoment(); e.Handled = true;
        };
        SizeChanged += (_, e) => ArrangeCharts(e.NewSize.Width >= 1100 ? 3 : 2);

        recording.IsChecked = session.Settings.UsageRecording;
        recording.Checked += (_, _) => ChangeRecording(true);
        recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => { if (syncing) return; offset = 0; resources.ClearSlot(); RequestRefresh(); };
        search.TextChanged += (_, _) => RefreshApplications();
        timer.Tick += () => _ = RefreshAsync();
        this.WhileLoaded(() =>
        {
            loaded = true;
            session.Changed += RefreshCurrent; session.HistoryClearChanged += HistoryClearChanged;
            timer.Start(); RefreshCurrent(); UpdatePeriod(); _ = RefreshAsync();
        },
        () =>
        {
            loaded = false;
            session.Changed -= RefreshCurrent; session.HistoryClearChanged -= HistoryClearChanged;
            timer.Stop(); revision++; stripRequest++; slotRequest++;
        });
        RefreshCurrent(); UpdatePeriod();
    }

    /// <summary>True when this page is the content of its own pop-out window, which has no use for the button that opens one.</summary>
    public bool InWindow { set => openInWindow.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }

    private static DateTimeOffset Middle(TrafficStripSlot slot) => slot.Start + (slot.End - slot.Start) / 2;

    /// <summary>Puts the cursor at the strip slot Left or Right moved to and marks its time on the CPU, RAM and GPU charts; nothing is listed.</summary>
    private void MoveOnStrip(int index)
    {
        if (strip.Strip is not { } shown || index < 0 || index >= shown.Slots.Count) return;
        marked = Middle(shown.Slots[index]); MarkMoment();
    }

    /// <summary>Lists the activated strip slot in the Applications table and reads it at once. It never lists or clears the Application
    /// resources card's slot.</summary>
    private void ShowApplicationsSlot(int index)
    {
        if (strip.Strip is not { } shown || index < 0 || index >= shown.Slots.Count) return;
        var slot = shown.Slots[index];
        applicationSlot = (slot.Start, slot.End);
        marked = Middle(slot); MarkMoment();
        applicationsHeading.Text = $"{ApplicationsTitle} · " + UsagePresenter.SlotSpan(TrafficStrips.CeilingMinute(slot.Start),
            TrafficStrips.CeilingMinute(slot.End), shown.To - shown.From > TimeSpan.FromDays(1));
        applicationsClear.Visibility = Visibility.Visible;
        slotApplications = null; slotPending = LoadingSlot;
        RefreshApplications();
        _ = ReadSlotAsync(slot.Start, slot.End);
        tabs.SelectedItem = applicationsTab; ShowTab();
        applications.StartBringIntoView();
    }

    /// <summary>Esc and the Applications Clear: both cards return to the period, and the cursor and every hairline clear.</summary>
    private void ClearMoment() { ClearApplicationsSlot(); resources.ClearSlot(); }

    private void ClearApplicationsSlot()
    {
        if (applicationSlot is null) return;
        applicationSlot = null; slotRequest++; slotApplications = null; slotPending = null;
        applicationsHeading.Text = ApplicationsTitle; applicationsClear.Visibility = Visibility.Collapsed;
        RefreshApplications();
    }

    /// <summary>Shows a strip read for the period; null shows "History unavailable". The minutes not saved yet stay unhatched.</summary>
    private void ApplyStrip(TrafficStrip? read, DateTimeOffset now)
    {
        var saveDelay = UsageHistoryService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds);
        strip.Apply(read, read is null ? now : TrafficStrips.DueUntil(read.To, now, saveDelay), now);
        MarkStrip();
    }

    private async Task ReadStripAsync(DateTimeOffset from, DateTimeOffset to, int points)
    {
        int current = ++stripRequest;
        TrafficStrip? read;
        try { read = await session.Usage.TryReadApplicationSeriesAsync(from, to, points); }
        catch (Exception) { read = null; }
        if (current != stripRequest || !loaded) return;
        ApplyStrip(read, DateTimeOffset.UtcNow);
    }

    private async Task ReadSlotAsync(DateTimeOffset from, DateTimeOffset to)
    {
        int current = ++slotRequest;
        UsageApplicationView? view;
        try { view = await session.Usage.TryReadApplicationsAsync(from, to); }
        catch (Exception) { view = null; }
        if (current != slotRequest || !loaded || applicationSlot is null) return;
        slotApplications = view; slotPending = view is null ? SlotUnavailable : null;
        RefreshApplications();
    }

    private void MarkStrip()
        => strip.SelectedIndex = marked is { } time && strip.Strip is { } shown ? TrafficStrips.SlotAt(shown, time) : null;

    private int Days => range.SelectedIndex >= 0 ? Ranges[range.SelectedIndex].Days : 1;

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    private void ArrangeCharts(int count)
    {
        if (columns == count) return;
        columns = count;
        grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        for (int i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (cells.Count + count - 1) / count; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < cells.Count; i++) { Grid.SetRow(cells[i], i / count); Grid.SetColumn(cells[i], i % count); }
    }

    private void ShowTab()
    {
        bool showResources = tabs.SelectedItem == resourcesTab;
        applicationsPanel.Visibility = showResources ? Visibility.Collapsed : Visibility.Visible;
        resources.Visibility = showResources ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetActionStatus(string text) { actionStatus.Text = text; actionStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    private void ChangeRecording(bool enabled)
    {
        if (syncing) return;
        session.SetUsageRecording(enabled); recordingStatus.Text = session.Usage.Status; resources.RefreshStatus();
    }

    /// <summary>Follows settings changed elsewhere, such as Per-process monitoring on the Processes page or the selected GPU.</summary>
    private void RefreshCurrent()
    {
        syncing = true;
        try { recording.IsChecked = session.Settings.UsageRecording; }
        finally { syncing = false; }
        if (data is null) recordingStatus.Text = session.Usage.Status;
        resources.RefreshStatus();
        string gpu = session.GpuName;
        titles["gpu"].Text = gpu == "GPU unavailable" ? "GPU usage" : $"GPU usage · {gpu}";
        titles["gputemp"].Text = gpu == "GPU unavailable" ? "GPU temperature" : $"GPU temperature · {gpu}";
    }

    // Another period has another bucket grid, so a listed slot and its hairlines clear with it.
    private void Step(int direction) { offset = Math.Max(0, offset + direction); resources.ClearSlot(); UpdatePeriod(); RequestRefresh(); }

    /// <summary>Selects the Day range and steps back to the day that contains <paramref name="at"/>.</summary>
    public void ShowDay(DateTimeOffset at)
    {
        syncing = true;
        try { range.SelectedIndex = 0; }
        finally { syncing = false; }
        offset = Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - at).TotalDays));
        resources.ClearSlot(); UpdatePeriod(); RequestRefresh();
    }

    private static string MetricOf(string kind) => kind switch { "cpu" => UsageMetrics.Cpu, "memory" => UsageMetrics.Memory, _ => UsageMetrics.Gpu };

    /// <summary>Lists the busiest apps in the activated CPU, RAM or GPU bucket, on the grid the three charts share, so the list
    /// aggregates exactly the minutes the point averaged. With no GPU selected there is no GPU view, so nothing to list.</summary>
    private void ShowMoment(string kind, int index, ProcessRank ranking)
    {
        if (data?.Metrics.GetValueOrDefault(MetricOf(kind)) is not { } view || index < 0 || index >= view.Buckets.Count) return;
        var bucket = view.Buckets[index];
        resources.ShowSlot(bucket.Start, bucket.End, ranking);
        marked = bucket.Start + (bucket.End - bucket.Start) / 2; MarkMoment();
        // The list sits below the Device charts: activation scrolls it in, and keyboard focus stays on the chart.
        tabs.SelectedItem = resourcesTab; ShowTab();
        resourcesCard.StartBringIntoView();
    }

    /// <summary>Puts the cursor at the bucket Left or Right moved to on one chart and marks its time on all three; nothing is listed until Enter.</summary>
    private void MoveMoment(string kind, int index)
    {
        if (data?.Metrics.GetValueOrDefault(MetricOf(kind)) is not { } view || index < 0 || index >= view.Buckets.Count) return;
        var bucket = view.Buckets[index];
        marked = bucket.Start + (bucket.End - bucket.Start) / 2; MarkMoment();
    }

    /// <summary>Draws the hairline on each of the CPU, RAM and GPU charts at its own bucket holding the marked time, or none where it
    /// has no such bucket. The charts match by time, not by index, because their bucket lists differ where one metric has no minutes.</summary>
    private void MarkMoment()
    {
        MarkStrip();
        if (data is not { } shown || marked is not { } time) return;
        foreach (var (kind, metric, _) in Moments)
            charts[kind].SelectedIndex = UsageChartModel.BucketHolding(shown.Metrics.GetValueOrDefault(metric)?.Buckets ?? [], time);
    }

    private (DateTimeOffset From, DateTimeOffset To) Period()
    {
        var to = DateTimeOffset.UtcNow.AddDays(-offset * Days);
        return (to.AddDays(-Days), to);
    }

    private void UpdatePeriod()
    {
        var (from, to) = Period();
        period.Text = Days == 1 ? $"{from.ToLocalTime():MMM d HH:mm} – {to.ToLocalTime():MMM d HH:mm}" : $"{from.ToLocalTime():MMM d yyyy} – {to.ToLocalTime():MMM d yyyy}";
        later.IsEnabled = offset > 0;
    }

    /// <summary>A Usage history or Application resources clear started or ended: everything is read again.</summary>
    private void HistoryClearChanged(HistoryClearStore store) { if (store is HistoryClearStore.Usage or HistoryClearStore.ProcessHistory) RequestRefresh(); }

    // A strip read still in flight is for the period before this request, so it is dropped when it answers.
    private void RequestRefresh() { revision++; stripRequest++; UpdatePeriod(); _ = RefreshAsync(); }

    private async Task RefreshAsync()
    {
        if (!loaded) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var result = await LoadAsync();
            if (requested != revision || !loaded) { refreshAgain = true; return; }
            Apply(result);
        }
        catch (Exception error) { recordingStatus.Text = $"Usage history could not be read ({error.GetType().Name}). {session.Usage.Status}"; }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (loaded) _ = RefreshAsync(); }
        }
    }

    private async Task<PageData> LoadAsync()
    {
        var (from, to) = Period();
        double width = charts["cpu"].ActualWidth;
        int points = (int)Math.Clamp(width > 0 ? width : 600, 60, 1500);
        // The resources view reads on its own, so the Device charts never wait for a month of process history. It reads the selected
        // app's series with the same point count, so that chart's buckets are the CPU, RAM and GPU charts' slots.
        resources.Load(from, to, points);
        // The strip, and a listed strip slot, read on their own the same way.
        _ = ReadStripAsync(from, to, points);
        if (applicationSlot is { } listed) _ = ReadSlotAsync(listed.From, listed.To);
        string? gpu = session.GpuName is var name && name != "GPU unavailable" ? name : null;
        var service = session.Usage;
        var reads = new Dictionary<string, Task<UsageHistoryView>>(StringComparer.Ordinal)
        {
            [UsageMetrics.Cpu] = service.ReadMetricAsync(UsageMetrics.Cpu, null, from, to, points),
            [UsageMetrics.CpuTemperature] = service.ReadMetricAsync(UsageMetrics.CpuTemperature, null, from, to, points),
            [UsageMetrics.Memory] = service.ReadMetricAsync(UsageMetrics.Memory, null, from, to, points),
            [UsageMetrics.Download] = service.ReadMetricAsync(UsageMetrics.Download, null, from, to, points),
            [UsageMetrics.Upload] = service.ReadMetricAsync(UsageMetrics.Upload, null, from, to, points),
        };
        if (gpu is not null)
        {
            reads[UsageMetrics.Gpu] = service.ReadMetricAsync(UsageMetrics.Gpu, gpu, from, to, points);
            reads[UsageMetrics.GpuTemperature] = service.ReadMetricAsync(UsageMetrics.GpuTemperature, gpu, from, to, points);
        }
        var applicationsTask = service.ReadApplicationsAsync(from, to);
        await Task.WhenAll(reads.Values.Concat<Task>([applicationsTask]));
        return new(from, to, reads.ToDictionary(p => p.Key, p => p.Value.Result, StringComparer.Ordinal), applicationsTask.Result, service.Status, gpu);
    }

    private void Apply(PageData result)
    {
        data = result; recordingStatus.Text = result.Status;
        UsageHistoryView View(string metric) => result.Metrics.GetValueOrDefault(metric) ?? new(result.From, result.To, 60_000, [], new(null, null, null), 0, null, 0);
        foreach (var (kind, title, unit, maximum) in Charts)
        {
            UsageChartSeries[] traces = kind switch
            {
                "network" => [new(View(UsageMetrics.Download), "download", false, "Download"), new(View(UsageMetrics.Upload), "upload", true, "Upload")],
                "cpu" => [new(View(UsageMetrics.Cpu), "cpu", false, title)],
                "cputemp" => [new(View(UsageMetrics.CpuTemperature), "cputemp", false, title)],
                "gpu" => [new(View(UsageMetrics.Gpu), "gpu", false, title)],
                "gputemp" => [new(View(UsageMetrics.GpuTemperature), "gputemp", false, title)],
                _ => [new(View(UsageMetrics.Memory), "memory", false, title)],
            };
            charts[kind].SetData(traces, unit, maximum);
            statistics[kind].Text = kind == "network"
                ? $"↓ {UsagePresenter.Statistics(View(UsageMetrics.Download), unit)}    ↑ {UsagePresenter.Statistics(View(UsageMetrics.Upload), unit)}"
                : kind is "gpu" or "gputemp" && result.GpuName is null ? "No GPU selected on the dashboard."
                : UsagePresenter.Statistics(traces[0].View, unit);
        }
        MarkMoment();
        var earliest = result.Metrics.Values.Select(v => v.EarliestRetained).FirstOrDefault(e => e is not null) ?? result.Applications.EarliestRetained;
        long bytes = result.Metrics.Values.Select(v => v.StorageBytes).DefaultIfEmpty(result.Applications.StorageBytes).Max();
        storage.Text = $"Local history: {HistoryChartModel.Bytes(bytes)} · up to 400 days" + (earliest is { } first ? $"\nEarliest retained minute: {first.ToLocalTime():g}" : "\nNo minutes recorded yet.");
        RefreshApplications();
    }

    /// <summary>Shows the period's applications filtered by the search, and re-selects the selected row by name.</summary>
    private void RefreshApplications()
    {
        if (data is null) return;
        // A listed slot shows its own read; while that loads or is unreadable the table is empty and the summary says why.
        var view = applicationSlot is null ? data.Applications : slotApplications;
        string query = search.Text.Trim();
        IReadOnlyList<TableRow> rows = view is null ? [] : UsagePresenter.ApplicationRows(view, query);
        applicationSummary.Text = view is null ? slotPending ?? LoadingSlot : UsagePresenter.ApplicationSummary(view, query, rows.Count, applicationSlot is not null);
        gapNotice.Text = view is { CaptureGapMinutes: > 0 }
            ? $"{view.CaptureGapMinutes:N0} recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out." : "";
        gapNotice.Visibility = gapNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedApplication is not null && !rows.Any(r => r.Key == selectedApplication)) selectedApplication = null;
        applications.SetRows(rows, selectedApplication);
        UpdateShowInConnections();
    }

    /// <summary>The whole minutes Show in Connections opens: the listed slot's, otherwise the applied period's, clipped to the 30 days Connections keeps.</summary>
    private ((DateTimeOffset From, DateTimeOffset To) Span, (DateTimeOffset From, DateTimeOffset To)? Target)? ConnectionsSpan()
    {
        if ((applicationSlot ?? (data is { } shown ? (shown.From, shown.To) : null)) is not { } span) return null;
        return (span, TrafficStrips.ConnectionsTarget(span.From, span.To, DateTimeOffset.UtcNow));
    }

    /// <summary>The selected row's bare name, or null for the Other row, "Unknown application" or no selection.</summary>
    private string? CarriedApplication()
        => selectedApplication is { } name && name != OtherKey && name != UsageMetrics.UnknownName ? name : null;

    private void UpdateShowInConnections()
    {
        if (ConnectionsSpan() is not { } connections) { showInConnections.IsEnabled = false; ToolTipService.SetToolTip(showInConnections, null); return; }
        showInConnections.IsEnabled = connections.Target is not null;
        if (connections.Target is not { } target) { ToolTipService.SetToolTip(showInConnections, "Connections keeps up to 30 days; this period is older."); return; }
        string tip = applicationSlot is null ? "Opens Connections History for this period." : "Opens Connections History for the listed slot.";
        if (CarriedApplication() is not null) tip += " Only the selected application is shown.";
        if (target.From - TrafficStrips.CeilingMinute(connections.Span.From) > TimeSpan.FromHours(1)) tip += " Connections keeps up to 30 days, so earlier minutes are left out.";
        ToolTipService.SetToolTip(showInConnections, tip);
    }

    private void ShowInConnections()
    {
        if (ConnectionsSpan() is { Target: { } target }) session.RequestConnectionsHistory(target.From, target.To, CarriedApplication());
    }

    private async Task ExportAsync(bool applicationsCsv)
    {
        var (from, to) = Period();
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"DeadWare-usage-{(applicationsCsv ? "applications" : "device")}-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("CSV files", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        Windows.Storage.StorageFile? file;
        try { file = await picker.PickSaveFileAsync(); }
        catch (Exception error) { SetActionStatus("The save dialog could not be opened. " + error.Message); return; }
        if (file is null) return;
        exportMetrics.IsEnabled = exportApplications.IsEnabled = false; SetActionStatus("Exporting saved history…");
        try
        {
            // The picker leaves an empty file at a new name, and the export only writes to a name that does not exist yet.
            // An existing file with content is left alone, so the export refuses it as the WPF app does.
            if (new FileInfo(file.Path) is { Exists: true, Length: 0 }) File.Delete(file.Path);
            await session.Usage.ExportCsvAsync(file.Path, applicationsCsv, from, to);
            SetActionStatus("Export saved: " + file.Path);
        }
        catch (Exception error) { SetActionStatus("Export could not be saved. Choose a new file name. " + error.Message); }
        finally { exportMetrics.IsEnabled = exportApplications.IsEnabled = true; }
    }
}
