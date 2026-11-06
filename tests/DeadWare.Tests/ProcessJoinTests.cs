using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessJoinTests
{
    static ProcessSample P(int pid,long gen)=>new(pid,gen,$"p{pid}.exe",1,null,0,null,null,ProcessGap.None,null);
    static ProcessTraffic T(int pid,long gen,double? down,double? up)=>new(pid,gen,$"p{pid}.exe",down,up,0,0);
    static IReadOnlyList<ProcessSample> Procs(int count)=>Enumerable.Range(1,count).Select(i=>P(i,1)).ToArray();
    static DiskProcessRate R(int pid,long gen,double read,double write)=>new(pid,gen,read,write);
    static DiskProcessTotal D(int pid,long gen,long read,long write)=>new(pid,gen,read,write);
    static DiskTake Take(DiskTraceState state=DiskTraceState.Running,string status="",bool lossy=false,bool warm=true,IReadOnlyList<DiskProcessRate>? rates=null,double unattributedShare=0,long unattributedEvents=0)
        =>new(state,status,lossy,warm,rates??[],unattributedShare,unattributedEvents);

    [Fact] public void Network_match_absent_and_other_generation(){ var r=ProcessJoins.Network([P(8,1),P(9,1),P(10,1)],[T(8,1,100,50),T(10,2,1,1)],true,"");
        Assert.Equal(100,r.Processes[0].DownloadPerSecond);Assert.Equal(50,r.Processes[0].UploadPerSecond);Assert.Equal(0,r.Processes[1].DownloadPerSecond);Assert.Equal(0,r.Processes[1].UploadPerSecond);
        Assert.Null(r.Processes[2].DownloadPerSecond);Assert.Equal(ProcessGap.NotMatched,r.Processes[2].NetworkGap);Assert.Equal(1,r.Misses);Assert.True(r.State.Available);}
    [Fact] public void Network_first_take_is_null_first_sample()=>Assert.Equal(ProcessGap.FirstSample,ProcessJoins.Network([P(8,1)],[T(8,1,null,null)],true,"").Processes[0].NetworkGap);
    [Fact] public void Network_unavailable_carries_capture_status(){ var r=ProcessJoins.Network([P(8,1)],[],false,"Per-app ETW unavailable (X)");Assert.Null(r.Processes[0].DownloadPerSecond);Assert.Equal("Per-app ETW unavailable (X)",r.State.Status);Assert.False(r.State.Available);}
    [Fact] public void Majority_misses_over_four_rows_make_network_unavailable(){ var r=ProcessJoins.Network(Procs(5),[T(1,1,1,1),T(2,1,1,1),T(3,2,1,1),T(4,2,1,1),T(5,2,1,1)],true,"");
        Assert.False(r.State.Available);Assert.Equal(ProcessJoins.NetworkUnmatched,r.State.Status);Assert.Equal(3,r.Misses);Assert.All(r.Processes,p=>Assert.Null(p.DownloadPerSecond));}
    [Fact] public void Exactly_half_misses_do_not_trip_the_guard()=>Assert.True(ProcessJoins.Network(Procs(4),[T(1,1,1,1),T(2,1,1,1),T(3,2,1,1),T(4,2,1,1)],true,"").State.Available);
    [Fact] public void Three_rows_never_trip_the_guard()=>Assert.True(ProcessJoins.Network(Procs(3),[T(1,2,1,1),T(2,2,1,1),T(3,2,1,1)],true,"").State.Available);
    [Fact] public void Capture_rows_for_pids_outside_the_scan_are_not_counted(){ var r=ProcessJoins.Network(Procs(4),[T(1,1,1,1),T(2,1,1,1),T(3,1,1,1),T(4,1,1,1),T(90,1,1,1),T(91,1,1,1),T(92,1,1,1)],true,"");Assert.True(r.State.Available);Assert.Equal(0,r.Misses);}
    [Fact] public void Disk_warm_up_is_first_sample_for_every_row(){ var r=ProcessJoins.Disk([P(8,1),P(9,1)],Take(warm:false));Assert.All(r.Processes,p=>{Assert.Null(p.DiskReadPerSecond);Assert.Equal(ProcessGap.FirstSample,p.DiskGap);});Assert.True(r.State.Available);}
    [Fact] public void Lossy_take_nulls_with_status(){ var r=ProcessJoins.Disk([P(8,1)],Take(lossy:true,status:"Windows dropped disk events",rates:[R(8,1,5,5)]));
        Assert.Equal("Windows dropped disk events",r.State.Status);Assert.False(r.State.Available);Assert.Null(r.Processes[0].DiskReadPerSecond);}
    [Fact] public void Stopped_trace_nulls_with_status(){ var r=ProcessJoins.Disk([P(8,1)],Take(DiskTraceState.Failed,"Disk trace failed (0xC0000022)"));Assert.Equal("Disk trace failed (0xC0000022)",r.State.Status);Assert.Null(r.Processes[0].DiskWritePerSecond);}
    [Fact] public void Off_take_names_its_reason(){ Assert.Equal("Per-app disk tracing is off",DiskTake.Off.Status);var r=ProcessJoins.Disk([P(8,1)],DiskTake.Off);
        Assert.False(r.State.Available);Assert.Equal(DiskTake.Off.Status,r.State.Status);Assert.Null(r.Processes[0].DiskReadPerSecond);}
    [Fact] public void Disk_match_absent_and_miss(){ var r=ProcessJoins.Disk([P(8,1),P(9,1),P(10,1)],Take(rates:[R(8,1,500,2000),R(10,2,1,1)]));
        Assert.Equal(500,r.Processes[0].DiskReadPerSecond);Assert.Equal(2000,r.Processes[0].DiskWritePerSecond);Assert.Equal(0,r.Processes[1].DiskReadPerSecond);Assert.Equal(0,r.Processes[1].DiskWritePerSecond);
        Assert.Null(r.Processes[2].DiskReadPerSecond);Assert.Equal(ProcessGap.NotMatched,r.Processes[2].DiskGap);Assert.Equal(1,r.Misses);Assert.True(r.State.Available);}
    [Fact] public void Majority_misses_make_disk_unavailable(){ var r=ProcessJoins.Disk(Procs(5),Take(rates:[R(1,1,1,1),R(2,1,1,1),R(3,2,1,1),R(4,2,1,1),R(5,2,1,1)]));Assert.Equal(ProcessJoins.DiskUnmatched,r.State.Status);Assert.All(r.Processes,p=>Assert.Null(p.DiskReadPerSecond));}
    [Fact] public void Majority_unattributed_events_make_disk_unavailable()=>Assert.Equal(ProcessJoins.DiskUnmatched,ProcessJoins.Disk([P(8,1)],Take(unattributedShare:0.51)).State.Status);
    [Fact] public void Half_unattributed_events_stay_available()=>Assert.True(ProcessJoins.Disk([P(8,1)],Take(unattributedShare:0.5)).State.Available);
    [Fact] public void Window_needs_two_seconds_and_restarts_after_reset(){ var w=new DiskRateWindow();w.Observe(0,[D(8,1,0,0)],0,0);w.Observe(1,[D(8,1,0,1000)],1,0);Assert.False(w.Warm);
        w.Observe(2,[D(8,1,0,4000)],2,0);Assert.True(w.Warm);Assert.Equal(2000,w.Rates().Single().Write);w.Reset(2.5);Assert.False(w.Warm);Assert.Empty(w.Rates());}
    [Fact] public void Window_uses_the_newest_observation_at_least_two_seconds_older(){ var w=new DiskRateWindow();w.Observe(0,[D(8,1,0,0)],0,0);w.Observe(1,[D(8,1,0,1000)],0,0);w.Observe(2,[D(8,1,0,2000)],0,0);w.Observe(3,[D(8,1,0,5000)],0,0);
        Assert.Equal(2000,w.Rates().Single().Write);}
    [Fact] public void Window_counts_a_new_key_from_zero(){ var w=new DiskRateWindow();w.Observe(0,[D(8,1,0,0)],0,0);w.Observe(2,[D(8,1,0,0),D(9,1,1000,0)],0,0);
        var rate=Assert.Single(w.Rates(),r=>r.Pid==9);Assert.Equal(500,rate.Read);}
    [Fact] public void Window_keeps_generations_of_a_reused_pid_apart(){ var w=new DiskRateWindow();w.Observe(0,[D(8,1,0,4000)],0,0);w.Observe(2,[D(8,2,0,100)],0,0);
        var rate=Assert.Single(w.Rates());Assert.Equal(2L,rate.Generation);Assert.Equal(50,rate.Write);}
    [Fact] public void Window_share_of_unattributed_events(){ var w=new DiskRateWindow();w.Observe(0,[],5,5);w.Observe(2,[],15,11);Assert.Equal(0.6,w.UnattributedShare,6);}
    [Fact] public void Window_share_is_zero_without_events(){ var w=new DiskRateWindow();w.Observe(0,[],5,1);w.Observe(2,[],5,1);Assert.Equal(0,w.UnattributedShare);}
    [Fact] public void Reconcile_with_index_keeps_matching_generation_without_a_native_read(){ int reads=0;var a=new ProcessAttribution(pid=>{reads++;return new(pid,1,"a");});a.Refresh(8);reads=0;
        a.Reconcile(pid=>new(pid,1,"a"));Assert.Equal(0,reads);}
    [Theory][InlineData(true,0L)][InlineData(false,0L)][InlineData(false,2L)]
    public void Reconcile_with_index_rereads_missing_older_or_newer(bool missing,long indexGeneration){ int reads=0;var a=new ProcessAttribution(pid=>{reads++;return new(pid,1,"a");});a.Refresh(8);reads=0;
        a.Reconcile(pid=>missing?null:new(pid,indexGeneration,"a"));Assert.Equal(1,reads);}
}
