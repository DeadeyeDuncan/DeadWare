using DeadWare.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// A device card's sections, each with a line above it, as on the approved mockups: for a keyboard, Saved looks above its zone rows
/// (<see cref="Looks"/>, a <see cref="SavedLooks"/>), then under them Key colours, Lighting mode (keyboards only), Layers, Brightness
/// ("This device") and Colour correction; and the note that goes first on the card of a device running its own effect. Tuning changes go
/// to the lighting host as <see cref="SetDeviceModeRequest"/> and <see cref="SetDeviceTuningRequest"/> through the page's send, whose
/// failures show in the page's status line; Paint keys… and Edit layers… open the large keyboard view through the page. A device running
/// its own effect shows the sections disabled; one DeadWare can't drive shows none. Everything is disabled while the host isn't running.
/// Lighting 4: the Aerox 9 mouse (a view with <see cref="DeviceView.Mouse"/>) has a line under its zone rows, then Battery and Sleep
/// first among the sections, all in <see cref="MouseTexts"/>' words; a sleep time goes as <see cref="SetMouseSleepRequest"/>.
/// </summary>
internal sealed class DeviceSections
{
    private const string WaveText = "This keyboard doesn't report its key positions.";
    private const string NamesText = "This keyboard doesn't report its keys' names.";
    private const string PictureText = "Painting keys and layers need the desk picture, which this PC doesn't have yet.";
    private const string CorrectionText = "For a device whose white looks tinted next to the others. 100 leaves a colour as it is.";
    private const string PartsText = "Brightness and colour correction apply to the whole device: ";

    // The drop-down's items in order, with the mode kind each sends: null follows the desk effect.
    private static readonly (string? Kind, string Text)[] Modes =
    [
        (null, "Follow the desk effect"),
        (DeviceModes.Static, "Static"),
        (DeviceModes.Breathing, "Breathing"),
        (DeviceModes.Cycle, "Colour cycle"),
        (DeviceModes.Wave, "Rainbow wave"),
    ];

