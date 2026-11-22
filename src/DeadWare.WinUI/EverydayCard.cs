using DeadWare.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace DeadWare.WinUI;

/// <summary>
/// The Lighting page's Everyday card, as on the approved mockup: the lights on/off shortcut (the keys, Change/Cancel, No shortcut, the
/// hint and a red line for what went wrong), lights off when the PC sits unused (After [minutes] and a switch) and lights off when it is
/// locked (a switch). Three columns, one under the other on a narrow page. Every change sends the whole <see cref="EverydaySettings"/> as
/// <see cref="SetEverydayRequest"/> through the page's send. The controls show only the host's state: while a change of the user's is on
/// its way they show that change instead, as the page's Lights on switch does, and nothing is kept once the host's state has caught up.
/// Every word comes from <see cref="EverydayTexts"/> and <see cref="Shortcuts"/>.
/// </summary>
internal sealed class EverydayCard
{
    // Below this width the three columns go one under the other, as the mockup's.
    private const double WideLayout = 900;

    private static readonly VirtualKey[] ModifierKeys =
    [
        VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.RightControl, VirtualKey.Menu, VirtualKey.LeftMenu, VirtualKey.RightMenu,
        VirtualKey.Shift, VirtualKey.LeftShift, VirtualKey.RightShift, VirtualKey.LeftWindows, VirtualKey.RightWindows,
    ];

    private readonly Action<HostMessage, Action<Reply>?> send;
    private readonly Grid columns = new() { ColumnSpacing = 18, RowSpacing = 16 };
    private readonly FrameworkElement[] parts;
    // The keys box: a focusable control around a bordered line of text, which takes the new keys while recording.
    private readonly ContentControl keysBox = new() { IsTabStop = true, UseSystemFocusVisuals = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock keysText = new() { TextAlignment = TextAlignment.Center };
    private readonly Border keysBorder;
    private readonly Button change = new(), noShortcut = new() { Content = EverydayTexts.NoShortcut };
    private readonly TextBlock shortcutHint = LightingPage.Muted(EverydayTexts.ShortcutHint);
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly ComboBox idleMinutes = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 72 };
    private readonly ToggleSwitch idleOff = Switch(), lockOff = Switch();
    // The state last shown and whether its host is connected; the controls are usable only while it is and its settings are there.
    private HostState? state;
    private bool connected, recording, syncing;
    // Changes on their way, and the last one sent: shown in place of the state's until the state has caught up with it.
    private int sending;
    private EverydaySettings? pending;
    // invalid: the keys pressed while recording don't make a shortcut. failure: the host's words for the last refused change.
    private string? invalid, failure;

