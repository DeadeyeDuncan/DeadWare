using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
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
    private long lastSample = Stopwatch.GetTimestamp();

    public ChartCard(string title, string unit, int seed)
    {
        this.unit = unit;
        source = new SyntheticSource(seed);
        reading.Style = (Style)Application.Current.Resources["TitleTextBlockStyle"];
        reading.Text = "—";
        canvas.Draw += OnDraw;
        canvas.Margin = new Thickness(0, 8, 0, 0);
        if (App.Smooth) ElementCompositionPreview.SetIsTranslationEnabled(canvas, true);

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
        lastSample = Stopwatch.GetTimestamp();
        if (!Drawing) return;
        Refresh();
        if (App.Smooth) StartScroll();
    }

    /// <summary>
    /// Slides the drawn chart one sample's width to the left over the next second, on the compositor: no redraw and
    /// no work on the UI thread until the next sample.
    /// </summary>
    private void StartScroll()
    {
        float width = (float)canvas.ActualWidth - LabelGutter;
        if (width <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(canvas);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.Zero);
        animation.InsertKeyFrame(1, new Vector3(-width / (Capacity - 1), 0, 0), visual.Compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromSeconds(1);
        visual.StartAnimation("Translation", animation);
    }

    /// <summary>Redraws without a new sample, so the trace scrolls between samples.</summary>
    public void Animate() => canvas.Invalidate();

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

        // With --every-frame the trace slides left by one sample's width over the second after each sample.
        float left = LabelGutter;
        if (App.EveryFrame && series.Capacity > 1)
            left -= (float)Math.Min(1, Stopwatch.GetElapsedTime(lastSample).TotalSeconds) * width / (series.Capacity - 1);

        foreach (var run in ChartGeometry.Runs(series, width, height, 0, 100))
        {
            if (run.Count == 1)
            {
                session.FillCircle(left + run[0].X, run[0].Y, 2, accent);
                continue;
            }
            using (var fill = new CanvasPathBuilder(sender))
            {
                fill.BeginFigure(left + run[0].X, height);
                foreach (var point in run) fill.AddLine(left + point.X, point.Y);
                fill.AddLine(left + run[^1].X, height);
                fill.EndFigure(CanvasFigureLoop.Closed);
                using var area = CanvasGeometry.CreatePath(fill);
                session.FillGeometry(area, Color.FromArgb(64, accent.R, accent.G, accent.B));
            }
            using var trace = new CanvasPathBuilder(sender);
            trace.BeginFigure(new Vector2(left + run[0].X, run[0].Y));
            for (int i = 1; i < run.Count; i++) trace.AddLine(left + run[i].X, run[i].Y);
            trace.EndFigure(CanvasFigureLoop.Open);
            using var path = CanvasGeometry.CreatePath(trace);
            session.DrawGeometry(path, accent, 2);
        }
    }
}
