using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ProcessSettingsTests
{
    [Fact] public void Defaults_are_off_and_the_seven_default_columns(){ var s=new UserSettings();Assert.False(s.ProcessMonitoring);Assert.Equal(["cpu","memory","io","disk","network","gpu","gpumemory"],s.ProcessColumns);Assert.Equal(2,s.ProcessColumnsVersion);}
    [Fact] public void Subsets_round_trip(){ var s=new UserSettings{ProcessMonitoring=true,ProcessColumns=["pid","upload"]};var r=SettingsCodec.Read(SettingsCodec.Write(s));Assert.True(r.Settings.ProcessMonitoring);Assert.Equal(["pid","upload"],r.Settings.ProcessColumns);}
    [Fact] public void Null_or_missing_list_becomes_default(){ Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{\"ProcessColumns\":null}").Settings.ProcessColumns);Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{}").Settings.ProcessColumns);}
    [Fact] public void Unknown_null_and_duplicate_keys_are_dropped_and_ordered(){ var r=SettingsCodec.Read("{\"ProcessColumns\":[\"upload\",\"gputemp\",null,\"cpu\",\"cpu\"]}");Assert.Equal(["cpu","upload"],r.Settings.ProcessColumns);Assert.Null(r.Error);}
    [Fact] public void Empty_list_is_kept()=>Assert.Empty(SettingsCodec.Read("{\"ProcessColumns\":[]}").Settings.ProcessColumns);
    [Fact] public void Administrator_and_disk_keys_are_always_known()=>Assert.Equal(ProcessColumnKeys.AdministratorOnly,ProcessColumnKeys.Normalize(ProcessColumnKeys.AdministratorOnly));
    [Fact] public void Toggle_changes_only_that_key(){ Assert.Equal(["pid","cpu","memory","io","disk","network","gpu","gpumemory"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"pid",true));
        Assert.Equal(["memory","io","disk","network","gpu","gpumemory"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",false));Assert.Equal(ProcessColumnKeys.Default,ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",true));}
    [Theory][InlineData(false,false,false,"Turning it on adds one Windows process scan per poll and one GPU counter read per poll.")][InlineData(true,true,false,"Turning it on adds one Windows process scan per poll, one GPU counter read per poll and a disk trace.")]
    [InlineData(true,false,true,"Turning it on adds one Windows process scan per poll (at most one a second) and one GPU counter read per poll.")][InlineData(true,true,true,"Turning it on adds one Windows process scan per poll (at most one a second), one GPU counter read per poll and a disk trace.")]
    public void Scan_sentence_follows_the_build(bool elevated,bool disk,bool loop,string expected)=>Assert.Equal(expected,ProcessCopy.ScanSentence(elevated,disk,loop));
    [Fact] public void Scan_sentence_adds_the_history_sentence()=>Assert.Equal("Turning it on adds one Windows process scan per poll and one GPU counter read per poll. While usage history is recorded, it also keeps the busiest apps for 30 days.",ProcessCopy.ScanSentence(false,false,false,history:true));
    [Theory][InlineData("[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]")][InlineData("[\"network\",\"disk\",\"io\",\"memory\",\"cpu\",\"cpu\"]")][InlineData("[\"cpu\",\"memory\",\"io\",\"disk\",\"network\",\"bogus\",null]")]
    public void A_version_less_0_13_default_reads_as_the_new_default(string list){ var s=SettingsCodec.Read($"{{\"ProcessColumns\":{list}}}").Settings;
        Assert.Equal(ProcessColumnKeys.Default,s.ProcessColumns);Assert.Equal(2,s.ProcessColumnsVersion);}
    [Fact] public void A_version_below_2_is_upgraded_too()=>Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{\"ProcessColumnsVersion\":1,\"ProcessColumns\":[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]}").Settings.ProcessColumns);
    [Theory][InlineData("[\"cpu\",\"memory\"]","cpu,memory")][InlineData("[\"pid\",\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]","pid,cpu,memory,io,disk,network")][InlineData("[]","")]
    public void Any_other_version_less_list_is_kept(string list,string expected){ var s=SettingsCodec.Read($"{{\"ProcessColumns\":{list}}}").Settings;
        Assert.Equal(expected,string.Join(",",s.ProcessColumns));Assert.Equal(2,s.ProcessColumnsVersion);}
    [Fact] public void The_new_default_without_gpu_survives_round_trips(){
        var once=SettingsCodec.Read("{\"ProcessColumnsVersion\":2,\"ProcessColumns\":[\"cpu\",\"memory\",\"io\",\"disk\",\"network\"]}").Settings;
        var twice=SettingsCodec.Read(SettingsCodec.Write(once)).Settings;Assert.Equal(ProcessColumnKeys.PreviousDefault,twice.ProcessColumns);Assert.Equal(2,twice.ProcessColumnsVersion);
        var fresh=new UserSettings();fresh.ProcessColumns=ProcessColumnKeys.Toggle(ProcessColumnKeys.Toggle(fresh.ProcessColumns,"gpu",false),"gpumemory",false);
        Assert.Equal(ProcessColumnKeys.PreviousDefault,SettingsCodec.Read(SettingsCodec.Write(SettingsCodec.Read(SettingsCodec.Write(fresh)).Settings)).Settings.ProcessColumns);}
    [Fact] public void Normalize_and_toggle_never_upgrade(){ Assert.Equal(ProcessColumnKeys.PreviousDefault,ProcessColumnKeys.Normalize(ProcessColumnKeys.PreviousDefault));
        Assert.Equal(["pid","cpu","memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.PreviousDefault,"pid",true));}
}
