using Xunit;
using DeadWare.Devices;

namespace DeadWare.Devices.Tests;

public sealed class HostLogTests:IDisposable
{
    readonly string dir=Directory.CreateTempSubdirectory("dw-log").FullName;
    public void Dispose(){try{Directory.Delete(dir,true);}catch{}}

    [Fact] public void Lines_carry_an_iso_utc_prefix()
    {
        using var log=new HostLog(dir,()=>new DateTimeOffset(2026,11,15,1,2,3,TimeSpan.Zero));
        log.Write("hello");
        Assert.StartsWith("2026-11-15T01:02:03",File.ReadAllText(Path.Combine(dir,"devices.log")));
        Assert.Contains("Z hello",File.ReadAllText(Path.Combine(dir,"devices.log")));
    }
    [Fact] public void Log_rotates_at_two_mebibytes_and_keeps_three_files()
    {
        using var log=new HostLog(dir);
        var line=new string('x',1000);
        for(var i=0;i<8000;i++) log.Write(line);
        var files=Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x=>x).ToArray();
        Assert.Equal(new[]{"devices.1.log","devices.2.log","devices.log"},files);
        foreach(var f in Directory.GetFiles(dir)) Assert.True(new FileInfo(f).Length<=HostLog.MaxBytes);
    }
    [Fact] public void Log_write_never_throws_when_directory_is_read_only()
    {
        var file=Path.Combine(dir,"blocker");File.WriteAllText(file,"x");
        using var log=new HostLog(Path.Combine(file,"sub"));
        log.Write("nope");
        var locked=Path.Combine(dir,"locked");Directory.CreateDirectory(locked);
        using var held=new FileStream(Path.Combine(locked,"devices.log"),FileMode.Create,FileAccess.ReadWrite,FileShare.None);
        using var log2=new HostLog(locked);
        log2.Write("nope");
    }
    // An elevated host whose protected log folder couldn't be prepared logs nowhere rather than somewhere unprotected.
    [Fact] public void Log_without_a_folder_writes_nothing_and_never_throws()
    {
        using var log=new HostLog(null);
        log.Write("nowhere");
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }
    // Polish round 2, item 6: an elevated host's log folder, %ProgramData%\DeadWare\logs\<user SID>, is made private by
    // ProtectedFolders.LogDirectory at start. Made again here, it would take the logs folder's rights, which let every user read, so the
    // log writes nothing more once it goes missing, even if it comes back, until the next start makes it again.
    [Fact] public void Log_that_must_not_create_its_folder_stops_once_the_folder_goes_missing()
    {
        var own=Path.Combine(dir,"S-1-5-21-1");Directory.CreateDirectory(own);
        using var log=new HostLog(own,createFolder:false);
        log.Write("first");
        Assert.Contains("first",File.ReadAllText(Path.Combine(own,"devices.log")));
        Directory.Delete(own,true);
        log.Write("second");
        Assert.False(Directory.Exists(own));
        Directory.CreateDirectory(own);
        log.Write("third");
        Assert.Empty(Directory.GetFileSystemEntries(own));
    }
    // A host without administrator rights logs in %LOCALAPPDATA%\DeadWare\logs, which it makes again if it goes missing, as before.
    [Fact] public void Log_makes_its_folder_again_by_default()
    {
        var local=Path.Combine(dir,"logs");
        using var log=new HostLog(local);
        log.Write("first");
        Directory.Delete(local,true);
        log.Write("second");
        Assert.Contains("second",File.ReadAllText(Path.Combine(local,"devices.log")));
    }
    [Fact] public void Concurrent_writes_do_not_throw_or_lose_lines()
    {
        using var log=new HostLog(dir);
        Parallel.For(0,200,i=>log.Write("l"+i));
        Assert.Equal(200,File.ReadAllLines(Path.Combine(dir,"devices.log")).Length);
    }
}
