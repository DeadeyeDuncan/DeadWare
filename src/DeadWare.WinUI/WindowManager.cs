using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// Opens, raises, places and remembers the pop-out graph windows, the history windows and the desktop widget. Which of
/// them are open, where each sits and whether it stays on top are saved with the settings, in the format the WPF app
/// used, and reopened at the next start when that setting is on.
/// </summary>
internal sealed class WindowManager
{
    private readonly MonitorSession session;
    private readonly Dictionary<string, Window> open = new(StringComparer.Ordinal);
    private readonly Dictionary<Window, Action> captures = [];
    private bool closing;

    public WindowManager(MonitorSession session)
    {
        this.session = session;
        session.ManageWindows(CaptureAll);
    }

    /// <summary>Reopens the windows remembered from the previous session.</summary>
    public void RestoreRemembered()
    {
        foreach (string key in session.RememberedWindows)
        {
            try { OpenCore(key); }
            catch (Exception error) { App.Note($"The {key} window could not be reopened: {error}"); }
        }
    }

    /// <summary>Opens a pop-out by kind, or raises it. CPU and GPU kinds become the joined window when that setting is on.</summary>
    public void Open(string kind) => OpenCore(PopoutGraphMode.Resolve(kind, session.Settings.JoinCpuGpuPopouts));

    private void OpenCore(string kind)
    {
        if (closing || !DetachedWindowState.IsAllowed(kind) || Raise(kind)) return;
        Window window = kind == "widget" ? new WidgetWindow(session, this) : new PopoutWindow(session, this, kind);
        open[kind] = window;
        window.Closed += (_, _) => { open.Remove(kind); session.WindowClosed(kind); };
        window.Activate();
        session.WindowOpened(kind);
    }

    /// <summary>Opens (or raises) one drive's pop-out; it closes with the app but is not remembered across restarts.</summary>
    public void OpenDrive(StorageDrive drive)
    {
        string key = "storage:" + drive.Id;
        if (closing || Raise(key)) return;
        var window = new PopoutWindow(session, this, drive);
        open[key] = window;
        window.Closed += (_, _) => open.Remove(key);
        window.Activate();
    }

    private bool Raise(string key)
    {
        if (!open.TryGetValue(key, out var existing)) return false;
        if (existing.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        existing.Activate();
        return true;
    }

    /// <summary>
    /// Gives a new window its icon, backdrop, minimum size and position. With a key, the saved rectangle and Always on top
    /// choice are restored and every later move, resize or pin is saved under that key.
    /// </summary>
    public void Place(Window window, Panel root, string? key, double width, double height, double minWidth, double minHeight)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!App.NoMica && MicaController.IsSupported()) window.SystemBackdrop = new MicaBackdrop();
        else root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        string icon = Path.Combine(AppContext.BaseDirectory, "deadware.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
        // A standard title bar is light unless the window asks otherwise, whatever the app theme: have it follow the content.
        void MatchTitleBar()
        {
            int dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        }
        root.ActualThemeChanged += (_, _) => MatchTitleBar();
        root.Loaded += (_, _) => MatchTitleBar();
        MatchTitleBar();

        WindowPlacement? saved = key is not null && session.Settings.Windows.TryGetValue(key, out var found) ? found : null;
        var presenter = window.AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null) presenter.IsAlwaysOnTop = key == "widget" ? session.Settings.WidgetAlwaysOnTop : saved?.Topmost ?? false;
        uint dpi = NativePlacement.Restore(handle, saved, width, height, minWidth, minHeight);
        if (presenter is not null)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * dpi / 96.0);
            presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * dpi / 96.0);
        }
        if (key is null) return;

        WindowPlacement? last = null;
        void Capture()
        {
            bool topmost = presenter?.IsAlwaysOnTop ?? false;
            if (NativePlacement.CaptureNormal(handle, topmost) is { } placement) last = placement;
            if (last is not null) session.SetWindowPlacement(key, last with { Topmost = topmost });
        }
        captures[window] = Capture;
        window.AppWindow.Changed += (_, args) => { if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) Capture(); };
        window.Closed += (_, _) => { Capture(); captures.Remove(window); };
        Capture();
    }

    public static bool IsTopmost(Window window) => window.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };

    /// <summary>Pins or unpins a window and saves the choice with its placement.</summary>
    public void SetTopmost(Window window, bool topmost)
    {
        if (window.AppWindow.Presenter is not OverlappedPresenter presenter || presenter.IsAlwaysOnTop == topmost) return;
        presenter.IsAlwaysOnTop = topmost;
        if (captures.TryGetValue(window, out var capture)) capture();
    }

    private void CaptureAll()
    {
        foreach (var capture in captures.Values.ToArray()) capture();
    }

    /// <summary>Closes every window during quit. The session has already recorded which were open.</summary>
    public void CloseAll()
    {
        closing = true;
        foreach (var window in open.Values.ToArray())
        {
            try { window.Close(); } catch (Exception error) { App.Note($"A window did not close cleanly: {error.Message}"); }
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
