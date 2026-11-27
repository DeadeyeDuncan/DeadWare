using DeadWare.Core;
using Xunit;

namespace DeadWare.Devices.Tests;

// Fix wave 4b: the scanner logs which OpenRGB processes a scan saw when it found another one, only when that text changed, and again after a scan
// that found none. The listing is the test's own; no real process list is read.
public sealed class ProcessConflictScannerTests:IDisposable
{
    readonly string dir=Path.Combine(Path.GetTempPath(),"deadware-scanner-"+Guid.NewGuid().ToString("N"));
    readonly HostLog log;
    IReadOnlyList<RunningProcess> listed=[];

    public ProcessConflictScannerTests(){log=new HostLog(Path.Combine(dir,"logs"));}
    public void Dispose(){log.Dispose();try{Directory.Delete(dir,true);}catch{}}

    string[] Lines(string contains)
    {
        var p=Path.Combine(dir,"logs","devices.log");
        return File.Exists(p)?File.ReadAllLines(p).Where(l=>l.Contains(contains)).Select(l=>l[(l.IndexOf(' ')+1)..]).ToArray():[];
    }

    static readonly RunningProcess Ours=new(100,"OpenRGB",@"C:\o\OpenRGB.exe",new DateTime(2026,11,27,20,41,55,0,DateTimeKind.Utc));
    static readonly RunningProcess Other=new(200,"OpenRGB",null,null);

    // Fix wave 4c (review D4): the scanner passes the engine's ended OpenRGB on and says whether it is still listed: the same process (id and start)
    // is not another OpenRGB and is listed; the same id with a later start is another one and the ended one is not listed.
    [Fact]
    public void The_ended_OpenRGB_is_passed_on_and_its_listing_reported()
    {
        var scanner=new ProcessConflictScanner(log,()=>listed);
        var ended=(Ours.Id,Ours.Started!.Value);
        listed=[Ours];
        Assert.Empty(scanner.Scan(null,ended,out var stillListed));
        Assert.True(stillListed);
        listed=[Ours with{Started=Ours.Started.Value.AddSeconds(30)}];
        Assert.Equal(ConflictKind.ForeignOpenRgb,Assert.Single(scanner.Scan(null,ended,out stillListed)).Kind);
        Assert.False(stillListed);
        listed=[];
        Assert.Empty(scanner.Scan(null,ended,out stillListed));
        Assert.False(stillListed);
    }

    [Fact]
    public void Another_OpenRGB_is_logged_once_per_change_and_again_after_a_clean_scan()
    {
        var scanner=new ProcessConflictScanner(log,()=>listed);
        listed=[Ours,Other];
        Assert.Single(scanner.Scan(100,null,out _));
        Assert.Single(scanner.Scan(100,null,out _));
        const string line=@"another OpenRGB seen: #100 started 20:41:55.000Z C:\o\OpenRGB.exe; #200 start unknown path unknown | ours: #100 | last ended of ours: none";
        Assert.Equal([line],Lines("another OpenRGB seen"));
        listed=[Ours];
        Assert.Empty(scanner.Scan(100,null,out _));
        listed=[Ours,Other];
        scanner.Scan(100,null,out _);
        Assert.Equal([line,line],Lines("another OpenRGB seen"));
        // A different text is a new line at once.
        scanner.Scan(null,null,out _);
        Assert.Equal(3,Lines("another OpenRGB seen").Length);
        Assert.Equal(@"another OpenRGB seen: #100 started 20:41:55.000Z C:\o\OpenRGB.exe; #200 start unknown path unknown | ours: none | last ended of ours: none",Lines("another OpenRGB seen")[2]);
    }
}
