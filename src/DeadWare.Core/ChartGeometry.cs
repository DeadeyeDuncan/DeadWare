namespace DeadWare.Core;

public readonly record struct ChartPoint(double X,double Y);
public readonly record struct CubicSegment(ChartPoint Control1,ChartPoint Control2,ChartPoint End);
public readonly record struct TimeTick(double MonotonicSeconds,string Label);

/// <summary>Fritsch–Carlson monotone cubic interpolation: curves never overshoot the samples they pass through.</summary>
public static class MonotoneSpline
{
    /// <summary>One gap-free run with strictly increasing X. Fewer than two points yields no segments.</summary>
    public static IReadOnlyList<CubicSegment> Build(IReadOnlyList<ChartPoint> points)
    {
        int n=points.Count;
        if(n<2)return [];
        var dx=new double[n-1];var d=new double[n-1];
        for(int i=0;i<n-1;i++){dx[i]=points[i+1].X-points[i].X;d[i]=dx[i]==0?0:(points[i+1].Y-points[i].Y)/dx[i];}
        var m=new double[n];
        m[0]=d[0];m[n-1]=d[n-2];
        for(int i=1;i<n-1;i++)m[i]=d[i-1]*d[i]<=0?0:(d[i-1]+d[i])/2;
        for(int i=0;i<n-1;i++)
        {
            if(d[i]==0){m[i]=0;m[i+1]=0;continue;}
            double a=m[i]/d[i],b=m[i+1]/d[i],h=a*a+b*b;
            if(h>9){double t=3/Math.Sqrt(h);m[i]=t*a*d[i];m[i+1]=t*b*d[i];}
        }
        var segments=new CubicSegment[n-1];
        for(int i=0;i<n-1;i++)
        {
            double third=dx[i]/3;
            segments[i]=new(new(points[i].X+third,points[i].Y+m[i]*third),new(points[i+1].X-third,points[i+1].Y-m[i+1]*third),points[i+1]);
        }
        return segments;
    }
}

/// <summary>Wall-clock tick positions for a live chart whose X axis is monotonic seconds.</summary>
public static class TimeTicks
{
    private const double MinimumSpacing=48;

    /// <summary>Ticks sit on wall-clock multiples of the step, ascending, labelled HH:mm (HH:mm:ss below a minute).
    /// The step doubles until labels are at least 48 px apart; compact charts and plots under 200 px keep the two nearest "Now".</summary>
    public static IReadOnlyList<TimeTick> For(int historySeconds,double nowMonotonic,DateTimeOffset nowWallClock,double plotWidth,bool compact)
    {
        if(historySeconds<=0||plotWidth<=0||!double.IsFinite(plotWidth))return [];
        int step=historySeconds switch {<=60=>15,<=300=>60,<=900=>300,<=3600=>900,_=>3600};
        while(step/(double)historySeconds*plotWidth<MinimumSpacing&&step<historySeconds)step*=2;
        if(step>=historySeconds)return [];
        string format=step<60?"HH:mm:ss":"HH:mm";
        // Align to local wall-clock multiples of the step (so hour ticks fall on local hours).
        long local=nowWallClock.ToUnixTimeSeconds()+(long)nowWallClock.Offset.TotalSeconds;
        long first=local-((local%step)+step)%step;
        var ticks=new List<TimeTick>();
        for(long t=first;t>=local-historySeconds;t-=step)
        {
            double age=local-t;
            var wall=nowWallClock.AddSeconds(-age);
            ticks.Add(new(nowMonotonic-age,wall.ToString(format,System.Globalization.CultureInfo.InvariantCulture)));
        }
        ticks.Reverse();
        int keep=compact||plotWidth<200?2:int.MaxValue;
        return ticks.Count>keep?ticks.Skip(ticks.Count-keep).ToArray():ticks;
    }
}

/// <summary>Finds the sample under a hover position without ever reporting a reading across a gap.</summary>
public static class NearestSample
{
    /// <summary>Index of the point nearest to <paramref name="monotonicSeconds"/>, or -1 when the series is empty,
    /// the nearest point is a gap marker (null value) or it lies farther than <paramref name="toleranceSeconds"/>.</summary>
    public static int Find(IReadOnlyList<SamplePoint> points,double monotonicSeconds,double toleranceSeconds)
    {
        int n=points.Count;
        if(n==0)return -1;
        int low=0,high=n-1;
        while(low<high)
        {
            int mid=(low+high)/2;
            if(points[mid].MonotonicSeconds<monotonicSeconds)low=mid+1;else high=mid;
        }
        int nearest=low;
        if(low>0&&Math.Abs(points[low-1].MonotonicSeconds-monotonicSeconds)<=Math.Abs(points[low].MonotonicSeconds-monotonicSeconds))nearest=low-1;
        if(points[nearest].Value is null)return -1;
        return Math.Abs(points[nearest].MonotonicSeconds-monotonicSeconds)>toleranceSeconds?-1:nearest;
    }
}
