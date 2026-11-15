using System.Runtime.InteropServices;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeadWare.WinUI;

/// <summary>The dashboard window: Mica or a solid fallback, the twelve destinations, and hide-to-tray on close.</summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The WPF dashboard's destinations, in its order and with its labels.</summary>
    private static readonly (string Key, string Label, Symbol Icon)[] Destinations =
    [
        ("overview", "Overview", Symbol.Home), ("network", "Network", Symbol.World), ("storage", "Storage", Symbol.SaveLocal),
        ("processes", "Processes", Symbol.AllApps), ("connections", "Connections", Symbol.Globe), ("battery", "Battery history", Symbol.Clock),
        ("usage", "Usage history", Symbol.Calendar), ("alerts", "Alerts", Symbol.Important), ("sensors", "Sensors", Symbol.List),
        ("cooling", "Cooling Health", Symbol.Repair), ("desktop", "Settings", Symbol.Setting), ("updates", "Updates", Symbol.Sync),
    ];

    private readonly Dictionary<string, (NavigationViewItem Item, Page Page)> pages = [];
    private readonly MonitorSession session;
    private readonly ApplicationLifetime lifetime;
    private readonly Func<bool> trayAvailable;
    private readonly Func<Task> quit;
    private string current = "overview";
    private bool closingForExit, syncingRange;
    private readonly List<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> ranges = [];

    /// <summary>The pages whose graphs or statistics follow the shared history range, as in the WPF header.</summary>
    private static bool FollowsRange(string key) => key is "network" or "storage" or "sensors" or "desktop";

    public MainWindow(MonitorSession session, ApplicationLifetime lifetime, Func<bool> trayAvailable, Func<Task> quit)
    {
        this.session = session;
        this.lifetime = lifetime;
        this.trayAvailable = trayAvailable;
        this.quit = quit;
        InitializeComponent();
        bool mica = !App.NoMica && MicaController.IsSupported();
        if (mica) SystemBackdrop = new MicaBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        Title = TitleText.Text = "DeadWare";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        App.WindowHandle = handle;
        double scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1220 * scale), (int)(900 * scale)));
        string icon = Path.Combine(AppContext.BaseDirectory, "deadware.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        foreach (var (key, label, symbol) in Destinations)
        {
            var item = new NavigationViewItem { Content = label, Icon = new SymbolIcon(symbol), Tag = key };
            Page page = key switch
            {
                "overview" => new OverviewPage(session),
                "network" => new NetworkPage(session),
                "processes" => new ProcessesPage(session, quit),
                "storage" => new StoragePage(session),
                "battery" => new BatteryPage(session),
                "updates" => new UpdatesPage(session, quit),
                "alerts" => new AlertsPage(session),
                "cooling" => new CoolingPage(session),
                "usage" => new UsagePage(session),
                "connections" => new ConnectionsPage(session),
                "desktop" => new SettingsPage(session, quit) { NavigateRequested = Navigate },
                "sensors" => new SensorsPage(session),
                _ => new PlaceholderPage(label),
            };
            pages[key] = (item, page);
            Nav.MenuItems.Add(item);
        }
        Navigate("overview");
        TrayNotice.Text = "Close hides to the system tray.\nQuit stops monitoring.";
        // Offered only where it would change anything, as in the WPF sidebar.
        ElevateButton.Visibility = Collector.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ElevateButton, "Some readings, such as low-level sensors and per-app network capture, need DeadWare to run as administrator. Restarting saves your workspace first.");
        // The shared graph history range, one setting every chart follows, as on Overview.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RangeRow, "Graph history range");
        ToolTipService.SetToolTip(RangeRow, "Choose how much graph history to show");
        ToolTipService.SetToolTip(OpenWidget, "Open the Quick glance widget in its own window");
        foreach (int seconds in HistoryRanges.Seconds)
        {
            string label = HistoryRanges.Label(seconds);
            var segment = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = label, Tag = seconds, Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(segment, label + " history");
            segment.Checked += (_, _) => { if (!syncingRange) session.SetRange(seconds); };
            // A segment never unchecks itself; the next choice replaces it.
            segment.Unchecked += (_, _) => { if (!syncingRange && session.HistorySeconds == seconds) segment.IsChecked = true; };
            ranges.Add(segment);
            RangeRow.Children.Add(segment);
        }
        // The foot of the pane has no room in the narrow pane.
        Nav.PaneOpening += (_, _) => PaneFoot.Visibility = Visibility.Visible;
        Nav.PaneClosing += (_, _) => PaneFoot.Visibility = Visibility.Collapsed;
        PaneFoot.Visibility = Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        // The header, status lines and pane foot follow each reading only while the dashboard is on screen: in the tray
        // they would be rewritten twice a second for nobody, and they catch up when the window returns.
        Root.WhileShowing(() => { session.Changed += RefreshShell; RefreshShell(); }, () => session.Changed -= RefreshShell);
        // The update service reports from its own thread.
        session.Updates.Changed += () => DispatcherQueue.TryEnqueue(RefreshUpdateNotice);
        RefreshShell();
        RefreshUpdateNotice();
        session.UsageDayRequested += at => { Navigate("usage"); if (pages["usage"].Page is UsagePage usage) usage.ShowDay(at); };
        session.ConnectionsHistoryRequested += request => { ShowFromTray(); Navigate("connections"); if (pages["connections"].Page is ConnectionsPage connections) connections.ShowHistory(request); };

        AppWindow.Closing += (_, args) =>
        {
            if (closingForExit) return;
            switch (lifetime.GetDashboardCloseBehavior(trayAvailable()))
            {
                case DashboardCloseBehavior.Hide: args.Cancel = true; AppWindow.Hide(); break;
                case DashboardCloseBehavior.KeepVisible: args.Cancel = true; break;
            }
        };
    }

    /// <summary>Selects a destination by its key; unknown keys are ignored.</summary>
    public void Navigate(string key)
    {
        if (pages.TryGetValue(key, out var destination)) Nav.SelectedItem = destination.Item;
    }

    /// <summary>Brings the window back from the tray, and up from the taskbar if it was minimized.</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    /// <summary>
    /// Puts the dashboard back where it was last left, keeps it from shrinking below the WPF dashboard's minimum and saves
    /// its later moves, under the key the WPF dashboard uses so either app opens where the other left it.
    /// </summary>
    internal void RememberPlacement(WindowManager manager) => manager.Place(this, Root, "main", 1220, 900, 900, 640);

    /// <summary>Says that closing cannot hide to a tray that is not there, as the WPF sidebar does.</summary>
    public void SetTrayUnavailable() => TrayNotice.Text = "System tray unavailable. Keep this window open; use Quit DeadWare to exit.";

    /// <summary>What the pane's foot says is running, and the Battery history entry, which a PC without a battery does not get.</summary>
    private void RefreshShell()
    {
        if (lifetime.IsEnding) return;
        MonitoringInfo.Text = $"Local monitoring\n{session.Settings.PollingIntervalSeconds:0.#} s polling · 6 h saved graphs · usage history\nv{App.Version}";
        var reading = session.Current;
        PageSubtitle.Text = reading is null ? "Discovering hardware and starting the shared collector…"
            : $"{reading.CpuName} · {session.GpuName} · {HistoryChartModel.Bytes(session.Value("memory/total"))} · last sample {Math.Max(0, (DateTimeOffset.Now - reading.Sample.Timestamp).TotalSeconds):0.0} s ago";
        StatusLines.Text = OverviewPresenter.Status(reading is not null, session.Stale, reading?.HardwareStatus ?? "", reading?.NetworkStatus ?? "", session.GraphHistory.Status,
            session.SettingsStatus, session.Settings.ShowCpuCoreUsage, reading?.CpuCoreCount ?? 0);
        syncingRange = true;
        try { foreach (var segment in ranges) segment.IsChecked = segment.Tag is int seconds && seconds == session.HistorySeconds; }
        finally { syncingRange = false; }
        bool noBattery = session.Current?.Battery.PowerState == BatteryPowerState.NoBattery;
        pages["battery"].Item.Visibility = noBattery ? Visibility.Collapsed : Visibility.Visible;
        if (noBattery && current == "battery") Navigate("overview");
    }

    /// <summary>The banner for a newer release, on every page but Updates itself.</summary>
    private void RefreshUpdateNotice()
    {
        if (lifetime.IsEnding) return;
        var state = session.Updates.State;
        bool show = state.HasNewerRelease && current != "updates";
        if (show) UpdateNotice.Title = $"DeadWare {state.Release!.Version} is available.";
        UpdateNotice.IsOpen = show;
    }

    private void OnReviewUpdate(object sender, RoutedEventArgs args) => Navigate("updates");

    private void OnOpenWidget(object sender, RoutedEventArgs args) => App.Popouts?.Open("widget");

    private async void OnElevate(object sender, RoutedEventArgs args)
    {
        ElevateButton.IsEnabled = false;
        string result;
        try { result = await session.RestartElevatedAsync(quit) ?? ""; }
        catch (Exception error) { result = $"DeadWare could not restart as administrator: {error.Message}"; }
        ElevateResult.Text = result;
        ElevateResult.Visibility = result.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.IsEnabled = true;
    }

    private async void OnQuit(object sender, RoutedEventArgs args)
    {
        QuitButton.IsEnabled = false;
        await quit();
    }

    /// <summary>Closes the window for good during quit, bypassing hide-to-tray.</summary>
    public void CloseForExit()
    {
        closingForExit = true;
        Close();
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string key } item && pages.TryGetValue(key, out var destination))
        {
            PageTitle.Text = item.Content as string ?? "";
            ContentFrame.Content = destination.Page;
            current = key;
            // Overview carries its own toolbar (with the GPU choice) and status card; the other pages get the shared ones.
            HeaderTools.Visibility = key == "overview" ? Visibility.Collapsed : Visibility.Visible;
            RangeRow.Visibility = FollowsRange(key) ? Visibility.Visible : Visibility.Collapsed;
            StatusLines.Visibility = key == "overview" ? Visibility.Collapsed : Visibility.Visible;
            RefreshUpdateNotice();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
