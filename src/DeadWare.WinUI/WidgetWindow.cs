using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeadWare.WinUI;

/// <summary>
/// The desktop widget ("Quick glance"): the graphs chosen in Settings stacked in one narrow window, optionally with the
/// live connections map below them. It follows the Widget always on top setting.
/// </summary>
internal sealed class WidgetWindow : Window
{
    private readonly MonitorSession session;
    private readonly Dictionary<string, WidgetGraph> graphs = new(StringComparer.Ordinal);
    private readonly Grid cards = new() { RowSpacing = 6 };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock empty = new()
    {
        Text = "No graphs selected. Choose graphs in Settings.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private WidgetMap? map;
    private string? applied;
    private uint dpi = 96;

    public WidgetWindow(MonitorSession session, WindowManager manager)
    {
        this.session = session;
        Title = "DeadWare · Quick glance";
        int count = WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs).Count;
        bool showMap = session.Settings.WidgetConnectionsMap;
        var root = new Grid { Padding = new Thickness(8) };
        Content = root;
        // With the map the cards scroll, so the window may be shorter than its content; without it every graph must fit.
        manager.Place(this, root, "widget", 340, WidgetGraphSelection.DefaultHeight(count, showMap), 300, showMap ? 210 : WidgetGraphSelection.MinimumHeight(count));
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (NativePlacement.CaptureNormal(handle, false) is { } placement) dpi = NativePlacement.DpiFor(placement);

        foreach (string kind in WidgetGraphSelection.Kinds)
        {
            cards.RowDefinitions.Add(new RowDefinition());
            var graph = new WidgetGraph(session, kind);
            graphs.Add(kind, graph);
            Grid.SetRow(graph, cards.RowDefinitions.Count - 1);
            cards.Children.Add(graph);
        }
        cards.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        scroll.Content = cards;
        // The cards fill the window when they fit and scroll when they do not.
        scroll.SizeChanged += (_, _) => FitCards();
        root.Children.Add(scroll);
        root.Children.Add(empty);

        session.Changed += Refresh;
        Closed += (_, _) => session.Changed -= Refresh;
        Refresh();
    }

    private double MinimumCardsHeight { get; set; }

    private void FitCards()
    {
        double height = Math.Max(MinimumCardsHeight, scroll.ActualHeight);
        if (height > 0 && (double.IsNaN(cards.Height) || Math.Abs(cards.Height - height) >= 1)) cards.Height = height;
    }

    private void Refresh()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != session.Settings.WidgetAlwaysOnTop)
            presenter.IsAlwaysOnTop = session.Settings.WidgetAlwaysOnTop;
        var selected = WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs);
        bool showMap = session.Settings.WidgetConnectionsMap;
        string signature = string.Join('|', selected) + "|map=" + showMap;
        if (signature != applied)
        {
            applied = signature;
            if (showMap && map is null)
            {
                map = new WidgetMap(session);
                Grid.SetRow(map, cards.RowDefinitions.Count - 1);
                cards.Children.Add(map);
            }
            if (map is not null) map.Visibility = showMap ? Visibility.Visible : Visibility.Collapsed;
            bool mapOnly = showMap && selected.Count == 0;
            cards.RowDefinitions[^1].Height = mapOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(showMap ? 150 : 0);
            scroll.VerticalScrollBarVisibility = showMap && !mapOnly ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            foreach (var (kind, graph) in graphs)
            {
                bool visible = selected.Contains(kind);
                graph.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                var row = cards.RowDefinitions[Grid.GetRow(graph)];
                row.MinHeight = showMap && visible ? 120 : 0;
                row.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
            empty.Visibility = selected.Count == 0 && !showMap ? Visibility.Visible : Visibility.Collapsed;
            MinimumCardsHeight = showMap && !mapOnly ? 120 * selected.Count + 150 + cards.RowSpacing * selected.Count : 0;
            FitCards();
            if (AppWindow.Presenter is OverlappedPresenter sized)
            {
                // A larger selection raises the minimum; the window grows to it but is never shrunk on the user's behalf.
                double minimum = showMap ? 210 : WidgetGraphSelection.MinimumHeight(selected.Count);
                int pixels = (int)Math.Round(minimum * dpi / 96.0);
                sized.PreferredMinimumHeight = pixels;
                if (AppWindow.Size.Height < pixels) AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, pixels));
            }
        }
        foreach (var graph in graphs.Values) if (graph.Visibility == Visibility.Visible) graph.Refresh();
        if (showMap) map!.Refresh();
    }
}

/// <summary>Compact readings and traces over the shared history: a title, the readings as a legend, and the chart.</summary>
internal sealed class WidgetGraph : UserControl
{
    private readonly MonitorSession session;
    private readonly string kind;
    private readonly HistoryChartView chart;
    private readonly TextBlock title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock primary = Reading(), secondary = Reading(), local = Reading(), fan = Reading(), pump = Reading();
    private readonly StackPanel fanLegend, pumpLegend;
    private readonly List<(Line Line, TextBlock Text, string Key)> legends = [];
    private readonly Dictionary<string, Windows.UI.Color> shown = new(StringComparer.Ordinal);

