using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DeadWare.App;
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
        Assert.Equal(10,navigation.Count);
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

        // Settings: the existing controls grouped into titled cards.
        var pages=Field<Dictionary<string,FrameworkElement>>("pages");
        Navigate("desktop");shell.UpdateLayout();
        var cardStyle=Application.Current.Resources["Card"];
        var cards=Descendants(pages["desktop"]).OfType<Border>().Where(border=>ReferenceEquals(border.Style,cardStyle)).ToArray();
        Assert.True(cards.Length>=5,$"Settings has {cards.Length} cards.");
        var titles=cards.Select(card=>Descendants(card).OfType<TextBlock>().First().Text).ToArray();
        foreach(string title in SettingsCards)Assert.Contains(title,titles);
        Assert.Contains(Descendants(pages["desktop"]).OfType<TextBlock>(),block=>ReferenceEquals(block,Field<TextBlock>("startupStatus")));
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
