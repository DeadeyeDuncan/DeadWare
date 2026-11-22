using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>
/// A keyboard's Saved looks section (lighting 3b, mockup key-painting.html), the one control the device card and the large keyboard view's
/// card both show: a drop-down of the looks with the current one chosen ("Gaming (changed)" when the keyboard no longer matches it), and
/// Save changes (only when changed), Save as a new look…, Rename and Delete; with no looks saved, only Save as a new look…. Choosing a look
/// switches the keyboard to it, asking first when the keyboard's setup is in no look (<see cref="LookNames.Unsaved"/>). The names asked for
/// in its dialogs are checked with <see cref="LookNames.Problem"/> as they are typed. Requests go through the page's send, whose refusals show
/// in the page's status line; after a refusal the control shows the state again. Rename and Delete act on the current look.
/// </summary>
internal sealed class SavedLooks
{
    private const string SwitchText = "The keyboard's current setup isn't saved in a look. Switch and lose it?";

    private readonly Action<HostMessage, Action<Reply>?> send;
    private readonly Action? before, chosen;
    private readonly ComboBox box = new() { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240, MaxWidth = TuningSlider.MaxRowWidth };
    private readonly Button saveChanges = new() { Content = "Save changes" }, saveNew = new() { Content = "Save as a new look…" };
    private readonly Button rename = new() { Content = "Rename" }, delete = new() { Content = "Delete" };
    // The keyboard as the last state listed it, and the names the drop-down holds. enabled: requests can go (the host runs and DeadWare
    // paints the keyboard). sending: look requests on their way, while which a state doesn't move the drop-down. ticket: moves on when the
    // control is disabled or reset, so a reply or a dialog from before counts for nothing.
    private DeviceView? device;
    private string[] names = [];
    private bool enabled, syncing;
    private int sending, ticket;
    private ContentDialog? dialog;

    /// <param name="send">The page's send: the reply comes back on the UI thread, and not at all from a client the page no longer follows.</param>
    /// <param name="before">Run before each request, so a change of the keyboard's still waiting goes first and the look takes it.</param>
    /// <param name="chosen">Told when the host has taken a choice of look.</param>
    public SavedLooks(Action<HostMessage, Action<Reply>?> send, Action? before = null, Action? chosen = null)
    {
        this.send = send;
        this.before = before;
        this.chosen = chosen;
        AutomationProperties.SetName(box, "Saved looks");
        box.SelectionChanged += (_, _) => Pick();
        saveChanges.Click += (_, _) => SaveChanges();
        saveNew.Click += (_, _) => SaveNew();
        rename.Click += (_, _) => Rename();
        delete.Click += (_, _) => Delete();
        Root = KeyboardEditor.Section(KeyboardEditor.Label("Saved looks"), box, KeyboardEditor.FlowOf(saveChanges, saveNew, rename, delete));
    }

    /// <summary>The section.</summary>
    public Border Root { get; }

    private string Key => device?.Key ?? "";

    /// <summary>The current look's name as the drop-down holds it; null when no look is current.</summary>
    private string? Current => device is { } shown && Index(names, shown.Look) is var at && at >= 0 ? names[at] : null;

    /// <param name="next">The keyboard as the host lists it.</param>
    /// <param name="enabled">The host runs and DeadWare paints the keyboard.</param>
    public void Update(DeviceView next, bool enabled)
    {
        device = next;
        this.enabled = enabled;
        Show();
    }

    /// <summary>Disables the control while the host isn't running or the device is gone: an open dialog closes and replies on their way count for nothing.</summary>
    public void Disable()
    {
        enabled = false;
        Forget();
        Show();
    }

    /// <summary>The large keyboard view closed: as <see cref="Disable"/>, and the keyboard is forgotten until the next <see cref="Update"/>.</summary>
    public void Reset()
    {
        enabled = false;
        Forget();
        device = null;
    }

    private void Forget()
    {
        ticket++;
        sending = 0;
        dialog?.Hide();
    }

    /// <summary>What shows and what can be used, then the drop-down from the state.</summary>
    private void Show()
    {
        if (device is not { } shown) return;
        bool any = shown.Looks is { Count: > 0 };
        LightingPage.Show(box, any);
        LightingPage.Show(rename, any);
        LightingPage.Show(delete, any);
        LightingPage.Show(saveChanges, shown.LookChanged && shown.Look is not null);
        // Saving and choosing need a keyboard DeadWare can paint key by key; renaming and deleting don't, so a keyboard that has lost its
        // names can still tidy its looks.
        bool adds = enabled && shown.CanPaint;
        box.IsEnabled = saveChanges.IsEnabled = saveNew.IsEnabled = adds;
        Apply();
        rename.IsEnabled = delete.IsEnabled = enabled && Current is not null;
    }

    /// <summary>The drop-down as the state has it, except while a look request of the user's is on its way or a dialog is open.</summary>
    private void Apply()
    {
        if (sending > 0 || dialog is not null || device is not { } shown) return;
        string[] next = [.. shown.Looks ?? []];
        int at = Index(next, shown.Look);
        syncing = true;
        try
        {
            if (!next.SequenceEqual(names, StringComparer.Ordinal))
            {
                names = next;
                box.Items.Clear();
                foreach (string name in names) box.Items.Add(new ComboBoxItem { Content = name });
            }
            // The closed drop-down shows a copy of the chosen item's content taken as it was chosen, so a renamed chosen item is chosen again.
            bool again = false;
            for (int i = 0; i < names.Length; i++)
            {
                string text = i == at && shown.LookChanged ? $"{names[i]} (changed)" : names[i];
                if (box.Items[i] is not ComboBoxItem item || Equals(item.Content, text)) continue;
                item.Content = text;
                again |= i == box.SelectedIndex;
            }
            if (again) box.SelectedIndex = -1;
            if (box.SelectedIndex != at) box.SelectedIndex = at;
        }
        finally { syncing = false; }
    }

