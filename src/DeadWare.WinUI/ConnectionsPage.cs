using System.Diagnostics;
using System.Globalization;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace DeadWare.WinUI;

/// <summary>
/// Where this PC's traffic goes: a map of live or saved connections from the home city, the nodes in view, the traffic strip for a
/// saved interval, the Destinations table, and the location database, recording and home city settings. Live follows the shared
/// publication; History reads are serialised and read-only.
/// </summary>
internal sealed class ConnectionsPage : Page
{
    private sealed record PeerRow(string Key, string Application, string Endpoint, string Location,
        string Download, string Upload, string Received, string Sent, string Coverage, string Seen);

    private readonly MonitorSession session;
    private readonly ComboBox mode = new(), range = new(), direction = new(), home = new() { Width = 220 };
    private readonly TextBox country = new() { Width = 70, MaxLength = 2, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox search = new() { Width = 180, MaxLength = 256 }, from = new() { Width = 150 }, to = new() { Width = 150 }, citySearch = new() { Width = 170, MaxLength = 128, PlaceholderText = "Search cities" };
    private readonly CheckBox minute = new() { Content = "Minute cursor", MinWidth = 0 }, recording = new() { Content = "Record history", MinWidth = 0 }, animation = new() { Content = "Animate", MinWidth = 0 };
    private readonly Slider cursor = new() { Minimum = 0, Maximum = 0, StepFrequency = 1, SnapsTo = SliderSnapsTo.StepValues, Width = 170 };
    private readonly TextBlock summary = Text("Waiting for endpoint capture."), capture = Muted(""), storage = Muted(""), actionStatus = Text(""),
        database = Muted("DB-IP Lite · no location database · download contacts download.db-ip.com · DB-IP.com (CC BY 4.0)."), cursorTime = Muted(""), selection = Muted(""),
        mappedStatus = Muted(""), nodeText = Text("");
    private readonly ConnectionMapView map;
    private readonly ListView mappedConnections = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 140 };
    private readonly List<string> mappedKeys = [];
    private readonly Canvas mapOverlay = new();
    private readonly Border nodeDetails, mapToolbar;
    private readonly Grid mapLayout = new(), mapHost = new() { Height = 460 }, nodePanel = new() { RowSpacing = 4 };
    private readonly StackPanel body = new() { Padding = new Thickness(24), Spacing = 12 };
    private readonly Button play, download, cancel, export, applicationButton;
    private readonly TextBlock applicationText = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Width = 90, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly FlowPanel historical = new();
    private readonly QueueTimer playback;
    private readonly TrafficStripView trafficStrip = new(TrafficStripKind.Connections) { Selectable = true };
    private readonly DestinationsView destinations;
    private readonly SelectorBar tabs = new();
    private readonly SelectorBarItem trafficTab = new() { Text = "Traffic", Visibility = Visibility.Collapsed }, destinationsTab = new() { Text = "Destinations" };
    private readonly Border lowerCard;
    private readonly List<string?> homeIds = [];
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private CancellationTokenSource? queryCancellation, downloadCancellation;
    private ConnectionHistoryQuery? pending;
    // The strip: the whole-interval query on screen (and when it was applied), one waiting, one in flight and the last that failed.
    private ConnectionHistoryQuery? stripContext, pendingStrip, stripReading, stripFailed;
    private DateTimeOffset stripNow, stripDue;
    // The whole-interval query of the latest request, which a strip reply must match to be applied.
    private ConnectionHistoryQuery? requestedWhole;
    // The latest requested query, and the generation whose read is in flight (−1 for none).
    private ConnectionHistoryQuery? requestedQuery;
    private long readingGeneration = -1;
    private ConnectionHistoryQuery? displayedHistoryQuery;
    private ConnectionLiveView live = new(default, [], null, false, false, "Waiting for endpoint capture.");
    private IReadOnlyList<PeerRow> rows = [];
    private long generation;
    private bool syncing, busy, playing, active, exportInProgress, historyMinuteReady, nodesBesideMap;
    private string? selectedKey;
    // The Destinations table: the location keys passed to the map's highlight, whether the map may still turn for the current
    // selection, and the whole-interval context its History rows belong to.
    private HashSet<(double Latitude, double Longitude)> highlighted = [];
    private bool turnPending;
    private ConnectionHistoryQuery? destinationsWhole;
    // The exact application filter: the key ("" for every application) and the name as given, for display.
    private string applicationFilter = "", applicationName = "";
    private string historyStorage = "", summaryTip = "";
    private bool IsHistory => mode.SelectedIndex == 1;
    private bool MinuteOn => minute.IsChecked == true;

