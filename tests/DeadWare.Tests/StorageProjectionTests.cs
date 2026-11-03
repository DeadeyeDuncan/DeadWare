using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class StorageProjectionTests
{
    static StorageDrive Drive(string? serial,string model,int index,bool removable=false)
        =>new(StorageProjection.DriveId(serial,model,index),StorageProjection.Normalize(serial),model,index,"NVMe",1000,removable,[]);

    [Theory][InlineData(null)][InlineData("")][InlineData("   ")]
    public void Blank_serial_falls_back_to_model_and_index(string? serial)=>
        Assert.Equal("Samsung 980#2",StorageProjection.DriveId(serial,"Samsung 980",2));
    [Fact] public void Serial_is_trimmed_and_used_as_id()=>Assert.Equal("S1ABC",StorageProjection.DriveId(" S1ABC ","x",0));
    [Fact] public void Same_model_without_serial_gets_distinct_live_only_ids(){ var a=Drive(null,"WD Blue",0);var b=Drive(null,"WD Blue",1);
        Assert.NotEqual(a.Id,b.Id);Assert.False(a.Persistable);Assert.False(b.Persistable);}
    [Fact] public void Removable_drive_is_not_persistable()=>Assert.False(Drive("S9","Kingston",3,removable:true).Persistable);
    [Fact] public void Persisted_caps_at_eight_by_index(){ var drives=Enumerable.Range(0,10).Select(i=>Drive("S"+i,"M",9-i)).ToList();
        var kept=StorageProjection.Persisted(drives);Assert.Equal(8,kept.Count);Assert.Equal(0,kept[0].Index);}
    [Fact] public void Keys_follow_the_spec(){ Assert.Equal("storage/S1/read",StorageMetrics.Key("S1","read"));
        Assert.Equal("storage/S1/volume/C/free",StorageMetrics.VolumeKey("S1","C"));}
    [Theory]
    [InlineData(false,false,null,0,"Unknown")][InlineData(true,true,80.0,0,"Failing")][InlineData(true,false,10.0,0,"Failing")]
    [InlineData(true,false,25.0,0,"Warning")][InlineData(true,false,90.0,1,"Warning")][InlineData(true,false,90.0,0,"OK")]
    public void Badge_table(bool readable,bool critical,double? life,long reallocated,string expected)=>
        Assert.Equal(expected,StorageProjection.Badge(new(readable,critical,life,40,70,reallocated,0,0,null,null,false)));
    [Fact] public void Over_max_temperature_warns()=>Assert.Equal("Warning",StorageProjection.Badge(new(true,false,90,75,70,0,0,0,null,null,false)));
    [Fact] public void Needs_administrator_is_unknown_with_reason(){ var h=StorageProjection.Health("S1",new(false,false,null,null,null,0,0,0,null,null,true),null);
        Assert.Equal("Unknown",h.Badge);Assert.Equal("Unknown · needs administrator",h.Unavailable);}
}
