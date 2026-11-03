using System.Windows;
using System.Windows.Media;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>ChartFrame chip placement and spline conversion, on the shared STA fixture (resources are needed for the chip brushes).</summary>
internal static class ChartFrameUiChecks
{
    internal static void Run()
    {
        var typeface=Theme.ChartTypeface;
        var plot=new Rect(10,10,300,100);
        var group=new DrawingGroup();Rect chip;
        using(var dc=group.Open())chip=ChartFrame.Chip(dc,new Point(plot.Right-10,plot.Top+40),plot,[("12:00:00",Theme.Brush("Muted")),("34.0 %",Theme.Brush("Graph/cpu"))],typeface,1,false);
        Assert.True(chip.Right<=plot.Right,$"Chip right {chip.Right} crosses plot right {plot.Right}.");
        Assert.True(chip.Width>20&&chip.Height>20);
        Assert.Contains(Shapes(group),shape=>ReferenceEquals(shape.Brush,Application.Current.Resources["GlassPopup"]));
        var left=new DrawingGroup();Rect leftChip;
        using(var dc=left.Open())leftChip=ChartFrame.Chip(dc,new Point(plot.Left+10,plot.Top+40),plot,[("34.0 %",Theme.Brush("Graph/cpu"))],typeface,1,true);
        Assert.True(leftChip.Left>=plot.Left+10+12-.01,"Chip should sit 12 px right of its anchor when there is room.");
        Point[] a=[new(0,50),new(10,40),new(20,45),new(30,10)];Point[] b=[new(50,20),new(60,25)];
        var spline=ChartFrame.Spline([a,b]);
        Assert.Equal(2,PathGeometry.CreateFromGeometry(spline).Figures.Count);
        // Diagnostic for the ledger: does flattening keep open stroked figures at all?
        var poly=new StreamGeometry();using(var c=poly.Open()){c.BeginFigure(new(0,0),false,false);c.LineTo(new(10,10),true,false);}poly.Freeze();
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"deadware-flatten-diag.txt"),$"spline flattened {spline.GetFlattenedPathGeometry().Figures.Count}; polyline flattened {poly.GetFlattenedPathGeometry().Figures.Count}; polyline converted {PathGeometry.CreateFromGeometry(poly).Figures.Count}");
        var figures=PathGeometry.CreateFromGeometry(spline).Figures;
        Assert.All(figures,figure=>Assert.All(figure.Segments,segment=>Assert.True(segment is BezierSegment or PolyBezierSegment,segment.GetType().Name)));
        Assert.Equal(new Point(0,50),figures[0].StartPoint);
        Assert.Empty(PathGeometry.CreateFromGeometry(ChartFrame.Spline([])).Figures);
        Assert.Empty(PathGeometry.CreateFromGeometry(ChartFrame.Spline([[new Point(1,1)]])).Figures);
    }

    private static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)=>drawing is GeometryDrawing shape?[shape]:drawing is DrawingGroup group?group.Children.SelectMany(Shapes):[];
}
