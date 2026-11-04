using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Per-core CPU colour/style settings on UiSession: persistence, reset and apply-to-all. Needs the fixture Application for Theme resources.</summary>
internal static class CpuCoreStyleUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    internal static void Run()
    {
        string settingsPath=Path.Combine(Path.GetTempPath(),"DeadWare-tests",Guid.NewGuid().ToString("N"),"settings.json");
        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        void Set(string field,object value)=>typeof(UiSession).GetField(field,All)!.SetValue(session,value);
        Set("<Settings>k__BackingField",new UserSettings());Set("settingsPath",settingsPath);
        Set("writer",new SettingsSnapshotWriter(settingsPath));Set("layout",new DetachedWindowState(null));
        int changes=0;session.Changed+=()=>changes++;
        UserSettings Saved(){CompleteSettingsSave(session);return SettingsCodec.Read(File.ReadAllText(settingsPath)).Settings;}
        try
        {
            session.SetGraphColor("cpucore/3","#abcdef");
            Assert.Equal("#ABCDEF",session.Settings.GraphColors["cpucore/3"]);Assert.Equal(1,changes);
            Assert.Equal("#ABCDEF",Saved().GraphColors["cpucore/3"]);
            session.SetGraphColor("cpucore/64","#ABCDEF");session.SetGraphColor("cpucore/00","#ABCDEF");
            Assert.Single(session.Settings.GraphColors);Assert.Equal(1,changes);
            session.SetGraphStyle("cpucore/3","dashed",true,2,1.5);
            Assert.Equal(new GraphLineStyle("dashed",true,2,1.5),session.Settings.GraphStyles["cpucore/3"]);Assert.Equal(2,changes);
            Assert.Equal(new GraphLineStyle("dashed",true,2,1.5),Saved().GraphStyles["cpucore/3"]);
            session.SetGraphStyle("cpucore/64","dashed",true);Assert.Single(session.Settings.GraphStyles);
            session.SetGraphColor("cpu","#123456");session.SetGraphStyle("cpu","dotted",false,1.5);
            Assert.Equal(4,changes);
            session.ResetCpuCoreStyles();
            Assert.Equal(5,changes);
            Assert.Equal(["cpu"],session.Settings.GraphColors.Keys);Assert.Equal(["cpu"],session.Settings.GraphStyles.Keys);
            var saved=Saved();Assert.Equal(["cpu"],saved.GraphColors.Keys);Assert.Equal(["cpu"],saved.GraphStyles.Keys);
            session.ResetCpuCoreStyles();Assert.Equal(5,changes);
            session.ApplyStyleToAllCores(4,true);
            Assert.Equal(6,changes);
            Assert.Equal(5,session.Settings.GraphColors.Count);Assert.Equal(5,session.Settings.GraphStyles.Count);
            for(int i=0;i<4;i++)
            {
                Assert.Equal("#123456",session.Settings.GraphColors[GraphColorSettings.CoreKey(i)]);
                Assert.Equal(new GraphLineStyle("dotted",false,1.5),session.Settings.GraphStyles[GraphColorSettings.CoreKey(i)]);
            }
            Assert.False(session.Settings.GraphColors.ContainsKey("cpucore/4"));
            saved=Saved();Assert.Equal(5,saved.GraphColors.Count);Assert.Equal(new GraphLineStyle("dotted",false,1.5),saved.GraphStyles["cpucore/3"]);
            session.ApplyStyleToAllCores(4,true);Assert.Equal(6,changes);
            // Default CPU style and no CPU colour: cores drop back to theme defaults.
            session.SetGraphColor("cpu",null);session.SetGraphStyle("cpu","default",false);Assert.Equal(8,changes);
            session.ApplyStyleToAllCores(2,true);
            Assert.Equal(9,changes);
            Assert.Equal(["cpucore/2","cpucore/3"],session.Settings.GraphColors.Keys.Order());Assert.Equal(["cpucore/2","cpucore/3"],session.Settings.GraphStyles.Keys.Order());
            session.SetGraphColor("cpu","#654321");
            session.ApplyStyleToAllCores(1,false);// default CPU style, no core/0 entry: nothing to change
            Assert.Equal(10,changes);
            Assert.Equal(["cpu","cpucore/2","cpucore/3"],session.Settings.GraphColors.Keys.Order());Assert.Equal(["cpucore/2","cpucore/3"],session.Settings.GraphStyles.Keys.Order());
            session.ApplyStyleToAllCores(100,true);
            Assert.Equal(65,session.Settings.GraphColors.Count);Assert.Empty(session.Settings.GraphStyles);Assert.Equal(11,changes);
            session.ApplyStyleToAllCores(-5,true);Assert.Equal(11,changes);
            session.ResetCpuCoreStyles();Assert.Equal(["cpu"],session.Settings.GraphColors.Keys);Assert.Empty(session.Settings.GraphStyles);
        }
        finally {session.SetGraphColor("cpu",null);Theme.SetGraphColors(new Dictionary<string,string>());Directory.Delete(Path.GetDirectoryName(settingsPath)!,true);}
    }
    /// <summary>The CPU card's colour button opens the popup without cores and the CpuCoreStyleWindow with them; the window's cards and bulk buttons.</summary>
    internal static void RunWindow()
    {
        string settingsPath=Path.Combine(Path.GetTempPath(),"DeadWare-tests",Guid.NewGuid().ToString("N"),"settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        UiSession Session(int cores)
        {
            var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
            var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
            typeof(Collector).GetField("latest",All)!.SetValue(collector,new Reading(new(DateTimeOffset.UtcNow,0,new Dictionary<string,double?>()),[],[],[],"CPU","","","",false){CpuCoreCount=cores});
            void Set(string field,object value)=>typeof(UiSession).GetField(field,All)!.SetValue(session,value);
            Set("<Collector>k__BackingField",collector);Set("<Settings>k__BackingField",new UserSettings());Set("settingsPath",settingsPath);
            Set("writer",new SettingsSnapshotWriter(settingsPath));Set("layout",new DetachedWindowState(null));Set("detached",new Dictionary<string,Window>());
            return session;
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;
        }
        static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Picker(UiSession session){var row=(Panel)GraphColorPicker.Create(session,"cpu");return row.Children.OfType<Button>().Single();}
        static FrameworkElement Prepare(FrameworkElement view){view.Measure(new Size(640,760));view.Arrange(new Rect(0,0,640,760));view.UpdateLayout();return view;}
        try
        {
            // (a) cores off: today's popup, no window.
            var session=Session(4);Window? shown=null;session.ShowWindow=w=>shown=w;
            var picker=Picker(session);new Window {Content=picker};Prepare(picker);
            Assert.Equal("Change CPU usage color and line style",picker.ToolTip);
            var popup=(System.Windows.Controls.Primitives.Popup)typeof(GraphColorPicker).GetField("popup",All)!.GetValue(picker)!;
            Click(picker);
            Assert.Null(shown);Assert.True(popup.IsOpen);popup.IsOpen=false;
            Assert.Single(Descendants(popup.Child).OfType<Button>(),b=>b.Content as string=="Theme default");
            // (b) cores on: the window via the test hook.
            session.SetCpuCoreUsage(true);
            Click(picker);
            Assert.False(popup.IsOpen);var window=Assert.IsType<CpuCoreStyleWindow>(shown);
            Assert.Equal("Change CPU and core line colors and styles",picker.ToolTip);Assert.Equal(picker.ToolTip,AutomationProperties.GetName(picker));
            Assert.Equal("CPU core lines — DeadWare",window.Title);
            // (c) one card per core, named Core 1..4.
            var root=Prepare((FrameworkElement)window.Content);
            var cards=Descendants(root).OfType<FrameworkElement>().Where(e=>AutomationProperties.GetName(e)?.StartsWith("Core ",StringComparison.Ordinal)==true).Select(e=>AutomationProperties.GetName(e)).ToArray();
            Assert.Equal(["Core 1","Core 2","Core 3","Core 4"],cards);
            Assert.Equal(4,Descendants(root).OfType<Button>().Count(b=>b.Content as string=="Auto tint"));
            Assert.Single(Descendants(root).OfType<Button>(),b=>b.Content as string=="Theme default");
            Assert.DoesNotContain(Descendants(root).OfType<TextBlock>(),t=>t.Text=="Per-core CPU usage unavailable.");
            // (d) apply style and colour to all cores.
            session.SetGraphColor("cpu","#123456");session.SetGraphStyle("cpu","dashed",true,2,1.5);
            Click(Descendants(root).OfType<Button>().Single(b=>b.Content as string=="Apply style and color to all cores"));
            for(int i=0;i<4;i++)
            {
                Assert.Equal("#123456",session.Settings.GraphColors[GraphColorSettings.CoreKey(i)]);
                Assert.Equal(new GraphLineStyle("dashed",true,2,1.5),session.Settings.GraphStyles[GraphColorSettings.CoreKey(i)]);
            }
            Assert.False(session.Settings.GraphColors.ContainsKey("cpucore/4"));
            // Row editors re-sync: every core's Dashed radio is now checked.
            Assert.Equal(5,Descendants(root).OfType<RadioButton>().Count(r=>r.Content as string=="Dashed"&&r.IsChecked==true));
            // (e) reset clears them; CPU stays.
            Click(Descendants(root).OfType<Button>().Single(b=>b.Content as string=="Reset all cores to auto"));
            Assert.Equal(["cpu"],session.Settings.GraphColors.Keys);Assert.Equal(["cpu"],session.Settings.GraphStyles.Keys);
            Assert.Equal(1,Descendants(root).OfType<RadioButton>().Count(r=>r.Content as string=="Dashed"&&r.IsChecked==true));
            window.Close();
            // Zero cores: the unavailable note instead of the list.
            var empty=new CpuCoreStyleWindow(Session(0));
            root=Prepare((FrameworkElement)empty.Content);
            Assert.Contains(Descendants(root).OfType<TextBlock>(),t=>t.Text=="Per-core CPU usage unavailable.");
            Assert.DoesNotContain(Descendants(root).OfType<Button>(),b=>b.Content as string=="Auto tint");
            empty.Close();
        }
        finally {Theme.SetGraphColors(new Dictionary<string,string>());Directory.Delete(Path.GetDirectoryName(settingsPath)!,true);}
    }
    private static void CompleteSettingsSave(UiSession session)
    {
        var save=session.SaveAsync();
        var frame=new System.Windows.Threading.DispatcherFrame();
        var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = save.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);save.GetAwaiter().GetResult();
    }
}
