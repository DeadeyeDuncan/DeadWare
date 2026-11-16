using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

/// <summary>
/// The column store behind <see cref="SampleHistory"/>: chunked growth, the six-hour window sliding through it, missing and
/// absent values, metrics that come and go, restore merges, and reads that match the dictionary-per-poll store it replaced.
/// </summary>
public sealed class HistoryStorageTests
{
    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;

    static Snapshot S(double seconds, string? session, params (string Key, double? Value)[] values) =>
        new(Epoch.AddSeconds(seconds), seconds, values.ToDictionary(v => v.Key, v => v.Value)) { SessionId = session };

    /// <summary>The store it replaced, kept to its rules: a dictionary per poll, the same bounds, the same reads.</summary>
    sealed class Reference
    {
        readonly Queue<Snapshot> samples = new();
        public Snapshot[] Samples => [.. samples];
        public void Add(Snapshot sample)
        {
            if (!double.IsFinite(sample.MonotonicSeconds) || samples.Count > 0 && sample.MonotonicSeconds <= last) return;
            samples.Enqueue(sample with { Values = sample.Values.ToDictionary(p => p.Key, p => SampleHistory.Finite(p.Value)) });
            last = sample.MonotonicSeconds;
            while (samples.Count > HistoryRanges.MaximumSamples || samples.Peek().MonotonicSeconds < sample.MonotonicSeconds - HistoryRanges.MaximumSeconds) samples.Dequeue();
        }
        double last;
        Snapshot[] Window(double now, int seconds) => samples.Where(s => s.MonotonicSeconds >= now - seconds && s.MonotonicSeconds <= now).ToArray();
        public List<SamplePoint> Series(string id, double now, int seconds)
        {
            var window = Window(now, seconds);
            return window.Select((s, i) => new SamplePoint(s.Timestamp, s.MonotonicSeconds, s.Values.GetValueOrDefault(id))
                { BreakBefore = i > 0 && window[i - 1].SessionId != s.SessionId, PollingIntervalSeconds = PollingPolicy.Normalize(s.PollingIntervalSeconds) }).ToList();
        }
        public List<ChartRangePoint> ChartRanges(string id, double now, int seconds, int pixelWidth)
        {
            var result = new List<ChartRangePoint>();
            int count = 0; double bucket = double.NaN, total = 0, min = 0, max = 0, start = 0, end = 0, bucketSeconds = seconds / (double)pixelWidth, previousInterval = 1;
            Snapshot? previous = null;
            void Flush() { if (count == 0) return; result.Add(new(start, end, total / count, min, max, count)); count = 0; bucket = double.NaN; }
            void Gap(double time) { Flush(); if (result.Count == 0 || result[^1].Average.HasValue) result.Add(new(time, time, null, null, null, 0)); }
            foreach (var sample in Window(now, seconds))
            {
                double time = sample.MonotonicSeconds, interval = PollingPolicy.Normalize(sample.PollingIntervalSeconds);
                if (previous is not null && (sample.SessionId != previous.SessionId || time - previous.MonotonicSeconds > Math.Max(previousInterval, interval) + 1.5)) Gap(time);
                previous = sample; previousInterval = interval;
                if (sample.Values.GetValueOrDefault(id) is not { } value) { Gap(time); continue; }
                double column = Math.Floor(time / bucketSeconds);
                if (column != bucket) Flush();
                if (count == 0) { bucket = column; start = time; min = max = value; total = 0; }
                end = time; min = Math.Min(min, value); max = Math.Max(max, value); total += value; count++;
            }
            Flush(); return result;
        }
        public Summary Summarize(string id, double now, int seconds)
        {
            var values = Window(now, seconds).Select(s => s.Values.GetValueOrDefault(id)).OfType<double>().ToArray();
            return values.Length == 0 ? new(null, null, null) : new(values.Aggregate(double.PositiveInfinity, Math.Min), values.Aggregate(double.NegativeInfinity, Math.Max), values.Sum() / values.Length);
        }
    }

