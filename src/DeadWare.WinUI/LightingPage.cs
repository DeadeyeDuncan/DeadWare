using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// Lighting: Lights on and Brightness, which the window shows in its header beside the page title as on the approved mockup; a
/// status line; the effect tiles with the chosen effect's settings (<see cref="LightingEffectBar"/>), Static's being Set all colours
/// with Undo; and a card per device, with a colour swatch for each zone of a device
/// DeadWare paints (one running its own effect shows that effect instead of colours). Everything
/// goes to the lighting host through the session's client (<see cref="MonitorSession.Lighting"/>), which the page follows only
/// while it is shown, so a hidden page does no work. Every change makes the host broadcast a new state, so the cards and zone rows
/// are kept by device and zone key and updated in place: a colour flyout stays open while states arrive. Colours go only to a
/// running host: until it runs, whether it is connecting, starting, waiting, blocked or without OpenRGB, the cards stay as they
/// were, disabled, so a relaunched host, which lists no devices until it runs, doesn't take them away.
/// </summary>
internal sealed class LightingPage : Page, IHeaderTool
{
    private const string ConnectingText = "Connecting to the lighting helper…";
    private const string StartingText = "Starting the lighting helper…";
    private const string WaitingText = "Waiting for OpenRGB…";
    private const string ElevationText = "Restart as administrator for RAM, motherboard and GPU lighting";

