using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeadWare.Core;
namespace DeadWare.App;
internal static class NativePlacement
{
    private static readonly DependencyProperty ScrollableMinimumHeightProperty=DependencyProperty.RegisterAttached("ScrollableMinimumHeight",typeof(double),typeof(NativePlacement),new PropertyMetadata(double.NaN));
    public static double MinimumHeightForWorkArea(WindowPlacement placement,IReadOnlyList<WorkArea> areas,double requestedHeightDips,uint dpi)
    {
        var target=TargetArea(placement,areas).FirstOrDefault();
        return target is null?requestedHeightDips:Math.Min(requestedHeightDips,target.Height/(dpi>0?dpi/96d:1));
    }
    public static void SetScrollableMinimumHeight(Window window,double requestedHeightDips,WindowPlacement? target=null)
    {
        window.SetValue(ScrollableMinimumHeightProperty,requestedHeightDips);
        var placement=CaptureNormal(window)??target;
        if(placement is null) {window.MinHeight=requestedHeightDips;return;}
        var areas=WorkAreas();var bounded=WindowPlacement.Clamp(placement,areas,800,600);
        window.MinHeight=MinimumHeightForWorkArea(bounded,areas,requestedHeightDips,TargetDpi(bounded));
    }
    public static void ClearScrollableMinimumHeight(Window window)=>window.ClearValue(ScrollableMinimumHeightProperty);
    private static void ApplyScrollableMinimum(Window window,WindowPlacement placement,IReadOnlyList<WorkArea> areas,uint dpi)
    {
        double requested=(double)window.GetValue(ScrollableMinimumHeightProperty);
        if(double.IsFinite(requested))window.MinHeight=MinimumHeightForWorkArea(placement,areas,requested,dpi);
    }
    private static IReadOnlyList<WorkArea> TargetArea(WindowPlacement placement,IReadOnlyList<WorkArea> areas)=>areas
        .Where(a=>double.IsFinite(a.Left)&&double.IsFinite(a.Top)&&double.IsFinite(a.Width)&&double.IsFinite(a.Height)&&a.Width>0&&a.Height>0)
        .OrderByDescending(a=>Math.Max(0,Math.Min(placement.Left+placement.Width,a.Left+a.Width)-Math.Max(placement.Left,a.Left))*Math.Max(0,Math.Min(placement.Top+placement.Height,a.Top+a.Height)-Math.Max(placement.Top,a.Top)))
        .Take(1).ToArray();
    private static uint TargetDpi(WindowPlacement bounded)
    {
        var rect=new RectI {Left=(int)bounded.Left,Top=(int)bounded.Top,Right=(int)(bounded.Left+bounded.Width),Bottom=(int)(bounded.Top+bounded.Height)};
        var target=MonitorFromRect(ref rect,2);
        return GetDpiForMonitor(target,0,out uint dpi,out _)==0&&dpi>0?dpi:96;
    }
    // Every rectangle in this class and persisted settings is in physical screen pixels.
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
    public static WindowPlacement? CaptureNormal(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;
        if(handle==IntPtr.Zero||IsIconic(handle)||IsZoomed(handle)||!GetWindowRect(handle,out var rect))return null;
        return new(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top,window.Topmost);
    }
    public static void Restore(Window window,WindowPlacement? saved,double defaultWidthDips,double defaultHeightDips)
    {
        var handle=new WindowInteropHelper(window).Handle;
        var current=CaptureNormal(window);if(current is null)return;
        var areas=WorkAreas();
        // First bound corrupt coordinates so native integer conversion cannot overflow.
        var bounded=WindowPlacement.Clamp(saved??current,areas,800,600);
        uint dpi=TargetDpi(bounded);
        var defaults=WindowCoordinates.FromDipSize(bounded.Left,bounded.Top,defaultWidthDips,defaultHeightDips,dpi,window.Topmost);
        var placement=WindowPlacement.Clamp(saved??current,areas,defaults.Width,defaults.Height);
        ApplyScrollableMinimum(window,placement,areas,dpi);
        placement=ClampMinimum(placement,areas,window.MinWidth,window.MinHeight,dpi);
        // SetWindowPos uses the same physical coordinates. WPF processes WM_DPICHANGED itself;
        // assigning these pixels to Window.Left/Width (DIPs) would apply scaling a second time.
        SetWindowPos(handle,IntPtr.Zero,(int)placement.Left,(int)placement.Top,(int)placement.Width,(int)placement.Height,0x0014);
    }
    public static WindowPlacement ClampMinimum(WindowPlacement placement,IReadOnlyList<WorkArea> areas,double minWidthDips,double minHeightDips,uint dpi)
    {
        // Grow in target-monitor pixels before clamping position, so old small layouts remain on screen.
        // Select the work area from the original bounded rectangle, not the enlarged footprint.
        var target=TargetArea(placement,areas);
        var minimum=WindowCoordinates.FromDipSize(placement.Left,placement.Top,minWidthDips,minHeightDips,dpi,placement.Topmost);
        return WindowPlacement.Clamp(placement with {Width=Math.Max(placement.Width,minimum.Width),Height=Math.Max(placement.Height,minimum.Height)},target,placement.Width,placement.Height);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectI {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public RectI Monitor,Work;public uint Flags;}
    private delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,ref RectI bounds,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RectI rect,uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out RectI rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
