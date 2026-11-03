using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class WindowsStorageTests
{
    [Theory][InlineData("0 C: D:","0")][InlineData("2","2")][InlineData("_Total",null)][InlineData("10 E:","10")]
    public void Instance_names_map_to_drive_index(string instance,string? expected)=>Assert.Equal(expected,WindowsStorage.InstanceDriveIndex(instance));
    [Fact] public void Parse_builds_drives_with_ids_and_volumes(){ var drives=WindowsStorage.Parse([new("S1","NVMe A",0,"NVMe",1000,false,[("C","OS",1000,400)]),new(null,"USB",1,"USB",64,true,[("E","",64,10)])]);
        Assert.Equal("S1",drives[0].Id);Assert.True(drives[0].Persistable);Assert.Equal("USB#1",drives[1].Id);Assert.False(drives[1].Persistable);Assert.Equal(400,drives[0].Volumes[0].FreeBytes);}
}
