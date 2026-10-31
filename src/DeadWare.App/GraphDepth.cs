using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Paint-only depth behind flat data. No decoration changes a sample, scale or gap.</summary>
internal static class GraphDepth
{
    public static void Surface(DrawingContext dc,Rect plot)
    {
        if(!Theme.GlassEnabled||plot.IsEmpty||plot.Width<=0||plot.Height<=0)return;
        bool light=!Theme.IsDark;
        var surface=new LinearGradientBrush {StartPoint=new(0,0),EndPoint=new(0,1)};
        surface.GradientStops.Add(new(Color.FromArgb(light?(byte)17:(byte)48,0,0,0),0));
        surface.GradientStops.Add(new(Color.FromArgb(light?(byte)5:(byte)17,0,0,0),.48));
        surface.GradientStops.Add(new(Color.FromArgb(light?(byte)18:(byte)7,255,255,255),1));surface.Freeze();
        var rim=Theme.Brush("GlassBorder").Clone();rim.Opacity=.42;rim.Freeze();
        var pen=new Pen(rim,1);pen.Freeze();
        dc.DrawRoundedRectangle(surface,pen,plot,5,5);
        // The inset dark upper edge and faint lower reflection suggest a recessed tray.
        var edge=new LinearGradientBrush {StartPoint=new(0,0),EndPoint=new(0,1)};
        edge.GradientStops.Add(new(Color.FromArgb(light?(byte)25:(byte)46,0,0,0),0));
        edge.GradientStops.Add(new(Colors.Transparent,.12));edge.GradientStops.Add(new(Colors.Transparent,.88));
        edge.GradientStops.Add(new(Color.FromArgb(light?(byte)55:(byte)18,255,255,255),1));edge.Freeze();
        var inset=new Pen(edge,1);inset.Freeze();
        if(plot.Width>2&&plot.Height>2){var inner=plot;inner.Inflate(-1,-1);dc.DrawRoundedRectangle(null,inset,inner,4,4);}
    }

    public static void Area(DrawingContext dc,IReadOnlyList<IReadOnlyList<Point>> segments,Rect plot,string color,double strength=1)
    {
        if(!Theme.GlassEnabled||strength<=0||plot.IsEmpty||plot.Width<=0||plot.Height<=0)return;
        // Filled paths remain separate from foreground data geometry and range bands.
        var area=new PathGeometry {FillRule=FillRule.Nonzero};
        foreach(var points in segments)
        {
            if(points.Count<2)continue;
            var figure=new PathFigure {StartPoint=new(points[0].X,plot.Bottom),IsClosed=true,IsFilled=true};
            var edge=new PolyLineSegment();foreach(var p in points)edge.Points.Add(p);
            edge.Points.Add(new(points[^1].X,plot.Bottom));figure.Segments.Add(edge);area.Figures.Add(figure);
        }
        if(area.Figures.Count==0)return;area.Freeze();
        var source=((SolidColorBrush)Theme.Brush(color)).Color;
        byte Alpha(double opacity)=>(byte)Math.Clamp(Math.Round(255*opacity*Math.Clamp(strength,0,1)),0,255);
        var fill=new LinearGradientBrush {StartPoint=new(plot.Left,plot.Top),EndPoint=new(plot.Left,plot.Bottom),MappingMode=BrushMappingMode.Absolute};
        fill.GradientStops.Add(new(Color.FromArgb(Alpha(Theme.IsDark ? .20 : .15),source.R,source.G,source.B),0));
        fill.GradientStops.Add(new(Color.FromArgb(Alpha(Theme.IsDark ? .075 : .055),source.R,source.G,source.B),.65));
        fill.GradientStops.Add(new(Color.FromArgb(Alpha(.012),source.R,source.G,source.B),1));fill.Freeze();
        dc.DrawGeometry(fill,null,area);
    }

    public static void TraceShadow(DrawingContext dc,Geometry geometry,UserSettings settings,string color,double width,bool dashed=false,bool dotted=false)
    {
        if(!Theme.GlassEnabled||geometry.IsEmpty())return;
        var style=GraphStroke.Style(settings,color);width*=style.Thickness;
        var pen=new Pen(ShadowBrush(),width+.65) {DashStyle=new(GraphStroke.Dashes(style,dashed,dotted).Select(d=>d*width/(width+.65)),0),LineJoin=PenLineJoin.Round};pen.Freeze();
        DrawShadow(dc,geometry.GetFlattenedPathGeometry(),pen);
    }

    public static void LineShadow(DrawingContext dc,Point from,Point to,Pen source)
    {
        if(!Theme.GlassEnabled)return;
        var geometry=new PathGeometry();var figure=new PathFigure {StartPoint=from,IsFilled=false};
        figure.Segments.Add(new LineSegment(to,true));geometry.Figures.Add(figure);geometry.Freeze();
        var pen=source.Clone();pen.Brush=ShadowBrush();pen.Freeze();DrawShadow(dc,geometry,pen);
    }

    private static SolidColorBrush ShadowBrush()
    {var brush=new SolidColorBrush(Color.FromArgb(Theme.IsDark?(byte)88:(byte)43,0,0,0));brush.Freeze();return brush;}
    private static void DrawShadow(DrawingContext dc,PathGeometry geometry,Pen pen)
    {
        if(!geometry.IsFrozen)geometry.Freeze();
        var offset=new TranslateTransform(.35,1.35);offset.Freeze();
        dc.PushTransform(offset);dc.DrawGeometry(null,pen,geometry);dc.Pop();
    }
    public static void PointHalo(DrawingContext dc,Point point,string color,double radius=2.5)
    {
        if(!Theme.GlassEnabled)return;
        var source=((SolidColorBrush)Theme.Brush(color)).Color;
        var halo=new RadialGradientBrush(Color.FromArgb(Theme.IsDark?(byte)60:(byte)38,source.R,source.G,source.B),Colors.Transparent);halo.Freeze();
        dc.DrawEllipse(halo,null,point,radius+3,radius+3);
    }
}
