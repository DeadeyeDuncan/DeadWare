using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

internal static class ConnectionsPageUiChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type PageType = typeof(Collector).Assembly.GetType("DeadWare.App.ConnectionsPage")!;
    public static void Run(Application app)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { RunCore(app); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Application app)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        typeof(UiSession).GetField("<Settings>k__BackingField", All)!.SetValue(session, new UserSettings());
        var page = (FrameworkElement)Activator.CreateInstance(PageType, All, null, [session], null)!;
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var peer = new ConnectionPeer(new(7, 42, "Browser"), "", 0, "8.8.8.8", 443, ConnectionProtocol.Tcp);
        var live = new ConnectionLiveView(now, [new(new(peer, now, now, 1024, 512, 128, 64), new("US", "United States", null, "Fixture", new(40,-90), "Fixture")),
            new(new(peer with { RemoteAddress="192.168.1.1" }, now, now, 100, 200, 0, 0), null),
            new(new(peer with { RemoteAddress="9.9.9.9" }, now, now, 300, 400, 0, 0), null)], null, true, false, "Incomplete capture");
        Call(page, "ApplyLive", live);
        Layout(page, 671, 440);
        string text = Text(page);
        Assert.Contains("Live", text);
        Assert.Contains("3 destinations", text);
        Assert.Contains("1 local", text);
        Assert.Contains("1 unlocated", text);
        Assert.Contains("Incomplete capture", text);
        var table = Assert.Single(Elements(page).OfType<DataGrid>());
        Assert.Equal(3, table.Items.Count);
        Assert.Contains(table.Items.Cast<object>(), row => row.ToString()!.Contains("192.168.1.1"));
        var query = new ConnectionHistoryQuery(now.AddMinutes(-5), now);
        var history = new ConnectionHistoryView(query, [], 401, new(1234567, 7654321, 3, 210, 15, 9), [], [], [new(query.From, query.To)], now.AddDays(-4), 4096, "Storage failure fixture");
        Call(page, "ApplyHistory", history); Layout(page, 671, 440); text = Text(page);
        Assert.Contains("History", text); Assert.Contains("interval-average", text);
        Assert.Contains("210 destinations", text); Assert.Contains("15 unlocated", text);
        Assert.Contains("Storage failure fixture", text); Assert.Contains("401", text);
        Assert.Contains("gap", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(table.Items);
        Assert.Contains(Elements(page).OfType<Button>(), b => b.Content?.ToString() == "Next");
        Assert.Contains(Elements(page).OfType<TextBox>(), b => AutomationProperties.GetName(b) == "Search application or IP");
        Assert.Equal("Home city", AutomationProperties.GetName((ComboBox)PageType.GetField("home", All)!.GetValue(page)!));
        Assert.Contains(Elements(page).OfType<Button>(), b => b.Content?.ToString() == "Download / update locations");
        Assert.True(Assert.Single(Elements(page).OfType<ConnectionMap>()).ActualHeight >= 100);
        CheckLiveControls(page, live);
        CheckWholePageLayout(page);
        CheckHistoryQueries(session, now);
        CheckHistoryGroupingIdentity(session,now);
        CheckSavedGeography(session, now);
        CheckBoundaryDates();
        CheckExportBusy(session,now);
        CheckDownloadControls(now);
        CheckThemes(app, page, live, history);
    }
    private static T Field<T>(object page, string name) => (T)PageType.GetField(name, All)!.GetValue(page)!;
    private static void Click(object page, string name) => Field<Button>(page, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CheckLiveControls(FrameworkElement page, ConnectionLiveView live)
    {
        var mode = Field<ComboBox>(page, "mode"); mode.SelectedIndex = 0; Call(page, "ApplyLive", live);
        var grid = Field<DataGrid>(page, "table"); grid.SelectedItem = grid.Items[0];
        Assert.Contains("Browser / 7", Field<TextBlock>(page, "selection").Text);
        Call(page, "ApplyLive", live);
        Assert.Contains("Browser / 7", Field<TextBlock>(page, "selection").Text);
        var search = Field<TextBox>(page, "search"); search.Text = "192.168"; Assert.Single(grid.Items.Cast<object>()); Assert.Contains("1 local", Field<TextBlock>(page, "summary").Text); search.Text = "";
        var country = Field<TextBox>(page, "country"); country.Text = "US"; Assert.Single(grid.Items.Cast<object>()); country.Text = "";
        Assert.False(country.IsReadOnly); Assert.True(country.Focusable);
        Call(page, "ApplyLive", live with { Available = false, Status = "Permission denied" }); Assert.Contains("Permission denied", Field<TextBlock>(page, "capture").Text); Assert.Contains("—", grid.Items[0].ToString());
        var options = Elements(page).OfType<Expander>().Single(); options.IsExpanded = true; Layout(page, 671, 600);
        var city = Field<TextBox>(page, "citySearch"); city.Text = "London"; var home = Field<ComboBox>(page, "home"); Assert.Contains(home.Items.Cast<object>(), item => item.ToString()!.Contains("London")); Assert.True(home.Items.Count <= 82); city.Text = ""; Assert.Single(home.Items.Cast<object>()); options.IsExpanded = false;
        Click(page, "download"); Assert.Contains("unavailable", Field<TextBlock>(page, "actionStatus").Text);
        Call(page, "RefreshSession", null!); Assert.Contains("unavailable", Field<TextBlock>(page, "actionStatus").Text);
        var map = Field<ConnectionMap>(page, "map"); var zoom = Elements(page).OfType<Button>().Single(b => b.Content?.ToString() == "+"); zoom.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(map.Camera.Zoom > 1);
        Elements(page).OfType<Button>().Single(b => b.Content?.ToString() == "Reset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, map.Camera.Zoom);
        Call(page, "ApplyLive", live with { Connections = Enumerable.Range(0, 401).Select(i => live.Connections[0] with { Traffic = live.Connections[0].Traffic with { Peer = live.Connections[0].Traffic.Peer with { RemotePort = i } } }).ToArray() });
        Assert.Equal(200, grid.Items.Count); Click(page, "next"); Assert.Equal(200, grid.Items.Count); Click(page, "next"); Assert.Single(grid.Items.Cast<object>()); Assert.False(Field<Button>(page, "next").IsEnabled); Click(page, "previous"); Assert.Equal(200, grid.Items.Count);
    }
    private static void CheckHistoryQueries(UiSession session, DateTimeOffset now)
    {
        var page = (FrameworkElement)Activator.CreateInstance(PageType, All, null, [session], null)!;
        var replies = new Queue<TaskCompletionSource<ConnectionHistoryView>>(); var requested = new List<ConnectionHistoryQuery>();
        Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>> read = q => { requested.Add(q); var reply = new TaskCompletionSource<ConnectionHistoryView>(); replies.Enqueue(reply); return reply.Task; };
        PageType.GetField("readHistory", All)!.SetValue(page, read);
        var mode = Field<ComboBox>(page, "mode"); mode.SelectedIndex = 1; Assert.Single(requested);
        var first = requested[0]; Assert.Equal(DateTime.Today, new DateTimeOffset(first.From.LocalDateTime).Date); Assert.Equal(DateTime.Today.AddDays(1), first.To.LocalDateTime);
        Field<TextBox>(page, "search").Text = "old"; Field<TextBox>(page, "search").Text = "new"; Assert.Single(requested);
        ConnectionHistoryView View(ConnectionHistoryQuery q, string status) => new(q, [], 0, new(0, 0, 0, 0, 0, 0), [], [], [], now.AddDays(-3), 1234, status);
        replies.Dequeue().SetResult(View(first, "STALE REPLY")); Pump(); Assert.Equal(2, requested.Count); Assert.Equal("new", requested[^1].Search); Assert.DoesNotContain("STALE REPLY", Field<TextBlock>(page, "capture").Text);
        replies.Dequeue().SetResult(View(requested[^1], "LATEST REPLY")); Pump(); Assert.Contains("LATEST REPLY", Field<TextBlock>(page, "capture").Text);
        var service = new ConnectionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"));
        typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, service);
        string saved = Field<TextBlock>(page, "storage").Text; PageType.GetMethod("RefreshSession", All)!.Invoke(page, null); Assert.Equal(saved.TrimEnd() + " " + service.StorageStatus, Field<TextBlock>(page, "storage").Text);
        typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, null);
        Click(page, "next"); Assert.Equal(2, requested.Count);
        Field<TextBox>(page, "from").Text = "bad date"; PageType.GetMethod("RequestHistory", All)!.Invoke(page, null); Assert.Equal(2, requested.Count); Assert.Contains("Choose valid local times", Field<TextBlock>(page, "actionStatus").Text);
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00"; Field<TextBox>(page, "to").Text = "2026-10-29 10:03"; Click(page, "play"); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].To - requested[^1].From);
        int before = requested.Count; PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Minute zero")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 1, requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].From - requested[^2].From);
        replies.Dequeue().SetResult(View(requested[^1], "Minute one")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 2, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Minute two")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 2, requested.Count); Assert.Equal("Play", Field<Button>(page, "play").Content);
        Field<TextBox>(page, "search").Text = "pending"; mode.SelectedIndex = 0; replies.Dequeue().SetResult(View(requested[^1], "HISTORY LEAK")); Pump(); Assert.DoesNotContain("HISTORY LEAK", Field<TextBlock>(page, "capture").Text); Assert.False(Field<DispatcherTimer>(page, "playback").IsEnabled);
    }
    private static void CheckWholePageLayout(FrameworkElement page)
    {
        Layout(page,671,440);Pump();Layout(page,671,440);
        var map=Field<ConnectionMap>(page,"map");var table=Field<DataGrid>(page,"table");
        Assert.True(map.ActualHeight>=260,$"Main map height was {map.ActualHeight:0.0}.");
        Assert.InRange(table.ActualHeight,164,440);
        Assert.InRange(Elements(table).OfType<DataGridRow>().Count(),3,20);
        var scroll=Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);
        Assert.True(scroll.ExtentHeight>scroll.ViewportHeight);Assert.True(double.IsFinite(scroll.ExtentHeight));
        scroll.ScrollToVerticalOffset(map.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y);Pump();Layout(page,671,440);
        var mapPoint=map.TransformToAncestor(page).Transform(new Point());Assert.InRange(mapPoint.Y,0,440-map.ActualHeight);
        scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var download=Field<Button>(page,"download");var point=download.TransformToAncestor(page).Transform(new Point());
        Assert.InRange(point.Y,0,440-download.ActualHeight);
        point=table.TransformToAncestor(page).Transform(new Point());Assert.InRange(point.Y,0,440-table.ActualHeight);
        var expander=Elements(page).OfType<Expander>().Single();expander.IsExpanded=true;Layout(page,671,440);scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var city=Field<ComboBox>(page,"home");point=city.TransformToAncestor(page).Transform(new Point());Assert.InRange(point.Y,0,440-city.ActualHeight);
        expander.IsExpanded=false;scroll.ScrollToTop();Pump();Layout(page,671,440);
    }
    private static void CheckDownloadControls(DateTimeOffset now)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        typeof(UiSession).GetField("<Settings>k__BackingField", All)!.SetValue(session, new UserSettings());
        var service = new ConnectionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")); typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, service);
        string settingsPath=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+"-connections-settings.json");
        typeof(UiSession).GetField("writer",All)!.SetValue(session,new SettingsSnapshotWriter(settingsPath));
        typeof(UiSession).GetField("layout",All)!.SetValue(session,new DetachedWindowState([]));
        using var http = new HttpClient(new BlockingDownload());
        typeof(ConnectionService).GetField("downloader", All)!.SetValue(service, new ConnectionDatabaseDownload(http));
        var page = new ConnectionsPage(session); Click(page, "download");
        Assert.False(Field<Button>(page, "download").IsEnabled); Assert.True(Field<Button>(page, "cancel").IsEnabled); Assert.Equal(Visibility.Visible, Field<ProgressBar>(page, "progress").Visibility);
        Click(page, "cancel"); PumpUntil(() => Field<Button>(page, "download").IsEnabled); Assert.False(Field<Button>(page, "cancel").IsEnabled); Assert.Contains("cancelled", Field<TextBlock>(page, "actionStatus").Text);
        Assert.Contains("download.db-ip.com", Field<TextBlock>(page, "database").Text); Assert.Contains("CC BY 4.0", Field<TextBlock>(page, "database").Text);
        using var denied = new HttpClient(new DeniedDownload()); typeof(ConnectionService).GetField("downloader", All)!.SetValue(service, new ConnectionDatabaseDownload(denied)); Click(page, "download"); PumpUntil(() => Field<Button>(page, "download").IsEnabled); Assert.Contains("update failed", Field<TextBlock>(page, "actionStatus").Text);
        Field<CheckBox>(page,"recording").IsChecked=false;Assert.False(session.Settings.ConnectionsRecording);Assert.Contains("paused",service.StorageStatus);
        Field<CheckBox>(page,"animation").IsChecked=false;Assert.False(session.Settings.ConnectionsAnimation);Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled);
        Field<TextBox>(page,"citySearch").Text="London";var home=Field<ComboBox>(page,"home");home.SelectedIndex=1;Assert.NotNull(session.Settings.ConnectionsHomeCityId);home.SelectedIndex=0;Assert.Null(session.Settings.ConnectionsHomeCityId);
        var save=session.SaveAsync();PumpUntil(()=>save.IsCompleted);save.GetAwaiter().GetResult();File.Delete(settingsPath);
    }
    private sealed class BlockingDownload : HttpMessageHandler
    { protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); } }
    private sealed class DeniedDownload : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
    private static void CheckSavedGeography(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); Field<ComboBox>(page, "mode").SelectedIndex = 1;
        var q = new ConnectionHistoryQuery(now.AddMinutes(-2), now);
        var location = new ConnectionLocation("JP", "Japan", null, "Tokyo", new(35.7, 139.7), "Saved");
        var a = new ConnectionHistoryRow(q.From, new(1, 1, "Saved A"), "8.8.8.8", 443, ConnectionProtocol.Tcp, q.From, q.To, 100, 50, 1, 2, location, new(41, -87), false);
        var b = a with { Process = new(2, 2, "Saved B"), Origin = new(-33, 151) };
        var unlocated = a with { Process = new(3, 3, "Local"), RemoteAddress = "192.168.1.1", Location = null, Origin = null };
        var history = new ConnectionHistoryView(q, [a, b, unlocated], 3, new(300, 150, 1, 2, 0, 1), [new(location, a.Origin, 100, 50), new(location, b.Origin, 100, 50)], [], [], q.From, 1024, "Saved fixture");
        page.ApplyHistory(history); Layout(page, 671, 500); var map = Field<ConnectionMap>(page, "map"); map.ResetView();
        Assert.Equal(-20, map.Camera.Longitude); // Multiple saved origins cannot become one invented route.
        var table = Field<DataGrid>(page, "table"); table.SelectedItem = table.Items[1]; map.ResetView(); Assert.Equal(151, map.Camera.Longitude); Assert.Contains("Incomplete", Field<TextBlock>(page, "selection").Text);
        Assert.Contains("First", Field<TextBlock>(page, "selection").Text);
        page.ApplyHistory(history);Assert.Equal(table.Items[1],table.SelectedItem);map.ResetView();Assert.Equal(151,map.Camera.Longitude);Assert.Equal(1,map.DisplayedPointCount);
        table.SelectedItem = table.Items[2]; Assert.Equal(0, map.DisplayedPointCount);
        page.ApplyHistory(history);Assert.Equal(table.Items[2],table.SelectedItem);Assert.Equal(0,map.DisplayedPointCount);
        table.SelectedItem=null;
        page.ApplyHistory(history);
        var points = (IReadOnlyList<ConnectionMapPoint>)typeof(ConnectionMap).GetField("points", All)!.GetValue(map)!;
        var select = (Action<string>)typeof(ConnectionMap).GetField("PointSelected", All)!.GetValue(map)!; select(points[0].Key);
        Assert.NotNull(table.SelectedItem);
        var replacement = history with { Rows = [a with { Process = new(99, 99, "Replacement") }], TotalRows = 1 }; page.ApplyHistory(replacement); Assert.Null(table.SelectedItem);
    }
    private static void CheckHistoryGroupingIdentity(UiSession session,DateTimeOffset now)
    {
        var page=new ConnectionsPage(session);Field<ComboBox>(page,"mode").SelectedIndex=1;
        var query=new ConnectionHistoryQuery(now.AddMinutes(-1),now);
        var location=new ConnectionLocation("JP","Japan","Kanto","Tokyo",new(35.7,139.7),"Saved A");
        var first=new ConnectionHistoryRow(query.From,new(1,1,"Saved A"),"8.8.8.8",443,ConnectionProtocol.Tcp,query.From,query.To,100,50,1,2,location,new(41,-87),false);
        var revisions=new[]{first with {Location=location with {Point=new(34,135)}},first with {Location=location with {CountryCode="XX"}},first with {Location=location with {Country="Revised"}},first with {Location=location with {Region="Revised"}},first with {Location=location with {City="Revised"}},first with {Location=location with {Source="Revised"}},first with {Location=null},first with {Process=first.Process with {Name="Renamed"}}};
        var table=Field<DataGrid>(page,"table");
        foreach(var revision in revisions)
        {
            var history=new ConnectionHistoryView(query,[first,revision],2,new(200,100,1,1,0,0),[],[],[],query.From,1024,"Saved revisions");
            page.ApplyHistory(history);table.SelectedItem=table.Items[1];
            page.ApplyHistory(history);Assert.Equal(table.Items[1],table.SelectedItem);
            // Traffic aggregates and observation bounds are values, not grouping identity.
            page.ApplyHistory(history with {Rows=[first,revision with {ReceivedBytes=999,SentBytes=888,DownloadBytesPerSecond=99,UploadBytesPerSecond=88,FirstSeen=revision.FirstSeen.AddSeconds(1),LastSeen=revision.LastSeen.AddSeconds(-1),Complete=true}]});
            Assert.Equal(table.Items[1],table.SelectedItem);
        }
    }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void CheckBoundaryDates()
    {
        var parse=PageType.GetMethod("TryParseLocalTime",All)!;
        bool Parse(string text,int hours,out DateTimeOffset utc)
        {
            var zone=TimeZoneInfo.CreateCustomTimeZone("Fixture "+hours,TimeSpan.FromHours(hours),"Fixture","Fixture");
            object?[] args=[text,zone,null];bool result=(bool)parse.Invoke(null,args)!;utc=(DateTimeOffset)args[2]!;return result;
        }
        Assert.False(Parse("9999-12-31 23:59",-6,out _));
        Assert.False(Parse("0001-01-01 00:00",6,out _));
        Assert.True(Parse("9999-12-31 17:59",-6,out var maximum));Assert.Equal(new DateTimeOffset(9999,12,31,23,59,0,TimeSpan.Zero),maximum);
        Assert.True(Parse("0001-01-01 06:00",6,out var minimum));Assert.Equal(DateTimeOffset.MinValue,minimum);
    }
    private static void CheckExportBusy(UiSession session,DateTimeOffset now)
    {
        var page=new ConnectionsPage(session);Field<ComboBox>(page,"mode").SelectedIndex=1;
        var completion=new TaskCompletionSource();int calls=0;
        Func<string,ConnectionHistoryQuery,Task> export=(path,query)=>{calls++;return completion.Task;};
        PageType.GetField("exportHistory",All)!.SetValue(page,export);
        var query=new ConnectionHistoryQuery(now.AddMinutes(-1),now);
        var exportMethod=PageType.GetMethod("ExportToPathAsync",All)!;
        var exporting=(Task)exportMethod.Invoke(page,["fixture.csv",query])!;
        Assert.False(Field<Button>(page,"export").IsEnabled);
        page.ApplyHistory(new(query,[],0,new(0,0,0,0,0,0),[],[],[],null,0,"History refreshed during export"));
        Assert.False(Field<Button>(page,"export").IsEnabled);
        var duplicate=(Task)exportMethod.Invoke(page,["duplicate.csv",query])!;Assert.Equal(1,calls);Assert.True(duplicate.IsCompleted);
        Field<ComboBox>(page,"mode").SelectedIndex=0;Field<ComboBox>(page,"mode").SelectedIndex=1;Assert.False(Field<Button>(page,"export").IsEnabled);
        completion.SetResult();PumpUntil(()=>exporting.IsCompleted);exporting.GetAwaiter().GetResult();Assert.True(Field<Button>(page,"export").IsEnabled);
    }
    private static void PumpUntil(Func<bool> done)
    {
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(4); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow >= deadline) frame.Continue = false; }; timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(done(), "The asynchronous UI action did not finish.");
    }
    private static void CheckThemes(Application app, FrameworkElement page, ConnectionLiveView live, ConnectionHistoryView history)
    {
        // Constructing a Window registers the resource-invalidation root, without creating an HWND.
        var host = new Window { Content = page };
        var theme = typeof(Collector).Assembly.GetType("DeadWare.App.Theme")!;
        foreach (string name in new[] { "Midnight", "Ember", "Glacier", "Amethyst", "Neon", "Daylight" })
        {
            theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [name]); Pump();
            Assert.Equal(((SolidColorBrush)app.Resources["Ink"]).Color, ((SolidColorBrush)Field<Button>(page, "download").Foreground).Color);
            foreach (var size in new[] { new Size(671, 440), new Size(1100, 700) })
                foreach (bool historical in new[] { false, true })
                {
                    var scroll=Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);
                    Field<ComboBox>(page, "mode").SelectedIndex = historical ? 1 : 0; if (historical) Call(page, "ApplyHistory", history); else Call(page, "ApplyLive", live);scroll.ScrollToTop(); Layout(page, size.Width, size.Height);
                Pump();Layout(page,size.Width,size.Height);
                var map = Field<ConnectionMap>(page, "map"); var table = Field<DataGrid>(page, "table"); Assert.InRange(map.ActualWidth, 500, size.Width); Assert.True(map.ActualHeight >= 260); Assert.True(table.ActualHeight >= 164);Assert.True(map.ActualHeight>table.ActualHeight);
                Assert.True(Field<TextBlock>(page,"summary").TransformToAncestor(page).Transform(new Point()).Y+Field<TextBlock>(page,"summary").ActualHeight<map.TransformToAncestor(page).Transform(new Point()).Y+1);
                    void Snapshot(string suffix)
                    {
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);bitmap.Render(page);
                        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");if(output is null)return;
                        Directory.CreateDirectory(output);using var stream=File.Create(Path.Combine(output,$"connections-page-{name}-{size.Width}-{(historical?"history":"live")}{suffix}.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
                    }
                    Snapshot("");scroll.ScrollToBottom();Pump();Layout(page,size.Width,size.Height);
                    var download=Field<Button>(page,"download");var location=download.TransformToAncestor(page).Transform(new Point());Assert.InRange(location.Y,0,size.Height-download.ActualHeight);
                    Snapshot("-bottom");scroll.ScrollToTop();Pump();
                }
        }
        Field<ComboBox>(page, "mode").SelectedIndex = 0;
        theme.GetMethod("SetGraphColors",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[new Dictionary<string,string>{{"download","#123456"},{"upload","#ABCDEF"}}]);Pump();
        foreach(var color in new[]{("DownloadProperty","#123456"),("UploadProperty","#ABCDEF")})
        {var property=(DependencyProperty)typeof(ConnectionMap).GetField(color.Item1,All)!.GetValue(null)!;Assert.Equal((Color)ColorConverter.ConvertFromString(color.Item2),((SolidColorBrush)Field<ConnectionMap>(page,"map").GetValue(property)).Color);}
        theme.GetMethod("SetGraphColors",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,[new Dictionary<string,string>()]);
        theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, ["Midnight"]);
        host.Content = null; host.Close();
    }
    internal static void CheckNavigation(MainWindow dashboard)
    {
        var shell = (Grid)dashboard.Content;
        var sidebar = (DockPanel)shell.Children[0];
        var nav = (StackPanel)sidebar.Children.OfType<ScrollViewer>().Single().Content;
        Assert.Contains(nav.Children.OfType<Button>(), b => b.Content?.ToString()?.Contains("Connections") == true);
        typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, ["connections"]);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)typeof(MainWindow).GetField("liveToolbar", All)!.GetValue(dashboard)!).Visibility);
        typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, ["overview"]);
    }
    private static void Call(object target, string name, object? value) => PageType.GetMethod(name, All)!.Invoke(target, value is null ? null : [value]);
    private static void Layout(FrameworkElement page, double width, double height) { page.Measure(new(width, height)); page.Arrange(new(0, 0, width, height)); page.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Elements(root).OfType<TextBlock>().Select(t => t.Text));
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Elements(child)) yield return descendant; } }
}