    public WidgetGraph(MonitorSession session, string kind)
    {
        this.session = session;
        this.kind = kind;
        bool network = kind == "network", memory = kind == "memory", disk = kind == "disk";
        chart = new HistoryChartView(session, disk ? "storage" : kind, kind is "cpu" or "cputemp") { PumpAndCoresWithDevices = true };
        string first = disk ? "diskread" : network ? "download" : kind;
        string second = network ? "upload" : disk ? "diskwrite" : kind == "cputemp" ? "gputemp" : "gpu";
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { Legend(primary, first, false, false) } };
        if (!memory) legend.Children.Add(Legend(secondary, second, true, false));
        if (network)
        {
            local.Visibility = Visibility.Collapsed;
            local.Opacity = 0.7;
            ToolTipService.SetToolTip(local, "Local-network share: LAN, loopback and link-local peers, from per-app capture");
            legend.Children.Add(local);
        }
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(legend, 1);
        header.Children.Add(legend);

        fanLegend = Legend(fan, "gpufan", false, true);
        pumpLegend = Legend(pump, "cpupump", false, true);
        fanLegend.Visibility = pumpLegend.Visibility = Visibility.Collapsed;
        // One under the other: side by side they are wider than the widget and the pump's line was cut off.
        var rpm = new StackPanel { Spacing = 1, Margin = new Thickness(0, 3, 0, 0), Children = { fanLegend, pumpLegend } };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(rpm, 1);
        Grid.SetRow(chart, 2);
        layout.Children.Add(header);
        layout.Children.Add(rpm);
        layout.Children.Add(chart);
        chart.MinHeight = 40;
        var card = Pages.Card(layout);
        card.Padding = new Thickness(10, 6, 10, 5);
        Content = card;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    private static TextBlock Reading() => new() { FontSize = 11, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A short sample of the trace's line beside its reading.</summary>
    private StackPanel Legend(TextBlock text, string key, bool dashed, bool dotted)
    {
        var line = new Line { X1 = 0, Y1 = 5, X2 = 14, Y2 = 5, Width = 14, Height = 10, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
        if (dotted) line.StrokeDashArray = new DoubleCollection { 1, 3 };
        else if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 2 };
        legends.Add((line, text, key));
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { line, text } };
    }

    private double? Live(string id) => session.Stale ? null : session.Value(id);

    private string Pattern(string key, bool dashed = false, bool dotted = false) =>
        OverviewPresenter.Pattern(session.Settings.GraphStyles.TryGetValue(key, out var style) ? style.Pattern : null, dashed, dotted);

    public void Refresh()
    {
        foreach (var (line, text, key) in legends)
        {
            var color = chart.ColorOf(key);
            if (shown.TryGetValue(key, out var previous) && previous.Equals(color)) continue;
            shown[key] = color;
            var brush = new SolidColorBrush(color);
            line.Stroke = brush;
            text.Foreground = brush;
        }
        bool showFan = kind == "cputemp" && session.Settings.ShowGpuFanSpeed;
        fanLegend.Visibility = showFan ? Visibility.Visible : Visibility.Collapsed;
        if (showFan)
        {
            fan.Text = OverviewPresenter.Rpm("GPU fan", session.Stale, Live(session.MetricId("gpufan")), Pattern("gpufan", dotted: true));
            ToolTipService.SetToolTip(fanLegend, "Selected GPU's first reported fan · separate left RPM scale");
        }
        bool showPump = kind == "cputemp" && session.Settings.ShowCpuPumpSpeed;
        pumpLegend.Visibility = showPump ? Visibility.Visible : Visibility.Collapsed;
        if (showPump)
        {
            pump.Text = OverviewPresenter.Rpm("CPU pump", session.Stale, Live(session.MetricId("cpupump")), Pattern("cpupump", dotted: true));
            ToolTipService.SetToolTip(pumpLegend, "CPU cooler pump, or the CPU Fan header · " + (showFan ? "shares the left RPM scale with the GPU fan" : "separate left RPM scale"));
        }

        if (kind == "network")
        {
            title.Text = "Network";
            ToolTipService.SetToolTip(title, $"Network download ({Pattern("download")}) and upload ({Pattern("upload", dashed: true)}), in bytes per second");
            primary.Text = "↓ " + HistoryChartModel.Bytes(Live("network/download"), true);
            secondary.Text = "↑ " + HistoryChartModel.Bytes(Live("network/upload"), true);
            double? localDown = Live("network/download/local");
            local.Visibility = localDown is null ? Visibility.Collapsed : Visibility.Visible;
            if (localDown is not null) local.Text = $"· local ↓ {HistoryChartModel.Bytes(localDown, true)} ↑ {HistoryChartModel.Bytes(session.Value("network/upload/local"), true)}";
        }
        else if (kind == "disk")
        {
            title.Text = "Disk";
            ToolTipService.SetToolTip(title, $"Disk read ({Pattern("diskread")}) and write ({Pattern("diskwrite", dashed: true)}) across all drives, in bytes per second");
            primary.Text = HistoryChartModel.Bytes(Live(session.MetricId("storage")), true) + " ↓";
            secondary.Text = HistoryChartModel.Bytes(Live("storage/total/write"), true) + " ↑";
        }
        else if (kind == "memory")
        {
            title.Text = "RAM";
            ToolTipService.SetToolTip(title, "Physical memory usage (%)");
            double? current = Live(session.MetricId("memory"));
            primary.Text = OverviewPresenter.Number(current, " %");
            if (current.HasValue) primary.Text += $" · {HistoryChartModel.Bytes(session.Value("memory/used"))} / {HistoryChartModel.Bytes(session.Value("memory/total"))}";
        }
        else
        {
            bool thermal = kind == "cputemp";
            string unit = thermal ? " °C" : " %";
            title.Text = thermal ? "Temp" : "Usage";
            ToolTipService.SetToolTip(title, thermal ? "CPU and selected GPU temperature (°C)" : "CPU and selected GPU usage (%)");
            primary.Text = "CPU " + OverviewPresenter.Number(Live(session.MetricId(kind)), unit);
            secondary.Text = "GPU " + OverviewPresenter.Number(Live(session.MetricId(thermal ? "gputemp" : "gpu")), unit);
            ToolTipService.SetToolTip(primary, session.Current?.CpuName);
            ToolTipService.SetToolTip(secondary, session.GpuName);
        }
    }
}

