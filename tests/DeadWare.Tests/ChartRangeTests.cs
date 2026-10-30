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
        Assert.InRange(ranges.Count,1,200);Assert.Equal(21601,ranges.Sum(p=>p.Samples));
        Assert.Equal(99,ranges.Max(p=>p.Maximum));Assert.All(ranges,p=>Assert.InRange(p.Average!.Value,10,11));
    }
}
