using Microsoft.UI.Xaml;

namespace WinUiPrototype;

/// <summary>Throwaway prototype entry point: parses the command line and records any unhandled error.</summary>
public partial class App : Application
{
    /// <summary>True when <c>--no-mica</c> forces the solid fallback background.</summary>
    internal static bool NoMica { get; private set; }
    /// <summary>True when <c>--smooth</c> has the compositor slide each drawn chart between samples, as the WPF charts' transform animation does.</summary>
    internal static bool Smooth { get; private set; }
    /// <summary>True when <c>--every-frame</c> redraws every chart on every frame: the worst case, like the WPF map's animation.</summary>
    internal static bool EveryFrame { get; private set; }
    /// <summary>The path given after <c>--error-file</c>, or null.</summary>
    internal static string? ErrorFile { get; private set; }

    /// <summary>Seconds after which the app records its frame count and exits 0; null in normal use.</summary>
    private static int? exitAfter;

    private static Timer? exitTimer;
    private MainWindow? window;

    public App()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--no-mica") NoMica = true;
            else if (args[i] == "--smooth") Smooth = true;
            else if (args[i] == "--every-frame") EveryFrame = true;
            else if (args[i] == "--error-file" && i + 1 < args.Length) ErrorFile = args[++i];
            else if (args[i] == "--exit-after" && i + 1 < args.Length && int.TryParse(args[++i], out int seconds) && seconds > 0) exitAfter = seconds;
        }
        UnhandledException += (_, e) => Fail(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail(e.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
        if (exitAfter is not { } seconds) return;
        // A thread-pool timer, so a saturated UI thread cannot stop the app from reporting and exiting.
        exitTimer = new Timer(_ =>
        {
            if (ErrorFile is not null)
            {
                File.WriteAllText(ErrorFile + ".frames", Interlocked.Read(ref OverviewPage.FramesDrawn).ToString());
                File.WriteAllText(ErrorFile + ".ticks", Interlocked.Read(ref OverviewPage.Ticks).ToString());
            }
            Environment.Exit(0);
        }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Appends a line to the error file without ending the process.</summary>
    internal static void Note(string text)
    {
        if (ErrorFile is null) return;
        try { File.AppendAllText(ErrorFile, text + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void Fail(Exception? error)
    {
        Note(error?.ToString() ?? "Unknown unhandled error.");
        Environment.Exit(1);
    }
}
