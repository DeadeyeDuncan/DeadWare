using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// Settings in six sections: Monitoring, Appearance, Widget, Pop-outs, Startup, and History and alerts. Light, dark and
/// accent colours follow Windows, so the only appearance choices are how the graph lines look.
/// </summary>
internal sealed class SettingsPage : Page
{
    private static readonly string[] Sections = ["Monitoring", "Appearance", "Widget", "Pop-outs", "Startup", "History and alerts"];
    private static readonly (string Key, string Label)[] WidgetGraphs =
        [("cpu", "CPU / GPU usage"), ("cputemp", "CPU / GPU temperature"), ("memory", "RAM usage"), ("network", "Network activity"), ("disk", "Disk activity")];
    private static readonly (string Key, string Label)[] Lines =
    [
        ("cpu", "CPU usage"), ("cputemp", "CPU temperature"), ("cpupump", "CPU pump speed"), ("gpu", "GPU usage"), ("gputemp", "GPU temperature"),
        ("gpufan", "GPU fan speed"), ("memory", "RAM usage"), ("download", "Download"), ("upload", "Upload"), ("diskread", "Disk read"), ("diskwrite", "Disk write"),
    ];
    private static readonly string[] Swatches = ["#68E0BD", "#52D6E8", "#82ADFF", "#B69CFF", "#EF8FE0", "#FF8899", "#F0B76C", "#F2D56B", "#A9DE7A", "#FFFFFF", "#A6B4C6", "#182A3B"];
    private static readonly string[] Patterns = ["default", "solid", "dashed", "dotted"];
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
    private readonly CheckBox graphSmoothing = new() { Content = "Graph smoothing" }, gpuFanSpeed = new() { Content = "Show GPU fan speed on temperature graphs" }, cpuPumpSpeed = new() { Content = "Show CPU pump speed on CPU temperature graphs" };
    private readonly ComboBox line = new() { MinWidth = 220 }, pattern = new() { MinWidth = 140 };
    private readonly Border lineDot = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock lineColor = Muted(""), thicknessLabel = new(), strengthLabel = new();
    private readonly Slider thickness = new() { Minimum = 50, Maximum = 300, StepFrequency = 25, SmallChange = 25, LargeChange = 50, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Slider strength = new() { Minimum = 0, Maximum = 300, StepFrequency = 10, SmallChange = 10, LargeChange = 50, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox glow = new() { Content = "Neon glow" };
    private readonly ColorPicker picker = new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false, IsMoreButtonVisible = true };
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

    /// <summary>Asks the window to open another destination by its key ("usage", "alerts", "connections").</summary>
    public Action<string>? NavigateRequested { get; set; }

    public SettingsPage(MonitorSession session, Func<Task> quit)
    {
        this.session = session;
        this.quit = quit;
        suggestions = new AppSuggestions(session);
        // The banner is a polite live region, so a screen reader reads a new status without moving focus.
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panels = [Monitoring(), Appearance(), Widget(), Popouts(), Startup(), History()];
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
        Sync();
    }

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

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
        panel.Children.Add(Bind(graphSmoothing, "Graph smoothing", session.SetGraphSmoothing));
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

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (string color in Swatches)
        {
            var swatch = new Button { Padding = new Thickness(4), Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Parse(color)) } };
            ToolTipService.SetToolTip(swatch, color);
            AutomationProperties.SetName(swatch, $"Use {color}");
            swatch.Click += (_, _) => { if (LineKey is { } key) session.SetGraphColor(key, color); };
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(new ScrollViewer { Content = swatches, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });

