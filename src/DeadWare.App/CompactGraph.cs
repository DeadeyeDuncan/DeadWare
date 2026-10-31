using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using DeadWare.Core;

namespace DeadWare.App;

/// <summary>A graph-first view for small detached windows; dashboard cards stay unchanged.</summary>
internal sealed class CompactGraph : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly string chartKind;
    private readonly bool joined;
    private readonly HistoryChart chart;
    private readonly TextBlock value=Ui.Text("—",26,font:"MetricFont"),upload=Ui.Text("—",18,"Cpu",font:"MetricFont"),status=Ui.Text("",10,"Warning");
    private readonly TextBlock fan=Ui.Text("",10,Theme.GraphKey("gpufan"),font:"MetricFont");
    private readonly TextBlock details=Ui.Text("Discovering hardware…",12,"Muted"),stats=Ui.Text("",12,"Muted");
    private readonly Button options;
    private readonly Popup popup;

    public CompactGraph(UiSession session,string kind,Window owner)
    {
        this.session=session;this.kind=kind;
        joined=kind is "usage" or "temperature";
        chartKind=kind switch {"usage"=>"cpu","temperature"=>"cputemp",_=>kind};
        Padding=new Thickness(8,3,8,3);CornerRadius=new CornerRadius(8);BorderThickness=new Thickness(1);
        SetResourceReference(BackgroundProperty,"GlassPanel");SetResourceReference(BorderBrushProperty,"GlassBorder");
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Child=grid;
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.Children.Add(header);
        var title=Ui.Text(kind switch {"usage"=>"Usage","temperature"=>"Temp","cputemp"=>"CPU temp","gputemp"=>"GPU temp","network"=>"Network",_=>Ui.Title(kind)},12,"Heading",font:"HeadingFont");
        title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.Margin=new Thickness(0,0,6,0);header.Children.Add(title);
        AutomationProperties.SetName(title,Ui.Title(kind));
        value.TextWrapping=TextWrapping.NoWrap;upload.TextWrapping=TextWrapping.NoWrap;
        var readings=new StackPanel {VerticalAlignment=VerticalAlignment.Center};readings.Children.Add(value);
        if(kind=="network"||joined){value.FontSize=18;readings.Children.Add(upload);}
        Grid.SetColumn(readings,1);header.Children.Add(readings);
        options=Ui.Button("⋯",OpenOptions);options.Width=28;options.Height=28;options.FontSize=18;options.Padding=new Thickness(0);options.Margin=new Thickness(6,0,0,0);options.ToolTip="Graph options";
        AutomationProperties.SetName(options,"Graph options");Grid.SetColumn(options,2);header.Children.Add(options);
        var annotations=new StackPanel();status.Visibility=Visibility.Collapsed;status.TextWrapping=TextWrapping.NoWrap;status.TextTrimming=TextTrimming.CharacterEllipsis;annotations.Children.Add(status);
        fan.Visibility=Visibility.Collapsed;fan.ToolTip="Selected GPU's first reported fan · separate left RPM scale";annotations.Children.Add(fan);Grid.SetRow(annotations,1);grid.Children.Add(annotations);
        chart=new HistoryChart(session,chartKind,true) {CompareDevices=joined,ClipToBounds=true};Grid.SetRow(chart,2);grid.Children.Add(chart);

        var menu=new StackPanel();KeyboardNavigation.SetTabNavigation(menu,KeyboardNavigationMode.Cycle);
        menu.Children.Add(Ui.Text("History range",12));var ranges=Ui.Range(session);ranges.Margin=new Thickness(-3,3,-3,5);menu.Children.Add(ranges);
        var pin=new CheckBox {Content="Always on top",Margin=new Thickness(0,5,0,12)};
        pin.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(Window.Topmost)){Source=owner,Mode=BindingMode.TwoWay});menu.Children.Add(pin);
        details.Margin=new Thickness(0,2,0,8);menu.Children.Add(details);menu.Children.Add(stats);
        var menuBorder=Ui.Panel(menu,new Thickness(12));menuBorder.Width=270;menuBorder.SetResourceReference(BackgroundProperty,"GlassPopup");
        popup=new Popup {Child=menuBorder,PlacementTarget=options,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        popup.Opened+=(_,_)=>ranges.Focus();
        popup.Closed+=(_,_)=>{if(IsLoaded)options.Focus();};
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!ranges.IsDropDownOpen){popup.IsOpen=false;e.Handled=true;}};
        Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};
        Unloaded+=(_,_)=>{session.Changed-=Refresh;popup.IsOpen=false;};
        Refresh();
    }

    private void OpenOptions() {popup.IsOpen=true;Refresh();}

    private void Refresh()
    {
        string Pattern(string metric,bool dashed=false)=>System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(GraphStroke.Pattern(session.Settings,metric,dashed));
        string id=session.MetricId(chartKind);double? current=session.Stale?null:session.Value(id);
        double? other=joined?(session.Stale?null:session.Value(session.MetricId(chartKind=="cpu"?"gpu":"gputemp"))):null;
        var data=session.Current;
        bool showFan=session.Settings.ShowGpuFanSpeed&&(kind=="gputemp"||kind=="temperature");
        fan.Visibility=showFan?Visibility.Visible:Visibility.Collapsed;
        if(showFan)fan.Text=Ui.GpuFanText(session)+$" · {GraphStroke.Pattern(session.Settings,"gpufan",dotted:true)} · left RPM";
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(chartKind));
        upload.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(joined?(chartKind=="cpu"?"gpu":"gputemp"):"upload"));
        if(joined)
        {
            string unit=chartKind=="cputemp"?" °C":" %";
            value.Text="CPU "+Ui.Number(current)+unit;upload.Text="GPU "+Ui.Number(other)+unit;
            value.ToolTip=data?.CpuName;upload.ToolTip=session.GpuName;
            details.Text=$"CPU · {data?.CpuName??"Unavailable"}\n{Pattern(chartKind)} CPU-color line\n\nGPU · {session.GpuName}\n{Pattern(chartKind=="cpu"?"gpu":"gputemp",true)} GPU-color line\n"+(showFan?"CPU / GPU temperatures share the right °C scale.":"Both devices share the same scale.");
            AutomationProperties.SetName(value,$"{data?.CpuName??"CPU"} {value.Text}");
            AutomationProperties.SetName(upload,$"{session.GpuName} {upload.Text}");
        }
        else if(kind=="network")
        {
            value.Text=$"↓ {Ui.Bytes(current,true)}";upload.Text=$"↑ {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}";
            details.Text=$"↓ Download: {GraphStroke.Pattern(session.Settings,"download")} line\n↑ Upload: {GraphStroke.Pattern(session.Settings,"upload",true)} line\nBoth directions share the same scale.";
            AutomationProperties.SetName(value,$"Download {Ui.Bytes(current,true)}");AutomationProperties.SetName(upload,$"Upload {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}");
        }
        else if(kind=="memory")
        {
            value.Text=Ui.Number(current," %");
            details.Text=data is null?"Discovering memory…":session.Stale?"RAM readings stale":current is null?"RAM usage unavailable":$"{Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))} in use";
            AutomationProperties.SetName(value,$"RAM usage {value.Text}");
        }
        else
        {
            value.Text=Ui.Number(current,kind.Contains("temp")?" °C":" %");
            details.Text=data is null?"Discovering hardware…":kind.StartsWith("cpu")?data.CpuName:session.GpuName;
            AutomationProperties.SetName(value,$"{Ui.Title(kind)} {value.Text}");
        }
        if(showFan)details.Text+=$"\n\nGPU first fan: {GraphStroke.Pattern(session.Settings,"gpufan",dotted:true)} line\nSeparate left RPM scale.";
        bool unavailable=current is null||(joined&&other is null)||(kind=="network"&&session.Value("network/upload") is null);
        status.Text=data is null?"Discovering hardware…":session.Stale?"Stale · waiting for fresh readings":unavailable?"Reading unavailable":"";
        status.Visibility=status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        // Keep secondary information accessible without reserving graph space for it.
        if(popup.IsOpen)RefreshStatistics();
        chart.Refresh();
    }

    private void RefreshStatistics()
    {
        double now=session.Collector.ElapsedSeconds;
        Func<double?,string> format=kind=="network"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,chartKind.Contains("temp")?" °C":" %");
        string Summary(string metric)
        {
            var summary=session.Collector.History.Summarize(session.MetricId(metric),now,session.HistorySeconds);
            return $"Min  {format(summary.Minimum)}\nAvg  {format(summary.Average)}\nMax  {format(summary.Maximum)}";
        }
        stats.Text=joined
            ?$"Last {HistoryRanges.Label(session.HistorySeconds)}\nCPU · {session.Current?.CpuName??"Unavailable"}\n{Summary(chartKind)}\n\nGPU · {session.GpuName}\n{Summary(chartKind=="cpu"?"gpu":"gputemp")}"
            :$"{(kind=="network"?"Download · ":"")}Last {HistoryRanges.Label(session.HistorySeconds)}\n{Summary(chartKind)}";
    }
}
