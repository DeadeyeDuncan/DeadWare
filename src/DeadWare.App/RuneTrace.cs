using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DeadWare.App;

/// <summary>Bounded decorative glyphs; the measured trace remains unchanged underneath.</summary>
internal static class RuneTrace
{
    internal const int MaximumRunes=64;
    public static bool Enabled=>Theme.ElderScrolls&&!SystemParameters.HighContrast;

    internal static IReadOnlyList<Point> Placements(IReadOnlyList<IReadOnlyList<Point>> segments,Rect plot)
    {
        var result=new List<Point>();
        if(plot.IsEmpty||plot.Width<26||plot.Height<14||!double.IsFinite(plot.Width)||!double.IsFinite(plot.Height))return result;
        const double inset=6;
        double spacing=Math.Max(52,plot.Width/MaximumRunes),anchor=plot.Left+spacing/2,lastX=double.NegativeInfinity;
        foreach(var segment in segments)
        {
            for(int i=1;i<segment.Count&&result.Count<MaximumRunes;i++)
            {
                var from=segment[i-1];var to=segment[i];
                if(!double.IsFinite(from.X)||!double.IsFinite(from.Y)||!double.IsFinite(to.X)||!double.IsFinite(to.Y)||to.X<=from.X)continue;
                // Horizontal spacing bounds the work even for a dense, spiky trace.
                // Each segment is visited independently, so missing readings stay empty.
                double first=Math.Max(from.X,plot.Left+inset);
                double x=anchor+Math.Max(0,Math.Ceiling((first-anchor)/spacing))*spacing;
                if(x<=lastX)x=lastX+spacing;
                double end=Math.Min(to.X,plot.Right-inset);
                for(;x<=end&&result.Count<MaximumRunes;x+=spacing)
                {
                    double y=from.Y+(to.Y-from.Y)*(x-from.X)/(to.X-from.X);
                    if(y<plot.Top+inset||y>plot.Bottom-inset)continue;
                    result.Add(new(x,y));lastX=x;
                }
            }
            if(result.Count==MaximumRunes)break;
        }
        return result;
    }

    public static void Draw(DrawingContext dc,IReadOnlyList<IReadOnlyList<Point>> segments,Rect plot,string color,double pixelsPerDip)
    {
        if(!Enabled)return;
        var positions=Placements(segments,plot);if(positions.Count==0)return;
        var typeface=new Typeface(Theme.RuneFont,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
        var glyphs=new[]{"ᚠ","ᚱ","ᛃ","ᛟ"}.Select(value=>new FormattedText(value,CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,typeface,11,Theme.Brush(color),pixelsPerDip)).ToArray();
        dc.PushClip(new RectangleGeometry(plot));dc.PushOpacity(.74);
        for(int i=0;i<positions.Count;i++)
        {
            var text=glyphs[i%glyphs.Length];var position=positions[i];
            dc.DrawText(text,new(position.X-text.Width/2,position.Y-text.Height/2));
        }
        dc.Pop();dc.Pop();
    }
}
