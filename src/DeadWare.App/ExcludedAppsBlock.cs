using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>App names to suggest for the Don't record list and the Clear history window: the current reading's apps, Usage history's
/// names over 400 days and Application resources' names over 30 days, united by <see cref="AppKey"/>. Holds no Settings state, so each
/// user builds its own instance.</summary>
internal sealed class AppSuggestions(UiSession session, Func<DateTimeOffset> clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private DateTimeOffset? lastRead;
    private Task? reading;
    // The last reads' names; empty until they return, and for a read that failed or a service that is absent (fixture sessions).
    private IReadOnlyList<string> usage = [], resources = [];

    /// <summary>Raised on the thread that called <see cref="RefreshAsync"/> (the UI thread) once a refresh's reads have returned.</summary>
    internal event Action? Changed;

    /// <summary>Reads the two histories off the UI thread, at most once a minute; a refresh while one runs joins it.</summary>
    internal Task RefreshAsync()
    {
        if (reading is { IsCompleted: false }) return reading;
        var now = clock();
        if (lastRead is { } last && now >= last && now - last < Interval) return Task.CompletedTask;
        lastRead = now;
        return reading = ReadAsync(now);
    }

    private async Task ReadAsync(DateTimeOffset now)
    {
        // The names are kept and Changed raised on the caller's dispatcher, whatever thread the reads finish on.
        var dispatcher = Dispatcher.CurrentDispatcher;
        var usageRead = ReadUsageAsync(session.Usage, now);
        var resourcesRead = ReadResourcesAsync(session.ProcessHistory, now);
        var usageNames = await usageRead.ConfigureAwait(false);
        var resourceNames = await resourcesRead.ConfigureAwait(false);
        await dispatcher.InvokeAsync(() => { usage = usageNames; resources = resourceNames; Changed?.Invoke(); });
    }

    private static async Task<IReadOnlyList<string>> ReadUsageAsync(UsageHistoryService? service, DateTimeOffset now)
    {
        if (service is null) return [];
        try
        {
            // A failed read reports in the service's own status and returns an empty view.
            var view = await service.ReadApplicationsAsync(now - TimeSpan.FromDays(400), now + TimeSpan.FromMinutes(1), 1000).ConfigureAwait(false);
            return view.Applications.Select(application => application.Name).ToArray();
        }
        catch (ArgumentException) { return []; }
    }

    private static async Task<IReadOnlyList<string>> ReadResourcesAsync(ProcessHistoryService? service, DateTimeOffset now)
    {
        if (service is null) return [];
        try
        {
            var view = await service.TryReadApplicationsAsync(now - TimeSpan.FromDays(30), now, ProcessRank.CpuTime, limit: 1000).ConfigureAwait(false);
            return view?.Rows.Select(row => row.Name).ToArray() ?? [];
        }
        catch (ArgumentException) { return []; }
    }

    /// <summary>The union by key of the current reading's apps (its processes, then the process activity's), the Usage history names
    /// and the Application resources names, in that order; each key keeps its first source's spelling. Names
    /// <see cref="AppExclusions.Accepts"/> rejects are left out, and listed ones too unless <paramref name="includeListed"/>.
    /// Sorted <see cref="StringComparer.OrdinalIgnoreCase"/>.</summary>
    internal IReadOnlyList<string> Names(bool includeListed)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        void Offer(string? name) { if (AppExclusions.Accepts(name)) byKey.TryAdd(AppKey.Of(name), name!); }
        if (session.Current is { } current)
        {
            foreach (var process in current.Processes ?? []) Offer(process.Name);
            foreach (var process in current.ProcessActivity?.Processes ?? []) Offer(process.Name);
        }
        foreach (string name in usage) Offer(name);
        foreach (string name in resources) Offer(name);
        var listed = session.ExcludedKeys;
        return byKey.Where(pair => includeListed || !listed.Contains(pair.Key)).Select(pair => pair.Value)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>Settings → History and alerts → Don't record these apps: the add field with its suggestions, the errors and note, and the
/// list with a Remove for each app. The list is saved through <see cref="UiSession.SetExcludedApps"/>.</summary>
internal sealed class ExcludedAppsBlock : StackPanel
{
    internal const string HelpText = "These apps still show on live pages, marked \"not recorded\", but none of their history is saved: no Connections history, no per-app totals in Usage history and no alerts for them. In Application resources their CPU, memory, I/O and GPU count under Everything else, so device totals stay true. Application traffic budgets for them stop counting. History saved before you add an app is kept until you clear it. Processes that Windows saves as \"PID n\" can't be matched.";
    internal const string KeptNote = "Saved history for this app is kept. Use Clear history… to remove it.";
    private const int MaximumLength = 260;
    private readonly UiSession session;
    private readonly List<Button> removes = [];
    // The list as last rendered (sorted) and the suggestions last offered; each re-renders only when it changes, so focus survives the
    // session's once-a-second Changed.
    private string[]? shown;
    private string[] offered = [];

    internal AppSuggestions Suggestions { get; }
    internal ComboBox Box { get; } = new() { IsEditable = true, Width = 280, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    internal Button Add { get; }
    internal TextBlock Error { get; } = Ui.Text("", 12, "Warning");
    internal TextBlock Note { get; } = Ui.Text(KeptNote, 12, "Muted");
    internal StackPanel List { get; } = new() { Margin = new(0, 8, 0, 0) };

    internal ExcludedAppsBlock(UiSession session, AppSuggestions suggestions)
    {
        this.session = session; Suggestions = suggestions;
        Children.Add(Ui.Heading("Don't record these apps", 16));
        var help = Ui.Text(HelpText, 12, "Muted"); help.Margin = new(0, 0, 0, 8); Children.Add(help);
        AutomationProperties.SetName(Box, "App to stop recording");
        Box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Box.IsDropDownOpen = false; AddFromBox(); e.Handled = true;
        };
        Box.DropDownOpened += (_, _) => Offer();
        Box.Loaded += (_, _) => { if (Box.Template?.FindName("PART_EditableTextBox", Box) is TextBox text) text.MaxLength = MaximumLength; };
        Add = Ui.Button("Add", AddFromBox); Add.VerticalAlignment = VerticalAlignment.Center;
        // Tab order: the box, Add, then each row's Remove.
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Box); row.Children.Add(Add); Children.Add(row);
        Error.Visibility = Visibility.Collapsed; Error.Margin = new(0, 4, 0, 0); Children.Add(Error);
        Note.Visibility = Visibility.Collapsed; Note.Margin = new(0, 4, 0, 0); Children.Add(Note);
        Children.Add(List);
        Sync(); Offer();
        session.Changed += Sync;
        suggestions.Changed += Offer;
        Loaded += (_, _) => { Sync(); Offer(); _ = suggestions.RefreshAsync(); };
    }

    private void AddFromBox()
    {
        string text = Box.Text ?? "";
        var list = session.Settings.ExcludedApps;
        string? error = text.Length > MaximumLength || !AppExclusions.Accepts(text) ? "Enter an app's executable name, such as chrome.exe."
            : list.Exists(name => AppKey.Of(name) == AppKey.Of(text)) ? "That app is already in the list."
            : list.Count >= AppExclusions.MaximumApps ? "At most 64 apps can be listed."
            : null;
        Note.Visibility = Visibility.Collapsed;
        if (error is not null) { Error.Text = error; Error.Visibility = Visibility.Visible; return; }
        Error.Text = ""; Error.Visibility = Visibility.Collapsed;
        session.SetExcludedApps([.. list, text]);
        Box.SelectedItem = null; Box.Text = "";
        Note.Visibility = Visibility.Visible;
        Sync(); Offer();
    }

    private void Remove(string name)
    {
        int index = shown is null ? 0 : Math.Max(0, Array.IndexOf(shown, name));
        string key = AppKey.Of(name);
        Note.Visibility = Visibility.Collapsed; Error.Text = ""; Error.Visibility = Visibility.Collapsed;
        session.SetExcludedApps(session.Settings.ExcludedApps.Where(listed => AppKey.Of(listed) != key).ToList());
        Sync(); Offer();
        // Focus moves to the next row's Remove (the last row's when the removed row was last), or to the box once the list is empty.
        // Without keyboard focus (offscreen, an inactive window) the logical focus moves.
        UIElement next = removes.Count == 0 ? Box : removes[Math.Min(index, removes.Count - 1)];
        if (!next.Focus() && FocusManager.GetFocusScope(this) is { } scope) FocusManager.SetFocusedElement(scope, next);
    }

    /// <summary>Renders the saved list, sorted <see cref="StringComparer.OrdinalIgnoreCase"/>, when it differs from the rendered one.</summary>
    private void Sync()
    {
        var names = session.Settings.ExcludedApps.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (shown is not null && shown.SequenceEqual(names, StringComparer.Ordinal)) return;
        shown = names; removes.Clear(); List.Children.Clear();
        if (names.Length == 0) { List.Children.Add(Ui.Text("No apps are excluded.", 12, "Muted")); return; }
        foreach (string name in names)
        {
            var line = new DockPanel { Margin = new(0, 4, 0, 0) };
            var remove = Ui.Button("Remove", () => Remove(name)); remove.Margin = new(8, 0, 0, 0);
            AutomationProperties.SetName(remove, $"Remove {name} from Don't record these apps");
            DockPanel.SetDock(remove, Dock.Right); line.Children.Add(remove);
            line.Children.Add(Ui.Text(name, 12)); List.Children.Add(line); removes.Add(remove);
        }
    }

    /// <summary>Offers the suggestions minus the listed apps, keeping whatever is typed in the box.</summary>
    private void Offer()
    {
        var names = Suggestions.Names(includeListed: false).ToArray();
        if (names.SequenceEqual(offered, StringComparer.Ordinal)) return;
        offered = names;
        string text = Box.Text ?? "";
        Box.ItemsSource = names;
        if (Box.Text != text) Box.Text = text;
    }
}