    private readonly MonitorSession session;
    private readonly DispatcherQueue queue;
    private readonly StackPanel headerTool = new() { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    // The same words on and off, so the switch's visible label is always its name. MinWidth 0 drops the blank space the theme's
    // minimum width leaves after the words.
    private readonly ToggleSwitch lightsOn = new() { OnContent = "Lights on", OffContent = "Lights on", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider brightness = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, SmallChange = 1, LargeChange = 10, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock brightnessLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border offCard;
    private readonly StackPanel onPanel = new() { Spacing = 12, Visibility = Visibility.Collapsed };
    // The status line in two parts: what went wrong, in the caution colour, then progress and the administrator note, muted.
    private readonly TextBlock alert = Caution(), status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly Button setAll = new();
    private readonly Border sharedDot = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout setAllFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly StackPanel setAllContent = new() { Spacing = 8 };
    private readonly Button undo = new() { Content = "Undo", IsEnabled = false };
    private readonly TextBlock noDevices = Muted("No lighting devices found yet.");
    private readonly StackPanel devices = new() { Spacing = 12 };
    private readonly Dictionary<string, DeviceCard> cards = new(StringComparer.Ordinal);
    // One zone flyout serves every swatch: the swatch that opens it names the zone.
    private readonly Flyout zoneFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly StackPanel zoneContent = new() { Spacing = 8 };
    private readonly TextBlock zoneHeading = Heading("");
    private readonly Button useShared = new() { Content = "Use the shared colour" };
    private readonly LightingEffectBar effectBar;
    private readonly Throttle brightnessSends, allSends;
    private readonly Dictionary<string, Throttle> zoneSends = new(StringComparer.Ordinal);
    private ColorPicker? allPicker, zonePicker;
    // The client the page follows, and what it last showed of it.
    private DeviceHostClient? client;
    private HostState? shownState;
    // The effect the effect bar last showed from a state, which it keeps showing, disabled, while the host reconnects.
    private EffectSettings? shownEffect;
    // allShown and zoneShown: each picker's colour as last set by the page or picked, so a repeated colour is not sent again.
    private string? sendError, sharedShown, allShown, zoneShown, zoneKey;
    // The colours as they were when Set all colours opened: what Undo puts back.
    private (string Shared, Dictionary<string, ZoneColorEntry> Zones)? snapshot;
    // generation: bumped when the page starts following another client, so a reply from the old one is dropped.
    private int generation, refreshQueued, lightsSending, brightnessSending;
    // seenState: the followed client has had a state, so Lights on and Brightness hold its values, not the controls' defaults.
    // live: its host is running, the only time colours go to it. undoReady: since Set all colours opened, a pick has been made or
    // an Undo has failed, so Undo has something to put back.
    private bool showing, syncing, dragging, seenState, live, undoReady;

    /// <summary>Asks the window to open another destination by its key: Open Settings asks for "desktop".</summary>
    public Action<string>? NavigateRequested { get; set; }

    /// <summary>
    /// Lights on and Brightness, which the window puts in its header while this page is shown. Hidden while lighting is off and
    /// until the client's first state, so they never show the controls' defaults as if they were the lights'.
    /// </summary>
    public UIElement HeaderTool => headerTool;

    public LightingPage(MonitorSession session)
    {
        this.session = session;
        queue = DispatcherQueue;
        brightnessSends = new Throttle(queue, SendBrightness);
        allSends = new Throttle(queue, request => Send(request));

        AutomationProperties.SetName(lightsOn, "Lights on");
        AutomationProperties.SetName(brightness, "Brightness");
        lightsOn.Toggled += (_, _) => { if (!syncing) SendLightsOn(); };
        // The label shows the value from the start: setting the value the slider already has, 0 at first, raises no change.
        ShowBrightness();
        brightness.ValueChanged += (_, args) =>
        {
            ShowBrightness();
            if (!syncing) brightnessSends.Post(new SetBrightnessRequest((int)Math.Round(args.NewValue)));
        };
        // The slider handles its own pointer events, so these listen to handled ones too. A drag ends when the pointer is released
        // or capture is lost, and its last value goes then rather than at the end of the interval.
        brightness.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => dragging = brightness.IsEnabled), true);
        var released = new PointerEventHandler((_, _) => EndDrag());
        brightness.AddHandler(UIElement.PointerReleasedEvent, released, true);
        brightness.AddHandler(UIElement.PointerCaptureLostEvent, released, true);
        brightness.AddHandler(UIElement.PointerCanceledEvent, released, true);
        // The arrow keys move it a step at a time; the last step goes when the key comes up.
        brightness.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => brightnessSends.Flush()), true);
        headerTool.Children.Add(lightsOn);
        // The widest text the label can show holds its width, so the slider beside it stays put as the value changes.
        var widest = new TextBlock { Text = "Brightness · 100%", Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(widest, AccessibilityView.Raw);
        headerTool.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Grid { Children = { widest, brightnessLabel } }, brightness } });

        var openSettings = new Button { Content = "Open Settings" };
        openSettings.Click += (_, _) => NavigateRequested?.Invoke("desktop");
        offCard = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children = { new TextBlock { Text = "Lighting is off. Turn it on in Settings → Lighting.", TextWrapping = TextWrapping.Wrap }, openSettings },
        });
        offCard.Visibility = Visibility.Collapsed;

        // Static: Set all colours opens a picker whose picks go to every zone, and Undo puts back the colours from when it opened.
        AutomationProperties.SetName(setAll, "Set all colours");
        setAll.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sharedDot, new TextBlock { Text = "Set all colours" } } };
        setAll.Flyout = setAllFlyout;
        setAllContent.Children.Add(undo);
        setAllFlyout.Content = setAllContent;
        setAllFlyout.Opening += (_, _) => OpenSetAll();
        setAllFlyout.Closed += (_, _) => allSends.Flush();
        undo.Click += (_, _) => Undo();
        // The effect tiles and the chosen effect's settings. Static's row is Set all colours, which the bar shows only for Static.
        var staticRow = new StackPanel
        {
            Spacing = 8,
            Children = { Muted("One colour for every zone. Setting it clears the zones' own colours; while the picker is open, Undo puts them back."), setAll },
        };
        effectBar = new LightingEffectBar(queue, Send, staticRow) { Padding = new Thickness(8) };
        var effectCard = Pages.Card(effectBar);

        zoneContent.Children.Add(zoneHeading);
        zoneContent.Children.Add(useShared);
        zoneFlyout.Content = zoneContent;
        zoneFlyout.Closed += (_, _) => { foreach (var pending in zoneSends.Values) pending.Flush(); };
        useShared.Click += (_, _) => UseShared();

        // Polite live regions, so a screen reader reads a new status without moving focus.
        AutomationProperties.SetLiveSetting(alert, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        alert.Visibility = status.Visibility = Visibility.Collapsed;
        noDevices.Visibility = Visibility.Collapsed;
        onPanel.Children.Add(alert);
        onPanel.Children.Add(status);
        onPanel.Children.Add(effectCard);
        onPanel.Children.Add(noDevices);
        onPanel.Children.Add(devices);
        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { offCard, onPanel } } };
        this.WhileShowing(Attach, Detach);
    }

    // Muted, Heading, Show, Hex, ToColor and NewPicker are shared with the effect bar (LightingEffectBar).
    internal static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
    // CoolingPage's warning text: what went wrong, in the caution colour.
    private static TextBlock Caution() => new() { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] };
    internal static TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    internal static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    internal static Color ToColor(string? hex) => Rgb.TryParse(hex, out var rgb) ? Color.FromArgb(255, rgb.R, rgb.G, rgb.B) : Microsoft.UI.Colors.Transparent;

    /// <summary>
    /// Follows the session's client while the page is shown. Its changes are handled from here on, and the session's change is
    /// watched for a new instance: Settings replaces the client when lighting is turned off and on, and with --page lighting the
    /// page can open before the session has made one.
    /// </summary>
    private void Attach()
    {
        showing = true;
        var current = session.Lighting;
        if (!ReferenceEquals(current, client)) Reset(current);
        if (client is not null) client.Changed += OnClientChanged;
        session.Changed += OnSessionChanged;
        Render();
    }

    /// <summary>Stops following, so a hidden page does no work. A change still waiting for its interval goes now rather than being lost.</summary>
    private void Detach()
    {
        showing = false;
        session.Changed -= OnSessionChanged;
        if (client is not null) client.Changed -= OnClientChanged;
        dragging = false;
        brightnessSends.Flush();
        allSends.Flush();
        foreach (var pending in zoneSends.Values) pending.Flush();
        effectBar.Leave();
    }

    /// <summary>The session's change, on the UI thread: once a second from the monitoring tick, and when lighting is turned on or off.</summary>
    private void OnSessionChanged()
    {
        if (!queue.HasThreadAccess) { queue.TryEnqueue(OnSessionChanged); return; }
        var current = session.Lighting;
        if (!showing || ReferenceEquals(current, client)) return;
        if (client is not null) client.Changed -= OnClientChanged;
        Reset(current);
        if (client is not null) client.Changed += OnClientChanged;
        Render();
    }

    /// <summary>
    /// The client's change, raised on any thread: a new state, none while the host reconnects, or a new problem. At most one
    /// refresh waits on the UI thread, and it reads the client as it is when it runs, so a burst of states costs one refresh.
    /// </summary>
    private void OnClientChanged()
    {
        if (Interlocked.Exchange(ref refreshQueued, 1) == 1) return;
        if (!queue.TryEnqueue(Refresh)) Volatile.Write(ref refreshQueued, 0);
    }

    private void Refresh()
    {
        // Cleared before reading, so a change raised while this runs queues the next refresh.
        Volatile.Write(ref refreshQueued, 0);
        if (showing) Render();
    }

    /// <summary>Starts afresh for a new client, or for none: the cards, waiting changes and messages all belonged to the old one.</summary>
    private void Reset(DeviceHostClient? next)
    {
        brightnessSends.Cancel();
        allSends.Cancel();
        foreach (var pending in zoneSends.Values) pending.Cancel();
        zoneSends.Clear();
        generation++;
        lightsSending = brightnessSending = 0;
        client = next;
        shownState = null;
        // The bar drops its waiting changes and drags, which belonged to the old client.
        shownEffect = null;
        effectBar.Show(new EffectSettings(), false);
        // The header hides until the new client's first state, so a drag on it can't get its release.
        seenState = live = dragging = undoReady = false;
        sendError = null;
        snapshot = null;
        sharedShown = null;
        sharedDot.Background = null;
        cards.Clear();
        devices.Children.Clear();
        Show(noDevices, false);
        if (zoneFlyout.IsOpen) zoneFlyout.Hide();
        if (setAllFlyout.IsOpen) setAllFlyout.Hide();
    }

    /// <summary>Brings the page up to date with the followed client, on the UI thread.</summary>
    private void Render()
    {
        bool on = client is not null;
        Show(onPanel, on);
        Show(offCard, !on);
        if (client is null)
        {
            Show(headerTool, false);
            return;
        }
        var state = client.State;
        shownState = state;
        bool connected = state is not null, wasLive = live;
        // Colours go only to a running host; until it runs, the cards stay as they were, disabled.
        live = state is { Status: HostStatus.Running };
        if (!connected)
        {
            // The slider is disabled now and may never get its release, so the drag ends here. A value still waiting for its interval
            // can't reach a host that isn't connected: it is dropped, and the slider shows the host's value once it is back.
            dragging = false;
            brightnessSends.Cancel();
        }
        if (!live)
        {
            // Nor can a colour still waiting for its interval go to a host that isn't running. The open pickers show the host's
            // colours once it runs again.
            allSends.Cancel();
            foreach (var pending in zoneSends.Values) pending.Cancel();
        }
        ShowStatus();
        seenState |= connected;
        Show(headerTool, seenState);
        lightsOn.IsEnabled = brightness.IsEnabled = connected;
        setAll.IsEnabled = live;
        if (state is null)
        {
            // The host is connecting or relaunching: the cards and header controls stay as they were, disabled, until its state is back.
            foreach (var card in cards.Values) card.Disable();
            Show(noDevices, false);
            effectBar.Show(shownEffect ?? new EffectSettings(), false);
        }
        else
        {
            if (state.Settings is { } settings)
            {
                ApplyLightsOn(settings);
                ApplyBrightness(settings);
                ShowShared(settings.DefaultColor);
                shownEffect = settings.Effect ?? shownEffect;
            }
            // Effects go only to a running host whose settings the bar has seen.
            effectBar.Show(shownEffect ?? new EffectSettings(), live && state.Settings is not null);
            ShowDevices(state);
        }
        UpdateFlyouts(resync: live && !wasLive);
    }

    /// <summary>
    /// The status line, in order: the client's problem, the host's message, its blocking conflicts and the administrator note, then
    /// a failed change. What went wrong is in the caution colour above the rest, which is muted. Before the first state, and while
    /// the host reconnects, it says it is connecting unless there is a problem; while the host starts or waits without a message of
    /// its own, it says so.
    /// </summary>
    private void ShowStatus()
    {
        var alerts = new List<string>();
        var notes = new List<string>();
        if (client?.Problem is { Length: > 0 } problem) alerts.Add(problem);
        if (shownState is not { } state)
        {
            if (alerts.Count == 0) notes.Add(ConnectingText);
        }
        else
        {
            string message = state.Message ?? "";
            // A waiting host's message says what it waits for. Any other message reports a problem: a conflict, OpenRGB missing or
            // failing, or a settings file the host couldn't use.
            if (message.Length > 0) (state.Status == HostStatus.Waiting ? notes : alerts).Add(message);
            else if (state.Status is HostStatus.Starting or HostStatus.Waiting) notes.Add(state.Status == HostStatus.Starting ? StartingText : WaitingText);
            // A blocked host's message is made of its blocking conflicts' descriptions, so one it already holds is not repeated.
            foreach (var conflict in state.Conflicts ?? [])
                if (conflict is { BlocksLighting: true, Description.Length: > 0 } && !message.Contains(conflict.Description, StringComparison.Ordinal)) alerts.Add(conflict.Description);
            if (!state.Elevated) notes.Add(ElevationText);
        }
        if (sendError is not null) alerts.Add(sendError);
        ShowLines(alert, alerts);
        ShowLines(status, notes);
    }

    /// <summary>Shows the lines in a status text, hidden while there are none; a screen reader hears it when it changes.</summary>
    private static void ShowLines(TextBlock block, List<string> lines)
    {
        string text = string.Join("\n", lines);
        if (block.Text == text) return;
        block.Text = text;
        Show(block, text.Length > 0);
        if (text.Length > 0) FrameworkElementAutomationPeer.FromElement(block)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>The shared colour on the Set all colours button, repainted only when it changes.</summary>
    private void ShowShared(string? color)
    {
        if (color == sharedShown) return;
        sharedShown = color;
        sharedDot.Background = new SolidColorBrush(ToColor(color));
        ToolTipService.SetToolTip(setAll, color is null ? null : $"Shared colour {color}");
    }

    private void ShowDevices(HostState state)
    {
        var list = (state.Devices ?? []).Where(device => device is { Key: not null }).DistinctBy(device => device.Key, StringComparer.Ordinal).ToArray();
        // Only a running host's list is complete: a relaunched host lists no devices until it runs. So only a running host's list
        // removes the cards of devices that left; until then every card stays, disabled.
        if (live)
        {
            foreach (string gone in cards.Keys.Except(list.Select(device => device.Key)).ToArray())
            {
                devices.Children.Remove(cards[gone].Root);
                cards.Remove(gone);
            }
        }
        else foreach (var card in cards.Values) card.Disable();
        // A device that arrived gets a card. A card that stays never leaves the tree, so a flyout opened from it stays open, and every
        // listed device's card updates in place.
        for (int i = 0; i < list.Length; i++)
        {
            if (cards.ContainsKey(list[i].Key)) continue;
            var card = cards[list[i].Key] = new DeviceCard(this);
            devices.Children.Insert(Math.Min(i, devices.Children.Count), card.Root);
        }
        var names = Names(list);
        for (int i = 0; i < list.Length; i++) cards[list[i].Key].Update(list[i], names[i], live);
        Show(noDevices, live && list.Length == 0);
    }

    /// <summary>The devices' names, with " (2)", " (3)" and so on after the second and later devices of one name, in list order.</summary>
    private static string[] Names(DeviceView[] list)
    {
        var names = new string[list.Length];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
        {
            string name = list[i].Name ?? "";
            int count = seen[name] = seen.GetValueOrDefault(name) + 1;
            names[i] = count == 1 ? name : $"{name} ({count})";
        }
        return names;
    }

    /// <summary>
    /// Keeps open colour flyouts in step with the host. Their controls work only while it runs, and when it runs again their pickers
    /// show its colours, since a pick made before it stopped may never have reached it. The zone flyout follows its zone and closes
    /// if the zone went away.
    /// </summary>
    private void UpdateFlyouts(bool resync)
    {
        if (allPicker is not null)
        {
            allPicker.IsEnabled = live;
            if (resync && setAllFlyout.IsOpen && shownState?.Settings is { } settings) SetPicker(allPicker, settings.DefaultColor, ref allShown);
        }
        UpdateUndo();
        if (zonePicker is null || !zoneFlyout.IsOpen || zoneKey is not { } key) return;
        if (cards.Values.Select(card => card.Find(key)).FirstOrDefault(row => row is not null) is not { Zone: { } zone } row)
        {
            zoneFlyout.Hide();
            return;
        }
        LabelZoneFlyout(row, zone);
        zonePicker.IsEnabled = row.CanPaint;
        useShared.IsEnabled = row.CanPaint && zone.OwnColor;
        if (resync && row.CanPaint) SetPicker(zonePicker, zone.Color, ref zoneShown);
    }

    private void SendLightsOn()
    {
        bool on = lightsOn.IsOn;
        lightsSending++;
        Send(new SetLightsOnRequest(on), reply =>
        {
            lightsSending--;
            // The state carrying this change follows its reply; until it is here the older one would flip the switch back.
            if (client?.State?.Settings is { } settings && (!reply.Ok || settings.LightsOn == on)) ApplyLightsOn(settings);
        });
    }

    private void SendBrightness(HostMessage request)
    {
        int percent = ((SetBrightnessRequest)request).Percent;
        brightnessSending++;
        Send(request, reply =>
        {
            brightnessSending--;
            if (client?.State?.Settings is { } settings && (!reply.Ok || settings.Brightness == percent)) ApplyBrightness(settings);
        });
    }

    /// <summary>Shows the host's Lights on, except while the user's own change is on its way. The guard keeps it from being sent back.</summary>
    private void ApplyLightsOn(LightingSettings settings)
    {
        if (lightsSending > 0) return;
        syncing = true;
        try { lightsOn.IsOn = settings.LightsOn; }
        finally { syncing = false; }
    }

    /// <summary>Shows the host's brightness, except while the user drags the slider or a value of theirs waits or is on its way.</summary>
    private void ApplyBrightness(LightingSettings settings)
    {
        if (dragging || brightnessSending > 0 || brightnessSends.Pending) return;
        syncing = true;
        try { brightness.Value = Math.Clamp(settings.Brightness, 0, 100); }
        finally { syncing = false; }
    }

    private void ShowBrightness() => brightnessLabel.Text = $"Brightness · {brightness.Value:0}%";

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        brightnessSends.Flush();
        // A press that changed nothing leaves nothing on its way, so the slider takes any state that arrived meanwhile.
        if (client?.State?.Settings is { } settings) ApplyBrightness(settings);
    }

    /// <summary>Makes the picker the first time; takes the Undo snapshot and shows the shared colour each time the flyout opens.</summary>
    private void OpenSetAll()
    {
        if (allPicker is null)
        {
            // The picker is a large control, so it is made the first time it is needed.
            allPicker = NewPicker();
            AutomationProperties.SetName(allPicker, "Colour for all devices");
            allPicker.ColorChanged += OnAllPicked;
            setAllContent.Children.Insert(0, allPicker);
        }
        var settings = client?.State?.Settings;
        snapshot = settings is null ? null : (settings.DefaultColor, Copy(settings.ZoneColors));
        undoReady = false;
        UpdateUndo();
        allPicker.IsEnabled = live;
        if (settings is not null) SetPicker(allPicker, settings.DefaultColor, ref allShown);
    }

    private void OnAllPicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!Picked(sender, args, ref allShown, out string color)) return;
        undoReady = true;
        UpdateUndo();
        allSends.Post(new SetAllColorsRequest(color));
    }

    /// <summary>Puts back the shared colour and every zone's own colour as they were when the flyout opened.</summary>
    private void Undo()
    {
        if (!live || snapshot is not { } taken) return;
        // A pick still waiting for its interval would land after the undo and paint over it.
        allSends.Cancel();
        undoReady = false;
        UpdateUndo();
        Send(new ReplaceColorsRequest(taken.Shared, Copy(taken.Zones)), reply =>
        {
            // A reply to an Undo from an earlier opening of the flyout changes nothing in this one.
            if (snapshot is not { } current || !ReferenceEquals(current.Zones, taken.Zones)) return;
            // A failed Undo can be tried again. One that worked shows the colour it put back, unless a pick has come since.
            if (!reply.Ok) undoReady = true;
            else if (!undoReady && allPicker is not null) SetPicker(allPicker, taken.Shared, ref allShown);
            UpdateUndo();
        });
    }

    /// <summary>Undo works while the host runs, once a pick or a failed Undo has left something to put back.</summary>
    private void UpdateUndo() => undo.IsEnabled = live && undoReady && snapshot is not null;

    /// <summary>Opens the zone flyout under a zone's swatch, for that zone.</summary>
    private void OpenZone(ZoneRow row)
    {
        if (!row.CanPaint || row.Zone is not { } zone) return;
        if (zonePicker is null)
        {
            zonePicker = NewPicker();
            zonePicker.ColorChanged += OnZonePicked;
            zoneContent.Children.Insert(1, zonePicker);
        }
        zoneKey = zone.Key;
        LabelZoneFlyout(row, zone);
        zonePicker.IsEnabled = true;
        useShared.IsEnabled = zone.OwnColor;
        SetPicker(zonePicker, zone.Color, ref zoneShown);
        zoneFlyout.ShowAt(row.Swatch);
    }

    /// <summary>Names the zone flyout's heading and picker after its zone and device.</summary>
    private void LabelZoneFlyout(ZoneRow row, ZoneView zone)
    {
        zoneHeading.Text = $"{row.Device} · {zone.Name}";
        if (zonePicker is not null) AutomationProperties.SetName(zonePicker, $"Colour for {zone.Name} on {row.Device}");
    }

    private void OnZonePicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (zoneKey is not { } key || !Picked(sender, args, ref zoneShown, out string color)) return;
        ZoneSends(key).Post(new SetZoneColorRequest(key, color));
    }

    private void UseShared()
    {
        if (!live || zoneKey is not { } key) return;
        // A pick still waiting for its interval would land after this and give the zone its own colour again.
        if (zoneSends.TryGetValue(key, out var pending)) pending.Cancel();
        Send(new SetZoneColorRequest(key, null));
        zoneFlyout.Hide();
    }

    /// <summary>Each zone has its own throttle, so one zone's waiting pick never replaces another's.</summary>
    private Throttle ZoneSends(string key)
    {
        if (!zoneSends.TryGetValue(key, out var throttle)) zoneSends[key] = throttle = new Throttle(queue, request => Send(request));
        return throttle;
    }

    /// <summary>
    /// True for the user's own pick of a new colour while the host runs: not the colour the page shows when a flyout opens, nor one
    /// the picker raises while it loads.
    /// </summary>
    private bool Picked(ColorPicker picker, ColorChangedEventArgs args, ref string? shown, out string color)
    {
        color = Hex(args.NewColor);
        if (syncing || !live || !picker.IsLoaded || color == shown) return false;
        shown = color;
        return true;
    }

    internal static ColorPicker NewPicker() => new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false, IsMoreButtonVisible = true };

    /// <summary>Shows a colour in a picker without sending it: the guard skips the change it raises.</summary>
    private void SetPicker(ColorPicker picker, string color, ref string? shown)
    {
        var value = ToColor(color);
        shown = Hex(value);
        syncing = true;
        try { picker.Color = value; }
        finally { syncing = false; }
    }

    /// <summary>A copy of the zones' own colours, so a snapshot never shares an entry with a state or a request.</summary>
    private static Dictionary<string, ZoneColorEntry> Copy(Dictionary<string, ZoneColorEntry>? zones)
    {
        var copy = new Dictionary<string, ZoneColorEntry>(StringComparer.Ordinal);
        if (zones is null) return copy;
        foreach (var (key, entry) in zones)
            if (entry is not null) copy[key] = new ZoneColorEntry { Color = entry.Color, ZoneName = entry.ZoneName };
        return copy;
    }

    /// <summary>
    /// Sends a request to the followed client; the reply comes back here, on the UI thread. A failure shows in the status line until a
    /// later change succeeds or the page follows another client: a state arriving doesn't end it. A reply from a client the page no
    /// longer follows is dropped with its callback.
    /// </summary>
    private async void Send(HostMessage request, Action<Reply>? done = null)
    {
        int sent = generation;
        Reply reply;
        if (client is not { } target) reply = new Reply(false, null);
        else
        {
            // SendAsync answers connection problems with a failed reply and throws only when its token is cancelled, which this one never is.
            try { reply = await target.SendAsync(request, CancellationToken.None); }
            catch (Exception failure) { reply = new Reply(false, failure.Message); }
        }
        if (sent != generation) return;
        done?.Invoke(reply);
        string? error = reply.Ok ? null : DeviceHostClient.FailedChangeText(reply.Error);
        if (error == sendError) return;
        sendError = error;
        ShowStatus();
    }

    /// <summary>
    /// One device's card, kept by device key and updated in place. Its zone rows are rebuilt only when the device's zones change, so
    /// a flyout opened from one of its swatches survives the state that every colour change brings.
    /// </summary>
    private sealed class DeviceCard
    {
        private readonly LightingPage page;
        private readonly TextBlock title = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock effect = Muted(""), staysLit = Muted("Stays lit when lights are off"), ownEffect = Muted("Runs its own effect, so DeadWare's colours don't show on it."),
            cannotDrive = Muted("DeadWare can't drive this device"), noZones = Muted("This device has no colour zones.");
        private readonly StackPanel zones = new() { Spacing = 4 };
        private readonly Dictionary<string, ZoneRow> rows = new(StringComparer.Ordinal);
        private string[] zoneKeys = [];

        public DeviceCard(LightingPage page)
        {
            this.page = page;
            Root = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { title, effect, staysLit, ownEffect, cannotDrive, noZones, zones } });
        }

        public Border Root { get; }

        public ZoneRow? Find(string zoneKey) => rows.GetValueOrDefault(zoneKey);

        /// <param name="device">The device as the host lists it.</param>
        /// <param name="name">Its name as the page shows it, numbered when another device has the same one.</param>
        /// <param name="live">The host is running, so the swatches of a device DeadWare paints can be used.</param>
        public void Update(DeviceView device, string name, bool live)
        {
            title.Text = name;
            bool hardware = device.Kind == PaintKind.Hardware, unsupported = device.Kind == PaintKind.Unsupported;
            // The device's own effect and whether it stays lit with lights off don't change with Lights on (Ruling AB), so the card
            // names the effect while the lights are off too, and warns before they go off that the device will stay lit.
            effect.Text = device.OwnEffect is { Length: > 0 } own ? $"Own hardware effect: {own}" : "Own hardware effect";
            Show(effect, hardware);
            Show(staysLit, hardware && device.StaysLitWhenOff);
            Show(ownEffect, hardware);
            Show(cannotDrive, unsupported);
            // Only a device DeadWare paints lists its zones. One running its own effect keeps its zones' colours, but they don't show on it,
            // so it shows none (polish item 1: a Node Pro's swatches in the shared colour read as its fans' colour); Set all colours still
            // sets the shared colour. One DeadWare can't drive takes no colours. Neither keeps rows, so a zone flyout open on one closes.
            bool paints = device.Kind == PaintKind.Direct;
            var list = paints ? (device.Zones ?? []).Where(zone => zone is { Key: not null }).DistinctBy(zone => zone.Key, StringComparer.Ordinal).ToArray() : [];
            Show(zones, paints);
            Show(noZones, paints && list.Length == 0);
            if (!list.Select(zone => zone.Key).SequenceEqual(zoneKeys))
            {
                zoneKeys = [.. list.Select(zone => zone.Key)];
                rows.Clear();
                zones.Children.Clear();
                foreach (var zone in list)
                {
                    var row = new ZoneRow(page);
                    rows[zone.Key] = row;
                    zones.Children.Add(row.Root);
                }
            }
            foreach (var zone in list) rows[zone.Key].Update(name, zone, live);
        }

        /// <summary>Disables the swatches while the host isn't running, leaving the card as it was.</summary>
        public void Disable()
        {
            foreach (var row in rows.Values) row.Swatch.IsEnabled = false;
        }
    }

    /// <summary>A zone's row: its colour swatch, which opens the zone flyout, its name, and whether its colour is its own or the shared one.</summary>
    private sealed class ZoneRow
    {
        private readonly Border dot = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(9) };
        private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock source = Muted("");
        private string? color, described;

        public ZoneRow(LightingPage page)
        {
            Swatch = new Button { Padding = new Thickness(4), Content = dot, VerticalAlignment = VerticalAlignment.Center };
            Swatch.Click += (_, _) => page.OpenZone(this);
            source.VerticalAlignment = VerticalAlignment.Center;
            Root = new Grid { ColumnSpacing = 12 };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 1);
            Grid.SetColumn(source, 2);
            Root.Children.Add(Swatch);
            Root.Children.Add(name);
            Root.Children.Add(source);
        }

        public Grid Root { get; }
        public Button Swatch { get; }
        /// <summary>The device's name as its card shows it.</summary>
        public string Device { get; private set; } = "";
        public ZoneView? Zone { get; private set; }
        /// <summary>The swatch can be used: the host runs, DeadWare paints the device and the zone has LEDs.</summary>
        public bool CanPaint => Swatch.IsEnabled;

        /// <param name="device">The device's name as its card shows it.</param>
        /// <param name="zone">The zone as the host lists it.</param>
        /// <param name="paints">The host runs and paints the device, so a zone with LEDs can take a colour.</param>
        public void Update(string device, ZoneView zone, bool paints)
        {
            Device = device;
            Zone = zone;
            name.Text = zone.Name;
            source.Text = zone.OwnColor ? "Own colour" : "Shared colour";
            // A zone with no LEDs has nothing to colour, so it gets no swatch.
            bool lit = zone.LedCount > 0;
            Show(Swatch, lit);
            Swatch.IsEnabled = paints && lit;
            if (zone.Color != color)
            {
                color = zone.Color;
                dot.Background = new SolidColorBrush(ToColor(color));
                ToolTipService.SetToolTip(Swatch, color);
            }
            string description = $"Colour for {zone.Name} on {device}";
            if (description == described) return;
            described = description;
            AutomationProperties.SetName(Swatch, description);
        }
    }
}