    /// <param name="send">The page's send: the reply comes back on the UI thread, and not at all from a client the page no longer follows.</param>
    public EverydayCard(Action<HostMessage, Action<Reply>?> send)
    {
        this.send = send;

        keysBorder = new Border
        {
            Child = keysText, MinWidth = 108, Padding = new Thickness(10, 4, 10, 4), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
        };
        keysBox.Content = keysBorder;
        AutomationProperties.SetName(keysBox, EverydayTexts.ShortcutLabel);
        AutomationProperties.SetLiveSetting(keysBox, AutomationLiveSetting.Polite);
        keysBox.KeyDown += OnKeyDown;
        change.Click += (_, _) => ToggleRecording();
        noShortcut.Click += (_, _) => ClearShortcut();
        problem.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        AutomationProperties.SetLiveSetting(problem, AutomationLiveSetting.Polite);
        var shortcut = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Label(EverydayTexts.ShortcutLabel), keysBox,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { change, noShortcut } },
                shortcutHint, problem,
            },
        };

        foreach (int minutes in Everydays.IdleChoices) idleMinutes.Items.Add(new ComboBoxItem { Content = minutes.ToString() });
        AutomationProperties.SetName(idleMinutes, EverydayTexts.IdleLabel);
        AutomationProperties.SetName(idleOff, EverydayTexts.IdleLabel);
        idleMinutes.SelectionChanged += (_, _) => PickMinutes();
        idleOff.Toggled += (_, _) => Change(next => next.IdleOff = idleOff.IsOn);
        var after = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { Centred("After"), idleMinutes, Centred("minutes") },
        };
        var idle = new StackPanel { Spacing = 6, Children = { Label(EverydayTexts.IdleLabel), Row(after, idleOff), LightingPage.Muted(EverydayTexts.IdleHint) } };

        AutomationProperties.SetName(lockOff, EverydayTexts.LockRow);
        lockOff.Toggled += (_, _) => Change(next => next.LockOff = lockOff.IsOn);
        var locked = new StackPanel { Spacing = 6, Children = { Label(EverydayTexts.LockLabel), Row(Centred(EverydayTexts.LockRow), lockOff), LightingPage.Muted(EverydayTexts.LockHint) } };

        parts = [shortcut, idle, locked];
        foreach (var part in parts) columns.Children.Add(part);
        Arrange(true);
        columns.SizeChanged += (_, args) => Arrange(args.NewSize.Width >= WideLayout);

        Root = Pages.Card(new StackPanel
        {
            Spacing = 8, Padding = new Thickness(8),
            Children = { LightingPage.Heading(EverydayTexts.Heading), LightingPage.Muted(EverydayTexts.Sub), columns },
        });
        Show();
    }

    /// <summary>The card, which the page puts under the desk area.</summary>
    public UIElement Root { get; }

    /// <summary>Shows the host's everyday settings and problems. Not connected (or no state yet) leaves the values as they were, disabled.</summary>
    /// <param name="next">The client's latest state; null while the host connects or relaunches.</param>
    /// <param name="connected">The host is connected, so changes can go to it.</param>
    public void Update(HostState? next, bool connected)
    {
        state = next ?? state;
        this.connected = connected && next is not null;
        // Nothing on its way: the state is the only source again (an Ok reply may have kept the change shown until this state came).
        if (sending == 0) pending = null;
        Show();
    }

    /// <summary>Starts afresh for a new client: the changes on their way and the messages belonged to the old one.</summary>
    public void Reset()
    {
        state = null;
        connected = recording = false;
        sending = 0;
        pending = null;
        invalid = failure = null;
        Show();
    }

    /// <summary>The settings the controls show: the change on its way, else the state's; null before any state.</summary>
    private EverydaySettings? Shown => pending ?? state?.Settings?.Everyday;

    private bool Usable => connected && state?.Settings?.Everyday is not null;

    private void Show()
    {
        bool usable = Usable;
        if (!usable)
        {
            recording = false;
            invalid = null;
        }
        var shown = Shown;
        keysText.Text = recording ? EverydayTexts.Recording : shown is null ? "" : Shortcuts.Text(shown.Shortcut);
        keysBorder.BorderBrush = (Brush)Application.Current.Resources[recording ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
        change.Content = recording ? EverydayTexts.Cancel : EverydayTexts.Change;
        change.IsEnabled = usable;
        noShortcut.IsEnabled = usable && (recording || shown?.Shortcut is not null);
        shortcutHint.Text = shown is not null && shown.Shortcut is null ? EverydayTexts.NoShortcutHint : EverydayTexts.ShortcutHint;
        keysBox.IsEnabled = idleMinutes.IsEnabled = idleOff.IsEnabled = lockOff.IsEnabled = usable;
        if (shown is not null)
        {
            syncing = true;
            try
            {
                int index = Array.IndexOf(Everydays.IdleChoices, shown.IdleMinutes);
                if (idleMinutes.SelectedIndex != index) idleMinutes.SelectedIndex = index;
                if (idleOff.IsOn != shown.IdleOff) idleOff.IsOn = shown.IdleOff;
                if (lockOff.IsOn != shown.LockOff) lockOff.IsOn = shown.LockOff;
            }
            finally { syncing = false; }
        }
        // The red line: what the user just did wrong or the host refused, then the host's own problems with the shortcut and the rules.
        var lines = new List<string>();
        foreach (string? line in new[] { invalid ?? failure, state?.ShortcutProblem, state?.EverydayProblem })
            if (line is { Length: > 0 } && !lines.Contains(line)) lines.Add(line);
        string text = string.Join("\n", lines);
        if (problem.Text != text) problem.Text = text;
        LightingPage.Show(problem, text.Length > 0);
    }

    /// <summary>Change starts recording and takes the focus to the keys box; Cancel ends it, leaving the shortcut as it is.</summary>
    private void ToggleRecording()
    {
        if (!Usable) return;
        recording = !recording;
        invalid = null;
        if (recording) failure = null;
        Show();
        if (recording) keysBox.Focus(FocusState.Programmatic);
    }

    /// <summary>No shortcut: ends any recording and sends the settings with none.</summary>
    private void ClearShortcut()
    {
        recording = false;
        invalid = null;
        Change(next => next.Shortcut = null);
        Show();
    }

    /// <summary>
    /// A key while recording: Esc cancels; Tab moves on as usual; a modifier alone waits for the key; any other key, with the modifiers
    /// held, is the new shortcut when it makes a valid one (sent at once), else the red line says what is needed and recording goes on.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!recording) return;
        var key = e.Key;
        if (key == VirtualKey.Tab) return;
        e.Handled = true;
        if (key == VirtualKey.Escape)
        {
            recording = false;
            invalid = null;
            Show();
            return;
        }
        if (Array.IndexOf(ModifierKeys, key) >= 0) return;
        bool win = Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows);
        var shortcut = EverydayTexts.FromKeys(Down(VirtualKey.Control), Down(VirtualKey.Menu), Down(VirtualKey.Shift), win, (int)key, out string? error);
        if (shortcut is null)
        {
            invalid = error;
            Show();
            return;
        }
        recording = false;
        invalid = null;
        Change(next => next.Shortcut = shortcut);
        Show();
    }

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void PickMinutes()
    {
        int index = idleMinutes.SelectedIndex;
        if (index < 0) return;
        Change(next => next.IdleMinutes = Everydays.IdleChoices[index]);
    }

    /// <summary>
    /// Sends the shown settings with one change made, unless the change came from showing a state or changes nothing. The reply's
    /// refusal goes on the red line, in the host's words; an Ok reply clears it.
    /// </summary>
    private void Change(Action<EverydaySettings> edit)
    {
        if (syncing || !Usable || Shown is not { } shown) return;
        var next = Everydays.Clone(shown);
        edit(next);
        if (Same(next, shown)) return;
        sending++;
        pending = next;
        Show();
        send(new SetEverydayRequest(next), reply =>
        {
            sending--;
            failure = reply.Ok ? null : reply.Error is { Length: > 0 } why ? HostReplyCodes.Explain(why) : null;
            // The state carrying an accepted change follows its reply; until it is here the older one would put the controls back, so
            // the change stays shown until the next state (Update). A refused one, or one the state already carries, goes now.
            if (sending == 0 && (!reply.Ok || state?.Settings?.Everyday is { } saved && Same(saved, next))) pending = null;
            Show();
        });
    }

    private static bool Same(EverydaySettings a, EverydaySettings b) =>
        a.Shortcut == b.Shortcut && a.IdleOff == b.IdleOff && a.IdleMinutes == b.IdleMinutes && a.LockOff == b.LockOff;

    /// <summary>Wide: three columns side by side. Narrow: one column, the three parts one under the other.</summary>
    private void Arrange(bool wide)
    {
        if (columns.ColumnDefinitions.Count == (wide ? 3 : 1)) return;
        columns.ColumnDefinitions.Clear();
        columns.RowDefinitions.Clear();
        for (int i = 0; i < parts.Length; i++)
        {
            if (wide) columns.ColumnDefinitions.Add(new ColumnDefinition());
            else columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(parts[i], wide ? i : 0);
            Grid.SetRow(parts[i], wide ? 0 : i);
        }
        if (!wide) columns.ColumnDefinitions.Add(new ColumnDefinition());
    }

    /// <summary>A switch with no words of its own: the row's text beside it says what it does.</summary>
    private static ToggleSwitch Switch() => new() { OnContent = "", OffContent = "", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Centred(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    /// <summary>The row's words on the left and its switch on the right, as the mockup's rows.</summary>
    private static Grid Row(FrameworkElement words, ToggleSwitch toggle)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 1);
        row.Children.Add(words);
        row.Children.Add(toggle);
        return row;
    }
}
