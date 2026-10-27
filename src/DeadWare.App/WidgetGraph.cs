using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
namespace DeadWare.App;

/// <summary>A compact usage/temperature view over the shared collector history.</summary>
internal sealed class WidgetGraph : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock title=Ui.Text("",12,"Muted"),usage=Ui.Text("",11),temperature=Ui.Text("",11,"Download");
    private readonly HistoryChart chart;
    public WidgetGraph(UiSession session,string kind)
    {
        this.session=session;this.kind=kind;
        CornerRadius=new(8);BorderThickness=new(1);Padding=new(8,5,8,4);Margin=new(0,5,0,0);
        SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        chart=new(session,kind,true){TemperatureOverlay=true,ClipToBounds=true};
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
        usage.SetResourceReference(TextBlock.ForegroundProperty,kind=="cpu"?"Cpu":"Gpu");
        var legend=Ui.Row(Legend(usage,kind=="cpu"?"Cpu":"Gpu",false),Legend(temperature,"Download",true));
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
        title.Text=kind=="cpu"?"CPU":"GPU";title.ToolTip=kind=="cpu"?session.Current?.CpuName:session.GpuName;
        usage.Text="Usage "+Ui.Number(Value(kind))+" %";temperature.Text="Temp "+Ui.Number(Value(kind=="cpu"?"cputemp":"gputemp"))+" °C";
        chart.Refresh();
    }
}
