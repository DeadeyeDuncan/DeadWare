using DeadWare.App;
using DeadWare.Core;
using DeadWare.UpdateCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// Settings in seven sections: Monitoring, Appearance, Widget, Pop-outs, Startup, History and alerts, and Lighting. Light, dark
/// and accent colours follow Windows, so the only appearance choices are how the graph lines look.
/// </summary>
internal sealed class SettingsPage : Page
{
    private static readonly string[] Sections = ["Monitoring", "Appearance", "Widget", "Pop-outs", "Startup", "History and alerts", "Lighting"];
    private static readonly int LightingSection = Array.IndexOf(Sections, "Lighting");
    private static readonly (string Key, string Label)[] WidgetGraphs =
        [("cpu", "CPU / GPU usage"), ("cputemp", "CPU / GPU temperature"), ("memory", "RAM usage"), ("network", "Network activity"), ("disk", "Disk activity")];
    private const int MaximumLength = 260;
    private const string ExcludedHelp = "These apps still show on live pages, marked \"not recorded\", but none of their history is saved: no Connections history, no per-app totals in Usage history and no alerts for them. In Application resources their CPU, memory, I/O and GPU count under Everything else, so device totals stay true. Application traffic budgets for them stop counting. History saved before you add an app is kept until you clear it. Processes that Windows saves as \"PID n\" can't be matched.";
    private const string KeptNote = "Saved history for this app is kept. Use Clear history… to remove it.";

    private readonly MonitorSession session;
    private readonly Func<Task> quit;
    private readonly AppSuggestions suggestions;
    private readonly StackPanel[] panels;
    private readonly SelectorBar tabs = new();
    private readonly InfoBar status = new() { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
    private readonly Button showProgress = new() { Content = "Show in History and alerts" };
    private bool syncing, loaded;

    // Monitoring
    private readonly ComboBox polling = new() { MinWidth = 180 };
    private readonly CheckBox processMonitoring = new() { Content = "Per-process monitoring" }, cpuCoreUsage = new() { Content = "Show per-core CPU usage on usage graphs" };
    private readonly CheckBox extended = new() { Content = "Extended sensors (may affect responsiveness)" }, storageSensors = new() { Content = "Storage sensors (temperature and health; restart to apply)" };
    private readonly TextBlock elevateResult = Muted("");
    // Appearance
    private readonly CheckBox graphSmoothing = new() { Content = "Smooth long-range graphs (1, 3 and 6 hours)" }, gpuFanSpeed = new() { Content = "Show GPU fan speed on temperature graphs" }, cpuPumpSpeed = new() { Content = "Show CPU pump speed on CPU temperature graphs" };
    private readonly ComboBox line = new() { MinWidth = 220 };
    private readonly Border lineDot = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock lineColor = Muted("");
    private readonly LineStyleEditor editor;
    private readonly StackPanel coreActions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private string[] lineKeys = [];
    private int lineCores = -1;
    // Widget and pop-outs
    private readonly Dictionary<string, CheckBox> widgetChoices = [];
    private readonly CheckBox widgetConnectionsMap = new() { Content = "Connections map (live)" }, widgetAlwaysOnTop = new() { Content = "Always on top" };
    private readonly CheckBox joinedPopouts = new() { Content = "Join CPU and GPU graphs in pop-outs" }, restore = new() { Content = "Reopen graphs and widget from the previous session" };
    // Startup
    private readonly CheckBox startup = new() { Content = "Start DeadWare when I sign in to Windows", IsThreeState = false }, startInTray = new() { Content = "Start in the system tray" };
    private readonly TextBlock startupStatus = Muted("Startup status is checked when this page opens."), startupResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button startupRepair = new() { Content = "Enable / repair startup" }, startupDisable = new() { Content = "Disable startup" }, startupRefresh = new() { Content = "Refresh status" };
    private bool startupBusy, startupQueryFailed, syncingStartup;
    // History and alerts
    private readonly CheckBox usageRecording = new() { Content = "Record usage history" }, alertNotifications = new() { Content = "Show tray notifications for alerts" };
    private readonly AutoSuggestBox excludedBox = new() { Width = 280, PlaceholderText = "chrome.exe" };
    private readonly TextBlock excludedError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, excludedNote = Muted(KeptNote);
    private readonly StackPanel excludedList = new() { Spacing = 4 };
    private string[]? excludedShown;
    private readonly Button clear = new() { Content = HistoryClearCopy.EntryButton, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel clearProgress = new() { Spacing = 4, Visibility = Visibility.Collapsed }, clearLines = new() { Spacing = 2 };
    private readonly TextBlock clearHeader = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] }, clearSummary = Muted("");
    private readonly Button clearRetry = new() { Content = HistoryClearCopy.TryAgainButton, Visibility = Visibility.Collapsed };
    private HistoryClearLine[]? clearShown;
    // Lighting
    // True whether or not DeadWare runs as administrator: only an elevated DeadWare refuses the folder (Ruling AM), and copying OpenRGB is the fix either way.
    private const string ProtectionWarningText = @"Programs running without administrator rights can change this OpenRGB folder, so while DeadWare runs as administrator it won't run OpenRGB from here. Copy OpenRGB to C:\Program Files\OpenRGB and choose it here.";
    private const string CheckingConflictsText = "Checking for programs that would fight DeadWare over the lights…";
    private const string NoConflictsText = "Nothing is running that would fight DeadWare over the lights.";
    private const string ScanFailedText = "DeadWare couldn't check for conflicting programs.";
    private const string HostScanFailedText = "The lighting helper couldn't check for conflicting programs.";
    // Shown only while the helper hasn't completed a conflict scan, as when its OpenRGB settings folder was refused: it then never starts
    // OpenRGB and never scans.
    private const string HostNotRunningText = "Conflicts can't be checked until OpenRGB is running.";
    private const string HostUnreachableText = "Conflicts can't be checked while DeadWare can't reach the lighting helper.";
    // How long the first scan after lighting goes off waits to look again when it found another OpenRGB, which may have been the
    // host's own on its way out.
    private static readonly TimeSpan LookAgainDelay = TimeSpan.FromSeconds(2);
    // The same words on and off, as on the Lighting page, so the switch's visible label is always its name; MinWidth 0 keeps its
    // messages close beside it.
    private readonly ToggleSwitch lightingEnabled = new() { OnContent = "Enable lighting", OffContent = "Enable lighting", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock lightingReason = Muted("Choose OpenRGB.exe below first."), lightingProblem = Caution(), openRgbProblem = Caution(), lightingError = Caution(), browseNote = Caution(), protectionWarning = Caution();
    private readonly TextBox openRgbLocation = new() { IsReadOnly = true, IsSpellCheckEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button browseOpenRgb = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Center };
    // The note beside Browse… takes a share of its row only while it shows, so it wraps there instead of running off the row.
    private readonly ColumnDefinition browseNoteColumn = new() { Width = GridLength.Auto };
    private readonly StackPanel conflictLines = new() { Spacing = 4 };
    private (string Text, bool Caution)[]? conflictsShown;
    // scanned: the last local scan's conflicts, kept while lighting is off. followingHost: whether the section last showed the
    // host's list rather than a local scan's; null until it has shown either. protectionFor: the path the folder message is for.
    // protectionChecks: the paths whose folder check is still running.
    private IReadOnlyList<LightingConflict>? scanned;
    private bool? followingHost;
    private string? protectionFor;
    private readonly HashSet<string> protectionChecks = new(StringComparer.Ordinal);
    // scanRun is bumped when a newer scan, or lighting turning on, makes a scan still on its way out of date.
    private int shownSection, scanRun;
    // lightingBusy: a lighting change or Browse… is under way; browsing: Browse… started it. windowShowing: the dashboard is on
    // screen rather than in the tray. lightingOnScreen: the section was on screen at the last sync, so its coming back is seen.
    // scanOwed: lighting is off and a scan is due; it waits until a change under way has returned. lookAgain: the scan due is the
    // first since lighting went off, so it looks once more if it finds another OpenRGB.
    private bool lightingBusy, browsing, windowShowing, lightingOnScreen, scanOwed, scanFailed, lookAgain;

    /// <summary>Asks the window to open another destination by its key ("usage", "alerts", "connections").</summary>
    public Action<string>? NavigateRequested { get; set; }

    public SettingsPage(MonitorSession session, Func<Task> quit)
    {
        this.session = session;
        this.quit = quit;
        suggestions = new AppSuggestions(session);
        editor = new LineStyleEditor(session);
        // The banner is a polite live region, so a screen reader reads a new status without moving focus.
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panels = [Monitoring(), Appearance(), Widget(), Popouts(), Startup(), History(), Lighting()];
        foreach (string section in Sections) tabs.Items.Add(new SelectorBarItem { Text = section });
        AutomationProperties.SetName(tabs, "Settings categories");
        tabs.SelectedItem = tabs.Items[0];
        tabs.SelectionChanged += (_, _) => ShowSection(Math.Max(0, tabs.Items.IndexOf(tabs.SelectedItem)));
        ShowSection(0);

        showProgress.Click += (_, _) => { tabs.SelectedItem = tabs.Items[5]; ShowSection(5); clearProgress.StartBringIntoView(); };
        status.ActionButton = showProgress;
        var card = new StackPanel { Spacing = 12, Padding = new Thickness(8) };
        card.Children.Add(tabs);
        foreach (var panel in panels) card.Children.Add(panel);
        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { status, Pages.Card(card) } } };

