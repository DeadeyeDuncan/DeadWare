using System.Runtime.InteropServices;
using System.Text;
using DeadWare.Core;
using Microsoft.Win32.SafeHandles;
namespace DeadWare.App;

internal static class NativeProcessIdentity
{
    public static ProcessIdentity? Read(int pid)
    {
        if(pid<=0)return null;
        // Query one process directly; do not enumerate every process for each ETW callback.
        using var process=OpenProcess(0x1000,false,pid);
        if(process.IsInvalid||Times(process) is not {Exited:false} times)return null;
        string name=ImagePath(process) is {} path?System.IO.Path.GetFileName(path):$"PID {pid}";
        return new(pid,times.Generation,name);
    }
    /// <summary>The process's Generation (its creation time as <see cref="DateTime"/> ticks) and whether it has exited; null when the times cannot be read.</summary>
    internal static (long Generation,bool Exited)? Times(SafeProcessHandle process)=>
        GetProcessTimes(process,out long created,out long exited,out _,out _)?(DateTime.FromFileTimeUtc(created).Ticks,exited!=0):null;
    /// <summary>The image path in Win32 form (flags 0); null when it cannot be read.</summary>
    internal static string? ImagePath(SafeProcessHandle process)
    {
        var path=new StringBuilder(1024);int size=path.Capacity;
        return QueryFullProcessImageName(process,0,path,ref size)?path.ToString():null;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
}
