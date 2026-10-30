using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

internal static class GraphStroke
{
    public static GraphLineStyle Style(UserSettings settings,string color)=>color.StartsWith("Graph/",StringComparison.Ordinal)
        ?settings.GraphStyles.GetValueOrDefault(color[6..],new()):new();
    public static string Pattern(UserSettings settings,string kind,bool dashed=false)=>settings.GraphStyles.GetValueOrDefault(kind=="network"?"download":kind,new()).Pattern is { } pattern&&pattern!="default"?pattern:dashed?"dashed":"solid";
    public static DoubleCollection Dashes(GraphLineStyle style,bool dashed,bool dotted=false)=>style.Pattern switch
    {
        "solid"=>new(),"dashed"=>new([4,3]),"dotted"=>new([1,3]),_=>dotted?new([1,3]):dashed?new([4,3]):new()
    };
    public static void Draw(DrawingContext dc,Geometry geometry,UserSettings settings,string color,double width,bool dashed,bool dotted)
    {
        var style=Style(settings,color);
        width*=style.Thickness;
        var pen=new Pen(Theme.Brush(color),width){DashStyle=new(Dashes(style,dashed,dotted),0),LineJoin=PenLineJoin.Round};
        pen.Freeze();
        if(style.Glow&&style.GlowStrength>0&&!SystemParameters.HighContrast)
        {
            double glowWidth=width+4*style.GlowStrength;
            var halo=new Pen(pen.Brush,glowWidth){DashStyle=new(pen.DashStyle.Dashes.Select(d=>d*width/glowWidth),0),LineJoin=PenLineJoin.Round};halo.Freeze();
            dc.PushOpacity(.16*style.GlowStrength);dc.DrawGeometry(null,halo,geometry);dc.Pop();
        }
        dc.DrawGeometry(null,pen,geometry);
    }
}
