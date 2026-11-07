using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessMinuteFolderTests
{
    static readonly DateTimeOffset T0=new(2026,11,5,10,0,0,TimeSpan.Zero);
    // gpu: null leaves GpuEngines null (unknown), 0 gives [], otherwise one engine of the given type at gpu %. gpuMem is the dedicated GPU bytes.
    // dGpu and dGpuMem are the sample's own Deltas.GpuSeconds and GpuMemoryByteSeconds.
    static ProcessSample P(string name,long cpu=0,long mem=0,long read=0,long write=0,int pid=1,double dt=1,double? gpu=null,long? gpuMem=null,string gpuType="3D",double? dGpu=null,double? dGpuMem=null)
        =>new(pid,1,name,1,null,mem,null,null,ProcessGap.None,new ProcessDeltas(cpu,read,write,mem*dt,dt){GpuSeconds=dGpu,GpuMemoryByteSeconds=dGpuMem})
        {GpuEngines=gpu is null?null:gpu==0?[]:[Engine(gpuType,gpuType=="3D"?0:1,gpu.Value)],GpuPercent=gpu,GpuDedicatedBytes=gpuMem};
    static GpuEngineUse Engine(string type,int eng,double percent)=>new(0xA,0,eng,type,$"GPU 0 · {type}",null,percent);
    static ProcessActivity A(double t,IReadOnlyList<ProcessSample> ps,ProcessOmitted? omitted=null,double? dt=1,ProcessActivityKind kind=ProcessActivityKind.Available,double wallOffset=0,double interval=1,double? gpuInterval=null)
        =>new(kind,(long)t,T0.AddSeconds(t+wallOffset),t,interval,8,ps,omitted??ProcessOmitted.None,ps.Count,dt){GpuIntervalSeconds=gpuInterval};
    static ProcessActivity A(double t,params ProcessSample[] ps)=>A(t,(IReadOnlyList<ProcessSample>)ps);
    // Folds one activity at t=0 and closes it with one in the next minute.
    static ProcessMinuteBatch Close(params ProcessSample[] ps){var f=new ProcessMinuteFolder();f.Observe(A(0,ps));return Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));}

    static ProcessAppMinute App(ProcessMinuteBatch b,string name)=>b.Applications.Single(a=>a.Name==name);
    // Folds one activity with a GPU interval of 1 at t=0 and closes it with one in the next minute.
    static ProcessMinuteBatch CloseGpu(params ProcessSample[] ps){var f=new ProcessMinuteFolder();f.Observe(A(0,[..ps],gpuInterval:1));return Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));}
    // Folds 60 one-second activities with a GPU interval of 1 (t=0..59) and closes them with one in the next minute.
    static ProcessMinuteBatch CloseMinute(IReadOnlyList<ProcessSample> ps,ProcessOmitted? omitted=null){var f=new ProcessMinuteFolder();for(int t=0;t<60;t++)f.Observe(A(t,ps,omitted,gpuInterval:1));return Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));}

    [Fact] public void Minute_keeps_the_union_of_three_top_tens_at_most_thirty()
    {
        var list=new List<ProcessSample>();
        for(int i=0;i<10;i++){list.Add(P($"cpu{i}.exe",cpu:1000+i));list.Add(P($"mem{i}.exe",mem:1_000_000+i));list.Add(P($"io{i}.exe",read:500+i,write:1));}
        long smallCpu=0;
        for(int i=0;i<10;i++){list.Add(P($"small{i}.exe",cpu:i+1,mem:i+1,read:i+1));smallCpu+=i+1;}
        var b=Close(list.ToArray());
        Assert.Equal(31,b.Applications.Count);
        Assert.Single(b.Applications,a=>a.EverythingElse);
        Assert.Equal(smallCpu,b.Applications.Single(a=>a.EverythingElse).CpuTime);
        Assert.DoesNotContain(b.Applications,a=>a.Name=="small0.exe");
    }
    [Fact] public void Overlapping_leaders_list_fewer_than_thirty()
    {
        var list=new List<ProcessSample>();
        for(int i=0;i<10;i++)list.Add(P($"lead{i}.exe",cpu:1000+i,mem:1_000_000+i,read:500+i));
        for(int i=0;i<10;i++)list.Add(P($"small{i}.exe",cpu:i+1,mem:i+1,read:i+1));
        Assert.Equal(11,Close(list.ToArray()).Applications.Count);
    }
    [Fact] public void Case_variants_fold_into_one_app_with_the_first_casing()
    {
        var b=Close(P("Explorer.EXE",cpu:5,pid:1),P("explorer.exe",cpu:7,pid:2));
        Assert.Single(b.Applications,a=>a.Name=="Explorer.EXE");
        Assert.DoesNotContain(b.Applications,a=>a.Name=="explorer.exe");
        Assert.Equal(12,b.Applications.Single(a=>a.Name=="Explorer.EXE").CpuTime);
    }
    [Fact] public void Empty_names_and_omitted_sums_go_to_everything_else()
    {
        var f=new ProcessMinuteFolder();
        f.Observe(A(0,[P("",cpu:3),P("a.exe",cpu:100)],new ProcessOmitted(2,0,new ProcessDeltas(40,null,null,0,1))));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));
        Assert.Equal(43,b.Applications.Single(a=>a.EverythingElse).CpuTime);
        Assert.Equal(["a.exe",ProcessHistory.EverythingElse],b.Applications.Select(a=>a.Name));
    }
    [Fact] public void Zero_values_never_qualify_and_ties_break_by_name()
    {
        var b=Close(P("idle.exe"),P("B.EXE",cpu:5),P("A.EXE",cpu:5));
        Assert.DoesNotContain(b.Applications,a=>a.Name=="idle.exe");
        Assert.Equal(["A.EXE","B.EXE"],b.Applications.Where(a=>!a.EverythingElse).Select(a=>a.Name));
    }
    [Fact] public void Memory_is_ranked_by_byte_seconds_and_cut_at_ten()
    {
        var f=new ProcessMinuteFolder();
        var list=Enumerable.Range(1,11).Select(i=>P($"m{i}.exe",mem:i*100)).ToArray();
        f.Observe(A(0,list));f.Observe(A(2,Enumerable.Range(1,11).Select(i=>P($"m{i}.exe",mem:i*100,dt:2)).ToArray(),dt:2));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));
        Assert.Equal(3,b.CoveredSeconds,6);
        Assert.DoesNotContain(b.Applications,a=>a.Name=="m1.exe");
        Assert.Equal(1100*3,b.Applications.Single(a=>a.Name=="m11.exe").MemoryByteSeconds,6);
        Assert.Equal(100*3,b.Applications.Single(a=>a.EverythingElse).MemoryByteSeconds,6);
    }
    [Fact] public void Covered_seconds_sum_only_activities_with_deltas()
    {
        var f=new ProcessMinuteFolder();
        f.Observe(A(0,[P("a.exe",cpu:1)],dt:null));
        for(int i=1;i<=58;i++)f.Observe(A(i,P("a.exe",cpu:1)));
        f.Observe(A(59,[P("a.exe",cpu:1)],dt:null));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));
        Assert.Equal(58,b.CoveredSeconds,6);
        Assert.Equal(58,b.Applications.Single(a=>a.Name=="a.exe").CpuTime);
    }
    [Fact] public void Null_io_deltas_add_nothing()
    {
        var f=new ProcessMinuteFolder();
        f.Observe(A(0,[new ProcessSample(1,1,"a.exe",1,null,0,null,null,ProcessGap.None,new ProcessDeltas(5,null,null,0,1)),new ProcessSample(2,1,"a.exe",1,null,0,null,null,ProcessGap.None,null)]));
        var a=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe")))).Applications.Single(x=>x.Name=="a.exe");
        Assert.Equal((5,0,0),(a.CpuTime,a.IoRead,a.IoWrite));
    }
    [Fact] public void Minute_with_zero_covered_time_is_skipped()
    {
        var f=new ProcessMinuteFolder();
        f.Observe(A(0,[P("a.exe",cpu:1)],dt:null));
        f.Observe(A(1,[P("a.exe",cpu:1)],ProcessOmitted.None,0));
        Assert.Null(f.Observe(A(60,P("z.exe"))));
    }
    [Fact] public void Clock_jump_breaks_the_segment_and_marks_the_minute_partial()
    {
        // 5 s polling: 12 publications (11 needed), no monotonic gap, wall stays inside 10:00. Only wall/monotonic drift can break it.
        var f=new ProcessMinuteFolder();
        ProcessActivity At(int t,double wall)=>A(t,[P("a.exe",cpu:1,dt:5)],dt:5,wallOffset:wall,interval:5);
        for(int t=0;t<10;t+=5)f.Observe(At(t,0));
        for(int t=10;t<60;t+=5)f.Observe(At(t,4));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Observe(At(60,4)));
        Assert.Equal(60,b.CoveredSeconds,6);
        Assert.False(b.Complete);
        var next=Assert.IsType<ProcessMinuteBatch>(f.Flush());
        Assert.NotEqual(b.Segment,next.Segment);
    }
    [Fact] public void Gaps_and_explicit_breaks_split_segments()
    {
        var f=new ProcessMinuteFolder();
        f.Observe(A(0,P("a.exe",cpu:1)));
        f.Observe(A(20,P("a.exe",cpu:1)));
        var first=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("a.exe",cpu:1))));
        Assert.False(first.Complete);
        f.Break();
        var second=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(120,P("a.exe",cpu:1))));
        Assert.False(second.Complete);
        Assert.NotEqual(first.Segment,second.Segment);
    }
    [Fact] public void Full_minute_is_complete_and_short_minute_is_not()
    {
        var f=new ProcessMinuteFolder();
        for(int i=0;i<60;i++)f.Observe(A(i,P("a.exe",cpu:1)));
        Assert.True(Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("a.exe",cpu:1)))).Complete);
        for(int i=61;i<100;i++)f.Observe(A(i,P("a.exe",cpu:1)));
        Assert.False(Assert.IsType<ProcessMinuteBatch>(f.Flush()).Complete);
    }
    [Fact] public void Off_and_unavailable_activities_add_nothing()
    {
        var f=new ProcessMinuteFolder();
        Assert.Null(f.Observe(A(0,[P("a.exe",cpu:9)],kind:ProcessActivityKind.Off)));
        Assert.Null(f.Observe(A(1,[P("a.exe",cpu:9)],kind:ProcessActivityKind.Unavailable)));
        Assert.Null(f.Flush());
        f.Observe(A(2,P("b.exe",cpu:1)));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Flush());
        Assert.Equal(1,b.CoveredSeconds);
        Assert.DoesNotContain(b.Applications,a=>a.Name=="a.exe");
    }
    [Fact] public void Eleven_apps_tied_on_cpu_send_the_last_key_to_everything_else()
    {
        var b=Close(Enumerable.Range(0,11).Select(i=>P($"app{i:00}.exe",cpu:5)).ToArray());
        Assert.DoesNotContain(b.Applications,a=>a.Name=="app10.exe");
        Assert.Equal(10,b.Applications.Count(a=>!a.EverythingElse));
        Assert.Equal(5,b.Applications.Single(a=>a.EverythingElse).CpuTime);
    }
    [Fact] public void Write_volume_alone_can_qualify_for_the_io_top_ten()
    {
        var list=Enumerable.Range(1,10).Select(i=>P($"r{i}.exe",read:1000+i)).Append(P("w.exe",write:5000)).Append(P("low.exe",read:1)).ToArray();
        var b=Close(list);
        Assert.Contains(b.Applications,a=>a.Name=="w.exe"&&a.IoWrite==5000);
        Assert.DoesNotContain(b.Applications,a=>a.Name=="r1.exe");
    }
    [Fact] public void Control_character_names_are_unknown_and_go_to_everything_else()
    {
        var b=Close(P("\u0007\u0001",cpu:9),P("a.exe",cpu:1));
        Assert.DoesNotContain(b.Applications,a=>a.Name=="Unknown application");
        Assert.Equal(9,b.Applications.Single(a=>a.EverythingElse).CpuTime);
    }
    [Fact] public void Unpaired_surrogates_fold_into_one_app_named_with_U_FFFD(){
        var b=Close(P("a\uD800.exe",cpu:5,pid:1),P("a\uD801.exe",cpu:7,pid:2),P("a\uFFFD.exe",cpu:1,pid:3),P("\U0001F600.exe",cpu:2,pid:4));
        Assert.Equal(["a\uFFFD.exe","\U0001F600.exe",ProcessHistory.EverythingElse],b.Applications.Select(a=>a.Name));Assert.Equal(13,b.Applications[0].CpuTime);}
    [Fact] public void Reserved_and_path_shaped_names_go_to_everything_else(){
        var b=Close(P("Everything else",cpu:1),P("EVERYTHING ELSE",cpu:2,pid:2),P("*Everything else",cpu:4,pid:3),P(@"C:\Tools\tool.exe",cpu:8,pid:4),P("tools/tool.exe",cpu:16,pid:5),P("a.exe",cpu:32,pid:6));
        Assert.Equal(["a.exe",ProcessHistory.EverythingElse],b.Applications.Select(a=>a.Name));Assert.Equal(31,b.Applications.Single(a=>a.EverythingElse).CpuTime);}
    [Fact] public void Omitted_io_and_memory_go_to_everything_else(){ var f=new ProcessMinuteFolder();
        f.Observe(A(0,[P("",cpu:3,read:5,write:6,mem:7),P("a.exe",cpu:100)],new ProcessOmitted(2,0,new ProcessDeltas(40,70,30,900,1))));
        var e=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe")))).Applications.Single(a=>a.EverythingElse);
        Assert.Equal((43L,75L,36L),(e.CpuTime,e.IoRead,e.IoWrite));Assert.Equal(907,e.MemoryByteSeconds,6);}
    [Fact] public void An_interval_change_marks_only_its_own_minute_incomplete(){ var f=new ProcessMinuteFolder();
        ProcessActivity Slow(double t)=>A(t,[P("a.exe",cpu:1,dt:5)],dt:5,interval:5);
        for(int t=0;t<30;t++)f.Observe(A(t,P("a.exe",cpu:1)));
        for(int t=30;t<60;t+=5)f.Observe(Slow(t));
        var first=Assert.IsType<ProcessMinuteBatch>(f.Observe(Slow(60)));Assert.Equal(60,first.CoveredSeconds,6);Assert.False(first.Complete);
        for(int t=65;t<120;t+=5)f.Observe(Slow(t));
        var second=Assert.IsType<ProcessMinuteBatch>(f.Observe(Slow(120)));Assert.True(second.Complete);Assert.Equal(first.Segment,second.Segment);}
    [Fact] public void Gpu_is_a_fourth_top_ten(){
        var list=new List<ProcessSample>();var gpuLeaders=new List<string>();
        for(int i=0;i<10;i++){list.Add(P($"cpu{i}.exe",cpu:1000+i));list.Add(P($"mem{i}.exe",mem:1_000_000+i));list.Add(P($"io{i}.exe",read:500+i,write:1));list.Add(P($"gpu{i}.exe",gpu:10+i));gpuLeaders.Add($"gpu{i}.exe");}
        for(int i=0;i<10;i++)list.Add(P($"small{i}.exe",cpu:i+1,mem:i+1,read:i+1,gpu:0.1*(i+1)));
        var b=CloseMinute(list);
        Assert.Equal(40,ProcessHistory.MaximumApplications);Assert.Equal(41,b.Applications.Count);
        Assert.All(gpuLeaders,n=>Assert.Contains(b.Applications,a=>a.Name==n));Assert.DoesNotContain(b.Applications,a=>a.Name=="small9.exe");
        Assert.Equal(60,b.GpuCoveredSeconds,6);}
    [Fact] public void Two_members_busy_on_different_engines_record_the_higher_sum(){
        var b=CloseMinute([P("a.exe",pid:1,gpu:30,gpuType:"3D"),P("a.exe",pid:2,gpu:50,gpuType:"Copy")]);
        Assert.Equal(30,App(b,"a.exe").GpuSeconds!.Value,6);}   // 0.5 × 60, not 0.8 × 60
    [Fact] public void Gpu_memory_adds_known_members_times_the_interval(){
        // Both members' GPU values are null: the CPU lists a.exe, since GPU memory is not ranked and zero never qualifies.
        var b=CloseMinute([P("a.exe",cpu:1,pid:1,gpuMem:1_000_000),P("a.exe",pid:2,gpuMem:null)]);
        Assert.Equal(6e7,App(b,"a.exe").GpuMemoryByteSeconds!.Value,1);}
    [Fact] public void Everything_else_holds_unlisted_unknown_name_and_omitted_gpu(){
        var list=Enumerable.Range(0,11).Select(i=>P($"g{i:00}.exe",pid:i+10,gpu:10+i)).ToList();   // g00 at 10 % is the smallest, so it is unlisted
        list.Add(P("",pid:30,dGpu:0.2));list.Add(P("tools/tool.exe",pid:31,gpu:50,dGpu:0.1));   // the 50 % never goes through GpuUse
        var b=CloseMinute(list,new ProcessOmitted(2,0,new ProcessDeltas(0,0,0,0,1){GpuSeconds=0.3,GpuMemoryByteSeconds=10}));
        double unlisted=0.10*60;
        var e=b.Applications.Single(a=>a.EverythingElse);
        Assert.Equal(unlisted+60*0.2+60*0.1+60*0.3,e.GpuSeconds!.Value,6);Assert.Equal(600,e.GpuMemoryByteSeconds!.Value,6);
        Assert.DoesNotContain(b.Applications,a=>a.Name=="g00.exe");Assert.DoesNotContain(b.Applications,a=>a.Name=="tools/tool.exe");}
    [Fact] public void Gpu_covered_seconds_count_only_folded_activities_with_an_interval(){
        var f=new ProcessMinuteFolder();
        for(int t=0;t<30;t++)f.Observe(A(t,[P("a.exe",cpu:1)],gpuInterval:1));
        for(int t=30;t<60;t++)f.Observe(A(t,[P("a.exe",cpu:1)],gpuInterval:null));
        f.Observe(A(59.5,[P("a.exe",cpu:1)],dt:null,gpuInterval:1));
        var b=Assert.IsType<ProcessMinuteBatch>(f.Observe(A(60,P("z.exe"))));
        Assert.Equal(30,b.GpuCoveredSeconds,6);}
    [Fact] public void A_minute_without_gpu_data_has_null_gpu_on_every_row(){
        var b=Close(P("a.exe",cpu:5),P("b.exe",cpu:1,pid:2));Assert.Equal(0,b.GpuCoveredSeconds);
        Assert.All(b.Applications,a=>{Assert.Null(a.GpuSeconds);Assert.Null(a.GpuMemoryByteSeconds);});}
    [Fact] public void A_minute_with_gpu_data_reads_zero_for_apps_without_known_values(){
        var b=CloseGpu(P("a.exe",gpu:10),P("b.exe",cpu:1,pid:2));
        Assert.Equal(0.0,App(b,"b.exe").GpuSeconds);Assert.Equal(0.0,App(b,"b.exe").GpuMemoryByteSeconds);}
    [Fact] public void Zero_gpu_never_qualifies_and_gpu_ties_break_by_key(){
        // Zero: only two apps, so idle.exe at 0 % is listed unless the GPU list drops zero.
        var zero=CloseGpu(P("idle.exe",gpu:0),P("busy.exe",pid:2,gpu:5));
        Assert.DoesNotContain(zero.Applications,a=>a.Name=="idle.exe");Assert.Contains(zero.Applications,a=>a.Name=="busy.exe");
        // Ties: passed k10 first, so a stable sort without the key-ordinal tie-break would keep k10 and drop k00.
        var ties=CloseGpu(Enumerable.Range(0,11).Reverse().Select(i=>P($"k{i:00}.exe",pid:i+1,gpu:5)).ToArray());
        Assert.DoesNotContain(ties.Applications,a=>a.Name=="k10.exe");Assert.Contains(ties.Applications,a=>a.Name=="k09.exe");
        Assert.Equal(10,ties.Applications.Count(a=>!a.EverythingElse));}
}
