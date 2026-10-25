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
    private readonly HistoryChart chart;
    private readonly TextBlock value=Ui.Text("—",26),upload=Ui.Text("—",18,"Cpu"),status=Ui.Text("",10,"Warning");
    private readonly TextBlock details=Ui.Text("Discovering hardware…",12,"Muted"),stats=Ui.Text("",12,"Muted");
    private readonly Button options;
    private readonly Popup popup;

    public CompactGraph(UiSession session,string kind,Window owner)
    {
        this.session=session;this.kind=kind;
        Padding=new Thickness(8,3,8,3);CornerRadius=new CornerRadius(8);BorderThickness=new Thickness(1);
        SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Child=grid;
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.Children.Add(header);
        var title=Ui.Text(kind switch {"cputemp"=>"CPU temp","gputemp"=>"GPU temp","network"=>"Network",_=>Ui.Title(kind)},12);
        title.FontWeight=FontWeights.SemiBold;title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.Margin=new Thickness(0,0,6,0);header.Children.Add(title);
        AutomationProperties.SetName(title,Ui.Title(kind));
        value.TextWrapping=TextWrapping.NoWrap;upload.TextWrapping=TextWrapping.NoWrap;
        var readings=new StackPanel {VerticalAlignment=VerticalAlignment.Center};readings.Children.Add(value);
        if(kind=="network"){value.FontSize=18;readings.Children.Add(upload);}
        Grid.SetColumn(readings,1);header.Children.Add(readings);
        options=Ui.Button("⋯",OpenOptions);options.Width=28;options.Height=28;options.FontSize=18;options.Padding=new Thickness(0);options.Margin=new Thickness(6,0,0,0);options.ToolTip="Graph options";
        AutomationProperties.SetName(options,"Graph options");Grid.SetColumn(options,2);header.Children.Add(options);
        status.Visibility=Visibility.Collapsed;status.TextWrapping=TextWrapping.NoWrap;status.TextTrimming=TextTrimming.CharacterEllipsis;Grid.SetRow(status,1);grid.Children.Add(status);
        chart=new HistoryChart(session,kind,true) {ClipToBounds=true};Grid.SetRow(chart,2);grid.Children.Add(chart);

        var menu=new StackPanel();KeyboardNavigation.SetTabNavigation(menu,KeyboardNavigationMode.Cycle);
        menu.Children.Add(Ui.Text("History range",12));var ranges=Ui.Range(session);ranges.Margin=new Thickness(-3,3,-3,5);menu.Children.Add(ranges);
        var pin=new CheckBox {Content="Always on top",Margin=new Thickness(0,5,0,12)};
        pin.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(Window.Topmost)){Source=owner,Mode=BindingMode.TwoWay});menu.Children.Add(pin);
        details.Margin=new Thickness(0,2,0,8);menu.Children.Add(details);menu.Children.Add(stats);
        var menuBorder=Ui.Panel(menu,new Thickness(12));menuBorder.Width=270;
        popup=new Popup {Child=menuBorder,PlacementTarget=options,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true};
        popup.Opened+=(_,_)=>((Button)ranges.Children[0]).Focus();
        popup.Closed+=(_,_)=>{if(IsLoaded)options.Focus();};
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;e.Handled=true;}};
        Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};
        Unloaded+=(_,_)=>{session.Changed-=Refresh;popup.IsOpen=false;};
        Refresh();
    }

    private void OpenOptions() {Refresh();popup.IsOpen=true;}

    private void Refresh()
    {
        string id=session.MetricId(kind);double? current=session.Stale?null:session.Value(id);
        var data=session.Current;
        value.SetResourceReference(TextBlock.ForegroundProperty,kind=="network"?"Download":kind.StartsWith("gpu")?"Gpu":"Cpu");
        if(kind=="network")
        {
            value.Text=$"↓ {Ui.Bytes(current,true)}";upload.Text=$"↑ {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}";
            details.Text="↓ Download: solid line\n↑ Upload: dashed line\nBoth directions share the same scale.";
            AutomationProperties.SetName(value,$"Download {Ui.Bytes(current,true)}");AutomationProperties.SetName(upload,$"Upload {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}");
        }
        else
        {
            value.Text=Ui.Number(current,kind.Contains("temp")?" °C":" %");
            details.Text=data is null?"Discovering hardware…":kind.StartsWith("cpu")?data.CpuName:session.GpuName;
            AutomationProperties.SetName(value,$"{Ui.Title(kind)} {value.Text}");
        }
        bool unavailable=current is null||(kind=="network"&&session.Value("network/upload") is null);
        status.Text=data is null?"Discovering hardware…":session.Stale?"Stale · waiting for fresh readings":unavailable?"Reading unavailable":"";
        status.Visibility=status.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
        // Keep secondary information accessible without reserving graph space for it.
        double now=session.Collector.ElapsedSeconds;var summary=SampleHistory.Statistics(session.Collector.History.Series(id,now,session.HistorySeconds));
        Func<double?,string> format=kind=="network"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,kind.Contains("temp")?" °C":" %");
        stats.Text=$"{(kind=="network"?"Download · ":"")}Last {session.HistorySeconds/60} min\nMin  {format(summary.Minimum)}\nAvg  {format(summary.Average)}\nMax  {format(summary.Maximum)}";
        chart.Refresh();
    }
}
