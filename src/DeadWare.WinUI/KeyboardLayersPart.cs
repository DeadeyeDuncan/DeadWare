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
using static DeadWare.WinUI.KeyboardEditor;

namespace DeadWare.WinUI;

/// <summary>
/// The Layers part of the large keyboard view's card (lighting 3b, mockup layers.html): the list, numbered, each row "kind · which keys"
/// with ↑, ↓ and ✕, then + Add a layer; the selected layer's settings in the mockup's order; and the note while a layer reacts to key
/// presses. Every change sends the whole list as a <see cref="SetDeviceLayersRequest"/>, held as the view shows it until the state carries it
/// (<see cref="HeldRequest{T}"/>); the sliders are <see cref="TuningSlider"/>s. Pick keys… makes the drawing the picker for the selected
/// layer: <see cref="KeyboardEditor"/> routes the drawing's presses and its group buttons here while <see cref="Picking"/>. The part never
/// redraws itself: after a change it asks the editor, whose refresh calls <see cref="Show"/>.
/// </summary>
internal sealed class KeyboardLayersPart
{
    // The four kinds with the line under them, the effects as the page's effect tiles name them, the note's paragraphs (spec, "Layers",
    // wording kept) and the sentence that takes the first one's place when key presses can't be watched.
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
    private readonly Action changed;
    private readonly HeldRequest<List<DeviceLayer>> layers;
    private readonly LayerRow[] layerRows = new LayerRow[DeviceLayers.MaxLayers];
    private readonly Button addLayer = new() { Content = "+ Add a layer" };
    private readonly Border layerSettings;
    private readonly StackPanel knownSettings = new() { Spacing = 6 };
    private readonly TextBlock layerHeading = Label(""), kindAbout = LightingPage.Muted(""), layerSkipped = LightingPage.Muted("");
    private readonly ToggleButton[] kindButtons, effectButtons, layerSwatchButtons;
    private readonly ToggleButton cover = new() { Content = "Covers it" }, addLight = new() { Content = "Adds light to it" };
    private readonly ToggleButton allKeys = new() { Content = "All keys" }, someKeys = new() { Content = "Only the keys I pick" };
    private readonly Button pickLayerKeys = new() { Content = "Pick keys…" };
    private readonly StackPanel colourRow, effectRow, durationRow;
    private readonly Flow pickRow;
    private readonly UIElement groupRow;
    // The sliders' places; the sliders are made anew for each opening, so nothing a slider held from the last one carries over.
    private readonly Border speedHost = new(), durationHost = new(), strengthHost = new();
    private readonly TextBlock noteFirst = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, noteSecond = LightingPage.Muted(NoteAnyKeyboard);
    private readonly Border note;
    // The selected layer's picks while Pick keys… is on; Key colours keeps its own.
    private readonly KeyPick layerPick = new();
    private LayerSlider? speed, duration, strength;
    private bool? noteFailedShown;
    // The keyboard as the editor last showed it (null while closed), and whether changes can go then. layerAt: the selected layer by index
    // (-1 none). layerPicking: its keys are being picked.
    private DeviceView? device;
    private bool can;
    private int layerAt = -1;
    private bool layerPicking;

