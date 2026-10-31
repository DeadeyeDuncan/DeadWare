using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DeadWare.Core;
using Microsoft.Win32;

namespace DeadWare.App;

/// <summary>Shared live publication and serialized, read-only history queries.</summary>
internal sealed class ConnectionsPage : Grid
{
    private sealed record PeerRow(string Key, string Application, string Endpoint, string Location,
        string Download, string Upload, string Received, string Sent, string Coverage, string Seen,
        GeoPoint? Point, GeoPoint? Origin, long ReceivedBytes, long SentBytes);
    private sealed class MapRow(string key, string label) : INotifyPropertyChanged
    {
        public string Key { get; } = key;
        public string Label { get; private set; } = label;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string value)
        {
            if(Label==value)return;
            Label=value;PropertyChanged?.Invoke(this,new(nameof(Label)));
        }
        public override string ToString()=>Label;
    }
    private sealed record CityChoice(string? Id, string Label) { public override string ToString() => Label; }
    private sealed record HistoryPeerKey(long MinuteUtcTicks, ProcessIdentity Process, string RemoteAddress,
        int RemotePort, ConnectionProtocol Protocol, ConnectionLocation? Location, GeoPoint? Origin);
    private readonly UiSession session;
    private readonly Grid body=new();
    private readonly ScrollViewer viewport=new() {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private readonly StackPanel layoutHeader,layoutFooter,layoutSettings;
    private readonly Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>> readHistory;
    private readonly Func<string,ConnectionHistoryQuery,Task> exportHistory;
    private readonly ComboBox mode = new(), range = new(), direction = new();
    private readonly TextBox country = new() { Width = 65, MaxLength = 2, CharacterCasing = CharacterCasing.Upper };
    private readonly TextBox search = new() { Width = 170, MaxLength = 256 }, from = new() { Width = 139 }, to = new() { Width = 139 }, citySearch = new() { Width = 145, MaxLength = 128 };
    private readonly ComboBox home = new() { Width = 190 };
    private readonly CheckBox minute = new() { Content = "Minute cursor" }, recording = new() { Content = "Record history" }, animation = new() { Content = "Animate" };
    private readonly Slider cursor = new() { Minimum = 0, Maximum = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 170 };
    private readonly TextBlock summary = Ui.Text("Waiting for endpoint capture.", 12), capture = Ui.Text("", 11, "Muted"), storage = Ui.Text("", 11, "Muted"),
        actionStatus = Ui.Text("", 11, "Warning"), database = Ui.Text("DB-IP Lite · no location database · download contacts download.db-ip.com · DB-IP.com (CC BY 4.0).", 11, "Muted"), cursorTime = Ui.Text("", 11, "Muted"), selection = Ui.Text("", 11, "Muted");
    private readonly ConnectionMap map = new() { MinHeight = 260 };
    private readonly ListBox mappedConnections = new() { MaxHeight = 140, MinHeight = 45 };
    private readonly ObservableCollection<MapRow> mappedRows = [];
    private readonly Canvas mapOverlay = new() { ClipToBounds = true };
    private readonly Border nodeDetails = new() { Padding = new(10), BorderThickness = new(1), CornerRadius = new(5), Visibility = Visibility.Collapsed };
    private readonly TextBlock nodeText = Ui.Text("", 12);
    private readonly TextBlock mappedStatus = Ui.Text("", 11, "Muted");
    private readonly Button play, download, cancel, export;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Width = 80, Height = 6, Visibility = Visibility.Collapsed };
    private readonly StackPanel historical = new();
    private readonly DispatcherTimer playback = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? queryCancellation, downloadCancellation;
    private ConnectionHistoryQuery? pending;
    private ConnectionHistoryQuery? displayedHistoryQuery;
    private ConnectionLiveView live = new(default, [], null, false, false, "Waiting for endpoint capture.");
    private IReadOnlyList<PeerRow> rows = [];
    private long generation;
    private bool syncing, busy, playing, active, exportInProgress, historyMinuteReady;
    private string? selectedKey;
    private string historyStorage = "";
    private bool IsHistory => mode.SelectedIndex == 1;

    public ConnectionsPage(UiSession session)
    {
        this.session = session;
        viewport.Content=body;Children.Add(viewport);
        readHistory = q => session.Connections is { } service ? service.ReadViewAsync(q) : Task.FromResult(Empty(q, "Connection history is unavailable."));
        exportHistory=(path,query)=>session.Connections is { } service?service.ExportCsvAsync(path,query):Task.FromException(new InvalidOperationException("Connection export unavailable."));
        Background = System.Windows.Media.Brushes.Transparent; ClipToBounds = true;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) body.RowDefinitions.Add(new() { Height = height });
        body.RowDefinitions[1].MinHeight = 300;
        var controls = new StackPanel { Margin = new(0, 6, 0, 3) }; var top = new WrapPanel();
        mode.ItemsSource = new[] { "Live", "History" }; mode.SelectedIndex = 0; Named(mode, "Connections view"); range.ItemsSource = new[] { "Today", "7 days", "30 days" }; range.SelectedIndex = 0; Named(range, "Connections time range");
        top.Children.Add(mode); top.Children.Add(range); top.Children.Add(Button("Globe / 2D", () => map.Flat = !map.Flat)); top.Children.Add(Button("+", () => map.ZoomBy(1.25))); top.Children.Add(Button("−", () => map.ZoomBy(.8))); top.Children.Add(Button("Reset", map.ResetView));
        animation.IsChecked = session.Settings.ConnectionsAnimation; top.Children.Add(animation); controls.Children.Add(top);
        var dates = new WrapPanel(); dates.Children.Add(Label("From local", from, "History from local time")); dates.Children.Add(Label("To (exclusive)", to, "History to exclusive local time")); dates.Children.Add(Button("Apply interval", () => FiltersChanged())); historical.Children.Add(dates);
        var timeline = new WrapPanel(); timeline.Children.Add(minute); Named(cursor, "History minute cursor"); timeline.Children.Add(cursor); timeline.Children.Add(cursorTime); play = Button("Play", TogglePlayback); timeline.Children.Add(play); historical.Children.Add(timeline); controls.Children.Add(historical);
        var filters = new WrapPanel(); Named(search, "Search application or IP"); search.ToolTip = "Application name, PID or remote IP"; filters.Children.Add(Label("Application / IP", search, "Search application or IP")); country.ToolTip = "ISO country code; blank for all"; Named(country, "Country code"); filters.Children.Add(Label("Country code", country, "Country code"));
        direction.ItemsSource = Enum.GetValues<ConnectionDirection>(); direction.SelectedIndex = 0; Named(direction, "Traffic direction"); filters.Children.Add(Label("Direction", direction, "Traffic direction")); filters.Children.Add(Button("Filter", () => FiltersChanged())); controls.Children.Add(filters);
        var header=layoutHeader=new StackPanel();header.Children.Add(controls);header.Children.Add(summary);header.Children.Add(capture);body.Children.Add(header);
        var mapHost = new Grid(); mapHost.Children.Add(map); mapHost.Children.Add(mapOverlay); Grid.SetRow(mapHost, 1); body.Children.Add(mapHost);
        nodeDetails.SetResourceReference(Border.BackgroundProperty, "GlassPanel"); nodeDetails.SetResourceReference(Border.BorderBrushProperty, "GlassBorder");
        nodeText.TextWrapping = TextWrapping.Wrap;
        var detailsLayout = new StackPanel(); var detailHeader = new DockPanel(); var close = Button("Close", ClearSelection); DockPanel.SetDock(close, Dock.Right); detailHeader.Children.Add(close); detailHeader.Children.Add(Ui.Text("Connection details", 13)); detailsLayout.Children.Add(detailHeader);
        detailsLayout.Children.Add(new ScrollViewer { Content = nodeText, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); nodeDetails.Child = detailsLayout; mapOverlay.Children.Add(nodeDetails); Named(nodeDetails, "Selected map connection details");
        var listLayout = new StackPanel(); listLayout.Children.Add(mappedStatus); listLayout.Children.Add(mappedConnections); Grid.SetRow(listLayout, 2); body.Children.Add(listLayout);
        Named(mappedConnections, "Connections drawn on map");
        mappedConnections.ItemsSource=mappedRows;
        mappedConnections.SetResourceReference(Control.BackgroundProperty,"GlassControl");
        mappedConnections.SetResourceReference(Control.ForegroundProperty,"Ink");
        mappedConnections.SetResourceReference(Control.BorderBrushProperty,"GlassBorder");
        mappedConnections.SetResourceReference(Control.FontFamilyProperty,"BodyFont");
        ScrollViewer.SetHorizontalScrollBarVisibility(mappedConnections, ScrollBarVisibility.Disabled);
        var rowText = new FrameworkElementFactory(typeof(TextBlock)); rowText.SetBinding(TextBlock.TextProperty, new Binding("Label")); rowText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); rowText.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        mappedConnections.ItemTemplate = new DataTemplate { VisualTree = rowText };
        var itemStyle = new Style(typeof(ListBoxItem)); itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Ink")));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("GlassPanel")));
        var itemBorder=new FrameworkElementFactory(typeof(Border)); itemBorder.SetBinding(Border.BackgroundProperty,new Binding("Background") {RelativeSource=RelativeSource.TemplatedParent});
        itemBorder.SetValue(Border.PaddingProperty,new Thickness(3));itemBorder.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(ListBoxItem)) {VisualTree=itemBorder}));
        foreach(var property in new[]{ListBoxItem.IsSelectedProperty,UIElement.IsMouseOverProperty,UIElement.IsKeyboardFocusWithinProperty})
        {var trigger=new Trigger {Property=property,Value=true};trigger.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("GlassControl")));itemStyle.Triggers.Add(trigger);}
        mappedConnections.ItemContainerStyle = itemStyle;
        var foot = layoutFooter = new StackPanel(); export = Button("Export CSV…", () => _ = ExportAsync()); export.HorizontalAlignment = HorizontalAlignment.Left; foot.Children.Add(export); foot.Children.Add(selection); Grid.SetRow(foot, 3); body.Children.Add(foot);
        var settings = layoutSettings = new StackPanel(); var databaseActions = new WrapPanel(); download = Button("Download / update locations", () => _ = UpdateDatabaseAsync()); cancel = Button("Cancel download", () => downloadCancellation?.Cancel()); cancel.IsEnabled = false; databaseActions.Children.Add(download); databaseActions.Children.Add(cancel); databaseActions.Children.Add(progress); settings.Children.Add(databaseActions); settings.Children.Add(database);
        var options = new StackPanel(); var choices = new WrapPanel(); recording.IsChecked = session.Settings.ConnectionsRecording; databaseActions.Children.Add(recording); Named(citySearch, "Search offline home cities"); citySearch.ToolTip = "Search 7,342 offline cities by name or country"; choices.Children.Add(citySearch); Named(home, "Home city"); choices.Children.Add(home); choices.Children.Add(Button("Clear home city", () => home.SelectedIndex = 0)); options.Children.Add(choices); settings.Children.Add(storage);
        options.Children.Add(Ui.Text("Origin and destination geography are approximate; curves are illustrative. The list follows the nodes visible on the map. Local / special and unlocated peers remain in totals and history CSV. Rolling history: 30 days, up to 256 MiB locally. CSV exports the full filtered interval to a new file.", 11, "Muted")); settings.Children.Add(new Expander { Header = "Recording, origin and storage", Content = options }); Grid.SetRow(settings, 4); settings.Children.Insert(0, actionStatus); body.Children.Add(settings);
        foreach (var element in new[] { mode, range, direction }) element.Margin = new(0, 0, 6, 3); foreach (var element in new[] { search, from, to, citySearch, country }) element.Margin = new(0, 0, 6, 3);
        selection.MaxHeight = 28; selection.TextTrimming = TextTrimming.CharacterEllipsis;
        layoutHeader.SizeChanged+=(_,_)=>InvalidateMeasure();layoutFooter.SizeChanged+=(_,_)=>InvalidateMeasure();layoutSettings.SizeChanged+=(_,_)=>InvalidateMeasure();
        SetPreset(); FilterCities(); historical.Visibility = Visibility.Collapsed;
        mode.SelectionChanged += (_, _) => ChangeMode(); range.SelectionChanged += (_, _) => { SetPreset(); FiltersChanged(); }; search.TextChanged += (_, _) => FiltersChanged(); direction.SelectionChanged += (_, _) => FiltersChanged(); country.TextChanged += (_, _) => FiltersChanged();
        minute.Checked += (_, _) => FiltersChanged(); minute.Unchecked += (_, _) => FiltersChanged(); cursor.ValueChanged += (_, _) => { if (!syncing) FiltersChanged(false); }; citySearch.TextChanged += (_, _) => FilterCities(); home.SelectionChanged += (_, _) => { if (!syncing && session.Connections is not null) session.SetConnectionsHomeCity((home.SelectedItem as CityChoice)?.Id); };
        recording.Checked += (_, _) => SetRecording(true); recording.Unchecked += (_, _) => SetRecording(false); animation.Checked += (_, _) => SetAnimation(true); animation.Unchecked += (_, _) => SetAnimation(false);
        mappedConnections.SelectionChanged += (_, _) => { if (!syncing && mappedConnections.SelectedItem is MapRow row) SelectMapPeer(row.Key); };
        map.PointSelected += SelectMapPeer; map.ViewChanged += RefreshMappedConnections;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { ClearSelection(); e.Handled = true; } }; playback.Tick += (_, _) => AdvancePlayback();
        Loaded += (_, _) => { active = true; session.Changed += RefreshSession; RefreshSession(); if (IsHistory) RequestHistory(); }; Unloaded += (_, _) => { active = false; session.Changed -= RefreshSession; CancelHistory(); StopPlayback(); downloadCancellation?.Cancel(); }; IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelHistory(); StopPlayback(); } else if (active) { RefreshSession(); if (IsHistory) RequestHistory(); } }; RefreshSession();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        // Keep the map large while allowing the whole page to scroll on compact displays.
        double width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width-SystemParameters.VerticalScrollBarWidth):1000;
        var measure=new Size(width,double.PositiveInfinity);
        layoutHeader.Measure(measure);layoutFooter.Measure(measure);layoutSettings.Measure(measure);
        double minimum=layoutHeader.DesiredSize.Height+layoutFooter.DesiredSize.Height+layoutSettings.DesiredSize.Height+300+170;
        body.Height=Math.Max(minimum,double.IsFinite(availableSize.Height)?availableSize.Height:minimum);
        return base.MeasureOverride(availableSize);
    }
    private static void Named(DependencyObject element, string name) => AutomationProperties.SetName(element, name);
    private static UIElement Label(string text, FrameworkElement input, string name) { Named(input, name); var p = new StackPanel { Margin = new(0, 0, 6, 0) }; p.Children.Add(Ui.Text(text, 10, "Muted")); p.Children.Add(input); return p; }
    private static Button Button(string text, Action action) { var b = Ui.Button(text, action); b.Padding = new(7, 4, 7, 4); b.Margin = new(0, 0, 5, 3); Named(b, text); return b; }
    private static ConnectionHistoryView Empty(ConnectionHistoryQuery query, string status) => new(query, [], 0, new(0, 0, 0, 0, 0, 0), [], [], [], null, 0, status);
    private void SetRecording(bool enabled) { if (!syncing && session.Connections is not null) session.SetConnectionsRecording(enabled); }
    private void UpdateMapAnimation()=>map.AnimationEnabled=animation.IsChecked==true&&(!IsHistory||playing&&minute.IsChecked==true&&historyMinuteReady);
    private void SetAnimation(bool enabled) { UpdateMapAnimation(); if (!syncing && session.Connections is not null) session.SetConnectionsAnimation(enabled); }
    private void FilterCities()
    {
        string text = citySearch.Text.Trim(); syncing = true;
        try
        {
            string? selected = (home.SelectedItem as CityChoice)?.Id ?? session.Settings.ConnectionsHomeCityId;
            var matches = ConnectionAssets.Cities.Where(c => text.Length > 0 && (c.Name + " " + c.Country).Contains(text, StringComparison.OrdinalIgnoreCase)).Take(80).ToList(); var saved = ConnectionAssets.Cities.FirstOrDefault(c => c.Id == selected); if (saved is not null && !matches.Any(c => c.Id == selected)) matches.Insert(0, saved);
            home.ItemsSource = new[] { new CityChoice(null, "No origin location") }.Concat(matches.Select(c => new CityChoice(c.Id, $"{c.Name}, {c.Country}"))).ToArray(); home.SelectedItem = home.Items.Cast<CityChoice>().FirstOrDefault(c => c.Id == selected) ?? home.Items[0]; home.ToolTip = "Up to 80 matches; refine a city or country search to find more.";
        }
        finally { syncing = false; }
    }
    private void RefreshSession()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RefreshSession); return; }
        syncing = true; recording.IsChecked = session.Settings.ConnectionsRecording; animation.IsChecked = session.Settings.ConnectionsAnimation; UpdateMapAnimation(); syncing = false;
        if (session.Connections is { } service)
        {
            storage.Text = (IsHistory ? historyStorage : "") + service.StorageStatus; database.Text = $"DB-IP Lite · {(service.DatabaseDate is { } date ? $"database {date:yyyy-MM-dd}" : "no location database")} · Download contacts download.db-ip.com · DB-IP.com (CC BY 4.0). {service.LookupStatus}"; live = service.Live; if (!IsHistory) ApplyLive(live);
        }
        else if (!IsHistory) ApplyLive(live);
    }
    private void ChangeMode() { CancelHistory(); displayedHistoryQuery=null; StopPlayback(); ClearSelection(); historical.Visibility = IsHistory ? Visibility.Visible : Visibility.Collapsed; range.IsEnabled = IsHistory; UpdateExportEnabled(); if (IsHistory) RequestHistory(); else ApplyLive(live); }
    private void SetPreset()
    { int days = range.SelectedIndex switch { 1 => 7, 2 => 30, _ => 1 }; from.Text = DateTime.Today.AddDays(1 - days).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); to.Text = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); syncing = true; cursor.Value = 0; syncing = false; }
    private bool TryInterval(out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        if (!TryParseLocalTime(from.Text,TimeZoneInfo.Local, out start) || !TryParseLocalTime(to.Text,TimeZoneInfo.Local, out end) || end <= start || (end - start).TotalDays > 31) { actionStatus.Text = "Choose valid local times (yyyy-MM-dd HH:mm), from before exclusive to, at most 31 days. Avoid ambiguous or skipped daylight-saving minutes."; return false; }
        syncing = true; cursor.Maximum = Math.Max(0, Math.Ceiling((end - start).TotalMinutes) - 1); cursor.Value = Math.Min(cursor.Value, cursor.Maximum); syncing = false; return true;
    }
    private static bool TryParseLocalTime(string text,TimeZoneInfo zone,out DateTimeOffset utc)
    {
        utc=default;
        if(!DateTime.TryParseExact(text.Trim(),"yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed))return false;
        parsed=DateTime.SpecifyKind(parsed,DateTimeKind.Unspecified);
        if(zone.IsInvalidTime(parsed)||zone.IsAmbiguousTime(parsed))return false;
        try {utc=new DateTimeOffset(parsed,zone.GetUtcOffset(parsed)).ToUniversalTime();return true;}
        catch(ArgumentOutOfRangeException) {return false;}
    }
    private ConnectionHistoryQuery? BuildQuery(bool wholeInterval = false)
    {
        if (!TryInterval(out var start, out var end)) return null; if (!wholeInterval && minute.IsChecked == true) { start = start.AddMinutes((int)cursor.Value); end = end < start.AddMinutes(1) ? end : start.AddMinutes(1); }
        cursorTime.Text = minute.IsChecked == true ? $"{start.ToLocalTime():MM-dd HH:mm}" : "Whole interval";
        return new(start, end, search.Text.Trim(), country.Text.Trim().ToUpperInvariant(), direction.SelectedItem is ConnectionDirection value ? value : ConnectionDirection.Both, 0, 200);
    }
    private void FiltersChanged(bool resetCursor = true) { if (syncing) return; StopPlayback(); ClearSelection(); if (resetCursor) { syncing = true; cursor.Value = 0; syncing = false; } if (IsHistory) RequestHistory(); else ApplyLive(live); }
    private void CancelHistory() { generation++; queryCancellation?.Cancel(); queryCancellation?.Dispose(); queryCancellation = null; pending = null; historyMinuteReady=false; UpdateMapAnimation(); }
    private static bool SameHistoryContext(ConnectionHistoryQuery left,ConnectionHistoryQuery right)=>
        left.From==right.From&&left.To==right.To&&left.Direction==right.Direction&&
        string.Equals(left.Search,right.Search,StringComparison.OrdinalIgnoreCase)&&string.Equals(left.CountryCode,right.CountryCode,StringComparison.OrdinalIgnoreCase);
    private void RequestHistory()
    {
        CancelHistory(); var query=BuildQuery();
        if(query is null || displayedHistoryQuery is null || !SameHistoryContext(displayedHistoryQuery,query))
        {
            displayedHistoryQuery=null; rows=[];
            selectedKey=null; map.SetData([],null,true,"Reading saved history…"); RefreshMappedConnections(); summary.Text="History · Reading saved observations…";
        }
        if(query is null){StopPlayback();return;}
        queryCancellation=new();pending=query;_=PumpHistoryAsync();
    }
    private async Task PumpHistoryAsync()
    {
        if (busy) return; busy = true;
        try
        {
            while (pending is { } query && IsHistory)
            {
                pending = null; long requested = generation; var token = queryCancellation!.Token; capture.Text = "Reading saved history…";
                try { var result = await readHistory(query); if (!token.IsCancellationRequested && requested == generation && IsHistory) ApplyHistory(result); }
                catch (Exception error) { if (!token.IsCancellationRequested && requested == generation && IsHistory) { actionStatus.Text = $"History could not be read ({error.GetType().Name})."; ApplyHistory(Empty(query, "History unavailable; no saved observations shown.")); StopPlayback(); } }
            }
        }
        finally { busy = false; }
    }
    private void TogglePlayback() { if (playing) { StopPlayback(); return; } if (!IsHistory || !TryInterval(out _, out _)) return; syncing = true; minute.IsChecked = true; syncing = false; playing = true; play.Content = "Pause"; UpdateMapAnimation(); RequestHistory(); playback.Start(); }
    private void StopPlayback() { playing = false; playback.Stop(); play.Content = "Play"; UpdateMapAnimation(); }
    private void AdvancePlayback() { if (!playing || !IsHistory) { StopPlayback(); return; } if (busy || pending is not null) return; if (cursor.Value >= cursor.Maximum) { StopPlayback(); return; } syncing = true; cursor.Value++; syncing = false; RequestHistory(); }
    internal void ApplyLive(ConnectionLiveView view)
    {
        live = view; var filtered = view.Connections.Where(c => Matches(c.Traffic.Peer.Process, c.Traffic.Peer.RemoteAddress, c.Location, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray();
        rows = filtered.Where(c => c.Location?.Point is not null).Select(c => new PeerRow(Key(c.Traffic.Peer), $"{c.Traffic.Peer.Process.Name} / {c.Traffic.Peer.Process.Pid}", Endpoint(c.Traffic.Peer.RemoteAddress,c.Traffic.Peer.RemotePort,c.Traffic.Peer.Protocol), Location(c.Traffic.Peer.RemoteAddress, c.Location), Ui.Bytes(view.Available ? c.Traffic.DownloadBytesPerSecond : null, true), Ui.Bytes(view.Available ? c.Traffic.UploadBytesPerSecond : null, true), Ui.Bytes(c.Traffic.ReceivedBytes), Ui.Bytes(c.Traffic.SentBytes), view.Complete ? "Observed" : "Incomplete", $"First {c.Traffic.FirstSeen.ToLocalTime():g} · Last {c.Traffic.LastSeen.ToLocalTime():g}", c.Location?.Point, view.Origin, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray();
        long received = 0, sent = 0; foreach (var item in filtered) { received = Add(received, item.Traffic.ReceivedBytes); sent = Add(sent, item.Traffic.SentBytes); }
        int local = filtered.Where(c => Kind(c.Traffic.Peer.RemoteAddress) == NetworkAddressKind.Local).Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count(); int unlocated = filtered.Where(c => Kind(c.Traffic.Peer.RemoteAddress) == NetworkAddressKind.Public && c.Location?.Point is null).Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count();
        summary.Text = $"Live · {filtered.Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count()} destinations · {filtered.Select(c=>c.Location?.CountryCode).Where(c=>!string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} countries · {local} local · {unlocated} unlocated · ↓ {Ui.Bytes(received)} received / ↑ {Ui.Bytes(sent)} sent (totals for active peers since first observed)"; capture.Text = $"{view.Status}{(!view.Complete ? " · Incomplete capture" : "")} · Current observed rates; geography approximate; curves illustrative.";
        map.SetData(filtered.Where(c => c.Location?.Point is not null).Select(c => new ConnectionMapPoint(Key(c.Traffic.Peer), c.Location!.Point!, c.Traffic.ReceivedBytes, c.Traffic.SentBytes, selectedKey == Key(c.Traffic.Peer),view.Available&&c.Traffic.DownloadBytesPerSecond>0,view.Available&&c.Traffic.UploadBytesPerSecond>0)).ToArray(), view.Origin, view.Available, view.Status); UpdateMapAnimation(); RefreshMappedConnections(); UpdateExportEnabled(); range.IsEnabled = IsHistory;
    }
    internal void ApplyHistory(ConnectionHistoryView view)
    {
        displayedHistoryQuery=view.Query;
        historyMinuteReady=view.Query.To-view.Query.From<=TimeSpan.FromMinutes(1);
        rows = view.Markers.Select(m => m.Representative ?? view.Rows.FirstOrDefault(r => r.Location == m.Location && r.Origin == m.Origin)).OfType<ConnectionHistoryRow>().DistinctBy(Key).Select(r => new PeerRow(Key(r), $"{r.Process.Name} / {r.Process.Pid}", Endpoint(r.RemoteAddress,r.RemotePort,r.Protocol), Location(r.RemoteAddress, r.Location), Ui.Bytes(r.DownloadBytesPerSecond, true), Ui.Bytes(r.UploadBytesPerSecond, true), Ui.Bytes(r.ReceivedBytes), Ui.Bytes(r.SentBytes), r.Complete ? "Recorded" : "Incomplete", $"First {r.FirstSeen.ToLocalTime():g} · Last {r.LastSeen.ToLocalTime():g}", r.Location?.Point, r.Origin, r.ReceivedBytes, r.SentBytes)).ToArray(); var s = view.Summary;
        summary.Text = $"History · minute-bucket average rates · {s.Destinations} destinations · {s.Countries} countries · {s.Local} local · {s.Unlocated} unlocated · ↓ {Ui.Bytes(s.ReceivedBytes)} received / ↑ {Ui.Bytes(s.SentBytes)} sent"; capture.Text = $"{view.Query.From.ToLocalTime():g} – {view.Query.To.ToLocalTime():g} (exclusive) · {view.Gaps.Count} gaps · {view.Status}"+(view.OmittedMarkers>0?$" · {view.OmittedMarkers:N0} grouped locations omitted from map":""); historyStorage = $"{Ui.Bytes(view.StorageBytes)} stored · Earliest {(view.EarliestRetainedTime is { } earliest ? earliest.ToLocalTime().ToString("g") : "none")} · rolling 30 days / 256 MiB. "; storage.Text = historyStorage + session.Connections?.StorageStatus;
        map.SetData(view.Markers.Where(m => m.Location.Point is not null).Select(m => new ConnectionMapPoint(m.Representative is not null?Key(m.Representative):view.Rows.FirstOrDefault(r=>r.Location==m.Location&&r.Origin==m.Origin) is { } representative?Key(representative):"saved:"+JsonSerializer.Serialize(new {m.Location,m.Origin}), m.Location.Point!, m.ReceivedBytes, m.SentBytes, selectedKey == (m.Representative is not null ? Key(m.Representative) : null))).ToArray(), CommonOrigin(view.Markers), true, "Saved approximate geography · illustrative curves"+(view.OmittedMarkers>0?$" · +{view.OmittedMarkers:N0} grouped locations omitted":""));
        UpdateMapAnimation(); RefreshMappedConnections(); UpdateExportEnabled(); range.IsEnabled = IsHistory;
    }
    private static string Description(PeerRow row) => $"{row.Application} · {row.Endpoint} · {row.Location} · {row.Coverage} · {row.Seen}";
    private void UpdateExportEnabled()=>export.IsEnabled=IsHistory&&!exportInProgress;
    private void ClearSelection()
    {
        selectedKey=null; nodeDetails.Visibility=Visibility.Collapsed; nodeText.Text="";
        syncing=true; mappedConnections.SelectedItem=null; syncing=false;
        map.SelectPoint(null);
        RefreshMappedConnections();
    }
    private void SelectMapPeer(string key)
    {
        if(IsHistory&&displayedHistoryQuery is null || map.PointPosition(key) is null)return;
        if(!rows.Any(r=>r.Key==key))return;
        selectedKey=key;
        map.SelectPoint(key);
        RefreshMappedConnections();
    }
    private void RefreshMappedConnections()
    {
        var visible=map.DisplayedPoints.Where(p=>map.PointPosition(p.Key) is not null).OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray();
        var peers=rows.ToDictionary(r=>r.Key,StringComparer.Ordinal);
        var items=visible.Where(p=>peers.ContainsKey(p.Key)).Select(p=>
        {
            var row=peers[p.Key];
            return new MapRow(p.Key,$"{row.Location} · {row.Application} · ↓ {Ui.Bytes(p.ReceivedBytes)} / ↑ {Ui.Bytes(p.SentBytes)}");
        }).ToArray();
        // Preserve item containers, focus and scrolling while live traffic labels change.
        syncing=true;
        var wanted=items.Select(r=>r.Key).ToHashSet(StringComparer.Ordinal);
        for(int i=mappedRows.Count-1;i>=0;i--)if(!wanted.Contains(mappedRows[i].Key))mappedRows.RemoveAt(i);
        var existing=mappedRows.ToDictionary(r=>r.Key,StringComparer.Ordinal);
        for(int i=0;i<items.Length;i++)
        {
            if(existing.TryGetValue(items[i].Key,out var retained))
            {
                retained.Update(items[i].Label);int oldIndex=mappedRows.IndexOf(retained);
                if(oldIndex!=i)mappedRows.Move(oldIndex,i);
            }
            else mappedRows.Insert(i,items[i]);
        }
        mappedConnections.SelectedItem=mappedRows.FirstOrDefault(r=>r.Key==selectedKey);syncing=false;
        mappedStatus.Text=$"{(IsHistory?"History":"Live")} · {items.Length} visible map nodes · location totals; click for connection details";
        selection.Text=items.Length==0?"No located connections in this map view. Rotate the globe, change filters or download locations.":"Select a node or listed connection to see its details beside the node.";
        selection.ToolTip=selection.Text;
        if(selectedKey is not null && !map.DisplayedPoints.Any(p=>p.Key==selectedKey))selectedKey=null;
        if(selectedKey is null || !peers.TryGetValue(selectedKey,out var selected) || map.PointPosition(selectedKey) is not Point anchor)
        {nodeDetails.Visibility=Visibility.Collapsed;nodeText.Text="";return;}
        var point=visible.First(p=>p.Key==selectedKey);
        nodeText.Text=$"{(IsHistory?"Historical connection (saved minute)":"Live connection")}\n{selected.Application}\n{selected.Endpoint}\n{selected.Location}\n↓ {selected.Download}   ↑ {selected.Upload}\nReceived {selected.Received} · Sent {selected.Sent}\n{selected.Coverage} · {selected.Seen}\nLocation total: ↓ {Ui.Bytes(point.ReceivedBytes)} / ↑ {Ui.Bytes(point.SentBytes)}\nRepresentative connection at this grouped location.";
        selection.Text=Description(selected); selection.ToolTip=selection.Text;
        nodeDetails.Width=Math.Min(320,Math.Max(1,map.ActualWidth-16)); nodeDetails.Visibility=Visibility.Visible;
        nodeDetails.Measure(new(nodeDetails.Width,double.PositiveInfinity));
        double x=anchor.X+12;if(x+nodeDetails.Width>map.ActualWidth-8)x=anchor.X-nodeDetails.Width-12;
        Canvas.SetLeft(nodeDetails,Math.Clamp(x,8,Math.Max(8,map.ActualWidth-nodeDetails.Width-8)));
        Canvas.SetTop(nodeDetails,Math.Clamp(anchor.Y-20,8,Math.Max(8,map.ActualHeight-nodeDetails.DesiredSize.Height-8)));
    }
    private bool Matches(ProcessIdentity process, string address, ConnectionLocation? location, long received, long sent) => (search.Text.Length == 0 || $"{process.Name} {process.Pid} {address}".Contains(search.Text, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrWhiteSpace(country.Text) || string.Equals(location?.CountryCode, country.Text.Trim(), StringComparison.OrdinalIgnoreCase)) && (direction.SelectedItem is not ConnectionDirection selected || selected == ConnectionDirection.Both || selected == ConnectionDirection.Received && received > 0 || selected == ConnectionDirection.Sent && sent > 0);
    private static string Key(ConnectionPeer peer) => "live:"+JsonSerializer.Serialize(peer);
    private static string Endpoint(string address,int port,ConnectionProtocol protocol)=>$"{(IPAddress.TryParse(address,out var ip)&&ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?$"[{address}]":address)}:{port} {protocol}";
    // Match the store's grouping identity; aggregates and first/last observations may change on refresh.
    private static string Key(ConnectionHistoryRow row) => "history:" + JsonSerializer.Serialize(new HistoryPeerKey(
        row.Minute.UtcTicks, row.Process, row.RemoteAddress, row.RemotePort, row.Protocol, row.Location, row.Origin));
    private static NetworkAddressKind Kind(string address) => IPAddress.TryParse(address, out var ip) ? NetworkAddress.Classify(ip) : NetworkAddressKind.Special;
    private static string Location(string address, ConnectionLocation? location) => Kind(address) switch { NetworkAddressKind.Local => "Local", NetworkAddressKind.Special => "Special / unlocated", _ => location is null ? "Unlocated" : string.Join(", ", new[] { location.City, location.Region, location.Country }.Where(s => !string.IsNullOrEmpty(s))) };
    private static long Add(long left, long right) => right <= 0 ? left : left > long.MaxValue - right ? long.MaxValue : left + right;
    private static GeoPoint? CommonOrigin(IReadOnlyList<ConnectionHistoryMarker> markers)
    { var origins = markers.Select(m => m.Origin).Distinct().Take(2).ToArray(); return origins.Length == 1 ? origins[0] : null; }
    private async Task ExportAsync()
    {
        if(exportInProgress)return;
        if (session.Connections is null) { actionStatus.Text = "Connection export unavailable."; return; }
        var query = BuildQuery(true); if (query is null) return; var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = $"connections-{DateTime.Now:yyyyMMdd-HHmmss}.csv", OverwritePrompt = true }; if (dialog.ShowDialog() != true) return;
        await ExportToPathAsync(dialog.FileName,query);
    }
    private async Task ExportToPathAsync(string path,ConnectionHistoryQuery query)
    {
        if(exportInProgress)return;
        exportInProgress=true;UpdateExportEnabled();
        try {await exportHistory(path,query);actionStatus.Text="CSV exported to a new file. Saved history is unchanged.";}
        catch(Exception error) {actionStatus.Text=$"Export failed ({error.GetType().Name}); choose a new writable file.";}
        finally {exportInProgress=false;UpdateExportEnabled();}
    }
    private async Task UpdateDatabaseAsync()
    {
        if (downloadCancellation is not null) return; if (session.Connections is null) { actionStatus.Text = "Location database service unavailable."; return; }
        using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation; download.IsEnabled = false; cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.Value = 0; actionStatus.Text = "Downloading locations from download.db-ip.com…";
        try { await session.Connections.UpdateLocationDatabaseAsync(new Progress<double>(value => progress.Value = Math.Clamp(value, 0, 1) * 100), cancellation.Token); actionStatus.Text = "Location database updated."; RefreshSession(); } catch (OperationCanceledException) { actionStatus.Text = "Location download cancelled; the previous valid database remains available."; } catch (Exception error) { actionStatus.Text = $"Location update failed ({error.GetType().Name}); the previous valid database remains available."; } finally { downloadCancellation = null; download.IsEnabled = true; cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; }
    }
}
