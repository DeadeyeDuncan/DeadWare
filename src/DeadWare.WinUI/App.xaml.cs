using System.Reflection;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeadWare.WinUI;

/// <summary>Start-up, single-instance handling, tray notifications and shutdown for the WinUI front end.</summary>
public partial class App : Application
{
    /// <summary>True when <c>--no-mica</c> forces the solid background used where Mica is unavailable.</summary>
    internal static bool NoMica { get; private set; }
    /// <summary>The path given after <c>--error-file</c>, or null.</summary>
    internal static string? ErrorFile { get; private set; }
    /// <summary>One-second ticks on which the session held a reading; written by <c>--exit-after</c> for the smoke check.</summary>
    internal static long TicksWithReading;

    internal static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static int? exitAfter;
    private static Timer? exitTimer;

    private readonly ApplicationLifetime lifetime = new();
    private readonly string[] arguments = Environment.GetCommandLineArgs()[1..];
    private DispatcherQueue? queue;
    private GuiInstance? instance;
    private MonitorSession? session;
    private MainWindow? window;
    private Tray? tray;

    public App()
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--no-mica") NoMica = true;
            else if (arguments[i] == "--error-file" && i + 1 < arguments.Length) ErrorFile = arguments[++i];
            else if (arguments[i] == "--exit-after" && i + 1 < arguments.Length && int.TryParse(arguments[++i], out int seconds) && seconds > 0) exitAfter = seconds;
        }
        UnhandledException += (_, e) => Fail(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail(e.ExceptionObject as Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        queue = DispatcherQueue.GetForCurrentThread();
        try
        {
            await GuiInstance.WaitForPreviousAsync(arguments);
            instance = new GuiInstance();
            if (!instance.Acquired)
            {
                // The WPF app and this one share the single-instance marker, so they never write the same histories at once.
                Note("DeadWare is already running in this Windows session.");
                Exit();
                return;
            }
            var cities = Cities.Load();
            session = new MonitorSession(Version, () => new QueueTimer(queue), id => id is not null && cities.TryGetValue(id, out var point) ? (id, point) : null);
            window = new MainWindow(session, lifetime, () => tray is { Available: true });
            tray = new Tray(ShowDashboard, RequestQuit);
            Subscribe(session);
            session.Changed += () => { if (session.Current is not null) Interlocked.Increment(ref TicksWithReading); };

            bool windowsStartup = arguments.Contains("--startup") && !arguments.Contains("--restart-from");
            if (ApplicationLifetime.ShouldShowDashboardOnLaunch(windowsStartup, session.Settings.StartInTray, tray.Available)) window.Activate();
            session.Start();

            if (exitAfter is { } seconds)
            {
                // A thread-pool timer starts the ordinary quit path, so the smoke check also proves a clean shutdown.
                exitTimer = new Timer(_ => queue.TryEnqueue(() =>
                {
                    if (ErrorFile is not null) File.WriteAllText(ErrorFile + ".status", $"readings={Interlocked.Read(ref TicksWithReading)}");
                    RequestQuit();
                }), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception error)
        {
            Note($"DeadWare could not start: {error}");
            await QuitAsync(1);
        }
    }

    /// <summary>Appends a line to the error file without ending the process.</summary>
    internal static void Note(string text)
    {
        if (ErrorFile is null) return;
        try { File.AppendAllText(ErrorFile, text + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Tray balloons for cooling warnings, alerts, first network use and available updates, as the WPF app shows them.</summary>
    private void Subscribe(MonitorSession session)
    {
        void OnUi(Action action)
        {
            if (lifetime.IsEnding) return;
            queue!.TryEnqueue(() => { if (!lifetime.IsEnding) action(); });
        }
        session.Cooling.WarningRaised += warning => OnUi(() =>
        {
            if (session.Settings.CoolingNotifications) tray?.ShowBalloon("DeadWare · Cooling change", warning.Message, warning: true, onClick: null);
        });
        session.Alerts.EpisodeStarted += episode => OnUi(() =>
        {
            if (!session.Settings.AlertNotifications) return;
            var rule = session.Settings.Alerts.FirstOrDefault(r => r.Id == episode.RuleId);
            string peak = rule is null ? episode.Peak.ToString("0.#") : AlertRules.FormatValue(rule, episode.Peak);
            tray?.ShowBalloon("DeadWare alert", $"{episode.Summary}. Observed {peak} at {episode.Start.ToLocalTime():t}. Open Alerts to review.", warning: true, ShowAlerts);
        });
        session.Alerts.FirstUseGroupClosed += group => OnUi(() =>
        {
            if (session.Settings.AlertNotifications)
                tray?.ShowBalloon("DeadWare alert", FirstNetworkUse.BalloonText(group, TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture), warning: true, ShowAlerts);
        });
    }

    private void ShowDashboard()
    {
        if (lifetime.IsEnding) return;
        window?.ShowFromTray();
    }

    private void ShowAlerts()
    {
        ShowDashboard();
        if (!lifetime.IsEnding) window?.Navigate("alerts");
    }

    private async void RequestQuit() => await QuitAsync();

    /// <summary>Stops the session, removes the tray icon and leaves through the ordinary WinUI exit.</summary>
    private Task QuitAsync(int exitCode = 0) => lifetime.QuitAsync(async () =>
    {
        try { if (session is not null) await session.ShutdownAsync(); }
        catch (Exception error) { Note($"DeadWare reported a shutdown error: {error}"); exitCode = exitCode == 0 ? 2 : exitCode; }
    }, () =>
    {
        try { tray?.Dispose(); tray = null; instance?.Dispose(); }
        finally
        {
            // Exit() is the normal path. A failed start has no clean state to unwind, so it leaves from the thread
            // pool: exiting the process from the UI thread can hang while the UI thread is needed for teardown.
            if (exitCode == 0) { window?.CloseForExit(); Exit(); }
            else Task.Run(() => Environment.Exit(exitCode));
        }
    });

    private static void Fail(Exception? error)
    {
        Note(error?.ToString() ?? "Unknown unhandled error.");
        Task.Run(() => Environment.Exit(1));
    }
}
