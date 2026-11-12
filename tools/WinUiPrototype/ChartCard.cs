using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.UI;
using WinUiPrototype.Logic;

namespace WinUiPrototype;

/// <summary>One Overview card: a title, the current reading and a Win2D history chart on synthetic samples.</summary>
internal sealed class ChartCard : UserControl
{
    private const int Capacity = 600;
    private const float LabelGutter = 28;
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };

    private readonly SampleSeries series = new(Capacity);
    private readonly SyntheticSource source;
    private readonly string unit;
    private readonly TextBlock reading = new();
    private readonly CanvasControl canvas = new();

    public ChartCard(string title, string unit, int seed)
    {
        this.unit = unit;
        source = new SyntheticSource(seed);
        reading.Style = (Style)Application.Current.Resources["TitleTextBlockStyle"];
        reading.Text = "—";
        canvas.Draw += OnDraw;
        canvas.Margin = new Thickness(0, 8, 0, 0);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        Grid.SetRow(reading, 1);
        Grid.SetRow(canvas, 2);
        layout.Children.Add(heading);
        layout.Children.Add(reading);
        layout.Children.Add(canvas);

        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = layout,
        };
    }

    /// <summary>Whether a new sample redraws the chart; off while the window is hidden.</summary>
    public bool Drawing { get; set; } = true;

    /// <summary>Takes one sample, updates the reading and, when drawing, invalidates the chart.</summary>
    public void Tick()
    {
        series.Add(source.Next());
        if (!Drawing) return;
        Refresh();
    }

    /// <summary>Brings the reading and chart up to date with the samples taken while hidden.</summary>
    public void Refresh()
    {
        reading.Text = series.Latest is { } value ? $"{value:0.0} {unit}" : "—";
        canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        Interlocked.Increment(ref OverviewPage.FramesDrawn);
        var session = args.DrawingSession;
        float width = (float)sender.ActualWidth - LabelGutter, height = (float)sender.ActualHeight;
        if (width <= 0 || height <= 0) return;

        bool dark = ActualTheme == ElementTheme.Dark;
        Color ink = dark ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(160, 0, 0, 0);
        Color grid = dark ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(32, 0, 0, 0);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];

        for (int line = 0; line < 4; line++)
        {
            float y = height * line / 3;
            session.DrawLine(LabelGutter, y, LabelGutter + width, y, grid, 1);
        }
        session.DrawText("100", 0, 0, ink, LabelFormat);
        session.DrawText("0", 0, height - 14, ink, LabelFormat);

        foreach (var run in ChartGeometry.Runs(series, width, height, 0, 100))
        {
            if (run.Count == 1)
            {
                session.FillCircle(LabelGutter + run[0].X, run[0].Y, 2, accent);
                continue;
            }
            using (var fill = new CanvasPathBuilder(sender))
            {
                fill.BeginFigure(LabelGutter + run[0].X, height);
                foreach (var point in run) fill.AddLine(LabelGutter + point.X, point.Y);
                fill.AddLine(LabelGutter + run[^1].X, height);
                fill.EndFigure(CanvasFigureLoop.Closed);
                using var area = CanvasGeometry.CreatePath(fill);
                session.FillGeometry(area, Color.FromArgb(64, accent.R, accent.G, accent.B));
            }
            using var trace = new CanvasPathBuilder(sender);
            trace.BeginFigure(new Vector2(LabelGutter + run[0].X, run[0].Y));
            for (int i = 1; i < run.Count; i++) trace.AddLine(LabelGutter + run[i].X, run[i].Y);
            trace.EndFigure(CanvasFigureLoop.Open);
            using var path = CanvasGeometry.CreatePath(trace);
            session.DrawGeometry(path, accent, 2);
        }
    }
}