    private readonly Action<HostMessage, Action<Reply>?> send;
    private readonly ComboBox mode = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240, MaxWidth = TuningSlider.MaxRowWidth };
    private readonly ComboBoxItem[] items;
    private readonly TuningSlider speed, brightness, red, green, blue;
    private readonly Button reset = new() { Content = "Reset", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock modeNote = LightingPage.Muted(""), brightnessNote = LightingPage.Muted(""), partsNote = LightingPage.Muted("");
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border modeSection, brightnessSection, correctionSection, keyColoursSection, layersSection;
    // Lighting 4, the mouse: the line under its zone rows, Battery (bar, reading and hint) and Sleep (the drop-down, its status and the
    // note shown while a time is chosen). SleepValues: the drop-down's items' minutes in order, null leaving the mouse as it is.
    private static readonly int?[] SleepValues = [null, .. MouseSleep.Choices.Select(minutes => (int?)minutes)];
    private readonly TextBlock sharedColours = LightingPage.Muted(MouseTexts.SharedColours);
    // LightingPage.Muted's opacity, which the shared-colours line keeps when the mouse is connected.
    private const double MutedOpacity = 0.7;
    private readonly ProgressBar batteryBar = new() { Minimum = 0, Maximum = 100, Width = 120, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock batteryLine = new() { VerticalAlignment = VerticalAlignment.Center }, batteryHint = LightingPage.Muted("");
    private readonly ComboBox sleep = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 200 };
    private readonly TextBlock sleepStatus = LightingPage.Muted("");
    private readonly Border sleepNote, batterySection, sleepSection;
    // Lighting 5, the hub: the Readings section. Its rows are HubTexts.ReadingRows, rebuilt only when they change (readingsShown).
    private readonly StackPanel readingRows = new() { Spacing = 4 };
    private readonly TextBlock readingsNote = LightingPage.Muted(HubTexts.ReadingsNote);
    private readonly Border readingsSection;
    private string readingsShown = "";
    // sleepSending: sleep time changes on their way, while which a state doesn't move the drop-down.
    private int sleepSending;
    // Key colours and Layers: the count, the button that opens the large keyboard view on that part, and why it can't be used.
    private readonly TextBlock keysCount = new() { VerticalAlignment = VerticalAlignment.Center }, layersCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock keysNote = LightingPage.Muted(""), layersNote = LightingPage.Muted("");
    private readonly Button paintKeys = new() { Content = "Paint keys…", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button editLayers = new() { Content = "Edit layers…", VerticalAlignment = VerticalAlignment.Center };
    private readonly SavedLooks looks;
    private readonly Func<string, KeyboardEditor.EditorPart, bool> open;
    // The device as the last state listed it, and the page's Brightness then. enabled: the sections can be used (the host runs and
    // DeadWare paints the device). modeSending: mode changes on their way, while which a state doesn't move the drop-down.
    private DeviceView? device;
    private int pageBrightness = 100, modeSending;
    // viewable: the large keyboard view can open (picture mode, whose picture it takes the place of).
    private bool enabled, syncing, viewable;

    /// <param name="queue">The page's queue, for the sliders' throttles.</param>
    /// <param name="send">The page's send: the reply comes back on the UI thread, and not at all from a client the page no longer follows.</param>
    /// <param name="open">The page's way into the large keyboard view for a keyboard and a part; false when it can't open.</param>
    public DeviceSections(DispatcherQueue queue, Action<HostMessage, Action<Reply>?> send, Func<string, KeyboardEditor.EditorPart, bool> open)
    {
        this.send = send;
        this.open = open;
        paintKeys.Click += (_, _) => Open(KeyboardEditor.EditorPart.KeyColours);
        editLayers.Click += (_, _) => Open(KeyboardEditor.EditorPart.Layers);
        keyColoursSection = Section(Label("Key colours"), Row(keysCount, paintKeys), keysNote);
        layersSection = Section(Label("Layers"), Row(layersCount, editLayers), layersNote);
        items = [.. Modes.Select(entry => new ComboBoxItem { Content = entry.Text })];
        foreach (var item in items) mode.Items.Add(item);
        AutomationProperties.SetName(mode, "Lighting mode");
        mode.SelectionChanged += (_, _) => PickMode();
        speed = new TuningSlider(queue, send, "Speed", 100, 10, value => new SetDeviceModeRequest(Key, Chosen ?? device?.LightingMode?.Kind, value));
        modeSection = Section(Label("Lighting mode"), mode, modeNote, speed.Root);
        // A save or a choice takes the mode as the drop-down and Speed show it, so a Speed change still waiting goes first.
        looks = new SavedLooks(send, before: speed.Leave);

        brightness = new TuningSlider(queue, send, "This device", 100, 10, value => new SetDeviceTuningRequest(Key, Brightness: value), value => $"{value}%", _ => ShowBrightnessNote());
        brightnessSection = Section(Label("Brightness"), brightness.Root, brightnessNote);

        // Each channel's slider sends all three, as the sliders show them.
        red = Channel(queue, "Red");
        green = Channel(queue, "Green");
        blue = Channel(queue, "Blue");
        AutomationProperties.SetName(reset, "Reset colour correction");
        reset.Click += (_, _) => Reset();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Label("Colour correction");
        heading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(reset, 1);
        header.Children.Add(heading);
        header.Children.Add(reset);
        correctionSection = Section(header, red.Root, green.Root, blue.Root, LightingPage.Muted(CorrectionText), partsNote);

        Note = NoteBox(noteText);

        // The mouse's Battery and Sleep.
        AutomationProperties.SetName(batteryBar, MouseTexts.Battery);
        batterySection = Section(Label(MouseTexts.Battery), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { batteryBar, batteryLine } }, batteryHint);
        foreach (int? minutes in SleepValues) sleep.Items.Add(new ComboBoxItem { Content = MouseTexts.SleepChoice(minutes) });
        AutomationProperties.SetName(sleep, MouseTexts.SleepRow);
        sleep.SelectionChanged += (_, _) => PickSleep();
        var sleepWords = new TextBlock { Text = MouseTexts.SleepRow, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var sleepRow = new Grid { ColumnSpacing = 12, MaxWidth = TuningSlider.MaxRowWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        sleepRow.ColumnDefinitions.Add(new ColumnDefinition());
        sleepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sleep, 1);
        sleepRow.Children.Add(sleepWords);
        sleepRow.Children.Add(sleep);
        sleepNote = NoteBox(new TextBlock { Text = MouseTexts.Note, TextWrapping = TextWrapping.Wrap });
        sleepSection = Section(Label(MouseTexts.Sleep), sleepRow, sleepStatus, sleepNote);

        readingsSection = Section(Label(HubTexts.ReadingsHead), readingRows, readingsNote);

        foreach (var element in Sections.Append(Note).Append(Looks)) LightingPage.Show(element, false);
    }

    /// <summary>The note that goes first on the card of a device running its own effect; collapsed otherwise.</summary>
    public Border Note { get; }

    /// <summary>
    /// The sections in order, which go under the card's zone rows: the mouse's line, Battery and Sleep (lighting 4), the hub's Readings (lighting 5), then Key colours,
    /// Lighting mode, Layers, Brightness, Colour correction.
    /// </summary>
    public IEnumerable<UIElement> Sections => [sharedColours, batterySection, sleepSection, readingsSection, keyColoursSection, modeSection, layersSection, brightnessSection, correctionSection];

    /// <summary>A keyboard's Saved looks section, which goes above the card's zone rows; collapsed for any other device.</summary>
    public Border Looks => looks.Root;

    private string Key => device?.Key ?? "";

    /// <summary>The mode kind of the drop-down's chosen item; null for Follow the desk effect or no item.</summary>
    private string? Chosen => mode.SelectedIndex >= 0 ? Modes[mode.SelectedIndex].Kind : null;

    /// <param name="next">The device as the host lists it.</param>
    /// <param name="page">The page's Brightness, which the device's own works with.</param>
    /// <param name="labels">In picture mode, the device's parts' labels; null in plain mode.</param>
    /// <param name="live">The host is running.</param>
    /// <param name="viewable">The large keyboard view can open: the page is in picture mode.</param>
    /// <param name="hubReadings">The session's last hub readings with their age, for the hub's Readings section.</param>
    /// <param name="pictureApplies">The desk picture applies, so the hub's fans are the radiator's.</param>
    public void Update(DeviceView next, int page, IReadOnlyList<string>? labels, bool live, bool viewable, (HubReadings Readings, TimeSpan Age)? hubReadings, bool pictureApplies)
    {
        device = next;
        pageBrightness = page;
        this.viewable = viewable;
        bool hardware = next.Kind == PaintKind.Hardware, unsupported = next.Kind == PaintKind.Unsupported;
        enabled = live && next.Kind == PaintKind.Direct;
        LightingPage.Show(Note, hardware);
        if (hardware) ShowNote(next.OwnEffect);
        // A device DeadWare can't drive has no sections; its sliders' waiting changes and drags go with them.
        // Lighting mode shows for a keyboard, and for a device with a mode saved while it was one, so that mode can be seen and cleared.
        LightingPage.Show(modeSection, !unsupported && (next.Keyboard || next.LightingMode is not null));
        LightingPage.Show(brightnessSection, !unsupported);
        LightingPage.Show(correctionSection, !unsupported);
        // Saved looks, Key colours and Layers: a keyboard's (Keyboard, not Keys: one that reports no key positions shows them disabled,
        // saying why), disabled while it runs its own effect.
        bool keyboard = next.Keyboard && !unsupported;
        LightingPage.Show(looks.Root, keyboard);
        LightingPage.Show(keyColoursSection, keyboard);
        LightingPage.Show(layersSection, keyboard);
        looks.Update(next, enabled);
        ShowKeyboard();

        // Lighting mode: only a keyboard has it (not every device with a key map is one). Rainbow wave needs the key positions. A device
        // that isn't a keyboard but has a saved mode shows that mode with the host's reason under it, and offers only Follow the desk
        // effect, which clears it; it has no Speed.
        for (int i = 1; i < items.Length; i++) items[i].IsEnabled = next.Keyboard && (i != 4 || next.Wave);
        mode.IsEnabled = enabled;
        ApplyMode();
        var lines = new List<string>();
        if (next.Keyboard && !next.Wave) lines.Add(WaveText);
        if (next.ModeProblem is { Length: > 0 } problem && !lines.Contains(problem)) lines.Add(problem);
        modeNote.Text = string.Join("\n", lines);
        LightingPage.Show(modeNote, lines.Count > 0);
        // A saved mode the device can't run (ModeProblem) would have every Speed change refused, so Speed shows it but is disabled.
        speed.Show(next.LightingMode?.Speed ?? 40, enabled && next.Keyboard && next.ModeProblem is null);

        brightness.Show(next.Brightness, enabled);
        ShowBrightnessNote();
        ShowCorrection();
        ShowParts(labels);
        ShowMouse(live);
        ShowReadings(next.Hub, hubReadings, pictureApplies);
    }

    /// <summary>
    /// Lighting 5: the hub's Readings section, shown only for a view with <see cref="DeviceView.Hub"/>. It only renders
    /// <see cref="HubTexts.ReadingRows"/>: an entry without a value is the section's one line, entries with values are rows, and the
    /// muted note goes under the rows of a hub DeadWare drives. The rule behind the rows is Core's.
    /// </summary>
    private void ShowReadings(string? route, (HubReadings Readings, TimeSpan Age)? hubReadings, bool pictureApplies)
    {
        LightingPage.Show(readingsSection, route is not null);
        if (route is null) return;
        var rows = HubTexts.ReadingRows(route, hubReadings?.Readings, hubReadings?.Age, pictureApplies);
        string signature = string.Join("\n", rows.Select(row => row.Label + "\t" + row.Value));
        if (signature != readingsShown)
        {
            readingsShown = signature;
            readingRows.Children.Clear();
            foreach (var (label, value) in rows)
            {
                if (value is null)
                {
                    readingRows.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                    continue;
                }
                var line = new Grid { ColumnSpacing = 12, MaxWidth = TuningSlider.MaxRowWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                var name = LightingPage.Muted(label);
                var reading = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(reading, 1);
                line.Children.Add(name);
                line.Children.Add(reading);
                readingRows.Children.Add(line);
            }
        }
        LightingPage.Show(readingsNote, route == HubRoutes.Driven);
    }

    /// <summary>
    /// Lighting 4: the mouse's line under its zone rows, Battery and Sleep, shown only for a view with <see cref="DeviceView.Mouse"/>. A
    /// mouse that isn't connected has both sections dimmed but usable: a sleep time chosen then is saved for when it is back. The drop-down
    /// works while the host runs, and shows the state's choice except while a change of the user's is on its way.
    /// </summary>
    private void ShowMouse(bool live)
    {
        var shown = device?.Mouse;
        foreach (var element in new UIElement[] { sharedColours, batterySection, sleepSection }) LightingPage.Show(element, shown is not null);
        if (shown is null) return;
        double dim = MouseTexts.Dimmed(shown) ? 0.38 : 1;
        batterySection.Opacity = sleepSection.Opacity = dim;
        // The line under the zone rows is dimmed with them, from its own muted opacity.
        sharedColours.Opacity = MutedOpacity * dim;

        // The bar is greyed while the reading is old or unknown, as the battery line then says.
        bool greyed = shown.Battery is null || shown.Stale || shown.Connection == MouseConnections.None;
        batteryBar.Value = Math.Clamp(shown.Battery ?? 0, 0, 100);
        if (greyed) batteryBar.Foreground = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
        else batteryBar.ClearValue(Control.ForegroundProperty);
        batteryLine.Text = MouseTexts.BatteryLine(shown);
        batteryHint.Text = MouseTexts.Hint(shown);

        sleep.IsEnabled = live;
        ApplySleep();
        sleepStatus.Text = MouseTexts.SleepStatus(shown);
        LightingPage.Show(sleepNote, MouseTexts.ShowNote(shown));
    }

    /// <summary>Shows the state's sleep time in the drop-down, except while a change of the user's is on its way.</summary>
    private void ApplySleep()
    {
        if (sleepSending > 0 || device?.Mouse is not { } shown) return;
        int index = Math.Max(Array.IndexOf(SleepValues, shown.SleepMinutes), 0);
        if (sleep.SelectedIndex == index) return;
        syncing = true;
        try { sleep.SelectedIndex = index; }
        finally { syncing = false; }
    }

    /// <summary>The user's sleep time goes at once; a refused one puts the drop-down back to the state's.</summary>
    private void PickSleep()
    {
        if (syncing || !sleep.IsEnabled || sleep.SelectedIndex < 0 || device?.Mouse is not { } shown) return;
        int? minutes = SleepValues[sleep.SelectedIndex];
        if (minutes == shown.SleepMinutes) return;
        sleepSending++;
        send(new SetMouseSleepRequest(minutes), reply =>
        {
            sleepSending--;
            // The state carrying an accepted change follows its reply; until it is here the older one would put the drop-down back.
            if (!reply.Ok || device?.Mouse?.SleepMinutes == minutes) ApplySleep();
        });
    }

    /// <summary>The picture's part labels for the note under Colour correction, shown when the device has more than one; null hides it.</summary>
    public void ShowParts(IReadOnlyList<string>? labels)
    {
        var distinct = labels?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        bool shown = distinct.Length > 1;
        if (shown) partsNote.Text = PartsText + string.Join(", ", distinct);
        LightingPage.Show(partsNote, shown);
    }

    /// <summary>
    /// The page went into or out of picture mode, which the large keyboard view needs: Paint keys… and Edit layers… follow. (A state
    /// can change the mode after the cards have taken it, so the page tells the shown card again.)
    /// </summary>
    public void ShowViewable(bool on)
    {
        if (viewable == on) return;
        viewable = on;
        ShowKeyboard();
    }

    /// <summary>
    /// Disables every control while the host isn't running, ending drags and dropping waiting changes; the values stay as they were.
    /// Saved looks also closes a dialog it has open and forgets requests on their way.
    /// </summary>
    public void Disable()
    {
        enabled = false;
        mode.IsEnabled = reset.IsEnabled = paintKeys.IsEnabled = editLayers.IsEnabled = sleep.IsEnabled = false;
        looks.Disable();
        foreach (var slider in new[] { speed, brightness, red, green, blue })
        {
            slider.Cancel();
            slider.Slider.IsEnabled = false;
        }
    }

    /// <summary>Ends the sliders' drags and sends their waiting changes now, as when the page is hidden.</summary>
    public void Leave()
    {
        foreach (var slider in new[] { speed, brightness, red, green, blue }) slider.Leave();
    }

    /// <summary>A colour channel's slider, 0 to 200 with a tick at 100, whose change sends all three channels as the sliders show them.</summary>
    private TuningSlider Channel(DispatcherQueue queue, string name)
    {
        var channel = new TuningSlider(queue, send, name, 200, 10, _ => new SetDeviceTuningRequest(Key, Correction: new DeviceCorrection { R = red.Value, G = green.Value, B = blue.Value }), changed: _ => ShowReset());
        channel.Slider.TickFrequency = 100;
        channel.Slider.TickPlacement = TickPlacement.Inline;
        return channel;
    }

    /// <summary>"This device runs its own effect, <b>mode</b>. …", or without the mode when the device doesn't name it.</summary>
    private void ShowNote(string? own)
    {
        noteText.Inlines.Clear();
        const string Rest = ". DeadWare's colours, brightness and colour correction don't reach it while it does.";
        if (own is { Length: > 0 })
        {
            noteText.Inlines.Add(new Run { Text = "This device runs its own effect, " });
            noteText.Inlines.Add(new Bold { Inlines = { new Run { Text = own } } });
            noteText.Inlines.Add(new Run { Text = Rest });
        }
        else noteText.Inlines.Add(new Run { Text = "This device runs its own effect" + Rest });
    }

    /// <summary>Shows the saved mode in the drop-down, except while a mode change of the user's is on its way, and Speed for the animated ones.</summary>
    private void ApplyMode()
    {
        if (modeSending == 0 && device is { } shown)
        {
            int index = Array.FindIndex(Modes, entry => entry.Kind == shown.LightingMode?.Kind);
            syncing = true;
            try { mode.SelectedIndex = Math.Max(index, 0); }
            finally { syncing = false; }
        }
        LightingPage.Show(speed.Root, device?.Keyboard == true && DeviceModes.Animated(Chosen));
    }

    /// <summary>The user's choice goes at once, with the speed the slider shows; a refused one puts the drop-down back to the state's mode.</summary>
    private void PickMode()
    {
        LightingPage.Show(speed.Root, device?.Keyboard == true && DeviceModes.Animated(Chosen));
        if (syncing || !enabled || mode.SelectedIndex < 0 || device is null) return;
        string? kind = Chosen;
        modeSending++;
        send(new SetDeviceModeRequest(Key, kind, speed.Value), reply =>
        {
            modeSending--;
            // The state carrying an accepted change follows its reply; until it is here the older one would put the drop-down back.
            if (!reply.Ok || device?.LightingMode?.Kind == kind) ApplyMode();
        });
    }

    private void ShowBrightnessNote()
    {
        int own = brightness.Value;
        bool shown = own < 100 || pageBrightness < 100;
        if (shown) brightnessNote.Text = $"Works with the page's Brightness: {own}% of {pageBrightness}% here.";
        LightingPage.Show(brightnessNote, shown);
    }

    private void ShowCorrection()
    {
        var correction = device?.Correction ?? new DeviceCorrection();
        red.Show(correction.R, enabled);
        green.Show(correction.G, enabled);
        blue.Show(correction.B, enabled);
        ShowReset();
    }

    /// <summary>Reset works while the host runs and the correction, saved or on the sliders, isn't 100/100/100.</summary>
    private void ShowReset()
    {
        var saved = device?.Correction ?? new DeviceCorrection();
        bool neutral = saved is { R: 100, G: 100, B: 100 } && red.Value == 100 && green.Value == 100 && blue.Value == 100;
        reset.IsEnabled = enabled && !neutral;
    }

    /// <summary>Puts the three channels back to 100. A channel's change still waiting for its interval would land after it, so it is dropped.</summary>
    private void Reset()
    {
        if (!enabled || device is null) return;
        foreach (var slider in new[] { red, green, blue }) slider.Cancel();
        // The sliders show the saved values again until the reset's state arrives, so a dropped change isn't left showing as if it went.
        ShowCorrection();
        send(new SetDeviceTuningRequest(Key, ResetCorrection: true), null);
    }

    /// <summary>
    /// Key colours' and Layers' counts ("1 key painted", "3 layers", "No layers"), and their buttons: usable when DeadWare paints the
    /// keyboard, the host runs, the keyboard reports its key positions and names, and the page is in picture mode. A keyboard that doesn't
    /// report them says which under each button; otherwise, in plain mode, a keyboard DeadWare paints says under each that the desk picture
    /// is needed. A keyboard running its own effect has the card's note instead.
    /// </summary>
    private void ShowKeyboard()
    {
        if (device is not { } shown) return;
        int painted = shown.Paint?.Count ?? 0, layered = shown.Layers?.Count ?? 0;
        string keys = painted switch { 0 => "No keys painted", 1 => "1 key painted", _ => $"{painted} keys painted" };
        string layers = layered switch { 0 => "No layers", 1 => "1 layer", _ => $"{layered} layers" };
        if (keysCount.Text != keys) keysCount.Text = keys;
        if (layersCount.Text != layers) layersCount.Text = layers;
        string? why = !shown.CanPaint ? (shown.Keys is null ? WaveText : NamesText)
            : !viewable && shown.Kind == PaintKind.Direct ? PictureText : null;
        foreach (var note in new[] { keysNote, layersNote })
        {
            note.Text = why ?? "";
            LightingPage.Show(note, why is not null);
        }
        paintKeys.IsEnabled = editLayers.IsEnabled = enabled && viewable && shown.CanPaint;
    }

    /// <summary>Opens the large keyboard view on a part, when it can.</summary>
    private void Open(KeyboardEditor.EditorPart part)
    {
        if (!enabled || !viewable || device is not { CanPaint: true } shown) return;
        open(shown.Key, part);
    }

    /// <summary>A count on the left and its button on the right, as the mockup's "8 keys painted   Paint keys…".</summary>
    private static Grid Row(TextBlock text, Button button)
    {
        var row = new Grid { ColumnSpacing = 12, MaxWidth = TuningSlider.MaxRowWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(button, 1);
        row.Children.Add(text);
        row.Children.Add(button);
        return row;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };

    /// <summary>A note in a bordered box: the own-effect note, and the mouse's sleep note.</summary>
    private static Border NoteBox(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
    };

    /// <summary>A section: a line above it, then its rows.</summary>
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
}
