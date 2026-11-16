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
    [Fact] public void Concurrent_writes_do_not_throw_or_lose_lines()
    {
        using var log=new HostLog(dir);
        Parallel.For(0,200,i=>log.Write("l"+i));
        Assert.Equal(200,File.ReadAllLines(Path.Combine(dir,"devices.log")).Length);
    }
}
