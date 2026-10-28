using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;
public class MonitoringTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    static Snapshot S(double seconds, double? value) => new(Epoch.AddSeconds(seconds), seconds, new Dictionary<string,double?> { ["cpu"] = value });
    [Fact] public void HistoryBoundsByTimeAndCountAndUsesActualTimestamps()
    {
        var h = new SampleHistory();
        for (int i=0;i<=21605;i++) h.Add(S(i, i));
        Assert.Equal(21601, h.Read().Count);
        Assert.Equal(Epoch.AddSeconds(5), h.Read()[0].Timestamp);
        Assert.Equal(21601, h.Series("cpu",21605,21600).Count);
        Assert.Equal(10801, h.Series("cpu",21605,10800).Count);
        Assert.Equal(3601, h.Series("cpu",21605,3600).Count);
        h.Add(S(43206, 2));
        Assert.Single(h.Read());
    }
    [Fact] public void DenseHistoryRemainsBoundedAndCopiesPublishedValues()
    {
        var h=new SampleHistory();
        var values=new Dictionary<string,double?> { ["cpu"]=12 };
        for(int i=0;i<44000;i++)h.Add(new Snapshot(Epoch.AddMilliseconds(i),i/1000d,values));
        values["cpu"]=99;
        var points=h.Series("cpu",44,60);
        Assert.Equal(43201,points.Count);
        Assert.All(points,p=>Assert.Equal(12,p.Value));
        Assert.Equal(0.799,points[0].MonotonicSeconds,5);
    }
    [Fact] public void UnknownAndNonFiniteAreGapsWhileZeroIsValid()
    {
        var h = new SampleHistory();
        h.Add(S(1,0)); h.Add(S(2,null)); h.Add(S(3,double.NaN)); h.Add(S(4,double.PositiveInfinity)); h.Add(S(5,10));
        var points=h.Series("cpu",5,60);
        Assert.Equal(new double?[]{0,null,null,null,10}, points.Select(p=>p.Value));
        var stats=SampleHistory.Statistics(points);
        Assert.Equal(0,stats.Minimum); Assert.Equal(10,stats.Maximum); Assert.Equal(5,stats.Average);
    }
    [Fact] public void HistoryDoesNotAcceptBackwardsMonotonicSamples()
    {
        var h=new SampleHistory();h.Add(S(10,1));h.Add(S(9,2)); Assert.Single(h.Read());
    }
    [Fact] public void RatesUseElapsedTimeAndRecoverAfterCounterReset()
    {
        var r = new CounterRate();
        Assert.Null(r.Update(100,10)); Assert.Equal(40,r.Update(180,12));
        Assert.Null(r.Update(2,13)); Assert.Equal(10,r.Update(22,15));
    }
    [Theory][InlineData(10)][InlineData(9)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public void InvalidElapsedTimeProducesUnknown(double time)
    { var r=new CounterRate();r.Update(100,10);Assert.Null(r.Update(200,time)); }
    [Fact] public void GpuSelectionUsesIdentityNotDiscoveryOrderAndMissingDeviceDoesNotSelectAnother()
    {
        var devices=new[]{new Device("gpu-b","Same model"),new Device("gpu-a","Same model")};
        Assert.Equal("gpu-a",DeviceSelection.Resolve("gpu-a",devices));
        Assert.Equal("gpu-b",DeviceSelection.Resolve(null,devices));
        Assert.Null(DeviceSelection.Resolve("removed",devices));
    }
    [Fact] public void PidReuseDoesNotInheritBytesAndDelayedOldEventsAreDiscarded()
    {
        var l=new ProcessLedger();l.Start(42,100,"first");l.Add(42,100,1000,200);l.Take(1);
        l.Add(42,100,100,20);var first=Assert.Single(l.Take(2));Assert.Equal(100,first.DownloadBytesPerSecond);
        l.Start(42,200,"second");l.Add(42,100,9000,9000);l.Add(42,200,30,10);
        var second=Assert.Single(l.Take(3));Assert.Equal("second",second.Name);Assert.Equal(30,second.ReceivedBytes);Assert.Equal(30,second.DownloadBytesPerSecond);
    }
    [Fact] public void EndedProcessesExpireAndCannotAcquireLateTraffic()
    {
        var l=new ProcessLedger();l.Start(1,100,"a");l.Take(1);l.Stop(1,100);l.Add(1,100,50,50);
        Assert.Empty(l.Take(2));
    }
    [Theory][InlineData("{bad")][InlineData("null")][InlineData("{\"HistorySeconds\":2,\"Windows\":null}")]
    public void CorruptOrUnsupportedSettingsRecoverToSafeDefaults(string json)
    { var s=SettingsCodec.Read(json); Assert.Equal(60,s.Settings.HistorySeconds);Assert.NotNull(s.Settings.Windows); }
    [Fact] public void SettingsRoundtripPreservesSelectedGpuAndBounds()
    {
        var s=new UserSettings { HistorySeconds=300,SelectedGpuId="/gpu/1",Windows=new(){["main"]=new WindowPlacement(50,60,1100,800,true)}};
        var decoded=SettingsCodec.Read(SettingsCodec.Write(s)).Settings;
        Assert.Equal("/gpu/1",decoded.SelectedGpuId);Assert.Equal(300,decoded.HistorySeconds);Assert.True(decoded.Windows["main"].Topmost);
    }
    [Theory][InlineData(3600)][InlineData(10800)][InlineData(21600)]
    public void HourRangesSurviveSettingsRoundtrip(int seconds)
    {
        var loaded=SettingsCodec.Read(SettingsCodec.Write(new UserSettings {HistorySeconds=seconds}));
        Assert.Null(loaded.Error);
        Assert.Equal(seconds,loaded.Settings.HistorySeconds);
    }
    [Fact] public void RemovedMonitorAndInvalidWindowSizesClampToVisibleWorkArea()
    {
        var area=new WorkArea(0,0,1920,1040);
        var p=WindowPlacement.Clamp(new(5000,-400,3000,2000,false),new[]{area},800,600);
        Assert.Equal(new WindowPlacement(0,0,1920,1040,false),p);
        var invalid=WindowPlacement.Clamp(new(double.NaN,double.PositiveInfinity,-1,0,false),new[]{area},800,600);
        Assert.True(double.IsFinite(invalid.Left));Assert.Equal(800,invalid.Width);Assert.Equal(600,invalid.Height);
    }
    [Fact] public void PlacementPreservesValidSecondaryMonitorPosition()
    {
        var p=new WindowPlacement(-1200,50,800,600,true);
        Assert.Equal(p,WindowPlacement.Clamp(p,new[]{new WorkArea(-1920,0,1920,1080),new WorkArea(0,0,1920,1040)},800,600));
    }
}
