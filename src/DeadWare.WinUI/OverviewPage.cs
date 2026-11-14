using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// The dashboard: seven fixed cards (CPU and GPU usage and temperature, RAM, network, disk) followed by one Disk card per
/// present drive, in one to three columns by width. The graphics card and the shared history range are chosen above them.
/// </summary>
internal sealed class OverviewPage : Page
{
    private readonly MonitorSession session;
    private readonly Grid cards = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly List<OverviewCard> fixedCards = [], driveCards = [];
    private readonly ScrollViewer scroller = new();
    private readonly ComboBox gpu = new() { MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel gpuControls;
    private readonly List<ToggleButton> ranges = [];
    private readonly TextBlock memory = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border footer;
    private string[] driveIds = [], gpuIds = [];
    private int columns = 2;
    private bool syncing;

    public OverviewPage(MonitorSession session)
    {
        this.session = session;
        AutomationProperties.SetName(gpu, "Graphics card");
        gpu.SelectionChanged += (_, _) =>
        {
            if (!syncing && gpu.SelectedIndex >= 0 && gpu.SelectedIndex < gpuIds.Length) session.SetGpu(gpuIds[gpu.SelectedIndex]);
        };
        gpuControls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed,
            Children = { new TextBlock { Text = "GPU", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center }, gpu },
        };

        // The shared graph history range: one setting that every chart follows.
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(rangeRow, "Graph history range");
        ToolTipService.SetToolTip(rangeRow, "Choose how much graph history to show");
        foreach (int seconds in HistoryRanges.Seconds)
        {
            string label = HistoryRanges.Label(seconds);
            var segment = new ToggleButton { Content = label, Tag = seconds, Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
            AutomationProperties.SetName(segment, label + " history");
            segment.Checked += (_, _) => { if (!syncing) session.SetRange(seconds); };
            // A segment never unchecks itself; the next choice replaces it.
            segment.Unchecked += (_, _) => { if (!syncing && session.HistorySeconds == seconds) segment.IsChecked = true; };
            ranges.Add(segment);
            rangeRow.Children.Add(segment);
        }
        var toolbar = new Grid();
        toolbar.Children.Add(gpuControls);
        var widget = new Button { Content = "Open widget", VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(widget, "Open the Quick glance widget in its own window");
        widget.Click += (_, _) => App.Popouts?.Open("widget");
        rangeRow.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Children = { rangeRow, widget } });

        foreach (string kind in OverviewPresenter.Kinds) fixedCards.Add(new OverviewCard(session, kind, null));

        footer = Pages.Card(new StackPanel
        {
            Spacing = 3, Padding = new Thickness(8, 2, 8, 2),
            Children = { memory, new TextBlock { Text = "— indicates an unavailable reading.", Opacity = 0.7, FontSize = 11 }, status },
        });

        scroller.Content = new StackPanel { Padding = new Thickness(24, 12, 24, 16), Spacing = 12, Children = { toolbar, cards, footer } };
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = scroller;

        // Charts take each card's star row, so growing the grid into spare height enlarges the plots; the floor makes short windows scroll instead.
        SizeChanged += (_, _) => Arrange();
        footer.SizeChanged += (_, _) => Fit();
        toolbar.SizeChanged += (_, _) => Fit();
        this.WhileLoaded(() => { session.Changed += Refresh; Refresh(); Arrange(); }, () => session.Changed -= Refresh);
        SyncDrives();
    }

    private IEnumerable<OverviewCard> AllCards => fixedCards.Concat(driveCards);

    private void Refresh()
    {
        SyncDrives();
        SyncToolbar();
        var reading = session.Current;
        memory.Text = reading is null ? "Memory —" : OverviewPresenter.Footer(session.Value("memory/used"), session.Value("memory/total"), reading.Sample.Timestamp.ToLocalTime());
        string administrator = Collector.IsElevated ? "" : "\nNot running as administrator, so some sensors and per-app capture are unavailable.";
        status.Text = OverviewPresenter.Status(reading is not null, session.Stale, reading?.HardwareStatus ?? "", reading?.NetworkStatus ?? "", session.GraphHistory.Status,
            session.SettingsStatus, session.Settings.ShowCpuCoreUsage, reading?.CpuCoreCount ?? 0) + administrator + $"\nVersion {App.Version}.";
        foreach (var card in AllCards) card.Refresh();
    }

