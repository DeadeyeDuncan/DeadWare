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
    private sealed class DriveCard(Border card, TextBlock title, TextBlock health, ProgressBar used, TextBlock free, Grid volumes, TextBlock noVolumes, StackPanel details)
    {
        public Border Card => card;
        public TextBlock Title => title;
        public TextBlock Health => health;
        public ProgressBar Used => used;
        public TextBlock Free => free;
        public Grid Volumes => volumes;
        public TextBlock NoVolumes => noVolumes;
        public StackPanel Details => details;
        /// <summary>The volume rows last drawn, so the grid is rebuilt only when they change.</summary>
        public IReadOnlyList<TableRow> Shown { get; set; } = [];
    }

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
        // Two drives can report the same id (the same serial number, on some virtual and USB hardware). One card per id:
        // adding the same card to the list twice is an error in XAML, and the health and chart are keyed by id anyway.
        var ordered = storage.Drives.OrderBy(drive => drive.Index).DistinctBy(drive => drive.Id).ToArray();
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
        // A plain grid, not the virtualised TableView: a drive has a handful of volumes, so there is nothing to virtualise,
        // and this avoids a list with its own scroller inside this page's scroller.
        var volumes = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        foreach (double weight in new[] { 0.6, 2, 1, 1 }) volumes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        var noVolumes = new TextBlock { Text = "No mounted volumes", Opacity = 0.7 };
        var details = new StackPanel { Spacing = 2 };
        var card = Pages.Card(new StackPanel { Spacing = 8, Padding = new Thickness(8), Children = { title, health, used, free, chart, volumes, noVolumes, details } });
        card.Tag = drive.Id;
        return new DriveCard(card, title, health, used, free, volumes, noVolumes, details);
    }

    /// <summary>Rebuilds the volume grid: a caption row, then letter, label, used and free for each volume.</summary>
    private static void ShowVolumes(DriveCard card, IReadOnlyList<TableRow> rows)
    {
        card.Shown = rows;
        card.Volumes.Children.Clear();
        card.Volumes.RowDefinitions.Clear();
        void Add(string text, int row, int column, bool caption)
        {
            var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = column >= 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            if (caption) { cell.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]; cell.Opacity = 0.7; }
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            card.Volumes.Children.Add(cell);
        }
        card.Volumes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] captions = ["LETTER", "LABEL", "USED", "FREE"];
        for (int column = 0; column < captions.Length; column++) Add(captions[column], 0, column, caption: true);
        for (int row = 0; row < rows.Count; row++)
        {
            card.Volumes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int column = 0; column < rows[row].Cells.Count; column++) Add(rows[row].Cells[column], row + 1, column, caption: false);
        }
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
        if (!rows.SequenceEqual(card.Shown)) ShowVolumes(card, rows);
        card.Volumes.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        card.NoVolumes.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var lines = StoragePresenter.Details(drive, health, status);
        while (card.Details.Children.Count > lines.Count) card.Details.Children.RemoveAt(card.Details.Children.Count - 1);
        while (card.Details.Children.Count < lines.Count) card.Details.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < lines.Count; i++) ((TextBlock)card.Details.Children[i]).Text = lines[i];
    }
}
