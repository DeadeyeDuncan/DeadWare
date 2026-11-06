using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private sealed record ApplicationRow(string Name, string Download, string Upload, string Total, string Share, long Bytes);
    private static readonly (string Kind, string Title, string Unit, double? Maximum)[] Charts =
    [
        ("cpu", "CPU usage", "%", 100), ("cputemp", "CPU temperature", "°C", null),
        ("gpu", "GPU usage", "%", 100), ("gputemp", "GPU temperature", "°C", null),
        ("memory", "RAM usage", "%", 100), ("network", "Network download / upload", "B/s", null),
    ];
    private readonly UiSession session;
    private readonly ComboBox range = new() { MinWidth = 110 };
    private readonly TextBlock period = Ui.Text("", 13), applicationSummary = Ui.Text("", 12, "Muted"), gapNotice = Ui.Text("", 12, "Warning");
    private readonly TextBlock recordingStatus = Ui.Text("", 12, "Muted"), storage = Ui.Text("", 11, "Muted"), actionStatus = Ui.Text("", 12, "Warning");
    private readonly Dictionary<string, UsageHistoryChart> charts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> statistics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> titles = new(StringComparer.Ordinal);
    private readonly Grid shareBar = new() { Height = 14, Margin = new(0, 6, 0, 10) };
    private readonly DataGrid applications = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 420, CanUserSortColumns = false };
    private readonly TextBox search = new() { Width = 220 };
    private readonly CheckBox recording = new() { Content = "Record usage history on this device" };
    private readonly Button exportMetrics, exportApplications, earlier, later;
    private readonly ApplicationResourcesCard resources;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private UsagePageData? data;
    private int revision, offset;
    private bool refreshing, refreshAgain, syncing;
    private int Days => (range.SelectedItem as RangeChoice)?.Days ?? 1;

    public UsageHistoryPage(UiSession session, bool showOpenWindow = true)
    {
        this.session = session;
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
        var deviceHint = Ui.Text("Each point is the average of the recorded minutes in its slot; the shaded band spans their minimum and maximum. Minutes with no readings stay empty and partial slots are not connected. Statistics use the stored minute values for the whole period. Dates use local time.", 11, "Muted");
        device.Children.Add(deviceHint); body.Children.Add(Card(device));

        var apps = new StackPanel(); apps.Children.Add(Ui.Heading("Applications", 16));
        var searchLabel = Ui.Text("Find application", 12, "Muted"); searchLabel.Margin = new(0, 0, 12, 0);
        var searchRow = Ui.Row(searchLabel, search); searchRow.Margin = new(0, 8, 0, 4); apps.Children.Add(searchRow);
        AutomationProperties.SetName(search, "Find application in usage history");
        apps.Children.Add(shareBar); apps.Children.Add(applicationSummary); gapNotice.Margin = new(0, 4, 0, 4); apps.Children.Add(gapNotice);
        Column("Application", nameof(ApplicationRow.Name), 3); Column("Download", nameof(ApplicationRow.Download), 1);
        Column("Upload", nameof(ApplicationRow.Upload), 1); Column("Total", nameof(ApplicationRow.Total), 1); Column("Share", nameof(ApplicationRow.Share), 1);
        var table = Ui.Panel(applications, new Thickness(0)); table.Margin = new(0, 6, 0, 8); apps.Children.Add(table);
        apps.Children.Add(Ui.Text("Totals come from observed per-application traffic (ETW) during the period, summed by executable name across its processes. Time without capture is a gap, not zero traffic. Open Connections to see where the bytes went.", 11, "Muted"));
        body.Children.Add(Card(apps));
        resources = new ApplicationResourcesCard(session); body.Children.Add(Card(resources));

        var settings = new StackPanel(); settings.Children.Add(Ui.Heading("Keep a longer record", 16)); recording.Margin = new(0, 12, 0, 8); settings.Children.Add(recording);
        AutomationProperties.SetName(recording, "Record usage history");
        settings.Children.Add(recordingStatus); storage.Margin = new(0, 8, 0, 8); settings.Children.Add(storage);
        settings.Children.Add(Ui.Text("One row per minute for CPU, GPU, RAM and network, plus per-application bytes, kept for up to 400 days or 256 MiB, whichever comes first. The six-hour live graphs are separate and unchanged. Recording begins with this version while DeadWare runs; sleep and stopped monitoring leave gaps. While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory and I/O for 30 days (up to 128 MiB).", 12, "Muted"));
        var local = Ui.Text("History stays in this Windows profile in usage-history.db and process-history.db and is never uploaded. Application names and byte totals describe what runs on this computer, so treat exports as private. Back up the databases only while DeadWare is fully quit.", 11, "Muted"); local.Margin = new(0, 8, 0, 0); settings.Children.Add(local);
        body.Children.Add(Card(settings)); body.Children.Add(actionStatus);

        recording.IsChecked = session.Settings.UsageRecording;
        recording.Checked += (_, _) => ChangeRecording(true); recording.Unchecked += (_, _) => ChangeRecording(false);
        range.SelectionChanged += (_, _) => { offset = 0; RequestRefresh(); };
        search.TextChanged += (_, _) => RefreshApplications();
        timer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { session.Changed += RefreshCurrent; UpdateVisibility(); };
        Unloaded += (_, _) => { session.Changed -= RefreshCurrent; timer.Stop(); revision++; };
        IsVisibleChanged += (_, _) => UpdateVisibility(); RefreshCurrent(); UpdatePeriod();
    }
    private void Column(string title, string binding, double weight)
        => applications.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = new DataGridLength(weight, DataGridLengthUnitType.Star) });
    private static Border Card(UIElement content) { var card = Ui.Panel(content, new(16)); card.Margin = new(0, 0, 0, 12); return card; }
    private void ChangeRecording(bool enabled)
    { if (syncing) return; session.SetUsageRecording(enabled); recordingStatus.Text = session.Usage.Status; }
    private void RefreshCurrent()
    {
        syncing = true; recording.IsChecked = session.Settings.UsageRecording; syncing = false;
        if (data is null) recordingStatus.Text = session.Usage.Status;
        string gpu = session.GpuName;
        titles["gpu"].Text = gpu == "GPU unavailable" ? "GPU usage" : $"GPU usage · {gpu}";
        titles["gputemp"].Text = gpu == "GPU unavailable" ? "GPU temperature" : $"GPU temperature · {gpu}";
    }
    private void UpdateVisibility()
    {
        if (IsLoaded && IsVisible) { timer.Start(); RefreshCurrent(); _ = RefreshAsync(); }
        else { timer.Stop(); revision++; }
    }
    private void Step(int direction) { offset = Math.Max(0, offset + direction); UpdatePeriod(); RequestRefresh(); }
    /// <summary>Selects the Day range and steps back to the day that contains <paramref name="at"/>.</summary>
    public void ShowDay(DateTimeOffset at)
    {
        range.SelectedIndex = 0;
        offset = Math.Max(0, (int)Math.Floor((DateTimeOffset.UtcNow - at).TotalDays));
        UpdatePeriod(); RequestRefresh();
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
    private void RequestRefresh() { revision++; UpdatePeriod(); _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        if (!IsLoaded || !IsVisible) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true; int requested = revision;
        try
        {
            var result = await LoadAsync();
            if (requested != revision || !IsLoaded || !IsVisible) { refreshAgain = true; return; }
            Apply(result);
        }
        finally
        {
            refreshing = false;
            if (refreshAgain) { refreshAgain = false; if (IsLoaded && IsVisible) _ = RefreshAsync(); }
        }
    }
    private async Task<UsagePageData> LoadAsync()
    {
        var (from, to) = Period();
        // The card reads on its own, so the Device charts never wait for a month of process history (about a second).
        resources.Load(from, to);
        string? gpu = session.GpuName is var name && name != "GPU unavailable" ? name : null;
        int points = (int)Math.Clamp(charts["cpu"].ActualWidth > 0 ? charts["cpu"].ActualWidth : 600, 60, 1500);
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
    private void RefreshApplications()
    {
        if (data is null) return;
        var view = data.Applications;
        long total = view.Applications.Sum(a => a.Received + a.Sent) + view.OtherReceived + view.OtherSent;
        string Share(long bytes) => total <= 0 ? "—" : $"{100.0 * bytes / total:0.#}%";
        string query = search.Text.Trim();
        var rows = view.Applications.Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(a => new ApplicationRow(a.Name, Ui.Bytes(a.Received), Ui.Bytes(a.Sent), Ui.Bytes(a.Received + a.Sent), Share(a.Received + a.Sent), a.Received + a.Sent)).ToList();
        if (view.OtherApplications > 0 && query.Length == 0)
            rows.Add(new($"Other applications ({view.OtherApplications:N0} more)", Ui.Bytes(view.OtherReceived), Ui.Bytes(view.OtherSent),
                Ui.Bytes(view.OtherReceived + view.OtherSent), Share(view.OtherReceived + view.OtherSent), view.OtherReceived + view.OtherSent));
        applications.ItemsSource = rows;
        applicationSummary.Text = view.Applications.Count == 0 ? "No application traffic recorded in this period."
            : query.Length > 0 ? $"{rows.Count} of {view.Applications.Count + view.OtherApplications:N0} applications match."
            : $"{view.Applications.Count + view.OtherApplications:N0} applications · {Ui.Bytes(total)} observed · {view.RecordedMinutes:N0} recorded minutes";
        gapNotice.Text = view.CaptureGapMinutes > 0 ? $"{view.CaptureGapMinutes:N0} recorded minutes had no per-application capture; their traffic is not counted here." : "";
        gapNotice.Visibility = gapNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        shareBar.Children.Clear(); shareBar.ColumnDefinitions.Clear();
        var shares = view.Applications.Take(8).Select(a => (a.Name, Bytes: a.Received + a.Sent)).ToList();
        long rest = total - shares.Sum(s => s.Bytes);
        if (rest > 0) shares.Add(("Other", rest));
        shareBar.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        string[] colors = ["Graph/download", "Graph/upload", "Graph/cpu", "Graph/gpu", "Graph/memory", "Graph/cputemp", "Graph/gputemp", "Accent", "Muted"];
        for (int i = 0; i < shares.Count; i++)
        {
            shareBar.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(1, shares[i].Bytes), GridUnitType.Star) });
            var piece = new Rectangle { RadiusX = 3, RadiusY = 3, Margin = new(i == 0 ? 0 : 1, 0, 0, 0), ToolTip = $"{shares[i].Name}: {Share(shares[i].Bytes)}" };
            piece.SetResourceReference(Shape.FillProperty, shares[i].Name == "Other" ? "Muted" : colors[i % colors.Length]);
            Grid.SetColumn(piece, i); shareBar.Children.Add(piece);
        }
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
