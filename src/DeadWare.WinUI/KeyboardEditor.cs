using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeadWare.WinUI;

/// <summary>
/// The large keyboard view's tools (lighting 3b, mockup key-painting.html, Way 1): the header's two-part switch Key colours | Layers and
/// Done (<see cref="Bar"/>), and the card beside the drawing (<see cref="Root"/>). The drawing is the page's <see cref="DeskPictureView"/>
/// in its keyboard view, whose key presses this follows. Key colours: pick keys by clicking and dragging or by group, then choose a colour
/// from the Colour section's picker or a swatch, and the picked keys are painted; Remove paint takes it off; Undo steps back through the
/// paint changes made since the view opened. Every change sends the whole painted set as a <see cref="SetDeviceKeysRequest"/> through the
/// page's send, whose refusals show in the page's status line; a refused change shows the state's paint again. Layers (mockup layers.html):
/// the list with its order buttons and the selected layer's settings, each change sending the whole list as a
/// <see cref="SetDeviceLayersRequest"/>; Pick keys… turns the drawing into the same picker for that layer. The saved looks control goes in
/// <see cref="looksSlot"/>.
/// </summary>
internal sealed class KeyboardEditor
{
    /// <summary>The two parts of the view, as the switch names them.</summary>
    public enum EditorPart { KeyColours, Layers }

    private const string HowText = "Click a key to pick it, click again to drop it. Drag across keys to pick several.";
    private const string ColourText = "Colour for the picked keys";
    private static readonly string[] Swatches = ["#FF3B3B", "#FF8A2B", "#FFD23B", "#22E04A", "#38E9FF", "#5B9DFF", "#B46BFF", "#FF5BD0", "#FFFFFF"];
    // The group buttons in the mockup's order, with the KeyShapes group each picks; None (null) drops every pick.
    private static readonly (string? Group, string Text)[] Groups =
    [
        ("all", "All"), (null, "None"), ("wasd", "WASD"), ("arrows", "Arrows"), ("numbers", "Number row"), ("fkeys", "F keys"), ("numpad", "Number pad"),
    ];
    // Layers: the four kinds with the line under them, the effects as the page's effect tiles name them, the note's paragraphs (spec,
    // "Layers", wording kept) and the sentence that takes the first one's place when key presses can't be watched.
    private static readonly (string Kind, string Text, string About)[] LayerKinds =
    [
        (DeviceLayers.Color, "A colour", "A fixed colour laid over the keys you choose."),
        (DeviceLayers.Effect, "An effect", "A moving effect over the keys you choose."),
        (DeviceLayers.Fade, "Key fade", "A key lights up when you press it, then fades out."),
        (DeviceLayers.Ripple, "Key ripple", "A ring of light spreads out from the key you press."),
    ];
    private static readonly (string Effect, string Text)[] LayerEffects = [("wave", "Wave"), ("breathing", "Breathing"), ("spectrum", "Spectrum")];
    private const string NoteBold = "Key fade and key ripple need DeadWare to notice key presses.";
    private const string NoteRest = " It learns only which light to flash. It never stores what you type and sends it nowhere outside DeadWare, and it stops noticing as soon as no layer needs it. While this page is open, the keyboard drawn here lights up with your presses too.";
    private const string NoteAnyKeyboard = "A press on any keyboard plugged into this PC lights this one: Windows doesn't say which keyboard a key came from.";
    private const string NoHookText = "DeadWare couldn't watch for key presses, so this layer won't react.";
    // How long it lasts: the slider's 0..29 is 0.1 s to 3.0 s.
    private const int DurationSteps = 29;