    /// <summary>Reflects the selected graphics card and history range without echoing them back to the session.</summary>
    private void SyncToolbar()
    {
        syncing = true;
        try
        {
            foreach (var segment in ranges) segment.IsChecked = segment.Tag is int seconds && seconds == session.HistorySeconds;
            var devices = session.Current?.Gpus ?? [];
            var ids = devices.Select(device => device.Id).ToArray();
            if (!ids.SequenceEqual(gpuIds, StringComparer.Ordinal))
            {
                gpuIds = ids;
                gpu.Items.Clear();
                foreach (var device in devices) gpu.Items.Add(device.Name);
            }
            gpuControls.Visibility = gpuIds.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            int selected = Array.IndexOf(gpuIds, session.SelectedGpuId);
            if (gpu.SelectedIndex != selected) gpu.SelectedIndex = selected;
        }
        finally { syncing = false; }
    }

    /// <summary>One card per present drive after the fixed cards; the set is rebuilt only when the drive ids change.</summary>
    private void SyncDrives()
    {
        var drives = (session.Current?.Storage.Drives ?? []).OrderBy(drive => drive.Index).ToList();
        var ids = drives.Select(drive => drive.Id).ToArray();
        if (ids.SequenceEqual(driveIds, StringComparer.Ordinal) && cards.Children.Count > 0) return;
        var reuse = OverviewPresenter.Reuse(driveIds, ids);
        var next = new List<OverviewCard>();
        for (int i = 0; i < ids.Length; i++) next.Add(reuse[i] is { } kept ? driveCards[kept] : new OverviewCard(session, "storage", ids[i]));
        driveIds = ids;
        driveCards.Clear();
        driveCards.AddRange(next);
        Place();
    }

    /// <summary>Picks the column count from the width, then lays the cards out again if it changed.</summary>
    private void Arrange()
    {
        int wanted = OverviewPresenter.Columns(ActualWidth);
        if (wanted != columns) { columns = wanted; Place(); }
        else Fit();
    }

    /// <summary>Puts every card in its cell, left to right, top to bottom.</summary>
    private void Place()
    {
        var all = AllCards.ToList();
        // Cards that stay are only moved to their new cell. Taking one out and putting it back makes WinUI raise Unloaded
        // after Loaded, which leaves its Win2D canvases believing they are off screen: they then never redraw.
        foreach (var gone in cards.Children.Except(all).ToList()) cards.Children.Remove(gone);
        cards.ColumnDefinitions.Clear();
        cards.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) cards.ColumnDefinitions.Add(new ColumnDefinition());
        int rows = OverviewPresenter.Rows(all.Count, columns);
        for (int i = 0; i < rows; i++) cards.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < all.Count; i++)
        {
            Grid.SetRow(all[i], i / columns);
            Grid.SetColumn(all[i], i % columns);
            if (!cards.Children.Contains(all[i])) cards.Children.Add(all[i]);
        }
        Fit();
    }

    /// <summary>Sizes the card grid to its rows, growing into spare viewport height.</summary>
    private void Fit()
    {
        int count = fixedCards.Count + driveCards.Count;
        int rows = OverviewPresenter.Rows(count, columns);
        // Everything in the page other than the cards: the toolbar, the footer, the paddings and the spacing between them and between rows.
        double chrome = footer.ActualHeight + 12 + 16 + 12 * 2 + 40 + (rows - 1) * cards.RowSpacing;
        double viewport = ActualHeight > 0 ? ActualHeight : double.NaN;
        double height = OverviewPresenter.GridHeight(count, columns, viewport, chrome) + (rows - 1) * cards.RowSpacing;
        if (double.IsNaN(cards.Height) || Math.Abs(cards.Height - height) >= 1) cards.Height = height;
    }
}

