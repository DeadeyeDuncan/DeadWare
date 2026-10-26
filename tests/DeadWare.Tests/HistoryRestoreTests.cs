using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class HistoryRestoreTests
{
    private static Snapshot S(double time,string? session,double? value,string metric="cpu")=>
        new(DateTimeOffset.UnixEpoch.AddSeconds(time),time,new Dictionary<string,double?>{{metric,value}}){SessionId=session};

    [Fact] public void RestoreKeepsOlderPointsAndLiveWinsTimestampCollisions()
    {
        var history=new SampleHistory();history.Add(S(10,null,80));history.Add(S(11,null,81));
        history.Restore([S(8,"old",60),S(10,"old",999)],11);
        Assert.Equal(new double?[]{60,80,81},history.Series("cpu",11,60).Select(p=>p.Value));
        history.Add(S(12,null,82));
        Assert.Equal(82,history.Read()[^1].Values["cpu"]);
    }

    [Fact] public void AQuickRestartStillBreaksTheLineAndReductionKeepsTheBreak()
    {
        var history=new SampleHistory();history.Add(S(10,null,80));
        history.Restore([S(9,"previous-session",60)],10);
        var series=history.Series("cpu",10,60);
        Assert.True(series[^1].BreakBefore);
        var reduced=SampleHistory.ReduceForChart(series,10,60,1);
        Assert.Equal(new double?[]{60,null,80},reduced.Select(p=>p.Value));
    }

    [Fact] public void LaterDeviceRestoreMergesMetricsWithoutReplacingPreviouslyRestoredValues()
    {
        var history=new SampleHistory();history.Add(S(10,null,90));
        history.Restore([S(8,"previous",10,"network")],10);
        history.Restore([S(8,"previous",50)],10);
        Assert.Equal(2,history.Read().Count);
        Assert.Equal(10,history.Read()[0].Values["network"]);
        Assert.Equal(50,history.Read()[0].Values["cpu"]);
        Assert.Equal(90,history.Read()[1].Values["cpu"]);
    }

    [Fact] public void RestoreRejectsFutureExpiredAndNonFiniteTimesAndCopiesMutableValues()
    {
        var history=new SampleHistory();history.Add(S(10,null,80));
        var values=new Dictionary<string,double?>{{"cpu",0},{"bad",double.NaN}};
        history.Restore([S(-21601,"old",1),S(12,"old",2),new(DateTimeOffset.UnixEpoch,double.NaN,values),new(DateTimeOffset.UnixEpoch,0,values){SessionId="old"}],10);
        values["cpu"]=999;
        Assert.Equal(2,history.Read().Count);
        Assert.Equal(0,history.Read()[0].Values["cpu"]);
        Assert.Null(history.Read()[0].Values["bad"]);
    }

    [Fact] public void RestoreCountIsBoundedAndAcceptsNewLiveReadingsAfterRestoreOnlyStartup()
    {
        var history=new SampleHistory();
        history.Restore(Enumerable.Range(0,30000).Select(i=>S(i/2d,"old",i)).ToArray(),15000);
        Assert.Equal(21601,history.Read().Count);
        history.Add(S(15000,null,100));
        Assert.Equal(21601,history.Read().Count);
        Assert.Equal(100,history.Read()[^1].Values["cpu"]);
    }
}
