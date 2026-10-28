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
                var noticeType=assembly.GetType("DeadWare.App.UpdateNotice");Assert.NotNull(noticeType);
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
                    int reviewed=0;
                    var notice=(FrameworkElement)Activator.CreateInstance(noticeType,All,null,[new Action(()=>reviewed++)],null)!;
                    var available=new DeadWare.UpdateCore.ReleaseInfo(new Version(0,6,2),"v0.6.2","Fixture",new Uri("https://example.invalid/update.zip"),new Uri("https://example.invalid/checksum"),new string('a',64),100);
                    void Notice(UpdateState value)=>noticeType.GetMethod("ApplyState",All)!.Invoke(notice,[value]);
                    Notice(new UpdateState("0.6.1"));Assert.Equal(Visibility.Collapsed,notice.Visibility);
                    Notice(new UpdateState("0.6.2"){Release=available});Assert.Equal(Visibility.Collapsed,notice.Visibility);
                    Notice(new UpdateState("0.6.1"){Release=available,Busy=true});Assert.Equal(Visibility.Visible,notice.Visibility);
                    notice.Measure(new Size(620,110));notice.Arrange(new Rect(0,0,620,110));notice.UpdateLayout();
                    Assert.Contains("0.6.2",string.Join(" ",Descendants(notice).OfType<TextBlock>().Select(t=>t.Text)));
                    var review=Descendants(notice).OfType<Button>().Single();Assert.Equal("Review update",review.Content);
                    review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(1,reviewed);
                    string? output=Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                    if(output is not null)
                    {
                        var noticeBitmap=new RenderTargetBitmap(620,110,96,96,PixelFormats.Pbgra32);noticeBitmap.Render(notice);
                        using(var noticeFile=File.Create(Path.Combine(output,$"startup-notice-{theme}.png")))
                        {var noticeEncoder=new PngBitmapEncoder();noticeEncoder.Frames.Add(BitmapFrame.Create(noticeBitmap));noticeEncoder.Save(noticeFile);}
                        var bitmap=new RenderTargetBitmap(620,720,96,96,PixelFormats.Pbgra32);bitmap.Render(updates);
                        using var file=File.Create(Path.Combine(output,$"updates-{theme}.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(file);
                    }
                }
                VerifyReleaseNotes(session, assembly);
                // Exercise the actual dashboard with an unstarted session: no hardware,
                // database writes, native window or installed settings are involved.
                Set(session, "captureWindows", new Dictionary<Window, Action>());
                Set(session, "layout", new DetachedWindowState([]));
                Set(session, "<GraphHistory>k__BackingField", new GraphHistoryService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"), "fixture", new SampleHistory()));
                Set(session, "<SettingsStatus>k__BackingField", "");
                Set(collector, "<History>k__BackingField", new SampleHistory());
                Set(collector, "latest", null!);
                var lifetime = new ApplicationLifetime();
                var dashboard = new MainWindow(session, lifetime, () => true, () => { });
                var shell = (Grid)dashboard.Content;
                var sidebar = (DockPanel)shell.Children[0];
                var navigation = (StackPanel)sidebar.Children.OfType<ScrollViewer>().Single().Content;
                var batteryNavigation = navigation.Children.OfType<Button>().Single(b => b.Content?.ToString()?.Contains("Battery history") == true);
                var pages = (Dictionary<string, FrameworkElement>)typeof(MainWindow).GetField("pages", All)!.GetValue(dashboard)!;
                pages["overview"].Measure(new Size(900, 900)); pages["overview"].Arrange(new Rect(0, 0, 900, 900)); pages["overview"].UpdateLayout();
                var overviewCards = Descendants(pages["overview"]).OfType<FrameworkElement>().Where(element => element.GetType().Name == "ChartCard").ToArray();
                Assert.Equal(6, overviewCards.Length);
                Assert.Single(overviewCards, card => (string)card.GetType().GetField("kind", All)!.GetValue(card)! == "memory");
                var settings = (StackPanel)((ScrollViewer)pages["desktop"]).Content;
                var widgetChoices = settings.Children.OfType<WrapPanel>().SelectMany(panel => panel.Children.OfType<CheckBox>()).Where(box => box.Tag is string kind && new[] { "cpu", "cputemp", "memory", "network" }.Contains(kind)).ToArray();
                Assert.Equal(4, widgetChoices.Length);
                Assert.All(widgetChoices, choice => Assert.True(choice.IsChecked));
                var widgetPin = Assert.Single(settings.Children.OfType<CheckBox>(), box => box.Content?.ToString() == "Widget always on top");
                Assert.True(widgetPin.IsChecked);
                Assert.DoesNotContain(settings.Children.OfType<Button>(), button => button.Content?.ToString()?.StartsWith("↗") == true);
                var joinedPopouts = Assert.Single(settings.Children.OfType<CheckBox>(), box => box.Content?.ToString() == "Join CPU and GPU graphs in pop-outs");
                Assert.False(joinedPopouts.IsChecked);
                var polling = Assert.Single(settings.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<ComboBox>()),
                    combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "Data polling interval");
                Assert.Equal(new[] { "0.5 seconds", "1 second (default)", "2 seconds", "5 seconds", "10 seconds" }, polling.Items.Cast<object>().Select(item => item.ToString()));
                Assert.Equal(1d, polling.SelectedValue);
                var pollingSettingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-polling-settings.json");
                var uiTimer = new System.Windows.Threading.DispatcherTimer();
                Set(session, "timer", uiTimer);
                Set(session, "writer", new SettingsSnapshotWriter(pollingSettingsPath));
                VerifyRangeDropdown(session, dashboard, assembly, pollingSettingsPath);
                foreach (double seconds in new double[] { 10, .5, 1 })
                {
                    polling.SelectedValue = seconds;
                    Assert.Equal(seconds, session.Settings.PollingIntervalSeconds);
                    Assert.Equal(seconds, collector.PollingIntervalSeconds);
                    Assert.Equal(Math.Min(1, seconds), uiTimer.Interval.TotalSeconds);
                    Assert.Contains(Descendants(sidebar).OfType<TextBlock>(), text => text.Text.Contains($"{seconds:0.#} s polling"));
                }
                var finalSave = session.SaveAsync();
                var saveFrame = new System.Windows.Threading.DispatcherFrame();
                var fixtureDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                _ = finalSave.ContinueWith(_ => fixtureDispatcher.BeginInvoke(new Action(() => saveFrame.Continue = false)), TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(saveFrame);
                finalSave.GetAwaiter().GetResult();
                Assert.Equal(1, SettingsCodec.Read(File.ReadAllText(pollingSettingsPath)).Settings.PollingIntervalSeconds);
                File.Delete(pollingSettingsPath);
                Set(collector, "latest", current with { Sample = current.Sample with { MonotonicSeconds = -11, PollingIntervalSeconds = 10 } });
                Assert.False(session.Stale);
                Set(collector, "latest", current with { Sample = current.Sample with { MonotonicSeconds = -14, PollingIntervalSeconds = 10 } });
                Assert.True(session.Stale);
                Set(collector, "latest", current);
                joinedPopouts.IsChecked = true; CompleteSettingsSave(session);
                Assert.True(SettingsCodec.Read(File.ReadAllText(pollingSettingsPath)).Settings.JoinCpuGpuPopouts);
                session.SetJoinedPopouts(false); Assert.False(joinedPopouts.IsChecked);
                session.SetWidgetGraph("network", false);
                Assert.False(widgetChoices.Single(choice => (string)choice.Tag == "network").IsChecked);
                session.SetWidgetGraph("network", true);
                Assert.All(widgetChoices, choice => Assert.True(choice.IsChecked));
                Assert.False(session.Settings.Windows.ContainsKey("widget"));
                widgetPin.IsChecked = false; CompleteSettingsSave(session);
                Assert.False(SettingsCodec.Read(File.ReadAllText(pollingSettingsPath)).Settings.WidgetAlwaysOnTop);
                Assert.False(session.Settings.Windows.ContainsKey("widget"));
                session.SetWidgetAlwaysOnTop(true); Assert.True(widgetPin.IsChecked);
                void RefreshDashboard() => ((Action)typeof(UiSession).GetField("Changed", All)!.GetValue(session)!).Invoke();
                void Navigate(string destination) => typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, [destination]);
                void AssertBatteryVisibility(Visibility expected)
                {
                    Assert.Equal(expected, batteryNavigation.Visibility);
                }
                AssertBatteryVisibility(Visibility.Visible);
                Set(collector, "latest", current with { Battery = new(null, BatteryPowerState.NoBattery, null) });
                RefreshDashboard(); AssertBatteryVisibility(Visibility.Collapsed);
                Set(collector, "latest", current with { Battery = BatteryReading.Unavailable });
                RefreshDashboard(); AssertBatteryVisibility(Visibility.Visible);
                foreach (var power in new[] { BatteryPowerState.OnBattery, BatteryPowerState.Charging, BatteryPowerState.PluggedIn })
                {
                    Set(collector, "latest", current with { Battery = new(73, power, false) });
                    RefreshDashboard(); AssertBatteryVisibility(Visibility.Visible);
                }
                Navigate("battery");
                Assert.Same(pages["battery"], ((ContentControl)typeof(MainWindow).GetField("content", All)!.GetValue(dashboard)!).Content);
                Set(collector, "latest", current with { Battery = new(null, BatteryPowerState.NoBattery, null) });
                RefreshDashboard(); AssertBatteryVisibility(Visibility.Collapsed);
                Assert.Same(pages["overview"], ((ContentControl)typeof(MainWindow).GetField("content", All)!.GetValue(dashboard)!).Content);
                // Suppress the startup-status query while rendering the real Settings page.
                Set(dashboard, "startupBusy", true); Navigate("desktop");
                string? dashboardOutput = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                if (dashboardOutput is not null)
                {
                    foreach (string theme in new[] { "Midnight", "Daylight" })
                    {
                        assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
                        shell.SetResourceReference(Panel.BackgroundProperty, "Background");
                        shell.Measure(new Size(1220, 900)); shell.Arrange(new Rect(0, 0, 1220, 900)); shell.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(1220, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(shell);
                        using var file = File.Create(Path.Combine(dashboardOutput, $"desktop-settings-{theme}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                }
                VerifyUpdateNotice(dashboard, updateService, pages, navigation, shell);
                lifetime.BeginSessionEnding(); dashboard.Close();
                VerifyWidget(session, collector, current, assembly, widgetChoices, widgetPin, pollingSettingsPath);
                app.Shutdown();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offscreen rendering timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyRangeDropdown(UiSession session, MainWindow dashboard, Assembly assembly, string settingsPath)
    {
        var toolbar = (DockPanel)typeof(MainWindow).GetField("liveToolbar", All)!.GetValue(dashboard)!;
        var main = Assert.IsType<ComboBox>(toolbar.Children[0]);
        var second = Assert.IsType<ComboBox>(assembly.GetType("DeadWare.App.Ui")!.GetMethod("Range", All)!.Invoke(null, [session]));
        int original = session.HistorySeconds;
        Assert.Equal(original, main.SelectedValue); Assert.Equal(original, second.SelectedValue);
        Assert.Equal("Graph history range", System.Windows.Automation.AutomationProperties.GetName(main));
        Assert.Equal(HistoryRanges.Seconds, main.Items.Cast<object>().Select(item => (int)item.GetType().GetProperty("Seconds")!.GetValue(item)!));
        Assert.Equal(HistoryRanges.Seconds.Select(HistoryRanges.Label), main.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!));
        main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        int notifications = 0; void Changed() => notifications++;
        Window? previewOwner = null;
        session.Changed += Changed;
        try
        {
            foreach (int seconds in HistoryRanges.Seconds)
            {
                int expected = session.HistorySeconds == seconds ? 0 : 1;
                notifications = 0; main.SelectedValue = seconds;
                Assert.Equal(seconds, session.HistorySeconds); Assert.Equal(seconds, second.SelectedValue);
                Assert.Equal(expected, notifications);
            }
            notifications = 0; second.SelectedValue = 10800;
            Assert.Equal(10800, main.SelectedValue); Assert.Equal(1, notifications);
            notifications = 0; session.SetRange(300);
            Assert.Equal(300, main.SelectedValue); Assert.Equal(300, second.SelectedValue); Assert.Equal(1, notifications);
            CompleteSettingsSave(session); Assert.Equal(300, SettingsCodec.Read(File.ReadAllText(settingsPath)).Settings.HistorySeconds);
            main.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); second.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            session.SetRange(60); Assert.Equal(300, main.SelectedValue); Assert.Equal(300, second.SelectedValue);
            main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(60, main.SelectedValue); Assert.Equal(60, second.SelectedValue);
            var themeType = assembly.GetType("DeadWare.App.Theme")!;
            var preview = new Border { Child = second };
            previewOwner = new Window { Content = preview };
            foreach (string theme in (string[])themeType.GetField("Names", All)!.GetValue(null)!)
            {
                themeType.GetMethod("Apply", All)!.Invoke(null, [theme]);
                preview.SetResourceReference(Border.BackgroundProperty, "Background");
                preview.Measure(new Size(160, 44)); preview.Arrange(new Rect(0, 0, 160, 44)); preview.UpdateLayout();
                Color ColorOf(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
                Assert.Equal(ColorOf("Control"), ((SolidColorBrush)second.Background).Color);
                Assert.Equal(ColorOf("Ink"), ((SolidColorBrush)second.Foreground).Color);
                Assert.Contains(Descendants(second).OfType<TextBlock>(), block => block.Text == HistoryRanges.Label(session.HistorySeconds));
                Assert.True(second.ActualHeight < 45); Assert.True(second.ActualWidth < 150);
                string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                if (output is not null)
                {
                    var bitmap = new RenderTargetBitmap(160, 44, 96, 96, PixelFormats.Pbgra32); bitmap.Render(preview);
                    using var file = File.Create(Path.Combine(output, $"history-range-{theme}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                }
            }
        }
        finally
        {
            session.Changed -= Changed;
            session.SetRange(original);
            main.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); second.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            CompleteSettingsSave(session);
            if (previewOwner is not null)
            {
                Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(previewOwner).Handle);
                previewOwner.Close();
            }
        }
    }

    private static void VerifyReleaseNotes(UiSession session, Assembly assembly)
    {
        var type = assembly.GetType("DeadWare.App.UpdatesPage")!;
        var page = (ScrollViewer)Activator.CreateInstance(type, All, null, [session], null)!;
        var release = new DeadWare.UpdateCore.ReleaseInfo(new Version(0, 6, 5), "v0.6.5", "Newest changes", new Uri("https://example.invalid/update.zip"), new Uri("https://example.invalid/checksum"), new string('a', 64), 100);
        void Apply(UpdateState state) { type.GetMethod("ApplyState", All)!.Invoke(page, [state]); page.UpdateLayout(); }
        TextBlock[] Text() => Descendants(page).OfType<TextBlock>().ToArray();
        string[] Versions() => Text().Where(block => block.Text.StartsWith("v0.6.")).Select(block => block.Text).ToArray();
        Apply(new UpdateState("0.6.2") { Release = release });
        page.Measure(new Size(620, 720)); page.Arrange(new Rect(0, 0, 620, 720)); page.UpdateLayout();
        Assert.Single(Descendants(page).OfType<Button>(), button => button.Content?.ToString() == "Show release note history");
        Assert.Contains(Text(), block => block.Text == "v0.6.5");
        Assert.Contains(Text(), block => block.Text == "Newest changes");
        string longNotes = string.Join("\n", Enumerable.Range(1, 30).Select(index => $"Change {index}: detailed release notes wrap within the available page width and remain readable while scrolling."));
        var state = new UpdateState("0.6.2")
        {
            Release = release,
            ReleaseNotes = [new(new(0, 6, 5), "v0.6.5", "Newest changes"), new(new(0, 6, 4), "v0.6.4", longNotes), new(new(0, 6, 3), "v0.6.3", " \n")]
        };
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight" })
        {
            assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
            page = (ScrollViewer)Activator.CreateInstance(type, All, null, [session], null)!;
            foreach (var size in new[] { new Size(420, 720), new Size(900, 900) })
            {
                Apply(state); page.Measure(size); page.Arrange(new Rect(new Point(), size)); page.UpdateLayout();
                Assert.Equal(new[] { "v0.6.5", "v0.6.4", "v0.6.3" }, Versions());
                Assert.DoesNotContain(Text(), block => block.Text == "v0.6.2");
                Assert.Contains(Text(), block => block.Text == "No release notes provided.");
                Assert.Equal(ScrollBarVisibility.Auto, page.VerticalScrollBarVisibility);
                Assert.True(page.ExtentHeight > page.ViewportHeight);
                var notesBody = Text().Single(block => block.Text == longNotes);
                Assert.Equal(TextWrapping.Wrap, notesBody.TextWrapping); Assert.True(notesBody.ActualWidth < size.Width);
                var first = Text().Single(block => block.Text == "v0.6.5");
                Apply(state with { Busy = true, Progress = 25 }); Assert.Same(first, Text().Single(block => block.Text == "v0.6.5"));
                if (output is not null)
                {
                    foreach (bool end in new[] { false, true })
                    {
                        double notesTop = Text().Single(block => block.Text == "Release notes").TransformToAncestor(page).Transform(new Point()).Y + page.VerticalOffset;
                        page.ScrollToVerticalOffset(end ? page.ExtentHeight : Math.Max(0, notesTop - 8)); page.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                        using var file = File.Create(Path.Combine(output, $"release-notes-{theme}-{size.Width:0}-{(end ? "end" : "start")}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                }
            }
        }
        Apply(state with { ReleaseNotes = [new(new(0, 6, 6), "v0.6.6", "Replacement history")] });
        Assert.Equal(new[] { "v0.6.6" }, Versions()); Assert.DoesNotContain(Text(), block => block.Text == longNotes);
        Apply(state with { ReleaseNotes = [], ReleaseNotesStatus = "Earlier release notes could not be loaded." });
        Assert.Equal(new[] { "v0.6.5" }, Versions()); Assert.Contains(Text(), block => block.Text == "Newest changes");
        var text = Text(); Assert.True(Array.FindIndex(text, block => block.Text == "Earlier release notes could not be loaded.") < Array.FindIndex(text, block => block.Text == "v0.6.5"));
        Apply(state with { ReleaseNotes = [], ReleaseNotesStatus = "Loading release notes…" });
        Assert.Contains(Text(), block => block.Text == "Loading release notes…"); Assert.Contains(Text(), block => block.Text == "Newest changes");
        Apply(new UpdateState("0.6.2") { Release = release with { Notes = "" } });
        Assert.Contains(Text(), block => block.Text == "No release notes provided."); Assert.DoesNotContain(Text(), block => block.Text == "Loading release notes…");
        Apply(new UpdateState("0.6.2")); Assert.Empty(Versions());
        Assert.Contains(Text(), block => block.Text == "Release notes appear after a published release is found.");
        VerifyReleaseHistory(session, assembly, release, longNotes);
    }

    private static void VerifyReleaseHistory(UiSession session, Assembly assembly, DeadWare.UpdateCore.ReleaseInfo release, string longNotes)
    {
        var original = session.Updates;
        using var handler = new ReleaseHistoryUiHandler(longNotes);
        using var http = new System.Net.Http.HttpClient(handler);
        var service = new UpdateService("0.6.5", http: http);
        var initial = new UpdateState("0.6.5") { Release = release, ReleaseNotes = [new(release.Version, release.Tag, release.Notes)] };
        typeof(UpdateService).GetField("state", All)!.SetValue(service, initial);
        typeof(UiSession).GetField("<Updates>k__BackingField", All)!.SetValue(session, service);
        try
        {
            var type = assembly.GetType("DeadWare.App.UpdatesPage")!;
            foreach (string theme in new[] { "Midnight", "Daylight" })
            {
                assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
                var page = (ScrollViewer)Activator.CreateInstance(type, All, null, [session], null)!;
                page.Measure(new Size(620, 720)); page.Arrange(new Rect(0, 0, 620, 720)); page.UpdateLayout();
                void Apply(UpdateState state) => type.GetMethod("ApplyState", All)!.Invoke(page, [state]);
                void Changed()
                {
                    if (page.Dispatcher.CheckAccess()) Apply(service.State);
                    else page.Dispatcher.BeginInvoke(new Action(() => Apply(service.State)));
                }
                service.Changed += Changed;
                try
                {
                    string[] Versions() => Descendants(page).OfType<TextBlock>().Where(block => block.Text.StartsWith("v0.")).Select(block => block.Text).ToArray();
                    var button = Descendants(page).OfType<Button>().Single(item => item.Content?.ToString() == "Show release note history");
                    var retry = Descendants(page).OfType<Button>().Single(item => item.Content?.ToString() == "Retry history");
                    foreach (var size in new[] { new Size(420, 720), new Size(900, 900) })
                    {
                        int before = handler.Calls;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        page.Measure(size); page.Arrange(new Rect(new Point(), size)); page.UpdateLayout();
                        Assert.Equal(before + 1, handler.Calls); Assert.Equal("Hide release note history", button.Content);
                        Assert.Equal(new[] { "v0.6.5", "v0.6.4", "v0.6.2", "v0.5.0" }, Versions());
                        Assert.Equal(Visibility.Collapsed, retry.Visibility); Assert.True(page.ExtentHeight > page.ViewportHeight);
                        var text = Descendants(page).OfType<TextBlock>().Single(block => block.Text == longNotes);
                        Assert.True(text.ActualWidth < size.Width); Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                        Apply(service.State with { Busy = true }); Assert.False(button.IsEnabled);
                        Apply(service.State with { ReleaseHistoryStatus = "Could not load release note history. Try again." }); Assert.Equal(Visibility.Visible, retry.Visibility);
                        Apply(service.State); Assert.Equal(Visibility.Collapsed, retry.Visibility);
                        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
                        if (output is not null)
                        {
                            double top = Descendants(page).OfType<TextBlock>().Single(block => block.Text == "Release note history").TransformToAncestor(page).Transform(new Point()).Y + page.VerticalOffset;
                            page.ScrollToVerticalOffset(Math.Max(0, top - 8)); page.UpdateLayout();
                            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(page);
                            using var file = File.Create(Path.Combine(output, $"full-release-history-{theme}-{size.Width:0}.png"));
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                        }
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Equal(before + 1, handler.Calls); Assert.Equal("Show release note history", button.Content);
                        Assert.Equal(new[] { "v0.6.5" }, Versions());
                    }
                    handler.Fail = true; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Visible, retry.Visibility);
                    Assert.Contains("Could not load", string.Join(" ", Descendants(page).OfType<TextBlock>().Select(block => block.Text)));
                    handler.Fail = false; int calls = handler.Calls;
                    retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(calls + 1, handler.Calls); Assert.Equal(Visibility.Collapsed, retry.Visibility);
                    Assert.Equal(new[] { "v0.6.5", "v0.6.4", "v0.6.2", "v0.5.0" }, Versions());
                    Apply(service.State with { ReleaseHistory = [], ReleaseHistoryLoaded = true });
                    Assert.Contains(Descendants(page).OfType<TextBlock>(), block => block.Text == "No normal release notes have been published.");
                }
                finally { service.Changed -= Changed; }
            }
        }
        finally { typeof(UiSession).GetField("<Updates>k__BackingField", All)!.SetValue(session, original); }
    }
    private sealed class ReleaseHistoryUiHandler(string longNotes) : System.Net.Http.HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            Assert.EndsWith("/releases", request.RequestUri!.AbsolutePath); Calls++;
            if (Fail) return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            var json = System.Text.Json.JsonSerializer.Serialize(new[] { ("v0.6.5", "Newest changes"), ("v0.6.4", longNotes), ("v0.6.2", "Installed earlier"), ("v0.5.0", "Older changes") }
                .Select(entry => new { tag_name = entry.Item1, body = entry.Item2, draft = false, prerelease = false }));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(json) });
        }
    }

    private static void VerifyUpdateNotice(MainWindow dashboard, UpdateService updates, Dictionary<string, FrameworkElement> pages, StackPanel navigation, Grid shell)
    {
        var notice = (Border)typeof(MainWindow).GetField("updateNotice", All)!.GetValue(dashboard)!;
        var content = (ContentControl)typeof(MainWindow).GetField("content", All)!.GetValue(dashboard)!;
        var available = new DeadWare.UpdateCore.ReleaseInfo(new Version(0, 7, 0), "v0.7.0", "Fixture", new Uri("https://example.invalid/update.zip"), new Uri("https://example.invalid/checksum"), new string('a', 64), 100);
        void Navigate(string destination) => typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, [destination]);
        void State(UpdateState state)
        {
            typeof(UpdateService).GetField("state", All)!.SetValue(updates, state);
            ((Action)typeof(UpdateService).GetField("Changed", All)!.GetValue(updates)!).Invoke();
        }
        void Capture(string name)
        {
            string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT"); if (output is null) return;
            shell.SetResourceReference(Panel.BackgroundProperty, "Background");
            shell.Measure(new Size(1220, 900)); shell.Arrange(new Rect(0, 0, 1220, 900)); shell.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1220, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(shell);
            using var file = File.Create(Path.Combine(output, name + ".png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
        }
        Navigate("overview"); State(new UpdateState("0.6.0") { Release = available });
        Assert.Equal(Visibility.Visible, notice.Visibility); Capture("update-notice-overview");
        ((Grid)notice.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(pages["updates"], content.Content); Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Capture("update-notice-updates");
        State(updates.State with { Busy = true }); Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Task.Run(() => updates.ReportFailure(new IOException("Fixture status change"))).GetAwaiter().GetResult();
        dashboard.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Navigate("overview"); Assert.Equal(Visibility.Visible, notice.Visibility);
        navigation.Children.OfType<Button>().Single(button => button.Content?.ToString()?.Contains("Updates") == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(pages["updates"], content.Content); Assert.Equal(Visibility.Collapsed, notice.Visibility);
        State(new UpdateState("0.6.0")); Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Navigate("overview"); Assert.Equal(Visibility.Collapsed, notice.Visibility);
        Navigate("updates"); Assert.Equal(Visibility.Collapsed, notice.Visibility);
    }

    private static void VerifyWidget(UiSession session, Collector collector, Reading current, Assembly assembly, CheckBox[] widgetChoices, CheckBox widgetPin, string settingsPath)
    {
        void Set(object target, string name, object? value) => target.GetType().GetField(name, All)!.SetValue(target, value);
        void Changed() => ((Action)typeof(UiSession).GetField("Changed", All)!.GetValue(session)!).Invoke();
        var history = new SampleHistory();
        for (int seconds = -55; seconds <= 0; seconds++)
        {
            if (seconds is -18 or -17 or -16) continue;
            history.Add(new(DateTimeOffset.UtcNow.AddSeconds(seconds), seconds, new Dictionary<string, double?>
            {
                ["cpu/usage"] = seconds == -40 ? null : 35 + 15 * Math.Sin(seconds / 5.0), ["cpu/temperature"] = seconds == -30 ? null : seconds == -3 ? 125 : 60 + 5 * Math.Sin(seconds / 7.0),
                ["gpu-a/usage"] = 5, ["gpu-a/temperature"] = 40,
                ["gpu-b/usage"] = 75 + 10 * Math.Sin(seconds / 4.0), ["gpu-b/temperature"] = seconds == -25 ? null : seconds == -4 ? 145 : 90 + 5 * Math.Sin(seconds / 6.0),
                ["memory/used"] = 8e9, ["memory/total"] = 32e9,
                ["memory/usage"] = seconds == -34 ? null : 25 + 20 * Math.Sin(seconds / 6.0),
                ["network/download"] = seconds == -36 ? null : 12345678 + 2e6 * Math.Sin(seconds / 5.0),
                ["network/upload"] = seconds == -32 ? null : 34567890 + 6e6 * Math.Sin(seconds / 4.0)
            }) { SessionId = seconds >= -10 ? "restart" : "fixture" });
        }
        Set(collector, "<History>k__BackingField", history);
        var reading = current with { Sample = history.Read()[^1], Gpus = [new("gpu-a", "First GPU", true), new("gpu-b", "Selected GPU", true)] };
        Set(collector, "latest", reading); session.Settings.HistorySeconds = 60; session.Settings.SelectedGpuId = "gpu-b";
        VerifyJoinedPopouts(session, collector, reading, assembly, history);
        var widgetType = assembly.GetType("DeadWare.App.WidgetWindow")!;
        var widget = (Window)Activator.CreateInstance(widgetType, All, null, [session], null)!;
        Assert.Equal(300, widget.MinWidth); Assert.Equal(520, widget.MinHeight);
        Assert.Equal(340, widget.Width); Assert.Equal(600, widget.Height);
        var content = (FrameworkElement)widget.Content;
        Assert.Empty(Descendants(content).OfType<CheckBox>());
        // Capture the viewport parent: rendering the Margin-bearing child directly
        // shifts its drawing but clips it at the unshifted child size.
        widget.Content = null;
        var viewport = new Border { Child = content };
        System.Windows.Documents.TextElement.SetFontFamily(viewport, widget.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(viewport, widget.FontSize);
        widget.Content = viewport;
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight" })
        {
            assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]); Changed();
            foreach (var size in new[] { new Size(284, 481), new Size(324, 561), new Size(620, 750) })
            {
                viewport.SetResourceReference(Border.BackgroundProperty, "Background");
                viewport.Measure(size); viewport.Arrange(new Rect(new Point(), size)); viewport.UpdateLayout();
                var charts = Descendants(content).OfType<FrameworkElement>().Where(element => element.GetType().Name == "HistoryChart").ToArray();
                Assert.Equal(4, charts.Length);
                Assert.Equal(new[] { "cpu", "cputemp", "memory", "network" }, charts.Select(chart => (string)chart.GetType().GetField("kind", All)!.GetValue(chart)!));
                foreach (var chart in charts)
                {
                    Assert.True(chart.ActualHeight >= 48, $"Chart clipped at {size}: {chart.ActualHeight}");
                    var drawing = VisualTreeHelper.GetDrawing(chart)!;
                    var series = Drawings(drawing).Where(item => item.Geometry is StreamGeometry).ToArray();
                    string kind = (string)chart.GetType().GetField("kind", All)!.GetValue(chart)!;
                    Assert.Equal(kind == "memory" ? 1 : 2, series.Length);
                    Color ColorOf(string name) => ((SolidColorBrush)Application.Current.Resources[name]).Color;
                    Assert.Equal(ColorOf(kind == "memory" ? "Accent" : kind == "network" ? "Download" : "Cpu"), ((SolidColorBrush)series[0].Pen.Brush).Color);
                    Assert.Empty(series[0].Pen.DashStyle.Dashes);
                    if (kind != "memory")
                    {
                        Assert.Equal(ColorOf(kind == "network" ? "Cpu" : "Gpu"), ((SolidColorBrush)series[1].Pen.Brush).Color);
                        Assert.NotEmpty(series[1].Pen.DashStyle.Dashes);
                        Assert.Equal(kind == "cpu" ? 3 : 4, PathGeometry.CreateFromGeometry(series[1].Geometry).Figures.Count);
                    }
                    Assert.Equal(4, PathGeometry.CreateFromGeometry(series[0].Geometry).Figures.Count);
                    var grid = Drawings(drawing).Select(item => item.Geometry).OfType<LineGeometry>().Where(line => line.StartPoint.Y == line.EndPoint.Y).OrderBy(line => line.StartPoint.Y).ToArray();
                    double top = grid[0].StartPoint.Y, height = grid[^1].StartPoint.Y - top;
                    string metric = kind == "cpu" ? "usage" : "temperature";
                    double maximum = kind is "cpu" or "memory" ? 100 : kind == "network" ? history.Read().SelectMany(sample => new[] { sample.Values["network/download"], sample.Values["network/upload"] }).Max()!.Value * 1.15 : 160;
                    for (int i = 0; i < series.Length; i++)
                    {
                        string id = kind == "memory" ? "memory/usage" : kind == "network" ? "network/" + (i == 0 ? "download" : "upload") : (i == 0 ? "cpu" : "gpu-b") + "/" + metric;
                        double peak = history.Read().Select(sample => sample.Values[id]).Where(value => value.HasValue).Max()!.Value;
                        Assert.Equal(top + height * (1 - peak / maximum), series[i].Geometry.Bounds.Top, 3);
                    }
                }
                string text = string.Join("\n", Descendants(content).OfType<TextBlock>().Select(block => block.Text));
                var status = Descendants(content).OfType<TextBlock>().Single(block => block.Text.StartsWith("Shared collector"));
                var statusBounds = status.TransformToAncestor(content).TransformBounds(new Rect(status.RenderSize));
                Assert.True(statusBounds.Bottom <= content.ActualHeight + .01, $"Status extends below widget: {statusBounds} / {content.RenderSize}");
                Assert.True(statusBounds.Right <= content.ActualWidth + .01, $"Status extends outside widget: {statusBounds} / {content.RenderSize}");
                Assert.Equal(2, Descendants(content).OfType<TextBlock>().Count(block => block.Text.StartsWith("GPU ") && block.ToolTip?.ToString() == "Selected GPU"));
                Assert.Contains("CPU 35.0 %", text); Assert.Contains("GPU 75.0 %", text);
                Assert.Contains("CPU 60.0 °C", text); Assert.Contains("GPU 90.0 °C", text);
                Assert.Contains("Network", text); Assert.Contains("↓ 12.3 MB/s", text); Assert.Contains("↑ 34.6 MB/s", text);
                Assert.DoesNotContain("First GPU", text); Assert.Contains("RAM", text); Assert.Contains("25.0", text); Assert.Contains("8.0 GB", text); Assert.Contains("32.0 GB", text); Assert.Contains("Shared collector", text);
                if (output is not null)
                {
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport);
                    using var file = File.Create(Path.Combine(output, $"widget-{theme}-{size.Width:0}x{size.Height:0}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                }
            }
        }
        var comparisonCharts = Descendants(content).OfType<FrameworkElement>().Where(element => element.GetType().Name == "HistoryChart" && (string)element.GetType().GetField("kind", All)!.GetValue(element)! is "cpu" or "cputemp").ToArray();
        string[] selectedTraces = comparisonCharts.Select(chart => Drawings(VisualTreeHelper.GetDrawing(chart)!).Where(item => item.Geometry is StreamGeometry).Last().Geometry.ToString()).ToArray();
        session.Settings.SelectedGpuId = "gpu-a"; Changed(); content.UpdateLayout();
        string switched = string.Join("\n", Descendants(content).OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("GPU 5.0 %", switched); Assert.Contains("GPU 40.0 °C", switched);
        Assert.Equal(2, Descendants(content).OfType<TextBlock>().Count(block => block.Text.StartsWith("GPU ") && block.ToolTip?.ToString() == "First GPU"));
        for (int i = 0; i < comparisonCharts.Length; i++)
            Assert.NotEqual(selectedTraces[i], Drawings(VisualTreeHelper.GetDrawing(comparisonCharts[i])!).Where(item => item.Geometry is StreamGeometry).Last().Geometry.ToString());
        var thermalDrawing = VisualTreeHelper.GetDrawing(comparisonCharts[1])!;
        var thermalGrid = Drawings(thermalDrawing).Select(item => item.Geometry).OfType<LineGeometry>().Where(line => line.StartPoint.Y == line.EndPoint.Y).OrderBy(line => line.StartPoint.Y).ToArray();
        double thermalTop = thermalGrid[0].StartPoint.Y, thermalHeight = thermalGrid[^1].StartPoint.Y - thermalTop;
        Assert.Equal(thermalTop + thermalHeight * (1 - 125.0 / 140), Drawings(thermalDrawing).First(item => item.Geometry is StreamGeometry).Geometry.Bounds.Top, 3);
        session.Settings.HistorySeconds = 3600; Changed(); content.UpdateLayout();
        Assert.All(Descendants(content).OfType<FrameworkElement>().Where(element => element.GetType().Name == "HistoryChart"), chart =>
            Assert.All(Drawings(VisualTreeHelper.GetDrawing(chart)!).Where(item => item.Geometry is StreamGeometry), item => Assert.Equal((string)chart.GetType().GetField("kind", All)!.GetValue(chart)! is "cpu" or "cputemp" ? 1 : 2, item.Pen.Thickness)));
        session.Settings.HistorySeconds = 60;
        Set(collector, "latest", reading with { Sample = reading.Sample with { MonotonicSeconds = -10 } }); Changed();
        string stale = string.Join("\n", Descendants(content).OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("Readings stale", stale); Assert.Contains("CPU — %", stale); Assert.Contains("GPU — %", stale); Assert.Contains("CPU — °C", stale); Assert.Contains("GPU — °C", stale);
        Assert.Contains("↓ —", stale); Assert.Contains("↑ —", stale); Assert.DoesNotContain("34.6 MB/s", stale);
        var ramGraph = Descendants(content).OfType<FrameworkElement>().Single(element => element.GetType().Name == "WidgetGraph" && (string)element.GetType().GetField("kind", All)!.GetValue(element)! == "memory");
        var ramValue = (TextBlock)ramGraph.GetType().GetField("primary", All)!.GetValue(ramGraph)!;
        Assert.Equal("— %", ramValue.Text);
        Assert.DoesNotContain("GPU 5.0 %", stale);
        Set(collector, "latest", reading with { Sample = reading.Sample with { Values = new Dictionary<string, double?>() } }); Changed();
        Assert.Contains("GPU — °C", string.Join("\n", Descendants(content).OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal("— %", ramValue.Text);
        Set(collector, "latest", reading with { Sample = reading.Sample with { Values = new Dictionary<string, double?> { ["memory/usage"] = 0, ["memory/used"] = 0, ["memory/total"] = 32e9 } } }); Changed();
        Assert.StartsWith("0.0 %", ramValue.Text); Assert.Contains("0.0 B / 32.0 GB", ramValue.Text);
        VerifyWidgetNetwork(session, collector, reading, content, viewport, history);
        Set(collector, "<History>k__BackingField", history); Set(collector, "latest", reading); Changed();
        VerifyWidgetSelection(session, collector, widget, content, viewport, assembly, widgetChoices, widgetPin, settingsPath);
        var subscribed = (Action)typeof(UiSession).GetField("Changed", All)!.GetValue(session)!;
        viewport.Child = null; widget.Content = content;
        widget.Close();
        var remaining = (Action?)typeof(UiSession).GetField("Changed", All)!.GetValue(session);
        Assert.DoesNotContain(remaining?.GetInvocationList() ?? [], handler => handler.Target == widget);
        Assert.Contains(subscribed.GetInvocationList(), handler => handler.Target == widget);
        var originalSettings = session.Settings;
        var savedWidget = new WindowPlacement(100, 200, 340, 390, false);
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(SettingsCodec.Write(originalSettings))!.AsObject();
        legacyJson.Remove(nameof(UserSettings.WidgetAlwaysOnTop));
        legacyJson["Windows"]!["widget"] = System.Text.Json.JsonSerializer.SerializeToNode(savedWidget);
        var migrated = SettingsCodec.Read(legacyJson.ToJsonString()).Settings;
        Assert.False(migrated.WidgetAlwaysOnTop);
        Set(session, "<Settings>k__BackingField", migrated);
        var restored = (Window)Activator.CreateInstance(widgetType, All, null, [session], null)!;
        Assert.False(restored.Topmost); Assert.Equal(WindowStartupLocation.Manual, restored.WindowStartupLocation);
        Assert.Equal(savedWidget, session.Settings.Windows["widget"]); Assert.Equal(520, restored.MinHeight);
        restored.Close();
        migrated.WidgetAlwaysOnTop = true;
        var preferred = (Window)Activator.CreateInstance(widgetType, All, null, [session], null)!;
        Assert.True(preferred.Topmost); Assert.Equal(savedWidget, session.Settings.Windows["widget"]);
        preferred.Close();
        var otherPlacement = new WindowPlacement(20, 30, 420, 230, false);
        migrated.Windows["cpu"] = otherPlacement;
        var other = new Window(); session.Place(other, "cpu", 420, 230);
        Assert.False(other.Topmost); other.Close();
        session.SetWidgetAlwaysOnTop(false);
        Assert.Equal(savedWidget, session.Settings.Windows["widget"]);
        Assert.Equal(otherPlacement, session.Settings.Windows["cpu"]);
        CompleteSettingsSave(session); File.Delete(settingsPath);
        Set(session, "<Settings>k__BackingField", originalSettings);
    }

    private static void VerifyJoinedPopouts(UiSession session, Collector collector, Reading reading, Assembly assembly, SampleHistory history)
    {
        var windowType = assembly.GetType("DeadWare.App.GraphWindow")!;
        var graphType = assembly.GetType("DeadWare.App.CompactGraph")!;
        void Latest(Reading? value) => typeof(Collector).GetField("latest", All)!.SetValue(collector, value);
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string kind in new[] { "usage", "temperature" })
        {
            var window = (Window)Activator.CreateInstance(windowType, All, null, [session, kind], null)!;
            Assert.Equal(kind == "usage" ? "DeadWare · CPU / GPU usage" : "DeadWare · CPU / GPU temperature", window.Title);
            var graph = (FrameworkElement)window.Content;
            var chart = (FrameworkElement)graphType.GetField("chart", All)!.GetValue(graph)!;
            var cpu = (TextBlock)graphType.GetField("value", All)!.GetValue(graph)!;
            var gpu = (TextBlock)graphType.GetField("upload", All)!.GetValue(graph)!;
            var status = (TextBlock)graphType.GetField("status", All)!.GetValue(graph)!;
            var stats = (TextBlock)graphType.GetField("stats", All)!.GetValue(graph)!;
            Assert.True((bool)chart.GetType().GetProperty("CompareDevices")!.GetValue(chart)!);
            Assert.Equal(kind == "usage" ? "cpu" : "cputemp", chart.GetType().GetField("kind", All)!.GetValue(chart));
            window.Content = null;
            var viewport = new Border { Child = graph }; window.Content = viewport;
            void Refresh() { graphType.GetMethod("Refresh", All)!.Invoke(graph, null); viewport.UpdateLayout(); }
            GeometryDrawing[] Traces() => Drawings(VisualTreeHelper.GetDrawing(chart)!).Where(item => item.Geometry is StreamGeometry).ToArray();
            foreach (string theme in new[] { "Midnight", "Daylight" })
            {
                assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]); Refresh();
                foreach (var size in new[] { new Size(284, 141), new Size(404, 191) })
                {
                    viewport.SetResourceReference(Border.BackgroundProperty, "Background");
                    viewport.Measure(size); viewport.Arrange(new Rect(new Point(), size)); viewport.UpdateLayout();
                    Assert.True(chart.ActualHeight >= 48, $"Joined {kind} clipped at {size}: {chart.ActualHeight}");
                    Assert.True(cpu.ActualWidth >= cpu.DesiredSize.Width - .01); Assert.True(gpu.ActualWidth >= gpu.DesiredSize.Width - .01);
                    var traces = Traces(); Assert.Equal(2, traces.Length);
                    Assert.Equal(((SolidColorBrush)Application.Current.Resources["Cpu"]).Color, ((SolidColorBrush)traces[0].Pen.Brush).Color);
                    Assert.Equal(((SolidColorBrush)Application.Current.Resources["Gpu"]).Color, ((SolidColorBrush)traces[1].Pen.Brush).Color);
                    Assert.Empty(traces[0].Pen.DashStyle.Dashes); Assert.NotEmpty(traces[1].Pen.DashStyle.Dashes);
                    Assert.Equal(((SolidColorBrush)traces[0].Pen.Brush).Color, ((SolidColorBrush)cpu.Foreground).Color);
                    Assert.Equal(((SolidColorBrush)traces[1].Pen.Brush).Color, ((SolidColorBrush)gpu.Foreground).Color);
                    Assert.Equal(4, PathGeometry.CreateFromGeometry(traces[0].Geometry).Figures.Count);
                    Assert.Equal(kind == "usage" ? 3 : 4, PathGeometry.CreateFromGeometry(traces[1].Geometry).Figures.Count);
                    var grid = Drawings(VisualTreeHelper.GetDrawing(chart)!).Select(item => item.Geometry).OfType<LineGeometry>().Where(line => line.StartPoint.Y == line.EndPoint.Y).OrderBy(line => line.StartPoint.Y).ToArray();
                    double top = grid[0].StartPoint.Y, height = grid[^1].StartPoint.Y - top;
                    for (int device = 0; device < 2; device++)
                    {
                        string metric = (device == 0 ? "cpu/" : "gpu-b/") + (kind == "usage" ? "usage" : "temperature");
                        double peak = history.Read().Select(sample => sample.Values[metric]).Max()!.Value;
                        Assert.Equal(top + height * (1 - peak / (kind == "usage" ? 100 : 160)), traces[device].Geometry.Bounds.Top, 3);
                    }
                    Assert.Equal(kind == "usage" ? "CPU 35.0 %" : "CPU 60.0 °C", cpu.Text);
                    Assert.Equal(kind == "usage" ? "GPU 75.0 %" : "GPU 90.0 °C", gpu.Text);
                    Assert.Equal("Selected GPU", gpu.ToolTip);
                    Assert.Contains("Selected GPU", System.Windows.Automation.AutomationProperties.GetName(gpu));
                    if (output is not null)
                    {
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport);
                        using var file = File.Create(Path.Combine(output, $"joined-{kind}-{theme}-{size.Width:0}x{size.Height:0}.png"));
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                    }
                }
            }
            graphType.GetMethod("RefreshStatistics", All)!.Invoke(graph, null);
            Assert.Contains("CPU · CPU", stats.Text); Assert.Contains("GPU · Selected GPU", stats.Text);
            Assert.Equal(2, stats.Text.Split("Min  ").Length - 1);
            string cpuTrace = Traces()[0].Geometry.ToString(), gpuTrace = Traces()[1].Geometry.ToString();
            session.Settings.SelectedGpuId = "gpu-a"; Refresh();
            Assert.Equal(kind == "usage" ? "GPU 5.0 %" : "GPU 40.0 °C", gpu.Text);
            Assert.Equal("First GPU", gpu.ToolTip); Assert.NotEqual(gpuTrace, Traces()[1].Geometry.ToString());
            if (kind == "usage") Assert.Equal(cpuTrace, Traces()[0].Geometry.ToString());
            session.Settings.SelectedGpuId = "gpu-b";
            string cpuMetric = kind == "usage" ? "cpu/usage" : "cpu/temperature";
            var missing = new Dictionary<string, double?>(reading.Sample.Values) { [cpuMetric] = null };
            Latest(reading with { Sample = reading.Sample with { Values = missing } }); Refresh();
            Assert.Contains("—", cpu.Text); Assert.DoesNotContain("—", gpu.Text); Assert.Equal(Visibility.Visible, status.Visibility);
            Latest(reading with { Sample = reading.Sample with { MonotonicSeconds = -10 } }); Refresh();
            Assert.Contains("—", cpu.Text); Assert.Contains("—", gpu.Text); Assert.Contains("Stale", status.Text);
            Latest(null); Refresh(); Assert.Contains("Discovering", status.Text);
            Latest(reading); Refresh(); Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Equal(IntPtr.Zero, new System.Windows.Interop.WindowInteropHelper(window).Handle);
            window.Close();
        }
    }

    private static void VerifyWidgetNetwork(UiSession session, Collector collector, Reading reading, FrameworkElement content, Border viewport, SampleHistory history)
    {
        void Set(object target, string name, object value) => target.GetType().GetField(name, All)!.SetValue(target, value);
        void Changed() { ((Action)typeof(UiSession).GetField("Changed", All)!.GetValue(session)!).Invoke(); content.UpdateLayout(); }
        var chart = Descendants(content).OfType<FrameworkElement>().Single(element => element.GetType().Name == "HistoryChart" && (string)element.GetType().GetField("kind", All)!.GetValue(element)! == "network");
        var uploadOnly = new SampleHistory();
        foreach (var sample in history.Read()) uploadOnly.Add(sample with { Values = sample.Values.Where(pair => pair.Key != "network/download").ToDictionary() });
        Set(collector, "<History>k__BackingField", uploadOnly);
        Set(collector, "latest", reading with { Sample = reading.Sample with { Values = reading.Sample.Values.Where(pair => pair.Key != "network/download").ToDictionary() } }); Changed();
        var series = Drawings(VisualTreeHelper.GetDrawing(chart)!).Where(item => item.Geometry is StreamGeometry).ToArray();
        Assert.True(series[0].Geometry.IsEmpty()); Assert.False(series[1].Geometry.IsEmpty());
        string text = string.Join("\n", Descendants(content).OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("↓ —", text); Assert.Contains("↑ 34.6 MB/s", text);
        var highThroughput = new SampleHistory();
        foreach (var sample in history.Read()) highThroughput.Add(sample with { Values = sample.Values.ToDictionary(pair => pair.Key, pair => pair.Key.StartsWith("network/") ? pair.Value * 10000 : pair.Value) });
        Set(collector, "<History>k__BackingField", highThroughput);
        Set(collector, "latest", reading with { Sample = highThroughput.Read()[^1] }); Changed();
        viewport.Measure(new Size(284, 481)); viewport.Arrange(new Rect(0, 0, 284, 481)); viewport.UpdateLayout();
        Assert.True(VisualTreeHelper.GetDrawing(chart)!.Bounds.Right <= chart.ActualWidth + .01, "High-throughput axis label clipped at minimum width.");
        foreach (var label in Descendants(content).OfType<TextBlock>().Where(block => block.ToolTip?.ToString() is "Download rate" or "Upload rate"))
            Assert.True(label.TransformToAncestor(content).TransformBounds(new Rect(label.RenderSize)).Right <= content.ActualWidth + .01);
        Set(collector, "<History>k__BackingField", new SampleHistory());
        Set(collector, "latest", reading with { Sample = reading.Sample with { Values = new Dictionary<string, double?>() } }); Changed();
        Assert.All(Drawings(VisualTreeHelper.GetDrawing(chart)!).Where(item => item.Geometry is StreamGeometry), item => Assert.True(item.Geometry.IsEmpty()));
    }

    private static void VerifyWidgetSelection(UiSession session, Collector collector, Window widget, FrameworkElement content,
        Border viewport, Assembly assembly, CheckBox[] choices, CheckBox pin, string settingsPath)
    {
        string[] kinds = ["cpu", "cputemp", "memory", "network"];
        double[] minima = [180, 200, 280, 400, 520];
        var graphs = Descendants(content).OfType<FrameworkElement>().Where(element => element.GetType().Name == "WidgetGraph").ToArray();
        var empty = Descendants(content).OfType<TextBlock>().Single(block => block.Text.StartsWith("No graphs selected"));
        var status = Descendants(content).OfType<TextBlock>().Single(block => block.Text.StartsWith("Shared collector"));
        Assert.Empty(Descendants(content).OfType<CheckBox>());
        double width = widget.Width, height = widget.Height;
        bool topmost = widget.Topmost;
        var windowState = widget.WindowState;
        int samples = collector.History.Read().Count;
        pin.IsChecked = false;
        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        foreach (string theme in new[] { "Midnight", "Daylight" })
        {
            assembly.GetType("DeadWare.App.Theme")!.GetMethod("Apply", All)!.Invoke(null, [theme]);
            for (int mask = 0; mask < 16; mask++)
            {
                foreach (var choice in choices) choice.IsChecked = (mask & (1 << Array.IndexOf(kinds, (string)choice.Tag))) != 0;
                var selected = kinds.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
                Assert.Equal(selected, session.Settings.WidgetGraphs);
                Assert.Equal(selected, graphs.Where(graph => graph.Visibility == Visibility.Visible)
                    .Select(graph => (string)graph.GetType().GetField("kind", All)!.GetValue(graph)!));
                Assert.Equal(minima[selected.Length], widget.MinHeight);
                Assert.Equal(width, widget.Width); Assert.Equal(height, widget.Height);
                Assert.False(widget.Topmost); Assert.Equal(windowState, widget.WindowState);
                Assert.Equal(samples, collector.History.Read().Count);
                Assert.Equal(selected.Length == 0 ? Visibility.Visible : Visibility.Collapsed, empty.Visibility);
                foreach (var graph in graphs)
                {
                    var row = ((Grid)graph.Parent).RowDefinitions[Grid.GetRow(graph)];
                    if (graph.Visibility == Visibility.Visible) Assert.True(row.Height.IsStar);
                    else Assert.Equal(0, row.Height.Value);
                }
                var size = new Size(284, widget.MinHeight - 39);
                viewport.SetResourceReference(Border.BackgroundProperty, "Background");
                viewport.Measure(size); viewport.Arrange(new Rect(new Point(), size)); viewport.UpdateLayout();
                foreach (var chart in graphs.Where(graph => graph.Visibility == Visibility.Visible).SelectMany(Descendants)
                    .OfType<FrameworkElement>().Where(element => element.GetType().Name == "HistoryChart"))
                {
                    Assert.True(chart.ActualHeight >= 48, $"Selected chart clipped for mask {mask}: {chart.ActualHeight}");
                    Assert.NotNull(VisualTreeHelper.GetDrawing(chart));
                }
                var bounds = status.TransformToAncestor(content).TransformBounds(new Rect(status.RenderSize));
                Assert.True(bounds.Bottom <= content.ActualHeight + .01); Assert.True(bounds.Right <= content.ActualWidth + .01);
                if (selected.Length == 0)
                {
                    var hint = empty.TransformToAncestor(content).TransformBounds(new Rect(empty.RenderSize));
                    Assert.True(hint.Bottom <= content.ActualHeight + .01); Assert.True(hint.Right <= content.ActualWidth + .01);
                }
                if (output is not null)
                {
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport);
                    using var file = File.Create(Path.Combine(output, $"widget-selection-{theme}-{mask:X}.png"));
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
                }
            }
        }
        foreach (var choice in choices) choice.IsChecked = (string)choice.Tag is "memory" or "network";
        CompleteSettingsSave(session);
        var savedSettings = SettingsCodec.Read(File.ReadAllText(settingsPath)).Settings;
        Assert.Equal(new[] { "memory", "network" }, savedSettings.WidgetGraphs);
        Assert.False(savedSettings.WidgetAlwaysOnTop);
        bool hadPlacement = session.Settings.Windows.Remove("widget", out var savedPlacement);
        var fresh = (Window)Activator.CreateInstance(assembly.GetType("DeadWare.App.WidgetWindow")!, All, null, [session], null)!;
        Assert.Equal(280, fresh.MinHeight); Assert.Equal(360, fresh.Height); Assert.Equal(340, fresh.Width);
        Assert.False(fresh.Topmost);
        Assert.Equal(2, Descendants((FrameworkElement)fresh.Content).OfType<FrameworkElement>().Count(element => element.GetType().Name == "WidgetGraph" && element.Visibility == Visibility.Visible));
        fresh.Close();
        if (hadPlacement) session.Settings.Windows["widget"] = savedPlacement!;
        foreach (var choice in choices) choice.IsChecked = true;
        pin.IsChecked = topmost;
        CompleteSettingsSave(session); File.Delete(settingsPath);
    }

    private static void CompleteSettingsSave(UiSession session)
    {
        var save = session.SaveAsync();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = save.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame); save.GetAwaiter().GetResult();
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
