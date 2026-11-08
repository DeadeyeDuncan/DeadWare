using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>What a row's cells depend on besides its destination: the mode, the Live view's capture state, whether a location database is installed and,
/// in Live, which apps are in Don't record these apps (<see cref="UiSession.IsExcluded"/>; null marks none, as History rows pass).</summary>
internal sealed record DestinationRowContext(bool History, bool Available, bool Complete, bool HasLocationDatabase, Func<string, bool>? IsExcluded = null);

/// <summary>One Destinations table row, kept per address. Every "—" has a tooltip naming its reason.</summary>
internal sealed class DestinationRow : INotifyPropertyChanged
{
    private sealed record Cells(string Destination, string Class, string Location, string Apps, string Ports, string Download, string Upload,
        string Received, string Sent, string Connections, string Minutes, string FirstSeen, string LastSeen,
        string? DestinationTip, string? ClassTip, string? LocationTip, string? AppsTip, string? PortsTip, string? DownloadTip, string? UploadTip, string Spoken);
    private const string Dash = "—";
    private Cells cells = null!;

    internal DestinationRow(ConnectionDestination value, DestinationRowContext context) { Key = value.Address; Update(value, context); }

    /// <summary>The normalized remote address.</summary>
    public string Key { get; }
    public string Destination => cells.Destination;
    public string Class => cells.Class;
    public string Location => cells.Location;
    public string Apps => cells.Apps;
    public string Ports => cells.Ports;
    public string Download => cells.Download;
    public string Upload => cells.Upload;
    public string Received => cells.Received;
    public string Sent => cells.Sent;
    public string Connections => cells.Connections;
    public string Minutes => cells.Minutes;
    public string FirstSeen => cells.FirstSeen;
    public string LastSeen => cells.LastSeen;
    public string? DestinationTip => cells.DestinationTip;
    public string? ClassTip => cells.ClassTip;
    public string? LocationTip => cells.LocationTip;
    public string? AppsTip => cells.AppsTip;
    public string? PortsTip => cells.PortsTip;
    public string? DownloadTip => cells.DownloadTip;
    public string? UploadTip => cells.UploadTip;
    /// <summary>The row's spoken text, for its automation name.</summary>
    public string Spoken => cells.Spoken;
    internal ConnectionDestination Value { get; private set; } = null!;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The shown text of one column's cell, as Copy row copies it.</summary>
    internal string Cell(ConnectionDestinationColumn column) => column switch
    {
        ConnectionDestinationColumn.Destination => Destination,
        ConnectionDestinationColumn.Class => Class,
        ConnectionDestinationColumn.Location => Location,
        ConnectionDestinationColumn.Apps => Apps,
        ConnectionDestinationColumn.Ports => Ports,
        ConnectionDestinationColumn.Download => Download,
        ConnectionDestinationColumn.Upload => Upload,
        ConnectionDestinationColumn.Received => Received,
        ConnectionDestinationColumn.Sent => Sent,
        ConnectionDestinationColumn.Connections => Connections,
        ConnectionDestinationColumn.Minutes => Minutes,
        ConnectionDestinationColumn.FirstSeen => FirstSeen,
        ConnectionDestinationColumn.LastSeen => LastSeen,
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <summary>Sets every cell's text and tooltip; raises one PropertyChanged(string.Empty) only when one of them changed.</summary>
    internal DestinationRow Update(ConnectionDestination value, DestinationRowContext context)
    {
        Value = value;
        var next = Build(value, context);
        if (next == cells) return this;
        bool first = cells is null;
        cells = next;
        if (!first) PropertyChanged?.Invoke(this, new(string.Empty));
        return this;
    }

    private static Cells Build(ConnectionDestination d, DestinationRowContext c)
    {
        var kind = d.Class.Kind;
        bool incomplete = c.History ? d.Complete == false : !c.Complete;
        var destinationTip = new List<string> { d.Address };
        if (!c.History) destinationTip.Add(DestinationCopy.ObservedSince(d.FirstSeen));
        if (incomplete) destinationTip.Add(c.History ? DestinationCopy.HistoryIncomplete : DestinationCopy.LiveIncomplete);
        string location; string? locationTip;
        switch (kind)
        {
            case NetworkAddressKind.Local: location = Dash; locationTip = DestinationCopy.LocalNotLocated; break;
            case NetworkAddressKind.Special: location = Dash; locationTip = DestinationCopy.SpecialNotLocated; break;
            default:
                if (d.Location is { } at)
                {
                    location = string.Join(", ", new[] { at.City, at.Region, at.Country }.Where(s => !string.IsNullOrEmpty(s)));
                    locationTip = d.Points.Count == 0 ? DestinationCopy.CountryOnly : d.Unlocated ? DestinationCopy.SomeMinutesUnlocated : null;
                }
                else
                {
                    location = Dash;
                    locationTip = c.History ? DestinationCopy.NoSavedLocation : c.HasLocationDatabase ? DestinationCopy.NotInDatabase : DestinationCopy.NoDatabase;
                }
                break;
        }
        // An app in Don't record these apps carries the " · not recorded" suffix after its name, in Live only; grouping, sorting and search keep the bare name.
        bool Excluded(string name) => !c.History && c.IsExcluded is { } excluded && excluded(name);
        bool busiestExcluded = d.Apps.Count > 0 && Excluded(d.Apps[0].Name);
        string apps = d.Apps.Count == 0 ? "" : d.Apps[0].Name + (busiestExcluded ? ProcessCopy.NotRecordedSuffix : "") + More(d.AppCount);
        string? appsTip = d.Apps.Count == 0 ? null : Lines(d.Apps.Select(a => Line(Excluded(a.Name) ? a.Name + ProcessCopy.NotRecordedSuffix : a.Name, a.ReceivedBytes, a.SentBytes)), d.AppCount - d.Apps.Count);
        string ports = d.Ports.Count == 0 ? "" : Port(d.Ports[0]) + More(d.PortCount);
        string? portsTip = d.Ports.Count == 0 ? null : Lines(d.Ports.Select(p => Line(Port(p), p.ReceivedBytes, p.SentBytes)), d.PortCount - d.Ports.Count);
        string download = c.History ? "" : Ui.Bytes(d.DownloadBytesPerSecond, true), upload = c.History ? "" : Ui.Bytes(d.UploadBytesPerSecond, true);
        string? downloadTip = !c.History && d.DownloadBytesPerSecond is null ? DestinationCopy.RateUnavailable : null;
        string? uploadTip = !c.History && d.UploadBytesPerSecond is null ? DestinationCopy.RateUnavailable : null;
        string received = Ui.Bytes(d.ReceivedBytes), sent = Ui.Bytes(d.SentBytes);
        string connections = d.Connections?.ToString("N0", CultureInfo.CurrentCulture) ?? "", minutes = d.Minutes?.ToString("N0", CultureInfo.CurrentCulture) ?? "";
        string firstSeen = c.History ? d.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
        string lastSeen = c.History ? d.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";
        // "{address}, {class}, {location}, {n} app(s): {busiest} and n more, port {busiest} and n more, received {x}, sent {y}", then the mode's own parts.
        var spoken = new List<string> { d.Address, d.Class.Label, location == Dash ? $"location unknown, {locationTip!.TrimEnd('.')}" : location };
        spoken.Add(d.Apps.Count == 0 ? "no apps" : $"{Count(d.AppCount, "app", "apps")}: {d.Apps[0].Name}{(busiestExcluded ? ", " + ProcessCopy.NotRecorded : "")}{SpokenMore(d.AppCount)}");
        spoken.Add(d.Ports.Count == 0 ? "no ports" : $"port {Port(d.Ports[0])}{SpokenMore(d.PortCount)}");
        spoken.Add($"received {received}"); spoken.Add($"sent {sent}");
        if (c.History)
        {
            if (d.Minutes is { } count) spoken.Add(Count(count, "minute", "minutes"));
            spoken.Add($"last seen {lastSeen}");
        }
        else
        {
            spoken.Add($"download {(download == Dash ? "unknown" : download)}"); spoken.Add($"upload {(upload == Dash ? "unknown" : upload)}");
            if (d.Connections is { } count) spoken.Add(Count(count, "connection", "connections"));
        }
        if (incomplete) spoken.Add("incomplete");
        return new(d.Address, d.Class.Label, location, apps, ports, download, upload, received, sent, connections, minutes, firstSeen, lastSeen,
            string.Join("\n", destinationTip), DestinationCopy.ClassTip(kind), locationTip, appsTip, portsTip, downloadTip, uploadTip, string.Join(", ", spoken));
    }

    private static string Port(ConnectionDestinationPort port) => $"{port.Port} {port.Protocol}";
    private static string More(int count) => count > 1 ? $" +{count - 1:N0}" : "";
    private static string SpokenMore(int count) => count > 1 ? $" and {count - 1:N0} more" : "";
    private static string Count(long count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";
    private static string Line(string name, long received, long sent) => $"{name} · ↓ {Ui.Bytes(received)} · ↑ {Ui.Bytes(sent)}";
    private static string Lines(IEnumerable<string> lines, int more) => string.Join("\n", more > 0 ? lines.Append(DestinationCopy.MoreLine(more)) : lines);
}

/// <summary>The Destinations card below the map: one row per remote address in Live and in History, narrowed by the All / Internet / Local scope.
/// Rows update in place and stay calm: new data re-sorts only outside a hold, while header clicks, the scope, F5, a mode change and filter changes re-sort at once.</summary>
internal sealed class DestinationsCard : Border
{
    /// <summary>How a show orders the rows: re-sorted at once, re-sorted unless the order is held (new data), or kept in place (same data, other values).</summary>
    private enum Order { Resort, NewData, Values }
    private sealed record ColumnSpec(ConnectionDestinationColumn Key, string Header, string Binding, string? Tip, double Star, double Listed, bool Value);
    // Star sizes and listed minimums; each MinWidth also fits the header, and FIRST SEEN and LAST SEEN fit a time.
    private static readonly ColumnSpec[] Specs =
    [
        new(ConnectionDestinationColumn.Destination, "DESTINATION", nameof(DestinationRow.Destination), nameof(DestinationRow.DestinationTip), 1.5, 120, false),
        new(ConnectionDestinationColumn.Class, "CLASS", nameof(DestinationRow.Class), nameof(DestinationRow.ClassTip), 1.1, 96, false),
        new(ConnectionDestinationColumn.Location, "LOCATION", nameof(DestinationRow.Location), nameof(DestinationRow.LocationTip), 1.6, 120, false),
        new(ConnectionDestinationColumn.Apps, "APPS", nameof(DestinationRow.Apps), nameof(DestinationRow.AppsTip), 1.3, 100, false),
        new(ConnectionDestinationColumn.Ports, "PORTS", nameof(DestinationRow.Ports), nameof(DestinationRow.PortsTip), 1, 80, false),
        new(ConnectionDestinationColumn.Download, "↓ DOWNLOAD", nameof(DestinationRow.Download), nameof(DestinationRow.DownloadTip), .8, 72, true),
        new(ConnectionDestinationColumn.Upload, "↑ UPLOAD", nameof(DestinationRow.Upload), nameof(DestinationRow.UploadTip), .8, 72, true),
        new(ConnectionDestinationColumn.Received, "RECEIVED", nameof(DestinationRow.Received), null, .8, 72, true),
        new(ConnectionDestinationColumn.Sent, "SENT", nameof(DestinationRow.Sent), null, .8, 72, true),
        new(ConnectionDestinationColumn.Connections, "CONNECTIONS", nameof(DestinationRow.Connections), null, .8, 72, true),
        new(ConnectionDestinationColumn.Minutes, "MINUTES", nameof(DestinationRow.Minutes), null, .8, 72, true),
        new(ConnectionDestinationColumn.FirstSeen, "FIRST SEEN", nameof(DestinationRow.FirstSeen), null, 1.1, 110, true),
        new(ConnectionDestinationColumn.LastSeen, "LAST SEEN", nameof(DestinationRow.LastSeen), null, 1.1, 110, true),
    ];
    // The theme's column header: 10 px text with 9 px side padding (App.xaml); the cell's 7 px side padding.
    private const double HeaderFontSize = 10, HeaderPadding = 18, CellPadding = 14;
    private static readonly Style TextCell = TextCellStyle();
    // Follows the theme's body font, so a theme change measures the minimum widths again.
    private static readonly DependencyProperty BodyFontProperty = DependencyProperty.Register("BodyFont", typeof(FontFamily), typeof(DestinationsCard),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private readonly Dictionary<ConnectionDestinationColumn, DataGridColumn> columns = [];
    private readonly Dictionary<string, DestinationRow> kept = new(StringComparer.Ordinal);
    private readonly TextBlock emptyText = Ui.Text("", 12, "Muted");
    private ConnectionDestinationColumn sortKey = ConnectionDestinationColumn.Received;
    private bool sortDescending = true, syncing;
    private bool? history;
    // Shows the last data again, ordered as asked (a scope change re-sorts).
    private Action<Order>? reshow;
    // The last Live view shown with what else its rows depend on; an equal one changes nothing. Null outside Live.
    private (ConnectionLiveView View, bool FiltersSet, bool HasLocationDatabase, string Marked)? lastLive;
    // A mode change or a new History context scrolls the grid to its top when its rows are next shown.
    private bool topPending;
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
    private (FontFamily? Font, double Size, string Culture) measured;

    internal readonly DataGrid Grid = new() { Height = 315 };
    internal readonly ComboBox Scope = new() { Width = 120 };
    /// <summary>Export destinations…: enabled only in History while no export runs; the page sets its state and handles its click.</summary>
    internal readonly Button Export = new() { Content = "Export destinations…", Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(8, 0, 0, 0) };
    internal readonly TextBlock CountLine = Ui.Text("", 11, "Muted"), Status = Ui.Text("", 11, "Muted"), Footnote = Ui.Text(DestinationCopy.Footnote(false), 11, "Muted");
    internal readonly Border Empty = new() { Height = 315, Visibility = Visibility.Collapsed };
    internal readonly ObservableCollection<DestinationRow> Rows = [];
    /// <summary>The list on screen: the Live list grouped from the filtered connections, or the History list; null before any or when History is unavailable.</summary>
    internal ConnectionDestinationList? List { get; private set; }
    /// <summary>Holds the order: the pointer over the grid, the row menu open, or 3 s after a navigation key in the grid.</summary>
    internal OrderHold Hold { get; } = new();
    /// <summary>The key hold's clock in seconds: monotonic, so a wall-clock change can neither extend nor end it. The page passes its own Stopwatch.</summary>
    internal Func<double> clockSeconds = () => Monotonic.Elapsed.TotalSeconds;
    /// <summary>The selected row's destination, or null.</summary>
    internal ConnectionDestination? Selected => (Grid.SelectedItem as DestinationRow)?.Value;
    /// <summary>Raised when the selected row changes, to another row or to none (also when the selected row leaves).</summary>
    internal event Action? SelectionChanged;
    /// <summary>Raised with the text Ctrl+C, Copy row or Copy address puts on the clipboard; the page writes it and reports the result.</summary>
    internal event Action<string>? CopyRequested;

    internal DestinationsCard(UiSession session)
    {
        SetResourceReference(StyleProperty, "Card"); SetResourceReference(BodyFontProperty, "BodyFont");
        Padding = new Thickness(12, 8, 12, 8); Margin = new Thickness(0, 8, 0, 8);
        var layout = new StackPanel();
        layout.Children.Add(Ui.Text("Destinations", 13));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var show = Ui.Text("Show", 11, "Muted"); show.Margin = new Thickness(0, 0, 6, 0); toolbar.Children.Add(show);
        Scope.ItemsSource = new[] { "All", "Internet", "Local" }; Scope.SelectedIndex = 0; AutomationProperties.SetName(Scope, "Destinations shown"); toolbar.Children.Add(Scope);
        AutomationProperties.SetName(Export, "Export destinations…"); ToolTipService.SetShowOnDisabled(Export, true); toolbar.Children.Add(Export);
        layout.Children.Add(toolbar);
        CountLine.Margin = new Thickness(0, 0, 0, 4); layout.Children.Add(CountLine);
        AutomationProperties.SetName(Grid, "Destinations");
        Grid.ItemsSource = Rows; Grid.AutoGenerateColumns = false; Grid.IsReadOnly = true; Grid.SelectionMode = DataGridSelectionMode.Single;
        Grid.CanUserReorderColumns = false; Grid.CanUserResizeRows = false;
        var row = new Style(typeof(DataGridRow), TryFindResource(typeof(DataGridRow)) as Style);
        row.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(DestinationRow.Spoken)))); Grid.RowStyle = row;
        // Pixel scrolling keeps virtualization inside the page's ScrollViewer.
        VirtualizingPanel.SetScrollUnit(Grid, ScrollUnit.Pixel);
        foreach (var spec in Specs) columns[spec.Key] = Column(spec);
        Grid.Sorting += OnSorting;
        Grid.PreviewMouseWheel += OnPreviewMouseWheel;
        Grid.SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, Grid)) SelectionChanged?.Invoke(); };
        Grid.MouseEnter += (_, _) => Hold.PointerOver = true;
        Grid.MouseLeave += (_, _) => Hold.PointerOver = false;
        // Arrow keys move the selection and hold the order; the DataGrid still acts on them.
        Grid.PreviewKeyDown += (_, e) => { if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) Hold.KeyNavigated(clockSeconds()); };
        // Ctrl+C copies the selected row; this binding replaces the grid's own Copy, which never runs. With no selection it does nothing.
        Grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { CopyRow(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        Grid.ContextMenu = RowMenu();
        Grid.ContextMenuOpening += OnContextMenuOpening;
        layout.Children.Add(Grid);
        emptyText.HorizontalAlignment = HorizontalAlignment.Center; emptyText.TextAlignment = TextAlignment.Center; emptyText.Margin = new Thickness(12);
        Empty.Child = emptyText; Empty.SetResourceReference(Border.BackgroundProperty, "GlassTable"); layout.Children.Add(Empty);
        Status.Margin = new Thickness(0, 4, 0, 0); Status.Visibility = Visibility.Collapsed; layout.Children.Add(Status);
        Footnote.Margin = new Thickness(0, 6, 0, 0); layout.Children.Add(Footnote);
        Child = layout;
        SetMode(false);
        Scope.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            if (session.Connections is not null) session.SetConnectionsDestinationScope(CurrentScope.ToString());
            ShowScope();
        };
    }

    /// <summary>The scope shown, which Export destinations… exports.</summary>
    internal DestinationScope CurrentScope => (DestinationScope)Math.Max(0, Scope.SelectedIndex);

    /// <summary>The row menu: Copy row and Copy address. While it is open the order is held.</summary>
    private ContextMenu RowMenu()
    {
        var menu = new ContextMenu();
        foreach (var (header, copy) in new (string, Action)[] { ("Copy row", CopyRow), ("Copy address", CopyAddress) })
        {
            var item = new MenuItem { Header = header }; AutomationProperties.SetName(item, header);
            item.Click += (_, _) => copy(); menu.Items.Add(item);
        }
        menu.Opened += (_, _) => Hold.MenuOpen = true;
        menu.Closed += (_, _) => Hold.MenuOpen = false;
        return menu;
    }

    /// <summary>Opens the row menu itself: from the pointer on the row under it, which it selects first; from the Menu key or Shift+F10 on the selected row.
    /// Off a row, or with no selection, no menu opens.</summary>
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        bool keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var row = RowForMenu(e.OriginalSource as DependencyObject, keyboard);
        if (Grid.SelectedItem is null || row is null && !keyboard) return;
        var menu = Grid.ContextMenu!;
        // Should the selected row still have no container, the menu opens on the grid.
        menu.PlacementTarget = (UIElement?)row ?? Grid;
        menu.Placement = keyboard ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>The row the menu opens on, or null. From the keyboard: the selected row, scrolled into view first so that it has a container.
    /// From the pointer: the row holding <paramref name="source"/>, which it selects.</summary>
    internal DataGridRow? RowForMenu(DependencyObject? source, bool keyboard)
    {
        if (keyboard)
        {
            if (Grid.SelectedItem is not { } item) return null;
            Grid.ScrollIntoView(item); Grid.UpdateLayout();
            return Grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
        }
        var row = source is null ? null : ItemsControl.ContainerFromElement(Grid, source) as DataGridRow;
        if (row?.Item is DestinationRow under) Grid.SelectedItem = under;
        return row;
    }

    /// <summary>Copies the selected row's shown cells in column order, tab-separated, on one line, with no header.</summary>
    private void CopyRow()
    {
        if (Grid.SelectedItem is not DestinationRow row) return;
        CopyRequested?.Invoke(string.Join("\t", Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => row.Cell(Enum.Parse<ConnectionDestinationColumn>(c.SortMemberPath)))));
    }

    /// <summary>Copies the selected row's address only.</summary>
    private void CopyAddress()
    {
        if (Grid.SelectedItem is DestinationRow row) CopyRequested?.Invoke(row.Value.Address);
    }

    /// <summary>Sets the scope from the saved setting without saving it back; the table follows when it changed.</summary>
    internal void SyncScope(DestinationScope scope)
    {
        if (CurrentScope == scope) return;
        syncing = true;
        try { Scope.SelectedIndex = (int)scope; }
        finally { syncing = false; }
        ShowScope();
    }

    /// <summary>A scope change re-sorts at once and scrolls the grid to its top.</summary>
    private void ShowScope()
    {
        reshow?.Invoke(Order.Resort);
        ScrollToTop();
    }

    /// <summary>Clears the table selection.</summary>
    internal void ClearSelection() => Grid.SelectedItem = null;

    /// <summary>Re-sorts the rows on screen at once, even during a hold (F5).</summary>
    internal void Resort() => Reconcile(Sorted(Rows));

    /// <summary>Releases the pointer and menu holds, for a page that is unloaded or hidden.</summary>
    internal void ReleaseHolds() { Hold.PointerOver = false; Hold.MenuOpen = false; }

    /// <summary>Sets the status line under the grid; empty collapses it.</summary>
    internal void SetStatus(string text)
    {
        Status.Text = text;
        Status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Groups the page's filtered live connections, one row per remote address. A view equal to the last one (record equality) changes nothing;
    /// one with a new Timestamp is new data; one with the same Timestamp and other values updates values only. <paramref name="resort"/>: the filters changed, so the rows re-sort at once.
    /// <paramref name="isExcluded"/>: <see cref="UiSession.IsExcluded"/>, which marks apps in Don't record these apps; a change in which shown apps it marks updates values only.</summary>
    internal void ShowLive(ConnectionLiveView view, IReadOnlyList<LocatedConnection> filtered, bool filtersSet, bool hasLocationDatabase, bool resort = false, Func<string, bool>? isExcluded = null)
    {
        bool modeChanged = SetMode(false);
        // The marked apps join the comparison, so taking an app on or off the list re-shows an otherwise equal view.
        string marked = isExcluded is null ? "" : string.Join("\n", filtered.Select(c => c.Traffic.Peer.Process.Name).Distinct(StringComparer.Ordinal).Where(isExcluded).Order(StringComparer.Ordinal));
        (ConnectionLiveView, bool, bool, string) shown = (view, filtersSet, hasLocationDatabase, marked);
        if (!resort && !modeChanged && lastLive is { } last && last.Equals(shown)) return;
        bool newData = lastLive is not { } previous || previous.View.Timestamp != view.Timestamp;
        lastLive = shown;
        var list = ConnectionDestinations.FromLive(filtered, view.Available);
        var context = new DestinationRowContext(false, view.Available, view.Complete, hasLocationDatabase, isExcluded);
        List = list;
        reshow = order => Present(list, context, scope => DestinationCopy.LiveEmpty(view.Available, view.Connections.Count > 0, filtersSet, scope), order);
        reshow(resort || modeChanged ? Order.Resort : newData ? Order.NewData : Order.Values);
    }

    /// <summary>Shows a History list; null (a failed read, the TEMP limit or no service) shows "Destinations unavailable" and "—" counts.</summary>
    internal void ShowHistory(ConnectionDestinationList? list, bool minute, bool filtersSet)
    {
        bool modeChanged = SetMode(true); List = list; lastLive = null;
        if (list is null) reshow = _ => ShowState(DestinationCopy.Unavailable, DestinationCopy.UnavailableCountLine, "History unavailable");
        else
        {
            var context = new DestinationRowContext(true, true, true, false);
            reshow = order => Present(list, context, scope => DestinationCopy.HistoryEmpty(minute, filtersSet, scope), order);
        }
        // Every applied History view is new data.
        reshow(modeChanged ? Order.Resort : Order.NewData);
    }

    /// <summary>Clears the rows and shows the text in place of the grid (History: an invalid interval, a read in progress).
    /// The card takes History's columns and footnote at once; with no rows left, the first History view sorts the same either way.</summary>
    internal void ShowText(string text)
    {
        SetMode(true); List = null; lastLive = null; topPending = true;
        reshow = _ => ShowState(text, "", null);
        reshow(Order.Resort);
    }

    private void ShowState(string text, string countLine, string? countTip)
    {
        ClearSelection(); Rows.Clear(); kept.Clear();
        CountLine.Text = countLine; CountLine.ToolTip = countTip;
        ShowEmpty(text);
    }

    private void Present(ConnectionDestinationList list, DestinationRowContext context, Func<DestinationScope, string> empty, Order order)
    {
        var scope = CurrentScope;
        // A culture or font change since the last measure shows in the time column's width at once.
        UpdateMinimumWidths();
        CountLine.Text = DestinationCopy.CountLine(list.Counts, scope, context.History); CountLine.ToolTip = null;
        var rows = ConnectionDestinations.InScope(list, scope).Select(d => kept.TryGetValue(d.Address, out var row) ? row.Update(d, context) : new DestinationRow(d, context)).ToList();
        var sorted = Sorted(rows);
        bool resort = order == Order.Resort || order == Order.NewData && !Hold.IsHeld(clockSeconds());
        if (resort) Reconcile(sorted);
        else
        {
            // Held: gone rows leave, new rows join the bottom in sorted order, and nothing moves.
            var byKey = sorted.ToDictionary(r => r.Key, StringComparer.Ordinal);
            Reconcile([.. CalmOrderPlanner.Held([.. Rows.Select(r => r.Key)], [.. sorted.Select(r => r.Key)], _ => null).Select(key => byKey[key])]);
        }
        // An empty list names why it is empty; the scope only when it emptied the table.
        ShowEmpty(rows.Count > 0 ? null : empty(list.Counts.All == 0 ? DestinationScope.All : scope));
        if (topPending) { topPending = false; ScrollToTop(); }
    }

    private List<DestinationRow> Sorted(IEnumerable<DestinationRow> rows)
    {
        var sorted = rows.ToList();
        sorted.Sort((a, b) => ConnectionDestinationSort.Compare(a.Value, b.Value, sortKey, sortDescending));
        return sorted;
    }

    /// <summary>Scrolls the grid to its first row; nothing before its template is applied, when there is no offset to reset.</summary>
    private void ScrollToTop() => Descendant<ScrollViewer>(Grid)?.ScrollToTop();

    private void ShowEmpty(string? text)
    {
        emptyText.Text = text ?? "";
        Empty.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Grid.Visibility = text is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves the collection to <paramref name="target"/>: gone rows leave (a selected one clears the selection), kept rows move as few as
    /// <see cref="CalmOrderPlanner.Plan"/> allows, and new rows are inserted at their place. Row instances are kept per address, so row containers,
    /// the selection, keyboard focus and the scroll offset survive.</summary>
    private void Reconcile(IReadOnlyList<DestinationRow> target)
    {
        kept.Clear(); foreach (var row in target) kept[row.Key] = row;
        var keep = target.ToHashSet();
        if (Grid.SelectedItem is DestinationRow selected && !keep.Contains(selected)) ClearSelection();
        for (int i = Rows.Count - 1; i >= 0; i--) if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        foreach (var move in CalmOrderPlanner.Plan([.. Rows.Select(r => r.Key)], [.. target.Select(r => r.Key)])) Rows.Move(move.From, move.To);
        for (int i = 0; i < target.Count; i++) if (i >= Rows.Count || !ReferenceEquals(Rows[i], target[i])) Rows.Insert(i, target[i]);
    }

    /// <summary>Swaps in the mode's columns; the sort stays when its column exists in the new mode and otherwise returns to RECEIVED descending.</summary>
    /// <summary>True when the mode changed: the rows then re-sort and the grid scrolls to its top.</summary>
    private bool SetMode(bool history)
    {
        if (this.history == history) return false;
        this.history = history; topPending = true;
        var key = ConnectionDestinationSort.InMode(sortKey, history);
        if (key != sortKey) { sortKey = key; sortDescending = true; }
        var wanted = ConnectionDestinationSort.Columns(history).Select(k => columns[k]).ToList();
        for (int i = Grid.Columns.Count - 1; i >= 0; i--) if (!wanted.Contains(Grid.Columns[i])) Grid.Columns.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = Grid.Columns.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at >= 0) Grid.Columns.Move(at, i); else Grid.Columns.Insert(i, wanted[i]);
        }
        foreach (var spec in Specs) SetHeaderTip(columns[spec.Key], DestinationCopy.HeaderTip(spec.Key, history));
        Footnote.Text = DestinationCopy.Footnote(history);
        ShowSortDirections();
        return true;
    }

    private void ShowSortDirections()
    {
        foreach (var (key, column) in columns)
            column.SortDirection = key != sortKey ? null : sortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }

    /// <summary>A header click: number and time columns sort descending first, the text columns ascending; clicking the sorted column flips it.</summary>
    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var key = Enum.Parse<ConnectionDestinationColumn>(e.Column.SortMemberPath);
        if (key == sortKey) sortDescending = !sortDescending;
        else { sortKey = key; sortDescending = ConnectionDestinationSort.DescendingFirst(key); }
        ShowSortDirections();
        Reconcile(Sorted(Rows));
    }

    /// <summary>The map takes the wheel for zoom, so a wheel the grid cannot use any further that way scrolls the page instead.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        var inner = Descendant<ScrollViewer>(Grid);
        bool canScroll = inner is not null && (e.Delta < 0 ? inner.VerticalOffset + .01 < inner.ScrollableHeight : inner.VerticalOffset > .01);
        if (canScroll) return;
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer page) continue;
            e.Handled = true;
            page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
            return;
        }
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        UpdateMinimumWidths();
        return base.MeasureOverride(constraint);
    }

    /// <summary>Each MinWidth is the largest of its listed minimum, its header's text plus the header padding and, for FIRST SEEN and LAST SEEN,
    /// 2026-12-28 23:59 in the current culture's "g" plus the cell padding, so no header and no time is trimmed.</summary>
    private void UpdateMinimumWidths()
    {
        var font = GetValue(BodyFontProperty) as FontFamily ?? Grid.FontFamily;
        (FontFamily? Font, double Size, string Culture) current = (font, Grid.FontSize, CultureInfo.CurrentCulture.Name);
        if (current.Equals(measured)) return;
        measured = current;
        double time = Math.Ceiling(TextWidth(new DateTime(2026, 12, 28, 23, 59, 0).ToString("g", CultureInfo.CurrentCulture), font, Grid.FontSize, Ui.ValueText)) + CellPadding;
        foreach (var spec in Specs)
        {
            double width = Math.Max(spec.Listed, Math.Ceiling(TextWidth(spec.Header, font, HeaderFontSize, null)) + HeaderPadding);
            if (spec.Key is ConnectionDestinationColumn.FirstSeen or ConnectionDestinationColumn.LastSeen) width = Math.Max(width, time);
            columns[spec.Key].MinWidth = width;
        }
    }

    private static double TextWidth(string text, FontFamily font, double size, Style? style)
    {
        var probe = new TextBlock { Text = text, FontFamily = font, FontSize = size };
        if (style is not null) probe.Style = style;
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private DataGridColumn Column(ColumnSpec spec)
    {
        var column = new DataGridTextColumn
        {
            Header = spec.Header, Binding = new Binding(spec.Binding), SortMemberPath = spec.Key.ToString(),
            Width = new DataGridLength(spec.Star, DataGridLengthUnitType.Star), MinWidth = spec.Listed, ElementStyle = spec.Value ? Ui.ValueText : TextCell,
        };
        if (spec.Tip is not null)
        {
            var cell = new Style(typeof(DataGridCell), TryFindResource(typeof(DataGridCell)) as Style);
            cell.Setters.Add(new Setter(ToolTipProperty, new Binding(spec.Tip)));
            column.CellStyle = cell;
        }
        return column;
    }

    /// <summary>Gives the column's header <paramref name="tip"/> through a style based on the theme's, so it keeps its padding and caption look.
    /// Without a tip the column's HeaderStyle is cleared, never set to null: DataGrid transfers a column's locally set HeaderStyle to its header
    /// ahead of the header's implicit style, so a local null would drop the theme's 10 px caption and 9,8 padding.</summary>
    private void SetHeaderTip(DataGridColumn column, string? tip)
    {
        if (tip is null) { column.ClearValue(DataGridColumn.HeaderStyleProperty); return; }
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), TryFindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)) as Style);
        header.Setters.Add(new Setter(ToolTipProperty, tip));
        column.HeaderStyle = header;
    }

    private static Style TextCellStyle()
    {
        var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Seal(); return style;
    }
}
