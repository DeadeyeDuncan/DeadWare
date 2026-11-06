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
    internal const int PathCapacity=1024,LongPathCapacity=32_768;
    private const int ErrorInsufficientBuffer=122;
    /// <summary>Reads the image path into a buffer of <paramref name="capacity"/> characters: the path, or null with the Win32 error.</summary>
    internal delegate string? PathQuery(int capacity,out int error);
    /// <summary>The image path in Win32 form (flags 0); null when it cannot be read.</summary>
    internal static string? ImagePath(SafeProcessHandle process)=>ImagePath((int capacity,out int error)=>QueryImagePath(process,capacity,out error));
    /// <summary>Asks with 1,024 characters and, only when Windows answers ERROR_INSUFFICIENT_BUFFER, once more with 32,768, the longest Win32 path and its terminator. Any other failure is null.</summary>
    internal static string? ImagePath(PathQuery query)
    {
        string? path=query(PathCapacity,out int error);
        return path is null&&error==ErrorInsufficientBuffer?query(LongPathCapacity,out _):path;
    }
    private static string? QueryImagePath(SafeProcessHandle process,int capacity,out int error)
    {
        var path=new StringBuilder(capacity);int size=capacity;
        if(QueryFullProcessImageName(process,0,path,ref size)){error=0;return path.ToString();}
        error=Marshal.GetLastWin32Error();return null;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder name,ref int size);
}
