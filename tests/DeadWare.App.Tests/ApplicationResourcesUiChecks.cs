using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeadWare.App.Tests;

/// <summary>Usage history's Application resources card, run at the end of <see cref="UsageHistoryUiChecks"/> (phase "usage history").
/// The card reads a real process-history.db, filled with fixture minutes, through a <see cref="ProcessHistoryService"/>. Pages here are
/// never Loaded, so after a range or earlier/later change <see cref="Load"/> runs the page's own load, as its refresh would. For click a
/// moment the page's Device charts read a usage-history.db whose CPU and RAM minutes cover the fixture day's process minutes and ten
/// minutes after them without process data.</summary>
internal static class ApplicationResourcesUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Machine = "fixture";
    private const string GapEnding = "minutes in this period have no process data (monitoring off, DeadWare not running, or a capture gap).";
    private const string Loading = "Loading application resources…";
    private const string NothingRecorded = "No application resources were recorded in this period.";
    private const string NoProcessData = "No process data in this span.";
    private const string NoGpuData = "No GPU data in this span.";
    private const string SelectToChart = "Select an application to chart it over this period.";
    private const string KeptForThirtyDays = "Application resources are kept for 30 days; earlier minutes are not kept.";
    private const string Paused = "recorded while Per-process monitoring and usage history are both on";
    // The page passes its CPU chart's point count to the card; cards built alone here pass the page's fallback width.
    private const int Points = 600;
    private const int DayMinutes = 90;
    private static readonly Size PageSize = new(1050, 2600);
    // The Device charts a moment can be clicked on.
    private static readonly string[] Three = ["cpu", "memory", "gpu"];
    // Every recorded minute of the fixture day (60 covered seconds, 8 processors): CPU 100-ns ticks, I/O read, I/O written, Σ memory × Δt.
    private static readonly ProcessAppMinute[] DayApps =
    [
        new("chrome.exe", 120_000_000, 2_000_000, 1_000_000, 800e6 * 60, false),
        new("Code.exe", 30_000_000, 500_000, 200_000, 1.5e9 * 60, false),
        new("steam.exe", 10_000_000, 50_000_000, 10_000_000, 300e6 * 60, false),
        new("explorer.exe", 5_000_000, 100_000, 100_000, 100e6 * 60, false),
        new(ProcessHistory.EverythingElse, 50_000_000, 5_000_000, 5_000_000, 2e9 * 60, true),
    ];
    // Ninety minutes of the day ranking: CPU seconds per app and in total.
    private const double ChromeCpu = DayMinutes * 12.0, EverythingElseCpu = DayMinutes * 5.0, CpuTotal = DayMinutes * 21.5;
    private static int reads;
    private static ManualResetEventSlim? hold;

    internal static void Run(Application app)
    {
        var culture = CultureInfo.CurrentCulture; var context = SynchronizationContext.Current;
        string folder = Path.Combine(Path.GetTempPath(), "deadware-resources-" + Guid.NewGuid().ToString("N"));
        var release = new ManualResetEventSlim(false);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            // The card awaits its reads on the UI thread, as it does in the app.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Directory.CreateDirectory(folder);
            var minute = UsageMetrics.Floor(DateTimeOffset.UtcNow);
            var service = Service(folder, minute);
            var page = new UsageHistoryPage(Session(service, folder, DeviceHistory(folder, minute)));
            Layout(page);
            var card = Assert.Single(Descendants(page).OfType<ApplicationResourcesCard>());
            Card_sits_between_applications_and_keep_a_longer_record(page);
            Before_the_first_read_the_card_says_it_is_loading(card);
            Load(page); WaitFor(() => card.View is not null && !card.Loading);
            Table_has_the_nine_columns_and_everything_else_last(card);
            Rows_show_the_period_totals(card);
            Search_filters_rows_and_keeps_everything_else(card);
            Share_bar_has_a_segment_per_row(card);
            Selecting_an_app_shows_its_chart_and_rank_by_redraws(card);
            Selecting_an_app_while_ranked_by_gpu_charts_its_gpu(card);
            The_note_names_gpu(card);
            The_app_chart_shares_the_device_charts_grid(page, card);
            Cpu_labels_show_tenths_under_ten_percent_on_the_app_chart_only(page, card);
            Gap_notice_counts_minutes_without_process_data(card);
            Storage_line_names_the_file(card);
            Quick_range_changes_show_only_the_last_period(page, card, release);
            Range_change_reloads_the_card(page, card);
            // Click a moment, back on Day: the Device charts show the fixture's usage minutes.
            SelectRange(page, "Day"); Load(page);
            Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
            Only_the_cpu_ram_and_gpu_charts_are_selectable(page);
            Selectable_charts_draw_a_frame_while_they_have_keyboard_focus(page);
            Clicking_a_point_brings_the_card_into_view(page, card);
            Clicking_a_cpu_point_lists_the_busiest_apps_in_its_slot(page, card);
            Both_charts_show_the_hairline_and_clear_removes_it(page, card);
            Clear_hands_its_focus_to_rank_by_after_a_chart_listed_the_slot(page, card);
            Ram_click_ranks_by_average_memory(page, card);
            Arrow_keys_move_the_selection_and_enter_lists_it(page, card);
            The_minute_refresh_keeps_the_slot_and_moves_its_hairlines_with_the_grid(page, card);
            Left_and_right_mark_both_charts_and_the_minute_refresh_keeps_the_cursor(page, card);
            Esc_clears_a_cursor_that_lists_nothing_and_the_refresh_keeps_it_cleared(page, card);
            A_new_period_clears_the_moment(page, card);
            Clicking_a_slot_without_process_data_says_so(page, card, minute.AddMinutes(-11));
            // Click a moment on the GPU chart: each check shows the dashboard's GPU selected first (WithGpu).
            Clicking_a_gpu_point_ranks_the_slot_by_gpu(page, card);
            Keys_on_the_gpu_chart_move_all_three_and_esc_clears(page, card);
            Clear_removes_the_gpu_hairline_too(page, card);
            A_gpu_click_on_a_slot_without_process_data_says_so(page, card, minute.AddMinutes(-11));
            Clearing_the_dashboard_gpu_keeps_the_listed_gpu_moment(page, card);
            Slot_heading_formats();
            Gap_notice_is_exact_and_hidden_without_gaps(folder);
            Empty_period_and_missing_history_say_why(folder);
            Unreadable_history_is_reported_not_shown_as_empty(folder);
            A_slot_that_cannot_be_read_says_why_and_clear_returns(folder);
            Gap_notice_starts_at_the_retention_cutoff(folder);
            More_applications_than_the_rows_read_say_top_n_of_m(folder);
            A_failed_series_read_keeps_the_chart_name(folder);
            Clear_without_a_chart_hands_its_focus_to_rank_by(folder);
            The_minute_refresh_updates_the_rows_in_place_and_keeps_the_current_cell(folder);
            A_new_ranking_starts_the_table_at_the_top(folder);
            Gpu_rank_columns_and_header_tooltips(folder);
            Gpu_dashes_name_their_reason(folder);
            Rank_by_gpu_over_version_1_minutes_shows_dashes_and_no_share_bar(folder);
            A_gpu_slot_with_gpu_data_lists_apps_by_gpu_and_everything_else(folder);
            Status_line_follows_the_recording_switches_without_a_read(folder);
        }
        finally
        {
            release.Set(); Volatile.Write(ref hold, null);
            CultureInfo.CurrentCulture = culture; SynchronizationContext.SetSynchronizationContext(context);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Card_sits_between_applications_and_keep_a_longer_record(UsageHistoryPage page)
        => Assert.Equal(["Device", "Applications", "Application resources", "Keep a longer record"], CardHeadings(page));

    private static void Before_the_first_read_the_card_says_it_is_loading(ApplicationResourcesCard card)
    {
        Assert.Null(card.View);
        Assert.Contains(Loading, Texts(card));
    }

    private static void Table_has_the_nine_columns_and_everything_else_last(ApplicationResourcesCard card)
    {
        Assert.Equal(["Application", "CPU time", "Avg CPU", "Avg memory while listed", "Avg GPU", "Avg GPU memory while listed", "I/O read", "I/O written",
            "Minutes listed"], Headers(card));
        Assert.Equal("Everything else", LastRowName(card));
        Assert.Equal(ProcessRank.CpuTime, card.Rank);
        Assert.Equal(["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"], RowNames(card));
        Assert.Contains($"4 applications · {DayMinutes} recorded minutes", Texts(card));
    }

    private static void Rows_show_the_period_totals(ApplicationResourcesCard card)
    {
        // 90 minutes of 12 s on 8 processors × 60 s: 18 minutes of CPU time, 2.5 % of the machine; memory averages over the minutes listed.
        var chrome = Rows(card)[0];
        Assert.Equal(["chrome.exe", "18 min 0 s", "2.5 %", "800.0 MB", "180.0 MB", "90.0 MB", "90"],
            new[] { "Name", "CpuTime", "AverageCpu", "AverageMemory", "IoRead", "IoWritten", "MinutesListed" }.Select(p => Cell(chrome, p)));
        Assert.Equal("7 min 30 s", Cell(Rows(card)[^1], "CpuTime"));
        Assert.Equal(["12.3 s", "2 min 5 s", "1 h 2 min", "—"], new[] { 12.34, 125.4, 3723, double.NaN }.Select(ApplicationResourcesCard.Duration));
    }

    private static void Search_filters_rows_and_keeps_everything_else(ApplicationResourcesCard card)
    {
        int before = Volatile.Read(ref reads);
        SetSearch(card, "chro"); Assert.Equal(["chrome.exe", "Everything else"], RowNames(card));
        Assert.Contains("1 of 4 applications match.", Texts(card));
        SetSearch(card, "  CHRO "); Assert.Equal(["chrome.exe", "Everything else"], RowNames(card));
        SetSearch(card, "no such app"); Assert.Equal(["Everything else"], RowNames(card));
        SetSearch(card, ""); Assert.True(RowNames(card).Count > 2);
        // Typing filters the rows already read; it never queries process-history.db again.
        Assert.Equal(before, Volatile.Read(ref reads));
    }

    private static void Share_bar_has_a_segment_per_row(ApplicationResourcesCard card)
    {
        var bar = Assert.IsType<Grid>(Named(card, "Share of period total"));
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal(RowNames(card).Count, Segments(bar).Count);
        // Each segment is its row's part of the period's CPU time; Everything else is last and Muted.
        Assert.Equal(ChromeCpu / CpuTotal, Fraction(bar, 0), 6);
        Assert.Equal(EverythingElseCpu / CpuTotal, Fraction(bar, Segments(bar).Count - 1), 6);
        Assert.Equal(((SolidColorBrush)Application.Current.Resources["Muted"]).Color, Assert.IsType<SolidColorBrush>(Segments(bar)[^1].Fill).Color);
        Assert.Equal("chrome.exe: 55.8 % of the period total", Segments(bar)[0].ToolTip);
        // A search keeps each segment's share of the whole period: the hidden rows leave their part empty.
        SetSearch(card, "chro");
        Assert.Equal(2, Segments(bar).Count);
        Assert.Equal(ChromeCpu / CpuTotal, Fraction(bar, 0), 6);
        SetSearch(card, "");
        Assert.Equal(RowNames(card).Count, Segments(bar).Count);
    }

    private static void Selecting_an_app_shows_its_chart_and_rank_by_redraws(ApplicationResourcesCard card)
    {
        Assert.False(IsShown(card.Chart, card));
        Select(card, "chrome.exe");
        Assert.Equal("chrome.exe", card.SelectedApp);
        Assert.True(IsShown(card.Chart, card));
        Assert.Equal("%", ChartUnit(card)); Assert.Equal(100d, ChartMaximum(card));
        WaitFor(() => Traces(card).Count == 1);
        var cpu = Traces(card)[0].View;
        Assert.Equal(DayMinutes, cpu.Minutes);
        Assert.Equal(2.5, cpu.Statistics.Average!.Value, 6);

        var ranked = card.View;
        SetRank(card, ProcessRank.AverageMemory);
        Assert.Equal(ProcessRank.AverageMemory, card.Rank);
        Assert.Equal("B", ChartUnit(card)); Assert.Null(ChartMaximum(card));
        WaitFor(() => !ReferenceEquals(card.View, ranked) && !card.Loading && Traces(card).Count == 1);
        Assert.Equal("chrome.exe", card.SelectedApp);
        Assert.NotNull(Table(card).SelectedItem);
        Assert.Equal("chrome.exe", Cell(Table(card).SelectedItem!, "Name"));
        Assert.Equal(800e6, Traces(card)[0].View.Statistics.Average!.Value, 0);
        Assert.Equal("Code.exe", RowNames(card)[0]);

        ranked = card.View;
        SetRank(card, ProcessRank.Io);
        Assert.Equal("B/s", ChartUnit(card)); Assert.Null(ChartMaximum(card));
        WaitFor(() => !ReferenceEquals(card.View, ranked) && !card.Loading && Traces(card).Count == 1);
        // chrome.exe reads 2 MB and writes 1 MB a minute: 50 kB/s.
        Assert.Equal(5e4, Traces(card)[0].View.Statistics.Average!.Value, 0);
        Assert.Equal("steam.exe", RowNames(card)[0]);
        Assert.Equal("chrome.exe", card.SelectedApp);

        // Stepping through Rank by starts one ranking read and one chart read, once it settles.
        int before = Volatile.Read(ref reads); ranked = card.View;
        SetRank(card, ProcessRank.AverageMemory); SetRank(card, ProcessRank.CpuTime);
        Assert.True(card.Loading);
        Assert.Equal("%", ChartUnit(card));
        WaitFor(() => !ReferenceEquals(card.View, ranked) && !card.Loading && Traces(card).Count == 1);
        Assert.Equal("chrome.exe", RowNames(card)[0]);
        Assert.Equal(before + 2, Volatile.Read(ref reads));

        // Stepping through rows, as the arrow keys do, starts one chart read for the row it stops on.
        before = Volatile.Read(ref reads);
        Select(card, "Code.exe"); Select(card, "steam.exe"); Select(card, "chrome.exe");
        WaitFor(() => Traces(card).Count == 1);
        Assert.Equal("chrome.exe", card.SelectedApp);
        Assert.Equal("chrome.exe CPU", Traces(card)[0].Label);
        Assert.Equal(before + 1, Volatile.Read(ref reads));
    }

    private static void Selecting_an_app_while_ranked_by_gpu_charts_its_gpu(ApplicationResourcesCard card)
    {
        SetRank(card, ProcessRank.Gpu);
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.Gpu);
        Select(card, "chrome.exe");
        WaitFor(() => Traces(card).Count == 1);
        Assert.Contains("chrome.exe · GPU % of the busiest engine", Texts(card));
        Assert.Equal("chrome.exe GPU", Traces(card)[0].Label);
        Assert.Equal("%", ChartUnit(card)); Assert.Equal(100d, ChartMaximum(card));
        // Everything else's GPU is a sum over many apps and can exceed 100 %, so its scale is automatic.
        Select(card, "Everything else");
        Assert.Null(ChartMaximum(card));
        // Hands back the state Selecting_an_app_shows_its_chart_and_rank_by_redraws left: The_app_chart_shares_the_device_charts_grid reads
        // chrome.exe's CPU trace and Range_change_reloads_the_card expects chrome.exe selected. Both reads have returned, so none is left
        // waiting for the checks after it.
        Select(card, "chrome.exe"); SetRank(card, ProcessRank.CpuTime);
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.CpuTime && Traces(card).Count == 1 && Traces(card)[0].Label == "chrome.exe CPU");
    }

    private static void The_note_names_gpu(ApplicationResourcesCard card)
    {
        var note = Texts(card).Single(t => t.StartsWith("CPU time, I/O read and I/O written", StringComparison.Ordinal));
        Assert.Contains("Each minute lists up to 40 of the busiest applications by CPU, memory, I/O and GPU; Everything else holds the rest. Usage after", note);
        Assert.Contains("Avg GPU is its busiest GPU engine's share of the time GPU data was recorded; Avg GPU memory while listed averages its dedicated GPU memory over the minutes it was listed with GPU data.", note);
    }

    private static void The_app_chart_shares_the_device_charts_grid(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        // The selected app's series is read with the CPU chart's point count, so its buckets are the CPU, RAM and GPU charts' slots.
        var device = Traces(page.Chart("cpu"))[0].View; var app = Traces(card)[0].View;
        Assert.Equal("chrome.exe CPU", Traces(card)[0].Label);
        Assert.Equal((device.From, device.To, device.BucketMilliseconds), (app.From, app.To, app.BucketMilliseconds));
        Assert.NotEmpty(app.Buckets);
        Assert.All(app.Buckets, bucket => Assert.Contains(device.Buckets, slot => slot.Start == bucket.Start && slot.End == bucket.End));
    }

    private static void Cpu_labels_show_tenths_under_ten_percent_on_the_app_chart_only(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        // An app's share of all logical processors is often under 1 %, so the card's chart (hover and axis) keeps one decimal under 10 %.
        Assert.Equal("%", ChartUnit(card));
        Assert.Equal(["0.4%", "2.5%", "9.9%", "12%", "0%"], new[] { 0.4, 2.46, 9.94, 12.3, 0 }.Select(card.Chart.Format));
        // The Device charts keep whole percents.
        Assert.Equal(["0%", "2%", "12%"], new[] { 0.4, 2.46, 12.3 }.Select(page.Chart("cpu").Format));
        Assert.Equal(["0%", "2%"], new[] { 0.4, 2.46 }.Select(page.Chart("memory").Format));
    }

    private static void Gap_notice_counts_minutes_without_process_data(ApplicationResourcesCard card)
    {
        string notice = Assert.Single(Texts(card), t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        long gaps = long.Parse(notice[..notice.IndexOf(' ')], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        // The day has 1,439 or 1,440 whole minutes that have ended, depending on where the clock sits in its minute.
        Assert.InRange(gaps, 1440 - DayMinutes - 2, 1440 - DayMinutes);
    }

    private static void Storage_line_names_the_file(ApplicationResourcesCard card)
    {
        Assert.Contains(Texts(card), t => t.StartsWith("process-history.db · ", StringComparison.Ordinal));
        Assert.True(card.View!.StorageBytes > 0);
        Assert.Contains("process-history.db · " + Ui.Bytes(card.View.StorageBytes), Texts(card));
    }

    private static void Quick_range_changes_show_only_the_last_period(UsageHistoryPage page, ApplicationResourcesCard card, ManualResetEventSlim release)
    {
        // The month read waits inside the store until the newer day read has been shown.
        Volatile.Write(ref hold, release);
        SelectRange(page, "Month"); Load(page);
        WaitFor(() => Volatile.Read(ref hold) is null);
        Assert.True(card.Loading);
        Assert.Contains(Loading, Texts(card));
        SelectRange(page, "Day"); Load(page);
        WaitFor(() => !card.Loading);
        Assert.Equal(TimeSpan.FromDays(1), card.View!.To - card.View.From);
        Assert.Equal(PageData(page), (card.View.From, card.View.To));
        // Release the month read and wait until the card has its result: it arrives late and is dropped.
        int returned = card.ReadsReturned;
        release.Set();
        WaitFor(() => card.ReadsReturned == returned + 1);
        Assert.Equal(TimeSpan.FromDays(1), card.View!.To - card.View.From);
        Assert.Equal(["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"], RowNames(card));
    }

    private static void Range_change_reloads_the_card(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var dayFrom = card.View!.From;
        SelectRange(page, "Week"); Load(page);
        WaitFor(() => card.View!.From != dayFrom && !card.Loading);
        Assert.Equal(PageData(page), (card.View!.From, card.View.To));
        Assert.Equal(TimeSpan.FromDays(7), card.View.To - card.View.From);
        Near(Period(page), card.View);
        // Three days ago is inside the week but not the day.
        Assert.Contains("backup.exe", RowNames(card));
        Assert.Equal("chrome.exe", card.SelectedApp);

        var weekFrom = card.View.From;
        Click(ButtonNamed(page, "Earlier period")); Load(page);
        WaitFor(() => card.View!.From != weekFrom && !card.Loading);
        Assert.Equal(PageData(page), (card.View!.From, card.View.To));
        Near(Period(page), card.View);
        Assert.InRange(card.View.To - weekFrom, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        // Nothing was recorded the week before: no rows, a reason, and the selection clears with its chart.
        Assert.Empty(RowNames(card));
        Assert.Contains(NothingRecorded, Texts(card));
        Assert.Null(card.SelectedApp);
        Assert.False(IsShown(card.Chart, card));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of period total").Visibility);
    }

    private static void Gap_notice_is_exact_and_hidden_without_gaps(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        var from = new DateTimeOffset(2026, 11, 2, 12, 0, 30, TimeSpan.Zero); var to = from.AddHours(1);
        // 12:01 to 12:59 have ended by 13:00:30; the minute starting 13:00 has not.
        Assert.Equal(59, ApplicationResourcesCard.ElapsedMinutes(from, to, to));
        Assert.Equal(60, ApplicationResourcesCard.ElapsedMinutes(from, to, to.AddDays(1)));
        Assert.Equal(0, ApplicationResourcesCard.ElapsedMinutes(from, to, from));
        card.Apply(View(from, to, 50), to);
        Assert.Contains("9 " + GapEnding, Texts(card));
        card.Apply(View(from, to, 59), to);
        Assert.DoesNotContain(Texts(card), t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        // 12:59 ended at 13:00:00. Ten seconds later it may still be on its way to the file (a poll to close it, then the 15 s flush),
        // so it is not a gap yet; once that time has passed, a missing minute is one.
        var justEnded = new DateTimeOffset(2026, 11, 2, 13, 0, 10, TimeSpan.Zero);
        card.Apply(View(from, to, 58), justEnded);
        Assert.DoesNotContain(Texts(card), t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        card.Apply(View(from, to, 58), justEnded.AddSeconds(10));
        Assert.Contains("1 " + GapEnding, Texts(card));
    }

    private static void Empty_period_and_missing_history_say_why(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        Assert.Contains(Loading, Texts(card));
        var from = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero); var to = from.AddDays(1);
        card.Load(from, to, Points);
        Assert.False(card.Loading);
        Assert.Contains("Application resource history is not available.", Texts(card));
        card.Apply(View(from, to, 0), to.AddMinutes(1));
        Assert.Empty(RowNames(card));
        Assert.Contains(NothingRecorded, Texts(card));
        Assert.Contains("1,440 " + GapEnding, Texts(card));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of period total").Visibility);
    }

    private static void Unreadable_history_is_reported_not_shown_as_empty(string folder)
    {
        string junk = Path.Combine(folder, "junk"); Directory.CreateDirectory(junk);
        string path = Path.Combine(junk, "process-history.db"); File.WriteAllText(path, "not a database");
        var card = new ApplicationResourcesCard(Session(new ProcessHistoryService(path, Machine), folder));
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        // Rows, a gap notice and a storage line on screen first: the failure must clear them, not leave them standing.
        card.Apply(View(from, to, 50), to);
        Assert.Contains(Texts(card), t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        card.Load(from, to, Points);
        WaitFor(() => !card.Loading);
        var texts = Texts(card);
        Assert.Contains("Application resources could not be read.", texts);
        Assert.Contains(texts, t => t.Contains("(InvalidDataException)", StringComparison.Ordinal));
        Assert.DoesNotContain(NothingRecorded, texts);
        Assert.DoesNotContain(texts, t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("0 B", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("process-history.db", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(junk, StringComparison.OrdinalIgnoreCase));
        Assert.Null(card.View);
        Assert.Empty(RowNames(card));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of period total").Visibility);
        // The file is preserved.
        Assert.Equal("not a database", File.ReadAllText(path));
    }

    private static void Only_the_cpu_ram_and_gpu_charts_are_selectable(UsageHistoryPage page)
    {
        foreach (string kind in Three) { Assert.True(page.Chart(kind).Selectable); Assert.True(page.Chart(kind).Focusable); }
        foreach (string kind in new[] { "cputemp", "gputemp", "network" }) Assert.False(page.Chart(kind).Selectable);
        Assert.InRange(Traces(page.Chart("cpu"))[0].View.Buckets.Count, 20, DayMinutes + 10);
        // The Device card's hint names all three.
        Assert.Contains(Texts(page), t => t.EndsWith(" Click a CPU, RAM or GPU point, or press Enter on it, to list its busiest apps under Application resources.",
            StringComparison.Ordinal));
    }

    private static void Selectable_charts_draw_a_frame_while_they_have_keyboard_focus(UsageHistoryPage page)
    {
        // Tab reaches the CPU, RAM and GPU charts, and each draws an Accent frame while it has keyboard focus, as the app's other focusable
        // controls do. Offscreen pages cannot take keyboard focus, so each chart is told its focus changed, as the keyboard tells it.
        foreach (string kind in Three)
        {
            var chart = page.Chart(kind);
            Layout(page); Assert.Equal(0, FocusFrames(chart));
            KeyboardFocus(chart, true); Layout(page); Assert.Equal(1, FocusFrames(chart));
            KeyboardFocus(chart, false); Layout(page); Assert.Equal(0, FocusFrames(chart));
        }
        // A chart that is not selectable is not a Tab stop and never draws one.
        var network = page.Chart("network");
        KeyboardFocus(network, true); Layout(page); Assert.Equal(0, FocusFrames(network));
        KeyboardFocus(network, false); Layout(page);
    }

    private static void Clicking_a_cpu_point_lists_the_busiest_apps_in_its_slot(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var cpu = page.Chart("cpu"); var bucket = Bucket(cpu, 1);
        Activate(cpu, 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        Assert.StartsWith("Busiest apps · ", Heading(card));
        Assert.Equal(ProcessRank.CpuTime, card.Rank);
        Assert.Equal("Everything else", LastRowName(card));
        // The list holds exactly the stored minutes the clicked point averaged: those that start in its slot. Slot 1 lies inside the
        // fixture's process minutes, so every one of its Device minutes has process data.
        var slot = card.View!;
        Assert.Equal((bucket.Start, bucket.End), (slot.From, slot.To));
        Assert.InRange(bucket.Minutes, 1, 10);
        Assert.Equal(bucket.Minutes, slot.RecordedMinutes);
        var first = Ceiling(bucket.Start); var lastEnd = first.AddMinutes(bucket.Minutes);
        Assert.Equal<DateTimeOffset?>(first, slot.FirstMinute); Assert.Equal<DateTimeOffset?>(lastEnd, slot.LastMinuteEnd);
        Assert.Equal($"Busiest apps · {first.ToLocalTime():HH:mm}–{lastEnd.ToLocalTime():HH:mm}", Heading(card));
        Assert.Equal(["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"], RowNames(card));
        // chrome.exe uses 12 s of CPU a minute on 8 processors (2.5 % of the machine), Everything else 5 s.
        Assert.Equal(ApplicationResourcesCard.Duration(12.0 * bucket.Minutes), Cell(Rows(card)[0], "CpuTime"));
        Assert.Equal("2.5 %", Cell(Rows(card)[0], "AverageCpu"));
        Assert.Equal(ApplicationResourcesCard.Duration(5.0 * bucket.Minutes), Cell(Rows(card)[^1], "CpuTime"));
        Assert.Contains($"4 applications · {bucket.Minutes} recorded minutes", Texts(card));
        // The share bar and its segments name the slot's total, not the period's.
        var bar = Assert.IsType<Grid>(Named(card, "Share of this span's total"));
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.DoesNotContain(Descendants(card).OfType<FrameworkElement>(), e => AutomationProperties.GetName(e) == "Share of period total");
        Assert.EndsWith(" % of this span's total", (string)Segments(bar)[0].ToolTip);
        // The period's gap notice does not describe a slot; Clear is offered.
        Assert.DoesNotContain(Texts(card), t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        Assert.True(IsShown(Button(card, "Clear"), card));
        Assert.Equal<(DateTimeOffset, DateTimeOffset)?>((bucket.Start, bucket.End), card.Slot);
    }

    private static void Clicking_a_point_brings_the_card_into_view(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        // At a window's height the card sits below the Device and Applications cards, so a click scrolls it into view.
        var window = new Size(1050, 800);
        page.ScrollToTop(); Layout(page, window);
        var frame = (FrameworkElement)VisualTreeHelper.GetParent(card);
        Assert.Equal(0, page.VerticalOffset);
        Assert.True(Top(frame, page) > page.ViewportHeight, "The Application resources card starts below the fold.");
        var bucket = Bucket(page.Chart("cpu"), 1);
        Activate(page.Chart("cpu"), 1);
        WaitFor(() => { Layout(page, window); return page.VerticalOffset > 0; });
        Assert.InRange(Top(frame, page), -0.5, page.ViewportHeight - 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        // Back to the period ranking, at the checks' full height.
        Click(Button(card, "Clear"));
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
        page.ScrollToTop(); Layout(page);
        Assert.Equal(0, page.VerticalOffset);
    }

    private static void Both_charts_show_the_hairline_and_clear_removes_it(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var cpu = page.Chart("cpu"); var memory = page.Chart("memory");
        Assert.Equal(1, cpu.SelectedIndex); Assert.Equal(1, memory.SelectedIndex);
        Layout(page);
        Assert.Equal(1, Hairlines(cpu)); Assert.Equal(1, Hairlines(memory));
        Click(Button(card, "Clear"));
        Assert.Null(cpu.SelectedIndex); Assert.Null(memory.SelectedIndex);
        Assert.Equal("Application resources", Heading(card));
        Assert.Null(card.Slot);
        Assert.False(IsShown(Button(card, "Clear"), card));
        Layout(page);
        Assert.Equal(0, Hairlines(cpu)); Assert.Equal(0, Hairlines(memory));
        // Clear returns to the period ranking.
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
    }

    private static void Clear_hands_its_focus_to_rank_by_after_a_chart_listed_the_slot(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var cpu = page.Chart("cpu"); var bucket = Bucket(cpu, 1);
        Activate(cpu, 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        // Clear collapses once pressed. Holding the focus, it hands it to Rank by just below it, inside the card, never back to the chart
        // that listed the slot: focusing that chart would scroll the page up to the Device card, away from the ranking Clear restores.
        var clear = Button(card, "Clear"); var scope = FocusManager.GetFocusScope(clear);
        FocusManager.SetFocusedElement(scope, clear);
        Click(clear);
        Assert.False(IsShown(clear, card));
        Assert.Same(RankBy(card), FocusManager.GetFocusedElement(scope));
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
        FocusManager.SetFocusedElement(scope, null);
    }

    private static void Ram_click_ranks_by_average_memory(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var memory = page.Chart("memory"); var bucket = Bucket(memory, 2);
        Activate(memory, 2);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        Assert.Equal(ProcessRank.AverageMemory, card.Rank);
        // Every minute holds Code.exe at 1.5 GB, chrome.exe at 800 MB, steam.exe at 300 MB and explorer.exe at 100 MB.
        Assert.Equal(["Code.exe", "chrome.exe", "steam.exe", "explorer.exe", "Everything else"], RowNames(card));
        Assert.Equal("800.0 MB", Cell(Rows(card)[1], "AverageMemory"));
        Assert.StartsWith("Busiest apps · ", Heading(card));
        Assert.Equal(2, page.Chart("cpu").SelectedIndex); Assert.Equal(2, memory.SelectedIndex);
        // Esc on the chart that was clicked (it holds the focus) returns to the period ranking, still by average memory.
        Press(memory, Key.Escape);
        Assert.Equal("Application resources", Heading(card));
        Assert.Null(page.Chart("cpu").SelectedIndex); Assert.Null(memory.SelectedIndex);
        Shows_the_day_ranking(card, ["Code.exe", "chrome.exe", "steam.exe", "explorer.exe", "Everything else"]);
    }

    private static void Arrow_keys_move_the_selection_and_enter_lists_it(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var cpu = page.Chart("cpu"); var memory = page.Chart("memory");
        // Right moves the selection on both charts; nothing is listed until Enter.
        Assert.True(cpu.HandleKey(Key.Right)); Assert.True(cpu.HandleKey(Key.Right));
        Assert.Equal(1, cpu.SelectedIndex);
        Assert.Null(card.Slot);
        Assert.Equal("Application resources", Heading(card));
        int before = Volatile.Read(ref reads);
        Assert.True(cpu.HandleKey(Key.Enter));
        // The CPU chart ranks by CPU time again; the slot is read at once, not after Rank by settles, and only once.
        Assert.False(Timer(card, "rankDelay").IsEnabled);
        var bucket = Bucket(cpu, 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        Assert.Equal(before + 1, Volatile.Read(ref reads));
        Assert.Equal(ProcessRank.CpuTime, card.Rank);
        Assert.Equal("chrome.exe", RowNames(card)[0]);
        Assert.Equal(1, memory.SelectedIndex);
    }

    private static void The_minute_refresh_keeps_the_slot_and_moves_its_hairlines_with_the_grid(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var data = Data(page);
        var slot = card.Slot; string heading = Heading(card);
        Assert.NotNull(slot);
        // A refresh reads the period again a little later, so the bucket grid moves under the slot: here two earlier slots now come first.
        page.Apply(data with { Metrics = data.Metrics.ToDictionary(p => p.Key, p => p.Value with { Buckets = [At(p.Value, 0), At(p.Value, 1), .. p.Value.Buckets] }) });
        Assert.Equal(3, page.Chart("cpu").SelectedIndex); Assert.Equal(3, page.Chart("memory").SelectedIndex);
        Assert.Equal(slot, card.Slot); Assert.Equal(heading, Heading(card));
        page.Apply(data);
        Assert.Equal(1, page.Chart("cpu").SelectedIndex); Assert.Equal(1, page.Chart("memory").SelectedIndex);
    }

    private static void Left_and_right_mark_both_charts_and_the_minute_refresh_keeps_the_cursor(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var data = Data(page);
        var cpu = page.Chart("cpu"); var memory = page.Chart("memory"); var slot = card.Slot; string heading = Heading(card);
        int moves = 0; Action<int> count = _ => moves++; cpu.SelectionMoved += count;
        Press(cpu, Key.Right); Press(cpu, Key.Right); cpu.SelectionMoved -= count; Assert.Equal(2, moves);
        Assert.Equal(3, cpu.SelectedIndex); Assert.Equal(3, memory.SelectedIndex);
        Layout(page); Assert.Equal(1, Hairlines(cpu)); Assert.Equal(1, Hairlines(memory));
        Press(memory, Key.Left);
        Assert.Equal(2, cpu.SelectedIndex); Assert.Equal(2, memory.SelectedIndex);
        Assert.Equal(slot, card.Slot); Assert.Equal(heading, Heading(card));
        // RAM lacks its first bucket: the charts match by time, not by index.
        page.Apply(data with { Metrics = data.Metrics.ToDictionary(p => p.Key, p => p.Key == "memory" ? p.Value with { Buckets = [.. p.Value.Buckets.Skip(1)] } : p.Value) });
        Assert.Equal(2, cpu.SelectedIndex); Assert.Equal(1, memory.SelectedIndex);
        Press(cpu, Key.Right);
        Assert.Equal(3, cpu.SelectedIndex); Assert.Equal(2, memory.SelectedIndex);
        page.Apply(data);
        Assert.Equal(3, cpu.SelectedIndex); Assert.Equal(3, memory.SelectedIndex);   // not back to the listed slot (1)
        // Two earlier slots now come first: the cursor follows its time.
        page.Apply(data with { Metrics = data.Metrics.ToDictionary(p => p.Key, p => p.Value with { Buckets = [At(p.Value, 0), At(p.Value, 1), .. p.Value.Buckets] }) });
        Assert.Equal(5, cpu.SelectedIndex); Assert.Equal(5, memory.SelectedIndex);
        page.Apply(data);
        Assert.Equal(3, cpu.SelectedIndex); Assert.Equal(slot, card.Slot);
        // Enter lists the bucket and leaves the focus on the chart: ShowMoment's BringIntoView scrolls the card in without moving focus.
        // Offscreen nothing can take keyboard focus, and Focus() then takes the scope's logical focus only while nothing holds it. So Enter
        // is pressed once with nothing focused, where any Focus() call while the bucket is listed would show, and once on the focused chart.
        var scope = FocusManager.GetFocusScope(cpu); FocusManager.SetFocusedElement(scope, null);
        Press(cpu, Key.Left); var earlier = Bucket(cpu, 2); Assert.True(cpu.HandleKey(Key.Enter));
        Assert.Null(FocusManager.GetFocusedElement(scope));
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == earlier.Start);
        Assert.Null(FocusManager.GetFocusedElement(scope));
        Press(cpu, Key.Right); FocusManager.SetFocusedElement(scope, cpu);
        var bucket = Bucket(cpu, 3); Assert.True(cpu.HandleKey(Key.Enter));
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        Assert.Equal((bucket.Start, bucket.End), card.Slot);
        Assert.Equal(3, cpu.SelectedIndex); Assert.Equal(3, memory.SelectedIndex);
        Assert.Same(cpu, FocusManager.GetFocusedElement(scope)); FocusManager.SetFocusedElement(scope, null);
    }

    private static void Esc_clears_a_cursor_that_lists_nothing_and_the_refresh_keeps_it_cleared(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        var data = Data(page);
        var cpu = page.Chart("cpu"); var memory = page.Chart("memory");
        Press(cpu, Key.Escape); Assert.Null(card.Slot);
        Press(cpu, Key.Right);
        Assert.Equal(0, cpu.SelectedIndex); Assert.Equal(0, memory.SelectedIndex); Assert.Null(card.Slot);
        Press(cpu, Key.Escape);
        Assert.Null(cpu.SelectedIndex); Assert.Null(memory.SelectedIndex);
        page.Apply(data);
        Assert.Null(cpu.SelectedIndex); Assert.Null(memory.SelectedIndex);
        // A refresh can leave the cursor's time in no bucket (here both charts lose their first), so neither chart draws it. Esc still
        // clears it, so a later refresh does not bring it back.
        Press(cpu, Key.Right); Assert.Equal(0, cpu.SelectedIndex);
        page.Apply(data with { Metrics = data.Metrics.ToDictionary(p => p.Key, p => p.Value with { Buckets = [.. p.Value.Buckets.Skip(1)] }) });
        Assert.Null(cpu.SelectedIndex); Assert.Null(memory.SelectedIndex);
        Press(cpu, Key.Escape); page.Apply(data);
        Assert.Null(cpu.SelectedIndex); Assert.Null(memory.SelectedIndex);
        // Leaves a listed slot for A_new_period_clears_the_moment.
        Activate(cpu, 1); var bucket = Bucket(cpu, 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
    }

    private static void A_new_period_clears_the_moment(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        // Another range, or an earlier or later period, has another bucket grid: the slot and both hairlines clear at once.
        Assert.NotNull(card.Slot);
        SelectRange(page, "Week");
        Assert.Null(card.Slot); Assert.Equal("Application resources", Heading(card));
        Assert.Null(page.Chart("cpu").SelectedIndex); Assert.Null(page.Chart("memory").SelectedIndex);
        // On Week (and Month) a listed slot's heading carries its date, formatted as the card formats it.
        Load(page);
        var week = Bucket(page.Chart("cpu"), 1);
        Activate(page.Chart("cpu"), 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == week.Start);
        Assert.Equal(TimeSpan.FromDays(7), PageData(page).To - PageData(page).From);
        var first = card.View!.FirstMinute!.Value.ToLocalTime();
        Assert.StartsWith($"Busiest apps · {first.ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}–", Heading(card));
        SelectRange(page, "Day"); Load(page);
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
        var bucket = Bucket(page.Chart("cpu"), 1);
        Activate(page.Chart("cpu"), 1);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        Click(ButtonNamed(page, "Earlier period"));
        Assert.Null(card.Slot); Assert.Equal("Application resources", Heading(card));
        Assert.Null(page.Chart("cpu").SelectedIndex); Assert.Null(page.Chart("memory").SelectedIndex);
        Click(ButtonNamed(page, "Later period")); Load(page);
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
    }

    private static void Clicking_a_slot_without_process_data_says_so(UsageHistoryPage page, ApplicationResourcesCard card, DateTimeOffset lastProcessMinute)
    {
        var cpu = page.Chart("cpu"); int emptySlot = EmptyIndex(page, lastProcessMinute); var bucket = Bucket(cpu, emptySlot);
        Activate(cpu, emptySlot);
        WaitFor(() => !card.Loading && card.View is { } shown && shown.From == bucket.Start);
        var texts = Texts(card);
        Assert.Contains(NoProcessData, texts);
        Assert.Equal(0, card.View!.RecordedMinutes);
        Assert.Empty(RowNames(card));
        // It says so instead of showing an empty table, and it is not the period's "nothing recorded" or gap notice.
        Assert.False(IsShown(Table(card), card));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of this span's total").Visibility);
        Assert.DoesNotContain(NothingRecorded, texts);
        Assert.DoesNotContain(texts, t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        // The heading names the span's minutes; Clear stays available and both charts mark the slot.
        Assert.Equal($"Busiest apps · {Ceiling(bucket.Start).ToLocalTime():HH:mm}–{Ceiling(bucket.End).ToLocalTime():HH:mm}", Heading(card));
        Assert.True(IsShown(Button(card, "Clear"), card));
        Assert.Equal(emptySlot, cpu.SelectedIndex); Assert.Equal(emptySlot, page.Chart("memory").SelectedIndex);
        Press(card, Key.Escape);
        Assert.Equal("Application resources", Heading(card));
        Assert.Null(cpu.SelectedIndex); Assert.Null(page.Chart("memory").SelectedIndex);
        // Esc returns to the period ranking: its rows and table are back, and the span's sentence is gone.
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
        Assert.DoesNotContain(NoProcessData, Texts(card));
    }

    private static void Clicking_a_gpu_point_ranks_the_slot_by_gpu(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        WithGpu(page);
        Activate(page.Chart("gpu"), 1);
        WaitFor(() => !card.Loading && card.Slot is not null && card.Rank == ProcessRank.Gpu);
        Assert.StartsWith("Busiest apps · ", Heading(card));
        // The GPU chart has the CPU chart's slots, so all three charts mark the one listed.
        Assert.Equal([1, 1, 1], Three.Select(k => page.Chart(k).SelectedIndex));
        Layout(page); Assert.All(Three, k => Assert.Equal(1, Hairlines(page.Chart(k))));
        // The fixture's minutes hold no GPU data: the span says so in place of the table, which leaves no app to select for the chart.
        Assert.Contains(NoGpuData, Texts(card));
        Assert.DoesNotContain(SelectToChart, Texts(card));
        Assert.False(IsShown(Table(card), card));
        Assert.True(IsShown(Button(card, "Clear"), card));
        // Another Rank by asked for, before its read returns, keeps the shown state: no hint over the hidden table.
        SetRank(card, ProcessRank.CpuTime);
        Assert.DoesNotContain(SelectToChart, Texts(card));
        Assert.False(IsShown(Table(card), card));
        // The same slot listed from the CPU chart shows its table, and keeps it when ranked by GPU, with "—" in the GPU columns: the
        // sentence follows a GPU chart click.
        Activate(page.Chart("cpu"), 1);
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.CpuTime);
        Assert.DoesNotContain(NoGpuData, Texts(card)); Assert.True(IsShown(Table(card), card));
        SetRank(card, ProcessRank.Gpu);
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.Gpu);
        Assert.DoesNotContain(NoGpuData, Texts(card)); Assert.True(IsShown(Table(card), card));
        Assert.Equal("—", Cell(Row(card, "chrome.exe"), "AverageGpu"));
        Click(Button(card, "Clear"));
    }

    private static void Keys_on_the_gpu_chart_move_all_three_and_esc_clears(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        WithGpu(page); var gpu = page.Chart("gpu");
        // Left and Right on the GPU chart move the cursor on all three charts; nothing is listed until Enter.
        Press(gpu, Key.Right); Assert.Equal([0, 0, 0], Three.Select(k => page.Chart(k).SelectedIndex));
        Press(gpu, Key.Right); Press(gpu, Key.Right); Assert.Equal([2, 2, 2], Three.Select(k => page.Chart(k).SelectedIndex));
        Press(gpu, Key.Left); Assert.Equal([1, 1, 1], Three.Select(k => page.Chart(k).SelectedIndex));
        Assert.Null(card.Slot);
        var bucket = Bucket(gpu, 1);
        Assert.True(gpu.HandleKey(Key.Enter));
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.Gpu && card.Slot is not null);
        Assert.Equal((bucket.Start, bucket.End), card.Slot);
        Press(gpu, Key.Escape);
        Assert.Null(card.Slot); Assert.All(Three, k => Assert.Null(page.Chart(k).SelectedIndex));
    }

    private static void Clear_removes_the_gpu_hairline_too(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        WithGpu(page);
        Activate(page.Chart("gpu"), 1); WaitFor(() => card.Slot is not null);
        Click(Button(card, "Clear"));
        Assert.All(Three, k => Assert.Null(page.Chart(k).SelectedIndex));
        Layout(page); Assert.All(Three, k => Assert.Equal(0, Hairlines(page.Chart(k))));
    }

    private static void A_gpu_click_on_a_slot_without_process_data_says_so(UsageHistoryPage page, ApplicationResourcesCard card, DateTimeOffset lastProcessMinute)
    {
        // The span Clicking_a_slot_without_process_data_says_so lists, from the GPU chart: no process data comes before no GPU data.
        WithGpu(page);
        Activate(page.Chart("gpu"), EmptyIndex(page, lastProcessMinute));
        WaitFor(() => !card.Loading && card.Slot is not null);
        Assert.Contains(NoProcessData, Texts(card)); Assert.DoesNotContain(NoGpuData, Texts(card));
        Assert.False(IsShown(Table(card), card));
        Assert.True(IsShown(Button(card, "Clear"), card));
        Click(Button(card, "Clear"));
    }

    private static void Clearing_the_dashboard_gpu_keeps_the_listed_gpu_moment(UsageHistoryPage page, ApplicationResourcesCard card)
    {
        // Review Focus 4: the dashboard's GPU selection is cleared while a GPU moment is listed. The GPU chart loses its points and its
        // hairline; the CPU and RAM hairlines and the listed slot stay.
        WithGpu(page);
        Activate(page.Chart("gpu"), 1); WaitFor(() => card.Slot is not null);
        var slot = card.Slot; var gpu = page.Chart("gpu");
        page.Apply(WithoutGpu(Data(page)));
        Assert.Null(gpu.SelectedIndex); Assert.Equal(1, page.Chart("cpu").SelectedIndex); Assert.Equal(1, page.Chart("memory").SelectedIndex);
        Assert.Equal(slot, card.Slot);
        Layout(page);
        Assert.Equal(0, Hairlines(gpu)); Assert.Equal(1, Hairlines(page.Chart("cpu"))); Assert.Equal(1, Hairlines(page.Chart("memory")));
        // The empty GPU chart has no point to click (Activate would assert one) and its keys do nothing.
        gpu.HandleClick(new(gpu.PlotRect.Left + gpu.PlotRect.Width / 2, gpu.PlotRect.Top + gpu.PlotRect.Height / 2));
        Assert.False(gpu.HandleKey(Key.Right)); Assert.False(gpu.HandleKey(Key.Enter));
        Assert.Null(gpu.SelectedIndex); Assert.Equal(slot, card.Slot); Assert.Equal(1, page.Chart("cpu").SelectedIndex);
        // The CPU chart's cursor still moves the RAM chart's, never the GPU chart's, and Esc clears the slot.
        Press(page.Chart("cpu"), Key.Right);
        Assert.Equal(2, page.Chart("memory").SelectedIndex); Assert.Null(gpu.SelectedIndex);
        Press(page.Chart("cpu"), Key.Escape);
        Assert.Null(card.Slot);
        // Like the other page checks, it ends on the day ranking by CPU time, with no read left waiting.
        SetRank(card, ProcessRank.CpuTime);
        Shows_the_day_ranking(card, ["chrome.exe", "Code.exe", "steam.exe", "explorer.exe", "Everything else"]);
    }

    private static void Slot_heading_formats()
    {
        static DateTimeOffset L(int hour, int minute, int day = 3) => new(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Local));
        Assert.Equal("Busiest apps · 03:13–03:16", ApplicationResourcesCard.SlotHeading(L(3, 13), L(3, 16), false));
        Assert.Equal("Busiest apps · Nov 3 03:05–03:28", ApplicationResourcesCard.SlotHeading(L(3, 5), L(3, 28), true));
        // A Month slot that crosses midnight names both dates.
        Assert.Equal("Busiest apps · Nov 3 23:30–Nov 4 01:06", ApplicationResourcesCard.SlotHeading(L(23, 30), L(1, 6, 4), true));
    }

    private static void A_slot_that_cannot_be_read_says_why_and_clear_returns(string folder)
    {
        string junk = Path.Combine(folder, "junk-slot"); Directory.CreateDirectory(junk);
        string path = Path.Combine(junk, "process-history.db"); File.WriteAllText(path, "not a database");
        var card = new ApplicationResourcesCard(Session(new ProcessHistoryService(path, Machine), folder));
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        card.Load(from, to, Points);
        WaitFor(() => !card.Loading);
        // Rows on screen first; the slot's failed read is reported like the period's, never as a span without process data.
        card.Apply(View(from, to, 50), to);
        card.ShowSlot(to.AddMinutes(-30), to.AddMinutes(-27), ProcessRank.CpuTime);
        WaitFor(() => !card.Loading);
        var texts = Texts(card);
        Assert.Contains("Application resources could not be read.", texts);
        Assert.Contains(texts, t => t.Contains("(InvalidDataException)", StringComparison.Ordinal));
        Assert.DoesNotContain(NoProcessData, texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("process-history.db", StringComparison.Ordinal));
        Assert.Null(card.View);
        Assert.Empty(RowNames(card));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of period total").Visibility);
        Assert.StartsWith("Busiest apps · ", Heading(card));
        Assert.True(IsShown(Button(card, "Clear"), card));
        Click(Button(card, "Clear"));
        Assert.Equal("Application resources", Heading(card));
        Assert.False(IsShown(Button(card, "Clear"), card));
        WaitFor(() => !card.Loading);
        Assert.Contains("Application resources could not be read.", Texts(card));
        // Without process history (fixtures), the slot says the history is not available.
        var missing = new ApplicationResourcesCard(Session(null, folder));
        missing.ShowSlot(to.AddMinutes(-30), to.AddMinutes(-27), ProcessRank.AverageMemory);
        Assert.False(missing.Loading);
        Assert.Equal(ProcessRank.AverageMemory, missing.Rank);
        Assert.Contains("Application resource history is not available.", Texts(missing));
        Assert.StartsWith("Busiest apps · ", Heading(missing));
    }

    private static void Gap_notice_starts_at_the_retention_cutoff(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 30, TimeSpan.Zero); var cutoff = ProcessHistoryStore.RetentionCutoff(now);
        // A day that straddles the cutoff can hold only its 720 minutes after it. 600 were recorded, so 120 are gaps, and a sentence
        // says why the earlier minutes are missing instead of blaming monitoring.
        card.Apply(View(cutoff.AddHours(-12), cutoff.AddHours(12), 600), now);
        Assert.Contains("120 " + GapEnding, Texts(card));
        Assert.Single(Texts(card), t => t == KeptForThirtyDays);
        // A period wholly before the cutoff says so in place of "nothing recorded", and has no gaps.
        card.Apply(View(cutoff.AddDays(-2), cutoff.AddDays(-1), 0), now);
        var texts = Texts(card);
        Assert.Single(texts, t => t == KeptForThirtyDays);
        Assert.DoesNotContain(NothingRecorded, texts);
        Assert.DoesNotContain(texts, t => t.EndsWith(GapEnding, StringComparison.Ordinal));
        // Inside the 30 days nothing changes.
        card.Apply(View(now.AddDays(-1), now, 0), now);
        texts = Texts(card);
        Assert.Contains(NothingRecorded, texts);
        Assert.Contains("1,439 " + GapEnding, texts);
        Assert.DoesNotContain(KeptForThirtyDays, texts);
    }

    private static void More_applications_than_the_rows_read_say_top_n_of_m(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        // The period holds 320 applications and the read returned its top rows (one here): both numbers come from the view.
        card.Apply(View(from, to, 50, applications: 320), to);
        Assert.Contains("Top 1 of 320 applications · 50 recorded minutes", Texts(card));
        SetSearch(card, "a.exe"); Assert.Contains("1 of the top 1 applications match.", Texts(card));
        SetSearch(card, "");
        // Every application read: the summary and the search count are unchanged.
        card.Apply(View(from, to, 50), to);
        Assert.Contains("1 applications · 50 recorded minutes", Texts(card));
        SetSearch(card, "a.exe"); Assert.Contains("1 of 1 applications match.", Texts(card));
        SetSearch(card, "");
    }

    private static void A_failed_series_read_keeps_the_chart_name(string folder)
    {
        string junk = Path.Combine(folder, "junk-series"); Directory.CreateDirectory(junk);
        string path = Path.Combine(junk, "process-history.db"); File.WriteAllText(path, "not a database");
        var card = new ApplicationResourcesCard(Session(new ProcessHistoryService(path, Machine), folder));
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        card.Apply(View(from, to, 50), to); Layout(card);
        Select(card, "a.exe");
        Assert.Equal("a.exe CPU history", AutomationProperties.GetName(card.Chart));
        WaitFor(() => Texts(card).Contains("a.exe · CPU % of all logical processors · could not be read"));
        // The plot is empty and the title says why; a screen reader still hears which app and metric the chart is for.
        Assert.Empty(Traces(card));
        Assert.Equal("a.exe CPU history", AutomationProperties.GetName(card.Chart));
    }

    private static void Clear_without_a_chart_hands_its_focus_to_rank_by(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        var to = DateTimeOffset.UtcNow;
        card.ShowSlot(to.AddMinutes(-30), to.AddMinutes(-27), ProcessRank.CpuTime);
        var clear = Button(card, "Clear"); var scope = FocusManager.GetFocusScope(clear);
        FocusManager.SetFocusedElement(scope, clear);
        Click(clear);
        Assert.False(IsShown(clear, card));
        Assert.Same(RankBy(card), FocusManager.GetFocusedElement(scope));
    }

    private static void The_minute_refresh_updates_the_rows_in_place_and_keeps_the_current_cell(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder)); var to = DateTimeOffset.UtcNow;
        // The same day each minute; each refresh adds to the forty apps' CPU time.
        ProcessResourceView Ranking(int extra, bool app39First = false) => FortyApps(to.AddDays(-1), to, extra, app39First);
        card.Apply(Ranking(0), to); Layout(card);
        var table = Table(card); var scroller = Descendants(table).OfType<ScrollViewer>().First();
        scroller.ScrollToBottom(); WaitFor(() => { Layout(card); return scroller.VerticalOffset > 0; }); double offset = scroller.VerticalOffset;
        Select(card, "app39.exe"); var row = Rows(card).Single(r => Cell(r, "Name") == "app39.exe"); var column = table.Columns[1];
        table.CurrentCell = new DataGridCellInfo(row, column); Layout(card);
        var container = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(row); var cell = (DataGridCell)column.GetCellContent(container).Parent;
        var scope = FocusManager.GetFocusScope(cell); FocusManager.SetFocusedElement(scope, cell);
        // The next minute: the same rows with new numbers, so the row, its container, the cell with the focus and the scroll offset stay.
        card.Apply(Ranking(60), to); Layout(card);
        Assert.Same(row, Rows(card).Single(r => Cell(r, "Name") == "app39.exe")); Assert.Equal(ApplicationResourcesCard.Duration(421), Cell(row, "CpuTime"));
        Assert.Same(container, table.ItemContainerGenerator.ContainerFromItem(row)); Assert.Equal(ApplicationResourcesCard.Duration(421), ((TextBlock)cell.Content).Text);
        Assert.True(cell.IsDescendantOf(table)); Assert.Same(cell, FocusManager.GetFocusedElement(scope));
        Assert.Same(row, table.SelectedItem); Assert.Same(row, table.CurrentCell.Item); Assert.Same(column, table.CurrentCell.Column);
        Assert.Equal(offset, scroller.VerticalOffset, 3); Assert.Equal("app39.exe", card.SelectedApp);
        // A new order moves the row itself, still selected.
        card.Apply(Ranking(120, app39First: true), to); Layout(card);
        Assert.Equal("app39.exe", RowNames(card)[0]); Assert.Equal("Everything else", LastRowName(card)); Assert.Equal(41, Rows(card).Count);
        Assert.Same(row, Rows(card)[0]); Assert.Same(row, table.SelectedItem);
        // Review Focus 5: a search that hides the selected row keeps the app selected; clearing it selects the row again.
        SetSearch(card, "app1"); Assert.DoesNotContain("app39.exe", RowNames(card)); Assert.Equal("app39.exe", card.SelectedApp);
        SetSearch(card, ""); Assert.Equal("app39.exe", Cell(table.SelectedItem!, "Name")); Assert.Equal("app39.exe", card.SelectedApp);
        FocusManager.SetFocusedElement(scope, null);
    }

    private static void A_new_ranking_starts_the_table_at_the_top(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder)); var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        card.Apply(FortyApps(from, to), to); Layout(card);
        var scroller = Descendants(Table(card)).OfType<ScrollViewer>().First();
        void ScrollDown() { scroller.ScrollToBottom(); WaitFor(() => { Layout(card); return scroller.VerticalOffset > 0; }); }
        // Every view here lists the same forty apps, so the rows stay where they are and only the new ranking can move the table to its top.
        void StartsAtTheTop(ProcessResourceView view) { card.Apply(view, to); WaitFor(() => { Layout(card); return scroller.VerticalOffset == 0; }); }
        // Rank by: with no period loaded it starts no read, and the next view is ranked by average memory.
        ScrollDown(); SetRank(card, ProcessRank.AverageMemory); StartsAtTheTop(FortyApps(from, to));
        // ◀ Earlier: the same length a day back. A range change: a week.
        ScrollDown(); StartsAtTheTop(FortyApps(from.AddDays(-1), from));
        ScrollDown(); StartsAtTheTop(FortyApps(to.AddDays(-7), to));
        // A listed slot, as ShowSlot holds it while its read runs: period to slot; the same slot read again by the minute refresh keeps
        // the offset; another slot; back to the period (Clear or Esc).
        (DateTimeOffset From, DateTimeOffset To) first = (to.AddMinutes(-30), to.AddMinutes(-27)), second = (to.AddMinutes(-20), to.AddMinutes(-17));
        ScrollDown(); SetSlot(card, first); StartsAtTheTop(FortyApps(first.From, first.To));
        ScrollDown(); double offset = scroller.VerticalOffset;
        card.Apply(FortyApps(first.From, first.To, extra: 60), to); Layout(card);
        Assert.Equal(offset, scroller.VerticalOffset, 3);
        SetSlot(card, second); StartsAtTheTop(FortyApps(second.From, second.To));
        ScrollDown(); SetSlot(card, null); StartsAtTheTop(FortyApps(from, to));
    }

    private static void Gpu_rank_columns_and_header_tooltips(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        Assert.Equal(["CPU time", "Average memory", "I/O", "GPU"], RankLabels(card));
        card.Apply(GpuView(43_440, 10, 20), DateTimeOffset.UtcNow);
        Assert.Equal("GPU recorded for 12 h 4 min of this range", HeaderTip(card, "Avg GPU"));
        Assert.Equal("GPU recorded for 12 h 4 min of this range", HeaderTip(card, "Avg GPU memory while listed"));
        // The minute refresh gives the headers already shown the new range's GPU time.
        card.Apply(GpuView(60, 10, 20), DateTimeOffset.UtcNow);
        Assert.Equal("GPU recorded for 1 min 0 s of this range", HeaderTip(card, "Avg GPU"));
        Assert.Equal("GPU recorded for 1 min 0 s of this range", HeaderTip(card, "Avg GPU memory while listed"));
        // Their explicit styles are based on the theme's, so the GPU headers and cells keep its captions, padding and selection colours.
        Assert.Equal(HeaderOf(card, "Avg CPU").FontSize, HeaderOf(card, "Avg GPU").FontSize);
        Assert.Equal(HeaderOf(card, "Avg CPU").Padding, HeaderOf(card, "Avg GPU memory while listed").Padding);
        Assert.Equal(CellOf(card, "chrome.exe", "Avg CPU").Padding, CellOf(card, "chrome.exe", "Avg GPU").Padding);
        Assert.Equal(CellOf(card, "chrome.exe", "Avg CPU").Padding, CellOf(card, "chrome.exe", "Avg GPU memory while listed").Padding);
        Assert.Equal(["10 %", "1.0 MB"], new[] { "AverageGpu", "AverageGpuMemory" }.Select(p => Cell(Row(card, "chrome.exe"), p)));
        // A shown value has no tooltip.
        Assert.Null(Tip(card, "chrome.exe", "Avg GPU")); Assert.Null(Tip(card, "chrome.exe", "Avg GPU memory while listed"));
        // When the tip clears, the GPU headers take the theme's header style again: 10 px captions and 9,8 padding.
        typeof(ApplicationResourcesCard).GetMethod("Unavailable", All)!.Invoke(card, ["History unavailable"]);
        foreach (string gpu in new[] { "Avg GPU", "Avg GPU memory while listed" })
        {
            var header = HeaderOf(card, gpu);
            Assert.Null(header.ToolTip);
            Assert.True(header.FontSize == 10 && header.Padding == new Thickness(9, 8, 9, 8) && header.FontSize == HeaderOf(card, "Avg CPU").FontSize,
                $"{gpu} header lost the theme's caption look after its tip cleared: {header.FontSize} px, padding {header.Padding}.");
        }
    }

    private static void Gpu_dashes_name_their_reason(string folder)
    {
        var card = new ApplicationResourcesCard(Session(null, folder));
        card.Apply(GpuView(0, null, null), DateTimeOffset.UtcNow);
        Assert.Equal("—", Cell(Row(card, "chrome.exe"), "AverageGpu"));
        Assert.Equal("No GPU data in this range", Tip(card, "chrome.exe", "Avg GPU"));
        // The minute refresh keeps the row: the same "—" now has GPU time in the range, so only its reason changes.
        var chrome = Row(card, "chrome.exe");
        card.Apply(GpuView(60, null, 30), DateTimeOffset.UtcNow);
        Assert.Same(chrome, Row(card, "chrome.exe"));
        Assert.Equal("—", Cell(chrome, "AverageGpuMemory"));
        Assert.Equal("No GPU data in the minutes this app was listed", Tip(card, "chrome.exe", "Avg GPU memory while listed"));
        Assert.Equal("No GPU data in the minutes this app was listed", Tip(card, "chrome.exe", "Avg GPU"));
        card.Apply(GpuView(60, 10, 125), DateTimeOffset.UtcNow);
        Assert.Equal("125 %", Cell(Row(card, "Everything else"), "AverageGpu"));
        Assert.Equal("Sum over many apps; can exceed 100 %", Tip(card, "Everything else", "Avg GPU"));
    }

    private static void Rank_by_gpu_over_version_1_minutes_shows_dashes_and_no_share_bar(string folder)
    {
        // Review Focus 5: the first day after the upgrade, over minutes saved without GPU data. code.exe leads by CPU time, memory and I/O,
        // so only the name order puts chrome.exe first.
        string root = Path.Combine(folder, "gpu-v1"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "process-history.db");
        var store = new ProcessHistoryStore(path); store.Initialize();
        var now = DateTimeOffset.UtcNow; var minute = UsageMetrics.Floor(now);
        ProcessAppMinute[] apps =
        [
            new("chrome.exe", 10_000_000, 100_000, 100_000, 100e6 * 60, false),
            new("code.exe", 60_000_000, 5_000_000, 5_000_000, 900e6 * 60, false),
            new(ProcessHistory.EverythingElse, 5_000_000, 1_000_000, 1_000_000, 1e9 * 60, true),
        ];
        store.Append(Machine, Enumerable.Range(0, 60).Select(i => new ProcessMinuteBatch(minute.AddMinutes(i - 70), 60, 8, true, "fixture", apps)).ToList(), now);
        var card = new ApplicationResourcesCard(Session(new ProcessHistoryService(path, Machine), folder));
        card.Load(now.AddDays(-1), now, Points);
        SetRank(card, ProcessRank.Gpu);
        WaitFor(() => !card.Loading && card.Rank == ProcessRank.Gpu && card.View is { GpuCoveredSeconds: 0 });
        Assert.Equal(["chrome.exe", "code.exe", "Everything else"], RowNames(card));
        Assert.All(Rows(card), r => Assert.Equal("—", Cell(r, "AverageGpu")));
        Assert.Equal("No GPU data in this range", Tip(card, "chrome.exe", "Avg GPU"));
        Assert.Equal("No GPU data in this range", Tip(card, "Everything else", "Avg GPU"));
        Assert.Equal(Visibility.Collapsed, Named(card, "Share of period total").Visibility);
        Select(card, "chrome.exe");
        WaitFor(() => Traces(card).Count == 1);
        Assert.Empty(Traces(card)[0].View.Buckets);
    }

    private static void A_gpu_slot_with_gpu_data_lists_apps_by_gpu_and_everything_else(string folder)
    {
        // One minute, ten minutes ago, with 60 GPU-covered seconds. a.exe and b.exe have the same CPU time, memory and I/O, so only the GPU
        // ranking puts b.exe first. Without a ProcessHistory service the card would only say "Application resource history is not available."
        string root = Path.Combine(folder, "gpu-slot"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "process-history.db");
        var store = new ProcessHistoryStore(path); store.Initialize();
        var now = DateTimeOffset.UtcNow; var minute = UsageMetrics.Floor(now).AddMinutes(-10);
        ProcessAppMinute[] apps =
        [
            new("a.exe", 10_000_000, 0, 0, 100e6 * 60, false) { GpuSeconds = 6 },
            new("b.exe", 10_000_000, 0, 0, 100e6 * 60, false) { GpuSeconds = 30 },
            new(ProcessHistory.EverythingElse, 5_000_000, 0, 0, 1e9 * 60, true) { GpuSeconds = 3 },
        ];
        store.Append(Machine, [new ProcessMinuteBatch(minute, 60, 8, true, "fixture", apps) { GpuCoveredSeconds = 60 }], now);
        var card = new ApplicationResourcesCard(Session(new ProcessHistoryService(path, Machine), folder));
        card.ShowSlot(minute, minute.AddMinutes(1), ProcessRank.Gpu);
        WaitFor(() => !card.Loading && card.View is not null);
        Assert.Equal(ProcessRank.Gpu, card.Rank);
        Assert.Equal(["b.exe", "a.exe", "Everything else"], RowNames(card));
        // Avg GPU is a share of the slot's GPU-covered seconds; Everything else's included.
        Assert.Equal("50 %", Cell(Row(card, "b.exe"), "AverageGpu"));
        Assert.Equal("10 %", Cell(Row(card, "a.exe"), "AverageGpu"));
        Assert.Equal("5 %", Cell(Row(card, "Everything else"), "AverageGpu"));
        // The span has GPU data, so it shows its table.
        Assert.DoesNotContain(NoGpuData, Texts(card));
        Assert.True(IsShown(Table(card), card));
    }

    private static void Status_line_follows_the_recording_switches_without_a_read(string folder)
    {
        string root = Path.Combine(folder, "status"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "process-history.db");
        var service = new ProcessHistoryService(path, Machine);
        var session = Session(service, folder);
        // The page's switch saves the settings, so the session gets somewhere to write them.
        typeof(UiSession).GetField("layout", All)!.SetValue(session, new DetachedWindowState([]));
        typeof(UiSession).GetField("writer", All)!.SetValue(session, new SettingsSnapshotWriter(Path.Combine(root, "settings.json")));
        service.Configure(session.Settings.ProcessMonitoring, session.Settings.UsageRecording);
        // Laid out first, so the page's template puts its cards and the switch in the visual tree; it is still never Loaded, so nothing reads.
        var page = new UsageHistoryPage(session); Layout(page);
        var card = Assert.Single(Descendants(page).OfType<ApplicationResourcesCard>());
        // The status line shows at once and follows each switch while the page is shown, not only when the card next reads.
        Assert.Equal(service.Status, Status(card)); Assert.DoesNotContain(Paused, Status(card));
        var toggle = Descendants(page).OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "Record usage history");
        toggle.IsChecked = false;
        Assert.False(session.Settings.UsageRecording);
        Assert.Contains(Paused, Status(card));
        toggle.IsChecked = true;
        Assert.Equal(service.Status, Status(card)); Assert.DoesNotContain(Paused, Status(card));
        // Per-process monitoring switched off and on elsewhere (Settings or the Processes page) reaches the page through session.Changed.
        service.Configure(false, true); RefreshCurrent(page);
        Assert.Contains(Paused, Status(card));
        service.Configure(true, true); RefreshCurrent(page);
        Assert.Equal(service.Status, Status(card)); Assert.DoesNotContain(Paused, Status(card));
        // None of this read process-history.db: the file was never even created.
        Assert.False(File.Exists(path));
        var save = session.SaveAsync(); WaitFor(() => save.IsCompleted);
    }

    /// <summary>Waits for the card's period ranking of the Day range and checks its rows, summary, share bar and table.</summary>
    private static void Shows_the_day_ranking(ApplicationResourcesCard card, string[] names)
    {
        WaitFor(() => !card.Loading && card.View is { } shown && shown.To - shown.From == TimeSpan.FromDays(1));
        Assert.Null(card.Slot);
        Assert.Equal("Application resources", Heading(card));
        Assert.Equal(names, RowNames(card));
        Assert.Contains($"4 applications · {DayMinutes} recorded minutes", Texts(card));
        Assert.Equal(Visibility.Visible, Named(card, "Share of period total").Visibility);
        Assert.True(IsShown(Table(card), card));
    }

    /// <summary>Forty apps ranked by CPU time, app00.exe first, and Everything else: more rows than the table shows at once.
    /// <paramref name="extra"/> adds to each app's CPU time; with <paramref name="app39First"/>, app39.exe has overtaken the rest and is
    /// listed first.</summary>
    private static ProcessResourceView FortyApps(DateTimeOffset from, DateTimeOffset to, int extra = 0, bool app39First = false)
    {
        var rows = Enumerable.Range(0, 40).Select(i => new ProcessResourceRow($"app{i:00}.exe", 400 - i + extra, 1, 1e6, 1, 1, 60, false, 6e7)).ToList();
        if (app39First) { rows.RemoveAt(39); rows.Insert(0, new("app39.exe", 1000, 1, 1e6, 1, 1, 60, false, 6e7)); }
        var everythingElse = new ProcessResourceRow(ProcessHistory.EverythingElse, 10, 1, 1e6, 1, 1, 60, true, 6e7);
        return new(from, to, rows, 40, everythingElse, rows.Sum(r => r.CpuSeconds) + everythingElse.CpuSeconds, 60, from, to, from, 4096);
    }

    /// <summary>Sets the slot the card lists, as <see cref="ApplicationResourcesCard.ShowSlot"/> does before its read, without starting a
    /// read; null is the period ranking, as after <see cref="ApplicationResourcesCard.ClearSlot"/>.</summary>
    private static void SetSlot(ApplicationResourcesCard card, (DateTimeOffset From, DateTimeOffset To)? slot)
        => typeof(ApplicationResourcesCard).GetField("slot", All)!.SetValue(card, slot);

    private static ProcessResourceView View(DateTimeOffset from, DateTimeOffset to, long recorded, long? applications = null)
        => new(from, to, recorded == 0 ? [] : [new("a.exe", 60, 1, 1e6, 1, 1, recorded, false, 6e7)], applications ?? (recorded == 0 ? 0 : 1),
            new(ProcessHistory.EverythingElse, recorded, 1, 1e6, 1, 1, recorded, true, recorded * 6e7), recorded == 0 ? 0 : 60 + recorded,
            recorded, recorded == 0 ? null : from, recorded == 0 ? null : to, recorded == 0 ? null : from, 4096);

    /// <summary>The last day's 60 recorded minutes with <paramref name="covered"/> GPU-covered seconds: chrome.exe and Everything else at the
    /// given Avg GPU, with GPU-seconds to match and 1 MB of GPU memory, or no GPU data when the value is null.</summary>
    private static ProcessResourceView GpuView(double covered, double? chrome, double? everythingElse)
    {
        var to = DateTimeOffset.UtcNow; var from = to.AddDays(-1);
        ProcessResourceRow Gpu(ProcessResourceRow row, double? percent)
            => row with { GpuSeconds = percent * covered / 100, AverageGpuPercent = percent, AverageGpuMemoryBytes = percent is null ? null : 1e6 };
        return new(from, to, [Gpu(new("chrome.exe", 60, 1, 1e6, 1, 1, 60, false, 6e7), chrome)], 1,
            Gpu(new(ProcessHistory.EverythingElse, 60, 1, 1e6, 1, 1, 60, true, 60 * 6e7), everythingElse), 120, 60, from, to, from, 4096) { GpuCoveredSeconds = covered };
    }

    /// <summary>A process-history.db with a fixture day (90 minutes ending ten minutes before <paramref name="minute"/>) and 30 minutes
    /// three days ago, read through a service whose store counts every read and can hold one read until released.</summary>
    private static ProcessHistoryService Service(string folder, DateTimeOffset minute)
    {
        string path = Path.Combine(folder, "process-history.db");
        var store = new ProcessHistoryStore(path); store.Initialize();
        var now = DateTimeOffset.UtcNow;
        var batches = Enumerable.Range(0, DayMinutes).Select(i => new ProcessMinuteBatch(minute.AddMinutes(i - DayMinutes - 10), 60, 8, true, "fixture", DayApps))
            .Concat(Enumerable.Range(0, 30).Select(i => new ProcessMinuteBatch(minute.AddDays(-3).AddMinutes(i), 60, 8, true, "fixture",
                [new("backup.exe", 20_000_000, 400_000_000, 300_000_000, 200e6 * 60, false), new(ProcessHistory.EverythingElse, 1_000_000, 0, 0, 1e9 * 60, true)])))
            .ToList();
        store.Append(Machine, batches, now);
        var service = new ProcessHistoryService(path, Machine);
        var reader = typeof(ProcessHistoryService).GetField("store", All)!.GetValue(service)!;
        typeof(ProcessHistoryStore).GetField("afterFirstRead", All)!.SetValue(reader, new Action(() =>
        {
            Interlocked.Increment(ref reads);
            if (Interlocked.Exchange(ref hold, null) is { } gate) gate.Wait(TimeSpan.FromSeconds(10));
        }));
        return service;
    }

    /// <summary>A usage-history.db with CPU and RAM minutes for the fixture day's 90 process minutes and the ten minutes after them, which
    /// were recorded while monitoring was off and so have no process data.</summary>
    private static UsageHistoryService DeviceHistory(string folder, DateTimeOffset minute)
    {
        string path = Path.Combine(folder, "usage-history.db");
        var store = new UsageHistoryStore(path); store.Initialize();
        var batches = Enumerable.Range(0, DayMinutes + 10).Select(i => minute.AddMinutes(i - DayMinutes - 10)).Select(at => new UsageMinuteBatch(at,
            [new(UsageMetrics.Cpu, null, at, 40, 20, 60, 60, true, "fixture"), new(UsageMetrics.Memory, null, at, 55, 50, 60, 60, true, "fixture")],
            [], false, "fixture")).ToList();
        store.Append(Machine, batches, DateTimeOffset.UtcNow);
        return new UsageHistoryService(path, Machine);
    }

    private static UiSession Session(ProcessHistoryService? history, string folder, UsageHistoryService? usage = null)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        typeof(Collector).GetField("clock", All)!.SetValue(collector, new Stopwatch());
        typeof(Collector).GetField("latest", All)!.SetValue(collector, new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()),
            [], [], [], "CPU", "", "", "", false));
        void Set(string name, object value) => typeof(UiSession).GetField(name, All)!.SetValue(session, value);
        Set("<Collector>k__BackingField", collector);
        Set("<Settings>k__BackingField", new UserSettings { ProcessMonitoring = true });
        Set("<Usage>k__BackingField", usage ?? new UsageHistoryService(Path.Combine(folder, Guid.NewGuid() + "-usage.db"), Machine));
        if (history is not null) Set("<ProcessHistory>k__BackingField", history);
        return session;
    }

    /// <summary>Runs the page's load, as its refresh does once a range or earlier/later change has updated its period.</summary>
    private static void Load(UsageHistoryPage page)
    {
        var load = (Task<UsagePageData>)typeof(UsageHistoryPage).GetMethod("LoadAsync", All)!.Invoke(page, [])!;
        WaitFor(() => load.IsCompleted);
        page.Apply(load.Result);
    }
    private static void RefreshCurrent(UsageHistoryPage page) => typeof(UsageHistoryPage).GetMethod("RefreshCurrent", All)!.Invoke(page, []);
    private static string Status(ApplicationResourcesCard card) => ((TextBlock)typeof(ApplicationResourcesCard).GetField("status", All)!.GetValue(card)!).Text;
    private static (DateTimeOffset From, DateTimeOffset To) Period(UsageHistoryPage page)
        => ((DateTimeOffset, DateTimeOffset))typeof(UsageHistoryPage).GetMethod("Period", All)!.Invoke(page, [])!;
    private static (DateTimeOffset From, DateTimeOffset To) PageData(UsageHistoryPage page) => (Data(page).From, Data(page).To);
    /// <summary>What the page last applied.</summary>
    private static UsagePageData Data(UsageHistoryPage page) => (UsagePageData)typeof(UsageHistoryPage).GetField("data", All)!.GetValue(page)!;
    /// <summary>Applies the page's data as it reads with a GPU selected on the dashboard: "Fixture GPU", whose view is the CPU view, so the GPU
    /// chart has the CPU chart's buckets. The fixture's process minutes hold no GPU data.</summary>
    private static void WithGpu(UsageHistoryPage page)
    {
        var data = Data(page);
        page.Apply(data with { GpuName = "Fixture GPU",
            Metrics = new Dictionary<string, UsageHistoryView>(data.Metrics, StringComparer.Ordinal) { [UsageMetrics.Gpu] = data.Metrics[UsageMetrics.Cpu] } });
    }
    /// <summary>The data as the page reads it with no GPU selected on the dashboard: no GPU name and no GPU view.</summary>
    private static UsagePageData WithoutGpu(UsagePageData data) => data with { GpuName = null,
        Metrics = data.Metrics.Where(p => p.Key != UsageMetrics.Gpu).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
    /// <summary>The CPU chart's last bucket. It lies in the fixture's last ten Device minutes, after <paramref name="lastProcessMinute"/>, which were
    /// recorded while monitoring was off: it has CPU readings but no process data.</summary>
    private static int EmptyIndex(UsageHistoryPage page, DateTimeOffset lastProcessMinute)
    {
        var cpu = page.Chart("cpu"); int index = Traces(cpu)[0].View.Buckets.Count - 1; var bucket = Bucket(cpu, index);
        Assert.True(bucket.Start > lastProcessMinute && bucket.Minutes > 0);
        return index;
    }
    private static void Near((DateTimeOffset From, DateTimeOffset To) period, ProcessResourceView view)
    {
        Assert.InRange((period.From - view.From).Duration(), TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.Equal(period.To - period.From, view.To - view.From);
    }
    private static void SelectRange(UsageHistoryPage page, string label)
    {
        var range = Descendants(page).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Usage history time range");
        range.SelectedItem = range.Items.Cast<object>().Single(item => item.ToString() == label);
    }
    private static Button ButtonNamed(DependencyObject root, string name) => Descendants(root).OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static Button Button(DependencyObject root, string content) => Descendants(root).OfType<Button>().Single(b => Equals(b.Content, content));
    private static string Heading(ApplicationResourcesCard card) => Descendants(card).OfType<TextBlock>().First().Text;
    private static DispatcherTimer Timer(ApplicationResourcesCard card, string name) => (DispatcherTimer)typeof(ApplicationResourcesCard).GetField(name, All)!.GetValue(card)!;
    private static DateTimeOffset Ceiling(DateTimeOffset time)
        => new((time.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>Clicks a Device chart where its bucket <paramref name="index"/> is drawn, through the chart's own click handling (pages here
    /// have no input source, so the mouse event itself cannot be raised).</summary>
    private static void Activate(UsageHistoryChart chart, int index)
    {
        // Laid out again at its current size, so the chart has drawn its latest data.
        var root = Root(chart);
        Layout(root, root.RenderSize is { Width: > 0, Height: > 0 } size ? size : PageSize);
        var plot = chart.PlotRect; double y = plot.Top + plot.Height / 2;
        double? x = null;
        for (double at = plot.Left; at <= plot.Right && x is null; at += 0.05) if (chart.BucketAt(new(at, y)) == index) x = at;
        Assert.True(x.HasValue, $"No point on the chart selects bucket {index}.");
        chart.HandleClick(new(x.Value, y));
        Assert.Equal(index, chart.SelectedIndex);
    }
    private static UsageHistoryBucket Bucket(UsageHistoryChart chart, int index) => Traces(chart)[0].View.Buckets[index];
    /// <summary>Bucket <paramref name="k"/> of the view's grid, one recorded minute, for a refresh whose grid holds a slot the fixture's does not.</summary>
    private static UsageHistoryBucket At(UsageHistoryView view, int k) => new(view.From.AddMilliseconds(k * view.BucketMilliseconds),
        view.From.AddMilliseconds((k + 1) * view.BucketMilliseconds), 40, 20, 60, 1, 1, 60);
    /// <summary>Accent hairlines the chart drew at its last render.</summary>
    private static int Hairlines(UsageHistoryChart chart)
    {
        var accent = Application.Current.Resources["Accent"];
        return Drawings(VisualTreeHelper.GetDrawing(chart)).Count(d => d.Geometry is LineGeometry line && line.StartPoint.X == line.EndPoint.X && ReferenceEquals(d.Pen?.Brush, accent));
    }
    /// <summary>Accent frames the chart drew at its last render: its keyboard focus cue.</summary>
    private static int FocusFrames(UsageHistoryChart chart)
    {
        var accent = Application.Current.Resources["Accent"];
        return Drawings(VisualTreeHelper.GetDrawing(chart)).Count(d => d.Geometry is RectangleGeometry && ReferenceEquals(d.Pen?.Brush, accent));
    }
    /// <summary>Tells an element its keyboard focus changed, as the keyboard does; offscreen nothing can take keyboard focus itself.</summary>
    private static void KeyboardFocus(UIElement element, bool focused)
        => typeof(UIElement).GetMethod("OnIsKeyboardFocusedChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(DependencyPropertyChangedEventArgs)])!
            .Invoke(element, [new DependencyPropertyChangedEventArgs(UIElement.IsKeyboardFocusedProperty, !focused, focused)]);
    private static IEnumerable<GeometryDrawing> Drawings(Drawing? drawing)
        => drawing is GeometryDrawing geometry ? [geometry] : drawing is DrawingGroup group ? group.Children.SelectMany(Drawings) : [];
    private static FrameworkElement Root(DependencyObject element)
    {
        while (VisualTreeHelper.GetParent(element) is { } parent) element = parent;
        return (FrameworkElement)element;
    }
    /// <summary>Presses a key on an element as the keyboard does: the preview first, then the key itself unless it was handled.</summary>
    private static void Press(UIElement target, Key key)
    {
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }
    /// <summary>The input source a key event must name; it is never added to the sources, and offscreen nothing reads it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get => null!; set { } }
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed => false;
    }

    private static DataGrid Table(ApplicationResourcesCard card) => Assert.Single(Descendants(card).OfType<DataGrid>());
    private static List<object> Rows(ApplicationResourcesCard card) => ((System.Collections.IEnumerable?)Table(card).ItemsSource ?? Array.Empty<object>()).Cast<object>().ToList();
    private static List<string> RowNames(ApplicationResourcesCard card) => Rows(card).Select(r => Cell(r, "Name")).ToList();
    private static string LastRowName(ApplicationResourcesCard card) => RowNames(card)[^1];
    private static string Cell(object row, string property) => (string)row.GetType().GetProperty(property)!.GetValue(row)!;
    private static string[] Headers(ApplicationResourcesCard card) => Table(card).Columns.Select(c => (string)c.Header).ToArray();
    private static object Row(ApplicationResourcesCard card, string name) => Rows(card).Single(r => Cell(r, "Name") == name);
    /// <summary>The realized header of a column. Cards built alone are never laid out, so the card is laid out first.</summary>
    private static DataGridColumnHeader HeaderOf(ApplicationResourcesCard card, string header)
    {
        Layout(card);
        return Descendants(Table(card)).OfType<DataGridColumnHeader>().Single(h => h.Column?.Header as string == header);
    }
    private static string? HeaderTip(ApplicationResourcesCard card, string header) => (string?)HeaderOf(card, header).ToolTip;
    /// <summary>The realized cell of an app's row in a column, so its text and tooltip come through the card's real bindings and styles.</summary>
    private static DataGridCell CellOf(ApplicationResourcesCard card, string app, string header)
    {
        Layout(card);
        var table = Table(card);
        var container = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromItem(Row(card, app)));
        var content = Assert.IsAssignableFrom<FrameworkElement>(table.Columns.Single(c => (string)c.Header == header).GetCellContent(container));
        return Assert.IsType<DataGridCell>(content.Parent);
    }
    private static string? Tip(ApplicationResourcesCard card, string app, string header) => (string?)CellOf(card, app, header).ToolTip;
    private static string[] RankLabels(ApplicationResourcesCard card) => RankBy(card).Items.Cast<object>().Select(item => item.ToString()!).ToArray();
    private static void Select(ApplicationResourcesCard card, string name) => Table(card).SelectedItem = Rows(card).Single(r => Cell(r, "Name") == name);
    private static void SetSearch(ApplicationResourcesCard card, string text)
        => Descendants(card).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Find application in application resources").Text = text;
    private static ComboBox RankBy(ApplicationResourcesCard card)
        => Descendants(card).OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Rank application resources by");
    private static void SetRank(ApplicationResourcesCard card, ProcessRank rank)
    {
        var box = RankBy(card);
        box.SelectedItem = box.Items.Cast<object>().Single(item => (ProcessRank)item.GetType().GetProperty("Rank")!.GetValue(item)! == rank);
    }
    private static string ChartUnit(ApplicationResourcesCard card) => (string)typeof(UsageHistoryChart).GetField("unit", All)!.GetValue(card.Chart)!;
    private static double? ChartMaximum(ApplicationResourcesCard card) => (double?)typeof(UsageHistoryChart).GetField("fixedMaximum", All)!.GetValue(card.Chart);
    private static IReadOnlyList<UsageTrace> Traces(ApplicationResourcesCard card) => Traces(card.Chart);
    private static IReadOnlyList<UsageTrace> Traces(UsageHistoryChart chart) => (IReadOnlyList<UsageTrace>)typeof(UsageHistoryChart).GetField("traces", All)!.GetValue(chart)!;
    private static FrameworkElement Named(DependencyObject root, string name) => Descendants(root).OfType<FrameworkElement>().Single(e => AutomationProperties.GetName(e) == name);
    private static List<Rectangle> Segments(Grid bar) => bar.Children.OfType<Rectangle>().ToList();
    private static double Fraction(Grid bar, int segment)
        => bar.ColumnDefinitions[Grid.GetColumn(Segments(bar)[segment])].Width.Value / bar.ColumnDefinitions.Sum(c => c.Width.Value);
    /// <summary>True when the element and every ancestor up to the card are visible.</summary>
    private static bool IsShown(FrameworkElement element, DependencyObject root)
    {
        for (DependencyObject? at = element; at is not null && at != root; at = VisualTreeHelper.GetParent(at))
            if (at is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    private static List<string> CardHeadings(UsageHistoryPage page)
        => ((StackPanel)page.Content).Children.OfType<Border>().Select(card => Descendants(card).OfType<TextBlock>().First().Text).ToList();
    private static List<string> Texts(DependencyObject root) => Descendants(root).OfType<TextBlock>().Select(t => t.Text).ToList();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Layout(FrameworkElement element) => Layout(element, PageSize);
    private static void Layout(FrameworkElement element, Size size)
    { element.Measure(size); element.Arrange(new(size)); element.UpdateLayout(); }
    /// <summary>The element's top in the page's viewport (the page's scroll offset included).</summary>
    private static double Top(FrameworkElement element, UsageHistoryPage page) => element.TransformToAncestor(page).Transform(new Point()).Y;

    /// <summary>Pumps the dispatcher until <paramref name="done"/> holds, for up to 5 s, so a read's result can arrive.</summary>
    private static void WaitFor(Func<bool> done)
    {
        if (!done())
        {
            var frame = new DispatcherFrame(); var deadline = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { if (done() || deadline.Elapsed >= TimeSpan.FromSeconds(5)) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        Assert.True(done(), "The Application resources card did not get there within 5 s.");
    }
}
