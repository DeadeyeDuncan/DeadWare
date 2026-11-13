using System.Collections.ObjectModel;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        var repeater = new ItemsRepeater { ItemsSource = rows, ItemTemplate = new RowFactory(columns), Layout = new StackLayout() };
        var scroll = new ScrollViewer { Content = repeater, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(scroll, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroll);
        Content = layout;
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
    private sealed class RowFactory(IReadOnlyList<TableColumn> columns) : IElementFactory
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
            var element = spare.Count > 0 ? spare.Pop() : NewRow(columns, heading: false);
            if (args.Data is TableRow row)
            {
                for (int i = 0; i < element.Children.Count; i++) ((TextBlock)element.Children[i]).Text = i < row.Cells.Count ? row.Cells[i] : "";
                ToolTipService.SetToolTip(element, row.Tip);
            }
            return element;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is Grid row) spare.Push(row);
        }
    }
}
