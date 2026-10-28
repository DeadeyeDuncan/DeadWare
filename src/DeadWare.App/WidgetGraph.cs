using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
namespace DeadWare.App;

/// <summary>Compact readings and traces over the shared history.</summary>
internal sealed class WidgetGraph : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock title=Ui.Text("",12,"Heading",font:"HeadingFont"),primary=Ui.Text("",11,font:"MetricFont"),secondary=Ui.Text("",11,font:"MetricFont");
    private readonly TextBlock fan=Ui.Text("",11,"Warning",font:"MetricFont");
    private readonly StackPanel fanLegend;
    private readonly HistoryChart chart;
    public WidgetGraph(UiSession session,string kind)
    {
        this.session=session;this.kind=kind;
        CornerRadius=new(8);BorderThickness=new(1);Padding=new(8,5,8,4);Margin=new(0,5,0,0);
        SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        bool network=kind=="network",memory=kind=="memory";
        chart=new(session,kind,true){CompareDevices=kind is "cpu" or "cputemp",ClipToBounds=true};
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
        string firstColor=Theme.GraphKey(kind),secondColor=Theme.GraphKey(network?"upload":kind=="cputemp"?"gputemp":"gpu");
        primary.SetResourceReference(TextBlock.ForegroundProperty,firstColor);secondary.SetResourceReference(TextBlock.ForegroundProperty,secondColor);
        var legend=memory?Ui.Row(Legend(primary,firstColor,false)):Ui.Row(Legend(primary,firstColor,false),Legend(secondary,secondColor,true));
        var header=new DockPanel();DockPanel.SetDock(legend,Dock.Right);header.Children.Add(legend);header.Children.Add(title);grid.Children.Add(header);
        fanLegend=Legend(fan,"Warning",false,true);fanLegend.Visibility=Visibility.Collapsed;fanLegend.Margin=new(0,3,0,0);fanLegend.ToolTip="Selected GPU's first reported fan · dotted trace · separate left RPM scale";Grid.SetRow(fanLegend,1);grid.Children.Add(fanLegend);
        Grid.SetRow(chart,2);grid.Children.Add(chart);Child=grid;
    }
    private static StackPanel Legend(TextBlock value,string color,bool dashed,bool dotted=false)
    {
        var line=new Line {X1=0,Y1=5,X2=14,Y2=5,Width=14,Height=10,StrokeThickness=2,Margin=new(0,0,4,0),VerticalAlignment=VerticalAlignment.Center};
        line.SetResourceReference(Shape.StrokeProperty,color);if(dotted)line.StrokeDashArray=new([1,3]);else if(dashed)line.StrokeDashArray=new([3,2]);
        var item=Ui.Row(line,value);item.Margin=new(0,0,10,0);return item;
    }
    public void Refresh()
    {
        bool showFan=kind=="cputemp"&&session.Settings.ShowGpuFanSpeed;
        fanLegend.Visibility=showFan?Visibility.Visible:Visibility.Collapsed;
        if(showFan)fan.Text=Ui.GpuFanText(session)+" · left RPM";
        double? Value(string metric)=>session.Stale?null:session.Value(session.MetricId(metric));
        if(kind=="network")
        {
            title.Text="Network";title.ToolTip="Network download (solid) and upload (dashed), in bytes per second";
            primary.Text="↓ "+Ui.Bytes(session.Stale?null:session.Value("network/download"),true);primary.ToolTip="Download rate";
            secondary.Text="↑ "+Ui.Bytes(session.Stale?null:session.Value("network/upload"),true);secondary.ToolTip="Upload rate";
        }
        else if(kind=="memory")
        {
            title.Text="RAM";title.ToolTip="Physical memory usage (%)";
            double? current=Value(kind);
            primary.Text=Ui.Number(current)+" %";
            if(current.HasValue)primary.Text+=$" · {Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))}";
            primary.ToolTip="RAM usage and used / total memory";
        }
        else
        {
            bool thermal=kind=="cputemp";string unit=thermal?" °C":" %";
            title.Text=thermal?"Temp":"Usage";title.ToolTip=thermal?"CPU and selected GPU temperature (°C)":"CPU and selected GPU usage (%)";
            primary.Text="CPU "+Ui.Number(Value(kind))+unit;primary.ToolTip=session.Current?.CpuName;
            secondary.Text="GPU "+Ui.Number(Value(thermal?"gputemp":"gpu"))+unit;secondary.ToolTip=session.GpuName;
        }
        chart.Refresh();
    }
}
