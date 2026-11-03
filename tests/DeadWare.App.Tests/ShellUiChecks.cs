using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Shell chrome: vector navigation rail, one-row header toolbar and grouped Settings cards, on the shared STA fixture's dashboard.</summary>
internal static class ShellUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly string[] SettingsCards=["Monitoring","Appearance","Widget","Pop-outs","Startup","History and alerts"];

    internal static void Run(MainWindow dashboard)
    {
        var shell=(Grid)dashboard.Content;
        shell.Measure(new Size(1220,900));shell.Arrange(new Rect(0,0,1220,900));shell.UpdateLayout();
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,All)!.GetValue(dashboard)!;
        void Navigate(string route)=>typeof(MainWindow).GetMethod("Navigate",All)!.Invoke(dashboard,[route]);

        // Rail: ten vector buttons, no glyph characters, exactly one active after navigating.
        var navigation=Field<Dictionary<string,Button>>("navigation");
        Assert.Equal(11,navigation.Count);
        var order=navigation.Keys.ToList();
        Assert.Equal(order.IndexOf("network")+1,order.IndexOf("storage"));
        Assert.True(order.IndexOf("network")>=0);
        foreach(var (route,button) in navigation)
        {
            Assert.NotEmpty(Descendants(button).OfType<Path>());
            var label=Assert.Single(Descendants(button).OfType<TextBlock>());
            Assert.DoesNotContain("▦",label.Text);Assert.DoesNotContain("⇄",label.Text);
            Assert.Equal(label.Text,AutomationProperties.GetName(button));
            Assert.Equal(route=="battery",label.Text=="Battery history");
        }
        Navigate("connections");
        var active=Assert.Single(navigation,pair=>Equals(pair.Value.Tag,"active"));
        Assert.Equal("connections",active.Key);
        Navigate("overview");
        Assert.Equal("active",navigation["overview"].Tag);Assert.NotEqual("active",navigation["connections"].Tag);

        // Header: one DockPanel holds the title block, the range pill, the GPU picker and the widget button.
        var header=Assert.IsType<DockPanel>(Field<TextBlock>("heading").Parent is StackPanel titleBlock?titleBlock.Parent:null);
        Assert.Single(Descendants(header).OfType<RangeSegments>());
        Assert.Contains(Descendants(header).OfType<ComboBox>(),box=>ReferenceEquals(box,Field<ComboBox>("gpu")));
        Assert.Contains(Descendants(header).OfType<Button>(),button=>button.Content?.ToString()=="↗ Open widget");
        var subtitle=Field<TextBlock>("subtitle");
        Assert.True(subtitle.FontSize<=12);Assert.False(string.IsNullOrWhiteSpace(subtitle.Text));
        Assert.Equal(26,Field<TextBlock>("heading").FontSize);

        // Table headers are small uppercase captions (spec: 10 px uppercase, Muted).
        foreach(var grid in new[]{Field<DataGrid>("apps"),Field<DataGrid>("sensors")})
            Assert.All(grid.Columns,column=>Assert.Equal(column.Header!.ToString()!.ToUpperInvariant(),column.Header.ToString()));
        // Settings: the existing controls grouped into titled cards.
        var pages=Field<Dictionary<string,FrameworkElement>>("pages");
        Navigate("desktop");shell.UpdateLayout();
        var cardStyle=Application.Current.Resources["Card"];
        var cards=Descendants(pages["desktop"]).OfType<Border>().Where(border=>ReferenceEquals(border.Style,cardStyle)).ToArray();
        Assert.True(cards.Length>=5,$"Settings has {cards.Length} cards.");
        var titles=cards.Select(card=>Descendants(card).OfType<TextBlock>().First().Text).ToArray();
        foreach(string title in SettingsCards)Assert.Contains(title,titles);
        Assert.Contains(Descendants(pages["desktop"]).OfType<TextBlock>(),block=>ReferenceEquals(block,Field<TextBlock>("startupStatus")));
        // Help text sits in the card of the control it explains.
        string CardOf(string prefix)
        {
            var block=Descendants(pages["desktop"]).OfType<TextBlock>().First(b=>b.Text.StartsWith(prefix,StringComparison.Ordinal));
            var card=cards.Single(c=>Descendants(c).Contains(block));
            return Descendants(card).OfType<TextBlock>().First().Text;
        }
        Assert.Equal("Startup",CardOf("At Windows sign-in"));Assert.Equal("Startup",CardOf("Windows asks for administrator"));
        Assert.Equal("History and alerts",CardOf("Keeps day, week and month history"));Assert.Equal("History and alerts",CardOf("Record endpoint history"));
        Assert.Equal("History and alerts",CardOf("Rules you set on the Alerts page"));Assert.Equal("Widget",CardOf("Choose the graphs shown in the widget"));
        Assert.Equal("Pop-outs",CardOf("Positions, sizes and Always on top"));Assert.Equal("Monitoring",CardOf("Includes motherboard"));
        // Per-core CPU usage: a Monitoring checkbox that drives the session setting.
        const string coreName="Show per-core CPU usage on usage graphs";
        var coreBox=Descendants(pages["desktop"]).OfType<CheckBox>().Single(box=>AutomationProperties.GetName(box)==coreName);
        Assert.Equal("Monitoring",Descendants(cards.Single(c=>Descendants(c).Contains(coreBox))).OfType<TextBlock>().First().Text);
        Assert.Equal("Monitoring",CardOf("One thin line per core"));
        var session=Field<UiSession>("session");int changes=0;void OnChanged()=>changes++;session.Changed+=OnChanged;
        try
        {
            bool before=session.Settings.ShowCpuCoreUsage;
            coreBox.IsChecked=!before;
            Assert.Equal(!before,session.Settings.ShowCpuCoreUsage);Assert.True(changes>=1);
            coreBox.IsChecked=before;
            Assert.Equal(before,session.Settings.ShowCpuCoreUsage);
        }
        finally{session.Changed-=OnChanged;}
        // Dashboard status line: unavailable notice when the switch is on and no cores were read.
        {
            var collector=session.Collector;var latestField=collector.GetType().GetField("latest",All)!;
            var previous=latestField.GetValue(collector);bool switchBefore=session.Settings.ShowCpuCoreUsage;
            var history=new SampleHistory();history.Add(new(DateTimeOffset.UtcNow,0,new Dictionary<string,double?>{["cpu/usage"]=10}));
            var status=Field<TextBlock>("status");var refresh=typeof(MainWindow).GetMethod("Refresh",All)!;
            try
            {
                latestField.SetValue(collector,new Reading(history.Read()[^1],[new("gpu","Fixture GPU")],[],[],"Fixture CPU","","","",false){CpuCoreCount=0});
                session.Settings.ShowCpuCoreUsage=true;refresh.Invoke(dashboard,null);
                Assert.Contains("Per-core CPU usage unavailable.",status.Text);
                session.Settings.ShowCpuCoreUsage=false;refresh.Invoke(dashboard,null);
                Assert.DoesNotContain("Per-core CPU usage unavailable.",status.Text);
            }
            finally{latestField.SetValue(collector,previous);session.Settings.ShowCpuCoreUsage=switchBefore;}
        }
        Navigate("overview");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var grandchild in Descendants(child))yield return grandchild;
        }
    }
}
