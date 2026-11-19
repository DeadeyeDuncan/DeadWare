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
    // Ruling AS: a later DeadWare's file is read, never set aside.
    [Fact] public void Newer_file_is_read_and_left_in_place()
    {
        const string json="{\"Version\":2,\"Brightness\":40}";
        File.WriteAllText(P,json);
        var r=new LightingStore(P).Load();
        Assert.True(r.Newer);Assert.False(r.Quarantine);Assert.Equal(40,r.Settings.Brightness);
        Assert.Equal(json,File.ReadAllText(P));
        Assert.Empty(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
    }
    // A fresh temporary name per save, as SafeFiles writes update metadata: a planted lighting.json.tmp (here a folder) neither blocks a
    // save nor receives it.
    [Fact] public void Save_ignores_a_planted_temporary_file()
    {
        Directory.CreateDirectory(P+".tmp");
        new LightingStore(P).Save(new LightingSettings{Brightness=40});
        Assert.Equal(40,new LightingStore(P).Load().Settings.Brightness);
        Assert.Empty(Directory.GetFileSystemEntries(P+".tmp"));
    }
    // Ruling AT: an elevated host refuses lighting.json reached through a link or junction (SafeFiles.NoLinks, the updater's defence for
    // its own %LOCALAPPDATA%\DeadWare writes), so a program without administrator rights can't steer its writes elsewhere.
    [Fact] public void Guarded_store_refuses_a_path_through_a_junction()
    {
        var target=Path.Combine(dir,"target");Directory.CreateDirectory(target);
        var link=Path.Combine(dir,"link");
        using(var mklink=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe"){ArgumentList={"/c","mklink","/J",link,target},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!)
        {mklink.StandardOutput.ReadToEnd();mklink.WaitForExit();Assert.Equal(0,mklink.ExitCode);}
        try
        {
            var through=Path.Combine(link,"lighting.json");
            var guarded=new LightingStore(through,guardLinks:true);
            Assert.ThrowsAny<IOException>(()=>guarded.Save(new LightingSettings{Brightness=40}));
            Assert.Empty(Directory.GetFileSystemEntries(target));
            File.WriteAllText(Path.Combine(target,"lighting.json"),"{ not json");
            Assert.ThrowsAny<IOException>(()=>guarded.Load());
            Assert.Equal("{ not json",File.ReadAllText(Path.Combine(target,"lighting.json")));
            // Without the guard (a host without administrator rights) the user's own junction is followed as before.
            new LightingStore(through).Save(new LightingSettings{Brightness=55});
            Assert.Equal(55,new LightingStore(through).Load().Settings.Brightness);
        }
        finally{Directory.Delete(link);}
    }
    // B2: a save whose move fails deletes its temporary file through the handle it wrote it with, never by looking the name up again, so a
    // program without administrator rights that changed what the name leads to meanwhile can't turn the cleanup into a delete of its
    // choosing. The failing move stands in for that program: it moves the temporary file away and leaves a decoy under its name.
    [Fact] public void Failed_save_deletes_its_own_temporary_file_never_what_its_name_leads_to_later()
    {
        var away=Path.Combine(dir,"moved-away");
        string? decoy=null;
        var store=new LightingStore(P,guardLinks:true,move:(from,_)=>{File.Move(from,away);decoy=from;File.WriteAllText(from,"decoy");throw new IOException("the move failed");});
        Assert.Throws<IOException>(()=>store.Save(new LightingSettings{Brightness=40}));
        Assert.Equal("decoy",File.ReadAllText(decoy!));
        Assert.False(File.Exists(away)); // the file the save wrote is gone, wherever it was moved
        Assert.False(File.Exists(P));
    }
    // A real move that fails (lighting.json is a folder here) leaves no temporary file behind.
    [Fact] public void Failed_save_leaves_no_temporary_file()
    {
        Directory.CreateDirectory(P);
        Assert.ThrowsAny<Exception>(()=>new LightingStore(P).Save(new LightingSettings{Brightness=40}));
        Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }
    [Fact] public void Save_refuses_invalid_settings()
    {
        var store=new LightingStore(P);
        Assert.Throws<InvalidDataException>(()=>store.Save(new LightingSettings{Brightness=500}));
        Assert.False(File.Exists(P));Assert.False(File.Exists(P+".tmp"));
    }
}
