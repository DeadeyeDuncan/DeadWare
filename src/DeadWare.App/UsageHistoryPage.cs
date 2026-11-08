using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.Core;
using Microsoft.Win32;

namespace DeadWare.App;

/// <summary>Everything one refresh of the page reads, so tests can apply a fixture without a store.</summary>
internal sealed record UsagePageData(DateTimeOffset From, DateTimeOffset To, IReadOnlyDictionary<string, UsageHistoryView> Metrics,
    UsageApplicationView Applications, string Status, string? GpuName);

internal sealed class UsageHistoryPage : ScrollViewer
{
    private sealed record RangeChoice(string Label, int Days) { public override string ToString() => Label; }
    /// <summary>A row of the Applications table; <see cref="IsOther"/> marks the "Other applications" row, which names no application.</summary>
    private sealed record ApplicationRow(string Name, string Download, string Upload, string Total, string Share, long Bytes, bool IsOther = false);
    private static readonly (string Kind, string Title, string Unit, double? Maximum)[] Charts =
    [
        ("cpu", "CPU usage", "%", 100), ("cputemp", "CPU temperature", "°C", null),
        ("gpu", "GPU usage", "%", 100), ("gputemp", "GPU temperature", "°C", null),
        ("memory", "RAM usage", "%", 100), ("network", "Network download / upload", "B/s", null),
    ];
    // Click a moment: the CPU, RAM and GPU charts list a slot's busiest apps in the Application resources card, ranked by their own metric.
    // The GPU chart plots only the dashboard's selected GPU, but its slots rank by each app's stored GPU-seconds (its busiest engine on any
    // adapter). Every metric is read with the CPU chart's point count, so the three share one slot grid.
    private static readonly (string Kind, ProcessRank Rank)[] Moments =
        [("cpu", ProcessRank.CpuTime), ("memory", ProcessRank.AverageMemory), ("gpu", ProcessRank.Gpu)];
    private readonly UiSession session;
    private readonly ComboBox range = new() { MinWidth = 110 };
    private readonly TextBlock period = Ui.Text("", 13), applicationSummary = Ui.Text("", 12, "Muted"), gapNotice = Ui.Text("", 12, "Warning");
    private readonly TextBlock recordingStatus = Ui.Text("", 12, "Muted"), storage = Ui.Text("", 11, "Muted"), actionStatus = Ui.Text("", 12, "Warning");
    private readonly Dictionary<string, UsageHistoryChart> charts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> statistics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> titles = new(StringComparer.Ordinal);
    private readonly DataGrid applications = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 420,
        CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly TextBlock applicationsHeading = Ui.Heading(ApplicationsTitle, 16);
    private readonly Button applicationsClear, showInConnections;
    // The traffic strip in the Applications card; it joins the CPU, RAM and GPU cursor and lists a slot's applications in the table.
    private readonly TrafficStripView strip;
    private readonly TextBox search = new() { Width = 220 };
    private readonly CheckBox recording = new() { Content = "Record usage history on this device" };
    private readonly Button exportMetrics, exportApplications, earlier, later;
    private readonly ApplicationResourcesCard resources;
    private readonly Border resourcesCard;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private const string ApplicationsTitle = "Applications", LoadingSlot = "Loading this slot…", SlotUnavailable = "History unavailable for this slot.";
    private UsagePageData? data;
    // The strip slot the Applications table lists, or null for the period; its last read (null while loading or unreadable) and the text
    // shown in place of the rows until a read arrives.
    private (DateTimeOffset From, DateTimeOffset To)? applicationSlot;
    private UsageApplicationView? slotApplications;
    private string? slotPending;
    // The selected row's name as the table shows it, kept across rebuilds; null when no row is selected or its row is no longer shown.
    private string? selectedApplication;
    private int stripRequest, slotRequest;
    // Reads and state the checks replace: the strip read, the slot read and whether the page is showing (assigned in the constructor,
    // since a field initializer cannot read this), and the clock that places the not-yet-saved minutes and the Connections target.
    private Func<DateTimeOffset, DateTimeOffset, int, Task<TrafficStrip?>> readStrip;
    private Func<DateTimeOffset, DateTimeOffset, Task<UsageApplicationView?>> readSlotApplications;
    private Func<bool> showing;
    internal Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
    // The moment the CPU, RAM and GPU hairlines mark: the listed slot's middle, or the bucket Left or Right moved to since; null for none.
    private DateTimeOffset? marked;
    private int revision, offset;
    private bool refreshing, refreshAgain, syncing;
    private int Days => (range.SelectedItem as RangeChoice)?.Days ?? 1;

