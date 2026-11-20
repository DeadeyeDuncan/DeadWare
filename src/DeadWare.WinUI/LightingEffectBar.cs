using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static DeadWare.WinUI.LightingPage;

namespace DeadWare.WinUI;

/// <summary>
/// The Lighting page's effect choice: four tiles (Static, Wave, Breathing, Spectrum) and, under them, the chosen effect's settings.
/// Static shows the page's own Set all colours row, which the bar only shows and hides; Wave has its colours, Speed, Direction and
/// Spread; Breathing and Spectrum have Speed. The bar keeps a working copy of every effect's settings: each control edits the copy
/// and sends the chosen effect's whole set, so one control's send can't put back another's newer value before its state has come.
/// A state replaces the copy, except the value of a control the user is dragging, whose change still waits to go, or whose change is
/// on its way to the host (the chosen tile too, for a tile's change); a refused change gives the control back to the latest state.
/// </summary>
internal sealed class LightingEffectBar : StackPanel
{
    private const string NoColoursText = "No colours chosen: the wave uses the full rainbow.";
    // The colour Add colour starts with when the wave has none: red, where the rainbow starts. After that it repeats the last colour.
    private const string FirstAddedColour = "#FF0000";
    private static readonly (EffectKind Kind, string Caption)[] Kinds =
        [(EffectKind.Static, "your saved colours"), (EffectKind.Wave, "sweeps across the desk"), (EffectKind.Breathing, "everything pulses together"), (EffectKind.Spectrum, "cycles through colours together")];

    private readonly Action<HostMessage, Action<Reply>?> send;
    private readonly UIElement staticRow;
    // Raised when the bar hides staticRow, so the page can close a flyout opened from it.
    private readonly Action? staticHidden;
    // One guard per control (the sliders hold their own): while a change of the bar's own is on its way, an arriving state doesn't
    // overwrite the control it came from. tileGuard keeps the chosen tile; colourGuard keeps the swatches (picks, Add and Remove).
    private readonly Guard tileGuard = new(), directionGuard = new(), colourGuard = new();
    private readonly ToggleButton[] tiles = new ToggleButton[Kinds.Length];
    private readonly StackPanel waveRows = new() { Spacing = 8 };
    // speedRow: every effect but Static. waveExtras: Direction and Spread, which only Wave has.
    private readonly FrameworkElement speedRow;
    private readonly FrameworkElement[] waveExtras;
    private readonly EffectSlider speed, spread;
    private readonly ComboBox direction = new() { MinWidth = 240 };
    private readonly Button[] swatches = new Button[LightingEffects.MaxColors];
    private readonly Border[] dots = new Border[LightingEffects.MaxColors];
    private readonly string?[] swatchColours = new string?[LightingEffects.MaxColors];
    private readonly string?[] swatchNames = new string?[LightingEffects.MaxColors];
    private readonly Button addColour = new() { Content = "Add colour", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock noColours = Muted(NoColoursText);
    // One colour flyout serves every swatch: the swatch that opens it is the colour it edits.
    private readonly Flyout colourFlyout = new() { Placement = FlyoutPlacementMode.Bottom };
    private readonly StackPanel colourContent = new() { Spacing = 8 };
    private readonly TextBlock colourHeading = Heading("");
    private readonly Button remove = new() { Content = "Remove" };
    private readonly Throttle colourSends;
    private ColorPicker? picker;
    private EffectSettings working = new();
    // The last state's effect, which a press that changed nothing gives back to its slider.
    private EffectSettings? stateEffect;
    private EffectKind chosen;
    // editing: the swatch the colour flyout edits or last edited, -1 after Remove. A light-dismiss leaves it in place, so whether the
    // flyout is open is colourFlyout.IsOpen, never editing. pickerShown: the picker's colour as last set or picked.
    private int editing = -1;
    // epoch: bumped when the guards are cleared, so a reply to a change sent before then touches nothing.
    private int epoch;
    private string? pickerShown;
    private bool syncing, live;

    public LightingEffectBar(DispatcherQueue queue, Action<HostMessage, Action<Reply>?> send, UIElement staticRow, Action? staticHidden = null)
    {
        this.send = send;
        this.staticRow = staticRow;
        this.staticHidden = staticHidden;
        Spacing = 12;

        var tileGrid = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < Kinds.Length; i++)
        {
            var (kind, caption) = Kinds[i];
            var tile = tiles[i] = new ToggleButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Padding = new Thickness(12, 8, 12, 8),
                Content = new StackPanel { Spacing = 2, Children = { Heading(kind.ToString()), Muted(caption) } },
            };
            // Radio-style: one of four, named for the effect, with its caption as help.
            AutomationProperties.SetName(tile, $"{kind} effect");
            AutomationProperties.SetHelpText(tile, caption);
            AutomationProperties.SetPositionInSet(tile, i + 1);
            AutomationProperties.SetSizeOfSet(tile, Kinds.Length);
            tile.Click += (_, _) => Choose(kind);
            tileGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(tile, i);
            tileGrid.Children.Add(tile);
        }
        Children.Add(tileGrid);
        Children.Add(staticRow);

