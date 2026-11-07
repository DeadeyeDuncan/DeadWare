using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Usage history's Application resources card: the busiest apps' saved CPU, memory, I/O and GPU for the page's period, from
/// process-history.db, or for one Device-chart slot while <see cref="ShowSlot"/> holds it. Reads run off the UI thread through
/// <see cref="ProcessHistoryService"/> and only the latest request is shown; the search filters the rows already read, and Rank by and
/// row changes wait a moment so stepping through them starts one read. <see cref="UiSession.ProcessHistory"/> may be null in fixtures.</summary>
internal sealed class ApplicationResourcesCard : StackPanel
{
    private sealed record RankChoice(string Label, ProcessRank Rank) { public override string ToString() => Label; }
    /// <summary>One application's row, kept per key and updated in place by each refresh, so the minute refresh keeps its container, the
    /// current cell, the keyboard focus and the scroll offset.</summary>
    private sealed class ResourceRow(string key, string name) : INotifyPropertyChanged
    {
        internal string Key { get; } = key;
        public string Name { get; } = name;
        public string CpuTime { get; private set; } = "";
        public string AverageCpu { get; private set; } = "";
        public string AverageMemory { get; private set; } = "";
        public string AverageGpu { get; private set; } = "";
        public string AverageGpuMemory { get; private set; } = "";
        /// <summary>The GPU cells' tooltips: the reason for a "—", and Everything else's note on a shown Avg GPU; null otherwise.</summary>
        public string? AverageGpuTip { get; private set; }
        public string? AverageGpuMemoryTip { get; private set; }
        public string IoRead { get; private set; } = "";
        public string IoWritten { get; private set; } = "";
        public string MinutesListed { get; private set; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Shows <paramref name="row"/>'s numbers, raising <see cref="PropertyChanged"/> only when a cell or a GPU tooltip changed.
        /// <paramref name="gpuCovered"/> is the view's GPU-covered seconds, which name the reason for a GPU "—".</summary>
        internal ResourceRow Update(ProcessResourceRow row, double gpuCovered)
        {
            string missing = gpuCovered > 0 ? "No GPU data in the minutes this app was listed" : "No GPU data in this range";
            // Everything else's GPU is a sum over many apps, shown unclamped.
            string? gpuTip = row.AverageGpuPercent is null ? missing : row.EverythingElse ? "Sum over many apps; can exceed 100 %" : null;
            // A tooltip can change while its cell does not (a "—" whose range gained GPU time), so the tooltips are cells here too.
            var cells = (Duration(row.CpuSeconds), Percent(row.AverageCpuPercent), Ui.Bytes(row.AverageMemoryBytes),
                row.AverageGpuPercent is { } gpu ? Percent(gpu) : "—", gpuTip, row.AverageGpuMemoryBytes is { } bytes ? Ui.Bytes(bytes) : "—",
                row.AverageGpuMemoryBytes is null ? missing : null, Ui.Bytes(row.IoRead), Ui.Bytes(row.IoWritten), row.MinutesListed.ToString("N0", CultureInfo.CurrentCulture));
            if (cells == (CpuTime, AverageCpu, AverageMemory, AverageGpu, AverageGpuTip, AverageGpuMemory, AverageGpuMemoryTip, IoRead, IoWritten, MinutesListed)) return this;
            (CpuTime, AverageCpu, AverageMemory, AverageGpu, AverageGpuTip, AverageGpuMemory, AverageGpuMemoryTip, IoRead, IoWritten, MinutesListed) = cells;
            PropertyChanged?.Invoke(this, new(string.Empty));
            return this;
        }
        private static string Percent(double value) => value.ToString("0.#", CultureInfo.CurrentCulture) + " %";

        // A row's automation name falls back to ToString().
        public override string ToString() => Name;
    }
    private const string Title = "Application resources", LoadingText = "Loading application resources…";
    private const string NotKept = "Application resources are kept for 30 days; earlier minutes are not kept.";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);
    // Share-bar colours are theme resources; Everything else is always Muted.
    private static readonly string[] Palette = ["Graph/cpu", "Graph/memory", "Graph/download", "Graph/upload", "Graph/gpu", "Graph/diskread", "Graph/cputemp", "Graph/gputemp", "Accent"];
    private readonly UiSession session;
    private readonly TextBlock heading = Ui.Heading(Title, 16);
    private readonly Button clear;
    private readonly Border frame;
    private readonly ComboBox rank = new() { MinWidth = 140 };
    private readonly TextBox search = new() { Width = 220 };
    private readonly Grid shareBar = new() { Height = 14, Margin = new(0, 6, 0, 10), Visibility = Visibility.Collapsed };
    private readonly DataGrid table = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, MaxHeight = 420,
        CanUserSortColumns = false, SelectionMode = DataGridSelectionMode.Single };
    private readonly ObservableCollection<ResourceRow> rows = [];
    private readonly TextBlock summary = Ui.Text(LoadingText, 12, "Muted"), retention = Ui.Text("", 12, "Muted"), gapNotice = Ui.Text("", 12, "Warning");
    private readonly TextBlock chartHint = Ui.Text("", 11, "Muted"), chartTitle = Ui.Text("", 13, "Heading", "HeadingFont");
    private readonly TextBlock status = Ui.Text("", 11, "Muted"), storage = Ui.Text("", 11, "Muted");
    private readonly StackPanel chart = new() { Visibility = Visibility.Collapsed };
    // Avg GPU and Avg GPU memory while listed, whose header tooltips give the shown view's GPU-covered time.
    private readonly DataGridColumn[] gpuColumns;
    private string? gpuHeaderTip;
    private readonly DispatcherTimer rankDelay = new() { Interval = Settle }, seriesDelay = new() { Interval = Settle };
    private (DateTimeOffset From, DateTimeOffset To)? period;
    // The Device-chart slot asked for, and the one the shown view was read for; null means the period.
    private (DateTimeOffset From, DateTimeOffset To)? slot, shownSlot;
    // True while the slot asked for came from the GPU chart, which lists it ranked by GPU (see NoGpuData).
    private bool slotFromGpu;
    private (string App, ProcessRank Rank, DateTimeOffset From, DateTimeOffset To)? charted;
    // The CPU chart's point count, so the selected app's series has the Device charts' bucket grid; 600 until the page says.
    private int points = 600;
    // The retention cutoff when the shown view was applied: minutes before it are never kept, so they are neither recorded nor gaps.
    private DateTimeOffset retainedFrom = DateTimeOffset.MinValue;
    // Loading or failure text shown in place of the summary until the next view arrives.
    private string? pending = LoadingText;
    private ProcessRank shownRank;
    private int request, seriesRequest;
    private bool syncing;

    public ApplicationResourcesCard(UiSession session)
    {
        this.session = session;
        // While a slot is listed the heading names it and Clear returns to the period ranking.
        clear = Ui.Button("Clear", ClearSlot); clear.Margin = new(12, 0, 0, 0); clear.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(clear, "Clear the selected moment");
        Children.Add(Ui.Row(heading, clear));
        var controls = new WrapPanel { Margin = new(0, 8, 0, 4) };
        var rankLabel = Ui.Text("Rank by", 12, "Muted"); rankLabel.Margin = new(0, 0, 8, 0); controls.Children.Add(rankLabel);
        rank.ItemsSource = new[] { new RankChoice("CPU time", ProcessRank.CpuTime), new("Average memory", ProcessRank.AverageMemory), new("I/O", ProcessRank.Io),
            new("GPU", ProcessRank.Gpu) };
        rank.SelectedIndex = 0; AutomationProperties.SetName(rank, "Rank application resources by"); controls.Children.Add(rank);
        var searchLabel = Ui.Text("Find application", 12, "Muted"); searchLabel.Margin = new(20, 0, 12, 0); controls.Children.Add(searchLabel);
        AutomationProperties.SetName(search, "Find application in application resources"); controls.Children.Add(search);
        Children.Add(controls);
        AutomationProperties.SetName(shareBar, "Share of period total"); Children.Add(shareBar);
        Children.Add(summary); Note(retention, ""); Children.Add(retention); gapNotice.Margin = new(0, 4, 0, 4); Note(gapNotice, ""); Children.Add(gapNotice);
        Column("Application", nameof(ResourceRow.Name), 2.4); Column("CPU time", nameof(ResourceRow.CpuTime), 1.1);
        Column("Avg CPU", nameof(ResourceRow.AverageCpu), 0.9); Column("Avg memory while listed", nameof(ResourceRow.AverageMemory), 1.5);
        gpuColumns = [Column("Avg GPU", nameof(ResourceRow.AverageGpu), 0.9, nameof(ResourceRow.AverageGpuTip)),
            Column("Avg GPU memory while listed", nameof(ResourceRow.AverageGpuMemory), 1.7, nameof(ResourceRow.AverageGpuMemoryTip))];
        Column("I/O read", nameof(ResourceRow.IoRead), 1); Column("I/O written", nameof(ResourceRow.IoWritten), 1); Column("Minutes listed", nameof(ResourceRow.MinutesListed), 1);
        AutomationProperties.SetName(table, "Application resources"); table.ItemsSource = rows;
        frame = Ui.Panel(table, new Thickness(0)); frame.Margin = new(0, 6, 0, 8); Children.Add(frame);
        Note(chartHint, ""); Children.Add(chartHint);
        chart.Children.Add(chartTitle); chart.Children.Add(Chart); Children.Add(chart);
        Children.Add(Ui.Text("CPU time, I/O read and I/O written are summed over the minutes each application was listed. Avg CPU is its share of all logical processors across every recorded minute; Avg memory while listed averages its private working set over the minutes it was listed. Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data. Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest. Usage after the last scan that saw a process is not counted, nor is a process that starts and exits between scans.", 11, "Muted"));
        status.Margin = new(0, 8, 0, 0); Children.Add(status); Children.Add(storage);
        rank.SelectionChanged += (_, _) => RankChanged();
        rankDelay.Tick += (_, _) => { rankDelay.Stop(); _ = ReloadAsync(); };
        seriesDelay.Tick += (_, _) => { seriesDelay.Stop(); ReadSeries(); };
        search.TextChanged += (_, _) => RefreshRows();
        table.SelectionChanged += (_, _) => { if (syncing) return; SelectedApp = (table.SelectedItem as ResourceRow)?.Name; ShowSeries(); };
        // A waiting read is dropped with the page; its next load reads again. A series that never started is read again then too.
        Unloaded += (_, _) =>
        {
            rankDelay.Stop();
            if (seriesDelay.IsEnabled) { seriesDelay.Stop(); charted = null; }
        };
    }

    internal ProcessRank Rank => (rank.SelectedItem as RankChoice)?.Rank ?? ProcessRank.CpuTime;
    /// <summary>The application whose chart is shown; it stays while a search hides its row.</summary>
    internal string? SelectedApp { get; private set; }
    internal UsageHistoryChart Chart { get; } = new() { Height = 170, Margin = new(0, 6, 0, 4), TenthsBelowTenPercent = true };
    /// <summary>The last view applied.</summary>
    internal ProcessResourceView? View { get; private set; }
    /// <summary>True while the latest ranking read is waiting or has not returned.</summary>
    internal bool Loading { get; private set; }
    /// <summary>Ranking reads that have returned, whether shown or dropped as stale.</summary>
    internal int ReadsReturned { get; private set; }

    /// <summary>Reads the ranking for the page's period off the UI thread. A later call, or a Rank by change, makes an earlier read stale and
    /// its result is dropped. The rows on screen stay until the new ones arrive. <paramref name="devicePoints"/> is the CPU chart's point
    /// count: the selected app's series is read with it (at most <see cref="ProcessHistoryStore.MaximumRows"/>), so up to a 1,000-point chart it
    /// shares that grid; a wider CPU chart has finer Device slots.</summary>
    internal void Load(DateTimeOffset from, DateTimeOffset to, int devicePoints)
    {
        period = (from, to); points = Math.Clamp(devicePoints, 1, ProcessHistoryStore.MaximumRows); rankDelay.Stop(); _ = ReloadAsync();
    }

    /// <summary>Shows the history's current status (recording, paused or failed) without reading the file, for a switch that changes it.</summary>
    internal void RefreshStatus() { if (session.ProcessHistory is { } service) status.Text = service.Status; }

    /// <summary>The Device-chart slot being listed, or null while the card shows the period ranking.</summary>
    internal (DateTimeOffset From, DateTimeOffset To)? Slot => slot;
    /// <summary>Raised by <see cref="ClearSlot"/>, so the page can clear the Device charts' selection.</summary>
    internal event Action? SlotCleared;

    /// <summary>Lists the busiest apps whose minutes start in [<paramref name="slotStart"/>, <paramref name="slotEnd"/>), ranked by
    /// <paramref name="ranking"/>, until <see cref="ClearSlot"/>. The slot is read at once; while it is shown the page's refresh reads it again.
    /// A slot ranked by GPU is the GPU chart's: while ranked by GPU, recorded minutes without GPU data say so in place of the table.</summary>
    internal void ShowSlot(DateTimeOffset slotStart, DateTimeOffset slotEnd, ProcessRank ranking)
    {
        slot = (slotStart, slotEnd); slotFromGpu = ranking == ProcessRank.Gpu;
        // Until the read returns the heading names every minute that starts in the slot.
        heading.Text = SlotHeading(CeilingMinute(slotStart), CeilingMinute(slotEnd), WithDate); clear.Visibility = Visibility.Visible;
        // A new Rank by switches the selected app's chart at once; the slot does not wait for Rank by to settle.
        rank.SelectedItem = rank.Items.Cast<RankChoice>().First(choice => choice.Rank == ranking);
        rankDelay.Stop(); _ = ReloadAsync();
    }

    /// <summary>Returns to the period ranking (Esc or Clear) and raises <see cref="SlotCleared"/>. The period is read once the choice
    /// settles, so a range change that follows reads only its own period.</summary>
    internal void ClearSlot()
    {
        bool listed = slot is not null;
        // Clear is about to collapse: if it holds the focus (a click gives it keyboard focus too), Rank by just below it takes the focus,
        // so Tab stays in the card and the page does not scroll, whichever chart listed the slot. Without keyboard focus (an inactive
        // window) the logical focus moves.
        var scope = FocusManager.GetFocusScope(clear);
        if ((clear.IsKeyboardFocused || ReferenceEquals(FocusManager.GetFocusedElement(scope), clear)) && !rank.Focus())
            FocusManager.SetFocusedElement(scope, rank);
        slot = null; slotFromGpu = false; heading.Text = Title; clear.Visibility = Visibility.Collapsed;
        SlotCleared?.Invoke();
        if (listed) ReadWhenSettled();
    }

    /// <summary>Like "Busiest apps · 03:13–03:16" in local time; <paramref name="withDate"/> (Week and Month) adds the date, on the end too
    /// when the span crosses midnight.</summary>
    internal static string SlotHeading(DateTimeOffset first, DateTimeOffset lastEnd, bool withDate)
    {
        DateTime start = first.ToLocalTime().DateTime, end = lastEnd.ToLocalTime().DateTime;
        static string Format(DateTime time, bool date) => time.ToString(date ? "MMM d HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
        return $"Busiest apps · {Format(start, withDate)}–{Format(end, withDate && end.Date != start.Date)}";
    }
    private bool WithDate => period is { } shown && shown.To - shown.From > TimeSpan.FromDays(1);
    private static DateTimeOffset CeilingMinute(DateTimeOffset time)
        => new((time.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);

    private void RankChanged()
    {
        // The chart switches unit at once; both reads wait until Rank by settles.
        ShowSeries();
        if ((slot ?? period) is null) return;
        ReadWhenSettled();
    }

    /// <summary>Drops a read already in flight (it is for the old ranking or scope, so the rows and the share bar stay on one) and reads
    /// again once the choice settles.</summary>
    private void ReadWhenSettled()
    {
        request++; Loading = true; pending = LoadingText; summary.Text = pending;
        rankDelay.Stop(); rankDelay.Start();
    }

    private async Task ReloadAsync()
    {
        if ((slot ?? period) is not { } range) return;
        int current = ++request; var ranking = Rank;
        if (session.ProcessHistory is not { } service) { Loading = false; Unavailable("Application resource history is not available."); return; }
        // The page's one-minute refresh keeps the numbers on screen; a new range or ranking says it is loading.
        if (!Shows(range.From, range.To, ranking)) { pending = LoadingText; summary.Text = pending; }
        Loading = true; status.Text = service.Status;
        ProcessResourceView? view; string reason = "Application resources could not be read.";
        try { view = await service.TryReadApplicationsAsync(range.From, range.To, ranking); }
        catch (ArgumentException error) { view = null; reason = $"Application resources could not be read ({error.GetType().Name})."; }
        ReadsReturned++;
        if (current != request) return;
        Loading = false; status.Text = service.Status;
        // An unreadable file is reported (the status names the error type), never shown as a period with nothing recorded.
        if (view is null) Unavailable(reason);
        else Apply(view, DateTimeOffset.UtcNow);
    }

    /// <summary>Clears the rows, share bar, gap notice, storage line and chart, and gives <paramref name="reason"/> instead.</summary>
    private void Unavailable(string reason)
    {
        View = null; SelectedApp = null; pending = reason; summary.Text = reason;
        syncing = true;
        try { rows.Clear(); }
        finally { syncing = false; }
        shareBar.Children.Clear(); shareBar.ColumnDefinitions.Clear(); shareBar.Visibility = Visibility.Collapsed;
        Note(retention, ""); Note(gapNotice, ""); storage.Text = ""; frame.Visibility = Visibility.Visible;
        GpuHeaderTips(null);
        ShowSeries();
    }
    /// <summary>True when the view on screen already answers the request: the same slot, or the period a minute refresh moved slightly.</summary>
    private bool Shows(DateTimeOffset from, DateTimeOffset to, ProcessRank ranking) => View is { } view && shownRank == ranking && shownSlot == slot
        && view.To - view.From == to - from && (slot is null ? (from - view.From).Duration() < TimeSpan.FromMinutes(2) : view.From == from);

    /// <summary>Shows a ranking read with the current Rank by, for the period or the slot being listed. <paramref name="now"/> bounds the
    /// minutes that could have been saved.</summary>
    internal void Apply(ProcessResourceView view, DateTimeOffset now)
    {
        // Another slot, Rank by or period is a new ranking, so the table starts at its top and its busiest apps show; the minute refresh
        // shows the same one again and keeps the scroll offset with the row, the current cell and the focus.
        bool newRanking = !Shows(view.From, view.To, Rank);
        View = view; shownRank = Rank; shownSlot = slot; pending = null;
        // The heading names the minutes listed, from the first one's start to the last one's end.
        if (shownSlot is { } listed)
            heading.Text = view is { FirstMinute: { } first, LastMinuteEnd: { } end } ? SlotHeading(first, end, WithDate)
                : SlotHeading(CeilingMinute(listed.From), CeilingMinute(listed.To), WithDate);
        // The chart follows a listed application; one missing from the new period or slot has nothing to show.
        if (SelectedApp is { } app && (view.RecordedMinutes == 0 || (app != ProcessHistory.EverythingElse
            && !view.Rows.Any(r => r.Name.Equals(app, StringComparison.OrdinalIgnoreCase))))) SelectedApp = null;
        // A minute that has just ended may not be saved yet, so it is not a gap until it has had time to arrive. Minutes before the
        // 30-day retention cutoff are never kept, so they are not gaps either. The notice describes the period, so a slot leaves it out.
        retainedFrom = ProcessHistoryStore.RetentionCutoff(now);
        long gaps = shownSlot is null ? ElapsedMinutes(view.From > retainedFrom ? view.From : retainedFrom, view.To, now - SaveDelay) - view.RecordedMinutes : 0;
        Note(gapNotice, gaps > 0 ? $"{gaps:N0} minutes in this period have no process data (monitoring off, DeadWare not running, or a capture gap)." : "");
        storage.Text = $"process-history.db · {Ui.Bytes(view.StorageBytes)}";
        GpuHeaderTips($"GPU recorded for {Duration(view.GpuCoveredSeconds)} of this range");
        RefreshRows();
        if (newRanking) FirstScroller(table)?.ScrollToTop();
        ShowSeries();
    }

    /// <summary>The first ScrollViewer inside <paramref name="element"/>: for the table, the one its template scrolls the rows with. Null
    /// until the template is applied, when there is no offset to reset.</summary>
    private static ScrollViewer? FirstScroller(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (VisualTreeHelper.GetChild(element, i) is var child && (child as ScrollViewer ?? FirstScroller(child)) is { } found) return found;
        return null;
    }

    /// <summary>How long a minute can take to reach the file after it ends: it closes at the next poll's publication and the service
    /// saves closed minutes every flush interval.</summary>
    private TimeSpan SaveDelay => ProcessHistoryService.DefaultFlushInterval + TimeSpan.FromSeconds(session.Settings.PollingIntervalSeconds);

    /// <summary>Whole minutes that start in [<paramref name="from"/>, <paramref name="to"/>) and have ended by <paramref name="now"/>:
    /// every minute the store could hold for the period.</summary>
    internal static long ElapsedMinutes(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        const long Minute = TimeSpan.TicksPerMinute;
        static long Ceiling(long ticks) => (ticks + Minute - 1) / Minute * Minute;
        long end = Math.Min(Ceiling(to.UtcTicks), now.UtcTicks / Minute * Minute);
        return Math.Max(0, (end - Ceiling(from.UtcTicks)) / Minute);
    }

    /// <summary>Like "18 min 0 s": seconds under a minute, minutes and seconds under an hour, then hours and minutes.</summary>
    internal static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        if (seconds < 59.95) return seconds.ToString("0.#", CultureInfo.CurrentCulture) + " s";
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours:N0} h {time.Minutes} min" : $"{time.Minutes} min {time.Seconds} s";
    }

    /// <summary>Gives the two GPU columns' headers <paramref name="tip"/> (none when null). A header style based on the theme's carries it,
    /// so those headers keep the theme's padding and captions, and each Header stays its title.</summary>
    private void GpuHeaderTips(string? tip)
    {
        if (tip == gpuHeaderTip) return;
        gpuHeaderTip = tip;
        Style? header = null;
        if (tip is not null)
        {
            header = new Style(typeof(DataGridColumnHeader), TryFindResource(typeof(DataGridColumnHeader)) as Style);
            header.Setters.Add(new Setter(ToolTipProperty, tip));
        }
        foreach (var column in gpuColumns) column.HeaderStyle = header;
    }

    private void RefreshRows()
    {
        if (View is not { } view) return;
        string query = search.Text.Trim();
        var listed = new List<ProcessResourceRow>();
        if (view.RecordedMinutes > 0)
        {
            listed.AddRange(view.Rows.Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            // Everything else always stays, whatever the search.
            listed.Add(view.EverythingElse);
        }
        // Removing the selected row (a search hid it) does not clear SelectedApp: the row is selected again when it comes back.
        syncing = true;
        try
        {
            // Each listed application keeps its row and only its numbers change. A name whose casing changed (its old minutes expired)
            // gets a new row, so the old casing is never shown. No two rows share a key (see Key).
            var current = rows.ToDictionary(r => r.Key, StringComparer.Ordinal);
            var target = listed.Select(r => (current.GetValueOrDefault(Key(r)) ?? new ResourceRow(Key(r), r.Name)).Update(r, view.GpuCoveredSeconds)).ToList();
            // As on the Processes page: gone rows leave, kept rows move as few as the planner allows, and new rows go in at their place.
            var keep = target.ToHashSet();
            for (int i = rows.Count - 1; i >= 0; i--) if (!keep.Contains(rows[i])) rows.RemoveAt(i);
            foreach (var move in CalmOrderPlanner.Plan([.. rows.Select(r => r.Key)], [.. target.Select(r => r.Key)])) rows.Move(move.From, move.To);
            for (int i = 0; i < target.Count; i++) if (i >= rows.Count || !ReferenceEquals(rows[i], target[i])) rows.Insert(i, target[i]);
            table.SelectedItem = rows.FirstOrDefault(r => r.Name.Equals(SelectedApp, StringComparison.OrdinalIgnoreCase));
        }
        finally { syncing = false; }
        // A slot without process data (monitoring was off) says so instead of showing an empty table, and so does a GPU chart slot without
        // GPU data.
        bool emptySlot = shownSlot is not null && view.RecordedMinutes == 0, noGpu = NoGpuData(view);
        // A period that starts before the retention cutoff says its earlier minutes are not kept; wholly before it, that is the summary.
        bool expired = shownSlot is null && view.RecordedMinutes == 0 && view.To <= retainedFrom;
        Note(retention, shownSlot is null && view.From < retainedFrom && !expired ? NotKept : "");
        // The read returns the top rows; the period may hold more applications than that.
        bool more = view.ApplicationCount > view.Rows.Count;
        frame.Visibility = emptySlot || noGpu ? Visibility.Collapsed : Visibility.Visible;
        summary.Text = pending ?? (emptySlot ? "No process data in this span."
            : noGpu ? "No GPU data in this span."
            : expired ? NotKept
            : view.RecordedMinutes == 0 ? "No application resources were recorded in this period."
            : query.Length > 0 ? $"{listed.Count - 1:N0} of {(more ? "the top " : "")}{view.Rows.Count:N0} applications match."
            : $"{(more ? $"Top {view.Rows.Count:N0} of {view.ApplicationCount:N0}" : $"{view.Rows.Count:N0}")} applications · {view.RecordedMinutes:N0} recorded minutes");
        DrawShares(view, listed);
    }

    /// <summary>True when the shown slot was listed from the GPU chart and is ranked by GPU, and its recorded minutes hold no GPU-covered time
    /// (minutes saved before the upgrade, or without GPU counters). A slot listed from the CPU or RAM chart and then ranked by GPU shows its
    /// table, with "—" in the GPU columns.</summary>
    private bool NoGpuData(ProcessResourceView view)
        => slotFromGpu && shownSlot is not null && shownRank == ProcessRank.Gpu && view.RecordedMinutes > 0 && view.GpuCoveredSeconds == 0;

    private void DrawShares(ProcessResourceView view, IReadOnlyList<ProcessResourceRow> listed)
    {
        shareBar.Children.Clear(); shareBar.ColumnDefinitions.Clear();
        // The bar and its segments name the same whole: the period's, or the listed slot's.
        AutomationProperties.SetName(shareBar, shownSlot is null ? "Share of period total" : "Share of this span's total");
        double total = view.RankTotal;
        bool visible = total > 0 && listed.Count > 0;
        shareBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        // Colours follow the unfiltered ranking, so a row keeps its colour while the search changes.
        var order = new Dictionary<ProcessResourceRow, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < view.Rows.Count; i++) order[view.Rows[i]] = i;
        double shown = 0;
        string whole = shownSlot is null ? "the period total" : "this span's total";
        for (int i = 0; i < listed.Count; i++)
        {
            var row = listed[i]; double part = Math.Max(0, row.PartOf(shownRank)); shown += part;
            shareBar.ColumnDefinitions.Add(new() { Width = new GridLength(part, GridUnitType.Star) });
            var piece = new Rectangle { ToolTip = $"{row.Name}: {100 * part / total:0.#} % of {whole}" };
            piece.SetResourceReference(Shape.FillProperty, row.EverythingElse ? "Muted" : Palette[order.GetValueOrDefault(row) % Palette.Length]);
            Grid.SetColumn(piece, i); shareBar.Children.Add(piece);
        }
        // Rows the search hides, or beyond the rows read, leave their part empty, so each segment stays a share of the whole period.
        if (total > shown) shareBar.ColumnDefinitions.Add(new() { Width = new GridLength(total - shown, GridUnitType.Star) });
    }

    /// <summary>Charts the selected application over the shown period with the Rank by metric; changing either redraws it. While a slot is
    /// listed the chart still covers the page's period.</summary>
    private void ShowSeries()
    {
        var view = View;
        if (SelectedApp is not { } app || view is null)
        {
            charted = null; seriesRequest++; seriesDelay.Stop(); chart.Visibility = Visibility.Collapsed;
            // A slot without process data, or a GPU chart slot without GPU data, shows no table to select from.
            Note(chartHint, view is { RecordedMinutes: > 0 } && !NoGpuData(view) ? "Select an application to chart it over this period." : "");
            return;
        }
        Note(chartHint, ""); chart.Visibility = Visibility.Visible;
        var metric = Rank;
        var (from, to) = shownSlot is not null && period is { } whole ? whole : (view.From, view.To);
        if (charted == (app, metric, from, to)) return;
        bool redraw = charted is not { } shown || shown.App != app || shown.Rank != metric;
        charted = (app, metric, from, to); seriesRequest++;
        var (unit, maximum, title, label, _) = Metric(metric, app);
        chartTitle.Text = $"{app} · {title}";
        // A new app or metric shows an empty plot in its unit until the read returns, never the previous metric on the wrong scale;
        // a new period for the same series keeps the old line until then, so the page's minute refresh does not flicker.
        if (redraw) { Chart.SetData([], unit, maximum); AutomationProperties.SetName(Chart, $"{app} {label} history"); }
        // Stepping through rows or Rank by starts one read, once the choice settles.
        seriesDelay.Stop(); seriesDelay.Start();
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
        catch (ArgumentException) { history = null; }
        if (current != seriesRequest) return;
        var (unit, maximum, title, label, color) = Metric(metric, app);
        if (history is null)
        {
            // Reported like the ranking: an empty plot under a title that says so, and the service status says why.
            chartTitle.Text = $"{app} · {title} · could not be read"; status.Text = service.Status;
            Chart.SetData([], unit, maximum); AutomationProperties.SetName(Chart, $"{app} {label} history");
            return;
        }
        Chart.SetData([new UsageTrace(history, color, false, $"{app} {label}")], unit, maximum);
    }
    /// <summary>The chart for <paramref name="metric"/>; <paramref name="app"/> is the charted application.</summary>
    private static (string Unit, double? Maximum, string Title, string Label, string Color) Metric(ProcessRank metric, string app) => metric switch
    {
        ProcessRank.CpuTime => ("%", 100, "CPU % of all logical processors", "CPU", Theme.GraphKey("cpu")),
        ProcessRank.AverageMemory => ("B", null, "memory (private working set)", "memory", Theme.GraphKey("memory")),
        // Everything else's GPU is a sum over many apps and can exceed 100 %, so its scale is automatic.
        ProcessRank.Gpu => ("%", app == ProcessHistory.EverythingElse ? null : 100, "GPU % of the busiest engine", "GPU", Theme.GraphKey("gpu")),
        _ => ("B/s", null, "I/O read + written per second", "I/O", Theme.GraphKey("diskread")),
    };

    /// <summary>A row's identity: its image name, ordinal, or "\0" for Everything else. Keys are unique: stored names never contain control
    /// characters (every version of the store rejects them), so no app's name is "\0"; and the store keeps one name per <c>upper</c>, its
    /// display fixed when first saved, and ranks one row per name, so no two apps' names are equal.</summary>
    private static string Key(ProcessResourceRow row) => row.EverythingElse ? "\0" : row.Name;
    /// <summary>Adds a column. With <paramref name="tip"/>, its cells take their tooltip from that row property, through a cell style based on
    /// the theme's, so they keep its padding and selection colours.</summary>
    private DataGridColumn Column(string title, string binding, double weight, string? tip = null)
    {
        var column = new DataGridTextColumn { Header = title, Binding = new Binding(binding), Width = new DataGridLength(weight, DataGridLengthUnitType.Star) };
        if (tip is not null)
        {
            var cell = new Style(typeof(DataGridCell), TryFindResource(typeof(DataGridCell)) as Style);
            cell.Setters.Add(new Setter(ToolTipProperty, new Binding(tip)));
            column.CellStyle = cell;
        }
        table.Columns.Add(column);
        return column;
    }
    private static void Note(TextBlock note, string text) { note.Text = text; note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
}
