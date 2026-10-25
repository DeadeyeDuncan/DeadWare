using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class RecoveryTests
{
    [Fact] public void LostLifecycleEventsRequireFreshIdentityAndDiscardOldTotals()
    {
        var live=new Dictionary<int,ProcessIdentity>{{42,new(42,100,"old")}};
        var tracker=new ProcessAttribution(pid=>live.GetValueOrDefault(pid));
        Assert.True(tracker.Record(42,150,1000,0));tracker.Take(1);
        // OS process changed; neither ETW stop nor start was delivered.
        live[42]=new(42,200,"replacement");
        Assert.True(tracker.ObserveLoss(1));Assert.Empty(tracker.Take(2));
        Assert.False(tracker.Record(42,150,9000,0));
        Assert.True(tracker.Record(42,250,30,10));
        var row=Assert.Single(tracker.Take(3));
        Assert.Equal("replacement",row.Name);Assert.Equal(200,row.Generation);Assert.Equal(30,row.ReceivedBytes);Assert.Equal(10,row.SentBytes);
    }
    [Fact] public void SameCumulativeLossCounterDoesNotRepeatedlyResetNewTraffic()
    {
        var tracker=new ProcessAttribution(pid=>new(pid,100,"app"));
        Assert.True(tracker.ObserveLoss(3));tracker.Record(7,150,40,5);tracker.Take(1);
        Assert.False(tracker.ObserveLoss(3));tracker.Record(7,160,10,0);
        var row=Assert.Single(tracker.Take(2));Assert.Equal(50,row.ReceivedBytes);Assert.Equal(10,row.DownloadBytesPerSecond);
    }
    [Fact] public void ReconciliationRepairsMissedReuseAndDropsExitedProcessesWithoutLifecycleEvents()
    {
        var live=new Dictionary<int,ProcessIdentity>{{1,new(1,100,"old")},{2,new(2,100,"exiting")}};
        var tracker=new ProcessAttribution(pid=>live.GetValueOrDefault(pid));
        tracker.Record(1,150,1000,0);tracker.Record(2,150,500,0);tracker.Take(1);
        live[1]=new(1,200,"new");live.Remove(2);tracker.Reconcile();
        Assert.Empty(tracker.Take(2));
        Assert.False(tracker.Record(2,250,100,0));Assert.False(tracker.Record(1,150,100,0));
        tracker.Record(1,250,20,0);var row=Assert.Single(tracker.Take(3));Assert.Equal("new",row.Name);Assert.Equal(20,row.ReceivedBytes);
    }
    [Fact] public void ReconciliationPreservesTotalsForStillRunningGeneration()
    {
        var tracker=new ProcessAttribution(pid=>new(pid,100,"same"));tracker.Record(1,150,100,10);tracker.Take(1);
        tracker.Reconcile();tracker.Record(1,160,20,2);var row=Assert.Single(tracker.Take(2));Assert.Equal(120,row.ReceivedBytes);Assert.Equal(20,row.DownloadBytesPerSecond);
    }
    [Fact] public void DpiConversionScalesOnlySizeAndKeepsPhysicalMonitorOrigin()
    {
        Assert.Equal(new WindowPlacement(-3200,120,1200,900,true),WindowCoordinates.FromDipSize(-3200,120,800,600,144,true));
        Assert.Equal(new WindowPlacement(2560,20,1600,1200,false),WindowCoordinates.FromDipSize(2560,20,800,600,192,false));
    }
    [Fact] public void LegacyDipSettingsAreNotMisreadAsPhysicalPixelPlacement()
    {
        var read=SettingsCodec.Read("{\"Theme\":\"Ember\",\"Windows\":{\"main\":{\"Left\":2000,\"Top\":40,\"Width\":800,\"Height\":600,\"Topmost\":true}}}");
        Assert.Empty(read.Settings.Windows);Assert.Equal("Ember",read.Settings.Theme);Assert.NotNull(read.Error);
    }
}
