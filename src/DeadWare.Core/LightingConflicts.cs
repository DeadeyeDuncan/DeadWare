using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.Core;
public sealed record RunningProcess(int Id,string Name,string? Path);
public enum ConflictKind{DeadLight,ForeignOpenRgb,ICue,CorsairService,LegacyCorsairService}
public sealed record LightingConflict(ConflictKind Kind,string Description,bool BlocksLighting);
public static class LightingConflicts
{
    public static IReadOnlyList<LightingConflict> Find(IEnumerable<RunningProcess> processes,int? ownOpenRgbPid)
    {
        var found=new List<LightingConflict>();var seen=new HashSet<(ConflictKind,string)>();
        foreach(var p in processes)
        {
            var c=Classify(p,ownOpenRgbPid);
            if(c!=null&&seen.Add((c.Kind,Bare(p.Name).ToLowerInvariant()))) found.Add(c);
        }
        return found;
    }
    // The running processes for Find: the lighting host's scan and Settings' check while lighting is off. A process that exits while it is listed is left out; one whose path can't be read has a null path.
    public static IReadOnlyList<RunningProcess> RunningProcesses()
    {
        var processes=Process.GetProcesses();
        try
        {
            var list=new List<RunningProcess>(processes.Length);
            foreach(var p in processes)
            {
                string name;
                try{name=p.ProcessName;}catch{continue;}
                int id;
                try{id=p.Id;}catch{continue;}
                // Only electron (DeadLight's install path) and OpenRGB need a path. It is read with limited query access, which works across
                // elevation (Task 4): MainModule needs VM read access, so a DeadLight running as administrator was invisible to a scan without it.
                string? path=null;
                if(name.Equals("electron",StringComparison.OrdinalIgnoreCase)||name.Equals("OpenRGB",StringComparison.OrdinalIgnoreCase)) path=ImagePath(id);
                list.Add(new(id,name,path));
            }
            return list;
        }
        finally{foreach(var p in processes) p.Dispose();}
    }
    // A process's image path (QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION, as UpdateCore's InstallProcesses reads
    // it), or null when the process is gone or can't be opened.
    public static string? ImagePath(int pid)
    {
        using var process=OpenProcess(0x1000,false,pid);
        if(process.IsInvalid) return null;
        string? path=Query(process,1024,out int error);
        return path is null&&error==122?Query(process,32_768,out _):path;
    }
    static string? Query(SafeProcessHandle process,int capacity,out int error)
    {
        var path=new StringBuilder(capacity);int size=capacity;
        if(QueryFullProcessImageName(process,0,path,ref size)){error=0;return path.ToString();}
        error=Marshal.GetLastWin32Error();return null;
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="QueryFullProcessImageNameW")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
    static string Bare(string name)=>name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?name[..^4]:name;
    static bool Is(string name,string expected)=>string.Equals(name,expected,StringComparison.OrdinalIgnoreCase);
    static LightingConflict? Classify(RunningProcess p,int? ownOpenRgbPid)
    {
        string n=Bare(p.Name);
        if(Is(n,"electron")) return p.Path!=null&&p.Path.Contains(@"\DeadLight\",StringComparison.OrdinalIgnoreCase)?new(ConflictKind.DeadLight,"DeadLight is running. Quit it from its tray icon before turning on DeadWare lighting.",true):null;
        if(Is(n,"OpenRGB")) return p.Id==ownOpenRgbPid?null:new(ConflictKind.ForeignOpenRgb,"Another OpenRGB is running. Quit it before turning on DeadWare lighting.",true);
        if(Is(n,"iCUE")||Is(n,"iCUE Launcher")) return new(ConflictKind.ICue,"iCUE is running. Quit it from its tray icon before turning on DeadWare lighting.",true);
        if(Is(n,"CorsairDeviceControlService")||Is(n,"CorsairDeviceListerService")) return new(ConflictKind.CorsairService,"A Corsair device service is running and would fight DeadWare over the lights. Stop it in Windows Services, or quit iCUE completely, before turning on DeadWare lighting.",true);
        if(Is(n,"Corsair.Service")) return new(ConflictKind.LegacyCorsairService,"The Corsair service is running. It does not block lighting, but it can block the Corsair hub driver later.",false);
        return null;
    }
}
