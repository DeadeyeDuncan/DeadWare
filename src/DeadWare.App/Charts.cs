using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.Core;
namespace DeadWare.App;
internal static class Ui
{
    public static TextBlock Text(string value,double size=13,string color="Ink")
    {var t=new TextBlock {Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};t.SetResourceReference(TextBlock.ForegroundProperty,color);return t;}
    public static Button Button(string value,Action action)
    {var b=new Button {Content=value};b.Click+=(_,_)=>action();return b;}
    public static StackPanel Row(params UIElement[] children)
    {var p=new StackPanel {Orientation=Orientation.Horizontal};foreach(var c in children)p.Children.Add(c);return p;}
    public static Border Panel(UIElement child,Thickness? padding=null)
    {var b=new Border {Child=child,Padding=padding??new Thickness(18),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1)};b.SetResourceReference(Border.BackgroundProperty,"Panel");b.SetResourceReference(Border.BorderBrushProperty,"Line");return b;}
    public static string Number(double? value,string unit="")=>value.HasValue&&double.IsFinite(value.Value)?$"{value.Value:0.0}{unit}":"—";
    public static string Bytes(double? bytes,bool rate=false)
    {
        if(!bytes.HasValue||!double.IsFinite(bytes.Value)) return "—";
        double value=bytes.Value;string[] units=["B","KB","MB","GB","TB"];int i=0;
        while(value>=1000&&i<units.Length-1){value/=1000;i++;}
        return $"{value:0.0} {units[i]}{(rate?"/s":"")}";
    }
    public static void Icon(Window w)
    {
        // An implicit Window style does not automatically style our derived Window types.
        w.SetResourceReference(Control.BackgroundProperty,"Background");w.SetResourceReference(Control.ForegroundProperty,"Ink");
        w.FontFamily=new FontFamily("Segoe UI");w.FontSize=13;
        try {w.Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.ico"));}catch(System.IO.IOException) {}
    }
    public static Image Logo()
    {var image=new Image {Width=38,Height=38,Margin=new Thickness(0,0,9,0)};try{image.Source=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.png"));}catch(System.IO.IOException){}return image;}
    public static string Title(string kind)=>kind switch {"cpu"=>"CPU usage","cputemp"=>"CPU temperature","gpu"=>"GPU usage","gputemp"=>"GPU temperature",_=>"Network activity"};
    public static StackPanel Range(UiSession session)
    {
        var row=Row();foreach(int seconds in new[]{60,300,900})
        {int n=seconds;var button=Button($"{seconds/60} min",()=>session.SetRange(n));button.Tag=seconds;row.Children.Add(button);}
        void Refresh(){foreach(Button b in row.Children){b.FontWeight=(int)b.Tag==session.HistorySeconds?FontWeights.Bold:FontWeights.Normal;b.SetResourceReference(Control.ForegroundProperty,(int)b.Tag==session.HistorySeconds?"Accent":"Muted");}}
        row.Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};row.Unloaded+=(_,_)=>session.Changed-=Refresh;return row;
    }
}

/// <summary>Native WPF drawing; real timestamps set X coordinates and missing readings split the line.</summary>
internal sealed class HistoryChart(UiSession session,string kind) : FrameworkElement
{
    private readonly UiSession session=session;
    private readonly string kind=kind;
    public void Refresh()=>InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=ActualWidth,h=ActualHeight;if(w<90||h<65)return;
        var axisLabel=Formatted("Now");
        double left=12,top=18,right=74,bottom=axisLabel.Height+18,pw=w-left-right,ph=h-top-bottom;
        var sample=session.Current?.Sample;
        double now=session.Collector.ElapsedSeconds;
        var points=session.Collector.History.Series(session.MetricId(kind),now,session.HistorySeconds);
        var up=kind=="network"?session.Collector.History.Series("network/upload",now,session.HistorySeconds):[];
        double maximum=kind is "cpu" or "gpu"?100:kind=="network"?Math.Max(1000,points.Concat(up).Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.15):Math.Max(100,points.Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.1);
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2,y=top+ph*(1-value/maximum);
            dc.DrawLine(new Pen(Theme.Brush("Line"),1){DashStyle=new DashStyle([3,5],0)},new(left,y),new(left+pw,y));
            Label(dc,kind=="network"?Ui.Bytes(value,true):$"{value:0}{(kind.Contains("temp")?"°":"%")}",left+pw+7,y-7);
        }
        Label(dc,$"{session.HistorySeconds/60} min ago",left,h-axisLabel.Height-6);Label(dc,"Now",left+pw-axisLabel.Width,h-axisLabel.Height-6);
        DrawSeries(dc,points,now,maximum,left,top,pw,ph,kind=="network"?"Download":kind.StartsWith("gpu")?"Gpu":"Cpu",false);
        if(kind=="network") DrawSeries(dc,up,now,maximum,left,top,pw,ph,"Cpu",true);
        if(!points.Concat(up).Any(p=>p.Value.HasValue)) Label(dc,sample is null?"Waiting for first sample…":"No available readings in this range",left+15,top+ph/2);
    }
    private void DrawSeries(DrawingContext dc,IReadOnlyList<SamplePoint> points,double now,double maximum,double left,double top,double width,double height,string color,bool dashed)
    {
        var geometry=new StreamGeometry();bool open=false;double previous=double.NaN;Point? lastPoint=null;
        using(var context=geometry.Open())
        {
            foreach(var point in points)
            {
                if(!point.Value.HasValue){open=false;previous=point.MonotonicSeconds;continue;}
                if(point.MonotonicSeconds-previous>2.5)open=false;
                var p=new Point(left+width*(1-(now-point.MonotonicSeconds)/session.HistorySeconds),top+height*(1-Math.Clamp(point.Value.Value/maximum,0,1)));
                if(!open){context.BeginFigure(p,false,false);open=true;}else context.LineTo(p,true,false);
                previous=point.MonotonicSeconds;lastPoint=p;
            }
        }
        geometry.Freeze();var pen=new Pen(Theme.Brush(color),2);if(dashed)pen.DashStyle=new DashStyle([4,3],0);dc.DrawGeometry(null,pen,geometry);
        if(lastPoint.HasValue) dc.DrawEllipse(Theme.Brush(color),null,lastPoint.Value,2.5,2.5);
    }
    private void Label(DrawingContext dc,string text,double x,double y)
    {dc.DrawText(Formatted(text),new(x,y));}
    private FormattedText Formatted(string text)=>new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,Theme.Brush("Muted"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
internal sealed class ChartCard : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock value=Ui.Text("—",30),subtitle=Ui.Text("Waiting for hardware…",11,"Muted"),stats=Ui.Text("",11,"Muted");
    private readonly HistoryChart chart;
    public ChartCard(UiSession session,string kind,bool popButton=true)
    {
        this.session=session;this.kind=kind;CornerRadius=new(10);BorderThickness=new(1);Padding=new(16,12,16,10);Margin=new(6);SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        // A forced MinHeight larger than the grid's remaining row caused WPF to clip axis labels.
        chart=new(session,kind){ClipToBounds=true};
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new DockPanel();var title=Ui.Text(Ui.Title(kind),14);title.FontWeight=FontWeights.SemiBold;header.Children.Add(title);
        if(popButton){var pop=Ui.Button("Pop out ↗",()=>session.OpenDetached(kind));pop.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(pop,Dock.Right);header.Children.Insert(0,pop);}
        grid.Children.Add(header);
        var metric=new StackPanel {Margin=new Thickness(0,5,0,5)};metric.Children.Add(value);metric.Children.Add(subtitle);Grid.SetRow(metric,1);grid.Children.Add(metric);
        Grid.SetRow(chart,2);grid.Children.Add(chart);Grid.SetRow(stats,3);grid.Children.Add(stats);Child=grid;
        Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};Unloaded+=(_,_)=>session.Changed-=Refresh;
    }
    private void Refresh()
    {
        string id=session.MetricId(kind);double? current=session.Stale?null:session.Value(id);var data=session.Current;
        value.SetResourceReference(TextBlock.ForegroundProperty,kind=="network"?"Download":kind.StartsWith("gpu")?"Gpu":"Cpu");
        if(kind=="network")
        {
            value.FontSize=24;value.Text=$"↓ {Ui.Bytes(current,true)}    ↑ {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}";
            subtitle.Text="Blue: download    ·    Dashed green: upload    ·    Shared scale";
        }
        else
        {
            value.Text=Ui.Number(current,kind.Contains("temp")?" °C":" %");
            subtitle.Text=data is null?"Discovering hardware…":kind.StartsWith("cpu")?data.CpuName:session.GpuName;
            if(current is null&&data is not null)subtitle.Text+=" · "+(session.Stale?"stale":"sensor unavailable");
        }
        double now=session.Collector.ElapsedSeconds;var summary=SampleHistory.Statistics(session.Collector.History.Series(id,now,session.HistorySeconds));
        Func<double?,string> format=kind=="network"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,kind.Contains("temp")?"°C":"%");
        stats.Text=$"MIN  {format(summary.Minimum)}      AVG  {format(summary.Average)}      MAX  {format(summary.Maximum)}"+(kind=="network"?"    · Download":"");
        chart.Refresh();
    }
}
