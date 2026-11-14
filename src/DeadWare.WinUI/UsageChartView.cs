using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>Trace colours for the usage charts by graph colour key; a colour saved in settings wins, and cpu and download default to the system accent.</summary>
internal static class UsageColors
{
    private static readonly Dictionary<string, Color> Palette = new()
    {
        ["gpu"] = Color.FromArgb(255, 0x8E, 0x6C, 0xEF), ["cputemp"] = Color.FromArgb(255, 0xF2, 0x99, 0x4A), ["gputemp"] = Color.FromArgb(255, 0xEB, 0x57, 0x57),
        ["memory"] = Color.FromArgb(255, 0x27, 0xAE, 0x60), ["upload"] = Color.FromArgb(255, 0xF2, 0xC9, 0x4C),
        ["diskread"] = Color.FromArgb(255, 0x2D, 0x9C, 0xDB), ["diskwrite"] = Color.FromArgb(255, 0xBB, 0x6B, 0xD9),
    };

    public static Color Accent => (Color)Application.Current.Resources["SystemAccentColor"];

    public static Color Of(MonitorSession session, string key)
    {
        // Saved colours are normalised to "#RRGGBB" when settings load.
        if (session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved))
            return Color.FromArgb(255, Convert.ToByte(saved[1..3], 16), Convert.ToByte(saved[3..5], 16), Convert.ToByte(saved[5..7], 16));
        return Palette.TryGetValue(key, out var color) ? color : Accent;
    }
}