        var flyout = new Flyout();
        var use = new Button { Content = "Use this colour", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        use.Click += (_, _) =>
        {
            var chosen = picker.Color;
            if (LineKey is { } key) session.SetGraphColor(key, $"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}");
            flyout.Hide();
        };
        flyout.Content = new StackPanel { Spacing = 8, Children = { picker, use } };
        flyout.Opening += (_, _) =>
        {
            if (LineKey is { } key && session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved)) picker.Color = Parse(saved);
        };
        var more = new Button { Content = "More colours…", Flyout = flyout };
        var automatic = Action("Default colour", () => { if (LineKey is { } key) session.SetGraphColor(key, null); });
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { more, automatic } });

        foreach (string label in new[] { "Default", "Solid", "Dashed", "Dotted" }) pattern.Items.Add(label);
        AutomationProperties.SetName(pattern, "Line pattern");
        AutomationProperties.SetName(thickness, "Line thickness percent");
        AutomationProperties.SetName(strength, "Neon glow strength percent");
        ToolTipService.SetToolTip(thickness, "50–300% of the normal trace width; 100% keeps the original thickness");
        ToolTipService.SetToolTip(strength, "0–300% glow brightness and spread; 100% keeps the original glow");
        pattern.SelectionChanged += (_, _) => SaveStyle();
        thickness.ValueChanged += (_, _) => SaveStyle();
        strength.ValueChanged += (_, _) => SaveStyle();
        glow.Checked += (_, _) => SaveStyle();
        glow.Unchecked += (_, _) => SaveStyle();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "Line pattern", VerticalAlignment = VerticalAlignment.Center }, pattern } });
        panel.Children.Add(thicknessLabel); panel.Children.Add(thickness);
        panel.Children.Add(glow);
        panel.Children.Add(strengthLabel); panel.Children.Add(strength);

        coreActions.Children.Add(Tipped(Action("Apply style to all cores", () => session.ApplyStyleToAllCores(lineCores, false)), "Copy the CPU line's pattern, thickness and glow to every core; colours keep their tints"));
        coreActions.Children.Add(Tipped(Action("Make all cores the CPU colour", () => session.ApplyStyleToAllCores(lineCores, true)), "Copy the CPU line's style and give every core the same colour as the CPU line"));
        coreActions.Children.Add(Tipped(Action("Reset all cores to auto", session.ResetCpuCoreStyles), "Drop every core's own colour and style"));
        panel.Children.Add(new ScrollViewer { Content = coreActions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled });
        return panel;
    }

    private static Button Tipped(Button button, string tip) { ToolTipService.SetToolTip(button, tip); return button; }

    private static Color Parse(string hex) =>
        Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));

    private string? LineKey => line.SelectedIndex >= 0 && line.SelectedIndex < lineKeys.Length ? lineKeys[line.SelectedIndex] : null;

    /// <summary>Lists the fixed traces and one entry per live core; rebuilt only when the core count changes.</summary>
    private void SyncLines()
    {
        int cores = Math.Clamp(session.Current?.CpuCoreCount ?? 0, 0, GraphColorSettings.MaxCores);
        if (cores != lineCores)
        {
            int selected = Math.Max(0, line.SelectedIndex);
            lineCores = cores;
            var keys = Lines.Select(item => item.Key).ToList();
            line.Items.Clear();
            foreach (var (_, label) in Lines) line.Items.Add(label);
            for (int i = 0; i < cores; i++) { keys.Add(GraphColorSettings.CoreKey(i)); line.Items.Add($"CPU core {i + 1}"); }
            lineKeys = [.. keys];
            line.SelectedIndex = Math.Min(selected, lineKeys.Length - 1);
            coreActions.Visibility = cores > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncLine();
    }

    /// <summary>Reflects the selected trace's saved colour and style into the editor.</summary>
    private void SyncLine()
    {
        if (LineKey is not { } key) return;
        bool was = syncing; syncing = true;
        try
        {
            bool own = session.Settings.GraphColors.TryGetValue(key, out string? saved) && GraphColorSettings.IsColor(saved);
            lineDot.Background = own ? new SolidColorBrush(Parse(saved!)) : null;
            lineDot.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
            lineColor.Text = own ? saved! : GraphColorSettings.TryCoreIndex(key, out _) ? "Auto tint" : "Default colour";
            var style = session.Settings.GraphStyles.GetValueOrDefault(key, new GraphLineStyle());
            pattern.SelectedIndex = Math.Max(0, Array.IndexOf(Patterns, style.Pattern));
            glow.IsChecked = style.Glow;
            thickness.Value = style.Thickness * 100;
            strength.Value = style.GlowStrength * 100;
        }
        finally { syncing = was; }
        UpdateStyleLabels();
    }

    private void UpdateStyleLabels()
    {
        thicknessLabel.Text = $"Line thickness · {thickness.Value:0}%";
        strengthLabel.Text = $"Glow strength · {strength.Value:0}%";
        strength.IsEnabled = glow.IsChecked == true;
        strengthLabel.Opacity = strength.IsEnabled ? 1 : 0.5;
    }

    private void SaveStyle()
    {
        UpdateStyleLabels();
        if (syncing || LineKey is not { } key || pattern.SelectedIndex < 0) return;
        session.SetGraphStyle(key, Patterns[pattern.SelectedIndex], glow.IsChecked == true, thickness.Value / 100, strength.Value / 100);
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
            SyncLines();
        }
        finally { syncing = false; }
        SyncExcluded(); SyncClear(); UpdateStatus();
    }
}