    private readonly DispatcherQueue queue;
    private readonly Action<HostMessage, Action<TrackedReply>?> send;
    private readonly DeskPictureView picture;
    private readonly ToggleButton keyColoursTab = new() { Content = "Key colours" }, layersTab = new() { Content = "Layers" };
    private readonly Button done = new() { Content = "Done", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = LightingPage.Heading(""), count = LightingPage.Muted(""), skipped = LightingPage.Muted("");
    private readonly Button undo = new() { Content = "Undo", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button[] groupButtons;
    private readonly Button colour = new() { Content = ColourText };
    private readonly Flyout colourFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly Button[] swatchButtons;
    private readonly Button removePicked = new() { Content = "From the picked keys" }, removeAll = new() { Content = "From every key" };
    private readonly StackPanel keyColoursPart = new() { Spacing = 10 };
    // The Layers part: the list (a row for each of up to 8 layers) and Add, then the selected layer's settings, then the note.
    private readonly StackPanel layersPart = new() { Spacing = 10 };
    private readonly LayerRow[] layerRows = new LayerRow[DeviceLayers.MaxLayers];
    private readonly Button addLayer = new() { Content = "+ Add a layer" };
    private readonly Border layerSettings;
    private readonly StackPanel knownSettings = new() { Spacing = 6 };
    private readonly TextBlock layerHeading = Label(""), kindAbout = LightingPage.Muted(""), layerSkipped = LightingPage.Muted("");
    private readonly ToggleButton[] kindButtons, effectButtons, layerSwatchButtons;
    private readonly ToggleButton cover = new() { Content = "Covers it" }, addLight = new() { Content = "Adds light to it" };
    private readonly ToggleButton allKeys = new() { Content = "All keys" }, someKeys = new() { Content = "Only the keys I pick" };
    private readonly Button pickLayerKeys = new() { Content = "Pick keys…" };
    private readonly Button[] layerGroupButtons;
    private readonly StackPanel colourRow, effectRow, durationRow;
    private readonly Flow pickRow, layerGroupRow;
    // The sliders' places; the sliders are made anew for each opening, so nothing a slider held from the last one carries over.
    private readonly Border speedHost = new(), durationHost = new(), strengthHost = new();
    private readonly TextBlock noteFirst = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, noteSecond = LightingPage.Muted(NoteAnyKeyboard);
    private readonly Border note;
    private bool? noteFailedShown;
    // Where the saved looks control goes, shown on the card in both parts as the mockup shows it on the normal card.
    private readonly Border looksSlot = new();
    private readonly KeyPick pick = new();
    private readonly PaintHistory history = new();
    // A layer's picks while Pick keys… is on for the selected layer; Key colours keeps its own picks.
    private readonly KeyPick layerPick = new();
    // Paint changes go at most once per 100 ms: the picker fires continuously while it is dragged, and its last colour goes when it closes.
    private readonly Throttle sends;
    // Layer lists go the same way: a drag picking a layer's keys sends at most once per 100 ms and its last list when it ends.
    private readonly Throttle layerSends;
    private LayerSlider? speed, duration, strength;
    private ColorPicker? picker;

    // The keyboard being edited, as the last state listed it; null while the view is closed. opening: bumped on every open and close, so a
    // reply to a change from an earlier opening changes nothing.
    private DeviceView? device;
    private string? deviceKey;
    private int opening;
    private bool live, syncing, lightsOn, keyHook;
    // The painted set this view last asked for, shown in place of the state's until the state carries it: from a change until no change waits
    // or is on its way and a state counted above the last reply's is shown (the host writes a reply before the state its request caused).
    private Dictionary<int, PaintedKey>? asked;
    private bool hasAsked;
    private int inFlight;
    private long shownSeq, settleAfter = -1;
    // The picker's opening: whether a pick has been made in it yet (the history takes one entry per opening), the painted set it paints over,
    // and its colour as last set or picked. lastColour: the colour last chosen, which the picker opens on when no picked key is painted.
    private bool pickedThisOpening;
    private Dictionary<int, PaintedKey>? pickerBase;
    private string? pickerShown, lastColour;
    private KeyMapView? groupsFor;
    // Layers: the list asked for, held as the paint is (shown until nothing waits or is on its way and a state counted above the last reply's
    // is shown); the selected layer by index (-1 none); whether its keys are being picked.
    private List<DeviceLayer>? askedLayers;
    private bool hasAskedLayers, layerPicking;
    private int layersInFlight, layerAt = -1;
    private long layersSettleAfter = -1;

    /// <param name="queue">The page's queue, for the paint throttle.</param>
    /// <param name="send">The page's tracked send: the reply comes back on the UI thread, not at all from a client the page no longer follows, and a refusal shows in the page's status line.</param>
    /// <param name="picture">The page's picture, whose keyboard view this drives and whose key presses it follows.</param>
    public KeyboardEditor(DispatcherQueue queue, Action<HostMessage, Action<TrackedReply>?> send, DeskPictureView picture)
    {
        this.queue = queue;
        this.send = send;
        this.picture = picture;
        sends = new Throttle(queue, SendNow);
        layerSends = new Throttle(queue, request => SendLayersNow(request, null));

        // The header's switch and Done.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        keyColoursTab.CornerRadius = new CornerRadius(4, 0, 0, 4);
        layersTab.CornerRadius = new CornerRadius(0, 4, 4, 0);
        tabs.Children.Add(keyColoursTab);
        tabs.Children.Add(layersTab);
        keyColoursTab.Click += (_, _) => SetPart(EditorPart.KeyColours);
        layersTab.Click += (_, _) => SetPart(EditorPart.Layers);
        done.Click += (_, _) => DoneRequested?.Invoke();
        Bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { tabs, done } };

        // The count line and Undo, then the skipped keys' note.
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(undo, 1);
        top.Children.Add(count);
        top.Children.Add(undo);
        undo.Click += (_, _) => Undo();
        LightingPage.Show(skipped, false);

        // Pick keys: the line, then the group buttons, each shown only when its group has keys on this keyboard.
        groupButtons = NewGroupButtons();
        var groupRow = new Flow();
        foreach (var button in groupButtons) groupRow.Children.Add(button);

        // The colour: the Colour section's picker in a flyout behind its button, and the swatches.
        colour.Flyout = colourFlyout;
        colourFlyout.Opening += (_, _) => OpenPicker();
        colourFlyout.Closed += (_, _) => { if (deviceKey is not null) sends.Flush(); };
        swatchButtons = [.. Swatches.Select(Swatch)];
        var swatchRow = new Flow();
        foreach (var button in swatchButtons) swatchRow.Children.Add(button);

        removePicked.Click += (_, _) => RemovePicked();
        removeAll.Click += (_, _) => RemoveAll();
        var removeRow = new Flow { Children = { removePicked, removeAll } };

        keyColoursPart.Children.Add(top);
        keyColoursPart.Children.Add(skipped);
        keyColoursPart.Children.Add(Section(Label("Pick keys"), LightingPage.Muted(HowText), groupRow));
        keyColoursPart.Children.Add(Section(colour, swatchRow));
        keyColoursPart.Children.Add(Section(Label("Remove paint"), removeRow));

        // Layers: the list, each row "kind · which keys" with ↑, ↓ and ✕, then Add and the line under it.
        var rows = new StackPanel { Spacing = 2 };
        for (int i = 0; i < layerRows.Length; i++)
        {
            var row = layerRows[i] = new LayerRow(i);
            int at = i;
            row.Select.Click += (_, _) => SelectLayer(at);
            row.Up.Click += (_, _) => MoveLayer(at, -1);
            row.Down.Click += (_, _) => MoveLayer(at, +1);
            row.Remove.Click += (_, _) => RemoveLayer(at);
            rows.Children.Add(row.Root);
        }
        addLayer.Click += (_, _) => AddLayer();
        layersPart.Children.Add(Section(Label("Layers"), rows, addLayer, LightingPage.Muted("Lower in the list is on top. Up to 8.")));

        // The selected layer's settings, in the mockup's order.
        kindButtons = [.. LayerKinds.Select(entry => Choice(entry.Text, () => ChangeLayer(l => l.Kind = entry.Kind)))];
        effectButtons = [.. LayerEffects.Select(entry => Choice(entry.Text, () => ChangeLayer(l => l.Effect = entry.Effect)))];
        layerSwatchButtons = [.. Swatches.Select(LayerSwatch)];
        cover.Click += (_, _) => ChangeLayer(l => l.Mix = DeviceLayers.Cover);
        addLight.Click += (_, _) => ChangeLayer(l => l.Mix = DeviceLayers.Add);
        allKeys.Click += (_, _) => AllKeys();
        someKeys.Click += (_, _) => SomeKeys();
        pickLayerKeys.Click += (_, _) => ToggleLayerPicking();
        layerGroupButtons = NewGroupButtons();
        colourRow = Rows(Label("Colour"), FlowOf(layerSwatchButtons));
        effectRow = Rows(Label("Effect"), FlowOf(effectButtons), Label("Speed"), speedHost);
        durationRow = Rows(Label("How long it lasts"), durationHost);
        pickRow = FlowOf(pickLayerKeys);
        layerGroupRow = FlowOf(layerGroupButtons);
        foreach (var element in new UIElement[]
        {
            Label("What it does"), FlowOf(kindButtons), kindAbout, colourRow, effectRow, durationRow, Label("Strength"), strengthHost,
            Label("How it mixes with what's under it"), FlowOf(cover, addLight), Label("Which keys"), FlowOf(allKeys, someKeys), pickRow, layerGroupRow, layerSkipped,
        })
            knownSettings.Children.Add(element);
        layerSettings = Section(layerHeading, knownSettings);
        layersPart.Children.Add(layerSettings);

        // The note, while any layer is a key fade or key ripple.
        note = new Border
        {
            Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = new StackPanel { Spacing = 4, Children = { noteFirst, noteSecond } },
        };
        layersPart.Children.Add(note);
        LightingPage.Show(layersPart, false);

        Root = Pages.Card(new StackPanel { Spacing = 10, Padding = new Thickness(8), Children = { title, keyColoursPart, layersPart, looksSlot } });

        picture.KeyPickDown += light => OnKey(p => p.Down(light));
        picture.KeyPickOver += light => OnKey(p => p.Over(light));
        picture.KeyPickUp += () =>
        {
            pick.Up();
            layerPick.Up();
            // A drag picking a layer's keys sends its last list as it ends.
            if (layerPicking) layerSends.Flush();
        };
        ShowTabs();
    }

    /// <summary>The header's two-part switch Key colours | Layers, and Done.</summary>
    public FrameworkElement Bar { get; }

    /// <summary>The card beside the drawing.</summary>
    public Border Root { get; }

    /// <summary>The part shown.</summary>
    public EditorPart Part { get; private set; }

    /// <summary>The view is open, for the keyboard <see cref="DeviceKey"/>.</summary>
    public bool IsOpen => deviceKey is not null;

    /// <summary>The keyboard being edited, by key; null while closed.</summary>
    public string? DeviceKey => deviceKey;

    /// <summary>The card's heading as last shown ("Keyboard · SteelSeries Apex Pro"), which the page's header repeats.</summary>
    public string Title => title.Text;

    /// <summary>Done was pressed: the page closes the view.</summary>
    public event Action? DoneRequested;

    /// <summary>The switch moved to the other part: the page's header title follows.</summary>
    public event Action? PartChanged;

    /// <summary>
    /// Opens the view for a keyboard on the given part, with nothing picked and no history: the picture draws the keyboard, and its presses
    /// pick keys in Key colours.
    /// </summary>
    /// <param name="heading">The card's heading: the keyboard as the page shows it.</param>
    /// <param name="seq">The page's shown state count.</param>
    /// <param name="live">The host is running.</param>
    /// <param name="lightsOn">The state's Lights on.</param>
    /// <param name="keyHook">The state's <see cref="HostState.KeyHook"/>: key presses are being watched.</param>
    public void Open(DeviceView keyboard, string heading, EditorPart part, long seq, bool live, bool lightsOn, bool keyHook)
    {
        Close(sendWaiting: true);
        opening++;
        deviceKey = keyboard.Key;
        picture.KeysDevice = deviceKey;
        picture.PickedKeys = pick.Picked;
        Part = part;
        layerAt = keyboard.Layers is { Count: > 0 } ? 0 : -1;
        speed = NewLayerSlider(speedHost, "Speed", 100, 10, (l, v) => l.Speed = v, null);
        duration = NewLayerSlider(durationHost, "How long it lasts", DurationSteps, 5, (l, v) => l.DurationMs = (v + 1) * 100, DurationText);
        // Its own 0..29 would show in the thumb's tooltip; the row's value shows "0.6 s".
        duration.Tuning.Slider.IsThumbToolTipEnabled = false;
        strength = NewLayerSlider(strengthHost, "Strength", 100, 10, (l, v) => l.Strength = v, v => $"{v}%");
        ShowTabs();
        Update(keyboard, heading, seq, live, lightsOn, keyHook);
    }

    /// <summary>
    /// Closes the view: a change waiting for its interval goes now with <paramref name="sendWaiting"/> (Done, leaving the page) or is dropped
    /// (the keyboard or the client gone), the picks, the history and the picker go, and late replies count for nothing. The page puts the
    /// picture back to the desk view.
    /// </summary>
    public void Close(bool sendWaiting)
    {
        if (sendWaiting) sends.Flush();
        else sends.Cancel();
        if (sendWaiting) layerSends.Flush();
        else layerSends.Cancel();
        foreach (var slider in Sliders)
        {
            if (sendWaiting) slider.Tuning.Leave();
            else slider.Tuning.Cancel();
            slider.Waiting = null;
        }
        opening++;
        deviceKey = null;
        device = null;
        asked = null;
        hasAsked = false;
        inFlight = 0;
        settleAfter = -1;
        pickerBase = null;
        groupsFor = null;
        pick.Clear();
        history.Clear();
        askedLayers = null;
        hasAskedLayers = false;
        layersInFlight = 0;
        layersSettleAfter = -1;
        layerPicking = false;
        layerPick.Clear();
        layerAt = -1;
        picture.PickingKeys = false;
        picture.PickedKeys = null;
        picture.KeysDevice = null;
        if (colourFlyout.IsOpen) colourFlyout.Hide();
    }

    /// <summary>
    /// The keyboard as the latest state lists it: the card follows, and the paint asked for is let go once that state carries it. A device
    /// other than the one open is ignored.
    /// </summary>
    /// <param name="heading">The card's heading: the keyboard as the page shows it.</param>
    /// <param name="seq">The page's shown state count.</param>
    /// <param name="live">The host is running, the only time changes go.</param>
    /// <param name="lightsOn">The state's Lights on.</param>
    /// <param name="keyHook">The state's <see cref="HostState.KeyHook"/>: key presses are being watched.</param>
    public void Update(DeviceView keyboard, string heading, long seq, bool live, bool lightsOn, bool keyHook)
    {
        if (deviceKey is null || keyboard.Key != deviceKey) return;
        device = keyboard;
        shownSeq = seq;
        this.live = live;
        this.lightsOn = lightsOn;
        this.keyHook = keyHook;
        if (!live)
        {
            sends.Cancel();
            layerSends.Cancel();
        }
        if (title.Text != heading) title.Text = heading;
        if (!ReferenceEquals(groupsFor, keyboard.Keys)) ShowGroups(keyboard.Keys);
        TrySettle();
        Refresh();
    }

    /// <summary>Forgets the paint history, as choosing a look does.</summary>
    public void ForgetHistory()
    {
        history.Clear();
        Refresh();
    }

    // ---- State ----

    /// <summary>The painted set as the view shows it: the one asked for until the state carries it, else the state's.</summary>
    private Dictionary<int, PaintedKey>? Current => hasAsked ? asked : device?.Paint;

    /// <summary>Changes can go: the host runs and the keyboard can be painted key by key and reports its keys.</summary>
    private bool CanSend => live && deviceKey is not null && device is { CanPaint: true, Keys: not null };

    private bool CanPaintPicked => CanSend && pick.Picked.Count > 0;

    /// <summary>Lets go of the paint asked for once nothing waits or is on its way and the state shown is later than the last reply.</summary>
    private void TrySettle()
    {
        if (hasAskedLayers && layersInFlight == 0 && !layerSends.Pending && Sliders.All(s => s.Waiting is null) && shownSeq > layersSettleAfter)
        {
            hasAskedLayers = false;
            askedLayers = null;
        }
        if (!hasAsked || inFlight > 0 || sends.Pending || shownSeq <= settleAfter) return;
        hasAsked = false;
        asked = null;
    }

    /// <summary>The count line, the skipped keys' note, what can be used, and the picks on the drawing.</summary>
    private void Refresh()
    {
        if (deviceKey is null) return;
        var current = Current;
        int painted = current?.Count ?? 0, picked = pick.Picked.Count;
        // Singular for exactly one, as "1 key painted · 0 picked"; 0 and 2 or more keep the plural.
        string line = painted == 1 ? $"1 key painted · {picked} picked" : $"{painted} keys painted · {picked} picked";
        if (count.Text != line) count.Text = line;
        int skip = device?.SkippedKeys ?? 0;
        if (skip > 0)
            skipped.Text = skip == 1
                ? "1 painted key doesn't match this keyboard's keys any more and is skipped."
                : $"{skip} painted keys don't match this keyboard's keys any more and are skipped.";
        LightingPage.Show(skipped, skip > 0);
        bool can = CanSend;
        undo.IsEnabled = can && history.CanUndo;
        foreach (var button in groupButtons) button.IsEnabled = can;
        colour.IsEnabled = can && picked > 0;
        foreach (var button in swatchButtons) button.IsEnabled = can && picked > 0;
        if (picker is not null) picker.IsEnabled = can && picked > 0;
        removePicked.IsEnabled = can && current is not null && pick.Picked.Any(current.ContainsKey);
        removeAll.IsEnabled = can && painted > 0;
        RefreshLayers(can);
        bool layerPicks = Part == EditorPart.Layers && layerPicking;
        picture.PickingKeys = Part == EditorPart.KeyColours || layerPicks;
        picture.PickedKeys = layerPicks ? layerPick.Picked : pick.Picked;
    }

    /// <summary>The group buttons for a key map: one whose group has no keys on this keyboard is hidden; None always shows.</summary>
    private void ShowGroups(KeyMapView? map)
    {
        groupsFor = map;
        for (int i = 0; i < Groups.Length; i++)
        {
            bool shown = Groups[i].Group is not { } group || map is not null && KeyShapes.Group(map, group).Count > 0;
            LightingPage.Show(groupButtons[i], shown);
            LightingPage.Show(layerGroupButtons[i], shown);
        }
        // A pick that is no longer one of the keyboard's lights goes.
        int lights = map?.Names.Count ?? 0;
        foreach (var picks in new[] { pick, layerPick })
            if (picks.Picked.Any(light => light < 0 || light >= lights)) picks.Set([.. picks.Picked.Where(light => light >= 0 && light < lights)]);
    }

    // ---- Parts ----

    private void SetPart(EditorPart next)
    {
        if (Part != next)
        {
            Part = next;
            if (colourFlyout.IsOpen) colourFlyout.Hide();
            // Switching to Key colours ends picking a layer's keys.
            EndLayerPicking();
            PartChanged?.Invoke();
        }
        ShowTabs();
        Refresh();
    }

    /// <summary>The switch shows the part shown (a click on the shown part's half leaves it checked), and that part's content.</summary>
    private void ShowTabs()
    {
        keyColoursTab.IsChecked = Part == EditorPart.KeyColours;
        layersTab.IsChecked = Part == EditorPart.Layers;
        LightingPage.Show(keyColoursPart, Part == EditorPart.KeyColours);
        LightingPage.Show(layersPart, Part == EditorPart.Layers);
    }

    // ---- Picking ----

    /// <summary>
    /// A press or drag on the drawing: in Key colours the pick changes and the card and drawing follow; in Layers, while the selected
    /// layer's keys are being picked, that layer's picks change and its new list goes through the throttle.
    /// </summary>
    private void OnKey(Action<KeyPick> change)
    {
        if (deviceKey is null) return;
        if (Part == EditorPart.KeyColours)
        {
            change(pick);
            Refresh();
        }
        else if (layerPicking)
        {
            var before = layerPick.Picked.ToHashSet();
            change(layerPick);
            if (!layerPick.Picked.SetEquals(before)) SendLayerPicks(now: false);
        }
    }

    /// <summary>A group button: Key colours' picks, or the selected layer's while they are being picked (its new list sent at once).</summary>
    private void PickGroup(string? group)
    {
        if (deviceKey is null || device?.Keys is not { } map) return;
        bool layer = Part == EditorPart.Layers && layerPicking;
        var picks = layer ? layerPick : pick;
        if (group is null) picks.Clear();
        else picks.Set(KeyShapes.Group(map, group));
        if (layer) SendLayerPicks(now: true);
        else Refresh();
    }

    /// <summary>The group buttons in the mockup's order, for Key colours or for picking a layer's keys; both act on the picks in use.</summary>
    private Button[] NewGroupButtons()
    {
        var buttons = Groups.Select(entry => new Button { Content = entry.Text }).ToArray();
        for (int i = 0; i < Groups.Length; i++)
        {
            string? group = Groups[i].Group;
            buttons[i].Click += (_, _) => PickGroup(group);
        }
        return buttons;
    }

    // ---- Painting ----

    /// <summary>A copy of <paramref name="from"/> with every picked key in <paramref name="color"/>, named as the keyboard names its light.</summary>
    private Dictionary<int, PaintedKey> WithPicked(Dictionary<int, PaintedKey>? from, string color)
    {
        var next = PaintHistory.Copy(from) ?? [];
        if (device?.Keys is not { } map) return next;
        foreach (int light in pick.Picked)
            if (light >= 0 && light < map.Names.Count) next[light] = new PaintedKey { Name = map.Names[light], Color = color };
        return next;
    }

    /// <summary>
    /// Asks for a whole new painted set (empty or null clears it), shown at once: through the throttle, or <paramref name="now"/> for a
    /// single change such as a swatch, Remove paint or Undo.
    /// </summary>
    private void Paint(Dictionary<int, PaintedKey>? next, bool now)
    {
        if (deviceKey is not { } key || !CanSend) return;
        var keys = next is { Count: > 0 } ? next : null;
        asked = keys;
        hasAsked = true;
        sends.Post(new SetDeviceKeysRequest(key, keys));
        if (now) sends.Flush();
        Refresh();
    }

    /// <summary>The throttle's send. A refusal drops what was asked for, so the card and drawing show the state's paint again.</summary>
    private void SendNow(HostMessage request)
    {
        int ticket = opening;
        inFlight++;
        send(request, tracked =>
        {
            if (ticket != opening) return;
            inFlight--;
            if (tracked.Reply.Ok) settleAfter = Math.Max(settleAfter, tracked.StatesBefore);
            else
            {
                // A colour still waiting would paint over the state the refusal leaves; the next pick sends again.
                sends.Cancel();
                hasAsked = false;
                asked = null;
            }
            TrySettle();
            Refresh();
        });
    }

    /// <summary>A swatch: the colour as the button's content, as the zone rows' swatches, so the button's hover look doesn't hide it.</summary>
    private Button Swatch(string hex)
    {
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(LightingPage.ToColor(hex)) };
        var button = new Button { Padding = new Thickness(3), MinWidth = 0, MinHeight = 0, Content = dot };
        AutomationProperties.SetName(button, hex);
        ToolTipService.SetToolTip(button, hex);
        button.Click += (_, _) =>
        {
            if (!CanPaintPicked) return;
            history.Push(Current);
            lastColour = hex;
            Paint(WithPicked(Current, hex), now: true);
        };
        return button;
    }

