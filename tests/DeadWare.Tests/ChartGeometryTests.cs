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
    [Fact] public void Narrow_plots_double_the_step_and_keep_two_ticks()
    {
        var now=new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero);
        Assert.True(TimeTicks.For(300,1000,now,120,false).Count<=3);
        Assert.Equal(2,TimeTicks.For(300,1000,now,180,true).Count);
    }
    [Fact] public void Compact_charts_label_the_full_width_like_the_dashboard()
    {
        // The widget's 60-second range at a typical width: every 15-second tick, not just the two nearest "Now".
        var now=new DateTimeOffset(2026,11,2,17,19,33,TimeSpan.Zero);
        var compact=TimeTicks.For(60,1000,now,560,true);
        Assert.Equal(TimeTicks.For(60,1000,now,560,false).Select(t=>t.Label),compact.Select(t=>t.Label));
        Assert.Equal(["17:18:45","17:19:00","17:19:15","17:19:30"],compact.Select(t=>t.Label));
    }
    [Fact] public void One_minute_range_uses_seconds()
        =>Assert.All(TimeTicks.For(60,1000,new DateTimeOffset(2026,11,2,14,32,40,TimeSpan.Zero),600,false),t=>Assert.Matches(@"^\d\d:\d\d:\d\d$",t.Label));
    [Fact] public void Hour_ticks_follow_local_time_across_a_dst_transition()
    {
        TimeZoneInfo zone;
        try { zone=TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); } catch(TimeZoneNotFoundException) { return; }
        // 03:15 CEST on 2026-03-29, just after clocks jumped from 02:00 to 03:00.
        var now=new DateTimeOffset(2026,3,29,3,15,0,TimeSpan.FromHours(2));
        var ticks=TimeTicks.For(21600,10000,now,900,false,zone);
        Assert.Equal(["21:00","22:00","23:00","00:00","01:00","03:00"],ticks.Select(t=>t.Label));
        Assert.Equal(10000-15*60,ticks[^1].MonotonicSeconds);
    }
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
