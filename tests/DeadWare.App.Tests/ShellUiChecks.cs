using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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

        // Rail: twelve vector buttons, no glyph characters, exactly one active after navigating.
        var navigation=Field<Dictionary<string,Button>>("navigation");
        Assert.Equal(12,navigation.Count);
        var order=navigation.Keys.ToList();
        Assert.True(order.IndexOf("network")>=0);
        Assert.Equal(order.IndexOf("network")+1,order.IndexOf("storage"));
        Assert.Equal(order.IndexOf("storage")+1,order.IndexOf("processes"));
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
        // Processes: its own page and heading, with the live toolbar collapsed (no history range).
        Navigate("processes");
        Assert.IsType<ProcessesPage>(Field<ContentControl>("content").Content);
        Assert.Equal("Processes",Field<TextBlock>("heading").Text);
        Assert.Equal(Visibility.Collapsed,Field<DockPanel>("liveToolbar").Visibility);
        Assert.Equal("Processes",AutomationProperties.GetName(navigation["processes"]));
        Navigate("overview");

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
        // The usage-history switch also keeps the busiest apps while Per-process monitoring is on.
        var usageHelp=Descendants(pages["desktop"]).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Keeps day, week and month history",StringComparison.Ordinal));
        Assert.EndsWith("for 30 days.",usageHelp.Text);
        Assert.EndsWith(" While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days.",usageHelp.Text);
        Assert.Contains("for up to 400 days",usageHelp.Text);
        Assert.Equal("History and alerts",CardOf("Rules you set on the Alerts page"));Assert.Equal("Widget",CardOf("Choose the graphs shown in the widget"));
        // The alert help names the first network use rule's learning period and keeps its Cooling Health ending.
        var alertHelp=Descendants(pages["desktop"]).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Rules you set on the Alerts page",StringComparison.Ordinal));
        Assert.EndsWith("Cooling Health keeps its own notification setting.",alertHelp.Text);
        Assert.Contains("The first network use rule learns your apps for 3 days",alertHelp.Text);
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
        // Storage sensors switch: a Monitoring checkbox bound to the session setting.
        {
            var box=Descendants(pages["desktop"]).OfType<CheckBox>().Single(b=>b.Content is string t&&t.StartsWith("Storage sensors"));
            Assert.Equal("Monitoring",Descendants(cards.Single(c=>Descendants(c).Contains(box))).OfType<TextBlock>().First().Text);
            bool was=session.Settings.StorageSensors;
            try{box.IsChecked=false;Assert.False(session.Settings.StorageSensors);box.IsChecked=true;Assert.True(session.Settings.StorageSensors);}
            finally{box.IsChecked=was;}
        }
        // Per-process monitoring switch: a Monitoring checkbox bound to the session setting, with its scan-cost help line.
        {
            const string processName="Per-process monitoring";
            var box=Descendants(pages["desktop"]).OfType<CheckBox>().Single(b=>AutomationProperties.GetName(b)==processName);
            Assert.Equal(processName,box.Content);
            Assert.Equal("Monitoring",Descendants(cards.Single(c=>Descendants(c).Contains(box))).OfType<TextBlock>().First().Text);
            Assert.Equal("Monitoring",CardOf("Turning it on adds one Windows process scan"));
            var help=Descendants(pages["desktop"]).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Turning it on adds one Windows process scan",StringComparison.Ordinal));
            Assert.Equal(ProcessCopy.ScanSentence(session.Current?.Elevated??Collector.IsElevated,ProcessFeatures.DiskColumns,ProcessFeatures.ScanLoop,history:true),help.Text);
            bool was=session.Settings.ProcessMonitoring;Assert.Equal(was,box.IsChecked);
            try
            {
                box.IsChecked=true;Assert.True(session.Settings.ProcessMonitoring);Assert.True(session.Collector.ProcessMonitoring);
                box.IsChecked=false;Assert.False(session.Settings.ProcessMonitoring);Assert.False(session.Collector.ProcessMonitoring);
                // The Processes page's Turn on calls the same session method; the checkbox follows it.
                session.SetProcessMonitoring(true);Assert.True(box.IsChecked);
                session.SetProcessMonitoring(false);Assert.False(box.IsChecked);
            }
            finally{box.IsChecked=was;}
        }
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
        CheckExcludedApps(cards,session);
        Navigate("overview");
    }

    /// <summary>Settings → History and alerts → Don't record these apps: copy, suggestions, adding, the three errors, the note and Remove with its focus.</summary>
    private static void CheckExcludedApps(Border[] cards,UiSession session)
    {
        const string Help="These apps still show on live pages, marked \"not recorded\", but none of their history is saved: no Connections history, no per-app totals in Usage history and no alerts for them. In Application resources their CPU, memory, I/O and GPU count under Everything else, so device totals stay true. Application traffic budgets for them stop counting. History saved before you add an app is kept until you clear it. Processes that Windows saves as \"PID n\" can't be matched.";
        const string Kept="Saved history for this app is kept. Use Clear history… to remove it.";
        var historyCard=cards.Single(c=>Descendants(c).OfType<TextBlock>().First().Text=="History and alerts");
        var block=Assert.Single(Descendants(historyCard).OfType<ExcludedAppsBlock>());
        // The block follows the Connections block in the same card.
        var history=Assert.IsType<StackPanel>(block.Parent);
        var connectionsOpen=history.Children.OfType<Button>().Single(b=>b.Content as string=="Open Connections controls");
        Assert.Equal(history.Children.IndexOf(connectionsOpen)+1,history.Children.IndexOf(block));
        var collector=session.Collector;var latestField=collector.GetType().GetField("latest",All)!;var previous=latestField.GetValue(collector);
        var listBefore=session.Settings.ExcludedApps.ToList();
        Assert.Empty(listBefore);
        void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Button[] Removes()=>block.List.Children.OfType<FrameworkElement>().SelectMany(LogicalButtons).ToArray();
        Button Remove(string name)=>Removes().Single(b=>AutomationProperties.GetName(b)==$"Remove {name} from Don't record these apps");
        try
        {
            latestField.SetValue(collector,new Reading(new(DateTimeOffset.UtcNow,0,new Dictionary<string,double?>()),[],[],[new ProcessTraffic(42,1,"fixture.exe",null,null,0,0)],"CPU","","","",false));
            // Showing Settings above already read the histories, so this refresh within the minute reads nothing new; clearing the
            // last read time makes it read again, and its completion offers the suggestions.
            Wait(block.Suggestions.RefreshAsync());
            typeof(AppSuggestions).GetField("lastRead",All)!.SetValue(block.Suggestions,null);
            Wait(block.Suggestions.RefreshAsync());
            // Exact strings.
            var heading=Assert.IsType<TextBlock>(block.Children[0]);
            Assert.Equal("Don't record these apps",heading.Text);Assert.Equal(16,heading.FontSize);
            Assert.Equal(Help,Assert.IsType<TextBlock>(block.Children[1]).Text);
            Assert.Equal("Add",block.Add.Content);
            Assert.True(block.Box.IsEditable);Assert.Equal("App to stop recording",AutomationProperties.GetName(block.Box));
            Assert.Equal("No apps are excluded.",Assert.IsType<TextBlock>(Assert.Single(block.List.Children)).Text);
            Assert.Equal(Visibility.Collapsed,block.Error.Visibility);Assert.Equal(Visibility.Collapsed,block.Note.Visibility);
            // Tab order: the box, then Add.
            var addRow=Assert.IsAssignableFrom<Panel>(block.Box.Parent);Assert.Same(addRow,block.Add.Parent);
            Assert.True(addRow.Children.IndexOf(block.Box)<addRow.Children.IndexOf(block.Add));
            // Adding from a suggestion: the finished refresh offers the current reading's app, and so does opening the drop-down (raised
            // directly; offscreen a popup would need a native window).
            Assert.Contains("fixture.exe",block.Box.Items.Cast<string>());
            typeof(ComboBox).GetMethod("OnDropDownOpened",All)!.Invoke(block.Box,[EventArgs.Empty]);
            Assert.Contains("fixture.exe",block.Box.Items.Cast<string>());
            block.Box.SelectedItem="fixture.exe";Assert.Equal("fixture.exe",block.Box.Text);
            Click(block.Add);
            Assert.Equal(["fixture.exe"],session.Settings.ExcludedApps);
            Assert.Equal("",block.Box.Text);
            Assert.Equal(Visibility.Visible,block.Note.Visibility);Assert.Equal(Kept,block.Note.Text);
            Assert.Equal(Visibility.Collapsed,block.Error.Visibility);
            // Suggestions leave listed apps out; the shared source can include them.
            Assert.DoesNotContain("fixture.exe",block.Box.Items.Cast<string>());
            Assert.DoesNotContain("fixture.exe",block.Suggestions.Names(includeListed:false));
            Assert.Contains("fixture.exe",block.Suggestions.Names(includeListed:true));
            // Typing and Enter adds too.
            block.Box.Text="Steam.exe";Press(block.Box,Key.Enter);
            Assert.Equal(["fixture.exe","Steam.exe"],session.Settings.ExcludedApps);
            Assert.Equal("",block.Box.Text);Assert.Equal(Visibility.Visible,block.Note.Visibility);
            // The three errors (Warning), each hiding the note and changing nothing.
            block.Box.Text="C:\\x.exe";Click(block.Add);
            Assert.Equal(Visibility.Visible,block.Error.Visibility);Assert.Equal("Enter an app's executable name, such as chrome.exe.",block.Error.Text);
            Assert.Equal(Visibility.Collapsed,block.Note.Visibility);
            Assert.Equal(["fixture.exe","Steam.exe"],session.Settings.ExcludedApps);
            block.Box.Text="STEAM.EXE";Click(block.Add);
            Assert.Equal("That app is already in the list.",block.Error.Text);
            Assert.Equal(["fixture.exe","Steam.exe"],session.Settings.ExcludedApps);
            session.SetExcludedApps(Enumerable.Range(0,64).Select(i=>$"app{i}.exe"));
            Assert.Equal(64,Removes().Length);
            block.Box.Text="new.exe";Click(block.Add);
            Assert.Equal("At most 64 apps can be listed.",block.Error.Text);
            Assert.Equal(64,session.Settings.ExcludedApps.Count);Assert.DoesNotContain("new.exe",session.Settings.ExcludedApps);
            session.SetExcludedApps(["fixture.exe"]);
            block.Box.Text="Steam.exe";Press(block.Box,Key.Enter);
            Assert.Equal(Visibility.Collapsed,block.Error.Visibility);Assert.Equal(Visibility.Visible,block.Note.Visibility);
            // The list: sorted, one Remove each, the first naming fixture.exe.
            Assert.Equal(["Remove fixture.exe from Don't record these apps","Remove Steam.exe from Don't record these apps"],Removes().Select(AutomationProperties.GetName));
            Assert.All(Removes(),b=>Assert.Equal("Remove",b.Content));
            // Remove: focus moves to the next row's Remove, then to the box once the list empties (logical focus; offscreen nothing takes keyboard focus).
            var scope=FocusManager.GetFocusScope(block);
            FocusManager.SetFocusedElement(scope,Remove("fixture.exe"));
            Click(Remove("fixture.exe"));
            Assert.Equal(["Steam.exe"],session.Settings.ExcludedApps);
            Assert.Same(Remove("Steam.exe"),FocusManager.GetFocusedElement(scope));
            Assert.Equal(Visibility.Collapsed,block.Note.Visibility);
            Click(Remove("Steam.exe"));
            Assert.Empty(session.Settings.ExcludedApps);
            Assert.Same(block.Box,FocusManager.GetFocusedElement(scope));
            Assert.Equal("No apps are excluded.",Assert.IsType<TextBlock>(Assert.Single(block.List.Children)).Text);
            Assert.Contains("fixture.exe",block.Box.Items.Cast<string>());
        }
        finally{latestField.SetValue(collector,previous);session.SetExcludedApps(listBefore);}
    }

    private static IEnumerable<Button> LogicalButtons(FrameworkElement root)
    {
        if(root is Button button){yield return button;yield break;}
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<FrameworkElement>())
            foreach(var found in LogicalButtons(child))yield return found;
    }
    /// <summary>Presses a key on an element as the keyboard does: the preview first, then the key itself unless it was handled.</summary>
    private static void Press(UIElement target,Key key)
    {
        var preview=new KeyEventArgs(Keyboard.PrimaryDevice,new KeySource(),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        target.RaiseEvent(preview);
        if(!preview.Handled)target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,new KeySource(),0,key){RoutedEvent=Keyboard.KeyDownEvent});
    }
    /// <summary>The input source a key event must name; it is never added to the sources, and offscreen nothing reads it.</summary>
    private sealed class KeySource : PresentationSource
    {
        public override Visual RootVisual {get=>null!;set{}}
        protected override CompositionTarget GetCompositionTargetCore()=>null!;
        public override bool IsDisposed=>false;
    }
    /// <summary>Runs the dispatcher until the task finishes (at most 10 s), then once more so its UI-thread continuations have run.</summary>
    private static void Wait(Task task)
    {
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(10);var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(5)};
        timer.Tick+=(_,_)=>{if(task.IsCompleted||DateTime.UtcNow>=deadline)frame.Continue=false;};timer.Start();
        try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        Assert.True(task.IsCompleted,"The suggestions' history reads did not finish within 10 s.");
        task.GetAwaiter().GetResult();
        var settle=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>settle.Continue=false));Dispatcher.PushFrame(settle);
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
