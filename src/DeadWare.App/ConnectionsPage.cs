using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        actionStatus = Ui.Text("", 11, "Warning"), database = Ui.Text("DB-IP Lite · no location database · download contacts download.db-ip.com · DB-IP.com (CC BY 4.0).", 11, "Muted"), pages = Ui.Text("", 11, "Muted"), cursorTime = Ui.Text("", 11, "Muted"), selection = Ui.Text("", 11, "Muted");
    private readonly ConnectionMap map = new() { MinHeight = 260 };
    private readonly DataGrid table = new() { MinHeight = 164, EnableRowVirtualization = true, IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly Button previous, next, play, download, cancel, export;
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100, Width = 80, Height = 6, Visibility = Visibility.Collapsed };
    private readonly StackPanel historical = new();
    private readonly DispatcherTimer playback = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? queryCancellation, downloadCancellation;
    private ConnectionHistoryQuery? pending;
    private ConnectionLiveView live = new(default, [], null, false, false, "Waiting for endpoint capture.");
    private IReadOnlyList<PeerRow> rows = [];
    private IReadOnlyDictionary<string,ConnectionHistoryMarker> historyMarkers = new Dictionary<string,ConnectionHistoryMarker>();
    private long generation, totalRows;
    private int offset;
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
        SetResourceReference(BackgroundProperty, "Background"); ClipToBounds = true;
        foreach (var height in new[] { GridLength.Auto, new GridLength(2, GridUnitType.Star), new GridLength(4), new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) body.RowDefinitions.Add(new() { Height = height });
        body.RowDefinitions[1].MinHeight = 260; body.RowDefinitions[3].MinHeight = 164;
        var controls = new StackPanel { Margin = new(0, 6, 0, 3) }; var top = new WrapPanel();
        mode.ItemsSource = new[] { "Live", "History" }; mode.SelectedIndex = 0; Named(mode, "Connections view"); range.ItemsSource = new[] { "Today", "7 days", "30 days" }; range.SelectedIndex = 0; Named(range, "Connections time range");
        top.Children.Add(mode); top.Children.Add(range); top.Children.Add(Button("Globe / 2D", () => map.Flat = !map.Flat)); top.Children.Add(Button("+", () => map.ZoomBy(1.25))); top.Children.Add(Button("−", () => map.ZoomBy(.8))); top.Children.Add(Button("Reset", map.ResetView));
        animation.IsChecked = session.Settings.ConnectionsAnimation; top.Children.Add(animation); controls.Children.Add(top);
        var dates = new WrapPanel(); dates.Children.Add(Label("From local", from, "History from local time")); dates.Children.Add(Label("To (exclusive)", to, "History to exclusive local time")); dates.Children.Add(Button("Apply interval", () => FiltersChanged())); historical.Children.Add(dates);
        var timeline = new WrapPanel(); timeline.Children.Add(minute); Named(cursor, "History minute cursor"); timeline.Children.Add(cursor); timeline.Children.Add(cursorTime); play = Button("Play", TogglePlayback); timeline.Children.Add(play); historical.Children.Add(timeline); controls.Children.Add(historical);
        var filters = new WrapPanel(); Named(search, "Search application or IP"); search.ToolTip = "Application name, PID or remote IP"; filters.Children.Add(Label("Application / IP", search, "Search application or IP")); country.ToolTip = "ISO country code; blank for all"; Named(country, "Country code"); filters.Children.Add(Label("Country code", country, "Country code"));
        direction.ItemsSource = Enum.GetValues<ConnectionDirection>(); direction.SelectedIndex = 0; Named(direction, "Traffic direction"); filters.Children.Add(Label("Direction", direction, "Traffic direction")); filters.Children.Add(Button("Filter", () => FiltersChanged())); controls.Children.Add(filters);
        var header=layoutHeader=new StackPanel();header.Children.Add(controls);header.Children.Add(summary);header.Children.Add(capture);body.Children.Add(header);
        Grid.SetRow(map, 1); body.Children.Add(map);
        var splitter = new GridSplitter { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext }; splitter.SetResourceReference(BackgroundProperty, "Line"); Grid.SetRow(splitter, 2); body.Children.Add(splitter);
        Column("Application / PID", "Application", 1.4); Column("Remote / protocol", "Endpoint", 1.5); Column("Approx. location", "Location", 1.2); Column("↓ rate", "Download", .85); Column("↑ rate", "Upload", .85); Column("Received", "Received", .85); Column("Sent", "Sent", .85); Column("Coverage", "Coverage", .8); Named(table, "Connection peers"); Grid.SetRow(table, 3); body.Children.Add(table);
        var foot = layoutFooter = new StackPanel(); var pagination = new WrapPanel(); previous = Button("Previous", () => MovePage(-200)); next = Button("Next", () => MovePage(200)); pagination.Children.Add(previous); pagination.Children.Add(pages); pagination.Children.Add(next); export = Button("Export CSV…", () => _ = ExportAsync()); pagination.Children.Add(export); foot.Children.Add(pagination); foot.Children.Add(selection); Grid.SetRow(foot, 4); body.Children.Add(foot);
        var settings = layoutSettings = new StackPanel(); var databaseActions = new WrapPanel(); download = Button("Download / update locations", () => _ = UpdateDatabaseAsync()); cancel = Button("Cancel download", () => downloadCancellation?.Cancel()); cancel.IsEnabled = false; databaseActions.Children.Add(download); databaseActions.Children.Add(cancel); databaseActions.Children.Add(progress); settings.Children.Add(databaseActions); settings.Children.Add(database);
        var options = new StackPanel(); var choices = new WrapPanel(); recording.IsChecked = session.Settings.ConnectionsRecording; databaseActions.Children.Add(recording); Named(citySearch, "Search offline home cities"); citySearch.ToolTip = "Search 7,342 offline cities by name or country"; choices.Children.Add(citySearch); Named(home, "Home city"); choices.Children.Add(home); choices.Children.Add(Button("Clear home city", () => home.SelectedIndex = 0)); options.Children.Add(choices); settings.Children.Add(storage);
        options.Children.Add(Ui.Text("Origin and destination geography are approximate; curves are illustrative. Local / special and unlocated peers remain in the table. Rolling history: 30 days, up to 256 MiB locally. CSV exports the full filtered interval to a new file.", 11, "Muted")); settings.Children.Add(new Expander { Header = "Recording, origin and storage", Content = options }); Grid.SetRow(settings, 5); settings.Children.Insert(0, actionStatus); body.Children.Add(settings);
        foreach (var element in new[] { mode, range, direction }) element.Margin = new(0, 0, 6, 3); foreach (var element in new[] { search, from, to, citySearch, country }) element.Margin = new(0, 0, 6, 3);
        selection.MaxHeight = 28; selection.TextTrimming = TextTrimming.CharacterEllipsis;
        layoutHeader.SizeChanged+=(_,_)=>InvalidateMeasure();layoutFooter.SizeChanged+=(_,_)=>InvalidateMeasure();layoutSettings.SizeChanged+=(_,_)=>InvalidateMeasure();
        SetPreset(); FilterCities(); historical.Visibility = Visibility.Collapsed;
        mode.SelectionChanged += (_, _) => ChangeMode(); range.SelectionChanged += (_, _) => { SetPreset(); FiltersChanged(); }; search.TextChanged += (_, _) => FiltersChanged(); direction.SelectionChanged += (_, _) => FiltersChanged(); country.TextChanged += (_, _) => FiltersChanged();
        minute.Checked += (_, _) => FiltersChanged(); minute.Unchecked += (_, _) => FiltersChanged(); cursor.ValueChanged += (_, _) => { if (!syncing) FiltersChanged(false); }; citySearch.TextChanged += (_, _) => FilterCities(); home.SelectionChanged += (_, _) => { if (!syncing && session.Connections is not null) session.SetConnectionsHomeCity((home.SelectedItem as CityChoice)?.Id); };
        recording.Checked += (_, _) => SetRecording(true); recording.Unchecked += (_, _) => SetRecording(false); animation.Checked += (_, _) => SetAnimation(true); animation.Unchecked += (_, _) => SetAnimation(false);
        table.SelectionChanged += (_, _) => { if (!syncing) { selectedKey = (table.SelectedItem as PeerRow)?.Key; RefreshSelection(); } }; map.PointSelected += SelectMapPeer; playback.Tick += (_, _) => AdvancePlayback();
        Loaded += (_, _) => { active = true; session.Changed += RefreshSession; RefreshSession(); if (IsHistory) RequestHistory(); }; Unloaded += (_, _) => { active = false; session.Changed -= RefreshSession; CancelHistory(); StopPlayback(); downloadCancellation?.Cancel(); }; IsVisibleChanged += (_, _) => { if (!IsVisible) { CancelHistory(); StopPlayback(); } else if (active) { RefreshSession(); if (IsHistory) RequestHistory(); } }; RefreshSession();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        // Scroll the page as one surface, but give star rows a finite height so the peer grid
        // retains its own bounded viewport and only realizes visible rows.
        double width=double.IsFinite(availableSize.Width)?Math.Max(1,availableSize.Width-SystemParameters.VerticalScrollBarWidth):1000;
        var measure=new Size(width,double.PositiveInfinity);
        layoutHeader.Measure(measure);layoutFooter.Measure(measure);layoutSettings.Measure(measure);
        double minimum=layoutHeader.DesiredSize.Height+layoutFooter.DesiredSize.Height+layoutSettings.DesiredSize.Height+260+164+4;
        body.Height=Math.Max(minimum,double.IsFinite(availableSize.Height)?availableSize.Height:minimum);
        return base.MeasureOverride(availableSize);
    }
    private static void Named(DependencyObject element, string name) => AutomationProperties.SetName(element, name);
    private static UIElement Label(string text, FrameworkElement input, string name) { Named(input, name); var p = new StackPanel { Margin = new(0, 0, 6, 0) }; p.Children.Add(Ui.Text(text, 10, "Muted")); p.Children.Add(input); return p; }
    private static Button Button(string text, Action action) { var b = Ui.Button(text, action); b.Padding = new(7, 4, 7, 4); b.Margin = new(0, 0, 5, 3); Named(b, text); return b; }
    private void Column(string title, string binding, double weight) => table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = new(weight, DataGridLengthUnitType.Star), MinWidth = 52 });
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
    private void ChangeMode() { CancelHistory(); StopPlayback(); offset = 0; historical.Visibility = IsHistory ? Visibility.Visible : Visibility.Collapsed; range.IsEnabled = IsHistory; UpdateExportEnabled(); if (IsHistory) RequestHistory(); else ApplyLive(live); }
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
        return new(start, end, search.Text.Trim(), country.Text.Trim().ToUpperInvariant(), direction.SelectedItem is ConnectionDirection value ? value : ConnectionDirection.Both, wholeInterval ? 0 : offset, 200);
    }
    private void FiltersChanged(bool resetCursor = true) { if (syncing) return; StopPlayback(); selectedKey=null; offset = 0; if (resetCursor) { syncing = true; cursor.Value = 0; syncing = false; } if (IsHistory) RequestHistory(); else ApplyLive(live); }
    private void CancelHistory() { generation++; queryCancellation?.Cancel(); queryCancellation?.Dispose(); queryCancellation = null; pending = null; historyMinuteReady=false; UpdateMapAnimation(); }
    private void RequestHistory() { CancelHistory(); var query = BuildQuery(); if (query is null) { StopPlayback(); return; } queryCancellation = new(); pending = query; _ = PumpHistoryAsync(); }
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
    private void MovePage(int delta) { if (delta < 0 && offset == 0 || delta > 0 && offset + 200 >= totalRows) return; offset = Math.Max(0, offset + delta); if (IsHistory) RequestHistory(); else ApplyLive(live); }
    private void TogglePlayback() { if (playing) { StopPlayback(); return; } if (!IsHistory || !TryInterval(out _, out _)) return; syncing = true; minute.IsChecked = true; syncing = false; offset = 0; playing = true; play.Content = "Pause"; UpdateMapAnimation(); RequestHistory(); playback.Start(); }
    private void StopPlayback() { playing = false; playback.Stop(); play.Content = "Play"; UpdateMapAnimation(); }
    private void AdvancePlayback() { if (!playing || !IsHistory) { StopPlayback(); return; } if (busy || pending is not null) return; if (cursor.Value >= cursor.Maximum) { StopPlayback(); return; } syncing = true; cursor.Value++; syncing = false; offset = 0; RequestHistory(); }
    internal void ApplyLive(ConnectionLiveView view)
    {
        live = view; var filtered = view.Connections.Where(c => Matches(c.Traffic.Peer.Process, c.Traffic.Peer.RemoteAddress, c.Location, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray(); totalRows = filtered.Length; offset = Math.Min(offset, Math.Max(0, ((filtered.Length - 1) / 200) * 200));
        rows = filtered.Skip(offset).Take(200).Select(c => new PeerRow(Key(c.Traffic.Peer), $"{c.Traffic.Peer.Process.Name} / {c.Traffic.Peer.Process.Pid}", Endpoint(c.Traffic.Peer.RemoteAddress,c.Traffic.Peer.RemotePort,c.Traffic.Peer.Protocol), Location(c.Traffic.Peer.RemoteAddress, c.Location), Ui.Bytes(view.Available ? c.Traffic.DownloadBytesPerSecond : null, true), Ui.Bytes(view.Available ? c.Traffic.UploadBytesPerSecond : null, true), Ui.Bytes(c.Traffic.ReceivedBytes), Ui.Bytes(c.Traffic.SentBytes), view.Complete ? "Observed" : "Incomplete", $"First {c.Traffic.FirstSeen.ToLocalTime():g} · Last {c.Traffic.LastSeen.ToLocalTime():g}", c.Location?.Point, view.Origin, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray();
        long received = 0, sent = 0; foreach (var item in filtered) { received = Add(received, item.Traffic.ReceivedBytes); sent = Add(sent, item.Traffic.SentBytes); }
        int local = filtered.Where(c => Kind(c.Traffic.Peer.RemoteAddress) == NetworkAddressKind.Local).Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count(); int unlocated = filtered.Where(c => Kind(c.Traffic.Peer.RemoteAddress) == NetworkAddressKind.Public && c.Location?.Point is null).Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count();
        summary.Text = $"Live · {filtered.Select(c => c.Traffic.Peer.RemoteAddress).Distinct().Count()} destinations · {filtered.Select(c=>c.Location?.CountryCode).Where(c=>!string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} countries · {local} local · {unlocated} unlocated · ↓ {Ui.Bytes(received)} received / ↑ {Ui.Bytes(sent)} sent (totals for active peers since first observed)"; capture.Text = $"{view.Status}{(!view.Complete ? " · Incomplete capture" : "")} · Current observed rates; geography approximate; curves illustrative.";
        BindRows(); map.SetData(filtered.Where(c => c.Location?.Point is not null).Select(c => new ConnectionMapPoint(Key(c.Traffic.Peer), c.Location!.Point!, c.Traffic.ReceivedBytes, c.Traffic.SentBytes, selectedKey == Key(c.Traffic.Peer),view.Available&&c.Traffic.DownloadBytesPerSecond>0,view.Available&&c.Traffic.UploadBytesPerSecond>0)).ToArray(), view.Origin, view.Available, view.Status); UpdateMapAnimation(); RefreshPager();
    }
    internal void ApplyHistory(ConnectionHistoryView view)
    {
        historyMinuteReady=view.Query.To-view.Query.From<=TimeSpan.FromMinutes(1);
        historyMarkers=view.Markers.Where(m=>m.Representative is not null).ToDictionary(m=>Key(m.Representative!),StringComparer.Ordinal);
        totalRows = view.TotalRows; offset = view.Query.Offset; rows = view.Rows.Select(r => new PeerRow(Key(r), $"{r.Process.Name} / {r.Process.Pid}", Endpoint(r.RemoteAddress,r.RemotePort,r.Protocol), Location(r.RemoteAddress, r.Location), Ui.Bytes(r.DownloadBytesPerSecond, true), Ui.Bytes(r.UploadBytesPerSecond, true), Ui.Bytes(r.ReceivedBytes), Ui.Bytes(r.SentBytes), r.Complete ? "Recorded" : "Incomplete", $"First {r.FirstSeen.ToLocalTime():g} · Last {r.LastSeen.ToLocalTime():g}", r.Location?.Point, r.Origin, r.ReceivedBytes, r.SentBytes)).ToArray(); var s = view.Summary;
        summary.Text = $"History · minute-bucket average rates · {s.Destinations} destinations · {s.Countries} countries · {s.Local} local · {s.Unlocated} unlocated · ↓ {Ui.Bytes(s.ReceivedBytes)} received / ↑ {Ui.Bytes(s.SentBytes)} sent"; capture.Text = $"{view.Query.From.ToLocalTime():g} – {view.Query.To.ToLocalTime():g} (exclusive) · {view.Gaps.Count} gaps · {view.Status}"+(view.OmittedMarkers>0?$" · {view.OmittedMarkers:N0} grouped locations omitted from map":""); historyStorage = $"{Ui.Bytes(view.StorageBytes)} stored · Earliest {(view.EarliestRetainedTime is { } earliest ? earliest.ToLocalTime().ToString("g") : "none")} · rolling 30 days / 256 MiB. "; storage.Text = historyStorage + session.Connections?.StorageStatus;
        BindRows(); map.SetData(view.Markers.Where(m => m.Location.Point is not null).Select(m => new ConnectionMapPoint(m.Representative is not null?Key(m.Representative):view.Rows.FirstOrDefault(r=>r.Location==m.Location&&r.Origin==m.Origin) is { } representative?Key(representative):"saved:"+JsonSerializer.Serialize(new {m.Location,m.Origin}), m.Location.Point!, m.ReceivedBytes, m.SentBytes, false)).ToArray(), CommonOrigin(view.Markers), true, "Saved approximate geography · illustrative curves"+(view.OmittedMarkers>0?$" · +{view.OmittedMarkers:N0} grouped locations omitted":""));
        if(IsHistory&&table.SelectedItem is not null){RefreshSelection();table.ScrollIntoView(table.SelectedItem);}
        UpdateMapAnimation();
        RefreshPager();
    }
    private void BindRows() { syncing = true; table.ItemsSource = rows; table.SelectedItem = rows.FirstOrDefault(r => r.Key == selectedKey); syncing = false; selection.Text = table.SelectedItem is PeerRow row ? Description(row) : rows.Count == 0 ? "No observations match this interval and filter. Gaps remain empty." : "Select a peer to highlight its saved / approximate location."; selection.ToolTip = selection.Text; }
    private void RefreshPager() { pages.Text = $"{(totalRows == 0 ? 0 : offset + 1)}–{Math.Min(offset + 200, totalRows)} of {totalRows:N0} rows · 200 per page"; previous.IsEnabled = offset > 0; next.IsEnabled = offset + 200 < totalRows; UpdateExportEnabled(); range.IsEnabled = IsHistory; }
    private static string Description(PeerRow row) => $"{row.Application} · {row.Endpoint} · {row.Location} · {row.Coverage} · {row.Seen}";
    private void UpdateExportEnabled()=>export.IsEnabled=IsHistory&&!exportInProgress;
    private void SelectMapPeer(string key)
    {
        if(IsHistory&&historyMarkers.TryGetValue(key,out var marker)&&!rows.Any(r=>r.Key==key))
        {
            if(marker.RowIndex>int.MaxValue){selection.Text="This saved group exceeds the table paging range; refine filters.";return;}
            selectedKey=key;offset=(int)(marker.RowIndex/200*200);RequestHistory();return;
        }
        if (!IsHistory)
        {
            var matching = live.Connections.Where(c => Matches(c.Traffic.Peer.Process, c.Traffic.Peer.RemoteAddress, c.Location, c.Traffic.ReceivedBytes, c.Traffic.SentBytes)).ToArray();
            int index = Array.FindIndex(matching, c => Key(c.Traffic.Peer) == key);
            if (index >= 0) { selectedKey = key; offset = index / 200 * 200; ApplyLive(live); }
        }
        var row = rows.FirstOrDefault(r => r.Key == key);
        if (row is not null) { table.SelectedItem = row; table.ScrollIntoView(row); RefreshSelection(); }
        else selection.Text = "Grouped location outside this table page; refine filters or change pages to inspect its peers.";
    }
    private void RefreshSelection() { var selected = table.SelectedItem as PeerRow; if (selected is null) return; selection.Text = Description(selected); selection.ToolTip = selection.Text; if (IsHistory && selected.Point is { } point) map.SetData([new(selected.Key, point, selected.ReceivedBytes, selected.SentBytes, true)], selected.Origin, true, "Saved location / origin · illustrative curve"); else if (IsHistory) map.SetData([], null, true, "Selected peer has no saved geographic point."); else ApplyLive(live); }
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
