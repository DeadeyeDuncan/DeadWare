using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace DeadWare.App;

internal static class NativeTheme
{
    private const uint UseImmersiveDarkMode=20,BorderColor=34,CaptionColor=35,TextColor=36,DefaultColor=0xFFFFFFFF;
    public static void Attach(Window window)
    {
        window.SourceInitialized+=(_,_)=>Apply(window);
        window.Activated+=(_,_)=>Apply(window);
        window.Deactivated+=(_,_)=>Apply(window);
    }
    public static void RefreshOpenWindows()
    {
        if(Application.Current is { } app)foreach(Window window in app.Windows)Apply(window);
    }
    private static void Apply(Window window)
    {
        // These public DWM attributes are supported from Windows 11 build 22000.
        // Older systems retain their standard native frame and all window behaviors.
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))return;
        var handle=new WindowInteropHelper(window).Handle;if(handle==IntPtr.Zero)return;
        bool highContrast=SystemParameters.HighContrast;
        Set(handle,UseImmersiveDarkMode,!highContrast&&Theme.IsDark?1u:0u);
        Set(handle,CaptionColor,highContrast?DefaultColor:ColorRef(window.IsActive?"Panel":"Background"));
        Set(handle,TextColor,highContrast?DefaultColor:ColorRef(window.IsActive?"Ink":"Muted"));
        Set(handle,BorderColor,highContrast?DefaultColor:ColorRef(window.IsActive?"Line":"Control"));
    }
    private static uint ColorRef(string resource)
    {
        var color=((SolidColorBrush)Theme.Brush(resource)).Color;
        return (uint)(color.R|(color.G<<8)|(color.B<<16));
    }
    private static void Set(IntPtr handle,uint attribute,uint value)
    {
        // An unsupported attribute or unavailable compositor leaves the native default intact.
        _=DwmSetWindowAttribute(handle,attribute,ref value,sizeof(uint));
    }
    [DllImport("dwmapi.dll",PreserveSig=true)]
    private static extern int DwmSetWindowAttribute(IntPtr window,uint attribute,ref uint value,int size);
}
