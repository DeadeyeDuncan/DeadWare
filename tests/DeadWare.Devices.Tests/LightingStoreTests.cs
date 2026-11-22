using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
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
        const string json="{\"Version\":4,\"Brightness\":40}";
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
    // B2: a save whose put-in-place fails deletes its temporary file through the handle it wrote it with, never by looking the name up
    // again, so what the name leads to by then can't turn the cleanup into a delete of something else. The failing put-in-place stands in
    // for that: it renames the temporary file away through its handle and leaves a decoy under its old name.
    [Fact] public void Failed_save_deletes_its_own_temporary_file_never_what_its_name_leads_to_later()
    {
        var away=Path.Combine(dir,"moved-away");
        string? decoy=null;
        var store=new LightingStore(P,guardLinks:true,place:(file,_)=>
        {
            decoy=LightingStore.FinalPath(file,out int _);
            Assert.NotNull(decoy);
            Assert.Equal(0,LightingStore.Rename(file,Path.GetFileName(away),replace:false));
            File.WriteAllText(decoy,"decoy");
            throw new IOException("putting it in place failed");
        });
        Assert.Throws<IOException>(()=>store.Save(new LightingSettings{Brightness=40}));
        Assert.Equal("decoy",File.ReadAllText(decoy!));
        Assert.False(File.Exists(away)); // the file the save wrote is gone, wherever it was renamed
        Assert.False(File.Exists(P));
    }
    // Polish round 2, item 1: a write that fails leaves no temporary file behind, and its own exception is the one that surfaces. Nothing
    // the cleanup does may write the failed bytes again: a retry would throw in its place and stop the temporary file being deleted.
    [Fact] public void Failed_write_leaves_no_temporary_file_and_surfaces_its_own_exception()
    {
        Exception? failed=null;
        var store=new LightingStore(P,guardLinks:true,write:(stream,json)=>
        {
            // Past anything a disk can hold, so the file stream's own write of the settings fails, as a full disk makes it fail.
            stream.Position=1L<<60;
            try{using(var writer=new StreamWriter(stream,leaveOpen:true)) writer.Write(json);stream.Flush();}
            catch(Exception e){failed=e;throw;}
        });
        var thrown=Assert.ThrowsAny<Exception>(()=>store.Save(new LightingSettings{Brightness=40}));
        Assert.Empty(Directory.GetFileSystemEntries(dir));
        Assert.NotNull(failed);
        Assert.Same(failed,thrown);
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

    // Whole-branch review D13: while a save writes its temporary file, no other program can open it, to read it or to delete or rename it,
    // so it stays where the save created it until the save renames it into place. Each attempt meets a sharing violation.
    [Fact] public void Save_holds_its_temporary_file_alone_until_it_is_in_place()
    {
        const int SharingViolation=unchecked((int)0x80070020);
        string? tmp=null;
        var store=new LightingStore(P,guardLinks:true,write:(stream,json)=>
        {
            tmp=Assert.Single(Directory.GetFiles(dir,"lighting.json.*.tmp"));
            Assert.Equal(SharingViolation,Assert.ThrowsAny<IOException>(()=>new FileStream(tmp,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete).Dispose()).HResult);
            Assert.Equal(SharingViolation,Assert.ThrowsAny<IOException>(()=>new FileInfo(tmp).Create(FileMode.Open,FileSystemRights.Delete,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.None,null).Dispose()).HResult);
            Assert.Equal(SharingViolation,Assert.ThrowsAny<IOException>(()=>File.Move(tmp,Path.Combine(dir,"moved-away"))).HResult);
            using var writer=new StreamWriter(stream,leaveOpen:true);writer.Write(json);
        });
        store.Save(new LightingSettings{Brightness=40});
        Assert.NotNull(tmp);
        Assert.Equal(40,new LightingStore(P).Load().Settings.Brightness);
        Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    // D13: a folder swapped for a junction after the check before use, here as soon as Guard has passed, is caught once the file is open:
    // its final path isn't the one asked for. The save is refused as a linked path, the file it made through the junction is deleted
    // through its handle, and nothing is left where the junction leads.
    [Fact] public void A_guarded_save_refuses_a_folder_swapped_for_a_junction_after_its_check()
    {
        var folder=Path.Combine(dir,"DeadWare");Directory.CreateDirectory(folder);
        var elsewhere=Path.Combine(dir,"elsewhere");Directory.CreateDirectory(elsewhere);
        var store=new LightingStore(Path.Combine(folder,"lighting.json"),guardLinks:true,race:step=>{if(step=="checked") SwapForJunction(folder,elsewhere);});
        try
        {
            Assert.Throws<LinkedPathException>(()=>store.Save(new LightingSettings{Brightness=40}));
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally{if(Directory.Exists(folder)) Directory.Delete(folder);} // the junction only
    }

    // D14: the same for a read. The file opened through the junction is refused before any of it is used: it is neither read into the
    // settings nor set aside, valid or damaged. F4 item 4c (whole-branch review D4): nor when it is over 1 MiB, which would be set aside
    // without being read, so the final-path check comes before the size check too.
    [Theory]
    [InlineData("{\"Brightness\":10}",0)]
    [InlineData("{ not json",0)]
    [InlineData("{\"Brightness\":10}",1_048_577)]
    public void A_guarded_load_refuses_a_folder_swapped_for_a_junction_after_its_check(string valid,int bytes)
    {
        var json=bytes>0?Padded(valid,bytes):valid;
        var folder=Path.Combine(dir,"DeadWare");Directory.CreateDirectory(folder);
        var elsewhere=Path.Combine(dir,"elsewhere");Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere,"lighting.json"),json);
        var store=new LightingStore(Path.Combine(folder,"lighting.json"),guardLinks:true,race:step=>{if(step=="checked") SwapForJunction(folder,elsewhere);});
        try
        {
            Assert.Throws<LinkedPathException>(()=>store.Load());
            Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(elsewhere).Select(Path.GetFileName));
            Assert.Equal(json,File.ReadAllText(Path.Combine(elsewhere,"lighting.json")));
        }
        finally{if(Directory.Exists(folder)) Directory.Delete(folder);} // the junction only
    }

    // D14: setting a damaged file aside renames the file Load read, through a handle to it, never what lighting.json's name leads to by
    // then. The seam stands in for a program without administrator rights that, once the file was read, moves it into another folder and
    // puts a file of its own under its name: that file is left alone, and the damaged one is renamed in the folder it was moved to.
    [Fact] public void Quarantine_sets_aside_the_file_it_read_never_what_its_name_leads_to_by_then()
    {
        File.WriteAllText(P,"{ not json");
        var other=Path.Combine(dir,"other");Directory.CreateDirectory(other);
        var moved=Path.Combine(other,"moved.json");
        var store=new LightingStore(P,guardLinks:true,race:step=>{if(step=="read"){File.Move(P,moved);File.WriteAllText(P,"{\"Brightness\":10}");}});
        Assert.True(store.Load().Quarantine);
        Assert.Equal("{\"Brightness\":10}",File.ReadAllText(P));
        Assert.Empty(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
        Assert.False(File.Exists(moved));
        var q=Assert.Single(Directory.GetFiles(other,"lighting.corrupt-*.json"));
        Assert.Equal("{ not json",File.ReadAllText(q));
    }

    // A damaged file is renamed in place, its content intact, and a name already taken isn't replaced: the next one is used. Every name
    // the stamp could give during the test is taken first, so the rename has to move on to -1.
    [Fact] public void Quarantine_moves_on_to_the_next_name_when_one_is_taken()
    {
        var start=DateTime.UtcNow;
        var taken=Enumerable.Range(-2,33).Select(s=>Path.Combine(dir,"lighting.corrupt-"+start.AddSeconds(s).ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture)+".json")).ToList();
        foreach(var name in taken) File.WriteAllText(name,"taken");
        File.WriteAllText(P,"{ not json");
        Assert.True(new LightingStore(P,guardLinks:true).Load().Quarantine);
        Assert.False(File.Exists(P));
        Assert.All(taken,name=>Assert.Equal("taken",File.ReadAllText(name)));
        var q=Assert.Single(Directory.GetFiles(dir,"lighting.corrupt-*-1.json"));
        Assert.Equal("{ not json",File.ReadAllText(q));
    }

    // The final-path check compares the long form of the path: one given with 8.3 short names, as %TEMP% is on some PCs, names the same
    // file and isn't refused. Path.GetFullPath expands short names that exist, and the final path has long ones. Where the volume makes no
    // short names, both forms are the same.
    [Fact] public void A_guarded_store_accepts_its_path_in_short_form()
    {
        var longDir=Path.Combine(dir,"a folder with a long name");Directory.CreateDirectory(longDir);
        var store=new LightingStore(Path.Combine(ShortPath(longDir),"lighting.json"),guardLinks:true);
        store.Save(new LightingSettings{Brightness=40});
        Assert.Equal(40,store.Load().Settings.Brightness);
        Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(longDir).Select(Path.GetFileName));
    }

    // GetFinalPathNameByHandleW gives a path with a drive letter as \\?\C:\…, and a network path as \\?\UNC\server\share\…; the check
    // compares them as a path is usually written.
    [Fact] public void A_final_path_is_compared_as_a_path_is_usually_written()
    {
        Assert.Equal(@"C:\Users\me\AppData\Local\DeadWare\lighting.json",LightingStore.DosPath(@"\\?\C:\Users\me\AppData\Local\DeadWare\lighting.json"));
        Assert.Equal(@"\\server\share\DeadWare\lighting.json",LightingStore.DosPath(@"\\?\UNC\server\share\DeadWare\lighting.json"));
        var path=Path.GetFullPath(Path.Combine(dir,"x.json"));
        using var file=File.OpenHandle(path,FileMode.CreateNew,FileAccess.Write);
        Assert.Equal(path,LightingStore.FinalPath(file,out _),StringComparer.OrdinalIgnoreCase);
    }

    // F2 item 1: a file whose final path Windows can't give is refused like one whose path leads elsewhere, since a host that can't confirm
    // which file it opened mustn't use it. On a drive the Mount Manager didn't create, GetFinalPathNameByHandleW gives no path in DOS form;
    // a pipe's handle stands in for a file there, as the named-pipe file system has no Mount Manager volume either. Load and Save run this
    // check on the file they opened, so Load's refusal reaches DeviceHost's linked-path note and Save's deletes its temporary file through
    // its handle, as for a junction. Without the guard, as in a host without administrator rights, nothing is checked.
    [Fact]
    public void A_guarded_check_refuses_a_file_whose_final_path_Windows_cant_give()
    {
        using var server=new AnonymousPipeServerStream(PipeDirection.Out);
        using var pipe=new SafeFileHandle(server.SafePipeHandle.DangerousGetHandle(),ownsHandle:false);
        var length=GetFinalPathNameByHandleW(pipe,new char[1024],1024,0); // flags 0, as the check asks: FILE_NAME_NORMALIZED and VOLUME_NAME_DOS
        var error=Marshal.GetLastPInvokeError();
        Assert.Equal(0u,length);
        var refused=Assert.Throws<LinkedPathException>(()=>new LightingStore(P,guardLinks:true).RequireOpened(pipe,P));
        Assert.Contains($"(error {error}: {new Win32Exception(error).Message.TrimEnd('.')})",refused.Message);
        Assert.EndsWith(", so the lighting helper running as administrator won't use it.",refused.Message);
        Assert.Null(Record.Exception(()=>new LightingStore(P).RequireOpened(pipe,P)));
    }

    // F3 item 2 (whole-branch review D1): the final-path check ignores letter case, as Windows does in a path. A path that differs from the
    // folder's spelling on disk only in case names the same file, so a guarded store saves and loads through it.
    [Fact] public void A_guarded_store_accepts_its_path_in_another_letter_case()
    {
        var upper=Path.Combine(Path.GetDirectoryName(dir)!,Path.GetFileName(dir).ToUpperInvariant());
        Assert.NotEqual(dir,upper);
        var store=new LightingStore(Path.Combine(upper,"lighting.json"),guardLinks:true);
        store.Save(new LightingSettings{Brightness=40});
        Assert.Equal(40,store.Load().Settings.Brightness);
        Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    // F3 item 2 (D1): a final path longer than the 260 characters FinalPath first makes room for is read in full, into a buffer of the
    // size GetFinalPathNameByHandleW asks for, so a guarded store saves and loads a lighting.json that deep.
    [Fact] public void A_guarded_store_accepts_a_path_longer_than_260_characters()
    {
        var deep=Path.Combine(dir,new string('a',60),new string('b',60),new string('c',60),new string('d',60));
        Directory.CreateDirectory(deep);
        var path=Path.Combine(deep,"lighting.json");
        Assert.True(path.Length>260);
        var store=new LightingStore(path,guardLinks:true);
        store.Save(new LightingSettings{Brightness=40});
        Assert.Equal(40,store.Load().Settings.Brightness);
        Assert.Equal(new[]{"lighting.json"},Directory.GetFileSystemEntries(deep).Select(Path.GetFileName));
    }

    // F3 item 3 (D4): Load reads the open file in 4096-byte pieces until there is no more, so a lighting.json past 8 KiB, three pieces or
    // more, loads whole, every zone entry intact.
    [Fact] public void A_guarded_load_reads_a_file_longer_than_two_of_its_reads()
    {
        var settings=new LightingSettings{Brightness=40};
        for(var i=0;i<150;i++) settings.ZoneColors[$"serial-{i}|location-{i}#0"]=new ZoneColorEntry{Color=$"#{i:X2}2233",ZoneName="Zone "+i};
        File.WriteAllText(P,LightingSettingsCodec.Write(settings));
        Assert.True(new FileInfo(P).Length>2*4096);
        var r=new LightingStore(P,guardLinks:true).Load();
        Assert.Null(r.Error);Assert.False(r.Quarantine);
        Assert.Equal(40,r.Settings.Brightness);
        Assert.Equal(settings.ZoneColors.Count,r.Settings.ZoneColors.Count);
        Assert.All(settings.ZoneColors,e=>{Assert.Equal(e.Value.Color,r.Settings.ZoneColors[e.Key].Color);Assert.Equal(e.Value.ZoneName,r.Settings.ZoneColors[e.Key].ZoneName);});
    }

    // F3 item 3 (D4): as File.ReadAllText does, Load decodes by the byte order mark when there is one, so a lighting.json an editor saved
    // as UTF-16 loads.
    [Fact] public void Load_reads_a_file_saved_as_UTF_16_with_its_byte_order_mark()
    {
        File.WriteAllText(P,"{\"Brightness\":40,\"DefaultColor\":\"#112233\"}",new System.Text.UnicodeEncoding(bigEndian:false,byteOrderMark:true));
        Assert.Equal(new byte[]{0xFF,0xFE},File.ReadAllBytes(P)[..2]);
        var r=new LightingStore(P).Load();
        Assert.Null(r.Error);Assert.False(r.Quarantine);
        Assert.Equal(40,r.Settings.Brightness);Assert.Equal("#112233",r.Settings.DefaultColor);
    }

    // F3 item 4 (D5): a lighting.json whose folder doesn't exist yet, as on a profile where DeadWare never wrote it, loads defaults with no
    // error, guarded or not, and Load makes no folder.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_from_a_folder_that_does_not_exist_gives_defaults_without_error(bool guarded)
    {
        var missing=Path.Combine(dir,"missing");
        var r=new LightingStore(Path.Combine(missing,"lighting.json"),guardLinks:guarded).Load();
        Assert.Null(r.Error);Assert.False(r.Quarantine);
        Assert.Equal(100,r.Settings.Brightness);
        Assert.False(Directory.Exists(missing));
    }

    // F3 item 5 (D6): a guarded Load checks the path before it opens the file, so a folder that is a junction is refused even when there's
    // no lighting.json behind it, rather than read as a missing file and given defaults with no note.
    [Fact] public void A_guarded_load_refuses_a_junction_with_no_lighting_json_behind_it()
    {
        var folder=Path.Combine(dir,"DeadWare");Directory.CreateDirectory(folder);
        var elsewhere=Path.Combine(dir,"elsewhere");Directory.CreateDirectory(elsewhere);
        SwapForJunction(folder,elsewhere);
        try
        {
            Assert.Throws<LinkedPathException>(()=>new LightingStore(Path.Combine(folder,"lighting.json"),guardLinks:true).Load());
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally{Directory.Delete(folder);} // the junction only
    }

    // F3 item 7 (whole-branch review D2): a lighting.json is a few KB, so one over 1 MiB counts as damaged without being read, even when its
    // JSON is valid: it is set aside through the handle it was opened with, like any invalid file, and defaults load. One of exactly 1 MiB
    // loads as usual.
    [Fact] public void A_file_of_exactly_1_MiB_loads()
    {
        File.WriteAllText(P,Padded("{\"Brightness\":40}",1_048_576));
        Assert.Equal(1_048_576,new FileInfo(P).Length);
        var r=new LightingStore(P).Load();
        Assert.Null(r.Error);Assert.False(r.Quarantine);
        Assert.Equal(40,r.Settings.Brightness);
        Assert.True(File.Exists(P));
    }
    [Fact] public void A_guarded_load_sets_aside_a_file_over_1_MiB_unread()
    {
        File.WriteAllText(P,Padded("{\"Brightness\":40}",1_048_577));
        var r=new LightingStore(P,guardLinks:true).Load();
        Assert.True(r.Quarantine);
        Assert.Equal("Lighting settings are larger than 1 MiB (1048577 bytes).",r.Error);
        Assert.Equal(100,r.Settings.Brightness);
        Assert.False(File.Exists(P));
        var q=Assert.Single(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
        Assert.Equal(1_048_577,new FileInfo(q).Length);
    }
    // F4 item 4a (whole-branch review D2): a file over 1 MiB isn't read at all. Another handle locks every byte of it, so any read of the
    // file fails, while its size and its rename through the handle don't need one: the load sets it aside all the same.
    [Fact] public void A_file_over_1_MiB_is_set_aside_without_a_single_read()
    {
        File.WriteAllText(P,Padded("{\"Brightness\":40}",1_048_577));
        LightingRead r;
        using(var locker=new FileStream(P,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            locker.Lock(0,locker.Length);
            Assert.Throws<IOException>(()=>File.ReadAllBytes(P)); // a read now fails
            r=new LightingStore(P,guardLinks:true).Load();
        }
        Assert.True(r.Quarantine);
        Assert.Equal("Lighting settings are larger than 1 MiB (1048577 bytes).",r.Error);
        Assert.False(File.Exists(P));
        var q=Assert.Single(Directory.GetFiles(dir,"lighting.corrupt-*.json"));
        Assert.Equal(1_048_577,new FileInfo(q).Length);
    }
    // Valid JSON padded with spaces to exactly bytes bytes of UTF-8.
    static string Padded(string json,int bytes)=>json+new string(' ',bytes-json.Length);

    // Moves folder aside and puts a junction to target in its place, as a program without administrator rights can in its own folders.
    static void SwapForJunction(string folder,string target)
    {
        Directory.Move(folder,folder+".old");
        using var mklink=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe"){ArgumentList={"/c","mklink","/J",folder,target},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!;
        mklink.StandardOutput.ReadToEnd();mklink.WaitForExit();Assert.Equal(0,mklink.ExitCode);
    }
    static string ShortPath(string path)
    {
        var buffer=new char[1024];
        var length=GetShortPathNameW(path,buffer,(uint)buffer.Length);
        return length is 0 or >=1024?path:new string(buffer,0,(int)length);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetShortPathNameW(string longPath,[Out] char[] shortPath,uint length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle file,[Out] char[] path,uint length,uint flags);
}
