using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// Lighting: Lights on and Brightness, which the window shows in its header beside the page title as on the approved mockup; a
/// status line; the effect tiles with the chosen effect's settings (<see cref="LightingEffectBar"/>), Static's being Set all colours
/// with Undo; the desk (<see cref="DeskView"/>) in live colours, with the selected device's panel beside it (below it on a narrow
/// window) and, under it, the list of every device and the devices not placed. The panel is that device's card: a colour swatch
/// for each zone of a device DeadWare paints (one running its own effect shows that effect instead of colours). Everything
/// goes to the lighting host through the session's client (<see cref="MonitorSession.Lighting"/>), which the page follows only
/// while it is shown, so a hidden page does no work. Every change makes the host broadcast a new state, so the cards, zone rows
/// and list rows are kept by device and zone key and updated in place: a colour flyout stays open while states arrive. Colours go
/// only to a running host: until it runs, whether it is connecting, starting, waiting, blocked or without OpenRGB, the desk, list
/// and panel stay as they were, disabled, so a relaunched host, which lists no devices until it runs, doesn't take them away.
/// Until there is something to draw (a picture named by the layout, or a device found) one line stands in place of the desk area.
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
    // Lighting 4: why the lights are off beyond the switch (idle, locked or waking), in the caution colour above the status line. It is a
    // line of its own, not one of the alerts, so the muted status line ("… · lights off") still shows under it.
    private readonly TextBlock reason = Caution();
    // Lighting 4: the Everyday card (shortcut, idle and lock), under the desk area in both modes.
    private readonly EverydayCard everyday;
    private readonly Button setAll = new();
    private readonly Border sharedDot = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout setAllFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly StackPanel setAllContent = new() { Spacing = 8 };
    private readonly Button undo = new() { Content = "Undo", IsEnabled = false };
    private readonly TextBlock noDevices = Muted("No lighting devices found yet.");
    // In place of the desk area while there is nothing to draw yet: no picture named and no device found. After NoDevicesAfterMs of a
    // running host listing none, noDevices takes its place. noneSince: when the running host was first shown listing none
    // (Environment.TickCount64); the session's once-a-second change is what looks at it again, so no timer is kept for it.
    private readonly TextBlock looking = Muted("Looking for lighting devices…");
    private const long NoDevicesAfterMs = 45_000;
    private long? noneSince;
    private const string NothingSelectedText = "Select a device on the desk or in the list.";
    private const string NothingSelectedHeading = "Nothing selected", TapDeviceText = "Tap a device in the picture to change its colours.", TapPartText = "Tap a part to change its colours.";
    // Below this width the panel goes under the desk card instead of beside it: the plain desk's, and the picture's.
    private const double WideLayout = 900, PictureWideLayout = 1040;
    // Picture mode's columns: the picture card 7 parts in 10, at most the picture's 1280 px and its card's padding; the panel 3 in 10,
    // at least 280 px and taking what the capped picture leaves, so the two columns end where the effect bar does. (The owner's maximised page is about 2290 px wide: the first look's screenshot was scaled.)
    private const double PictureCardMax = 1312, PicturePanelMin = 280, PicturePanelMax = double.PositiveInfinity;
    private readonly Dictionary<string, DeviceCard> cards = new(StringComparer.Ordinal);
    // The desk card, the panel and the list card. Wide: the desk card and the list card in leftColumn, the panel beside them. Narrow:
    // the desk card, the panel and leftColumn (the list card alone) one under the other. Only the desk card moves between the two,
    // so the panel, which a zone flyout may be open from, never leaves the tree.
    private readonly DeskView desk = new();
    private readonly Border deskCard, panelHost = new(), panelSlot = new(), emptyPanel, layoutPanel, listCard;
    private readonly TextBlock emptyText = Muted(NothingSelectedText);
    private readonly Grid deskHeader = new() { ColumnSpacing = 12 };
    // Picture mode: the layout names a picture of a known rig (the host decides, never the page), and the desk card shows that picture
    // (DeskPictureView) instead of the plain desk, with nothing that could send a layout: no Edit layout, device list or Not placed.
    // shownPicture is null in plain mode. The view (desk or inside) and selectedDevice, the selected part's device, are view state:
    // not saved, and back to the desk view with nothing selected when the page is shown again, the client is replaced or the mode changes.
    private const string DeskHint = "tap a device to change its colours · tap the PC to see inside";
    private const string InsideHint = "side window, front on the right · tap a part to change its colours";
    private const string KeepColourText = " · they keep your saved colour during effects";
    private readonly DeskPictureView picture = new();
    // Lighting 3b: the large keyboard view's tools, for the picture's keyboard view: its switch and Done in the header, its card in the panel.
    private readonly KeyboardEditor keyEditor;
    private readonly Grid pictureHeader = new() { ColumnSpacing = 10 };
    private readonly TextBlock pictureTitle = Heading("Desk"), pictureHint = Muted(DeskHint);
    private readonly Button backToDesk = new() { Content = "← Back to desk", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock notInPicture = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Run notInPictureList = new();
    private DeskPicture? shownPicture;
    // The picture the latest state's layout named, running or not; null with a new client.
    private string? statePicture;
    private PictureScene? scene;
    private string? selectedDevice;
    // The effect from the last running state, for the picture's wave band.
    private EffectSettings? deskEffect;
    // The card that last showed the picture's heading and highlighted row, cleared when it leaves the panel.
    private DeviceCard? markedCard;
    // Edit layout: the toggle on the desk card, its tools (shown only while it is on), and the selected device's layout controls,
    // shown under its panel while it is on.
    private readonly ToggleButton editToggle = new() { Content = "Edit layout", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel editTools = new() { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly ComboBox shapeBox = new() { Header = "Desk shape", Items = { "Straight", "L left", "L right" }, MinWidth = 140 };
    private readonly Button resetPositions = new() { Content = "Reset positions", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly CheckBox inTower = new() { Content = "Inside PC tower" };
    private readonly ToggleSwitch splitZones = new() { OnContent = "Move zones separately", OffContent = "Move zones separately", MinWidth = 0 };
    private readonly Button takeOff = new() { Content = "Take off the desk" };
    // When the desk's draft goes (LayoutDraftGate): SetLayout requests on their way, and the client's state count when the read loop read
    // the Ok reply that left none on its way. The host writes a reply before the state its request caused, so the draft goes once a state
    // counted above that is shown (shownSeq: the shown state's count). Dropping the draft makes every late reply count for nothing.
    private readonly LayoutDraftGate layoutGate = new();
    private long shownSeq;
    // Arrow keys: at most one nudge save on its way (nudgeTicket names it; dropping the draft retires it). nudgeDirty: a nudge moved the
    // draft while one was on its way and no save has carried it yet; it goes when that one's reply is Ok, and holds the draft until then.
    private int nudgeTicket;
    private bool nudgeInFlight, nudgeDirty;
    // And at most one nudge save per NudgeGap: a nudge that comes sooner after the last nudge save was sent waits (nudgeDirty), and
    // nudgeTimer, one-shot on the page's queue, sends the waiting draft once the gap is up. Dropping the draft stops it.
    private const int NudgeGapMs = 200;
    private readonly DispatcherQueueTimer nudgeTimer;
    private long? lastNudgeSentAt;
    private bool layoutSyncing, resetAsking;
    private readonly Grid deskArea = new() { RowSpacing = 12 };
    private readonly StackPanel leftColumn = new() { Spacing = 12 };
    // The empty panel's heading: the tower's caption while the plain desk's tower is selected, "Nothing selected" in picture mode.
    private readonly TextBlock emptyHeading = Heading("");
    private readonly ListView deviceList = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Dictionary<string, ListRow> listRows = new(StringComparer.Ordinal);
    private readonly StackPanel notPlaced = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly StackPanel notPlacedRows = new() { Spacing = 4 };
    private readonly Dictionary<string, PlaceRow> placeRows = new(StringComparer.Ordinal);
    // Place on desk sends on their way, by device key. At: null until the reply; after an Ok reply, the state the client had then. The
    // pipe writes a reply before the state its request caused, so the button stays disabled until a newer state than that is shown.
    private readonly Dictionary<string, (bool Replied, HostState? At)> placing = new(StringComparer.Ordinal);
    // What the desk and list last showed from a running host, kept while it isn't running.
    private DeskLayout deskLayout = DeskLayouts.Empty();
    private DeviceView[] deskDevices = [];
    private bool deskLightsOn = true, listSyncing;
    // The running state's KeyHook as the desk last showed it, for the layers editor's note.
    private bool deskKeyHook;
    // How the desk area was last arranged, and the page's width then: the mode changes the arrangement too.
    private (bool Wide, bool Picture)? arranged;
    private double deskWidth;
    // The preview frame waiting for the UI thread, at most one: a newer frame replaces it. previewHandler: the handler on the followed client.
    private sealed record PreviewFrame(DeviceHostClient Source, PreviewColors Colors);
    private PreviewFrame? pendingFrame;
    private int frameQueued;
    private Action<PreviewColors>? previewHandler;
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
    // Lighting 5: whether the hub readings were fresh when the page last rendered, so the once-a-second change renders again as they go stale.
    private bool hubFresh;
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
        nudgeTimer = queue.CreateTimer();
        nudgeTimer.IsRepeating = false;
        nudgeTimer.Tick += (_, _) => SendWaitingNudge();
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
        effectBar = new LightingEffectBar(queue, Send, staticRow, () => { if (setAllFlyout.IsOpen) setAllFlyout.Hide(); }) { Padding = new Thickness(8) };
        var effectCard = Pages.Card(effectBar);

        zoneContent.Children.Add(zoneHeading);
        zoneContent.Children.Add(useShared);
        zoneFlyout.Content = zoneContent;
        zoneFlyout.Closed += (_, _) => { foreach (var pending in zoneSends.Values) pending.Flush(); };
        useShared.Click += (_, _) => UseShared();

        // Polite live regions, so a screen reader reads a new status without moving focus.
        AutomationProperties.SetLiveSetting(alert, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(reason, AutomationLiveSetting.Polite);
        reason.Visibility = alert.Visibility = status.Visibility = Visibility.Collapsed;
        noDevices.Visibility = Visibility.Collapsed;
        onPanel.Children.Add(reason);
        onPanel.Children.Add(alert);
        onPanel.Children.Add(status);
        onPanel.Children.Add(effectCard);
        onPanel.Children.Add(noDevices);
        onPanel.Children.Add(looking);

        // The desk, the selected device's panel, the device list and the devices not placed.
        deskHeader.ColumnDefinitions.Add(new ColumnDefinition());
        deskHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var deskHeading = Heading("Desk");
        deskHeading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editToggle, 1);
        deskHeader.Children.Add(deskHeading);
        deskHeader.Children.Add(editToggle);
        editToggle.Click += (_, _) => SetEditing(editToggle.IsChecked == true);
        editTools.Children.Add(shapeBox);
        editTools.Children.Add(resetPositions);
        shapeBox.SelectionChanged += (_, _) => ChangeShape();
        resetPositions.Click += (_, _) => ResetPositions();
        // Picture mode's header: the view's title and hint, and Back to desk in the inside view; under the picture, what isn't in it.
        pictureHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pictureHeader.ColumnDefinitions.Add(new ColumnDefinition());
        pictureHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pictureTitle.VerticalAlignment = pictureHint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(pictureHint, 1);
        Grid.SetColumn(backToDesk, 2);
        pictureHeader.Children.Add(pictureTitle);
        pictureHeader.Children.Add(pictureHint);
        pictureHeader.Children.Add(backToDesk);
        backToDesk.Click += (_, _) => ShowPictureView(PictureView.Desk);
        // The keyboard view's switch and Done sit where Back to desk does; only one of them shows.
        keyEditor = new KeyboardEditor(queue, SendTracked, picture);
        keyEditor.DoneRequested += () => LeaveKeys(sendWaiting: true);
        keyEditor.PartChanged += ShowPictureHeader;
        Grid.SetColumn(keyEditor.Bar, 2);
        Show(keyEditor.Bar, false);
        pictureHeader.Children.Add(keyEditor.Bar);
        notInPicture.Inlines.Add(new Run { Text = "Not in the picture:", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        notInPictureList.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        notInPicture.Inlines.Add(notInPictureList);
        picture.PartTapped += SelectPart;
        picture.TowerTapped += () => ShowPictureView(PictureView.Inside);
        picture.DeskRequested += () => ShowPictureView(PictureView.Desk);
        Show(pictureHeader, false);
        Show(picture, false);
        Show(notInPicture, false);
        deskCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { deskHeader, pictureHeader, editTools, desk, picture, notInPicture } });
        desk.SelectionChanged += _ => { SyncList(); ShowPanel(); };
        desk.LayoutEdited += next => SendLayout(next);
        desk.Nudged += OnNudged;
        Show(emptyHeading, false);
        emptyPanel = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { emptyHeading, emptyText } });
        AutomationProperties.SetName(splitZones, "Move zones separately");
        inTower.Click += (_, _) => SetInTower(inTower.IsChecked == true);
        splitZones.Toggled += (_, _) => { if (!layoutSyncing && shownPicture is null && desk.Selected is { } key && key != DeskView.Tower) desk.SetSplit(key, splitZones.IsOn); };
        takeOff.Click += (_, _) => TakeOff();
        layoutPanel = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { inTower, splitZones, takeOff } });
        layoutPanel.Visibility = Visibility.Collapsed;
        panelSlot.Child = emptyPanel;
        panelHost.Child = new StackPanel { Spacing = 12, Children = { panelSlot, layoutPanel } };
        panelHost.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(deviceList, "Devices");
        deviceList.SelectionChanged += (_, _) => OnListSelected();
        notPlaced.Children.Add(Heading("Not placed"));
        notPlaced.Children.Add(notPlacedRows);
        listCard = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { Heading("Devices"), deviceList, notPlaced } });
        deskArea.ColumnDefinitions.Add(new ColumnDefinition());
        deskArea.ColumnDefinitions.Add(new ColumnDefinition());
        deskArea.Children.Add(leftColumn);
        deskArea.Children.Add(panelHost);
        leftColumn.Children.Add(listCard);
        ArrangeDesk(0);
        everyday = new EverydayCard(Send);
        // The Everyday card's columns follow the same panel width as the desk area.
        onPanel.SizeChanged += (_, args) =>
        {
            ArrangeDesk(args.NewSize.Width);
            everyday.ArrangeFor(args.NewSize.Width);
        };
        onPanel.Children.Add(deskArea);
        onPanel.Children.Add(everyday.Root);
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
        // The picture's view and selection are view state: the page shows the desk with nothing selected again.
        ResetPictureView();
        Hook();
        session.Changed += OnSessionChanged;
        Render();
    }

    /// <summary>Stops following, so a hidden page does no work. A change still waiting for its interval goes now rather than being lost.</summary>
    private void Detach()
    {
        showing = false;
        session.Changed -= OnSessionChanged;
        Unhook();
        dragging = false;
        brightnessSends.Flush();
        allSends.Flush();
        foreach (var pending in zoneSends.Values) pending.Flush();
        effectBar.Leave();
        // So do the cards' tuning sliders, which may never get their release either.
        foreach (var card in cards.Values) card.Leave();
        // The large keyboard view closes with the page, as Done closes it, its waiting change sent.
        LeaveKeys(sendWaiting: true);
        // Edit layout ends with the page, and the desk forgets edits on their way: it shows the host's layout when it is back.
        DropLayoutDraft();
        SetEditing(false);
    }

    /// <summary>The session's change, on the UI thread: once a second from the monitoring tick, and when lighting is turned on or off.</summary>
    private void OnSessionChanged()
    {
        if (!queue.HasThreadAccess) { queue.TryEnqueue(OnSessionChanged); return; }
        var current = session.Lighting;
        // The same client: only the wait for a running host's first device is looked at again, which no state need end.
        if (showing && ReferenceEquals(current, client) && noneSince is not null) ShowDeskArea();
        // Lighting 5: readings that stopped coming are shown as — once they are too old, with no state or event to say so.
        if (showing && ReferenceEquals(current, client) && hubFresh && !ReadingsFresh()) Render();
        if (!showing || ReferenceEquals(current, client)) return;
        Unhook();
        Reset(current);
        Hook();
        Render();
    }

    /// <summary>Follows the client's changes and preview frames, and asks it for frames.</summary>
    private void Hook()
    {
        if (client is not { } source) return;
        source.Changed += OnClientChanged;
        source.HubReadingsChanged += OnClientChanged;
        // The handler names its client, so a frame the old client raised as the page moved on is told apart from the new one's.
        previewHandler = colors => OnPreview(source, colors);
        source.Preview += previewHandler;
        source.SetPreview(true);
    }

    /// <summary>Stops following the client's changes and frames, and tells it no frames are wanted. A frame still waiting is dropped.</summary>
    private void Unhook()
    {
        Interlocked.Exchange(ref pendingFrame, null);
        if (client is not { } source) return;
        source.Changed -= OnClientChanged;
        source.HubReadingsChanged -= OnClientChanged;
        if (previewHandler is not null) source.Preview -= previewHandler;
        previewHandler = null;
        source.SetPreview(false);
    }

    /// <summary>
    /// A preview frame, raised on the client's read loop, where a slow handler would hold up every reply: it is only kept, replacing
    /// a frame that waits, and at most one show waits on the UI thread.
    /// </summary>
    private void OnPreview(DeviceHostClient source, PreviewColors colors)
    {
        Interlocked.Exchange(ref pendingFrame, new PreviewFrame(source, colors));
        if (Interlocked.Exchange(ref frameQueued, 1) == 1) return;
        if (!queue.TryEnqueue(ShowFrame)) Volatile.Write(ref frameQueued, 0);
    }

    /// <summary>
    /// Shows the waiting frame on the desk. Dropped while the page is hidden, when it came from a client the page no longer follows,
    /// while the latest state isn't Running, and while nothing animates (no desk effect or device mode running, see LightingPaint.Animating): the host can send one frame computed just before a switch to Static or lights
    /// off, after that state, and no later frame would replace it. Lighting 3b: a frame is also shown while some painted device has painted
    /// keys or layers with the lights on (LightingPaint.ShowsLights), since those colours are only in the host's frames, never in the state.
    /// </summary>
    private void ShowFrame()
    {
        // Cleared before taking the frame, so a frame raised while this runs queues the next show.
        Volatile.Write(ref frameQueued, 0);
        if (Interlocked.Exchange(ref pendingFrame, null) is not { } frame || !showing || client is null || !ReferenceEquals(frame.Source, client)) return;
        // Nor while the host isn't running, so a frame can't paint over the colours a non-running state cleared.
        if (client.State is not { Status: HostStatus.Running, Settings: not null } state || !ShowsFrames(Effective(state), state.Devices)) return;
        ShowDeskColors(frame.Colors);
    }

    /// <summary>
    /// Whether the desk draws the host's frames: something animates, or (lighting 3b) painted keys or layers show, whose colours only the
    /// frames carry.
    /// </summary>
    private static bool ShowsFrames(LightingSettings settings, IReadOnlyList<DeviceView>? devices) =>
        LightingPaint.Animating(settings, devices ?? []) || LightingPaint.ShowsLights(settings, devices ?? []);

    /// <summary>
    /// Lighting 4: the state's settings as the lights really are: with Lights on off while a reason (idle, locked, waking) holds them off
    /// (<see cref="EverydayTexts.Lit"/>), so the pictures and frames go dark although the saved switch stays on. Needs the state's settings.
    /// </summary>
    private static LightingSettings Effective(HostState state) => EverydayTexts.Lit(state) ? state.Settings : state.Settings.WithLightsOff();

    /// <summary>
    /// A preview frame to the drawing the mode shows, the plain desk or the picture; null (the state's colours) to both, so neither keeps
    /// a frame that is no longer the lights' colours.
    /// </summary>
    private void ShowDeskColors(PreviewColors? colors)
    {
        if (colors is null || shownPicture is null) desk.ShowColors(colors?.Zones);
        if (colors is null || shownPicture is not null) picture.ShowColors(colors);
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
        // The cards' tuning sliders drop their waiting changes, which would otherwise go to the new client.
        foreach (var card in cards.Values) card.Disable();
        cards.Clear();
        noneSince = null;
        statePicture = null;
        everyday.Reset();
        if (zoneFlyout.IsOpen) zoneFlyout.Hide();
        if (setAllFlyout.IsOpen) setAllFlyout.Hide();
        // The desk, list, selection and Place on desk sends belonged to the old client too.
        Interlocked.Exchange(ref pendingFrame, null);
        placing.Clear();
        DropLayoutDraft();
        // The new client counts its states from 0.
        shownSeq = 0;
        deskLayout = DeskLayouts.Empty();
        deskDevices = [];
        deskLightsOn = true;
        deskKeyHook = false;
        deskEffect = null;
        desk.Selected = null;
        ShowDeskColors(null);
        // The empty layout names no picture: plain mode until the new client's host publishes one, with the picture's view state reset.
        SetMode(null);
        ResetPictureView();
        scene = null;
        desk.Show(deskLayout, deskDevices, deskLightsOn, false);
        listSyncing = true;
        try { deviceList.Items.Clear(); }
        finally { listSyncing = false; }
        listRows.Clear();
        notPlacedRows.Children.Clear();
        placeRows.Clear();
        Show(notPlaced, false);
        ShowPanel();
        ShowDeskArea();
    }

    private bool ReadingsFresh() => session.HubReadings is { } hub && HubTexts.Fresh(hub.Readings, hub.Age);

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
        // The state and its count together, so the desk's draft is released only for a state shown after the one its last edit caused.
        var snapshot = client.Snapshot();
        var state = snapshot.State;
        shownState = state;
        shownSeq = snapshot.Seq;
        hubFresh = ReadingsFresh();
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
            // A frame shown before the host stopped running is no longer the lights' colours. On every state that isn't Running, not only
            // a null one: a refresh reads only the latest state, so a null state followed quickly by a Starting one may never be rendered.
            ShowDeskColors(null);
            // Layout edits on their way can't be known to land: the desk drops its draft and shows the host's layout once it runs.
            DropLayoutDraft();
        }
        ShowStatus();
        seenState |= connected;
        Show(headerTool, seenState);
        lightsOn.IsEnabled = brightness.IsEnabled = connected;
        setAll.IsEnabled = live;
        everyday.Update(state, connected);
        if (state is null)
        {
            // The host is connecting or relaunching: the cards and header controls stay as they were, disabled, until its state is back.
            foreach (var card in cards.Values) card.Disable();
            effectBar.Show(shownEffect ?? new EffectSettings(), false);
            // So do the desk, the list and the panel, in the state's colours (cleared above).
            ShowDesk(null);
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
            // While nothing animates (LightingPaint.Animating) and nothing painted shows (LightingPaint.ShowsLights) the desk shows the state's
            // colours, and frames are dropped until one of them holds again.
            if (state.Settings is null || !ShowsFrames(Effective(state), state.Devices)) ShowDeskColors(null);
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
        // The reason line is its own: it never counts as an alert, so the running text below still shows.
        string? why = shownState is { Settings.Everyday: { } rules } reasoned ? EverydayTexts.ReasonLine(reasoned.LightsOffReason, rules.IdleMinutes) : null;
        // Lighting 5: the hub's line shows only when the everyday reason has none, so one reason line shows at a time.
        why ??= HubTexts.ReasonLine(shownState?.Hub, session.PictureApplies);
        ShowLines(reason, why is null ? [] : [why]);
        // A running host with no problem to report: how many devices and what the lights are doing, above the administrator note.
        if (alerts.Count == 0 && shownState is { Status: HostStatus.Running, Settings: not null } runningState) notes.Insert(0, HubTexts.Running(runningState));
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
            // A card that goes drops its tuning sliders' waiting changes first: the device isn't there to take them.
            foreach (string gone in cards.Keys.Except(list.Select(device => device.Key)).ToArray())
            {
                cards[gone].Disable();
                cards.Remove(gone);
            }
        }
        else foreach (var card in cards.Values) card.Disable();
        // A device that arrived gets a card, which the panel shows while it is selected. The selected card stays in the panel while
        // states arrive, so a flyout opened from it stays open, and every listed device's card updates in place.
        foreach (var device in list) if (!cards.ContainsKey(device.Key)) cards[device.Key] = new DeviceCard(this);
        var names = Names(list);
        for (int i = 0; i < list.Length; i++) cards[list[i].Key].Update(list[i], names[i], live);
        ShowDesk(live ? (state, list) : null);
    }

    /// <summary>
    /// Brings the desk, the list and Not placed up to date with a running host's state and devices. With none (the host connecting,
    /// relaunching, starting or waiting) they stay as they were, disabled: nothing is removed and the selection stays. A selected
    /// device that left a running host's list is no longer selected.
    /// </summary>
    private void ShowDesk((HostState State, DeviceView[] Devices)? running)
    {
        if (running is { } shown)
        {
            if (shown.State.Settings is { } settings)
            {
                deskLayout = settings.Layout ?? deskLayout;
                // Lighting 4: the lights as they really are, so the pictures go dark while a reason holds them off.
                deskLightsOn = EverydayTexts.Lit(shown.State);
                deskEffect = settings.Effect;
            }
            deskKeyHook = shown.State.KeyHook;
            deskDevices = shown.Devices;
            if (desk.Selected is { } key && key != DeskView.Tower && !deskDevices.Any(device => device.Key == key)) desk.Selected = null;
            // A Place on desk whose reply came is over once a newer state than the one at its reply is shown.
            // Or once the layout places the device: the state it caused may already have been the client's when the reply was handled.
            foreach (var (device, send) in placing.ToArray())
                if (send.Replied && (!ReferenceEquals(send.At, shown.State) || !deskLayout.Unplaced.Contains(device))) placing.Remove(device);
            // The desk's draft goes at the first state shown with no SetLayout on its way, before the desk takes the layout.
            TryRelease();
        }
        // The layout decides the mode, so the plain desk is never shown a layout that names a picture. Until the host runs, the layout a
        // state carries names it too (the saved one), so the picture shows before the first running state, dimmed, its parts not yet found.
        // It is remembered, so the picture stays while the host reconnects before it has ever run.
        if (shownState?.Settings?.Layout is { } saved) statePicture = saved.Picture;
        SetMode(DeskPictures.Named(deskLayout.Picture) ?? DeskPictures.Named(statePicture));
        if (shownPicture is null) desk.Show(deskLayout, deskDevices, deskLightsOn, live);
        else ShowPicture();
        ShowLayout();
        ShowDeskArea();
    }

    /// <summary>
    /// The desk area, or a line in its place while there is nothing to draw: no picture named and no device found. The line is "Looking
    /// for lighting devices…" while the host connects, starts, waits or has run for less than <see cref="NoDevicesAfterMs"/> without
    /// listing a device (OpenRGB lists them a few at a time), then "No lighting devices found yet."; a host that is blocked, without
    /// OpenRGB or stopping isn't looking, and the status line says why, so neither line shows. The plain desk appears with the first
    /// device, the picture as soon as a layout names it.
    /// </summary>
    private void ShowDeskArea()
    {
        bool any = shownPicture is not null || deskDevices.Length > 0;
        bool none = !any && live;
        if (!none) noneSince = null;
        else noneSince ??= Environment.TickCount64;
        bool waited = noneSince is { } since && Environment.TickCount64 - since >= NoDevicesAfterMs;
        // No state and a client problem is a helper that failed for good (it can't start, or crashed too often): the alert says so, and nothing is looking.
        bool failed = shownState is null && client?.Problem is { Length: > 0 };
        bool searching = !any && !waited && !failed && shownState is null or { Status: HostStatus.Starting or HostStatus.Waiting or HostStatus.Running };
        Show(deskArea, any);
        Show(noDevices, none && waited);
        Show(looking, searching);
    }

    /// <summary>
    /// Plain mode (null) or picture mode. A change clears the selection, both drawings' frames and the picture's view state. Picture mode
    /// collapses the plain desk, Edit layout and its tools, the device list and Not placed, ends Edit layout and drops any layout draft
    /// through the usual drop path, because the host refuses layouts while a picture applies.
    /// </summary>
    private void SetMode(DeskPicture? next)
    {
        if (ReferenceEquals(next, shownPicture)) return;
        shownPicture = next;
        bool on = next is not null;
        desk.Selected = null;
        ResetPictureView();
        ShowDeskColors(null);
        if (on)
        {
            DropLayoutDraft();
            SetEditing(false);
        }
        Show(deskHeader, !on);
        Show(desk, !on);
        Show(listCard, !on);
        Show(pictureHeader, on);
        Show(picture, on);
        if (!on) Show(notInPicture, false);
        // The picture's columns are its own (ArrangeDesk).
        ArrangeDesk(deskWidth);
        SyncList();
        ShowPanel();
    }

    /// <summary>
    /// The picture's view back to the desk with nothing selected. The large keyboard view closes with its waiting change dropped: this runs
    /// for a new client, a change of mode and the page shown again (leaving the page already closed it, sending that change).
    /// </summary>
    private void ResetPictureView()
    {
        keyEditor.Close(sendWaiting: false);
        selectedDevice = null;
        picture.SelectedPart = null;
        picture.View = PictureView.Desk;
        picture.ClearStop();
        ShowPictureHeader();
    }

    /// <summary>
    /// The desk card's title and hint for the picture's view, with Back to desk in the inside view; in the keyboard view "Painting keys" or
    /// "Layers" by the editor's part, the keyboard's name, and the editor's switch and Done.
    /// </summary>
    private void ShowPictureHeader()
    {
        bool inside = picture.View == PictureView.Inside, keys = picture.View == PictureView.Keys;
        pictureTitle.Text = keys ? (keyEditor.Part == KeyboardEditor.EditorPart.Layers ? "Layers" : "Painting keys") : inside ? "Inside the PC" : "Desk";
        pictureHint.Text = keys ? keyEditor.Title : inside ? InsideHint : DeskHint;
        Show(backToDesk, inside);
        Show(keyEditor.Bar, keys);
    }

    /// <summary>
    /// Opens the large keyboard view for a keyboard of the running host's list, on one part: the picture's keyboard view through
    /// <see cref="ShowPictureView"/>, the editor's card in the panel. The keyboard stays the selected device. Only in picture mode, whose
    /// picture the view takes the place of; false when it can't open (plain mode, the host not running, or not a keyboard with keys).
    /// </summary>
    internal bool OpenKeyboard(string deviceKey, KeyboardEditor.EditorPart part)
    {
        if (shownPicture is null || !live || scene is null) return false;
        if (deskDevices.FirstOrDefault(d => d.Key == deviceKey) is not { Keyboard: true, Keys: not null } keyboard) return false;
        if (selectedDevice != deviceKey)
        {
            // Selected as a tap on its part selects it, so the card's heading and the selection follow the keyboard.
            selectedDevice = deviceKey;
            picture.SelectedPart = scene.Parts.FirstOrDefault(p => p.Found && p.DeviceKey == deviceKey)?.Part.Id;
        }
        keyEditor.Open(keyboard, KeyboardHeading(keyboard), part, shownSeq, live, deskLightsOn, deskKeyHook);
        ShowPictureView(PictureView.Keys);
        return true;
    }

    /// <summary>
    /// Closes the large keyboard view, if it is open, back to the desk view with the keyboard still selected: Done and leaving the page
    /// (its waiting change sent), the keyboard gone or another device selected (dropped).
    /// </summary>
    private void LeaveKeys(bool sendWaiting)
    {
        if (!keyEditor.IsOpen && picture.View != PictureView.Keys) return;
        keyEditor.Close(sendWaiting);
        if (picture.View == PictureView.Keys) picture.View = PictureView.Desk;
        ShowPictureHeader();
        SyncList();
        ShowPanel();
    }

    /// <summary>The editor card's heading: the picture's for the keyboard's part ("Keyboard · SteelSeries Apex Pro"), else the device's name as the page shows it.</summary>
    private string KeyboardHeading(DeviceView keyboard)
    {
        if (selectedDevice == keyboard.Key && MarkFor() is { } mark) return mark.Title;
        int at = Array.FindIndex(deskDevices, d => d.Key == keyboard.Key);
        return at >= 0 ? Names(deskDevices)[at] : keyboard.Name ?? "";
    }

    /// <summary>
    /// The open keyboard view from the devices the desk last showed: the editor follows its keyboard, and the view closes when a running
    /// host no longer lists it as a keyboard with keys. While the host isn't running the list is the last running one, and the view stays, disabled.
    /// </summary>
    private void UpdateKeys()
    {
        if (keyEditor.DeviceKey is not { } key) return;
        if (deskDevices.FirstOrDefault(d => d.Key == key) is { Keyboard: true, Keys: not null } keyboard)
        {
            keyEditor.Update(keyboard, KeyboardHeading(keyboard), shownSeq, live, deskLightsOn, deskKeyHook);
            if (picture.View == PictureView.Keys && pictureHint.Text != keyEditor.Title) pictureHint.Text = keyEditor.Title;
        }
        else LeaveKeys(sendWaiting: false);
    }

    /// <summary>
    /// The picture from the devices the desk last showed: their colours, the parts as resolved, the wave while it runs with lights on,
    /// and what isn't in the picture. A selected part whose device is gone or no longer found, or is now another device, is deselected.
    /// </summary>
    private void ShowPicture()
    {
        if (shownPicture is not { } shown) return;
        var next = DeskPictures.Resolve(shown, deskDevices);
        scene = next;
        if (picture.SelectedPart is { } id)
        {
            var part = next.Parts.FirstOrDefault(p => p.Part.Id == id);
            if (part is not { Found: true } || part.DeviceKey != selectedDevice)
            {
                picture.SelectedPart = null;
                selectedDevice = null;
            }
        }
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        // Lighting 4 (ruling P7): a mouse that isn't connected gives no colours, so it is drawn unlit, still found and selectable. Lighting 5:
        // so does a hub on its own (a part drawn as own effect, selectable all the same).
        foreach (var device in deskDevices.Where(device => !MouseTexts.Unlit(device) && device.Hub != HubRoutes.Own))
            foreach (var zone in device.Zones ?? [])
                if (zone?.Key is { } key && zone.Color is { } color) colors[key] = color;
        picture.SetStateColors(colors);
        picture.SetKeyMaps(deskDevices.Where(d => d.Keys is not null).ToDictionary(d => d.Key, d => d.Keys!, StringComparer.Ordinal));
        bool wave = deskLightsOn && deskEffect is { } effect && LightingEffects.KindOf(effect.Kind) == EffectKind.Wave;
        double angle = deskEffect is { } current ? LightingEffects.ParamsFor(current, EffectKind.Wave)?.AngleDeg ?? 0 : 0;
        picture.Show(next, deskLightsOn, live, wave, angle);
        UpdateKeys();
        if (next.NotInPicture.Count > 0) notInPictureList.Text = " " + string.Join(" · ", next.NotInPicture) + KeepColourText;
        Show(notInPicture, next.NotInPicture.Count > 0);
    }

    /// <summary>A tap on a found part selects it and its device's card shows in the panel; null (empty space) clears the selection.</summary>
    private void SelectPart(ResolvedPart? part)
    {
        if (shownPicture is null) return;
        bool found = part is { Found: true };
        // Selecting another device closes the large keyboard view, as Done does but dropping a waiting change. (The keyboard view itself
        // raises no part taps, so this guards any other way of selecting.)
        if (keyEditor.IsOpen && (!found || part!.DeviceKey != keyEditor.DeviceKey)) LeaveKeys(sendWaiting: false);
        picture.SelectedPart = found ? part!.Part.Id : null;
        selectedDevice = found ? part!.DeviceKey : null;
        SyncList();
        ShowPanel();
    }

    /// <summary>
    /// The desk or the inside view, with nothing selected (the large keyboard view, if open, closes first, its waiting change sent); or the
    /// keyboard view, which <see cref="OpenKeyboard"/> opens with its keyboard still selected.
    /// </summary>
    private void ShowPictureView(PictureView view)
    {
        if (shownPicture is null) return;
        if (view != PictureView.Keys)
        {
            keyEditor.Close(sendWaiting: true);
            selectedDevice = null;
            picture.SelectedPart = null;
        }
        picture.View = view;
        ShowPictureHeader();
        SyncList();
        ShowPanel();
    }

    /// <summary>
    /// How the selected part's device card shows in picture mode, as the mockups: "Keyboard · SteelSeries Apex Pro" over "selected on the
    /// desk" (or "selected inside the PC") for a device with one part in the picture; the device's name over "radiator fan 2 selected" for
    /// one with several. Either way the row of the zone the part shows is highlighted (the mouse's Middle, the card's GPU zone 1, radiator
    /// fan 2's own). A row whose zone is one part's alone is named as the picture names the part
    /// ("RAM 1", "Radiator fan 2", "Pump"); the zones of a part that covers several keep their own names, which tell them apart. The
    /// device's name is the host's, never numbered: the part's name is what tells two sticks apart, and the page's number, which follows
    /// the host's list order, need not agree with it. Null when no found part is selected.
    /// </summary>
    private PictureMark? MarkFor()
    {
        if (scene is not { } shown || picture.SelectedPart is not { } id || selectedDevice is not { } key) return null;
        if (shown.Parts.FirstOrDefault(p => p.Part.Id == id && p.DeviceKey == key) is not { } part) return null;
        string device = deskDevices.FirstOrDefault(d => d.Key == key)?.Name ?? part.Part.DeviceName;
        var own = shown.Parts.Where(p => p.DeviceKey == key).ToList();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in own) if (p.ZoneKeys.Count == 1) labels[p.ZoneKeys[0]] = p.Part.Label;
        string[] parts = [.. own.Select(p => p.Part.Label)];
        if (own.Count == 1) return new($"{part.Part.Label} · {device}", part.Part.View == PictureView.Desk ? "selected on the desk" : "selected inside the PC", part.ShownZoneKey, device, labels, parts);
        return new(device, $"{part.Part.Label.ToLowerInvariant()} selected", part.ShownZoneKey, device, labels, parts);
    }

    /// <summary>
    /// A device card's picture-mode look: its heading and sub-line, the zone whose row is highlighted, the device's name, its rows' labels by
    /// zone key, and its parts' labels in the picture's order, which the note under Colour correction names when there is more than one.
    /// </summary>
    private sealed record PictureMark(string Title, string Sub, string? Zone, string Device, IReadOnlyDictionary<string, string> Labels, IReadOnlyList<string> Parts);

    /// <summary>
    /// The list, Not placed, the panel and the edit tools from the layout the desk edits from: its draft while one is held, so an edit
    /// shows in them at once. Only a running host's device list is complete, so rows go only while it runs.
    /// </summary>
    private void ShowLayout()
    {
        var names = Names(deskDevices);
        UpdateList(names, live);
        UpdateNotPlaced(names, live);
        deviceList.IsEnabled = live;
        SyncList();
        ShowPanel();
        ShowEditTools();
    }

    /// <summary>Edit layout on or off: the toggle, the tools and the panel's layout controls follow. Leaving it ends a drag in progress.</summary>
    private void SetEditing(bool on)
    {
        // Never in picture mode, where the host refuses layouts.
        on &= shownPicture is null;
        desk.Editing = on;
        if (editToggle.IsChecked != on) editToggle.IsChecked = on;
        editToggle.Content = on ? "Done" : "Edit layout";
        ShowEditTools();
        ShowPanel();
    }

    /// <summary>The edit tools, shown only in edit layout and usable only while the host runs; the desk shape shown from the layout without sending it.</summary>
    private void ShowEditTools()
    {
        editToggle.IsEnabled = live;
        Show(editTools, desk.Editing);
        shapeBox.IsEnabled = resetPositions.IsEnabled = live;
        int index = DeskLayouts.ShapeOf(desk.Layout) switch { DeskShape.Straight => 0, DeskShape.LLeft => 1, DeskShape.LRight => 2, _ => -1 };
        if (shapeBox.SelectedIndex == index) return;
        layoutSyncing = true;
        try { shapeBox.SelectedIndex = index; }
        finally { layoutSyncing = false; }
    }

    /// <summary>
    /// Holds an edited layout on the desk and sends it. A refusal or failed send drops the draft at once; the Ok reply that leaves none
    /// on its way makes <see cref="layoutGate"/> wait for a state counted above it. The error shows through the send-error line, from SendTracked.
    /// </summary>
    private void SendLayout(DeskLayout next, Action<Reply>? done = null)
    {
        if (!live || shownPicture is not null)
        {
            DropLayoutDraft();
            ShowLayout();
            return;
        }
        // Every save carries the whole draft, so it carries any nudge still waiting for one too.
        nudgeDirty = false;
        desk.Hold(next);
        int ticket = layoutGate.Sent();
        ShowLayout();
        SendTracked(new SetLayoutRequest(next), tracked =>
        {
            switch (layoutGate.Replied(ticket, tracked.Reply.Ok, tracked.StatesBefore))
            {
                case LayoutReplied.Refused:
                    DropLayoutDraft();
                    ShowLayout();
                    break;
                case LayoutReplied.Last:
                    // The state it caused may be in already: shown, or stored and not yet shown, whose refresh would come a little later.
                    if (TryRelease()) ShowLayout();
                    else if (!nudgeDirty && client is { } current && current.Snapshot().Seq > tracked.StatesBefore) Render();
                    break;
            }
            done?.Invoke(tracked.Reply);
        });
    }

    /// <summary>
    /// Drops the desk's draft once no SetLayout is on its way, no nudge waits for a save, and the shown state was stored after the last
    /// reply was read: a later state than the one that reply's request caused. True when it dropped one.
    /// </summary>
    private bool TryRelease()
    {
        if (desk.Held is null || !layoutGate.ShouldRelease(shownSeq, nudgeDirty)) return false;
        desk.Release();
        return true;
    }

    /// <summary>Gives up the layout edits on their way: the desk's draft and any drag go, and their late replies count for nothing.</summary>
    private void DropLayoutDraft()
    {
        layoutGate.Drop();
        nudgeTicket++;
        nudgeInFlight = nudgeDirty = false;
        nudgeTimer.Stop();
        desk.DropDraft();
    }

    /// <summary>
    /// An arrow key moved the draft (already held and drawn). Saved at once unless a nudge's save is on its way or the last nudge save
    /// went less than <see cref="NudgeGapMs"/> ago; then the move waits, and the latest draft goes when that save's reply is Ok and the
    /// gap is up, so a held key saves at most once per round trip and at most five times a second, and its last position always goes.
    /// </summary>
    private void OnNudged(DeskLayout next)
    {
        if (!live || shownPicture is not null)
        {
            DropLayoutDraft();
            ShowLayout();
            return;
        }
        if (nudgeInFlight || NudgeWait() > 0)
        {
            nudgeDirty = true;
            if (!nudgeInFlight) ArmNudge();
            ShowLayout();
            return;
        }
        SendNudge(next);
    }

    private void SendNudge(DeskLayout next)
    {
        int ticket = ++nudgeTicket;
        nudgeInFlight = true;
        lastNudgeSentAt = Environment.TickCount64;
        SendLayout(next, reply =>
        {
            // A nudge retired by a dropped draft, or a client swap, leaves the newer one's state alone.
            if (ticket != nudgeTicket) return;
            nudgeInFlight = false;
            if (reply.Ok && nudgeDirty) SendWaitingNudge();
        });
    }

    /// <summary>The waiting nudge's draft goes now if the gap since the last nudge save is up, else when it is; not while one is on its way.</summary>
    private void SendWaitingNudge()
    {
        if (nudgeInFlight || !nudgeDirty || desk.Held is not { } draft) return;
        if (NudgeWait() > 0) ArmNudge();
        else SendNudge(draft);
    }

    /// <summary>Milliseconds until a nudge save may go, 0 when it may go now.</summary>
    private long NudgeWait() => lastNudgeSentAt is { } at ? Math.Max(0, NudgeGapMs - (Environment.TickCount64 - at)) : 0;

    private void ArmNudge()
    {
        if (nudgeTimer.IsRunning) return;
        nudgeTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, NudgeWait()));
        nudgeTimer.Start();
    }

    /// <summary>A new desk shape from the tools, by the user only: one set from a state is not sent.</summary>
    private void ChangeShape()
    {
        if (layoutSyncing || !live || !desk.Editing) return;
        DeskShape? shape = shapeBox.SelectedIndex switch { 0 => DeskShape.Straight, 1 => DeskShape.LLeft, 2 => DeskShape.LRight, _ => null };
        if (shape is not { } chosen || DeskLayouts.ShapeOf(desk.Layout) == chosen) return;
        SendLayout(DeskGeometry.WithShape(desk.Layout, chosen));
    }

    /// <summary>Asks before arranging every device from scratch, keeping the desk shape.</summary>
    private async void ResetPositions()
    {
        if (resetAsking || !live || !desk.Editing || XamlRoot is null) return;
        resetAsking = true;
        int sent = generation;
        ContentDialogResult result;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Reset positions?",
                Content = new TextBlock { Text = "Arrange every device from scratch? The desk shape is kept.", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Reset", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            };
            result = await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            App.Note($"Reset positions dialog failed: {error.Message}");
            return;
        }
        finally { resetAsking = false; }
        // Not if the page left, the client changed, the host stopped running or edit layout ended meanwhile.
        if (result != ContentDialogResult.Primary || !showing || sent != generation || !live || !desk.Editing) return;
        SendLayout(DeskGeometry.Reset(desk.Layout, deskDevices));
    }

    /// <summary>The selected device inside the tower or out of it; the choice sticks, as a drag in or out does.</summary>
    private void SetInTower(bool inside)
    {
        if (!live || !desk.Editing || desk.Selected is not { } key || !desk.CanEdit(key)) return;
        if (desk.Layout.Tower.Members.Contains(key) == inside) return;
        SendLayout(DeskGeometry.SetInTower(desk.Layout, key, inside));
    }

    /// <summary>Takes the selected device off the desk, into Not placed, and clears the selection.</summary>
    private void TakeOff()
    {
        if (!live || !desk.Editing || desk.Selected is not { } key || !desk.CanEdit(key)) return;
        SendLayout(DeskGeometry.SetUnplaced(desk.Layout, deskDevices, key, true));
        desk.Selected = null;
        SyncList();
        ShowPanel();
    }

    /// <summary>The list's rows, one per device in the host's order, each with where the device is as its second line.</summary>
    private void UpdateList(string[] names, bool complete)
    {
        listSyncing = true;
        try
        {
            if (complete)
                foreach (string gone in listRows.Keys.Except(deskDevices.Select(device => device.Key)).ToArray())
                {
                    deviceList.Items.Remove(listRows[gone].Item);
                    listRows.Remove(gone);
                }
            for (int i = 0; i < deskDevices.Length; i++)
            {
                if (!listRows.TryGetValue(deskDevices[i].Key, out var row))
                {
                    row = listRows[deskDevices[i].Key] = new ListRow(deskDevices[i].Key);
                    deviceList.Items.Insert(Math.Min(i, deviceList.Items.Count), row.Item);
                }
                row.Update(names[i], DeskGeometry.Where(desk.Layout, deskDevices[i]));
            }
        }
        finally { listSyncing = false; }
    }

    /// <summary>The devices the layout doesn't place, each with Place on desk, disabled while the host isn't running or its own send is on its way.</summary>
    private void UpdateNotPlaced(string[] names, bool complete)
    {
        var unplaced = DeskGeometry.Scene(desk.Layout, deskDevices, false, new HashSet<string>()).NotPlaced.Select(device => device.Key).ToHashSet(StringComparer.Ordinal);
        if (complete)
            foreach (string gone in placeRows.Keys.Where(key => !unplaced.Contains(key)).ToArray())
            {
                notPlacedRows.Children.Remove(placeRows[gone].Root);
                placeRows.Remove(gone);
            }
        for (int i = 0; i < deskDevices.Length; i++)
        {
            string key = deskDevices[i].Key;
            if (!unplaced.Contains(key)) continue;
            if (!placeRows.TryGetValue(key, out var row))
            {
                row = placeRows[key] = new PlaceRow(this, key);
                notPlacedRows.Children.Add(row.Root);
            }
            row.Update(names[i]);
        }
        RefreshPlaceButtons();
        Show(notPlaced, placeRows.Count > 0);
    }

    /// <summary>Puts a device the layout doesn't place back on the desk, where auto-arrange finds room for it.</summary>
    private void PlaceOnDesk(string key)
    {
        if (!live || shownPicture is not null || placing.ContainsKey(key) || !deskDevices.Any(device => device.Key == key)) return;
        placing[key] = (false, null);
        RefreshPlaceButtons();
        // From the desk's draft when one is held, and held and counted like every other layout edit.
        SendLayout(DeskGeometry.SetUnplaced(desk.Layout, deskDevices, key, false), reply =>
        {
            if (reply.Ok) placing[key] = (true, client?.State);
            else placing.Remove(key);
            RefreshPlaceButtons();
        });
    }

    private void RefreshPlaceButtons()
    {
        foreach (var (key, row) in placeRows) row.Place.IsEnabled = live && !placing.ContainsKey(key);
    }

    /// <summary>The list's selection, by the user: the desk takes it without raising its own, and the panel follows.</summary>
    private void OnListSelected()
    {
        if (listSyncing || shownPicture is not null) return;
        desk.Selected = (deviceList.SelectedItem as ListViewItem)?.Tag as string;
        ShowPanel();
    }

    /// <summary>Selects the desk's selected device in the list, or nothing for the tower or no selection, without echoing it back.</summary>
    private void SyncList()
    {
        object? item = Selected is { } key && listRows.TryGetValue(key, out var row) ? row.Item : null;
        if (ReferenceEquals(deviceList.SelectedItem, item)) return;
        listSyncing = true;
        try { deviceList.SelectedItem = item; }
        finally { listSyncing = false; }
    }

    /// <summary>
    /// The panel: the selected device's card, or, with nothing selected, a line saying how to select one (under the tower's caption
    /// while the tower is selected, under "Nothing selected" in picture mode). A zone flyout opened from the card that leaves closes.
    /// </summary>
    private void ShowPanel()
    {
        DeviceCard? card = Selected is { } key && key != DeskView.Tower && cards.TryGetValue(key, out var found) ? found : null;
        // The large keyboard view's card takes the panel while the view is open.
        UIElement next = keyEditor.IsOpen ? keyEditor.Root : card?.Root ?? emptyPanel;
        bool tower = shownPicture is null && desk.Selected == DeskView.Tower;
        bool pictureOn = shownPicture is not null;
        Show(emptyHeading, tower || pictureOn);
        if (tower) emptyHeading.Text = DeskView.TowerCaption(DeskGeometry.Scene(desk.Layout, deskDevices, false, new HashSet<string>()).TowerDevices);
        else if (pictureOn) emptyHeading.Text = NothingSelectedHeading;
        emptyText.Text = !pictureOn ? NothingSelectedText : picture.View == PictureView.Inside ? TapPartText : TapDeviceText;
        // The picture's heading, row names and highlighted row on the shown card; a card leaving the panel, or shown in plain mode, goes back to its own.
        if (markedCard is not null && !ReferenceEquals(markedCard, card)) markedCard.Mark(null);
        markedCard = card;
        card?.Mark(pictureOn ? MarkFor() : null);
        card?.ShowViewable(pictureOn);
        ShowLayoutPanel();
        if (ReferenceEquals(panelSlot.Child, next)) return;
        if (zoneFlyout.IsOpen) zoneFlyout.Hide();
        panelSlot.Child = next;
    }

    /// <summary>
    /// The selected device's layout controls, in edit layout only, and not for the tower or a device the desk doesn't place (one it
    /// never draws, or one taken off it). They show the desk's draft, and work only while the host runs.
    /// </summary>
    private void ShowLayoutPanel()
    {
        bool shown = shownPicture is null && desk.Editing && Selected is { } key && key != DeskView.Tower && cards.ContainsKey(key) && desk.CanEdit(key);
        Show(layoutPanel, shown);
        if (!shown || Selected is not { } selected) return;
        inTower.IsEnabled = splitZones.IsEnabled = takeOff.IsEnabled = live;
        layoutSyncing = true;
        try
        {
            inTower.IsChecked = desk.Layout.Tower.Members.Contains(selected);
            splitZones.IsOn = desk.IsSplit(selected);
        }
        finally { layoutSyncing = false; }
    }

    /// <summary>The selected device: the plain desk's selection, or in picture mode the selected part's device.</summary>
    private string? Selected => shownPicture is null ? desk.Selected : selectedDevice;

    /// <summary>
    /// Beside the desk card on a wide page, below it on a narrow one: only the desk card changes parent, and only when the page goes
    /// from one to the other. The plain desk's columns are 2 to 1 with the desk's column stopping at the desk's own width; the picture's
    /// are 7 to 3, the picture growing with the page up to 1280 px, and it stacks only below <see cref="PictureWideLayout"/>.
    /// </summary>
    private void ArrangeDesk(double width)
    {
        deskWidth = width;
        bool pictureOn = shownPicture is not null;
        var next = (Wide: width >= (pictureOn ? PictureWideLayout : WideLayout), Picture: pictureOn);
        if (arranged is { } now && now == next) return;
        bool move = arranged?.Wide != next.Wide;
        arranged = next;
        var (left, right) = (deskArea.ColumnDefinitions[0], deskArea.ColumnDefinitions[1]);
        if (next.Wide)
        {
            // The plain desk is at most 720 px wide, so its column stops at that and its card's padding; the panel takes the rest.
            left.Width = new GridLength(pictureOn ? 7 : 2, GridUnitType.Star);
            left.MaxWidth = pictureOn ? PictureCardMax : 752;
            right.Width = new GridLength(pictureOn ? 3 : 1, GridUnitType.Star);
            right.MinWidth = pictureOn ? PicturePanelMin : 0;
            right.MaxWidth = pictureOn ? PicturePanelMax : double.PositiveInfinity;
            deskArea.ColumnSpacing = 12;
            if (!move) return;
            if (deskCard.Parent is Panel parent) parent.Children.Remove(deskCard);
            deskArea.RowDefinitions.Clear();
            leftColumn.Children.Insert(0, deskCard);
            Cell(leftColumn, 0, 0);
            Cell(panelHost, 0, 1);
        }
        else
        {
            left.Width = new GridLength(1, GridUnitType.Star);
            left.MaxWidth = double.PositiveInfinity;
            right.Width = new GridLength(0);
            right.MinWidth = 0;
            right.MaxWidth = double.PositiveInfinity;
            deskArea.ColumnSpacing = 0;
            if (!move) return;
            if (deskCard.Parent is Panel parent) parent.Children.Remove(deskCard);
            deskArea.RowDefinitions.Clear();
            for (int i = 0; i < 3; i++) deskArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            deskArea.Children.Add(deskCard);
            Cell(deskCard, 0, 0);
            Cell(panelHost, 1, 0);
            Cell(leftColumn, 2, 0);
        }
    }

    private static void Cell(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }

    /// <summary>The devices' names, with " (2)", " (3)" and so on after the second and later devices of one name, in list order.</summary>
    internal static string[] Names(DeviceView[] list)
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
        zoneHeading.Text = $"{row.Device} · {row.Label}";
        if (zonePicker is not null) AutomationProperties.SetName(zonePicker, $"Colour for {row.Label} on {row.Device}");
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
    private void Send(HostMessage request, Action<Reply>? done = null) => SendTracked(request, done is null ? null : tracked => done(tracked.Reply));

    /// <summary>
    /// <see cref="Send"/> with the reply's state count (<see cref="TrackedReply.StatesBefore"/>): the same generation check, callback and
    /// send-error line. A reply made up here, with no client or after a throw, carries the client's count as it is then.
    /// </summary>
    private async void SendTracked(HostMessage request, Action<TrackedReply>? done)
    {
        int sent = generation;
        TrackedReply tracked;
        if (client is not { } target) tracked = new TrackedReply(new Reply(false, null), 0);
        else
        {
            // SendTrackedAsync answers connection problems with a failed reply and throws only when its token is cancelled, which this one never is.
            try { tracked = await target.SendTrackedAsync(request, CancellationToken.None); }
            catch (Exception failure) { tracked = new TrackedReply(new Reply(false, failure.Message), target.Snapshot().Seq); }
        }
        if (sent != generation) return;
        var reply = tracked.Reply;
        done?.Invoke(tracked);
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
        private readonly TextBlock staysLit = Muted("Stays lit when lights are off"), cannotDrive = Muted("DeadWare can't drive this device"), noZones = Muted("This device has no colour zones.");
        // A keyboard's Saved looks above the zone rows; Key colours, Lighting mode, Layers, Brightness and Colour correction under them;
        // and the own-effect note that goes first.
        private readonly DeviceSections sections;
        // The picture's sub-line under the title ("selected on the desk", "radiator fan 2 selected"), collapsed in plain mode.
        private readonly TextBlock subtitle = Muted("");
        private readonly StackPanel zones = new() { Spacing = 4 };
        private readonly Dictionary<string, ZoneRow> rows = new(StringComparer.Ordinal);
        private string[] zoneKeys = [];
        // Picture mode's heading, row names and highlighted row; null in plain mode.
        private PictureMark? mark;
        // Lighting 4: the mouse's connection and battery when the device is the Aerox 9 mouse, whose card has its own heading and sub-line.
        private MouseView? mouse;
        // Lighting 5: how the device is lit when it is the Corsair hub (HubRoutes), else null.
        private string? hub;

        public DeviceCard(LightingPage page)
        {
            this.page = page;
            Show(subtitle, false);
            var top = new StackPanel { Spacing = 2, Children = { title, subtitle } };
            sections = new DeviceSections(page.queue, page.Send, page.OpenKeyboard);
            var content = new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { top, sections.Note, staysLit, cannotDrive, sections.Looks, noZones, zones } };
            foreach (var section in sections.Sections) content.Children.Add(section);
            Root = Pages.Card(content);
        }

        public Border Root { get; }

        /// <summary>The device's name as the page shows it, numbered when another device has the same one.</summary>
        public string Name { get; private set; } = "";

        /// <summary>
        /// The picture's heading and sub-line in place of the name, its names for the device and the rows, and the zone whose row is
        /// highlighted; null puts the card back as plain mode shows it. Kept across updates, and applied to rows rebuilt later.
        /// </summary>
        public void Mark(PictureMark? next)
        {
            mark = next;
            ShowTitle();
            sections.ShowParts(next?.Parts);
            foreach (var (key, row) in rows)
            {
                row.Rename(next?.Device ?? Name, next?.Labels.GetValueOrDefault(key));
                row.Highlight(next?.Zone is { } zone && key == zone);
            }
        }

        public ZoneRow? Find(string zoneKey) => rows.GetValueOrDefault(zoneKey);

        /// <summary>
        /// The heading and sub-line: the picture's while it marks the card, else the name and none. The mouse's are its own in either mode
        /// (lighting 4): <see cref="MouseTexts.Heading"/> over how it is connected and its battery (<see cref="MouseTexts.SubLine"/>).
        /// </summary>
        private void ShowTitle()
        {
            string heading = mouse is not null ? MouseTexts.Heading : mark?.Title ?? Name;
            // Lighting 5: the hub's card says how it is lit first, then the picture's own sub-line in picture mode.
            string? sub = mouse is { } shown ? MouseTexts.SubLine(shown)
                : hub is { } route ? HubTexts.CardSubLine(route, mark?.Sub)
                : mark?.Sub;
            if (title.Text != heading) title.Text = heading;
            if (subtitle.Text != (sub ?? "")) subtitle.Text = sub ?? "";
            Show(subtitle, sub is not null);
        }

        /// <param name="device">The device as the host lists it.</param>
        /// <param name="name">Its name as the page shows it, numbered when another device has the same one.</param>
        /// <param name="live">The host is running, so the swatches of a device DeadWare paints can be used.</param>
        public void Update(DeviceView device, string name, bool live)
        {
            Name = name;
            mouse = device.Mouse;
            hub = device.Hub;
            ShowTitle();
            bool hardware = device.Kind == PaintKind.Hardware, unsupported = device.Kind == PaintKind.Unsupported;
            // The device's own effect and whether it stays lit with lights off don't change with Lights on (Ruling AB), so the card
            // names the effect while the lights are off too, and warns before they go off that the device will stay lit. The note
            // naming the effect goes first, and the tuning sections show disabled (DeviceSections).
            sections.Update(device, page.shownState?.Settings?.Brightness ?? 100, mark?.Parts, live, page.shownPicture is not null,
                page.session.HubReadings, page.session.PictureApplies);
            Show(staysLit, hardware && device.StaysLitWhenOff);
            Show(cannotDrive, unsupported);
            // Only a device DeadWare paints lists its zones. One running its own effect keeps its zones' colours, but they don't show on it,
            // so it shows none (polish item 1: a Node Pro's swatches in the shared colour read as its fans' colour); Set all colours still
            // sets the shared colour. One DeadWare can't drive takes no colours. Neither keeps rows, so a zone flyout open on one closes.
            bool paints = device.Kind == PaintKind.Direct;
            var list = paints ? (device.Zones ?? []).Where(zone => zone is { Key: not null }).DistinctBy(zone => zone.Key, StringComparer.Ordinal).ToArray() : [];
            Show(zones, paints);
            Show(noZones, paints && list.Length == 0);
            // A mouse that isn't connected shows its rows dimmed; they stay usable, and its colours are saved for when it is back.
            zones.Opacity = mouse is { } dimmed && MouseTexts.Dimmed(dimmed) || hub == HubRoutes.Own ? 0.38 : 1;
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
            foreach (var zone in list)
            {
                rows[zone.Key].Update(mark?.Device ?? name, mark?.Labels.GetValueOrDefault(zone.Key), zone, live);
                rows[zone.Key].Highlight(mark?.Zone is { } marked && zone.Key == marked);
            }
        }

        /// <summary>Ends the tuning sliders' drags and sends their waiting changes now, as when the page is hidden.</summary>
        public void Leave() => sections.Leave();

        /// <summary>Whether the page is in picture mode, which Paint keys… and Edit layers… need, as the card is shown.</summary>
        public void ShowViewable(bool on) => sections.ShowViewable(on);

        /// <summary>Disables the swatches and the tuning sections while the host isn't running, leaving the card as it was.</summary>
        public void Disable()
        {
            foreach (var row in rows.Values) row.Swatch.IsEnabled = false;
            sections.Disable();
        }
    }

    /// <summary>A device's row in the list: its name, and where the layout has it as the second line. Its tag is the device key.</summary>
    private sealed class ListRow
    {
        private readonly TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock where = Muted("");
        private string? described;

        public ListRow(string key) => Item = new ListViewItem { Tag = key, Content = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4), Children = { name, where } } };

        public ListViewItem Item { get; }

        public void Update(string device, string place)
        {
            name.Text = device;
            where.Text = place;
            string description = $"{device}, {place}";
            if (description == described) return;
            described = description;
            AutomationProperties.SetName(Item, description);
        }
    }

    /// <summary>A device the layout doesn't place: its name and Place on desk.</summary>
    private sealed class PlaceRow
    {
        private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        public PlaceRow(LightingPage page, string key)
        {
            Place = new Button { Content = "Place on desk", VerticalAlignment = VerticalAlignment.Center };
            Place.Click += (_, _) => page.PlaceOnDesk(key);
            Root = new Grid { ColumnSpacing = 12 };
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Place, 1);
            Root.Children.Add(name);
            Root.Children.Add(Place);
        }

        public Grid Root { get; }
        public Button Place { get; }

        public void Update(string device)
        {
            if (name.Text == device) return;
            name.Text = device;
            AutomationProperties.SetName(Place, $"Place {device} on desk");
        }
    }

    /// <summary>A zone's row: its colour swatch, which opens the zone flyout, its name, and whether its colour is its own or the shared one.</summary>
    private sealed class ZoneRow
    {
        private readonly Border dot = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(9) };
        private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock source = Muted("");
        // label: the picture's name for the row's part, in place of the zone's name; null in plain mode.
        private string? color, described, label;
        private bool highlighted;
        // The selected part's row in picture mode: a subtle accent background, as the mockup's radiator fan 2.
        private static Brush? highlight;
        private static Brush HighlightBrush => highlight ??= new SolidColorBrush((Color)Application.Current.Resources["SystemAccentColor"]) { Opacity = 0.16 };

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
        /// <summary>The row's name: the picture's name for its part in picture mode, else the zone's.</summary>
        public string Label => label ?? Zone?.Name ?? "";
        /// <summary>The swatch can be used: the host runs, DeadWare paints the device and the zone has LEDs.</summary>
        public bool CanPaint => Swatch.IsEnabled;

        /// <summary>Highlights the row, or clears it; only picture mode highlights one.</summary>
        public void Highlight(bool on)
        {
            if (on == highlighted) return;
            highlighted = on;
            Root.Background = on ? HighlightBrush : null;
            Root.CornerRadius = on ? new CornerRadius(4) : default;
        }

        /// <summary>The device's and the row's names as the card now shows them: the picture's, or with a null label the zone's own.</summary>
        public void Rename(string device, string? label)
        {
            Device = device;
            this.label = label;
            ShowName();
        }

        private void ShowName()
        {
            if (Zone is null) return;
            if (name.Text != Label) name.Text = Label;
            string description = $"Colour for {Label} on {Device}";
            if (description == described) return;
            described = description;
            AutomationProperties.SetName(Swatch, description);
        }

        /// <param name="device">The device's name as its card shows it.</param>
        /// <param name="label">The picture's name for the row's part, or null for the zone's own name.</param>
        /// <param name="zone">The zone as the host lists it.</param>
        /// <param name="paints">The host runs and paints the device, so a zone with LEDs can take a colour.</param>
        public void Update(string device, string? label, ZoneView zone, bool paints)
        {
            Zone = zone;
            Rename(device, label);
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
        }
    }
}
