using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
namespace DeadWare.App;

/// <summary>Compare CPU and GPU readings for one metric over the shared history.</summary>
internal sealed class WidgetGraph : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock title=Ui.Text("",12,"Muted"),cpu=Ui.Text("",11,"Cpu"),gpu=Ui.Text("",11,"Gpu");
    private readonly HistoryChart chart;
    public WidgetGraph(UiSession session,string kind)
    {
        this.session=session;this.kind=kind;
        CornerRadius=new(8);BorderThickness=new(1);Padding=new(8,5,8,4);Margin=new(0,5,0,0);
        SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        chart=new(session,kind,true){CompareDevices=true,ClipToBounds=true};
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
        var legend=Ui.Row(Legend(cpu,"Cpu",false),Legend(gpu,"Gpu",true));
        var header=new DockPanel();DockPanel.SetDock(legend,Dock.Right);header.Children.Add(legend);header.Children.Add(title);grid.Children.Add(header);
        Grid.SetRow(chart,1);grid.Children.Add(chart);Child=grid;
    }
    private static StackPanel Legend(TextBlock value,string color,bool dashed)
    {
        var line=new Line {X1=0,Y1=5,X2=14,Y2=5,Width=14,Height=10,StrokeThickness=2,Margin=new(0,0,4,0),VerticalAlignment=VerticalAlignment.Center};
        line.SetResourceReference(Shape.StrokeProperty,color);if(dashed)line.StrokeDashArray=new([3,2]);
        var item=Ui.Row(line,value);item.Margin=new(0,0,10,0);return item;
    }
    public void Refresh()
    {
        double? Value(string metric)=>session.Stale?null:session.Value(session.MetricId(metric));
        bool thermal=kind=="cputemp";string unit=thermal?" °C":" %";
        title.Text=thermal?"Temp":"Usage";title.ToolTip=thermal?"CPU and selected GPU temperature (°C)":"CPU and selected GPU usage (%)";
        cpu.Text="CPU "+Ui.Number(Value(kind))+unit;cpu.ToolTip=session.Current?.CpuName;
        gpu.Text="GPU "+Ui.Number(Value(thermal?"gputemp":"gpu"))+unit;gpu.ToolTip=session.GpuName;
        chart.Refresh();
    }
}
