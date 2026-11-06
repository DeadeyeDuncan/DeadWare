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
    [Fact] public void Diagnostic_json_has_a_summary_and_no_process_names(){ var activity=ActivityWith("secret-app.exe","OtherSecret.EXE");
        var reading=Fixture() with {ProcessActivity=activity,ProcessSummary=ProcessSummary.From(activity)};
        string json=JsonSerializer.Serialize(new{Samples=new[]{reading}},new JsonSerializerOptions{WriteIndented=true});
        Assert.Contains("\"ProcessSummary\"",json);Assert.DoesNotContain("\"State\": \"Off\"",json);Assert.DoesNotContain("secret-app",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("OtherSecret",json,StringComparison.OrdinalIgnoreCase);}
    [Fact] public async Task Summary_is_set_only_with_diagnostic_timings_and_reports_off(){ var off=await FirstReading(new(DiagnosticTimings:true));Assert.NotNull(off.ProcessSummary);Assert.Equal("Off",off.ProcessSummary!.State);
        var on=await SecondReading(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Available",on.ProcessSummary!.State);Assert.Equal(on.ProcessActivity.Processes.Count,on.ProcessSummary.Processes);
        Assert.Null((await FirstReading(new(DiagnosticTimings:false,ProcessMonitoring:true))).ProcessSummary);
        var failed=await FirstReadingWithThrowingStorage(new(DiagnosticTimings:true,ProcessMonitoring:true));Assert.Equal("Unavailable",failed.ProcessSummary!.State);}
    [Fact] public async Task Timing_keys_appear_when_the_scan_runs(){ var r=await SecondReading(new(DiagnosticTimings:true,ProcessMonitoring:true));
        Assert.Contains("windows/processes",r.ProviderMilliseconds!.Keys);Assert.Contains("processes/join",r.ProviderMilliseconds.Keys);Assert.DoesNotContain("etw/disk",r.ProviderMilliseconds.Keys);}
}
