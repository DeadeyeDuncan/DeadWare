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
    private readonly ContentControl content=new();
    private readonly TextBlock heading=Ui.Text("System overview",28),subtitle=Ui.Text("The numbers you care about. Room to breathe.",13,"Muted"),status=Ui.Text("Starting the shared collector…",11,"Muted"),memory=Ui.Text("Memory —",12,"Muted"),networkStatus=Ui.Text("",12,"Warning"),networkEmpty=Ui.Text("",13,"Muted"),sensorCount=Ui.Text("",12,"Muted");
    private readonly ComboBox gpu=new(){MaxWidth=340,DisplayMemberPath="Name",SelectedValuePath="Id"};
    private readonly TextBox appSearch=new(){Width=250},sensorSearch=new(){Width=280};
    private readonly DataGrid apps=new(),sensors=new();
    private readonly Dictionary<string,FrameworkElement> pages=new();
    private string page="overview",gpuSignature="";
    private bool syncingGpu,closing,closed;
    public MainWindow(UiSession session)
    {
        this.session=session;Title="DeadWare";MinWidth=900;MinHeight=640;Ui.Icon(this);session.Place(this,"main",1220,900);
        var shell=new Grid();shell.ColumnDefinitions.Add(new(){Width=new GridLength(185)});shell.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Content=shell;
        var sidebar=new DockPanel {Margin=new Thickness(14,24,14,16)};Grid.SetColumn(sidebar,0);shell.Children.Add(sidebar);
        var brand=Ui.Row(Ui.Logo(),Ui.Text("DeadWare",21));brand.Margin=new(0,0,0,27);DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
        var sideBottom=new StackPanel();DockPanel.SetDock(sideBottom,Dock.Bottom);sidebar.Children.Add(sideBottom);
        sideBottom.Children.Add(Ui.Text("COLOR THEME",10,"Muted"));var theme=new ComboBox {ItemsSource=Theme.Names,SelectedItem=session.Settings.Theme,Margin=new Thickness(0,8,0,15)};theme.SelectionChanged+=(_,_)=>{if(theme.SelectedItem is string name)session.SetTheme(name);};sideBottom.Children.Add(theme);
        var elevate=Ui.Button("Restart as administrator",session.RestartElevated);elevate.FontSize=10;sideBottom.Children.Add(elevate);
        sideBottom.Children.Add(Ui.Text($"Local monitoring\n1 second samples · 15 min memory\nv{BuildInfo.Version}",10,"Muted"));
        var nav=new StackPanel();sidebar.Children.Add(nav);
        foreach(var item in new[]{("overview","▦  Overview"),("network","⇄  Network"),("sensors","≡  Sensors"),("desktop","▱  Desktop layout")})
        {var key=item.Item1;var button=Ui.Button(item.Item2,()=>Navigate(key));button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Margin=new(0,3,0,3);button.Padding=new(10,13,10,13);nav.Children.Add(button);}
        var main=new DockPanel {Margin=new Thickness(18,25,26,16)};Grid.SetColumn(main,1);shell.Children.Add(main);
        var footer=new StackPanel {Margin=new Thickness(4,10,4,0)};footer.Children.Add(status);DockPanel.SetDock(footer,Dock.Bottom);main.Children.Add(footer);
        var header=new StackPanel();DockPanel.SetDock(header,Dock.Top);main.Children.Add(header);
        var titleRow=new DockPanel();var widget=Ui.Button("↗ Open widget",()=>session.OpenDetached("widget"));widget.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(widget,Dock.Right);titleRow.Children.Add(widget);titleRow.Children.Add(heading);header.Children.Add(titleRow);subtitle.Margin=new(0,8,0,0);header.Children.Add(subtitle);
        var toolbar=new DockPanel {Margin=new Thickness(0,18,0,12)};var ranges=Ui.Range(session);DockPanel.SetDock(ranges,Dock.Right);toolbar.Children.Add(ranges);var gpuLabel=Ui.Text("GPU",12,"Muted");gpuLabel.Margin=new(0,0,8,0);toolbar.Children.Add(Ui.Row(gpuLabel,gpu));header.Children.Add(toolbar);
        gpu.SelectionChanged+=(_,_)=>{if(!syncingGpu)session.SetGpu((gpu.SelectedItem as Device)?.Id);};
        main.Children.Add(content);
        pages["overview"]=Overview();pages["network"]=Network();pages["sensors"]=Sensors();pages["desktop"]=Desktop();Navigate("overview");
        session.Changed+=Refresh;Closed+=(_,_)=>session.Changed-=Refresh;Closing+=OnClosing;
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
        var open=Ui.Button("↗  Compact desktop widget",()=>session.OpenDetached("widget"));open.HorizontalAlignment=HorizontalAlignment.Left;open.MinWidth=240;open.Margin=new(0,20,0,4);stack.Children.Add(open);
        var saved=Ui.Text("Window positions, sizes and topmost choices are remembered. Closing the main window also closes its graphs and stops collection.",12,"Muted");saved.Margin=new(0,20,0,0);stack.Children.Add(saved);
        var extended=new CheckBox {Content="Extended sensors (may affect responsiveness)",IsChecked=session.Settings.ExtendedSensors,Margin=new Thickness(0,22,0,8)};
        extended.Checked+=(_,_)=>session.SetExtendedSensors(true);extended.Unchecked+=(_,_)=>session.SetExtendedSensors(false);stack.Children.Add(extended);
        stack.Children.Add(Ui.Text("Includes motherboard, controller, storage and memory sensors. Off by default. Restart DeadWare after changing this setting.",12,"Muted"));
        return stack;
    }
    private static void Column(DataGrid table,string title,string binding,double weight)=>table.Columns.Add(new DataGridTextColumn {Header=title,Binding=new Binding(binding),Width=new DataGridLength(weight,DataGridLengthUnitType.Star)});
    private void Navigate(string destination)
    {
        page=destination;content.Content=pages[destination];heading.Text=destination switch {"network"=>"Your network, explained","sensors"=>"Hardware sensors","desktop"=>"Make room for your work",_=>"System overview"};
        subtitle.Text=destination switch {"network"=>"See which applications are sending and receiving data.","sensors"=>"Live values and statistics over the selected history range.","desktop"=>"A few useful numbers, wherever you need them.",_=>"The numbers you care about. Room to breathe."};Refresh();
    }
    private void Refresh()
    {
        var r=session.Current;if(r is null){status.Text="Discovering hardware and starting the shared collector…";return;}
        var signature=string.Join('|',r.Gpus.Select(d=>d.Id+":"+d.Name));
        if(signature!=gpuSignature)
        {
            gpuSignature=signature;syncingGpu=true;gpu.ItemsSource=r.Gpus;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;
        }
        else if((gpu.SelectedItem as Device)?.Id!=session.SelectedGpuId){syncingGpu=true;gpu.SelectedValue=session.SelectedGpuId;syncingGpu=false;}
        status.Text=(session.Stale?"STALE — the collector has not published a recent sample. ":"")+r.HardwareStatus+"\n"+r.NetworkStatus+(session.SettingsStatus.Length>0?"\n"+session.SettingsStatus:"");
        memory.Text=$"Memory  {Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))}    ·    Last sample {r.Sample.Timestamp:HH:mm:ss}";
        networkStatus.Text=r.EtwStatus;RefreshTables();
    }
    private void RefreshTables()
    {
        var r=session.Current;if(r is null)return;
        if(page=="network")
        {
            var rows=r.Processes.Where(p=>p.Name.Contains(appSearch.Text,StringComparison.OrdinalIgnoreCase)||p.Pid.ToString().Contains(appSearch.Text)).OrderByDescending(p=>(p.DownloadBytesPerSecond??0)+(p.UploadBytesPerSecond??0))
                .Select(p=>new {p.Name,p.Pid,Download=Ui.Bytes(session.Stale?null:p.DownloadBytesPerSecond,true),Upload=Ui.Bytes(session.Stale?null:p.UploadBytesPerSecond,true),Received=Ui.Bytes(p.ReceivedBytes),Sent=Ui.Bytes(p.SentBytes)}).ToArray();
            apps.ItemsSource=rows;networkEmpty.Text=rows.Length==0?(r.Processes.Count==0?"No attributed traffic yet. Check the accounting status above.":"No applications match your search."):$"{rows.Length} applications";
        }
        if(page=="sensors")
        {
            var rows=r.Sensors.Where(s=>(s.Hardware+" "+s.Name).Contains(sensorSearch.Text,StringComparison.OrdinalIgnoreCase)).Select(s=>
            {
                var summary=SampleHistory.Statistics(session.Collector.History.Series(s.Id,session.Collector.ElapsedSeconds,session.HistorySeconds));
                return new {Name=$"{s.Hardware}  /  {s.Name}",Current=Ui.Number(session.Stale?null:s.Value," "+s.Unit),Minimum=Ui.Number(summary.Minimum," "+s.Unit),Average=Ui.Number(summary.Average," "+s.Unit),Maximum=Ui.Number(summary.Maximum," "+s.Unit)};
            }).ToArray();sensors.ItemsSource=rows;sensorCount.Text=$"{rows.Length} sensors · statistics over {session.HistorySeconds/60} minute{(session.HistorySeconds==60?"":"s")} · — means unavailable";
        }
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closed)return;e.Cancel=true;if(closing)return;closing=true;IsEnabled=false;status.Text="Stopping collectors and saving window positions…";
        try {await session.ShutdownAsync();}catch(Exception ex){MessageBox.Show($"A collector reported a shutdown error: {ex.Message}","DeadWare");}
        closed=true;Application.Current.Shutdown();
    }
}
internal sealed class GraphWindow : Window
{
    public GraphWindow(UiSession session,string kind)
    {
        Title=$"DeadWare · {Ui.Title(kind)}";MinWidth=420;MinHeight=300;Ui.Icon(this);session.Place(this,kind,680,430);
        var root=new DockPanel {Margin=new Thickness(12)};var bar=new DockPanel();var pin=new CheckBox {Content="Always on top",IsChecked=Topmost,VerticalAlignment=VerticalAlignment.Center};pin.Checked+=(_,_)=>Topmost=true;pin.Unchecked+=(_,_)=>Topmost=false;DockPanel.SetDock(pin,Dock.Right);bar.Children.Add(pin);bar.Children.Add(Ui.Range(session));DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);root.Children.Add(new ChartCard(session,kind,false));Content=root;
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
