using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class ChartGeometryTests
{
    [Fact] public void Monotone_spline_never_overshoots_its_inputs()
    {
        ChartPoint[] pts=[new(0,10),new(10,90),new(20,91),new(30,20),new(40,20)];
        var segments=MonotoneSpline.Build(pts);
        Assert.Equal(4,segments.Count);
        foreach(var s in segments){Assert.InRange(s.Control1.Y,10,91);Assert.InRange(s.Control2.Y,10,91);}
        Assert.Equal(pts[4],segments[3].End);
        // Flat run keeps flat control points.
        Assert.Equal(20,segments[3].Control1.Y);Assert.Equal(20,segments[3].Control2.Y);
    }
    [Fact] public void Spline_with_fewer_than_two_points_is_empty()
        =>Assert.Empty(MonotoneSpline.Build([new ChartPoint(1,1)]));
    [Fact] public void Five_minute_ticks_fall_on_minute_boundaries_with_hh_mm_labels()
    {
        var now=new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero);
        var ticks=TimeTicks.For(300,1000,now,330,false);
        Assert.Equal(["14:28","14:29","14:30","14:31","14:32"],ticks.Select(t=>t.Label));
        Assert.Equal(1000-40,ticks[^1].MonotonicSeconds);
    }
    [Fact] public void Narrow_plots_double_the_step_and_compact_keeps_two()
    {
        var now=new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero);
        Assert.True(TimeTicks.For(300,1000,now,120,false).Count<=3);
        Assert.Equal(2,TimeTicks.For(300,1000,now,330,true).Count);
    }
    [Fact] public void One_minute_range_uses_seconds()
        =>Assert.All(TimeTicks.For(60,1000,new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero),600,false),t=>Assert.Matches(@"^\d\d:\d\d:\d\d$",t.Label));
    [Fact] public void Nearest_sample_skips_gaps_and_respects_tolerance()
    {
        SamplePoint[] pts=[new(DateTimeOffset.UnixEpoch,10,5),new(DateTimeOffset.UnixEpoch,11,null),new(DateTimeOffset.UnixEpoch,12,7)];
        Assert.Equal(0,NearestSample.Find(pts,10.2,1));
        Assert.Equal(-1,NearestSample.Find(pts,11,0.4));
        Assert.Equal(2,NearestSample.Find(pts,12.3,1));
        Assert.Equal(-1,NearestSample.Find(pts,30,1));
        Assert.Equal(-1,NearestSample.Find([],1,1));
    }
}
