using System.Windows;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Cooling Health chart: spline through connected windows, solid hairline grid, hover chip over a window.</summary>
internal static class ThermalChartUiChecks
{
    internal static void Run()
    {
        var chart=new ThermalHistoryChart();
        var windows=Enumerable.Range(0,6).Select(i=>Window(i*60,60+i*2)).ToArray();
        var from=DateTimeOffset.UnixEpoch;var to=from.AddSeconds(360);
        chart.SetData(windows,"temperature",from,to);
        chart.Measure(new Size(620,240));chart.Arrange(new Rect(0,0,620,240));chart.UpdateLayout();
        var drawings=Shapes(VisualTreeHelper.GetDrawing(chart)!).ToArray();
        var trace=Assert.Single(drawings,d=>d.Geometry is StreamGeometry&&d.Pen is not null);
        var figures=PathGeometry.CreateFromGeometry(trace.Geometry).Figures;
        Assert.Single(figures);
        Assert.All(figures[0].Segments,segment=>Assert.True(segment is BezierSegment or PolyBezierSegment,segment.GetType().Name));
        Assert.All(drawings.Where(d=>d.Geometry is LineGeometry line&&line.StartPoint.Y==line.EndPoint.Y&&d.Pen is not null),d=>Assert.Empty(d.Pen.DashStyle.Dashes));
        Assert.Equal(6,drawings.Count(d=>d.Geometry is EllipseGeometry));
        var plot=chart.PlotRect;Assert.True(plot.Width>400&&plot.Height>100,$"Plot {plot}");
        foreach(var glyph in Glyphs(VisualTreeHelper.GetDrawing(chart)!).Where(g=>Text(g).EndsWith("°C")))Assert.True(plot.Contains(glyph.Bounds),$"Label {Text(glyph)} at {glyph.Bounds} outside {plot}.");
        // Window 2 spans 120–170 s of the 360 s range; its centre is at 145 s.
        chart.SetHover(new Point(plot.Left+plot.Width*(145.0/360),plot.Top+20));
        Assert.Equal(2,chart.HoverIndex);
        chart.UpdateLayout();
        var hovered=VisualTreeHelper.GetDrawing(chart)!;
        Assert.Contains(Shapes(hovered),d=>ReferenceEquals(d.Brush,Application.Current.Resources["GlassPopup"]));
        // Font fallback can split "°" into its own run, so match the accent-colored run that starts the value.
        Assert.Contains(Glyphs(hovered),g=>Text(g)=="64 °C");
        chart.SetHover(new Point(plot.Left+plot.Width*(145.0/360),plot.Top-30));
        Assert.Equal(-1,chart.HoverIndex);
        chart.SetHover(null);chart.UpdateLayout();
        Assert.DoesNotContain(Shapes(VisualTreeHelper.GetDrawing(chart)!),d=>ReferenceEquals(d.Brush,Application.Current.Resources["GlassPopup"]));
    }

    private static ThermalWindow Window(int seconds,double temperature)=>new(new("cpu","CPU","CPU","setup","map"),"session",
        DateTimeOffset.UnixEpoch.AddSeconds(seconds),DateTimeOffset.UnixEpoch.AddSeconds(seconds+50),seconds,50,6,
        new(6,temperature-2,temperature+2,temperature),new(6,50,50,50),new(6,100,100,100),new(0,null,null,null),new(0,null,null,null),false,"Insufficient sample coverage")
        {PollingIntervalSeconds=10};
    private static string Text(GlyphRunDrawing glyph)=>new(glyph.GlyphRun.Characters.ToArray());
    private static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
    private static IEnumerable<GlyphRunDrawing> Glyphs(Drawing drawing)=>drawing is GlyphRunDrawing glyph?[glyph]:drawing is DrawingGroup group?group.Children.SelectMany(Glyphs):[];
}
