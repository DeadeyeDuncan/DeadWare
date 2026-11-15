using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeadWare.WinUI;

/// <summary>
/// The Destinations table below the map: one row per remote address in Live and in History, narrowed by the All / Internet / Local
/// scope. Rows update in place and stay calm: new data re-sorts only outside a hold (the pointer over the table, the row menu open,
/// or 3 s after a navigation key), while header clicks, the scope, F5, a mode change and filter changes re-sort at once.
/// </summary>
internal sealed class DestinationsView : UserControl
{
    /// <summary>How a show orders the rows: re-sorted at once, re-sorted unless the order is held (new data), or kept in place (same data, other values).</summary>
    private enum Order { Resort, NewData, Values }

    private sealed class Row(string key)
    {
        public string Key { get; } = key;
        public ConnectionDestination Value = null!;
        public DestinationCells? Cells;
        public ListViewItem Item = null!;
        public TextBlock[] Texts = [];
    }

    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
    private readonly MonitorSession session;
    private readonly Dictionary<string, Row> kept = new(StringComparer.Ordinal);
    private readonly List<Row> rows = [];
    private readonly ComboBox scope = new() { Width = 130 };
    private readonly TextBlock countLine = Muted(""), status = Muted(""), footnote = Muted(DestinationCopy.Footnote(false)), emptyText = Muted("");
    private readonly Grid header = new() { Padding = new Thickness(16, 4, 12, 4), ColumnSpacing = 8 };
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, Height = 315 };
    private readonly Border empty = new() { Height = 315, Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(4) };
    private readonly MenuFlyout menu = new();
    private IReadOnlyList<ConnectionDestinationColumn> columns = [];
    private ConnectionDestinationColumn sortKey = ConnectionDestinationColumn.Received;
    private bool sortDescending = true, syncing, topPending;
    private bool? history;
    // Shows the last data again, ordered as asked (a scope change re-sorts).
    private Action<Order>? reshow;
    // The last Live view shown with what else its rows depend on; an equal one changes nothing. Null outside Live.
    private (ConnectionLiveView View, bool FiltersSet, bool HasLocationDatabase, string Marked)? lastLive;

    public DestinationsView(MonitorSession session)
    {
        this.session = session;
        foreach (string label in new[] { "All", "Internet", "Local" }) scope.Items.Add(label);
        scope.SelectedIndex = 0;
        AutomationProperties.SetName(scope, "Destinations shown");
        AutomationProperties.SetName(Export, "Export destinations…");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var show = Muted("Show"); show.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(show); toolbar.Children.Add(scope); toolbar.Children.Add(Export);

        AutomationProperties.SetName(list, "Destinations");
        list.SelectionChanged += (_, _) => { if (!syncing) SelectionChanged?.Invoke(); };
        list.PointerEntered += (_, _) => Hold.PointerOver = true;
        list.PointerExited += (_, _) => Hold.PointerOver = false;
        // Arrow keys move the selection and hold the order; the list still acts on them.
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End) Hold.KeyNavigated(ClockSeconds());
        };
        // Ctrl+C copies the selected row; with no selection it does nothing.
        var copy = new KeyboardAccelerator { Key = VirtualKey.C, Modifiers = VirtualKeyModifiers.Control };
        copy.Invoked += (_, e) => { CopyRow(); e.Handled = true; };
        list.KeyboardAccelerators.Add(copy);
        foreach (var (text, action) in new (string, Action)[] { ("Copy row", CopyRow), ("Copy address", CopyAddress) })
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // The row menu opens on the row under the pointer, which it selects first; with no selection there is nothing to copy.
        menu.Opening += (_, _) => { if (list.SelectedItem is null) menu.Hide(); else Hold.MenuOpen = true; };
        menu.Closed += (_, _) => Hold.MenuOpen = false;
        list.ContextFlyout = menu;
        list.RightTapped += (_, e) =>
        {
            for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
                if (element is ListViewItem item) { list.SelectedItem = item; break; }
        };

        emptyText.HorizontalAlignment = HorizontalAlignment.Center; emptyText.VerticalAlignment = VerticalAlignment.Center;
        emptyText.TextAlignment = TextAlignment.Center; emptyText.Margin = new Thickness(12);
        empty.Child = emptyText;
        empty.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        status.Visibility = Visibility.Collapsed;
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.Children.Add(header);
        Grid.SetRow(list, 1); table.Children.Add(list);
        Grid.SetRow(empty, 1); table.Children.Add(empty);
        Content = new StackPanel { Spacing = 6, Children = { toolbar, countLine, table, status, footnote } };
        SetMode(false);
        scope.SelectionChanged += (_, _) =>
        {
            if (syncing) return;
            session.SetConnectionsDestinationScope(CurrentScope.ToString());
            ShowScope();
        };
    }

    /// <summary>Export destinations…: enabled only in History while no export runs; the page sets its state and handles its click.</summary>
    public Button Export { get; } = new() { Content = "Export destinations…" };
    /// <summary>The list on screen: the Live list grouped from the filtered connections, or the History list; null before any or when History is unavailable.</summary>
    public ConnectionDestinationList? List { get; private set; }
    /// <summary>Holds the order: the pointer over the table, the row menu open, or 3 s after a navigation key in it.</summary>
    public OrderHold Hold { get; } = new();
    /// <summary>The key hold's clock in seconds: monotonic, so a wall-clock change can neither extend nor end it.</summary>
    public Func<double> ClockSeconds { get; set; } = () => Monotonic.Elapsed.TotalSeconds;
    /// <summary>The selected row's destination, or null.</summary>
    public ConnectionDestination? Selected => list.SelectedItem is ListViewItem { Tag: string key } && kept.TryGetValue(key, out var row) ? row.Value : null;
    /// <summary>The scope shown, which Export destinations… exports.</summary>
    public DestinationScope CurrentScope => (DestinationScope)Math.Max(0, scope.SelectedIndex);
    /// <summary>Raised when the selected row changes, to another row or to none (also when the selected row leaves).</summary>
    public event Action? SelectionChanged;
    /// <summary>Raised with the text Ctrl+C, Copy row or Copy address puts on the clipboard; the page writes it and reports the result.</summary>
    public event Action<string>? CopyRequested;

    private static TextBlock Muted(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private Row? SelectedRow => list.SelectedItem is ListViewItem { Tag: string key } && kept.TryGetValue(key, out var row) ? row : null;

    /// <summary>Copies the selected row's shown cells in column order, tab-separated, on one line, with no header.</summary>
    private void CopyRow()
    {
        if (SelectedRow is { Cells: { } cells }) CopyRequested?.Invoke(string.Join("\t", columns.Select(cells.Cell)));
    }

    /// <summary>Copies the selected row's address only.</summary>
    private void CopyAddress()
    {
        if (SelectedRow is { } row) CopyRequested?.Invoke(row.Value.Address);
    }

    /// <summary>Sets the scope from the saved setting without saving it back; the table follows when it changed.</summary>
    public void SyncScope(DestinationScope value)
    {
        if (CurrentScope == value) return;
        syncing = true;
        try { scope.SelectedIndex = (int)value; }
        finally { syncing = false; }
        ShowScope();
    }

    /// <summary>A scope change re-sorts at once and scrolls the table to its top.</summary>
    private void ShowScope()
    {
        reshow?.Invoke(Order.Resort);
        ScrollToTop();
    }

    /// <summary>Clears the table selection.</summary>
    public void ClearSelection() { if (list.SelectedItem is not null) list.SelectedItem = null; }

    /// <summary>Re-sorts the rows on screen at once, even during a hold (F5).</summary>
    public void Resort() => Reconcile(Sorted(rows));

    /// <summary>Releases the pointer and menu holds, for a page that is unloaded or hidden.</summary>
    public void ReleaseHolds() { Hold.PointerOver = false; Hold.MenuOpen = false; }

    /// <summary>Sets the status line under the table; empty collapses it.</summary>
    public void SetStatus(string text)
    {
        if (status.Text != text) status.Text = text;
        status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Groups the page's filtered live connections, one row per remote address. A view equal to the last one changes nothing;
    /// one with a new timestamp is new data; one with the same timestamp and other values updates values only.
    /// <paramref name="resort"/>: the filters changed, so the rows re-sort at once.</summary>
    public void ShowLive(ConnectionLiveView view, IReadOnlyList<LocatedConnection> filtered, bool filtersSet, bool hasLocationDatabase, bool resort, Func<string, bool> isExcluded)
    {
        bool modeChanged = SetMode(false);
        // The marked apps join the comparison, so taking an app on or off the list re-shows an otherwise equal view.
        string marked = string.Join("\n", filtered.Select(c => c.Traffic.Peer.Process.Name).Distinct(StringComparer.Ordinal).Where(isExcluded).Order(StringComparer.Ordinal));
        (ConnectionLiveView, bool, bool, string) shown = (view, filtersSet, hasLocationDatabase, marked);
        if (!resort && !modeChanged && lastLive is { } last && last.Equals(shown)) return;
        bool newData = lastLive is not { } previous || previous.View.Timestamp != view.Timestamp;
        lastLive = shown;
        var grouped = ConnectionDestinations.FromLive(filtered, view.Available);
        var context = new DestinationCellContext(false, view.Available, view.Complete, hasLocationDatabase, isExcluded);
        List = grouped;
        reshow = order => Present(grouped, context, inScope => DestinationCopy.LiveEmpty(view.Available, view.Connections.Count > 0, filtersSet, inScope), order);
        reshow(resort || modeChanged ? Order.Resort : newData ? Order.NewData : Order.Values);
    }

    /// <summary>Shows a History list; null (a failed read, the TEMP limit or no service) shows "Destinations unavailable" and "—" counts.</summary>
    public void ShowHistory(ConnectionDestinationList? value, bool minute, bool filtersSet)
    {
        bool modeChanged = SetMode(true); List = value; lastLive = null;
        if (value is null) reshow = _ => ShowState(DestinationCopy.Unavailable, DestinationCopy.UnavailableCountLine);
        else
        {
            var context = new DestinationCellContext(true, true, true, false);
            reshow = order => Present(value, context, inScope => DestinationCopy.HistoryEmpty(minute, filtersSet, inScope), order);
        }
        // Every applied History view is new data.
        reshow(modeChanged ? Order.Resort : Order.NewData);
    }

    /// <summary>Clears the rows and shows the text in place of the table (History: an invalid interval, a read in progress).</summary>
    public void ShowText(string text)
    {
        SetMode(true); List = null; lastLive = null; topPending = true;
        reshow = _ => ShowState(text, "");
        reshow(Order.Resort);
    }

    private void ShowState(string text, string count)
    {
        bool selected = list.SelectedItem is not null;
        syncing = true;
        try { list.Items.Clear(); rows.Clear(); kept.Clear(); }
        finally { syncing = false; }
        if (selected) SelectionChanged?.Invoke();
        countLine.Text = count;
        ShowEmpty(text);
    }

    private void Present(ConnectionDestinationList value, DestinationCellContext context, Func<DestinationScope, string> emptyReason, Order order)
    {
        var inScope = CurrentScope;
        countLine.Text = DestinationCopy.CountLine(value.Counts, inScope, context.History);
        var shown = new List<Row>();
        foreach (var destination in ConnectionDestinations.InScope(value, inScope))
        {
            if (!kept.TryGetValue(destination.Address, out var row)) row = NewRow(destination.Address);
            Update(row, destination, context);
            shown.Add(row);
        }
        var sorted = Sorted(shown);
        bool resort = order == Order.Resort || order == Order.NewData && !Hold.IsHeld(ClockSeconds());
        if (resort) Reconcile(sorted);
        else
        {
            // Held: gone rows leave, new rows join the bottom in sorted order, and nothing moves.
            var byKey = sorted.ToDictionary(r => r.Key, StringComparer.Ordinal);
            Reconcile([.. CalmOrderPlanner.Held([.. rows.Select(r => r.Key)], [.. sorted.Select(r => r.Key)], _ => null).Select(key => byKey[key])]);
        }
        // An empty list names why it is empty; the scope only when it emptied the table.
        ShowEmpty(shown.Count > 0 ? null : emptyReason(value.Counts.All == 0 ? DestinationScope.All : inScope));
        if (topPending) { topPending = false; ScrollToTop(); }
    }

    private List<Row> Sorted(IEnumerable<Row> source)
    {
        var sorted = source.ToList();
        sorted.Sort((a, b) => ConnectionDestinationSort.Compare(a.Value, b.Value, sortKey, sortDescending));
        return sorted;
    }

    private void ScrollToTop()
    {
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
    }

    private void ShowEmpty(string? text)
    {
        emptyText.Text = text ?? "";
        empty.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        list.Visibility = text is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lays out one line of the table: a star column per shown column.</summary>
    private Grid NewLine()
    {
        var line = new Grid { ColumnSpacing = 8 };
        foreach (var column in columns)
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DestinationCells.Column(column).Weight, GridUnitType.Star) });
        return line;
    }

    private Row NewRow(string key)
    {
        var row = new Row(key);
        var line = NewLine();
        row.Texts = new TextBlock[columns.Count];
        for (int i = 0; i < columns.Count; i++)
        {
            bool value = DestinationCells.Column(columns[i]).Value;
            var text = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(text, i);
            line.Children.Add(text);
            row.Texts[i] = text;
        }
        row.Item = new ListViewItem { Tag = key, Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(16, 0, 12, 0), MinHeight = 30 };
        return row;
    }

    /// <summary>Sets every cell's text and tooltip, touching only what changed.</summary>
    private void Update(Row row, ConnectionDestination value, DestinationCellContext context)
    {
        row.Value = value;
        var next = DestinationCells.Build(value, context);
        if (next == row.Cells) return;
        row.Cells = next;
        for (int i = 0; i < columns.Count; i++)
        {
            string text = next.Cell(columns[i]);
            if (row.Texts[i].Text != text) row.Texts[i].Text = text;
            ToolTipService.SetToolTip(row.Texts[i], next.Tip(columns[i]));
        }
        AutomationProperties.SetName(row.Item, next.Spoken);
    }

    /// <summary>Moves the list to <paramref name="target"/>: gone rows leave (a selected one clears the selection), kept rows move only
    /// when their place changed, and new rows are inserted at their place. Row items are kept per address, so the selection and the
    /// scroll offset survive.</summary>
    private void Reconcile(IReadOnlyList<Row> target)
    {
        string? selectedKey = list.SelectedItem is ListViewItem { Tag: string key } ? key : null;
        kept.Clear(); foreach (var row in target) kept[row.Key] = row;
        syncing = true;
        try
        {
            for (int i = list.Items.Count - 1; i >= 0; i--)
                if (list.Items[i] is ListViewItem { Tag: string gone } && !kept.ContainsKey(gone)) list.Items.RemoveAt(i);
            for (int i = 0; i < target.Count; i++)
            {
                if (i < list.Items.Count && ReferenceEquals(list.Items[i], target[i].Item)) continue;
                int at = list.Items.IndexOf(target[i].Item);
                if (at >= 0) list.Items.RemoveAt(at);
                list.Items.Insert(i, target[i].Item);
            }
            rows.Clear(); rows.AddRange(target);
            // Moving the selected item takes it out of the list for a moment, which drops the selection.
            var wanted = selectedKey is not null && kept.TryGetValue(selectedKey, out var selected) ? selected.Item : null;
            if (!ReferenceEquals(list.SelectedItem, wanted)) list.SelectedItem = wanted;
        }
        finally { syncing = false; }
        if (selectedKey is not null && !kept.ContainsKey(selectedKey)) SelectionChanged?.Invoke();
    }

    /// <summary>Swaps in the mode's columns; the sort stays when its column exists in the new mode and otherwise returns to RECEIVED
    /// descending. True when the mode changed: the rows then re-sort and the table scrolls to its top.</summary>
    private bool SetMode(bool value)
    {
        if (history == value) return false;
        history = value; topPending = true;
        var key = ConnectionDestinationSort.InMode(sortKey, value);
        if (key != sortKey) { sortKey = key; sortDescending = true; }
        columns = ConnectionDestinationSort.Columns(value);
        // The rows hold one text per column, so another column set needs new rows.
        bool selected = list.SelectedItem is not null;
        syncing = true;
        try { list.Items.Clear(); rows.Clear(); kept.Clear(); }
        finally { syncing = false; }
        if (selected) SelectionChanged?.Invoke();
        footnote.Text = DestinationCopy.Footnote(value);
        BuildHeader();
        return true;
    }

    private void BuildHeader()
    {
        header.Children.Clear(); header.ColumnDefinitions.Clear();
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var (title, weight, value) = DestinationCells.Column(column);
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            string arrow = column != sortKey ? "" : sortDescending ? " ▼" : " ▲";
            var button = new Button
            {
                Content = new TextBlock { Text = title + arrow, FontSize = 10, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis },
                Padding = new Thickness(0, 2, 0, 2), MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(button, "Sort by " + title);
            ToolTipService.SetToolTip(button, DestinationCopy.HeaderTip(column, history == true));
            button.Click += (_, _) => SortBy(column);
            Grid.SetColumn(button, i);
            header.Children.Add(button);
        }
    }

    /// <summary>A header click: number and time columns sort descending first, the text columns ascending; clicking the sorted column flips it.</summary>
    private void SortBy(ConnectionDestinationColumn key)
    {
        if (key == sortKey) sortDescending = !sortDescending;
        else { sortKey = key; sortDescending = ConnectionDestinationSort.DescendingFirst(key); }
        BuildHeader();
        Reconcile(Sorted(rows));
    }
}
