using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeadWare.WinUI;

/// <summary>
/// The dashboard: seven fixed cards (CPU and GPU usage and temperature, RAM, network, disk) followed by one Disk card per
/// present drive, in one to three columns by width. The graphics card choice sits in the window's header, beside the shared
/// history range, so the cards start right under it.
/// </summary>
internal sealed class OverviewPage : Page, IHeaderTool
{
    private readonly MonitorSession session;
    private readonly Grid cards = new() { ColumnSpacing = 10, RowSpacing = 10 };
    private readonly List<OverviewCard> fixedCards = [], driveCards = [];
    private readonly ScrollViewer scroller = new();
    private readonly StackPanel content;
    private readonly ComboBox gpu = new() { MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel gpuControls;
    private readonly TextBlock memory = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Border footer;
    private string[] driveIds = [], gpuIds = [];
    private int columns = 2;
    private bool syncing;
    // The colour flyout every card's dots share, made the first time a dot is clicked.
    private LineStyleFlyout? colours;

    public OverviewPage(MonitorSession session)
    {
        this.session = session;
        AutomationProperties.SetName(gpu, "Graphics card");
        gpu.SelectionChanged += (_, _) =>
        {
            if (!syncing && gpu.SelectedIndex >= 0 && gpu.SelectedIndex < gpuIds.Length) session.SetGpu(gpuIds[gpu.SelectedIndex]);
        };
        // Shown only when there is more than one card to choose from.
        gpuControls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "GPU", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center }, gpu },
        };

        Func<LineStyleFlyout> flyout = () => colours ??= new LineStyleFlyout(session);
        foreach (string kind in OverviewPresenter.Kinds) fixedCards.Add(new OverviewCard(session, kind, null, flyout));

        footer = Pages.Card(new StackPanel
        {
            Spacing = 3, Padding = new Thickness(8, 2, 8, 2),
            Children = { memory, new TextBlock { Text = "— indicates an unavailable reading.", Opacity = 0.7, FontSize = 11 }, status },
        });

        // Narrow margins: the window's header holds the page's controls, so the cards take the rest of the width and height.
        content = new StackPanel { Padding = new Thickness(16, 4, 16, 12), Spacing = 10, Children = { cards, footer } };
        scroller.Content = content;
        scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Content = scroller;

        // Charts take each card's star row, so growing the grid into spare height enlarges the plots; the floor makes short windows scroll instead.
        SizeChanged += (_, _) => Arrange();
        footer.SizeChanged += (_, _) => Fit();
        this.WhileShowing(() => { session.Changed += Refresh; Refresh(); Arrange(); }, () => session.Changed -= Refresh);
        SyncDrives();
    }

    /// <summary>The graphics card choice, which the window puts in its header while this page is shown.</summary>
    public UIElement HeaderTool => gpuControls;

    private IEnumerable<OverviewCard> AllCards => fixedCards.Concat(driveCards);

    private void Refresh()
    {
        SyncDrives();
        SyncGpus();
        var reading = session.Current;
        memory.Text = reading is null ? "Memory —" : OverviewPresenter.Footer(session.Value("memory/used"), session.Value("memory/total"), reading.Sample.Timestamp.ToLocalTime());
        string administrator = Collector.IsElevated ? "" : "\nNot running as administrator, so some sensors and per-app capture are unavailable.";
        status.Text = OverviewPresenter.Status(reading is not null, session.Stale, reading?.HardwareStatus ?? "", reading?.NetworkStatus ?? "", session.GraphHistory.Status,
            session.SettingsStatus, session.Settings.ShowCpuCoreUsage, reading?.CpuCoreCount ?? 0) + administrator + $"\nVersion {App.Version}.";
        foreach (var card in AllCards) card.Refresh();
    }

    /// <summary>Reflects the graphics cards present and the selected one without echoing the choice back to the session.</summary>
    private void SyncGpus()
    {
        syncing = true;
        try
        {
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
        // Everything in the page other than the cards: the footer, the paddings, the spacing between the cards and the footer, and between rows.
        double chrome = footer.ActualHeight + content.Padding.Top + content.Padding.Bottom + content.Spacing + (rows - 1) * cards.RowSpacing;
        double viewport = ActualHeight > 0 ? ActualHeight : double.NaN;
        double height = OverviewPresenter.GridHeight(count, columns, viewport, chrome) + (rows - 1) * cards.RowSpacing;
        if (double.IsNaN(cards.Height) || Math.Abs(cards.Height - height) >= 1) cards.Height = height;
    }
}

