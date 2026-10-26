using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HistoryChartTests
{
    private static SamplePoint P(double seconds,double? value)=>new(DateTimeOffset.UnixEpoch.AddSeconds(seconds),seconds,value);

    [Fact] public void ReductionKeepsPeakTroughAndEndpointsInTheirOriginalTimeOrder()
    {
        SamplePoint[] points=[P(0,20),P(1,90),P(2,0),P(3,30),P(4,40)];
        var reduced=SampleHistory.ReduceForChart(points,5,60,1);
        Assert.Equal(new[]{0d,1,2,4},reduced.Select(p=>p.MonotonicSeconds));
        Assert.Equal(new double?[]{20,90,0,40},reduced.Select(p=>p.Value));
    }

    [Fact] public void SixHoursOfContinuousReadingsReduceToPixelBudgetWithoutArtificialGaps()
    {
        var points=Enumerable.Range(0,21601).Select(i=>P(i,i==11000?99:10)).ToArray();
        var reduced=SampleHistory.ReduceForChart(points,21600,21600,200);
        Assert.InRange(reduced.Count,2,800);
        Assert.Equal(0,reduced[0].MonotonicSeconds);
        Assert.Equal(21600,reduced[^1].MonotonicSeconds);
        Assert.Contains(reduced,p=>p.MonotonicSeconds==11000&&p.Value==99);
        Assert.All(reduced,p=>Assert.NotNull(p.Value));
        Assert.True(reduced.Select(p=>p.MonotonicSeconds).SequenceEqual(reduced.Select(p=>p.MonotonicSeconds).Order()));
    }

    [Fact] public void MissingReadingsAndElapsedGapsSplitLinesEvenWithinOnePixel()
    {
        SamplePoint[] points=[P(1,10),P(2,null),P(2.5,null),P(3,20),P(20,30)];
        var reduced=SampleHistory.ReduceForChart(points,30,60,1);
        Assert.Equal(new double?[]{10,null,20,null,30},reduced.Select(p=>p.Value));
    }

    [Fact] public void NonFiniteReadingsBecomeGapsAndZeroRemainsVisible()
    {
        SamplePoint[] points=[P(1,10),P(2,double.NaN),P(3,double.PositiveInfinity),P(4,0)];
        var reduced=SampleHistory.ReduceForChart(points,5,60,1);
        Assert.Equal(new double?[]{10,null,0},reduced.Select(p=>p.Value));
    }

    [Fact] public void StatisticsUseEveryReadingAndExcludeOutOfRangeUnknownAndNonFiniteValues()
    {
        var history=new SampleHistory();
        foreach(var (time,cpu,gpu) in new[]{(0,100d,100d),(1,0d,10d),(2,3d,double.NaN),(3,9d,30d),(4,80d,80d)})
            history.Add(new(DateTimeOffset.UnixEpoch.AddSeconds(time),time,new Dictionary<string,double?>{{"cpu",cpu},{"gpu",gpu}}));
        var summaries=history.Summaries(new[]{"cpu","gpu","missing","cpu"},3,2);
        Assert.Equal(new Summary(0,9,4),summaries["cpu"]);
        Assert.Equal(new Summary(10,30,20),summaries["gpu"]);
        Assert.Equal(new Summary(null,null,null),summaries["missing"]);
        Assert.Equal(new Summary(0,9,4),history.Summarize("cpu",3,2));
    }
}