    /// <param name="queue">The page's queue, for the throttles.</param>
    /// <param name="send">The page's tracked send.</param>
    /// <param name="groupRow">The editor's group buttons for picking a layer's keys, shown here while picking.</param>
    /// <param name="changed">The editor's refresh, asked for after every change and reply; it calls <see cref="Show"/>.</param>
    public KeyboardLayersPart(DispatcherQueue queue, Action<HostMessage, Action<TrackedReply>?> send, UIElement groupRow, Action changed)
    {
        this.queue = queue;
        this.changed = changed;
        this.groupRow = groupRow;
        layers = new HeldRequest<List<DeviceLayer>>(queue, send, changed, () => Sliders.Any(s => s.Waiting is not null));

        // The list, then Add and the line under it.
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
        Root.Children.Add(Section(Label("Layers"), rows, addLayer, LightingPage.Muted("Lower in the list is on top. Up to 8.")));

        // The selected layer's settings, in the mockup's order.
        kindButtons = [.. LayerKinds.Select(entry => Choice(entry.Text, () => ChangeLayer(l => l.Kind = entry.Kind)))];
        effectButtons = [.. LayerEffects.Select(entry => Choice(entry.Text, () => ChangeLayer(l => l.Effect = entry.Effect)))];
        layerSwatchButtons = [.. Swatches.Select(LayerSwatch)];
        cover.Click += (_, _) => ChangeLayer(l => l.Mix = DeviceLayers.Cover);
        addLight.Click += (_, _) => ChangeLayer(l => l.Mix = DeviceLayers.Add);
        allKeys.Click += (_, _) => AllKeys();
        someKeys.Click += (_, _) => SomeKeys();
        pickLayerKeys.Click += (_, _) => ToggleLayerPicking();
        colourRow = Rows(Label("Colour"), FlowOf(layerSwatchButtons));
        effectRow = Rows(Label("Effect"), FlowOf(effectButtons), Label("Speed"), speedHost);
        durationRow = Rows(Label("How long it lasts"), durationHost);
        pickRow = FlowOf(pickLayerKeys);
        foreach (var element in new UIElement[]
        {
            Label("What it does"), FlowOf(kindButtons), kindAbout, colourRow, effectRow, durationRow, Label("Strength"), strengthHost,
            Label("How it mixes with what's under it"), FlowOf(cover, addLight), Label("Which keys"), FlowOf(allKeys, someKeys), pickRow, groupRow, layerSkipped,
        })
            knownSettings.Children.Add(element);
        layerSettings = Section(layerHeading, knownSettings);
        Root.Children.Add(layerSettings);

        // The note, while any layer is a key fade or key ripple.
        note = new Border
        {
            Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = new StackPanel { Spacing = 4, Children = { noteFirst, noteSecond } },
        };
        Root.Children.Add(note);
    }

    /// <summary>The part's content on the card.</summary>
    public StackPanel Root { get; } = new() { Spacing = 10 };

    /// <summary>The selected layer's keys are being picked: the drawing's presses and the group buttons act on <see cref="Picked"/>.</summary>
    public bool Picking => layerPicking;

    /// <summary>The selected layer's picks while <see cref="Picking"/>.</summary>
    public IReadOnlySet<int> Picked => layerPick.Picked;

    /// <summary>Opens on a keyboard: its first layer selected, new sliders.</summary>
    public void Open(DeviceView keyboard)
    {
        layerAt = keyboard.Layers is { Count: > 0 } ? 0 : -1;
        speed = NewLayerSlider(speedHost, "Speed", 100, 10, (l, v) => l.Speed = v, null);
        duration = NewLayerSlider(durationHost, "How long it lasts", DurationSteps, 5, (l, v) => l.DurationMs = (v + 1) * 100, DurationText);
        // Its own 0..29 would show in the thumb's tooltip; the row's value shows "0.6 s".
        duration.Tuning.Slider.IsThumbToolTipEnabled = false;
        strength = NewLayerSlider(strengthHost, "Strength", 100, 10, (l, v) => l.Strength = v, v => $"{v}%");
    }

    /// <summary>
    /// Closes: a list or slider value waiting goes now with <paramref name="sendWaiting"/> or is dropped, picking ends, and late replies
    /// count for nothing.
    /// </summary>
    public void Close(bool sendWaiting)
    {
        foreach (var slider in Sliders)
        {
            if (sendWaiting) slider.Tuning.Leave();
            else slider.Tuning.Cancel();
            slider.Waiting = null;
        }
        layers.Reset(sendWaiting);
        layerPicking = false;
        layerPick.Clear();
        layerAt = -1;
        device = null;
    }

    /// <summary>The page's shown state count, as a state is shown: the list asked for goes once that state carries it.</summary>
    public void Shown(long seq) => layers.Shown(seq);

    /// <summary>The host stopped running: a list waiting is dropped.</summary>
    public void Cancel() => layers.Cancel();

    /// <summary>A list asked for (by a change or a slider's value) isn't in the last state yet: it waits, is on its way or is not yet shown.</summary>
    public bool Held => layers.Held;

