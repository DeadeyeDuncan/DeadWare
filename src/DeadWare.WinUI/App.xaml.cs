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
    /// <summary>The dashboard window's handle, which file pickers need as their owner.</summary>
    internal static nint WindowHandle { get; set; }
    /// <summary>The path given after <c>--error-file</c>, or null.</summary>
    internal static string? ErrorFile { get; private set; }
    /// <summary>One-second ticks on which the session held a reading; written by <c>--exit-after</c> for the smoke check.</summary>
    internal static long TicksWithReading;
    /// <summary>The pop-out windows and the widget; null until the session exists.</summary>
    internal static WindowManager? Popouts { get; private set; }

    internal static string Version { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static int? exitAfter;
    /// <summary>The destination given after <c>--page</c>, shown at start instead of Overview.</summary>
    private static string? startPage;
    /// <summary>"light" or "dark" given after <c>--theme</c>, overriding the Windows app theme for this run.</summary>
    private static string? theme;
    private static Timer? exitTimer;

    private readonly ApplicationLifetime lifetime = new();
    private readonly CancellationTokenSource updateChecks = new();
    private readonly string[] arguments = Environment.GetCommandLineArgs()[1..];
    private DispatcherQueue? queue;
    private GuiInstance? instance;
    private MonitorSession? session;
    private MainWindow? window;
    private Tray? tray;
    private SessionEndHook? sessionEnd;

    public App()
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--no-mica") NoMica = true;
            else if (arguments[i] == "--error-file" && i + 1 < arguments.Length) ErrorFile = arguments[++i];
            else if (arguments[i] == "--page" && i + 1 < arguments.Length) startPage = arguments[++i];
            else if (arguments[i] == "--theme" && i + 1 < arguments.Length) theme = arguments[++i];
            else if (arguments[i] == "--exit-after" && i + 1 < arguments.Length && int.TryParse(arguments[++i], out int seconds) && seconds > 0) exitAfter = seconds;
        }
        UnhandledException += (_, e) => Fail(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fail(e.ExceptionObject as Exception);
        InitializeComponent();
        // The app follows Windows; the flag is for looking at the other theme without changing the Windows setting.
        if (theme is "light" or "dark") RequestedTheme = theme == "light" ? ApplicationTheme.Light : ApplicationTheme.Dark;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        queue = DispatcherQueue.GetForCurrentThread();
        // Two kinds of run show no window and leave with their result: the elevated helper that Settings starts to
        // register or remove the sign-in task, and the diagnostic collection. Both must come before the single-instance
        // check, since the helper runs while the dashboard that started it is open.
        if (await StartupRegistration.HandleCommandLineAsync(arguments) is int configured) { Leave(configured); return; }
        if (arguments.Contains("--diagnose")) { Leave(await DiagnosticRun.RunAsync(arguments, Version)); return; }
        try
        {
            await GuiInstance.WaitForPreviousAsync(arguments);
            instance = new GuiInstance();
            if (!instance.Acquired)
            {
                // The WPF app and this one share the single-instance marker, so they never write the same histories at once.
                // A sign-in launch that finds DeadWare running leaves quietly, as the WPF app does.
                if (!arguments.Contains("--startup"))
                    Tell("DeadWare is already running in this Windows session. Double-click its system tray icon or right-click it and choose Show dashboard. The icon may be in the tray overflow.", MessageIcon.Information);
                Exit();
                return;
            }
            var cities = Cities.Load();
            session = new MonitorSession(Version, () => new QueueTimer(queue), id => id is not null && cities.TryGetValue(id, out var point) ? (id, point) : null,
                LayoutDiagnostics.RequestedPath(arguments));
            window = new MainWindow(session, lifetime, () => tray is { Available: true }, () => QuitAsync());
            Popouts = new WindowManager(session);
            window.RememberPlacement(Popouts);
            sessionEnd = new SessionEndHook(WindowHandle, OnSessionEnding);
            if (startPage is not null) window.Navigate(startPage);
            tray = new Tray(ShowDashboard, RequestQuit);
            if (!tray.Available) window.SetTrayUnavailable();
            Subscribe(session);
            session.Changed += () => { if (session.Current is not null) Interlocked.Increment(ref TicksWithReading); };

            bool windowsStartup = arguments.Contains("--startup") && !arguments.Contains("--restart-from");
            if (ApplicationLifetime.ShouldShowDashboardOnLaunch(windowsStartup, session.Settings.StartInTray, tray.Available)) window.Activate();
            session.Start();
            // Graphs and the widget from the previous session come back whether or not the dashboard is shown.
            Popouts.RestoreRemembered();
            // As the WPF app does: acknowledge an update that just restarted us, then look for a newer release once.
            UpdateService.AcknowledgeStartup(arguments, AppContext.BaseDirectory, Version);
            _ = session.Updates.CheckAtStartupAsync(updateChecks.Token);

            if (exitAfter is { } seconds)
            {
                // A thread-pool timer starts the ordinary quit path, so the smoke check also proves a clean shutdown.
                exitTimer = new Timer(_ => queue.TryEnqueue(() =>
                {
                    if (ErrorFile is not null) File.WriteAllText(ErrorFile + ".status", $"readings={Interlocked.Read(ref TicksWithReading)} frames={Interlocked.Read(ref HistoryChartView.FramesDrawn)} rows={Interlocked.Read(ref TableView.RowsBound)} drives={StoragePage.DrivesSeen} driveids={StoragePage.DriveIdsSeen}");
                    RequestQuit();
                }), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception error)
        {
            Note($"DeadWare could not start: {error}");
            if (ErrorFile is null) Tell($"DeadWare could not start: {error.Message}", MessageIcon.Error);
            await QuitAsync(1);
        }
    }

    /// <summary>Ends a run that never showed a window, with its exit code. From the thread pool: nothing here needs unwinding.</summary>
    private static void Leave(int exitCode) => Task.Run(() => Environment.Exit(exitCode));

    private enum MessageIcon : uint { Error = 0x10, Warning = 0x30, Information = 0x40 }

    /// <summary>
    /// A plain Windows message box, for the moments the WPF app also stops to say something and there may be no window of
    /// ours to say it in. A run given <c>--error-file</c> is automated and gets the note in that file only.
    /// </summary>
    private static void Tell(string text, MessageIcon icon)
    {
        Note(text);
        if (ErrorFile is null) MessageBoxW(WindowHandle, text, "DeadWare", (uint)icon);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);

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
        session.Updates.UpdateAvailable += release => OnUi(() =>
            tray?.ShowBalloon("DeadWare update available", $"Version {release.Version} is available. Open Updates to review it and install when you are ready.", warning: false, ShowUpdates));
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

    private void ShowUpdates()
    {
        ShowDashboard();
        if (!lifetime.IsEnding) window?.Navigate("updates");
    }

    /// <summary>
    /// Windows is signing out or shutting down: record the window layout and settings synchronously and stop the
    /// collector within its short deadline, as the WPF app does. The process is ended by Windows soon after.
    /// </summary>
    private void OnSessionEnding()
    {
        lifetime.BeginSessionEnding();
        updateChecks.Cancel();
        try { session?.SaveForSessionEnding(); }
        finally
        {
            try { session?.StopCollectorForSessionEnding(); }
            finally { tray?.Dispose(); tray = null; }
        }
    }

    private async void RequestQuit() => await QuitAsync();

    /// <summary>Stops the session, removes the tray icon and leaves through the ordinary WinUI exit.</summary>
    private Task QuitAsync(int exitCode = 0) => lifetime.QuitAsync(async () =>
    {
        updateChecks.Cancel();
        try { if (session is not null) await session.ShutdownAsync(); }
        catch (Exception error)
        {
            Note($"DeadWare reported a shutdown error: {error}");
            if (ErrorFile is null) Tell($"DeadWare reported a shutdown error: {error.Message}", MessageIcon.Warning);
            exitCode = exitCode == 0 ? 2 : exitCode;
        }
    }, () =>
    {
        try { sessionEnd?.Dispose(); sessionEnd = null; Popouts?.CloseAll(); tray?.Dispose(); tray = null; instance?.Dispose(); }
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