    /// <summary>
    /// Seven hours of half-second polls with session changes, missed polls, cadence changes, readings missing and keys absent, so
    /// the six-hour window has slid through several dozen chunks. Values are quarters, which a float holds exactly.
    /// </summary>
    static (SampleHistory History, Reference Reference) Mixed(int seed, int polls = 7 * 3600 * 2)
    {
        var random = new Random(seed);
        var history = new SampleHistory(); var reference = new Reference();
        double time = 0; string? session = "saved"; double interval = 0.5;
        for (int i = 0; i < polls; i++)
        {
            int roll = random.Next(1000);
            if (roll < 2) session = session is null ? "saved" : null;
            else if (roll < 6) time += random.Next(2, 30);
            else if (roll < 8) interval = interval == 0.5 ? 2 : 0.5;
            time += interval;
            var values = new Dictionary<string, double?>();
            foreach (string key in new[] { "a", "b", "c" })
            {
                int pick = random.Next(20);
                if (key == "c" && (i / 3000) % 2 == 1) continue;   // absent for whole stretches, as an unplugged drive is
                if (pick == 0) continue;                            // absent from this poll
                values[key] = pick == 1 ? null : pick == 2 ? double.NaN : random.Next(-400, 400) / 4d;
            }
            var sample = new Snapshot(Epoch.AddSeconds(time), time, values) { SessionId = session, PollingIntervalSeconds = interval };
            history.Add(sample); reference.Add(sample);
        }
        return (history, reference);
    }

    static IEnumerable<(double Now, int Seconds)> Windows(Reference reference)
    {
        var samples = reference.Samples;
        double last = samples[^1].MonotonicSeconds, oldest = samples[0].MonotonicSeconds;
        return [(last, 60), (last - 0.25, 300), (last + 30, 900), (last - 4000, 3600), (last, 21600), (oldest + 20, 60), (oldest - 5, 60), (last + 500, 60)];
    }

    [Fact]
    public void Reads_match_the_dictionary_store_after_the_window_has_slid_through_many_chunks()
    {
        var (history, reference) = Mixed(7);
        var expected = reference.Samples;
        Assert.Equal(expected.Length, history.Count);
        var all = history.Read();
        Assert.Equal(expected.Select(s => (s.MonotonicSeconds, s.Timestamp, s.SessionId, s.PollingIntervalSeconds)), all.Select(s => (s.MonotonicSeconds, s.Timestamp, s.SessionId, s.PollingIntervalSeconds)));
        for (int i = 0; i < all.Count; i += 97)
            Assert.Equal(expected[i].Values.OrderBy(p => p.Key), all[i].Values.OrderBy(p => p.Key));
        foreach (var (now, seconds) in Windows(reference))
            foreach (string id in new[] { "a", "c", "never" })
            {
                Assert.Equal(reference.Series(id, now, seconds), history.Series(id, now, seconds));
                Assert.Equal(reference.ChartRanges(id, now, seconds, 300), history.ChartRanges(id, now, seconds, 300));
                Assert.Equal(reference.Summarize(id, now, seconds), history.Summarize(id, now, seconds));
                Assert.Equal(reference.Summarize(id, now, seconds), history.Summaries([id, "b"], now, seconds)[id]);
            }
    }