    /// <summary>
    /// Sends a slider value and a list change still waiting now, as a release and Done do (a slider's drag ends), without ending picking:
    /// a look saved or chosen next then takes, or replaces, the list as the view shows it. At most one of them waits (<see cref="SetLayers"/>
    /// sends the sliders' values first, and a slider's request sends the list first), so their order here doesn't matter.
    /// </summary>
    public void Flush()
    {
        foreach (var slider in Sliders) slider.Tuning.Leave();
        layers.Flush();
    }

    /// <summary>A pick that is no longer one of the keyboard's lights goes.</summary>
    public void KeepPicks(int lights)
    {
        if (layerPick.Picked.Any(light => light < 0 || light >= lights)) layerPick.Set([.. layerPick.Picked.Where(light => light >= 0 && light < lights)]);
    }

    /// <summary>A press or drag on the drawing while picking: the picks change and the layer's new list goes through the throttle.</summary>
    public void OnKey(Action<KeyPick> change)
    {
        if (!layerPicking) return;
        var before = layerPick.Picked.ToHashSet();
        change(layerPick);
        if (!layerPick.Picked.SetEquals(before)) SendLayerPicks(now: false);
    }

    /// <summary>The drawing's press ended: a drag's last list goes now.</summary>
    public void KeyUp()
    {
        layerPick.Up();
        if (layerPicking) layers.Flush();
    }

    /// <summary>A group button while picking: the layer's picks become that group (None: no keys), sent at once.</summary>
    public void PickGroup(KeyMapView map, string? group)
    {
        if (!layerPicking) return;
        if (group is null) layerPick.Clear();
        else layerPick.Set(KeyShapes.Group(map, group));
        SendLayerPicks(now: true);
    }

    /// <summary>Ends picking a layer's keys: a list still waiting from a drag goes now. The caller refreshes.</summary>
    public void EndPicking()
    {
        if (!layerPicking) return;
        layerPicking = false;
        layerPick.Up();
        layers.Flush();
        layerPick.Clear();
    }

