using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using DeadWare.App;
using DeadWare.Core;
using DeadWare.UpdateCore;

// Disposable visual playground. It loads the production view and styles, but never
// constructs UiSession/Collector normally: no hardware, ETW, real settings or databases.
internal static class Program
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly=typeof(UiSession).Assembly;
    private static readonly string[] Themes=["Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Daylight"];
    private static readonly string Scratch=Path.Combine(Path.GetTempPath(),"DeadWare-GlassPreview",Guid.NewGuid().ToString("N"));
    private static UiSession session=null!;
    private static Collector collector=null!;
    private static readonly Stopwatch Clock=new();
    private static string state="Sample readings";
    private static void Set(object target,string field,object? value)=>target.GetType().GetField(field,All)!.SetValue(target,value);
    private static object? Call(string type,string method,params object[] args)=>AppAssembly.GetType("DeadWare.App."+type)!.GetMethod(method,All)!.Invoke(null,args);
    private static object? Invoke(object target,string method,params object[] args)=>target.GetType().GetMethod(method,All)!.Invoke(target,args);
    private static T Field<T>(object target,string field)=>(T)target.GetType().GetField(field,All)!.GetValue(target)!;
    private static void Notify()=>((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var source=XDocument.Load(Path.Combine(AppContext.BaseDirectory,"Fixtures","App.xaml"));
            XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources=new XElement(ns+"ResourceDictionary",new XAttribute(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),new XAttribute(XNamespace.Xmlns+"local","clr-namespace:DeadWare.App;assembly=DeadWare"),source.Root!.Element(ns+"Application.Resources")!.Elements());
            foreach(var color in resources.Descendants().Attributes("Color").Where(a=>a.Value.Contains("local:Theme.ScrollCorner")))color.Value="#151B24";
            app.Resources=(ResourceDictionary)XamlReader.Parse(resources.ToString());
            CreateSession();Call("Theme","Apply","Midnight");
            if(args.Length==2&&args[0]=="--render-shell")
            {
                RenderShell(args[1]);app.Shutdown();return 0;
            }
            var root=BuildView();
            if(args.Length==2&&args[0]=="--render")
            {
                RenderChecks(root,args[1]);app.Shutdown();return 0;
            }
            var window=new Window {Title="DeadWare · Glass UI preview",Content=root,Width=1060,Height=970,MinWidth=660,MinHeight=600,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            Call("Ui","Icon",window);
            var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
            timer.Tick+=(_,_)=>{UpdateSample();Notify();};
            window.Closed+=(_,_)=>{timer.Stop();foreach(var other in app.Windows.Cast<Window>().ToArray())other.Close();app.Shutdown();};
            Clock.Start();timer.Start();window.Show();app.Run();return 0;
        }
        catch(Exception error)
        {
            Directory.CreateDirectory(Scratch);File.WriteAllText(Path.Combine(Scratch,"error.txt"),error.ToString());
            if(args.Length==0)MessageBox.Show(error.ToString(),"Glass UI preview error");
            else Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CreateSession()
    {
        collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        Set(collector,"clock",Clock);Set(collector,"<History>k__BackingField",new SampleHistory());
        session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        Set(session,"<Collector>k__BackingField",collector);
        Set(session,"<Settings>k__BackingField",new UserSettings {HistorySeconds=300,SelectedGpuId="preview-gpu",ShowGpuFanSpeed=true,ConnectionsAnimation=false});
        Set(session,"<SettingsStatus>k__BackingField","");
        Set(session,"settingsPath",Path.Combine(Scratch,"settings.json"));Set(session,"writer",new SettingsSnapshotWriter(Path.Combine(Scratch,"settings.json")));
        Set(session,"layout",new DetachedWindowState([]));Set(session,"captureWindows",new Dictionary<Window,Action>());Set(session,"detached",new Dictionary<string,Window>());
        Set(session,"timer",new DispatcherTimer());
        // Constructors only allocate their local service state. None is started, configured,
        // queried or attached to the collector; no store or geolocation database is opened.
        var graphHistory=new GraphHistoryService(Path.Combine(Scratch,"graphs.db"),"synthetic-preview",collector.History);
        Set(graphHistory,"status","SYNTHETIC PREVIEW · graph history is generated in memory; no recordings are read or written.");
        Set(session,"<GraphHistory>k__BackingField",graphHistory);
        Set(session,"<Cooling>k__BackingField",new CoolingHistoryService(Path.Combine(Scratch,"cooling.db"),"synthetic-preview",session.Settings.CoolingConfigurationId));
        var battery=new BatteryHistoryService(Path.Combine(Scratch,"battery.db"),"synthetic-preview");
        Set(battery,"status","SYNTHETIC PREVIEW · sample observations only; recording is not running.");Set(session,"<Battery>k__BackingField",battery);
        var connections=new ConnectionService(Path.Combine(Scratch,"connections.db"));
        var sampleConnections=SampleConnections();
        Set(connections,"clock",new Func<DateTimeOffset>(()=>sampleConnections.Timestamp));
        Set(connections,"live",sampleConnections);Set(connections,"storageStatus","SYNTHETIC PREVIEW · recording is not running.");Set(session,"<Connections>k__BackingField",connections);
        var updates=new UpdateService(AppAssembly.GetName().Version!.ToString(3),install:Scratch,http:new HttpClient(new RejectNetwork()),cacheBase:Path.Combine(Scratch,"updates"));
        Set(updates,"state",new UpdateState(AppAssembly.GetName().Version!.ToString(3))
        {
            Status="SYNTHETIC PREVIEW · update checks and installation are not running.",LocalStatus="No installed app files or updater cache have been inspected.",
            ReleaseNotes=[new(new Version(0,0,0),"Glass UI · sample release notes","Frosted surfaces, gentle highlights and a shared palette across every page. These notes are synthetic preview content.")]
        });
        Set(session,"<Updates>k__BackingField",updates);
        var saveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(500)};
        saveTimer.Tick+=async (_,_)=>{saveTimer.Stop();await session.SaveAsync();};Set(session,"saveTimer",saveTimer);
        for(int second=-21600;second<0;second++)collector.History.Add(Sample(second));
        UpdateSample();
    }

    private static Snapshot Sample(double second)
    {
        double Wave(double basis,double amplitude,double period)=>basis+amplitude*Math.Sin(second/period)+amplitude*.2*Math.Sin(second/3);
        return new(DateTimeOffset.UtcNow.AddSeconds(second-Clock.Elapsed.TotalSeconds),second,new Dictionary<string,double?>
        {
            ["cpu/usage"]=Wave(34,19,17),["cpu/temperature"]=Wave(56,5,23),
            ["preview-gpu/usage"]=Wave(68,17,21),["preview-gpu/temperature"]=Wave(64,6,27),["preview-gpu/fan"]=Wave(1380,180,19),
            ["memory/usage"]=Wave(47,3,39),["memory/used"]=15.04e9,["memory/total"]=32e9,
            ["network/download"]=Wave(12.4e6,7e6,11),["network/upload"]=Wave(1.8e6,1e6,15)
        });
    }

    private static void UpdateSample()
    {
        double now=Clock.Elapsed.TotalSeconds;
        var sample=Sample(now);
        if(state=="Unavailable sensors")sample=sample with {Values=sample.Values.ToDictionary(p=>p.Key,p=>p.Key.Contains("temperature")||p.Key.EndsWith("/fan")?(double?)null:p.Value)};
        if(state=="Stale readings")sample=sample with {MonotonicSeconds=now-60};
        if(state!="Stale readings")collector.History.Add(sample);
        var sensors=new SensorReading[]
        {
            new("cpu/usage","Ryzen 7 5800X · Sample CPU","CPU total","%",sample.Values["cpu/usage"]),
            new("cpu/temperature","Ryzen 7 5800X · Sample CPU","Core temperature","°C",sample.Values["cpu/temperature"]),
            new("preview-gpu/usage","Radeon RX 6800 XT · Sample GPU","GPU core","%",sample.Values["preview-gpu/usage"]),
            new("preview-gpu/temperature","Radeon RX 6800 XT · Sample GPU","Core temperature","°C",sample.Values["preview-gpu/temperature"]),
            new("preview-gpu/fan","Radeon RX 6800 XT · Sample GPU","Fan","RPM",sample.Values["preview-gpu/fan"]),
            new("memory/usage","System memory · Sample","Memory usage","%",sample.Values["memory/usage"])
        };
        ProcessTraffic[] processes=[new(4280,1,"Sample browser",8.4e6,1.2e6,2_400_000_000L,84_000_000L),new(9312,1,"Sample sync",2.8e6,540e3,850_000_000L,120_000_000L),new(6408,1,"Sample music",1.2e6,60e3,180_000_000L,6_000_000L)];
        Set(collector,"latest",state=="Discovering hardware"?null:new Reading(sample,[new("preview-gpu","Radeon RX 6800 XT · Preview GPU")],sensors,processes,"Ryzen 7 5800X · Preview CPU","SYNTHETIC SAMPLE DATA · no hardware monitoring","Sample interface totals · no live network accounting","Sample process traffic · ETW is not running",false)
            {Battery=new(73,BatteryPowerState.Charging,false)});
    }

    private static TextBlock Text(string value,double size=13,string color="Ink")=>(TextBlock)Call("Ui","Text",value,size,color,"BodyFont")!;
    private static Border BuildView()
    {
        var frame=new Border {Padding=new Thickness(24,20,24,16)};frame.SetResourceReference(Border.BackgroundProperty,"WindowSurface");
        var layout=new DockPanel();frame.Child=layout;
        var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);layout.Children.Add(top);
        var label=Text("VISUAL PREVIEW  /  SAMPLE DATA",10,"Accent");label.Margin=new(6,0,0,14);top.Children.Add(label);
        var title=new DockPanel();
        var themes=new ComboBox {ItemsSource=Themes,SelectedItem="Midnight",Width=142,Margin=new(8,0,6,0),VerticalAlignment=VerticalAlignment.Center};
        AutomationProperties.SetName(themes,"Preview color theme");themes.SelectionChanged+=(_,_)=>{if(themes.SelectedItem is string name)session.SetTheme(name);};DockPanel.SetDock(themes,Dock.Right);title.Children.Add(themes);
        var heading=(TextBlock)Call("Ui","Heading","System overview",28d)!;heading.Margin=new(6,0,0,0);title.Children.Add(heading);top.Children.Add(title);
        var toolbar=new DockPanel {Margin=new(6,14,6,12)};
        var range=(ComboBox)Call("Ui","Range",session)!;DockPanel.SetDock(range,Dock.Right);toolbar.Children.Add(range);
        var gpuLabel=Text("GPU  ·  Radeon RX 6800 XT",12,"Muted");toolbar.Children.Add(gpuLabel);top.Children.Add(toolbar);
        var footer=new WrapPanel {Margin=new(6,12,6,0)};DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        var states=new ComboBox {ItemsSource=new[]{"Sample readings","Unavailable sensors","Stale readings","Discovering hardware"},SelectedIndex=0,Width=188};AutomationProperties.SetName(states,"Preview reading state");
        states.SelectionChanged+=(_,_)=>{state=(string)states.SelectedItem;UpdateSample();Notify();};footer.Children.Add(states);
        var fan=new CheckBox {Content="GPU fan",IsChecked=true,VerticalAlignment=VerticalAlignment.Center,Margin=new(14,0,10,0)};fan.Checked+=(_,_)=>session.SetGpuFanSpeed(true);fan.Unchecked+=(_,_)=>session.SetGpuFanSpeed(false);footer.Children.Add(fan);
        var hint=Text("Sample data only · your running monitor is unchanged",11,"Muted");hint.Margin=new(8,7,0,7);footer.Children.Add(hint);
        var view=(FrameworkElement)Activator.CreateInstance(AppAssembly.GetType("DeadWare.App.OverviewPage")!,All,null,[session,Text("Memory · 15.0 GB / 32.0 GB",11,"Muted")],null)!;
        layout.Children.Add(view);return frame;
    }

    private static void RenderChecks(Border root,string output)
    {
        Directory.CreateDirectory(output);
        var owner=new Window {Content=root};
        Call("Ui","Icon",owner);
        using var source=new OffscreenSource(root);Pump();
        var checks=new List<object>();
        foreach(string theme in Themes)
        foreach(int width in new[]{660,1000})
        {
            var themeChoice=Descendants(root).OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Preview color theme");
            themeChoice.SelectedItem=theme;Call("Theme","Apply",theme);Notify();Pump();
            root.Measure(new Size(width,1040));root.Arrange(new Rect(0,0,width,1040));root.UpdateLayout();Pump();
            if(!ReferenceEquals(root.Background,Application.Current.Resources["WindowSurface"]))throw new InvalidOperationException("Glass window surface did not update with the theme.");
            var charts=Descendants(root).OfType<FrameworkElement>().Where(e=>e.GetType().Name=="HistoryChart").ToArray();
            if(charts.Length!=6)throw new InvalidOperationException("Expected all six Overview charts.");
            foreach(var chart in charts)
            {
                if(chart.ActualWidth<160||chart.ActualHeight<65)throw new InvalidOperationException($"Chart area too small: {chart.RenderSize}");
                var drawing=VisualTreeHelper.GetDrawing(chart);
                if(drawing is null||!Shapes(drawing).Any(d=>d.Geometry is StreamGeometry&&d.Geometry.Bounds.Width>15))throw new InvalidOperationException("Fixture must render a continuous history trace.");
                if(drawing is not null&&drawing.Bounds.Right>chart.ActualWidth+1)throw new InvalidOperationException("Chart axis extends beyond its card.");
            }
            Save(root,width,1040,Path.Combine(output,$"overview-{theme}-{width}.png"));
            checks.Add(new {theme,width,charts=charts.Length,minChartHeight=charts.Min(c=>c.ActualHeight)});
        }
        Call("Theme","Apply","Midnight");
        foreach(string scenario in new[]{"Unavailable sensors","Stale readings","Discovering hardware"})
        {
            state=scenario;UpdateSample();Notify();Pump();root.UpdateLayout();
            string labels=string.Join(" ",Descendants(root).OfType<TextBlock>().Select(b=>b.Text));
            if(!labels.Contains('—'))throw new InvalidOperationException("Unknown readings must remain unavailable.");
            Save(root,1000,1040,Path.Combine(output,$"overview-{scenario.Replace(' ','-')}.png"));checks.Add(new {scenario,unknown=true});
        }
        owner.Content=null;owner.Close();
        File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new {Note="Production Overview with synthetic data. Offscreen layout at 96 DPI; no hardware collector, installed data or native popup interaction.",checks},new JsonSerializerOptions{WriteIndented=true}));
    }

    private static ConnectionLiveView SampleConnections()
    {
        var now=DateTimeOffset.UtcNow;
        (string Name,string Country,string Code,GeoPoint Point)[] locations=[("Sample London","United Kingdom","GB",new(51.5,-.12)),("Sample Toronto","Canada","CA",new(43.65,-79.38)),("Sample Seattle","United States","US",new(47.6,-122.33)),("Sample Frankfurt","Germany","DE",new(50.1,8.68))];
        var connections=locations.Select((location,index)=>new LocatedConnection(
            new(new(new(4280+index,1,"Sample browser"),"192.0.2.20",52000+index,$"203.0.113.{10+index}",443,ConnectionProtocol.Tcp),now.AddMinutes(-12),now,84_000_000+index*18_000_000,4_000_000+index*1_000_000,820_000+index*80_000,52_000+index*10_000),
            new(location.Code,location.Country,null,location.Name,location.Point,"Synthetic preview location"))).ToArray();
        return new(now,connections,new(41.88,-87.63),true,true,"SYNTHETIC PREVIEW · reserved example IPs and illustrative locations; no capture or lookup is running.");
    }

    private static void RenderShell(string output)
    {
        Directory.CreateDirectory(output);
        var lifetime=new ApplicationLifetime();
        var dashboard=new MainWindow(session,lifetime,()=>true,()=>{});
        // The real Settings route queries scheduled-task status when opened. Keep its
        // existing busy gate closed; rendering must never query or modify startup tasks.
        Set(dashboard,"startupBusy",true);
        var pages=Field<Dictionary<string,FrameworkElement>>(dashboard,"pages");
        var shell=(Grid)dashboard.Content;dashboard.Content=null;
        // Detached visuals do not receive Application resource invalidation through a
        // Window. Register the same live dictionary on this fixture root so every
        // DynamicResource (glass, fonts and text) follows the selected production theme.
        shell.Resources=Application.Current.Resources;
        shell.SetResourceReference(Panel.BackgroundProperty,"WindowSurface");
        var frame=shell;
        var checks=new List<object>();
        var failures=new List<string>();
        try
        {
            ApplySampleHistoryPages(pages);
            var themeChoice=Descendants(shell).OfType<ComboBox>().Single(combo=>combo.Items.Cast<object>().Select(item=>item.ToString()).SequenceEqual(Themes));
            foreach(string theme in Themes)
            {
                themeChoice.SelectedItem=theme;Call("Theme","Apply",theme);
                foreach(string route in new[]{"overview","network","connections","battery","sensors","cooling","desktop","updates"})
                {
                    Invoke(dashboard,"Navigate",route);
                    // No Window is shown. History/update pages never receive a presentation
                    // source, so their Loaded handlers cannot query stores or start timers.
                    if(route=="network") Field<DataGrid>(dashboard,"apps").ItemsSource=collector.Latest!.Processes.Select(p=>new
                    {p.Name,p.Pid,Download=FormatBytes(p.DownloadBytesPerSecond,true),Upload=FormatBytes(p.UploadBytesPerSecond,true),Received=FormatBytes(p.ReceivedBytes),Sent=FormatBytes(p.SentBytes)}).ToArray();
                    if(route=="sensors") Field<DataGrid>(dashboard,"sensors").ItemsSource=collector.Latest!.Sensors.Select(sensor=>
                    {
                        var summary=collector.History.Summarize(sensor.Id,0,session.HistorySeconds);
                        return new {Name=sensor.Hardware+" / "+sensor.Name,Current=FormatNumber(sensor.Value,sensor.Unit),Minimum=FormatNumber(summary.Minimum,sensor.Unit),Average=FormatNumber(summary.Average,sensor.Unit),Maximum=FormatNumber(summary.Maximum,sensor.Unit)};
                    }).ToArray();
                    Field<TextBlock>(dashboard,"status").Text="GLASS UI PREVIEW · SYNTHETIC SAMPLE DATA\nHardware, ETW, recordings, startup actions and update checks are not running.";
                    Field<TextBlock>(dashboard,"monitoringInfo").Text="SAMPLE DATA ONLY\nUnstarted preview session";
                    if(route=="desktop") Field<TextBlock>(dashboard,"startupStatus").Text="Preview only · startup registration has not been queried or changed.";
                    if(route is "overview" or "network") RefreshSampleCards(dashboard,pages[route]);
                    foreach(var size in route is "network" or "desktop" ? new[]{new Size(1220,900),new Size(900,640)} : new[]{new Size(1220,900)})
                    {
                    frame.Measure(size);frame.Arrange(new Rect(new Point(),size));frame.UpdateLayout();Pump();frame.UpdateLayout();
                    if(!ReferenceEquals(frame.Background,Application.Current.Resources["WindowSurface"]))throw new InvalidOperationException("Shell glass window surface did not update.");
                    if(pages.Values.Any(p=>p.IsLoaded)||new System.Windows.Interop.WindowInteropHelper(dashboard).Handle!=IntPtr.Zero)throw new InvalidOperationException("Shell rendering must remain unloaded and must not create a native window.");
                    var text=string.Join(" ",Descendants(shell).OfType<TextBlock>().Select(t=>t.Text));
                    if(!text.Contains("SYNTHETIC SAMPLE DATA"))throw new InvalidOperationException("Every shell capture must label synthetic data.");
                    if(Directory.Exists(Scratch)&&Directory.EnumerateFiles(Scratch,"*.db*",SearchOption.AllDirectories).Any())throw new InvalidOperationException("Preview unexpectedly created a history database.");
                    double? tableHeight=route=="network"?Field<DataGrid>(dashboard,"apps").ActualHeight:null;
                    if(tableHeight is <70) failures.Add($"{theme} Network table at {size.Width:0}x{size.Height:0} has {tableHeight:0.##} px height; at least 70 px is required.");
                    double? chartHeight=null;bool? chartTrace=null;
                    if(route=="network")
                    {
                        var chart=Descendants(pages[route]).OfType<FrameworkElement>().Single(e=>e.GetType().Name=="HistoryChart");
                        chartHeight=chart.ActualHeight;
                        var drawing=VisualTreeHelper.GetDrawing(chart);
                        chartTrace=drawing is not null&&Shapes(drawing).Any(shape=>shape.Pen is not null&&shape.Geometry is StreamGeometry&&shape.Geometry.Bounds.Width>15);
                        if(chartHeight is <65||chartTrace!=true)failures.Add($"{theme} Network graph at {size.Width:0}x{size.Height:0} has {chartHeight:0.##} px height and visible trace={chartTrace}; at least 65 px and a continuous sample trace are required.");
                    }
                    string suffix=size.Width==1220?"":$"-{size.Width:0}x{size.Height:0}";
                    Save(frame,(int)size.Width,(int)size.Height,Path.Combine(output,$"glass-shell-{theme}-{route}{suffix}.png"));
                    checks.Add(new {theme,route,width=size.Width,height=size.Height,tableHeight,chartHeight,chartTrace,synthetic=true,nativeWindow=false,storesOpened=false});
                    }
                }
            }
        }
        finally
        {
            lifetime.BeginSessionEnding();dashboard.Close();
        }
        File.WriteAllText(Path.Combine(output,"shell-checks.json"),JsonSerializer.Serialize(new
        {
            Note="Actual MainWindow and all eight production pages with in-memory synthetic fixtures; Network and Settings also at the supported 900x640 minimum. No hardware/ETW workers, installed settings, history queries, native windows, startup-task actions or network requests. Offscreen at 96 DPI; no native input/compositor/soak proof.",checks,failures
        },new JsonSerializerOptions {WriteIndented=true}));
        if(failures.Count>0)throw new InvalidOperationException(string.Join(Environment.NewLine,failures));
    }

    private static void RefreshSampleCards(MainWindow dashboard,FrameworkElement page)
    {
        // ChartCard deliberately skips Refresh while disconnected. Load only these
        // display-only graph routes through the existing offscreen fixture, then remove
        // the source before rendering the full shell. No history/update page is loaded.
        var host=Field<ContentControl>(dashboard,"content");host.Content=null;
        try
        {
            using(var source=new OffscreenSource(page))
            {
                page.Measure(new Size(960,700));page.Arrange(new Rect(0,0,960,700));page.UpdateLayout();Pump();
                foreach(var card in Descendants(page).OfType<FrameworkElement>().Where(e=>e.GetType().Name=="ChartCard"))
                {
                    Invoke(card,"Refresh");
                    string kind=Field<string>(card,"kind");
                    string value=kind=="network"?Field<System.Windows.Documents.Run>(card,"downloadValue").Text:Field<TextBlock>(card,"value").Text;
                    if(string.IsNullOrWhiteSpace(value)||value.Contains('—'))throw new InvalidOperationException($"Synthetic {kind} card did not display its available sample reading.");
                }
                page.UpdateLayout();
            }
            Pump();
        }
        finally { host.Content=page; }
    }

    private static void ApplySampleHistoryPages(IReadOnlyDictionary<string,FrameworkElement> pages)
    {
        var now=DateTimeOffset.UtcNow;
        var buckets=Enumerable.Range(0,1440).Select(index=>
        {
            var at=now.AddMinutes(index-1439);double percentage=52+index*.015+3*Math.Sin(index/180d);
            bool charging=index>1320,saver=index is >=180 and <360;
            return new BatteryHistoryBucket(at,at,percentage,percentage,percentage,1,1,charging?1:0,saver?1:0,1,"synthetic-preview");
        }).ToArray();
        Invoke(pages["battery"],"ApplyView",new BatteryHistoryView(now.AddDays(-1),now,buckets,buckets.Length,buckets[0].Start,262144,"SYNTHETIC PREVIEW · sample charge observations held in memory; no recording store is opened."));
        var device=new ThermalDevice("synthetic-cpu","Ryzen 7 5800X · Sample CPU","CPU",session.Settings.CoolingConfigurationId,"preview");
        var windows=Enumerable.Range(0,288).Select(index=>
        {
            var at=now.AddMinutes((index-288)*5);double temperature=55+6*Math.Sin(index/25d);
            return new ThermalWindow(device,"synthetic-preview",at,at.AddMinutes(5),index*300,300,300,new(300,temperature-2,temperature+2,temperature),new(300,25,60,42),new(300,35,80,58),new(300,3200,4600,4100),new(300,900,1500,1200),true,"Synthetic preview",300);
        }).ToArray();
        var reference=new ThermalReference("synthetic-reference",device.Id,device.ConfigurationId,"Sample reference · typical workload",now.AddDays(-2),windows[0].Start,windows[^1].End,windows);
        Invoke(pages["cooling"],"ApplyView",new CoolingView([device],device,windows,[reference],reference,
            new("Within reference range","SYNTHETIC PREVIEW · illustrative comparison with a sample reference. No cooling diagnosis has been made.",10,10,60,58,2,53,65,false),[],524288,"SYNTHETIC PREVIEW · in-memory thermal windows; recording is not running.",0));
        Invoke(pages["connections"],"ApplyLive",session.Connections.Live);
        Invoke(pages["updates"],"ApplyState",session.Updates.State);
    }
    private static string FormatNumber(double? value,string unit)=>value is { } number&&double.IsFinite(number)?$"{number:0.0} {unit}":"—";
    private static string FormatBytes(double? value,bool rate=false)=>(string)Call("Ui","Bytes",value!,rate)!;
    private sealed class RejectNetwork:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromException<HttpResponseMessage>(new InvalidOperationException("Network requests are disabled in the Glass UI preview."));
    }
    private static void Save(Visual root,int width,int height,string path)
    {var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static IEnumerable<GeometryDrawing> Shapes(Drawing drawing)
    {if(drawing is GeometryDrawing shape)yield return shape;if(drawing is DrawingGroup group)foreach(var child in group.Children)foreach(var nested in Shapes(child))yield return nested;}
    private static void Pump(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
    private sealed class OffscreenSource:PresentationSource,IDisposable
    {
        private Visual? root;
        public OffscreenSource(Visual visual){AddSource();RootVisual=visual;}
        public override Visual RootVisual {get=>root!;set{var old=root;root=value;RootChanged(old,root);}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
        public void Dispose(){RootVisual=null!;RemoveSource();}
    }
}
