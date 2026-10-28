using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeadWare.Core;
namespace DeadWare.App;
internal static class Ui
{
    public static TextBlock Text(string value,double size=13,string color="Ink",string font="BodyFont")
    {var t=new TextBlock {Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};t.SetResourceReference(TextBlock.ForegroundProperty,color);t.SetResourceReference(TextBlock.FontFamilyProperty,font);return t;}
    public static TextBlock Heading(string value,double size=16)=>Text(value,size,"Heading","HeadingFont");
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
        w.SetResourceReference(Control.FontFamilyProperty,"BodyFont");w.FontSize=13;
        try {w.Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.ico"));}catch(System.IO.IOException) {}
    }
    public static Image Logo()
    {var image=new Image {Width=38,Height=38,Margin=new Thickness(0,0,9,0)};try{image.Source=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.png"));}catch(System.IO.IOException){}return image;}
    public static string Title(string kind)=>kind switch {"cpu"=>"CPU usage","cputemp"=>"CPU temperature","gpu"=>"GPU usage","gputemp"=>"GPU temperature","usage"=>"CPU / GPU usage","temperature"=>"CPU / GPU temperature","memory"=>"RAM usage",_=>"Network activity"};
    private sealed record RangeChoice(int Seconds,string Label)
    {public override string ToString()=>Label;}
    public static ComboBox Range(UiSession session)
    {
        var range=new ComboBox {Width=140,VerticalAlignment=VerticalAlignment.Center,
            ItemsSource=HistoryRanges.Seconds.Select(seconds=>new RangeChoice(seconds,HistoryRanges.Label(seconds))).ToArray(),
            DisplayMemberPath=nameof(RangeChoice.Label),SelectedValuePath=nameof(RangeChoice.Seconds),SelectedValue=session.HistorySeconds,
            ToolTip="Choose how much graph history to show"};
        System.Windows.Automation.AutomationProperties.SetName(range,"Graph history range");
        range.SelectionChanged+=(_,_)=>{if(range.SelectedValue is int seconds&&seconds!=session.HistorySeconds)session.SetRange(seconds);};
        void Refresh()=>range.SelectedValue=session.HistorySeconds;
        range.Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};range.Unloaded+=(_,_)=>session.Changed-=Refresh;return range;
    }
}

/// <summary>Native WPF drawing; real timestamps set X coordinates and missing readings split the line.</summary>
internal sealed class HistoryChart(UiSession session,string kind,bool compact=false) : FrameworkElement
{
    private readonly UiSession session=session;
    private readonly string kind=kind;
    public bool CompareDevices {get;init;}
    public void Refresh()=>InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=ActualWidth,h=ActualHeight;if(w<90||h<(compact?48:65))return;
        var axisLabel=Formatted("Now");
        var sample=session.Current?.Sample;
        double now=session.Collector.ElapsedSeconds;
        var points=session.Collector.History.Series(session.MetricId(kind),now,session.HistorySeconds);
        bool compare=CompareDevices&&kind is "cpu" or "cputemp";
        var secondary=kind=="network"?session.Collector.History.Series("network/upload",now,session.HistorySeconds):compare?session.Collector.History.Series(session.MetricId(kind=="cpu"?"gpu":"gputemp"),now,session.HistorySeconds):[];
        double maximum=kind is "cpu" or "gpu" or "memory"?100:kind=="network"?Math.Max(1000,points.Concat(secondary).Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.15):Math.Max(100,points.Concat(secondary).Where(p=>p.Value.HasValue).Select(p=>p.Value!.Value).DefaultIfEmpty(0).Max()*1.1);
        if(compare&&kind=="cputemp")maximum=Math.Ceiling(maximum/20)*20;
        string Axis(double value)=>kind=="network"?Ui.Bytes(value,true):$"{value:0}{(kind.Contains("temp")?(compare?"°C":"°"):"%")}";
        double left=compact?3:12,top=compact?8:18;
        double right=compact?Enumerable.Range(0,3).Max(i=>Formatted(Axis(maximum*i/2)).Width)+10:74;
        double bottom=axisLabel.Height+(compact?10:18),pw=w-left-right,ph=h-top-bottom;
        if(pw<=0||ph<=0)return;
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2,y=top+ph*(1-value/maximum);
            dc.DrawLine(new Pen(Theme.Brush("Line"),1){DashStyle=new DashStyle([3,5],0)},new(left,y),new(left+pw,y));
            Label(dc,Axis(value),left+pw+7,y-7);
        }
        double timeLabelY=h-axisLabel.Height-(compact?2:6);
        Label(dc,$"{HistoryRanges.Label(session.HistorySeconds)} ago",left,timeLabelY);Label(dc,"Now",left+pw-axisLabel.Width,timeLabelY);
        DrawSeries(dc,points,now,maximum,left,top,pw,ph,Theme.GraphKey(kind),false);
        if(kind=="network") DrawSeries(dc,secondary,now,maximum,left,top,pw,ph,Theme.GraphKey("upload"),true);
        else if(compare) DrawSeries(dc,secondary,now,maximum,left,top,pw,ph,Theme.GraphKey(kind=="cpu"?"gpu":"gputemp"),true);
        if(!points.Concat(secondary).Any(p=>p.Value.HasValue)) Label(dc,sample is null?(compact?"Waiting for data…":"Waiting for first sample…"):(compact?"No readings in this range":"No available readings in this range"),left+(compact?4:15),top+ph/2);
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
        bool dense=CompareDevices&&session.HistorySeconds>=3600;
        geometry.Freeze();var pen=new Pen(Theme.Brush(color),dense?1:2);if(dashed)pen.DashStyle=new DashStyle([4,3],0);dc.DrawGeometry(null,pen,geometry);
        double markerRadius=dense?1.5:2.5;
        if(lastPoint.HasValue) dc.DrawEllipse(Theme.Brush(color),null,lastPoint.Value,markerRadius,markerRadius);
    }
    private void Label(DrawingContext dc,string text,double x,double y)
    {dc.DrawText(Formatted(text),new(x,y));}
    private FormattedText Formatted(string text)=>new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Theme.ChartTypeface,11,Theme.Brush("Muted"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
internal sealed class ChartCard : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock value=Ui.Text("—",30,font:"MetricFont"),subtitle=Ui.Text("Waiting for hardware…",11,"Muted"),stats=Ui.Text("",11,"Muted",font:"MetricFont");
    private readonly Run downloadValue=new(),uploadValue=new();
    private readonly HistoryChart chart;
    public ChartCard(UiSession session,string kind,bool popButton=true)
    {
        this.session=session;this.kind=kind;CornerRadius=new(10);BorderThickness=new(1);Padding=new(16,12,16,10);Margin=new(6);SetResourceReference(BackgroundProperty,"Panel");SetResourceReference(BorderBrushProperty,"Line");
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            value.FontSize=24;value.Inlines.Clear();value.Inlines.Add(downloadValue);value.Inlines.Add(uploadValue);
            downloadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("download"));uploadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("upload"));
        }
        // A forced MinHeight larger than the grid's remaining row caused WPF to clip axis labels.
        chart=new(session,kind){ClipToBounds=true};
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new DockPanel();var title=Ui.Heading(Ui.Title(kind),14);title.FontWeight=FontWeights.SemiBold;header.Children.Add(title);
        if(popButton){var pop=Ui.Button("Pop out ↗",()=>session.OpenDetached(kind));pop.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(pop,Dock.Right);header.Children.Insert(0,pop);}
        var colors=GraphColorPicker.Create(session,kind);DockPanel.SetDock(colors,Dock.Right);header.Children.Insert(0,colors);
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
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            downloadValue.Text=$"↓ {Ui.Bytes(current,true)}    ";uploadValue.Text=$"↑ {Ui.Bytes(session.Stale?null:session.Value("network/upload"),true)}";
            subtitle.Text="Solid: download    ·    Dashed: upload    ·    Shared scale";
        }
        else if(kind=="memory")
        {
            value.Text=Ui.Number(current," %");
            subtitle.Text=data is null?"Discovering memory…":session.Stale?"RAM readings stale":current is null?"RAM usage unavailable":$"{Ui.Bytes(session.Value("memory/used"))} / {Ui.Bytes(session.Value("memory/total"))} in use";
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