    /// <summary>
    /// Makes the picker the first time; each opening starts with no pick made, on the colour of the first painted picked key (or the
    /// colour last chosen, or white), set without painting.
    /// </summary>
    private void OpenPicker()
    {
        if (picker is null)
        {
            picker = LightingPage.NewPicker();
            AutomationProperties.SetName(picker, ColourText);
            picker.ColorChanged += OnPicked;
            colourFlyout.Content = picker;
        }
        pickedThisOpening = false;
        pickerBase = null;
        picker.IsEnabled = CanPaintPicked;
        var current = Current;
        string start = pick.Picked.Order().Select(light => current?.GetValueOrDefault(light)?.Color).FirstOrDefault(c => c is not null) ?? lastColour ?? "#FFFFFF";
        var value = LightingPage.ToColor(start);
        pickerShown = LightingPage.Hex(value);
        syncing = true;
        try { picker.Color = value; }
        finally { syncing = false; }
    }

    /// <summary>
    /// The user's pick of a new colour: the first in this opening puts the painted set as it was into the history; each paints the picked
    /// keys over that set, through the throttle, so only the latest colour waits and the last goes when the flyout closes.
    /// </summary>
    private void OnPicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        string color = LightingPage.Hex(args.NewColor);
        if (syncing || !sender.IsLoaded || color == pickerShown) return;
        pickerShown = color;
        if (!CanPaintPicked) return;
        if (!pickedThisOpening)
        {
            pickedThisOpening = true;
            pickerBase = PaintHistory.Copy(Current);
            history.Push(pickerBase);
        }
        lastColour = color;
        Paint(WithPicked(pickerBase, color), now: false);
    }

    private void RemovePicked()
    {
        if (!CanSend || Current is not { } current || !pick.Picked.Any(current.ContainsKey)) return;
        history.Push(current);
        var next = PaintHistory.Copy(current)!;
        foreach (int light in pick.Picked) next.Remove(light);
        Paint(next, now: true);
    }

    private void RemoveAll()
    {
        if (!CanSend || Current is not { Count: > 0 } current) return;
        history.Push(current);
        Paint(null, now: true);
    }

    /// <summary>Puts back the painted set from before the last change; a change still waiting is dropped, as the undone set replaces it.</summary>
    private void Undo()
    {
        if (!CanSend || !history.CanUndo) return;
        sends.Cancel();
        Paint(history.Pop(), now: true);
    }

    // ---- Layers ----

    /// <summary>The layer list as the view shows it: the one asked for until the state carries it, else the state's.</summary>
    private List<DeviceLayer>? CurrentLayers => hasAskedLayers ? askedLayers : device?.Layers;

    /// <summary>The selected layer's sliders, once the view has been opened.</summary>
    private IEnumerable<LayerSlider> Sliders => new[] { speed, duration, strength }.OfType<LayerSlider>();

    private static string DurationText(int value) => ((value + 1) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// A slider for one setting of the selected layer, in its place on the card: each value the user gives sends the whole list with that
    /// layer changed, through the slider's own throttle, and its replies count as the layer list's do.
    /// </summary>
    private LayerSlider NewLayerSlider(Border host, string label, int maximum, double largeChange, Action<DeviceLayer, int> set, Func<int, string>? format)
    {
        LayerSlider? made = null;
        var tuning = new TuningSlider(queue, (message, done) =>
        {
            if (made is not null && ReferenceEquals(message, made.Waiting)) made.Waiting = null;
            SendLayersNow(message, done);
        }, label, maximum, largeChange, value =>
        {
            // A list still waiting goes first, so this one, made over it, lands after it.
            layerSends.Flush();
            // The slider is shown and enabled only with a layer selected; this answers a value that could come any other way harmlessly.
            if (deviceKey is not { } key || CurrentLayers is not { } layers || layerAt < 0 || layerAt >= layers.Count) return new GetStateRequest();
            var request = AskLayers(key, LayerEdits.With(layers, layerAt, l => set(l, value)));
            made!.Waiting = request;
            return request;
        }, format);
        made = new LayerSlider(tuning);
        // The row's label sits above the slider, as the mockup draws it, so the slider's own label column (too narrow for "How long it
        // lasts") is folded away.
        tuning.Root.ColumnDefinitions[0].Width = new GridLength(0);
        tuning.Root.Children[0].Visibility = Visibility.Collapsed;
        host.Child = tuning.Root;
        return made;
    }

    /// <summary>A state's value for a slider, and whether it can be used; a disabled slider drops a value it had waiting.</summary>
    private static void ShowSlider(LayerSlider? slider, double value, bool enabled)
    {
        if (slider is null) return;
        if (!enabled) slider.Waiting = null;
        slider.Tuning.Show(value, enabled);
    }

    /// <summary>The list, the selected layer's settings and the note, from the list as the view shows it.</summary>
    private void RefreshLayers(bool can)
    {
        var layers = CurrentLayers;
        int n = layers?.Count ?? 0;
        if (layerAt >= n) layerAt = n - 1;
        if (layerAt < 0 && n > 0) layerAt = 0;
        var layer = layerAt >= 0 ? layers![layerAt] : null;
        bool known = layer is not null && DeviceLayers.Kinds.Contains(layer.Kind);
        // Picking ends when there is nothing left to pick for: no layer, one of a kind this version doesn't know, every key, or no changes can go.
        if (layerPicking && (!known || layer!.Lights is null || !can)) EndLayerPicking();

        for (int i = 0; i < layerRows.Length; i++)
        {
            var row = layerRows[i];
            LightingPage.Show(row.Root, i < n);
            if (i >= n) continue;
            var each = layers![i];
            string title = LayerEdits.Title(each);
            if (row.Title.Text != title)
            {
                row.Title.Text = title;
                AutomationProperties.SetName(row.Select, title);
            }
            row.Select.IsChecked = i == layerAt;
            // A layer of a kind this version doesn't know shows only its title and ✕.
            bool rowKnown = each is not null && DeviceLayers.Kinds.Contains(each.Kind);
            LightingPage.Show(row.Up, rowKnown);
            LightingPage.Show(row.Down, rowKnown);
            row.Up.IsEnabled = can && i > 0;
            row.Down.IsEnabled = can && i < n - 1;
            row.Remove.IsEnabled = can;
        }
        addLayer.IsEnabled = can && n < DeviceLayers.MaxLayers;
        ShowNote(layers);

        LightingPage.Show(layerSettings, layer is not null);
        LightingPage.Show(knownSettings, known);
        if (layer is null || !known)
        {
            if (layer is not null) layerHeading.Text = LayerEdits.Title(layer);
            foreach (var slider in Sliders) ShowSlider(slider, slider.Tuning.Value, false);
            return;
        }
        layerHeading.Text = $"Layer {layerAt + 1}";
        string kind = layer.Kind;
        bool effect = kind == DeviceLayers.Effect, reacts = kind is DeviceLayers.Fade or DeviceLayers.Ripple;
        for (int i = 0; i < kindButtons.Length; i++)
        {
            kindButtons[i].IsChecked = LayerKinds[i].Kind == kind;
            kindButtons[i].IsEnabled = can;
        }
        kindAbout.Text = LayerKinds.First(entry => entry.Kind == kind).About;
        // Colour: not for the effects Wave and Spectrum.
        LightingPage.Show(colourRow, !effect || layer.Effect == "breathing");
        for (int i = 0; i < layerSwatchButtons.Length; i++)
        {
            layerSwatchButtons[i].IsChecked = string.Equals(layer.Color, Swatches[i], StringComparison.OrdinalIgnoreCase);
            layerSwatchButtons[i].IsEnabled = can;
        }
        LightingPage.Show(effectRow, effect);
        for (int i = 0; i < effectButtons.Length; i++)
        {
            effectButtons[i].IsChecked = LayerEffects[i].Effect == layer.Effect;
            effectButtons[i].IsEnabled = can;
        }
        LightingPage.Show(durationRow, reacts);
        ShowSlider(speed, layer.Speed, can && effect);
        ShowSlider(duration, Math.Clamp(layer.DurationMs / 100 - 1, 0, DurationSteps), can && reacts);
        ShowSlider(strength, layer.Strength, can);
        cover.IsChecked = layer.Mix == DeviceLayers.Cover;
        addLight.IsChecked = layer.Mix == DeviceLayers.Add;
        allKeys.IsChecked = layer.Lights is null;
        someKeys.IsChecked = layer.Lights is not null;
        cover.IsEnabled = addLight.IsEnabled = allKeys.IsEnabled = someKeys.IsEnabled = pickLayerKeys.IsEnabled = can;
        LightingPage.Show(pickRow, layer.Lights is not null);
        string pickText = layerPicking ? "Finished picking" : "Pick keys…";
        if (!Equals(pickLayerKeys.Content, pickText)) pickLayerKeys.Content = pickText;
        LightingPage.Show(layerGroupRow, layerPicking);
        foreach (var button in layerGroupButtons) button.IsEnabled = can;
        int skip = device?.SkippedLayerLights is { } skips && layerAt < skips.Count ? skips[layerAt] : 0;
        if (skip > 0)
            layerSkipped.Text = skip == 1
                ? "1 of this layer's keys doesn't match this keyboard's keys any more and is skipped."
                : $"{skip} of this layer's keys don't match this keyboard's keys any more and are skipped.";
        LightingPage.Show(layerSkipped, skip > 0);
    }

    /// <summary>
    /// The note, while any layer is a key fade or key ripple. Its first paragraph gives way to <see cref="NoHookText"/> when the lights are
    /// on, the keyboard is painted, one of its reaction layers (as the state has them) could do something, and key presses aren't watched.
    /// </summary>
    private void ShowNote(List<DeviceLayer>? layers)
    {
        bool shown = LayerEdits.Reacts(layers);
        LightingPage.Show(note, shown);
        if (!shown) return;
        bool failed = lightsOn && device is { OwnEffect: null, Kind: PaintKind.Direct } shownDevice && CouldReact(shownDevice) && !keyHook;
        if (noteFailedShown == failed) return;
        noteFailedShown = failed;
        noteFirst.Inlines.Clear();
        if (failed) noteFirst.Inlines.Add(new Run { Text = NoHookText });
        else
        {
            noteFirst.Inlines.Add(new Run { Text = NoteBold, FontWeight = FontWeights.SemiBold });
            noteFirst.Inlines.Add(new Run { Text = NoteRest });
        }
    }

    /// <summary>A key fade or key ripple layer with some strength that covers at least one of the keyboard's lights.</summary>
    private static bool CouldReact(DeviceView keyboard)
    {
        if (keyboard.Layers is not { } layers) return false;
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            int skipped = keyboard.SkippedLayerLights is { } skips && i < skips.Count ? skips[i] : 0;
            if (layer is not null && layer.Kind is DeviceLayers.Fade or DeviceLayers.Ripple && layer.Strength > 0 && (layer.Lights is null || layer.Lights.Count > skipped)) return true;
        }
        return false;
    }

    private void SelectLayer(int i)
    {
        // Switching to another layer ends picking.
        if (i != layerAt)
        {
            EndLayerPicking();
            layerAt = i;
        }
        Refresh();
    }

    private void AddLayer()
    {
        EndLayerPicking();
        var layers = CurrentLayers;
        if (!CanSend || (layers?.Count ?? 0) >= DeviceLayers.MaxLayers)
        {
            Refresh();
            return;
        }
        var next = LayerEdits.Add(layers);
        layerAt = next.Count - 1;
        SetLayers(next, now: true);
    }

    /// <summary>✕: the layer goes; the selection stays with the layer it was on, or the one taking the removed one's place.</summary>
    private void RemoveLayer(int i)
    {
        EndLayerPicking();
        if (!CanSend || CurrentLayers is not { } layers || i >= layers.Count)
        {
            Refresh();
            return;
        }
        if (layerAt > i) layerAt--;
        SetLayers(LayerEdits.Remove(layers, i), now: true);
    }

    /// <summary>↑ or ↓: the layer swaps with its neighbour, and the selection goes with the layer it was on.</summary>
    private void MoveLayer(int i, int by)
    {
        EndLayerPicking();
        if (!CanSend || CurrentLayers is not { } layers || i + by < 0 || i + by >= layers.Count)
        {
            Refresh();
            return;
        }
        if (layerAt == i) layerAt = i + by;
        else if (layerAt == i + by) layerAt = i;
        SetLayers(LayerEdits.Move(layers, i, by), now: true);
    }

    /// <summary>A change to the selected layer, sent at once; one that changes nothing (a click on the choice already made) sends nothing.</summary>
    private void ChangeLayer(Action<DeviceLayer> change)
    {
        if (CurrentLayers is { } layers && layerAt >= 0 && layerAt < layers.Count)
        {
            var next = LayerEdits.With(layers, layerAt, change);
            if (!DeviceLayers.Same(new DeviceTuning { Layers = layers }, new DeviceLook { Layers = next }))
            {
                SetLayers(next, now: true);
                return;
            }
        }
        Refresh();
    }

    /// <summary>All keys: the layer's list goes (every key), and picking ends.</summary>
    private void AllKeys()
    {
        EndLayerPicking();
        ChangeLayer(l => l.Lights = null);
    }

    /// <summary>Only the keys I pick: a layer on every key starts with no keys; either way picking starts.</summary>
    private void SomeKeys()
    {
        if (!CanSend || CurrentLayers is not { } layers || layerAt < 0 || layerAt >= layers.Count)
        {
            Refresh();
            return;
        }
        if (layers[layerAt].Lights is null) ChangeLayer(l => l.Lights = []);
        StartLayerPicking();
    }

    private void ToggleLayerPicking()
    {
        if (layerPicking)
        {
            EndLayerPicking();
            Refresh();
        }
        else StartLayerPicking();
    }

    /// <summary>The drawing becomes the picker for the selected layer, its picks set from the layer's lights.</summary>
    private void StartLayerPicking()
    {
        if (!CanSend || CurrentLayers is not { } layers || layerAt < 0 || layerAt >= layers.Count || layers[layerAt].Lights is not { } lights)
        {
            Refresh();
            return;
        }
        int count = device?.Keys?.Names.Count ?? 0;
        layerPick.Set(lights.Where(light => light is not null && light.Index >= 0 && light.Index < count).Select(light => light.Index));
        layerPicking = true;
        Refresh();
    }

    /// <summary>Ends picking a layer's keys: a list still waiting from a drag goes now. The caller refreshes.</summary>
    private void EndLayerPicking()
    {
        if (!layerPicking) return;
        layerPicking = false;
        layerPick.Up();
        layerSends.Flush();
        layerPick.Clear();
    }

    /// <summary>The selected layer's new list from its picks, in light order, each light with the keyboard's name for it.</summary>
    private void SendLayerPicks(bool now)
    {
        if (device?.Keys is { } map && CurrentLayers is { } layers && layerAt >= 0 && layerAt < layers.Count)
        {
            var lights = layerPick.Picked.Where(light => light >= 0 && light < map.Names.Count).Order()
                .Select(light => new LayerLight { Index = light, Name = map.Names[light] }).ToList();
            SetLayers(LayerEdits.With(layers, layerAt, l => l.Lights = lights), now);
        }
        else Refresh();
    }

    /// <summary>
    /// Asks for a whole new layer list, shown at once: through the throttle, or <paramref name="now"/> for a single change. A slider's
    /// value still waiting goes first, or it would land after this list and put back the list it was made from.
    /// </summary>
    private void SetLayers(List<DeviceLayer> next, bool now)
    {
        if (deviceKey is { } key && CanSend)
        {
            foreach (var slider in Sliders) slider.Tuning.Leave();
            layerSends.Post(AskLayers(key, next));
            if (now) layerSends.Flush();
        }
        Refresh();
    }

    /// <summary>
    /// The request for a list, which the view shows from now on. A listed light at or beyond the keyboard's light count is left out: the
    /// host refuses a list holding one, and the light is skipped already.
    /// </summary>
    private SetDeviceLayersRequest AskLayers(string key, List<DeviceLayer> next)
    {
        int count = device?.Keys?.Names.Count ?? 0;
        foreach (var layer in next) layer?.Lights?.RemoveAll(light => light is null || light.Index >= count);
        askedLayers = next.Count > 0 ? next : null;
        hasAskedLayers = true;
        return new SetDeviceLayersRequest(key, askedLayers);
    }

    /// <summary>
    /// Sends a layer list, from the throttle or a slider (whose reply bookkeeping <paramref name="done"/> always gets). A refusal drops what
    /// was asked for, so the card shows the state's list again; a list waiting from a drag goes with it.
    /// </summary>
    private void SendLayersNow(HostMessage request, Action<Reply>? done)
    {
        int ticket = opening;
        layersInFlight++;
        send(request, tracked =>
        {
            done?.Invoke(tracked.Reply);
            if (ticket != opening) return;
            layersInFlight--;
            if (tracked.Reply.Ok) layersSettleAfter = Math.Max(layersSettleAfter, tracked.StatesBefore);
            else
            {
                layerSends.Cancel();
                hasAskedLayers = false;
                askedLayers = null;
            }
            TrySettle();
            Refresh();
        });
    }

    /// <summary>A colour swatch for the selected layer, checked while it is the layer's colour.</summary>
    private ToggleButton LayerSwatch(string hex)
    {
        var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(LightingPage.ToColor(hex)) };
        var button = new ToggleButton { Padding = new Thickness(3), MinWidth = 0, MinHeight = 0, Content = dot };
        AutomationProperties.SetName(button, hex);
        ToolTipService.SetToolTip(button, hex);
        button.Click += (_, _) => ChangeLayer(l => l.Color = hex);
        return button;
    }

    // ---- Layout ----

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };

    /// <summary>Rows one under another, as part of a section.</summary>
    private static StackPanel Rows(params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows) panel.Children.Add(row);
        return panel;
    }

    private static Flow FlowOf(params UIElement[] children)
    {
        var flow = new Flow();
        foreach (var child in children) flow.Children.Add(child);
        return flow;
    }

    /// <summary>One of a set of choices: checked while it is the layer's, which <see cref="RefreshLayers"/> puts right after every click.</summary>
    private static ToggleButton Choice(string text, Action chosen)
    {
        var button = new ToggleButton { Content = text };
        button.Click += (_, _) => chosen();
        return button;
    }

    /// <summary>One row of the layers list: its number and title (a click selects the layer), then ↑, ↓ and ✕.</summary>
    private sealed class LayerRow
    {
        public LayerRow(int index)
        {
            Number.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Select = new ToggleButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Number, Title } },
            };
            Root.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Up, 1);
            Grid.SetColumn(Down, 2);
            Grid.SetColumn(Remove, 3);
            Root.Children.Add(Select);
            Root.Children.Add(Up);
            Root.Children.Add(Down);
            Root.Children.Add(Remove);
        }

        public Grid Root { get; } = new() { ColumnSpacing = 3 };
        public ToggleButton Select { get; }
        public TextBlock Number { get; } = new() { Opacity = 0.7, MinWidth = 14, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Title { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        public Button Up { get; } = Small("↑");
        public Button Down { get; } = Small("↓");
        public Button Remove { get; } = Small("✕");

        private static Button Small(string text) => new() { Content = text, Padding = new Thickness(7, 1, 7, 1), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch };
    }

    /// <summary>A layer's slider, and the request it last posted while that waits for its interval (null once it has gone or been dropped).</summary>
    private sealed class LayerSlider(TuningSlider tuning)
    {
        public TuningSlider Tuning { get; } = tuning;
        public HostMessage? Waiting { get; set; }
    }

    /// <summary>A section: a line above it, then its rows, as the device card's sections (DeviceSections).</summary>
    private static Border Section(params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows) panel.Children.Add(row);
        return new Border
        {
            Child = panel, Padding = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }

    /// <summary>Its children left to right, wrapping onto a new row when the next doesn't fit; collapsed children take no room.</summary>
    private sealed class Flow : Panel
    {
        private const double Gap = 6;

        protected override Size MeasureOverride(Size availableSize)
        {
            double max = double.IsFinite(availableSize.Width) ? availableSize.Width : double.PositiveInfinity;
            double x = 0, y = 0, row = 0, width = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(max, double.PositiveInfinity));
                if (child.Visibility == Visibility.Collapsed) continue;
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > max) { y += row + Gap; x = 0; row = 0; }
                width = Math.Max(width, x + size.Width);
                x += size.Width + Gap;
                row = Math.Max(row, size.Height);
            }
            return new Size(width, y + row);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, row = 0;
            foreach (var child in Children)
            {
                if (child.Visibility == Visibility.Collapsed) { child.Arrange(new Rect(0, 0, 0, 0)); continue; }
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width) { y += row + Gap; x = 0; row = 0; }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width + Gap;
                row = Math.Max(row, size.Height);
            }
            return finalSize;
        }
    }
}
