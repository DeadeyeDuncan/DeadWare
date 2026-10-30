using System.Diagnostics;
using System.IO;
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

// Disposable visual playground. It loads the production view and styles, but never
// constructs UiSession/Collector normally: no hardware, ETW, real settings or databases.
internal static class Program
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly Assembly AppAssembly=typeof(UiSession).Assembly;
    private static readonly string[] Themes=["Midnight","Ember","Glacier","Amethyst","Neon","Blackout","Daylight"];
    private static readonly string Scratch=Path.Combine(Path.GetTempPath(),"DeadWare-OverviewPreview",Guid.NewGuid().ToString("N"));
    private static UiSession session=null!;
    private static Collector collector=null!;
    private static readonly Stopwatch Clock=new();
    private static string state="Sample readings";
    private static void Set(object target,string field,object? value)=>target.GetType().GetField(field,All)!.SetValue(target,value);
    private static object? Call(string type,string method,params object[] args)=>AppAssembly.GetType("DeadWare.App."+type)!.GetMethod(method,All)!.Invoke(null,args);
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
            var root=BuildView();
            if(args.Length==2&&args[0]=="--render")
            {
                RenderChecks(root,args[1]);app.Shutdown();return 0;
            }
            var window=new Window {Title="DeadWare · Raven-inspired Overview preview",Content=root,Width=1060,Height=970,MinWidth=660,MinHeight=600,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            Call("Ui","Icon",window);
            var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
            timer.Tick+=(_,_)=>{UpdateSample();Notify();};
            window.Closed+=(_,_)=>{timer.Stop();foreach(var other in app.Windows.Cast<Window>().ToArray())other.Close();app.Shutdown();};
            Clock.Start();timer.Start();window.Show();app.Run();return 0;
        }
        catch(Exception error)
        {
            Directory.CreateDirectory(Scratch);File.WriteAllText(Path.Combine(Scratch,"error.txt"),error.ToString());
            if(args.Length==0)MessageBox.Show(error.ToString(),"Overview preview error");
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
        Set(session,"<Settings>k__BackingField",new UserSettings {HistorySeconds=300,SelectedGpuId="preview-gpu",ShowGpuFanSpeed=true});
        Set(session,"<SettingsStatus>k__BackingField","");
        Set(session,"settingsPath",Path.Combine(Scratch,"settings.json"));Set(session,"writer",new SettingsSnapshotWriter(Path.Combine(Scratch,"settings.json")));
        Set(session,"layout",new DetachedWindowState([]));Set(session,"captureWindows",new Dictionary<Window,Action>());Set(session,"detached",new Dictionary<string,Window>());
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
        Set(collector,"latest",state=="Discovering hardware"?null:new Reading(sample,[new("preview-gpu","Radeon RX 6800 XT · Preview GPU")],[],[],"Ryzen 7 5800X · Preview CPU","","","",false));
    }

    private static TextBlock Text(string value,double size=13,string color="Ink")=>(TextBlock)Call("Ui","Text",value,size,color,"BodyFont")!;
    private static Border BuildView()
    {
        var frame=new Border {Padding=new Thickness(24,20,24,16)};frame.SetResourceReference(Border.BackgroundProperty,"Background");
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
            if(((SolidColorBrush)root.Background).Color!=((SolidColorBrush)Application.Current.Resources["Background"]).Color)throw new InvalidOperationException("Theme background did not update.");
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
