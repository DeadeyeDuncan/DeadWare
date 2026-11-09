using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DeadWare.Core;
namespace DeadWare.App;
public sealed class MainWindow : Window
{
    private sealed record PollingChoice(double Seconds,string Label)
    {
        public override string ToString()=>Label;
    }
    private readonly UiSession session;
    private readonly ApplicationLifetime lifetime;
    private readonly Func<bool> trayAvailable;
    private readonly ContentControl content=new();
    private readonly TextBlock heading=Ui.Heading("System overview",26),subtitle=Ui.Text("",11.5,"Muted"),status=Ui.Text("Starting the shared collector…",11,"Muted"),memory=Ui.Text("Memory —",12,"Muted"),networkStatus=Ui.Text("",12,"Warning"),networkEmpty=Ui.Text("",13,"Muted"),sensorCount=Ui.Text("",12,"Muted");
    private readonly ComboBox gpu=new(){MaxWidth=340,VerticalAlignment=VerticalAlignment.Center,DisplayMemberPath="Name",SelectedValuePath="Id"};
    private readonly StackPanel gpuControls;
    private readonly DockPanel liveToolbar;
    private readonly UpdateNotice updateNotice;
    private readonly ThemePicker themePicker;
    private readonly TextBox appSearch=new(){Width=250},sensorSearch=new(){Width=280};
    private readonly DataGrid apps=new(),sensors=new();
    private readonly Dictionary<string,FrameworkElement> pages=new();
    private readonly Dictionary<string,Button> navigation=new();
    private readonly Dictionary<string,CheckBox> widgetGraphChoices=new(StringComparer.Ordinal);
    private readonly CheckBox widgetConnectionsMap=new() {Content="Connections map (live)",Margin=new Thickness(0,0,20,8)};
    private readonly CheckBox widgetAlwaysOnTop=new() {Content="Widget always on top",Margin=new Thickness(0,0,0,12)};
    private readonly CheckBox joinedPopouts=new() {Content="Join CPU and GPU graphs in pop-outs",Margin=new Thickness(0,0,0,8)};
    private readonly CheckBox gpuFanSpeed=new() {Content="Show GPU fan speed on temperature graphs",Margin=new Thickness(0,0,0,8)};
    private readonly CheckBox cpuPumpSpeed=new() {Content="Show CPU pump speed on CPU temperature graphs",Margin=new Thickness(0,0,0,8)};
    private readonly CheckBox cpuCoreUsage=new() {Content="Show per-core CPU usage on usage graphs",Margin=new Thickness(0,12,0,8)};
    private readonly CheckBox processMonitoring=new() {Content="Per-process monitoring",Margin=new Thickness(0,14,0,8)};
    private readonly ComboBox glass=new() {MinWidth=160};
    private readonly CheckBox graphSmoothing=new() {Content="Smooth long-range graphs (1, 3 and 6 hours)",Margin=new Thickness(0,0,0,8)};
    private readonly CheckBox usageRecording=new() {Content="Record usage history on this device",Margin=new Thickness(0,8,0,8)};
    private readonly CheckBox alertNotifications=new() {Content="Show tray notifications for alerts",Margin=new Thickness(0,8,0,8)};
    private readonly CheckBox startup=new() {Content="Start with Windows (administrator)",Margin=new Thickness(0,22,0,8)};
    private readonly TextBlock startupStatus=Ui.Text("Startup status is checked when this page opens.",12,"Muted"),startupResult=Ui.Text("",12,"Warning");
    private readonly TextBlock settingsStatus=Ui.Text("",12,"Warning");
    private Border settingsStatusCard=null!;
    private Button settingsHistory=null!;
    private HistoryClearProgressView settingsClearProgress=null!;
    /// <summary>Raised when the Settings status banner announces a change to assistive technology; tests count it.</summary>
    internal event Action<string>? SettingsStatusAnnounced;
    private string announcedStatus="";
    internal TabControl SettingsTabs {get;}=new();
    internal TabItem SettingsTab(string header)=>SettingsTabs.Items.Cast<TabItem>().Single(tab=>Equals(tab.Header,header));
    private Button startupRepair=null!,startupDisable=null!,startupRefresh=null!,batteryNavigation=null!;
    private string page="overview",gpuSignature="";
    private ExcludedAppsBlock? excludedApps;
    private bool syncingGpu;
    private bool refreshingSensors;
    private bool syncingStartup,startupBusy,syncingWidgetGraphs,startupQueryFailed;
    private readonly TextBlock trayNotice=Ui.Text("Close hides to the system tray.\nQuit stops monitoring.",10,"Muted");
    private readonly TextBlock monitoringInfo=Ui.Text("",10,"Muted");
    /// <summary>Shows the dashboard window; the checks replace it so that no native window is created.</summary>
    internal Action<Window> present=window=>window.Show();
    public MainWindow(UiSession session,ApplicationLifetime lifetime,Func<bool> trayAvailable,Action quit)
    {
        this.session=session;this.lifetime=lifetime;this.trayAvailable=trayAvailable;Title="DeadWare";MinWidth=900;MinHeight=640;Ui.Icon(this);session.Place(this,"main",1220,900);
        var shell=new Grid();shell.ColumnDefinitions.Add(new(){Width=new GridLength(205)});shell.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Content=shell;
        var sidebar=new DockPanel();var sidePanel=Ui.Panel(sidebar,new Thickness(14,20,14,14));sidePanel.Margin=new Thickness(12);Grid.SetColumn(sidePanel,0);shell.Children.Add(sidePanel);
        var logo=Ui.Logo();logo.Width=30;logo.Height=30;var brandName=Ui.Heading("DeadWare",19);brandName.FontWeight=FontWeights.SemiBold;var brand=Ui.Row(logo,brandName);brand.Margin=new(2,0,0,22);DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
        var sideBottom=new StackPanel();DockPanel.SetDock(sideBottom,Dock.Bottom);sidebar.Children.Add(sideBottom);
        sideBottom.Children.Add(Ui.Caption("Color theme"));themePicker=new ThemePicker(session.Settings.Theme,session.SetTheme) {Margin=new Thickness(0,8,0,15)};sideBottom.Children.Add(themePicker);
        var elevate=Ui.Button("Restart as administrator",session.RestartElevated);elevate.FontSize=10;elevate.Background=System.Windows.Media.Brushes.Transparent;sideBottom.Children.Add(elevate);
        var quitButton=Ui.Button("Quit DeadWare",quit);quitButton.FontSize=11;sideBottom.Children.Add(quitButton);trayNotice.Margin=new Thickness(3,5,3,8);sideBottom.Children.Add(trayNotice);
        sideBottom.Children.Add(monitoringInfo);
        var nav=new StackPanel();sidebar.Children.Add(new ScrollViewer{Content=nav,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        foreach(var (key,label) in new[]{("overview","Overview"),("network","Network"),("storage","Storage"),("processes","Processes"),("connections","Connections"),("battery","Battery history"),("usage","Usage history"),("alerts","Alerts"),("sensors","Sensors"),("cooling","Cooling Health"),("desktop","Settings"),("updates","Updates")})
        {var button=Ui.NavButton(key,label,()=>Navigate(key));navigation[key]=button;nav.Children.Add(button);if(key=="battery")batteryNavigation=button;}
        var main=new DockPanel {Margin=new Thickness(8,12,18,16)};Grid.SetColumn(main,1);shell.Children.Add(main);
        var footer=new StackPanel {Margin=new Thickness(4,10,4,0)};footer.Children.Add(status);DockPanel.SetDock(footer,Dock.Bottom);main.Children.Add(footer);
        var header=new StackPanel();var headerPanel=Ui.Panel(header,new Thickness(18,14,18,12));headerPanel.Margin=new Thickness(6,0,6,12);DockPanel.SetDock(headerPanel,Dock.Top);main.Children.Add(headerPanel);
        // One toolbar row: title and subtitle on the left, GPU picker, range pill and widget button on the right.
        var titleRow=new DockPanel();var tools=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(tools,Dock.Right);titleRow.Children.Add(tools);
        liveToolbar=new DockPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)};var ranges=Ui.Range(session);DockPanel.SetDock(ranges,Dock.Right);liveToolbar.Children.Add(ranges);var gpuLabel=Ui.Text("GPU",11,"Muted");gpuLabel.Margin=new(0,0,10,0);gpu.MaxWidth=240;gpuControls=Ui.Row(gpuLabel,gpu);gpuControls.Margin=new(0,0,12,0);liveToolbar.Children.Add(gpuControls);tools.Children.Add(liveToolbar);
        var widget=Ui.Button("↗ Open widget",()=>session.OpenDetached("widget"));widget.VerticalAlignment=VerticalAlignment.Center;tools.Children.Add(widget);
        heading.TextWrapping=TextWrapping.NoWrap;heading.TextTrimming=TextTrimming.CharacterEllipsis;subtitle.TextWrapping=TextWrapping.NoWrap;subtitle.TextTrimming=TextTrimming.CharacterEllipsis;subtitle.Margin=new(1,2,0,0);
        var titles=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};titles.Children.Add(heading);titles.Children.Add(subtitle);titleRow.Children.Add(titles);header.Children.Add(titleRow);
        updateNotice=new(ShowUpdates);header.Children.Add(updateNotice);
        gpu.SelectionChanged+=(_,_)=>{if(!syncingGpu)session.SetGpu((gpu.SelectedItem as Device)?.Id);};
        main.Children.Add(content);
        pages["overview"]=Overview();pages["network"]=Network();pages["storage"]=new StoragePage(session);pages["processes"]=new ProcessesPage(session);pages["connections"]=new ConnectionsPage(session);pages["cooling"]=new CoolingHealthPage(session);pages["battery"]=new BatteryHistoryPage(session);pages["usage"]=new UsageHistoryPage(session);pages["alerts"]=new AlertsPage(session);pages["sensors"]=Sensors();pages["desktop"]=Desktop();pages["updates"]=new UpdatesPage(session);Navigate("overview");
        session.Changed+=Refresh;session.Changed+=UpdateSettingsStatus;session.HistoryClearChanged+=SettingsClearChanged;session.Updates.Changed+=RefreshUpdateNotice;session.UsageDayRequested+=ShowUsageDay;session.ConnectionsHistoryRequested+=ShowConnectionsHistory;
        Closed+=(_,_)=>{session.Changed-=Refresh;session.Changed-=UpdateSettingsStatus;session.HistoryClearChanged-=SettingsClearChanged;session.Updates.Changed-=RefreshUpdateNotice;session.UsageDayRequested-=ShowUsageDay;session.ConnectionsHistoryRequested-=ShowConnectionsHistory;};Closing+=OnClosing;
        RefreshUpdateNotice();
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
        KeyDown+=(_,e)=>{if(PageKey(e.OriginalSource,e.Key,Keyboard.Modifiers))e.Handled=true;};
    }
    /// <summary>Ctrl+F, F5 and Esc for the Processes page while it is shown and the key comes from the shell around it (the rail, the header) or the window itself; true when the page acted.
    /// The window's KeyDown bubbles, so it sees only keys nobody handled: inside the page, its preview takes F5 and Esc and its key binding takes Ctrl+F first, and chrome controls keep the keys they handle.
    /// Pop-up content (menus, drop-downs) is outside the shell's visual tree, so it keeps its keys too.</summary>
    internal bool PageKey(object source,Key key,ModifierKeys modifiers)=>
        content.Content is ProcessesPage processes
        &&(ReferenceEquals(source,this)||source is Visual from&&Content is Visual shell&&from.IsDescendantOf(shell)&&!from.IsDescendantOf(processes))
        &&processes.PageKey(key,modifiers);
    private FrameworkElement Overview()=>new OverviewPage(session,memory);
    private FrameworkElement Network()
    {
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=300});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=160});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(new ChartCard(session,"network"));
        var controls=new StackPanel();networkStatus.Margin=new(0,0,0,10);controls.Children.Add(networkStatus);appSearch.ToolTip="Search application name or PID";System.Windows.Automation.AutomationProperties.SetName(appSearch,"Search applications");
        var searchLabel=Ui.Text("Find application",12,"Muted");searchLabel.Margin=new(0,0,12,0);var search=new WrapPanel();search.Children.Add(searchLabel);search.Children.Add(appSearch);networkEmpty.Margin=new(16,0,0,0);networkEmpty.VerticalAlignment=VerticalAlignment.Center;search.Children.Add(networkEmpty);controls.Children.Add(search);appSearch.TextChanged+=(_,_)=>RefreshTables();
        var controlsCard=Ui.Panel(controls,new Thickness(14,10,14,10));controlsCard.Margin=new Thickness(6,2,6,10);Grid.SetRow(controlsCard,1);grid.Children.Add(controlsCard);
        Column(apps,"Application","Name",2,new NotRecordedNameColumn(session));Column(apps,"PID","Pid",.6);Column(apps,"↓ Download","Download",1);Column(apps,"↑ Upload","Upload",1);Column(apps,"Received","Received",1);Column(apps,"Sent","Sent",1);
        var table=Ui.Panel(apps,new Thickness(0));table.Margin=new Thickness(6,0,6,0);Grid.SetRow(table,2);grid.Children.Add(table);
        var about=new Expander {Header="About these totals",IsExpanded=false,Content=Ui.Text("Per-app totals cover each current process lifetime in this monitoring session. Exited processes are removed. ETW attribution is approximate and differs from interface totals. No packet payloads are captured. Apps marked not recorded are in Settings → Don't record these apps; their traffic isn't saved.",11,"Muted")};
        var aboutCard=Ui.Panel(about,new Thickness(14,6,14,6));aboutCard.Margin=new Thickness(6,10,6,6);Grid.SetRow(aboutCard,3);grid.Children.Add(aboutCard);
        var scroller=new FillingScroller(grid,width=>{var size=new Size(width,double.PositiveInfinity);controlsCard.Measure(size);aboutCard.Measure(size);return controlsCard.DesiredSize.Height+aboutCard.DesiredSize.Height+300+160;});
        controlsCard.SizeChanged+=(_,_)=>scroller.InvalidateMeasure();aboutCard.SizeChanged+=(_,_)=>scroller.InvalidateMeasure();scroller.ForwardWheelFrom(apps);return scroller;
    }
    private FrameworkElement Sensors()
    {
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        // The count and statistics line shares the search card: beside the search when there is room, wrapped under it when narrow.
        var label=Ui.Text("Find sensor or hardware",12,"Muted");label.Margin=new(0,0,12,0);var search=new WrapPanel();search.Children.Add(label);search.Children.Add(sensorSearch);sensorCount.Margin=new(16,0,0,0);sensorCount.VerticalAlignment=VerticalAlignment.Center;search.Children.Add(sensorCount);var sensorStrip=Ui.Panel(search,new Thickness(14,10,14,10));sensorStrip.Margin=new(6,2,6,12);grid.Children.Add(sensorStrip);sensorSearch.ToolTip="Search sensors and device names";System.Windows.Automation.AutomationProperties.SetName(sensorSearch,"Search sensors");sensorSearch.TextChanged+=(_,_)=>RefreshTables();
        Column(sensors,"Hardware / sensor","Name",3);Column(sensors,"Current","Current",1);Column(sensors,"Min","Minimum",1);Column(sensors,"Average","Average",1);Column(sensors,"Max","Maximum",1);
        var table=Ui.Panel(sensors,new Thickness(0));table.Margin=new Thickness(6,0,6,6);Grid.SetRow(table,1);grid.Children.Add(table);return grid;
    }
    private FrameworkElement Desktop()
    {
        var stack=new StackPanel {Margin=new Thickness(8)};
        StackPanel monitoring=new(),appearance=new(),widgetGroup=new(),popouts=new(),startupGroup=new(),history=new();
        popouts.Children.Add(Ui.Heading("Your readings, wherever you need them",16));var intro=Ui.Text("Open any graph in its own resizable native window. Choose Always on top to keep it visible while you work. All windows share the same readings, selected GPU, history range and theme.",12,"Muted");intro.Margin=new(0,12,0,24);popouts.Children.Add(intro);
        var polling=new ComboBox {ItemsSource=PollingPolicy.Intervals.Select(seconds=>new PollingChoice(seconds,seconds==1?"1 second (default)":$"{seconds:0.#} seconds")).ToArray(),DisplayMemberPath="Label",SelectedValuePath="Seconds",SelectedValue=session.Settings.PollingIntervalSeconds,MinWidth=160};
        System.Windows.Automation.AutomationProperties.SetName(polling,"Data polling interval");
        polling.SelectionChanged+=(_,_)=>{if(polling.SelectedValue is double seconds)session.SetPollingInterval(seconds);};
        var pollingLabel=Ui.Text("Data polling interval",14);pollingLabel.Margin=new(0,0,12,0);
        var pollingRow=Ui.Row(pollingLabel,polling);pollingRow.Margin=new(0,0,0,8);monitoring.Children.Add(pollingRow);
        var pollingHelp=Ui.Text("Applies without restarting after the current wait or reading finishes. Faster polling uses more resources; slow hardware may take longer than the selected interval. At 5 or 10 seconds, Cooling Health records history but does not gather enough samples for reference comparisons.",12,"Muted");pollingHelp.Margin=new(0,0,0,20);monitoring.Children.Add(pollingHelp);
        glass.ItemsSource=GlassLevels.All;glass.SelectedItem=session.Settings.GlassLevel;
        System.Windows.Automation.AutomationProperties.SetName(glass,"Window glass");
        glass.SelectionChanged+=(_,_)=>{if(!syncingWidgetGraphs&&glass.SelectedItem is string level)session.SetGlassLevel(level);};
        var glassLabel=Ui.Text("Window glass",14);glassLabel.Margin=new(0,0,12,0);
        var glassRow=Ui.Row(glassLabel,glass);glassRow.Margin=new(0,0,0,8);appearance.Children.Add(glassRow);
        var glassHelp=Ui.Text("Subtle, Balanced and Strong let the blurred desktop show through every DeadWare window by increasing amounts. Windows shows the glass in the focused window; other DeadWare windows use the solid theme surface until selected. Off keeps solid windows. Needs Windows 11 22H2 or later with Windows Transparency effects on; Blackout and High Contrast always stay solid.",12,"Muted");glassHelp.Margin=new(0,0,0,20);appearance.Children.Add(glassHelp);
        graphSmoothing.IsChecked=session.Settings.GraphSmoothing;
        System.Windows.Automation.AutomationProperties.SetName(graphSmoothing,"Graph smoothing");
        graphSmoothing.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetGraphSmoothing(true);};
        graphSmoothing.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetGraphSmoothing(false);};appearance.Children.Add(graphSmoothing);
        var smoothingHelp=Ui.Text("On shows an average line with a min/max band. Off shows raw traces. Applies immediately to the dashboard, pop-outs and widget; recorded data is unchanged.",12,"Muted");smoothingHelp.Margin=new(0,0,0,20);appearance.Children.Add(smoothingHelp);
        gpuFanSpeed.IsChecked=session.Settings.ShowGpuFanSpeed;
        System.Windows.Automation.AutomationProperties.SetName(gpuFanSpeed,"Show GPU fan speed on temperature graphs");
        gpuFanSpeed.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetGpuFanSpeed(true);};
        gpuFanSpeed.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetGpuFanSpeed(false);};appearance.Children.Add(gpuFanSpeed);
        var fanHelp=Ui.Text("Adds the selected GPU's first reported fan on its own left RPM scale, dotted and amber by default. Use the GPU fan color button on the overview's GPU temperature card to change its appearance across the overview, temperature pop-outs and widget. Unsupported fan readings stay unavailable; 0 RPM means stopped.",12,"Muted");fanHelp.Margin=new(0,0,0,20);appearance.Children.Add(fanHelp);
        cpuPumpSpeed.IsChecked=session.Settings.ShowCpuPumpSpeed;
        System.Windows.Automation.AutomationProperties.SetName(cpuPumpSpeed,"Show CPU pump speed on CPU temperature graphs");
        cpuPumpSpeed.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetCpuPumpSpeed(true);};
        cpuPumpSpeed.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetCpuPumpSpeed(false);};appearance.Children.Add(cpuPumpSpeed);
        var pumpHelp=Ui.Text("Adds the CPU cooler's pump speed on its own left RPM scale, dotted and blue by default, to the CPU temperature card, CPU temperature pop-out and the widget's temperature graph. DeadWare uses a motherboard or AIO header labeled as a pump, otherwise the CPU Fan header; this needs Extended sensors and a single CPU. Use the CPU pump color button on the overview's CPU temperature card to change its appearance.",12,"Muted");pumpHelp.Margin=new(0,0,0,20);appearance.Children.Add(pumpHelp);
        var widgetLabel=Ui.Text("Widget graphs",14);widgetLabel.Margin=new(0,0,0,8);widgetGroup.Children.Add(widgetLabel);
        var widgetOptions=new WrapPanel {Margin=new Thickness(0,0,0,6)};
        var selectedGraphs=WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs);
        foreach(var item in new[]{("cpu","CPU / GPU usage"),("cputemp","CPU / GPU temperature"),("memory","RAM usage"),("network","Network activity"),("disk","Disk activity")})
        {
            var key=item.Item1;
            var choice=new CheckBox {Content=item.Item2,Tag=key,IsChecked=selectedGraphs.Contains(key),Margin=new Thickness(0,0,20,8)};
            System.Windows.Automation.AutomationProperties.SetName(choice,"Show "+item.Item2+" in widget");
            choice.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetGraph(key,true);};
            choice.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetGraph(key,false);};
            widgetGraphChoices.Add(key,choice);widgetOptions.Children.Add(choice);
        }
        widgetConnectionsMap.IsChecked=session.Settings.WidgetConnectionsMap;
        System.Windows.Automation.AutomationProperties.SetName(widgetConnectionsMap,"Show Connections map (live) in widget");
        widgetConnectionsMap.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetConnectionsMap(true);};
        widgetConnectionsMap.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetConnectionsMap(false);};
        widgetOptions.Children.Add(widgetConnectionsMap);widgetGroup.Children.Add(widgetOptions);
        widgetAlwaysOnTop.IsChecked=session.Settings.WidgetAlwaysOnTop;
        System.Windows.Automation.AutomationProperties.SetName(widgetAlwaysOnTop,"Widget always on top");
        widgetAlwaysOnTop.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetAlwaysOnTop(true);};
        widgetAlwaysOnTop.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetWidgetAlwaysOnTop(false);};
        widgetGroup.Children.Add(widgetAlwaysOnTop);
        var widgetHelp=Ui.Text("Choose the graphs shown in the widget. Changes apply immediately; resize the widget to your preferred height.",12,"Muted");widgetHelp.Margin=new(0,0,0,20);widgetGroup.Children.Add(widgetHelp);
        history.Children.Add(Ui.Heading("Usage history",16));
        usageRecording.IsChecked=session.Settings.UsageRecording;
        System.Windows.Automation.AutomationProperties.SetName(usageRecording,"Record usage history");
        usageRecording.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetUsageRecording(true);};
        usageRecording.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetUsageRecording(false);};history.Children.Add(usageRecording);
        var usageHelp=Ui.Text("Keeps day, week and month history of CPU, GPU, RAM and network usage plus per-application network totals for up to 400 days in this Windows profile. Pausing keeps what was saved. Open Usage history to browse, export or check storage. While Per-process monitoring is on, it also keeps the busiest apps' CPU, memory, I/O and GPU for 30 days.",12,"Muted");usageHelp.Margin=new(0,0,0,8);history.Children.Add(usageHelp);
        var usageOpen=Ui.Button("Open Usage history",()=>Navigate("usage"));usageOpen.Margin=new(0,0,0,20);history.Children.Add(usageOpen);
        history.Children.Add(Ui.Heading("Alerts",16));
        alertNotifications.IsChecked=session.Settings.AlertNotifications;
        System.Windows.Automation.AutomationProperties.SetName(alertNotifications,"Alert notifications");
        alertNotifications.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetAlertNotifications(true);};
        alertNotifications.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetAlertNotifications(false);};history.Children.Add(alertNotifications);
        var alertHelp=Ui.Text("Rules you set on the Alerts page notify from the tray once per episode and are always logged there, whether or not notifications are on. The first network use rule learns your apps for 3 days once per-app capture has run as administrator, then logs each new app; apps seen within a minute share one notification. Cooling Health keeps its own notification setting.",12,"Muted");alertHelp.Margin=new(0,0,0,8);history.Children.Add(alertHelp);
        var alertOpen=Ui.Button("Open Alerts",()=>Navigate("alerts"));alertOpen.Margin=new(0,0,0,20);history.Children.Add(alertOpen);
        history.Children.Add(Ui.Heading("Connections",16));
        history.Children.Add(Ui.Text("Record endpoint history, choose an optional home city, manage the offline location database and export saved traffic from Connections. Local history rolls over after 30 days or 256 MiB.",12,"Muted"));
        history.Children.Add(Ui.Button("Open Connections controls",()=>Navigate("connections")));
        excludedApps=new ExcludedAppsBlock(session,new AppSuggestions(session,()=>DateTimeOffset.UtcNow)) {Margin=new Thickness(0,20,0,0)};history.Children.Add(excludedApps);
        var clearHeading=Ui.Heading(HistoryClearCopy.SettingsHeading,16);clearHeading.Margin=new(0,20,0,0);history.Children.Add(clearHeading);
        var clearHelp=Ui.Text(HistoryClearCopy.SettingsHelp,12,"Muted");clearHelp.Margin=new(0,0,0,8);history.Children.Add(clearHelp);
        history.Children.Add(ClearHistoryEntry.Create(session,()=>this));
        settingsClearProgress=new HistoryClearProgressView(session,closeButton:false) {Margin=new Thickness(0,10,0,0)};history.Children.Add(settingsClearProgress);
        var popoutLabel=Ui.Text("Graph pop-outs",14);popoutLabel.Margin=new(0,0,0,8);popouts.Children.Add(popoutLabel);
        joinedPopouts.IsChecked=session.Settings.JoinCpuGpuPopouts;
        System.Windows.Automation.AutomationProperties.SetName(joinedPopouts,"Join CPU and GPU graphs in pop-outs");
        joinedPopouts.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetJoinedPopouts(true);};
        joinedPopouts.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetJoinedPopouts(false);};popouts.Children.Add(joinedPopouts);
        var popoutHelp=Ui.Text("Use the overview's Pop out buttons. Joined mode puts CPU and GPU usage on one chart and both temperatures on another. Applies to new pop-outs; existing windows and remembered layouts keep their current form.",12,"Muted");popoutHelp.Margin=new(0,0,0,20);popouts.Children.Add(popoutHelp);
        var saved=Ui.Text("Positions, sizes and Always on top choices are saved as you work. Closing the dashboard hides it to the system tray while graphs and the widget keep monitoring. Choose Quit DeadWare to save the windows left open for next time and stop collection. Close an individual graph or widget to leave it closed next time.",12,"Muted");saved.Margin=new(0,20,0,0);popouts.Children.Add(saved);
        var restore=new CheckBox {Content="Reopen graphs and widget from the previous session",IsChecked=session.Settings.RestoreOpenWindows,Margin=new Thickness(0,18,0,8)};
        restore.Checked+=(_,_)=>session.SetRestoreOpenWindows(true);restore.Unchecked+=(_,_)=>session.SetRestoreOpenWindows(false);popouts.Children.Add(restore);
        startup.Checked+=async (_,_)=>{if(!syncingStartup)await ChangeStartupAsync(true);};
        startup.Unchecked+=async (_,_)=>{if(!syncingStartup)await ChangeStartupAsync(false);};
        startupGroup.Children.Add(startup);startupGroup.Children.Add(startupStatus);startupResult.Margin=new(0,8,0,4);startupGroup.Children.Add(startupResult);
        var startInTray=new CheckBox {Content="Start in the system tray",IsChecked=session.Settings.StartInTray,Margin=new Thickness(0,10,0,6)};
        startInTray.Checked+=(_,_)=>session.SetStartInTray(true);startInTray.Unchecked+=(_,_)=>session.SetStartInTray(false);startupGroup.Children.Add(startInTray);
        startupGroup.Children.Add(Ui.Text("At Windows sign-in, hide only the dashboard. Graphs and the widget still follow the reopen setting in Pop-outs. Manual launches show the dashboard.",12,"Muted"));
        startupRepair=Ui.Button("Enable / repair startup",()=>_ = ChangeStartupAsync(true));
        startupDisable=Ui.Button("Disable startup",()=>_ = ChangeStartupAsync(false));
        startupRefresh=Ui.Button("Refresh status",()=>_ = RefreshStartupAsync());
        var startupButtons=new WrapPanel();startupButtons.Children.Add(startupRepair);startupButtons.Children.Add(startupDisable);startupButtons.Children.Add(startupRefresh);
        var startupRepairContent=new StackPanel();startupRepairContent.Children.Add(startupButtons);
        startupRepairContent.Children.Add(Ui.Text("Windows asks for administrator approval when changing startup. This applies to your Windows account and this executable location. After copying DeadWare to a new location, start it from there and repair startup. The drive must be available when you sign in.",12,"Muted"));
        startupGroup.Children.Add(new Expander {Header="Repair startup registration",IsExpanded=false,Content=startupRepairContent});
        var sensorSources=new StackPanel();monitoring.Children.Add(new Expander {Header="Sensor sources",IsExpanded=false,Content=sensorSources});
        var extended=new CheckBox {Content="Extended sensors (may affect responsiveness)",IsChecked=session.Settings.ExtendedSensors,Margin=new Thickness(0,22,0,8)};
        extended.Checked+=(_,_)=>session.SetExtendedSensors(true);extended.Unchecked+=(_,_)=>session.SetExtendedSensors(false);sensorSources.Children.Add(extended);
        sensorSources.Children.Add(Ui.Text("Includes motherboard, controller, storage and memory sensors. Off by default. Restart DeadWare after changing this setting.",12,"Muted"));
        var storageSensors=new CheckBox {Content="Storage sensors (temperature and health; restart to apply)",IsChecked=session.Settings.StorageSensors,Margin=new Thickness(0,14,0,8)};
        storageSensors.Checked+=(_,_)=>session.SetStorageSensors(true);storageSensors.Unchecked+=(_,_)=>session.SetStorageSensors(false);sensorSources.Children.Add(storageSensors);
        sensorSources.Children.Add(Ui.Text("Reads drive temperature, SMART health and wear from LibreHardwareMonitor every 30 seconds. Throughput and free space do not depend on this. Changing it takes effect after restarting DeadWare.",12,"Muted"));
        processMonitoring.IsChecked=session.Settings.ProcessMonitoring;
        System.Windows.Automation.AutomationProperties.SetName(processMonitoring,"Per-process monitoring");
        processMonitoring.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetProcessMonitoring(true);};
        processMonitoring.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetProcessMonitoring(false);};monitoring.Children.Add(processMonitoring);
        monitoring.Children.Add(Ui.Text(ProcessCopy.ScanSentence(session.Current?.Elevated??Collector.IsElevated,ProcessFeatures.DiskColumns,ProcessFeatures.ScanLoop,history:true),12,"Muted"));
        cpuCoreUsage.IsChecked=session.Settings.ShowCpuCoreUsage;
        System.Windows.Automation.AutomationProperties.SetName(cpuCoreUsage,"Show per-core CPU usage on usage graphs");
        cpuCoreUsage.Checked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetCpuCoreUsage(true);};
        cpuCoreUsage.Unchecked+=(_,_)=>{if(!syncingWidgetGraphs)session.SetCpuCoreUsage(false);};monitoring.Children.Add(cpuCoreUsage);
        monitoring.Children.Add(Ui.Text("One thin line per core under the total on the CPU usage card, its pop-out and the widget. Core traces are kept in the six-hour graph history.",12,"Muted"));
        var statusContent=new StackPanel();statusContent.Children.Add(settingsStatus);AutomationProperties.SetLiveSetting(settingsStatus,AutomationLiveSetting.Polite);
        settingsHistory=Ui.Button("Show in History and alerts",ShowClearProgress);settingsHistory.HorizontalAlignment=HorizontalAlignment.Left;settingsHistory.Margin=new(0,6,0,0);statusContent.Children.Add(settingsHistory);
        settingsStatusCard=Ui.Panel(statusContent,new Thickness(16,10,16,10));settingsStatusCard.Margin=new(0,0,0,12);settingsStatusCard.Visibility=Visibility.Collapsed;stack.Children.Add(settingsStatusCard);
        var tabs=SettingsTabs;AutomationProperties.SetName(tabs,"Settings categories");
        foreach(var (title,group) in new[]{("Monitoring",monitoring),("Appearance",appearance),("Widget",widgetGroup),("Pop-outs",popouts),("Startup",startupGroup),("History and alerts",history)})tabs.Items.Add(new TabItem {Header=title,Content=group});
        tabs.SelectedIndex=0;
        stack.Children.Add(SettingsCard(tabs));UpdateSettingsStatus();
        return new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    private static Border SettingsCard(UIElement body)
    {
        var card=Ui.Panel(body,new Thickness(16));card.Margin=new(0,0,0,12);return card;
    }
    private void SettingsClearChanged(HistoryClearStore _)=>UpdateSettingsStatus();
    /// <summary>Selects History and alerts and scrolls the clear-progress results into view; the exclusions list above them can be long.</summary>
    private void ShowClearProgress()
    {
        SettingsTabs.SelectedItem=SettingsTab("History and alerts");UpdateLayout();
        settingsClearProgress.BringIntoView();
        if(settingsClearProgress.TryAgain.IsEnabled&&settingsClearProgress.TryAgain.IsVisible)settingsClearProgress.TryAgain.Focus();else settingsClearProgress.Focus();
    }
    private void SetStartupResult(string text){startupResult.Text=text;UpdateSettingsStatus();}
    private void UpdateSettingsStatus()
    {
        var lines=session.ClearLines();bool running=lines.Any(line=>line.State.Phase is HistoryClearPhase.Queued or HistoryClearPhase.Clearing);
        string clear=running?HistoryClearCopy.Header(lines):lines.Any(line=>line.State.Phase is HistoryClearPhase.Failed or HistoryClearPhase.Unavailable)?HistoryClearCopy.Summary(lines):"";
        string text=startupResult.Text.Length>0?startupResult.Text:startupQueryFailed?startupStatus.Text:"";
        if(clear.Length>0)text+=(text.Length>0?" · ":"")+"Clear history · "+clear;
        settingsHistory.Visibility=clear.Length>0?Visibility.Visible:Visibility.Collapsed;
        settingsStatusCard.Visibility=text.Length>0?Visibility.Visible:Visibility.Collapsed;
        if(settingsStatus.Text==text)return;
        settingsStatus.Text=text;
        // Announce a new message, not a percentage tick of the same one.
        string announce=System.Text.RegularExpressions.Regex.Replace(text,@"\d+","#");
        if(text.Length==0||announce==announcedStatus)return;
        announcedStatus=announce;SettingsStatusAnnounced?.Invoke(text);
        (UIElementAutomationPeer.FromElement(settingsStatus)??UIElementAutomationPeer.CreatePeerForElement(settingsStatus))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void SetStartupBusy(bool busy)
    {
        startupBusy=busy;startup.IsEnabled=!busy;startupRepair.IsEnabled=!busy;startupDisable.IsEnabled=!busy;startupRefresh.IsEnabled=!busy;
    }
    private void ShowStartupStatus(StartupRegistrationStatus registration)
    {
        syncingStartup=true;startup.IsChecked=registration.Error is null?registration.Ready:null;syncingStartup=false;
        startupStatus.Text=registration.Message+(registration.Executable is null?"":"\nRegistered executable: "+registration.Executable)+(registration.Error is null?"":"\n"+registration.Error);
        startupQueryFailed=registration.Error is not null;UpdateSettingsStatus();
        bool ownedOrUnknown=!registration.Registered||registration.Owned;
        startup.IsEnabled=ownedOrUnknown;startupRepair.IsEnabled=ownedOrUnknown;startupDisable.IsEnabled=ownedOrUnknown;
    }
    private async Task RefreshStartupAsync()
    {
        if(startupBusy)return;SetStartupBusy(true);SetStartupResult("");
        var registration=await Task.Run(StartupRegistration.Query);SetStartupBusy(false);ShowStartupStatus(registration);
    }
    private async Task ChangeStartupAsync(bool enabled)
    {
        if(startupBusy)return;SetStartupBusy(true);SetStartupResult("Waiting for Windows administrator approval…");
        var result=await StartupRegistration.ConfigureAsync(enabled);SetStartupBusy(false);ShowStartupStatus(result.Registration);SetStartupResult(result.Message);
    }
    /// <summary>Adds a star-sized column with an upper-cased header: a text column bound to <paramref name="binding"/>, or <paramref name="column"/> sorted by it.</summary>
    private static void Column(DataGrid table,string title,string binding,double weight,DataGridColumn? column=null)
    {
        if(column is null)column=new DataGridTextColumn {Binding=new Binding(binding)};
        else{column.SortMemberPath=binding;column.ClipboardContentBinding=new Binding(binding);}
        column.Header=title.ToUpperInvariant();column.Width=new DataGridLength(weight,DataGridLengthUnitType.Star);
        table.Columns.Add(column);
    }
    public void ShowUpdates()=>Navigate("updates");
    public void ShowAlerts()=>Navigate("alerts");
    private void ShowUsageDay(DateTimeOffset at) {Navigate("usage");if(pages["usage"] is UsageHistoryPage usage)usage.ShowDay(at);}
    // The request can come from the separate Usage history window, so the dashboard is shown as App.ShowDashboard shows it; nothing happens during Quit or sign-out.
    private void ShowConnectionsHistory(ConnectionsHistoryRequest request)
    {
        if(lifetime.IsEnding||Dispatcher.HasShutdownStarted)return;
        present(this);if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Activate();
        Navigate("connections");
        if(pages["connections"] is ConnectionsPage connections)connections.ShowHistory(request.From,request.To,request.Application);
    }
    private void RefreshUpdateNotice()
    {
        if(lifetime.IsEnding||Dispatcher.HasShutdownStarted)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.InvokeAsync(RefreshUpdateNotice);return;}
        updateNotice.ApplyState(session.Updates.State);
        if(page=="updates")updateNotice.Visibility=Visibility.Collapsed;
    }
    private void Navigate(string destination)
    {
        gpuControls.Visibility=destination=="overview"?Visibility.Visible:Visibility.Collapsed;
        liveToolbar.Visibility=destination is "cooling" or "battery" or "usage" or "alerts" or "updates" or "connections" or "processes"?Visibility.Collapsed:Visibility.Visible;
        foreach(var (key,button) in navigation)button.Tag=key==destination?"active":null;
        page=destination;content.Content=pages[destination];heading.Text=destination switch {"network"=>"Network","storage"=>"Storage","processes"=>"Processes","connections"=>"Connections","cooling"=>"Cooling Health","battery"=>"Battery history","usage"=>"Usage history","alerts"=>"Alerts","updates"=>"Keep DeadWare current","sensors"=>"Hardware sensors","desktop"=>"Settings",_=>"System overview"};
        RefreshUpdateNotice();Refresh();
        // A freshly shown Settings page refreshes the app suggestions from the block's own Loaded; only an already-shown block is refreshed
        // here. Unloaded pages (the offscreen shell preview, the UI checks) must never open a history store.
        if(destination=="desktop"){_ = RefreshStartupAsync();if(excludedApps is {IsLoaded:true})_ = excludedApps.Suggestions.RefreshAsync();}
    }
    private void Refresh()
    {
        themePicker.SetSelection(session.Settings.Theme);
        monitoringInfo.Text=$"Local monitoring\n{session.Settings.PollingIntervalSeconds:0.#} s polling · 6 h saved graphs · usage history\nv{BuildInfo.Version}";
        syncingWidgetGraphs=true;
        try
        {
            var selectedGraphs=WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs);
            foreach(var choice in widgetGraphChoices)choice.Value.IsChecked=selectedGraphs.Contains(choice.Key);
            widgetConnectionsMap.IsChecked=session.Settings.WidgetConnectionsMap;
            widgetAlwaysOnTop.IsChecked=session.Settings.WidgetAlwaysOnTop;
            joinedPopouts.IsChecked=session.Settings.JoinCpuGpuPopouts;
            gpuFanSpeed.IsChecked=session.Settings.ShowGpuFanSpeed;
            cpuPumpSpeed.IsChecked=session.Settings.ShowCpuPumpSpeed;
            cpuCoreUsage.IsChecked=session.Settings.ShowCpuCoreUsage;
            // The Processes page's Turn on changes the same setting.
            processMonitoring.IsChecked=session.Settings.ProcessMonitoring;
            graphSmoothing.IsChecked=session.Settings.GraphSmoothing;
            usageRecording.IsChecked=session.Settings.UsageRecording;
            alertNotifications.IsChecked=session.Settings.AlertNotifications;
            glass.SelectedItem=session.Settings.GlassLevel;
        }
        finally {syncingWidgetGraphs=false;}
        var r=session.Current;bool noBattery=r?.Battery.PowerState==BatteryPowerState.NoBattery;
        batteryNavigation.Visibility=noBattery?Visibility.Collapsed:Visibility.Visible;
        if(noBattery&&page=="battery"){Navigate("overview");return;}
        if(r is null){status.Text="Discovering hardware and starting the shared collector…\n"+session.GraphHistory.Status;subtitle.Text="Discovering hardware and starting the shared collector…";return;}
        var signature=string.Join('|',r.Gpus.Select(d=>d.Id+":"+d.Name));
        if(signature!=gpuSignature)
        {
            gpuSignature=signature;syncingGpu=true;gpu.ItemsSource=r.Gpus;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;
        }
        else if((gpu.SelectedItem as Device)?.Id!=session.SelectedGpuId){syncingGpu=true;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;}
        status.Text=(session.Stale?"STALE — the collector has not published a recent sample. ":"")+r.HardwareStatus+"\n"+r.NetworkStatus+"\n"+session.GraphHistory.Status+(session.SettingsStatus.Length>0?"\n"+session.SettingsStatus:"");
        if(session.Settings.ShowCpuCoreUsage&&r.CpuCoreCount==0)status.Text+=" · Per-core CPU usage unavailable.";
        memory.Text=$"Memory  {Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))}    ·    Last sample {r.Sample.Timestamp:HH:mm:ss}";
        subtitle.Text=$"{r.CpuName} · {session.GpuName} · {Ui.Bytes(session.Value("memory/total"))} · last sample {Math.Max(0,(DateTimeOffset.Now-r.Sample.Timestamp).TotalSeconds):0.0} s ago";
        networkStatus.Text=r.EtwStatus;RefreshTables();
    }
    private async void RefreshTables()
    {
        var r=session.Current;if(r is null||!IsVisible)return;
        if(page=="network")
        {
            var rows=r.Processes.Where(p=>p.Name.Contains(appSearch.Text,StringComparison.OrdinalIgnoreCase)||p.Pid.ToString().Contains(appSearch.Text)).OrderByDescending(p=>(p.DownloadBytesPerSecond??0)+(p.UploadBytesPerSecond??0))
                .Select(p=>new {p.Name,p.Pid,Download=Ui.Bytes(session.Stale?null:p.DownloadBytesPerSecond,true),Upload=Ui.Bytes(session.Stale?null:p.UploadBytesPerSecond,true),Received=Ui.Bytes(p.ReceivedBytes),Sent=Ui.Bytes(p.SentBytes)}).ToArray();
            apps.ItemsSource=rows;networkEmpty.Text=rows.Length==0?(r.Processes.Count==0?"No attributed traffic yet. Check the accounting status above.":"No applications match your search."):$"{rows.Length} applications";
        }
        if(page=="sensors")
        {
            if(refreshingSensors)return;
            refreshingSensors=true;
            try
            {
                string query=sensorSearch.Text;int seconds=session.HistorySeconds;double now=session.Collector.ElapsedSeconds;
                var matching=r.Sensors.Where(s=>(s.Hardware+" "+s.Name).Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
                var summaries=await Task.Run(()=>session.Collector.History.Summaries(matching.Select(s=>s.Id),now,seconds));
                // A range/search/page change must not publish an older query over the new view.
                if(!IsVisible||page!="sensors"||query!=sensorSearch.Text||seconds!=session.HistorySeconds)return;
                var rows=matching.Select(s=>
                {
                    var summary=summaries[s.Id];
                    return new {Name=$"{s.Hardware}  /  {s.Name}",Current=Ui.Number(session.Stale?null:s.Value," "+s.Unit),Minimum=Ui.Number(summary.Minimum," "+s.Unit),Average=Ui.Number(summary.Average," "+s.Unit),Maximum=Ui.Number(summary.Maximum," "+s.Unit)};
                }).ToArray();sensors.ItemsSource=rows;sensorCount.Text=$"{rows.Length} sensors · statistics over {HistoryRanges.Label(seconds)} · — means unavailable";
            }
            finally {refreshingSensors=false;}
        }
    }
    public void SetTrayUnavailable()=>trayNotice.Text="System tray unavailable. Keep this window open; use Quit DeadWare to exit.";
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        switch(lifetime.GetDashboardCloseBehavior(trayAvailable()))
        {
            case DashboardCloseBehavior.AllowClose:return;
            case DashboardCloseBehavior.Hide:e.Cancel=true;Hide();return;
            default:e.Cancel=true;SetTrayUnavailable();return;
        }
    }
}
/// <summary>The Network page's Application cell: the row's bare Name and, for an app in Don't record these apps, a Muted " · not recorded" after it.
/// The cell rebuilds its text on <see cref="UiSession.Changed"/> only when the name or the mark changed, so it does not depend on the table being refreshed.</summary>
internal sealed class NotRecordedNameColumn(UiSession session) : DataGridTemplateColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell,object dataItem)
    {
        var text=new TextBlock {Style=DataGridTextColumn.DefaultElementStyle};
        (string Name,bool Marked)? shown=null;bool listening=false;
        void Build()
        {
            object? item=text.DataContext??dataItem;
            string name=item?.GetType().GetProperty("Name")?.GetValue(item) as string??"";
            bool marked=session.IsExcluded(name);
            if(shown==(name,marked))return;
            shown=(name,marked);
            text.Inlines.Clear();text.Inlines.Add(new System.Windows.Documents.Run(name));
            if(!marked)return;
            var mark=new System.Windows.Documents.Run(ProcessCopy.NotRecordedSuffix);mark.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Muted");
            text.Inlines.Add(mark);
        }
        void OnChanged()
        {
            if(text.Dispatcher.HasShutdownStarted)return;
            if(!text.Dispatcher.CheckAccess()){text.Dispatcher.InvokeAsync(OnChanged);return;}
            Build();
        }
        text.DataContextChanged+=(_,_)=>Build();
        text.Loaded+=(_,_)=>{if(!listening){listening=true;session.Changed+=OnChanged;}Build();};
        text.Unloaded+=(_,_)=>{if(listening){listening=false;session.Changed-=OnChanged;}};
        Build();
        return text;
    }
}

