using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ProcessSettingsTests
{
    [Fact] public void Defaults_are_off_and_the_five_default_columns(){ var s=new UserSettings();Assert.False(s.ProcessMonitoring);Assert.Equal(["cpu","memory","io","disk","network"],s.ProcessColumns);}
    [Fact] public void Subsets_round_trip(){ var s=new UserSettings{ProcessMonitoring=true,ProcessColumns=["pid","upload"]};var r=SettingsCodec.Read(SettingsCodec.Write(s));Assert.True(r.Settings.ProcessMonitoring);Assert.Equal(["pid","upload"],r.Settings.ProcessColumns);}
    [Fact] public void Null_or_missing_list_becomes_default(){ Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{\"ProcessColumns\":null}").Settings.ProcessColumns);Assert.Equal(ProcessColumnKeys.Default,SettingsCodec.Read("{}").Settings.ProcessColumns);}
    [Fact] public void Unknown_null_and_duplicate_keys_are_dropped_and_ordered(){ var r=SettingsCodec.Read("{\"ProcessColumns\":[\"upload\",\"gpu\",null,\"cpu\",\"cpu\"]}");Assert.Equal(["cpu","upload"],r.Settings.ProcessColumns);Assert.Null(r.Error);}
    [Fact] public void Empty_list_is_kept()=>Assert.Empty(SettingsCodec.Read("{\"ProcessColumns\":[]}").Settings.ProcessColumns);
    [Fact] public void Administrator_and_disk_keys_are_always_known()=>Assert.Equal(ProcessColumnKeys.AdministratorOnly,ProcessColumnKeys.Normalize(ProcessColumnKeys.AdministratorOnly));
    [Fact] public void Toggle_changes_only_that_key(){ Assert.Equal(["pid","cpu","memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"pid",true));
        Assert.Equal(["memory","io","disk","network"],ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",false));Assert.Equal(ProcessColumnKeys.Default,ProcessColumnKeys.Toggle(ProcessColumnKeys.Default,"cpu",true));}
    [Theory][InlineData(false,false,false,"Turning it on adds one Windows process scan per poll and one GPU counter read per poll.")][InlineData(true,true,false,"Turning it on adds one Windows process scan per poll, one GPU counter read per poll and a disk trace.")]
    [InlineData(true,false,true,"Turning it on adds one Windows process scan per poll (at most one a second) and one GPU counter read per poll.")][InlineData(true,true,true,"Turning it on adds one Windows process scan per poll (at most one a second), one GPU counter read per poll and a disk trace.")]
    public void Scan_sentence_follows_the_build(bool elevated,bool disk,bool loop,string expected)=>Assert.Equal(expected,ProcessCopy.ScanSentence(elevated,disk,loop));
    [Fact] public void Scan_sentence_adds_the_history_sentence()=>Assert.Equal("Turning it on adds one Windows process scan per poll and one GPU counter read per poll. While usage history is recorded, it also keeps the busiest apps for 30 days.",ProcessCopy.ScanSentence(false,false,false,history:true));
}