/// <summary>The widget's live connections map: no filters, replay or history, and a button that switches between the world and North America.</summary>
internal sealed class WidgetMap : Grid
{
    private readonly MonitorSession session;
    private readonly ConnectionMapView map;
    private readonly Button region = new()
    {
        Content = "NA", MinWidth = 32, Height = 24, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(6), FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
    };
    private bool? previousNorthAmerica;
    private bool loaded;

    public WidgetMap(MonitorSession session)
    {
        this.session = session;
        map = new ConnectionMapView(session) { Flat = true, MapOnly = true, AnimationEnabled = false };
        ToolTipService.SetToolTip(map, "Drag to move · wheel or +/− to zoom · Home to reset this region");
        map.ResetView();
        Children.Add(map);
        Children.Add(region);
        region.Click += (_, _) => { session.SetWidgetConnectionsNorthAmerica(!session.Settings.WidgetConnectionsNorthAmerica); Refresh(); };
        this.WhileLoaded(() => { loaded = true; Refresh(); }, () => { loaded = false; map.AnimationEnabled = false; });
    }

    /// <summary>The app's name, with " · not recorded" when it is in Don't record these apps.</summary>
    private string Marked(string name) => session.IsExcluded(name) ? name + ProcessCopy.NotRecordedSuffix : name;

    public void Refresh()
    {
        var live = session.Connections.Live;
        string status = live.Status + (!live.Complete ? " · Incomplete capture" : "");
        var points = live.Connections.Where(c => c.Location?.Point is { } point && ConnectionProjection.IsValid(point))
            .Select(c => new ConnectionMapPoint($"{c.Traffic.Peer.Process}:{c.Traffic.Peer.RemoteAddress}:{c.Traffic.Peer.RemotePort}:{c.Traffic.Peer.Protocol}",
                c.Location!.Point!, c.Traffic.ReceivedBytes, c.Traffic.SentBytes, false, live.Available && c.Traffic.DownloadBytesPerSecond > 0, live.Available && c.Traffic.UploadBytesPerSecond > 0,
                Label: Marked(c.Traffic.Peer.Process.Name))).ToArray();
        map.SetData(points, live.Origin, live.Available, status);
        bool northAmerica = session.Settings.WidgetConnectionsNorthAmerica;
        if (previousNorthAmerica != northAmerica)
        {
            previousNorthAmerica = northAmerica;
            if (northAmerica) map.SetView(new GeoPoint(43, -103), 2.5); else map.ResetView();
            ToolTipService.SetToolTip(region, northAmerica ? "Return to World view" : "Zoom to North America");
            AutomationProperties.SetName(region, northAmerica ? "North America view. Switch to World view." : "World view. Zoom to North America.");
            region.Style = northAmerica ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
        }
        bool animate = loaded && Visibility == Visibility.Visible && session.Settings.ConnectionsAnimation;
        if (map.AnimationEnabled != animate) map.AnimationEnabled = animate;
    }
}
