using DeadWare.UpdateCore;
using Xunit;
namespace DeadWare.Tests;

public class InstallProcessesTests
{
    private const string Install=@"C:\Program Files\DeadWare";
    [Fact] public void Finds_host_from_install_folder()=>Assert.Equal(new[]{7,9},InstallProcesses.Running(Install,new (int,string?)[]{(7,@"C:\Program Files\DeadWare\DeadWare.Devices.exe"),(8,@"C:\Elsewhere\DeadWare.Devices.exe"),(9,@"c:\program files\deadware\deadware.devices.exe")}));
    [Fact] public void Ignores_host_from_another_folder()=>Assert.Empty(InstallProcesses.Running(Install,new (int,string?)[]{(1,@"C:\Program Files\DeadWare2\DeadWare.Devices.exe"),(2,@"C:\Program Files\DeadWare\sub\DeadWare.Devices.exe")}));
    // Task 11: the updater's fail-closed helper check depends on this reader, separate from the Services copy.
    [Fact] public void Image_path_of_this_process_is_its_executable()=>Assert.Equal(Environment.ProcessPath,InstallProcesses.ImagePath(Environment.ProcessId),ignoreCase:true);
    [Fact] public void Counts_processes_without_path_as_running()=>Assert.Equal(new[]{1,2},InstallProcesses.Running(Install,new (int,string?)[]{(1,null),(2,"")}));
    [Fact] public void Trailing_separator_on_install_folder_does_not_matter()=>Assert.Equal(new[]{3},InstallProcesses.Running(Install+@"\",new (int,string?)[]{(3,@"C:\Program Files\DeadWare\DeadWare.Devices.exe")}));
}
