using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
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
                // Parse only the real styles, never the Application root/startup code.
                var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml"));
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var dictionary = new XElement(presentation + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:DeadWare.App;assembly=DeadWare"),
                    source.Root!.Element(presentation + "Application.Resources")!.Elements());
                // The app's internal static corner brush is the sole code-backed resource;
                // use the same color without introducing a second Application instance.
                foreach (var color in dictionary.Descendants().Attributes("Color").Where(a => a.Value.Contains("local:Theme.ScrollCorner")))
                    color.Value = "#151B24";
                app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
                var assembly = typeof(Collector).Assembly;
                var pageType = assembly.GetType("DeadWare.App.BatteryHistoryPage"); Assert.NotNull(pageType);
                var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
                var collector = (Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
                void Set(object target, string name, object value) => target.GetType().GetField(name, All)!.SetValue(target, value);
                // A frozen elapsed clock keeps fixture freshness independent of runner speed.
                Set(collector, "clock", new Stopwatch());
                Set(collector, "latest", new Reading(new(DateTimeOffset.UtcNow, 0, new Dictionary<string, double?>()), [], [], [], "CPU", "", "", "", false)
                    { Battery = new(73, BatteryPowerState.Charging, true) });
                Set(session, "<Collector>k__BackingField", collector);
                Set(session, "<Settings>k__BackingField", new UserSettings());
                Set(session, "<Battery>k__BackingField", new BatteryHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture"));
                var now = DateTimeOffset.UtcNow;
                foreach (string theme in new[] { "Midnight", "Daylight" })
                {
                    assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
                    var traceType = assembly.GetType("DeadWare.App.BatteryHistoryChart")!;
                    var trace = (FrameworkElement)Activator.CreateInstance(traceType, true)!;
                    var start = now.AddMinutes(-9);
                    var traceBuckets = Enumerable.Range(0, 7).Select(i => Bucket(start.AddMinutes(i), 90 - i * 4) with
                    { SaverSamples = i is 2 or 3 ? 1 : 0, SaverKnownSamples = i == 5 ? 0 : 1 }).ToList();
                    traceBuckets.Add(Bucket(start.AddMinutes(7), 60) with
                    { End = start.AddMinutes(8), Minimum = 55, Maximum = 65, Samples = 10, ValidSamples = 10, SaverSamples = 4, SaverKnownSamples = 10 });
                    var traceView = new BatteryHistoryView(start, now, traceBuckets, 17, start, 12345, "Fixture saved locally.");
                    traceType.GetMethod("SetData", All)!.Invoke(trace, [traceView]);
                    trace.Measure(new Size(620, 320)); trace.Arrange(new Rect(0, 0, 620, 320)); trace.UpdateLayout();
                    var drawings = Drawings(VisualTreeHelper.GetDrawing(trace)!).ToArray();
                    var lines = drawings.Where(d => d.Geometry is LineGeometry line && line.StartPoint.X < line.EndPoint.X && line.StartPoint.Y != line.EndPoint.Y).ToArray();
                    Color ResourceColor(string key) => ((SolidColorBrush)app.Resources[key]).Color;
                    Assert.Equal(new[] { "Accent", "Accent", "Warning", "Warning", "Accent", "Muted" }.Select(ResourceColor),
                        lines.Select(d => ((SolidColorBrush)d.Pen.Brush).Color));
                    Assert.Empty(lines[0].Pen.DashStyle.Dashes);
                    Assert.NotEmpty(lines[2].Pen.DashStyle.Dashes);
                    Assert.NotEqual(lines[2].Pen.DashStyle.Dashes.ToArray(), lines[5].Pen.DashStyle.Dashes.ToArray());
                    // Unknown and mixed summary points must not imply a single known saver state.
                    Assert.Equal(2, drawings.Count(d => d.Geometry is EllipseGeometry && d.Brush is null && d.Pen is not null));
                    string? traceOutput = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                    if (traceOutput is not null)
                    {
                        Directory.CreateDirectory(traceOutput);
                        var tracePage = (FrameworkElement)Activator.CreateInstance(pageType, All, null, [session, false], null)!;
                        pageType.GetMethod("ApplyView", All)!.Invoke(tracePage, [traceView]);
                        tracePage.Measure(new Size(620, 760)); tracePage.Arrange(new Rect(0, 0, 620, 760)); tracePage.UpdateLayout();
                        var traceBitmap = new RenderTargetBitmap(620, 760, 96, 96, PixelFormats.Pbgra32); traceBitmap.Render(tracePage);
                        using var stream = File.Create(Path.Combine(traceOutput, $"battery-{theme}-saver.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(traceBitmap)); encoder.Save(stream);
                    }
                    foreach (bool empty in new[] { true, false })
                    {
                        var page = (FrameworkElement)Activator.CreateInstance(pageType, All, null, [session, true], null)!;
                        var buckets = empty ? Array.Empty<BatteryHistoryBucket>() : Enumerable.Range(0, 200).Select(i =>
                            Bucket(now.AddYears(-5).AddDays(i * 8), 20 + i % 80) with
                            { Minimum = 10, Maximum = 100, Samples = 10, ValidSamples = 10, ChargingSamples = 5, SaverSamples = 4, SaverKnownSamples = 8 }).ToArray();
                        var view = new BatteryHistoryView(now.AddYears(-5), now, buckets, empty ? 0 : 2000, empty ? null : now.AddYears(-5), 12345, "Fixture saved locally.");
                        pageType.GetMethod("ApplyView", All)!.Invoke(page, [view]);
                        var selector = (ComboBox)pageType.GetField("range", All)!.GetValue(page)!;
                        selector.SelectedIndex = empty ? 0 : 4;
                        double width = empty ? 620 : 1050;
                        page.Measure(new Size(width, 760)); page.Arrange(new Rect(0, 0, width, 760)); page.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)width, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                        Assert.Equal(width, page.ActualWidth); Assert.Equal(760, page.ActualHeight);
                        var text = string.Join("\n", Descendants(page).OfType<TextBlock>().Select(t => t.Text));
                        var currentCard = (Border)((StackPanel)((ScrollViewer)page).Content).Children[0];
                        Assert.DoesNotContain("saver", string.Join(" ", Descendants(currentCard).OfType<TextBlock>().Select(t => t.Text)), StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("Charging", text);
                        Assert.Contains("Saver off", text); Assert.Contains("Saver on", text);
                        Assert.Contains("retained indefinitely", text);
                        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                        if (output is not null)
                        {
                            Directory.CreateDirectory(output); using var stream = File.Create(Path.Combine(output, $"battery-{theme}-{(empty ? "empty" : "years")}.png"));
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
                        }
                    }
                }
                var current = collector.Latest!;
                Set(collector, "latest", current with { Sample = current.Sample with { MonotonicSeconds = -10 } });
                var stalePage = (FrameworkElement)Activator.CreateInstance(pageType, All, null, [session, false], null)!;
                stalePage.Measure(new Size(620, 760)); stalePage.Arrange(new Rect(0, 0, 620, 760)); stalePage.UpdateLayout();
                var staleText = string.Join("\n", Descendants(stalePage).OfType<TextBlock>().Select(t => t.Text));
                Assert.Contains("Waiting for a fresh reading", staleText); Assert.DoesNotContain("Battery saver unknown", staleText);
                var updatesType=assembly.GetType("DeadWare.App.UpdatesPage"); Assert.NotNull(updatesType);
                var updateService=new UpdateService("0.6.0",Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")));
                Set(session,"<Updates>k__BackingField",updateService);
                foreach(string theme in new[]{"Midnight","Daylight"})
                {
                    assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply",All)!.Invoke(null,[theme]);
                    var updates=(FrameworkElement)Activator.CreateInstance(updatesType,All,null,[session],null)!;
                    var state=new UpdateState("0.6.0"){Status="No normal release has been published yet.",LocalStatus="No earlier packaged installation is available for rollback yet."};
                    updatesType.GetMethod("ApplyState",All)!.Invoke(updates,[state]);
                    updates.Measure(new Size(620,720));updates.Arrange(new Rect(0,0,620,720));updates.UpdateLayout();
                    Assert.NotNull(((ScrollViewer)updates).Background);
                    var updateText=string.Join("\n",Descendants(updates).OfType<TextBlock>().Select(t=>t.Text));
                    Assert.Contains("0.6.0",updateText);Assert.Contains("No normal release",updateText);
                    var buttons=Descendants(updates).OfType<Button>().ToArray();
                    Assert.False(buttons.Single(b=>b.Content?.ToString()=="Update & restart").IsEnabled);
                    Assert.False(buttons.Single(b=>b.Content?.ToString()!.StartsWith("Roll back")==true).IsEnabled);
                    string? output=Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                    if(output is not null)
                    {
                        var bitmap=new RenderTargetBitmap(620,720,96,96,PixelFormats.Pbgra32);bitmap.Render(updates);
                        using var file=File.Create(Path.Combine(output,$"updates-{theme}.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(file);
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
    private static IEnumerable<GeometryDrawing> Drawings(Drawing drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var nested in Drawings(child)) yield return nested;
    }
    private static BatteryHistoryBucket Bucket(DateTimeOffset stamp, double value = 80) =>
        new(stamp, stamp, value, value, value, 1, 1, 0, 0, 1, "fixture");
}
