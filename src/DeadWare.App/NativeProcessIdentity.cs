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
        if(process.IsInvalid||!GetProcessTimes(process,out long created,out long exited,out _,out _)||exited!=0)return null;
        var path=new StringBuilder(1024);int size=path.Capacity;
        string name=QueryFullProcessImageName(process,0,path,ref size)?System.IO.Path.GetFileName(path.ToString()):$"PID {pid}";
        return new(pid,DateTime.FromFileTimeUtc(created).Ticks,name);
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
}