    public UsageHistoryPage(UiSession session, bool showOpenWindow = true)
    {
        this.session = session;
        readStrip = (from, to, points) => session.Usage.TryReadApplicationSeriesAsync(from, to, points);
        readSlotApplications = (from, to) => session.Usage.TryReadApplicationsAsync(from, to);
        showing = () => IsLoaded && IsVisible;
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        var body = new StackPanel { Margin = new(6, 10, 6, 10) }; Content = body;
        var controls = new WrapPanel { Margin = new(0, 0, 0, 10) };
        range.ItemsSource = new[] { new RangeChoice("Day", 1), new("Week", 7), new("Month", 30) }; range.SelectedIndex = 0;
        AutomationProperties.SetName(range, "Usage history time range"); controls.Children.Add(range);
        earlier = Ui.Button("◀ Earlier", () => Step(1)); later = Ui.Button("Later ▶", () => Step(-1));
        AutomationProperties.SetName(earlier, "Earlier period"); AutomationProperties.SetName(later, "Later period");
        controls.Children.Add(earlier); controls.Children.Add(later);
        period.Margin = new(10, 0, 10, 0); controls.Children.Add(period);
        controls.Children.Add(Ui.Button("Refresh", () => RequestRefresh()));
        exportMetrics = Ui.Button("Export device CSV…", () => _ = ExportAsync(false)); controls.Children.Add(exportMetrics);
        exportApplications = Ui.Button("Export applications CSV…", () => _ = ExportAsync(true)); controls.Children.Add(exportApplications);
        if (showOpenWindow) controls.Children.Add(Ui.Button("Open in window ↗", () => session.OpenDetached("usagehistory")));
        body.Children.Add(controls);

        var device = new StackPanel(); device.Children.Add(Ui.Heading("Device", 16));
        var grid = new UniformGrid { Columns = 2, Margin = new(0, 8, 0, 0) };
        foreach (var (kind, title, _, _) in Charts)
        {
            var cell = new StackPanel { Margin = new(0, 0, 12, 14) };
            var heading = Ui.Text(title, 13, "Heading", "HeadingFont"); titles[kind] = heading; cell.Children.Add(heading);
            var chart = new UsageHistoryChart { Height = 170, Margin = new(0, 6, 0, 4) }; charts[kind] = chart; cell.Children.Add(chart);
            var stats = Ui.Text("", 11, "Muted"); statistics[kind] = stats; cell.Children.Add(stats);
            grid.Children.Add(cell);
        }
        device.Children.Add(grid);
        var deviceHint = Ui.Text("Each point is the average of the recorded minutes in its slot; the shaded band spans their minimum and maximum. Minutes with no readings stay empty and partial slots are not connected. Statistics use the stored minute values for the whole period. Dates use local time. Click a CPU, RAM or GPU point, or press Enter on it, to list its busiest apps under Application resources.", 11, "Muted");
        device.Children.Add(deviceHint); body.Children.Add(Card(device));

        // While a strip slot is listed the heading names it and Clear returns to the period; Show in Connections follows.
        applicationsClear = Ui.Button("Clear", ClearMoment); applicationsClear.Margin = new(12, 0, 0, 0); applicationsClear.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(applicationsClear, "Clear the listed slot");
        showInConnections = Ui.Button("Show in Connections", ShowInConnections); showInConnections.Margin = new(12, 0, 0, 0); showInConnections.IsEnabled = false;
        AutomationProperties.SetName(showInConnections, "Show in Connections"); ToolTipService.SetShowOnDisabled(showInConnections, true);
        var apps = new StackPanel(); apps.Children.Add(Ui.Row(applicationsHeading, applicationsClear, showInConnections));
        var searchLabel = Ui.Text("Find application", 12, "Muted"); searchLabel.Margin = new(0, 0, 12, 0);
        var searchRow = Ui.Row(searchLabel, search); searchRow.Margin = new(0, 8, 0, 4); apps.Children.Add(searchRow);
        AutomationProperties.SetName(search, "Find application in usage history");
        strip = new TrafficStripView(session, TrafficStripPage.UsageHistory) { Margin = new(0, 6, 0, 10) }; apps.Children.Add(strip);
        apps.Children.Add(applicationSummary); gapNotice.Margin = new(0, 4, 0, 4); apps.Children.Add(gapNotice);
        Column("Application", nameof(ApplicationRow.Name), 3); Column("Download", nameof(ApplicationRow.Download), 1);
        Column("Upload", nameof(ApplicationRow.Upload), 1); Column("Total", nameof(ApplicationRow.Total), 1); Column("Share", nameof(ApplicationRow.Share), 1);
        var table = Ui.Panel(applications, new Thickness(0)); table.Margin = new(0, 6, 0, 8); apps.Children.Add(table);
        apps.Children.Add(Ui.Text("Totals come from observed per-application traffic (ETW) during the period, summed by executable name across its processes. Time without capture is a gap, not zero traffic. The strip stacks the six applications with the most traffic, plus Other, as average rates over each slot's captured minutes; its colours identify applications, not direction. Click a slot, or press Enter on it, to list that slot's applications. Show in Connections opens where the bytes went, for up to the last 30 days.", 11, "Muted"));
        body.Children.Add(Card(apps));
        resources = new ApplicationResourcesCard(session); resourcesCard = Card(resources); body.Children.Add(resourcesCard);
        foreach (var (kind, ranking) in Moments)
        {
            charts[kind].Selectable = true;
            charts[kind].BucketActivated += index => ShowMoment(kind, index, ranking);
            charts[kind].SelectionMoved += index => MoveMoment(kind, index);
        }
        // The strip joins the cursor but is not a Moments chart: moving on it marks the time, and activating a slot lists it in the table.
        strip.Chart.Selectable = true;
        strip.Chart.SelectionMoved += MoveOnStrip;
        strip.Chart.SlotActivated += (index, _) => ShowApplicationsSlot(index);
        applications.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            selectedApplication = (applications.SelectedItem as ApplicationRow)?.Name; UpdateShowInConnections();
        };
        // ClearSlot always raises SlotCleared, so a range change, Earlier, Later and ShowDay clear the Applications slot too.
        resources.SlotCleared += () =>
        {
            marked = null; foreach (var (kind, _) in Moments) charts[kind].SelectedIndex = null;
            strip.Chart.SelectedIndex = null; ClearApplicationsSlot();
        };
        PreviewKeyDown += (_, e) =>
        {
            // Esc returns both cards to the period from anywhere on the page, and clears the cursor even where a refresh left its time in
            // no bucket and no hairline shows; keys in a drop-down (its own popup) stay its own.
            if (e.Key == Key.Escape && e.OriginalSource is Visual source && source.IsDescendantOf(this)
                && (resources.Slot is not null || applicationSlot is not null || marked is not null))
            { ClearMoment(); e.Handled = true; }
        };

