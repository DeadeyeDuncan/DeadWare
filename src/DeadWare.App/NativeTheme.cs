using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace DeadWare.App;

internal static class NativeTheme
{
    private const uint UseImmersiveDarkMode=20,BorderColor=34,CaptionColor=35,TextColor=36,SystemBackdropType=38,DefaultColor=0xFFFFFFFF;
    private const uint BackdropNone=1,BackdropAcrylic=3;
    // The system backdrop attribute is public from Windows 11 build 22621.
    public static bool BackdropSupported=>OperatingSystem.IsWindowsVersionAtLeast(10,0,22621);
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
        bool backdrop=Theme.BackdropActive;
        // Windows replaces the acrylic of an inactive window with a flat gray, so unfocused
        // windows show the theme's solid surface until they are activated again.
        bool clear=backdrop&&window.IsActive;
        Set(handle,UseImmersiveDarkMode,!highContrast&&Theme.IsDark?1u:0u);
        ApplyBackdrop(handle,backdrop,clear);
        // A default caption lets the acrylic backdrop continue through the title bar.
        Set(handle,CaptionColor,highContrast||clear?DefaultColor:ColorRef(window.IsActive?"Panel":"Background"));
        Set(handle,TextColor,highContrast?DefaultColor:ColorRef(window.IsActive?"Ink":"Muted"));
        Set(handle,BorderColor,highContrast?DefaultColor:ColorRef(window.IsActive?"Line":"Control"));
    }
    private static void ApplyBackdrop(IntPtr handle,bool backdrop,bool clear)
    {
        if(!BackdropSupported)return;
        // WPF must clear its composition surface for the DWM backdrop to show through the
        // translucent WindowSurface; solid mode restores an opaque surface and frame.
        if(HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor=clear?Colors.Transparent:((SolidColorBrush)Theme.Brush("Background")).Color;
        var margins=backdrop?new Margins(-1):new Margins(0);
        _=DwmExtendFrameIntoClientArea(handle,ref margins);
        Set(handle,SystemBackdropType,backdrop?BackdropAcrylic:BackdropNone);
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
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Margins(int all)
    {
        private readonly int left=all,right=all,top=all,bottom=all;
    }
    [DllImport("dwmapi.dll",PreserveSig=true)]
    private static extern int DwmSetWindowAttribute(IntPtr window,uint attribute,ref uint value,int size);
    [DllImport("dwmapi.dll",PreserveSig=true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
}
