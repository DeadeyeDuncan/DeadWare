using DeadWare.App;
using DeadWare.Core;
using Xunit;
using System.IO;

namespace DeadWare.App.Tests;

public class CoolingHistoryServiceTests
{
    [Fact]
    public async Task Recorded_stream_can_save_fixed_reference_and_log_muted_warning_across_restart()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(root,"history.db");var service=new CoolingHistoryService(path,"machine","setup");
        int delivered=0;service.WarningRaised+=_=>delivered++;
        try
        {
            service.Start();var now=DateTimeOffset.UtcNow;var origin=now.AddMinutes(-65);
            async Task Feed(DateTimeOffset start,int seconds,double mono,double temperature)
            {
                for(int i=0;i<=seconds;i++)
                {
                    service.TryRecord(ThermalProjectionTests.Reading(
                        ThermalProjectionTests.Sensor("t","CPU Package","Temperature",temperature),
                        ThermalProjectionTests.Sensor("p","CPU Package","Power",70),
                        ThermalProjectionTests.Sensor("l","CPU Total","Load",50)) with
                        {Sample=new(start.AddSeconds(i),mono+i,new Dictionary<string,double?>())});
                    if(i%60==59)
                    {
                        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while(service.PendingSamples>0)await Task.Delay(1,deadline.Token);
                    }
                }
                using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while(service.PendingSamples>0)await Task.Delay(1,finish.Token);
            }
            await Feed(origin,37*60,0,60);
            var baseline=await service.ReadViewAsync(null,null,1);
            Assert.NotNull(baseline.SelectedDevice);
            await service.CreateReferenceAsync(baseline.SelectedDevice,origin,origin.AddMinutes(37),"Known reference");
            await Feed(now.AddMinutes(-18),18*60,47*60,70);
            await service.StopAsync();
            var result=await service.ReadViewAsync(null,null,1);
            Assert.Equal(0,service.DroppedSamples);Assert.Equal(0,delivered);
            var warning=Assert.Single(result.Warnings);Assert.InRange(warning.Delta,9.9,10.1);
            Assert.All(result.ActiveReference!.Windows,w=>Assert.Equal(60,w.Temperature.Average));
            var reopened=new CoolingHistoryService(path,"machine","setup");
            reopened.Start();await reopened.StopAsync();
            var saved=await reopened.ReadViewAsync(null,null,1);
            Assert.Equal(warning.Id,Assert.Single(saved.Warnings).Id);
            Assert.Same(saved.ActiveReference,(await reopened.ReadViewAsync(null,null,1)).ActiveReference);
            await reopened.CreateReferenceAsync(saved.SelectedDevice!,origin,origin.AddMinutes(37),"Another fixed reference");
            Assert.NotEqual(saved.ActiveReference!.Id,(await reopened.ReadViewAsync(null,null,1)).ActiveReference!.Id);
            await reopened.ActivateReferenceAsync(saved.ActiveReference.Id);
            Assert.Equal(saved.ActiveReference.Id,(await reopened.ReadViewAsync(null,null,1)).ActiveReference!.Id);
            await reopened.AcknowledgeAsync(warning.Id);
            Assert.True(Assert.Single((await reopened.ReadViewAsync(null,null,1)).Warnings).Acknowledged);
        }
        finally {await service.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task New_configuration_waits_for_its_own_first_window_instead_of_selecting_old_reference()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(root,"history.db");var service=new CoolingHistoryService(path,"machine","old");
        try
        {
            service.Start();service.TryRecord(ThermalProjectionTests.Reading(ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60)));
            await service.StopAsync();
            var old=await service.ReadViewAsync(null,null,1);Assert.Single(old.Devices);
            service.Configure(true,false,30,90,"new");
            var pending=await service.ReadViewAsync(null,null,1);
            Assert.Single(pending.Devices);Assert.Null(pending.SelectedDevice);Assert.Null(pending.ActiveReference);
            var explicitOld=await service.ReadViewAsync(old.Devices[0].Id,"old",1);
            Assert.Equal("old",explicitOld.SelectedDevice!.ConfigurationId);
        }
        finally {await service.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Burst_publication_is_nonblocking_bounded_and_reports_dropped_history()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        var service=new CoolingHistoryService(Path.Combine(root,"history.db"),"machine","setup");
        try
        {
            service.Start();
            var reading=ThermalProjectionTests.Reading(ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60));
            var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<100000;i++)service.TryRecord(reading);
            watch.Stop();
            Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3),$"Publication took {watch.Elapsed}");
            await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.DroppedSamples>0);Assert.Contains("gaps",service.Status);
        }
        finally {await service.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Disabled_recording_creates_no_thermal_rows()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        var service=new CoolingHistoryService(Path.Combine(root,"history.db"),"machine","setup");
        try
        {
            service.Configure(false,false,7,30,"setup");service.Start();
            service.TryRecord(ThermalProjectionTests.Reading(ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60)));
            await service.StopAsync();Assert.Empty((await service.ReadViewAsync(null,null,1)).Devices);
        }
        finally {await service.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task No_io_until_started_and_quit_flushes_a_partial_window_without_inventing_reference_data()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(root,"history.db");
        var service=new CoolingHistoryService(path,"machine","setup");
        Assert.False(Directory.Exists(root));
        try
        {
            service.Start();
            var origin=DateTimeOffset.UtcNow.AddSeconds(-20);
            for(int i=0;i<20;i++)service.TryRecord(ThermalProjectionTests.Reading(
                ThermalProjectionTests.Sensor("t","CPU Package","Temperature",60),
                ThermalProjectionTests.Sensor("p","CPU Package","Power",70),
                ThermalProjectionTests.Sensor("l","CPU Total","Load",50)) with
                {Sample=new(origin.AddSeconds(i),i,new Dictionary<string,double?>())});
            await service.StopAsync();
            var view=await service.ReadViewAsync(null,null,1);
            Assert.Single(view.Devices); Assert.NotEmpty(view.History);
            Assert.All(view.History,w=>Assert.False(w.Eligible));
            Assert.False(view.Assessment.ShouldWarn); Assert.Empty(view.References);
            Assert.Equal(0,service.DroppedSamples);
        }
        finally {await service.StopAsync();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Corrupt_storage_reports_fault_and_publishing_does_not_throw()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string path=Path.Combine(root,"history.db");
        await File.WriteAllTextAsync(path,"not a database");
        var service=new CoolingHistoryService(path,"machine","setup");
        try
        {
            service.Start();await service.StopAsync();
            service.TryRecord(ThermalProjectionTests.Reading());
            var view=await service.ReadViewAsync(null,null,1);
            Assert.Contains("unavailable",view.Status,StringComparison.OrdinalIgnoreCase);
            Assert.False(view.Assessment.ShouldWarn);
            Assert.Equal("not a database",await File.ReadAllTextAsync(path));
        }
        finally {await service.StopAsync();Directory.Delete(root,true);}
    }

    [Fact]
    public void Legacy_settings_have_safe_persisted_cooling_defaults()
    {
        var settings=SettingsCodec.Read("{}").Settings;
        Assert.True(settings.CoolingRecording); Assert.False(settings.CoolingNotifications);
        Assert.Equal(30,settings.CoolingDetailedDays);Assert.Equal(90,settings.CoolingSummaryDays);
        Assert.True(Guid.TryParse(settings.CoolingConfigurationId,out _));
        Assert.Equal(settings.CoolingConfigurationId,SettingsCodec.Read(SettingsCodec.Write(settings)).Settings.CoolingConfigurationId);
    }
}
