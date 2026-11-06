using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessActivityTrackerTests
{
    static readonly DateTimeOffset Epoch=new(2026,1,1,0,0,0,TimeSpan.Zero);
    const long Second=10_000_000;
    static long Ticks(double secondsAfterEpoch)=>Epoch.UtcTicks+(long)(secondsAfterEpoch*Second);
    static ProcessScanEntry Entry(int pid,long cpu=0,long ws=0,long read=0,long write=0,long? gen=null,string? name=null)=>new(pid,gen??Ticks(-1000),name??$"p{pid}.exe",1,1,ws,cpu,0,read,write);
    static ProcessScan Scan(double seconds,params ProcessScanEntry[] entries)=>ScanAt(seconds,1,entries);
    static ProcessScan ScanAt(double seconds,double interval,params ProcessScanEntry[] entries)=>new(Epoch.AddSeconds(seconds),seconds,interval,entries,entries.Length,0);
    static ProcessSample First(TrackedScan r)=>r.Processes.Single();

    [Fact] public void First_scan_has_null_rates_and_memory(){ var t=new ProcessActivityTracker(8);var r=t.Update(Scan(0,Entry(8,cpu:0,ws:4096)));
        var s=r.Processes.Single();Assert.Null(s.CpuPercent);Assert.Null(s.IoReadPerSecond);Assert.Equal(4096,s.PrivateBytes);Assert.Equal(ProcessGap.FirstSample,s.RateGap);Assert.Null(s.Deltas);Assert.Null(r.DeltaSeconds);}
    [Fact] public void One_busy_thread_on_eight_processors_reads_twelve_and_a_half_percent(){ var t=new ProcessActivityTracker(8);t.Update(Scan(0,Entry(8,cpu:0)));
        var s=t.Update(Scan(1,Entry(8,cpu:10_000_000,ws:1000,read:500,write:250))).Processes[0];
        Assert.Equal(12.5,s.CpuPercent!.Value,6);Assert.Equal(500,s.IoReadPerSecond!.Value,6);Assert.Equal(250,s.IoWritePerSecond!.Value,6);Assert.Equal(ProcessGap.None,s.RateGap);
        Assert.Equal(10_000_000,s.Deltas!.CpuTime);Assert.Equal(1000,s.Deltas.MemoryByteSeconds,6);Assert.Equal(1,s.Deltas.Seconds,6);}
    [Fact] public void Pid_reuse_starts_a_new_baseline(){ var t=new ProcessActivityTracker(8);t.Update(Scan(0,Entry(8,cpu:5_000_000,gen:Ticks(-500))));
        var second=t.Update(Scan(5,Entry(8,cpu:100,gen:Ticks(-2))));Assert.Null(second.Processes[0].CpuPercent);Assert.Null(second.Processes[0].Deltas!.CpuTime);}
    [Fact] public void Process_started_during_the_interval_is_credited_in_full(){ var t=new ProcessActivityTracker(8);t.Update(Scan(10,Entry(1)));
        var s=t.Update(Scan(11,Entry(1),Entry(9,cpu:2_000_000,read:1000,gen:Ticks(9.5)))).Processes.Single(p=>p.Pid==9);
        Assert.Equal(2_000_000,s.Deltas!.CpuTime);Assert.Equal(1000,s.Deltas.IoReadBytes);Assert.Equal(2_000_000/(double)(Second*8)*100,s.CpuPercent!.Value,6);}
    [Fact] public void Running_process_missed_by_the_previous_scan_is_null(){ var t=new ProcessActivityTracker(8);t.Update(Scan(10,Entry(1)));
        var s=t.Update(Scan(11,Entry(1),Entry(9,cpu:2_000_000,gen:Ticks(0)))).Processes.Single(p=>p.Pid==9);
        Assert.Null(s.CpuPercent);Assert.Null(s.Deltas!.CpuTime);Assert.Equal(ProcessGap.FirstSample,s.RateGap);}
    [Theory][InlineData(0.0)][InlineData(-1.0)][InlineData(4.01)] public void Delta_t_outside_freshness_is_null(double dt){ var t=new ProcessActivityTracker(8);t.Update(Scan(10,Entry(8)));
        var r=t.Update(Scan(10+dt,Entry(8,cpu:1_000_000)));Assert.Null(r.Processes[0].CpuPercent);Assert.Null(r.Processes[0].Deltas);Assert.Null(r.DeltaSeconds);}
    [Fact] public void Delta_t_at_exactly_freshness_is_valid(){ var t=new ProcessActivityTracker(1);t.Update(Scan(10,Entry(8)));
        var r=t.Update(Scan(14,Entry(8,cpu:4*Second)));Assert.Equal(100,r.Processes[0].CpuPercent!.Value,6);Assert.Equal(4,r.DeltaSeconds!.Value,6);}
    [Fact] public void Counter_going_backwards_is_null(){ var t=new ProcessActivityTracker(8);t.Update(Scan(0,Entry(8,read:100,write:100)));
        var r=t.Update(Scan(1,Entry(8,read:200,write:50)));Assert.Null(r.Processes[0].IoWritePerSecond);Assert.Null(r.Processes[0].CpuPercent);Assert.Equal(ProcessGap.FirstSample,r.Processes[0].RateGap);}
    [Fact] public void Interval_change_uses_the_longer_interval_for_freshness(){
        var t=new ProcessActivityTracker(8);t.Update(ScanAt(0,1,Entry(8,cpu:0,read:0)));
        // 1 s -> 10 s: freshness is 13 s, so a 9 s gap still yields rates.
        var up=t.Update(ScanAt(9,10,Entry(8,cpu:9*Second,read:900))).Processes[0];
        Assert.Equal(12.5,up.CpuPercent!.Value,6);Assert.Equal(100,up.IoReadPerSecond!.Value,6);Assert.Equal(ProcessGap.None,up.RateGap);Assert.Equal(9*Second,up.Deltas!.CpuTime);Assert.Equal(9,up.Deltas.Seconds,6);
        // 10 s -> 1 s: the previous interval still counts, so a 5 s gap (> 4 s) yields rates.
        var down=t.Update(ScanAt(14,1,Entry(8,cpu:14*Second,read:900))).Processes[0];
        Assert.Equal(5*Second/(5.0*Second*8)*100,down.CpuPercent!.Value,6);Assert.Equal(0,down.IoReadPerSecond!.Value,6);
        // Same interval on both sides keeps the short window: 5 s is stale.
        Assert.Null(t.Update(ScanAt(19,1,Entry(8,cpu:20*Second))).Processes[0].CpuPercent);
        // Beyond the longer window (13 s) is stale too.
        var u=new ProcessActivityTracker(8);u.Update(ScanAt(0,1,Entry(8)));Assert.Null(u.Update(ScanAt(13.01,10,Entry(8,cpu:Second))).Processes[0].CpuPercent);
        var v=new ProcessActivityTracker(8);v.Update(ScanAt(0,1,Entry(8)));Assert.Equal(1/(13.0*8)*100,v.Update(ScanAt(13,10,Entry(8,cpu:Second))).Processes[0].CpuPercent!.Value,6);}
    [Fact] public void Cap_keeps_4096_ranked_by_cpu_then_io_then_memory_then_pid(){ var t=new ProcessActivityTracker(8);
        var before=Enumerable.Range(1,4100).Select(i=>Entry(i)).ToArray();t.Update(Scan(0,before));
        // pid 4100 highest cpu; pids 7,5 tie on cpu 1000 and differ on io; pids 3,2 tie on cpu+io and differ on memory; the rest idle, pid asc.
        var after=Enumerable.Range(1,4100).Select(i=>i switch{
            4100=>Entry(i,cpu:5_000_000),7=>Entry(i,cpu:1_000_000,read:10),5=>Entry(i,cpu:1_000_000,read:99),
            3=>Entry(i,cpu:1_000,ws:10),2=>Entry(i,cpu:1_000,ws:50),_=>Entry(i)}).ToArray();
        var r=t.Update(Scan(1,after));
        Assert.Equal(4096,r.Processes.Count);Assert.Equal(4,r.Omitted.Count);Assert.Equal(4100,r.TotalProcesses);
        Assert.Equal([4100,5,7,2,3,1],r.Processes.Take(6).Select(p=>p.Pid));
        Assert.DoesNotContain(r.Processes,p=>p.Pid>=4096&&p.Pid<=4099);}
    [Fact] public void All_null_first_scan_keeps_the_largest_by_memory(){ var t=new ProcessActivityTracker(8);
        var entries=Enumerable.Range(1,4100).Select(i=>Entry(i,ws:i*10L)).ToArray();var r=t.Update(Scan(0,entries));
        long smallestKept=r.Processes.Min(p=>p.PrivateBytes);Assert.Equal(410,smallestKept);Assert.DoesNotContain(r.Processes,p=>p.PrivateBytes<smallestKept);
        Assert.Equal(10+20+30+40,r.Omitted.PrivateBytes);Assert.Equal(ProcessDeltas.Zero,r.Omitted.Deltas);}
    [Fact] public void Omitted_carries_summed_exact_deltas(){ var t=new ProcessActivityTracker(8);
        t.Update(Scan(0,Enumerable.Range(1,4100).Select(i=>Entry(i)).ToArray()));
        // pids 1..4 stay idle with distinct small deltas and rank last on ties of memory 0 only if cpu is lowest.
        var r=t.Update(Scan(2,Enumerable.Range(1,4100).Select(i=>i<=4?Entry(i,cpu:0,read:i*100,ws:0):Entry(i,cpu:1_000+i,ws:0)).ToArray()));
        Assert.Equal(4,r.Omitted.Count);Assert.Equal(0,r.Omitted.Deltas.CpuTime);Assert.Equal(100+200+300+400,r.Omitted.Deltas.IoReadBytes);Assert.Equal(2,r.Omitted.Deltas.Seconds,6);
        var cpuOmitted=new ProcessActivityTracker(8);cpuOmitted.Update(Scan(0,Enumerable.Range(1,4100).Select(i=>Entry(i)).ToArray()));
        var r2=cpuOmitted.Update(Scan(1,Enumerable.Range(1,4100).Select(i=>Entry(i,cpu:i<=4?100L*i:Second)).ToArray()));
        Assert.Equal(100+200+300+400,r2.Omitted.Deltas.CpuTime);}
    [Fact] public void Index_covers_every_scanned_process(){ var t=new ProcessActivityTracker(8);var r=t.Update(Scan(0,Enumerable.Range(1,4100).Select(i=>Entry(i)).ToArray()));
        Assert.Equal(4100,r.Index.Count);Assert.Equal("p4100.exe",r.Index[4100].Name);}
    [Fact] public void Ten_thousand_create_exit_cycles_keep_baselines_bounded(){ var t=new ProcessActivityTracker(8);
        for(int i=0;i<10_000;i++)t.Update(Scan(i,Entry(1000+i)));Assert.True(t.TrackedCount<=1);}
    [Fact] public void Reset_returns_to_first_sample(){ var t=new ProcessActivityTracker(8);t.Update(Scan(0,Entry(8)));t.Reset();Assert.Equal(0,t.TrackedCount);
        var s=First(t.Update(Scan(1,Entry(8,cpu:Second))));Assert.Null(s.CpuPercent);Assert.Equal(ProcessGap.FirstSample,s.RateGap);}
    [Fact] public void Off_and_unavailable_instances(){ Assert.Equal(ProcessActivityKind.Off,ProcessActivity.Off.Kind);Assert.Equal(ProcessActivityKind.None,ProcessActivity.None.Kind);
        var u=ProcessActivity.Unavailable("IOException");Assert.Equal(ProcessActivityKind.Unavailable,u.Kind);Assert.Equal("IOException",u.Scan.Status);Assert.False(u.Scan.Available);Assert.True(u.Disk.Available);Assert.True(u.Network.Available);
        Assert.True(ProcessColumnState.Ready.Available);Assert.Equal("",ProcessColumnState.Ready.Status);}
    [Fact] public void Deltas_add_treats_null_as_zero_only_when_the_other_side_is_known(){
        var a=new ProcessDeltas(5,null,null,10,1);var b=new ProcessDeltas(null,7,null,20,2);var c=a.Add(b);
        Assert.Equal((5L,7L,(long?)null),(c.CpuTime,c.IoReadBytes,c.IoWriteBytes));Assert.Equal(30,c.MemoryByteSeconds,6);Assert.Equal(3,c.Seconds,6);}
}
