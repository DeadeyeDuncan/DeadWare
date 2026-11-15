using System.Globalization;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// Usage history's Application resources: the busiest apps' saved CPU, memory, I/O and GPU for the page's period, from
/// process-history.db, or for one Device-chart slot while <see cref="ShowSlot"/> holds it. Reads run off the UI thread and only the
/// latest request is shown; the search filters the rows already read, and Rank by and row changes wait a moment so stepping through
/// them starts one read.
/// </summary>
internal sealed class ApplicationResourcesView : UserControl
{
    private static readonly (string Label, ProcessRank Rank)[] Ranks =
        [("CPU time", ProcessRank.CpuTime), ("Average memory", ProcessRank.AverageMemory), ("I/O", ProcessRank.Io), ("GPU", ProcessRank.Gpu)];
    private const string Title = "Application resources", LoadingText = "Loading application resources…";
    private const string NotKept = "Application resources are kept for 30 days; earlier minutes are not kept.";
    private const string EverythingElseKey = "\0";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);
    // Share-bar colours follow the graph colours; Everything else is always muted.
    private static readonly string[] Palette = ["cpu", "memory", "gpu", "upload", "diskread", "cputemp", "gputemp", "diskwrite"];

    private readonly MonitorSession session;
    private readonly TextBlock heading = new() { Text = Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
    private readonly Button clear = new() { Content = "Clear", Visibility = Visibility.Collapsed };
    private readonly ComboBox rank = new() { MinWidth = 160 };
    private readonly TextBox search = new() { Width = 220, PlaceholderText = "Find application" };
    private readonly Grid shareBar = new() { Height = 14, Visibility = Visibility.Collapsed };
    private readonly PickTable table;
    private readonly TextBlock summary = Muted(LoadingText), retention = Muted(""), gapNotice = Muted(""), chartHint = Muted("");
    private readonly TextBlock chartTitle = new() { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
    private readonly TextBlock status = Muted(""), storage = Muted("");
    private readonly StackPanel chartPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly QueueTimer rankDelay, seriesDelay;
    private (DateTimeOffset From, DateTimeOffset To)? period;
    // The Device-chart slot asked for, and the one the shown view was read for; null means the period.
    private (DateTimeOffset From, DateTimeOffset To)? slot, shownSlot;
    // True while the slot asked for, and the one the shown view was read for, came from the GPU chart, which lists it ranked by GPU.
    private bool slotFromGpu, shownFromGpu;
    private (string App, ProcessRank Rank, DateTimeOffset From, DateTimeOffset To)? charted;
    // The CPU chart's point count, so the selected app's series has the Device charts' bucket grid; 600 until the page says.
    private int points = 600;
    // The retention cutoff when the shown view was applied: minutes before it are never kept, so they are neither recorded nor gaps.
    private DateTimeOffset retainedFrom = DateTimeOffset.MinValue;
    // Loading or failure text shown in place of the summary until the next view arrives.
    private string? pending = LoadingText;
    private ProcessResourceView? view;
    private ProcessRank shownRank;
    private string? selectedApp;
    private int request, seriesRequest;
    private bool syncing, rankPending, loading, loaded;

    public ApplicationResourcesView(MonitorSession session)
    {
        this.session = session;
        rankDelay = new QueueTimer(DispatcherQueue) { Interval = Settle };
        seriesDelay = new QueueTimer(DispatcherQueue) { Interval = Settle };
        Chart = new UsageChartView(session) { Height = 170, TenthsBelowTenPercent = true };
        table = new PickTable(
        [
            new("Application", 2.4), new("CPU time", 1.1, true), new("Avg CPU", 0.9, true), new("Avg memory while listed", 1.5, true),
            new("Avg GPU", 0.9, true), new("Avg GPU memory while listed", 1.7, true), new("I/O read", 1, true), new("I/O written", 1, true),
            new("Minutes listed", 1, true),
        ], "Application resources") { MaxHeight = 420 };

        // While a slot is listed the heading names it and Clear returns to the period ranking.
        AutomationProperties.SetName(clear, "Clear the selected moment");
        clear.Click += (_, _) => ClearSlot();
        foreach (var (label, _) in Ranks) rank.Items.Add(label);
        rank.SelectedIndex = 0;
        AutomationProperties.SetName(rank, "Rank application resources by");
        AutomationProperties.SetName(search, "Find application in application resources");
        AutomationProperties.SetName(shareBar, "Share of period total");
        var rankLabel = Muted("Rank by"); rankLabel.VerticalAlignment = VerticalAlignment.Center;
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { rankLabel, rank, search } };
        chartPanel.Children.Add(chartTitle); chartPanel.Children.Add(Chart);
        Note(retention, ""); Note(gapNotice, ""); Note(chartHint, "");
        gapNotice.Opacity = 1;
        gapNotice.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { heading, clear } },
                controls, shareBar, summary, retention, gapNotice, table, chartHint, chartPanel,
                Muted("CPU time, I/O read and I/O written are summed over the minutes each application was listed. Avg CPU is its share of all logical processors across every recorded minute; Avg memory while listed averages its private working set over the minutes it was listed. Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data. Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest, including apps in Don't record these apps. Usage after the last scan that saw a process is not counted, nor is a process that starts and exits between scans."),
                status, storage,
            },
        };

        rank.SelectionChanged += (_, _) => { if (!syncing) RankChanged(); };
        rankDelay.Tick += () => { rankDelay.Stop(); _ = ReloadAsync(); };
        seriesDelay.Tick += () => { seriesDelay.Stop(); ReadSeries(); };
        search.TextChanged += (_, _) => RefreshRows();
        table.SelectionChanged += key =>
        {
            selectedApp = key is null ? null : key == EverythingElseKey ? ProcessHistory.EverythingElse : key;
            ShowSeries();
        };
        // The tab unloads its content. Keep stopped debounce work until the view is shown again; reads already in flight finish normally.
        this.WhileLoaded(() =>
        {
            loaded = true;
            if (rankPending && loading) { rankDelay.Stop(); rankDelay.Start(); }
            if (selectedApp is not null && charted is null) ShowSeries();
        },
        () =>
        {
            loaded = false;
            rankPending |= rankDelay.IsEnabled; rankDelay.Stop();
            if (seriesDelay.IsEnabled) { seriesDelay.Stop(); charted = null; }
        });
    }

    /// <summary>The selected application's chart over the page's period.</summary>
    public UsageChartView Chart { get; }

    /// <summary>The Device-chart slot being listed, or null while the view shows the period ranking.</summary>
    public (DateTimeOffset From, DateTimeOffset To)? Slot => slot;

    /// <summary>Raised by <see cref="ClearSlot"/>, so the page can clear the Device charts' selection.</summary>
    public event Action? SlotCleared;

    private ProcessRank Rank => rank.SelectedIndex >= 0 ? Ranks[rank.SelectedIndex].Rank : ProcessRank.CpuTime;
    private bool WithDate => period is { } shown && shown.To - shown.From > TimeSpan.FromDays(1);

    private static TextBlock Muted(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };

    private static void Note(TextBlock note, string text) { note.Text = text; note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    /// <summary>Reads the ranking for the page's period. A later call, or a Rank by change, makes an earlier read stale and its result is
    /// dropped. The rows on screen stay until the new ones arrive. <paramref name="devicePoints"/> is the CPU chart's point count.</summary>
    public void Load(DateTimeOffset from, DateTimeOffset to, int devicePoints)
    {
        period = (from, to); points = Math.Clamp(devicePoints, 1, ProcessHistoryStore.MaximumRows);
        rankDelay.Stop(); _ = ReloadAsync();
    }

    /// <summary>Shows the history's current status (recording, paused or failed) without reading the file.</summary>
    public void RefreshStatus() { if (session.ProcessHistory is { } service) status.Text = service.Status; }

    /// <summary>Lists the busiest apps whose minutes start in the slot, ranked by <paramref name="ranking"/>, until <see cref="ClearSlot"/>.</summary>
    public void ShowSlot(DateTimeOffset slotStart, DateTimeOffset slotEnd, ProcessRank ranking)
    {
        slot = (slotStart, slotEnd); slotFromGpu = ranking == ProcessRank.Gpu;
        // Until the read returns the heading names every minute that starts in the slot.
        heading.Text = UsagePresenter.SlotHeading(TrafficStrips.CeilingMinute(slotStart), TrafficStrips.CeilingMinute(slotEnd), WithDate);
        clear.Visibility = Visibility.Visible;
        syncing = true;
        try { rank.SelectedIndex = Array.FindIndex(Ranks, choice => choice.Rank == ranking); }
        finally { syncing = false; }
        // A new Rank by switches the selected app's chart at once; the slot does not wait for Rank by to settle.
        ShowSeries();
        rankDelay.Stop(); _ = ReloadAsync();
    }

    /// <summary>Returns to the period ranking (Esc or Clear) and raises <see cref="SlotCleared"/>. The period is read once the choice
    /// settles, so a range change that follows reads only its own period.</summary>
    public void ClearSlot()
    {
        bool listed = slot is not null;
        slot = null; slotFromGpu = false; heading.Text = Title; clear.Visibility = Visibility.Collapsed;
        SlotCleared?.Invoke();
        if (listed) ReadWhenSettled();
    }

    private void RankChanged()
    {
        // The chart switches unit at once; both reads wait until Rank by settles.
        ShowSeries();
        if ((slot ?? period) is null) return;
        ReadWhenSettled();
    }

    /// <summary>Drops a read already in flight and reads again once the choice settles.</summary>
    private void ReadWhenSettled()
    {
        request++; loading = true; pending = LoadingText; summary.Text = pending;
        rankDelay.Stop();
        if (loaded) rankDelay.Start(); else rankPending = true;
    }

    private async Task ReloadAsync()
    {
        rankPending = false;
        if ((slot ?? period) is not { } range) return;
        int current = ++request; var ranking = Rank;
        if (session.ProcessHistory is not { } service) { loading = false; Unavailable("Application resource history is not available."); return; }
        // The page's one-minute refresh keeps the numbers on screen; a new range or ranking says it is loading.
        if (!Shows(range.From, range.To, ranking)) { pending = LoadingText; summary.Text = pending; }
        loading = true; status.Text = service.Status;
        ProcessResourceView? read; string reason = "Application resources could not be read.";
        try { read = await service.TryReadApplicationsAsync(range.From, range.To, ranking); }
        catch (Exception error) { read = null; reason = $"Application resources could not be read ({error.GetType().Name})."; }
        if (current != request) return;
        loading = false; status.Text = service.Status;
        // An unreadable file is reported, never shown as a period with nothing recorded.
        if (read is null) Unavailable(reason);
        else Apply(read, DateTimeOffset.UtcNow);
    }

    /// <summary>Clears the rows, share bar, gap notice, storage line and chart, and gives <paramref name="reason"/> instead.</summary>
    private void Unavailable(string reason)
    {
        view = null; selectedApp = null; pending = reason; summary.Text = reason;
        table.SetRows([], null);
        shareBar.Children.Clear(); shareBar.ColumnDefinitions.Clear(); shareBar.Visibility = Visibility.Collapsed;
        Note(retention, ""); Note(gapNotice, ""); storage.Text = ""; table.Visibility = Visibility.Visible;
        ShowSeries();
    }

    /// <summary>True when the view on screen already answers the request: the same slot, or the period a minute refresh moved slightly.</summary>
    private bool Shows(DateTimeOffset from, DateTimeOffset to, ProcessRank ranking) => view is { } shown && shownRank == ranking && shownSlot == slot
        && shown.To - shown.From == to - from && (slot is null ? (from - shown.From).Duration() < TimeSpan.FromMinutes(2) : shown.From == from);

    /// <summary>How long a minute can take to reach the file after it ends.</summary>
    private TimeSpan SaveDelay => ProcessHistoryService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds);

    private void Apply(ProcessResourceView read, DateTimeOffset now)
    {
        // Another slot, Rank by or period is a new ranking, so the table starts at its top; the minute refresh keeps the scroll offset.
        bool newRanking = !Shows(read.From, read.To, Rank);
        view = read; shownRank = Rank; shownSlot = slot; shownFromGpu = slotFromGpu; pending = null;
        // The heading names the minutes listed, from the first one's start to the last one's end.
        if (shownSlot is { } listed)
            heading.Text = read is { FirstMinute: { } first, LastMinuteEnd: { } end } ? UsagePresenter.SlotHeading(first, end, WithDate)
                : UsagePresenter.SlotHeading(TrafficStrips.CeilingMinute(listed.From), TrafficStrips.CeilingMinute(listed.To), WithDate);
        // The chart follows a listed application; one missing from the new period or slot has nothing to show.
        if (selectedApp is { } app && (read.RecordedMinutes == 0 || (app != ProcessHistory.EverythingElse
            && !read.Rows.Any(r => r.Name.Equals(app, StringComparison.OrdinalIgnoreCase))))) selectedApp = null;
        // A minute that has just ended may not be saved yet, so it is not a gap until it has had time to arrive. Minutes before the
        // 30-day retention cutoff are never kept, so they are not gaps either. The notice describes the period, so a slot leaves it out.
        retainedFrom = ProcessHistoryStore.RetentionCutoff(now);
        long gaps = shownSlot is null ? UsagePresenter.ElapsedMinutes(read.From > retainedFrom ? read.From : retainedFrom, read.To, now - SaveDelay) - read.RecordedMinutes : 0;
        Note(gapNotice, gaps > 0 ? $"{gaps:N0} minutes in this period have no process data (monitoring off, DeadWare not running, or a capture gap)." : "");
        storage.Text = $"process-history.db · {HistoryChartModel.Bytes(read.StorageBytes)} · GPU recorded for {UsagePresenter.Duration(read.GpuCoveredSeconds)} of this range";
        RefreshRows();
        if (newRanking) table.ScrollToTop();
        ShowSeries();
    }

    private void RefreshRows()
    {
        if (view is not { } shown) return;
        string query = search.Text.Trim();
        var listed = new List<ProcessResourceRow>();
        if (shown.RecordedMinutes > 0)
        {
            listed.AddRange(shown.Rows.Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            // Everything else always stays, whatever the search.
            listed.Add(shown.EverythingElse);
        }
        // A search that hides the selected row does not clear the selection: the row is selected again when it comes back.
        string? selectedKey = selectedApp is null ? null
            : selectedApp == ProcessHistory.EverythingElse ? EverythingElseKey
            : listed.FirstOrDefault(r => !r.EverythingElse && r.Name.Equals(selectedApp, StringComparison.OrdinalIgnoreCase))?.Name;
        table.SetRows(listed.Select(r => new TableRow(r.EverythingElse ? EverythingElseKey : r.Name, UsagePresenter.ResourceCells(r),
            UsagePresenter.ResourceTip(r, shown.GpuCoveredSeconds))).ToArray(), selectedKey);
        // A slot without process data says so instead of showing an empty table, and so does a GPU chart slot without GPU data.
        bool emptySlot = shownSlot is not null && shown.RecordedMinutes == 0, noGpu = NoGpuData(shown);
        // A period that starts before the retention cutoff says its earlier minutes are not kept; wholly before it, that is the summary.
        bool expired = shownSlot is null && shown.RecordedMinutes == 0 && shown.To <= retainedFrom;
        Note(retention, shownSlot is null && shown.From < retainedFrom && !expired ? NotKept : "");
        // The read returns the top rows; the period may hold more applications than that.
        bool more = shown.ApplicationCount > shown.Rows.Count;
        table.Visibility = emptySlot || noGpu ? Visibility.Collapsed : Visibility.Visible;
        summary.Text = pending ?? (emptySlot ? "No process data in this span."
            : noGpu ? "No GPU data in this span."
            : expired ? NotKept
            : shown.RecordedMinutes == 0 ? "No application resources were recorded in this period."
            : query.Length > 0 ? $"{listed.Count - 1:N0} of {(more ? "the top " : "")}{shown.Rows.Count:N0} applications match."
            : $"{(more ? $"Top {shown.Rows.Count:N0} of {shown.ApplicationCount:N0}" : $"{shown.Rows.Count:N0}")} applications · {shown.RecordedMinutes:N0} recorded minutes");
        DrawShares(shown, listed);
    }

    /// <summary>True when the shown slot was listed from the GPU chart and is ranked by GPU, and its recorded minutes hold no GPU-covered time.</summary>
    private bool NoGpuData(ProcessResourceView shown)
        => shownFromGpu && shownSlot is not null && shownRank == ProcessRank.Gpu && shown.RecordedMinutes > 0 && shown.GpuCoveredSeconds == 0;

    private void DrawShares(ProcessResourceView shown, IReadOnlyList<ProcessResourceRow> listed)
    {
        shareBar.Children.Clear(); shareBar.ColumnDefinitions.Clear();
        // The bar and its segments name the same whole: the period's, or the listed slot's.
        AutomationProperties.SetName(shareBar, shownSlot is null ? "Share of period total" : "Share of this span's total");
        double total = shown.RankTotal;
        bool visible = total > 0 && listed.Count > 0;
        shareBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // Colours follow the unfiltered ranking, so a row keeps its colour while the search changes.
        var order = new Dictionary<ProcessResourceRow, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < shown.Rows.Count; i++) order[shown.Rows[i]] = i;
        byte c = ActualTheme == ElementTheme.Dark ? (byte)255 : (byte)0;
        Color muted = Color.FromArgb(110, c, c, c);
        double drawn = 0;
        string whole = shownSlot is null ? "the period total" : "this span's total";
        for (int i = 0; i < listed.Count; i++)
        {
            var row = listed[i]; double part = Math.Max(0, row.PartOf(shownRank)); drawn += part;
            shareBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(part, GridUnitType.Star) });
            int position = order.GetValueOrDefault(row);
            Color color = row.EverythingElse ? muted : UsageColors.Of(session, Palette[position % Palette.Length]);
            // Past the palette's end the colours repeat, a little fainter each time round.
            if (!row.EverythingElse && position >= Palette.Length) color = Color.FromArgb((byte)Math.Max(90, 255 - 50 * (position / Palette.Length)), color.R, color.G, color.B);
            var piece = new Rectangle { Fill = new SolidColorBrush(color) };
            ToolTipService.SetToolTip(piece, string.Create(CultureInfo.CurrentCulture, $"{row.Name}: {100 * part / total:0.#} % of {whole}"));
            Grid.SetColumn(piece, i); shareBar.Children.Add(piece);
        }
        // Rows the search hides, or beyond the rows read, leave their part empty, so each segment stays a share of the whole.
        if (total > drawn) shareBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total - drawn, GridUnitType.Star) });
    }

    /// <summary>Charts the selected application over the shown period with the Rank by metric; changing either redraws it. While a slot is
    /// listed the chart still covers the page's period.</summary>
    private void ShowSeries()
    {
        var shown = view;
        if (selectedApp is not { } app || shown is null)
        {
            charted = null; seriesRequest++; seriesDelay.Stop(); chartPanel.Visibility = Visibility.Collapsed;
            // A slot without process data, or a GPU chart slot without GPU data, shows no table to select from.
            Note(chartHint, shown is { RecordedMinutes: > 0 } && !NoGpuData(shown) ? "Select an application to chart it over this period." : "");
            return;
        }
        Note(chartHint, ""); chartPanel.Visibility = Visibility.Visible;
        var metric = Rank;
        var (from, to) = shownSlot is not null && period is { } whole ? whole : (shown.From, shown.To);
        if (charted == (app, metric, from, to)) return;
        bool redraw = charted is not { } previous || previous.App != app || previous.Rank != metric;
        charted = (app, metric, from, to); seriesRequest++;
        var (unit, maximum, title, label, _) = Metric(metric, app);
        chartTitle.Text = $"{app} · {title}";
        // A new app or metric shows an empty plot in its unit until the read returns, never the previous metric on the wrong scale;
        // a new period for the same series keeps the old line until then, so the page's minute refresh does not flicker.
        if (redraw) { Chart.SetData([], unit, maximum); AutomationProperties.SetName(Chart, $"{app} {label} history"); }
        // Stepping through rows or Rank by starts one read, once the choice settles.
        seriesDelay.Stop();
        if (loaded) seriesDelay.Start(); else charted = null;
    }

    private void ReadSeries()
    {
        if (charted is { } shown && session.ProcessHistory is { } service)
            _ = ChartAsync(service, seriesRequest, shown.App, shown.Rank, shown.From, shown.To);
    }

    private async Task ChartAsync(ProcessHistoryService service, int current, string app, ProcessRank metric, DateTimeOffset from, DateTimeOffset to)
    {
        UsageHistoryView? history;
        try { history = await service.TryReadSeriesAsync(app, metric, from, to, points); }
        catch (Exception) { history = null; }
        if (current != seriesRequest) return;
        var (unit, maximum, title, label, color) = Metric(metric, app);
        if (history is null)
        {
            // Reported like the ranking: an empty plot under a title that says so, and the service status says why.
            chartTitle.Text = $"{app} · {title} · could not be read"; status.Text = service.Status;
            Chart.SetData([], unit, maximum); AutomationProperties.SetName(Chart, $"{app} {label} history");
            return;
        }
        Chart.SetData([new UsageChartSeries(history, color, false, $"{app} {label}")], unit, maximum);
    }

    /// <summary>The chart for <paramref name="metric"/>; <paramref name="app"/> is the charted application.</summary>
    private static (string Unit, double? Maximum, string Title, string Label, string Color) Metric(ProcessRank metric, string app) => metric switch
    {
        ProcessRank.CpuTime => ("%", 100, "CPU % of all logical processors", "CPU", "cpu"),
        ProcessRank.AverageMemory => ("B", null, "memory (private working set)", "memory", "memory"),
        // Everything else's GPU is a sum over many apps and can exceed 100 %, so its scale is automatic.
        ProcessRank.Gpu => ("%", app == ProcessHistory.EverythingElse ? null : 100, "GPU % of the busiest engine", "GPU", "gpu"),
        _ => ("B/s", null, "I/O read + written per second", "I/O", "diskread"),
    };
}