/// <summary>
/// One Overview card: a header with the title and a device, drive or legend line after it; the current reading in its trace's
/// colour with MIN / AVG / MAX over the range (and a temperature card's fan or pump speed) on the same line; then the chart.
/// </summary>
internal sealed class OverviewCard : UserControl
{
    private readonly MonitorSession session;
    private readonly string kind;
    private readonly string? driveId;
    private readonly HistoryChartView chart;
    private readonly TextBlock title = new()
    {
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    // The device, drive or legend line in the header's spare width: smaller and muted, on the title's baseline, and the first to
    // trim when the card is narrow. Its full text is its tooltip.
    private readonly TextBlock subtitle = new()
    {
        Text = "", FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 0),
    };
    private readonly TextBlock value = Reading(22), upload = Reading(20);
    private readonly TextBlock[] statistics = new TextBlock[3];
    // A temperature card's fan (GPU) or pump (CPU) speed, after MIN / AVG / MAX while its line is drawn; null on other cards.
    private readonly TextBlock rpm = new() { Text = "—", FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly StackPanel? rpmItem;
    // Lighting 5: the CPU temperature card's coolant legend, beside the pump's while the option is ticked.
    private readonly TextBlock coolant = new() { Text = "—", FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
    private readonly Border tile = new() { Width = 24, Height = 24, CornerRadius = new CornerRadius(7) };
    private readonly Microsoft.UI.Xaml.Shapes.Path glyph = new()
    {
        Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.35, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };
    // A wash of the trace colour from the top left corner, a little stronger under the pointer.
    private readonly Border wash = new() { CornerRadius = new CornerRadius(8), Opacity = RestingWash, IsHitTestVisible = false, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } };
    private const double RestingWash = 0.035, HoveredWash = 0.065;
    private readonly BusiestDrive busiest = new();
    private readonly Dictionary<TextBlock, Windows.UI.Color> tints = [];
    private readonly Dictionary<FrameworkElement, (ToolTip Tip, string Text)> tips = [];
    // The network rates last shown, so their texts and spoken names are rewritten only when they change.
    private string shownDownload = "", shownUpload = "";
    private readonly LineStyleDot[] dots = [];

    /// <param name="colours">The colour flyout a page shares among its cards; a card without one makes its own when a dot is first clicked.</param>
    public OverviewCard(MonitorSession session, string kind, string? driveId, Func<LineStyleFlyout>? colours = null)
    {
        this.session = session;
        this.kind = kind;
        this.driveId = kind == "storage" ? driveId : null;
        chart = new HistoryChartView(session, kind) { DriveId = this.driveId };
        chart.StatisticsReady += ShowStatistics;
        title.Text = this.driveId is not null ? "Disk" : OverviewPresenter.Title(kind);
        Tip(title, OverviewPresenter.Title(kind));

        glyph.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), GlyphData(kind));
        tile.Child = glyph;
        var header = new Grid { ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(tile);
        var names = new TitleLine(title, subtitle) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(names, 1);
        header.Children.Add(names);
        // One colour dot per trace between the title and the pop-out button, as in the WPF app. Drive cards share the Disk
        // card's read and write colours, so they carry none.
        if (this.driveId is null)
        {
            LineStyleFlyout? own = null;
            var flyout = colours ?? (() => own ??= new LineStyleFlyout(session));
            dots = [.. LineStyleDot.Keys(kind).Select(key => new LineStyleDot(session, key, flyout))];
            // The 26 px dots overhang the 24 px glyph tile by a pixel above and below, so the header keeps its height and
            // the chart lines up with a drive card's beside it.
            var dotRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, -1) };
            foreach (var dot in dots) dotRow.Children.Add(dot.Button);
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(dotRow, 2);
            header.Children.Add(dotRow);
        }
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
        Grid.SetColumn(popOut, header.ColumnDefinitions.Count - 1);
        header.Children.Add(popOut);

        // MIN / AVG / MAX share the reading's line, so the chart's star row takes the height a footer would.
        var statsLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Bottom };
        string[] labels = ["MIN", "AVG", "MAX"];
        for (int i = 0; i < 3; i++)
        {
            statistics[i] = new TextBlock { Text = "—", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom };
            statsLine.Children.Add(Item((kind == "network" ? "↓ " : "") + labels[i], statistics[i]));
        }
        // A temperature card's fan or pump speed joins them as one more item rather than taking a line of its own.
        if (kind is "gputemp" or "cputemp")
        {
            rpmItem = Item(kind == "gputemp" ? "FAN" : "PUMP", rpm);
            rpmItem.Visibility = Visibility.Collapsed;
            statsLine.Children.Add(rpmItem);
            if (kind == "cputemp")
            {
                coolant.Visibility = Visibility.Collapsed;
                statsLine.Children.Add(coolant);
            }
        }

        var metric = new StackPanel { Margin = new Thickness(0, 2, 0, 2), Spacing = 1 };
        if (kind == "network")
        {
            ToolTipService.SetToolTip(statsLine, "Download statistics over the selected history range");
            // Both rates on one row in their traces' colours; the arrows say which is which, the tooltips spell it out.
            value.FontSize = 20;
            value.Text = "↓ —";
            upload.Text = "↑ —";
            ToolTipService.SetToolTip(value, "Download");
            ToolTipService.SetToolTip(upload, "Upload");
            metric.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { value, upload } });
            metric.Children.Add(statsLine);
        }
        else
        {
            // The bottom margin puts the small texts on the reading's baseline.
            statsLine.Margin = new Thickness(14, 0, 0, 3);
            metric.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { value, statsLine } });
        }

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
        card.Padding = new Thickness(14, 9, 14, 8);
        Content = new Grid { Children = { card, wash } };
        PointerEntered += (_, _) => wash.Opacity = HoveredWash;
        PointerExited += (_, _) => wash.Opacity = RestingWash;
        PointerCanceled += (_, _) => wash.Opacity = RestingWash;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>The metric's outline glyph, on a 20 by 20 grid: a chip, a thermometer, a memory module, a drive, or the two network arrows.</summary>
    private static string GlyphData(string kind) => kind switch
    {
        "cpu" or "gpu" => "M 5,5 L 15,5 15,15 5,15 Z M 8,8 L 12,8 12,12 8,12 Z M 7,2 L 7,5 M 13,2 L 13,5 M 7,15 L 7,18 M 13,15 L 13,18 M 2,7 L 5,7 M 2,13 L 5,13 M 15,7 L 18,7 M 15,13 L 18,13",
        "cputemp" or "gputemp" => "M 8,11 L 8,4 C 8,1 12,1 12,4 L 12,11 C 17,15 11,21 7,17 C 5,15 6,12 8,11 M 10,7 L 10,14 M 15,5 L 18,5 M 15,9 L 17,9",
        "memory" => "M 2,5 L 18,5 18,15 2,15 Z M 5,8 L 8,8 8,12 5,12 Z M 12,8 L 15,8 15,12 12,12 Z M 5,15 L 5,18 M 10,15 L 10,18 M 15,15 L 15,18",
        "storage" => "M 2,4 L 18,4 18,16 2,16 Z M 2,12 L 18,12 M 14,14 L 15,14 M 5,14 L 9,14",
        _ => "M 6,2 L 6,16 M 2,12 L 6,16 10,12 M 14,18 L 14,4 M 10,8 L 14,4 18,8",
    };

    private static TextBlock Reading(double size) => new() { Text = "—", FontSize = size, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 9.5, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };

    /// <summary>A caption and its value side by side, one item on the reading's line.</summary>
    private static StackPanel Item(string caption, TextBlock value) => new() { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Caption(caption), value } };

    /// <summary>Colours a text with a graph colour, replacing its brush only when the colour changed.</summary>
    private void Tint(TextBlock text, string key)
    {
        var color = chart.ColorOf(key);
        if (tints.TryGetValue(text, out var shown) && shown.Equals(color)) return;
        tints[text] = color;
        text.Foreground = new SolidColorBrush(color);
    }

    /// <summary>
    /// Sets an element's tooltip text, only when it changed. Each element keeps one tooltip, so a reading that changes while its
    /// tooltip is open updates it in place, and an unchanged text costs nothing on each reading.
    /// </summary>
    /// <returns>Whether the text changed.</returns>
    private bool Tip(FrameworkElement element, string text)
    {
        if (tips.TryGetValue(element, out var shown) && shown.Text == text) return false;
        var tip = shown.Tip;
        if (tip is null) ToolTipService.SetToolTip(element, tip = new ToolTip());
        tip.Content = text;
        tips[element] = (tip, text);
        return true;
    }

    /// <summary>A network rate after its arrow, named in words for screen readers; both are rewritten only when the rate changed.</summary>
    private static void Rate(TextBlock text, ref string shown, string arrow, string name, string rate)
    {
        if (rate == shown) return;
        shown = rate;
        text.Text = arrow + rate;
        AutomationProperties.SetName(text, name + " " + rate);
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
        if (tile.Background is not SolidColorBrush brush || !brush.Color.Equals(tileFill))
        {
            tile.Background = new SolidColorBrush(tileFill);
            glyph.Stroke = new SolidColorBrush(tileColor);
            wash.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0.85, 1),
                GradientStops = { new GradientStop { Color = tileColor, Offset = 0 }, new GradientStop { Color = Windows.UI.Color.FromArgb(0, tileColor.R, tileColor.G, tileColor.B), Offset = 1 } },
            };
        }
        // The first dot is the card's own trace, whose colour the tile has just looked up.
        foreach (var dot in dots) dot.Refresh(chart, dot.Key == OverviewPresenter.ColorKey(kind) ? tileColor : null);

        if (rpmItem is not null)
        {
            bool fan = kind == "gputemp";
            bool show = fan ? session.Settings.ShowGpuFanSpeed : session.Settings.ShowCpuPumpSpeed;
            rpmItem.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                string key = fan ? "gpufan" : "cpupump";
                double? speed = Live(session.MetricId(key));
                Tint(rpm, key);
                rpm.Text = OverviewPresenter.RpmValue(session.Stale, speed);
                // The whole line the card used to give it, then which fan or header it is.
                Tip(rpmItem, OverviewPresenter.Rpm(fan ? "GPU fan" : "CPU pump", session.Stale, speed, OverviewPresenter.Pattern(SavedPattern(key), dotted: true))
                    + (fan ? "\nThe selected GPU's first reported fan" : "\nThe CPU cooler pump, or the CPU Fan header"));
            }
        }

        if (kind == "cputemp")
        {
            bool show = session.Settings.ShowCoolantTemperature;
            coolant.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                // The words are Core's: the live value, or why there is none, and how the line is drawn (dashed by default).
                Tint(coolant, "cpucoolant");
                coolant.Text = HubTexts.CoolantLegend(Live(session.MetricId("cpucoolant")), session.Hub?.State, OverviewPresenter.Pattern(SavedPattern("cpucoolant"), dashed: true));
                Tip(coolant, coolant.Text);
            }
        }

        string line;
        if (kind == "network")
        {
            Tint(upload, "upload");
            Rate(value, ref shownDownload, "↓ ", "Download", OverviewPresenter.Value(kind, current));
            Rate(upload, ref shownUpload, "↑ ", "Upload", OverviewPresenter.Value(kind, Live("network/upload")));
            line = OverviewPresenter.NetworkSubtitle(SavedPattern("download"), SavedPattern("upload"));
        }
        else
        {
            value.Text = OverviewPresenter.Value(kind, current);
            if (kind == "storage")
            {
                var drives = data?.Storage.Drives ?? [];
                if (driveId is not null)
                {
                    // The drive's name (its letters, then its model) is the title, so the line after it carries the bus, size and write rate.
                    var drive = drives.FirstOrDefault(candidate => candidate.Id == driveId);
                    string name = drive is null ? "Disk" : StoragePresenter.Name(drive);
                    if (Tip(title, name)) title.Text = name;
                    line = OverviewPresenter.DriveSubtitle(drive, Live(StorageMetrics.Key(driveId, "write")));
                }
                else
                {
                    string? name = drives.Count == 0 ? null
                        : StoragePresenter.Name(busiest.Pick(drives, drive => (session.Value(StorageMetrics.Key(drive.Id, "read")) ?? 0) + (session.Value(StorageMetrics.Key(drive.Id, "write")) ?? 0)));
                    line = OverviewPresenter.StorageSubtitle(data is not null, drives.Count, name);
                }
            }
            else if (kind == "memory")
                line = OverviewPresenter.MemorySubtitle(data is not null, session.Stale, current is not null, session.Value("memory/used"), session.Value("memory/total"));
            else
                line = OverviewPresenter.DeviceSubtitle(kind, data is not null, data?.CpuName ?? "", session.GpuName,
                    session.Settings.ShowCpuCoreUsage ? data?.CpuCoreCount ?? 0 : 0, current is not null, session.Stale);
        }
        // The line and its tooltip carry the same text, so both are rewritten only when it changes.
        if (Tip(subtitle, line)) subtitle.Text = line;
    }

    private void ShowStatistics(Summary summary)
    {
        double?[] values = [summary.Minimum, summary.Average, summary.Maximum];
        for (int i = 0; i < statistics.Length; i++) statistics[i].Text = OverviewPresenter.Statistic(kind, values[i]);
    }
}

/// <summary>
/// A card's title and, after it, a muted line filling the rest of the row. The title is measured first and keeps the width it
/// needs, so a narrow card trims the line after it first, and the title itself only once it alone is too wide (a drive with a
/// long model). A grid column sized to the title could not trim it, and would push the buttons beside it off the card.
/// </summary>
internal sealed class TitleLine : Panel
{
    private readonly FrameworkElement title, line;

    public TitleLine(FrameworkElement title, FrameworkElement line)
    {
        (this.title, this.line) = (title, line);
        Children.Add(title);
        Children.Add(line);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        title.Measure(availableSize);
        line.Measure(new Size(Math.Max(0, availableSize.Width - title.DesiredSize.Width), availableSize.Height));
        double width = title.DesiredSize.Width + line.DesiredSize.Width;
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, Math.Max(title.DesiredSize.Height, line.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Min(title.DesiredSize.Width, finalSize.Width);
        title.Arrange(new Rect(0, 0, width, finalSize.Height));
        line.Arrange(new Rect(width, 0, Math.Max(0, finalSize.Width - width), finalSize.Height));
        return finalSize;
    }
}
