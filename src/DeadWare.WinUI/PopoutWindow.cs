using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// A detached window: one graph with its readings on a single line and the rest behind an options button, or the
/// Battery history or Usage history page with an Always on top choice.
/// </summary>
internal sealed class PopoutWindow : Window
{
    public PopoutWindow(MonitorSession session, WindowManager manager, string kind)
    {
        var root = new Grid();
        Content = root;
        if (kind is "batteryhistory" or "usagehistory")
        {
            bool battery = kind == "batteryhistory";
            Title = battery ? "Battery history — DeadWare" : "Usage history — DeadWare";
            manager.Place(this, root, kind, battery ? 940 : 1100, battery ? 830 : 900, battery ? 620 : 720, battery ? 520 : 560);
            var pin = new CheckBox { Content = "Always on top", IsChecked = WindowManager.IsTopmost(this), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            pin.Checked += (_, _) => manager.SetTopmost(this, true);
            pin.Unchecked += (_, _) => manager.SetTopmost(this, false);
            var header = new Grid { Margin = new Thickness(24, 12, 24, 0) };
            header.Children.Add(new TextBlock { Text = battery ? "Battery history" : "Usage history", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(pin);
            Page page = battery ? new BatteryPage(session) { InWindow = true } : new UsagePage(session) { InWindow = true };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(page, 1);
            root.Children.Add(header);
            root.Children.Add(page);
            return;
        }
        Title = $"DeadWare · {GraphPopout.Heading(kind)}";
        manager.Place(this, root, kind, 420, 230, 300, 180);
        root.Children.Add(new GraphPopout(session, manager, this, kind, null) { Margin = new Thickness(6) });
    }

    /// <summary>One drive's read / write pop-out. Not placed or restored with the saved windows.</summary>
    public PopoutWindow(MonitorSession session, WindowManager manager, StorageDrive drive)
    {
        var root = new Grid();
        Content = root;
        Title = $"Disk · {drive.Model}";
        manager.Place(this, root, null, 420, 230, 300, 180);
        root.Children.Add(new GraphPopout(session, manager, this, "storage", drive.Id) { Margin = new Thickness(6) });
    }
}

/// <summary>A graph-first view for small detached windows: title, readings and an options button on one line, the trace below.</summary>
internal sealed class GraphPopout : UserControl
{
    private readonly MonitorSession session;
    private readonly string kind, chartKind;
    private readonly string? driveId;
    private readonly bool joined;
    private readonly HistoryChartView chart;
    private readonly TextBlock value = Reading(22), second = Reading(14), local = Small(), status = Small(), fan = Small(), pump = Small();
    private readonly TextBlock details = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap }, statistics = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly List<ToggleButton> ranges = [];
    private readonly Dictionary<TextBlock, Windows.UI.Color> tints = [];
    private bool syncing;

    /// <summary>The long name of a pop-out kind, for window titles.</summary>
    public static string Heading(string kind) => kind switch
    {
        "usage" => "CPU / GPU usage", "temperature" => "CPU / GPU temperature", "storage" => "Disk", _ => OverviewPresenter.Title(kind),
    };

    public GraphPopout(MonitorSession session, WindowManager manager, Window owner, string kind, string? driveId)
    {
        this.session = session;
        this.kind = kind;
        this.driveId = kind == "storage" ? driveId : null;
        joined = kind is "usage" or "temperature";
        chartKind = kind switch { "usage" => "cpu", "temperature" => "cputemp", _ => kind };
        chart = new HistoryChartView(session, chartKind, joined) { DriveId = this.driveId };
        chart.StatisticsReady += summary => statistics.Text =
            $"MIN {OverviewPresenter.Statistic(chartKind, summary.Minimum)} · AVG {OverviewPresenter.Statistic(chartKind, summary.Average)} · MAX {OverviewPresenter.Statistic(chartKind, summary.Maximum)}";

        var title = new TextBlock
        {
            Text = kind switch { "usage" => "Usage", "temperature" => "Temp", "cputemp" => "CPU temp", "gputemp" => "GPU temp", "network" => "Network", "storage" => "Disk", _ => OverviewPresenter.Title(kind) },
            FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(title, Heading(kind));
        var readings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { value } };
        if (kind is "network" or "storage" || joined) { value.FontSize = 14; readings.Children.Add(second); }
        if (kind == "network")
        {
            local.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(local, "Local-network share: LAN, loopback and link-local peers, from per-app capture");
            readings.Children.Add(local);
        }

        // Secondary information lives in the flyout, so the trace keeps the window's height.
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        AutomationProperties.SetName(rangeRow, "Graph history range");
        foreach (int seconds in HistoryRanges.Seconds)
        {
            string label = HistoryRanges.Label(seconds);
            var segment = new ToggleButton { Content = label, Tag = seconds, Padding = new Thickness(6, 3, 6, 3), FontSize = 11, MinWidth = 0 };
            AutomationProperties.SetName(segment, label + " history");
            segment.Checked += (_, _) => { if (!syncing) session.SetRange(seconds); };
            segment.Unchecked += (_, _) => { if (!syncing && session.HistorySeconds == seconds) segment.IsChecked = true; };
            ranges.Add(segment);
            rangeRow.Children.Add(segment);
        }
        var pin = new CheckBox { Content = "Always on top" };
        pin.Checked += (_, _) => { if (!syncing) manager.SetTopmost(owner, true); };
        pin.Unchecked += (_, _) => { if (!syncing) manager.SetTopmost(owner, false); };
        var menu = new StackPanel
        {
            Width = 290, Spacing = 8,
            Children = { new TextBlock { Text = "History range", FontSize = 12 }, rangeRow, pin, details, statistics },
        };
        var flyout = new Flyout { Content = menu };
        flyout.Opening += (_, _) =>
        {
            syncing = true;
            try { pin.IsChecked = WindowManager.IsTopmost(owner); } finally { syncing = false; }
            Refresh();
        };
        var options = new Button { Content = "⋯", Width = 28, Height = 26, Padding = new Thickness(0), Flyout = flyout, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(options, "Graph options");
        AutomationProperties.SetName(options, "Graph options");

        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(readings, 1);
        header.Children.Add(readings);
        Grid.SetColumn(options, 2);
        header.Children.Add(options);

        status.Visibility = fan.Visibility = pump.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(fan, "Selected GPU's first reported fan · separate left RPM scale");
        ToolTipService.SetToolTip(pump, "CPU cooler pump, or the CPU Fan header · separate left RPM scale");
        var annotations = new StackPanel { Children = { status, fan, pump } };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(annotations, 1);
        Grid.SetRow(chart, 2);
        layout.Children.Add(header);
        layout.Children.Add(annotations);
        layout.Children.Add(chart);
        var card = Pages.Card(layout);
        card.Padding = new Thickness(10, 4, 10, 4);
        Content = card;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        Loaded += (_, _) => { session.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => session.Changed -= Refresh;
    }

    private static TextBlock Reading(double size) => new() { Text = "—", FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Small() => new() { FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    private void Tint(TextBlock text, string key)
    {
        var color = chart.ColorOf(key);
        if (tints.TryGetValue(text, out var shown) && shown.Equals(color)) return;
        tints[text] = color;
        text.Foreground = new SolidColorBrush(color);
    }

    private double? Live(string id) => session.Stale ? null : session.Value(id);

    private string Pattern(string key, bool dashed = false, bool dotted = false) =>
        OverviewPresenter.Pattern(session.Settings.GraphStyles.TryGetValue(key, out var style) ? style.Pattern : null, dashed, dotted);

    private void Refresh()
    {
        syncing = true;
        try { foreach (var segment in ranges) segment.IsChecked = segment.Tag is int seconds && seconds == session.HistorySeconds; }
        finally { syncing = false; }

        var data = session.Current;
        string id = driveId is not null ? StorageMetrics.Key(driveId, "read") : session.MetricId(chartKind);
        string writeId = driveId is not null ? StorageMetrics.Key(driveId, "write") : "storage/total/write";
        double? current = Live(id);
        double? other = joined ? Live(session.MetricId(chartKind == "cpu" ? "gpu" : "gputemp")) : null;

        bool showFan = session.Settings.ShowGpuFanSpeed && kind is "gputemp" or "temperature";
        fan.Visibility = showFan ? Visibility.Visible : Visibility.Collapsed;
        if (showFan) { Tint(fan, "gpufan"); fan.Text = OverviewPresenter.Rpm("GPU fan", session.Stale, Live(session.MetricId("gpufan")), Pattern("gpufan", dotted: true)); }
        // The joined CPU / GPU temperature pop-out keeps only the GPU fan line.
        bool showPump = session.Settings.ShowCpuPumpSpeed && kind == "cputemp";
        pump.Visibility = showPump ? Visibility.Visible : Visibility.Collapsed;
        if (showPump) { Tint(pump, "cpupump"); pump.Text = OverviewPresenter.Rpm("CPU pump", session.Stale, Live(session.MetricId("cpupump")), Pattern("cpupump", dotted: true)); }

        Tint(value, OverviewPresenter.ColorKey(chartKind));
        Tint(second, joined ? (chartKind == "cpu" ? "gpu" : "gputemp") : kind == "storage" ? "diskwrite" : "upload");
        if (joined)
        {
            string unit = chartKind == "cputemp" ? " °C" : " %";
            value.Text = "CPU " + OverviewPresenter.Number(current, unit);
            second.Text = "GPU " + OverviewPresenter.Number(other, unit);
            ToolTipService.SetToolTip(value, data?.CpuName);
            ToolTipService.SetToolTip(second, session.GpuName);
            details.Text = $"CPU · {data?.CpuName ?? "Unavailable"}\n{Pattern(chartKind)} CPU-colour line\n\nGPU · {session.GpuName}\n{Pattern(chartKind == "cpu" ? "gpu" : "gputemp", dashed: true)} GPU-colour line\n"
                + (showFan ? "CPU / GPU temperatures share the right °C scale." : "Both devices share the same scale.");
        }
        else if (kind == "network")
        {
            double? up = Live("network/upload");
            value.Text = "↓ " + HistoryChartModel.Bytes(current, true);
            second.Text = "↑ " + HistoryChartModel.Bytes(up, true);
            double? localDown = Live("network/download/local");
            local.Visibility = localDown is null ? Visibility.Collapsed : Visibility.Visible;
            if (localDown is not null) local.Text = $"· local ↓ {HistoryChartModel.Bytes(localDown, true)} ↑ {HistoryChartModel.Bytes(session.Value("network/upload/local"), true)}";
            details.Text = $"↓ Download: {Pattern("download")} line\n↑ Upload: {Pattern("upload", dashed: true)} line\nBoth directions share the same scale."
                + (localDown is null ? "" : "\nMuted traces: local-network share (LAN, loopback, link-local) from per-app capture.");
            AutomationProperties.SetName(value, "Download " + HistoryChartModel.Bytes(current, true));
            AutomationProperties.SetName(second, "Upload " + HistoryChartModel.Bytes(up, true));
        }
        else if (kind == "storage")
        {
            double? write = Live(writeId);
            value.Text = "↓ " + HistoryChartModel.Bytes(current, true);
            second.Text = "↑ " + HistoryChartModel.Bytes(write, true);
            var drive = driveId is null ? null : data?.Storage.Drives.FirstOrDefault(candidate => candidate.Id == driveId);
            details.Text = (driveId is null ? "All drives" : drive?.Model ?? "Drive not detected")
                + $"\n↓ Read: {Pattern("diskread")} line\n↑ Write: {Pattern("diskwrite", dashed: true)} line\nBoth directions share the same scale.";
            AutomationProperties.SetName(value, "Read " + HistoryChartModel.Bytes(current, true));
            AutomationProperties.SetName(second, "Write " + HistoryChartModel.Bytes(write, true));
        }
        else if (kind == "memory")
        {
            value.Text = OverviewPresenter.Number(current, " %");
            details.Text = data is null ? "Discovering memory…" : session.Stale ? "RAM readings stale" : current is null ? "RAM usage unavailable"
                : $"{HistoryChartModel.Bytes(session.Value("memory/used"))} / {HistoryChartModel.Bytes(session.Value("memory/total"))} in use";
        }
        else
        {
            value.Text = OverviewPresenter.Number(current, kind.Contains("temp") ? " °C" : " %");
            details.Text = data is null ? "Discovering hardware…" : kind.StartsWith("cpu", StringComparison.Ordinal) ? data.CpuName : session.GpuName;
        }
        if (showFan) details.Text += $"\n\nGPU first fan: {Pattern("gpufan", dotted: true)} line\nSeparate left RPM scale.";
        if (showPump) details.Text += $"\n\nCPU pump: {Pattern("cpupump", dotted: true)} line\nSeparate left RPM scale.";

        bool unavailable = current is null || (joined && other is null) || (kind == "network" && session.Value("network/upload") is null) || (kind == "storage" && session.Value(writeId) is null);
        status.Text = data is null ? "Discovering hardware…" : session.Stale ? "Stale · waiting for fresh readings" : unavailable ? "Reading unavailable" : "";
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