    /// <summary>The list, the selected layer's settings and the note, from the list as the view shows it.</summary>
    /// <param name="keyboard">The keyboard as the latest state lists it.</param>
    /// <param name="canSend">Changes can go.</param>
    /// <param name="lightsOn">The state's Lights on.</param>
    /// <param name="keyHook">The state's <see cref="HostState.KeyHook"/>.</param>
    public void Show(DeviceView keyboard, bool canSend, bool lightsOn, bool keyHook)
    {
        device = keyboard;
        can = canSend;
        var list = CurrentLayers;
        int n = list?.Count ?? 0;
        if (layerAt >= n) layerAt = n - 1;
        if (layerAt < 0 && n > 0) layerAt = 0;
        var layer = layerAt >= 0 ? list![layerAt] : null;
        bool known = layer is not null && DeviceLayers.Kinds.Contains(layer.Kind);
        // Picking ends when there is nothing left to pick for: no layer, one of a kind this version doesn't know, every key, or no changes can go.
        if (layerPicking && (!known || layer!.Lights is null || !can)) EndPicking();
        // While picking, the picks follow the list shown: after a refusal, or a list changed from elsewhere, they are the state's again.
        if (layerPicking && layer!.Lights is { } listed)
        {
            var shown = ValidLights(listed);
            if (!layerPick.Picked.SetEquals(shown)) layerPick.Set(shown);
        }

        for (int i = 0; i < layerRows.Length; i++)
        {
            var row = layerRows[i];
            LightingPage.Show(row.Root, i < n);
            if (i >= n) continue;
            var each = list![i];
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
        ShowNote(list, lightsOn, keyHook);

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
        LightingPage.Show(groupRow, layerPicking);
        int skip = keyboard.SkippedLayerLights is { } skips && layerAt < skips.Count ? skips[layerAt] : 0;
        if (skip > 0)
            layerSkipped.Text = skip == 1
                ? "1 of this layer's keys doesn't match this keyboard's keys any more and is skipped."
                : $"{skip} of this layer's keys don't match this keyboard's keys any more and are skipped.";
        LightingPage.Show(layerSkipped, skip > 0);
    }

    // ---- State ----

    /// <summary>The layer list as the view shows it: the one asked for until the state carries it, else the state's.</summary>
    private List<DeviceLayer>? CurrentLayers => layers.Current(device?.Layers);

    /// <summary>The selected layer's sliders, once the view has been opened.</summary>
    private IEnumerable<LayerSlider> Sliders => new[] { speed, duration, strength }.OfType<LayerSlider>();

    private int LightCount => device?.Keys?.Names.Count ?? 0;

    /// <summary>The listed lights that are among the keyboard's, by index.</summary>
    private HashSet<int> ValidLights(List<LayerLight> lights)
    {
        int count = LightCount;
        return [.. lights.Where(light => light is not null && light.Index >= 0 && light.Index < count).Select(light => light.Index)];
    }

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
            layers.Send(message, done);
        }, label, maximum, largeChange, value =>
        {
            // A list still waiting goes first, so this one, made over it, lands after it.
            layers.Flush();
            // The slider is shown and enabled only with a layer selected; this answers a value that could come any other way harmlessly.
            if (device?.Key is not { } key || CurrentLayers is not { } list || layerAt < 0 || layerAt >= list.Count) return new GetStateRequest();
            var (asked, request) = AskLayers(key, LayerEdits.With(list, layerAt, l => set(l, value)));
            layers.Ask(asked);
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

    /// <summary>
    /// A state's value for a slider, and whether it can be used. A slider disabled while changes can go (another layer selected, the kind
    /// changed) sends a value it had waiting first, as a release does; one disabled because nothing can go drops it.
    /// </summary>
    private void ShowSlider(LayerSlider? slider, double value, bool enabled)
    {
        if (slider is null) return;
        if (!enabled)
        {
            if (can) slider.Tuning.Leave();
            slider.Waiting = null;
        }
        slider.Tuning.Show(value, enabled);
    }

    /// <summary>
    /// The note, while any layer is a key fade or key ripple. Its first paragraph gives way to <see cref="NoHookText"/> when the lights are
    /// on, the keyboard is painted, one of its reaction layers (as the state has them) could do something, and key presses aren't watched.
    /// </summary>
    private void ShowNote(List<DeviceLayer>? list, bool lightsOn, bool keyHook)
    {
        bool shown = LayerEdits.Reacts(list);
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
        if (keyboard.Layers is not { } list) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var layer = list[i];
            int skipped = keyboard.SkippedLayerLights is { } skips && i < skips.Count ? skips[i] : 0;
            if (layer is not null && layer.Kind is DeviceLayers.Fade or DeviceLayers.Ripple && layer.Strength > 0 && (layer.Lights is null || layer.Lights.Count > skipped)) return true;
        }
        return false;
    }

    // ---- Edits ----

    private void SelectLayer(int i)
    {
        // Switching to another layer ends picking.
        if (i != layerAt)
        {
            EndPicking();
            layerAt = i;
        }
        changed();
    }

    private void AddLayer()
    {
        EndPicking();
        var list = CurrentLayers;
        if (!can || (list?.Count ?? 0) >= DeviceLayers.MaxLayers)
        {
            changed();
            return;
        }
        var next = LayerEdits.Add(list);
        layerAt = next.Count - 1;
        SetLayers(next, now: true);
    }

    /// <summary>✕: the layer goes; the selection stays with the layer it was on, or the one taking the removed one's place.</summary>
    private void RemoveLayer(int i)
    {
        EndPicking();
        if (!can || CurrentLayers is not { } list || i >= list.Count)
        {
            changed();
            return;
        }
        if (layerAt > i) layerAt--;
        SetLayers(LayerEdits.Remove(list, i), now: true);
    }

    /// <summary>↑ or ↓: the layer swaps with its neighbour, and the selection goes with the layer it was on.</summary>
    private void MoveLayer(int i, int by)
    {
        EndPicking();
        if (!can || CurrentLayers is not { } list || i + by < 0 || i + by >= list.Count)
        {
            changed();
            return;
        }
        if (layerAt == i) layerAt = i + by;
        else if (layerAt == i + by) layerAt = i;
        SetLayers(LayerEdits.Move(list, i, by), now: true);
    }

    /// <summary>A change to the selected layer, sent at once; one that changes nothing (a click on the choice already made) sends nothing.</summary>
    private void ChangeLayer(Action<DeviceLayer> change)
    {
        if (CurrentLayers is { } list && layerAt >= 0 && layerAt < list.Count)
        {
            var next = LayerEdits.With(list, layerAt, change);
            if (!DeviceLayers.Same(new DeviceTuning { Layers = list }, new DeviceLook { Layers = next }))
            {
                SetLayers(next, now: true);
                return;
            }
        }
        changed();
    }

