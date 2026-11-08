using System.Globalization;
using System.Reflection;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
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
    /// <summary>The Recording, origin and storage expander holds Clear history….</summary>
    private static void CheckClearHistoryEntry(FrameworkElement page)
    {
        var expander = Assert.Single(Elements(page).OfType<Expander>(), e => e.Header as string == "Recording, origin and storage");
        var options = Assert.IsType<StackPanel>(expander.Content);
        var button = Assert.Single(options.Children.OfType<Button>(), b => b.Content as string == "Clear history…");
        Assert.True(ToolTipService.GetShowOnDisabled(button)); Assert.True(button.IsEnabled);
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
        CheckCards(page);
        CheckClearHistoryEntry(page);
        Assert.Equal("Destinations", AutomationProperties.GetName(Assert.Single(Elements(page).OfType<DataGrid>())));
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
        CheckHistoryLimitError(session);
        CheckStripReads(session, now);
        CheckHistoryClearRereads(session);
        CheckStripCursor(session, now);
        CheckMinuteReadFailureKeepsTheStrip(session, now);
        CheckApplicationFilter(session, now);
        CheckShowHistory(session, now);
        CheckDestinationsLive(page, live);
        CheckDestinationsHistory(session, now);
        CheckDestinationScopeSaved(live);
        CheckDestinationsInPlace(page, live);
        CheckDestinationHighlight(page, live);
        CheckScopeChangeClearsTheSelection(page);
        CheckDestinationsCopy(session, live);
        CheckDestinationsExport(session, now);
        CheckBoundaryDates();
        CheckIntervalWarning(session);
        CheckExportBusy(session,now);
        CheckExportRefusesExistingFile(session,now);
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
        Assert.Contains("1 local",Field<TextBlock>(page,"summary").Text);
        // Surrounding whitespace is trimmed like the history query, so a pasted "Browser " still matches.
        Field<TextBox>(page,"search").Text=" 8.8.8 "; Assert.Single(list.Items.Cast<object>()); Assert.Contains("Live · 1 destination · ",Field<TextBlock>(page,"summary").Text);
        Field<TextBox>(page,"search").Text="   "; Assert.Contains("3 destinations",Field<TextBlock>(page,"summary").Text); Field<TextBox>(page,"search").Text="";
        Field<TextBox>(page,"country").Text="JP"; Assert.Empty(list.Items); Field<TextBox>(page,"country").Text="";
        Call(page,"ApplyLive",live with {Available=false,Status="Permission denied"}); list.SelectedIndex=0;
        Assert.Contains("Permission denied",Field<TextBlock>(page,"capture").Text);Assert.Contains("—",Field<TextBlock>(page,"nodeText").Text);
        var options=Elements(page).OfType<Expander>().Single();options.IsExpanded=true;Layout(page,671,600);
        Assert.Contains(Elements((DependencyObject)options.Content).OfType<TextBlock>(),t=>t.Text.Contains("The strip under the map stacks the six applications with the most traffic in the interval, plus Other; its colours identify applications, not direction. Click it to move the minute cursor, or click an application to show only it.",StringComparison.Ordinal));
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
        // After the first minute the whole interval is read for the strip, and playback waits for it as for any read.
        replies.Dequeue().SetResult(View(requested[^1], "Minute zero")); Pump(); Assert.Equal(before + 1, requested.Count);
        Assert.Equal((new DateTime(2026, 10, 29, 10, 0, 0), new DateTime(2026, 10, 29, 10, 3, 0)), (requested[^1].From.LocalDateTime, requested[^1].To.LocalDateTime));
        PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 1, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Whole interval")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 2, requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].From - requested[^3].From); Assert.Equal(TimeSpan.FromMinutes(1), requested[^1].To - requested[^1].From);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled); // Pending next minute cannot animate the prior minute.
        replies.Dequeue().SetResult(View(requested[^1], "Minute one")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 3, requested.Count);
        replies.Dequeue().SetResult(View(requested[^1], "Minute two")); Pump(); PageType.GetMethod("AdvancePlayback", All)!.Invoke(page, null); Assert.Equal(before + 3, requested.Count); Assert.Equal("Play", Field<Button>(page, "play").Content);
        Assert.False(Field<ConnectionMap>(page,"map").AnimationEnabled);
        Field<TextBox>(page, "search").Text = "pending"; mode.SelectedIndex = 0; replies.Dequeue().SetResult(View(requested[^1], "HISTORY LEAK")); Pump(); Assert.DoesNotContain("HISTORY LEAK", Field<TextBlock>(page, "capture").Text); Assert.False(Field<DispatcherTimer>(page, "playback").IsEnabled);
    }
    private static void CheckWholePageLayout(FrameworkElement page)
    {
        // The traffic strip sits in row 2, under the map and above the node list, in History only.
        var card=Field<Border>(page,"trafficCard");var mode=Field<ComboBox>(page,"mode");
        mode.SelectedIndex=1;Layout(page,671,440);Pump();Layout(page,671,440);
        var nodeList=(UIElement)VisualTreeHelper.GetParent(Field<ListBox>(page,"mappedConnections"));
        Assert.Equal(Visibility.Visible,card.Visibility);Assert.Equal(2,Grid.GetRow(card));Assert.Equal(3,Grid.GetRow(nodeList));
        Assert.True(card.ActualHeight>0);
        Assert.True(card.TransformToAncestor(page).Transform(new Point()).Y+card.ActualHeight<=nodeList.TransformToAncestor(page).Transform(new Point()).Y+1,"The traffic strip sits above the node list.");
        Assert.True(Field<ConnectionMap>(page,"map").ActualHeight>=300,$"Map height {Field<ConnectionMap>(page,"map").ActualHeight} with the strip.");
        mode.SelectedIndex=0;Layout(page,671,440);Assert.Equal(Visibility.Collapsed,card.Visibility);
        Layout(page,671,440);Pump();Layout(page,671,440);
        var map=Field<ConnectionMap>(page,"map");Assert.True(map.ActualHeight>=300);
        Assert.Equal("Destinations",AutomationProperties.GetName(Assert.Single(Elements(page).OfType<DataGrid>())));
        var scroll=Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);Assert.True(double.IsFinite(scroll.ExtentHeight));
        scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var download=Field<Button>(page,"download");Assert.InRange(download.TransformToAncestor(page).Transform(new Point()).Y,0,440-download.ActualHeight);
        var expander=Elements(page).OfType<Expander>().Single();expander.IsExpanded=true;Layout(page,671,440);scroll.ScrollToBottom();Pump();Layout(page,671,440);
        var city=Field<ComboBox>(page,"home");Assert.InRange(city.TransformToAncestor(page).Transform(new Point()).Y,0,440-city.ActualHeight);
        expander.IsExpanded=false;scroll.ScrollToTop();Pump();Layout(page,671,440);
        // The Destinations card: a fixed 315 DIP grid, reached by scrolling the page.
        var destinations=Card(page);Assert.Equal(5,Grid.GetRow(destinations));Assert.Equal(315,destinations.Grid.ActualHeight);
        double top=destinations.Grid.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y;
        scroll.ScrollToVerticalOffset(top-40);Pump();Layout(page,671,440);
        double shown=destinations.Grid.TransformToAncestor(page).Transform(new Point()).Y;
        Assert.True(shown>=0&&shown+315<=440,$"Destinations grid at {shown} after scrolling.");
        // A wheel down over the grid's last row, which cannot scroll further, scrolls the page.
        scroll.ScrollToTop();Pump();Layout(page,671,440);Assert.Equal(0,scroll.VerticalOffset);
        var last=Assert.IsType<DataGridRow>(destinations.Grid.ItemContainerGenerator.ContainerFromIndex(destinations.Rows.Count-1));
        var preview=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120) {RoutedEvent=UIElement.PreviewMouseWheelEvent};last.RaiseEvent(preview);
        if(!preview.Handled)last.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,-120) {RoutedEvent=UIElement.MouseWheelEvent});
        Pump();Layout(page,671,440);Assert.True(scroll.VerticalOffset>0,"The wheel over the grid's last row did not scroll the page.");
        scroll.ScrollToTop();Pump();Layout(page,671,440);
    }
    private static DestinationsCard Card(object page) => (DestinationsCard)PageType.GetField("destinations", All)!.GetValue(page)!;
    private static T Cell<T>(DestinationsCard card, string key, Func<DestinationRow, T> pick) => pick(card.Rows.Single(r => r.Key == key));
    private static DataGridColumn Column(DestinationsCard card, string header) => card.Grid.Columns.Single(c => (string)c.Header == header);
    private static string[] Keys(DestinationsCard card) => card.Rows.Select(r => r.Key).ToArray();
    /// <summary>The empty text when the empty border replaces the grid, or null when the grid shows.</summary>
    private static string? EmptyText(DestinationsCard card)
    {
        bool empty = card.Empty.Visibility == Visibility.Visible;
        Assert.Equal(empty ? Visibility.Collapsed : Visibility.Visible, card.Grid.Visibility);
        return empty ? ((TextBlock)card.Empty.Child).Text : null;
    }
    /// <summary>The realized header's tooltip, through the card's real header style.</summary>
    private static string? HeaderTip(FrameworkElement page, DestinationsCard card, string header)
    {
        Layout(page, 1000, 700);
        return (string?)Elements(card.Grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Single(h => h.Column?.Header as string == header).ToolTip;
    }
    /// <summary>A header click through DataGrid's own Sorting event.</summary>
    private static void Sort(DestinationsCard card, string header) => typeof(DataGrid).GetMethod("OnSorting", All)!.Invoke(card.Grid, [new DataGridSortingEventArgs(Column(card, header))]);
    private static void CheckDestinationsLive(FrameworkElement page, ConnectionLiveView live)
    {
        var card = Card(page); var connections = (ConnectionsPage)page; var summary = Field<TextBlock>(page, "summary");
        Field<ComboBox>(page, "mode").SelectedIndex = 0; connections.ApplyLive(live);
        Assert.Equal(["DESTINATION", "CLASS", "LOCATION", "APPS", "PORTS", "↓ DOWNLOAD", "↑ UPLOAD", "RECEIVED", "SENT", "CONNECTIONS"], card.Grid.Columns.Select(c => (string)c.Header));
        Assert.Equal("Destinations shown", AutomationProperties.GetName(card.Scope)); Assert.Equal(120, card.Scope.Width);
        Assert.True(card.Grid.IsReadOnly); Assert.Equal(DataGridSelectionMode.Single, card.Grid.SelectionMode); Assert.False(card.Grid.CanUserReorderColumns); Assert.False(card.Grid.CanUserResizeRows);
        // RECEIVED descending by default.
        Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card)); Assert.Equal(System.ComponentModel.ListSortDirection.Descending, Column(card, "RECEIVED").SortDirection);
        Assert.Equal(3, card.Rows.Count); card.Scope.SelectedIndex = 1; Assert.Equal(2, card.Rows.Count); card.Scope.SelectedIndex = 2; Assert.Single(card.Rows); card.Scope.SelectedIndex = 0;
        Assert.Equal(["Local · private", "Internet", "Internet"], card.Rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Class));
        Assert.Equal("Local address: counted under Local.", Cell(card, "192.168.1.1", r => r.ClassTip)); Assert.Equal("Public Internet address.", Cell(card, "8.8.8.8", r => r.ClassTip));
        Assert.Equal("Since each flow was first observed.", HeaderTip(page, card, "RECEIVED"));
        Assert.Equal("Observed flows to this address: one per app process, local address and port, remote port and protocol. A flow idle for 60 s drops out.", HeaderTip(page, card, "CONNECTIONS"));
        Assert.Null(HeaderTip(page, card, "DESTINATION"));
        string tip = Cell(card, "8.8.8.8", r => r.DestinationTip)!;
        Assert.StartsWith("8.8.8.8", tip); Assert.Contains(DestinationCopy.ObservedSince(live.Timestamp), tip); Assert.Contains("Capture is incomplete; totals may be low.", tip);
        Assert.Equal("Fixture, United States", Cell(card, "8.8.8.8", r => r.Location)); Assert.Null(Cell(card, "8.8.8.8", r => r.LocationTip));
        Assert.Equal("—", Cell(card, "192.168.1.1", r => r.Location)); Assert.Equal("Local addresses are not located.", Cell(card, "192.168.1.1", r => r.LocationTip));
        Assert.Equal(("Browser", "443 Tcp", "1.0 KB", "512.0 B", "128.0 B/s", "64.0 B/s", "1"), Cell(card, "8.8.8.8", r => (r.Apps, r.Ports, r.Received, r.Sent, r.Download, r.Upload, r.Connections)));
        Assert.Equal("Browser · ↓ 1.0 KB · ↑ 512.0 B", Cell(card, "8.8.8.8", r => r.AppsTip));
        Assert.Equal("8.8.8.8, Internet, Fixture, United States, 1 app: Browser, port 443 Tcp, received 1.0 KB, sent 512.0 B, download 128.0 B/s, upload 64.0 B/s, 1 connection, incomplete", Cell(card, "8.8.8.8", r => r.Spoken));
        Layout(page, 1000, 700); Assert.Equal(card.Rows[0].Spoken, AutomationProperties.GetName(Assert.IsType<DataGridRow>(card.Grid.ItemContainerGenerator.ContainerFromIndex(0))));
        // The unlocated public row's reason follows the location database hook.
        var hook = connections.hasLocationDatabase;
        connections.hasLocationDatabase = () => false; connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(1) });
        Assert.Equal("—", Cell(card, "9.9.9.9", r => r.Location)); Assert.Equal("No location database. Use Download / update locations at the bottom of the page.", Cell(card, "9.9.9.9", r => r.LocationTip));
        connections.hasLocationDatabase = () => true; connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(2) });
        Assert.Equal("Not in the location database, or its lookup has not finished.", Cell(card, "9.9.9.9", r => r.LocationTip));
        connections.hasLocationDatabase = hook;
        Assert.Equal("3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.", card.CountLine.Text);
        Assert.Equal(DestinationCopy.Footnote(false), card.Footnote.Text); Assert.Equal(Visibility.Collapsed, card.Status.Visibility);
        // A stale view has no current rates.
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(3), Available = false });
        Assert.Equal(("—", "—"), Cell(card, "8.8.8.8", r => (r.Download, r.Upload)));
        Assert.Equal("Endpoint capture is stale or unavailable; no current rate.", Cell(card, "8.8.8.8", r => r.DownloadTip)); Assert.Equal("Endpoint capture is stale or unavailable; no current rate.", Cell(card, "8.8.8.8", r => r.UploadTip));
        // The summary's counts come from the table's list: an address with a located and an unlocated peer is located in both.
        var first = live.Connections[0];
        var second = first with { Traffic = first.Traffic with { Peer = first.Traffic.Peer with { RemotePort = 80 }, ReceivedBytes = 10, SentBytes = 0 }, Location = null };
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(4), Connections = [.. live.Connections, second] });
        Assert.StartsWith("Live · 3 destinations · 1 country · 1 local · 1 unlocated · ", summary.Text);
        Assert.Equal("3 destinations: 2 Internet (1 unlocated), 1 local, 0 special-purpose.", card.CountLine.Text);
        Assert.Equal(("443 Tcp +1", "2"), Cell(card, "8.8.8.8", r => (r.Ports, r.Connections)));
        // Each Live empty text.
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(5), Available = false, Connections = [] });
        Assert.Equal("No destinations: per-app capture is not running. The capture status above says why.", EmptyText(card)); Assert.Empty(card.Rows);
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(6), Connections = [] });
        Assert.Equal("No destinations yet. Apps that send or receive traffic appear here.", EmptyText(card));
        connections.ApplyLive(live); var search = Field<TextBox>(page, "search");
        search.Text = "no such app"; Assert.Equal("No destinations match the filters.", EmptyText(card));
        // Filters that remove every connection win over the scope, and the application filter counts as a filter.
        card.Scope.SelectedIndex = 1; Assert.Equal("No destinations match the filters.", EmptyText(card));
        search.Text = ""; Assert.Null(EmptyText(card));
        PageType.GetField("applicationFilter", All)!.SetValue(page, "NO SUCH APP"); connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(7) });
        Assert.Equal("No destinations match the filters.", EmptyText(card));
        PageType.GetField("applicationFilter", All)!.SetValue(page, "");
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(8), Connections = [live.Connections[1]] });
        Assert.Equal("No Internet destinations right now.", EmptyText(card));
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(9), Connections = [live.Connections[0]] }); card.Scope.SelectedIndex = 2;
        Assert.Equal("No local destinations right now.", EmptyText(card));
        card.Scope.SelectedIndex = 0; connections.ApplyLive(live); Assert.Null(EmptyText(card)); Assert.Equal(3, card.Rows.Count);
        // An IPv6 destination is written without brackets, in its cell and when spoken.
        var v6 = first with { Traffic = first.Traffic with { Peer = first.Traffic.Peer with { RemoteAddress = "2001:4860::8888" } } };
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(10), Connections = [v6] });
        Assert.Equal("2001:4860::8888", Cell(card, "2001:4860::8888", r => r.Destination));
        Assert.StartsWith("2001:4860::8888, Internet, ", Cell(card, "2001:4860::8888", r => r.Spoken));
        connections.ApplyLive(live); Assert.Equal(3, card.Rows.Count);
    }
    private static void CheckDestinationsHistory(UiSession session, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "DeadWare-destinations-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The two-location fixture of CheckSavedMapDetails: 201 minutes to 8.8.8.8 from 201 apps, the first one busiest.
            var store = new ConnectionHistoryStore(Path.Combine(root, "history.db")); store.Initialize();
            var start = now.AddMinutes(-201); var place = new ConnectionLocation("JP", "Japan", null, "Saved Tokyo", new(35, 139), "old source");
            var records = Enumerable.Range(0, 201).Select(i => new ConnectionHistoryRecord("b" + i, "fixture", new(start.AddMinutes(i), new(new(10 + i, 1, "Peer " + i), "", 0, "8.8.8.8", 443, ConnectionProtocol.Tcp), i == 0 ? 10000 : 1, 0, start.AddMinutes(i), start.AddMinutes(i)), i == 0 ? place : place with { City = "Revised", Point = new(40, -90) }, i == 0 ? new(41, -87) : new(51, 0), true)).ToArray(); store.Append(records, [], now);
            var page = new ConnectionsPage(session); var card = Card(page); var from = Field<TextBox>(page, "from");
            string startText = start.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); from.Text = startText; Field<TextBox>(page, "to").Text = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var read = (Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>>)(q => Task.FromResult(store.Read(q)));
            PageType.GetField("readHistory", All)!.SetValue(page, read);
            var mode = Field<ComboBox>(page, "mode"); mode.SelectedIndex = 1;
            Assert.Equal(["DESTINATION", "CLASS", "LOCATION", "APPS", "PORTS", "RECEIVED", "SENT", "MINUTES", "FIRST SEEN", "LAST SEEN"], card.Grid.Columns.Select(c => (string)c.Header));
            Assert.Equal("Saved in the shown interval.", HeaderTip(page, card, "RECEIVED"));
            Assert.Equal("Saved minutes with traffic to this address in the shown interval.", HeaderTip(page, card, "MINUTES"));
            var row = Assert.Single(card.Rows);
            Assert.Equal(("8.8.8.8", "Internet", "Revised, Japan", "Peer 0 +200", "443 Tcp", "201"), (row.Key, row.Class, row.Location, row.Apps, row.Ports, row.Minutes));
            Assert.Equal((start.ToLocalTime().ToString("g"), now.AddMinutes(-1).ToLocalTime().ToString("g")), (row.FirstSeen, row.LastSeen));
            Assert.Equal("8.8.8.8, Internet, Revised, Japan, 201 apps: Peer 0 and 200 more, port 443 Tcp, received 10.2 KB, sent 0.0 B, 201 minutes, last seen " + now.AddMinutes(-1).ToLocalTime().ToString("g"), row.Spoken);
            Assert.Equal("1 destination: 1 Internet (0 unlocated), 0 local, 0 special-purpose.", card.CountLine.Text); Assert.Null(card.CountLine.ToolTip);
            Assert.Equal(DestinationCopy.Footnote(true), card.Footnote.Text);
            // Filters that leave no rows win over the scope; the scope alone names its kind.
            Field<TextBox>(page, "search").Text = "nobody"; Assert.Equal("No saved destinations match the filters in this interval.", EmptyText(card));
            card.Scope.SelectedIndex = 2; Assert.Equal("No saved destinations match the filters in this interval.", EmptyText(card));
            Field<TextBox>(page, "search").Text = ""; Assert.Equal("No saved local destinations in this interval.", EmptyText(card));
            card.Scope.SelectedIndex = 0; Assert.Null(EmptyText(card));
            // The two-location row highlights both of its nodes; minute-cursor moves keep it selected while its address is listed.
            var map = Field<ConnectionMap>(page, "map"); var minuteBox = Field<CheckBox>(page, "minute");
            card.Grid.SelectedItem = Assert.Single(card.Rows);
            Assert.Equal(2, map.HighlightedKeys.Count); Assert.Equal("Highlighted 2 map nodes.", card.Status.Text); Assert.Equal(Visibility.Visible, card.Status.Visibility);
            minuteBox.IsChecked = true; Assert.Equal("8.8.8.8", card.Selected?.Address);
            Assert.Equal([map.DisplayedPoints.Single(p => p.Point == place.Point).Key], map.HighlightedKeys); Assert.Equal("Highlighted 1 map node.", card.Status.Text);
            Field<Slider>(page, "cursor").Value = 1; Assert.Equal("8.8.8.8", card.Selected?.Address);
            Assert.Equal([map.DisplayedPoints.Single(p => p.Point == new GeoPoint(40, -90)).Key], map.HighlightedKeys);
            minuteBox.IsChecked = false; Assert.Equal("8.8.8.8", card.Selected?.Address); Assert.Equal(2, map.HighlightedKeys.Count);
            // An invalid interval, a pending read, then a thrown one.
            from.Text = "bad date"; Call(page, "RequestHistory", null); Assert.Equal("Choose a valid interval to list saved destinations.", EmptyText(card)); Assert.Empty(card.Rows);
            var pending = new TaskCompletionSource<ConnectionHistoryView>();
            PageType.GetField("readHistory", All)!.SetValue(page, (Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>>)(_ => pending.Task));
            from.Text = startText; Call(page, "RequestHistory", null); Assert.Equal("Reading saved destinations…", EmptyText(card)); Assert.Empty(card.Rows);
            pending.SetException(new IOException()); Pump();
            Assert.Equal("Destinations unavailable; no saved observations shown.", EmptyText(card));
            Assert.Equal("— destinations: — Internet (— unlocated), — local, — special-purpose.", card.CountLine.Text); Assert.Equal("History unavailable", card.CountLine.ToolTip);
            // The minute cursor on a minute with no saved rows.
            PageType.GetField("readHistory", All)!.SetValue(page, read);
            from.Text = start.AddMinutes(-10).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); Field<CheckBox>(page, "minute").IsChecked = true;
            Assert.Equal("No saved destinations in this minute.", EmptyText(card)); Assert.Null(card.CountLine.ToolTip);
            Field<CheckBox>(page, "minute").IsChecked = false; from.Text = startText; Call(page, "RequestHistory", null); Assert.Single(card.Rows);
            // A destination whose location is outside the 256 markers drawn has no node to highlight.
            var drawn = Enumerable.Range(0, 256).Select(i => new ConnectionHistoryMarker(place with { Point = new(0, -150 + i) }, null, 1000, 0)).ToArray();
            var outside = new ConnectionDestination("1.1.1.1", NetworkAddress.Describe("1.1.1.1"), place, [new(50, 50)], false, [new("Peer", 1, 0)], 1,
                [new(443, ConnectionProtocol.Tcp, 1, 0)], 1, 1, 0, null, null, null, 1, start, start, true);
            var interval = new ConnectionHistoryQuery(start, now);
            page.ApplyHistory(new(interval, [], 0, new(1, 0, 1, 1, 0, 0), drawn, [], [], null, 0, "fixture", Strip: StripFor(interval), Destinations: new([outside], new(1, 1, 0, 0, 0))));
            Assert.Equal(256, map.DisplayedPointCount);
            card.Grid.SelectedItem = Assert.Single(card.Rows); Assert.Empty(map.HighlightedKeys);
            Assert.Equal("Not on the map: its location is not among the 256 drawn.", card.Status.Text);
            // Sorted by ↓ DOWNLOAD in Live, History falls back to RECEIVED descending.
            mode.SelectedIndex = 0;
            LocatedConnection Peer(string address, long received, double download) => new(new(new ConnectionPeer(new(1, 1, "Browser"), "", 0, address, 443, ConnectionProtocol.Tcp), now, now, received, 0, download, 0), null);
            page.ApplyLive(new ConnectionLiveView(now, [Peer("8.8.8.8", 1024, 1), Peer("1.1.1.1", 10, 500)], null, true, true, "fixture"));
            Assert.Equal(["8.8.8.8", "1.1.1.1"], Keys(card));
            Sort(card, "↓ DOWNLOAD"); Assert.Equal(["1.1.1.1", "8.8.8.8"], Keys(card));
            Assert.Equal(System.ComponentModel.ListSortDirection.Descending, Column(card, "↓ DOWNLOAD").SortDirection); Assert.Null(Column(card, "RECEIVED").SortDirection);
            Sort(card, "↓ DOWNLOAD"); Assert.Equal(["8.8.8.8", "1.1.1.1"], Keys(card)); Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, Column(card, "↓ DOWNLOAD").SortDirection);
            Sort(card, "DESTINATION"); Assert.Equal(["1.1.1.1", "8.8.8.8"], Keys(card)); Assert.Equal(System.ComponentModel.ListSortDirection.Ascending, Column(card, "DESTINATION").SortDirection);
            Sort(card, "↓ DOWNLOAD"); Assert.Equal(System.ComponentModel.ListSortDirection.Descending, Column(card, "↓ DOWNLOAD").SortDirection);
            mode.SelectedIndex = 1; Assert.Single(card.Rows);
            Assert.DoesNotContain(card.Grid.Columns, c => (string)c.Header == "↓ DOWNLOAD");
            Assert.Equal(System.ComponentModel.ListSortDirection.Descending, Column(card, "RECEIVED").SortDirection);
            Assert.All(card.Grid.Columns.Where(c => (string)c.Header != "RECEIVED"), c => Assert.Null(c.SortDirection));
            // Switching to History while its read is pending shows History's footnote at once.
            mode.SelectedIndex = 0; Assert.Equal(DestinationCopy.Footnote(false), card.Footnote.Text);
            PageType.GetField("readHistory", All)!.SetValue(page, (Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>>)(_ => new TaskCompletionSource<ConnectionHistoryView>().Task));
            mode.SelectedIndex = 1; Assert.Equal("Reading saved destinations…", EmptyText(card)); Assert.Equal(DestinationCopy.Footnote(true), card.Footnote.Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void CheckDestinationScopeSaved(ConnectionLiveView live)
    {
        var session = (UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        typeof(UiSession).GetField("<Settings>k__BackingField", All)!.SetValue(session, new UserSettings());
        var service = new ConnectionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")); typeof(UiSession).GetField("<Connections>k__BackingField", All)!.SetValue(session, service);
        string settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "-destinations-settings.json");
        typeof(UiSession).GetField("writer", All)!.SetValue(session, new SettingsSnapshotWriter(settingsPath));
        typeof(UiSession).GetField("layout", All)!.SetValue(session, new DetachedWindowState([]));
        var page = new ConnectionsPage(session); var card = Card(page); Assert.Equal(0, card.Scope.SelectedIndex);
        page.ApplyLive(live); Layout(page, 1000, 700); var map = Field<ConnectionMap>(page, "map"); map.Flat = true; map.ResetView(); Layout(page, 1000, 700);
        string[] Nodes() => Field<ListBox>(page, "mappedConnections").Items.Cast<object>().Select(item => item.ToString()!).ToArray();
        string summary = Field<TextBlock>(page, "summary").Text; var nodes = Nodes(); Assert.Single(nodes);
        card.Scope.SelectedIndex = 2;
        Assert.Equal("Local", session.Settings.ConnectionsDestinationScope); Assert.Equal(["192.168.1.1"], Keys(card));
        // The scope narrows the table only.
        Assert.Equal(summary, Field<TextBlock>(page, "summary").Text); Assert.Equal(nodes, Nodes());
        var reopened = new ConnectionsPage(session); Assert.Equal(2, Card(reopened).Scope.SelectedIndex);
        reopened.ApplyLive(live); Assert.Equal(["192.168.1.1"], Keys(Card(reopened)));
        card.Scope.SelectedIndex = 0; Assert.Equal("All", session.Settings.ConnectionsDestinationScope); Assert.Equal(3, card.Rows.Count);
        var save = session.SaveAsync(); PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult(); File.Delete(settingsPath);
    }
    private static void Select(DestinationsCard card, string address) => card.Grid.SelectedItem = card.Rows.Single(r => r.Key == address);
    /// <summary>Presses a key on an element as the keyboard does: the preview first, then the key itself unless it was handled.</summary>
    private static void Press(UIElement target, System.Windows.Input.Key key)
    {
        var preview = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, new KeySource(), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
    }
    /// <summary>The input source a key event must name; it is never added to the sources, and offscreen nothing reads it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual { get => null!; set { } }
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public override bool IsDisposed => false;
    }
    /// <summary>Scrolls the page so that the Destinations grid is in view, and returns the grid's own ScrollViewer.</summary>
    private static ScrollViewer GridInView(FrameworkElement page, DestinationsCard card)
    {
        var scroll = Assert.IsType<ScrollViewer>(((Grid)page).Children[0]);
        Layout(page, 1000, 700);
        double top = card.Grid.TransformToAncestor((Visual)scroll.Content).Transform(new Point()).Y;
        scroll.ScrollToVerticalOffset(top - 40); Pump(); Layout(page, 1000, 700);
        return Elements(card.Grid).OfType<ScrollViewer>().First();
    }
    private static void CheckDestinationsInPlace(FrameworkElement page, ConnectionLiveView live)
    {
        var card = Card(page); var connections = (ConnectionsPage)page; var search = Field<TextBox>(page, "search");
        var clock = card.clockSeconds; double seconds = 1000; card.clockSeconds = () => seconds;
        Field<ComboBox>(page, "mode").SelectedIndex = 0; card.Scope.SelectedIndex = 0;
        var first = live.Connections[0];
        LocatedConnection Peer(string address, long received) => first with { Traffic = first.Traffic with { Peer = first.Traffic.Peer with { RemoteAddress = address }, ReceivedBytes = received }, Location = null };
        // 40 Internet destinations, enough rows to scroll the grid.
        ConnectionLiveView Many(int second, long extra) => live with { Timestamp = live.Timestamp.AddSeconds(second), Connections = Enumerable.Range(0, 40).Select(i => Peer($"20.0.0.{i + 1}", 100_000 - i * 1000 + extra)).ToArray() };
        connections.ApplyLive(Many(100, 0));
        var grid = GridInView(page, card);
        grid.ScrollToVerticalOffset(100); Pump(); Layout(page, 1000, 700);
        double offset = grid.VerticalOffset; Assert.True(offset > 0, $"The Destinations grid did not scroll ({grid.ScrollableHeight} scrollable).");
        var rows = card.Rows.ToArray(); var selected = rows[5]; card.Grid.SelectedItem = selected; Layout(page, 1000, 700);
        var container = card.Grid.ItemContainerGenerator.ContainerFromItem(selected); Assert.NotNull(container);
        int changes = 0, notified = 0; card.Rows.CollectionChanged += (_, _) => changes++;
        foreach (var row in rows) row.PropertyChanged += (_, _) => notified++;
        // A view with a new Timestamp (every RECEIVED cell 0.5 KB higher, same order) updates the same rows in place: row containers, the selection and the scroll offset survive.
        var second = Many(101, 500); connections.ApplyLive(second); Layout(page, 1000, 700); Pump(); Layout(page, 1000, 700);
        Assert.True(rows.SequenceEqual(card.Rows), "The rows were replaced."); Assert.Equal(0, changes); Assert.Equal(40, notified);
        Assert.Same(selected, card.Grid.SelectedItem); Assert.Same(container, card.Grid.ItemContainerGenerator.ContainerFromItem(selected));
        // Pixel scrolling re-measures the updated rows, so the offset may drift by up to a pixel; it must not jump.
        Assert.True(Math.Abs(grid.VerticalOffset - offset) <= 1, $"The Destinations grid scrolled from {offset} to {grid.VerticalOffset} across an in-place update.");
        Assert.Equal("20.0.0.6", card.Selected?.Address);
        // An equal view, and a stale view read twice from a ConnectionService (a new copy each read), change nothing.
        notified = 0; connections.ApplyLive(second); connections.ApplyLive(second with { }); Assert.Equal((0, 0), (changes, notified));
        var service = new ConnectionService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db"));
        typeof(ConnectionService).GetField("live", All)!.SetValue(service, second);
        var stale = service.Live; Assert.NotSame(stale, service.Live); Assert.False(stale.Available);
        connections.ApplyLive(stale); Assert.Equal(0, changes); Assert.Equal(40, notified); Assert.Equal("—", selected.Download);
        notified = 0; connections.ApplyLive(service.Live); Assert.Equal((0, 0), (changes, notified));
        // A scope change re-sorts and scrolls the grid to its top.
        card.Scope.SelectedIndex = 1; Pump(); Layout(page, 1000, 700); Assert.Equal(0, grid.VerticalOffset); Assert.Same(selected, card.Grid.SelectedItem);
        // The keyboard menu opens on the selected row, scrolled into view first; the pointer selects the row under it, and off a row there is none.
        var last = card.Rows[^1]; card.Grid.SelectedItem = last;
        var keyed = card.RowForMenu(null, true); Assert.NotNull(keyed); Assert.Same(last, keyed!.Item); Assert.True(grid.VerticalOffset > 0, "The selected row was not scrolled into view.");
        var pointed = Assert.IsType<DataGridRow>(card.Grid.ItemContainerGenerator.ContainerFromItem(card.Rows[^2]));
        Assert.Same(pointed, card.RowForMenu(pointed, false)); Assert.Same(card.Rows[^2], card.Grid.SelectedItem);
        Assert.Null(card.RowForMenu(card.Grid, false)); Assert.Same(card.Rows[^2], card.Grid.SelectedItem);
        card.ClearSelection(); Assert.Null(card.RowForMenu(null, true));
        grid.ScrollToVerticalOffset(0); Pump(); Layout(page, 1000, 700);
        card.Scope.SelectedIndex = 0; card.ClearSelection(); Assert.Null(card.Selected);
        // While the pointer is over the grid, new data moves nothing: a new destination joins the bottom.
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(200) }); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card));
        var kept = card.Rows.ToArray();
        LocatedConnection big = Peer("1.1.1.1", 5000), small = Peer("1.1.1.1", 10);
        ConnectionLiveView With(int s, params LocatedConnection[] extra) => live with { Timestamp = live.Timestamp.AddSeconds(s), Connections = [.. live.Connections, .. extra] };
        card.Hold.PointerOver = true;
        connections.ApplyLive(With(201, big)); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1", "1.1.1.1"], Keys(card)); Assert.True(kept.SequenceEqual(card.Rows.Take(3)));
        // During the hold, F5, a header click and a Live search change each re-sort at once; a selected row that F5 moves stays selected.
        Select(card, "1.1.1.1"); var moved = card.Grid.SelectedItem;
        Press(card.Grid, System.Windows.Input.Key.F5); Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card));
        Assert.Same(moved, card.Grid.SelectedItem); Assert.Equal("1.1.1.1", card.Selected?.Address); card.ClearSelection();
        connections.ApplyLive(With(202, small)); Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card));
        Sort(card, "RECEIVED"); Assert.Equal(["1.1.1.1", "192.168.1.1", "9.9.9.9", "8.8.8.8"], Keys(card));
        Sort(card, "RECEIVED"); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1", "1.1.1.1"], Keys(card));
        connections.ApplyLive(With(203, big)); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1", "1.1.1.1"], Keys(card));
        search.Text = "."; Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card)); search.Text = "";
        // A selected row that leaves clears the selection.
        Select(card, "9.9.9.9"); Assert.Equal("9.9.9.9", card.Selected?.Address);
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(204), Connections = [live.Connections[0], live.Connections[1], big] });
        Assert.Null(card.Grid.SelectedItem); Assert.Null(card.Selected); Assert.Equal(["1.1.1.1", "8.8.8.8", "192.168.1.1"], Keys(card));
        // Without the hold the next new Timestamp re-sorts.
        card.Hold.PointerOver = false;
        connections.ApplyLive(With(205, small)); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1", "1.1.1.1"], Keys(card));
        // A navigation key holds the order for 3 s on the card's clock.
        Press(card.Grid, System.Windows.Input.Key.Down); seconds += 2.9; Assert.True(card.Hold.IsHeld(card.clockSeconds()));
        connections.ApplyLive(With(206, big)); Assert.Equal(["8.8.8.8", "9.9.9.9", "192.168.1.1", "1.1.1.1"], Keys(card));
        seconds += .2; Assert.False(card.Hold.IsHeld(card.clockSeconds()));
        connections.ApplyLive(With(207, big)); Assert.Equal(["1.1.1.1", "8.8.8.8", "9.9.9.9", "192.168.1.1"], Keys(card));
        // Unloading the page releases the pointer and menu holds.
        card.Hold.PointerOver = true; card.Hold.MenuOpen = true;
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Assert.False(card.Hold.PointerOver); Assert.False(card.Hold.MenuOpen);
        card.clockSeconds = clock; card.ClearSelection(); connections.ApplyLive(live);
        Assert.IsType<ScrollViewer>(((Grid)page).Children[0]).ScrollToTop(); Pump(); Layout(page, 671, 440);
    }
    private static void CheckDestinationHighlight(FrameworkElement page, ConnectionLiveView live)
    {
        var card = Card(page); var map = Field<ConnectionMap>(page, "map"); var connections = (ConnectionsPage)page; var box = Field<Border>(page, "nodeDetails");
        var first = live.Connections[0]; var place = first.Location!;
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(400) }); Layout(page, 1000, 700);
        string node = map.DisplayedPoints.Single(p => p.Point == place.Point).Key;
        // With the globe turned away, selecting the row turns the map to its node.
        map.Flat = false; map.SetView(new(-40, 90), 1); Assert.Null(map.PointPosition(node));
        Select(card, "8.8.8.8");
        Assert.Equal([node], map.HighlightedKeys); Assert.Equal(Visibility.Collapsed, box.Visibility);
        Assert.Equal("Highlighted 1 map node.", card.Status.Text); Assert.Equal(Visibility.Visible, card.Status.Visibility);
        Assert.NotNull(map.PointPosition(node));
        Select(card, "192.168.1.1"); Assert.Empty(map.HighlightedKeys); Assert.Equal("Not on the map: local addresses are not located.", card.Status.Text);
        Select(card, "9.9.9.9"); Assert.Equal("Not on the map: no map position for this address.", card.Status.Text);
        // A located special-purpose peer is drawn, so its row highlights its node.
        var special = first with { Traffic = first.Traffic with { Peer = first.Traffic.Peer with { RemoteAddress = "100.64.0.1" } }, Location = place with { Point = new(10, 10) } };
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(401), Connections = [.. live.Connections, special] });
        Select(card, "100.64.0.1"); Assert.Single(map.HighlightedKeys); Assert.Equal("Highlighted 1 map node.", card.Status.Text);
        // A node selected after a row clears the table selection and the highlight; the node stays selected after the regroup and the next update.
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(402) }); map.Flat = true; map.ResetView(); Layout(page, 1000, 700);
        Select(card, "8.8.8.8"); Assert.Single(map.HighlightedKeys);
        SelectNode(page, node); Layout(page, 1000, 700);
        Assert.Null(card.Selected); Assert.Null(card.Grid.SelectedItem); Assert.Empty(map.HighlightedKeys); Assert.Equal(Visibility.Collapsed, card.Status.Visibility);
        Assert.True(map.DisplayedPoints.Single(p => p.Key == node).Selected); Assert.Equal(Visibility.Visible, box.Visibility);
        connections.ApplyLive(live with { Timestamp = live.Timestamp.AddSeconds(403) }); Assert.True(map.DisplayedPoints.Single(p => p.Key == node).Selected);
        // Selecting a row clears the node selection and its detail box.
        Select(card, "8.8.8.8"); Assert.Equal(Visibility.Collapsed, box.Visibility); Assert.DoesNotContain(map.DisplayedPoints, p => p.Selected);
        Assert.Null(PageType.GetField("selectedKey", All)!.GetValue(page)); Assert.Equal([node], map.HighlightedKeys);
        // Esc clears both selections.
        Press(card.Grid, System.Windows.Input.Key.Escape); Assert.Null(card.Selected); Assert.Empty(map.HighlightedKeys); Assert.Equal(Visibility.Collapsed, card.Status.Visibility);
        SelectNode(page, node); Layout(page, 1000, 700); Assert.Equal(Visibility.Visible, box.Visibility);
        Press(page, System.Windows.Input.Key.Escape); Assert.Equal(Visibility.Collapsed, box.Visibility); Assert.DoesNotContain(map.DisplayedPoints, p => p.Selected);
        // The detail box's Close leaves the table selection.
        Select(card, "8.8.8.8");
        var close = Elements(box).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Close"); close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("8.8.8.8", card.Selected?.Address); Assert.Equal([node], map.HighlightedKeys);
        // Live: the selected row gains a location in a later view with the same Timestamp; its node is highlighted and the camera stays.
        card.ClearSelection(); map.Flat = false; map.SetView(place.Point!, 1);
        var located = live with { Timestamp = live.Timestamp.AddSeconds(410) }; connections.ApplyLive(located);
        Select(card, "9.9.9.9"); var camera = map.Camera; Assert.Empty(map.HighlightedKeys);
        connections.ApplyLive(located with { Connections = [located.Connections[0], located.Connections[1], located.Connections[2] with { Location = place with { City = "Late", Point = new(-30, 150) } }] });
        string late = map.DisplayedPoints.Single(p => p.Point == new GeoPoint(-30, 150)).Key;
        Assert.Equal("9.9.9.9", card.Selected?.Address); Assert.Equal([late], map.HighlightedKeys); Assert.Equal(camera, map.Camera); Assert.Equal("Highlighted 1 map node.", card.Status.Text);
        // Live with 300 locations: a highlighted location beyond the first 256 keys is drawn.
        card.ClearSelection();
        var many = live with { Timestamp = live.Timestamp.AddSeconds(420), Connections = Enumerable.Range(0, 300).Select(i => first with { Traffic = first.Traffic with { Peer = first.Traffic.Peer with { RemoteAddress = $"20.0.{i / 200}.{i % 200 + 1}" } }, Location = place with { Point = new(0, -150 + i) } }).ToArray() };
        connections.ApplyLive(many); Assert.Equal(256, map.DisplayedPointCount); Assert.Equal(300, card.Rows.Count);
        var hidden = many.Connections.First(c => !map.DisplayedPoints.Any(p => p.Point == c.Location!.Point));
        Select(card, hidden.Traffic.Peer.RemoteAddress);
        Assert.Equal(256, map.DisplayedPointCount); Assert.Contains(map.DisplayedPoints, p => p.Point == hidden.Location!.Point);
        Assert.Single(map.HighlightedKeys); Assert.Equal("Highlighted 1 map node.", card.Status.Text);
        card.ClearSelection(); Assert.Empty(map.HighlightedKeys); Assert.Equal(Visibility.Collapsed, card.Status.Visibility);
        connections.ApplyLive(live); map.Flat = true; map.ResetView(); Layout(page, 671, 440);
    }
    /// <summary>Review Focus 5: a scope that hides the selected row clears the selection, the highlight and the status line.</summary>
    private static void CheckScopeChangeClearsTheSelection(FrameworkElement page)
    {
        var card = Card(page); var map = Field<ConnectionMap>(page, "map");
        Assert.Equal(0, card.Scope.SelectedIndex); Assert.Equal(3, card.Rows.Count);
        Select(card, "8.8.8.8"); Assert.Single(map.HighlightedKeys); Assert.Equal(Visibility.Visible, card.Status.Visibility);
        card.Scope.SelectedIndex = 2;
        Assert.Null(card.Selected); Assert.Empty(map.HighlightedKeys); Assert.Equal(Visibility.Collapsed, card.Status.Visibility); Assert.Equal("", card.Status.Text);
        card.Scope.SelectedIndex = 0; Assert.Null(card.Grid.SelectedItem); Assert.Equal(3, card.Rows.Count);
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


    private static DateTimeOffset Local(int hour, int minute, int second = 0)
    { var time = new DateTime(2026, 10, 29, hour, minute, second); return new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time)); }
    /// <summary>One-minute slots over the query, one application, leaving out the minute indexes in <paramref name="gaps"/>.</summary>
    private static TrafficStrip StripFor(ConnectionHistoryQuery query, params int[] gaps)
    {
        var slots = new List<TrafficStripSlot>();
        for (int i = 0; query.From.AddMinutes(i) < query.To; i++)
        {
            if (gaps.Contains(i)) continue;
            var end = query.From.AddMinutes(i + 1);
            slots.Add(new(query.From.AddMinutes(i), end < query.To ? end : query.To, 1, 60, false, [1000, 0], [500, 0]));
        }
        return new(query.From, query.To, 60_000, TrafficMeasure.Total, [new("Browser", "BROWSER", 1000L * slots.Count, 500L * slots.Count)], 0, 0, 0, slots);
    }
    private static ConnectionHistoryView StripView(ConnectionHistoryQuery query, long destinations = 1, TrafficStrip? strip = null) =>
        new(query, [], 0, new(0, 0, 0, destinations, 0, 0), [], [], [], null, 0, "fixture", Strip: strip ?? StripFor(query));
    /// <summary>A readHistory that queues each request and its reply, as CheckHistoryQueries does.</summary>
    private sealed class FakeReads
    {
        public readonly List<ConnectionHistoryQuery> Requested = [];
        public readonly Queue<(ConnectionHistoryQuery Query, TaskCompletionSource<ConnectionHistoryView> Reply)> Replies = new();
        public FakeReads(object page) => PageType.GetField("readHistory", All)!.SetValue(page, (Func<ConnectionHistoryQuery, Task<ConnectionHistoryView>>)(q =>
        { Requested.Add(q); var reply = new TaskCompletionSource<ConnectionHistoryView>(); Replies.Enqueue((q, reply)); return reply.Task; }));
        public ConnectionHistoryQuery Last => Requested[^1];
        /// <summary>Answers the oldest outstanding read with a view of its own query and a strip for it, then lets the pump go on.</summary>
        public void Answer(long destinations = 1, TrafficStrip? strip = null) { var (query, reply) = Replies.Dequeue(); reply.SetResult(StripView(query, destinations, strip)); Pump(); }
        public void Fail(Exception error) { Replies.Dequeue().Reply.SetException(error); Pump(); }
        public void Drain() { while (Replies.Count > 0) Answer(); }
    }
    private static bool IsMinute(ConnectionHistoryQuery query, int hour, int minute) => query.From == Local(hour, minute) && query.To == Local(hour, minute + 1);
    private static bool IsWhole(ConnectionHistoryQuery query, int minutes) => query.From == Local(10, 0) && query.To == Local(10, minutes);
    private static string? ChartMessage(TrafficStripChart chart) => (string?)typeof(TrafficStripChart).GetField("message", All)!.GetValue(chart);
    private static void CheckStripReads(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        var chart = Field<TrafficStripView>(page, "trafficStrip").Chart;
        var minute = Field<CheckBox>(page, "minute"); var cursor = Field<Slider>(page, "cursor"); var search = Field<TextBox>(page, "search");
        var summary = Field<TextBlock>(page, "summary"); var status = Field<TextBlock>(page, "actionStatus");
        // Cursor off: one request per context, and its reply carries the strip.
        Field<ComboBox>(page, "mode").SelectedIndex = 1; Assert.Single(reads.Requested);
        var today = StripFor(reads.Last); reads.Answer(strip: today); Assert.Single(reads.Requested); Assert.Same(today, chart.Strip);
        // Cursor on, 10:00–10:03: the minute, then the whole interval for the strip.
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00"; Field<TextBox>(page, "to").Text = "2026-10-29 10:03";
        minute.IsChecked = true; Assert.Equal(2, reads.Requested.Count); Assert.True(IsMinute(reads.Last, 10, 0));
        reads.Answer(destinations: 4); Assert.Equal(3, reads.Requested.Count); Assert.True(IsWhole(reads.Last, 3));
        var whole = StripFor(reads.Last); reads.Answer(destinations: 99, strip: whole); Assert.Same(whole, chart.Strip);
        // A cursor move reads only its minute.
        cursor.Value = 1; Assert.Equal(4, reads.Requested.Count); Assert.True(IsMinute(reads.Last, 10, 1)); reads.Answer(); Assert.Equal(4, reads.Requested.Count); Assert.Empty(reads.Replies);
        // A search change reads the minute, then the whole interval; the strip reply applies only its Strip.
        search.Text = "x"; Assert.True(IsMinute(reads.Last, 10, 0)); Assert.Equal("x", reads.Last.Search); Assert.Null(chart.Strip);
        reads.Answer(destinations: 4); Assert.True(IsWhole(reads.Last, 3)); Assert.Equal("x", reads.Last.Search);
        var searched = StripFor(reads.Last); reads.Answer(destinations: 99, strip: searched);
        Assert.Same(searched, chart.Strip); Assert.Contains("4 destinations", summary.Text); Assert.DoesNotContain("99", summary.Text);
        // Ten cursor moves while the strip read is in flight start no other strip read; the last minute is read after it.
        search.Text = "y"; reads.Answer(); Assert.True(IsWhole(reads.Last, 3)); int count = reads.Requested.Count;
        for (int i = 0; i < 10; i++) cursor.Value = i % 2 + 1;
        Assert.Equal(count, reads.Requested.Count);
        reads.Answer(); Assert.Equal(count + 1, reads.Requested.Count); Assert.True(IsMinute(reads.Last, 10, 2)); Assert.Equal("y", reads.Last.Search); reads.Drain();
        Assert.Equal(count + 1, reads.Requested.Count);
        // A strip reply for an old context is dropped.
        search.Text = "z1"; reads.Answer(); Assert.True(IsWhole(reads.Last, 3)); search.Text = "z2"; var stale = StripFor(reads.Last);
        reads.Answer(strip: stale); Assert.Null(chart.Strip); Assert.True(IsMinute(reads.Last, 10, 0)); Assert.Equal("z2", reads.Last.Search);
        reads.Answer(); Assert.True(IsWhole(reads.Last, 3)); Assert.Equal("z2", reads.Last.Search); reads.Answer(); Assert.NotNull(chart.Strip); Assert.NotSame(stale, chart.Strip);
        // A thrown strip read shows "History unavailable" and is not read again for that context.
        search.Text = "w"; reads.Answer(destinations: 6); Assert.True(IsWhole(reads.Last, 3)); reads.Fail(new IOException());
        Assert.Null(chart.Strip); Assert.Equal("History unavailable", ChartMessage(chart)); Assert.NotNull(PageType.GetField("stripFailed", All)!.GetValue(page));
        Assert.Equal("History could not be read (IOException).", status.Text); Assert.Contains("6 destinations", summary.Text);
        count = reads.Requested.Count; cursor.Value = 1; Assert.Equal(count + 1, reads.Requested.Count); Assert.True(IsMinute(reads.Last, 10, 1));
        reads.Answer(); Assert.Equal(count + 1, reads.Requested.Count); Assert.Empty(reads.Replies); Assert.Equal("History unavailable", ChartMessage(chart));
        // A waiting strip read for a context left before it started is dropped: back on the failed context, only its minute is read.
        search.Text = "w2"; Assert.True(IsMinute(reads.Last, 10, 0)); Assert.Equal("w2", reads.Last.Search); search.Text = "w"; count = reads.Requested.Count;
        reads.Drain(); Assert.Equal(count + 1, reads.Requested.Count); Assert.True(IsMinute(reads.Last, 10, 0)); Assert.Equal("w", reads.Last.Search);
        Assert.Equal("History unavailable", ChartMessage(chart));
        // A thrown read with the cursor off: every count is "—", with the tooltip naming why.
        minute.IsChecked = false; Assert.True(IsWhole(reads.Last, 3)); reads.Fail(new IOException());
        Assert.Equal("History · minute-bucket average rates · — destinations · — countries · — local · — unlocated · ↓ — received / ↑ — sent", summary.Text);
        Assert.Equal("History unavailable", summary.ToolTip); Assert.Equal("History unavailable", ChartMessage(chart));
        // Showing again re-reads a strip applied before its interval ended, and keeps one applied after.
        page.clock = () => Local(10, 2);
        minute.IsChecked = true; Assert.True(IsMinute(reads.Last, 10, 0)); reads.Answer(); Assert.Empty(reads.Replies);
        search.Text = "v"; reads.Answer(); Assert.True(IsWhole(reads.Last, 3)); reads.Answer(); Assert.NotNull(chart.Strip);
        Assert.Null(summary.ToolTip);
        count = reads.Requested.Count; page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(count + 1, reads.Requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), reads.Last.To - reads.Last.From);
        page.clock = () => Local(10, 10);
        reads.Answer(); Assert.Equal(count + 2, reads.Requested.Count); Assert.True(IsWhole(reads.Last, 3)); reads.Answer(); Assert.NotNull(chart.Strip);
        count = reads.Requested.Count; page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(count + 1, reads.Requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), reads.Last.To - reads.Last.From);
        reads.Answer(); Assert.Equal(count + 1, reads.Requested.Count); Assert.Empty(reads.Replies);
        // ReloadHistory re-reads the minute and then the whole interval with the cursor on.
        page.ReloadHistory(); Assert.Equal(count + 2, reads.Requested.Count); Assert.Equal(TimeSpan.FromMinutes(1), reads.Last.To - reads.Last.From);
        reads.Answer(); Assert.Equal(count + 3, reads.Requested.Count); Assert.True(IsWhole(reads.Last, 3)); reads.Answer(); Assert.NotNull(chart.Strip); Assert.Empty(reads.Replies);
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
    }
    /// <summary>Raises the session's HistoryClearChanged backing delegate, as its timer does when a clear starts or ends.</summary>
    private static void RaiseHistoryClear(UiSession session, HistoryClearStore store)
        => ((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged", All)!.GetValue(session))?.Invoke(store);
    private static void CheckHistoryClearRereads(UiSession session)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        Field<ComboBox>(page, "mode").SelectedIndex = 1; reads.Drain();
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); reads.Drain();
        var whole = reads.Last; int count = reads.Requested.Count;
        // A Usage clear is not Connections history's: no read.
        RaiseHistoryClear(session, HistoryClearStore.Usage); RaiseHistoryClear(session, HistoryClearStore.Alerts); Pump();
        Assert.Equal(count, reads.Requested.Count);
        // A Connections clear starting or ending repeats the shown whole-interval read.
        RaiseHistoryClear(session, HistoryClearStore.Connections);
        Assert.Equal(count + 1, reads.Requested.Count); Assert.Equal((whole.From, whole.To), (reads.Last.From, reads.Last.To)); reads.Drain();
        Assert.Equal(count + 1, reads.Requested.Count);
        // Unloaded, the page no longer listens.
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); count = reads.Requested.Count;
        RaiseHistoryClear(session, HistoryClearStore.Connections); Pump();
        Assert.Equal(count, reads.Requested.Count);
    }
    private static void CheckStripCursor(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        var chart = Field<TrafficStripView>(page, "trafficStrip").Chart;
        var minute = Field<CheckBox>(page, "minute"); var cursor = Field<Slider>(page, "cursor");
        Field<ComboBox>(page, "mode").SelectedIndex = 1; reads.Drain();
        // 10:00–10:05 with no slot for 10:02.
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00"; Field<TextBox>(page, "to").Text = "2026-10-29 10:05";
        Call(page, "RequestHistory", null); Assert.True(IsWhole(reads.Last, 5)); reads.Answer(strip: StripFor(reads.Last, 2));
        Layout(page, 1000, 700); chart.SlotAt(new Point());
        Point At(DateTimeOffset time) => new(chart.PlotRect.Left + chart.PlotRect.Width * (time - Local(10, 0)).TotalSeconds / 300, chart.PlotRect.Top + chart.PlotRect.Height / 2);
        Assert.True(chart.PlotRect.Width > 100, $"Strip plot width {chart.PlotRect.Width}.");
        Assert.Null(chart.MarkTime); Assert.Null(chart.SelectedIndex);
        // A click turns the minute cursor on at the minute under the pointer; FiltersChanged does not reset it.
        chart.HandleClick(At(Local(10, 1, 40)));
        Assert.True(minute.IsChecked); Assert.Equal(1, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 1)); Assert.Single(reads.Replies);
        Assert.Equal(Local(10, 1, 30), chart.MarkTime); Assert.Equal(1, chart.SelectedIndex); reads.Drain();
        // Right and Left go to the first minute of the next or previous listed slot.
        Assert.True(chart.HandleKey(System.Windows.Input.Key.Right)); Assert.Equal(3, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 3)); reads.Drain();
        Assert.True(chart.HandleKey(System.Windows.Input.Key.Left)); Assert.Equal(1, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 1)); reads.Drain();
        // In a gap, to the neighbours; the hairline moves at once, before the minute's reply.
        cursor.Value = 2; Assert.Equal(Local(10, 2, 30), chart.MarkTime); Assert.Null(chart.SelectedIndex); Assert.True(IsMinute(reads.Last, 10, 2)); reads.Drain();
        chart.HandleKey(System.Windows.Input.Key.Left); Assert.Equal(1, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 1)); reads.Drain();
        cursor.Value = 2; reads.Drain(); chart.HandleKey(System.Windows.Input.Key.Right); Assert.Equal(3, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 3)); reads.Drain();
        cursor.Value = 4; Assert.Equal(Local(10, 4, 30), chart.MarkTime); Assert.Equal(3, chart.SelectedIndex); Assert.Single(reads.Replies); reads.Drain();
        // Playback moves the hairline as it steps the cursor.
        cursor.Value = 0; reads.Drain(); Click(page, "play"); reads.Drain(); Assert.Equal("Pause", Field<Button>(page, "play").Content);
        Call(page, "AdvancePlayback", null); Assert.Equal(1, cursor.Value); Assert.Equal(Local(10, 1, 30), chart.MarkTime); Assert.Equal(1, chart.SelectedIndex); Assert.Single(reads.Replies);
        Click(page, "play"); reads.Drain();
        // cursorTime keeps the cursor minute while the strip read is queued and in flight.
        Field<TextBox>(page, "search").Text = "s"; Assert.Equal(Local(10, 0).ToString("MM-dd HH:mm"), Field<TextBlock>(page, "cursorTime").Text);
        reads.Answer(); Assert.True(IsWhole(reads.Last, 5)); Assert.Equal(Local(10, 0).ToString("MM-dd HH:mm"), Field<TextBlock>(page, "cursorTime").Text);
        reads.Answer(strip: StripFor(reads.Last, 2)); Assert.Equal(0, chart.SelectedIndex);
        // With the from box edited and not applied, a click and a key change nothing.
        Field<TextBox>(page, "from").Text = "2026-10-29 09:00"; int count = reads.Requested.Count;
        chart.HandleClick(At(Local(10, 4, 10))); chart.HandleKey(System.Windows.Input.Key.Right);
        Assert.Equal(count, reads.Requested.Count); Assert.Equal(0, cursor.Value); Assert.Equal(0, chart.SelectedIndex); Assert.Equal(Local(10, 0, 30), chart.MarkTime);
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00";
        chart.HandleClick(At(Local(10, 4, 10))); Assert.Equal(4, cursor.Value); Assert.True(IsMinute(reads.Last, 10, 4)); reads.Drain();
    }
    /// <summary>Review Focus 3: a minute read that fails while the cursor is on keeps the strip of the whole interval.</summary>
    private static void CheckMinuteReadFailureKeepsTheStrip(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        var chart = Field<TrafficStripView>(page, "trafficStrip").Chart; var summary = Field<TextBlock>(page, "summary");
        Field<ComboBox>(page, "mode").SelectedIndex = 1; reads.Drain();
        Field<TextBox>(page, "from").Text = "2026-10-29 10:00"; Field<TextBox>(page, "to").Text = "2026-10-29 10:03";
        Call(page, "RequestHistory", null); var strip = StripFor(reads.Last); reads.Answer(strip: strip); Assert.Same(strip, chart.Strip);
        Field<CheckBox>(page, "minute").IsChecked = true; Assert.True(IsMinute(reads.Last, 10, 0));
        reads.Fail(new IOException());
        Assert.Same(strip, chart.Strip); Assert.Empty(reads.Replies);
        Assert.Contains("— destinations", summary.Text); Assert.Equal("History unavailable", summary.ToolTip);
        Assert.Equal("History could not be read (IOException).", Field<TextBlock>(page, "actionStatus").Text);
        Field<Slider>(page, "cursor").Value = 1; Assert.True(IsMinute(reads.Last, 10, 1));
        reads.Answer(destinations: 5); Assert.Contains("5 destinations", summary.Text); Assert.Null(summary.ToolTip); Assert.Same(strip, chart.Strip); Assert.Empty(reads.Replies);
        // The summary groups its counts like the Destinations count line and says "1 destination".
        Field<Slider>(page, "cursor").Value = 2; reads.Answer(destinations: 1234); Assert.Contains($"· {1234:N0} destinations · 0 countries ·", summary.Text);
        Field<Slider>(page, "cursor").Value = 1; reads.Answer(destinations: 1); Assert.Contains("· 1 destination · 0 countries ·", summary.Text);
    }
    /// <summary>chrome.exe and two more applications in Other, over the query's first minute.</summary>
    private static TrafficStrip ChromeStrip(ConnectionHistoryQuery query)
    {
        var end = query.From.AddMinutes(1) < query.To ? query.From.AddMinutes(1) : query.To;
        return new(query.From, query.To, 60_000, TrafficMeasure.Total, [new("chrome.exe", "CHROME.EXE", 1000, 500)], 300, 100, 2,
            [new(query.From, end, 1, 60, false, [1000, 300], [500, 100])]);
    }
    private static void CheckApplicationFilter(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        var view = Field<TrafficStripView>(page, "trafficStrip"); var button = Field<Button>(page, "applicationButton"); var mode = Field<ComboBox>(page, "mode");
        mode.SelectedIndex = 1; Assert.Equal("", reads.Last.Application); Assert.Equal(Visibility.Collapsed, button.Visibility);
        reads.Answer(strip: ChromeStrip(reads.Last));
        void ChooseChrome()
        {
            // The Other entry is not a button, so chrome.exe's is the only one.
            Assert.Contains(view.Legend, entry => entry.Text.StartsWith("Other (2 more)") && !entry.IsButton);
            var chrome = Assert.Single(Elements(view).OfType<Button>());
            Assert.Equal("chrome.exe: show only this application", AutomationProperties.GetName(chrome));
            // The peer raises Click at Input priority; Pump runs it.
            int count = reads.Requested.Count;
            ((IInvokeProvider)new ButtonAutomationPeer(chrome).GetPattern(PatternInterface.Invoke)).Invoke(); Pump();
            Assert.Equal(count + 1, reads.Requested.Count); Assert.Equal("CHROME.EXE", reads.Last.Application);
        }
        ChooseChrome();
        Assert.Equal(Visibility.Visible, button.Visibility); Assert.Equal(240, button.MaxWidth);
        var text = Assert.IsType<TextBlock>(button.Content); Assert.Equal("Application: chrome.exe ✕", text.Text); Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Equal("Clear application filter", AutomationProperties.GetName(button));
        Assert.Equal("Only chrome.exe is shown. Click to show every application.", button.ToolTip);
        // The export query carries the filter, and the field is part of the history context.
        object?[] args = [null]; var returned = (ConnectionHistoryQuery)PageType.GetMethod("BuildQuery", All)!.Invoke(page, args)!; var whole = (ConnectionHistoryQuery)args[0]!;
        Assert.Equal("CHROME.EXE", whole.Application); Assert.Equal("CHROME.EXE", returned.Application);
        var same = PageType.GetMethod("SameHistoryContext", All)!;
        Assert.True((bool)same.Invoke(null, [whole, whole with { }])!); Assert.False((bool)same.Invoke(null, [whole, whole with { Application = "X" }])!);
        // Clearing the filter reads every application again.
        reads.Answer(strip: ChromeStrip(reads.Last)); Click(page, "applicationButton");
        Assert.Equal("", reads.Last.Application); Assert.Equal(Visibility.Collapsed, button.Visibility);
        Assert.Null(button.ToolTip); Assert.Equal("", text.Text);
        reads.Answer(strip: ChromeStrip(reads.Last)); ChooseChrome(); reads.Drain();
        // Live filters by key too: chrome.exe and Chrome.exe, never other.exe.
        mode.SelectedIndex = 0; Assert.Equal(Visibility.Visible, button.Visibility);
        var place = new ConnectionLocation("US", "United States", null, "Fixture", new(40, -90), "Fixture");
        LocatedConnection Connection(string name, int pid, string address) =>
            new(new(new ConnectionPeer(new(pid, 1, name), "", 0, address, 443, ConnectionProtocol.Tcp), now, now, 1024, 512, 0, 0), place);
        var live = new ConnectionLiveView(now, [Connection("chrome.exe", 1, "1.1.1.1"), Connection("Chrome.exe", 2, "2.2.2.2"), Connection("other.exe", 3, "3.3.3.3")], null, true, true, "fixture");
        page.ApplyLive(live);
        var summary = Field<TextBlock>(page, "summary"); Assert.StartsWith("Live · 2 destinations", summary.Text);
        var listed = ((System.Collections.IEnumerable)PageType.GetField("rows", All)!.GetValue(page)!).Cast<object>().Select(row => (string)row.GetType().GetProperty("Application")!.GetValue(row)!).ToArray();
        Assert.Equal(["chrome.exe / 1", "Chrome.exe / 2"], listed);
        Click(page, "applicationButton"); Assert.StartsWith("Live · 3 destinations", summary.Text); Assert.Equal(Visibility.Collapsed, button.Visibility);
    }
    private static void CheckShowHistory(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var reads = new FakeReads(page);
        var mode = Field<ComboBox>(page, "mode"); var range = Field<ComboBox>(page, "range"); var direction = Field<ComboBox>(page, "direction");
        var search = Field<TextBox>(page, "search"); var country = Field<TextBox>(page, "country"); var from = Field<TextBox>(page, "from"); var to = Field<TextBox>(page, "to");
        var minute = Field<CheckBox>(page, "minute"); var cursor = Field<Slider>(page, "cursor"); var button = Field<Button>(page, "applicationButton");
        static string Box(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        range.SelectedIndex = 2; search.Text = "x"; country.Text = "US"; direction.SelectedItem = ConnectionDirection.Sent; minute.IsChecked = true;
        Assert.Empty(reads.Requested);
        // From Live: History for the whole minutes, every other filter cleared, the application keyed from its bare name.
        page.ShowHistory(now.AddMinutes(-90).AddSeconds(20), now.AddMinutes(-30), "Chrome.exe");
        Assert.Equal(1, mode.SelectedIndex); Assert.Single(reads.Requested);
        // Showing the page while that read is in flight reads nothing more.
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); Assert.Single(reads.Requested);
        Assert.Equal(Box(now.AddMinutes(-89)), from.Text); Assert.Equal(Box(now.AddMinutes(-30)), to.Text);
        Assert.Equal("", search.Text); Assert.Equal("", country.Text); Assert.Equal(ConnectionDirection.Both, direction.SelectedItem);
        Assert.False(minute.IsChecked); Assert.Equal(0, cursor.Value); Assert.Equal(2, range.SelectedIndex);
        var first = reads.Last;
        Assert.Equal((now.AddMinutes(-89), now.AddMinutes(-30)), (first.From, first.To));
        Assert.Equal(("", "", ConnectionDirection.Both, "CHROME.EXE"), (first.Search, first.CountryCode, first.Direction, first.Application));
        Assert.Equal(Visibility.Visible, button.Visibility); Assert.Equal("Application: Chrome.exe ✕", Assert.IsType<TextBlock>(button.Content).Text);
        // From History a request follows at once; an empty interval opens its minute, and null shows every application.
        reads.Drain(); int count = reads.Requested.Count;
        page.ShowHistory(now.AddMinutes(-20), now.AddMinutes(-20), null);
        Assert.Equal(count + 1, reads.Requested.Count); Assert.Equal(1, mode.SelectedIndex);
        var second = reads.Last; Assert.Equal((now.AddMinutes(-20), now.AddMinutes(-19), ""), (second.From, second.To, second.Application));
        Assert.Equal(Visibility.Collapsed, button.Visibility);
        // An interval wholly before what Connections keeps: one "Not kept" band, and the storage line still names the earliest retained time.
        var earliest = now.AddDays(-2); var (query, reply) = reads.Replies.Dequeue();
        var notKept = new TrafficStrip(query.From, query.To, 60_000, TrafficMeasure.Total, [], 0, 0, 0, [], RetainedFrom: query.To);
        reply.SetResult(new(query, [], 0, new(0, 0, 0, 0, 0, 0), [], [], [], earliest, 4096, "fixture", Strip: notKept)); Pump();
        var view = Field<TrafficStripView>(page, "trafficStrip"); var chart = view.Chart;
        Layout(page, 1000, 700); chart.SlotAt(new Point());
        Assert.Same(notKept, chart.Strip); Assert.True(chart.PlotRect.Width > 100, $"Strip plot width {chart.PlotRect.Width}.");
        Assert.Equal<Rect?>(chart.PlotRect, chart.NotKept);
        Assert.StartsWith("Not kept before ", view.NotKeptLine);
        Assert.Contains("Earliest " + earliest.ToLocalTime().ToString("g"), Field<TextBlock>(page, "storage").Text);
        // The boxes widen over daylight-saving minutes so that they always hold the interval asked for.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 10, 5, DayOfWeek.Sunday));
        var zone = TimeZoneInfo.CreateCustomTimeZone("dst-test", TimeSpan.Zero, "dst-test", "dst-test", "dst-test-summer", [rule]);
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 10, 25, 1, 30, 0))); Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 29, 1, 30, 0)));
        var parse = PageType.GetMethod("TryParseLocalTime", All)!;
        DateTimeOffset Parse(string text) { object?[] values = [text, zone, null]; Assert.True((bool)parse.Invoke(null, values)!, text); return (DateTimeOffset)values[2]!; }
        // Fall back: 00:30 UTC is 01:30 summer time and 01:10 UTC is 01:10 standard time, both ambiguous; the start widens to 00:59 and the end to 02:00.
        var ambiguousStart = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero); var ambiguousEnd = new DateTimeOffset(2026, 10, 25, 1, 10, 0, TimeSpan.Zero);
        var fall = ConnectionsPage.LocalBoxes(ambiguousStart, ambiguousEnd, zone);
        Assert.Equal(("2026-10-25 00:59", "2026-10-25 02:00"), fall);
        Assert.True(Parse(fall.From) <= ambiguousStart && Parse(fall.To) >= ambiguousEnd);
        // Spring forward: local 01:00–01:59 does not exist; an end at the change (01:00 UTC) is written as 02:00, the first valid minute after it, never 01:00.
        var springStart = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero); var invalidEnd = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);
        var spring = ConnectionsPage.LocalBoxes(springStart, invalidEnd, zone);
        Assert.Equal(("2026-03-29 00:30", "2026-03-29 02:00"), spring);
        Assert.Equal((springStart, invalidEnd), (Parse(spring.From), Parse(spring.To)));
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
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
    /// <summary>Ctrl+C and the row menu copy the selected row's shown cells, or its address, through SetClipboard; the grid's own copy never runs.</summary>
    private static void CheckDestinationsCopy(UiSession session, ConnectionLiveView live)
    {
        var page = new ConnectionsPage(session); Call(page, "ApplyLive", live); Layout(page, 1000, 700); var card = Card(page); var status = Field<TextBlock>(page, "actionStatus");
        string? copied = null; Exception? failure = null;
        PageType.GetField("SetClipboard", All)!.SetValue(page, (Action<string>)(text => { if (failure is not null) throw failure; copied = text; }));
        int gridCopies = 0; card.Grid.CopyingRowClipboardContent += (_, _) => gridCopies++;
        void CtrlC() => System.Windows.Input.ApplicationCommands.Copy.Execute(null, card.Grid);
        MenuItem Item(string header) => card.Grid.ContextMenu!.Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
        Assert.Equal("Copy row", AutomationProperties.GetName(Item("Copy row"))); Assert.Equal("Copy address", AutomationProperties.GetName(Item("Copy address")));
        // With no selection Ctrl+C does nothing.
        CtrlC(); Assert.Null(copied); Assert.Equal("", status.Text);
        // Ctrl+C: the shown cells in column order, tab-separated, on one line, with no header.
        Select(card, "8.8.8.8"); var row = card.Rows.Single(r => r.Key == "8.8.8.8");
        string expected = string.Join("\t", row.Destination, row.Class, row.Location, row.Apps, row.Ports, row.Download, row.Upload, row.Received, row.Sent, row.Connections);
        Assert.Equal(["DESTINATION", "CLASS", "LOCATION", "APPS", "PORTS", "↓ DOWNLOAD", "↑ UPLOAD", "RECEIVED", "SENT", "CONNECTIONS"], card.Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => (string)c.Header));
        CtrlC();
        Assert.Equal(expected, copied); Assert.StartsWith("8.8.8.8\tInternet\tFixture, United States\tBrowser\t443 Tcp\t", copied);
        Assert.DoesNotContain('\n', copied!); Assert.Equal(10, copied!.Split('\t').Length);
        Assert.Equal("Copied to the clipboard.", status.Text); Assert.Equal(0, gridCopies);
        // The row menu: Copy row copies the same line, Copy address only the address.
        copied = null; status.Text = ""; Item("Copy row").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Equal(expected, copied); Assert.Equal("Copied to the clipboard.", status.Text);
        copied = null; status.Text = ""; Item("Copy address").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.Equal("8.8.8.8", copied); Assert.Equal("Copied to the clipboard.", status.Text);
        // A clipboard that fails names the exception type only.
        failure = new System.Runtime.InteropServices.ExternalException(); copied = null;
        CtrlC(); Assert.Null(copied); Assert.Equal("Copy failed (ExternalException); try again.", status.Text);
        failure = null; status.Text = "";
        // While the row menu is open the order is held.
        var menu = card.Grid.ContextMenu!;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent)); Assert.True(card.Hold.MenuOpen);
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent)); Assert.False(card.Hold.MenuOpen);
        // Cleared again, Ctrl+C does nothing.
        card.ClearSelection(); copied = null; CtrlC(); Assert.Null(copied); Assert.Equal("", status.Text); Assert.Equal(0, gridCopies);
    }
    /// <summary>Export destinations… exports the whole-interval query with every filter and the card's scope, shares Export CSV's busy state and texts,
    /// and names the TEMP limit.</summary>
    private static void CheckDestinationsExport(UiSession session, DateTimeOffset now)
    {
        var page = new ConnectionsPage(session); var card = Card(page); var button = card.Export; var csv = Field<Button>(page, "export"); var status = Field<TextBlock>(page, "actionStatus");
        Assert.Equal("Export destinations…", AutomationProperties.GetName(button)); Assert.Equal("Export destinations…", button.Content);
        var calls = new List<(string Path, ConnectionHistoryQuery Query, DestinationScope Scope)>(); Func<Task> result = () => Task.CompletedTask;
        PageType.GetField("exportDestinations", All)!.SetValue(page, (Func<string, ConnectionHistoryQuery, DestinationScope, Task>)((path, query, scope) => { calls.Add((path, query, scope)); return result(); }));
        // Live: disabled, with a tooltip that shows on the disabled button.
        Assert.False(button.IsEnabled); Assert.Equal("Export is available in History.", button.ToolTip); Assert.True(ToolTipService.GetShowOnDisabled(button));
        Field<ComboBox>(page, "mode").SelectedIndex = 1; Pump();
        Assert.True(button.IsEnabled); Assert.Null(button.ToolTip);
        // Every filter, the application filter included; the minute cursor is ignored.
        var fromLocal = new DateTime(2026, 10, 29, 10, 0, 0); var toLocal = fromLocal.AddHours(1);
        Field<TextBox>(page, "from").Text = fromLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); Field<TextBox>(page, "to").Text = toLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Field<TextBox>(page, "search").Text = "8.8"; Field<TextBox>(page, "country").Text = "us"; Field<ComboBox>(page, "direction").SelectedItem = ConnectionDirection.Received;
        PageType.GetField("applicationFilter", All)!.SetValue(page, "BROWSER");
        Field<CheckBox>(page, "minute").IsChecked = true; Field<Slider>(page, "cursor").Value = 3; card.Scope.SelectedIndex = 1; Pump();
        static DateTimeOffset Utc(DateTime local) => new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        var whole = new ConnectionHistoryQuery(Utc(fromLocal), Utc(toLocal), "8.8", "US", ConnectionDirection.Received, 0, 200, "BROWSER");
        var exportTo = PageType.GetMethod("ExportDestinationsToPathAsync", All)!;
        Task ExportTo(string path) => (Task)exportTo.Invoke(page, [path])!;
        string root = Path.Combine(Path.GetTempPath(), "DeadWare-destinations-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string fresh = Path.Combine(root, "destinations.csv");
            var exporting = ExportTo(fresh); PumpUntil(() => exporting.IsCompleted); exporting.GetAwaiter().GetResult();
            var call = Assert.Single(calls); Assert.Equal((fresh, whole, DestinationScope.Internet), call);
            Assert.Equal("CSV exported to a new file. Saved history is unchanged.", status.Text); Assert.True(button.IsEnabled); Assert.True(csv.IsEnabled);
            // An existing file is refused before the export runs.
            string existing = Path.Combine(root, "existing.csv"); File.WriteAllText(existing, "keep");
            var refused = ExportTo(existing); Assert.True(refused.IsCompleted); Assert.Single(calls); Assert.Equal("keep", File.ReadAllText(existing));
            Assert.Equal("That file already exists; exports never overwrite. Choose a new file name.", status.Text);
            // While Export CSV runs this button is disabled and starts nothing; while this one runs Export CSV is disabled and starts nothing.
            var csvDone = new TaskCompletionSource(); int csvCalls = 0;
            PageType.GetField("exportHistory", All)!.SetValue(page, (Func<string, ConnectionHistoryQuery, Task>)((_, _) => { csvCalls++; return csvDone.Task; }));
            var csvExport = (Task)PageType.GetMethod("ExportToPathAsync", All)!.Invoke(page, [Path.Combine(root, "connections.csv"), whole])!;
            Assert.False(button.IsEnabled); Assert.False(csv.IsEnabled);
            var skipped = ExportTo(Path.Combine(root, "skipped.csv")); Assert.True(skipped.IsCompleted); Assert.Single(calls);
            csvDone.SetResult(); PumpUntil(() => csvExport.IsCompleted); Assert.True(button.IsEnabled); Assert.True(csv.IsEnabled);
            var mine = new TaskCompletionSource(); result = () => mine.Task;
            var running = ExportTo(Path.Combine(root, "running.csv")); Assert.Equal(2, calls.Count);
            Assert.False(button.IsEnabled); Assert.False(csv.IsEnabled);
            var csvSkipped = (Task)PageType.GetMethod("ExportToPathAsync", All)!.Invoke(page, [Path.Combine(root, "connections-2.csv"), whole])!; Assert.True(csvSkipped.IsCompleted); Assert.Equal(1, csvCalls);
            Field<ComboBox>(page, "mode").SelectedIndex = 0; Assert.False(button.IsEnabled); Assert.Equal("Export is available in History.", button.ToolTip);
            Field<ComboBox>(page, "mode").SelectedIndex = 1; Pump(); Assert.False(button.IsEnabled);
            mine.SetResult(); PumpUntil(() => running.IsCompleted); Assert.True(button.IsEnabled); Assert.True(csv.IsEnabled);
            // The TEMP limit names itself; any other failure uses Export CSV's text.
            result = () => Task.FromException(new ConnectionHistoryLimitException(new IOException()));
            var limited = ExportTo(Path.Combine(root, "limited.csv")); PumpUntil(() => limited.IsCompleted);
            Assert.Equal("This interval holds too many saved rows to export destinations. Choose a shorter interval.", status.Text); Assert.True(button.IsEnabled);
            result = () => Task.FromException(new UnauthorizedAccessException());
            var failed = ExportTo(Path.Combine(root, "failed.csv")); PumpUntil(() => failed.IsCompleted);
            Assert.Equal("Export failed (UnauthorizedAccessException); choose a new writable file.", status.Text); Assert.True(button.IsEnabled);
        }
        finally { Directory.Delete(root, true); }
    }
    private static void CheckHistoryLimitError(UiSession session)
    {
        var page=new ConnectionsPage(session);
        PageType.GetField("readHistory",All)!.SetValue(page,(Func<ConnectionHistoryQuery,Task<ConnectionHistoryView>>)(_=>Task.FromException<ConnectionHistoryView>(new ConnectionHistoryLimitException(new IOException()))));
        Field<ComboBox>(page,"mode").SelectedIndex=1;Pump();
        Assert.Equal("This interval holds too many saved rows to show. Choose a shorter interval.",Field<TextBlock>(page,"actionStatus").Text);
        Assert.Contains("History unavailable; no saved observations shown.",Field<TextBlock>(page,"capture").Text);
        Assert.Empty(Field<ListBox>(page,"mappedConnections").Items);
    }
    private static void CheckIntervalWarning(UiSession session)
    {
        var page=new ConnectionsPage(session);Field<ComboBox>(page,"mode").SelectedIndex=1;
        var tryInterval=PageType.GetMethod("TryInterval",All)!;
        bool Interval(){object?[] args=[null,null];return (bool)tryInterval.Invoke(page,args)!;}
        var status=Field<TextBlock>(page,"actionStatus");
        Field<TextBox>(page,"from").Text="2026-11-04 25:00";Assert.False(Interval());Assert.Contains("Choose valid local times",status.Text);
        Field<TextBox>(page,"from").Text="2026-11-04 10:00";Field<TextBox>(page,"to").Text="2026-11-04 11:00";Assert.True(Interval());Assert.Equal("",status.Text);
        // Only the interval warning clears itself; an unrelated action message survives a valid interval.
        status.Text="CSV exported to a new file. Saved history is unchanged.";Assert.True(Interval());Assert.Contains("CSV exported",status.Text);
    }
    private static void CheckExportRefusesExistingFile(UiSession session,DateTimeOffset now)
    {
        var page=new ConnectionsPage(session);Field<ComboBox>(page,"mode").SelectedIndex=1;
        int calls=0;Func<string,ConnectionHistoryQuery,Task> export=(_,_)=>{calls++;return Task.CompletedTask;};
        PageType.GetField("exportHistory",All)!.SetValue(page,export);
        string existing=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"DeadWare-export-"+Guid.NewGuid().ToString("N")+".csv");System.IO.File.WriteAllText(existing,"keep");
        try
        {
            var exporting=(Task)PageType.GetMethod("ExportToPathAsync",All)!.Invoke(page,[existing,new ConnectionHistoryQuery(now.AddMinutes(-1),now)])!;
            Assert.True(exporting.IsCompleted);Assert.Equal(0,calls);Assert.Equal("keep",System.IO.File.ReadAllText(existing));
            Assert.Contains("never overwrite",Field<TextBlock>(page,"actionStatus").Text);Assert.True(Field<Button>(page,"export").IsEnabled);
        }
        finally {System.IO.File.Delete(existing);}
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
        var destination=new ConnectionDestination("8.8.8.8",NetworkAddress.Describe("8.8.8.8"),place,[place.Point!],false,[new("Saved browser",12000,6000)],1,
            [new(443,ConnectionProtocol.Tcp,12000,6000)],1,12000,6000,null,null,null,3,history.Query.From,history.Query.To,false);
        history=history with {Rows=[saved],Markers=[new(place,saved.Origin,12000,6000,saved)],TotalRows=1,Strip=StripFor(history.Query),Destinations=new([destination],new(1,1,0,0,0))};
        // 50 local destinations: none is drawn, so the node list and the map keep their size.
        var locals=Enumerable.Range(1,50).Select(i=>new LocatedConnection(live.Connections[0].Traffic with {Peer=live.Connections[0].Traffic.Peer with {RemoteAddress=$"192.168.0.{i}"}},null)).ToArray();
        var culture=CultureInfo.CurrentCulture;CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US");
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
                var stripCard=Field<Border>(page,"trafficCard");var stripChart=Field<TrafficStripView>(page,"trafficStrip").Chart;
                Assert.Equal(historical?Visibility.Visible:Visibility.Collapsed,stripCard.Visibility);
                if(historical){Assert.Same(history.Strip,stripChart.Strip);Assert.True(stripChart.ActualHeight>=120,$"Strip height {stripChart.ActualHeight}.");}
                Assert.True(Field<TextBlock>(page,"summary").TransformToAncestor(page).Transform(new Point()).Y+Field<TextBlock>(page,"summary").ActualHeight<map.TransformToAncestor(page).Transform(new Point()).Y+1);
                var destinations=Card(page);Assert.Equal(315,destinations.Grid.ActualHeight);Assert.Equal(historical?1:3,destinations.Rows.Count);
                CheckNotTrimmed(destinations,name,size.Width<900);
                if(!historical)
                {
                    Call(page,"ApplyLive",live with {Connections=[]});Layout(page,size.Width,size.Height);Pump();Layout(page,size.Width,size.Height);double empty=map.ActualHeight;
                    Call(page,"ApplyLive",live with {Connections=locals});Layout(page,size.Width,size.Height);Pump();Layout(page,size.Width,size.Height);
                    Assert.Equal(50,destinations.Rows.Count);Assert.Equal(empty,map.ActualHeight,3);Assert.Equal(315,destinations.Grid.ActualHeight);
                    Call(page,"ApplyLive",live);Layout(page,size.Width,size.Height);Pump();Layout(page,size.Width,size.Height);
                }
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
        CultureInfo.CurrentCulture=culture;
    }
    /// <summary>No column header and no FIRST SEEN or LAST SEEN cell is trimmed: each text's unconstrained DesiredSize fits its header or cell.
    /// <paramref name="atMinimum"/>: every column is first forced to its MinWidth (and asserted to sit there), the check runs at those widths,
    /// and the columns' own widths are then restored and laid out again.</summary>
    private static void CheckNotTrimmed(DestinationsCard card,string theme,bool atMinimum)
    {
        if(!atMinimum){CheckNotTrimmedAsLaidOut(card,theme,false);return;}
        var saved=card.Grid.Columns.Select(c=>c.Width).ToArray();
        try
        {
            foreach(var column in card.Grid.Columns)column.Width=new DataGridLength(column.MinWidth);
            card.UpdateLayout();Pump();card.UpdateLayout();
            CheckNotTrimmedAsLaidOut(card,theme,true);
        }
        finally
        {
            for(int i=0;i<saved.Length;i++)card.Grid.Columns[i].Width=saved[i];
            card.UpdateLayout();Pump();card.UpdateLayout();
        }
    }
    private static void CheckNotTrimmedAsLaidOut(DestinationsCard card,string theme,bool atMinimum)
    {
        static double Natural(TextBlock text){text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));double width=text.DesiredSize.Width;text.InvalidateMeasure();return width;}
        var headers=Elements(card.Grid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().Where(h=>h.Column is not null).ToArray();
        Assert.Equal(card.Grid.Columns.Count,headers.Length);
        foreach(var header in headers)
        {
            // Every header keeps the theme's caption look (App.xaml): 10 px text with 9,8 padding, with or without a tooltip.
            Assert.True(header.FontSize==10&&header.Padding==new Thickness(9,8,9,8),$"{theme} header {header.Column.Header} lost the theme's caption look: {header.FontSize} px, padding {header.Padding}.");
            if(atMinimum)Assert.True(Math.Abs(header.Column.ActualWidth-header.Column.MinWidth)<.5,$"{theme} {header.Column.Header} is {header.Column.ActualWidth} wide after forcing its minimum {header.Column.MinWidth}.");
            // The header's ContentPresenter shows the string through one TextBlock (inside an AccessText when it recognizes access keys).
            var text=Elements(header).OfType<TextBlock>().First();
            double natural=Natural(text);
            Assert.True(natural<=header.ActualWidth-header.Padding.Left-header.Padding.Right+.01,$"{theme} header {header.Column.Header} trimmed: {natural} in {header.ActualWidth}.");
        }
        int cells=0;
        foreach(var column in card.Grid.Columns.Where(c=>(string)c.Header is "FIRST SEEN" or "LAST SEEN"))
            foreach(var row in card.Rows)
            {
                if(column.GetCellContent(row) is not TextBlock text)continue;
                DependencyObject parent=text;while(parent is not DataGridCell)parent=VisualTreeHelper.GetParent(parent);
                var cell=(DataGridCell)parent;double natural=Natural(text);cells++;
                Assert.True(natural<=cell.ActualWidth-cell.BorderThickness.Left-cell.BorderThickness.Right+.01,$"{theme} {column.Header} cell {text.Text} trimmed: {natural} in {cell.ActualWidth}.");
            }
        if(card.Grid.Columns.Any(c=>(string)c.Header=="FIRST SEEN"))Assert.True(cells>0,"No FIRST SEEN or LAST SEEN cell was realized.");
    }
    internal static void CheckNavigation(MainWindow dashboard)
    {
        var shell = (Grid)dashboard.Content;
        var sidebar = Assert.IsType<DockPanel>(shell.Children[0] is Border surface ? surface.Child : shell.Children[0]);
        var nav = (StackPanel)sidebar.Children.OfType<ScrollViewer>().Single().Content;
        Assert.Contains(nav.Children.OfType<Button>(), b => System.Windows.Automation.AutomationProperties.GetName(b) == "Connections");
        typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, ["connections"]);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)typeof(MainWindow).GetField("liveToolbar", All)!.GetValue(dashboard)!).Visibility);
        typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, ["overview"]);
        // Show in Connections restores a minimized dashboard, shows Connections and opens History there, without a native window.
        int presented = 0; dashboard.present = _ => presented++;
        var session = (UiSession)typeof(MainWindow).GetField("session", All)!.GetValue(dashboard)!;
        var content = (ContentControl)typeof(MainWindow).GetField("content", All)!.GetValue(dashboard)!;
        var pages = (Dictionary<string, FrameworkElement>)typeof(MainWindow).GetField("pages", All)!.GetValue(dashboard)!;
        var page = Assert.IsType<ConnectionsPage>(pages["connections"]);
        var from = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero); var to = from.AddMinutes(15);
        dashboard.WindowState = WindowState.Minimized;
        session.RequestConnectionsHistory(from, to, "chrome.exe");
        Assert.Equal(1, presented); Assert.Equal(WindowState.Normal, dashboard.WindowState); Assert.Same(page, content.Content);
        Assert.Equal(1, Field<ComboBox>(page, "mode").SelectedIndex);
        Assert.Equal(from.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Field<TextBox>(page, "from").Text);
        Assert.Equal(to.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Field<TextBox>(page, "to").Text);
        Assert.Equal("CHROME.EXE", Field<string>(page, "applicationFilter")); Assert.Equal(Visibility.Visible, Field<Button>(page, "applicationButton").Visibility);
        Assert.Equal(IntPtr.Zero, new WindowInteropHelper(dashboard).Handle);
        // While the lifetime is ending a request does nothing.
        typeof(MainWindow).GetMethod("Navigate", All)!.Invoke(dashboard, ["overview"]);
        var lifetime = (ApplicationLifetime)typeof(MainWindow).GetField("lifetime", All)!.GetValue(dashboard)!;
        lifetime.BeginSessionEnding();
        session.RequestConnectionsHistory(from, to, "chrome.exe");
        Assert.Equal(1, presented); Assert.Same(pages["overview"], content.Content);
        // The later phases reuse this dashboard, and RefreshUpdateNotice returns while the lifetime is ending.
        typeof(ApplicationLifetime).GetField("<IsEnding>k__BackingField", All)!.SetValue(lifetime, false);
    }
    private static void Call(object target, string name, object? value) => PageType.GetMethod(name, All)!.Invoke(target, value is null ? null : [value]);
    private static void Layout(FrameworkElement page, double width, double height) { page.Measure(new(width, height)); page.Arrange(new(0, 0, width, height)); page.UpdateLayout(); }
    private static string Text(DependencyObject root) => string.Join("\n", Elements(root).OfType<TextBlock>().Select(t => t.Text));
    /// <summary>Refined glass: the controls strip, the map and the detail box each sit in a Card; the strip is above the map.</summary>
    private static void CheckCards(FrameworkElement page)
    {
        var card = Application.Current.Resources["Card"];
        static Border? NearestBorder(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Border border) return border;
            return null;
        }
        var map = Field<ConnectionMap>(page, "map");
        var mapCard = NearestBorder(map); Assert.NotNull(mapCard); Assert.Same(card, mapCard!.Style);
        var stripCard = NearestBorder(Field<ComboBox>(page, "mode")); Assert.NotNull(stripCard); Assert.Same(card, stripCard!.Style);
        Assert.NotSame(mapCard, stripCard);
        Assert.True(stripCard.TransformToAncestor(page).Transform(new Point()).Y < mapCard.TransformToAncestor(page).Transform(new Point()).Y, "The controls strip sits above the map card.");
        Assert.Same(card, Field<Border>(page, "nodeDetails").Style);
        Assert.True(map.ActualHeight >= 280, $"Map height {map.ActualHeight} with card padding.");
    }
    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Elements(child)) yield return descendant; } }
}
