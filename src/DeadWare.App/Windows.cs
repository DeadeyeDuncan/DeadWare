using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using DeadWare.Core;
namespace DeadWare.App;
public sealed class MainWindow : Window
{
    private readonly UiSession session;
    private readonly ApplicationLifetime lifetime;
    private readonly Func<bool> trayAvailable;
    private readonly ContentControl content=new();
    private readonly TextBlock heading=Ui.Text("System overview",28),subtitle=Ui.Text("The numbers you care about. Room to breathe.",13,"Muted"),status=Ui.Text("Starting the shared collector…",11,"Muted"),memory=Ui.Text("Memory —",12,"Muted"),networkStatus=Ui.Text("",12,"Warning"),networkEmpty=Ui.Text("",13,"Muted"),sensorCount=Ui.Text("",12,"Muted");
    private readonly ComboBox gpu=new(){MaxWidth=340,VerticalAlignment=VerticalAlignment.Center,DisplayMemberPath="Name",SelectedValuePath="Id"};
    private readonly StackPanel gpuControls;
    private readonly DockPanel liveToolbar;
    private readonly TextBox appSearch=new(){Width=250},sensorSearch=new(){Width=280};
    private readonly DataGrid apps=new(),sensors=new();
    private readonly Dictionary<string,FrameworkElement> pages=new();
    private readonly CheckBox startup=new() {Content="Start with Windows (administrator)",Margin=new Thickness(0,22,0,8)};
    private readonly TextBlock startupStatus=Ui.Text("Startup status is checked when this page opens.",12,"Muted"),startupResult=Ui.Text("",12,"Warning");
    private Button startupRepair=null!,startupDisable=null!,startupRefresh=null!;
    private string page="overview",gpuSignature="";
    private bool syncingGpu;
    private bool refreshingSensors;
    private bool syncingStartup,startupBusy;
    private readonly TextBlock trayNotice=Ui.Text("Close hides to the system tray.\nQuit stops monitoring.",10,"Muted");
    public MainWindow(UiSession session,ApplicationLifetime lifetime,Func<bool> trayAvailable,Action quit)
    {
        this.session=session;this.lifetime=lifetime;this.trayAvailable=trayAvailable;Title="DeadWare";MinWidth=900;MinHeight=640;Ui.Icon(this);session.Place(this,"main",1220,900);
        var shell=new Grid();shell.ColumnDefinitions.Add(new(){Width=new GridLength(185)});shell.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Content=shell;
        var sidebar=new DockPanel {Margin=new Thickness(14,24,14,16)};Grid.SetColumn(sidebar,0);shell.Children.Add(sidebar);
        var brand=Ui.Row(Ui.Logo(),Ui.Text("DeadWare",21));brand.Margin=new(0,0,0,27);DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
        var sideBottom=new StackPanel();DockPanel.SetDock(sideBottom,Dock.Bottom);sidebar.Children.Add(sideBottom);
        sideBottom.Children.Add(Ui.Text("COLOR THEME",10,"Muted"));var theme=new ComboBox {ItemsSource=Theme.Names,SelectedItem=session.Settings.Theme,Margin=new Thickness(0,8,0,15)};theme.SelectionChanged+=(_,_)=>{if(theme.SelectedItem is string name)session.SetTheme(name);};sideBottom.Children.Add(theme);
        var elevate=Ui.Button("Restart as administrator",session.RestartElevated);elevate.FontSize=10;sideBottom.Children.Add(elevate);
        var quitButton=Ui.Button("Quit DeadWare",quit);quitButton.FontSize=11;sideBottom.Children.Add(quitButton);trayNotice.Margin=new Thickness(3,5,3,8);sideBottom.Children.Add(trayNotice);
        sideBottom.Children.Add(Ui.Text($"Local monitoring\n1 second samples · 6 h saved graphs\nv{BuildInfo.Version}",10,"Muted"));
        var nav=new StackPanel();sidebar.Children.Add(nav);
        foreach(var item in new[]{("overview","▦  Overview"),("network","⇄  Network"),("cooling","◷  Cooling Health"),("battery","▤  Battery history"),("sensors","≡  Sensors"),("desktop","▱  Desktop layout")})
        {var key=item.Item1;var button=Ui.Button(item.Item2,()=>Navigate(key));button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Margin=new(0,3,0,3);button.Padding=new(10,13,10,13);nav.Children.Add(button);}
        var main=new DockPanel {Margin=new Thickness(18,25,26,16)};Grid.SetColumn(main,1);shell.Children.Add(main);
        var footer=new StackPanel {Margin=new Thickness(4,10,4,0)};footer.Children.Add(status);DockPanel.SetDock(footer,Dock.Bottom);main.Children.Add(footer);
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);main.Children.Add(header);
        var titleRow=new DockPanel();var widget=Ui.Button("↗ Open widget",()=>session.OpenDetached("widget"));widget.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(widget,Dock.Right);titleRow.Children.Add(widget);titleRow.Children.Add(heading);header.Children.Add(titleRow);subtitle.Margin=new(0,8,0,0);header.Children.Add(subtitle);
        liveToolbar=new DockPanel {Margin=new Thickness(0,18,0,12)};var ranges=Ui.Range(session);DockPanel.SetDock(ranges,Dock.Right);liveToolbar.Children.Add(ranges);var gpuLabel=Ui.Text("GPU",12,"Muted");gpuLabel.Margin=new(0,0,8,0);gpuControls=Ui.Row(gpuLabel,gpu);liveToolbar.Children.Add(gpuControls);header.Children.Add(liveToolbar);
        gpu.SelectionChanged+=(_,_)=>{if(!syncingGpu)session.SetGpu((gpu.SelectedItem as Device)?.Id);};
        main.Children.Add(content);
        pages["overview"]=Overview();pages["network"]=Network();pages["cooling"]=new CoolingHealthPage(session);pages["battery"]=new BatteryHistoryPage(session);pages["sensors"]=Sensors();pages["desktop"]=Desktop();Navigate("overview");
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;Closing+=OnClosing;
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
    }
    private FrameworkElement Overview()
    {
        var stack=new StackPanel();var charts=new UniformGrid {Columns=2,Rows=2,Height=530};
        foreach(var kind in new[]{"cpu","gpu","cputemp","gputemp"})charts.Children.Add(new ChartCard(session,kind));stack.Children.Add(charts);
        memory.Margin=new(8,8,8,8);stack.Children.Add(memory);stack.Children.Add(new ChartCard(session,"network"){Height=265});
        var note=Ui.Text("One collector feeds every window. Unknown readings stay blank; no synthetic data is shown.",11,"Muted");note.Margin=new(8);stack.Children.Add(note);
        return new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    private FrameworkElement Network()
    {
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(265)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(new ChartCard(session,"network"));
        var controls=new StackPanel {Margin=new Thickness(8,8,8,12)};networkStatus.Margin=new(0,0,0,10);controls.Children.Add(networkStatus);appSearch.ToolTip="Search application name or PID";System.Windows.Automation.AutomationProperties.SetName(appSearch,"Search applications");
        var searchLabel=Ui.Text("Find application",12,"Muted");searchLabel.Margin=new(0,0,12,0);controls.Children.Add(Ui.Row(searchLabel,appSearch));appSearch.TextChanged+=(_,_)=>RefreshTables();Grid.SetRow(controls,1);grid.Children.Add(controls);
        Column(apps,"Application","Name",2);Column(apps,"PID","Pid",.6);Column(apps,"↓ Download","Download",1);Column(apps,"↑ Upload","Upload",1);Column(apps,"Received","Received",1);Column(apps,"Sent","Sent",1);
        Grid.SetRow(apps,2);grid.Children.Add(apps);var foot=new StackPanel {Margin=new Thickness(8)};foot.Children.Add(networkEmpty);foot.Children.Add(Ui.Text("Per-app totals cover each current process lifetime in this monitoring session. Exited processes are removed. ETW attribution is approximate and differs from interface totals. No packet payloads are captured.",11,"Muted"));Grid.SetRow(foot,3);grid.Children.Add(foot);return grid;
    }
    private FrameworkElement Sensors()
    {
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var label=Ui.Text("Find sensor or hardware",12,"Muted");label.Margin=new(0,0,12,0);var search=Ui.Row(label,sensorSearch);search.Margin=new(8,4,8,16);grid.Children.Add(search);sensorSearch.ToolTip="Search sensors and device names";System.Windows.Automation.AutomationProperties.SetName(sensorSearch,"Search sensors");sensorSearch.TextChanged+=(_,_)=>RefreshTables();
        Column(sensors,"Hardware / sensor","Name",3);Column(sensors,"Current","Current",1);Column(sensors,"Min","Minimum",1);Column(sensors,"Average","Average",1);Column(sensors,"Max","Maximum",1);
        Grid.SetRow(sensors,1);grid.Children.Add(sensors);Grid.SetRow(sensorCount,2);sensorCount.Margin=new(8,12,8,0);grid.Children.Add(sensorCount);return grid;
    }
    private FrameworkElement Desktop()
    {
        var stack=new StackPanel {Margin=new Thickness(8)};
        stack.Children.Add(Ui.Text("Your readings, wherever you need them",20));var intro=Ui.Text("Open any graph in its own resizable native window. Choose Always on top to keep it visible while you work. All windows share the same readings, selected GPU, history range and theme.",14,"Muted");intro.Margin=new(0,12,0,24);stack.Children.Add(intro);
        foreach(var kind in new[]{"cpu","gpu","cputemp","gputemp","network"}){var key=kind;var button=Ui.Button("↗  "+Ui.Title(kind),()=>session.OpenDetached(key));button.HorizontalAlignment=HorizontalAlignment.Left;button.MinWidth=240;button.Margin=new(0,4,0,4);stack.Children.Add(button);}
        var batteryWindow=Ui.Button("↗  Battery history",()=>session.OpenDetached("batteryhistory"));batteryWindow.HorizontalAlignment=HorizontalAlignment.Left;batteryWindow.MinWidth=240;batteryWindow.Margin=new(0,4,0,4);stack.Children.Add(batteryWindow);
        var open=Ui.Button("↗  Compact desktop widget",()=>session.OpenDetached("widget"));open.HorizontalAlignment=HorizontalAlignment.Left;open.MinWidth=240;open.Margin=new(0,20,0,4);stack.Children.Add(open);
        var saved=Ui.Text("Positions, sizes and Always on top choices are saved as you work. Closing the dashboard hides it to the system tray while graphs and the widget keep monitoring. Choose Quit DeadWare to save the windows left open for next time and stop collection. Close an individual graph or widget to leave it closed next time.",12,"Muted");saved.Margin=new(0,20,0,0);stack.Children.Add(saved);
        var restore=new CheckBox {Content="Reopen graphs and widget from the previous session",IsChecked=session.Settings.RestoreOpenWindows,Margin=new Thickness(0,18,0,8)};
        restore.Checked+=(_,_)=>session.SetRestoreOpenWindows(true);restore.Unchecked+=(_,_)=>session.SetRestoreOpenWindows(false);stack.Children.Add(restore);
        startup.Checked+=async (_,_)=>{if(!syncingStartup)await ChangeStartupAsync(true);};
        startup.Unchecked+=async (_,_)=>{if(!syncingStartup)await ChangeStartupAsync(false);};
        stack.Children.Add(startup);stack.Children.Add(startupStatus);startupResult.Margin=new(0,8,0,4);stack.Children.Add(startupResult);
        var startInTray=new CheckBox {Content="Start in the system tray",IsChecked=session.Settings.StartInTray,Margin=new Thickness(0,10,0,6)};
        startInTray.Checked+=(_,_)=>session.SetStartInTray(true);startInTray.Unchecked+=(_,_)=>session.SetStartInTray(false);stack.Children.Add(startInTray);
        stack.Children.Add(Ui.Text("At Windows sign-in, hide only the dashboard. Graphs and the widget still follow the reopen setting above. Manual launches show the dashboard.",12,"Muted"));
        startupRepair=Ui.Button("Enable / repair startup",()=>_ = ChangeStartupAsync(true));
        startupDisable=Ui.Button("Disable startup",()=>_ = ChangeStartupAsync(false));
        startupRefresh=Ui.Button("Refresh status",()=>_ = RefreshStartupAsync());
        var startupButtons=new WrapPanel();startupButtons.Children.Add(startupRepair);startupButtons.Children.Add(startupDisable);startupButtons.Children.Add(startupRefresh);stack.Children.Add(startupButtons);
        stack.Children.Add(Ui.Text("Windows asks for administrator approval when changing startup. This applies to your Windows account and this executable location. After moving DeadWare, repair startup from the new location. The drive must be available when you sign in.",12,"Muted"));
        var extended=new CheckBox {Content="Extended sensors (may affect responsiveness)",IsChecked=session.Settings.ExtendedSensors,Margin=new Thickness(0,22,0,8)};
        extended.Checked+=(_,_)=>session.SetExtendedSensors(true);extended.Unchecked+=(_,_)=>session.SetExtendedSensors(false);stack.Children.Add(extended);
        stack.Children.Add(Ui.Text("Includes motherboard, controller, storage and memory sensors. Off by default. Restart DeadWare after changing this setting.",12,"Muted"));
        return new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    }
    private void SetStartupBusy(bool busy)
    {
        startupBusy=busy;startup.IsEnabled=!busy;startupRepair.IsEnabled=!busy;startupDisable.IsEnabled=!busy;startupRefresh.IsEnabled=!busy;
    }
    private void ShowStartupStatus(StartupRegistrationStatus registration)
    {
        syncingStartup=true;startup.IsChecked=registration.Error is null?registration.Ready:null;syncingStartup=false;
        startupStatus.Text=registration.Message+(registration.Executable is null?"":"\nRegistered executable: "+registration.Executable)+(registration.Error is null?"":"\n"+registration.Error);
        bool ownedOrUnknown=!registration.Registered||registration.Owned;
        startup.IsEnabled=ownedOrUnknown;startupRepair.IsEnabled=ownedOrUnknown;startupDisable.IsEnabled=ownedOrUnknown;
    }
    private async Task RefreshStartupAsync()
    {
        if(startupBusy)return;SetStartupBusy(true);startupResult.Text="";
        var registration=await Task.Run(StartupRegistration.Query);SetStartupBusy(false);ShowStartupStatus(registration);
    }
    private async Task ChangeStartupAsync(bool enabled)
    {
        if(startupBusy)return;SetStartupBusy(true);startupResult.Text="Waiting for Windows administrator approval…";
        var result=await StartupRegistration.ConfigureAsync(enabled);SetStartupBusy(false);ShowStartupStatus(result.Registration);startupResult.Text=result.Message;
    }
    private static void Column(DataGrid table,string title,string binding,double weight)=>table.Columns.Add(new DataGridTextColumn {Header=title,Binding=new Binding(binding),Width=new DataGridLength(weight,DataGridLengthUnitType.Star)});
    private void Navigate(string destination)
    {
        gpuControls.Visibility=destination=="overview"?Visibility.Visible:Visibility.Collapsed;
        liveToolbar.Visibility=destination is "cooling" or "battery"?Visibility.Collapsed:Visibility.Visible;
        page=destination;content.Content=pages[destination];heading.Text=destination switch {"network"=>"Your network, explained","cooling"=>"Cooling Health","battery"=>"Battery history","sensors"=>"Hardware sensors","desktop"=>"Make room for your work",_=>"System overview"};
        subtitle.Text=destination switch {"network"=>"See which applications are sending and receiving data.","cooling"=>"Learn your usual temperatures. Notice sustained changes.","battery"=>"See charge fall and recover. Keep the history for years.","sensors"=>"Current-session values and statistics over the selected range.","desktop"=>"A few useful numbers, wherever you need them.",_=>"The numbers you care about. Room to breathe."};Refresh();
        if(destination=="desktop")_ = RefreshStartupAsync();
    }
    private void Refresh()
    {
        var r=session.Current;if(r is null){status.Text="Discovering hardware and starting the shared collector…\n"+session.GraphHistory.Status;return;}
        var signature=string.Join('|',r.Gpus.Select(d=>d.Id+":"+d.Name));
        if(signature!=gpuSignature)
        {
            gpuSignature=signature;syncingGpu=true;gpu.ItemsSource=r.Gpus;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;
        }
        else if((gpu.SelectedItem as Device)?.Id!=session.SelectedGpuId){syncingGpu=true;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;}
        status.Text=(session.Stale?"STALE — the collector has not published a recent sample. ":"")+r.HardwareStatus+"\n"+r.NetworkStatus+"\n"+session.GraphHistory.Status+(session.SettingsStatus.Length>0?"\n"+session.SettingsStatus:"");
        memory.Text=$"Memory  {Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))}    ·    Last sample {r.Sample.Timestamp:HH:mm:ss}";
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
internal sealed class BatteryHistoryWindow : Window
{
    public BatteryHistoryWindow(UiSession session)
    {
        Title="Battery history — DeadWare";MinWidth=620;MinHeight=520;Ui.Icon(this);session.Place(this,"batteryhistory",940,830);
        var panel=new DockPanel {Margin=new Thickness(16)};
        var header=new DockPanel {Margin=new Thickness(6,0,6,8)};
        var pin=new CheckBox {Content="Always on top",VerticalAlignment=VerticalAlignment.Center,IsChecked=Topmost};
        pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);header.Children.Add(pin);
        header.Children.Add(Ui.Text("Battery history",24));DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);
        panel.Children.Add(new BatteryHistoryPage(session,false));Content=panel;
    }
}
internal sealed class GraphWindow : Window
{
    public GraphWindow(UiSession session,string kind)
    {
        Title=$"DeadWare · {Ui.Title(kind)}";MinWidth=300;MinHeight=180;Ui.Icon(this);session.Place(this,kind,420,230);
        Content=new CompactGraph(session,kind,this) {Margin=new Thickness(6)};
    }
}
internal sealed class WidgetWindow : Window
{
    private readonly UiSession session;
    private readonly TextBlock cpu=Ui.Text("—",22,"Cpu"),gpu=Ui.Text("—",22,"Gpu"),memory=Ui.Text("—",12,"Muted"),network=Ui.Text("—",13),status=Ui.Text("",10,"Muted");
    public WidgetWindow(UiSession session)
    {
        this.session=session;Title="DeadWare · Quick glance";MinWidth=300;MinHeight=320;Ui.Icon(this);Topmost=true;session.Place(this,"widget",340,390);
        var stack=new StackPanel {Margin=new Thickness(18)};var pin=new CheckBox {Content="Always on top",IsChecked=Topmost};pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;stack.Children.Add(pin);
        stack.Children.Add(Ui.Text("CPU",11,"Muted"));cpu.Margin=new(0,3,0,18);stack.Children.Add(cpu);stack.Children.Add(Ui.Text("GPU",11,"Muted"));gpu.Margin=new(0,3,0,18);stack.Children.Add(gpu);memory.Margin=new(0,0,0,15);stack.Children.Add(memory);network.Margin=new(0,0,0,15);stack.Children.Add(network);stack.Children.Add(status);Content=stack;
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;Refresh();
    }
    private void Refresh()
    {
        double? V(string id)=>session.Stale?null:session.Value(id);
        cpu.Text=$"{Ui.Number(V("cpu/usage")," %")}   {Ui.Number(V("cpu/temperature")," °C")}";
        gpu.Text=$"{Ui.Number(V(session.MetricId("gpu"))," %")}   {Ui.Number(V(session.MetricId("gputemp"))," °C")}";
        memory.Text=$"Memory  {Ui.Bytes(V("memory/used"))} / {Ui.Bytes(V("memory/total"))}";network.Text=$"↓ {Ui.Bytes(V("network/download"),true)}    ↑ {Ui.Bytes(V("network/upload"),true)}";
        status.Text=session.Current is null?"Discovering hardware…":session.Stale?"Readings stale · open main window for status":"Shared collector · — means unavailable\n"+session.GpuName;
    }
}
