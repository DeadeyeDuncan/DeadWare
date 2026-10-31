using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

internal static class NetworkChartUiChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            // One 1 MB/s burst is lower after averaging than the other direction's
            // sustained 200/300 KB/s. Both directions must determine the shared axis.
            foreach (bool uploadDominates in new[] { true, false })
            {
                var history = new SampleHistory();
                var epoch = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
                for (int second = -21600; second <= 0; second++)
                    history.Add(new(epoch.AddSeconds(second), second, new Dictionary<string, double?>
                    {
                        ["network/download"] = uploadDominates ? second == -5 ? 1_000_000 : 0 : 300_000,
                        ["network/upload"] = uploadDominates ? 200_000 : second == -5 ? 1_000_000 : 0
                    }));
                var session = Session(history);
                var chart = new HistoryChart(session, "network");
                chart.Measure(new Size(620, 240)); chart.Arrange(new Rect(0, 0, 620, 240)); chart.UpdateLayout();
                Summary? statistics = null;
                chart.StatisticsReady += summary => statistics = summary;
                DrawingGroup Render(int seconds, bool smoothing)
                {
                    session.Settings.HistorySeconds = seconds; session.Settings.GraphSmoothing = smoothing; chart.Refresh();
                    var drawing = Draw(chart);
                    Assert.Equal(uploadDominates ? 0d : 300_000d, statistics!.Minimum);
                    Assert.Equal(uploadDominates ? 1_000_000d : 300_000d, statistics.Maximum);
                    Assert.Equal(uploadDominates ? 1_000_000d / (seconds + 1) : 300_000d, statistics.Average!.Value, 7);
                    return drawing;
                }
                foreach (int seconds in new[] { 60, 300, 900 })
                    AssertAxis(Render(seconds, true), "575.0 KB/s", "1.2 MB/s");
                foreach (int seconds in new[] { 3600, 10800, 21600 })
                {
                    var raw = Render(seconds, false);
                    AssertAxis(raw, "575.0 KB/s", "1.2 MB/s");
                    var smoothed = Render(seconds, true);
                    if (seconds == 3600)
                    {
                        string name = uploadDominates ? "upload" : "download";
                        Capture(raw, $"network-{name}-raw.png"); Capture(smoothed, $"network-{name}-smoothed.png");
                    }
                    AssertAxis(smoothed, uploadDominates ? "115.0 KB/s" : "172.5 KB/s", uploadDominates ? "230.0 KB/s" : "345.0 KB/s");
                    // Switching back on the same chart must immediately restore raw peak scaling.
                    AssertAxis(Render(seconds, false), "575.0 KB/s", "1.2 MB/s");
                }
            }
            foreach (double? idle in new double?[] { 0, null })
            {
                var history = new SampleHistory();
                history.Add(new(DateTimeOffset.UnixEpoch, 0, new Dictionary<string, double?>
                    { ["network/download"] = idle, ["network/upload"] = idle }));
                var session = Session(history);
                var chart = new HistoryChart(session, "network");
                chart.Measure(new Size(620, 240)); chart.Arrange(new Rect(0, 0, 620, 240)); chart.UpdateLayout();
                foreach (bool smoothing in new[] { true, false })
                {
                    session.Settings.GraphSmoothing = smoothing; chart.Refresh();
                    AssertAxis(Draw(chart), "500.0 B/s", "1.0 KB/s");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    private static UiSession Session(SampleHistory history)
    {
        void Set(object target, string field, object value) => target.GetType().GetField(field, Fields)!.SetValue(target, value);
        var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector, "clock", new Stopwatch()); Set(collector, "<History>k__BackingField", history);
        Set(collector, "latest", new Reading(history.Read()[^1], [], [], [], "Fixture CPU", "", "", "", false));
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session, "<Collector>k__BackingField", collector);
        Set(session, "<Settings>k__BackingField", new UserSettings { HistorySeconds = 21600 });
        return session;
    }

    private static DrawingGroup Draw(HistoryChart chart)
    {
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        typeof(HistoryChart).GetMethod("OnRender", Fields)!.Invoke(chart, [context]);
        return drawing;
    }

    private static void AssertAxis(Drawing drawing, string middle, string maximum)
    {
        IEnumerable<GlyphRunDrawing> Glyphs(Drawing value) => value is GlyphRunDrawing glyph ? [glyph]
            : value is DrawingGroup group ? group.Children.SelectMany(Glyphs) : [];
        var labels = Glyphs(drawing).Select(glyph => new string(glyph.GlyphRun.Characters.ToArray()))
            .Where(label => label.EndsWith("/s", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "0.0 B/s", middle, maximum }, labels);
    }

    private static void Capture(Drawing drawing, string name)
    {
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        if (output is null) return;
        Directory.CreateDirectory(output);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        { context.DrawRectangle(Theme.Brush("Background"), null, new Rect(0, 0, 620, 240)); context.DrawDrawing(drawing); }
        var bitmap = new RenderTargetBitmap(620, 240, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