    /// <summary>All keys: the layer's list goes (every key), and picking ends.</summary>
    private void AllKeys()
    {
        EndPicking();
        ChangeLayer(l => l.Lights = null);
    }

    /// <summary>Only the keys I pick: a layer on every key starts with no keys; either way picking starts.</summary>
    private void SomeKeys()
    {
        if (!can || CurrentLayers is not { } list || layerAt < 0 || layerAt >= list.Count)
        {
            changed();
            return;
        }
        if (list[layerAt].Lights is null) ChangeLayer(l => l.Lights = []);
        StartPicking();
    }

    private void ToggleLayerPicking()
    {
        if (layerPicking)
        {
            EndPicking();
            changed();
        }
        else StartPicking();
    }

    /// <summary>The drawing becomes the picker for the selected layer, its picks set from the layer's lights.</summary>
    private void StartPicking()
    {
        if (can && CurrentLayers is { } list && layerAt >= 0 && layerAt < list.Count && list[layerAt].Lights is { } lights)
        {
            layerPick.Set(ValidLights(lights));
            layerPicking = true;
        }
        changed();
    }

    /// <summary>The selected layer's new list from its picks, in light order, each light with the keyboard's name for it.</summary>
    private void SendLayerPicks(bool now)
    {
        if (device?.Keys is { } map && CurrentLayers is { } list && layerAt >= 0 && layerAt < list.Count)
        {
            var lights = layerPick.Picked.Where(light => light >= 0 && light < map.Names.Count).Order()
                .Select(light => new LayerLight { Index = light, Name = map.Names[light] }).ToList();
            SetLayers(LayerEdits.With(list, layerAt, l => l.Lights = lights), now);
        }
        else changed();
    }

    /// <summary>
    /// Asks for a whole new layer list, shown at once: through the throttle, or <paramref name="now"/> for a single change. A slider's
    /// value still waiting goes first, or it would land after this list and put back the list it was made from.
    /// </summary>
    private void SetLayers(List<DeviceLayer> next, bool now)
    {
        if (device?.Key is { } key && can)
        {
            foreach (var slider in Sliders) slider.Tuning.Leave();
            var (asked, request) = AskLayers(key, next);
            layers.Post(asked, request, now);
        }
        changed();
    }

    /// <summary>
    /// The list to show and its request. A listed light at or beyond the keyboard's light count is left out: the host refuses a list
    /// holding one, and the light is skipped already. An empty list is sent as none.
    /// </summary>
    private (List<DeviceLayer>? Asked, SetDeviceLayersRequest Request) AskLayers(string key, List<DeviceLayer> next)
    {
        int count = LightCount;
        foreach (var layer in next) layer?.Lights?.RemoveAll(light => light is null || light.Index >= count);
        var asked = next.Count > 0 ? next : null;
        return (asked, new SetDeviceLayersRequest(key, asked));
    }

    // ---- Layout ----

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

    /// <summary>Rows one under another, as part of a section.</summary>
    private static StackPanel Rows(params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in rows) panel.Children.Add(row);
        return panel;
    }

    /// <summary>One of a set of choices: checked while it is the layer's, which <see cref="Show"/> puts right after every click.</summary>
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
        public Button Up { get; } = Small("↑", "Move up");
        public Button Down { get; } = Small("↓", "Move down");
        public Button Remove { get; } = Small("✕", "Remove layer");

        private static Button Small(string text, string name)
        {
            var button = new Button { Content = text, Padding = new Thickness(7, 1, 7, 1), MinWidth = 0, VerticalAlignment = VerticalAlignment.Stretch };
            AutomationProperties.SetName(button, name);
            return button;
        }
    }

    /// <summary>A layer's slider, and the request it last posted while that waits for its interval (null once it has gone or been dropped).</summary>
    private sealed class LayerSlider(TuningSlider tuning)
    {
        public TuningSlider Tuning { get; } = tuning;
        public HostMessage? Waiting { get; set; }
    }
}