/// <summary>One Overview card: title, the current reading in its trace's colour, MIN / AVG / MAX over the range, a device line and the chart.</summary>
internal sealed class OverviewCard : UserControl
{
    private readonly MonitorSession session;
    private readonly string kind;
    private readonly string? driveId;
    private readonly HistoryChartView chart;
    private readonly TextBlock title = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock value = Reading(28), upload = Reading(26);
    private readonly TextBlock subtitle = Line(), fan = Line(), pump = Line();
    private readonly TextBlock[] statistics = new TextBlock[3];
    private readonly Border tile = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Opacity = 0.9 };
    private readonly BusiestDrive busiest = new();
    private readonly Dictionary<TextBlock, Windows.UI.Color> tints = [];

    public OverviewCard(MonitorSession session, string kind, string? driveId)
    {
        this.session = session;
        this.kind = kind;
        this.driveId = kind == "storage" ? driveId : null;
        chart = new HistoryChartView(session, kind) { DriveId = this.driveId };
        chart.StatisticsReady += ShowStatistics;
        title.Text = this.driveId is not null ? "Disk" : OverviewPresenter.Title(kind);
        ToolTipService.SetToolTip(title, OverviewPresenter.Title(kind));

        var header = new Grid { ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(tile);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        // Pop this graph out into its own window; a drive card opens that drive's window.
        var popOut = new Button
        {
            Content = new FontIcon { Glyph = "\uE8A7", FontSize = 12 }, Padding = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        string popOutName = this.driveId is not null ? "Pop out this drive" : "Open in window";
        ToolTipService.SetToolTip(popOut, popOutName);
        AutomationProperties.SetName(popOut, this.driveId is not null ? popOutName : $"Open {OverviewPresenter.Title(kind)} in a window");
        popOut.Click += (_, _) =>
        {
            if (this.driveId is null) { App.Popouts?.Open(kind); return; }
            var drive = session.Current?.Storage.Drives.FirstOrDefault(candidate => candidate.Id == this.driveId);
            if (drive is not null) App.Popouts?.OpenDrive(drive);
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(popOut, 2);
        header.Children.Add(popOut);

        // MIN / AVG / MAX share the reading's line, so the chart's star row takes the height a footer would.
        var statsLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom };
        string[] labels = ["MIN", "AVG", "MAX"];
        for (int i = 0; i < 3; i++)
        {
            statistics[i] = new TextBlock { Text = "—", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom };
            statsLine.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children = { Caption((kind == "network" ? "↓ " : "") + labels[i]), statistics[i] },
            });
        }

        var metric = new StackPanel { Margin = new Thickness(0, 5, 0, 5), Spacing = 1 };
        if (kind == "network")
        {
            ToolTipService.SetToolTip(statsLine, "Download statistics over the selected history range");
            value.FontSize = 26;
            var rates = new Grid { ColumnSpacing = 8 };
            rates.ColumnDefinitions.Add(new ColumnDefinition());
            rates.ColumnDefinitions.Add(new ColumnDefinition());
            var down = new StackPanel { Children = { Caption("DOWNLOAD"), value } };
            var up = new StackPanel { Children = { Caption("UPLOAD"), upload } };
            Grid.SetColumn(up, 1);
            rates.Children.Add(down);
            rates.Children.Add(up);
            metric.Children.Add(rates);
            metric.Children.Add(statsLine);
            subtitle.FontSize = 10;
        }
        else
        {
            statsLine.Margin = new Thickness(14, 0, 0, 5);
            metric.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { value, statsLine } });
        }
        metric.Children.Add(subtitle);
        fan.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(fan, "Selected GPU's first reported fan · separate left RPM scale");
        metric.Children.Add(fan);
        pump.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(pump, "CPU cooler pump, or the CPU Fan header · separate left RPM scale");
        metric.Children.Add(pump);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(metric, 1);
        Grid.SetRow(chart, 2);
        layout.Children.Add(header);
        layout.Children.Add(metric);
        layout.Children.Add(chart);
        var card = Pages.Card(layout);
        card.Padding = new Thickness(16, 13, 16, 11);
        Content = card;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    private static TextBlock Reading(double size) => new() { Text = "—", FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };

    private static TextBlock Line() => new() { Text = "", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 9.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };

    /// <summary>Colours a text with a graph colour, replacing its brush only when the colour changed.</summary>
    private void Tint(TextBlock text, string key)
    {
        var color = chart.ColorOf(key);
        if (tints.TryGetValue(text, out var shown) && shown.Equals(color)) return;
        tints[text] = color;
        text.Foreground = new SolidColorBrush(color);
    }

    private double? Live(string id) => session.Stale ? null : session.Value(id);

    private string? SavedPattern(string key) => session.Settings.GraphStyles.TryGetValue(key, out var style) ? style.Pattern : null;

    /// <summary>Rewrites the card's texts from the latest reading. The chart redraws itself.</summary>
    public void Refresh()
    {
        var data = session.Current;
        string id = driveId is not null ? StorageMetrics.Key(driveId, "read") : session.MetricId(kind);
        double? current = Live(id);
        Tint(value, OverviewPresenter.ColorKey(kind));
        var tileColor = chart.ColorOf(OverviewPresenter.ColorKey(kind));
        var tileFill = Windows.UI.Color.FromArgb(36, tileColor.R, tileColor.G, tileColor.B);
        if (tile.Background is not SolidColorBrush brush || !brush.Color.Equals(tileFill)) tile.Background = new SolidColorBrush(tileFill);

        bool showFan = kind == "gputemp" && session.Settings.ShowGpuFanSpeed;
        fan.Visibility = showFan ? Visibility.Visible : Visibility.Collapsed;
        if (showFan)
        {
            Tint(fan, "gpufan");
            fan.Text = OverviewPresenter.Rpm("GPU fan", session.Stale, Live(session.MetricId("gpufan")), OverviewPresenter.Pattern(SavedPattern("gpufan"), dotted: true));
        }
        bool showPump = kind == "cputemp" && session.Settings.ShowCpuPumpSpeed;
        pump.Visibility = showPump ? Visibility.Visible : Visibility.Collapsed;
        if (showPump)
        {
            Tint(pump, "cpupump");
            pump.Text = OverviewPresenter.Rpm("CPU pump", session.Stale, Live(session.MetricId("cpupump")), OverviewPresenter.Pattern(SavedPattern("cpupump"), dotted: true));
        }

        value.Text = OverviewPresenter.Value(kind, current);
        if (kind == "network")
        {
            Tint(upload, "upload");
            upload.Text = OverviewPresenter.Value(kind, Live("network/upload"));
            subtitle.Text = OverviewPresenter.NetworkSubtitle(SavedPattern("download"), SavedPattern("upload"));
        }
        else if (kind == "storage")
        {
            var drives = data?.Storage.Drives ?? [];
            if (driveId is not null)
            {
                // The model is the title, so the line below carries the bus, size and write rate.
                var drive = drives.FirstOrDefault(candidate => candidate.Id == driveId);
                title.Text = drive?.Model ?? "Disk";
                ToolTipService.SetToolTip(title, title.Text);
                subtitle.Text = OverviewPresenter.DriveSubtitle(drive, Live(StorageMetrics.Key(driveId, "write")));
            }
            else
            {
                string? model = drives.Count == 0 ? null
                    : busiest.Pick(drives, drive => (session.Value(StorageMetrics.Key(drive.Id, "read")) ?? 0) + (session.Value(StorageMetrics.Key(drive.Id, "write")) ?? 0)).Model;
                subtitle.Text = OverviewPresenter.StorageSubtitle(data is not null, drives.Count, model);
            }
        }
        else if (kind == "memory")
            subtitle.Text = OverviewPresenter.MemorySubtitle(data is not null, session.Stale, current is not null, session.Value("memory/used"), session.Value("memory/total"));
        else
            subtitle.Text = OverviewPresenter.DeviceSubtitle(kind, data is not null, data?.CpuName ?? "", session.GpuName,
                session.Settings.ShowCpuCoreUsage ? data?.CpuCoreCount ?? 0 : 0, current is not null, session.Stale);
        ToolTipService.SetToolTip(subtitle, subtitle.Text);
    }

    private void ShowStatistics(Summary summary)
    {
        double?[] values = [summary.Minimum, summary.Average, summary.Maximum];
        for (int i = 0; i < statistics.Length; i++) statistics[i].Text = OverviewPresenter.Statistic(kind, values[i]);
    }
}
