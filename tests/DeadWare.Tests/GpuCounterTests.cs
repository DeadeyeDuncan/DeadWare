using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class GpuCounterTests
{
    static readonly DateTimeOffset Epoch=new(2026,1,1,0,0,0,TimeSpan.Zero);
    sealed class FakeGpuApi:IGpuCounterApi
    {
        public uint OpenStatus,CollectStatus;
        public Dictionary<GpuCounterKind,GpuRawArray> Arrays{get;}=new(){[GpuCounterKind.Engine]=new(0,[]),[GpuCounterKind.Dedicated]=new(0,[]),[GpuCounterKind.Shared]=new(0,[])};
        public IReadOnlyList<GpuAdapterInfo>? Adapters{get;set;}=[new(0xA,"NVIDIA GeForce RTX 4070",0)];
        public int Opens,Closes,Collects,AdapterReads;
        public string Throws="";
        void MaybeThrow(string call){if(Throws==call)throw new InvalidOperationException(call);}
        public uint Open(){Opens++;MaybeThrow("Open");return OpenStatus;}
        public uint Collect(){Collects++;MaybeThrow("Collect");return CollectStatus;}
        public GpuRawArray Read(GpuCounterKind kind){MaybeThrow("Read");return Arrays[kind];}
        IReadOnlyList<GpuAdapterInfo>? IGpuCounterApi.Adapters(){AdapterReads++;MaybeThrow("Adapters");return Adapters;}
        public void Close()=>Closes++;
    }
    readonly FakeGpuApi api=new();
    readonly GpuCounterReader reader;
    public GpuCounterTests()=>reader=new(api);
    static GpuRawItem E(int pid,int eng,double value,uint cstatus=0,long luid=0xA,int phys=0,string type="3D")=>new($"pid_{pid}_luid_0x{luid>>32:X8}_0x{luid&0xFFFFFFFF:X8}_phys_{phys}_eng_{eng}_engtype_{type}",cstatus,value);
    static GpuRawItem M(int pid,double bytes,uint cstatus=0,long luid=0xA,int phys=0)=>new($"pid_{pid}_luid_0x{luid>>32:X8}_0x{luid&0xFFFFFFFF:X8}_phys_{phys}",cstatus,bytes);
    static GpuRawItem Bad(int n)=>new($"bad_{n}",0,1);
    static DateTimeOffset T(double s)=>Epoch.AddSeconds(s);
    GpuCounterRead R(double s,double interval=1)=>reader.Read(s,T(s),interval);
    // Reads at 0 s and 1 s, so the second read has a previous collection, and joins PID 8, started well before both.
    (GpuCounterRead Read,ProcessSample P8) JoinSecondRead(){ R(0);var r=R(1);
        return (r,GpuProcessJoin.Join([new ProcessSample(8,Epoch.AddSeconds(-60).UtcTicks,"p8.exe",1,0,0,0,0,ProcessGap.None,new ProcessDeltas(0,0,0,0,1))],r).Processes[0]);}
    const double MaximumBytes=1125899906842624d;   // 2^50

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D3A2_phys_0_eng_3_engtype_3D",1234,0xD3A2L,0,3,"3D")]
    [InlineData("pid_8_luid_0x1_0xFFFFFFFF_phys_1_eng_12_engtype_Compute_0",8,0x1FFFFFFFFL,1,12,"Compute_0")]
    [InlineData("pid_8_luid_0x0_0xa_phys_0_eng_0_engtype_High Priority 3D",8,0xAL,0,0,"High Priority 3D")]
    [InlineData("pid_8_luid_0x0_0xA_phys_0_eng_2_engtype_Graphics_1",8,0xAL,0,2,"Graphics_1")]
    [InlineData("pid_8_luid_0x0_0xA_phys_0_eng_0_engtype_",8,0xAL,0,0,"")]
    public void Engine_names_parse(string name,int pid,long luid,int phys,int engine,string type){
        Assert.True(GpuCounterInstance.TryParse(name,GpuCounterKind.Engine,out var i));Assert.Equal(new GpuCounterInstance(pid,luid,phys,engine,type),i);}
    [Fact] public void Memory_names_parse(){ Assert.True(GpuCounterInstance.TryParse("pid_1234_luid_0x00000000_0x0000D3A2_phys_1",GpuCounterKind.Dedicated,out var i));
        Assert.Equal(new GpuCounterInstance(1234,0xD3A2,1,null,null),i);}
    [Theory][InlineData(null)][InlineData("")][InlineData("pid_x_luid_0x0_0xA_phys_0")][InlineData("pid_1_luid_0x123456789_0xA_phys_0")][InlineData("pid_1_luid_0x_0xA_phys_0")]
    [InlineData("pid_1_luid_0x0_0xG_phys_0")][InlineData("pid_1_luid_0x0_0xA_phys_0_eng_1")][InlineData("pid_1_luid_0x0_0xA_phys_0_tail")][InlineData("pid_99999999999_luid_0x0_0xA_phys_0")]
    [InlineData("pid_+8_luid_0x0_0xA_phys_0")][InlineData("pid_ 8_luid_0x0_0xA_phys_0")][InlineData("pid_8_luid_0x000000000_0xA_phys_0")][InlineData("pid_8_luid_0x 1_0xA_phys_0")]
    public void Malformed_names_do_not_parse(string? name){ Assert.False(GpuCounterInstance.TryParse(name,GpuCounterKind.Engine,out _));Assert.False(GpuCounterInstance.TryParse(name,GpuCounterKind.Shared,out _));}
    [Fact] public void Engine_and_memory_shapes_do_not_cross(){ Assert.False(GpuCounterInstance.TryParse("pid_8_luid_0x0_0xA_phys_0",GpuCounterKind.Engine,out _));
        Assert.False(GpuCounterInstance.TryParse("pid_8_luid_0x0_0xA_phys_0_eng_0_engtype_3D",GpuCounterKind.Dedicated,out _));}

    [Fact] public void Items_are_ready_only_with_valid_or_new_data_and_never_skipped(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,40),E(8,1,10,cstatus:GpuPdhStatus.CStatusNewData),E(9,0,99,cstatus:0x800007D3)]);
        var r=R(0);Assert.Equal([true,true,false],r.Engines.Select(e=>e.Ready));Assert.Equal(0,r.Skipped);Assert.Equal(3,r.Instances);Assert.True(r.State.Available);}
    // -1 is what 0xFFFF… reads as under PDH_FMT_LARGE, and 2^62 overflows as byte-seconds: either once stopped all app history.
    [Theory][InlineData(GpuCounterKind.Dedicated,-1d)][InlineData(GpuCounterKind.Dedicated,4611686018427387904d)][InlineData(GpuCounterKind.Dedicated,double.NaN)][InlineData(GpuCounterKind.Dedicated,double.PositiveInfinity)]
    [InlineData(GpuCounterKind.Shared,-1d)][InlineData(GpuCounterKind.Shared,4611686018427387904d)][InlineData(GpuCounterKind.Shared,double.NaN)][InlineData(GpuCounterKind.Shared,double.PositiveInfinity)]
    public void A_negative_huge_or_non_finite_memory_value_is_not_ready(GpuCounterKind kind,double bytes){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);
        api.Arrays[kind]=new(0,[M(8,bytes),M(9,MaximumBytes),M(9,0,phys:1)]);
        var (r,p)=JoinSecondRead();var items=kind==GpuCounterKind.Dedicated?r.Dedicated:r.Shared;
        // 2^50 and 0 bytes are the bounds, and both are ready.
        Assert.Equal([false,true,true],items.Select(m=>m.Ready));Assert.Equal([0L,(long)MaximumBytes,0L],items.Select(m=>m.Bytes));Assert.True(r.State.Available);Assert.Equal(0,r.Skipped);
        // The join makes that memory unknown, a first sample, with no byte-seconds; GPU % stays known.
        Assert.Null(kind==GpuCounterKind.Dedicated?p.GpuDedicatedBytes:p.GpuSharedBytes);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);Assert.Equal(5,p.GpuPercent);
        if(kind==GpuCounterKind.Dedicated)Assert.Null(p.Deltas!.GpuMemoryByteSeconds);}
    [Fact] public void A_nan_engine_beside_a_ready_engine_is_ignored(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,double.NaN),E(8,1,20,type:"Copy")]);
        var (r,p)=JoinSecondRead();Assert.Equal([false,true],r.Engines.Select(e=>e.Ready));
        Assert.Equal(20,p.GpuPercent);Assert.Equal("GPU 0 · Copy",p.GpuEngine);Assert.Equal(ProcessGap.None,p.GpuGap);}
    // A lone NaN once read as a known 0 %, and +∞ as 100 %, a full GPU-second per second.
    [Theory][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)]
    public void A_lone_non_finite_engine_is_unknown_never_a_value(double value){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,value)]);
        var (r,p)=JoinSecondRead();var item=Assert.Single(r.Engines);Assert.False(item.Ready);Assert.Equal(0,item.Percent);
        Assert.Null(p.GpuPercent);Assert.Null(p.GpuEngines);Assert.Equal(ProcessGap.FirstSample,p.GpuGap);Assert.Null(p.Deltas!.GpuSeconds);}
    [Fact] public void The_first_read_after_open_has_no_previous_collection(){ var first=R(10);Assert.True(reader.LastReadOpened);Assert.Null(first.PreviousUtcTime);Assert.Null(first.IntervalSeconds);
        var second=R(11.5);Assert.False(reader.LastReadOpened);Assert.Equal(T(10),second.PreviousUtcTime);Assert.Equal(1.5,second.IntervalSeconds!.Value,6);}
    [Theory][InlineData(4.01)][InlineData(0.0)][InlineData(-1.0)]
    public void An_interval_outside_freshness_has_no_previous_collection(double dt){ R(10);var r=R(10+dt);Assert.Null(r.PreviousUtcTime);Assert.Null(r.IntervalSeconds);}   // 1 s polling: 4 s
    [Fact] public void A_slower_previous_interval_keeps_its_freshness_limit(){ R(0,interval:10);var r=R(10,interval:0.5);   // FreshnessSeconds(Math.Max(10,0.5)) = 13 s, as the tracker
        Assert.Equal(T(0),r.PreviousUtcTime);Assert.Equal(10,r.IntervalSeconds!.Value,6);Assert.Null(R(20,interval:0.5).IntervalSeconds);}   // then 0.5 s on both sides: 4 s
    [Theory][InlineData(GpuPdhStatus.InvalidData)][InlineData(GpuPdhStatus.CStatusInvalidData)][InlineData(GpuPdhStatus.CalcNegativeDenominator)][InlineData(GpuPdhStatus.CalcNegativeValue)]
    public void A_benign_array_status_is_not_ready_and_fails_only_after_60_s(uint status){ api.Arrays[GpuCounterKind.Engine]=new(status,[]);
        foreach(double s in new[]{0,30,59.9}){var r=R(s);Assert.True(r.State.Available);Assert.False(r.EnginesReady);Assert.Equal(0,r.Skipped);}
        Assert.Equal(new ProcessColumnState(false,$"GPU counters unavailable (error 0x{status:X8})"),R(60).State);Assert.Equal(1,api.Closes);
        api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);R(119.9);Assert.Equal(1,api.Opens);
        var reopened=R(120);Assert.Equal(2,api.Opens);Assert.True(reopened.State.Available);Assert.Null(reopened.PreviousUtcTime);}
    [Fact] public void A_ready_read_ends_the_not_ready_run(){ api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.InvalidData,[]);R(0);R(50);
        api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);R(55);api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.InvalidData,[]);R(56);Assert.True(R(114).State.Available);Assert.Equal(0,api.Closes);}
    [Theory][InlineData(GpuPdhStatus.NoData)][InlineData(GpuPdhStatus.CStatusNoInstance)][InlineData(0u)]
    public void No_engine_instances_make_the_column_unavailable_and_keep_the_query_open(uint status){ api.Arrays[GpuCounterKind.Engine]=new(status,[]);api.Arrays[GpuCounterKind.Dedicated]=new(0,[M(8,100)]);
        Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),R(0).State);Assert.True(reader.IsOpen);Assert.Equal(0,api.Closes);}
    [Theory][InlineData(GpuPdhStatus.NoData)][InlineData(GpuPdhStatus.CStatusNoInstance)]
    public void No_instances_from_the_collect_call_keep_the_query_open_and_the_previous_collection(uint status){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);
        Assert.True(R(0).State.Available);api.CollectStatus=status;Assert.Equal("GPU counters unavailable",R(1).State.Status);Assert.True(reader.IsOpen);Assert.Equal(0,api.Closes);
        api.CollectStatus=0;var r=R(2);Assert.True(r.State.Available);Assert.Equal(T(0),r.PreviousUtcTime);}
    [Fact] public void No_memory_instances_are_known_empty(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);api.Arrays[GpuCounterKind.Dedicated]=new(GpuPdhStatus.NoData,[]);
        var r=R(0);Assert.True(r.State.Available);Assert.Empty(r.Dedicated);Assert.True(r.DedicatedReady);}
    [Fact] public void More_than_half_skipped_is_unreadable_for_that_read(){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5),Bad(1)]);api.Arrays[GpuCounterKind.Shared]=new(0,[M(8,1),Bad(2),Bad(3)]);
        var r=R(0);Assert.Equal(new ProcessColumnState(false,"GPU counters unreadable"),r.State);Assert.Equal(3,r.Skipped);Assert.Equal(5,r.Instances);
        api.Arrays[GpuCounterKind.Shared]=new(0,[M(8,1)]);Assert.True(R(1).State.Available);}
    [Fact] public void More_than_65536_items_is_too_many_for_that_read(){ api.Arrays[GpuCounterKind.Engine]=new(0,[..Enumerable.Range(0,65537).Select(i=>E(8,i,1))]);
        Assert.Equal(new ProcessColumnState(false,"Too many GPU counter instances"),R(0).State);Assert.True(reader.IsOpen);
        api.Arrays[GpuCounterKind.Engine]=new(0,[..Enumerable.Range(0,65536).Select(i=>E(8,i,1))]);Assert.True(R(1).State.Available);}
    [Theory][InlineData(GpuPdhStatus.CStatusNoObject,"GPU counters unavailable")][InlineData(GpuPdhStatus.CStatusNoCounter,"GPU counters unavailable")][InlineData(0xC0000BBCu,"GPU counters unavailable (error 0xC0000BBC)")]
    public void An_open_failure_is_retried_at_most_once_a_minute(uint status,string text){ api.OpenStatus=status;Assert.Equal(new ProcessColumnState(false,text),R(0).State);Assert.Equal(api.Opens,api.Closes);
        Assert.Equal(text,R(59.9).State.Status);Assert.Equal(1,api.Opens);api.OpenStatus=0;api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);Assert.True(R(60).State.Available);Assert.Equal(2,api.Opens);}
    [Theory][InlineData("Open")][InlineData("Collect")][InlineData("Read")]
    public void An_api_exception_is_a_failure_retried_at_most_once_a_minute(string call){ api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);api.Throws=call;
        Assert.Equal(new ProcessColumnState(false,"GPU counters unavailable"),R(0).State);Assert.False(reader.IsOpen);Assert.Equal(api.Opens,api.Closes);
        R(30);Assert.Equal(1,api.Opens);api.Throws="";R(59.9);Assert.Equal(1,api.Opens);var r=R(60);Assert.Equal(2,api.Opens);Assert.True(r.State.Available);Assert.Null(r.PreviousUtcTime);}
    [Fact] public void A_throwing_dxgi_read_is_a_dxgi_failure(){ api.Throws="Adapters";api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,5)]);var r=R(0);Assert.True(r.State.Available);Assert.Empty(r.Adapters);Assert.True(reader.IsOpen);}
    [Fact] public void A_more_data_status_left_after_sizing_is_a_failure(){ api.Arrays[GpuCounterKind.Engine]=new(GpuPdhStatus.MoreData,[]);
        Assert.Equal("GPU counters unavailable (error 0x800007D2)",R(0).State.Status);Assert.False(reader.IsOpen);}
    [Fact] public void A_collect_failure_closes_and_reopens_at_most_once_a_minute(){ R(0);api.CollectStatus=0xC0000BBC;Assert.Equal("GPU counters unavailable (error 0xC0000BBC)",R(1).State.Status);Assert.Equal(1,api.Closes);
        api.CollectStatus=0;R(60.9);Assert.Equal(1,api.Opens);var r=R(61);Assert.Equal(2,api.Opens);Assert.Null(r.PreviousUtcTime);}
    [Fact] public void Any_other_array_status_is_a_failure(){ api.Arrays[GpuCounterKind.Shared]=new(0xC0000BBD,[]);Assert.Equal("GPU counters unavailable (error 0xC0000BBD)",R(0).State.Status);Assert.False(reader.IsOpen);}
    [Fact] public void Engines_that_appear_later_are_read_without_reopening(){   // Review Focus 3
        Assert.Equal("GPU counters unavailable",R(0).State.Status);Assert.Equal("GPU counters unavailable",R(1).State.Status);
        api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,12)]);var r=R(2);Assert.True(r.State.Available);Assert.Equal(12,Assert.Single(r.Engines).Percent);Assert.Equal(1,api.Opens);Assert.Equal(0,api.Closes);}
    [Fact] public void Hardware_adapters_are_numbered_in_enumeration_order_and_the_rest_are_not(){
        api.Adapters=[new(0xA,"NVIDIA GeForce RTX 4070",0),new(0xB,"Microsoft Basic Render Driver",GpuAdapterInfo.SoftwareFlag),new(0xC,"Intel(R) UHD Graphics 770",0)];
        api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10,luid:0xA),E(8,0,10,luid:0xB),E(8,0,10,luid:0xC),E(8,0,10,luid:0xD)]);var r=R(0);
        Assert.Equal(new GpuAdapterName(0,"NVIDIA GeForce RTX 4070"),r.Adapters[0xA]);Assert.Equal(new GpuAdapterName(1,"Intel(R) UHD Graphics 770"),r.Adapters[0xC]);
        Assert.Equal(new GpuAdapterName(null,"Microsoft Basic Render Driver"),r.Adapters[0xB]);Assert.False(r.Adapters.ContainsKey(0xD));Assert.Equal(4,r.Engines.Count);
        Assert.Equal("GPU 1 · 3D",r.Adapters[0xC].Label("3D"));Assert.Equal("GPU ? · Engine",GpuAdapterName.Unknown.Label(""));}
    [Fact] public void A_dxgi_failure_or_an_unknown_luid_rereads_adapters_at_most_once_a_minute(){ api.Adapters=null;api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10)]);
        var r=R(0);Assert.Empty(r.Adapters);Assert.Single(r.Engines);R(30);Assert.Equal(1,api.AdapterReads);
        api.Adapters=[new(0xA,"NVIDIA GeForce RTX 4070",0)];Assert.Equal(0,R(60).Adapters[0xA].Number);Assert.Equal(2,api.AdapterReads);
        api.Arrays[GpuCounterKind.Engine]=new(0,[E(8,0,10,luid:0xE)]);R(61);Assert.Equal(2,api.AdapterReads);R(120);Assert.Equal(3,api.AdapterReads);}
    [Fact] public void One_slow_read_in_twenty_keeps_every_poll_and_a_second_moves_the_read(){ var g=new GpuPlacementGuard();
        for(int i=0;i<19;i++)g.Record(2,false);g.Record(40,false);Assert.False(g.OwnLoop);g.Record(15,false);Assert.False(g.OwnLoop);g.Record(15.01,false);Assert.True(g.OwnLoop);}
    [Fact] public void The_first_read_after_open_is_not_counted(){ var g=new GpuPlacementGuard();g.Record(500,true);g.Record(500,true);g.Record(500,false);Assert.False(g.OwnLoop);}
    [Fact] public void A_slow_read_leaves_the_window_after_twenty_newer(){ var g=new GpuPlacementGuard();g.Record(40,false);for(int i=0;i<20;i++)g.Record(1,false);g.Record(40,false);Assert.False(g.OwnLoop);}
    [Fact] public void The_loop_placement_holds_until_reset(){ var g=new GpuPlacementGuard();g.Record(20,false);g.Record(20,false);for(int i=0;i<100;i++)g.Record(1,false);Assert.True(g.OwnLoop);
        g.Reset();Assert.False(g.OwnLoop);g.Record(20,false);Assert.False(g.OwnLoop);}
}
