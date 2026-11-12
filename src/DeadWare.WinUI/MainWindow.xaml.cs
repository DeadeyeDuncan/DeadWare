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
    private readonly ApplicationLifetime lifetime;
    private readonly Func<bool> trayAvailable;
    private bool closingForExit;

    public MainWindow(MonitorSession session, ApplicationLifetime lifetime, Func<bool> trayAvailable)
    {
        this.lifetime = lifetime;
        this.trayAvailable = trayAvailable;
        InitializeComponent();
        bool mica = !App.NoMica && MicaController.IsSupported();
        if (mica) SystemBackdrop = new MicaBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        Title = TitleText.Text = "DeadWare";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1220 * scale), (int)(900 * scale)));
        string icon = Path.Combine(AppContext.BaseDirectory, "deadware.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        foreach (var (key, label, symbol) in Destinations)
        {
            var item = new NavigationViewItem { Content = label, Icon = new SymbolIcon(symbol), Tag = key };
            Page page = key == "overview" ? new StatusPage(session, mica) : new PlaceholderPage(label);
            pages[key] = (item, page);
            Nav.MenuItems.Add(item);
        }
        Navigate("overview");

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

    /// <summary>Brings the window back from the tray.</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
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
            Nav.Header = item.Content;
            ContentFrame.Content = destination.Page;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