    /// <summary>The user chose a look: it goes at once, or after Switch when the keyboard's setup is in no look; Cancel puts the drop-down back.</summary>
    private async void Pick()
    {
        int at = box.SelectedIndex;
        if (syncing || device is not { } shown || at < 0 || at >= names.Length) return;
        string name = names[at];
        if (Same(name, shown.Look)) return;
        if (!enabled || !shown.CanPaint || dialog is not null)
        {
            Apply();
            return;
        }
        if (LookNames.Unsaved(shown))
        {
            int asked = ticket;
            bool yes = await Ask(new ContentDialog
            {
                Content = Sentence(SwitchText), PrimaryButtonText = "Switch", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            });
            // Not if the control was disabled or reset meanwhile, or the look has gone.
            if (!yes || asked != ticket || !enabled || device is not { CanPaint: true } now || Index(now.Looks, name) < 0)
            {
                Apply();
                return;
            }
        }
        Request(new ChooseLookRequest(Key, name), now => Same(now.Look, name), chosen);
    }

    /// <summary>Saves the keyboard as it is now over the current look.</summary>
    private void SaveChanges()
    {
        if (!enabled || device is not { CanPaint: true } || Current is not { } name) return;
        Request(new SaveLookRequest(Key, name, Overwrite: true), now => !now.LookChanged);
    }

    /// <summary>Asks for a name, then saves the keyboard as it is now as a new look, which becomes the current one.</summary>
    private async void SaveNew()
    {
        if (!enabled || device is not { CanPaint: true } || dialog is not null) return;
        int asked = ticket;
        string? name = await AskName("Save as a new look", "Save", null);
        if (name is null || asked != ticket || !enabled)
        {
            Show();
            return;
        }
        Request(new SaveLookRequest(Key, name), now => Same(now.Look, name));
    }

    /// <summary>Asks for a new name for the current look, which may be its own in another case.</summary>
    private async void Rename()
    {
        if (!enabled || Current is not { } old || dialog is not null) return;
        int asked = ticket;
        string? name = await AskName("Rename", "Rename", old);
        if (name is null || asked != ticket || !enabled || name == old)
        {
            Show();
            return;
        }
        Request(new RenameLookRequest(Key, old, name), now => Same(now.Look, name));
    }

    /// <summary>Asks first, then deletes the current look; the keyboard's lights stay as they are.</summary>
    private async void Delete()
    {
        if (!enabled || Current is not { } name || dialog is not null) return;
        int asked = ticket;
        bool yes = await Ask(new ContentDialog
        {
            Content = Sentence($"Delete the look {name}? The keyboard's lights stay as they are."),
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
        });
        if (!yes || asked != ticket || !enabled)
        {
            Show();
            return;
        }
        Request(new DeleteLookRequest(Key, name), now => Index(now.Looks, name) < 0);
    }

    /// <summary>
    /// Sends a look request, holding the drop-down until the reply. A refusal shows the state again at once; an accepted request shows it
    /// as soon as a state carries it (<paramref name="landed"/>), since the state follows its reply.
    /// </summary>
    private void Request(HostMessage request, Func<DeviceView, bool> landed, Action? ok = null)
    {
        before?.Invoke();
        int sent = ticket;
        sending++;
        send(request, reply =>
        {
            if (sent != ticket) return;
            sending = Math.Max(0, sending - 1);
            if (reply.Ok) ok?.Invoke();
            if (!reply.Ok || device is { } now && landed(now)) Show();
        });
    }

    /// <summary>
    /// A dialog with a text box for a look's name: its primary button works only while <see cref="LookNames.Problem"/> finds nothing, and
    /// its sentence shows under the box. The name trimmed, or null when cancelled.
    /// </summary>
    private async Task<string?> AskName(string title, string primary, string? renaming)
    {
        var text = new TextBox { Text = renaming ?? "" };
        AutomationProperties.SetName(text, title);
        var problem = LightingPage.Muted("");
        var asking = new ContentDialog
        {
            Title = title, Content = new StackPanel { Spacing = 6, MinWidth = 320, Children = { text, problem } },
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        void Check()
        {
            string? why = LookNames.Problem(text.Text, device?.Looks ?? [], renaming);
            problem.Text = why ?? "";
            LightingPage.Show(problem, why is not null);
            asking.IsPrimaryButtonEnabled = why is null;
        }
        text.TextChanged += (_, _) => Check();
        asking.Opened += (_, _) =>
        {
            text.Focus(FocusState.Programmatic);
            text.SelectAll();
        };
        Check();
        return await Ask(asking) ? text.Text.Trim() : null;
    }

    /// <summary>Shows a dialog on the control's window (one at a time); true when its primary button was pressed.</summary>
    private async Task<bool> Ask(ContentDialog next)
    {
        if (dialog is not null || Root.XamlRoot is null) return false;
        next.XamlRoot = Root.XamlRoot;
        dialog = next;
        try { return await next.ShowAsync() == ContentDialogResult.Primary; }
        catch (Exception error)
        {
            App.Note($"Saved looks dialog failed: {error.Message}");
            return false;
        }
        finally
        {
            if (ReferenceEquals(dialog, next)) dialog = null;
        }
    }

    private static TextBlock Sentence(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>Where a look's name is in a list, ignoring case as the host does; -1 when it isn't or is null.</summary>
    private static int Index(IReadOnlyList<string>? list, string? name)
    {
        if (list is null || name is null) return -1;
        for (int i = 0; i < list.Count; i++) if (Same(list[i], name)) return i;
        return -1;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
