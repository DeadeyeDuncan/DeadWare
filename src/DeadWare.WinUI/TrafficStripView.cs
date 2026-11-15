using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>Lays its children out left to right and wraps to a new line when the width runs out.</summary>
internal sealed class FlowPanel : Panel
{
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;
            if (x > 0 && x + size.Width > availableSize.Width) { y += line + RowSpacing; x = 0; line = 0; }
            x += size.Width; widest = Math.Max(widest, x); x += ColumnSpacing;
            line = Math.Max(line, size.Height);
        }
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(widest, availableSize.Width) : widest, y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) { child.Arrange(new Rect(0, 0, 0, 0)); continue; }
            if (x > 0 && x + size.Width > finalSize.Width) { y += line + RowSpacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, Math.Max(0, finalSize.Width)), size.Height));
            x += size.Width + ColumnSpacing;
            line = Math.Max(line, size.Height);
        }
        return finalSize;
    }
}

/// <summary>
/// The traffic strip: a caption, the stacked chart drawn with Win2D from <see cref="TrafficStripModel"/>, a legend and the notes
/// under it. When <see cref="Selectable"/>, a click or Enter activates a slot and Left and Right move the selection; on the
/// Connections page each application's legend entry is a button that filters to it.
/// </summary>
internal sealed class TrafficStripView : UserControl
{
    private const double FullOpacity = 0.85, PartialOpacity = 0.45;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat RightLabelFormat = new() { FontSize = 11, HorizontalAlignment = CanvasHorizontalAlignment.Right };
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 0x2D, 0x9C, 0xDB), Color.FromArgb(255, 0x8E, 0x6C, 0xEF), Color.FromArgb(255, 0x27, 0xAE, 0x60),
        Color.FromArgb(255, 0xF2, 0x99, 0x4A), Color.FromArgb(255, 0xEB, 0x57, 0x57), Color.FromArgb(255, 0xF2, 0xC9, 0x4C),
    ];

    private readonly TrafficStripKind page;
    private readonly TrafficStripModel model;
    private readonly CanvasControl canvas = new();
    private readonly Grid chart;
    private readonly TextBlock caption = new() { Text = "Traffic by application", FontSize = 11, Opacity = 0.7 };
    private readonly TextBlock notes = new() { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly FlowPanel legend = new();
    private readonly List<(FrameworkElement Entry, TextBlock Label)> entries = [];
    private TrafficStripHover? hover;
    private Point? pointer;
    private bool selectable, focused;
    private int? selectedIndex;
    private DateTimeOffset? markTime;

    public TrafficStripView(TrafficStripKind page)
    {
        this.page = page;
        model = new TrafficStripModel(page);
        canvas.Draw += Draw;
        chart = new Grid { Height = 130, MinHeight = 120, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        chart.Children.Add(canvas);
        AutomationProperties.SetName(this, "Traffic by application");
        chart.PointerMoved += (_, e) => { pointer = e.GetCurrentPoint(chart).Position; UpdateHover(); };
        chart.PointerExited += (_, _) => { pointer = null; UpdateHover(); };
        chart.PointerPressed += (_, e) =>
        {
            if (!selectable) return;
            // Handled, so an enclosing ScrollViewer does not take the focus back and Left, Right and Enter stay the chart's.
            Focus(FocusState.Pointer);
            HandleClick(e.GetCurrentPoint(chart).Position);
            e.Handled = true;
        };
        chart.SizeChanged += (_, _) => Relayout();
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => { focused = true; canvas.Invalidate(); };
        LostFocus += (_, _) => { focused = false; canvas.Invalidate(); };
        ActualThemeChanged += (_, _) => canvas.Invalidate();
        Content = new StackPanel { Spacing = 4, Children = { caption, chart, legend, notes } };
    }

    public TrafficStrip? Strip => model.Strip;
    public bool FilterNotRecorded => model.FilterNotRecorded;

    /// <summary>When true the chart takes focus, a click or Enter activates a slot and Left and Right move the selection.</summary>
    public bool Selectable
    {
        get => selectable;
        set { selectable = value; IsTabStop = value; UseSystemFocusVisuals = false; canvas.Invalidate(); }
    }

    /// <summary>Index into the strip's slots drawn with an accent hairline at its middle when <see cref="MarkTime"/> is unset.</summary>
    public int? SelectedIndex
    {
        get => selectedIndex;
        set { if (selectedIndex == value) return; selectedIndex = value; canvas.Invalidate(); UpdateLegend(); }
    }

    /// <summary>When set, the accent hairline is drawn here, and Left and Right from no selection go to the listed slots around it.</summary>
    public DateTimeOffset? MarkTime
    {
        get => markTime;
        set { if (markTime == value) return; markTime = value; canvas.Invalidate(); }
    }

    /// <summary>Raised on a click (at the time under the pointer, clamped into the slot) and on Enter (at the slot's start).</summary>
    public event Action<int, DateTimeOffset>? SlotActivated;
    /// <summary>Raised with the new index when Left or Right moves the selection.</summary>
    public event Action<int>? SelectionMoved;
    /// <summary>Raised with the application's key and name when its legend button is clicked (Connections only).</summary>
    public event Action<string, string>? ApplicationChosen;

    /// <summary>Shows a strip; null shows "History unavailable".</summary>
    public void Apply(TrafficStrip? strip, DateTimeOffset dueUntil, DateTimeOffset now, bool filterNotRecorded = false)
    {
        if (strip is null) { ShowText("History unavailable"); return; }
        model.SetData(strip, dueUntil, now, filterNotRecorded);
        Changed();
    }

    /// <summary>Clears the data and shows a text in place of the chart.</summary>
    public void ShowText(string text)
    {
        model.SetText(text);
        Changed();
    }

    private void Changed()
    {
        hover = null;
        caption.Text = model.Caption();
        var lines = model.NoteLines();
        string joined = string.Join("\n", lines);
        if (notes.Text != joined) notes.Text = joined;
        notes.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Relayout();
        RebuildLegend();
    }

    private void Relayout()
    {
        model.Layout(chart.ActualWidth, chart.ActualHeight);
        UpdateHover();
        canvas.Invalidate();
    }

    private int? ShownIndex => hover?.Index ?? (selectedIndex is { } index && index >= 0 && index < (model.Strip?.Slots.Count ?? 0) ? index : null);

    private void UpdateHover()
    {
        int? before = hover?.Index;
        hover = pointer is { } p ? model.Hover(new PlotPoint(p.X, p.Y)) : null;
        canvas.Invalidate();
        if (hover?.Index != before) UpdateLegend();
    }

    private void HandleClick(Point point)
    {
        if (!selectable || model.Strip is null || model.SlotAt(new PlotPoint(point.X, point.Y)) is not { } index) return;
        var at = model.ClickTime(index, point.X);
        SelectedIndex = index; SlotActivated?.Invoke(index, at);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!selectable || model.Strip is not { } strip || strip.Slots.Count == 0) return;
        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.Right:
                if (model.Step(e.Key == VirtualKey.Left, selectedIndex, markTime) is { } next) { SelectedIndex = next; SelectionMoved?.Invoke(next); }
                e.Handled = true; break;
            case VirtualKey.Enter when selectedIndex is { } index && index >= 0 && index < strip.Slots.Count:
                SlotActivated?.Invoke(index, strip.Slots[index].Start); e.Handled = true; break;
        }
    }

    private Color SeriesColor(int series, bool dark) =>
        series < (model.Strip?.Applications.Count ?? 0) ? Palette[series % Palette.Length] : dark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 120, 120, 120);

    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255), color.R, color.G, color.B);

    // An exception that escapes a Win2D draw callback ends the process, so a drawing fault is recorded and that frame is skipped.
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try { DrawStrip(sender, args); }
        catch (Exception error) when (error is not OutOfMemoryException) { App.Note($"The traffic strip could not be drawn: {error}"); }
    }

    private void DrawStrip(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!model.Drawn && model.Message is null) return;
        var ds = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(230, c, c, c), muted = Color.FromArgb(170, c, c, c), hairline = Color.FromArgb(48, c, c, c);
        Color accent = UsageColors.Accent;
        var plot = model.Plot;
        float left = (float)plot.X, right = (float)plot.Right, top = (float)plot.Y, bottom = (float)plot.Bottom;
        if (right - left < 1 || bottom - top < 1) return;
        ds.FillRoundedRectangle(left, top, right - left, bottom - top, 4, 4, Color.FromArgb(10, c, c, c));
        if (selectable && focused) ds.DrawRoundedRectangle(left - 2, top - 2, right - left + 4, bottom - top + 4, 6, 6, accent, 1);
        if (model.Message is { } message) { ds.DrawText(message, left + 10, (top + bottom) / 2 - 8, muted, LabelFormat); return; }
        if (model.Strip is null) return;

        if (model.NotKept is { } notKept)
        {
            ds.FillRectangle((float)notKept.X, (float)notKept.Y, (float)notKept.Width, (float)notKept.Height, Alpha(hairline, .9));
            if (notKept.Width >= 60 && model.BandLabel is { } label) ds.DrawText(label, (float)notKept.X + 8, (float)(notKept.Y + notKept.Height / 2 - 8), muted, LabelFormat);
        }
        foreach (var span in model.Hatch)
        {
            if (span.Width <= 0) continue;
            using (ds.CreateLayer(1, new Rect(span.X, span.Y, span.Width, span.Height)))
                for (double x = span.X - span.Height; x < span.Right; x += 6)
                    ds.DrawLine((float)x, (float)span.Bottom, (float)(x + span.Height), (float)span.Y, hairline, 1);
        }
        foreach (var (y, text) in model.AxisLabels())
        {
            ds.DrawLine(left, (float)y, right, (float)y, Alpha(hairline, .6), 1);
            ds.DrawText(text, right - 4, (float)Math.Max(top, y - 15), muted, RightLabelFormat);
        }
        foreach (var band in model.Bands)
        {
            var fill = Alpha(SeriesColor(band.Series, dark), band.Full ? FullOpacity : PartialOpacity);
            foreach (var polygon in band.Polygons)
            {
                if (polygon.Length < 3) continue;
                using var geometry = CanvasGeometry.CreatePolygon(sender, polygon.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray());
                ds.FillGeometry(geometry, fill);
            }
        }
        Color panel = dark ? Color.FromArgb(160, 32, 32, 32) : Color.FromArgb(160, 250, 250, 250);
        foreach (var (x0, x1, y) in model.Separators) ds.DrawLine((float)x0, (float)y, (float)x1, (float)y, panel, 1);
        foreach (var (x0, x1) in model.Baseline) ds.DrawLine((float)x0, bottom, (float)x1, bottom, Alpha(ink, .35), 1);
        var (fromLabel, toLabel) = model.DateLabels();
        ds.DrawText(fromLabel, left, bottom + 6, muted, LabelFormat);
        ds.DrawText(toLabel, right, bottom + 6, muted, RightLabelFormat);
        if (model.EmptyText is { } empty) ds.DrawText(empty, left + 10, (top + bottom) / 2 - 8, muted, LabelFormat);
        if (selectable)
        {
            double? x = markTime is { } mark ? model.X(mark) : selectedIndex is { } picked ? model.SlotMiddle(picked) : null;
            if (x is { } at) ds.DrawLine((float)at, top, (float)at, bottom, accent, 1);
        }
        if (hover is { Lines.Count: > 0 } shown)
        {
            ds.DrawLine((float)shown.X, top, (float)shown.X, bottom, Alpha(ink, .35), 1);
            var lines = shown.Lines.Select((line, i) => (line, i == 0 ? muted : ink)).ToArray();
            ConnectionMapView.DrawChip(sender, ds, new PlotPoint(shown.X, shown.Y), plot, lines, dark, hairline);
        }
    }

    /// <summary>Rebuilds the legend's entries; only a new strip or text changes which entries there are.</summary>
    private void RebuildLegend()
    {
        legend.Children.Clear(); entries.Clear();
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach (var row in model.LegendRows(ShownIndex))
        {
            var label = new TextBlock { Text = row.Text, FontSize = 11, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (row.Series < 0) label.Opacity = 0.7;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (row.Series >= 0)
                content.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(SeriesColor(row.Series, dark)), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(label);
            FrameworkElement entry;
            if (page == TrafficStripKind.Connections && row.Application is { } app)
            {
                var button = new Button { Content = content, Padding = new Thickness(6, 2, 6, 2), MinHeight = 0 };
                AutomationProperties.SetName(button, $"{app.Name}: show only this application");
                button.Click += (_, _) => ApplicationChosen?.Invoke(app.Key, app.Name);
                entry = button;
            }
            else entry = new Border { Child = content, Padding = new Thickness(6, 2, 6, 2) };
            entry.MaxWidth = 240;
            ToolTipService.SetToolTip(entry, row.Tip);
            legend.Children.Add(entry);
            entries.Add((entry, label));
        }
    }

    /// <summary>Hover and selection change only the texts and tooltips: the entries stay, and so does keyboard focus on a legend button.</summary>
    private void UpdateLegend()
    {
        var rows = model.LegendRows(ShownIndex);
        if (rows.Count != entries.Count) { RebuildLegend(); return; }
        for (int i = 0; i < rows.Count; i++)
        {
            if (entries[i].Label.Text != rows[i].Text) entries[i].Label.Text = rows[i].Text;
            ToolTipService.SetToolTip(entries[i].Entry, rows[i].Tip);
        }
    }
}