        this.WhileLoaded(() =>
        {
            loaded = true;
            session.Changed += Sync; session.HistoryClearChanged += ClearChanged; suggestions.Changed += OfferExcluded;
            Sync();
            _ = RefreshStartupAsync(); _ = RefreshSuggestionsAsync();
        },
        () =>
        {
            loaded = false;
            session.Changed -= Sync; session.HistoryClearChanged -= ClearChanged; suggestions.Changed -= OfferExcluded;
        });
        // The page stays loaded while the dashboard is in the tray, so the Lighting section also follows the window: it is checked
        // afresh when the dashboard comes back, as when the page or the section opens.
        this.WhileShowing(() => { windowShowing = true; SyncLighting(); }, () => { windowShowing = false; SyncLighting(); });
        Sync();
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    // CoolingPage's warning text: what went wrong, in the caution colour.
    private static TextBlock Caution(string text = "") => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] };

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows a message, hidden while it is empty; a screen reader hears it when it appears, unless <paramref name="announce"/> is false.</summary>
    private static void Say(TextBlock block, string text, bool announce = true)
    {
        if (block.Text == text) return;
        block.Text = text;
        Show(block, text.Length > 0);
        if (announce && text.Length > 0) Announce(block);
    }

    private static void Announce(UIElement element) =>
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);

    private static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };

    private static StackPanel Section() => new() { Spacing = 8 };

    private static Button Action(string label, Action run)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>Names a check box for assistive technology and saves its changes, except while settings are being reflected into it.</summary>
    private CheckBox Bind(CheckBox box, string name, Action<bool> save)
    {
        AutomationProperties.SetName(box, name);
        box.Checked += (_, _) => { if (!syncing) save(true); };
        box.Unchecked += (_, _) => { if (!syncing) save(false); };
        return box;
    }

    private void ShowSection(int index)
    {
        for (int i = 0; i < panels.Length; i++) panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        shownSection = index;
        // The Lighting section is checked afresh as it comes on screen, and leaving it lets its next showing do the same.
        SyncLighting();
    }

    private StackPanel Monitoring()
    {
        var panel = Section();
        foreach (double seconds in PollingPolicy.Intervals) polling.Items.Add(seconds == 1 ? "1 second (default)" : $"{seconds:0.#} seconds");
        AutomationProperties.SetName(polling, "Data polling interval");
        polling.SelectionChanged += (_, _) => { if (!syncing && polling.SelectedIndex >= 0) session.SetPollingInterval(PollingPolicy.Intervals[polling.SelectedIndex]); };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "Data polling interval", VerticalAlignment = VerticalAlignment.Center }, polling } });
        panel.Children.Add(Muted("Applies without restarting after the current wait or reading finishes. Faster polling uses more resources; slow hardware may take longer than the selected interval. At 5 or 10 seconds, Cooling Health records history but does not gather enough samples for reference comparisons."));

        panel.Children.Add(Bind(processMonitoring, "Per-process monitoring", session.SetProcessMonitoring));
        panel.Children.Add(Muted(ProcessCopy.ScanSentence(session.Current?.Elevated ?? Collector.IsElevated, ProcessFeatures.DiskColumns, ProcessFeatures.ScanLoop, history: true)));
        panel.Children.Add(Bind(cpuCoreUsage, "Show per-core CPU usage on usage graphs", session.SetCpuCoreUsage));
        panel.Children.Add(Muted("One thin line per core under the total on the CPU usage card, its pop-out and the widget. Core traces are kept in the six-hour graph history."));

        var sources = Section();
        sources.Children.Add(Bind(extended, "Extended sensors", session.SetExtendedSensors));
        sources.Children.Add(Muted("Includes motherboard, controller, storage and memory sensors. Off by default. Restart DeadWare after changing this setting."));
        sources.Children.Add(Bind(storageSensors, "Storage sensors", session.SetStorageSensors));
        sources.Children.Add(Muted("Reads drive temperature, SMART health and wear from LibreHardwareMonitor every 30 seconds. Throughput and free space do not depend on this. Changing it takes effect after restarting DeadWare."));
        panel.Children.Add(new Expander { Header = "Sensor sources", Content = sources, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });

        if (!Collector.IsElevated)
        {
            panel.Children.Add(Heading("Administrator access"));
            panel.Children.Add(Muted("Some readings, such as low-level sensors and per-app network capture, need DeadWare to run as administrator. Restarting saves your workspace first."));
            var elevate = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left };
            elevate.Click += async (_, _) =>
            {
                elevate.IsEnabled = false; elevateResult.Text = "";
                try { elevateResult.Text = await session.RestartElevatedAsync(quit) ?? ""; }
                catch (Exception error) { elevateResult.Text = $"DeadWare could not restart as administrator: {error.Message}"; }
                finally { elevate.IsEnabled = true; }
            };
            panel.Children.Add(elevate); panel.Children.Add(elevateResult);
        }
        return panel;
    }

    private StackPanel Appearance()
    {
        var panel = Section();
        panel.Children.Add(Muted("Light or dark mode and the accent colour follow your Windows settings."));
        panel.Children.Add(Bind(graphSmoothing, "Smooth long-range graphs (1, 3 and 6 hours)", session.SetGraphSmoothing));
        panel.Children.Add(Muted("On shows an average line with a min/max band. Off shows raw traces. Applies immediately to the dashboard, pop-outs and widget; recorded data is unchanged."));
        panel.Children.Add(Bind(gpuFanSpeed, "Show GPU fan speed on temperature graphs", session.SetGpuFanSpeed));
        panel.Children.Add(Muted("Adds the selected GPU's first reported fan on its own left RPM scale, dotted and amber by default. Unsupported fan readings stay unavailable; 0 RPM means stopped."));
        panel.Children.Add(Bind(cpuPumpSpeed, "Show CPU pump speed on CPU temperature graphs", session.SetCpuPumpSpeed));
        panel.Children.Add(Muted("Adds the CPU cooler's pump speed on its own left RPM scale, dotted and blue by default. DeadWare uses a motherboard or AIO header labeled as a pump, otherwise the CPU Fan header; this needs Extended sensors and a single CPU."));

        panel.Children.Add(Heading("Graph lines"));
        panel.Children.Add(Muted("Colour and line style for each trace across the dashboard, pop-outs and widget. Core lines appear here once per-core CPU usage has a reading."));
        AutomationProperties.SetName(line, "Graph line");
        line.SelectionChanged += (_, _) => { if (!syncing) SyncLine(); };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { line, lineDot, lineColor } });

        // The swatch row, More colours… and Default colour, then the pattern, thickness and glow: the same editor the colour
        // dots on Overview cards open. Its two parts space their controls as this section does, so they sit as they always have.
        panel.Children.Add(editor.ColourControls);
        panel.Children.Add(editor.StyleControls);

        foreach (var action in LineStyleEditor.CoreActions(session, () => lineCores)) coreActions.Children.Add(action);
        panel.Children.Add(new ScrollViewer { Content = coreActions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });
        return panel;
    }

    private string? LineKey => line.SelectedIndex >= 0 && line.SelectedIndex < lineKeys.Length ? lineKeys[line.SelectedIndex] : null;

    /// <summary>Lists the fixed traces and one entry per live core; rebuilt only when the core count changes.</summary>
    private void SyncLines()
    {
        int cores = Math.Clamp(session.Current?.CpuCoreCount ?? 0, 0, GraphColorSettings.MaxCores);
        if (cores != lineCores)
        {
            int selected = Math.Max(0, line.SelectedIndex);
            lineCores = cores;
            var keys = LineStyleEditor.Lines.Select(item => item.Key).ToList();
            line.Items.Clear();
            foreach (var (_, label) in LineStyleEditor.Lines) line.Items.Add(label);
            for (int i = 0; i < cores; i++) { string key = GraphColorSettings.CoreKey(i); keys.Add(key); line.Items.Add(LineStyleEditor.Label(key)); }
            lineKeys = [.. keys];
            line.SelectedIndex = Math.Min(selected, lineKeys.Length - 1);
            coreActions.Visibility = cores > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncLine();
    }

    /// <summary>Shows the selected trace's saved colour beside the list and points the editor at that trace.</summary>
    private void SyncLine()
    {
        if (LineKey is not { } key) return;
        bool own = session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved);
        lineDot.Background = own ? new SolidColorBrush(LineStyleEditor.Parse(saved!)) : null;
        lineDot.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        lineColor.Text = own ? saved! : GraphColorSettings.TryCoreIndex(key, out _) ? "Auto tint" : "Default colour";
        // The editor guards its own controls while it reads the saved style into them.
        editor.Key = key;
        editor.Sync();
    }

    private StackPanel Widget()
    {
        var panel = Section();
        panel.Children.Add(new TextBlock { Text = "Widget graphs" });
        foreach (var (key, label) in WidgetGraphs)
        {
            var choice = Bind(new CheckBox { Content = label }, $"Show {label} in widget", enabled => session.SetWidgetGraph(key, enabled));
            widgetChoices.Add(key, choice);
            panel.Children.Add(choice);
        }
        panel.Children.Add(Bind(widgetConnectionsMap, "Show Connections map (live) in widget", session.SetWidgetConnectionsMap));
        panel.Children.Add(Bind(widgetAlwaysOnTop, "Widget always on top", session.SetWidgetAlwaysOnTop));
        panel.Children.Add(Muted("Choose the graphs shown in the widget. Changes apply immediately; resize the widget to your preferred height."));
        return panel;
    }

    private StackPanel Popouts()
    {
        var panel = Section();
        panel.Children.Add(Heading("Your readings, wherever you need them"));
        panel.Children.Add(Muted("Open any graph in its own resizable native window. Choose Always on top to keep it visible while you work. All windows share the same readings, selected GPU and history range."));
        panel.Children.Add(Bind(joinedPopouts, "Join CPU and GPU graphs in pop-outs", session.SetJoinedPopouts));
        panel.Children.Add(Muted("Use the overview's Pop out buttons. Joined mode puts CPU and GPU usage on one chart and both temperatures on another. Applies to new pop-outs; existing windows and remembered layouts keep their current form."));
        panel.Children.Add(Bind(restore, "Reopen graphs and widget from the previous session", session.SetRestoreOpenWindows));
        panel.Children.Add(Muted("Positions, sizes and Always on top choices are saved as you work. Closing the dashboard hides it to the system tray while graphs and the widget keep monitoring. Choose Quit DeadWare to save the windows left open for next time and stop collection. Close an individual graph or widget to leave it closed next time."));
        return panel;
    }

    private StackPanel Startup()
    {
        var panel = Section();
        AutomationProperties.SetName(startup, "Start DeadWare at Windows sign-in");
        startup.Checked += async (_, _) => { if (!syncingStartup) await ChangeStartupAsync(true); };
        startup.Unchecked += async (_, _) => { if (!syncingStartup) await ChangeStartupAsync(false); };
        panel.Children.Add(startup); panel.Children.Add(startupStatus); panel.Children.Add(startupResult);
        panel.Children.Add(Bind(startInTray, "Start in the system tray", session.SetStartInTray));
        panel.Children.Add(Muted("At Windows sign-in, hide only the dashboard. Graphs and the widget still follow the reopen setting in Pop-outs. Manual launches show the dashboard."));
        startupRepair.Click += async (_, _) => await ChangeStartupAsync(true);
        startupDisable.Click += async (_, _) => await ChangeStartupAsync(false);
        startupRefresh.Click += async (_, _) => await RefreshStartupAsync();
        var repair = Section();
        repair.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { startupRepair, startupDisable, startupRefresh } });
        repair.Children.Add(Muted("Windows asks for administrator approval when changing startup. This applies to your Windows account and this executable location. After copying DeadWare to a new location, start it from there and repair startup. The drive must be available when you sign in."));
        panel.Children.Add(new Expander { Header = "Repair startup registration", Content = repair, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        return panel;
    }

    private void SetStartupBusy(bool busy)
    {
        startupBusy = busy;
        startup.IsEnabled = !busy; startupRepair.IsEnabled = !busy; startupDisable.IsEnabled = !busy; startupRefresh.IsEnabled = !busy;
    }

    private void SetStartupResult(string text) { startupResult.Text = text; UpdateStatus(); }

    private void ShowStartupStatus(StartupRegistrationStatus registration)
    {
        syncingStartup = true;
        try { startup.IsChecked = registration.Error is null ? registration.Ready : null; }
        finally { syncingStartup = false; }
        startupStatus.Text = registration.Message + (registration.Executable is null ? "" : "\nRegistered executable: " + registration.Executable) + (registration.Error is null ? "" : "\n" + registration.Error);
        startupQueryFailed = registration.Error is not null;
        UpdateStatus();
        bool ownedOrUnknown = !registration.Registered || registration.Owned;
        startup.IsEnabled = ownedOrUnknown; startupRepair.IsEnabled = ownedOrUnknown; startupDisable.IsEnabled = ownedOrUnknown;
    }

    private async Task RefreshStartupAsync()
    {
        if (startupBusy) return;
        SetStartupBusy(true); SetStartupResult("");
        try
        {
            var registration = await Task.Run(StartupRegistration.Query);
            SetStartupBusy(false); ShowStartupStatus(registration);
        }
        catch (Exception error) { SetStartupBusy(false); startupQueryFailed = true; startupStatus.Text = $"Startup status could not be read: {error.Message}"; UpdateStatus(); }
    }

    private async Task ChangeStartupAsync(bool enabled)
    {
        if (startupBusy) return;
        SetStartupBusy(true); SetStartupResult("Waiting for Windows administrator approval…");
        try
        {
            var result = await StartupRegistration.ConfigureAsync(enabled);
            SetStartupBusy(false); ShowStartupStatus(result.Registration); SetStartupResult(result.Message);
        }
        catch (Exception error) { SetStartupBusy(false); SetStartupResult($"Startup could not be changed: {error.Message}"); }
    }

    private StackPanel History()
    {
        var panel = Section();
        panel.Children.Add(Heading("Usage history"));
        panel.Children.Add(Bind(usageRecording, "Record usage history", session.SetUsageRecording));
        panel.Children.Add(Muted("Keeps day, week and month history of CPU, GPU, RAM and network usage plus per-application network totals for up to 400 days in this Windows profile. Pausing keeps what was saved. Open Usage history to browse, export or check storage. While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days."));
        panel.Children.Add(Left(Action("Open Usage history", () => NavigateRequested?.Invoke("usage"))));

        panel.Children.Add(Heading("Alerts"));
        panel.Children.Add(Bind(alertNotifications, "Alert notifications", session.SetAlertNotifications));
        panel.Children.Add(Muted("Rules you set on the Alerts page notify from the tray once per episode and are always logged there, whether or not notifications are on. The first network use rule learns your apps for 3 days once per-app capture has run as administrator, then logs each new app; apps seen within a minute share one notification. Cooling Health keeps its own notification setting."));
        panel.Children.Add(Left(Action("Open Alerts", () => NavigateRequested?.Invoke("alerts"))));

        panel.Children.Add(Heading("Connections"));
        panel.Children.Add(Muted("Record endpoint history, choose an optional home city, manage the offline location database and export saved traffic from Connections. Local history rolls over after 30 days or 256 MiB."));
        panel.Children.Add(Left(Action("Open Connections controls", () => NavigateRequested?.Invoke("connections"))));

        panel.Children.Add(Heading("Don't record these apps"));
        panel.Children.Add(Muted(ExcludedHelp));
        AutomationProperties.SetName(excludedBox, "App to stop recording");
        excludedBox.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) OfferExcluded(); };
        excludedBox.GotFocus += (_, _) => OfferExcluded();
        excludedBox.QuerySubmitted += (_, args) => AddExcluded(args.ChosenSuggestion as string ?? args.QueryText ?? "");
        var add = Action("Add", () => AddExcluded(excludedBox.Text ?? ""));
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { excludedBox, add } });
        excludedNote.Visibility = Visibility.Collapsed;
        panel.Children.Add(excludedError); panel.Children.Add(excludedNote); panel.Children.Add(excludedList);

        panel.Children.Add(Heading(HistoryClearCopy.SettingsHeading));
        panel.Children.Add(Muted(HistoryClearCopy.SettingsHelp));
        clear.Click += async (_, _) =>
        {
            try { await ClearHistoryDialog.ShowAsync(session, suggestions, XamlRoot); }
            catch (Exception error) { App.Note($"Clear history dialog failed: {error.Message}"); }
            SyncClear(); UpdateStatus();
        };
        panel.Children.Add(clear);
        clearRetry.Click += (_, _) =>
        {
            foreach (var shown in clearShown ?? []) if (shown.CanRetry) session.RetryClear(shown.Store);
            SyncClear();
        };
        clearProgress.Children.Add(clearHeader); clearProgress.Children.Add(clearLines); clearProgress.Children.Add(clearSummary); clearProgress.Children.Add(clearRetry);
        panel.Children.Add(clearProgress);
        return panel;
    }

    private static Button Left(Button button) { button.HorizontalAlignment = HorizontalAlignment.Left; return button; }

    private async Task RefreshSuggestionsAsync()
    {
        try { await suggestions.RefreshAsync(); } catch (Exception) { }
    }

    private void OfferExcluded()
    {
        if (!loaded) return;
        excludedBox.ItemsSource = suggestions.Matching(excludedBox.Text ?? "", includeListed: false);
    }

    private void AddExcluded(string text)
    {
        var list = session.Settings.ExcludedApps;
        string? error = text.Length > MaximumLength || !AppExclusions.Accepts(text) ? "Enter an app's executable name, such as chrome.exe."
            : list.Exists(name => AppKey.Of(name) == AppKey.Of(text)) ? "That app is already in the list."
            : list.Count >= AppExclusions.MaximumApps ? "At most 64 apps can be listed."
            : null;
        excludedNote.Visibility = Visibility.Collapsed;
        if (error is not null) { excludedError.Text = error; excludedError.Visibility = Visibility.Visible; return; }
        excludedError.Text = ""; excludedError.Visibility = Visibility.Collapsed;
        session.SetExcludedApps([.. list, text]);
        excludedBox.Text = "";
        excludedNote.Visibility = Visibility.Visible;
        SyncExcluded();
    }

    private void RemoveExcluded(string name)
    {
        string key = AppKey.Of(name);
        excludedNote.Visibility = Visibility.Collapsed; excludedError.Text = ""; excludedError.Visibility = Visibility.Collapsed;
        session.SetExcludedApps(session.Settings.ExcludedApps.Where(listed => AppKey.Of(listed) != key).ToList());
        SyncExcluded();
    }

    /// <summary>Renders the saved list when it differs from the rendered one, so focus survives the session's once-a-second change.</summary>
    private void SyncExcluded()
    {
        var names = session.Settings.ExcludedApps.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (excludedShown is not null && excludedShown.SequenceEqual(names, StringComparer.Ordinal)) return;
        excludedShown = names;
        excludedList.Children.Clear();
        if (names.Length == 0) { excludedList.Children.Add(Muted("No apps are excluded.")); return; }
        foreach (string name in names)
        {
            var remove = Action("Remove", () => RemoveExcluded(name));
            AutomationProperties.SetName(remove, $"Remove {name} from Don't record these apps");
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(remove, 1); row.Children.Add(remove);
            excludedList.Children.Add(row);
        }
    }

    private void ClearChanged(HistoryClearStore _) { SyncClear(); UpdateStatus(); }

    /// <summary>The clear's progress: a header, one line per store, the summary once finished and Try again for a store that failed.</summary>
    private void SyncClear()
    {
        // One at a time: Queued, Clearing, or Failed with its marker saved.
        bool waiting = session.ClearWaitingForRestart, enabled = !session.ClearActive && !waiting;
        if (clear.IsEnabled != enabled) clear.IsEnabled = enabled;
        ToolTipService.SetToolTip(clear, enabled ? null : HistoryClearCopy.DisabledTip(waiting));
        var lines = session.ClearLines();
        if (clearShown is not null && clearShown.SequenceEqual(lines)) return;
        clearShown = [.. lines];
        clearLines.Children.Clear();
        foreach (var shown in clearShown) clearLines.Children.Add(new TextBlock { Text = HistoryClearCopy.Line(shown), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        clearHeader.Text = HistoryClearCopy.Header(clearShown);
        string summary = HistoryClearCopy.Summary(clearShown);
        clearSummary.Text = summary; clearSummary.Visibility = summary.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        clearRetry.Visibility = clearShown.Any(shown => shown.CanRetry) ? Visibility.Visible : Visibility.Collapsed;
        clearProgress.Visibility = clearShown.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Lighting: Enable lighting with the lighting client's problem and the host's reason OpenRGB is missing or failing beside it,
    /// the OpenRGB location with Browse…, the folder message, and the programs that would fight DeadWare over the lights. Changes go
    /// through <see cref="MonitorSession.SetLightingAsync"/>, which saves them and starts, restarts or stops the lighting client.
    /// </summary>
    private StackPanel Lighting()
    {
        var panel = Section();
        AutomationProperties.SetName(lightingEnabled, "Enable lighting");
        lightingEnabled.Toggled += async (_, _) =>
        {
            if (syncing) return;
            // A change under way takes no other. Disabling the switch meanwhile would take its focus, so it stays enabled and goes
            // back to the choice being applied.
            if (lightingBusy) { ShowLightingChoice(); return; }
            await ToggleLightingAsync();
        };
        // Beside the switch: why it is disabled, the lighting client's problem, whose words say what to do here, the host's reason
        // OpenRGB is missing or failing, which Browse… below can fix, and a failed change.
        var toggleMessages = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { lightingReason, lightingProblem, openRgbProblem, lightingError } };
        var toggleRow = new Grid { ColumnSpacing = 12 };
        toggleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toggleRow.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(toggleMessages, 1);
        toggleRow.Children.Add(lightingEnabled); toggleRow.Children.Add(toggleMessages);
        panel.Children.Add(toggleRow);

        panel.Children.Add(Heading("OpenRGB location"));
        AutomationProperties.SetName(openRgbLocation, "OpenRGB location");
        browseOpenRgb.Click += async (_, _) => await BrowseOpenRgbAsync();
        browseNote.VerticalAlignment = VerticalAlignment.Center;
        // The box is 420 wide at most and gives up width to a note beside Browse…, which wraps in the row rather than running off it.
        var location = new Grid { ColumnSpacing = 8 };
        location.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 420 });
        location.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        location.ColumnDefinitions.Add(browseNoteColumn);
        Grid.SetColumn(browseOpenRgb, 1); Grid.SetColumn(browseNote, 2);
        location.Children.Add(openRgbLocation); location.Children.Add(browseOpenRgb); location.Children.Add(browseNote);
        panel.Children.Add(location);
        panel.Children.Add(Muted("DeadWare is tested with OpenRGB 1.0rc3."));
        panel.Children.Add(protectionWarning);

        panel.Children.Add(Heading("Conflicts"));
        panel.Children.Add(conflictLines);

        // Polite live regions, so a screen reader reads a lighting problem, OpenRGB's, a failed change, a note beside Browse… or the
        // folder message without moving focus.
        foreach (var message in new[] { lightingProblem, openRgbProblem, lightingError, browseNote, protectionWarning })
        {
            AutomationProperties.SetLiveSetting(message, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            message.Visibility = Visibility.Collapsed;
        }
        return panel;
    }

    private bool LightingShown => loaded && windowShowing && shownSection == LightingSection;

    /// <summary>
    /// Reflects the lighting choices into the section. While it is on screen it also follows the conflicts and checks the folder
    /// again when the path changes. As it comes on screen (the section is chosen, the page opens or the dashboard comes back from
    /// the tray), it checks the folder again and, with lighting off, rescans.
    /// </summary>
    private void SyncLighting()
    {
        SyncLightingControls();
        bool onScreen = LightingShown, cameOn = onScreen && !lightingOnScreen;
        lightingOnScreen = onScreen;
        if (!onScreen) return;
        RefreshProtection(force: cameOn);
        FollowConflicts(rescan: cameOn);
    }

    /// <summary>The switch and its messages, the location and Browse… as the saved choices, the lighting client and a change under way leave them.</summary>
    private void SyncLightingControls()
    {
        string? path = session.Settings.OpenRgbPath;
        lightingEnabled.IsEnabled = path is not null;
        Show(lightingReason, path is null);
        // The client's problem, such as a host that couldn't be started or stopped too often, shows for as long as it lasts.
        var client = session.Lighting;
        Say(lightingProblem, client?.Problem ?? "", announce: LightingShown);
        // So does the host's reason OpenRGB is missing or failing, such as a wrong path, here where Browse… can fix it.
        Say(openRgbProblem, client?.State is { Status: HostStatus.OpenRgbMissing or HostStatus.OpenRgbFailed } state ? state.Message ?? "" : "", announce: LightingShown);
        // Browse… waits while a change from the switch is under way. During its own change it stays enabled, so it keeps its focus,
        // and ignores clicks.
        browseOpenRgb.IsEnabled = !lightingBusy || browsing;
        if (openRgbLocation.Text == (path ?? "")) return;
        openRgbLocation.Text = path ?? "";
        ToolTipService.SetToolTip(openRgbLocation, path);
    }

    private void SetLightingBusy(bool busy, bool browse = false)
    {
        lightingBusy = busy;
        browsing = busy && browse;
        SyncLightingControls();
    }

    /// <summary>Puts the switch back to the saved choice without saving it again: the guard skips the change this raises.</summary>
    private void ShowLightingChoice()
    {
        syncing = true;
        try { lightingEnabled.IsOn = session.Settings.LightingEnabled; }
        finally { syncing = false; }
    }

    /// <summary>
    /// Saves the switch's choice, which starts or stops lighting. Until the change returns the switch takes no other choice, and a
    /// scan due because lighting went off waits, since the host may still be stopping.
    /// </summary>
    private async Task ToggleLightingAsync()
    {
        bool enabled = lightingEnabled.IsOn;
        SetLightingBusy(true);
        Say(lightingError, ""); SayBesideBrowse("");
        try { await session.SetLightingAsync(enabled, session.Settings.OpenRgbPath); }
        catch (Exception error) { Say(lightingError, $"Lighting couldn't be changed: {error.Message}"); }
        finally { SetLightingBusy(false); Sync(); }
    }

    /// <summary>
    /// Browse…: takes only an OpenRGB.exe whose path the settings file would keep, and saves it with the current choice to enable
    /// lighting, which restarts lighting on the new path when it is on. Any other file changes nothing. A refused file, a picker
    /// that fails and a change that fails are all reported beside the button.
    /// </summary>
    private async Task BrowseOpenRgbAsync()
    {
        if (lightingBusy) return;
        SetLightingBusy(true, browse: true);
        Say(lightingError, ""); SayBesideBrowse("");
        try
        {
            string? path;
            try
            {
                // The Windows App SDK's picker, which also works while DeadWare runs as administrator, as lighting often needs.
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(App.WindowHandle));
                picker.FileTypeFilter.Add(".exe");
                path = (await picker.PickSingleFileAsync())?.Path;
            }
            catch (Exception error) { SayBesideBrowse($"The file picker couldn't open: {error.Message}"); return; }
            if (path is null) return;
            if (!string.Equals(Path.GetFileName(path), "OpenRGB.exe", StringComparison.OrdinalIgnoreCase) || !SettingsCodec.IsSafeOpenRgbPath(path))
            {
                SayBesideBrowse("Choose OpenRGB.exe.");
                return;
            }
            string? before = session.Settings.OpenRgbPath;
            try { await session.SetLightingAsync(session.Settings.LightingEnabled, path); }
            catch (Exception error) { SayBesideBrowse($"Lighting couldn't be changed: {error.Message}"); }
            // A new path is checked as soon as it is saved; picking the same path again checks its folder again too.
            RefreshProtection(force: session.Settings.OpenRgbPath == before);
        }
        finally { SetLightingBusy(false); Sync(); }
    }

    /// <summary>A message beside Browse…, hidden while empty. While one shows, its column shares the row with the location box, so it wraps.</summary>
    private void SayBesideBrowse(string text)
    {
        browseNoteColumn.Width = text.Length > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Say(browseNote, text);
    }

    /// <summary>
    /// Checks, off the UI thread, whether programs without administrator rights can change the chosen OpenRGB folder: when
    /// <paramref name="force"/> asks, and whenever the path has changed since the last check. Each check walks the whole tree and
    /// can't be stopped, so while one for the same path is still running, its answer is awaited rather than another started.
    /// </summary>
    private void RefreshProtection(bool force)
    {
        string? path = session.Settings.OpenRgbPath;
        if (!force && path == protectionFor) return;
        // Another path's message doesn't apply to this one. The same path's stays until the new check replaces it.
        if (path != protectionFor) Say(protectionWarning, "");
        protectionFor = path;
        if (path is not null && protectionChecks.Add(path)) _ = CheckProtectionAsync(path);
    }

    private async Task CheckProtectionAsync(string path)
    {
        string text;
        // It reads the permissions of the folder, everything in it and every parent, so it stays off the UI thread.
        try { text = ProtectionText(await Task.Run(() => InstallLocation.Check(Path.GetDirectoryName(Path.GetFullPath(path))!))); }
        catch (Exception error) { text = CantCheckText(error.Message); }
        finally { protectionChecks.Remove(path); }
        // The answer counts only while its path is still the chosen one.
        if (path == protectionFor) Say(protectionWarning, text);
    }

    /// <summary>
    /// The folder check in words: nothing when only administrators can change the folder, and the warning to move OpenRGB when other
    /// programs can, or when the folder is reached through a link or junction, which moving OpenRGB to C:\Program Files\OpenRGB also
    /// fixes. A folder that is missing, unreadable or on a network drive can't be judged, so its reason is shown instead of a warning
    /// about permissions that weren't the problem.
    /// </summary>
    private static string ProtectionText(LocationProtection protection)
    {
        if (protection.Protected) return "";
        string reason = protection.Reason ?? "";
        // InstallLocation.Evaluate's reasons for an entry it couldn't judge. Its other reasons, a link or junction, or an owner or a
        // rule that lets other programs change the folder, and a reason not known here, get the warning.
        bool unjudged = (reason.StartsWith("Permissions for ", StringComparison.Ordinal) && reason.Contains(" could not be read: ", StringComparison.Ordinal))
            || reason.EndsWith(" is not on a local drive.", StringComparison.Ordinal) || reason == "No folder permissions were checked.";
        return unjudged ? CantCheckText(reason) : ProtectionWarningText;
    }

    private static string CantCheckText(string reason) => $"DeadWare can't check this OpenRGB folder: {reason}";

    /// <summary>
    /// Follows what would fight DeadWare over the lights. While lighting is on that is the host's own list: only the host knows
    /// which OpenRGB it started, and a local scan would call that one foreign. While lighting is off, the running programs are
    /// scanned here, off the UI thread, when <paramref name="rescan"/> asks (the section has come on screen) and when lighting has
    /// been turned off. Such a scan waits until a change under way has returned: turning lighting off returns once the host has
    /// stopped, so a scan taken sooner could find its OpenRGB. That OpenRGB can still be on its way out even then, so the first scan
    /// after lighting goes off looks once more if it finds another OpenRGB. Nothing else scans after a delay or on a timer.
    /// </summary>
    private void FollowConflicts(bool rescan)
    {
        bool host = session.Lighting is not null;
        if (host != followingHost)
        {
            // The other list no longer applies, and a scan still on its way began before the switch. Only a switch from the host's
            // list to a local one is lighting going off.
            lookAgain = followingHost == true;
            followingHost = host;
            scanned = null;
            scanFailed = false;
            scanOwed = false;
            scanRun++;
            rescan = true;
        }
        if (rescan && !host) scanOwed = true;
        if (scanOwed && !host && !lightingBusy)
        {
            scanOwed = false;
            _ = ScanConflictsAsync(firstSinceOff: lookAgain);
            lookAgain = false;
        }
        ShowConflicts();
    }

    /// <summary>
    /// Scans the running programs and shows what would fight DeadWare over the lights. In the first scan since lighting went off
    /// (<paramref name="firstSinceOff"/>) another OpenRGB may be the host's own on its way out, so that scan looks once more.
    /// </summary>
    private async Task ScanConflictsAsync(bool firstSinceOff = false)
    {
        int run = ++scanRun;
        IReadOnlyList<LightingConflict>? found;
        try { found = await Task.Run(() => LightingConflicts.Find(LightingConflicts.RunningProcesses(), null)); }
        catch (Exception) { found = null; }
        if (run != scanRun) return;
        scanned = found;
        scanFailed = found is null;
        ShowConflicts();
        if (firstSinceOff && found is not null && found.Any(conflict => conflict.Kind == ConflictKind.ForeignOpenRgb)) await LookAgainAsync(run);
    }

    /// <summary>
    /// One more scan, <see cref="LookAgainDelay"/> after the scan <paramref name="run"/>, whose answer replaces that one's: only while
    /// that scan's answer is still the latest, the section is still shown and lighting is still off. A change under way holds it
    /// until the change returns, as it holds any scan.
    /// </summary>
    private async Task LookAgainAsync(int run)
    {
        await Task.Delay(LookAgainDelay);
        if (run != scanRun || !LightingShown || session.Lighting is not null) return;
        if (lightingBusy) scanOwed = true;
        else _ = ScanConflictsAsync();
    }

    /// <summary>The conflict lines, rebuilt only when they change.</summary>
    private void ShowConflicts()
    {
        var lines = ConflictLines();
        if (conflictsShown is not null && conflictsShown.SequenceEqual(lines)) return;
        conflictsShown = lines;
        conflictLines.Children.Clear();
        foreach (var (text, caution) in lines) conflictLines.Children.Add(caution ? Caution(text) : Muted(text));
    }

    /// <summary>
    /// What to say about conflicts. With lighting off: the local scan's conflicts, or that nothing was found, that the scan is under
    /// way or that it failed. With lighting on: the host's list, whatever its status. When it lists nothing, a blocked host's latest
    /// scan failed; a host that has completed a scan (<see cref="HostState.ConflictsChecked"/>) found nothing, whether or not its
    /// OpenRGB runs (polish item 2); one that hasn't, without a working OpenRGB or stopping, may never scan (one whose OpenRGB settings
    /// folder was refused never does), so it says conflicts can't be checked until OpenRGB runs; and a starting host hasn't said yet.
    /// With no state the host is connecting or relaunching, unless a problem has stopped the client, and then nothing checks until
    /// lighting is turned off and on: a scan here could call the host's own OpenRGB foreign.
    /// </summary>
    private (string Text, bool Caution)[] ConflictLines()
    {
        if (session.Lighting is not { } client)
        {
            if (scanFailed) return [(ScanFailedText, true)];
            if (scanned is null) return [(CheckingConflictsText, false)];
            var found = Listed(scanned);
            return found.Length > 0 ? found : [(NoConflictsText, false)];
        }
        if (client.State is not { } state) return [(client.Problem is { Length: > 0 } ? HostUnreachableText : CheckingConflictsText, false)];
        var listed = Listed(state.Conflicts);
        if (listed.Length > 0) return listed;
        // Blocked with nothing listed: its latest scan failed, whatever an earlier one found.
        if (state.Status == HostStatus.Blocked) return [(HostScanFailedText, true)];
        if (state.ConflictsChecked) return [(NoConflictsText, false)];
        return state.Status switch
        {
            // These last until something changes, so "Checking…" would never end, and the host may never scan, so an empty list isn't an
            // all-clear.
            HostStatus.OpenRgbMissing or HostStatus.OpenRgbFailed or HostStatus.Stopping => [(HostNotRunningText, false)],
            // Starting, before its first scan.
            _ => [(CheckingConflictsText, false)],
        };
    }

    /// <summary>
    /// The conflicts that have a description, blocking ones first in the caution colour, then the rest, whose descriptions say they
    /// don't block lighting.
    /// </summary>
    private static (string Text, bool Caution)[] Listed(IReadOnlyList<LightingConflict>? conflicts) =>
        [.. (conflicts ?? []).Where(conflict => conflict is { Description.Length: > 0 }).OrderByDescending(conflict => conflict.BlocksLighting).Select(conflict => (conflict.Description, conflict.BlocksLighting))];

    /// <summary>The banner above the sections: a startup result or failure, and a running or failed clear.</summary>
    private void UpdateStatus()
    {
        var lines = session.ClearLines();
        bool running = lines.Any(shown => shown.State.Phase is HistoryClearPhase.Queued or HistoryClearPhase.Clearing);
        string clearing = running ? HistoryClearCopy.Header(lines)
            : lines.Any(shown => shown.State.Phase is HistoryClearPhase.Failed or HistoryClearPhase.Unavailable) ? HistoryClearCopy.Summary(lines) : "";
        string text = startupResult.Text.Length > 0 ? startupResult.Text : startupQueryFailed ? startupStatus.Text : "";
        if (session.SettingsStatus.Length > 0) text += (text.Length > 0 ? " · " : "") + session.SettingsStatus;
        if (clearing.Length > 0) text += (text.Length > 0 ? " · " : "") + "Clear history · " + clearing;
        showProgress.Visibility = clearing.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool changed = status.Message != text;
        if (changed) status.Message = text;
        status.IsOpen = text.Length > 0;
        if (changed && text.Length > 0)
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(status)
                ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    /// <summary>Reflects the saved settings into every control.</summary>
    private void Sync()
    {
        var settings = session.Settings;
        syncing = true;
        try
        {
            int interval = -1;
            for (int i = 0; i < PollingPolicy.Intervals.Count; i++) if (PollingPolicy.Intervals[i] == settings.PollingIntervalSeconds) interval = i;
            if (polling.SelectedIndex != interval) polling.SelectedIndex = interval;
            processMonitoring.IsChecked = settings.ProcessMonitoring;
            cpuCoreUsage.IsChecked = settings.ShowCpuCoreUsage;
            extended.IsChecked = settings.ExtendedSensors;
            storageSensors.IsChecked = settings.StorageSensors;
            graphSmoothing.IsChecked = settings.GraphSmoothing;
            gpuFanSpeed.IsChecked = settings.ShowGpuFanSpeed;
            cpuPumpSpeed.IsChecked = settings.ShowCpuPumpSpeed;
            var selected = WidgetGraphSelection.Normalize(settings.WidgetGraphs);
            foreach (var (key, choice) in widgetChoices) choice.IsChecked = selected.Contains(key);
            widgetConnectionsMap.IsChecked = settings.WidgetConnectionsMap;
            widgetAlwaysOnTop.IsChecked = settings.WidgetAlwaysOnTop;
            joinedPopouts.IsChecked = settings.JoinCpuGpuPopouts;
            restore.IsChecked = settings.RestoreOpenWindows;
            startInTray.IsChecked = settings.StartInTray;
            usageRecording.IsChecked = settings.UsageRecording;
            alertNotifications.IsChecked = settings.AlertNotifications;
            lightingEnabled.IsOn = settings.LightingEnabled;
            SyncLines();
        }
        finally { syncing = false; }
        SyncExcluded(); SyncClear(); UpdateStatus(); SyncLighting();
    }
}
