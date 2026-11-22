using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeadWare.WinUI;

/// <summary>
/// The large keyboard view's tools (lighting 3b, mockup key-painting.html, Way 1): the header's two-part switch Key colours | Layers and
/// Done (<see cref="Bar"/>), and the card beside the drawing (<see cref="Root"/>). The drawing is the page's <see cref="DeskPictureView"/>
/// in its keyboard view, whose key presses this follows. Key colours: pick keys by clicking and dragging or by group, then choose a colour
/// from the Colour section's picker or a swatch, and the picked keys are painted; Remove paint takes it off; Undo steps back through the
/// paint changes made since the view opened. Every change sends the whole painted set as a <see cref="SetDeviceKeysRequest"/> through the
/// page's send (held as the view shows it by a <see cref="HeldRequest{T}"/>), whose refusals show in the page's status line; a refused
/// change shows the state's paint again. The Layers part is <see cref="KeyboardLayersPart"/>, to which this routes the drawing's presses and
/// the group buttons while it picks a layer's keys. The saved looks control goes in <see cref="looksSlot"/>.
/// </summary>
internal sealed class KeyboardEditor
{
    /// <summary>The two parts of the view, as the switch names them.</summary>
    public enum EditorPart { KeyColours, Layers }

    private const string HowText = "Click a key to pick it, click again to drop it. Drag across keys to pick several.";
    private const string ColourText = "Colour for the picked keys";
    /// <summary>The colour swatches, for painted keys and for a layer's colour.</summary>
    internal static readonly string[] Swatches = ["#FF3B3B", "#FF8A2B", "#FFD23B", "#22E04A", "#38E9FF", "#5B9DFF", "#B46BFF", "#FF5BD0", "#FFFFFF"];
    // The group buttons in the mockup's order, with the KeyShapes group each picks; None (null) drops every pick.
    private static readonly (string? Group, string Text)[] Groups =
    [
        ("all", "All"), (null, "None"), ("wasd", "WASD"), ("arrows", "Arrows"), ("numbers", "Number row"), ("fkeys", "F keys"), ("numpad", "Number pad"),
    ];

