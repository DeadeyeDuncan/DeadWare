using System.Globalization;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>
/// App names to suggest for the Don't record list and Clear history: the current reading's apps, Usage history's names over
/// 400 days and Application resources' names over 30 days, united by <see cref="AppKey"/>.
/// </summary>
internal sealed class AppSuggestions(MonitorSession session)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private DateTimeOffset? lastRead;
    private Task? reading;
    private IReadOnlyList<string> usage = [], resources = [];

    /// <summary>Raised on the UI thread once a refresh's reads have returned.</summary>
    public event Action? Changed;

    /// <summary>Reads the two histories, at most once a minute; a refresh while one runs joins it. Call on the UI thread.</summary>
    public Task RefreshAsync()
    {
        if (reading is { IsCompleted: false }) return reading;
        var now = DateTimeOffset.UtcNow;
        if (lastRead is { } last && now >= last && now - last < Interval) return Task.CompletedTask;
        lastRead = now;
        return reading = ReadAsync(now);
    }

    private async Task ReadAsync(DateTimeOffset now)
    {
        // Awaited without ConfigureAwait(false), so the names are kept and Changed raised back on the UI thread.
        var usageRead = Task.Run<IReadOnlyList<string>>(async () =>
        {
            try { return (await session.Usage.ReadApplicationsAsync(now - TimeSpan.FromDays(400), now + TimeSpan.FromMinutes(1), 1000)).Applications.Select(a => a.Name).ToArray(); }
            catch (Exception) { return Array.Empty<string>(); }
        });
        var resourcesRead = Task.Run<IReadOnlyList<string>>(async () =>
        {
            try { return (await session.ProcessHistory.TryReadApplicationsAsync(now - TimeSpan.FromDays(30), now, ProcessRank.CpuTime, limit: 1000))?.Rows.Select(r => r.Name).ToArray() ?? Array.Empty<string>(); }
            catch (Exception) { return Array.Empty<string>(); }
        });
        usage = await usageRead; resources = await resourcesRead;
        Changed?.Invoke();
    }

    /// <summary>The united names sorted without regard to case; listed (Don't record) ones only when asked for.</summary>
    public IReadOnlyList<string> Names(bool includeListed)
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

    /// <summary>The names containing the typed text, for an AutoSuggestBox.</summary>
    public string[] Matching(string text, bool includeListed) =>
        Names(includeListed).Where(name => text.Length == 0 || name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(50).ToArray();
}

/// <summary>Clear history: the apps and range to clear, then a confirmation. Progress shows in Settings → History and alerts.</summary>
internal static class ClearHistoryDialog
{
    private const int MaximumLength = 260;

    /// <summary>Asks what to clear and to confirm it; true when a clear was sent to the stores.</summary>
    public static async Task<bool> ShowAsync(MonitorSession session, AppSuggestions suggestions, XamlRoot root)
    {
        if (session.ClearActive || session.Ending) return false;
        var apps = new RadioButtons { Header = HistoryClearCopy.AppsGroup, MaxColumns = 2, SelectedIndex = 0 };
        apps.Items.Add(HistoryClearCopy.OneApp); apps.Items.Add(HistoryClearCopy.AllApps);
        var range = new RadioButtons { Header = HistoryClearCopy.RangeGroup, MaxColumns = 2, SelectedIndex = 0 };
        range.Items.Add(HistoryClearCopy.OlderThan7Days); range.Items.Add(HistoryClearCopy.AllTime);
        var box = new AutoSuggestBox { PlaceholderText = "chrome.exe" };
        AutomationProperties.SetName(box, HistoryClearCopy.AppBoxName);
        var validation = new TextBlock { Text = HistoryClearCopy.InvalidApp, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 8, MaxWidth = 460 };
        foreach (string text in new[] { HistoryClearCopy.WindowHelp, HistoryClearCopy.WindowKept, HistoryClearCopy.WindowNotCleared, HistoryClearCopy.WindowFreedSpace, HistoryClearCopy.WindowKeepsRecording })
            body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 });
        body.Children.Add(apps); body.Children.Add(box); body.Children.Add(validation); body.Children.Add(range);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = HistoryClearCopy.WindowHeading, Content = new ScrollViewer { Content = body },
            PrimaryButtonText = HistoryClearCopy.ClearButton, CloseButtonText = HistoryClearCopy.CancelButton, DefaultButton = ContentDialogButton.Close,
        };
        bool typed = false;
        void Validate()
        {
            bool one = apps.SelectedIndex == 0;
            string text = box.Text ?? "";
            bool valid = text.Length <= MaximumLength && AppExclusions.Accepts(text);
            box.IsEnabled = one;
            dialog.IsPrimaryButtonEnabled = !one || valid;
            validation.Visibility = one && typed && text.Length > 0 && !valid ? Visibility.Visible : Visibility.Collapsed;
        }
        void Offer() => box.ItemsSource = suggestions.Matching(box.Text ?? "", includeListed: true);
        box.TextChanged += (_, args) => { if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { typed = true; Offer(); } Validate(); };
        box.GotFocus += (_, _) => Offer();
        apps.SelectionChanged += (_, _) => Validate();
        suggestions.Changed += Offer;
        Validate();
        _ = suggestions.RefreshAsync();
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { suggestions.Changed -= Offer; }
        if (result != ContentDialogResult.Primary) return false;

        string? application = apps.SelectedIndex == 0 ? box.Text ?? "" : null;
        var clear = HistoryClear.Create(application, range.SelectedIndex == 1, DateTimeOffset.UtcNow);
        string? name = application is null ? null : ProcessHistory.SavedName(application);
        var confirm = new ContentDialog
        {
            XamlRoot = root, Title = "DeadWare",
            Content = new TextBlock { Text = HistoryClearCopy.Confirmation(clear, name, TimeZoneInfo.Local, CultureInfo.CurrentCulture), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Yes", CloseButtonText = "No", DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return false;
        // Nothing is sent once Quit or sign-out began, or when another clear started meanwhile.
        if (session.Ending || session.ClearActive) return false;
        return session.ClearHistory(clear);
    }
}

/// <summary>The Clear history… button of the Usage history and Connections pages. It is disabled while a clear is active or waits for a
/// restart, with the reason as its tooltip, and opens <see cref="ClearHistoryDialog"/> over the page it sits in.</summary>
internal static class ClearHistoryEntry
{
    public static Button Create(MonitorSession session)
    {
        var button = new Button { Content = HistoryClearCopy.EntryButton, HorizontalAlignment = HorizontalAlignment.Left };
        AppSuggestions? suggestions = null;
        void Refresh()
        {
            // One at a time: Queued, Clearing, or Failed with its marker saved.
            bool waiting = session.ClearWaitingForRestart, enabled = !session.ClearActive && !waiting;
            if (button.IsEnabled != enabled) button.IsEnabled = enabled;
            ToolTipService.SetToolTip(button, enabled ? null : HistoryClearCopy.DisabledTip(waiting));
        }
        void Changed(HistoryClearStore _) => Refresh();
        button.Click += async (_, _) =>
        {
            if (session.ClearActive || session.Ending) return;
            try { await ClearHistoryDialog.ShowAsync(session, suggestions ??= new AppSuggestions(session), button.XamlRoot); }
            catch (Exception error) { App.Note($"Clear history dialog failed: {error.Message}"); }
            Refresh();
        };
        button.WhileLoaded(
            () => { session.Changed += Refresh; session.HistoryClearChanged += Changed; Refresh(); },
            () => { session.Changed -= Refresh; session.HistoryClearChanged -= Changed; });
        return button;
    }
}
