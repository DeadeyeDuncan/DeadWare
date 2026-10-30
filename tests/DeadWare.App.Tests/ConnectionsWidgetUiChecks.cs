using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Input;
using System.Text.Json;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

internal static class ConnectionsWidgetUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly DateTimeOffset Epoch=new(2026,10,29,12,0,0,TimeSpan.Zero);
    private sealed class Resolver:IConnectionGeolocation
    {
        public ConnectionLocation? Lookup(string address)=>address=="8.8.8.8"?new("US","United States",null,"Fixture",new(40,-90),"fixture"):null;
    }

    // Called only from BatteryUiTests' existing sole STA Application fixture.
    internal static void Run(Application app)
    {
        app.Dispatcher.VerifyAccess();
        // Dynamic application resources must only be consumed on this fixture's STA.
        CheckLive();
        CheckRegion();
        CheckLayout();
        var service=new ConnectionService(new Resolver(),()=>Epoch,(_,_)=>throw new InvalidOperationException("Widget must not record history."));
        service.Configure(false,new(41,-87));service.Start();
        try
        {
            var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            typeof(UiSession).GetField("<Settings>k__BackingField",All)!.SetValue(session,new UserSettings {ConnectionsAnimation=false});
            typeof(UiSession).GetField("<Connections>k__BackingField",All)!.SetValue(session,service);
            var peer=new ConnectionPeer(new(1,1,"Fixture"),"",0,"8.8.8.8",443,ConnectionProtocol.Tcp);
            var traffic=new ConnectionTraffic(peer,Epoch,Epoch,100,50,128,64);
            service.TryRecord(new ConnectionSample(Epoch,0,1,"fixture",[traffic],[],null,true,true,0,0,0,"Current synthetic capture"));
            Assert.True(SpinWait.SpinUntil(()=>service.Live.Connections[0].Location is not null,TimeSpan.FromSeconds(3)));
            var widget=new ConnectionsWidget(session);
            var owner=new Window {Content=widget};
            using var source=new OffscreenSource(widget);Pump();
            var map=Assert.Single(widget.Children.OfType<ConnectionMap>());
            Assert.True(map.IsHitTestVisible);Assert.True(map.Focusable);
            foreach(string name in new[]{"Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Daylight"})
            foreach(var size in new[]{new Size(600,150),new Size(600,300),new Size(300,171)})
            {
                Theme.Apply(name);Pump();widget.Refresh();
                Layout(widget,size.Width,size.Height);
                Assert.Equal(size.Height,widget.ActualHeight);Assert.Equal(size.Height,map.ActualHeight);Assert.Equal(size.Width,map.ActualWidth);
                CheckMapOnly(widget,map);
                Assert.Equal(1,map.DisplayedPointCount);Assert.False(map.IsAnimating);
                Render(widget,(int)size.Width,(int)size.Height,$"widget-map-{name}-{size.Width}x{size.Height}");
            }
            if(!SystemParameters.HighContrast)
            {
                Theme.SetGraphColors(new Dictionary<string,string>{{"download","#123456"},{"upload","#ABCDEF"}});
                widget.Refresh();Layout(widget,284,150);Render(widget,284,150,"widget-map-custom-colors");
                var colors=Drawings(VisualTreeHelper.GetDrawing(map)!).SelectMany(d=>new[]{d.Brush,d.Pen?.Brush}).OfType<SolidColorBrush>().Select(b=>b.Color).ToArray();
                Assert.Contains((Color)ColorConverter.ConvertFromString("#123456"),colors);
                Assert.Contains((Color)ColorConverter.ConvertFromString("#ABCDEF"),colors);
            }
            {
                Pump();session.Settings.ConnectionsAnimation=true;widget.Refresh();
                Assert.True(widget.IsLoaded);Assert.True(map.IsAnimating);
                widget.Visibility=Visibility.Collapsed;Assert.False(map.IsAnimating);
                widget.Visibility=Visibility.Visible;widget.Refresh();Assert.True(map.IsAnimating);
                service.TryRecord(new ConnectionSample(Epoch,0,2,"fixture",[traffic with {DownloadBytesPerSecond=0,UploadBytesPerSecond=0}],[],null,true,true,0,0,0,"Quiet fixture"));widget.Refresh();Assert.True(map.IsAnimating);Assert.Equal(1,map.DisplayedPointCount);
                service.TryRecord(new ConnectionSample(Epoch,0,3,"fixture",[traffic],[],null,false,false,0,0,0,"Stopped capture"));widget.Refresh();Assert.True(map.IsAnimating);
                session.Settings.ConnectionsAnimation=false;widget.Refresh();Assert.False(map.IsAnimating);
            }
            owner.Close();
            // Exercise the production restore boundary on a real WPF Window, without an HWND.
            foreach(uint dpi in new uint[]{96,144,192})
            {
                var window=new Window();NativePlacement.SetScrollableMinimumHeight(window,670);
                typeof(NativePlacement).GetMethod("ApplyScrollableMinimum",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[window,new WindowPlacement(10,10,300,200,false),new WorkArea[]{new(0,0,1600,480)},dpi]);
                Assert.Equal(480/(dpi/96d),window.MinHeight);
            }
        }
        finally {service.StopAsync().GetAwaiter().GetResult();Theme.SetGraphColors(new Dictionary<string,string>());Theme.Apply("Midnight");}
    }

    private sealed class RegionalResolver:IConnectionGeolocation
    {
        internal static readonly GeoPoint[] Cities=[new(47.61,-122.33),new(37.77,-122.42),new(39.74,-104.99),new(41.88,-87.63),new(38.91,-77.04),new(40.71,-74.01),new(45.50,-73.57),new(19.43,-99.13)];
        public ConnectionLocation? Lookup(string address)=>int.TryParse(address.Split('.')[^1],out int city)&&city is >=1 and <=8?new("NA","Fixture",null,"Synthetic city",Cities[city-1],"fixture"):null;
    }

    private static void CheckRegion()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-widget-region-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(directory,"settings.json");
        var service=new ConnectionService(new RegionalResolver(),()=>Epoch,(_,_)=>throw new InvalidOperationException("Widget must not record history."));
        service.Configure(false,new(41.88,-87.63));service.Start();
        Window? owner=null;
        try
        {
            var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            void Set(string name,object value)=>typeof(UiSession).GetField(name,All)!.SetValue(session,value);
            Set("<Settings>k__BackingField",new UserSettings {ConnectionsAnimation=false,WidgetConnectionsMap=true});Set("<Connections>k__BackingField",service);
            Set("layout",new DetachedWindowState([]));Set("writer",new SettingsSnapshotWriter(path));
            var traffic=Enumerable.Range(1,8).Select(i=>new ConnectionTraffic(new(new(i,i,"Synthetic city"),"",0,"8.8.8."+i,443,ConnectionProtocol.Tcp),Epoch,Epoch,100,50,128,64)).ToArray();
            void Publish(int sequence)=>service.TryRecord(new ConnectionSample(Epoch,0,sequence,"fixture",traffic,[],null,true,true,0,0,0,"Synthetic regional capture"));
            Publish(1);PumpUntil(()=>service.Live.Connections.All(c=>c.Location is not null));
            var widget=new ConnectionsWidget(session);
            owner=new Window {Content=widget};
            using var source=new OffscreenSource(widget);Pump();Layout(widget,284,150);
            var map=Assert.Single(widget.Children.OfType<ConnectionMap>());
            var button=Assert.Single(widget.Children.OfType<Button>());
            Assert.Equal("NA",button.Content);Assert.True(button.Focusable);Assert.True(KeyboardNavigation.GetIsTabStop(button));
            Assert.Contains("North America",AutomationProperties.GetName(button));Assert.Contains("North America",button.ToolTip!.ToString());
            Assert.True(map.IsHitTestVisible);Assert.True(map.Focusable);
            Assert.Equal(1,map.Camera.Zoom);
            var mainPage=new ConnectionsPage(session);var mainMap=(ConnectionMap)typeof(ConnectionsPage).GetField("map",All)!.GetValue(mainPage)!;var mainCamera=mainMap.Camera;
            GeoPoint[] points=RegionalResolver.Cities;
            Point[] Centers()
            {
                Layout(widget,284,150);
                var centers=Drawings(VisualTreeHelper.GetDrawing(map)!).Where(d=>d.Geometry is EllipseGeometry e&&e.RadiusX==3).Select(d=>((EllipseGeometry)d.Geometry).Center).ToArray();
                Assert.Equal(points.Length,centers.Length);
                return centers;
            }
            double Spread(bool horizontal)
            {
                var centers=Centers();
                return horizontal?centers.Max(p=>p.X)-centers.Min(p=>p.X):centers.Max(p=>p.Y)-centers.Min(p=>p.Y);
            }
            double Separation()
            {
                var centers=Centers();
                return centers.SelectMany((a,i)=>centers.Skip(i+1).Select(b=>(a-b).Length)).Min();
            }
            double worldWidth=Spread(true),worldHeight=Spread(false),worldSeparation=Separation();int changes=0;
            session.Changed+=()=>{changes++;widget.Refresh();};
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(Region(session.Settings));Assert.Equal(1,changes);Assert.True(map.Camera.Zoom>1);
            Assert.True(Spread(true)>worldWidth*2.4);Assert.True(Spread(false)>worldHeight*2.4);
            Assert.True(Separation()>worldSeparation*2.4);
            var regionalCamera=map.Camera;
            Assert.All(points,p=>{var projected=ConnectionProjection.Project(p,regionalCamera);Assert.InRange(projected.X,-.95,.95);Assert.InRange(projected.Y,-.95,.95);});
            Assert.True(Region(ReadSavedSettings(session,path)));
            long revision=(long)typeof(UiSession).GetField("saveRevision",All)!.GetValue(session)!;
            typeof(UiSession).GetMethod("SetWidgetConnectionsNorthAmerica")!.Invoke(session,[true]);
            Assert.Equal(1,changes);Assert.Equal(revision,typeof(UiSession).GetField("saveRevision",All)!.GetValue(session));
            Publish(2);widget.Refresh();Assert.Equal(regionalCamera,map.Camera);
            var london=ConnectionAssets.Cities.First(c=>c.Name=="London");
            session.SetConnectionsHomeCity(london.Id);Assert.Equal(london.Point,service.Live.Origin);Assert.Equal(regionalCamera,map.Camera);
            widget.Visibility=Visibility.Collapsed;widget.Visibility=Visibility.Visible;widget.Refresh();Assert.Equal(regionalCamera,map.Camera);
            CheckHistoryIsolation(session,service,widget,"Synthetic regional capture");Assert.Equal(regionalCamera,map.Camera);
            foreach(string theme in Theme.Names)
            foreach(var size in new[]{new Size(284,150),new Size(584,134),new Size(584,284)})
            {
                Theme.Apply(theme);Pump();widget.Refresh();Layout(widget,size.Width,size.Height);
                Assert.Equal(regionalCamera,map.Camera);CheckRegionButton(widget,button);
                Assert.Equal(((SolidColorBrush)Theme.Brush("Accent")).Color,((SolidColorBrush)button.BorderBrush).Color);
                Assert.Equal(((SolidColorBrush)Theme.Brush("Ink")).Color,((SolidColorBrush)button.Foreground).Color);
                Assert.Equal(((SolidColorBrush)Theme.Brush("Control")).Color,((SolidColorBrush)button.Background).Color);
                Render(widget,(int)size.Width,(int)size.Height,$"widget-region-{theme}-{size.Width}x{size.Height}");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(widget,size.Width,size.Height);
                Assert.Equal(1,map.Camera.Zoom);CheckMapOnly(widget,map);
                Render(widget,(int)size.Width,(int)size.Height,$"widget-world-{theme}-{size.Width}x{size.Height}");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(regionalCamera,map.Camera);
            }
            var savedSettings=ReadSavedSettings(session,path);Assert.True(Region(savedSettings));
            Set("<Settings>k__BackingField",savedSettings);
            var restored=new ConnectionsWidget(session);Assert.Equal(regionalCamera,Assert.Single(restored.Children.OfType<ConnectionMap>()).Camera);
            Assert.Contains("World",Assert.Single(restored.Children.OfType<Button>()).ToolTip!.ToString());
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.False(Region(session.Settings));Assert.Equal(1,map.Camera.Zoom);Assert.Equal(0,map.Camera.Latitude);
            Assert.False(Region(ReadSavedSettings(session,path)));
            // The real routed handlers own zoom/pan; refresh must not recenter either mode.
            foreach(bool northAmerica in new[]{false,true})
            {
                session.SetWidgetConnectionsNorthAmerica(northAmerica);widget.Refresh();
                var preset=map.Camera;
                var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120){RoutedEvent=Mouse.MouseWheelEvent};
                map.RaiseEvent(wheel);Assert.True(wheel.Handled);Assert.True(map.Camera.Zoom>preset.Zoom);
                var pan=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(map)!,0,Key.Right){RoutedEvent=Keyboard.KeyDownEvent};
                map.RaiseEvent(pan);Assert.True(pan.Handled);Assert.NotEqual(preset.Longitude,map.Camera.Longitude);
                var custom=map.Camera;
                Publish(northAmerica?4:3);widget.Refresh();Assert.Equal(custom,map.Camera);
                session.SetConnectionsHomeCity(northAmerica?london.Id:null);widget.Refresh();Assert.Equal(custom,map.Camera);
                widget.Visibility=Visibility.Collapsed;widget.Visibility=Visibility.Visible;widget.Refresh();Assert.Equal(custom,map.Camera);
                var home=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(map)!,0,Key.Home){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                map.RaiseEvent(home);Assert.True(home.Handled);Assert.Equal(northAmerica?2.5:1,map.Camera.Zoom);
                if(northAmerica){Assert.Equal(43,map.Camera.Latitude);Assert.Equal(-103,map.Camera.Longitude);}
            }
            Assert.Equal(mainCamera,mainMap.Camera);
        }
        finally {owner?.Close();service.StopAsync().GetAwaiter().GetResult();Theme.Apply("Midnight");if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    private static bool Region(UserSettings settings)
    {
        using var saved=JsonDocument.Parse(SettingsCodec.Write(settings));
        return saved.RootElement.TryGetProperty("WidgetConnectionsNorthAmerica",out var region)&&region.GetBoolean();
    }

    private static UserSettings ReadSavedSettings(UiSession session,string path)
    {
        var writer=(SettingsSnapshotWriter)typeof(UiSession).GetField("writer",All)!.GetValue(session)!;
        var gate=typeof(SettingsSnapshotWriter).GetField("gate",All)!.GetValue(writer)!;
        var savedRevision=typeof(SettingsSnapshotWriter).GetField("savedRevision",All)!;
        long requested=(long)typeof(UiSession).GetField("saveRevision",All)!.GetValue(session)!;
        // A matching older snapshot is insufficient: the latest atomic replacement must finish.
        PumpUntil(()=>
        {
            if(!Monitor.TryEnter(gate))return false;
            try {return (long)savedRevision.GetValue(writer)!>=requested;}
            finally {Monitor.Exit(gate);}
        });
        var saved=SettingsCodec.Read(File.ReadAllText(path));Assert.Null(saved.Error);return saved.Settings;
    }

    private static void CheckRegionButton(ConnectionsWidget widget,Button button)
    {
        var bounds=Bounds(button,widget);Assert.InRange(bounds.Width,28,40);Assert.InRange(bounds.Height,22,30);
        Assert.InRange(bounds.Right,widget.ActualWidth-10,widget.ActualWidth-3);Assert.InRange(bounds.Top,3,10);
        Assert.InRange(bounds.Bottom,0,widget.ActualHeight);
        Assert.All(widget.Children.OfType<UIElement>(),e=>Assert.True(e.IsHitTestVisible));
        Assert.IsType<ConnectionMap>(widget.InputHitTest(new Point(10,widget.ActualHeight-10)));
        Assert.Contains(Elements(button),e=>ReferenceEquals(e,widget.InputHitTest(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2))));
        FocusManager.SetIsFocusScope(widget,true);FocusManager.SetFocusedElement(widget,button);
        Assert.Same(button,FocusManager.GetFocusedElement(widget));
    }

    internal static void CheckSettings(UiSession session,Panel settings,string path)
    {
        var choice=Assert.Single(settings.Children.OfType<WrapPanel>().SelectMany(p=>p.Children.OfType<CheckBox>()),c=>c.Content?.ToString()=="Connections map (live)");
        Assert.False(choice.IsChecked);Assert.False(session.Settings.WidgetConnectionsMap);
        choice.IsChecked=true;Assert.True(session.Settings.WidgetConnectionsMap);
        Assert.True(ReadSavedSettings(session,path).WidgetConnectionsMap);
        session.SetWidgetConnectionsMap(false);Assert.False(choice.IsChecked);
        Assert.False(ReadSavedSettings(session,path).WidgetConnectionsMap);
    }

    private static void CheckLayout()
    {
        var service=new ConnectionService(new Resolver(),()=>Epoch,(_,_)=>throw new InvalidOperationException());
        service.Configure(false,null);service.Start();
        try
        {
            var settings=new UserSettings {WidgetConnectionsMap=true,WidgetConnectionsNorthAmerica=true,WidgetGraphs=[],ConnectionsAnimation=false,OpenWindows=["widget"],WidgetAlwaysOnTop=false};
            var saved=new WindowPlacement(20,30,340,390,false);settings.Windows["widget"]=saved;
            var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            void Set(object target,string name,object value)=>target.GetType().GetField(name,All)!.SetValue(target,value);
            Set(session,"<Settings>k__BackingField",settings);Set(session,"<Connections>k__BackingField",service);
            Set(session,"captureWindows",new Dictionary<Window,Action>());
            var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
            Set(collector,"clock",new Stopwatch());Set(collector,"<History>k__BackingField",new SampleHistory());Set(session,"<Collector>k__BackingField",collector);
            var window=new WidgetWindow(session);
            var content=(FrameworkElement)window.Content;
            Layout(content,300,171);
            var map=Assert.Single(Elements(content).OfType<ConnectionsWidget>());
            Assert.True(Assert.Single(map.Children.OfType<ConnectionMap>()).Camera.Zoom>1);
            Assert.Equal(210,window.MinHeight);Assert.Equal(240,window.Height);Assert.Equal(300,window.MinWidth);
            Assert.False(window.Topmost);Assert.Equal(saved,settings.Windows["widget"]);
            var viewport=new Border();viewport.SetResourceReference(Border.BackgroundProperty,"Background");window.Content=null;viewport.Child=content;window.Content=viewport;
            using var source=new OffscreenSource(viewport);Pump();
            // The remembered widget can consume fresh shared publications without constructing a dashboard.
            var peer=new ConnectionPeer(new(1,1,"Fixture"),"",0,"8.8.8.8",443,ConnectionProtocol.Tcp);
            service.TryRecord(new ConnectionSample(Epoch,0,1,"fixture",[new(peer,Epoch,Epoch,10,5,7,3)],[],null,true,true,0,0,0,"Widget-only live"));
            PumpUntil(()=>service.Live.Connections[0].Location is not null);Changed(session);
            Assert.Contains("Widget-only live",Assert.Single(map.Children.OfType<ConnectionMap>()).StatusSummary);
            CheckHistoryIsolation(session,service,map);
            foreach(string theme in new[]{"Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Daylight"})
            foreach(int count in new[]{0,1,4})
            {
                Theme.Apply(theme);Pump();settings.WidgetGraphs=WidgetGraphSelection.Kinds.Take(count).ToList();Changed(session);
                double height=count==0?171:260;
                Layout(viewport,300,height);
                Assert.Equal(count==0?height-16:150,map.ActualHeight);Assert.Equal(Visibility.Visible,map.Visibility);
                CheckMapOnly(map,Assert.Single(map.Children.OfType<ConnectionMap>()));
                if(count==0)Assert.Equal(284,map.ActualWidth);
                var graphs=Elements(content).OfType<WidgetGraph>().Where(g=>g.Visibility==Visibility.Visible).ToArray();
                Assert.Equal(count,graphs.Length);Assert.All(graphs,g=>Assert.True(g.ActualHeight>=120));
                var scroll=Assert.Single(Elements(content).OfType<ScrollViewer>());
                Assert.Equal(count==0?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto,scroll.VerticalScrollBarVisibility);
                if(count==0)
                {
                    Assert.Equal(0,scroll.ScrollableHeight);
                    foreach(var size in new[]{new Size(600,300),new Size(300,171),new Size(600,150)})
                    {
                        Layout(viewport,size.Width,size.Height);
                        Assert.Equal(size.Height-16,map.ActualHeight);Assert.Equal(size.Width-16,map.ActualWidth);
                        CheckMapOnly(map,Assert.Single(map.Children.OfType<ConnectionMap>()));
                        Render(viewport,(int)size.Width,(int)size.Height,$"widget-viewport-{theme}-{size.Width}x{size.Height}");
                    }
                    Layout(viewport,300,height);
                }
                if(count>0)
                {
                    var mapBounds=Bounds(map,viewport);
                    Assert.All(graphs,g=>Assert.True(Bounds(g,viewport).Bottom<=mapBounds.Top+.01,"The map must be below every selected graph."));
                    foreach(var graph in graphs)ScrollToAndCheck(graph,scroll,viewport,300,height);
                    if(count==4)Render(viewport,300,(int)height,$"widget-layout-{theme}-4-last-graph");
                    Assert.True(scroll.ScrollableHeight>0);scroll.ScrollToBottom();Pump();Layout(viewport,300,height);
                    CheckFullyVisible(map,scroll,viewport);
                    Render(viewport,300,(int)height,$"widget-layout-{theme}-{count}-bottom");scroll.ScrollToTop();Pump();Layout(viewport,300,height);
                }
                Render(viewport,300,(int)height,$"widget-layout-{theme}-{count}");
            }
            foreach(string theme in new[]{"Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Daylight"})
            foreach(uint dpi in new uint[]{96,144,192})
            {
                Theme.Apply(theme);Pump();
                typeof(NativePlacement).GetMethod("ApplyScrollableMinimum",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[window,new WindowPlacement(10,10,340,390,false),new WorkArea[]{new(0,0,1600,480)},dpi]);
                Assert.Equal(480/(dpi/96d),window.MinHeight);
                double clientHeight=window.MinHeight-39;Layout(viewport,300,clientHeight);
                var scroll=Assert.Single(Elements(content).OfType<ScrollViewer>());
                Assert.True(scroll.ScrollableHeight>0);
                var graphs=Elements(content).OfType<WidgetGraph>().Where(g=>g.Visibility==Visibility.Visible).ToArray();
                foreach(var graph in graphs)ScrollToAndCheck(graph,scroll,viewport,300,clientHeight);
                Render(viewport,300,(int)clientHeight,$"widget-short-{theme}-{dpi}-last-graph");
                scroll.ScrollToBottom();Pump();Layout(viewport,300,clientHeight);CheckFullyVisible(map,scroll,viewport);
                Render(viewport,300,(int)clientHeight,$"widget-short-{theme}-{dpi}-map");
            }
            settings.WidgetConnectionsMap=false;settings.WidgetGraphs=[];Changed(session);Layout(viewport,300,171);
            Assert.Equal(Visibility.Collapsed,map.Visibility);
            Assert.Contains(Elements(content).OfType<TextBlock>(),t=>t.Visibility==Visibility.Visible&&t.Text.StartsWith("No graphs selected"));
            settings.WidgetGraphs=WidgetGraphSelection.Kinds.ToList();Changed(session);
            Assert.Equal(520,window.MinHeight);
            window.Close();
            settings.WidgetConnectionsMap=true;
            var reopened=new WidgetWindow(session);
            Layout((FrameworkElement)reopened.Content,300,260);
            Assert.True(Assert.Single(Elements((DependencyObject)reopened.Content).OfType<ConnectionMap>()).Camera.Zoom>1);
            reopened.Close();
        }
        finally {service.StopAsync().GetAwaiter().GetResult();Theme.Apply("Midnight");}
    }

    private static void CheckHistoryIsolation(UiSession session,ConnectionService service,ConnectionsWidget widget,string liveStatus="Widget-only live")
    {
        var widgetMap=Assert.Single(widget.Children.OfType<ConnectionMap>());var camera=widgetMap.Camera;
        var page=new ConnectionsPage(session);
        T Field<T>(string name)=>(T)typeof(ConnectionsPage).GetField(name,All)!.GetValue(page)!;
        void Call(string name)=>typeof(ConnectionsPage).GetMethod(name,All)!.Invoke(page,null);
        Field<ComboBox>("mode").SelectedIndex=1;
        Field<TextBox>("search").Text="Saved different endpoint";Field<TextBox>("country").Text="JP";
        Field<ComboBox>("direction").SelectedItem=ConnectionDirection.Sent;
        var query=new ConnectionHistoryQuery(Epoch.AddMinutes(-2),Epoch);
        page.ApplyHistory(new(query,[],0,new(99999,88888,0,0,0,0),[],[],[],null,0,"Historical playback fixture"));
        Call("TogglePlayback");Call("AdvancePlayback");
        var live=service.Live;Changed(session);
        var map=Assert.Single(widget.Children.OfType<ConnectionMap>());
        Assert.Contains(liveStatus,map.StatusSummary);Assert.DoesNotContain("Historical",map.StatusSummary);
        Assert.Contains(liveStatus,AutomationProperties.GetName(widget));
        Assert.Equal(liveStatus=="Widget-only live"?7:128,live.Connections[0].Traffic.DownloadBytesPerSecond);
        Assert.Equal(camera,map.Camera);
        Assert.Equal(live,service.Live);Call("StopPlayback");
    }

    private sealed class OffscreenSource:PresentationSource,IDisposable
    {
        private Visual? root;
        public OffscreenSource(Visual visual) {AddSource();RootVisual=visual;}
        public override Visual RootVisual {get=>root!;set {var old=root;root=value;RootChanged(old,root);}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
        public void Dispose() {RootVisual=null!;RemoveSource();}
    }

    private static void Changed(UiSession session)=>((Action)typeof(UiSession).GetField("Changed",All)!.GetValue(session)!).Invoke();
    private static IEnumerable<DependencyObject> Elements(DependencyObject element)
    {
        yield return element;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)foreach(var child in Elements(VisualTreeHelper.GetChild(element,i)))yield return child;
    }
    private static void Layout(FrameworkElement element,double width,double height) {element.Measure(new(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();}
    private static Rect Bounds(FrameworkElement element,Visual ancestor)=>element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static void CheckFullyVisible(FrameworkElement element,ScrollViewer scroll,FrameworkElement viewport)
    {
        var bounds=Bounds(element,viewport);var clip=Bounds(scroll,viewport);
        Assert.InRange(bounds.Left,clip.Left-.01,clip.Right);Assert.InRange(bounds.Right,clip.Left,clip.Right+.01);
        Assert.InRange(bounds.Top,clip.Top-.01,clip.Bottom);Assert.InRange(bounds.Bottom,clip.Top,clip.Bottom+.01);
    }
    private static void ScrollToAndCheck(FrameworkElement element,ScrollViewer scroll,FrameworkElement viewport,double width,double height)
    {
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset+Bounds(element,viewport).Top-Bounds(scroll,viewport).Top);Pump();Layout(viewport,width,height);
        CheckFullyVisible(element,scroll,viewport);
    }
    private static void Pump() {var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private static void PumpUntil(Func<bool> done) {var timer=Stopwatch.StartNew();while(!done()&&timer.Elapsed<TimeSpan.FromSeconds(3)){Pump();Thread.Sleep(1);}Assert.True(done());}

    private static IEnumerable<GeometryDrawing> Drawings(Drawing drawing)=>drawing is GeometryDrawing geometry?[geometry]:drawing is DrawingGroup group?group.Children.SelectMany(Drawings):[];
    private static int GlyphRuns(Drawing drawing)=>drawing is GlyphRunDrawing?1:drawing is DrawingGroup group?group.Children.Sum(GlyphRuns):0;
    private static void CheckMapOnly(ConnectionsWidget widget,ConnectionMap map)
    {
        Assert.Empty(Elements(map).OfType<TextBlock>());
        Assert.All(Elements(widget).OfType<TextBlock>(),text=>Assert.Equal("NA",text.Text));
        CheckRegionButton(widget,Assert.Single(widget.Children.OfType<Button>()));
        var drawing=VisualTreeHelper.GetDrawing(map)!;
        Assert.Equal(0,GlyphRuns(drawing));
        if(map.Camera.Zoom>1)return;
        var world=Assert.Single(Drawings(drawing),d=>d.Geometry is RectangleGeometry&&d.Pen is not null).Geometry.Bounds;
        Assert.InRange(world.Width,map.ActualWidth*.9,map.ActualWidth);
        Assert.InRange(world.Height,map.ActualHeight*.9,map.ActualHeight);
        Assert.InRange(world.Left,0,map.ActualWidth*.05);Assert.InRange(world.Top,0,map.ActualHeight*.05);
        Assert.InRange(world.Right,map.ActualWidth*.95,map.ActualWidth);Assert.InRange(world.Bottom,map.ActualHeight*.95,map.ActualHeight);
    }
    private static void Render(FrameworkElement element,int width,int height,string name)
    {
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        string? output=Environment.GetEnvironmentVariable("DEADWARE_UI_OUTPUT");
        if(output is null&&name.StartsWith("widget-"))output=Environment.GetEnvironmentVariable("DEADWARE_WIDGET_REGION_OUTPUT");
        if(output is null&&(name.StartsWith("widget-layout-")||name.StartsWith("widget-viewport-")||name.StartsWith("widget-short-")))output=Environment.GetEnvironmentVariable("DEADWARE_WIDGET_LAYOUT_OUTPUT");
        if(output is null)return;
        Directory.CreateDirectory(output);using var stream=File.Create(Path.Combine(output,name+".png"));
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
    }

    private static void CheckLive()
    {
        DateTimeOffset clock=Epoch;int writes=0;
        var service=new ConnectionService(new Resolver(),()=>clock,(_,_)=>writes++);
        service.Configure(false,null);service.Start();
        try
        {
            var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            typeof(UiSession).GetField("<Settings>k__BackingField",All)!.SetValue(session,new UserSettings {ConnectionsAnimation=true});
            typeof(UiSession).GetField("<Connections>k__BackingField",All)!.SetValue(session,service);
            var widget=new ConnectionsWidget(session);
            var map=Assert.Single(widget.Children.OfType<ConnectionMap>());
            Assert.Null(widget.ToolTip);Assert.Contains("Drag to move",map.ToolTip!.ToString());
            Assert.True(map.Flat);
            Assert.Equal(0,map.Camera.Latitude);
            Assert.False(map.AnimationEnabled);
            Assert.Contains("Waiting",map.StatusSummary);
            service.Configure(false,new(41,-87));
            var peer=new ConnectionPeer(new(1,1,"Fixture"),"",0,"8.8.8.8",443,ConnectionProtocol.Tcp);
            var traffic=new ConnectionTraffic(peer,Epoch,Epoch,999999,888888,128,64);
            service.TryRecord(new ConnectionSample(Epoch,0,1,"fixture",[traffic,traffic with {Peer=peer with {RemoteAddress="192.168.1.1"},DownloadBytesPerSecond=1,UploadBytesPerSecond=0}],[],null,true,false,0,0,0,"Fixture live capture"));
            Assert.True(SpinWait.SpinUntil(()=>service.Live.Connections[0].Location is not null,TimeSpan.FromSeconds(3)));
            widget.Refresh();
            Assert.Equal(1,map.DisplayedPointCount);
            Assert.Empty(Elements(map).OfType<TextBlock>());
            string text=AutomationProperties.GetName(widget);
            Assert.Contains("live",text);Assert.DoesNotContain("B/s",text);
            Assert.Equal(129,service.Live.Connections.Sum(c=>c.Traffic.DownloadBytesPerSecond));
            Assert.Equal(64,service.Live.Connections.Sum(c=>c.Traffic.UploadBytesPerSecond));
            Assert.Contains("Incomplete",map.StatusSummary);Assert.Contains("Fixture live capture",map.StatusSummary);
            Assert.False(map.AnimationEnabled);
            clock=Epoch.AddMinutes(1);widget.Refresh();
            Assert.Contains("stale",map.StatusSummary,StringComparison.OrdinalIgnoreCase);
            Assert.False(service.Live.Available);Assert.Contains("stale",AutomationProperties.GetName(widget),StringComparison.OrdinalIgnoreCase);
            Assert.Empty(widget.Children.OfType<DataGrid>());Assert.Empty(widget.Children.OfType<ComboBox>());
        }
        finally {service.StopAsync().GetAwaiter().GetResult();}
        Assert.Equal(0,writes);
    }
}
