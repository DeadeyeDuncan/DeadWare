using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinUiPrototype;

/// <summary>
/// A notification-area icon through Shell_NotifyIcon, with no library. Left click opens; right click offers Open and Quit.
/// It owns a hidden top-level window (not a message-only one, which cannot take the foreground and so could not
/// dismiss its menu on an outside click).
/// </summary>
internal sealed class Tray : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint OpenCommand = 1, QuitCommand = 2;

    private readonly Action open, quit;
    private readonly WindowProcedure procedure; // kept alive for the native window class
    private readonly nint window, icon;
    private bool disposed;

    public Tray(Action open, Action quit)
    {
        this.open = open;
        this.quit = quit;
        procedure = Procedure;
        try
        {
            nint module = GetModuleHandleW(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = module,
                ClassName = "DeadWarePrototypeTray",
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception();
            window = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, windowClass.ClassName, "", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, module, 0);
            if (window == 0) throw new Win32Exception();
            int size = GetSystemMetrics(49 /* SM_CXSMICON */);
            icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "deadware.ico"), 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
            var data = Data();
            data.Flags = 0x1 | 0x2 | 0x4; // NIF_MESSAGE | NIF_ICON | NIF_TIP
            data.CallbackMessage = CallbackMessage;
            data.Icon = icon;
            data.Tip = "DeadWare prototype";
            if (!Shell_NotifyIconW(0 /* NIM_ADD */, ref data)) throw new Win32Exception();
            Available = true;
        }
        catch (Win32Exception error)
        {
            // A machine with no shell must still be measurable: note it and run without a tray.
            App.Note($"Tray unavailable: Win32 error {error.NativeErrorCode} ({error.Message}).");
        }
    }

    /// <summary>False when the icon could not be added; closing the window then ends the app.</summary>
    public bool Available { get; private set; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Available)
        {
            var data = Data();
            Shell_NotifyIconW(2 /* NIM_DELETE */, ref data);
            Available = false;
        }
        if (window != 0) DestroyWindow(window);
        if (icon != 0) DestroyIcon(icon);
    }

    private NotifyIconData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1, Tip = "", Info = "", InfoTitle = "" };

    private nint Procedure(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case 0x0202 or 0x0203: open(); break; // WM_LBUTTONUP, WM_LBUTTONDBLCLK
                case 0x0205: ShowMenu(); break;        // WM_RBUTTONUP
            }
            return 0;
        }
        return DefWindowProcW(handle, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, 0, OpenCommand, "Open");
            AppendMenuW(menu, 0, QuitCommand, "Quit");
            GetCursorPos(out var point);
            SetForegroundWindow(window); // without this the menu does not dismiss on an outside click
            uint command = TrackPopupMenuEx(menu, 0x0100 | 0x0002 /* TPM_RETURNCMD | TPM_RIGHTBUTTON */, point.X, point.Y, window, 0);
            PostMessageW(window, 0, 0, 0);
            if (command == OpenCommand) open();
            else if (command == QuitCommand) quit();
        }
        finally { DestroyMenu(menu); }
    }

    private delegate nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint load);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
