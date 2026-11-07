using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class GpuProcessJoinTests
{
    static readonly DateTimeOffset Epoch=new(2026,1,1,0,0,0,TimeSpan.Zero);
    static DateTimeOffset T(double s)=>Epoch.AddSeconds(s);
    static readonly DateTimeOffset Prev=T(0);
    static readonly long Old=Prev.AddSeconds(-10).UtcTicks;
    static readonly IReadOnlyDictionary<long,GpuAdapterName> Two=new Dictionary<long,GpuAdapterName>{[0xA]=new(0,"NVIDIA GeForce RTX 4070"),[0xB]=new(1,"Intel(R) UHD Graphics 770")};
    // A not-ready item keeps the value given here, so a join that read it would show it. The reader itself writes 0 (see the reader's-zero fact).
    static GpuEngineItem En(int pid,long luid,int eng,double pct,string type="3D",bool ready=true,int phys=0)=>new(new(pid,luid,phys,eng,type),ready,pct);
    static GpuMemoryItem Mem(int pid,long luid,long bytes,bool ready=true)=>new(new(pid,luid,0,null,null),ready,bytes);
    // A Ready read at T(1). previous is the previous collection in seconds after Epoch (Prev by default), null for none.
    static GpuCounterRead Read(IReadOnlyList<GpuEngineItem> engines,IReadOnlyList<GpuMemoryItem>? dedicated=null,IReadOnlyList<GpuMemoryItem>? shared=null,double? previous=0,double? interval=1,IReadOnlyDictionary<long,GpuAdapterName>? adapters=null)
        =>new(ProcessColumnState.Ready,T(1),1,previous is{} p?T(p):null,interval,engines,dedicated??[],shared??[],adapters??Two,engines.Count+(dedicated?.Count??0)+(shared?.Count??0),0);
    static ProcessSample S(int pid,long? gen=null)=>new(pid,gen??Old,$"p{pid}.exe",1,0,0,0,0,ProcessGap.None,new ProcessDeltas(0,0,0,0,1));
    static IReadOnlyList<ProcessSample> Join(GpuCounterRead read,params ProcessSample[] samples)=>GpuProcessJoin.Join(samples,read).Processes;

    [Fact] public void The_busiest_ready_engine_wins_and_ties_go_to_engine_order(){
        var p=Join(Read([En(8,0x1,0,40),En(8,0xD,0,40),En(8,0xB,0,40),En(8,0xA,0,40,phys:1),En(8,0xA,1,40,"Copy"),En(8,0xC,0,40),En(8,0xA,0,40),En(8,0xA,2,10)]),S(8)).Single();
        Assert.Equal(40,p.GpuPercent);Assert.Equal("GPU 0 · 3D",p.GpuEngine);Assert.Equal("NVIDIA GeForce RTX 4070",p.GpuAdapter);
        // Numbered adapters by number, then "GPU ?" by LUID (0x1 before 0xC and 0xD, though 0x1 is the lowest LUID of all), then phys, then eng; the 10 % engine last.
        Assert.Equal([(0xAL,0,0),(0xAL,0,1),(0xAL,1,0),(0xBL,0,0),(0x1L,0,0),(0xCL,0,0),(0xDL,0,0),(0xAL,0,2)],p.GpuEngines!.Select(e=>(e.Luid,e.Phys,e.Engine)));
        Assert.Equal(["GPU 0 · 3D","GPU 0 · Copy","GPU 0 · 3D","GPU 1 · 3D","GPU ? · 3D","GPU ? · 3D","GPU ? · 3D","GPU 0 · 3D"],p.GpuEngines!.Select(e=>e.Label));Assert.Null(p.GpuEngines![4].Adapter);
        Assert.True(GpuProcessJoin.CompareEngineOrder(new(0xB,0,0,"3D","GPU 0 · 3D",null,1),new(0xA,0,0,"3D","GPU 1 · 3D",null,1))<0);}
    [Fact] public void A_not_ready_engine_is_ignored_when_another_is_ready(){ var p=Join(Read([En(8,0xA,0,90,ready:false),En(8,0xA,1,20,"Copy")]),S(8)).Single();Assert.Equal(20,p.GpuPercent);Assert.Equal(ProcessGap.None,p.GpuGap);}
    [Fact] public void All_engines_not_ready_is_a_first_sample(){ var p=Join(Read([En(8,0xA,0,90,ready:false)]),S(8)).Single();Assert.Null(p.GpuPercent);Assert.Null(p.GpuEngine);Assert.Null(p.GpuEngines);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);}
    [Fact] public void Memory_sums_over_adapters_and_a_not_ready_item_makes_it_unknown(){ var p=Join(Read([],[Mem(8,0xA,100),Mem(8,0xB,200)],[Mem(8,0xA,5,ready:false)]),S(8)).Single();
        Assert.Equal(300,p.GpuDedicatedBytes);Assert.Null(p.GpuSharedBytes);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);}
    // GpuCounterReader gives every not-ready item a value of 0; only its Ready flag says the value is unknown.
    [Fact] public void A_not_ready_item_carrying_the_readers_zero_is_never_counted_as_zero(){
        var ps=Join(Read([En(8,0xA,0,0,ready:false),En(9,0xA,0,0)],[Mem(8,0xA,0,ready:false),Mem(8,0xB,50),Mem(9,0xA,0)],[Mem(8,0xA,0,ready:false),Mem(9,0xA,0)]),S(8),S(9));
        Assert.Null(ps[0].GpuPercent);Assert.Null(ps[0].GpuEngine);Assert.Null(ps[0].GpuEngines);Assert.Null(ps[0].GpuDedicatedBytes);Assert.Null(ps[0].GpuSharedBytes);Assert.Equal(ProcessGap.FirstSample,ps[0].GpuGap);
        Assert.Null(ps[0].Deltas!.GpuSeconds);Assert.Null(ps[0].Deltas!.GpuMemoryByteSeconds);
        Assert.Equal(0,ps[1].GpuPercent);Assert.Equal(0,ps[1].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuSharedBytes);Assert.Equal(ProcessGap.None,ps[1].GpuGap);}
    [Fact] public void A_process_without_instances_or_with_idle_engines_is_a_known_zero(){ var ps=Join(Read([En(9,0xA,0,0)]),S(8),S(9));
        Assert.All(ps,p=>{Assert.Equal(0,p.GpuPercent);Assert.Equal("",p.GpuEngine);Assert.Null(p.GpuAdapter);Assert.Empty(p.GpuEngines!);Assert.Equal(0,p.GpuDedicatedBytes);Assert.Equal(0,p.GpuSharedBytes);Assert.Equal(ProcessGap.None,p.GpuGap);});}
    [Fact] public void Engine_values_are_clamped(){ var p=Join(Read([En(8,0xA,0,104.5),En(8,0xA,1,-3,"Copy")]),S(8)).Single();Assert.Equal(100,p.GpuPercent);Assert.Single(p.GpuEngines!);}
    [Fact] public void At_most_32_engines_are_published_busiest_first(){ var p=Join(Read([..Enumerable.Range(0,40).Select(i=>En(8,0xA,i,i+1))]),S(8)).Single();
        Assert.Equal(32,p.GpuEngines!.Count);Assert.Equal(40,p.GpuEngines[0].Percent);Assert.Equal(9,p.GpuEngines[^1].Percent);}
    [Fact] public void A_process_created_at_or_after_the_previous_collection_less_one_second_is_a_first_sample(){
        var ps=Join(Read([],[Mem(8,0xA,100)]),S(8,gen:Prev.AddSeconds(-0.5).UtcTicks),S(9,gen:Prev.AddSeconds(-1.5).UtcTicks),S(10,gen:Prev.AddSeconds(-1).UtcTicks));
        Assert.Null(ps[0].GpuPercent);Assert.Equal(ProcessGap.FirstSample,ps[0].GpuGap);Assert.Equal(100,ps[0].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuPercent);   // before the known-zero rule
        Assert.Null(ps[2].GpuPercent);Assert.Equal(ProcessGap.FirstSample,ps[2].GpuGap);}   // exactly previous − 1 s: "at or after"
    [Fact] public void Without_a_previous_collection_gpu_percent_is_unknown_and_memory_is_published(){ var ps=Join(Read([En(8,0xA,0,50)],[Mem(8,0xA,100)],previous:null,interval:null),S(8),S(9));
        Assert.All(ps,p=>{Assert.Null(p.GpuPercent);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});Assert.Equal(100,ps[0].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuDedicatedBytes);
        // Memory is shown, but a read without a previous collection adds no GPU time or GPU memory to history.
        Assert.All(ps,p=>{Assert.Null(p.Deltas!.GpuSeconds);Assert.Null(p.Deltas!.GpuMemoryByteSeconds);});}
    [Fact] public void A_not_ready_engine_counter_is_a_first_sample_for_every_process(){ var ps=Join(Read([],[Mem(8,0xA,7)]) with{EnginesReady=false},S(8),S(9));
        // PID 9 has no engine item, so without the counter flag it would be a known 0 %.
        Assert.All(ps,p=>{Assert.Null(p.GpuPercent);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});Assert.Equal(7,ps[0].GpuDedicatedBytes);Assert.Equal(0,ps[1].GpuDedicatedBytes);}
    [Fact] public void A_not_ready_memory_counter_is_a_first_sample_for_every_process(){ var r=Read([En(8,0xA,0,30)],[Mem(8,0xA,7)],[Mem(8,0xA,3)]);
        // PID 9 has no memory item, so without the counter flag it would be a known 0 B.
        var dedicated=GpuProcessJoin.Join([S(8),S(9)],r with{DedicatedReady=false}).Processes;
        Assert.All(dedicated,p=>{Assert.Null(p.GpuDedicatedBytes);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});Assert.Equal(3,dedicated[0].GpuSharedBytes);Assert.Equal(0,dedicated[1].GpuSharedBytes);Assert.Equal(30,dedicated[0].GpuPercent);
        var shared=GpuProcessJoin.Join([S(8),S(9)],r with{SharedReady=false}).Processes;
        Assert.All(shared,p=>{Assert.Null(p.GpuSharedBytes);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);});Assert.Equal(7,shared[0].GpuDedicatedBytes);Assert.Equal(0,shared[1].GpuDedicatedBytes);}
    [Fact] public void A_null_or_unavailable_read_leaves_every_value_unknown(){ var none=GpuProcessJoin.Join([S(8)],null);Assert.Equal(ProcessGap.FirstSample,none.Processes[0].GpuGap);Assert.True(none.State.Available);Assert.Null(none.IntervalSeconds);
        Assert.Null(none.Processes[0].GpuPercent);Assert.Null(none.Processes[0].GpuDedicatedBytes);Assert.Null(none.Processes[0].GpuSharedBytes);Assert.Null(none.Processes[0].GpuEngines);
        var off=GpuProcessJoin.Join([S(8)],GpuCounterRead.Unavailable("GPU counters unreadable",1,T(1)));var p=off.Processes[0];
        Assert.Equal("GPU counters unreadable",off.State.Status);Assert.Null(p.GpuPercent);Assert.Null(p.GpuDedicatedBytes);Assert.Equal(ProcessGap.None,p.GpuGap);}
    [Fact] public void Gpu_deltas_are_percent_and_bytes_times_the_interval(){ var r=Read([En(8,0xA,0,40)],[Mem(8,0xA,300)],interval:2);var p=GpuProcessJoin.Join([S(8)],r).Processes[0];
        Assert.Equal(0.8,p.Deltas!.GpuSeconds!.Value,9);Assert.Equal(600,p.Deltas.GpuMemoryByteSeconds!.Value,9);Assert.Equal(2,GpuProcessJoin.IntervalSeconds(r));
        // A read joined again for display only shows its values and adds nothing to history, GPU memory included.
        var display=GpuProcessJoin.Join([S(8)],r with{Credit=false});Assert.Null(display.IntervalSeconds);Assert.Null(display.Processes[0].Deltas!.GpuSeconds);Assert.Equal(40,display.Processes[0].GpuPercent);
        Assert.Null(display.Processes[0].Deltas!.GpuMemoryByteSeconds);Assert.Equal(300,display.Processes[0].GpuDedicatedBytes);
        Assert.Null(GpuProcessJoin.Join([S(8) with{Deltas=null}],r).Processes[0].Deltas);
        Assert.Null(GpuProcessJoin.IntervalSeconds(r with{State=new(false,GpuCounterStatus.Unavailable)}));}
    [Fact] public void Gpu_deltas_add_with_the_cpu_rule(){ var a=new ProcessDeltas(1,1,1,1,1);Assert.Null(a.Add(a).GpuSeconds);Assert.Equal(0.5,a.Add(a with{GpuSeconds=0.5}).GpuSeconds);
        Assert.Null(a.Add(a).GpuMemoryByteSeconds);Assert.Equal(7,a.Add(a with{GpuMemoryByteSeconds=7}).GpuMemoryByteSeconds);
        Assert.Equal(0,ProcessDeltas.Zero.GpuSeconds);Assert.Equal(0,ProcessDeltas.Zero.GpuMemoryByteSeconds);}
}
