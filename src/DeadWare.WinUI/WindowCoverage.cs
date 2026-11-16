using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.WinUI;

/// <summary>
/// Whether part of a window can be seen. It cannot when the window is minimised or cloaked (on another virtual desktop), when the part
/// is on no monitor, or when visible windows above it cover every pixel of it. A window that may be see-through (layered,
/// click-through, shaped by a window region, or cloaked) never counts as covering, so a doubtful case is called visible.
/// </summary>
internal static class WindowCoverage
{
    private const int GwHwndPrev = 3, GwlExStyle = -20, DwmwaExtendedFrameBounds = 9, DwmwaCloaked = 14;
    private const long WsExTransparent = 0x20, WsExLayered = 0x80000;
    // The most windows above it that are looked at. Leaving out any beyond can only make the part look less covered.
    private const int MaximumWindows = 256;

    /// <summary>True when <paramref name="area"/> (screen pixels) of <paramref name="window"/> cannot be seen at all.</summary>
    public static bool Hidden(nint window, ScreenRect area)
    {
        if (window == 0 || area.IsEmpty) return false;
        if (IsIconic(window) || Cloaked(window)) return true;
        var bounds = new RectI { Left = area.Left, Top = area.Top, Right = area.Right, Bottom = area.Bottom };
        if (MonitorFromRect(ref bounds, 0 /* MONITOR_DEFAULTTONULL */) == 0) return true;
        var covers = new List<ScreenRect>();
        int looked = 0;
        for (nint above = GetWindow(window, GwHwndPrev); above != 0 && looked < MaximumWindows; above = GetWindow(above, GwHwndPrev), looked++)
        {
            if (!IsWindowVisible(above) || IsIconic(above)) continue;
            if (((long)GetWindowLongPtrW(above, GwlExStyle) & (WsExTransparent | WsExLayered)) != 0) continue;
            // GetWindowRgnBox answers 0 (ERROR) for a window without a region, the usual case; a shaped window may not fill its rectangle.
            if (Cloaked(above) || GetWindowRgnBox(above, out _) != 0) continue;
            if (Frame(above) is { } frame && !frame.Intersect(area).IsEmpty) covers.Add(frame);
        }
        return ScreenCoverage.Covered(area, covers);
    }

    /// <summary>A rectangle of the window's client area, in client pixels, as screen pixels; null when it cannot be converted.</summary>
    public static ScreenRect? ClientToScreen(nint window, double left, double top, double right, double bottom)
    {
        if (!(double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(right) && double.IsFinite(bottom))) return null;
        static int Pixel(double value) => (int)Math.Clamp(value, -1e8, 1e8);
        var first = new PointI { X = Pixel(Math.Floor(left)), Y = Pixel(Math.Floor(top)) };
        var second = new PointI { X = Pixel(Math.Ceiling(right)), Y = Pixel(Math.Ceiling(bottom)) };
        if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref second)) return null;
        return new ScreenRect(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
    }

    private static bool Cloaked(nint window) => DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>The window's visible frame, without the invisible resize borders Windows puts around most windows.</summary>
    private static ScreenRect? Frame(nint window)
    {
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out RectI rect, Marshal.SizeOf<RectI>()) != 0 && !GetWindowRect(window, out rect)) return null;
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PointI { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern int GetWindowRgnBox(nint window, out RectI rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectI rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref PointI point);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref RectI rect, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out RectI value, int size);
}
