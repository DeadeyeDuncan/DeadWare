using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeadWare.Core;
namespace DeadWare.App;
internal static class NativePlacement
{
    public static IReadOnlyList<WorkArea> WorkAreas()
    {
        var areas=new List<WorkArea>();
        MonitorCallback callback=(IntPtr monitor,IntPtr dc,ref RectI bounds,IntPtr data)=>
        {
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(monitor,ref info))areas.Add(new(info.Work.Left,info.Work.Top,info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,callback,IntPtr.Zero);
        return areas;
    }
    public static void Clamp(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;
        if(!GetWindowRect(handle,out var rect)) return;
        var monitor=MonitorFromWindow(handle,2);var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(monitor,ref info)) return;
        var work=info.Work;int w=Math.Min(rect.Right-rect.Left,work.Right-work.Left),h=Math.Min(rect.Bottom-rect.Top,work.Bottom-work.Top);
        SetWindowPos(handle,IntPtr.Zero,Math.Clamp(rect.Left,work.Left,work.Right-w),Math.Clamp(rect.Top,work.Top,work.Bottom-h),w,h,0x0014);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public RectI Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    private delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref RectI bounds,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out RectI rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
