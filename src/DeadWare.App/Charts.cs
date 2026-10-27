using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        NativeTheme.Attach(w);
        // An implicit Window style does not automatically style our derived Window types.
        w.SetResourceReference(Control.BackgroundProperty,"Background");w.SetResourceReference(Control.ForegroundProperty,"Ink");
        w.FontFamily=new FontFamily("Segoe UI");w.FontSize=13;
        try {w.Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.ico"));}catch(System.IO.IOException) {}
    }
    public static Image Logo()
    {var image=new Image {Width=38,Height=38,Margin=new Thickness(0,0,9,0)};try{image.Source=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.png"));}catch(System.IO.IOException){}return image;}
    public static string Title(string kind)=>kind switch {"cpu"=>"CPU usage","cputemp"=>"CPU temperature","gpu"=>"GPU usage","gputemp"=>"GPU temperature",_=>"Network activity"};
    public static UniformGrid Range(UiSession session)
    {
        var row=new UniformGrid {Columns=3,Width=216};foreach(int seconds in HistoryRanges.Seconds)
        {int n=seconds;var button=Button(HistoryRanges.Label(seconds),()=>session.SetRange(n));button.Tag=seconds;button.ToolTip="Show the last "+HistoryRanges.Label(seconds)+" of graph history";row.Children.Add(button);}
        void Refresh(){foreach(Button b in row.Children){b.FontWeight=(int)b.Tag==session.HistorySeconds?FontWeights.Bold:FontWeights.Normal;b.SetResourceReference(Control.ForegroundProperty,(int)b.Tag==session.HistorySeconds?"Accent":"Muted");}}
        row.Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};row.Unloaded+=(_,_)=>session.Changed-=Refresh;return row;
    }
}

/// <summary>Native WPF drawing; real timestamps set X coordinates and missing readings split the line.</summary>
internal sealed class HistoryChart(UiSession session,string kind,bool compact=false) : FrameworkElement
{
    private readonly UiSession session=session;
    private readonly string kind=kind;
    public bool TemperatureOverlay {get;init;}
    public void Refresh()=>InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=ActualWidth,h=ActualHeight;if(w<90||h<(compact?48:65))return;
        var axisLabel=Formatted("Now");
        var sample=session.Current?.Sample;
        double now=session.Collector.ElapsedSeconds;
        var points=session.Collector.History.Series(session.MetricId(kind),now,session.HistorySeconds);
        var up=kind=="network"?session.Collector.History.Series("network/upload",now,session.HistorySeconds):[];
        bool combined=TemperatureOverlay&&kind is "cpu" or "gpu";
        var temperature=combined?session.Collector.History.Series(session.MetricId(kind=="cpu"?"cputemp":"gputemp"),now,session.HistorySeconds):[];
        double maximum=kind is "cpu" or "gpu"?100:kind=="network"?Math.Max(1000,points.Concat(up).Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.15):Math.Max(100,points.Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.1);
        double temperatureMaximum=Math.Max(100,Math.Ceiling(temperature.Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.1/20)*20);
        string Axis(double value)=>kind=="network"?Ui.Bytes(value,true):$"{value:0}{(kind.Contains("temp")?"°":"%")}";
        string TemperatureAxis(double value)=>$"{value:0}°C";
        double left=combined?Formatted("100%").Width+8:compact?3:12,top=compact?8:18;
        double right=combined?Enumerable.Range(0,3).Max(i=>Formatted(TemperatureAxis(temperatureMaximum*i/2)).Width)+10:compact?Enumerable.Range(0,3).Max(i=>Formatted(Axis(maximum*i/2)).Width)+10:74;
        double bottom=axisLabel.Height+(compact?10:18),pw=w-left-right,ph=h-top-bottom;
        if(pw<=0||ph<=0)return;
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2,y=top+ph*(1-value/maximum);
            dc.DrawLine(new Pen(Theme.Brush("Line"),1){DashStyle=new DashStyle([3,5],0)},new(left,y),new(left+pw,y));
            if(combined)
            {
                Label(dc,Axis(value),0,y-7);
                Label(dc,TemperatureAxis(temperatureMaximum*i/2),left+pw+7,y-7);
            }
            else Label(dc,Axis(value),left+pw+7,y-7);
        }
        double timeLabelY=h-axisLabel.Height-(compact?2:6);
        Label(dc,$"{HistoryRanges.Label(session.HistorySeconds)} ago",left,timeLabelY);Label(dc,"Now",left+pw-axisLabel.Width,timeLabelY);
        DrawSeries(dc,points,now,maximum,left,top,pw,ph,kind=="network"?"Download":kind.StartsWith("gpu")?"Gpu":"Cpu",false);
        if(kind=="network") DrawSeries(dc,up,now,maximum,left,top,pw,ph,"Cpu",true);
        if(combined) DrawSeries(dc,temperature,now,temperatureMaximum,left,top,pw,ph,"Download",true);
        if(!points.Concat(up).Concat(temperature).Any(p=>p.Value.HasValue)) Label(dc,sample is null?(compact?"Waiting for data…":"Waiting for first sample…"):(compact?"No readings in this range":"No available readings in this range"),left+(compact?4:15),top+ph/2);
    }
    private void DrawSeries(DrawingContext dc,IReadOnlyList<SamplePoint> points,double now,double maximum,double left,double top,double width,double height,string color,bool dashed)
    {
        var geometry=new StreamGeometry();bool open=false;Point? lastPoint=null;
        using(var context=geometry.Open())
        {
            foreach(var point in SampleHistory.ReduceForChart(points,now,session.HistorySeconds,(int)Math.Ceiling(width)))
            {
                if(!point.Value.HasValue){open=false;continue;}
                var p=new Point(left+width*(1-(now-point.MonotonicSeconds)/session.HistorySeconds),top+height*(1-Math.Clamp(point.Value.Value/maximum,0,1)));
                if(!open){context.BeginFigure(p,false,false);open=true;}else context.LineTo(p,true,false);
                lastPoint=p;
            }
        }
        bool dense=TemperatureOverlay&&session.HistorySeconds>=3600;
        geometry.Freeze();var pen=new Pen(Theme.Brush(color),dense?1:2);if(dashed)pen.DashStyle=new DashStyle([4,3],0);dc.DrawGeometry(null,pen,geometry);
        double markerRadius=dense?1.5:2.5;
        if(lastPoint.HasValue) dc.DrawEllipse(Theme.Brush(color),null,lastPoint.Value,markerRadius,markerRadius);
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
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
    }
    private void Refresh()
    {
        if(!IsVisible)return;
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
        double now=session.Collector.ElapsedSeconds;var summary=session.Collector.History.Summarize(id,now,session.HistorySeconds);
        Func<double?,string> format=kind=="network"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,kind.Contains("temp")?"°C":"%");
        stats.Text=$"MIN  {format(summary.Minimum)}      AVG  {format(summary.Average)}      MAX  {format(summary.Maximum)}"+(kind=="network"?"    · Download":"");
        chart.Refresh();
    }
}