    [Theory]
    [InlineData(1, 600)] [InlineData(2, 37)] [InlineData(3, 1)]
    public void A_reduced_line_matches_reducing_the_series_and_keeps_its_statistics(int seed, int pixelWidth)
    {
        var (history, reference) = Mixed(seed);
        foreach (var (now, seconds) in Windows(reference))
            foreach (string id in new[] { "a", "b", "c", "never" })
            {
                var series = history.Series(id, now, seconds);
                var reduced = history.ReduceSeries(id, now, seconds, pixelWidth);
                Assert.Equal(SampleHistory.ReduceForChart(series, now, seconds, pixelWidth), reduced.Points);
                Assert.Equal(SampleHistory.Statistics(series), reduced.Statistics);
            }
        Assert.Throws<ArgumentOutOfRangeException>(() => history.ReduceSeries("a", double.NaN, 60, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.ReduceSeries("a", 1, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.ReduceSeries("a", 1, 60, 0));
    }

    [Fact]
    public void Samples_grow_through_many_chunks_in_order()
    {
        var history = new SampleHistory();
        for (int i = 0; i < 5000; i++) history.Add(S(i, null, ("cpu", i % 101), ("gpu", -i)));
        Assert.Equal(5000, history.Count);
        var cpu = history.Series("cpu", 4999, 21600);
        Assert.Equal(Enumerable.Range(0, 5000).Select(i => (double?)(i % 101)), cpu.Select(p => p.Value));
        Assert.Equal(Enumerable.Range(0, 5000).Select(i => (double)i), cpu.Select(p => p.MonotonicSeconds));
        Assert.Equal(-4999, history.Read()[^1].Values["gpu"]);
    }

    [Fact]
    public void A_key_present_without_a_reading_stays_distinct_from_an_absent_one()
    {
        var history = new SampleHistory();
        history.Add(S(1, null, ("a", null), ("b", 1)));
        history.Add(S(2, null, ("b", 2)));
        history.Add(S(3, null, ("a", double.PositiveInfinity), ("b", 3)));
        history.Add(S(4, null, ("a", 4), ("b", 4)));
        var all = history.Read();
        Assert.True(all[0].Values.ContainsKey("a")); Assert.Null(all[0].Values["a"]);
        Assert.False(all[1].Values.ContainsKey("a"));
        Assert.True(all[2].Values.ContainsKey("a")); Assert.Null(all[2].Values["a"]);
        Assert.Equal(4, all[3].Values["a"]);
        Assert.Equal(new double?[] { null, null, null, 4 }, history.Series("a", 4, 60).Select(p => p.Value));
        Assert.Equal(new Summary(4, 4, 4), history.Summarize("a", 4, 60));
    }

    [Fact]
    public void A_metric_that_appears_late_has_no_readings_before_it()
    {
        var history = new SampleHistory();
        for (int i = 0; i < 3000; i++) history.Add(i < 2500 ? S(i, null, ("cpu", 1)) : S(i, null, ("cpu", 1), ("late", i)));
        var late = history.Series("late", 2999, 21600);
        Assert.All(late.Take(2500), p => Assert.Null(p.Value));
        Assert.Equal(Enumerable.Range(2500, 500).Select(i => (double?)i), late.Skip(2500).Select(p => p.Value));
        Assert.DoesNotContain("late", history.Read()[0].Values.Keys);
        var ranges = history.ChartRanges("late", 2999, 3000, 10);
        Assert.Null(ranges[0].Average);
        Assert.Equal(500, ranges.Sum(p => p.Samples));
        Assert.Equal(new Summary(2500, 2999, 2749.5), history.Summarize("late", 2999, 3000));
    }

    [Fact]
    public void A_metric_gone_for_the_whole_window_is_forgotten_and_its_slot_reused_without_its_values()
    {
        var history = new SampleHistory();
        for (int i = 0; i < 100; i++) history.Add(S(i * 0.5, null, ("cpu", 1), ("usb", 7)));
        // Six hours and more without it: every chunk that held it ages out.
        for (int i = 100; i < 100 + HistoryRanges.MaximumSamples + 2048; i++) history.Add(S(i * 0.5, null, ("cpu", 1)));
        Assert.DoesNotContain(history.Read(), s => s.Values.ContainsKey("usb"));
        double now = (100 + HistoryRanges.MaximumSamples + 2047) * 0.5;
        Assert.All(history.Series("usb", now, 21600), p => Assert.Null(p.Value));
        // A new metric may take the forgotten one's slot; no sample before it may show a value for it.
        history.Add(S(now + 0.5, null, ("cpu", 1), ("other", 3)));
        history.Add(S(now + 1, null, ("cpu", 1), ("usb", 9)));
        var all = history.Read();
        Assert.Equal(1, all.Count(s => s.Values.ContainsKey("other")));
        Assert.Equal(1, all.Count(s => s.Values.ContainsKey("usb")));
        Assert.Equal(9, Assert.Single(history.Series("usb", now + 1, 21600), p => p.Value.HasValue).Value);
    }

    [Fact]
    public void Restore_fills_only_the_keys_a_restored_sample_lacks()
    {
        var history = new SampleHistory(); history.Add(S(10, null, ("cpu", 90)));
        history.Restore([S(8, "saved", ("a", null), ("b", 1))], 10);
        history.Restore([S(8, "saved", ("a", 5), ("b", 2), ("c", 3)), S(8, "other", ("d", 4))], 10);
        var all = history.Read();
        Assert.Equal(2, all.Count);
        Assert.Null(all[0].Values["a"]);
        Assert.Equal(1, all[0].Values["b"]);
        Assert.Equal(3, all[0].Values["c"]);
        Assert.False(all[0].Values.ContainsKey("d"));
        Assert.Equal(90, all[1].Values["cpu"]);
    }

    [Fact]
    public void Restore_places_saved_samples_between_earlier_restored_ones_and_keeps_breaks()
    {
        var history = new SampleHistory(); history.Add(S(100, null, ("cpu", 100)));
        history.Restore([S(1, "s", ("cpu", 1)), S(3, "s", ("cpu", 3)), S(5, "s", ("cpu", 5))], 100);
        history.Restore([S(4, "s", ("cpu", 4), ("gpu", 40)), S(2, "s", ("gpu", 20)), S(2, "s", ("cpu", 2), ("gpu", 99)), S(3, "s", ("gpu", 30))], 100);
        var all = history.Read();
        Assert.Equal(new double[] { 1, 2, 3, 4, 5, 100 }, all.Select(s => s.MonotonicSeconds));
        Assert.Equal(new double?[] { 1, 2, 3, 4, 5, 100 }, all.Select(s => s.Values.GetValueOrDefault("cpu")));
        // At one time the first saved sample wins and a later one of its session adds only what it lacks.
        Assert.Equal(new double?[] { null, 20, 30, 40, null, null }, all.Select(s => s.Values.GetValueOrDefault("gpu")));
        var series = history.Series("cpu", 100, 21600);
        Assert.Equal(new[] { false, false, false, false, false, true }, series.Select(p => p.BreakBefore));
        // Rebuilt, the store reads as one built by adding in time order.
        var added = new SampleHistory();
        foreach (var sample in all) added.Add(sample);
        Assert.Equal(added.ChartRanges("cpu", 100, 21600, 7), history.ChartRanges("cpu", 100, 21600, 7));
        Assert.Equal(added.Series("gpu", 100, 21600), history.Series("gpu", 100, 21600));
    }

    [Fact]
    public void A_later_restore_merges_in_place_across_chunks_and_new_live_readings_follow()
    {
        var history = new SampleHistory(); history.Add(S(5000, null, ("cpu", 1)));
        history.Restore(Enumerable.Range(0, 4000).Select(i => S(i, "s", ("network", i))).ToArray(), 5000);
        history.Restore(Enumerable.Range(0, 4000).Select(i => S(i, "s", ("disk", -i))).ToArray(), 5000);
        Assert.Equal(4001, history.Count);
        var all = history.Read();
        Assert.All(all.Take(4000), (s, i) => { Assert.Equal(i, s.Values["network"]); Assert.Equal(-i, s.Values["disk"]); });
        history.Add(S(5001, null, ("cpu", 2)));
        Assert.Equal(new double?[] { 1, 2 }, history.Series("cpu", 5001, 2).Select(p => p.Value));
        Assert.Equal(new Summary(-3999, 0, -1999.5), history.Summarize("disk", 5001, 21600));
    }

    [Fact]
    public void Restore_drops_samples_past_the_cutoff_and_keeps_the_newest_within_the_count()
    {
        var history = new SampleHistory();
        history.Restore(Enumerable.Range(0, 50000).Select(i => S(i * 0.5, "s", ("cpu", i))).ToArray(), 24999.5);
        Assert.Equal(HistoryRanges.MaximumSamples, history.Count);
        Assert.Equal(24999.5 - HistoryRanges.MaximumSeconds, history.Read()[0].MonotonicSeconds);
        // A restore with nothing to place still drops what is past the cutoff of a later now.
        history.Restore([], 25100);
        Assert.Equal(25100 - HistoryRanges.MaximumSeconds, history.Read()[0].MonotonicSeconds);
        history.Add(S(25100, null, ("cpu", -1)));
        Assert.Equal(-1, history.Read()[^1].Values["cpu"]);
    }

    [Fact]
    public void History_keeps_what_is_read_back_and_not_the_values_only_read_live()
    {
        var history = new SampleHistory();
        history.Add(S(1, null, ("memory/usage", 50), ("memory/used", 8e9), ("memory/total", 16e9), ("storage/total/read", 1), ("storage/d0/read", 1), ("storage/d0/write", 2),
            ("storage/d0/active", 3), ("storage/d0/temperature", 40), ("storage/d0/life", 99), ("storage/d0/used", 1), ("storage/d0/volume/C/free", 5e11),
            ("/lpc/nct6798d/0/temperature/0", 35), ("/nvme/0/temperature/0", 41), ("/windows/thermalzones/TZ0", 45)));
        Assert.Equal(["/lpc/nct6798d/0/temperature/0", "/nvme/0/temperature/0", "/windows/thermalzones/TZ0", "memory/usage", "storage/d0/read", "storage/d0/write", "storage/total/read"],
            history.Read()[0].Values.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Readings_are_kept_to_float_precision_and_stay_readings()
    {
        var history = new SampleHistory();
        history.Add(S(1, null, ("a", 0.1), ("b", 1e300), ("c", -1e300), ("d", 123456789.123)));
        var values = history.Read()[0].Values;
        Assert.Equal((double)0.1f, values["a"]);
        Assert.Equal((double)float.MaxValue, values["b"]);
        Assert.Equal((double)float.MinValue, values["c"]);
        Assert.Equal((double)123456789.123f, values["d"]);
    }

    [Fact]
    public void A_poll_of_300_values_costs_little_more_than_a_float_each()
    {
        var keys = Enumerable.Range(0, 300).Select(i => $"/hardware/{i}/sensor").ToArray();
        var values = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double?>(keys.ToDictionary(key => key, key => (double?)key.Length));
        var samples = Enumerable.Range(0, 3 * 4096).Select(i => new Snapshot(Epoch.AddSeconds(i * 0.5), i * 0.5, values)).ToArray();
        var history = new SampleHistory();
        foreach (var sample in samples.Take(4096)) history.Add(sample);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var sample in samples.Skip(4096)) history.Add(sample);
        double perSample = (GC.GetAllocatedBytesForCurrentThread() - before) / (2 * 4096d);
        // A dictionary copy per poll allocated some 11 KB for 300 values; a float column is 1.2 KB of it.
        Assert.InRange(perSample, 1200, 2000);
    }

    [Fact]
    public async Task Reads_while_the_collector_adds_see_whole_samples_in_order()
    {
        var history = new SampleHistory();
        double newest = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                history.Add(S(i * 0.5, i % 500 < 3 ? "saved" : null, ("a", i), ("b", i % 7 == 0 ? null : -i)));
                Volatile.Write(ref newest, i * 0.5); i++;
            }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                double now = Volatile.Read(ref newest) + 1;
                var series = history.Series("a", now, 21600);
                Assert.True(series.Zip(series.Skip(1)).All(pair => pair.First.MonotonicSeconds < pair.Second.MonotonicSeconds));
                Assert.All(series, p => Assert.Equal(p.MonotonicSeconds * 2, p.Value));
                history.ReduceSeries("b", now, 3600, 600);
                history.ChartRanges("a", now, 21600, 600);
                history.Summaries(["a", "b"], now, 300);
            }
        })).ToArray();
        await Task.WhenAll([writer, .. readers]);
    }
}
