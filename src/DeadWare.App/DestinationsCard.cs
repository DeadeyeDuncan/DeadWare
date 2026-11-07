using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>What a row's cells depend on besides its destination: the mode, the Live view's capture state and whether a location database is installed.</summary>
internal sealed record DestinationRowContext(bool History, bool Available, bool Complete, bool HasLocationDatabase);

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
        string apps = d.Apps.Count == 0 ? "" : d.Apps[0].Name + More(d.AppCount);
        string? appsTip = d.Apps.Count == 0 ? null : Lines(d.Apps.Select(a => Line(a.Name, a.ReceivedBytes, a.SentBytes)), d.AppCount - d.Apps.Count);
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
        spoken.Add(d.Apps.Count == 0 ? "no apps" : $"{Count(d.AppCount, "app", "apps")}: {d.Apps[0].Name}{SpokenMore(d.AppCount)}");
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

/// <summary>The Destinations card below the map: one row per remote address in Live and in History, narrowed by the All / Internet / Local scope.</summary>
internal sealed class DestinationsCard : Border
{
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
    // Shows the last data again, for a scope change.
    private Action? reshow;
    private (FontFamily? Font, double Size, string Culture) measured;

    internal readonly DataGrid Grid = new() { Height = 315 };
    internal readonly ComboBox Scope = new() { Width = 120 };
    internal readonly TextBlock CountLine = Ui.Text("", 11, "Muted"), Status = Ui.Text("", 11, "Muted"), Footnote = Ui.Text(DestinationCopy.Footnote(false), 11, "Muted");
    internal readonly Border Empty = new() { Height = 315, Visibility = Visibility.Collapsed };
    internal readonly ObservableCollection<DestinationRow> Rows = [];
    /// <summary>The list on screen: the Live list grouped from the filtered connections, or the History list; null before any or when History is unavailable.</summary>
    internal ConnectionDestinationList? List { get; private set; }

    internal DestinationsCard(UiSession session)
    {
        SetResourceReference(StyleProperty, "Card"); SetResourceReference(BodyFontProperty, "BodyFont");
        Padding = new Thickness(12, 8, 12, 8); Margin = new Thickness(0, 8, 0, 8);
        var layout = new StackPanel();
        layout.Children.Add(Ui.Text("Destinations", 13));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var show = Ui.Text("Show", 11, "Muted"); show.Margin = new Thickness(0, 0, 6, 0); toolbar.Children.Add(show);
        Scope.ItemsSource = new[] { "All", "Internet", "Local" }; Scope.SelectedIndex = 0; AutomationProperties.SetName(Scope, "Destinations shown"); toolbar.Children.Add(Scope);
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
            reshow?.Invoke();
        };
    }

    private DestinationScope CurrentScope => (DestinationScope)Math.Max(0, Scope.SelectedIndex);

    /// <summary>Sets the scope from the saved setting without saving it back; the table follows when it changed.</summary>
    internal void SyncScope(DestinationScope scope)
    {
        if (CurrentScope == scope) return;
        syncing = true;
        try { Scope.SelectedIndex = (int)scope; }
        finally { syncing = false; }
        reshow?.Invoke();
    }

    /// <summary>Groups the page's filtered live connections, one row per remote address.</summary>
    internal void ShowLive(ConnectionLiveView view, IReadOnlyList<LocatedConnection> filtered, bool filtersSet, bool hasLocationDatabase)
    {
        var list = ConnectionDestinations.FromLive(filtered, view.Available);
        var context = new DestinationRowContext(false, view.Available, view.Complete, hasLocationDatabase);
        SetMode(false); List = list;
        reshow = () => Present(list, context, scope => DestinationCopy.LiveEmpty(view.Available, view.Connections.Count > 0, filtersSet, scope));
        reshow();
    }

    /// <summary>Shows a History list; null (a failed read, the TEMP limit or no service) shows "Destinations unavailable" and "—" counts.</summary>
    internal void ShowHistory(ConnectionDestinationList? list, bool minute, bool filtersSet)
    {
        SetMode(true); List = list;
        if (list is null) reshow = () => ShowState(DestinationCopy.Unavailable, DestinationCopy.UnavailableCountLine, "History unavailable");
        else
        {
            var context = new DestinationRowContext(true, true, true, false);
            reshow = () => Present(list, context, scope => DestinationCopy.HistoryEmpty(minute, filtersSet, scope));
        }
        reshow();
    }

    /// <summary>Clears the rows and shows the text in place of the grid (an invalid interval, a read in progress).</summary>
    internal void ShowText(string text)
    {
        List = null;
        reshow = () => ShowState(text, "", null);
        reshow();
    }

    private void ShowState(string text, string countLine, string? countTip)
    {
        Rows.Clear(); kept.Clear();
        CountLine.Text = countLine; CountLine.ToolTip = countTip;
        ShowEmpty(text);
    }

    private void Present(ConnectionDestinationList list, DestinationRowContext context, Func<DestinationScope, string> empty)
    {
        var scope = CurrentScope;
        // A culture or font change since the last measure shows in the time column's width at once.
        UpdateMinimumWidths();
        CountLine.Text = DestinationCopy.CountLine(list.Counts, scope, context.History); CountLine.ToolTip = null;
        var rows = ConnectionDestinations.InScope(list, scope).Select(d => kept.TryGetValue(d.Address, out var row) ? row.Update(d, context) : new DestinationRow(d, context)).ToList();
        Reconcile(rows);
        // An empty list names why it is empty; the scope only when it emptied the table.
        ShowEmpty(rows.Count > 0 ? null : empty(list.Counts.All == 0 ? DestinationScope.All : scope));
    }

    private void ShowEmpty(string? text)
    {
        emptyText.Text = text ?? "";
        Empty.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        Grid.Visibility = text is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Sorts the rows and moves the collection to that order: gone rows leave, kept rows move and new rows are inserted at their place.</summary>
    private void Reconcile(List<DestinationRow> rows)
    {
        rows.Sort((a, b) => ConnectionDestinationSort.Compare(a.Value, b.Value, sortKey, sortDescending));
        kept.Clear(); foreach (var row in rows) kept[row.Key] = row;
        for (int i = Rows.Count - 1; i >= 0; i--) if (!kept.TryGetValue(Rows[i].Key, out var row) || !ReferenceEquals(row, Rows[i])) Rows.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = Rows.IndexOf(rows[i]);
            if (at == i) continue;
            if (at >= 0) Rows.Move(at, i); else Rows.Insert(i, rows[i]);
        }
    }

    /// <summary>Swaps in the mode's columns; the sort stays when its column exists in the new mode and otherwise returns to RECEIVED descending.</summary>
    private void SetMode(bool history)
    {
        if (this.history == history) return;
        this.history = history;
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
        foreach (var spec in Specs) columns[spec.Key].HeaderStyle = HeaderStyle(DestinationCopy.HeaderTip(spec.Key, history));
        Footnote.Text = DestinationCopy.Footnote(history);
        ShowSortDirections();
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
        Reconcile(Rows.ToList());
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

    /// <summary>A header style based on the theme's, so the header keeps its padding and caption look, carrying the tooltip; null without one.</summary>
    private Style? HeaderStyle(string? tip)
    {
        if (tip is null) return null;
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), TryFindResource(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader)) as Style);
        header.Setters.Add(new Setter(ToolTipProperty, tip));
        return header;
    }

    private static Style TextCellStyle()
    {
        var style = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Seal(); return style;
    }
}
