using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeadWare.WinUI;

/// <summary>
/// Applications by resource use: one row per application with its processes' totals, in the columns chosen in
/// settings, with search and click-to-sort headings. Expanding an application, the column picker and End task are
/// not ported yet.
/// </summary>
internal sealed class ProcessesPage : Page
{
    private readonly MonitorSession session;
    private readonly TextBlock banner = new() { TextWrapping = TextWrapping.Wrap }, count = new() { Opacity = 0.7 }, footer = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBox search = new() { PlaceholderText = "Search application name or PID", MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border card = Pages.Card(new TableView([new("Name", 2)]));
    private IReadOnlyList<string> shown = [];
    private TableView? table;
    private string? sortKey;
    private bool sortDescending;

    public ProcessesPage(MonitorSession session)
    {
        this.session = session;
        footer.Text = string.Join("  ", ProcessCopy.Footer(ProcessFeatures.DiskColumns));
        Content = Pages.Layout(banner, search, card, count, footer);
        search.TextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { session.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => session.Changed -= Refresh;
    }

    private void Refresh()
    {
        if (session.Current is not { } reading) return;
        bool elevated = Collector.IsElevated;
        banner.Text = ProcessCopy.Banner(elevated, ProcessFeatures.DiskColumns) ?? "";
        banner.Visibility = banner.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var visible = ProcessTableColumns.Visible(session.Settings.ProcessColumns, elevated, ProcessFeatures.DiskColumns);
        if (table is null || !visible.SequenceEqual(shown)) Rebuild(visible);
        var sort = ProcessTableColumns.EffectiveSort(sortKey, sortDescending, visible);
        table!.SetSort(ProcessTablePresenter.Keys(visible).ToList().IndexOf(sort.Key), sort.Descending);

        var activity = reading.ProcessActivity;
        if (activity.Kind == ProcessActivityKind.Off)
        {
            table.SetRows([]);
            count.Text = ProcessCopy.OffTitle;
            return;
        }
        // Only a published scan replaces the rows; a transient unavailable reading keeps the last ones on screen.
        if (activity.Kind != ProcessActivityKind.Available)
        {
            count.Text = "The process list is unavailable right now.";
            return;
        }
        var rows = ProcessTablePresenter.Rows(activity.Processes, visible, sortKey, sortDescending, search.Text);
        table.SetRows(rows);
        string apps = rows.Count == 0 ? (search.Text.Length > 0 ? "No applications match your search." : "No processes reported.") : $"{rows.Count} applications";
        string cap = activity.TotalProcesses > activity.Processes.Count ? " · " + ProcessCopy.CapNote(activity.TotalProcesses) : "";
        count.Text = apps + cap + (session.Stale ? " · " + ProcessCopy.Stale : "");
    }

    /// <summary>The chosen columns changed: build a table with those columns and carry the sort over.</summary>
    private void Rebuild(IReadOnlyList<string> visible)
    {
        shown = visible.ToList();
        var keys = ProcessTablePresenter.Keys(visible);
        var headers = ProcessTablePresenter.Headers(visible);
        table = new TableView(keys.Select((key, i) => new TableColumn(headers[i], key == "name" ? 2 : 1, Numeric: key is not ("name" or "gpuengine"))).ToList());
        table.HeaderClicked += column =>
        {
            (sortKey, sortDescending) = ProcessTablePresenter.Clicked(ProcessTableColumns.EffectiveSort(sortKey, sortDescending, shown).Key, ProcessTableColumns.EffectiveSort(sortKey, sortDescending, shown).Descending, keys[column]);
            Refresh();
        };
        card.Child = table;
    }
}
