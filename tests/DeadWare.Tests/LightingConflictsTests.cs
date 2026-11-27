using System.Diagnostics;
using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class LightingConflictsTests
{
    static IReadOnlyList<LightingConflict> Find(int? own,params RunningProcess[] p)=>LightingConflicts.Find(p,own);
    [Fact] public void DeadLight_electron_blocks_by_path()
    {
        var r=Find(null,new RunningProcess(1,"electron",@"I:\DeadLight\node_modules\electron\dist\electron.exe"));
        var c=Assert.Single(r);
        Assert.Equal(ConflictKind.DeadLight,c.Kind);
        Assert.True(c.BlocksLighting);
    }
    [Fact] public void Other_electron_apps_do_not_block()
    {
        Assert.Empty(Find(null,new(1,"electron",@"C:\Users\x\AppData\Local\Discord\electron.exe"),new(2,"electron",null)));
    }
    [Fact] public void Foreign_openrgb_blocks_but_own_does_not()
    {
        var r=Find(10,new(10,"OpenRGB",@"C:\o\OpenRGB.exe"),new(11,"openrgb.exe",null));
        var c=Assert.Single(r);
        Assert.Equal(ConflictKind.ForeignOpenRgb,c.Kind);
        Assert.True(c.BlocksLighting);
    }
    // Fix wave 4, A: the host's own OpenRGB it has ended, still listed, is not another OpenRGB; the same id with another start time is one.
    [Fact] public void The_hosts_ended_OpenRGB_is_not_foreign_but_a_reused_id_is()
    {
        var started=new DateTime(2026,11,27,20,41,55);
        var ended=(Id:10,Started:started);
        Assert.Empty(LightingConflicts.Find([new RunningProcess(10,"OpenRGB",null,started)],null,ended));
        Assert.True(LightingConflicts.Listed([new RunningProcess(10,"OpenRGB",null,started)],ended));
        var reused=new RunningProcess(10,"OpenRGB",null,started.AddSeconds(3));
        Assert.Equal(ConflictKind.ForeignOpenRgb,Assert.Single(LightingConflicts.Find([reused],null,ended)).Kind);
        Assert.False(LightingConflicts.Listed([reused],ended));
        Assert.Equal(ConflictKind.ForeignOpenRgb,Assert.Single(LightingConflicts.Find([new RunningProcess(11,"OpenRGB",null,started)],null,ended)).Kind);
        Assert.Equal(ConflictKind.ForeignOpenRgb,Assert.Single(LightingConflicts.Find([new RunningProcess(10,"OpenRGB",null,null)],null,ended)).Kind);
    }
    // Fix wave 4b: the helper's log line naming every OpenRGB a scan listed when it found another one; none when it found none.
    [Fact] public void The_another_OpenRGB_line_names_every_OpenRGB_listed_and_what_is_ours()
    {
        var a=new RunningProcess(4120,"OpenRGB",@"C:\Program Files\OpenRGB\OpenRGB.exe",new DateTime(2026,11,27,20,41,55,867,DateTimeKind.Utc));
        var b=new RunningProcess(5532,"OpenRGB.exe",null,null);
        var other=new RunningProcess(7,"explorer",null,null);
        Assert.Equal(@"another OpenRGB seen: #4120 started 20:41:55.867Z C:\Program Files\OpenRGB\OpenRGB.exe; #5532 start unknown path unknown | ours: none | last ended of ours: none",
            LightingConflicts.ForeignOpenRgbLine([a,other,b],null,null));
        Assert.Equal(@"another OpenRGB seen: #4120 started 20:41:55.867Z C:\Program Files\OpenRGB\OpenRGB.exe; #5532 start unknown path unknown | ours: #4120 | last ended of ours: #3001 started 20:41:53.817Z",
            LightingConflicts.ForeignOpenRgbLine([a,other,b],4120,(3001,new DateTime(2026,11,27,20,41,53,817,DateTimeKind.Utc))));
        Assert.Null(LightingConflicts.ForeignOpenRgbLine([a,other],4120,null));
        Assert.Null(LightingConflicts.ForeignOpenRgbLine([a],null,(4120,a.Started!.Value)));
        Assert.Null(LightingConflicts.ForeignOpenRgbLine([other],null,null));
    }
    [Fact] public void iCUE_and_launcher_block()
    {
        var r=Find(null,new(1,"iCUE",null),new(2,"iCUE Launcher",null));
        Assert.Equal(2,r.Count);
        Assert.All(r,c=>{Assert.Equal(ConflictKind.ICue,c.Kind);Assert.True(c.BlocksLighting);});
    }
    [Fact] public void Device_services_block()
    {
        var r=Find(null,new(1,"CorsairDeviceControlService",null),new(2,"CorsairDeviceListerService",null));
        Assert.Equal(2,r.Count);
        Assert.All(r,c=>{Assert.Equal(ConflictKind.CorsairService,c.Kind);Assert.True(c.BlocksLighting);});
    }
    [Fact] public void Legacy_corsair_service_is_reported_but_does_not_block_lighting()
    {
        var c=Assert.Single(Find(null,new RunningProcess(1,"Corsair.Service",@"C:\Program Files\Corsair\Corsair iCUE5 Software\clink\Corsair.Service.exe")));
        Assert.Equal(ConflictKind.LegacyCorsairService,c.Kind);
        Assert.False(c.BlocksLighting);
        // It runs all the time on many PCs and so always shows in Settings: plain words, nothing about a driver this release doesn't have.
        Assert.Equal("The Corsair service, Corsair.Service.exe, is running. It doesn't block lighting.",c.Description);
    }
    // Task 4: paths are read with limited query access (QueryFullProcessImageName), which works across elevation, unlike MainModule: a
    // DeadLight running as administrator is still recognised by a scan without administrator rights.
    [Fact] public void Image_path_reader_reads_this_process()
        =>Assert.Equal(Environment.ProcessPath,LightingConflicts.ImagePath(Environment.ProcessId),ignoreCase:true);
    [Fact] public void Image_path_reader_gives_null_for_a_process_that_is_gone()
        =>Assert.Null(LightingConflicts.ImagePath(int.MaxValue-1));
    [Fact] public void Running_processes_list_this_process_by_id_and_name_and_read_no_path_for_it()
    {
        using var self=Process.GetCurrentProcess();
        var listed=Assert.Single(LightingConflicts.RunningProcesses(),p=>p.Id==Environment.ProcessId);
        Assert.Equal(self.ProcessName,listed.Name);
        // Only electron and OpenRGB get a path: reading every process's modules is slow and often denied.
        Assert.Null(listed.Path);
    }
}
