using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class BatteryUiTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Fact]
    public void Charge_trace_only_connects_contiguous_known_observations_in_the_same_segment()
    {
        var type = typeof(Collector).Assembly.GetType("DeadWare.App.BatteryHistoryChart"); Assert.NotNull(type);
        var method = type.GetMethod("CanConnect", All)!; Assert.NotNull(method);
        var start = DateTimeOffset.UtcNow;
        var first = Bucket(start); var next = Bucket(start.AddMinutes(1));
        bool Connect(BatteryHistoryBucket a, BatteryHistoryBucket b) => (bool)method.Invoke(null, [a, b])!;
        Assert.True(Connect(first, next));
        Assert.False(Connect(first, next with { Segment = "restart" }));
        Assert.False(Connect(first, next with { Samples = 3 }));
        Assert.False(Connect(first, next with { ValidSamples = 0, Average = null }));
        Assert.False(Connect(first, next with { Start = start.AddHours(1) }));
    }

    [Fact]
    public void Real_battery_page_renders_dark_light_empty_and_multi_year_without_native_windows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A plain Application has no DeadWare startup handler, settings IO or hardware worker.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var assembly = typeof(Collector).Assembly;
                var pageType = assembly.GetType("DeadWare.App.BatteryHistoryPage"); Assert.NotNull(pageType);
                var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
                var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
                void Set(object target, string name, object value) => target.GetType().GetField(name, All)!.SetValue(target, value);
                Set(collector, "clock", Stopwatch.StartNew());
                Set(collector, "latest", new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()), [], [], [], "CPU", "", "", "", false)
                    { Battery = new(73, BatteryPowerState.Charging, true) });
                Set(session, "<Collector>k__BackingField", collector);
                Set(session, "<Settings>k__BackingField", new UserSettings());
                Set(session, "<Battery>k__BackingField", new BatteryHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture"));
                var now = DateTimeOffset.UtcNow;
                foreach (string theme in new[] { "Midnight", "Daylight" })
                {
                    assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
                    foreach (bool empty in new[] { true, false })
                    {
                        var page = (FrameworkElement)Activator.CreateInstance(pageType, All, null, [session, true], null)!;
                        var buckets = empty ? Array.Empty<BatteryHistoryBucket>() : Enumerable.Range(0, 200).Select(i =>
                            Bucket(now.AddYears(-5).AddDays(i * 8), 20 + i % 80) with
                            { Minimum = 10, Maximum = 100, Samples = 10, ValidSamples = 10, ChargingSamples = 5, SaverSamples = 4, SaverKnownSamples = 8 }).ToArray();
                        var view = new BatteryHistoryView(now.AddYears(-5), now, buckets, empty ? 0 : 2000, empty ? null : now.AddYears(-5), 12345, "Fixture saved locally.");
                        pageType.GetMethod("ApplyView", All)!.Invoke(page, [view]);
                        double width = empty ? 620 : 1050;
                        page.Measure(new Size(width, 760)); page.Arrange(new Rect(0, 0, width, 760)); page.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)width, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                        Assert.Equal(width, page.ActualWidth); Assert.Equal(760, page.ActualHeight);
                        var text = string.Join("\n", Descendants(page).OfType<TextBlock>().Select(t => t.Text));
                        Assert.Contains("Battery saver on", text); Assert.Contains("Charging", text);
                        Assert.Contains("retained indefinitely", text);
                        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                        if (output is not null)
                        {
                            Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, $"battery-{theme}-{(empty ? "empty" : "years")}.png"));
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                        }
                    }
                }
                app.Shutdown();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offscreen rendering timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static BatteryHistoryBucket Bucket(DateTimeOffset stamp, double value = 80) =>
        new(stamp, stamp, value, value, value, 1, 1, 0, 0, 1, "fixture");
}
