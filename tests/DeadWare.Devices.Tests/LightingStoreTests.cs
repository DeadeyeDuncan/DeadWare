using Xunit;
using DeadWare.Core;
using DeadWare.Devices;

namespace DeadWare.Devices.Tests;

public sealed class LightingStoreTests:IDisposable
{
    readonly string dir=Directory.CreateTempSubdirectory("dw-store").FullName;
    string P=>Path.Combine(dir,"lighting.json");
    public void Dispose(){try{Directory.Delete(dir,true);}catch{}}

    [Fact] public void Missing_file_loads_defaults_without_error()
    {
        var r=new LightingStore(P).Load();
        Assert.Null(r.Error);Assert.False(r.Quarantine);
        Assert.Equal(100,r.Settings.Brightness);Assert.Equal("#4682B4",r.Settings.DefaultColor);
    }
    [Fact] public void Corrupt_file_is_quarantined_and_defaults_load()
    {
        File.WriteAllText(P,"{ not json");
        var r=new LightingStore(P).Load();
        Assert.True(r.Quarantine);Assert.NotNull(r.Error);Assert.Equal(100,r.Settings.Brightness);
        Assert.False(File.Exists(P));
        var q=Assert.Single(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
        Assert.Equal("{ not json",File.ReadAllText(q));
    }
    [Fact] public void Two_quarantines_in_one_second_do_not_overwrite()
    {
        var store=new LightingStore(P);
        for(var i=0;i<3;i++){File.WriteAllText(P,"bad"+i);store.Load();}
        Assert.Equal(3,Directory.GetFiles(dir,"lighting.corrupt-*.json").Length);
    }
    [Fact] public void Unreadable_file_throws_and_is_not_touched()
    {
        File.WriteAllText(P,"{ not json");
        using(new FileStream(P,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Assert.ThrowsAny<IOException>(()=>new LightingStore(P).Load());
        Assert.Equal("{ not json",File.ReadAllText(P));
        Assert.Empty(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
    }
    [Fact] public void Save_replaces_atomically()
    {
        var store=new LightingStore(P);
        store.Save(new LightingSettings{Brightness=40});
        store.Save(new LightingSettings{Brightness=55});
        Assert.False(File.Exists(P+".tmp"));
        Assert.Equal(55,store.Load().Settings.Brightness);
        Assert.Equal(new[]{"lighting.json"},Directory.GetFiles(dir).Select(Path.GetFileName));
    }
    [Fact] public void Save_refuses_invalid_settings()
    {
        var store=new LightingStore(P);
        Assert.Throws<InvalidDataException>(()=>store.Save(new LightingSettings{Brightness=500}));
        Assert.False(File.Exists(P));Assert.False(File.Exists(P+".tmp"));
    }
}
