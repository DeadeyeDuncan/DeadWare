using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.UpdateCore;

public static class InstallProcesses
{
    public const string DevicesExecutable="DeadWare.Devices.exe";
    /// <summary>The ids of processes that are, or cannot be confirmed not to be, the install folder's device host: a null path counts as running.</summary>
    public static IReadOnlyList<int> Running(string installDirectory,IEnumerable<(int Id,string? Path)> processes)
    {
        string host=Path.Combine(installDirectory,DevicesExecutable);
        return processes.Where(p=>string.IsNullOrEmpty(p.Path)||string.Equals(p.Path,host,StringComparison.OrdinalIgnoreCase)).Select(p=>p.Id).ToList();
    }
    /// <summary>The process's image path (flags 0), read with limited query access so it works across elevation; null when it cannot be read.</summary>
    public static string? ImagePath(int pid)
    {
        using var process=OpenProcess(0x1000,false,pid);
        if(process.IsInvalid)return null;
        string? path=Query(process,1024,out int error);
        return path is null&&error==122?Query(process,32_768,out _):path;
    }
    private static string? Query(SafeProcessHandle process,int capacity,out int error)
    {
        var path=new StringBuilder(capacity);int size=capacity;
        if(QueryFullProcessImageName(process,0,path,ref size)){error=0;return path.ToString();}
        error=Marshal.GetLastWin32Error();return null;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
}
