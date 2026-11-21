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
/// A device card's tuning, under its zone rows as on the approved mockup: Lighting mode (keyboards only), Brightness ("This device") and
/// Colour correction, each a section with a line above it; and the note that goes first on the card of a device running its own effect.
/// Changes go to the lighting host as <see cref="SetDeviceModeRequest"/> and <see cref="SetDeviceTuningRequest"/> through the page's send,
/// whose failures show in the page's status line. A device running its own effect shows the sections disabled; one DeadWare can't drive
/// shows none. Everything is disabled while the host isn't running.
/// </summary>
internal sealed class DeviceSections
{
    private const string WaveText = "This keyboard doesn't report its key positions.";
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
    private readonly ComboBox mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBoxItem[] items;
    private readonly TuningSlider speed, brightness, red, green, blue;
    private readonly Button reset = new() { Content = "Reset", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock modeNote = LightingPage.Muted(""), brightnessNote = LightingPage.Muted(""), partsNote = LightingPage.Muted("");
    private readonly TextBlock noteText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border modeSection, brightnessSection, correctionSection;
    // The device as the last state listed it, and the page's Brightness then. enabled: the sections can be used (the host runs and
    // DeadWare paints the device). modeSending: mode changes on their way, while which a state doesn't move the drop-down.
    private DeviceView? device;
    private int pageBrightness = 100, modeSending;
    private bool enabled, syncing;

    /// <param name="queue">The page's queue, for the sliders' throttles.</param>
    /// <param name="send">The page's send: the reply comes back on the UI thread, and not at all from a client the page no longer follows.</param>
    public DeviceSections(DispatcherQueue queue, Action<HostMessage, Action<Reply>?> send)
    {
        this.send = send;
        items = [.. Modes.Select(entry => new ComboBoxItem { Content = entry.Text })];
        foreach (var item in items) mode.Items.Add(item);
        AutomationProperties.SetName(mode, "Lighting mode");
        mode.SelectionChanged += (_, _) => PickMode();
        speed = new TuningSlider(queue, send, "Speed", 100, 10, value => new SetDeviceModeRequest(Key, Chosen ?? device?.LightingMode?.Kind, value));
        modeSection = Section(Label("Lighting mode"), mode, modeNote, speed.Root);

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

        Note = new Border
        {
            Child = noteText, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
        foreach (var element in Sections.Append(Note)) LightingPage.Show(element, false);
    }

    /// <summary>The note that goes first on the card of a device running its own effect; collapsed otherwise.</summary>
    public Border Note { get; }

    /// <summary>The three sections in order, which go under the card's zone rows.</summary>
    public IEnumerable<UIElement> Sections => [modeSection, brightnessSection, correctionSection];

    private string Key => device?.Key ?? "";

    /// <summary>The mode kind of the drop-down's chosen item; null for Follow the desk effect or no item.</summary>
    private string? Chosen => mode.SelectedIndex >= 0 ? Modes[mode.SelectedIndex].Kind : null;

    /// <param name="next">The device as the host lists it.</param>
    /// <param name="page">The page's Brightness, which the device's own works with.</param>
    /// <param name="labels">In picture mode, the device's parts' labels; null in plain mode.</param>
    /// <param name="live">The host is running.</param>
    public void Update(DeviceView next, int page, IReadOnlyList<string>? labels, bool live)
    {
        device = next;
        pageBrightness = page;
        bool hardware = next.Kind == PaintKind.Hardware, unsupported = next.Kind == PaintKind.Unsupported;
        enabled = live && next.Kind == PaintKind.Direct;
        LightingPage.Show(Note, hardware);
        if (hardware) ShowNote(next.OwnEffect);
        // A device DeadWare can't drive has no sections; its sliders' waiting changes and drags go with them.
        // Lighting mode shows for a keyboard, and for a device with a mode saved while it was one, so that mode can be seen and cleared.
        LightingPage.Show(modeSection, !unsupported && (next.Keyboard || next.LightingMode is not null));
        LightingPage.Show(brightnessSection, !unsupported);
        LightingPage.Show(correctionSection, !unsupported);

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
    }

    /// <summary>The picture's part labels for the note under Colour correction, shown when the device has more than one; null hides it.</summary>
    public void ShowParts(IReadOnlyList<string>? labels)
    {
        var distinct = labels?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        bool shown = distinct.Length > 1;
        if (shown) partsNote.Text = PartsText + string.Join(", ", distinct);
        LightingPage.Show(partsNote, shown);
    }

    /// <summary>Disables every control while the host isn't running, ending drags and dropping waiting changes; the values stay as they were.</summary>
    public void Disable()
    {
        enabled = false;
        mode.IsEnabled = reset.IsEnabled = false;
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

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };

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
