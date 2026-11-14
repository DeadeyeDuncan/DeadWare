using DeadWare.App;
using DeadWare.UpdateCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>
/// The installed and published versions, checking, Update &amp; restart and rollback with their progress and status,
/// and the release notes or their history. Update and rollback enable themselves only for a verified installation,
/// which a development build of this app is not.
/// </summary>
internal sealed class UpdatesPage : Page
{
    private readonly UpdateService service;
    private readonly Func<Task> quit;
    private readonly TextBlock installed = new() { FontSize = 24 }, latest = new() { FontSize = 16 }, last = new() { Opacity = 0.7 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap }, local = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 }, notesStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock notesHeading = new() { Text = "Release notes", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel noteSections = new() { Spacing = 12 };
    private readonly Button check = new() { Content = "Check for updates" }, update = new() { Content = "Update & restart" }, rollback = new() { Content = "Roll back & restart" }, cancel = new() { Content = "Cancel" };
    private readonly Button history = new() { Content = "Show release note history", HorizontalAlignment = HorizontalAlignment.Right }, retryHistory = new() { Content = "Retry history" };
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 100 };
    private bool showHistory, displayedHistory, displayedHistoryLoaded;
    private IReadOnlyList<ReleaseNote>? displayedNotes;
    private ReleaseInfo? displayedRelease;
    private CancellationTokenSource? operation;

    public UpdatesPage(MonitorSession session, Func<Task> quit)
    {
        service = session.Updates;
        this.quit = quit;
        check.Click += async (_, _) => await RunAsync(token => service.CheckAsync(token));
        update.Click += async (_, _) => await InstallAsync(false);
        rollback.Click += async (_, _) => await InstallAsync(true);
        cancel.Click += (_, _) => operation?.Cancel();
        history.Click += async (_, _) => await ToggleHistoryAsync();
        retryHistory.Click += async (_, _) => await RunAsync(service.ReadHistoryAsync);

        // The version lines, the actions, progress while busy, then the operation status and the installation status, so a failure shows beside the button that caused it.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, update, rollback, cancel } };
        var controls = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { installed, latest, last, buttons, progress, status, local } });

        var notesHeader = new Grid();
        notesHeader.Children.Add(notesHeading);
        notesHeader.Children.Add(history);
        var releaseNotes = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { notesHeader, notesStatus, retryHistory, noteSections } });

        var information = new StackPanel { Spacing = 8 };
        information.Children.Add(new TextBlock
        {
            Text = "DeadWare checks normal GitHub releases once at startup. A notice lets you know when a newer version is available; you choose when to download and install it. Update & restart saves your workspace and history, installs the verified package, then reopens DeadWare. Rollback restores the previous app files while keeping current settings and recordings.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        information.Children.Add(new TextBlock
        {
            Text = "The first updater-enabled installation may have no earlier version to restore. A previous version becomes available after an in-app update.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
        });
        var about = new Expander { Header = "How updates and rollback work", Content = information, HorizontalAlignment = HorizontalAlignment.Stretch };

        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { controls, releaseNotes, about } } };
        this.WhileLoaded(async () =>
        {
            service.Changed += Changed;
            ApplyState(service.State);
            try { await service.ReadLocalAsync(); } catch (Exception error) { service.ReportFailure(error); }
        },
        () => service.Changed -= Changed);
    }

    /// <summary>The service raises its change from whichever thread finished the work.</summary>
    private void Changed()
    {
        if (DispatcherQueue.HasThreadAccess) ApplyState(service.State);
        else DispatcherQueue.TryEnqueue(() => ApplyState(service.State));
    }

    private void ApplyState(UpdateState state)
    {
        installed.Text = "Installed version " + state.InstalledVersion;
        latest.Text = state.Release is null ? "Normal GitHub releases" : "Published version " + state.Release.Version;
        last.Text = state.LastChecked is { } checkedAt ? $"Last checked {checkedAt:g}" : "Checks once at startup. You can also check here.";
        status.Text = state.Status;
        status.Visibility = state.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        local.Text = state.LocalStatus + (state.LastOperation.Length > 0 ? "\nLast operation: " + state.LastOperation : "");
        local.Visibility = local.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyReleaseNotes(state);
        check.IsEnabled = !state.Busy;
        update.IsEnabled = state.CanUpdate;
        rollback.IsEnabled = state.CanRollback;
        history.IsEnabled = retryHistory.IsEnabled = !state.Busy;
        history.Content = showHistory ? "Hide release note history" : "Show release note history";
        retryHistory.Visibility = showHistory && !state.Busy && state.ReleaseHistoryStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        rollback.Content = state.Previous is { } prior ? $"Roll back to {prior.Version} & restart" : "Roll back & restart";
        cancel.IsEnabled = state.CanCancel;
        cancel.Visibility = progress.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        progress.Value = state.Progress;
        progress.IsIndeterminate = state.Busy && state.Progress == 0;
    }

    private void ApplyReleaseNotes(UpdateState state)
    {
        notesHeading.Text = showHistory ? "Release note history" : "Release notes";
        notesStatus.Text = showHistory ? state.ReleaseHistoryStatus : state.ReleaseNotesStatus;
        notesStatus.Visibility = notesStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var notes = showHistory ? state.ReleaseHistory : state.ReleaseNotes;
        var selectedRelease = showHistory ? null : state.Release;
        // Rebuild the sections only when what they show changed, so a progress tick does not reset scrolling.
        if (ReferenceEquals(displayedNotes, notes) && displayedRelease == selectedRelease && displayedHistory == showHistory && displayedHistoryLoaded == state.ReleaseHistoryLoaded) return;
        displayedNotes = notes;
        displayedRelease = selectedRelease;
        displayedHistory = showHistory;
        displayedHistoryLoaded = state.ReleaseHistoryLoaded;
        noteSections.Children.Clear();
        IReadOnlyList<ReleaseNote> entries = notes.Count > 0 ? notes : selectedRelease is { } release ? [new(release.Version, release.Tag, release.Notes)] : [];
        if (entries.Count == 0)
        {
            if (!showHistory) noteSections.Children.Add(new TextBlock { Text = "Release notes appear after a published release is found." });
            else if (state.ReleaseHistoryLoaded) noteSections.Children.Add(new TextBlock { Text = "No normal release notes have been published." });
            return;
        }
        foreach (var entry in entries)
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock { Text = entry.Tag, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            section.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(entry.Notes) ? "No release notes provided." : entry.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            noteSections.Children.Add(section);
        }
    }

    private async Task ToggleHistoryAsync()
    {
        showHistory = !showHistory;
        ApplyState(service.State);
        if (showHistory) await RunAsync(service.ReadHistoryAsync);
    }

    /// <summary>One operation at a time; a failure is reported through the service so it shows in the status line.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        try { await action(cancellation.Token); }
        catch (Exception error) { service.ReportFailure(error); }
        finally { operation = null; }
    }

    private Task InstallAsync(bool restore) => RunAsync(async token =>
    {
        var job = await service.PrepareAsync(restore, token);
        if (job is not null && await service.StartHelperAsync(job, token)) await quit();
    });
}
