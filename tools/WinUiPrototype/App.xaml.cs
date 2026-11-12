using Microsoft.UI.Xaml;

namespace WinUiPrototype;

/// <summary>Throwaway prototype entry point: parses the command line and records any unhandled error.</summary>
public partial class App : Application
{
    /// <summary>True when <c>--no-mica</c> forces the solid fallback background.</summary>
    internal static bool NoMica { get; private set; }
    /// <summary>The path given after <c>--error-file</c>, or null.</summary>
    internal static string? ErrorFile { get; private set; }

    private MainWindow? window;

    public App()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--no-mica") NoMica = true;
            else if (args[i] == "--error-file" && i + 1 < args.Length) ErrorFile = args[++i];
        }
        UnhandledException += (_, e) => Fail(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail(e.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
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
