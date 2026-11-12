using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace WinUiPrototype;

/// <summary>The prototype's only window: Mica or a solid fallback, a title bar and two navigation entries.</summary>
public sealed partial class MainWindow : Window
{
    private readonly OverviewPage overview = new();
    private readonly Page empty = new();

    public MainWindow()
    {
        InitializeComponent();
        bool mica = !App.NoMica && MicaController.IsSupported();
        if (mica) SystemBackdrop = new MicaBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        // The title names the backdrop so a person and the smoke check can both read which path ran.
        Title = TitleText.Text = mica ? "DeadWare prototype (Mica)" : "DeadWare prototype (solid)";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1220 * scale), (int)(900 * scale)));
        string icon = Path.Combine(AppContext.BaseDirectory, "deadware.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        Nav.SelectedItem = OverviewItem;
    }

    /// <summary>Brings the window back from the tray.</summary>
    public void ShowFromTray() => Activate();

    /// <summary>Ends the process from the tray menu.</summary>
    public void QuitFromTray() => Environment.Exit(0);

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        ContentFrame.Content = ReferenceEquals(args.SelectedItem, EmptyItem) ? empty : overview;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
