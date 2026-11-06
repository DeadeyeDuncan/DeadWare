using System.Text.Json;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

[Collection(TestHostThreadPool.TimingCollection)]
public class DiagnosticOptionsTests : IAsyncLifetime
{
    private readonly List<Collector> started=[];
    public Task InitializeAsync()=>Task.CompletedTask;
    public async Task DisposeAsync() {foreach(var c in started)await c.DisposeAsync();}

    private async Task<IReadOnlyList<Reading>> Readings(CollectorOptions options,int count,IStorageFastSource? storage=null)
    {
        var c=new Collector(options with {HardwareEnabled=false,EtwEnabled=false,PollingIntervalSeconds=0.5},storage,processes:new FakeProcesses());started.Add(c);
        var list=new List<Reading>();
        var tcs=new TaskCompletionSource<IReadOnlyList<Reading>>(TaskCreationOptions.RunContinuationsAsynchronously);
        c.SampleAvailable+=r=>{lock(list){if(list.Count<count){list.Add(r);if(list.Count==count)tcs.TrySetResult(list.ToArray());}}};
        c.Start();
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private async Task<Reading> FirstReading(CollectorOptions options)=>(await Readings(options,1))[0];
    private async Task<Reading> SecondReading(CollectorOptions options)=>(await Readings(options,2))[1];
    private async Task<Reading> FirstReadingWithThrowingStorage(CollectorOptions options)=>(await Readings(options,1,new ThrowingStorage()))[0];
    private static Reading Fixture()=>new(new(DateTimeOffset.UtcNow,0,new Dictionary<string,double?>()),[],[],[],"CPU","","","",false);
    private static ProcessActivity ActivityWith(params string[] names)=>new(ProcessActivityKind.Available,1,DateTimeOffset.UtcNow,1,1,8,
        names.Select((n,i)=>new ProcessSample(i+1,1,n,1,1.0,1000,null,null,ProcessGap.None,null)).ToArray(),ProcessOmitted.None,names.Length,1);

    [Fact] public void Diagnose_runs_the_scan_unless_no_processes(){ Assert.True(App.DiagnosticOptions(["--diagnose","o.json"],false).ProcessMonitoring);Assert.False(App.DiagnosticOptions(["--diagnose","o.json","--no-processes"],false).ProcessMonitoring);}
    [Theory][InlineData(new[]{"--diagnose","o"},false,false)][InlineData(new[]{"--diagnose","o","--no-etw"},true,false)][InlineData(new[]{"--diagnose","o","--no-processes"},true,false)]
    public void Disk_tracing_needs_elevation_and_the_disk_build(string[] args,bool elevated,bool expected)=>Assert.Equal(expected,App.DiagnosticOptions(args,elevated).ProcessDiskTracing);
    [Fact] public void Disk_tracing_follows_the_disk_build_when_elevated()=>Assert.Equal(ProcessFeatures.DiskColumns,App.DiagnosticOptions(["--diagnose","o"],true).ProcessDiskTracing);
    [Fact] public void Diagnostic_report_has_counts_and_no_app_names(){
        var traffic=new ProcessTraffic[]{new(101,1,"secret-net.exe",10,20,100,200),new(102,2,"PID 102",null,null,5,0)};
        var activity=ActivityWith("secret-app.exe","OtherSecret.EXE");
        var reading=Fixture() with {Processes=traffic,ProcessActivity=activity,ProcessSummary=ProcessSummary.From(activity)};
        string json=App.DiagnosticReport("Completed","Idle between samples",15,App.DiagnosticOptions(["--diagnose","o.json"],true),[reading]);
        foreach(string name in new[]{"secret-net","PID 102","secret-app","OtherSecret"})Assert.DoesNotContain(name,json,StringComparison.OrdinalIgnoreCase);
        using var doc=JsonDocument.Parse(json);var s=doc.RootElement.GetProperty("Samples")[0];
        Assert.False(s.TryGetProperty("Processes",out _));Assert.False(s.TryGetProperty("Connections",out _));Assert.False(s.TryGetProperty("ProcessActivity",out _));
        Assert.Equal(2,s.GetProperty("NetworkApplications").GetInt32());
        Assert.Equal("Available",s.GetProperty("ProcessSummary").GetProperty("State").GetString());Assert.Equal(2,s.GetProperty("ProcessSummary").GetProperty("Processes").GetInt32());
        Assert.Equal("Completed",doc.RootElement.GetProperty("Status").GetString());Assert.Equal(3,doc.RootElement.GetProperty("ShutdownDeadlineSeconds").GetInt32());}
    [Fact] public void Network_applications_counts_the_in_memory_list(){ var r=Fixture() with {Processes=[new(7,1,"a.exe",1,1,1,1)]};
        Assert.Single(r.Processes);Assert.Equal(1,r.NetworkApplications);}
    [Fact] public async Task Summary_is_set_only_with_diagnostic_timings_and_reports_off(){ var off=await FirstReading(new(DiagnosticTimings:true));Assert.NotNull(off.ProcessSummary);Assert.Equal("Off",off.ProcessSummary!.State);
        var on=await SecondReading(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Available",on.ProcessSummary!.State);Assert.Equal(on.ProcessActivity.Processes.Count,on.ProcessSummary.Processes);
        Assert.Null((await FirstReading(new(DiagnosticTimings:false,ProcessMonitoring:true))).ProcessSummary);
        var failed=await FirstReadingWithThrowingStorage(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Unavailable",failed.ProcessSummary!.State);}
    [Fact] public async Task Timing_keys_appear_when_the_scan_runs(){ var r=await SecondReading(new(DiagnosticTimings:true,ProcessMonitoring:true));
        Assert.Contains("windows/processes",r.ProviderMilliseconds!.Keys);Assert.Contains("processes/join",r.ProviderMilliseconds.Keys);Assert.DoesNotContain("etw/disk",r.ProviderMilliseconds.Keys);}
}
