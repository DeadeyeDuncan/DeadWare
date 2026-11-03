using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class WindowsStorageTests
{
    [Theory][InlineData("0 C: D:","0")][InlineData("2","2")][InlineData("_Total",null)][InlineData("10 E:","10")]
    public void Instance_names_map_to_drive_index(string instance,string? expected)=>Assert.Equal(expected,WindowsStorage.InstanceDriveIndex(instance));
    [Fact] public void Parse_builds_drives_with_ids_and_volumes(){ var drives=WindowsStorage.Parse([new("S1","NVMe A",0,"NVMe",1000,false,[("C","OS",1000,400)]),new(null,"USB",1,"USB",64,true,[("E","",64,10)])]);
        Assert.Equal("S1",drives[0].Id);Assert.True(drives[0].Persistable);Assert.Equal("USB#1",drives[1].Id);Assert.False(drives[1].Persistable);Assert.Equal(400,drives[0].Volumes[0].FreeBytes);
        Assert.Equal("",drives[0].IdentityNote);Assert.Equal("no serial number",drives[1].IdentityNote);
        Assert.Equal("removable drive",WindowsStorage.Parse([new("R1","Stick",2,"USB",64,true,[])])[0].IdentityNote);}
    private static readonly WmiDisk Disk=new("S1","NVMe A",0,"NVMe",1000,false,[]);
    [Fact] public void Enumeration_failure_keeps_the_list_and_does_not_retry_before_60_seconds()
    {
        int calls=0;bool fail=false;
        var storage=new WindowsStorage(()=>{calls++;if(fail)throw new InvalidOperationException();return [Disk];},()=>new());
        Assert.Single(storage.Drives(0));Assert.Equal(1,calls);
        fail=true;
        Assert.Single(storage.Drives(60));Assert.Equal(2,calls);
        Assert.Single(storage.Drives(61));Assert.Single(storage.Drives(119));Assert.Equal(2,calls);
        Assert.Single(storage.Drives(120));Assert.Equal(3,calls);
    }
    [Fact] public void Counter_error_appears_only_after_60_seconds_of_failure_and_opens_are_throttled()
    {
        int opens=0;
        var storage=new WindowsStorage(()=>[Disk],()=>{opens++;throw new InvalidOperationException();});
        foreach(var (t,expectError) in new[]{(0d,false),(1d,false),(59d,false),(60d,true)})
        {
            var values=new Dictionary<string,double?>();
            var drives=storage.Drives(t);storage.Read(drives,values,out var error);
            Assert.Null(values["storage/S1/read"]);
            if(expectError)Assert.Contains("Disk counters unavailable",error);else Assert.Null(error);
        }
        Assert.Equal(2,opens);
    }
    [Fact] public void Counters_publish_activity_as_clamped_inverse_of_idle()
    {
        var storage=new WindowsStorage(()=>[Disk],()=>new(){["0"]=new(()=>100,()=>50,()=>105)});
        var values=new Dictionary<string,double?>();storage.Read(storage.Drives(0),values,out var error);
        Assert.Null(values["storage/S1/read"]);
        values.Clear();storage.Read(storage.Drives(0.5),values,out error);
        Assert.Null(error);Assert.Equal(100,values["storage/S1/read"]);Assert.Equal(50,values["storage/S1/write"]);Assert.Equal(0,values["storage/S1/active"]);
    }

    [Fact] public void Counters_survive_reenumeration_with_the_same_indexes_and_reopen_when_they_change()
    {
        int opens=0;IReadOnlyList<WmiDisk> disks=[Disk];
        var storage=new WindowsStorage(()=>disks,()=>{opens++;return new(){["0"]=new(()=>100,()=>50,()=>90),["1"]=new(()=>7,()=>8,()=>90)};});
        double?[] Poll(double t){var v=new Dictionary<string,double?>();storage.Read(storage.Drives(t),v,out _);return [v["storage/S1/read"]];}
        Assert.Null(Poll(0)[0]);Assert.Equal(100,Poll(0.5)[0]);
        Assert.Equal(100,Poll(60)[0]);Assert.Equal(100,Poll(60.5)[0]);Assert.Equal(1,opens);
        disks=[Disk,new("S2","SATA B",1,"SATA",2000,false,[])];
        Assert.Null(Poll(120)[0]);Assert.Equal(2,opens);Assert.Equal(100,Poll(120.5)[0]);Assert.Equal(2,opens);
    }
}
