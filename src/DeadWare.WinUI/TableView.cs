using System.Collections.ObjectModel;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>A table column: its heading, its share of the width, and whether its cells are right-aligned numbers.</summary>
internal sealed record TableColumn(string Title, double Weight, bool Numeric = false);

/// <summary>
/// A read-only table for rows that refresh every second. Rows are virtualised (only those on screen have elements),
/// and a refresh rewrites only the rows whose text changed, so scrolling position survives and idle rows cost nothing.
/// </summary>
internal sealed class TableView : UserControl
{
    /// <summary>Row elements bound since start-up, across all tables; reported by <c>--exit-after</c> for the smoke check.</summary>
    public static long RowsBound;

    private readonly ObservableCollection<TableRow> rows = [];
    private readonly IReadOnlyList<TableColumn> columns;
    private readonly Grid header;
    private readonly ScrollViewer scroller;

    public TableView(IReadOnlyList<TableColumn> columns)
    {
        this.columns = columns;
        header = RowFactory.NewRow(columns, heading: true);
        for (int i = 0; i < columns.Count; i++)
        {
            int column = i;
            var cell = (TextBlock)header.Children[i];
            cell.Text = columns[i].Title.ToUpperInvariant();
            cell.Tapped += (_, _) => HeaderClicked?.Invoke(column);
        }
        var repeater = new ItemsRepeater { ItemsSource = rows, ItemTemplate = new RowFactory(columns, this), Layout = new StackLayout() };
        var scroll = new ScrollViewer { Content = repeater, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(scroll, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroll);
        Content = layout;
        scroller = scroll;
        KeyDown += OnKeyDown;
        PointerEntered += (_, _) => PointerOver = true;
        PointerExited += (_, _) => PointerOver = false;
        PointerCanceled += (_, _) => PointerOver = false;
    }

    // Row elements on screen now, with the key each shows, so a selection change repaints only those.
    private readonly Dictionary<Grid, string> live = [];
    private string? selectedKey;

    /// <summary>True while the pointer is over the table; a page can hold its row order still meanwhile.</summary>
    public bool PointerOver { get; private set; }

    /// <summary>When true, clicking a row selects it and the selected row is highlighted. Off by default.</summary>
    public bool Selectable
    {
        get => selectable;
        // A selectable table is one tab stop: the arrow keys move its selection.
        set { selectable = value; IsTabStop = value; UseSystemFocusVisuals = value; }
    }
    private bool selectable;

    /// <summary>Moves the selection with Up, Down, Home, End, Page Up and Page Down and keeps it in view; Enter acts as a double-click.</summary>
    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!Selectable || rows.Count == 0) return;
        int current = -1;
        if (selectedKey is not null) for (int i = 0; i < rows.Count && current < 0; i++) if (rows[i].Key == selectedKey) current = i;
        double rowHeight = RowHeight();
        int page = Math.Max(1, (int)(scroller.ViewportHeight / rowHeight) - 1);
        int target;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down: target = current + 1; break;
            case Windows.System.VirtualKey.Up: target = current < 0 ? 0 : current - 1; break;
            case Windows.System.VirtualKey.Home: target = 0; break;
            case Windows.System.VirtualKey.End: target = rows.Count - 1; break;
            case Windows.System.VirtualKey.PageDown: target = current + page; break;
            case Windows.System.VirtualKey.PageUp: target = current < 0 ? 0 : current - page; break;
            case Windows.System.VirtualKey.Enter:
                if (selectedKey is { } chosen && current >= 0) { RowDoubleClicked?.Invoke(chosen); e.Handled = true; }
                return;
            default: return;
        }
        target = Math.Clamp(target, 0, rows.Count - 1);
        SelectedKey = rows[target].Key;
        ScrollTo(target, rowHeight);
        e.Handled = true;
    }

    private double RowHeight()
    {
        foreach (var element in live.Keys) if (element.ActualHeight > 0) return element.ActualHeight;
        return 32;
    }

    /// <summary>Scrolls just far enough that the row at this position is inside the viewport. Rows share one height, so the position is arithmetic
    /// and works for rows that have no element yet.</summary>
    private void ScrollTo(int index, double rowHeight)
    {
        double top = index * rowHeight, bottom = top + rowHeight, offset = scroller.VerticalOffset, viewport = scroller.ViewportHeight;
        if (viewport <= 0) return;
        if (top < offset) scroller.ChangeView(null, top, null, true);
        else if (bottom > offset + viewport) scroller.ChangeView(null, bottom - viewport, null, true);
    }

    /// <summary>The selected row's key, or null. Setting it repaints the rows on screen; it survives refreshes while a row has that key.</summary>
    public string? SelectedKey
    {
        get => selectedKey;
        set
        {
            if (selectedKey == value) return;
            selectedKey = value;
            foreach (var (element, key) in live) Paint(element, key);
            SelectionChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;

    /// <summary>A row was clicked: its key, the column under the pointer, and the pointer's distance from that cell's left edge.</summary>
    public event Action<string, int, double>? RowClicked;

    public event Action<string>? RowDoubleClicked;

    /// <summary>A row was right-clicked or long-pressed: its key, its element, and the pointer position within it.</summary>
    public event Action<string, FrameworkElement, Windows.Foundation.Point>? RowContext;

    private void Paint(Grid element, string key) =>
        element.Background = Selectable && key == selectedKey && Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object brush) && brush is Brush fill
            ? fill : Clear;

    private static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    private void Hook(Grid element)
    {
        element.Tapped += (_, e) =>
        {
            if (!live.TryGetValue(element, out string? key)) return;
            if (Selectable) { SelectedKey = key; Focus(FocusState.Pointer); }
            double x = e.GetPosition(element).X;
            int column = 0;
            double offset = x;
            for (int i = 0; i < element.Children.Count; i++)
            {
                if (element.Children[i] is not FrameworkElement cell) continue;
                double left = cell.ActualOffset.X;
                // A right-aligned cell starts where its text does; the column it sits in is the last one that starts at or before the pointer.
                if (i < element.ColumnDefinitions.Count) left = ColumnLeft(element, i);
                if (x >= left) { column = i; offset = x - left; }
            }
            RowClicked?.Invoke(key, column, offset);
        };
        element.DoubleTapped += (_, _) => { if (live.TryGetValue(element, out string? key)) RowDoubleClicked?.Invoke(key); };
        element.RightTapped += (_, e) =>
        {
            if (!live.TryGetValue(element, out string? key)) return;
            if (Selectable) SelectedKey = key;
            RowContext?.Invoke(key, element, e.GetPosition(element));
            e.Handled = true;
        };
    }

    private static double ColumnLeft(Grid row, int column)
    {
        double left = row.Padding.Left;
        for (int i = 0; i < column; i++) left += row.ColumnDefinitions[i].ActualWidth + row.ColumnSpacing;
        return left;
    }

    public int Count => rows.Count;

    /// <summary>Raised with the column's position when its heading is clicked; a page that sorts handles it.</summary>
    public event Action<int>? HeaderClicked;

    /// <summary>Marks one heading as the sort column with an arrow, or none when <paramref name="column"/> is null.</summary>
    public void SetSort(int? column, bool descending)
    {
        for (int i = 0; i < columns.Count; i++)
            ((TextBlock)header.Children[i]).Text = columns[i].Title.ToUpperInvariant() + (i == column ? (descending ? " ▼" : " ▲") : "");
    }

    /// <summary>Shows these rows, in this order, redrawing only the ones that changed.</summary>
    public void SetRows(IReadOnlyList<TableRow> source) => TableSync.Apply(rows, source);

    /// <summary>Builds and recycles row elements for the repeater.</summary>
    private sealed class RowFactory(IReadOnlyList<TableColumn> columns, TableView owner) : IElementFactory
    {
        private readonly Stack<Grid> spare = new();

        public static Grid NewRow(IReadOnlyList<TableColumn> columns, bool heading)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, heading ? 8 : 6, 12, heading ? 8 : 6) };
            for (int i = 0; i < columns.Count; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columns[i].Weight, GridUnitType.Star) });
                var cell = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = columns[i].Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };
                if (heading)
                {
                    cell.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
                    cell.Opacity = 0.7;
                }
                Grid.SetColumn(cell, i);
                row.Children.Add(cell);
            }
            return row;
        }

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            Interlocked.Increment(ref RowsBound);
            Grid element;
            if (spare.Count > 0) element = spare.Pop();
            else { element = NewRow(columns, heading: false); owner.Hook(element); }
            if (args.Data is TableRow row)
            {
                owner.live[element] = row.Key;
                owner.Paint(element, row.Key);
                for (int i = 0; i < element.Children.Count; i++) ((TextBlock)element.Children[i]).Text = i < row.Cells.Count ? row.Cells[i] : "";
                ToolTipService.SetToolTip(element, row.Tip);
                // A screen reader reads a row as its cells in order.
                AutomationProperties.SetName(element, string.Join(", ", row.Cells.Where(cell => cell.Length > 0)));
            }
            return element;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is Grid row) { owner.live.Remove(row); spare.Push(row); }
        }
    }
}