    private readonly DeskPictureView picture;
    private readonly ToggleButton keyColoursTab = new() { Content = "Key colours" }, layersTab = new() { Content = "Layers" };
    private readonly Button done = new() { Content = "Done", Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock title = LightingPage.Heading(""), count = LightingPage.Muted(""), skipped = LightingPage.Muted("");
    private readonly Button undo = new() { Content = "Undo", VerticalAlignment = VerticalAlignment.Center };
    // The group buttons twice: Key colours' own, and the set the Layers part shows while it picks a layer's keys.
    private readonly Button[] groupButtons, layerGroupButtons;
    private readonly Button colour = new() { Content = ColourText };
    private readonly Flyout colourFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly Button[] swatchButtons;
    private readonly Button removePicked = new() { Content = "From the picked keys" }, removeAll = new() { Content = "From every key" };
    private readonly StackPanel keyColoursPart = new() { Spacing = 10 };
    private readonly KeyboardLayersPart layers;
    // Where the saved looks control goes, shown on the card in both parts as the mockup shows it on the normal card.
    private readonly Border looksSlot = new();
    private readonly KeyPick pick = new();
    private readonly PaintHistory history = new();
    // The painted set: changes go at most once per 100 ms (the picker fires continuously while it is dragged, and its last colour goes when
    // it closes), and the set asked for shows until the state carries it.
    private readonly HeldRequest<Dictionary<int, PaintedKey>> paint;
    private ColorPicker? picker;

    // The keyboard being edited, as the last state listed it; null while the view is closed.
    private DeviceView? device;
    private string? deviceKey;
    private bool live, syncing, lightsOn, keyHook;
    // The picker's opening: whether a pick has been made in it yet (the history takes one entry per opening), the painted set it paints over,
    // and its colour as last set or picked. lastColour: the colour last chosen, which the picker opens on when no picked key is painted.
    private bool pickedThisOpening;
    private Dictionary<int, PaintedKey>? pickerBase;
    private string? pickerShown, lastColour;
    private KeyMapView? groupsFor;

    /// <param name="queue">The page's queue, for the throttles.</param>
    /// <param name="send">The page's tracked send: the reply comes back on the UI thread, not at all from a client the page no longer follows, and a refusal shows in the page's status line.</param>
    /// <param name="picture">The page's picture, whose keyboard view this drives and whose key presses it follows.</param>
    public KeyboardEditor(DispatcherQueue queue, Action<HostMessage, Action<TrackedReply>?> send, DeskPictureView picture)
    {
        this.picture = picture;
        paint = new HeldRequest<Dictionary<int, PaintedKey>>(queue, send, Refresh);

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
        layerGroupButtons = NewGroupButtons();

        // The colour: the Colour section's picker in a flyout behind its button, and the swatches.
        colour.Flyout = colourFlyout;
        colourFlyout.Opening += (_, _) => OpenPicker();
        colourFlyout.Closed += (_, _) => { if (deviceKey is not null) paint.Flush(); };
        swatchButtons = [.. Swatches.Select(Swatch)];

        removePicked.Click += (_, _) => RemovePicked();
        removeAll.Click += (_, _) => RemoveAll();

        keyColoursPart.Children.Add(top);
        keyColoursPart.Children.Add(skipped);
        keyColoursPart.Children.Add(Section(Label("Pick keys"), LightingPage.Muted(HowText), FlowOf(groupButtons)));
        keyColoursPart.Children.Add(Section(colour, FlowOf(swatchButtons)));
        keyColoursPart.Children.Add(Section(Label("Remove paint"), FlowOf(removePicked, removeAll)));

        layers = new KeyboardLayersPart(queue, send, FlowOf(layerGroupButtons), Refresh);
        LightingPage.Show(layers.Root, false);

        Root = Pages.Card(new StackPanel { Spacing = 10, Padding = new Thickness(8), Children = { title, keyColoursPart, layers.Root, looksSlot } });

        picture.KeyPickDown += light => OnKey(p => p.Down(light));
        picture.KeyPickOver += light => OnKey(p => p.Over(light));
        picture.KeyPickUp += () =>
        {
            pick.Up();
            // A drag picking a layer's keys sends its last list as it ends.
            layers.KeyUp();
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
        deviceKey = keyboard.Key;
        picture.KeysDevice = deviceKey;
        picture.PickedKeys = pick.Picked;
        Part = part;
        layers.Open(keyboard);
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
        paint.Reset(sendWaiting);
        layers.Close(sendWaiting);
        deviceKey = null;
        device = null;
        pickerBase = null;
        groupsFor = null;
        pick.Clear();
        history.Clear();
        picture.PickingKeys = false;
        picture.PickedKeys = null;
        picture.KeysDevice = null;
        if (colourFlyout.IsOpen) colourFlyout.Hide();
    }

    /// <summary>
    /// The keyboard as the latest state lists it: the card follows, and the paint and layers asked for are let go once that state carries
    /// them. A device other than the one open is ignored.
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
        this.live = live;
        this.lightsOn = lightsOn;
        this.keyHook = keyHook;
        if (!live)
        {
            paint.Cancel();
            layers.Cancel();
        }
        if (title.Text != heading) title.Text = heading;
        if (!ReferenceEquals(groupsFor, keyboard.Keys)) ShowGroups(keyboard.Keys);
        paint.Shown(seq);
        layers.Shown(seq);
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
    private Dictionary<int, PaintedKey>? Current => paint.Current(device?.Paint);

    /// <summary>Changes can go: the host runs and the keyboard can be painted key by key and reports its keys.</summary>
    private bool CanSend => live && deviceKey is not null && device is { CanPaint: true, Keys: not null };

    private bool CanPaintPicked => CanSend && pick.Picked.Count > 0;

    /// <summary>The count line, the skipped keys' note, what can be used, the Layers part, and the picks on the drawing.</summary>
    private void Refresh()
    {
        if (deviceKey is null || device is null) return;
        var current = Current;
        int painted = current?.Count ?? 0, picked = pick.Picked.Count;
        // Singular for exactly one, as "1 key painted · 0 picked"; 0 and 2 or more keep the plural.
        string line = painted == 1 ? $"1 key painted · {picked} picked" : $"{painted} keys painted · {picked} picked";
        if (count.Text != line) count.Text = line;
        int skip = device.SkippedKeys;
        if (skip > 0)
            skipped.Text = skip == 1
                ? "1 painted key doesn't match this keyboard's keys any more and is skipped."
                : $"{skip} painted keys don't match this keyboard's keys any more and are skipped.";
        LightingPage.Show(skipped, skip > 0);
        bool can = CanSend;
        undo.IsEnabled = can && history.CanUndo;
        foreach (var button in groupButtons) button.IsEnabled = can;
        foreach (var button in layerGroupButtons) button.IsEnabled = can;
        colour.IsEnabled = can && picked > 0;
        foreach (var button in swatchButtons) button.IsEnabled = can && picked > 0;
        if (picker is not null) picker.IsEnabled = can && picked > 0;
        removePicked.IsEnabled = can && current is not null && pick.Picked.Any(current.ContainsKey);
        removeAll.IsEnabled = can && painted > 0;
        layers.Show(device, can, lightsOn, keyHook);
        bool layerPicks = Part == EditorPart.Layers && layers.Picking;
        picture.PickingKeys = Part == EditorPart.KeyColours || layerPicks;
        picture.PickedKeys = layerPicks ? layers.Picked : pick.Picked;
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
        if (pick.Picked.Any(light => light < 0 || light >= lights)) pick.Set([.. pick.Picked.Where(light => light >= 0 && light < lights)]);
        layers.KeepPicks(lights);
    }

    // ---- Parts ----

    private void SetPart(EditorPart next)
    {
        if (Part != next)
        {
            Part = next;
            if (colourFlyout.IsOpen) colourFlyout.Hide();
            // Switching to Key colours ends picking a layer's keys.
            layers.EndPicking();
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
        LightingPage.Show(layers.Root, Part == EditorPart.Layers);
    }

    // ---- Picking ----

    /// <summary>
    /// A press or drag on the drawing: in Key colours the pick changes and the card and drawing follow; in Layers, while the selected
    /// layer's keys are being picked, the Layers part takes it.
    /// </summary>
    private void OnKey(Action<KeyPick> change)
    {
        if (deviceKey is null) return;
        if (Part == EditorPart.KeyColours)
        {
            change(pick);
            Refresh();
        }
        else layers.OnKey(change);
    }

    /// <summary>A group button: Key colours' picks, or the selected layer's while they are being picked (its new list sent at once).</summary>
    private void PickGroup(string? group)
    {
        if (deviceKey is null || device?.Keys is not { } map) return;
        if (Part == EditorPart.Layers && layers.Picking)
        {
            layers.PickGroup(map, group);
            return;
        }
        if (group is null) pick.Clear();
        else pick.Set(KeyShapes.Group(map, group));
        Refresh();
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
        paint.Post(keys, new SetDeviceKeysRequest(key, keys), now);
        Refresh();
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
        paint.Cancel();
        Paint(history.Pop(), now: true);
    }

    // ---- Layout ----

    /// <summary>A small bold label over a row.</summary>
    internal static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };

    /// <summary>A <see cref="Flow"/> of the given children.</summary>
    internal static Flow FlowOf(params UIElement[] children)
    {
        var flow = new Flow();
        foreach (var child in children) flow.Children.Add(child);
        return flow;
    }

    /// <summary>A section: a line above it, then its rows, as the device card's sections (DeviceSections).</summary>
    internal static Border Section(params UIElement[] rows)
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
    internal sealed class Flow : Panel
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
