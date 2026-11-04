using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static void CompleteSettingsSave(UiSession session)
    {
        var save=session.SaveAsync();
        var frame=new System.Windows.Threading.DispatcherFrame();
        var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _ = save.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)),TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);save.GetAwaiter().GetResult();
    }
}
