using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Typography helpers and the segmented history-range control, on the shared STA fixture.</summary>
internal static class RangeSegmentsUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;

    internal static void Run(Application app)
    {
        var metric=Ui.Metric("31.9 %",34);
        Assert.Equal(FontNumeralAlignment.Tabular,Typography.GetNumeralAlignment(metric));
        Assert.Equal(34,metric.FontSize);Assert.Equal(FontWeights.SemiBold,metric.FontWeight);
        var caption=Ui.Caption("min");
        Assert.Equal("MIN",caption.Text);Assert.True(caption.FontSize<11);

        var session=(UiSession)RuntimeHelpers.GetUninitializedObject(typeof(UiSession));
        var collector=(Collector)RuntimeHelpers.GetUninitializedObject(typeof(Collector));
        void Set(object target,string name,object value)=>target.GetType().GetField(name,All)!.SetValue(target,value);
        Set(collector,"clock",new Stopwatch());
        Set(session,"<Collector>k__BackingField",collector);
        var settings=new UserSettings {HistorySeconds=300};
        Set(session,"<Settings>k__BackingField",settings);
        var first=new RangeSegments(session);var second=new RangeSegments(session);
        var host=new StackPanel();host.Children.Add(first);host.Children.Add(second);
        host.Measure(new Size(600,200));host.Arrange(new Rect(0,0,600,200));host.UpdateLayout();
        // No window hosts the panel, so Loaded never fires on its own; the controls subscribe to the session there.
        first.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));second.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal("Graph history range",AutomationProperties.GetName(first));
        ToggleButton[] Buttons(DependencyObject root)=>Descendants(root).OfType<ToggleButton>().ToArray();
        var buttons=Buttons(first);
        Assert.Equal(6,buttons.Length);
        Assert.Equal(["1 min","5 min","15 min","1 h","3 h","6 h"],buttons.Select(b=>b.Content?.ToString()));
        var checkedButton=Assert.Single(buttons.Where(b=>b.IsChecked==true));
        Assert.Equal(300,checkedButton.Tag);
        // One shared setting; every control follows the session's Changed notification.
        settings.HistorySeconds=3600;
        ((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();
        Pump();
        Assert.Equal(3600,Assert.Single(Buttons(first).Where(b=>b.IsChecked==true)).Tag);
        Assert.Equal(3600,Assert.Single(Buttons(second).Where(b=>b.IsChecked==true)).Tag);
        // Focusing the control (the pop-out menu does this when it opens) lands on the checked segment so Left/Right and Escape work at once.
        // Offscreen there is no keyboard device, so the contract is observed through logical focus in a scope.
        FocusManager.SetIsFocusScope(host,true);
        first.FocusChecked();
        Assert.Same(Assert.Single(Buttons(first).Where(b=>b.IsChecked==true)),FocusManager.GetFocusedElement(host));
        Assert.False(first.Focusable,"Tab stops on the segments, not on the pill itself.");
    }

    private sealed class OffscreenSource : PresentationSource,IDisposable
    {
        private Visual? root;
        public OffscreenSource(Visual visual){AddSource();RootVisual=visual;}
        public override Visual RootVisual {get=>root!;set{var previous=root;root=value;RootChanged(previous,root);}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
        public void Dispose(){RootVisual=null!;RemoveSource();}
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var grandchild in Descendants(child))yield return grandchild;
        }
    }

    private static void Pump()
    {
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
        Dispatcher.PushFrame(frame);
    }
}
