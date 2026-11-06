using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class ProcessTableTests
{
    static ProcessSample S(int pid,string name,double? cpu=0)=>new(pid,1,name,1,cpu,0,null,null,ProcessGap.None,null);
    static ProcessAppRow G(params ProcessSample[] members)=>Assert.Single(ProcessGrouping.Group(members));
    static IReadOnlyList<string> SortBy(bool descending){
        ProcessSample[] list=[S(1,"a",null),S(3,"c",5),S(2,"b",5)];
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
    [Fact] public void Standard_user_hides_the_six_administrator_columns()=>Assert.Equal(["cpu","memory","io"],ProcessTableColumns.Visible(ProcessColumnKeys.All.Except(["pid","ioread","iowrite"]).ToList(),false,true));
    [Fact] public void Disk_build_off_removes_disk_from_table_and_picker(){ Assert.DoesNotContain("disk",ProcessTableColumns.Visible(ProcessColumnKeys.Default,true,false));Assert.DoesNotContain(ProcessTableColumns.Picker(ProcessColumnKeys.Default,false,false),c=>c.Key.StartsWith("disk"));}
    [Fact] public void Picker_disables_admin_entries_and_keeps_their_saved_state(){ var p=ProcessTableColumns.Picker(["network"],false,true);var n=p.Single(c=>c.Key=="network");Assert.False(n.Enabled);Assert.True(n.Checked);Assert.Equal("NETWORK (administrator)",n.Label);
        Assert.Equal(["cpu","memory","io","disk","network","pid","ioread","iowrite","diskread","diskwrite","download","upload"],p.Select(c=>c.Key));}
    [Fact] public void Hidden_sort_column_falls_back_to_cpu_then_name(){ Assert.Equal(("cpu",true),ProcessTableColumns.EffectiveSort("upload",false,["cpu"]));Assert.Equal(("name",false),ProcessTableColumns.EffectiveSort("upload",false,["memory"]));}
    [Fact] public void Nulls_sort_last_both_ways_and_ties_by_name_then_pid(){ Assert.Equal(["b","c","a"],SortBy(true));Assert.Equal(["b","c","a"],SortBy(false));}
    [Theory][InlineData(false,true,"Disk and network need administrator")][InlineData(false,false,"Network needs administrator")][InlineData(true,true,null)]
    public void Banner_follows_elevation_and_build(bool elevated,bool disk,string? expected)=>Assert.Equal(expected,ProcessCopy.Banner(elevated,disk));
    [Fact] public void Footer_omits_disk_without_the_disk_build(){ Assert.Equal(3,ProcessCopy.Footer(false).Count);Assert.Equal(4,ProcessCopy.Footer(true).Count);}
}
