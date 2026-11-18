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
