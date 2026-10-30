using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public sealed class ChartRangeTests
{
    private static SampleHistory History(params double?[] values)
    {
        var history=new SampleHistory();
        for(int i=0;i<values.Length;i++)history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(i),i,new Dictionary<string,double?>{{"cpu",values[i]}}));
        return history;
    }
    [Fact] public void Average_trend_retains_exact_extrema_and_all_sample_statistics()
    {
        var history=History(20,90,0,30,40);
        var point=Assert.Single(history.ChartRanges("cpu",5,60,1));
        Assert.Equal(36,point.Average);Assert.Equal(0,point.Minimum);Assert.Equal(90,point.Maximum);Assert.Equal(5,point.Samples);
        Assert.Equal(new Summary(0,90,36),history.Summarize("cpu",5,60));
    }
    [Fact] public void Missing_values_split_averages_even_inside_one_pixel()
    {
        var points=History(10,null,30,50).ChartRanges("cpu",4,60,1);
        Assert.Equal(new double?[]{10,null,40},points.Select(p=>p.Average));
        Assert.Equal(30,points[^1].Minimum);Assert.Equal(50,points[^1].Maximum);
    }
    [Fact] public void Session_and_elapsed_gaps_are_not_smoothed_over()
    {
        var history=new SampleHistory();
        foreach(var (time,session) in new[]{(0,"one"),(1,"two"),(20,"two")})
            history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(time),time,new Dictionary<string,double?>{{"cpu",time}}){SessionId=session});
        Assert.Equal(new double?[]{0,null,1,null,20},history.ChartRanges("cpu",20,60,1).Select(p=>p.Average));
    }
    [Fact] public void Snapshot_cache_invalidates_for_new_and_restored_readings()
    {
        var history=History(10);Assert.Equal(10,Assert.Single(history.ChartRanges("cpu",1,60,1)).Average);
        history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(1),1,new Dictionary<string,double?>{{"cpu",30}}));
        Assert.Equal(20,Assert.Single(history.ChartRanges("cpu",1,60,1)).Average);
        history.Restore([new(DateTimeOffset.UnixEpoch.AddSeconds(-1),-1,new Dictionary<string,double?>{{"cpu",100}}){SessionId="saved"}],1);
        Assert.Contains(history.ChartRanges("cpu",1,60,1),p=>p.Average==100);
    }
    [Fact] public void Six_hour_trend_has_pixel_bounded_points_without_losing_spikes()
    {
        var history=History(Enumerable.Range(0,21601).Select(i=>(double?)(i==11000?99:10)).ToArray());
        var ranges=history.ChartRanges("cpu",21600,21600,200);
        // An inclusive window can intersect a partial bucket at each edge.
        Assert.InRange(ranges.Count,1,201);Assert.Equal(21601,ranges.Sum(p=>p.Samples));
        Assert.Equal(99,ranges.Max(p=>p.Maximum));Assert.All(ranges,p=>Assert.InRange(p.Average!.Value,10,11));
    }
    [Theory][InlineData(0)][InlineData(-130)]
    public void Completed_buckets_keep_their_values_and_timestamps_as_the_window_moves(int offset)
    {
        var history=new SampleHistory();
        var initial=Enumerable.Range(0,120).Select(i=>new Snapshot(DateTimeOffset.UnixEpoch.AddSeconds(offset+i),offset+i,
            new Dictionary<string,double?>{{"cpu",i==41?null:i%7*13}}){SessionId="fixture"}).ToArray();
        if(offset<0)history.Restore(initial,0);else foreach(var sample in initial)history.Add(sample);
        var before=history.ChartRanges("cpu",offset+119,120,12);
        history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(offset+120),offset+120,new Dictionary<string,double?>{{"cpu",99}}){SessionId="fixture"});
        var after=history.ChartRanges("cpu",offset+120,120,12);
        var interior=before.Where(point=>point.StartSeconds>=offset+20&&point.EndSeconds<=offset+99).ToArray();
        Assert.NotEmpty(interior);
        Assert.All(interior,point=>Assert.Contains(point,after));
        Assert.Contains(interior,point=>point.Average is null);
        Assert.Equal(history.Series("cpu",offset+120,120).Count(point=>point.Value.HasValue),after.Sum(point=>point.Samples));
        var summary=history.Summarize("cpu",offset+120,120);
        Assert.Equal(summary.Minimum,after.Min(point=>point.Minimum));Assert.Equal(summary.Maximum,after.Max(point=>point.Maximum));
        Assert.Equal(summary.Average!.Value,after.Sum(point=>(point.Average??0)*point.Samples)/after.Sum(point=>point.Samples),8);
    }
    [Fact] public void Restored_negative_times_use_the_same_fixed_boundaries_as_live_times()
    {
        var history=new SampleHistory();
        history.Restore([new(DateTimeOffset.UnixEpoch.AddSeconds(-2),-2,new Dictionary<string,double?>{{"cpu",10}}){SessionId="fixture"},
            new(DateTimeOffset.UnixEpoch.AddSeconds(-1),-1,new Dictionary<string,double?>{{"cpu",20}}){SessionId="fixture"}],0);
        history.Add(new(DateTimeOffset.UnixEpoch,0,new Dictionary<string,double?>{{"cpu",30}}){SessionId="fixture"});
        history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(1),1,new Dictionary<string,double?>{{"cpu",40}}){SessionId="fixture"});
        var ranges=history.ChartRanges("cpu",1,60,6);
        Assert.Equal(new double?[]{15,35},ranges.Select(point=>point.Average));
        Assert.Equal(new double[]{-2,0},ranges.Select(point=>point.StartSeconds));
        Assert.Equal(new double[]{-1,1},ranges.Select(point=>point.EndSeconds));
    }
}
