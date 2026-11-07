using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessTableTests
{
    static ProcessSample S(int pid,string name,double? cpu=0)=>new(pid,1,name,1,cpu,0,null,null,ProcessGap.None,null);
    static ProcessSample Full()=>new(7,1,"a.exe",1,1.5,4096,10,20,ProcessGap.None,null){DiskReadPerSecond=300,DiskWritePerSecond=400,DownloadPerSecond=5000,UploadPerSecond=6000,
        GpuPercent=12.5,GpuEngines=[U(0xA,0,12.5)],GpuEngine="GPU 0 · 3D",GpuDedicatedBytes=8192,GpuSharedBytes=1024};
    // The label defaults to "GPU 0 · <type>" for luid 0xA, "GPU 1 · <type>" for 0xB and "GPU ? · <type>" otherwise.
    static GpuEngineUse U(long luid,int eng,double pct,string type="3D",int phys=0,string? label=null,string? adapter=null)=>
        new(luid,phys,eng,type,label??(luid==0xA?"GPU 0":luid==0xB?"GPU 1":"GPU ?")+" · "+type,adapter,pct);
    static ProcessSample GS(int pid,IReadOnlyList<GpuEngineUse> engines)=>S(pid,"a.exe",1) with{GpuEngines=engines,GpuPercent=engines.Count==0?0:engines[0].Percent,GpuEngine=engines.Count==0?"":engines[0].Label,GpuAdapter=engines.Count==0?null:engines[0].Adapter};
    static ProcessAppRow G(params ProcessSample[] members)=>Assert.Single(ProcessGrouping.Group(members));
    static IReadOnlyList<string> SortBy(bool descending){
        ProcessSample[] list=[S(1,"a",null),S(2,"c",5),S(3,"b",5)];
        var samples=list.OrderBy(p=>p,Comparer<ProcessSample>.Create((x,y)=>ProcessSort.Compare(x,y,"cpu",descending))).Select(p=>p.Name).ToList();
        var rows=ProcessGrouping.Group(list).OrderBy(r=>r,Comparer<ProcessAppRow>.Create((x,y)=>ProcessSort.Compare(x,y,"cpu",descending))).Select(r=>r.Name).ToList();
        Assert.Equal(samples,rows);
        return rows;
    }

    [Fact] public void Case_variants_are_one_row_with_the_first_casing(){ var rows=ProcessGrouping.Group([S(8,"Explorer.EXE",cpu:1),S(9,"explorer.exe",cpu:2)]);var r=Assert.Single(rows);Assert.Equal("Explorer.EXE",r.Name);Assert.Equal(3,r.Cpu);Assert.Equal("EXPLORER.EXE",r.Key);}
    [Fact] public void Non_ascii_case_variants_are_one_row()=>Assert.Single(ProcessGrouping.Group([S(8,"Éclair.exe"),S(9,"éCLAIR.EXE")]));
    [Fact] public void Row_key_is_stable_when_the_first_casing_changes()=>Assert.Equal(ProcessGrouping.Key("explorer.exe"),ProcessGrouping.Key("EXPLORER.exe"));
    [Fact] public void Sum_is_null_only_when_no_member_is_known(){ Assert.Null(G(S(8,"a",cpu:null),S(9,"a",cpu:null)).Cpu);Assert.Equal(2,G(S(8,"a",cpu:null),S(9,"a",cpu:2)).Cpu);}
    [Fact] public void Empty_and_control_char_names_normalize_like_usage_history(){ Assert.Equal("Unknown application",G(S(8,"")).Name);Assert.Equal("a.exe",G(S(9,"a\u0007.exe ")).Name);Assert.Equal(256,G(S(10,new string('x',300))).Name.Length);}
    [Theory][InlineData(" chrome ",true)][InlineData("12",true)][InlineData(" 12 ",true)][InlineData(" 12",true)][InlineData("34 ",true)][InlineData("zzz",false)][InlineData(" 99 ",false)][InlineData("",true)][InlineData("   ",true)]
    public void Search_trims_and_matches_name_or_pid(string q,bool hit)=>Assert.Equal(hit,ProcessGrouping.Matches(G(S(1234,"chrome.exe")),q));
    [Fact] public void Search_matches_any_member_pid_of_the_app(){ var row=G(S(1234,"chrome.exe"),S(777,"Chrome.exe"));Assert.True(ProcessGrouping.Matches(row," 77 "));Assert.True(ProcessGrouping.Matches(row,"CHROME"));Assert.False(ProcessGrouping.Matches(row,"55"));}
    [Fact] public void Visible_columns_keep_the_fixed_order()=>Assert.Equal(["pid","cpu","upload"],ProcessTableColumns.Visible(["upload","cpu","pid"],true,true));
    [Fact] public void Standard_user_hides_the_six_administrator_columns()=>Assert.Equal(["cpu","memory","io","gpu","gpuengine","gpumemory","gpushared"],ProcessTableColumns.Visible(ProcessColumnKeys.All.Except(["pid","ioread","iowrite"]).ToList(),false,true));
    [Fact] public void Disk_build_off_removes_disk_from_table_and_picker(){ Assert.DoesNotContain("disk",ProcessTableColumns.Visible(ProcessColumnKeys.Default,true,false));Assert.DoesNotContain(ProcessTableColumns.Picker(ProcessColumnKeys.Default,false,false),c=>c.Key.StartsWith("disk"));}
    [Fact] public void Picker_disables_admin_entries_and_keeps_their_saved_state(){ var p=ProcessTableColumns.Picker(["network"],false,true);var n=p.Single(c=>c.Key=="network");Assert.False(n.Enabled);Assert.True(n.Checked);Assert.Equal("NETWORK (administrator)",n.Label);
        Assert.Equal(["cpu","memory","io","disk","network","gpu","gpumemory","pid","ioread","iowrite","diskread","diskwrite","download","upload","gpuengine","gpushared"],p.Select(c=>c.Key));
        Assert.All(p.Where(c=>c.Key.StartsWith("gpu",StringComparison.Ordinal)),c=>{Assert.True(c.Enabled);Assert.Equal(c.Header,c.Label);});}
    [Fact] public void Gpu_keys_follow_upload_and_none_needs_administrator(){ Assert.Equal(["gpu","gpuengine","gpumemory","gpushared"],ProcessColumnKeys.All.Skip(12));Assert.Equal(16,ProcessColumnKeys.All.Count);
        Assert.Empty(ProcessColumnKeys.AdministratorOnly.Intersect(ProcessColumnKeys.All.Skip(12)));
        Assert.Equal(["GPU","GPU ENGINE","GPU MEMORY","SHARED GPU MEMORY"],ProcessColumnKeys.All.Skip(12).Select(ProcessTableColumns.Header));}
    [Fact] public void Hidden_sort_column_falls_back_to_cpu_then_name(){ Assert.Equal(("cpu",true),ProcessTableColumns.EffectiveSort("upload",false,["cpu"]));Assert.Equal(("name",false),ProcessTableColumns.EffectiveSort("upload",false,["memory"]));}
    [Fact] public void Nulls_sort_last_both_ways_and_ties_by_name_then_pid(){ Assert.Equal(["b","c","a"],SortBy(true));Assert.Equal(["b","c","a"],SortBy(false));
        Assert.Equal([2,9],new[]{S(9,"a",5),S(2,"a",5)}.Order(Comparer<ProcessSample>.Create((x,y)=>ProcessSort.Compare(x,y,"cpu",true))).Select(p=>p.Pid));}
    [Fact] public void An_app_sums_each_engine_across_members(){ var r=G(GS(1,[U(0xA,1,50,"Copy"),U(0xA,0,30)]),GS(2,[U(0xA,0,40)]));
        Assert.Equal(70,r.Gpu);Assert.Equal("GPU 0 · 3D",r.GpuBusiest!.Label);Assert.Equal(70,r.GpuBusiest.Percent);}   // no member's own busiest
    [Fact] public void Two_phys_values_under_one_luid_stay_apart()=>Assert.Equal(40,G(GS(1,[U(0xA,0,30,phys:0)]),GS(2,[U(0xA,0,40,phys:1)])).Gpu);
    [Fact] public void Empty_lists_are_zero_and_all_unknown_is_unknown(){ var zero=G(GS(1,[]),S(2,"a.exe",1));Assert.Equal(0,zero.Gpu);Assert.Null(zero.GpuBusiest);Assert.Equal("",ProcessGrouping.EngineText(zero));
        var unknown=G(S(1,"a.exe",1),S(2,"a.exe",1));Assert.Null(unknown.Gpu);Assert.Null(ProcessGrouping.EngineText(unknown));}
    [Fact] public void An_app_sum_is_clamped_and_ties_go_to_engine_order(){ Assert.Equal(100,G(GS(1,[U(0xA,0,60)]),GS(2,[U(0xA,0,60)])).Gpu);
        Assert.Equal("GPU 0 · 3D",G(GS(1,[U(0xB,0,50)]),GS(2,[U(0xA,0,50)])).GpuBusiest!.Label);Assert.Equal("GPU 1 · 3D",G(GS(1,[U(0xC,0,50)]),GS(2,[U(0xB,0,50)])).GpuBusiest!.Label);
        Assert.Equal(0xCL,G(GS(1,[U(0xD,0,50)]),GS(2,[U(0xC,0,50)])).GpuBusiest!.Luid);Assert.Equal(0,G(GS(1,[U(0xA,0,50,phys:1)]),GS(2,[U(0xA,0,50)])).GpuBusiest!.Phys);   // "GPU ?" by LUID, then phys
        Assert.Equal("GPU 1 · 3D",G(GS(1,[U(0x1,0,50)]),GS(2,[U(0xB,0,50)])).GpuBusiest!.Label);   // numbered adapters come before "GPU ?" whatever the LUID
        Assert.Equal("GPU 0 · 3D",G(GS(1,[U(0x5,0,50,label:"GPU 1 · 3D")]),GS(2,[U(0xA,0,50)])).GpuBusiest!.Label);}   // the number, not the LUID, orders numbered adapters
    [Fact] public void Gpu_memory_sums_and_is_unknown_only_when_every_member_is(){ var r=G(S(1,"a.exe",1) with{GpuDedicatedBytes=100},S(2,"a.exe",1));
        Assert.Equal(100,r.GpuMemory);Assert.Null(r.GpuShared);Assert.Equal(100,ProcessGrouping.Value(r,"gpumemory"));}
    [Theory][InlineData("NVIDIA GeForce RTX 4070","3D","NVIDIA GeForce RTX 4070 · 3D engine")][InlineData(null,"3D","3D engine")][InlineData(null,"","engine")][InlineData("Intel(R) UHD Graphics 770","","Intel(R) UHD Graphics 770 · engine")]
    public void Engine_tooltip_names_the_adapter_and_type(string? adapter,string type,string expected)=>Assert.Equal(expected,ProcessCopy.EngineTip(U(0xA,0,10,type,adapter:adapter)));
    [Fact] public void Gpu_engine_sorts_as_text_with_empty_then_unknown_last_both_ways(){
        var rows=new[]{G(GS(1,[U(0xB,0,9)])) with{Name="x1"},G(GS(2,[U(0xA,1,9,"Copy",label:"gpu 0 · Copy")])) with{Name="x2"},G(GS(3,[])) with{Name="x3"},G(S(4,"a.exe",1)) with{Name="x4"},G(GS(5,[U(0xA,1,9,"Copy")])) with{Name="x5"}};
        string[] By(bool d)=>[..rows.Order(Comparer<ProcessAppRow>.Create((a,b)=>ProcessSort.Compare(a,b,"gpuengine",d))).Select(r=>r.Name)];
        Assert.Equal(["x2","x5","x1","x3","x4"],By(false));Assert.Equal(["x1","x2","x5","x3","x4"],By(true));
        int[] ByPid(bool d)=>[..rows.Select(r=>r.Members[0]).Order(Comparer<ProcessSample>.Create((a,b)=>ProcessSort.Compare(a,b,"gpuengine",d))).Select(p=>p.Pid)];
        Assert.Equal([2,5,1,3,4],ByPid(false));Assert.Equal([1,2,5,3,4],ByPid(true));
        Assert.Null(ProcessGrouping.EngineText(rows[3].Members[0]));Assert.Equal("",ProcessGrouping.EngineText(rows[2].Members[0]));Assert.Equal("GPU 1 · 3D",ProcessGrouping.EngineText(rows[0].Members[0]));}
    [Fact] public void Only_name_and_gpu_engine_sort_ascending_first(){ Assert.True(ProcessTableColumns.AscendingFirst("gpuengine"));Assert.True(ProcessTableColumns.AscendingFirst("name"));Assert.False(ProcessTableColumns.AscendingFirst("gpu"));}
    [Fact] public void The_footer_ends_with_the_gpu_line(){ Assert.Equal("GPU is the app's busiest GPU engine, as in Task Manager; GPU memory is dedicated video memory.",ProcessCopy.Footer(true)[^1]);Assert.Equal(5,ProcessCopy.Footer(true).Count);}
    [Theory][InlineData("pid",7)][InlineData("cpu",1.5)][InlineData("memory",4096)][InlineData("io",30)][InlineData("ioread",10)][InlineData("iowrite",20)]
    [InlineData("disk",700)][InlineData("diskread",300)][InlineData("diskwrite",400)][InlineData("network",11000)][InlineData("download",5000)][InlineData("upload",6000)]
    [InlineData("gpu",12.5)][InlineData("gpumemory",8192)][InlineData("gpushared",1024)]
    public void Each_column_key_reads_its_own_field(string key,double expected){ Assert.Equal(expected,ProcessGrouping.Value(Full(),key));
        Assert.Equal(key=="pid"?3:key=="gpu"?25:2*expected,ProcessGrouping.Value(G(Full(),Full() with{Pid=3}),key));}
    [Theory][InlineData("io")][InlineData("disk")][InlineData("network")]
    public void A_sum_column_is_unknown_when_either_half_is(string key)=>Assert.Null(ProcessGrouping.Value(Full() with{IoWritePerSecond=key=="io"?null:20,DiskReadPerSecond=key=="disk"?null:300,UploadPerSecond=key=="network"?null:6000},key));
    [Theory][InlineData(false,true,"Disk and network need administrator")][InlineData(false,false,"Network needs administrator")][InlineData(true,true,null)]
    public void Banner_follows_elevation_and_build(bool elevated,bool disk,string? expected)=>Assert.Equal(expected,ProcessCopy.Banner(elevated,disk));
    [Fact] public void Footer_omits_disk_without_the_disk_build(){ Assert.Equal(4,ProcessCopy.Footer(false).Count);Assert.Equal(5,ProcessCopy.Footer(true).Count);}
}
