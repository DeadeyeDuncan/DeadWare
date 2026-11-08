using System.Globalization;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.Tests;

public class UsageHistoryStoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private static UsageMetricMinute Metric(DateTimeOffset minute, string metric, double average, string segment = "seg-a", int samples = 60,
        bool complete = true, double? minimum = null, double? maximum = null, string? hardware = null)
        => new(metric, hardware, minute, average, minimum ?? average, maximum ?? average, samples, complete, segment);

    private static UsageMinuteBatch Batch(DateTimeOffset minute, string segment = "seg-a", bool gap = false,
        IReadOnlyList<UsageMetricMinute>? metrics = null, (string Name, long Received, long Sent)[]? apps = null)
        => new(minute, metrics ?? [Metric(minute, UsageMetrics.Cpu, 10, segment)],
            (apps ?? []).Select(a => new UsageApplicationMinute(a.Name, minute, a.Received, a.Sent, segment)).ToArray(), gap, segment);

    [Fact]
    public void Reopen_preserves_rows_partitions_machines_and_refuses_newer_versions()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        store.Append("tablet", [Batch(Day, apps: [("chrome.exe", 100, 10)]), Batch(Day.AddMinutes(1))], now);
        store.Append("desktop", [Batch(Day)], now);
        var reopened = fixture.Open();
        var view = reopened.ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now);
        Assert.Equal(2, view.Minutes); Assert.Equal(Day, view.EarliestRetained); Assert.True(view.StorageBytes > 0);
        Assert.Equal(1, reopened.ReadMetric("desktop", UsageMetrics.Cpu, null, Day, now).Minutes);
        Assert.Empty(reopened.ReadApplications("desktop", Day, now).Applications);
        Assert.Equal("chrome.exe", Assert.Single(reopened.ReadApplications("tablet", Day, now).Applications).Name);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=2"; command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => fixture.Open());
    }

    [Fact]
    public void A_restart_inside_a_minute_merges_segments_but_a_retried_batch_is_ignored()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        var first = Batch(Day, "seg-a", metrics: [Metric(Day, UsageMetrics.Cpu, 10, "seg-a", 20, true, 5, 15)], apps: [("chrome.exe", 100, 10)]);
        store.Append("tablet", [first], now);
        store.Append("tablet", [first], now);
        var once = store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now);
        var bucket = Assert.Single(once.Buckets);
        Assert.Equal((20L, 1L, 10.0), (bucket.Samples, bucket.CompleteMinutes, bucket.Average));
        Assert.Equal(100, Assert.Single(store.ReadApplications("tablet", Day, now).Applications).Received);
        var restarted = Batch(Day, "seg-b", metrics: [Metric(Day, UsageMetrics.Cpu, 40, "seg-b", 20, true, 30, 50)], apps: [("chrome.exe", 50, 5)]);
        store.Append("tablet", [restarted], now);
        var merged = Assert.Single(store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now).Buckets);
        Assert.Equal(40, merged.Samples); Assert.Equal(25, merged.Average); Assert.Equal(5, merged.Minimum); Assert.Equal(50, merged.Maximum);
        Assert.Equal(0, merged.CompleteMinutes); Assert.Equal(1, merged.Minutes);
        var app = Assert.Single(store.ReadApplications("tablet", Day, now).Applications);
        Assert.Equal((150L, 15L, 1L), (app.Received, app.Sent, app.Minutes));
    }

    [Fact]
    public void Retention_keeps_400_days_and_ignores_rows_outside_the_window()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(12);
        var tooOld = Day.AddDays(-401); var old = Day.AddDays(-399); var future = now.AddMinutes(10); var soon = now.AddMinutes(2);
        store.Append("tablet", [Batch(tooOld), Batch(old), Batch(future), Batch(soon)], now);
        Assert.Equal(old, store.EarliestRetained("tablet"));
        var view = store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day.AddDays(-400), now.AddHours(1));
        Assert.Equal(2, view.Minutes);
        store.Append("tablet", [Batch(now.AddDays(2))], now.AddDays(2));
        Assert.Equal(soon, store.EarliestRetained("tablet"));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day.AddDays(-500), now));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadMetric("tablet", UsageMetrics.Cpu, null, now, now));
    }

    [Fact]
    public void Reads_use_aligned_buckets_preserve_gaps_and_weight_statistics_by_samples()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var start = Day; var end = Day.AddHours(3);
        var batches = new List<UsageMinuteBatch>(); var values = new List<double>();
        for (int i = 0; i < 180; i++)
        {
            if (i is >= 60 and < 80) continue;
            double value = i % 100; values.Add(value);
            batches.Add(Batch(start.AddMinutes(i), metrics: [Metric(start.AddMinutes(i), UsageMetrics.Cpu, value, complete: i % 7 != 0),
                Metric(start.AddMinutes(i), UsageMetrics.Gpu, 50, hardware: "/gpu/0")]));
        }
        store.Append("tablet", batches, end);
        var view = store.ReadMetric("tablet", UsageMetrics.Cpu, null, start, end, 18);
        Assert.Equal(600_000, view.BucketMilliseconds);
        Assert.Equal(16, view.Buckets.Count);
        Assert.Equal(start, view.Buckets[0].Start); Assert.Equal(start.AddMinutes(10), view.Buckets[0].End);
        Assert.All(view.Buckets, b => Assert.Equal(10, b.Minutes));
        Assert.DoesNotContain(view.Buckets, b => b.Start == start.AddMinutes(60) || b.Start == start.AddMinutes(70));
        Assert.Equal(start.AddMinutes(80), view.Buckets[6].Start);
        Assert.Equal(160, view.Minutes);
        Assert.Equal(0, view.Statistics.Minimum); Assert.Equal(99, view.Statistics.Maximum);
        Assert.Equal(values.Average(), view.Statistics.Average!.Value, 9);
        Assert.Equal(batches.Count(b => b.Metrics[0].Complete), view.Buckets.Sum(b => b.CompleteMinutes));
        var month = store.ReadMetric("tablet", UsageMetrics.Cpu, null, end.AddDays(-30), end);
        Assert.InRange(month.Buckets.Count, 1, 1500); Assert.Equal(160, month.Buckets.Sum(b => b.Minutes));
        Assert.Equal(160, store.ReadMetric("tablet", UsageMetrics.Gpu, "/gpu/0", start, end).Minutes);
        Assert.Equal(0, store.ReadMetric("tablet", UsageMetrics.Gpu, "/gpu/1", start, end).Minutes);
        Assert.Null(store.ReadMetric("tablet", UsageMetrics.Memory, null, start, end).Statistics.Average);
        Assert.Throws<ArgumentException>(() => store.ReadMetric("tablet", UsageMetrics.Gpu, null, start, end));
        Assert.Throws<ArgumentException>(() => store.ReadMetric("tablet", UsageMetrics.Cpu, "/gpu/0", start, end));
        Assert.Throws<ArgumentException>(() => store.ReadMetric("tablet", "gpufan", null, start, end));
    }

    [Fact]
    public void Application_totals_are_bounded_with_a_remainder_and_disclose_capture_gaps()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        var apps = Enumerable.Range(0, 250).Select(i => ($"app{i:000}.exe", (i + 1) * 1000L, 1L)).ToArray();
        var batches = new List<UsageMinuteBatch> { Batch(Day, apps: apps), Batch(Day.AddMinutes(1), apps: [("app249.exe", 1, 1)]) };
        for (int i = 2; i < 5; i++) batches.Add(Batch(Day.AddMinutes(i), gap: true, metrics: []));
        store.Append("tablet", batches, now);
        var view = store.ReadApplications("tablet", Day, now);
        Assert.Equal(200, view.Applications.Count);
        Assert.Equal("app249.exe", view.Applications[0].Name); Assert.Equal(250_001, view.Applications[0].Received); Assert.Equal(2, view.Applications[0].Minutes);
        Assert.Equal(50, view.OtherApplications); Assert.Equal(Enumerable.Range(1, 50).Sum(i => i * 1000L), view.OtherReceived); Assert.Equal(50, view.OtherSent);
        Assert.Equal(3, view.CaptureGapMinutes); Assert.Equal(2, view.RecordedMinutes); Assert.Equal(Day, view.EarliestRetained);
        var all = store.ReadApplications("tablet", Day, now, 1000);
        Assert.Equal(250, all.Applications.Count); Assert.Equal(0, all.OtherApplications); Assert.Equal(0, all.OtherReceived);
        Assert.Empty(store.ReadApplications("tablet", Day.AddHours(-2), Day).Applications);
        var minutes = store.ReadApplicationMinutes("tablet", "APP249.EXE", Day, now);
        Assert.Equal(new[] { (Day, 250_000L, "seg-a"), (Day.AddMinutes(1), 1L, "seg-a") }, minutes.Select(m => (m.Minute, m.Received, m.Segment)));
        Assert.All(minutes, m => Assert.Equal("app249.exe", m.Name));
        Assert.Empty(store.ReadApplicationMinutes("tablet", "nothing.exe", Day, now));
        Assert.Throws<ArgumentException>(() => store.ReadApplicationMinutes("tablet", " ", Day, now));
    }

    [Fact]
    public void Eviction_drops_whole_oldest_days_and_never_the_day_being_recorded()
    {
        using var fixture = new Fixture(); const long cap = 131_072; var store = fixture.Open(cap);
        static UsageMinuteBatch[] DayOf(DateTimeOffset day) => Enumerable.Range(0, 240)
            .Select(i => Batch(day.AddMinutes(i), apps: [("app.exe", 1, 1)])).ToArray();
        var days = Enumerable.Range(0, 10).Select(i => Day.AddDays(i)).ToArray();
        foreach (var day in days) store.Append("tablet", DayOf(day), day.AddHours(23));
        var earliest = store.EarliestRetained("tablet");
        Assert.NotNull(earliest);
        Assert.True(earliest > days[0], "the oldest day was evicted");
        Assert.Equal(TimeSpan.Zero, earliest.Value.TimeOfDay);
        Assert.Equal(240, store.ReadMetric("tablet", UsageMetrics.Cpu, null, earliest.Value, earliest.Value.AddDays(1)).Minutes);
        Assert.Equal(240, store.ReadMetric("tablet", UsageMetrics.Cpu, null, days[9], days[9].AddDays(1)).Minutes);
        Assert.Equal(240, Assert.Single(store.ReadApplications("tablet", days[9], days[9].AddDays(1)).Applications).Minutes);
        Assert.InRange(new FileInfo(fixture.Path).Length, 1, cap);
    }

    [Fact]
    public void A_full_store_with_only_today_reports_an_error_and_keeps_existing_rows()
    {
        using var fixture = new Fixture(); var store = fixture.Open(65_536); var now = Day.AddHours(12);
        store.Append("tablet", Enumerable.Range(0, 30).Select(i => Batch(Day.AddMinutes(i))).ToArray(), now);
        var huge = Enumerable.Range(30, 400).Select(i => Batch(Day.AddMinutes(i), metrics: UsageMetrics.All
            .Select(m => Metric(Day.AddMinutes(i), m, 1, hardware: UsageMetrics.UsesHardwareId(m) ? "/gpu/0" : null)).ToArray())).ToArray();
        Assert.Throws<IOException>(() => store.Append("tablet", huge, now));
        Assert.Equal(30, store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now).Minutes);
        Assert.Equal(Day, store.EarliestRetained("tablet"));
    }

    [Fact]
    public void Csv_exports_neutralize_formulas_and_use_invariant_culture()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        string hostile = "=cmd|' /C calc'!A0";
        store.Append("tablet", [Batch(Day, metrics: [Metric(Day, UsageMetrics.Cpu, 12.5, minimum: 1.25, maximum: 99.75),
            Metric(Day, UsageMetrics.GpuTemperature, 60, hardware: "/gpu/0", complete: false)], apps: [(hostile, 10, 20), ("chrome.exe", 1, 2)])], now);
        store.Append("other", [Batch(Day, apps: [("steam.exe", 5, 5)])], now);
        using var metrics = new StringWriter(CultureInfo.GetCultureInfo("fr-FR"));
        store.ExportMetricsCsv("tablet", metrics, Day, now);
        string csv = metrics.ToString();
        Assert.StartsWith("minute_utc,metric,hardware_id,average,minimum,maximum,samples,complete,segment", csv);
        Assert.Contains("\"cpu\",\"\",\"12.5\",\"1.25\",\"99.75\",\"60\",\"True\",\"seg-a\"", csv);
        Assert.Contains("\"gputemp\",\"/gpu/0\",\"60\",\"60\",\"60\",\"60\",\"False\"", csv);
        Assert.Contains("2026-11-02T00:00:00.0000000+00:00", csv);
        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using var apps = new StringWriter(CultureInfo.InvariantCulture);
        store.ExportApplicationsCsv("tablet", apps, Day, now);
        string totals = apps.ToString();
        Assert.StartsWith("application,received_bytes,sent_bytes,minutes_observed", totals);
        Assert.Contains("\"'" + hostile + "\",\"10\",\"20\",\"1\"", totals);
        Assert.Contains("\"chrome.exe\",\"1\",\"2\",\"1\"", totals);
        Assert.DoesNotContain("steam", totals);
        Assert.Equal(3, totals.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Invalid_batches_are_rejected_before_any_write()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        var good = Batch(Day);
        var wrongMinute = new UsageMinuteBatch(Day, [Metric(Day.AddMinutes(1), UsageMetrics.Cpu, 1)], [], false, "seg-a");
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [good, wrongMinute], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, metrics: [Metric(Day, "gpufan", 1)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, metrics: [Metric(Day, UsageMetrics.Gpu, 1)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, metrics: [Metric(Day, UsageMetrics.Cpu, 1, samples: 0)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, metrics: [Metric(Day, UsageMetrics.Cpu, double.NaN)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, apps: [("chrome.exe", -1, 0)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, apps: [(" chrome.exe", 1, 0)])], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", [Batch(Day, "bad segment!")], now));
        Assert.Throws<ArgumentException>(() => store.Append("", [good], now));
        Assert.Throws<ArgumentException>(() => store.Append("tablet", Enumerable.Range(0, UsageHistoryStore.MaximumBatchRows + 1)
            .Select(i => Batch(Day.AddMinutes(i))).ToArray(), now));
        Assert.Equal(0, store.ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now).Minutes);
        Assert.Null(store.EarliestRetained("tablet"));
        store.Append("tablet", [], now);
        Assert.Throws<InvalidOperationException>(() => new UsageHistoryStore(fixture.Path).ReadMetric("tablet", UsageMetrics.Cpu, null, Day, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsageHistoryStore(fixture.Path, 1024));
    }

    [Theory]
    [InlineData(1, 60)] [InlineData(1, 450)] [InlineData(1, 1500)]
    [InlineData(7, 60)] [InlineData(7, 450)] [InlineData(7, 1500)]
    [InlineData(30, 60)] [InlineData(30, 450)] [InlineData(30, 1500)] [InlineData(30, 4000)] [InlineData(1, 700)]
    public void Application_series_uses_the_metric_bucket_grid(int days, int points)
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var end = Day.AddDays(days);
        // A CPU minute and an a.exe row every 7 minutes over the period, plus the last minute so the last slot (partial for 1 day × 700) is listed;
        // 2 rows a batch keeps each Append within MaximumBatchRows.
        var batches = Enumerable.Range(0, (days * 1440 + 6) / 7).Select(i => Batch(Day.AddMinutes(7 * i), apps: [("a.exe", 100, 10)]))
            .Append(Batch(end.AddMinutes(-1), apps: [("a.exe", 100, 10)])).ToArray();
        foreach (var chunk in batches.Chunk(UsageHistoryStore.MaximumBatchRows / 2)) store.Append("m", chunk, end);
        var series = store.ReadApplicationSeries("m", Day, end, points);
        var cpu = store.ReadMetric("m", UsageMetrics.Cpu, null, Day, end, points);
        Assert.Equal(cpu.BucketMilliseconds, series.BucketMilliseconds);
        Assert.Equal(cpu.Buckets.Count, series.Slots.Count);
        Assert.All(cpu.Buckets, b => Assert.Contains(series.Slots, s => s.Start == b.Start && s.End == b.End && s.Minutes == b.Minutes));
        Assert.Equal(end, series.Slots[^1].End);
        if (points == 700) Assert.True(series.Slots[^1].End - series.Slots[^1].Start < TimeSpan.FromMilliseconds(series.BucketMilliseconds));
        Assert.Equal((TrafficMeasure.Total, (DateTimeOffset?)null, Day, end), (series.Measure, series.RetainedFrom, series.From, series.To));
        Assert.Equal(batches.Length * 100L, series.Slots.Sum(s => s.Received[0]));
    }

    [Fact]
    public void Application_series_ranks_like_the_applications_read()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var end = Day.AddDays(1);
        // Nine names: c and d tie on received + sent, and f and g tie across the sixth place, so the name decides both.
        (string Name, long Received, long Sent)[] totals =
            [("i.exe", 50, 0), ("a.exe", 800, 100), ("d.exe", 100, 600), ("c.exe", 600, 100), ("b.exe", 700, 100),
             ("e.exe", 500, 0), ("g.exe", 300, 100), ("f.exe", 100, 300), ("h.exe", 90, 10)];
        // Each name's traffic is split over two minutes an hour apart, so the ranking sums across slots.
        store.Append("m", [Batch(Day.AddMinutes(10), apps: totals.Select(t => (t.Name, t.Received - t.Received / 2, t.Sent - t.Sent / 2)).ToArray()),
            Batch(Day.AddMinutes(70), apps: totals.Select(t => (t.Name, t.Received / 2, t.Sent / 2)).ToArray())], end);
        var series = store.ReadApplicationSeries("m", Day, end);
        var applications = store.ReadApplications("m", Day, end);
        Assert.Equal(new[] { "a.exe", "b.exe", "c.exe", "d.exe", "e.exe", "f.exe" }, series.Applications.Select(a => a.Name));
        Assert.Equal(applications.Applications.Take(6).Select(a => (a.Name, a.Received, a.Sent)), series.Applications.Select(a => (a.Name, a.Received, a.Sent)));
        Assert.All(series.Applications, a => Assert.Equal(a.Name, a.Key));
        Assert.Equal((applications.Applications.Skip(6).Sum(a => a.Received) + applications.OtherReceived,
            applications.Applications.Skip(6).Sum(a => a.Sent) + applications.OtherSent,
            applications.Applications.Count - 6 + applications.OtherApplications), (series.OtherReceived, series.OtherSent, series.OtherApplications));
        Assert.Equal((440L, 110L, 3L), (series.OtherReceived, series.OtherSent, series.OtherApplications));
        Assert.All(series.Slots, s => Assert.Equal((7, 7), (s.Received.Count, s.Sent.Count)));
        Assert.Equal(totals.Sum(t => t.Received), series.Slots.Sum(s => s.Received.Sum()));
        Assert.Equal(series.OtherSent, series.Slots.Sum(s => s.Sent[6]));
    }

    [Fact]
    public void Application_series_keeps_capture_gap_minutes_out_of_slot_bytes()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var end = Day.AddDays(1);
        // 288 points over a day gives 5-minute slots. Slot 0: minutes 0-3 captured and minute 4 a capture gap, each with a.exe 100 B;
        // b.exe appears only in the gap minute. Slot 1: two capture-gap minutes only.
        var batches = Enumerable.Range(0, 4).Select(i => Batch(Day.AddMinutes(i), apps: [("a.exe", 100, 0)])).ToList();
        batches.Add(Batch(Day.AddMinutes(4), gap: true, apps: [("a.exe", 100, 0), ("b.exe", 50, 5)]));
        batches.Add(Batch(Day.AddMinutes(5), gap: true, apps: [("a.exe", 100, 0)]));
        batches.Add(Batch(Day.AddMinutes(6), gap: true, apps: [("a.exe", 100, 0)]));
        store.Append("m", batches, end);
        var series = store.ReadApplicationSeries("m", Day, end, 288);
        Assert.Equal(300_000, series.BucketMilliseconds);
        Assert.Equal(new[] { ("a.exe", 700L, 0L), ("b.exe", 50L, 5L) }, series.Applications.Select(a => (a.Name, a.Received, a.Sent)));
        Assert.Equal(2, series.Slots.Count);
        var first = series.Slots[0];
        Assert.Equal((Day, Day.AddMinutes(5), 5L, 240L, true), (first.Start, first.End, first.Minutes, first.RecordedSeconds, first.Incomplete));
        Assert.Equal(new long[] { 400, 0, 0 }, first.Received); Assert.Equal(new long[] { 0, 0, 0 }, first.Sent);
        var second = series.Slots[1];
        Assert.Equal((Day.AddMinutes(5), Day.AddMinutes(10), 2L, 0L, true), (second.Start, second.End, second.Minutes, second.RecordedSeconds, second.Incomplete));
        Assert.Equal(new long[] { 0, 0, 0 }, second.Received); Assert.Equal(new long[] { 0, 0, 0 }, second.Sent);
    }

    [Fact]
    public void Application_series_lists_recorded_minutes_without_traffic_as_zero_slots()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var end = Day.AddDays(1);
        // 5-minute slots. Slot 0: metric rows only. Slot 1: nothing. Slot 2: one minute with traffic. Slot 3: one minute with an
        // application row and no metric row, which still counts as recorded.
        var batches = Enumerable.Range(0, 5).Select(i => Batch(Day.AddMinutes(i))).ToList();
        batches.Add(Batch(Day.AddMinutes(10), apps: [("a.exe", 30, 3)]));
        batches.Add(Batch(Day.AddMinutes(15), metrics: [], apps: [("a.exe", 20, 2)]));
        store.Append("m", batches, end);
        var series = store.ReadApplicationSeries("m", Day, end, 288);
        Assert.Equal(new[] { Day, Day.AddMinutes(10), Day.AddMinutes(15) }, series.Slots.Select(s => s.Start));
        Assert.Equal(new[] { (5L, 300L, false), (1L, 60L, false), (1L, 60L, false) }, series.Slots.Select(s => (s.Minutes, s.RecordedSeconds, s.Incomplete)));
        Assert.Equal(new long[] { 0, 0 }, series.Slots[0].Received); Assert.Equal(new long[] { 0, 0 }, series.Slots[0].Sent);
        Assert.Equal(new long[] { 30, 0 }, series.Slots[1].Received); Assert.Equal(new long[] { 3, 0 }, series.Slots[1].Sent);
        Assert.Equal(new long[] { 20, 0 }, series.Slots[2].Received);
        Assert.Empty(store.ReadApplicationSeries("m", Day.AddDays(-1), Day).Slots);
    }

    [Fact]
    public void Application_series_rejects_bad_arguments()
    {
        using var fixture = new Fixture(); var store = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplicationSeries("m", Day, Day.AddDays(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadApplicationSeries("m", Day, Day.AddDays(1), 4001));
        Assert.ThrowsAny<ArgumentException>(() => store.ReadApplicationSeries("m", Day, Day.AddDays(-1)));
        Assert.ThrowsAny<ArgumentException>(() => store.ReadApplicationSeries("m", Day, Day.AddDays(402)));
        Assert.ThrowsAny<ArgumentException>(() => store.ReadApplicationSeries("", Day, Day.AddDays(1)));
        Assert.Throws<InvalidOperationException>(() => new UsageHistoryStore(fixture.Path).ReadApplicationSeries("m", Day, Day.AddDays(1)));
    }

    // Task 9: clearing usage-history.db.
    private static readonly DateTimeOffset ClearBefore = Day.AddMinutes(600);
    private static void ClearSeam(UsageHistoryStore store, string field, object? value)
        => typeof(UsageHistoryStore).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(store, value);

    /// <summary>Machines "A" and "B", 64 apps a minute for 660 minutes from Day: "Chrome.exe" on A and "chrome.exe" on B among 63 others,
    /// a CPU metric row a minute and a capture gap every tenth minute.</summary>
    private static void FillForClear(UsageHistoryStore store)
    {
        foreach (var machine in new[] { "A", "B" })
            for (int start = 0; start < 660; start += 50)
            {
                var batches = new List<UsageMinuteBatch>();
                for (int m = start; m < start + 50 && m < 660; m++)
                {
                    var apps = new (string, long, long)[64];
                    apps[0] = (machine == "A" ? "Chrome.exe" : "chrome.exe", 100, 10);
                    for (int a = 1; a < 64; a++) apps[a] = ($"app{a}.exe", a, 1);
                    batches.Add(Batch(Day.AddMinutes(m), gap: m % 10 == 0, apps: apps));
                }
                store.Append(machine, batches, Day.AddDays(1));
            }
    }

    private static long Count(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Application_names_return_each_name_with_its_first_and_last_minute_for_this_machine_only()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); var now = Day.AddHours(1);
        store.Append("a", [Batch(Day.AddMinutes(1), apps: [("chrome.exe", 1, 1)]), Batch(Day.AddMinutes(5), apps: [("code.exe", 1, 1)]), Batch(Day.AddMinutes(9), apps: [("chrome.exe", 1, 1)])], now);
        store.Append("b", [Batch(Day.AddMinutes(3), apps: [("steam.exe", 1, 1)])], now);
        var names = store.ReadApplicationNames("a")!;
        Assert.Equal([new ApplicationSeen("chrome.exe", Day.AddMinutes(1), Day.AddMinutes(9)), new ApplicationSeen("code.exe", Day.AddMinutes(5), Day.AddMinutes(5))], names);
        store.BeginClear(new(null, ClearBefore, ClearBefore));
        Assert.Null(store.ReadApplicationNames("a"));
    }

    private static long Wal(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;
    private static long BeforeMs => ClearBefore.ToUnixTimeMilliseconds();
    private static string ChromeBefore => $"SELECT count(*) FROM application_minutes WHERE name IN ('Chrome.exe','chrome.exe') AND minute<{BeforeMs}";

    [Fact]
    public void Usage_one_app_clear_removes_every_spelling_on_every_machine()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); FillForClear(store);
        long metrics = Count(fixture.Path, "SELECT count(*) FROM metric_minutes"), gaps = Count(fixture.Path, "SELECT count(*) FROM capture_gaps");
        long others = Count(fixture.Path, "SELECT count(*) FROM application_minutes WHERE name NOT IN ('Chrome.exe','chrome.exe')");
        int largest = 0, commits = 0; long largestWal = 0;
        ClearSeam(store, "clearCommitted", (Action<int>)(rows => { largest = Math.Max(largest, rows); commits++; largestWal = Math.Max(largestWal, Wal(fixture.Path)); }));
        store.BeginClear(new("CHROME.EXE", ClearBefore, ClearBefore));
        HistoryClearProgress progress;
        do progress = store.ClearStep(TimeSpan.FromMilliseconds(250)); while (!progress.Finished);
        Assert.Equal(1200, progress.Total); Assert.Equal(1200, progress.Done); Assert.Equal(100, progress.Percent);
        Assert.Equal(0, Count(fixture.Path, ChromeBefore));
        Assert.Equal(60, Count(fixture.Path, $"SELECT count(*) FROM application_minutes WHERE machine='A' AND name='Chrome.exe' AND minute>={BeforeMs}"));
        Assert.Equal(60, Count(fixture.Path, $"SELECT count(*) FROM application_minutes WHERE machine='B' AND name='chrome.exe' AND minute>={BeforeMs}"));
        Assert.Equal(others, Count(fixture.Path, "SELECT count(*) FROM application_minutes WHERE name NOT IN ('Chrome.exe','chrome.exe')"));
        Assert.Equal(metrics, Count(fixture.Path, "SELECT count(*) FROM metric_minutes")); Assert.Equal(gaps, Count(fixture.Path, "SELECT count(*) FROM capture_gaps"));
        Assert.InRange(largest, 1, 512); Assert.True(commits >= 4); Assert.InRange(largestWal, 1, 8 * 1024 * 1024);
        Assert.False(store.ClearPending); Assert.Null(store.PendingClear);
        Assert.Throws<InvalidOperationException>(() => store.ClearStep(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Usage_all_apps_clear_keeps_device_minutes()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); FillForClear(store);
        long metrics = Count(fixture.Path, "SELECT count(*) FROM metric_minutes"), gaps = Count(fixture.Path, "SELECT count(*) FROM capture_gaps");
        long kept = Count(fixture.Path, $"SELECT count(*) FROM application_minutes WHERE minute>={BeforeMs}");
        int largest = 0, commits = 0; long largestWal = 0;
        ClearSeam(store, "clearCommitted", (Action<int>)(rows => { largest = Math.Max(largest, rows); commits++; largestWal = Math.Max(largestWal, Wal(fixture.Path)); }));
        store.BeginClear(new(null, ClearBefore, ClearBefore));
        HistoryClearProgress progress;
        do progress = store.ClearStep(TimeSpan.FromMilliseconds(250)); while (!progress.Finished);
        Assert.Equal(2 * 600 * 64, progress.Total); Assert.Equal(progress.Total, progress.Done);
        Assert.Equal(0, Count(fixture.Path, $"SELECT count(*) FROM application_minutes WHERE minute<{BeforeMs}"));
        Assert.Equal(kept, Count(fixture.Path, "SELECT count(*) FROM application_minutes"));
        Assert.Equal(metrics, Count(fixture.Path, "SELECT count(*) FROM metric_minutes")); Assert.Equal(gaps, Count(fixture.Path, "SELECT count(*) FROM capture_gaps"));
        Assert.Equal(4096, largest); Assert.True(commits >= 2 * 10); Assert.InRange(largestWal, 1, 8 * 1024 * 1024);
        Assert.Equal(new UsageClearedMarks(ClearBefore, null), store.ClearedMarks);
    }

    [Fact]
    public void Usage_clear_marker_is_atomic_and_resumes()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); FillForClear(store);
        string marker = System.IO.Path.ChangeExtension(fixture.Path, ".clear.json"), marks = System.IO.Path.ChangeExtension(fixture.Path, ".cleared.json");
        Assert.False(store.ClearPending); Assert.False(File.Exists(marker));
        var request = new HistoryClear("CHROME.EXE", ClearBefore, ClearBefore.AddDays(7).AddSeconds(5));
        store.BeginClear(request);
        Assert.True(store.ClearPending); Assert.Equal(request, store.PendingClear); Assert.False(File.Exists(marker + ".tmp"));
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker)))
        {
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("Version").GetInt32()); Assert.Equal("CHROME.EXE", root.GetProperty("AppKey").GetString());
            Assert.Equal(request.Before, root.GetProperty("Before").GetDateTimeOffset()); Assert.Equal(request.Requested, root.GetProperty("Requested").GetDateTimeOffset());
            Assert.EndsWith("Z", root.GetProperty("Before").GetString());
        }
        Assert.Equal(request, HistoryClearMarker.Read(marker));
        // An equal request is a no-op; a different one is refused while this one is pending.
        store.BeginClear(request);
        Assert.Throws<InvalidOperationException>(() => store.BeginClear(new(null, ClearBefore, ClearBefore)));
        // A leftover .tmp is deleted by a new instance's Initialize, which picks up the marker.
        File.WriteAllText(marker + ".tmp", "partial");
        var reopened = fixture.Open();
        Assert.False(File.Exists(marker + ".tmp")); Assert.True(reopened.ClearPending); Assert.Equal(request, reopened.PendingClear); Assert.False(reopened.DiscardedInvalidMarker);
        // One step (1 ms on a ticking clock: exactly one transaction), then another instance resumes and finishes.
        long t = 0; ClearSeam(reopened, "clearClock", (Func<TimeSpan>)(() => TimeSpan.FromMilliseconds(t++)));
        Assert.False(reopened.ClearStep(TimeSpan.FromMilliseconds(1)).Finished); Assert.True(reopened.ClearPending);
        Assert.Equal(1200 - 512, Count(fixture.Path, ChromeBefore));
        var resumed = new UsageHistoryStore(fixture.Path); var secure = new List<long>();
        ClearSeam(resumed, "clearConnectionOpened", (Action<SqliteConnection>)(db => { using var command = db.CreateCommand(); command.CommandText = "PRAGMA secure_delete"; secure.Add((long)command.ExecuteScalar()!); }));
        resumed.Initialize();
        Assert.True(resumed.ClearPending); Assert.Equal(request, resumed.PendingClear);
        while (true)
        {
            Assert.True(resumed.ClearPending); Assert.True(File.Exists(marker));
            if (resumed.ClearStep(TimeSpan.FromMilliseconds(250)).Finished) break;
        }
        Assert.False(resumed.ClearPending); Assert.Null(resumed.PendingClear); Assert.False(File.Exists(marker)); Assert.False(File.Exists(marks + ".tmp"));
        Assert.Equal(0, Count(fixture.Path, ChromeBefore));
        Assert.Equal(new UsageClearedMarks(null, ClearBefore), resumed.ClearedMarks); Assert.Equal(resumed.ClearedMarks, UsageClearedMarks.Read(marks));
        Assert.NotEmpty(secure); Assert.All(secure, value => Assert.Equal(1, value));
        secure.Clear(); resumed.Append("A", [Batch(Day.AddMinutes(700))], Day.AddDays(1)); Assert.Equal([0L], secure);
        // Invalid content: Initialize deletes the marker and reports it; nothing is pending and no row is deleted.
        long rows = Count(fixture.Path, "SELECT count(*) FROM application_minutes");
        const string Times = "\"Before\":\"2026-11-02T10:00:00Z\",\"Requested\":\"2026-11-02T10:00:00Z\"";
        foreach (var bad in new[] { "{", "[]", "{\"Version\":2,\"AppKey\":null," + Times + "}", "{\"Version\":1,\"AppKey\":\"\"," + Times + "}",
            "{\"Version\":1,\"AppKey\":null,\"Before\":\"yesterday\",\"Requested\":\"2026-11-02T10:00:00Z\"}", "{\"Version\":1,\"AppKey\":null}" })
        {
            File.WriteAllText(marker, bad);
            Assert.Throws<InvalidDataException>(() => HistoryClearMarker.Read(marker));
            var invalid = fixture.Open();
            Assert.True(invalid.DiscardedInvalidMarker); Assert.False(invalid.ClearPending); Assert.Null(invalid.PendingClear); Assert.False(File.Exists(marker));
            Assert.Throws<InvalidOperationException>(() => invalid.ClearStep(TimeSpan.FromMilliseconds(250)));
        }
        Assert.Equal(rows, Count(fixture.Path, "SELECT count(*) FROM application_minutes"));
        Assert.Null(HistoryClearMarker.Read(marker));
        // A marker that cannot be read is kept: the clear stays pending without a request, and Initialize still passes.
        HistoryClearMarker.Write(marker, request);
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => HistoryClearMarker.Read(marker));
            var locked = fixture.Open();
            Assert.True(locked.ClearPending); Assert.Null(locked.PendingClear); Assert.False(locked.DiscardedInvalidMarker);
        }
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Usage_clear_finishes_when_each_step_allows_one_call()
    {
        using var fixture = new Fixture(); var store = fixture.Open(); FillForClear(store);
        string marker = System.IO.Path.ChangeExtension(fixture.Path, ".clear.json"), marks = System.IO.Path.ChangeExtension(fixture.Path, ".cleared.json");
        // A 1 ms budget on a ticking clock and a zero budget each allow exactly one delegate call per step. The step whose call finds
        // the work gone must scan and finish in that same call.
        long t = 0; ClearSeam(store, "clearClock", (Func<TimeSpan>)(() => TimeSpan.FromMilliseconds(t++)));
        foreach (var (clear, budget) in new[] { (new HistoryClear("CHROME.EXE", ClearBefore, ClearBefore), TimeSpan.FromMilliseconds(1)),
            (new HistoryClear(null, ClearBefore, ClearBefore), TimeSpan.Zero) })
        {
            store.BeginClear(clear);
            int steps = 0; HistoryClearProgress progress;
            do { progress = store.ClearStep(budget); Assert.InRange(++steps, 1, 100); } while (!progress.Finished);
            Assert.False(store.ClearPending); Assert.False(File.Exists(marker)); Assert.True(File.Exists(marks));
            Assert.Equal(0, Count(fixture.Path, clear.AppKey is null ? $"SELECT count(*) FROM application_minutes WHERE minute<{BeforeMs}" : ChromeBefore));
        }
        Assert.Equal(new UsageClearedMarks(ClearBefore, ClearBefore), store.ClearedMarks); Assert.Equal(store.ClearedMarks, UsageClearedMarks.Read(marks));
        // A resumed clear with nothing left finishes in its first one-call step.
        store.BeginClear(new(null, ClearBefore, ClearBefore.AddMinutes(1)));
        var resumed = fixture.Open();
        Assert.True(resumed.ClearPending); Assert.True(resumed.ClearStep(TimeSpan.Zero).Finished); Assert.False(File.Exists(marker));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DeadWare-usage-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "usage.db");
        public UsageHistoryStore Open(long maximumBytes = UsageHistoryStore.MaximumDatabaseBytes)
        { var store = new UsageHistoryStore(Path, maximumBytes); store.Initialize(); return store; }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