internal sealed class BatteryHistoryWindow : Window
{
    public BatteryHistoryWindow(UiSession session)
    {
        Title="Battery history — DeadWare";MinWidth=620;MinHeight=520;Ui.Icon(this);session.Place(this,"batteryhistory",940,830);
        var panel=new DockPanel {Margin=new Thickness(16)};
        var header=new DockPanel {Margin=new Thickness(6,0,6,8)};
        var pin=new CheckBox {Content="Always on top",VerticalAlignment=VerticalAlignment.Center,IsChecked=Topmost};
        pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
        header.Children.Add(Ui.Heading("Battery history",24));DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        panel.Children.Add(new BatteryHistoryPage(session,false));Content=panel;
    }
}
internal sealed class UsageHistoryWindow : Window
{
    public UsageHistoryWindow(UiSession session)
    {
        Title="Usage history — DeadWare";MinWidth=720;MinHeight=560;Ui.Icon(this);session.Place(this,"usagehistory",1100,900);
        var panel=new DockPanel {Margin=new Thickness(16)};
        var header=new DockPanel {Margin=new Thickness(6,0,6,8)};
        var pin=new CheckBox {Content="Always on top",VerticalAlignment=VerticalAlignment.Center,IsChecked=Topmost};
        pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
        header.Children.Add(Ui.Heading("Usage history",24));DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        panel.Children.Add(new UsageHistoryPage(session,false));Content=panel;
    }
}
internal sealed class GraphWindow : Window
{
    public GraphWindow(UiSession session,string kind)
    {
        Title=$"DeadWare · {Ui.Title(kind)}";MinWidth=300;MinHeight=180;Ui.Icon(this);session.Place(this,kind,420,230);
        Content=new CompactGraph(session,kind,this) {Margin=new Thickness(6)};
    }
    /// <summary>One drive's read / write pop-out. Not placed or restored with the saved windows.</summary>
    public GraphWindow(UiSession session,StorageDrive drive)
    {
        Title=$"Disk · {drive.Model}";MinWidth=300;MinHeight=180;Width=420;Height=230;Ui.Icon(this);
        Content=new CompactGraph(session,"storage",drive.Id,this) {Margin=new Thickness(6)};
    }
}
internal sealed class WidgetWindow : Window
{
    private readonly UiSession session;
    private readonly Dictionary<string,WidgetGraph> graphs=new(StringComparer.Ordinal);
    private readonly Grid cards=new();
    private readonly ScrollViewer scroll=new() {HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private ConnectionsWidget? connections;
    private readonly TextBlock empty=Ui.Text("No graphs selected. Choose graphs in Settings.",12,"Muted");
    private string? appliedSelection;
    public WidgetWindow(UiSession session)
    {
        this.session=session;Title="DeadWare · Quick glance";MinWidth=300;
        int count=WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs).Count;
        bool showMap=session.Settings.WidgetConnectionsMap;
        MinHeight=WidgetGraphSelection.MinimumHeight(count,showMap);Ui.Icon(this);session.Place(this,"widget",340,WidgetGraphSelection.DefaultHeight(count,showMap));
        var grid=new Grid {Margin=new Thickness(8)};grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        foreach(string kind in WidgetGraphSelection.Kinds)
        {
            int row=cards.RowDefinitions.Count;cards.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
            var graph=new WidgetGraph(session,kind);graphs.Add(kind,graph);Grid.SetRow(graph,row);cards.Children.Add(graph);
        }
        cards.RowDefinitions.Add(new() {Height=new GridLength(0)});
        scroll.Content=cards;
        var area=new Grid();area.Children.Add(scroll);empty.TextAlignment=TextAlignment.Center;empty.HorizontalAlignment=HorizontalAlignment.Center;empty.VerticalAlignment=VerticalAlignment.Center;empty.Margin=new(12);area.Children.Add(empty);grid.Children.Add(area);
        Content=grid;
        DpiChanged+=(_,_)=>{if(session.Settings.WidgetConnectionsMap)Dispatcher.BeginInvoke(new Action(()=>NativePlacement.Restore(this,null,340,WidgetGraphSelection.DefaultHeight(WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs).Count,true))));};
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;Refresh();
    }
    private void Refresh()
    {
        Topmost=session.Settings.WidgetAlwaysOnTop;
        var selected=WidgetGraphSelection.Normalize(session.Settings.WidgetGraphs);
        bool showMap=session.Settings.WidgetConnectionsMap;
        string signature=string.Join('|',selected)+"|map="+showMap;
        if(signature!=appliedSelection)
        {
            double minimum=WidgetGraphSelection.MinimumHeight(selected.Count,showMap);
            var before=minimum>MinHeight?NativePlacement.CaptureNormal(this):null;
            if(showMap)NativePlacement.SetScrollableMinimumHeight(this,minimum,session.Settings.Windows.GetValueOrDefault("widget"));
            else {NativePlacement.ClearScrollableMinimumHeight(this);MinHeight=minimum;}
            if(showMap&&connections is null) {connections=new ConnectionsWidget(session);Grid.SetRow(connections,cards.RowDefinitions.Count-1);cards.Children.Add(connections);}
            if(connections is not null)connections.Visibility=showMap?Visibility.Visible:Visibility.Collapsed;
            bool mapOnly=showMap&&selected.Count==0;
            cards.RowDefinitions[cards.RowDefinitions.Count-1].Height=mapOnly?new GridLength(1,GridUnitType.Star):new GridLength(showMap?150:0);
            scroll.VerticalScrollBarVisibility=showMap&&!mapOnly?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
            foreach(var graph in graphs)
            {
                bool visible=selected.Contains(graph.Key);
                graph.Value.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
                graph.Value.MinHeight=showMap&&visible?120:0;
                var row=cards.RowDefinitions[Grid.GetRow(graph.Value)];
                row.MinHeight=showMap&&visible?120:0;
                row.Height=visible?new GridLength(1,GridUnitType.Star):new GridLength(0);
            }
            empty.Visibility=selected.Count==0&&!showMap?Visibility.Visible:Visibility.Collapsed;
            appliedSelection=signature;
            if(before is not null)NativePlacement.Restore(this,before,340,WidgetGraphSelection.DefaultHeight(selected.Count,showMap));
        }
        foreach(var graph in graphs.Values)if(graph.Visibility==Visibility.Visible)graph.Refresh();
        if(showMap)connections!.Refresh();
    }
}
