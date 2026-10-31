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
        Assert.Empty(Elements(page).OfType<DataGrid>());
        string text = Text(page);
        Assert.Contains("Live", text);
        Assert.Contains("3 destinations", text);
        Assert.Contains("1 local", text);
        Assert.Contains("1 unlocated", text);
        Assert.Contains("Incomplete capture", text);
        var map=Field<ConnectionMap>(page,"map"); map.Flat=true; Layout(page,671,440);
        Assert.Single(Field<ListBox>(page,"mappedConnections").Items.Cast<object>());
        Assert.DoesNotContain(Elements(page).OfType<Button>(),b=>b.Content?.ToString() is "Previous" or "Next");
        var query = new ConnectionHistoryQuery(now.AddMinutes(-5), now);
        var history = new ConnectionHistoryView(query, [], 401, new(1234567, 7654321, 3, 210, 15, 9), [], [], [new(query.From, query.To)], now.AddDays(-4), 4096, "Storage failure fixture");
        Field<ComboBox>(page,"mode").SelectedIndex=1;
        Call(page,"ApplyHistory",history); Layout(page,671,440); text=Text(page);
        Assert.Contains("minute-bucket average",text); Assert.Contains("Storage failure fixture",text); Assert.Contains("gap",text,StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Field<ListBox>(page,"mappedConnections").Items);
        CheckLiveControls(page,live);
        CheckVisibleNodesAndPopup(session,live);
        CheckSavedMapDetails(session,now);
        CheckWholePageLayout(page);
        CheckHistoryQueries(session,now);
        CheckBoundaryDates();
        CheckExportBusy(session,now);
        CheckDownloadControls(now);
        CheckThemes(app, page, live, history);
    }
    private static T Field<T>(object page, string name) => (T)PageType.GetField(name, All)!.GetValue(page)!;



    private static void Click(object page, string name) => Field<Button>(page, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CheckLiveControls(FrameworkElement page, ConnectionLiveView live)
    {
        Field<ComboBox>(page,"mode").SelectedIndex=0; Call(page,"ApplyLive",live); Layout(page,671,440);
        var map=Field<ConnectionMap>(page,"map"); map.Flat=true;
        var list=Field<ListBox>(page,"mappedConnections"); list.SelectedIndex=0;
        Assert.Contains("Browser / 7",Field<TextBlock>(page,"nodeText").Text);
        Call(page,"ApplyLive",live with {Connections=live.Connections.Select(c=>c with {Traffic=c.Traffic with {ReceivedBytes=4096,DownloadBytesPerSecond=256}}).ToArray()});
        Assert.Contains("4.1 KB",Field<TextBlock>(page,"nodeText").Text);
        Field<TextBox>(page,"search").Text="192.168"; Assert.Empty(list.Items); Assert.Equal(Visibility.Collapsed,Field<Border>(page,"nodeDetails").Visibility);
        Assert.Contains("1 local",Field<TextBlock>(page,"summary").Text); Field<TextBox>(page,"search").Text="";
        Field<TextBox>(page,"country").Text="JP"; Assert.Empty(list.Items); Field<TextBox>(page,"country").Text="";
        Call(page,"ApplyLive",live with {Available=false,Status="Permission denied"}); list.SelectedIndex=0;
        Assert.Contains("Permission denied",Field<TextBlock>(page,"capture").Text);Assert.Contains("—",Field<TextBlock>(page,"nodeText").Text);
        var options=Elements(page).OfType<Expander>().Single();options.IsExpanded=true;Layout(page,671,600);
        Field<TextBox>(page,"citySearch").Text="London";var home=Field<ComboBox>(page,"home");Assert.Contains(home.Items.Cast<object>(),item=>item.ToString()!.Contains("London"));Assert.True(home.Items.Count<=82);options.IsExpanded=false;
        Click(page,"download");Assert.Contains("unavailable",Field<TextBlock>(page,"actionStatus").Text);
        Call(page,"ApplyLive",live); list.SelectedIndex=0; Call(page,"ClearSelection",null);
        Assert.Equal(Visibility.Collapsed,Field<Border>(page,"nodeDetails").Visibility);Assert.Null(list.SelectedItem);
    }
    private static void SelectNode(FrameworkElement page,string key)
    {
        var map=Field<ConnectionMap>(page,"map");
        ((Action<string>)typeof(ConnectionMap).GetField("PointSelected",All)!.GetValue(map)!)(key);
    }
    private static void CheckVisibleNodesAndPopup(UiSession session,ConnectionLiveView live)
    {
        var page=new ConnectionsPage(session);var first=live.Connections[0];
        var other=first with {Traffic=first.Traffic with {Peer=first.Traffic.Peer with {RemoteAddress="1.1.1.1",Process=new(8,43,"Other")}},Location=first.Location! with {City="Far side",Point=new(-40,90)}};
        page.ApplyLive(live with {Connections=[..live.Connections,other]});Layout(page,1000,700);
        var map=Field<ConnectionMap>(page,"map");map.SetView(first.Location!.Point!,1);
        var list=Field<ListBox>(page,"mappedConnections");Assert.Single(list.Items.Cast<object>());
        string key=map.DisplayedPoints.Single(p=>p.Point==first.Location.Point).Key;SelectNode(page,key);Layout(page,1000,700);
        var box=Field<Border>(page,"nodeDetails");Assert.Equal(Visibility.Visible,box.Visibility);
        Assert.Contains("Live connection",Field<TextBlock>(page,"nodeText").Text);Assert.Contains("8.8.8.8:443 Tcp",Field<TextBlock>(page,"nodeText").Text);
        double oldX=Canvas.GetLeft(box);map.SetView(new(40,-60),1);Layout(page,1000,700);Assert.NotEqual(oldX,Canvas.GetLeft(box));
        Assert.InRange(Canvas.GetLeft(box),0,map.ActualWidth-box.ActualWidth);Assert.InRange(Canvas.GetTop(box),0,map.ActualHeight-box.ActualHeight);
        map.SetView(other.Location!.Point!,1);Assert.Single(list.Items.Cast<object>());Assert.DoesNotContain("Browser",list.Items[0].ToString());Assert.Equal(Visibility.Collapsed,box.Visibility);
        map.Flat=true;map.ResetView();Assert.Equal(2,list.Items.Count);
        page.ApplyLive(live with {Connections=[first with {Traffic=first.Traffic with {Peer=first.Traffic.Peer with {RemoteAddress="2001:4860::8888"}}}]});list.SelectedIndex=0;Assert.Contains("[2001:4860::8888]:443",Field<TextBlock>(page,"nodeText").Text);
        // Shared nodes aggregate location volume but retain a clearly labeled representative's own rates/bytes.
        var same=first with {Traffic=first.Traffic with {Peer=first.Traffic.Peer with {LocalAddress="192.168.2.1"}}};
        page.ApplyLive(live with {Connections=[first,same]});Assert.Single(list.Items.Cast<object>());list.SelectedIndex=0;
        Assert.Contains("Location total: ↓ 2.0 KB",Field<TextBlock>(page,"nodeText").Text);Assert.Contains("Received 1.0 KB",Field<TextBlock>(page,"nodeText").Text);
        // The list follows the map's cap and never falls back to the full all-peer ledger.
        page.ApplyLive(live with {Connections=Enumerable.Range(0,300).Select(i=>first with {Traffic=first.Traffic with {Peer=first.Traffic.Peer with {RemotePort=i}},Location=first.Location! with {Point=new(0,-150+i)}}).ToArray()});
        Assert.Equal(256,map.DisplayedPointCount);Assert.Equal(44,map.OverflowCount);Assert.Equal(256,list.Items.Count);
        Layout(page,1000,700);Pump();
        var listScroll=Elements(list).OfType<ScrollViewer>().First();listScroll.ScrollToVerticalOffset(100);Pump();Layout(page,1000,700);
        double scrollOffset=listScroll.VerticalOffset;var retained=list.Items[100];
        Call(page,"RefreshMappedConnections",null);Layout(page,1000,700);Pump();
        Assert.Same(retained,list.Items[100]);Assert.Equal(scrollOffset,listScroll.VerticalOffset);
        SelectNode(page,map.DisplayedPoints[^1].Key);Assert.Equal(44,map.OverflowCount);
        page.ApplyLive(live with {Connections=[]});Assert.Empty(list.Items);Assert.Equal(Visibility.Collapsed,box.Visibility);Assert.Empty(Field<TextBlock>(page,"nodeText").Text);
    }
    private static void CheckSavedMapDetails(UiSession session,DateTimeOffset now)
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-map-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new ConnectionHistoryStore(Path.Combine(root,"history.db"));store.Initialize();
            var start=now.AddMinutes(-201);var place=new ConnectionLocation("JP","Japan",null,"Saved Tokyo",new(35,139),"old source");
            var records=Enumerable.Range(0,201).Select(i=>new ConnectionHistoryRecord("b"+i,"fixture",new(start.AddMinutes(i),new(new(10+i,1,"Peer "+i),"",0,"8.8.8.8",443,ConnectionProtocol.Tcp),i==0?10000:1,0,start.AddMinutes(i),start.AddMinutes(i)),i==0?place:place with {City="Revised",Point=new(40,-90)},i==0?new(41,-87):new(51,0),true)).ToArray();store.Append(records,[],now);
            var page=new ConnectionsPage(session);Field<TextBox>(page,"from").Text=start.ToLocalTime().ToString("yyyy-MM-dd HH:mm");Field<TextBox>(page,"to").Text=now.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            int calls=0;PageType.GetField("readHistory",All)!.SetValue(page,(Func<ConnectionHistoryQuery,Task<ConnectionHistoryView>>)(q=>{calls++;return Task.FromResult(store.Read(q));}));
            Field<ComboBox>(page,"mode").SelectedIndex=1;Layout(page,1000,700);var map=Field<ConnectionMap>(page,"map");map.Flat=true;map.ResetView();
            Assert.Equal(2,Field<ListBox>(page,"mappedConnections").Items.Count);
            string key=map.DisplayedPoints.Single(p=>p.Point==place.Point).Key;SelectNode(page,key);
            Assert.Equal(1,calls);Assert.Contains("Peer 0 /",Field<TextBlock>(page,"nodeText").Text);Assert.Contains("Historical connection",Field<TextBlock>(page,"nodeText").Text);Assert.Contains("Saved Tokyo",Field<TextBlock>(page,"nodeText").Text);
            Assert.Equal(2,map.DisplayedPointCount);Assert.False(map.AnimationEnabled);
            var pending=new TaskCompletionSource<ConnectionHistoryView>();ConnectionHistoryQuery? requested=null;
            PageType.GetField("readHistory",All)!.SetValue(page,(Func<ConnectionHistoryQuery,Task<ConnectionHistoryView>>)(q=>{requested=q;return pending.Task;}));
            Field<TextBox>(page,"search").Text="Peer 200";Assert.Empty(Field<ListBox>(page,"mappedConnections").Items);Assert.Equal(Visibility.Collapsed,Field<Border>(page,"nodeDetails").Visibility);
            SelectNode(page,key);Assert.Empty(Field<TextBlock>(page,"nodeText").Text);pending.SetResult(store.Read(requested!));Pump();Assert.Single(Field<ListBox>(page,"mappedConnections").Items.Cast<object>());
            Assert.DoesNotContain("Peer 0 /",Field<TextBlock>(page,"nodeText").Text);
            Field<ListBox>(page,"mappedConnections").SelectedIndex=0;Assert.Contains("Peer 200 /",Field<TextBlock>(page,"nodeText").Text);
            Field<ComboBox>(page,"mode").SelectedIndex=0;Assert.Equal(Visibility.Collapsed,Field<Border>(page,"nodeDetails").Visibility);Assert.Empty(Field<TextBlock>(page,"nodeText").Text);
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void CheckHistoryQueries(UiSession session, DateTimeOffset now)
    {
        var page = (FrameworkElement)Activator.CreateInstance(PageType, All, null, [session], null)!;
        var replies = new Queue<TaskCompletionSource<ConnectionHistoryView>>(); var requested = new List<ConnectionHistoryQuery>();
        Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>> read = q => { requested.Add(q); var reply = new TaskCompletionSource<ConnectionHistoryView>(); replies.Enqueue(reply); return reply.Task; };
        PageType.GetField("readHistory", All)!.SetValue(page, read);
        var mode = Field<ComboBox>(page, "mode"); mode.SelectedIndex = 1; Assert.Single(requested);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled);
        var first = requested[0]; Assert.Equal(DateTime.Today, new DateTimeOffset(first.From.LocalDateTime).Date); Assert.Equal(DateTime.Today.AddDays(1), first.To.LocalDateTime);
        Field<TextBox>(page, "search").Text = "old"; Field<TextBox>(page, "search").Text = "new"; Assert.Single(requested);
        ConnectionHistoryView View(ConnectionHistoryQuery q, string status) => new(q, [], 0, new(0, 0, 0, 0, 0, 0), [], [], [], now.AddDays(-3), 1234, status);
        replies.Dequeue().SetResult(View(first, "STALE REPLY")); Pump(); Assert.Equal(2, requested.Count); Assert.Equal("new", requested[^1].Search); Assert.DoesNotContain("STALE REPLY", Field<TextBlock>(page, "capture").Text);
        replies.Dequeue().SetResult(View(requested[^1], "LATEST REPLY")); Pump(); Assert.Contains("LATEST REPLY", Field<TextBlock>(page, "capture").Text);
        var service = new ConnectionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"));
        typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, service);
        string saved = Field<TextBlock>(page, "storage").Text; PageType.GetMethod("RefreshSession", All)!.Invoke(page, null); Assert.Equal(saved.TrimEnd() + " " + service.StorageStatus, Field<TextBlock>(page, "storage").Text);
        typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, null);

        Field<TextBox>(page, "from").Text = "bad date"; PageType.GetMethod("RequestHistory", All)!.Invoke(page, null); Assert.Equal(2, requested.Count); Assert.Contains("Choose valid local times", Field<TextBlock>(page, "actionStatus").Text);
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00"; Field<TextBox>(page, "to").Text = "2026-10-29 10:03"; Click(page, "play"); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].To - requested[^1].From);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled); // The prior range must stay static while its first minute is pending.
        int before = requested.Count; PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Minute zero")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 1, requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].From - requested[^2].From);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled); // Pending next minute cannot animate the prior minute.
        replies.Dequeue().SetResult(View(requested[^1], "Minute one")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 2, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Minute two")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 2, requested.Count); Assert.Equal("Play", Field<Button>(page, "play").Content);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled);
        Field<TextBox>(page, "search").Text = "pending"; mode.SelectedIndex = 0; replies.Dequeue().SetResult(View(requested[^1], "HISTORY LEAK")); Pump(); Assert.DoesNotContain("HISTORY LEAK", Field<TextBlock>(page, "capture").Text); Assert.False(Field<DispatcherTimer>(page, "playback").IsEnabled);
    }
    private static void CheckWholePageLayout(FrameworkElement page)
    {
        Layout(page,671,440);Pump();Layout(page,671,440);
        var map=Field<ConnectionMap>(page,"map");Assert.True(map.ActualHeight>=300);
        Assert.Empty(Elements(page).OfType<DataGrid>());
        var scroll=Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);Assert.True(double.IsFinite(scroll.ExtentHeight));
        scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var download=Field<Button>(page,"download");Assert.InRange(download.TransformToAncestor(page).Transform(new Point()).Y,0,440-download.ActualHeight);
        var expander=Elements(page).OfType<Expander>().Single();expander.IsExpanded=true;Layout(page,671,440);scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var city=Field<ComboBox>(page,"home");Assert.InRange(city.TransformToAncestor(page).Transform(new Point()).Y,0,440-city.ActualHeight);
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
        var place=live.Connections[0].Location!;
        var saved=new ConnectionHistoryRow(history.Query.From,new(9,3,"Saved browser"),"8.8.8.8",443,ConnectionProtocol.Tcp,history.Query.From,history.Query.To,12000,6000,200,100,place,new(41,-87),false);
        history=history with {Rows=[saved],Markers=[new(place,saved.Origin,12000,6000,saved)],TotalRows=1};
        // Constructing a Window registers the resource-invalidation root, without creating an HWND.
        var host = new Window { Content = page };
        var theme = typeof(Collector).Assembly.GetType("DeadWare.App.Theme")!;
        foreach (string name in new[] { "Midnight", "Ember", "Glacier", "Amethyst", "Neon", "Blackout", "Skyrim", "Mage", "Daylight" })
        {
            theme.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [name]); Pump();
            Assert.Equal(((SolidColorBrush)app.Resources["Ink"]).Color, ((SolidColorBrush)Field<Button>(page, "download").Foreground).Color);
            foreach (var size in new[] { new Size(671, 440), new Size(1100, 700) })
                foreach (bool historical in new[] { false, true })
                {
                    var scroll=Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);
                    Field<ComboBox>(page, "mode").SelectedIndex = historical ? 1 : 0; if (historical) Call(page, "ApplyHistory", history); else Call(page, "ApplyLive", live);scroll.ScrollToTop(); Layout(page, size.Width, size.Height);
                Pump();Layout(page,size.Width,size.Height);
                var map = Field<ConnectionMap>(page, "map"); var list=Field<ListBox>(page,"mappedConnections"); Assert.InRange(map.ActualWidth,500,size.Width);Assert.True(map.ActualHeight>=300);Assert.InRange(list.ActualHeight,45,140);
                Assert.True(Field<TextBlock>(page,"summary").TransformToAncestor(page).Transform(new Point()).Y+Field<TextBlock>(page,"summary").ActualHeight<map.TransformToAncestor(page).Transform(new Point()).Y+1);
                    void Snapshot(string suffix)
                    {
                        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);bitmap.Render(page);
                        string? output = Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");if(output is null)return;
                        Directory.CreateDirectory(output);using var stream=File.Create(Path.Combine(output,$"connections-page-{name}-{size.Width}-{(historical?"history":"live")}{suffix}.png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
                    }
                    Snapshot("");
                    map.Flat=true;map.ResetView();if(list.Items.Count>0)
                    {
                        list.SelectedIndex=0;Layout(page,size.Width,size.Height);Snapshot("-selected");
                        Assert.Equal(((SolidColorBrush)app.Resources["Ink"]).Color,((SolidColorBrush)list.Foreground).Color);
                        Assert.Same(app.Resources["GlassControl"],list.Background);
                        Assert.Same(app.Resources["GlassPanel"],Field<Border>(page,"nodeDetails").Background);
                        Assert.Contains(historical?"Historical connection":"Live connection",Field<TextBlock>(page,"nodeText").Text);
                    }
                    scroll.ScrollToBottom();Pump();Layout(page,size.Width,size.Height);
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
        var sidebar = Assert.IsType<DockPanel>(shell.Children[0] is Border surface ? surface.Child : shell.Children[0]);
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
