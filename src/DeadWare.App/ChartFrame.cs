using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Shared chart chrome: a crisp hairline grid with labels inside the plot, wall-clock ticks below it,
/// hover chips and monotone spline paths. Pure drawing; no sample, scale or gap is changed here.</summary>
internal static class ChartFrame
{
    public const double FontSize=11;

    public static FormattedText Text(string text,Typeface typeface,double pixelsPerDip,Brush brush,double size=FontSize)
        =>new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,typeface,size,brush,pixelsPerDip);

    public static void Grid(DrawingContext dc,Rect plot,IReadOnlyList<(double Y,string Label)> lines,IReadOnlyList<(double X,string Label)> ticks,Typeface typeface,double pixelsPerDip,bool compact)
        =>Grid(dc,plot,lines,ticks,typeface,pixelsPerDip,compact,null);

    /// <summary>Value lines are 1 px <c>Hairline</c> strokes snapped with a <see cref="GuidelineSet"/>. Their labels sit inside the plot,
    /// right-aligned 6 px from the edge and 2 px above each line (below the top line). <paramref name="leftLabels"/> adds a second
    /// scale on the left. Tick labels go 4 px under the plot with "Now" right-aligned; ticks that would collide are skipped.</summary>
    public static void Grid(DrawingContext dc,Rect plot,IReadOnlyList<(double Y,string Label)> lines,IReadOnlyList<(double X,string Label)> ticks,Typeface typeface,double pixelsPerDip,bool compact,IReadOnlyList<(double Y,string Label)>? leftLabels,string? nowLabel="Now")
    {
        if(plot.IsEmpty||plot.Width<=0||plot.Height<=0)return;
        var muted=Theme.Brush("Muted");
        var pen=new Pen(Theme.Brush("Hairline"),1);pen.Freeze();
        var guides=new GuidelineSet();
        foreach(var line in lines)guides.GuidelinesY.Add(line.Y+.5);
        foreach(var tick in ticks)guides.GuidelinesX.Add(tick.X+.5);
        guides.Freeze();
        dc.PushGuidelineSet(guides);
        foreach(var line in lines)dc.DrawLine(pen,new(plot.Left,line.Y),new(plot.Right,line.Y));
        dc.Pop();
        double size=compact?10:FontSize;
        double LabelY(double y,FormattedText text){double top=y-text.Height-2;return top<plot.Top?y+2:top;}
        // Short plots keep every line but label only the outer two, so inside labels never stack on each other.
        double labelHeight=Text("0",typeface,pixelsPerDip,muted,size).Height;
        bool sparse=plot.Height<4*labelHeight+8;
        bool Labelled(int index,int count)=>!sparse||index==0||index==count-1;
        for(int i=0;i<lines.Count;i++)
        {
            var line=lines[i];
            if(string.IsNullOrEmpty(line.Label)||!Labelled(i,lines.Count))continue;
            var text=Text(line.Label,typeface,pixelsPerDip,muted,size);
            dc.DrawText(text,new(plot.Right-6-text.Width,LabelY(line.Y,text)));
        }
        if(leftLabels is not null)
            for(int i=0;i<leftLabels.Count;i++)
            {
                var line=leftLabels[i];
                if(string.IsNullOrEmpty(line.Label)||!Labelled(i,leftLabels.Count))continue;
                var text=Text(line.Label,typeface,pixelsPerDip,muted,size);
                dc.DrawText(text,new(plot.Left+6,LabelY(line.Y,text)));
            }
        if(nowLabel is null)return;
        var now=Text(nowLabel,typeface,pixelsPerDip,muted,size);
        double labelY=plot.Bottom+4;
        dc.DrawText(now,new(plot.Right-now.Width,labelY));
        double limit=plot.Right-now.Width-6,lastRight=double.NegativeInfinity;
        foreach(var tick in ticks)
        {
            var text=Text(tick.Label,typeface,pixelsPerDip,muted,size);
            double x=tick.X-text.Width/2;
            if(x<plot.Left-1||x+text.Width>limit||x<lastRight+6)continue;
            dc.DrawText(text,new(x,labelY));lastRight=x+text.Width;
        }
    }

    /// <summary>Draws the hover chip 12 px beside <paramref name="anchor"/>, flipped to the left when it would cross the plot's right edge,
    /// clamped vertically inside the plot. Returns the chip rectangle.</summary>
    public static Rect Chip(DrawingContext dc,Point anchor,Rect plot,IReadOnlyList<(string Text,Brush Color)> lines,Typeface typeface,double pixelsPerDip,bool compact)
    {
        const double padding=8,gap=12,radius=8,spacing=2;
        var texts=lines.Where(line=>!string.IsNullOrEmpty(line.Text)).Select(line=>Text(line.Text,typeface,pixelsPerDip,line.Color,compact?10:FontSize)).ToArray();
        if(texts.Length==0)return Rect.Empty;
        double width=texts.Max(t=>t.Width)+padding*2,height=texts.Sum(t=>t.Height)+padding*2+(texts.Length-1)*spacing;
        double x=anchor.X+gap;if(x+width>plot.Right)x=anchor.X-gap-width;
        if(x<plot.Left)x=Math.Max(plot.Left,Math.Min(anchor.X+gap,plot.Right-width));
        double y=Math.Clamp(anchor.Y-height/2,plot.Top,Math.Max(plot.Top,plot.Bottom-height));
        var rect=new Rect(x,y,width,height);
        var border=new Pen(Theme.Brush("Hairline"),1);border.Freeze();
        dc.DrawRoundedRectangle(Theme.Brush("GlassPopup"),border,rect,radius,radius);
        double textY=y+padding;
        foreach(var text in texts){dc.DrawText(text,new(x+padding,textY));textY+=text.Height+spacing;}
        return rect;
    }

    /// <summary>Nearest hover target by X within each target's tolerance, or -1.</summary>
    public static int Nearest(IReadOnlyList<(double X,double Tolerance)> targets,Point? position,Rect plot)
    {
        if(position is not { } p||!plot.Contains(p))return -1;
        int best=-1;double bestDistance=double.PositiveInfinity;
        for(int i=0;i<targets.Count;i++)
        {
            double distance=Math.Abs(targets[i].X-p.X);
            if(distance<=targets[i].Tolerance&&distance<bestDistance){best=i;bestDistance=distance;}
        }
        return best;
    }

    /// <summary>One figure per run, each a chain of monotone cubic Béziers through the run's points. Runs under two points are skipped.</summary>
    public static StreamGeometry Spline(IReadOnlyList<IReadOnlyList<Point>> runs)
    {
        var geometry=new StreamGeometry();
        using(var context=geometry.Open())
            foreach(var run in runs)
            {
                if(run.Count<2)continue;
                var points=new ChartPoint[run.Count];
                for(int i=0;i<run.Count;i++)points[i]=new(run[i].X,run[i].Y);
                context.BeginFigure(run[0],false,false);
                foreach(var segment in MonotoneSpline.Build(points))
                    context.BezierTo(new(segment.Control1.X,segment.Control1.Y),new(segment.Control2.X,segment.Control2.Y),new(segment.End.X,segment.End.Y),true,false);
            }
        geometry.Freeze();
        return geometry;
    }
}
