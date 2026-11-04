using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

internal static class GraphStroke
{
    public static GraphLineStyle Style(UserSettings settings,string color)=>color.StartsWith("Graph/",StringComparison.Ordinal)
        ?settings.GraphStyles.GetValueOrDefault(color[6..],new()):new();
    public static string Pattern(UserSettings settings,string kind,bool dashed=false,bool dotted=false)=>settings.GraphStyles.GetValueOrDefault(kind=="network"?"download":kind,new()).Pattern is { } pattern&&pattern!="default"?pattern:dotted?"dotted":dashed?"dashed":"solid";
    public static DoubleCollection Dashes(GraphLineStyle style,bool dashed,bool dotted=false)=>style.Pattern switch
    {
        "solid"=>new(),"dashed"=>new([4,3]),"dotted"=>new([1,3]),_=>dotted?new([1,3]):dashed?new([4,3]):new()
    };
    public static void Draw(DrawingContext dc,Geometry geometry,UserSettings settings,string color,double width,bool dashed,bool dotted)
    {
        var style=Style(settings,color);
        DrawStyled(dc,geometry,Theme.Brush(color),style,width*style.Thickness,dashed,dotted);
    }
    /// <summary>The one pen/halo pass: <paramref name="width"/> is final (thickness already applied), the brush is the caller's.</summary>
    public static void DrawStyled(DrawingContext dc,Geometry geometry,Brush brush,GraphLineStyle style,double width,bool dashed=false,bool dotted=false)
    {
        var pen=new Pen(brush,width){DashStyle=new(Dashes(style,dashed,dotted),0),LineJoin=PenLineJoin.Round};
        pen.Freeze();
        if(style.Glow&&style.GlowStrength>0&&!SystemParameters.HighContrast)
        {
            double glowWidth=width+4*style.GlowStrength;
            var halo=new Pen(pen.Brush,glowWidth){DashStyle=new(pen.DashStyle.Dashes.Select(d=>d*width/glowWidth),0),LineJoin=PenLineJoin.Round};halo.Freeze();
            dc.PushOpacity(.16*style.GlowStrength);dc.DrawGeometry(null,halo,geometry);dc.Pop();
        }
        dc.DrawGeometry(null,pen,geometry);
    }
    /// <summary>A solid round-joined stroke with no glow or dashes (per-core CPU traces).</summary>
    public static void DrawPlain(DrawingContext dc,Geometry geometry,Brush brush,double width)
    {
        var pen=new Pen(brush,width){LineJoin=PenLineJoin.Round};pen.Freeze();
        dc.DrawGeometry(null,pen,geometry);
    }
}
