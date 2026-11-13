using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeadWare.WinUI;

/// <summary>
/// One card per drive, in disk order: identity, health, free space, its own read and write chart, its volumes and its
/// health details. The WPF page selects one drive at a time; here every drive is on the page and it scrolls.
/// </summary>
internal sealed class StoragePage : Page
{
    private sealed record DriveCard(Border Card, TextBlock Title, TextBlock Health, ProgressBar Used, TextBlock Free, TableView Volumes, TextBlock NoVolumes, StackPanel Details);

    private readonly MonitorSession session;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 }, empty = new() { Opacity = 0.7 };
    private readonly StackPanel drives = new() { Spacing = 12 };
    private readonly Dictionary<string, DriveCard> cards = new(StringComparer.Ordinal);

    public StoragePage(MonitorSession session)
    {
        this.session = session;
        Content = new ScrollViewer { Content = new StackPanel { Padding = new Thickness(24), Spacing = 12, Children = { status, empty, drives } } };
        Loaded += (_, _) => { session.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => session.Changed -= Refresh;
    }

    private void Refresh()
    {
        var reading = session.Current;
        var storage = reading?.Storage ?? StorageReading.Empty;
        status.Text = reading is null ? "" : StoragePresenter.Status(storage.Status, storage.Health, reading.HardwareStatus);
        status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var ordered = storage.Drives.OrderBy(drive => drive.Index).ToArray();
        empty.Text = reading is null ? "Discovering drives…" : "No drives detected";
        empty.Visibility = ordered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Cards are kept per drive and updated in place; a changed set of drives rebuilds the list in disk order.
        if (!ordered.Select(drive => drive.Id).SequenceEqual(drives.Children.Select(child => (string)((FrameworkElement)child).Tag)))
        {
            foreach (string gone in cards.Keys.Except(ordered.Select(drive => drive.Id)).ToArray()) cards.Remove(gone);
            drives.Children.Clear();
            foreach (var drive in ordered)
            {
                if (!cards.TryGetValue(drive.Id, out var card)) cards[drive.Id] = card = Build(drive);
                drives.Children.Add(card.Card);
            }
        }
        foreach (var drive in ordered)
            Update(cards[drive.Id], drive, storage.Health.FirstOrDefault(health => health.DriveId == drive.Id), status.Text);
    }

    private DriveCard Build(StorageDrive drive)
    {
        var title = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        var health = new TextBlock();
        var used = new ProgressBar { Maximum = 1 };
        var free = new TextBlock { Opacity = 0.7 };
        var chart = new HistoryChartView(session, "storage") { DriveId = drive.Id, Height = 200 };
        var volumes = new TableView([new("Letter", 0.6), new("Label", 2), new("Used", 1, true), new("Free", 1, true)]);
        var noVolumes = new TextBlock { Text = "No mounted volumes", Opacity = 0.7 };
        var details = new StackPanel { Spacing = 2 };
        var card = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { title, health, used, free, chart, volumes, noVolumes, details } });
        card.Tag = drive.Id;
        return new DriveCard(card, title, health, used, free, volumes, noVolumes, details);
    }

    private static void Update(DriveCard card, StorageDrive drive, StorageHealth? health, string status)
    {
        card.Title.Text = StoragePresenter.Title(drive);
        card.Health.Text = StoragePresenter.HealthLine(health);
        if (StoragePresenter.Failing(health)) card.Health.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        else card.Health.ClearValue(TextBlock.ForegroundProperty);
        var (freeText, usedFraction) = StoragePresenter.Free(drive);
        card.Free.Text = freeText;
        card.Used.Value = usedFraction;

        var rows = StoragePresenter.Volumes(drive);
        card.Volumes.SetRows(rows);
        // The table sits in a stack, so it is given the height of its rows instead of filling spare space.
        card.Volumes.Height = 36 + 30 * rows.Count;
        card.Volumes.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        card.NoVolumes.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var lines = StoragePresenter.Details(drive, health, status);
        while (card.Details.Children.Count > lines.Count) card.Details.Children.RemoveAt(card.Details.Children.Count - 1);
        while (card.Details.Children.Count < lines.Count) card.Details.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < lines.Count; i++) ((TextBlock)card.Details.Children[i]).Text = lines[i];
    }
}