        var settings = new StackPanel(); settings.Children.Add(Ui.Heading("Keep a longer record", 16)); recording.Margin = new(0, 12, 0, 8); settings.Children.Add(recording);
        AutomationProperties.SetName(recording, "Record usage history");
        settings.Children.Add(recordingStatus); storage.Margin = new(0, 8, 0, 8); settings.Children.Add(storage);
        settings.Children.Add(Ui.Text("One row per minute for CPU, GPU, RAM and network, plus per-application bytes, kept for up to 400 days or 256 MiB, whichever comes first. The six-hour live graphs are separate and unchanged. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days (up to 192 MiB).", 12, "Muted"));
        var local = Ui.Text("History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit.", 11, "Muted"); local.Margin = new(0, 8, 0, 0); settings.Children.Add(local);
        body.Children.Add(Card(settings)); body.Children.Add(actionStatus);

        recording.IsChecked = session.Settings.UsageRecording;
        recording.Checked += (_, _) => ChangeRecording(true); recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => { offset = 0; resources.ClearSlot(); RequestRefresh(); };
        search.TextChanged += (_, _) => RefreshApplications();
        timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += RefreshCurrent; session.HistoryClearChanged += HistoryClearChanged; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= RefreshCurrent; session.HistoryClearChanged -= HistoryClearChanged; timer.Stop(); revision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility(); RefreshCurrent(); UpdatePeriod();
    }
    private void Column(string title, string binding, double weight)
        => applications.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = new DataGridLength(weight, DataGridLengthUnitType.Star) });
    private static Border Card(UIElement content) { var card = Ui.Panel(content, new(16)); card.Margin = new(0, 0, 0, 12); return card; }
    private void ChangeRecording(bool enabled)
    { if (syncing) return; session.SetUsageRecording(enabled); recordingStatus.Text = session.Usage.Status; resources.RefreshStatus(); }
    /// <summary>Follows settings changed elsewhere (session.Changed), such as Per-process monitoring on the Processes page.</summary>
    private void RefreshCurrent()
    {
        syncing = true; recording.IsChecked = session.Settings.UsageRecording; syncing = false;
        if (data is null) recordingStatus.Text = session.Usage.Status;
        resources.RefreshStatus();
        string gpu = session.GpuName;
        titles["gpu"].Text = gpu == "GPU unavailable" ? "GPU usage" : $"GPU usage · {gpu}";
        titles["gputemp"].Text = gpu == "GPU unavailable" ? "GPU temperature" : $"GPU temperature · {gpu}";
    }
    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { timer.Start(); RefreshCurrent(); _ = RefreshAsync(); }
        else { timer.Stop(); revision++; }
    }
    // Another period has another bucket grid, so a listed slot and its hairlines clear with it.
    private void Step(int direction) { offset = Math.Max(0, offset + direction); resources.ClearSlot(); UpdatePeriod(); RequestRefresh(); }
    /// <summary>Selects the Day range and steps back to the day that contains <paramref name="at"/>.</summary>
    public void ShowDay(DateTimeOffset at)
    {
        range.SelectedIndex = 0;
        offset = Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - at).TotalDays));
        resources.ClearSlot(); UpdatePeriod(); RequestRefresh();
    }
    /// <summary>Lists the busiest apps in the activated CPU, RAM or GPU bucket. The bucket is the slot <c>ReadMetric</c> grouped,
    /// [from + k × bucket, from + (k + 1) × bucket) clipped to the period end, on the grid the CPU, RAM and GPU charts share, so the card
    /// aggregates exactly the minutes the point averaged. With no GPU selected on the dashboard there is no GPU view, so nothing to list.</summary>
    private void ShowMoment(string kind, int index, ProcessRank ranking)
    {
        if (data?.Metrics.GetValueOrDefault(kind) is not { } view || index >= view.Buckets.Count) return;
        var bucket = view.Buckets[index];
        resources.ShowSlot(bucket.Start, bucket.End, ranking);
        marked = Middle(bucket); MarkMoment();
        // The card sits below the Device charts, out of view at a window's height: a click or Enter scrolls it in. Only activation does
        // this (never the minute refresh), and keyboard focus stays on the chart, so Left, Right and Enter keep working.
        resourcesCard.BringIntoView();
    }
    /// <summary>Puts the cursor at the bucket Left or Right moved to on one chart and marks its time on all three charts; nothing is listed until Enter.</summary>
    private void MoveMoment(string kind, int index)
    {
        if (data?.Metrics.GetValueOrDefault(kind) is not { } view || index < 0 || index >= view.Buckets.Count) return;
        marked = Middle(view.Buckets[index]); MarkMoment();
    }
    private static DateTimeOffset Middle(UsageHistoryBucket bucket) => bucket.Start + (bucket.End - bucket.Start) / 2;
    private static DateTimeOffset Middle(TrafficStripSlot slot) => slot.Start + (slot.End - slot.Start) / 2;
    /// <summary>Puts the cursor at the strip slot Left or Right moved to and marks its time on the CPU, RAM and GPU charts; nothing is listed.</summary>
    private void MoveOnStrip(int index)
    {
        if (strip.Chart.Strip is not { } shown || index < 0 || index >= shown.Slots.Count) return;
        marked = Middle(shown.Slots[index]); MarkMoment();
    }
    /// <summary>Lists the activated strip slot in the Applications table and reads it at once. It never lists or clears the Application
    /// resources card's slot. Keyboard focus stays on the strip, and only activation brings the table into view.</summary>
    private void ShowApplicationsSlot(int index)
    {
        if (strip.Chart.Strip is not { } shown || index < 0 || index >= shown.Slots.Count) return;
        var slot = shown.Slots[index];
        applicationSlot = (slot.Start, slot.End);
        marked = Middle(slot); MarkMoment();
        applicationsHeading.Text = $"{ApplicationsTitle} · " + ApplicationResourcesCard.SlotSpan(TrafficStrips.CeilingMinute(slot.Start),
            TrafficStrips.CeilingMinute(slot.End), shown.To - shown.From > TimeSpan.FromDays(1));
        applicationsClear.Visibility = Visibility.Visible;
        slotApplications = null; slotPending = LoadingSlot;
        RefreshApplications();
        _ = ReadSlotAsync(slot.Start, slot.End);
        applications.BringIntoView();
    }
    /// <summary>Esc and the Applications Clear: both cards return to the period, and the cursor and every hairline clear.</summary>
    private void ClearMoment() { ClearApplicationsSlot(); resources.ClearSlot(); }
    private void ClearApplicationsSlot()
    {
        // Clear is about to collapse: holding the focus, it hands it to the search box just below it, as the card's Clear does to Rank by.
        var scope = FocusManager.GetFocusScope(applicationsClear);
        if ((applicationsClear.IsKeyboardFocused || ReferenceEquals(FocusManager.GetFocusedElement(scope), applicationsClear)) && !search.Focus())
            FocusManager.SetFocusedElement(scope, search);
        if (applicationSlot is null) return;
        applicationSlot = null; slotRequest++; slotApplications = null; slotPending = null;
        applicationsHeading.Text = ApplicationsTitle; applicationsClear.Visibility = Visibility.Collapsed;
        RefreshApplications();
    }
    /// <summary>Shows a strip read for the period; null shows "History unavailable". The minutes not saved yet by <paramref name="now"/>
    /// stay unhatched. The strip's selection then follows <see cref="marked"/>.</summary>
    internal void ApplyStrip(TrafficStrip? strip, DateTimeOffset now)
    {
        var saveDelay = UsageHistoryService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds);
        this.strip.Apply(strip, strip is null ? now : TrafficStrips.DueUntil(strip.To, now, saveDelay), now);
        MarkStrip();
    }
    /// <summary>Shows a read of the listed slot; null says it is unavailable, with no rows and no gap notice.</summary>
    internal void ApplySlotApplications(UsageApplicationView? view)
    {
        if (applicationSlot is null) return;
        slotApplications = view; slotPending = view is null ? SlotUnavailable : null;
        RefreshApplications();
    }
    private async Task ReadStripAsync(DateTimeOffset from, DateTimeOffset to, int points)
    {
        int current = ++stripRequest;
        TrafficStrip? read;
        try { read = await readStrip(from, to, points); }
        catch (ArgumentException) { read = null; }
        if (current != stripRequest || !showing()) return;
        ApplyStrip(read, clock());
    }
    private async Task ReadSlotAsync(DateTimeOffset from, DateTimeOffset to)
    {
        int current = ++slotRequest;
        UsageApplicationView? view;
        try { view = await readSlotApplications(from, to); }
        catch (ArgumentException) { view = null; }
        if (current != slotRequest || !showing()) return;
        ApplySlotApplications(view);
    }
    private void MarkStrip()
        => strip.Chart.SelectedIndex = marked is { } time && strip.Chart.Strip is { } shown ? TrafficStrips.SlotAt(shown, time) : null;
    /// <summary>Draws the hairline on each of the CPU, RAM and GPU charts at its own bucket holding <see cref="marked"/>, or none where it has
    /// no such bucket (the GPU chart has none while no GPU is selected), and on the strip at its slot holding it. The charts match by time,
    /// not by index, because their bucket lists differ where one metric has no minutes; and the mark follows that time when a refresh moves
    /// the grid.</summary>
    private void MarkMoment()
    {
        MarkStrip();
        if (data is not { } shown || marked is not { } time) return;
        foreach (var (kind, _) in Moments)
        {
            var buckets = shown.Metrics.GetValueOrDefault(kind)?.Buckets ?? [];
            int at = -1;
            for (int i = 0; i < buckets.Count && at < 0; i++) if (buckets[i].Start <= time && time < buckets[i].End) at = i;
            charts[kind].SelectedIndex = at >= 0 ? at : null;
        }
    }
    internal UsageHistoryChart Chart(string kind) => charts[kind];
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
    /// <summary>A Usage history or Application resources clear started or ended: the strip, the table, a listed slot and the card are read again.</summary>
    private void HistoryClearChanged(HistoryClearStore store) { if (store is HistoryClearStore.Usage or HistoryClearStore.ProcessHistory) RequestRefresh(); }
    // A strip read still in flight is for the period before this request, so it is dropped when it answers.
    private void RequestRefresh() { revision++; stripRequest++; UpdatePeriod(); _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        if (!showing()) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var result = await LoadAsync();
            if (requested != revision || !showing()) { refreshAgain = true; return; }
            Apply(result);
        }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (showing()) _ = RefreshAsync(); }
        }
    }
    private async Task<UsagePageData> LoadAsync()
    {
        var (from, to) = Period();
        int points = (int)Math.Clamp(charts["cpu"].ActualWidth > 0 ? charts["cpu"].ActualWidth : 600, 60, 1500);
        // The card reads on its own, so the Device charts never wait for a month of process history (about a second). It reads the
        // selected app's series with the same point count, so that chart's buckets are the CPU, RAM and GPU charts' slots. The strip, and
        // a listed strip slot, read on their own the same way.
        resources.Load(from, to, points);
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
    internal void Apply(UsagePageData result)
    {
        data = result; recordingStatus.Text = result.Status;
        UsageHistoryView Empty() => new(result.From, result.To, 60_000, [], new(null, null, null), 0, null, 0);
        UsageHistoryView View(string metric) => result.Metrics.GetValueOrDefault(metric) ?? Empty();
        foreach (var (kind, title, unit, maximum) in Charts)
        {
            UsageTrace[] traces = kind switch
            {
                "network" => [new(View(UsageMetrics.Download), Theme.GraphKey("download"), false, "Download"), new(View(UsageMetrics.Upload), Theme.GraphKey("upload"), true, "Upload")],
                "gpu" => [new(View(UsageMetrics.Gpu), Theme.GraphKey("gpu"), false, title)],
                "gputemp" => [new(View(UsageMetrics.GpuTemperature), Theme.GraphKey("gputemp"), false, title)],
                _ => [new(View(kind), Theme.GraphKey(kind), false, title)],
            };
            charts[kind].SetData(traces, unit, maximum);
            statistics[kind].Text = kind == "network"
                ? $"↓ {Statistics(View(UsageMetrics.Download), unit)}    ↑ {Statistics(View(UsageMetrics.Upload), unit)}"
                : kind is "gpu" or "gputemp" && result.GpuName is null ? "No GPU selected on the dashboard."
                : Statistics(traces[0].View, unit);
        }
        MarkMoment();
        var earliest = result.Metrics.Values.Select(v => v.EarliestRetained).FirstOrDefault(e => e is not null) ?? result.Applications.EarliestRetained;
        long bytes = result.Metrics.Values.Select(v => v.StorageBytes).DefaultIfEmpty(result.Applications.StorageBytes).Max();
        storage.Text = $"Local history: {Ui.Bytes(bytes)} · up to 400 days" + (earliest is { } first ? $"\nEarliest retained minute: {first.ToLocalTime():g}" : "\nNo minutes recorded yet.");
        RefreshApplications();
    }
    private static string Statistics(UsageHistoryView view, string unit)
    {
        if (view.Minutes == 0 || view.Statistics.Average is null) return "No readings in this period.";
        string Format(double? value) => unit == "B/s" ? Ui.Bytes(value, true) : Ui.Number(value, unit == "%" ? "%" : " " + unit);
        return $"MIN {Format(view.Statistics.Minimum)}    AVG {Format(view.Statistics.Average)}    MAX {Format(view.Statistics.Maximum)}    ·    {view.Minutes:N0} minutes";
    }
    /// <summary>Shows the period's applications, or the listed slot's, filtered by the search, and re-selects the selected row by name.</summary>
    private void RefreshApplications()
    {
        if (data is null) return;
        var view = applicationSlot is null ? data.Applications : slotApplications;
        string query = search.Text.Trim();
        var rows = new List<ApplicationRow>();
        if (view is null)
        {
            applicationSummary.Text = slotPending ?? LoadingSlot; gapNotice.Text = "";
        }
        else
        {
            long total = view.Applications.Sum(a => a.Received + a.Sent) + view.OtherReceived + view.OtherSent;
            string Share(long bytes) => total <= 0 ? "—" : $"{100.0 * bytes / total:0.#}%";
            rows.AddRange(view.Applications.Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(a => new ApplicationRow(a.Name, Ui.Bytes(a.Received), Ui.Bytes(a.Sent), Ui.Bytes(a.Received + a.Sent), Share(a.Received + a.Sent), a.Received + a.Sent)));
            if (view.OtherApplications > 0 && query.Length == 0)
                rows.Add(new($"Other applications ({view.OtherApplications:N0} more)", Ui.Bytes(view.OtherReceived), Ui.Bytes(view.OtherSent),
                    Ui.Bytes(view.OtherReceived + view.OtherSent), Share(view.OtherReceived + view.OtherSent), view.OtherReceived + view.OtherSent, IsOther: true));
            applicationSummary.Text = view.Applications.Count == 0
                ? applicationSlot is null ? "No application traffic recorded in this period." : "No application traffic recorded in this slot."
                : query.Length > 0 ? $"{rows.Count} of {view.Applications.Count + view.OtherApplications:N0} applications match."
                : $"{view.Applications.Count + view.OtherApplications:N0} applications · {Ui.Bytes(total)} observed · {view.RecordedMinutes:N0} recorded minutes";
            gapNotice.Text = view.CaptureGapMinutes > 0
                ? $"{view.CaptureGapMinutes:N0} recorded minutes had no or incomplete per-application capture; the totals may miss some of their traffic, and the strip leaves them out." : "";
        }
        gapNotice.Visibility = gapNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Replacing the rows clears the selection and raises SelectionChanged first, so the rebuild keeps the name and selects its row again.
        syncing = true;
        try
        {
            applications.ItemsSource = rows;
            var selected = rows.FirstOrDefault(r => string.Equals(r.Name, selectedApplication, StringComparison.Ordinal));
            applications.SelectedItem = selected;
            if (selected is null) selectedApplication = null;
        }
        finally { syncing = false; }
        UpdateShowInConnections();
    }
    /// <summary>The whole minutes Show in Connections opens: the listed slot's, otherwise the applied period's, clipped to the 30 days
    /// Connections keeps; null when none of them is kept.</summary>
    private ((DateTimeOffset From, DateTimeOffset To) Span, (DateTimeOffset From, DateTimeOffset To)? Target)? ConnectionsSpan()
    {
        if ((applicationSlot ?? (data is { } shown ? (shown.From, shown.To) : null)) is not { } span) return null;
        return (span, TrafficStrips.ConnectionsTarget(span.From, span.To, clock()));
    }
    /// <summary>The selected row's bare name, or null for the Other row, "Unknown application" or no selection.</summary>
    private string? CarriedApplication()
        => applications.SelectedItem is ApplicationRow { IsOther: false } row && row.Name != UsageMetrics.UnknownName ? row.Name : null;
    private void UpdateShowInConnections()
    {
        if (ConnectionsSpan() is not { } connections) { showInConnections.IsEnabled = false; showInConnections.ToolTip = null; return; }
        showInConnections.IsEnabled = connections.Target is not null;
        if (connections.Target is not { } target) { showInConnections.ToolTip = "Connections keeps up to 30 days; this period is older."; return; }
        string tip = applicationSlot is null ? "Opens Connections History for this period." : "Opens Connections History for the listed slot.";
        if (CarriedApplication() is not null) tip += " Only the selected application is shown.";
        if (target.From - TrafficStrips.CeilingMinute(connections.Span.From) > TimeSpan.FromHours(1)) tip += " Connections keeps up to 30 days, so earlier minutes are left out.";
        showInConnections.ToolTip = tip;
    }
    private void ShowInConnections()
    {
        if (ConnectionsSpan() is { Target: { } target }) session.RequestConnectionsHistory(target.From, target.To, CarriedApplication());
    }
    private async Task ExportAsync(bool applicationsCsv)
    {
        var (from, to) = Period();
        var dialog = new SaveFileDialog { Title = applicationsCsv ? "Export application totals to a new CSV" : "Export device usage minutes to a new CSV",
            Filter = "CSV files (*.csv)|*.csv", FileName = $"DeadWare-usage-{(applicationsCsv ? "applications" : "device")}-{DateTime.Now:yyyyMMdd-HHmmss}.csv", OverwritePrompt = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        exportMetrics.IsEnabled = exportApplications.IsEnabled = false; actionStatus.Text = "Exporting saved history…";
        try { await session.Usage.ExportCsvAsync(dialog.FileName, applicationsCsv, from, to); actionStatus.Text = "Export saved: " + dialog.FileName; }
        catch (Exception error) { actionStatus.Text = "Export could not be saved. Choose a new file name. " + error.Message; }
        finally { exportMetrics.IsEnabled = exportApplications.IsEnabled = true; }
    }
}