    public ConnectionsPage(MonitorSession session)
    {
        this.session = session;
        map = new ConnectionMapView(session);
        destinations = new DestinationsView(session) { ClockSeconds = () => stopwatch.Elapsed.TotalSeconds };
        playback = new QueueTimer(DispatcherQueue) { Interval = TimeSpan.FromSeconds(1) };

        // Header: the view, the interval, the filters and what is shown.
        Fill(mode, "Connections view", "Live", "History"); mode.SelectedIndex = 0;
        Fill(range, "Connections time range", "Today", "7 days", "30 days"); range.SelectedIndex = 0; range.IsEnabled = false;
        Fill(direction, "Traffic direction", "Both", "Received", "Sent"); direction.SelectedIndex = 0;
        var top = new FlowPanel();
        top.Children.Add(Bottom(mode)); top.Children.Add(Bottom(range)); top.Children.Add(historical);
        historical.Children.Add(Label("From local", from, "History from local time"));
        historical.Children.Add(Label("To (exclusive)", to, "History to exclusive local time"));
        historical.Children.Add(Bottom(Action("Apply interval", () => FiltersChanged())));
        historical.Children.Add(Bottom(minute));
        AutomationProperties.SetName(cursor, "History minute cursor");
        historical.Children.Add(Bottom(cursor));
        cursorTime.VerticalAlignment = VerticalAlignment.Center;
        historical.Children.Add(Bottom(cursorTime));
        play = Action("Play", TogglePlayback);
        historical.Children.Add(Bottom(play));
        historical.Visibility = Visibility.Collapsed;
        var filters = new FlowPanel();
        ToolTipService.SetToolTip(search, "Application name, PID or remote IP");
        filters.Children.Add(Label("Application / IP", search, "Search application or IP"));
        ToolTipService.SetToolTip(country, "ISO country code; blank for all");
        filters.Children.Add(Label("Country code", country, "Country code"));
        filters.Children.Add(Label("Direction", direction, "Traffic direction"));
        applicationButton = new Button { Content = applicationText, MaxWidth = 260, Visibility = Visibility.Collapsed };
        applicationButton.Click += (_, _) => ClearApplicationFilter();
        AutomationProperties.SetName(applicationButton, "Clear application filter");
        filters.Children.Add(Bottom(applicationButton));
        filters.Children.Add(Bottom(Action("Filter", () => FiltersChanged())));
        actionStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        actionStatus.Visibility = Visibility.Collapsed;
        body.Children.Add(Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { top, filters, summary, capture, actionStatus } }));

        // The map, its toolbar and the selected node's details over it; the nodes in view beside it when the page is wide.
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        toolbar.Children.Add(Action("Globe / 2D", () => map.Flat = !map.Flat));
        toolbar.Children.Add(Action("+", () => map.ZoomBy(1.25)));
        toolbar.Children.Add(Action("−", () => map.ZoomBy(.8)));
        toolbar.Children.Add(Action("Reset", map.ResetView));
        animation.IsChecked = session.Settings.ConnectionsAnimation;
        toolbar.Children.Add(animation);
        mapToolbar = new Border
        {
            Child = toolbar, Padding = new Thickness(6), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8), Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
        };
        var close = Action("Close", ClearSelection);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var detailHeader = new Grid();
        detailHeader.Children.Add(new TextBlock { Text = "Connection details", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        detailHeader.Children.Add(close);
        nodeDetails = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed,
            Background = (Brush)Application.Current.Resources["AcrylicInAppFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 6, Children = { detailHeader, new ScrollViewer { Content = nodeText, MaxHeight = 190 } } },
        };
        AutomationProperties.SetName(nodeDetails, "Selected map connection details");
        mapOverlay.Children.Add(nodeDetails);
        mapHost.Children.Add(map); mapHost.Children.Add(mapOverlay); mapHost.Children.Add(mapToolbar);
        mapLayout.ColumnDefinitions.Add(new ColumnDefinition());
        mapLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        mapLayout.Children.Add(mapHost);
        body.Children.Add(Pages.Card(mapLayout));

        nodePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nodePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        nodePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetName(mappedConnections, "Connections drawn on map");
        nodePanel.Children.Add(mappedStatus);
        Grid.SetRow(mappedConnections, 1); nodePanel.Children.Add(mappedConnections);
        export = Action("Export CSV…", () => _ = ExportAsync());
        export.HorizontalAlignment = HorizontalAlignment.Left;
        selection.MaxLines = 2; selection.TextTrimming = TextTrimming.CharacterEllipsis;
        var foot = new StackPanel { Spacing = 4, Children = { export, selection } };
        Grid.SetRow(foot, 2); nodePanel.Children.Add(foot);
        body.Children.Add(nodePanel);

        // Traffic (History only) and Destinations share the lower card.
        tabs.Items.Add(trafficTab); tabs.Items.Add(destinationsTab); tabs.SelectedItem = destinationsTab;
        trafficStrip.Visibility = Visibility.Collapsed;
        lowerCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { tabs, trafficStrip, destinations } });
        body.Children.Add(lowerCard);

        // Location database, recording, home city and what is kept.
        download = Action("Download / update locations", () => _ = UpdateDatabaseAsync());
        cancel = Action("Cancel download", () => downloadCancellation?.Cancel());
        cancel.IsEnabled = false;
        recording.IsChecked = session.Settings.ConnectionsRecording;
        var databaseActions = new FlowPanel();
        databaseActions.Children.Add(download); databaseActions.Children.Add(cancel); databaseActions.Children.Add(progress); databaseActions.Children.Add(recording);
        AutomationProperties.SetName(citySearch, "Search offline home cities");
        ToolTipService.SetToolTip(citySearch, "Search the offline cities by name or country");
        AutomationProperties.SetName(home, "Home city");
        ToolTipService.SetToolTip(home, "Up to 80 matches; refine a city or country search to find more.");
        var choices = new FlowPanel();
        choices.Children.Add(citySearch); choices.Children.Add(home); choices.Children.Add(Action("Clear home city", () => home.SelectedIndex = 0));
        var options = new StackPanel { Spacing = 8, Children = { databaseActions, database, storage, choices } };
        options.Children.Add(Muted("Origin and destination geography are approximate; curves are illustrative. The node list follows the nodes visible on the map; the Destinations table lists every destination, including local, special-purpose and unlocated ones. The strip under the map stacks the six applications with the most traffic in the interval, plus Other; its colours identify applications, not direction. Click it to move the minute cursor, or click an application to show only it. Rolling history: 30 days, up to 256 MiB locally. CSV exports the full filtered interval to a new file. Apps in Don't record these apps show live, marked not recorded, and are never saved. Saved connection history is cleared from Settings."));
        body.Children.Add(new Expander
        {
            Header = "Location database, recording and storage", Content = options,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        Content = new ScrollViewer { Content = body };

        trafficStrip.SlotActivated += (_, time) => ActivateStripAt(time);
        trafficStrip.SelectionMoved += index => { if (trafficStrip.Strip is { } shown) ActivateStripAt(shown.Slots[index].Start); };
        trafficStrip.ApplicationChosen += (key, name) => { applicationFilter = key; applicationName = name; UpdateApplicationButton(); FiltersChanged(); };
        destinations.SelectionChanged += OnDestinationSelectionChanged;
        destinations.CopyRequested += CopyDestination;
        destinations.Export.Click += (_, _) => _ = ExportDestinationsAsync();
        tabs.SelectionChanged += (_, _) => ShowTab();
        SizeChanged += (_, e) => UpdateNodePanelPlacement(e.NewSize.Width);
        mapToolbar.SizeChanged += (_, _) => { UpdateReservedArea(); RefreshMappedConnections(); };
        map.SizeChanged += (_, _) => UpdateReservedArea();
        SetPreset(); FilterCities();
        mode.SelectionChanged += (_, _) => ChangeMode();
        range.SelectionChanged += (_, _) => { SetPreset(); FiltersChanged(); };
        search.TextChanged += (_, _) => FiltersChanged();
        direction.SelectionChanged += (_, _) => FiltersChanged();
        country.TextChanged += (_, _) => FiltersChanged();
        // The hairline follows the cursor at once, also under syncing (playback and strip clicks), before the minute's read returns.
        minute.Checked += (_, _) => { UpdateHairline(); FiltersChanged(); };
        minute.Unchecked += (_, _) => { UpdateHairline(); FiltersChanged(); };
        cursor.ValueChanged += (_, _) => { UpdateHairline(); if (!syncing) FiltersChanged(false); };
        citySearch.TextChanged += (_, _) => FilterCities();
        home.SelectionChanged += (_, _) =>
        {
            if (syncing || home.SelectedIndex < 0 || home.SelectedIndex >= homeIds.Count) return;
            session.SetConnectionsHomeCity(homeIds[home.SelectedIndex]);
        };
        recording.Checked += (_, _) => SetRecording(true);
        recording.Unchecked += (_, _) => SetRecording(false);
        animation.Checked += (_, _) => SetAnimation(true);
        animation.Unchecked += (_, _) => SetAnimation(false);
        mappedConnections.SelectionChanged += (_, _) => { if (!syncing && mappedConnections.SelectedItem is ListViewItem { Tag: string key }) SelectMapPeer(key); };
        map.PointSelected += SelectMapPeer;
        map.ViewChanged += RefreshMappedConnections;
        // Esc clears the node and the table selection; F5 re-sorts the Destinations table.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { ClearSelection(); ClearDestinationSelection(); e.Handled = true; }
            else if (e.Key == VirtualKey.F5) { destinations.Resort(); e.Handled = true; }
        };
        playback.Tick += AdvancePlayback;
        Loaded += (_, _) =>
        {
            active = true;
            session.Changed += RefreshSession; session.HistoryClearChanged += HistoryClearChanged;
            RefreshSession(); ShowStripAgain();
            if (IsHistory && !ReadOutstanding()) RequestHistory();
        };
        Unloaded += (_, _) =>
        {
            active = false;
            session.Changed -= RefreshSession; session.HistoryClearChanged -= HistoryClearChanged;
            CancelHistory(); pendingStrip = null; StopPlayback(); downloadCancellation?.Cancel(); destinations.ReleaseHolds();
        };
        ShowTab(); RefreshSession();
    }

    private static TextBlock Text(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Muted(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private static string B(double? bytes, bool rate = false) => HistoryChartModel.Bytes(bytes, rate);

    private static void Fill(ComboBox box, string name, params string[] items)
    {
        foreach (string item in items) box.Items.Add(item);
        AutomationProperties.SetName(box, name);
    }

    private static T Bottom<T>(T element) where T : FrameworkElement { element.VerticalAlignment = VerticalAlignment.Bottom; return element; }

    private static StackPanel Label(string text, FrameworkElement input, string name)
    {
        AutomationProperties.SetName(input, name);
        var caption = Muted(text); caption.FontSize = 11;
        return new StackPanel { Spacing = 2, Children = { caption, input } };
    }

    private static Button Action(string text, Action run)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => run();
        return button;
    }

    private void SetActionStatus(string text)
    {
        actionStatus.Text = text;
        actionStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowTab()
    {
        bool traffic = IsHistory && tabs.SelectedItem == trafficTab;
        trafficStrip.Visibility = traffic ? Visibility.Visible : Visibility.Collapsed;
        destinations.Visibility = traffic ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Tells the map where the overlay toolbar is, so hover chips are placed clear of it.</summary>
    private void UpdateReservedArea() => map.ReservedArea = ToolbarBounds(map);

    private Rect? ToolbarBounds(UIElement relativeTo)
    {
        if (mapToolbar.ActualWidth <= 0 || mapToolbar.ActualHeight <= 0 || map.ActualWidth <= 0) return null;
        try
        {
            var origin = mapToolbar.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
            return new Rect(origin.X, origin.Y, mapToolbar.ActualWidth, mapToolbar.ActualHeight);
        }
        catch (ArgumentException) { return null; }
    }

    private void UpdateNodePanelPlacement(double pageWidth)
    {
        bool beside = pageWidth >= 1100;
        if (nodesBesideMap == beside) return;
        if (nodePanel.Parent is Panel parent) parent.Children.Remove(nodePanel);
        mappedConnections.MaxHeight = beside ? double.PositiveInfinity : 140;
        nodePanel.RowDefinitions[1].Height = beside ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        nodePanel.Margin = beside ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        nodePanel.Height = beside ? mapHost.Height : double.NaN;
        mapLayout.ColumnDefinitions[1].Width = new GridLength(beside ? 280 : 0);
        if (beside) { Grid.SetColumn(nodePanel, 1); mapLayout.Children.Add(nodePanel); }
        else { Grid.SetColumn(nodePanel, 0); body.Children.Insert(body.Children.IndexOf(lowerCard), nodePanel); }
        nodesBesideMap = beside;
    }

    private void SetRecording(bool enabled) { if (!syncing) session.SetConnectionsRecording(enabled); }
    private void UpdateMapAnimation() => map.AnimationEnabled = animation.IsChecked == true && (!IsHistory || playing && MinuteOn && historyMinuteReady);
    private void SetAnimation(bool enabled) { UpdateMapAnimation(); if (!syncing) session.SetConnectionsAnimation(enabled); }

    private void FilterCities()
    {
        string text = citySearch.Text.Trim(); syncing = true;
        try
        {
            string? selected = home.SelectedIndex >= 0 && home.SelectedIndex < homeIds.Count ? homeIds[home.SelectedIndex] : session.Settings.ConnectionsHomeCityId;
            var cities = MapAssets.Geography.Cities;
            var matches = cities.Where(c => text.Length > 0 && (c.Name + " " + c.Country).Contains(text, StringComparison.OrdinalIgnoreCase)).Take(80).ToList();
            var saved = cities.FirstOrDefault(c => c.Id == selected);
            if (saved is not null && !matches.Any(c => c.Id == selected)) matches.Insert(0, saved);
            home.Items.Clear(); homeIds.Clear();
            home.Items.Add("No origin location"); homeIds.Add(null);
            foreach (var city in matches) { home.Items.Add($"{city.Name}, {city.Country}"); homeIds.Add(city.Id); }
            home.SelectedIndex = Math.Max(0, homeIds.IndexOf(selected));
        }
        finally { syncing = false; }
    }

    private void RefreshSession()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(RefreshSession); return; }
        syncing = true;
        try
        {
            recording.IsChecked = session.Settings.ConnectionsRecording; animation.IsChecked = session.Settings.ConnectionsAnimation;
            destinations.SyncScope(ConnectionDestinations.ParseScope(session.Settings.ConnectionsDestinationScope));
            UpdateMapAnimation();
        }
        finally { syncing = false; }
        var service = session.Connections;
        storage.Text = (IsHistory ? historyStorage : "") + service.StorageStatus;
        database.Text = $"DB-IP Lite · {(service.DatabaseDate is { } date ? $"database {date:yyyy-MM-dd}" : "no location database")} · Download contacts download.db-ip.com · DB-IP.com (CC BY 4.0). {service.LookupStatus}";
        live = service.Live;
        if (!IsHistory) ShowLiveView(live, false);
        RefreshNotRecordedFilter();
    }

    private void ChangeMode()
    {
        CancelHistory(); pendingStrip = null;
        if (!IsHistory) { stripContext = null; stripFailed = null; }
        displayedHistoryQuery = null; destinationsWhole = null; StopPlayback(); ClearSelection(); ClearDestinationSelection();
        historical.Visibility = trafficTab.Visibility = IsHistory ? Visibility.Visible : Visibility.Collapsed;
        tabs.SelectedItem = IsHistory ? trafficTab : destinationsTab;
        ShowTab();
        range.IsEnabled = IsHistory; UpdateExportEnabled();
        if (IsHistory) RequestHistory(); else ShowLiveView(live, true);
    }

    private void SetPreset()
    {
        int days = range.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 };
        from.Text = DateTime.Today.AddDays(1 - days).ToString(ConnectionsPresenter.LocalTimeFormat, CultureInfo.InvariantCulture);
        to.Text = DateTime.Today.AddDays(1).ToString(ConnectionsPresenter.LocalTimeFormat, CultureInfo.InvariantCulture);
        syncing = true; cursor.Value = 0; syncing = false;
    }

    private bool TryInterval(out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        if (!ConnectionsPresenter.TryParseLocalTime(from.Text, TimeZoneInfo.Local, out start) || !ConnectionsPresenter.TryParseLocalTime(to.Text, TimeZoneInfo.Local, out end) || end <= start || (end - start).TotalDays > 31)
        { SetActionStatus(ConnectionsPresenter.IntervalWarning); return false; }
        // A corrected interval clears its own warning; other action messages stay.
        if (actionStatus.Text == ConnectionsPresenter.IntervalWarning) SetActionStatus("");
        syncing = true;
        try { cursor.Maximum = Math.Max(0, Math.Ceiling((end - start).TotalMinutes) - 1); cursor.Value = Math.Min(cursor.Value, cursor.Maximum); }
        finally { syncing = false; }
        return true;
    }

    /// <summary>Returns the minute query while the minute cursor is on and the whole-interval query otherwise; <paramref name="whole"/> is always the whole interval.</summary>
    private ConnectionHistoryQuery? BuildQuery(out ConnectionHistoryQuery? whole)
    {
        whole = null;
        if (!TryInterval(out var start, out var end)) return null;
        whole = new(start, end, search.Text.Trim(), country.Text.Trim().ToUpperInvariant(), (ConnectionDirection)Math.Max(0, direction.SelectedIndex), 0, 200, applicationFilter);
        var query = whole;
        if (MinuteOn) { var first = start.AddMinutes((int)cursor.Value); query = whole with { From = first, To = end < first.AddMinutes(1) ? end : first.AddMinutes(1) }; }
        cursorTime.Text = MinuteOn ? $"{query.From.ToLocalTime():MM-dd HH:mm}" : "Whole interval";
        return query;
    }

    private void FiltersChanged(bool resetCursor = true)
    {
        if (syncing) return;
        StopPlayback(); ClearSelection();
        if (resetCursor) { syncing = true; cursor.Value = 0; syncing = false; }
        if (IsHistory) RequestHistory(); else ShowLiveView(live, true);
    }

    private void CancelHistory()
    {
        generation++; queryCancellation?.Cancel(); queryCancellation?.Dispose(); queryCancellation = null; pending = null; historyMinuteReady = false; UpdateMapAnimation();
    }

    private void SetSummary(string text, string tip)
    {
        summary.Text = text; summaryTip = tip;
        ToolTipService.SetToolTip(summary, tip.Length == 0 ? null : tip);
    }

    private void RequestHistory()
    {
        CancelHistory(); var query = BuildQuery(out var whole); requestedWhole = whole;
        if (query is null || displayedHistoryQuery is null || !ConnectionsPresenter.SameContext(displayedHistoryQuery, query))
        {
            displayedHistoryQuery = null; rows = [];
            selectedKey = null; map.SetData([], null, true, "Reading saved history…"); RefreshMappedConnections(); UpdateDestinationHighlight();
            SetSummary("History · Reading saved observations…", capture.Text);
        }
        // The Destinations rows clear only for a new whole-interval context: a cursor move or a playback step keeps them until the next view.
        if (query is null || whole is null || !ConnectionsPresenter.SameContext(whole, destinationsWhole))
        {
            destinationsWhole = null;
            destinations.ShowText(query is null ? DestinationCopy.InvalidInterval : DestinationCopy.Reading);
        }
        if (query is null || whole is null) { StopPlayback(); stripContext = null; trafficStrip.ShowText("Choose a valid interval"); UpdateHairline(); return; }
        bool cursorOn = MinuteOn;
        // The strip clears only when the whole-interval context changes, never on a cursor move.
        if (!ConnectionsPresenter.SameContext(whole, stripContext))
        {
            stripContext = null;
            trafficStrip.ShowText(cursorOn && ConnectionsPresenter.SameContext(whole, stripFailed) ? "History unavailable" : "Reading saved history…");
            UpdateHairline();
        }
        // With the cursor on, a minute read carries no strip, so the whole interval is read after it, unless it is on screen, in flight
        // or failed (a failed context waits for a change). With the cursor off, the one read carries the strip.
        pendingStrip = cursorOn && !ConnectionsPresenter.SameContext(whole, stripContext) && !ConnectionsPresenter.SameContext(whole, stripReading) && !ConnectionsPresenter.SameContext(whole, stripFailed) ? whole : null;
        requestedQuery = query; queryCancellation = new(); pending = query; _ = PumpHistoryAsync();
    }

    /// <summary>
    /// A read of exactly the query the boxes give is waiting or in flight and not cancelled, with the cursor off so that it carries the
    /// strip too. Show in Connections from another page requests its read before the page is shown; showing it then reads nothing more.
    /// </summary>
    private bool ReadOutstanding() => !MinuteOn && (pending is not null || readingGeneration == generation) && requestedQuery is { } asked && BuildQuery(out _) == asked;

    /// <summary>A Connections clear started (its rows are hidden at once) or ended: the shown History view is read again. Live is unaffected.</summary>
    private void HistoryClearChanged(HistoryClearStore store)
    {
        if (store != HistoryClearStore.Connections) return;
        stripContext = null; stripFailed = null;
        if (IsHistory && active) RequestHistory();
    }

    // Showing the page again re-reads a strip that may hold minutes saved since, and retries a failed one.
    private void ShowStripAgain() { if (stripContext is { } shown && shown.To > stripNow) stripContext = null; stripFailed = null; }

    private async Task PumpHistoryAsync()
    {
        if (busy) return;
        busy = true;
        try
        {
            // The minute first; the strip only when no minute is pending.
            while (IsHistory && (pending is not null || pendingStrip is not null))
            {
                if (pending is { } query)
                {
                    pending = null; long requested = generation; var token = queryCancellation!.Token; SetCapture("Reading saved history…"); readingGeneration = requested;
                    bool Current() => !token.IsCancellationRequested && requested == generation && IsHistory;
                    try { var result = await session.Connections.ReadViewAsync(query); if (Current()) ApplyHistory(result); }
                    catch (ConnectionHistoryLimitException) { if (Current()) Failed(query, ConnectionsPresenter.LimitWarning); }
                    catch (Exception error) { if (Current()) Failed(query, $"History could not be read ({error.GetType().Name})."); }
                    finally { readingGeneration = -1; }
                    continue;
                }
                // The strip read is not invalidated by generation; only its Strip is ever applied.
                var whole = pendingStrip!; pendingStrip = null; stripReading = whole;
                ConnectionHistoryView? view = null; Exception? failure = null;
                try { view = await session.Connections.ReadViewAsync(whole); }
                catch (Exception error) { failure = error; }
                finally { stripReading = null; }
                if (!IsHistory || !ConnectionsPresenter.SameContext(whole, requestedWhole)) continue;
                if (failure is not null) SetActionStatus(failure is ConnectionHistoryLimitException ? ConnectionsPresenter.LimitWarning : $"History could not be read ({failure.GetType().Name}).");
                ApplyStrip(whole, view?.Strip);
            }
        }
        catch (Exception error) { App.Note($"Connections history could not be shown: {error}"); SetActionStatus($"History could not be shown ({error.GetType().Name})."); }
        finally { busy = false; }
    }

    private void Failed(ConnectionHistoryQuery query, string message)
    {
        SetActionStatus(message);
        ApplyHistory(ConnectionsPresenter.Empty(query, "History unavailable; no saved observations shown."));
        StopPlayback();
    }

    /// <summary>Shows a whole-interval strip, or "History unavailable" for null, and records which context is on screen or failed.</summary>
    private void ApplyStrip(ConnectionHistoryQuery whole, TrafficStrip? strip)
    {
        var current = DateTimeOffset.UtcNow;
        var due = TrafficStrips.DueUntil(whole.To, current, ConnectionService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds));
        trafficStrip.Apply(strip, due, current, FilterNotRecorded);
        if (strip is null) { stripContext = null; stripFailed = whole; }
        else { stripContext = whole; stripNow = current; stripDue = due; stripFailed = null; }
        UpdateHairline();
    }

    // While the minute cursor is on, an accent hairline marks the middle of the cursor minute and the slot holding it is selected.
    private void UpdateHairline()
    {
        if (MinuteOn && trafficStrip.Strip is { } shown)
        {
            var mark = shown.From.AddMinutes(cursor.Value).AddSeconds(30);
            trafficStrip.MarkTime = mark; trafficStrip.SelectedIndex = TrafficStrips.SlotAt(shown, mark);
        }
        else { trafficStrip.MarkTime = null; trafficStrip.SelectedIndex = null; }
    }

    /// <summary>A click or key on the strip turns the minute cursor on at the minute holding the time, while the boxes still show the strip's interval.</summary>
    private void ActivateStripAt(DateTimeOffset time)
    {
        if (!IsHistory || stripContext is not { } shown || !ConnectionsPresenter.TryParseLocalTime(from.Text, TimeZoneInfo.Local, out var start)
            || !ConnectionsPresenter.TryParseLocalTime(to.Text, TimeZoneInfo.Local, out var end) || start != shown.From || end != shown.To)
        { UpdateHairline(); return; }
        syncing = true;
        try { minute.IsChecked = true; cursor.Value = Math.Clamp(Math.Floor((time - shown.From).TotalMinutes), 0, cursor.Maximum); }
        finally { syncing = false; }
        StopPlayback(); ClearSelection(); RequestHistory();
    }

    /// <summary>The application filter is set to an app in Don't record these apps (by key), so its traffic isn't saved.</summary>
    private bool FilterNotRecorded => applicationFilter.Length > 0 && session.IsExcluded(applicationName);

    private void UpdateApplicationButton()
    {
        bool set = applicationFilter.Length > 0, excluded = FilterNotRecorded;
        string text = set ? $"Application: {applicationName}{(excluded ? ProcessCopy.NotRecordedSuffix : "")} ✕" : "";
        if (applicationText.Text != text) applicationText.Text = text;
        ToolTipService.SetToolTip(applicationButton, set ? $"Only {applicationName} is shown. Click to show every application." + (excluded ? " It is in Don't record these apps, so its traffic isn't saved." : "") : null);
        applicationButton.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>On Changed: the Don't record list may have changed, so the filter button and the shown strip follow it.</summary>
    private void RefreshNotRecordedFilter()
    {
        UpdateApplicationButton();
        bool excluded = FilterNotRecorded;
        if (trafficStrip.Strip is { } shown && trafficStrip.FilterNotRecorded != excluded) { trafficStrip.Apply(shown, stripDue, stripNow, excluded); UpdateHairline(); }
    }

    private void ClearApplicationFilter() { applicationFilter = applicationName = ""; UpdateApplicationButton(); FiltersChanged(); }

    /// <summary>Opens History for the whole minutes holding the request's interval, with every other filter cleared and only the application, when one is given.</summary>
    public void ShowHistory(ConnectionsHistoryRequest request)
    {
        StopPlayback(); ClearSelection();
        var start = TrafficStrips.CeilingMinute(request.From); var end = TrafficStrips.CeilingMinute(request.To);
        if (end < start.AddMinutes(1)) end = start.AddMinutes(1);
        var (fromText, toText) = ConnectionsPresenter.LocalBoxes(start, end, TimeZoneInfo.Local);
        syncing = true;
        try
        {
            from.Text = fromText; to.Text = toText; search.Text = ""; country.Text = ""; direction.SelectedIndex = 0;
            minute.IsChecked = false; cursor.Value = 0;
            bool set = !string.IsNullOrWhiteSpace(request.Application);
            applicationFilter = set ? ProcessGrouping.Key(request.Application!) : ""; applicationName = set ? request.Application! : "";
            UpdateApplicationButton();
        }
        finally { syncing = false; }
        // The range preset is left alone: changing it would apply a preset over these boxes.
        if (IsHistory) RequestHistory(); else mode.SelectedIndex = 1;
    }

    private void TogglePlayback()
    {
        if (playing) { StopPlayback(); return; }
        if (!IsHistory || !TryInterval(out _, out _)) return;
        syncing = true; minute.IsChecked = true; syncing = false;
        playing = true; play.Content = "Pause"; UpdateMapAnimation(); RequestHistory(); playback.Start();
    }

    private void StopPlayback() { playing = false; playback.Stop(); play.Content = "Play"; UpdateMapAnimation(); }

    private void AdvancePlayback()
    {
        try
        {
            if (!playing || !IsHistory) { StopPlayback(); return; }
            if (busy || pending is not null) return;
            if (cursor.Value >= cursor.Maximum) { StopPlayback(); return; }
            syncing = true; cursor.Value++; syncing = false;
            RequestHistory();
        }
        catch (Exception error) { syncing = false; App.Note($"Connections playback stopped: {error}"); StopPlayback(); }
    }

    /// <summary>A live app's name, with " · not recorded" when it is in Don't record these apps; search and the filters match the bare name.</summary>
    private string Marked(string name) => session.IsExcluded(name) ? name + ProcessCopy.NotRecordedSuffix : name;

    /// <summary>Shows a Live view; <paramref name="resort"/>: a filter or mode change, so the Destinations table re-sorts at once.</summary>
    private void ShowLiveView(ConnectionLiveView view, bool resort)
    {
        live = view;
        var filtered = view.Connections.Where(c => Matches(c.Traffic.Peer.Process, c.Traffic.Peer.RemoteAddress, c.Location, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray();
        var located = filtered.Where(c => c.Location?.Point is not null).ToArray();
        rows = located.Select(c => new PeerRow(ConnectionsPresenter.Key(c.Traffic.Peer), $"{Marked(c.Traffic.Peer.Process.Name)} / {c.Traffic.Peer.Process.Pid}",
            ConnectionsPresenter.Endpoint(c.Traffic.Peer.RemoteAddress, c.Traffic.Peer.RemotePort, c.Traffic.Peer.Protocol), ConnectionsPresenter.Location(c.Traffic.Peer.RemoteAddress, c.Location),
            B(view.Available ? c.Traffic.DownloadBytesPerSecond : null, true), B(view.Available ? c.Traffic.UploadBytesPerSecond : null, true), B(c.Traffic.ReceivedBytes), B(c.Traffic.SentBytes),
            view.Complete ? "Observed" : "Incomplete", $"First {c.Traffic.FirstSeen.ToLocalTime():g} · Last {c.Traffic.LastSeen.ToLocalTime():g}")).ToArray();
        long received = 0, sent = 0;
        foreach (var item in filtered) { received = ConnectionsPresenter.Add(received, item.Traffic.ReceivedBytes); sent = ConnectionsPresenter.Add(sent, item.Traffic.SentBytes); }
        // The summary counts the table's list, built from the same filtered array, so the two never disagree.
        destinations.ShowLive(view, filtered, LiveFiltersSet(), session.Connections.DatabaseDate is not null, resort, session.IsExcluded);
        var counts = destinations.List!.Counts;
        long countries = filtered.Select(c => c.Location?.CountryCode).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SetSummary($"Live · {ConnectionsPresenter.Count(counts.All, "destination", "destinations")} · {ConnectionsPresenter.Count(countries, "country", "countries")} · {counts.Local:N0} local · {counts.Unlocated:N0} unlocated · ↓ {B(received)} received / ↑ {B(sent)} sent (totals for active peers since first observed)", "");
        SetCapture($"{view.Status}{(!view.Complete ? " · Incomplete capture" : "")} · Current observed rates; geography approximate; curves illustrative.");
        map.SetData(located.Select(c =>
        {
            string key = ConnectionsPresenter.Key(c.Traffic.Peer);
            return new ConnectionMapPoint(key, c.Location!.Point!, c.Traffic.ReceivedBytes, c.Traffic.SentBytes, selectedKey == key,
                view.Available && c.Traffic.DownloadBytesPerSecond > 0, view.Available && c.Traffic.UploadBytesPerSecond > 0,
                Label: Marked(c.Traffic.Peer.Process.Name),
                Detail: $"{ConnectionsPresenter.Location(c.Traffic.Peer.RemoteAddress, c.Location)} · ↓ {B(view.Available ? c.Traffic.DownloadBytesPerSecond : null, true)} · ↑ {B(view.Available ? c.Traffic.UploadBytesPerSecond : null, true)}");
        }).ToArray(), view.Origin, view.Available, view.Status);
        UpdateDestinationHighlight(); UpdateMapAnimation(); RefreshMappedConnections(); UpdateExportEnabled(); range.IsEnabled = IsHistory;
    }

    private void ApplyHistory(ConnectionHistoryView view)
    {
        displayedHistoryQuery = view.Query;
        historyMinuteReady = view.Query.To - view.Query.From <= TimeSpan.FromMinutes(1);
        ConnectionHistoryRow? Representative(ConnectionHistoryMarker m) => m.Representative ?? view.Rows.FirstOrDefault(r => r.Location == m.Location && r.Origin == m.Origin);
        rows = view.Markers.Select(Representative).OfType<ConnectionHistoryRow>().DistinctBy(ConnectionsPresenter.Key).Select(r => new PeerRow(ConnectionsPresenter.Key(r), $"{r.Process.Name} / {r.Process.Pid}",
            ConnectionsPresenter.Endpoint(r.RemoteAddress, r.RemotePort, r.Protocol), ConnectionsPresenter.Location(r.RemoteAddress, r.Location), B(r.DownloadBytesPerSecond, true), B(r.UploadBytesPerSecond, true),
            B(r.ReceivedBytes), B(r.SentBytes), r.Complete ? "Recorded" : "Incomplete", $"First {r.FirstSeen.ToLocalTime():g} · Last {r.LastSeen.ToLocalTime():g}")).ToArray();
        var s = view.Summary;
        // A view without a strip is unavailable (a failed read, the TEMP limit or no service): unknown counts are "—", never 0.
        if (view.Strip is null) SetSummary(ConnectionsPresenter.UnavailableSummary, "History unavailable");
        else SetSummary($"History · minute-bucket average rates · {ConnectionsPresenter.Count(s.Destinations, "destination", "destinations")} · {ConnectionsPresenter.Count(s.Countries, "country", "countries")} · {s.Local:N0} local · {s.Unlocated:N0} unlocated · ↓ {B(s.ReceivedBytes)} received / ↑ {B(s.SentBytes)} sent", "");
        // A minute read never touches the strip; with the cursor off the one read carries it. The context is recorded from the raw
        // request, as RequestHistory compares it, not from the store-normalised view.Query.
        if (!MinuteOn) ApplyStrip(requestedWhole ?? view.Query, view.Strip);
        destinations.ShowHistory(view.Destinations, MinuteOn, FiltersSet(view.Query)); destinationsWhole = requestedWhole;
        SetCapture($"{view.Query.From.ToLocalTime():g} – {view.Query.To.ToLocalTime():g} (exclusive) · {view.Gaps.Count} gaps · {view.Status}" + (view.OmittedMarkers > 0 ? $" · {view.OmittedMarkers:N0} grouped locations omitted from map" : ""));
        historyStorage = $"{B(view.StorageBytes)} stored · Earliest {(view.EarliestRetainedTime is { } earliest ? earliest.ToLocalTime().ToString("g") : "none")} · rolling 30 days / 256 MiB. ";
        storage.Text = historyStorage + session.Connections.StorageStatus;
        map.SetData(view.Markers.Where(m => m.Location.Point is not null).Select(m =>
        {
            var representative = Representative(m);
            return new ConnectionMapPoint(representative is not null ? ConnectionsPresenter.Key(representative) : ConnectionsPresenter.Key(m), m.Location.Point!, m.ReceivedBytes, m.SentBytes,
                selectedKey is not null && m.Representative is not null && selectedKey == ConnectionsPresenter.Key(m.Representative),
                Label: representative?.Process.Name,
                Detail: representative is null ? null : $"{ConnectionsPresenter.Location(representative.RemoteAddress, representative.Location)} · ↓ {B(representative.DownloadBytesPerSecond, true)} · ↑ {B(representative.UploadBytesPerSecond, true)}");
        }).ToArray(), ConnectionsPresenter.CommonOrigin(view.Markers), true, "Saved approximate geography · illustrative curves" + (view.OmittedMarkers > 0 ? $" · +{view.OmittedMarkers:N0} grouped locations omitted" : ""));
        UpdateDestinationHighlight(); UpdateMapAnimation(); RefreshMappedConnections(); UpdateExportEnabled(); range.IsEnabled = IsHistory;
    }

    private void SetCapture(string text)
    {
        capture.Text = text;
        if (summaryTip != "History unavailable") { summaryTip = text; ToolTipService.SetToolTip(summary, text); }
    }

    /// <summary>Search, country, direction or the application filter is set.</summary>
    private bool LiveFiltersSet() => search.Text.Trim().Length > 0 || !string.IsNullOrWhiteSpace(country.Text) || direction.SelectedIndex > 0 || applicationFilter.Length > 0;
    private static bool FiltersSet(ConnectionHistoryQuery query) => query.Search.Length > 0 || query.CountryCode.Length > 0 || query.Direction != ConnectionDirection.Both || query.Application.Length > 0;

    /// <summary>Export CSV… and Export destinations… share one busy state and are enabled only in History; in Live the latter says why.</summary>
    private void UpdateExportEnabled()
    {
        export.IsEnabled = destinations.Export.IsEnabled = IsHistory && !exportInProgress;
        ToolTipService.SetToolTip(destinations.Export, IsHistory ? null : DestinationCopy.ExportLiveOnly);
    }

    /// <summary>Puts Destinations text on the clipboard; the action status says whether it worked, naming only the exception type.</summary>
    private void CopyDestination(string text)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            SetActionStatus(DestinationCopy.Copied);
        }
        catch (Exception error) { SetActionStatus(DestinationCopy.CopyFailed(error.GetType().Name)); }
    }

    private void ClearSelection()
    {
        selectedKey = null; nodeDetails.Visibility = Visibility.Collapsed; nodeText.Text = "";
        syncing = true; mappedConnections.SelectedItem = null; syncing = false;
        map.SelectPoint(null);
        RefreshMappedConnections();
    }

    private void SelectMapPeer(string key)
    {
        if (IsHistory && displayedHistoryQuery is null || map.PointPosition(key) is null) return;
        if (!rows.Any(r => r.Key == key)) return;
        // Only a real node selection clears the table selection and its highlight.
        ClearDestinationSelection();
        selectedKey = key;
        map.SelectPoint(key);
        RefreshMappedConnections();
    }

    /// <summary>Clears the Destinations selection, the map highlight and the status line.</summary>
    private void ClearDestinationSelection()
    {
        // Clearing a selection raises SelectionChanged, which updates the highlight; with nothing selected, update it here.
        bool selected = destinations.Selected is not null;
        destinations.ClearSelection();
        if (!selected) UpdateDestinationHighlight();
    }

    /// <summary>Selecting a row clears the node selection and its detail box, then highlights the row's points; the map may turn once for it.</summary>
    private void OnDestinationSelectionChanged()
    {
        if (destinations.Selected is not null) { ClearSelection(); turnPending = true; }
        UpdateDestinationHighlight();
    }

    /// <summary>Passes the selected row's current points to the map's highlight, turns the map once per selection when no highlighted
    /// node is in view, and sets the status line. Runs after every update, so a finished lookup or a new minute shows at once.</summary>
    private void UpdateDestinationHighlight()
    {
        var selected = destinations.Selected;
        IReadOnlyList<GeoPoint> points = selected?.Points ?? [];
        var keys = points.Where(ConnectionProjection.IsValid).Select(ConnectionProjection.LocationKey).ToHashSet();
        if (!keys.SetEquals(highlighted))
        {
            highlighted = keys; map.Highlight(keys.Count == 0 ? null : [.. points]);
            // With more than 256 groups the displayed nodes can change.
            RefreshMappedConnections();
        }
        if (selected is null) { turnPending = false; destinations.SetStatus(""); return; }
        var nodes = map.DisplayedPoints.Where(p => map.HighlightedKeys.Contains(p.Key)).ToList();
        // The turn waits while the row has points but no highlighted node is displayed, as while History reads after clearing the
        // map; a row without points uses it up, so a location found later keeps the camera.
        if (turnPending && (nodes.Count > 0 || keys.Count == 0))
        {
            turnPending = false;
            if (nodes.Count > 0 && nodes.All(p => map.PointPosition(p.Key) is null))
                map.SetView(nodes.MaxBy(p => (double)p.ReceivedBytes + p.SentBytes)!.Point, map.Camera.Zoom);
        }
        destinations.SetStatus(nodes.Count > 0 ? DestinationCopy.Highlighted(nodes.Count) : keys.Count > 0 ? DestinationCopy.NotAmongDrawn : DestinationCopy.NotOnMap(selected.Class.Kind));
    }

    private void RefreshMappedConnections()
    {
        var visible = map.DisplayedPoints.Where(p => map.PointPosition(p.Key) is not null).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var peers = new Dictionary<string, PeerRow>(StringComparer.Ordinal);
        foreach (var row in rows) peers.TryAdd(row.Key, row);
        var items = visible.Where(p => peers.ContainsKey(p.Key))
            .Select(p => (p.Key, Label: $"{peers[p.Key].Location} · {peers[p.Key].Application} · ↓ {B(p.ReceivedBytes)} / ↑ {B(p.SentBytes)}")).ToArray();
        // The same nodes in the same order only change their text, so the list keeps its items, focus and scrolling while live traffic changes.
        syncing = true;
        try
        {
            if (!mappedKeys.SequenceEqual(items.Select(i => i.Key)))
            {
                mappedConnections.Items.Clear(); mappedKeys.Clear();
                foreach (var item in items)
                {
                    mappedKeys.Add(item.Key);
                    mappedConnections.Items.Add(new ListViewItem { Tag = item.Key, Content = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, MinHeight = 28 });
                }
            }
            for (int i = 0; i < items.Length; i++)
                if (mappedConnections.Items[i] is ListViewItem { Content: TextBlock label } && label.Text != items[i].Label) label.Text = items[i].Label;
            int index = selectedKey is null ? -1 : mappedKeys.IndexOf(selectedKey);
            if (mappedConnections.SelectedIndex != index) mappedConnections.SelectedIndex = index;
        }
        finally { syncing = false; }
        mappedStatus.Text = $"{(IsHistory ? "History" : "Live")} · {items.Length} visible nodes · click for details";
        SetSelectionText(items.Length == 0 ? "No located connections in this map view. Rotate the globe, change filters or download locations." : "Select a node or listed connection to see its details beside the node.");
        if (selectedKey is not null && !map.DisplayedPoints.Any(p => p.Key == selectedKey)) selectedKey = null;
        if (selectedKey is null || !peers.TryGetValue(selectedKey, out var selected) || map.PointPosition(selectedKey) is not { } anchor)
        { nodeDetails.Visibility = Visibility.Collapsed; nodeText.Text = ""; return; }
        var point = visible.First(p => p.Key == selectedKey);
        nodeText.Text = $"{(IsHistory ? "Historical connection (saved minute)" : "Live connection")}\n{selected.Application}\n{selected.Endpoint}\n{selected.Location}\n↓ {selected.Download}   ↑ {selected.Upload}\nReceived {selected.Received} · Sent {selected.Sent}\n{selected.Coverage} · {selected.Seen}\nLocation total: ↓ {B(point.ReceivedBytes)} / ↑ {B(point.SentBytes)}\nRepresentative connection at this grouped location.";
        SetSelectionText($"{selected.Application} · {selected.Endpoint} · {selected.Location} · {selected.Coverage} · {selected.Seen}");
        double width = Math.Min(320, Math.Max(1, map.ActualWidth - 16));
        nodeDetails.Width = width; nodeDetails.Visibility = Visibility.Visible;
        nodeDetails.Measure(new Size(width, double.PositiveInfinity));
        double height = nodeDetails.DesiredSize.Height;
        double x = anchor.X + 12; if (x + width > map.ActualWidth - 8) x = anchor.X - width - 12;
        x = Math.Clamp(x, 8, Math.Max(8, map.ActualWidth - width - 8));
        double y = Math.Clamp(anchor.Y - 20, 8, Math.Max(8, map.ActualHeight - height - 8));
        if (ToolbarBounds(mapOverlay) is { } toolbar && x < toolbar.Right && x + width > toolbar.Left && y < toolbar.Bottom && y + height > toolbar.Top) y = toolbar.Bottom + 8;
        Canvas.SetLeft(nodeDetails, x); Canvas.SetTop(nodeDetails, y);
    }

    private void SetSelectionText(string text)
    {
        if (selection.Text == text) return;
        selection.Text = text;
        ToolTipService.SetToolTip(selection, text);
    }

    // Trimmed like the history query, so the same text filters both views alike.
    private bool Matches(ProcessIdentity process, string address, ConnectionLocation? location, long received, long sent) =>
        (applicationFilter.Length == 0 || ProcessGrouping.Key(process.Name) == applicationFilter)
        && (search.Text.Trim() is not { Length: > 0 } text || $"{process.Name} {process.Pid} {address}".Contains(text, StringComparison.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(country.Text) || string.Equals(location?.CountryCode, country.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        && direction.SelectedIndex switch { 1 => received > 0, 2 => sent > 0, _ => true };

    /// <summary>Asks for a new CSV file. The picker leaves an empty file at a new name, which is removed so the export, which only ever
    /// writes a new file, can create it; a file with content is refused as the export itself would refuse it.</summary>
    private async Task<string?> PickCsvAsync(string name)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}" };
        picker.FileTypeChoices.Add("CSV files", [".csv"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;
        if (new FileInfo(file.Path) is { Exists: true, Length: 0 }) File.Delete(file.Path);
        if (File.Exists(file.Path)) { SetActionStatus(ConnectionsPresenter.ExportExists); return null; }
        return file.Path;
    }

    private async Task ExportAsync()
    {
        if (exportInProgress) return;
        BuildQuery(out var query);
        if (query is null) return;
        exportInProgress = true; UpdateExportEnabled();
        try
        {
            if (await PickCsvAsync("connections") is not { } path) return;
            await session.Connections.ExportCsvAsync(path, query);
            SetActionStatus(ConnectionsPresenter.ExportDone);
        }
        catch (Exception error) { SetActionStatus(ConnectionsPresenter.ExportFailed(error)); }
        finally { exportInProgress = false; UpdateExportEnabled(); }
    }

    /// <summary>Exports the scope's destinations over the whole-interval query, every filter included and the minute cursor ignored, to a new file.</summary>
    private async Task ExportDestinationsAsync()
    {
        if (exportInProgress) return;
        BuildQuery(out var whole);
        if (whole is null) return;
        var scope = destinations.CurrentScope;
        exportInProgress = true; UpdateExportEnabled();
        try
        {
            if (await PickCsvAsync("destinations") is not { } path) return;
            await session.Connections.ExportDestinationsCsvAsync(path, whole, scope);
            SetActionStatus(ConnectionsPresenter.ExportDone);
        }
        catch (ConnectionHistoryLimitException) { SetActionStatus(DestinationCopy.ExportLimit); }
        catch (Exception error) { SetActionStatus(ConnectionsPresenter.ExportFailed(error)); }
        finally { exportInProgress = false; UpdateExportEnabled(); }
    }

    private sealed class QueueProgress(Microsoft.UI.Dispatching.DispatcherQueue queue, Action<double> report) : IProgress<double>
    {
        public void Report(double value) => queue.TryEnqueue(() => report(value));
    }

    private async Task UpdateDatabaseAsync()
    {
        if (downloadCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        downloadCancellation = cancellation; download.IsEnabled = false; cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.Value = 0;
        SetActionStatus("Downloading locations from download.db-ip.com…");
        try
        {
            await session.Connections.UpdateLocationDatabaseAsync(new QueueProgress(DispatcherQueue, value => progress.Value = Math.Clamp(value, 0, 1) * 100), cancellation.Token);
            SetActionStatus("Location database updated."); RefreshSession();
        }
        catch (OperationCanceledException) { SetActionStatus("Location download cancelled; the previous valid database remains available."); }
        catch (Exception error) { SetActionStatus($"Location update failed ({error.GetType().Name}); the previous valid database remains available."); }
        finally { downloadCancellation = null; download.IsEnabled = true; cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; }
    }
}