/// <summary>
/// A usage chart drawn with Win2D from <see cref="UsageChartModel"/>: an average line over a min/max band per bucket, with a hover
/// readout. When <see cref="Selectable"/>, a click or Enter activates a bucket and Left and Right move the selection.
/// </summary>
internal sealed class UsageChartView : UserControl
{
    private const double Left = 12, Right = 8, Top = 12, Bottom = 30;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat RightLabelFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };

    private readonly MonitorSession session;
    private readonly CanvasControl canvas = new();
    private IReadOnlyList<UsageChartSeries> series = [];
    private string unit = "%";
    private double? fixedMaximum = 100;
    private UsageChartFrame? frame;
    private Point? pointer;
    private bool selectable;
    private int? selectedIndex;

    public UsageChartView(MonitorSession session)
    {
        this.session = session;
        canvas.Draw += Draw;
        var layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(canvas);
        layers.PointerMoved += (_, e) => { pointer = e.GetCurrentPoint(layers).Position; canvas.Invalidate(); };
        layers.PointerExited += (_, _) => { pointer = null; canvas.Invalidate(); };
        layers.PointerPressed += (_, e) =>
        {
            if (!selectable) return;
            // Handled, so an enclosing scroller does not take the focus back and Left, Right and Enter stay the chart's.
            Focus(FocusState.Pointer);
            var position = e.GetCurrentPoint(layers).Position;
            if (frame is { } current && UsageChartModel.BucketAt(current, position.X, position.Y) is >= 0 and var index)
            {
                SelectedIndex = index;
                BucketActivated?.Invoke(index);
            }
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
        Content = layers;
        MinHeight = 150;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Usage history chart");
        SizeChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => canvas.Invalidate();
    }

    /// <summary>When true, % values under 10 keep one decimal: an app's share of all logical processors is often under 1 %.</summary>
    public bool TenthsBelowTenPercent { get; init; }

    /// <summary>When true the chart is a Tab stop, a click or Enter activates a bucket and Left/Right move <see cref="SelectedIndex"/>.</summary>
    public bool Selectable
    {
        get => selectable;
        set { selectable = value; IsTabStop = value; canvas.Invalidate(); }
    }

    /// <summary>Index into the first series' buckets drawn with an accent hairline; null draws nothing.</summary>
    public int? SelectedIndex
    {
        get => selectedIndex;
        set { if (selectedIndex == value) return; selectedIndex = value; canvas.Invalidate(); }
    }

    /// <summary>Raised with the bucket index when a bucket is clicked or Enter is pressed on the selection.</summary>
    public event Action<int>? BucketActivated;
    /// <summary>Raised with the new index each time Left or Right moves the selection. A click and the setter never raise it.</summary>
    public event Action<int>? SelectionMoved;

    public void SetData(IReadOnlyList<UsageChartSeries> data, string unitLabel, double? maximum)
    {
        series = data; unit = unitLabel; fixedMaximum = maximum;
        if (data.Count > 0) AutomationProperties.SetName(this, string.Join(" and ", data.Select(s => s.Label)) + " history");
        Rebuild();
    }

    private void Rebuild()
    {
        double width = ActualWidth, height = ActualHeight;
        frame = width < 160 || height < 120 ? null
            : UsageChartModel.Build(series, unit, fixedMaximum, new ChartPlot(Left, Top, width - Left - Right, height - Top - Bottom), TenthsBelowTenPercent);
        canvas.Invalidate();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int count = series.Count > 0 ? series[0].View.Buckets.Count : 0;
        if (!selectable || count == 0) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: Move(UsageChartModel.Step(selectedIndex, count, -1)); e.Handled = true; break;
            case Windows.System.VirtualKey.Right: Move(UsageChartModel.Step(selectedIndex, count, 1)); e.Handled = true; break;
            case Windows.System.VirtualKey.Enter when selectedIndex is { } index && index < count: BucketActivated?.Invoke(index); e.Handled = true; break;
        }
    }

    private void Move(int? index)
    {
        if (index is not { } moved) return;
        SelectedIndex = moved;
        SelectionMoved?.Invoke(moved);
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (frame is not { } current) return;
        var drawing = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(170, c, c, c), line = Color.FromArgb(32, c, c, c);
        var plot = current.Plot;
        float left = (float)plot.Left, right = (float)(plot.Left + plot.Width), top = (float)plot.Top, bottom = (float)(plot.Top + plot.Height);
        drawing.FillRectangle(left, top, right - left, bottom - top, Color.FromArgb(10, c, c, c));
        foreach (var value in current.Lines)
        {
            drawing.DrawLine(left, (float)value.Y, right, (float)value.Y, line, 1);
            drawing.DrawText(value.Label, left + 4, (float)value.Y - 14, ink, LabelFormat);
        }

        using var dashes = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash, LineJoin = CanvasLineJoin.Round };
        using var solid = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
        foreach (var trace in current.Traces)
        {
            Color color = UsageColors.Of(session, trace.ColorKey);
            Color band = Color.FromArgb(46, color.R, color.G, color.B);
            foreach (var part in trace.Bands) drawing.FillRectangle((float)part.X, (float)part.Y, (float)part.Width, (float)part.Height, band);
            foreach (var run in trace.Runs)
            {
                // Runs of adjacent full buckets become one line each; a lone bucket stays a dot.
                if (run.Count == 1) { drawing.FillCircle((float)run[0].X, (float)run[0].Y, 1.6f, color); continue; }
                using var builder = new CanvasPathBuilder(sender);
                builder.BeginFigure((float)run[0].X, (float)run[0].Y);
                HistoryChartView.AddRun(builder, [.. run.Select(point => new ChartPoint(point.X, point.Y))], session.Settings.GraphSmoothing);
                builder.EndFigure(CanvasFigureLoop.Open);
                using var geometry = CanvasGeometry.CreatePath(builder);
                drawing.DrawGeometry(geometry, color, 1.8f, trace.Dashed ? dashes : solid);
            }
        }
        drawing.DrawText(current.FromLabel, left, bottom + 8, ink, LabelFormat);
        drawing.DrawText(current.ToLabel, right, bottom + 8, ink, RightLabelFormat);
        if (!current.Any) drawing.DrawText("No history in this range yet", left + 10, (top + bottom) / 2 - 7, ink, LabelFormat);

        if (selectable && selectedIndex is { } picked && picked >= 0 && picked < current.Targets.Count)
            drawing.DrawLine((float)current.Targets[picked].X, top, (float)current.Targets[picked].X, bottom, UsageColors.Accent, 1);

        if (pointer is not { } position) return;
        int hovered = UsageChartModel.BucketAt(current, position.X, position.Y);
        if (UsageChartModel.Hover(current, series, hovered, unit, TenthsBelowTenPercent) is not { } hover) return;
        drawing.DrawLine((float)hover.X, top, (float)hover.X, bottom, ink, 1);
        var rows = new List<(string Text, Color Color)> { (hover.Time, ink) };
        foreach (var value in hover.Values)
        {
            Color color = UsageColors.Of(session, value.ColorKey);
            drawing.FillCircle((float)hover.X, (float)value.Y, 3, color);
            rows.Add((value.Text, color));
        }
        float width = 0;
        foreach (var row in rows)
        {
            using var layout = new CanvasTextLayout(sender, row.Text, LabelFormat, 0, 0);
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        width += 12;
        float height = rows.Count * 16 + 12;
        float anchor = hover.Values.Count > 0 ? (float)hover.Values[0].Y : (top + bottom) / 2;
        float x = (float)hover.X + 10;
        if (x + width > right) x = (float)hover.X - 10 - width;
        float chipTop = Math.Clamp(anchor - height / 2, top, Math.Max(top, bottom - height));
        drawing.FillRoundedRectangle(x, chipTop, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        drawing.DrawRoundedRectangle(x, chipTop, width, height, 4, 4, line, 1);
        for (int i = 0; i < rows.Count; i++) drawing.DrawText(rows[i].Text, x + 6, chipTop + 6 + i * 16, rows[i].Color, LabelFormat);
    }
}