        // A waiting send goes with the copy as it is when it goes, for the effect it was made for, so a send from another control that
        // went in between is never undone by it.
        speed = new EffectSlider(this, "Speed", queue, value => { if (LightingEffects.ParamsFor(working, chosen) is { } p) p.Speed = value; });
        spread = new EffectSlider(this, "Spread", queue, value => working.Wave.Spread = value);
        colourSends = new Throttle(queue, request => SendCopy(KindOf(request), colourGuard));

        // Wave's colours: up to eight swatches, each opening the colour flyout, then Add colour, hidden at eight.
        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (int i = 0; i < swatches.Length; i++)
        {
            int index = i;
            dots[i] = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9) };
            swatches[i] = new Button { Padding = new Thickness(4), Content = dots[i], VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            swatches[i].Click += (_, _) => OpenColour(index);
            swatchRow.Children.Add(swatches[i]);
        }
        AutomationProperties.SetName(addColour, "Add colour");
        addColour.Click += (_, _) => AddColour();
        swatchRow.Children.Add(addColour);
        waveRows.Children.Add(Row("Colours", new StackPanel { Spacing = 4, Children = { swatchRow, noColours } }));

        AutomationProperties.SetName(remove, "Remove colour");
        remove.Click += (_, _) => RemoveColour();
        colourContent.Children.Add(colourHeading);
        colourContent.Children.Add(remove);
        colourFlyout.Content = colourContent;
        // A pick still waiting goes as the flyout closes. If the swatches' last reply was a refusal and nothing has been sent since, the
        // swatches go back to the latest state again, so no later send carries the refused colour.
        colourFlyout.Closed += (_, _) =>
        {
            colourSends.Flush();
            if (colourGuard.Refused && stateEffect is { } effect) Show(effect, live);
        };

        speedRow = Row("Speed", speed.Slider);
        foreach (var (name, _) in LightingEffects.Directions) direction.Items.Add(name);
        AutomationProperties.SetName(direction, "Direction");
        direction.SelectionChanged += (_, _) => PickDirection();
        // Wave's rows in order: Colours, Speed, Direction, Spread. Breathing and Spectrum show Speed alone; Speed moves between them.
        Children.Add(waveRows);
        Children.Add(speedRow);
        var directionRow = Row("Direction", direction);
        var spreadRow = Row("Spread", spread.Slider);
        Children.Add(directionRow);
        Children.Add(spreadRow);
        waveExtras = [directionRow, spreadRow];
        Fill(resync: false);
    }

    // The page's Show, under another name: this class's own Show is the state's.
    private static void Reveal(UIElement element, bool visible) => LightingPage.Show(element, visible);

    /// <summary>A label and its control on one line, the labels in a column of their own.</summary>
    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>
    /// Shows the host's effect: the tile for its kind, and that kind's settings, without sending them. The working copy becomes the
    /// state's, except the value of a control being dragged or whose change still waits. Everything is disabled, in place, while the
    /// host isn't running; a change still waiting is then dropped, since it can't reach it.
    /// </summary>
    public void Show(EffectSettings effect, bool live)
    {
        bool wasLive = this.live;
        this.live = live;
        stateEffect = effect;
        if (!live)
        {
            // A disabled slider may never get its release, so its drag ends here. No guard outlives the connection its change went to.
            speed.Cancel();
            spread.Cancel();
            colourSends.Cancel();
            ClearGuards();
        }
        // The tile holds while its own change is on its way and not refused. When the kind changes any other way (a refused tile change,
        // or a state from elsewhere), a waiting parameter send is dropped rather than sent: every send sets the kind too, so it would put
        // back the kind it was made under. Dropped first, so its value doesn't hold below either.
        var kind = tileGuard.Holds ? chosen : LightingEffects.KindOf(effect.Kind);
        if (kind != chosen)
        {
            speed.Sends.Cancel();
            spread.Sends.Cancel();
            colourSends.Cancel();
        }
        // Each held value is kept for the kind it was changed for, so a hold on Wave's Speed or Spread outlives a visit to another tile.
        // A control whose last reply was a refusal holds only while dragged or waiting: otherwise it takes the state's value.
        var next = LightingEffects.Clone(effect);
        if (speed.Holding && LightingEffects.ParamsFor(working, speed.HeldKind) is { } held && LightingEffects.ParamsFor(next, speed.HeldKind) is { } p) p.Speed = held.Speed;
        if (spread.Holding) next.Wave.Spread = working.Wave.Spread;
        if (directionGuard.Holds) next.Wave.AngleDeg = working.Wave.AngleDeg;
        if (colourGuard.Holds || colourSends.Pending) next.Wave.Colors = new List<string>(working.Wave.Colors);
        // The colour being picked is the colour flyout's own while it is open, unless the swatches' last reply was a refusal; once the
        // host runs again, or after a refusal, the picker shows the state's colour.
        else if (!colourGuard.Refused && live && wasLive && colourFlyout.IsOpen && editing >= 0 && editing < working.Wave.Colors.Count && editing < next.Wave.Colors.Count)
            next.Wave.Colors[editing] = working.Wave.Colors[editing];
        chosen = kind;
        next.Kind = chosen.ToString();
        working = next;
        Fill(resync: (live && !wasLive) || colourGuard.Refused);
    }

    /// <summary>
    /// The page is being hidden: every waiting change goes now, so the final value isn't lost, drags end, the guards are cleared and
    /// the colour flyout closes. The page shows a state again when it is next shown.
    /// </summary>
    public void Leave()
    {
        speed.Leave();
        spread.Leave();
        colourSends.Flush();
        ClearGuards();
        if (colourFlyout.IsOpen) colourFlyout.Hide();
    }

    private void ClearGuards()
    {
        epoch++;
        foreach (var guard in new[] { tileGuard, directionGuard, colourGuard, speed.Guard, spread.Guard }) guard.Clear();
    }

    private static EffectKind KindOf(HostMessage request) => LightingEffects.KindOf(((SetEffectRequest)request).Kind);

    /// <summary>Brings every control up to date with the working copy and the chosen kind, without sending anything.</summary>
    private void Fill(bool resync)
    {
        bool staticCollapsed;
        syncing = true;
        try
        {
            for (int i = 0; i < tiles.Length; i++)
            {
                tiles[i].IsChecked = Kinds[i].Kind == chosen;
                tiles[i].IsEnabled = live;
            }
            bool wave = chosen == EffectKind.Wave, showStatic = chosen == EffectKind.Static;
            staticCollapsed = !showStatic && staticRow.Visibility == Visibility.Visible;
            Reveal(staticRow, showStatic);
            Reveal(waveRows, wave);
            foreach (var row in waveExtras) Reveal(row, wave);
            var settings = LightingEffects.ParamsFor(working, chosen);
            Reveal(speedRow, settings is not null);
            if (settings is not null && !speed.Dragging) speed.Slider.Value = Math.Clamp(settings.Speed, 0, 100);
            if (!spread.Dragging) spread.Slider.Value = Math.Clamp(working.Wave.Spread, 0, 100);
            speed.Slider.IsEnabled = spread.Slider.IsEnabled = live;
            direction.SelectedIndex = LightingEffects.NearestDirection(working.Wave.AngleDeg);
            direction.IsEnabled = live;
            FillSwatches();
        }
        finally { syncing = false; }
        FillFlyout(resync);
        // Static's row just went: a flyout the page opened from it must not stay up with its button hidden.
        if (staticCollapsed) staticHidden?.Invoke();
    }

    private void FillSwatches()
    {
        var colours = working.Wave.Colors;
        int count = Math.Min(colours.Count, swatches.Length);
        for (int i = 0; i < swatches.Length; i++)
        {
            var swatch = swatches[i];
            Reveal(swatch, i < count);
            swatch.IsEnabled = live && i < count;
            if (i >= count) continue;
            if (colours[i] != swatchColours[i])
            {
                swatchColours[i] = colours[i];
                dots[i].Background = new SolidColorBrush(ToColor(colours[i]));
                ToolTipService.SetToolTip(swatch, colours[i]);
            }
            string name = $"Wave colour {i + 1} of {count}";
            if (name == swatchNames[i]) continue;
            swatchNames[i] = name;
            AutomationProperties.SetName(swatch, name);
        }
        Reveal(noColours, count == 0);
        Reveal(addColour, count < LightingEffects.MaxColors);
        addColour.IsEnabled = live;
    }

    /// <summary>
    /// Keeps an open colour flyout in step: its controls don't take the bar's IsEnabled, so they are disabled and enabled here, and it
    /// closes when its colour went away or the wave is no longer chosen. When the host runs again, the picker shows its colour.
    /// </summary>
    private void FillFlyout(bool resync)
    {
        if (!colourFlyout.IsOpen) return;
        int count = working.Wave.Colors.Count;
        if (chosen != EffectKind.Wave || editing < 0 || editing >= count)
        {
            colourFlyout.Hide();
            return;
        }
        LabelFlyout(count);
        remove.IsEnabled = live;
        if (picker is null) return;
        picker.IsEnabled = live;
        if (resync) SetPicker(working.Wave.Colors[editing]);
    }

    private void LabelFlyout(int count)
    {
        string name = $"Wave colour {editing + 1} of {count}";
        colourHeading.Text = name;
        if (picker is not null) AutomationProperties.SetName(picker, name);
    }

    /// <summary>A tile's click: the kind is chosen at once and sent with its settings from the working copy.</summary>
    private void Choose(EffectKind kind)
    {
        if (!live || kind == chosen)
        {
            // A toggle button unchecks itself when the chosen tile is clicked again; it stays chosen.
            Fill(resync: false);
            return;
        }
        // A change still waiting goes first, for the effect it was made for.
        speed.Sends.Flush();
        spread.Sends.Flush();
        colourSends.Flush();
        chosen = kind;
        working.Kind = kind.ToString();
        Fill(resync: false);
        SendCopy(kind, tileGuard);
    }

    /// <summary>
    /// Sends a kind with its whole set of settings as the working copy holds them now (Static has none), guarding the control it came
    /// from until its reply. An Ok reply ends the guard and the next state is shown as it is; a refused or failed one ends it too,
    /// marks the control refused (it holds no more until it sends again) and the bar goes back to the latest state, whose reason the
    /// page's send-error line shows. A reply from before the guards were cleared does nothing.
    /// </summary>
    private void SendCopy(EffectKind kind, Guard guard)
    {
        int sent = epoch;
        guard.Count++;
        guard.Refused = false;
        send(Request(kind), reply =>
        {
            if (sent != epoch) return;
            guard.Count = Math.Max(0, guard.Count - 1);
            guard.Refused = !reply.Ok;
            if (reply.Ok) return;
            // A pick still waiting would carry the swatches' working colours to the host before they are the state's again.
            if (guard == colourGuard) colourSends.Cancel();
            if (stateEffect is { } effect) Show(effect, live);
        });
    }

    private SetEffectRequest Request(EffectKind kind) => new(kind.ToString(), LightingEffects.ParamsFor(LightingEffects.Clone(working), kind));

    private void PickDirection()
    {
        if (syncing || !live || chosen != EffectKind.Wave || direction.SelectedIndex is not (>= 0 and var index)) return;
        working.Wave.AngleDeg = LightingEffects.Directions[index].Angle;
        SendCopy(EffectKind.Wave, directionGuard);
    }

    /// <summary>Opens the colour flyout under a swatch, for that colour. The picker is made the first time it is needed.</summary>
    private void OpenColour(int index)
    {
        if (!live || chosen != EffectKind.Wave || index >= working.Wave.Colors.Count) return;
        if (picker is null)
        {
            picker = NewPicker();
            picker.ColorChanged += OnPicked;
            colourContent.Children.Insert(1, picker);
        }
        editing = index;
        LabelFlyout(working.Wave.Colors.Count);
        picker.IsEnabled = remove.IsEnabled = true;
        SetPicker(working.Wave.Colors[index]);
        colourFlyout.ShowAt(swatches[index]);
    }

    private void OnPicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        string color = Hex(args.NewColor);
        // Not the colour the bar shows when the flyout opens, nor one the picker raises while it loads.
        if (syncing || !live || !sender.IsLoaded || color == pickerShown || editing < 0 || editing >= working.Wave.Colors.Count) return;
        pickerShown = color;
        working.Wave.Colors[editing] = color;
        syncing = true;
        try { FillSwatches(); }
        finally { syncing = false; }
        colourSends.Post(Request(EffectKind.Wave));
    }

    private void RemoveColour()
    {
        if (!live || editing < 0 || editing >= working.Wave.Colors.Count) return;
        // A pick still waiting would go after the removal; the removal's send carries every other colour as it is.
        colourSends.Cancel();
        working.Wave.Colors.RemoveAt(editing);
        editing = -1;
        colourFlyout.Hide();
        Fill(resync: false);
        SendCopy(EffectKind.Wave, colourGuard);
    }

    private void AddColour()
    {
        var colours = working.Wave.Colors;
        if (!live || chosen != EffectKind.Wave || colours.Count >= LightingEffects.MaxColors) return;
        colours.Add(colours.Count == 0 ? FirstAddedColour : colours[^1]);
        Fill(resync: false);
        SendCopy(EffectKind.Wave, colourGuard);
    }

    /// <summary>Shows a colour in the picker without sending it: the guard skips the change it raises.</summary>
    private void SetPicker(string color)
    {
        if (picker is null) return;
        var value = ToColor(color);
        pickerShown = Hex(value);
        syncing = true;
        try { picker.Color = value; }
        finally { syncing = false; }
    }

    /// <summary>
    /// A 0–100 slider for one setting with its own throttle, as Brightness has: a drag sends at most once per 100 ms and its last value
    /// when it is released, and while it is dragged, its change waits or is on its way, an arriving state doesn't move it.
    /// </summary>
    private sealed class EffectSlider
    {
        private readonly LightingEffectBar bar;
        // changed: the drag has moved the value, so a release with nothing changed gives the slider the last state's value.
        private bool changed;

        public EffectSlider(LightingEffectBar bar, string name, DispatcherQueue queue, Action<double> edit)
        {
            this.bar = bar;
            Sends = new Throttle(queue, request => bar.SendCopy(KindOf(request), Guard));
            AutomationProperties.SetName(Slider, name);
            Slider.ValueChanged += (_, args) =>
            {
                if (bar.syncing || !bar.live || bar.chosen == EffectKind.Static) return;
                changed = true;
                HeldKind = bar.chosen;
                edit(Math.Round(args.NewValue));
                Sends.Post(bar.Request(bar.chosen));
            };
            // The slider handles its own pointer events, so these listen to handled ones too. A drag ends when the pointer is released
            // or capture is lost, and its last value goes then rather than at the end of the interval.
            Slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { Dragging = Slider.IsEnabled; changed = false; }), true);
            var released = new PointerEventHandler((_, _) => EndDrag());
            Slider.AddHandler(UIElement.PointerReleasedEvent, released, true);
            Slider.AddHandler(UIElement.PointerCaptureLostEvent, released, true);
            Slider.AddHandler(UIElement.PointerCanceledEvent, released, true);
            // The arrow keys move it a step at a time; the last step goes when the key comes up.
            Slider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => Sends.Flush()), true);
        }

        public Slider Slider { get; } = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, SmallChange = 1, LargeChange = 10, Width = 240 };
        public Throttle Sends { get; }
        public Guard Guard { get; } = new();
        public bool Dragging { get; private set; }
        /// <summary>The kind whose setting the slider last changed: the one its held value belongs to.</summary>
        public EffectKind HeldKind { get; private set; }
        /// <summary>
        /// The user's value holds against an arriving state: it is being dragged, waits to go, or is on its way and its last reply
        /// wasn't a refusal.
        /// </summary>
        public bool Holding => Dragging || Sends.Pending || Guard.Holds;

        /// <summary>Ends a drag and drops a waiting change, as when the host stops running.</summary>
        public void Cancel()
        {
            Dragging = false;
            Sends.Cancel();
        }

        /// <summary>Ends a drag and sends a waiting change now, as when the page is hidden.</summary>
        public void Leave()
        {
            Dragging = false;
            Sends.Flush();
        }

        private void EndDrag()
        {
            if (!Dragging) return;
            Dragging = false;
            bool waiting = Sends.Pending;
            Sends.Flush();
            // A press that changed nothing leaves nothing on its way, so the slider takes the state that arrived meanwhile. So does a drag
            // whose last reply was a refusal with nothing waiting to go at the release: its value goes back to the state's.
            if ((!changed || (Guard.Refused && !waiting)) && bar.stateEffect is { } effect) bar.Show(effect, bar.live);
        }
    }

    /// <summary>
    /// How many of one control's changes are on their way (sent, their reply not yet seen), and whether its last reply was a refusal.
    /// A refused control holds no more until it sends again.
    /// </summary>
    private sealed class Guard
    {
        public int Count;
        public bool Refused;
        public bool Holds => Count > 0 && !Refused;

        public void Clear()
        {
            Count = 0;
            Refused = false;
        }
    }
}
