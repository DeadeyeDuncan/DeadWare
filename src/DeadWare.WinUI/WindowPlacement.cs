using System.Runtime.InteropServices;
using DeadWare.Core;

namespace DeadWare.WinUI;

/// <summary>
/// Reads and restores a window's normal rectangle. Every rectangle here and in the saved settings is in physical
/// screen pixels, the format the WPF app saved, so a layout carries over between the two front ends.
/// </summary>
internal static class NativePlacement
{
    /// <summary>The work area of every monitor.</summary>
    public static IReadOnlyList<WorkArea> WorkAreas()
    {
        var areas = new List<WorkArea>();
        MonitorCallback callback = (nint monitor, nint dc, ref RectI bounds, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfoW(monitor, ref info)) areas.Add(new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return areas;
    }

    /// <summary>The window's rectangle, or null while it is minimised, maximised or gone.</summary>
    public static WindowPlacement? CaptureNormal(nint handle, bool topmost)
    {
        if (handle == 0 || IsIconic(handle) || IsZoomed(handle) || !GetWindowRect(handle, out var rect)) return null;
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, topmost);
    }

    /// <summary>The DPI of the monitor a rectangle mostly sits on.</summary>
    public static uint DpiFor(WindowPlacement placement)
    {
        var rect = new RectI { Left = (int)placement.Left, Top = (int)placement.Top, Right = (int)(placement.Left + placement.Width), Bottom = (int)(placement.Top + placement.Height) };
        nint monitor = MonitorFromRect(ref rect, 2 /* MONITOR_DEFAULTTONEAREST */);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi : 96;
    }

    /// <summary>
    /// Moves the window to its saved rectangle, or to the default size centred on its current monitor when nothing was saved.
    /// The result is kept on a monitor and no smaller than the minimum, as the WPF app does. Returns the DPI it used.
    /// </summary>
    public static uint Restore(nint handle, WindowPlacement? saved, double defaultWidthDips, double defaultHeightDips, double minWidthDips, double minHeightDips)
    {
        var current = CaptureNormal(handle, false);
        if (current is null) return 96;
        var areas = WorkAreas();
        // First bound corrupt coordinates so the integer conversion below cannot overflow.
        var bounded = WindowPlacement.Clamp(saved ?? current, areas, 800, 600);
        uint dpi = DpiFor(bounded);
        var defaults = WindowCoordinates.FromDipSize(bounded.Left, bounded.Top, defaultWidthDips, defaultHeightDips, dpi, false);
        WindowPlacement placement;
        if (saved is null)
        {
            var area = Target(bounded, areas);
            double width = Math.Min(defaults.Width, area.Width), height = Math.Min(defaults.Height, area.Height);
            placement = new(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height, false);
        }
        else placement = WindowPlacement.Clamp(saved, areas, defaults.Width, defaults.Height);
        var minimum = WindowCoordinates.FromDipSize(placement.Left, placement.Top, minWidthDips, minHeightDips, dpi, false);
        var target = Target(placement, areas);
        placement = WindowPlacement.Clamp(placement with { Width = Math.Max(placement.Width, minimum.Width), Height = Math.Max(placement.Height, minimum.Height) }, [target], placement.Width, placement.Height);
        SetWindowPos(handle, 0, (int)placement.Left, (int)placement.Top, (int)placement.Width, (int)placement.Height, 0x0014 /* SWP_NOZORDER | SWP_NOACTIVATE */);
        return dpi;
    }

    private static WorkArea Target(WindowPlacement placement, IReadOnlyList<WorkArea> areas)
    {
        var valid = areas.Where(a => double.IsFinite(a.Left) && double.IsFinite(a.Top) && a.Width > 0 && a.Height > 0).ToArray();
        if (valid.Length == 0) return new(0, 0, 1920, 1040);
        return valid.OrderByDescending(a => Math.Max(0, Math.Min(placement.Left + placement.Width, a.Left + a.Width) - Math.Max(placement.Left, a.Left))
            * Math.Max(0, Math.Min(placement.Top + placement.Height, a.Top + a.Height) - Math.Max(placement.Top, a.Top))).First();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref RectI bounds, nint data);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref RectI rect, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectI rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

/// <summary>
/// Tells the app that Windows is signing out, restarting or shutting down. WinUI raises no event for it in an unpackaged
/// app, so the dashboard window is subclassed and WM_ENDSESSION is watched. The callback runs on the UI thread and must
/// finish quickly: Windows ends the process a few seconds later.
/// </summary>
internal sealed class SessionEndHook : IDisposable
{
    private readonly nint window;
    private readonly SubclassProcedure procedure; // kept alive for the native subclass
    private readonly Action ending;
    private bool raised, disposed;

    public SessionEndHook(nint window, Action ending)
    {
        this.window = window;
        this.ending = ending;
        procedure = Procedure;
        if (!SetWindowSubclass(window, procedure, 1, 0)) App.Note("The sign-out hook could not be installed; settings are saved only on Quit.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RemoveWindowSubclass(window, procedure, 1);
    }

    private nint Procedure(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        // WM_ENDSESSION with a true wParam: the session really is ending (a cancelled shutdown sends false).
        if (message == 0x0016 && wParam != 0 && !raised)
        {
            raised = true;
            try { ending(); }
            catch (Exception error) { App.Note($"Saving at sign-out failed: {error}"); }
        }
        return DefSubclassProc(handle, message, wParam, lParam);
    }

    private delegate nint SubclassProcedure(nint handle, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}