/// <summary>
/// A read-only table whose rows can be selected, for tables of at most a few hundred rows that refresh once a minute. A refresh
/// with the same rows in the same order rewrites only their text, so the selection, focus and scroll position stay.
/// </summary>
internal sealed class PickTable : UserControl
{
    private readonly IReadOnlyList<TableColumn> columns;
    private readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly List<string> keys = [];
    private bool syncing;

    public PickTable(IReadOnlyList<TableColumn> columns, string name)
    {
        this.columns = columns;
        var header = NewRow();
        header.Padding = new Thickness(16, 8, 12, 8);
        for (int i = 0; i < columns.Count; i++)
        {
            var cell = (TextBlock)header.Children[i];
            cell.Text = columns[i].Title.ToUpperInvariant();
            cell.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
            cell.Opacity = 0.7;
            cell.TextWrapping = TextWrapping.Wrap;
        }
        AutomationProperties.SetName(list, name);
        list.SelectionChanged += (_, _) => { if (!syncing) SelectionChanged?.Invoke(SelectedKey); };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(list, 1);
        layout.Children.Add(header);
        layout.Children.Add(list);
        Content = layout;
    }

    /// <summary>The selected row's key, or null.</summary>
    public string? SelectedKey => list.SelectedItem is ListViewItem { Tag: string key } ? key : null;

    /// <summary>Raised with the selected row's key (null for none) when the user changes the selection, never by <see cref="SetRows"/>.</summary>
    public event Action<string?>? SelectionChanged;

    /// <summary>Shows these rows and selects the one with <paramref name="selectKey"/>, if it is among them.</summary>
    public void SetRows(IReadOnlyList<TableRow> rows, string? selectKey)
    {
        syncing = true;
        try
        {
            if (!keys.SequenceEqual(rows.Select(r => r.Key)))
            {
                list.Items.Clear(); keys.Clear();
                foreach (var row in rows)
                {
                    keys.Add(row.Key);
                    list.Items.Add(new ListViewItem
                    {
                        Tag = row.Key, Content = NewRow(), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(16, 0, 12, 0), MinHeight = 32,
                    });
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var item = (ListViewItem)list.Items[i];
                var cells = (Grid)item.Content;
                for (int column = 0; column < cells.Children.Count; column++)
                {
                    string text = column < rows[i].Cells.Count ? rows[i].Cells[column] : "";
                    var cell = (TextBlock)cells.Children[column];
                    if (cell.Text != text) cell.Text = text;
                }
                ToolTipService.SetToolTip(item, rows[i].Tip);
                if (rows[i].Cells.Count > 0) AutomationProperties.SetName(item, rows[i].Cells[0]);
            }
            int selected = selectKey is null ? -1 : keys.IndexOf(selectKey);
            if (list.SelectedIndex != selected) list.SelectedIndex = selected;
        }
        finally { syncing = false; }
    }

    /// <summary>Scrolls back to the first row, for a new ranking.</summary>
    public void ScrollToTop()
    {
        if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
    }

    private Grid NewRow()
    {
        var row = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < columns.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columns[i].Weight, GridUnitType.Star) });
            var cell = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = columns[i].Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        return row;
    }
}
