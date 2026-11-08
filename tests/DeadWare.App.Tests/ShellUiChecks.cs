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

/// <summary>Shell chrome: vector navigation rail, one-row header toolbar and Settings categories, on the shared STA fixture's dashboard.</summary>
internal static class ShellUiChecks
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly string[] SettingsCategories=["Monitoring","Appearance","Widget","Pop-outs","Startup","History and alerts"];

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
        // Settings: one tab card, a normally collapsed status card, and the existing controls in six categories.
        var pages=Field<Dictionary<string,FrameworkElement>>("pages");
        var session=Field<UiSession>("session");
        typeof(MainWindow).GetField("startupBusy",All)!.SetValue(dashboard,true);
        Navigate("desktop");shell.UpdateLayout();
        var tabs=Assert.Single(Descendants(pages["desktop"]).OfType<TabControl>());
        Assert.Equal(SettingsCategories,tabs.Items.Cast<TabItem>().Select(tab=>tab.Header));
        Assert.Equal("Monitoring",Assert.IsType<TabItem>(tabs.SelectedItem).Header);
        Assert.Equal("Settings categories",AutomationProperties.GetName(tabs));
        var cardStyle=Application.Current.Resources["Card"];
        var tabCard=Assert.IsType<Border>(tabs.Parent);Assert.Same(cardStyle,tabCard.Style);Assert.Equal(new Thickness(16),tabCard.Padding);
        var settings=Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(pages["desktop"]).Content);
        Assert.Equal(2,settings.Children.Count);Assert.Same(tabCard,settings.Children[1]);
        StackPanel Select(string header){var tab=tabs.Items.Cast<TabItem>().Single(item=>Equals(item.Header,header));tabs.SelectedItem=tab;shell.UpdateLayout();return Assert.IsType<StackPanel>(tab.Content);}
        // This runs before History is ever visited: the mirror cannot depend on loaded progress TextBlocks.
        CheckSettingsStatus(dashboard,session,tabs,shell);
        Select("Startup");
        Assert.Contains(Descendants(tabs).OfType<TextBlock>(),block=>ReferenceEquals(block,Field<TextBlock>("startupStatus")));
        var repair=Assert.Single(Logical(Select("Startup")).OfType<Expander>());Assert.Equal("Repair startup registration",repair.Header);Assert.False(repair.IsExpanded);
        Assert.Equal(["Enable / repair startup","Disable startup","Refresh status"],Logical(repair).OfType<Button>().Select(button=>button.Content));
        Assert.Contains(Logical(repair).OfType<TextBlock>(),block=>block.Text.StartsWith("Windows asks for administrator",StringComparison.Ordinal));
        Assert.Contains(Logical(Select("Startup")).OfType<TextBlock>(),block=>block.Text=="At Windows sign-in, hide only the dashboard. Graphs and the widget still follow the reopen setting in Pop-outs. Manual launches show the dashboard.");
        var popouts=Select("Pop-outs");Assert.Equal("Your readings, wherever you need them",Assert.IsType<TextBlock>(popouts.Children[0]).Text);Assert.Equal(16,((TextBlock)popouts.Children[0]).FontSize);Assert.Equal(12,Assert.IsType<TextBlock>(popouts.Children[1]).FontSize);
        var sources=Assert.Single(Logical(Select("Monitoring")).OfType<Expander>());Assert.Equal("Sensor sources",sources.Header);Assert.False(sources.IsExpanded);
        Assert.Equal(2,Logical(sources).OfType<CheckBox>().Count());Assert.Contains(Logical(sources).OfType<CheckBox>(),box=>box.Content is string value&&value.StartsWith("Extended sensors",StringComparison.Ordinal));
        // Logical contents include collapsed folds; selection is restored after each location lookup.
        string TabOf(string prefix)
        {
            var selected=tabs.SelectedItem;
            try{foreach(var tab in tabs.Items.Cast<TabItem>()){tabs.SelectedItem=tab;shell.UpdateLayout();if(Logical((DependencyObject)tab.Content).OfType<TextBlock>().Any(block=>block.Text.StartsWith(prefix,StringComparison.Ordinal)))return (string)tab.Header;}throw new InvalidOperationException("Settings help not found: "+prefix);}
            finally{tabs.SelectedItem=selected;shell.UpdateLayout();}
        }
        Assert.Equal("Startup",TabOf("At Windows sign-in"));Assert.Equal("Startup",TabOf("Windows asks for administrator"));
        Assert.Equal("History and alerts",TabOf("Keeps day, week and month history"));Assert.Equal("History and alerts",TabOf("Record endpoint history"));
        // The usage-history switch also keeps the busiest apps while Per-process monitoring is on.
        var usageHelp=Descendants(Select("History and alerts")).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Keeps day, week and month history",StringComparison.Ordinal));
        Assert.EndsWith("for 30 days.",usageHelp.Text);
        Assert.EndsWith(" While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days.",usageHelp.Text);
        Assert.Contains("for up to 400 days",usageHelp.Text);
        Assert.Equal("History and alerts",TabOf("Rules you set on the Alerts page"));Assert.Equal("Widget",TabOf("Choose the graphs shown in the widget"));
        // The alert help names the first network use rule's learning period and keeps its Cooling Health ending.
        var alertHelp=Descendants(Select("History and alerts")).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Rules you set on the Alerts page",StringComparison.Ordinal));
        Assert.EndsWith("Cooling Health keeps its own notification setting.",alertHelp.Text);
        Assert.Contains("The first network use rule learns your apps for 3 days",alertHelp.Text);
        Assert.Equal("Pop-outs",TabOf("Positions, sizes and Always on top"));Assert.Equal("Monitoring",TabOf("Includes motherboard"));
        // Per-core CPU usage: a Monitoring checkbox that drives the session setting.
        const string coreName="Show per-core CPU usage on usage graphs";
        var coreBox=Descendants(Select("Monitoring")).OfType<CheckBox>().Single(box=>AutomationProperties.GetName(box)==coreName);
        Assert.Equal("Monitoring",TabOf("One thin line per core"));
        int changes=0;void OnChanged()=>changes++;session.Changed+=OnChanged;
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
            Select("Monitoring");sources.IsExpanded=true;shell.UpdateLayout();
            var box=Descendants(sources).OfType<CheckBox>().Single(b=>b.Content is string t&&t.StartsWith("Storage sensors"));
            bool was=session.Settings.StorageSensors;
            try{box.IsChecked=false;Assert.False(session.Settings.StorageSensors);box.IsChecked=true;Assert.True(session.Settings.StorageSensors);}
            finally{box.IsChecked=was;sources.IsExpanded=false;}
        }
        // Per-process monitoring switch: a Monitoring checkbox bound to the session setting, with its scan-cost help line.
        {
            const string processName="Per-process monitoring";
            var box=Descendants(Select("Monitoring")).OfType<CheckBox>().Single(b=>AutomationProperties.GetName(b)==processName);
            Assert.Equal(processName,box.Content);
            Assert.Equal("Monitoring",TabOf("Turning it on adds one Windows process scan"));
            var help=Descendants(Select("Monitoring")).OfType<TextBlock>().Single(b=>b.Text.StartsWith("Turning it on adds one Windows process scan",StringComparison.Ordinal));
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
        CheckExcludedApps(Select("History and alerts"),session);
        CheckClearHistory(Select("History and alerts"),session);
        Select("Monitoring");CheckSettingsKeyboard(tabs,tabCard);CheckSettingsStatusLifetime(session);
        Navigate("overview");
    }

    /// <summary>Unseen and unloaded History cannot suppress progress, terminal failures, or the route back to retry.</summary>
    private static void CheckSettingsStatus(MainWindow dashboard,UiSession session,TabControl tabs,FrameworkElement shell)
    {
        T Field<T>(string name)=>(T)typeof(MainWindow).GetField(name,All)!.GetValue(dashboard)!;
        var status=Field<TextBlock>("settingsStatus");var card=Field<Border>("settingsStatusCard");
        var history=tabs.Items.Cast<TabItem>().Single(tab=>Equals(tab.Header,"History and alerts"));var monitoring=tabs.SelectedItem;
        void Result(string value)=>typeof(MainWindow).GetMethod("SetStartupResult",All)!.Invoke(dashboard,[value]);
        void Registration(StartupRegistrationStatus value)=>typeof(MainWindow).GetMethod("ShowStartupStatus",All)!.Invoke(dashboard,[value]);
        void Changed()=>((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();
        void ClearChanged()=>((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged",All)!.GetValue(session))?.Invoke(HistoryClearStore.Usage);
        var before=session.Usage.ClearState;var worker=typeof(UsageHistoryService).GetField("worker",All)!;var oldWorker=worker.GetValue(session.Usage);
        var command=typeof(UsageHistoryService).GetField("clears",All)!.GetValue(session.Usage)!;var kept=command.GetType().GetField("kept",All)!;var oldKept=kept.GetValue(command);
        Assert.Equal(Visibility.Collapsed,card.Visibility);Assert.Equal("",status.Text);Assert.Equal(AutomationLiveSetting.Polite,AutomationProperties.GetLiveSetting(status));
        Assert.Same(card,Assert.IsType<StackPanel>(card.Parent).Children[0]);Assert.Equal(12,status.FontSize);Assert.Equal(TextWrapping.Wrap,status.TextWrapping);
        try
        {
            // The registration query completes with an error while Monitoring remains selected.
            Registration(new("fixture",false,false,false,false,null,"Startup status unavailable.","Access denied."));
            Assert.Same(monitoring,tabs.SelectedItem);Assert.Contains("Access denied.",status.Text);Assert.Equal(Visibility.Visible,card.Visibility);
            Registration(new("fixture",false,false,false,false,null,"Startup disabled."));Assert.Equal(Visibility.Collapsed,card.Visibility);
            Result("Startup repair needs attention.");Assert.Equal("Startup repair needs attention.",status.Text);Assert.Equal(Visibility.Visible,card.Visibility);
            session.Usage.ClearState=new(HistoryClearPhase.Queued,null,null,false,false);ClearChanged();
            Assert.Equal("Startup repair needs attention. · Clear history · Clearing history…",status.Text);
            Result("");Assert.Equal("Clear history · Clearing history…",status.Text);Assert.Same(monitoring,tabs.SelectedItem);
            // Repeated timer refreshes preserve the same text; percent-only updates do not change its announcement.
            var announcements=new List<string>();Action<string> announced=announcements.Add;dashboard.SettingsStatusAnnounced+=announced;
            try
            {
                Changed();Changed();session.Usage.ClearState=new(HistoryClearPhase.Clearing,35,null,true,false);Changed();
                Assert.Empty(announcements);
                // A new message announces once; the same message with another percentage does not.
                Result("Startup repair needs attention.");Assert.Single(announcements);
                session.Usage.ClearState=new(HistoryClearPhase.Clearing,60,null,true,false);Changed();Assert.Single(announcements);
                Result("");
            }
            finally{dashboard.SettingsStatusAnnounced-=announced;}
            // Visit History, then leave it. Its unloaded progress view is deliberately stale when the state changes.
            tabs.SelectedItem=history;shell.UpdateLayout();var progress=Assert.Single(Descendants((DependencyObject)history.Content).OfType<HistoryClearProgressView>());
            progress.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));tabs.SelectedItem=monitoring;shell.UpdateLayout();progress.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            session.Usage.ClearState=new(HistoryClearPhase.Clearing,70,null,true,true);Changed();
            Assert.Equal("Clear history · Finishing a clear that was interrupted…",status.Text);
            session.Usage.ClearState=new(HistoryClearPhase.Failed,null,"IOException",false,false);ClearChanged();
            Assert.False(session.ClearActive);Assert.Equal("Clear history · 0 of 4 finished. Histories marked not cleared need Clear history again.",status.Text);Assert.Equal(Visibility.Visible,card.Visibility);
            // A retained request plus running worker makes Try again available; no store worker or actual clear runs here.
            worker.SetValue(session.Usage,new TaskCompletionSource().Task);kept.SetValue(command,HistoryClear.Create(null,true,DateTimeOffset.UtcNow));
            ClearChanged();shell.UpdateLayout();var show=Assert.Single(Logical(card).OfType<Button>(),button=>Equals(button.Content,"Show in History and alerts"));
            Assert.Equal(Visibility.Visible,show.Visibility);show.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));shell.UpdateLayout();
            Assert.Same(history,tabs.SelectedItem);progress.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Contains(progress.TryAgain,Descendants((DependencyObject)history.Content));Assert.Equal(Visibility.Visible,progress.TryAgain.Visibility);Assert.True(progress.TryAgain.IsEnabled);
            // The results sit under the exclusions list: the button scrolls them into the viewport and hands focus to Try again.
            {var viewport=Assert.IsType<ScrollViewer>(Field<Dictionary<string,FrameworkElement>>("pages")["desktop"]);var top=progress.TransformToAncestor(viewport).Transform(new Point()).Y;Assert.InRange(top,-1,viewport.ViewportHeight-progress.ActualHeight+1);if(PresentationSource.FromVisual(shell) is not null)Assert.True(progress.TryAgain.IsKeyboardFocused||progress.TryAgain.IsFocused,"Try again takes focus after Show in History and alerts.");}// focus needs a hosted window; offscreen shells cannot take it
            tabs.SelectedItem=monitoring;shell.UpdateLayout();progress.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            session.Usage.ClearState=new(HistoryClearPhase.Unavailable,null,"IOException",false,false);Changed();Assert.False(session.ClearActive);Assert.Equal(Visibility.Visible,card.Visibility);Assert.Contains("Histories marked not cleared",status.Text);
            session.Usage.ClearState=new(HistoryClearPhase.Queued,null,null,false,false);Changed();Assert.Equal("Clear history · Clearing history…",status.Text);
            session.Usage.ClearState=new(HistoryClearPhase.Done,100,null,false,false);Changed();Assert.Equal("",status.Text);Assert.Equal(Visibility.Collapsed,card.Visibility);
            session.Usage.ClearState=HistoryClearState.None;Changed();Assert.Equal(Visibility.Collapsed,card.Visibility);
        }
        finally{worker.SetValue(session.Usage,oldWorker);kept.SetValue(command,oldKept);session.Usage.ClearState=before;Result("");Registration(new("fixture",false,false,false,false,null,"Startup disabled."));Changed();tabs.SelectedItem=monitoring;shell.UpdateLayout();}
    }

    /// <summary>The lifetime mirror detaches even if its History tab was never loaded.</summary>
    private static void CheckSettingsStatusLifetime(UiSession session)
    {
        var window=new MainWindow(session,new ApplicationLifetime(),()=>true,()=>{});
        var status=(TextBlock)typeof(MainWindow).GetField("settingsStatus",All)!.GetValue(window)!;
        typeof(MainWindow).GetMethod("SetStartupResult",All)!.Invoke(window,["Lifetime fixture."]);
        typeof(Window).GetMethod("OnClosed",All)!.Invoke(window,[EventArgs.Empty]);
        var before=session.Usage.ClearState;
        try
        {
            session.Usage.ClearState=new(HistoryClearPhase.Clearing,10,null,true,false);
            ((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();
            ((Action<HistoryClearStore>?)typeof(UiSession).GetField("HistoryClearChanged",All)!.GetValue(session))?.Invoke(HistoryClearStore.Usage);
            Assert.Equal("Lifetime fixture.",status.Text);
        }
        finally{session.Usage.ClearState=before;((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();}
    }

    /// <summary>Real WPF focus traversal: the header group is one stop, its content is reachable, and arrows select adjacent headers.</summary>
    private static void CheckSettingsKeyboard(TabControl tabs,Border card)
    {
        var selected=tabs.SelectedItem;card.Child=null;
        var host=new Window{Content=tabs,Width=1000,Height=700,Left=-32000,Top=-32000,ShowInTaskbar=false,WindowStyle=WindowStyle.None};
        try
        {
            host.Show();host.Activate();host.UpdateLayout();Wait(Task.CompletedTask);
            var monitoring=tabs.Items.Cast<TabItem>().Single(tab=>Equals(tab.Header,"Monitoring"));tabs.SelectedItem=monitoring;host.UpdateLayout();
            Assert.True(monitoring.Focus(),"The offscreen keyboard host must accept keyboard focus.");
            Assert.True(monitoring.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),"Tab must reach Monitoring content.");
            var polling=Assert.Single(Descendants((DependencyObject)monitoring.Content).OfType<ComboBox>(),box=>AutomationProperties.GetName(box)=="Data polling interval");
            Assert.Same(polling,Keyboard.FocusedElement);
            Assert.True(polling.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)),"Shift+Tab must return to the selected header.");Assert.Same(monitoring,Keyboard.FocusedElement);
            // Arrow handling is KeyboardNavigation's input postprocessor; RaiseEvent alone bypasses that stage.
            void Arrow(Key key)=>InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(tabs)!,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
            Arrow(Key.Right);Assert.Equal("Appearance",Assert.IsType<TabItem>(tabs.SelectedItem).Header);
            var appearance=Assert.IsType<TabItem>(tabs.SelectedItem);Assert.Same(appearance,Keyboard.FocusedElement);
            Arrow(Key.Left);Assert.Same(monitoring,tabs.SelectedItem);Assert.Same(monitoring,Keyboard.FocusedElement);
            Assert.NotEqual(KeyboardNavigationMode.Once,KeyboardNavigation.GetTabNavigation(tabs));
        }
        finally{host.Content=null;host.Close();card.Child=tabs;tabs.SelectedItem=selected;card.UpdateLayout();}
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){yield return child;foreach(var descendant in Logical(child))yield return descendant;}
    }

    /// <summary>Settings → History and alerts → Clear history: heading, help, the Clear history… button disabled with each tooltip during a
    /// fake clear, and the progress block shown whenever a store has a clear or a result, until a new clear starts.</summary>
    private static void CheckClearHistory(StackPanel history,UiSession session)
    {
        var children=history.Children.Cast<UIElement>().ToList();
        var heading=children.OfType<TextBlock>().Single(t=>t.Text=="Clear history");
        Assert.Equal(16,heading.FontSize);
        int at=children.IndexOf(heading);
        Assert.True(at>children.FindIndex(c=>c is ExcludedAppsBlock));
        Assert.Equal("Removes saved history for one app or all apps, older than 7 days or from all time, from Connections history, Usage history, Application resources and the alert log together. Device-wide history stays.",Assert.IsType<TextBlock>(children[at+1]).Text);
        var button=Assert.IsType<Button>(children[at+2]);
        Assert.Equal("Clear history…",button.Content);
        var progress=Assert.IsType<HistoryClearProgressView>(children[at+3]);
        progress.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Null(progress.Close);
        Assert.True(button.IsEnabled);Assert.Null(button.ToolTip);Assert.Equal(Visibility.Collapsed,progress.Visibility);
        Assert.True(ToolTipService.GetShowOnDisabled(button));
        void Changed()=>((Action?)typeof(UiSession).GetField("Changed",All)!.GetValue(session))?.Invoke();
        try
        {
            session.Usage.ClearState=new(HistoryClearPhase.Clearing,10,null,true,false);Changed();
            Assert.False(button.IsEnabled);Assert.Equal("A clear is in progress.",button.ToolTip);
            Assert.Equal(Visibility.Visible,progress.Visibility);
            Assert.Equal(["Usage history · clearing (10 %)"],progress.Lines.Select(t=>t.Text));
            // The worker never started, so the store cannot retry: the clear waits for the next start.
            session.Usage.ClearState=new(HistoryClearPhase.Failed,null,"IOException",true,false);Changed();
            Assert.False(session.Usage.CanRetryClear);
            Assert.False(button.IsEnabled);Assert.Equal("A clear is waiting to finish; it resumes when DeadWare next starts.",button.ToolTip);
            // Failed without its marker does not hold the next clear back (B8).
            session.Usage.ClearState=new(HistoryClearPhase.Failed,null,"IOException",false,false);Changed();
            Assert.True(button.IsEnabled);Assert.Null(button.ToolTip);
            Assert.Equal(Visibility.Visible,progress.Visibility);
            Assert.Equal("History partly cleared.",progress.Header.Text);
            // The result stays until a new clear starts.
            session.Usage.ClearState=new(HistoryClearPhase.Done,100,null,false,false);Changed();Changed();
            Assert.Equal(Visibility.Visible,progress.Visibility);Assert.Equal("History cleared.",progress.Header.Text);
            session.Usage.ClearState=new(HistoryClearPhase.Queued,null,null,false,false);Changed();
            Assert.Equal("Clearing history…",progress.Header.Text);Assert.Equal(["Usage history · waiting"],progress.Lines.Select(t=>t.Text));
        }
        finally{session.Usage.ClearState=HistoryClearState.None;Changed();}
        Assert.True(button.IsEnabled);Assert.Equal(Visibility.Collapsed,progress.Visibility);
    }

    /// <summary>Settings → History and alerts → Don't record these apps: copy, suggestions, adding, the three errors, the note and Remove with its focus.</summary>
    private static void CheckExcludedApps(StackPanel history,UiSession session)
    {
        const string Help="These apps still show on live pages, marked \"not recorded\", but none of their history is saved: no Connections history, no per-app totals in Usage history and no alerts for them. In Application resources their CPU, memory, I/O and GPU count under Everything else, so device totals stay true. Application traffic budgets for them stop counting. History saved before you add an app is kept until you clear it. Processes that Windows saves as \"PID n\" can't be matched.";
        const string Kept="Saved history for this app is kept. Use Clear history… to remove it.";
        var block=Assert.Single(Descendants(history).OfType<ExcludedAppsBlock>());
        // The block follows the Connections block in the same card.
        Assert.Same(history,block.Parent);
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
