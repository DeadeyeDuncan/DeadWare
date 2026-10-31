using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
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
    {var b=new Border {Child=child,Padding=padding??new Thickness(18),CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1)};b.SetResourceReference(Border.BackgroundProperty,"GlassPanel");b.SetResourceReference(Border.BorderBrushProperty,"GlassBorder");return b;}
    public static string Number(double? value,string unit="")=>value.HasValue&&double.IsFinite(value.Value)?$"{value.Value:0.0}{unit}":"—";
    public static string GpuFanText(UiSession session)
    {
        double? rpm=session.Stale?null:session.Value(session.MetricId("gpufan"));
        return "GPU fan · "+(session.Stale?"stale":rpm.HasValue&&double.IsFinite(rpm.Value)?$"{rpm.Value:0} RPM":"unavailable");
    }
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
        w.SetResourceReference(Control.BackgroundProperty,"WindowSurface");w.SetResourceReference(Control.ForegroundProperty,"Ink");
        w.SetResourceReference(Control.FontFamilyProperty,"BodyFont");w.FontSize=13;
        try {w.Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.ico"));}catch(System.IO.IOException) {}
    }
    public static Image Logo()
    {var image=new Image {Width=38,Height=38,Margin=new Thickness(0,0,9,0)};try{image.Source=new BitmapImage(new Uri("pack://application:,,,/Assets/deadware.png"));}catch(System.IO.IOException){}return image;}
    public static string Title(string kind)=>kind switch {"cpu"=>"CPU usage","cputemp"=>"CPU temperature","gpu"=>"GPU usage","gputemp"=>"GPU temperature","gpufan"=>"GPU fan speed","usage"=>"CPU / GPU usage","temperature"=>"CPU / GPU temperature","memory"=>"RAM usage",_=>"Network activity"};
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
    private sealed record Query(SampleHistory History,string Primary,string? Secondary,string? Fan,int Seconds,int Columns,bool Smoothed);
    private sealed record ChartData(Query Query,Dictionary<string,IReadOnlyList<SamplePoint>> Series,Dictionary<string,IReadOnlyList<ChartRangePoint>> Ranges,Summary Statistics);
    public event Action<Summary>? StatisticsReady;
    private ChartData? prepared;
    private (Query Query,double Now,int Revision)? pending;
    private int revision;
    private bool preparing,preparationFailed;
    private Query CurrentQuery()
    {
        bool compare=CompareDevices&&kind is "cpu" or "cputemp";
        bool fan=session.Settings.ShowGpuFanSpeed&&(kind=="gputemp"||kind=="cputemp"&&compare);
        return new(session.Collector.History,session.MetricId(kind),kind=="network"?"network/upload":compare?session.MetricId(kind=="cpu"?"gpu":"gputemp"):null,
            fan?session.MetricId("gpufan"):null,session.HistorySeconds,Math.Max(1,(int)ActualWidth-90),session.Settings.GraphSmoothing&&session.HistorySeconds>=3600);
    }
    private static ChartData Prepare(Query query,double now)
    {
        var series=new Dictionary<string,IReadOnlyList<SamplePoint>>();var ranges=new Dictionary<string,IReadOnlyList<ChartRangePoint>>();
        Summary rawStatistics=new(null,null,null);
        foreach(var metric in new[]{query.Primary,query.Secondary,query.Fan}.OfType<string>().Distinct())
        {
            if(!query.Smoothed)
            {
                var raw=query.History.Series(metric,now,query.Seconds);
                if(metric==query.Primary)rawStatistics=SampleHistory.Statistics(raw);
                // Keep raw peaks/gaps while preparing dense traces off the UI thread too.
                series[metric]=query.Seconds>=3600?SampleHistory.ReduceForChart(raw,now,query.Seconds,query.Columns):raw;
            }
            else
            {
                var values=query.History.ChartRanges(metric,now,query.Seconds,query.Columns);ranges[metric]=values;
                series[metric]=values.Select(p=>new SamplePoint(DateTimeOffset.UnixEpoch,p.EndSeconds,p.Average)).ToArray();
            }
        }
        Summary summary;
        if(ranges.TryGetValue(query.Primary,out var primary))
        {
            long count=primary.Sum(p=>(long)p.Samples);
            summary=count==0?new(null,null,null):new(primary.Min(p=>p.Minimum),primary.Max(p=>p.Maximum),primary.Sum(p=>(p.Average??0)*(p.Samples/(double)count)));
        }
        else summary=rawStatistics;
        return new(query,series,ranges,summary);
    }
    public void Refresh()
    {
        revision++;preparationFailed=false;
        var query=CurrentQuery();
        if(IsLoaded&&IsVisible&&query.Seconds>=3600&&query.History.Count>4096&&prepared?.Query==query)
        {
            // One worker per chart, with just the latest pending refresh. No WPF objects leave this dispatcher.
            pending=(query,session.Collector.ElapsedSeconds,revision);if(!preparing)PrepareLatest();
        }
        else {pending=null;prepared=null;}
        InvalidateVisual();
    }
    private void PrepareLatest()
    {
        if(pending is not { } request)return;
        pending=null;
        preparing=true;
        _=Task.Run(()=>Prepare(request.Query,request.Now)).ContinueWith(work=>
        {
            // Explicit handoff also covers callers without a DispatcherSynchronizationContext.
            var error=work.Exception;
            if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;
            _=Dispatcher.InvokeAsync(()=>
            {
                preparing=false;
                if(request.Revision==revision&&IsLoaded&&IsVisible&&request.Query==CurrentQuery())
                {
                    preparationFailed=error is not null;
                    if(error is null){prepared=work.Result;StatisticsReady?.Invoke(prepared.Statistics);}
                    InvalidateVisual();
                }
                if(!IsLoaded||!IsVisible)pending=null;else if(pending is not null)PrepareLatest();
            });
        },TaskScheduler.Default);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=ActualWidth,h=ActualHeight;if(w<90||h<(compact?48:65))return;
        if(preparationFailed){Label(dc,"Chart temporarily unavailable",8,8);return;}
        var axisLabel=Formatted("Now");
        var sample=session.Current?.Sample;
        double now=session.Collector.ElapsedSeconds;
        var query=CurrentQuery();bool averaged=query.Smoothed;
        if(prepared?.Query!=query){prepared=Prepare(query,now);StatisticsReady?.Invoke(prepared.Statistics);}
        var bands=prepared.Ranges;
        IReadOnlyList<SamplePoint> Read(string metric)=>prepared.Series[metric];
        var points=Read(session.MetricId(kind));
        bool compare=CompareDevices&&kind is "cpu" or "cputemp";
        var secondary=kind=="network"?Read("network/upload"):compare?Read(session.MetricId(kind=="cpu"?"gpu":"gputemp")):[];
        bool showFan=session.Settings.ShowGpuFanSpeed&&(kind=="gputemp"||kind=="cputemp"&&compare);
        var fan=showFan?Read(session.MetricId("gpufan")):[];
        // Smoothed network axes follow the average trace; raw peaks remain in the band and statistics.
        double Peak(string metric,IReadOnlyList<SamplePoint> fallback)=>bands.TryGetValue(metric,out var range)?range.Max(p=>kind=="network"&&averaged?p.Average:p.Maximum)??0:fallback.Max(p=>p.Value)??0;
        double peak=Math.Max(Peak(session.MetricId(kind),points),Peak(kind=="network"?"network/upload":session.MetricId(kind=="cpu"?"gpu":"gputemp"),secondary));
        double maximum=kind is "cpu" or "gpu" or "memory"?100:kind=="network"?Math.Max(1000,peak*1.15):Math.Max(100,peak*1.1);
        if(compare&&kind=="cputemp")maximum=Math.Ceiling(maximum/20)*20;
        string Axis(double value)=>kind=="network"?Ui.Bytes(value,true):$"{value:0}{(kind.Contains("temp")?(compare?"°C":"°"):"%")}";
        double fanMaximum=Math.Max(1000,Math.Ceiling(Peak(session.MetricId("gpufan"),fan)/1000)*1000);
        string FanAxis(double value)=>$"{value:0} RPM";
        double left=compact?3:12,top=compact?8:18;
        if(showFan)left=Enumerable.Range(0,3).Max(i=>Formatted(FanAxis(fanMaximum*i/2)).Width)+10;
        double right=compact?Enumerable.Range(0,3).Max(i=>Formatted(Axis(maximum*i/2)).Width)+10:74;
        double bottom=axisLabel.Height+(compact?10:18),pw=w-left-right,ph=h-top-bottom;
        if(pw<=0||ph<=0)return;
        var plot=new Rect(left,top,pw,ph);GraphDepth.Surface(dc,plot);
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2,y=top+ph*(1-value/maximum);
            dc.DrawLine(new Pen(Theme.Brush("Line"),1){DashStyle=new DashStyle([3,5],0)},new(left,y),new(left+pw,y));
            Label(dc,Axis(value),left+pw+7,y-7);
            if(showFan)
            {
                string text=FanAxis(fanMaximum*i/2);
                Label(dc,text,left-Formatted(text).Width-7,y-7);
            }
        }
        double timeLabelY=h-axisLabel.Height-(compact?2:6);
        Label(dc,$"{HistoryRanges.Label(session.HistorySeconds)} ago"+(averaged?" · avg + range":query.Seconds>=3600?" · raw":""),left,timeLabelY);Label(dc,"Now",left+pw-axisLabel.Width,timeLabelY);
        dc.PushClip(new RectangleGeometry(new Rect(left,top,pw,ph)));
        // Retained WPF transform scrolls the recorded trace between polls without rebuilding it each frame.
        var scroll=new TranslateTransform();
        if(IsLoaded&&IsVisible&&points.Count+secondary.Count+fan.Count>0)
            scroll.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,-pw/session.HistorySeconds*Math.Min(1,session.Settings.PollingIntervalSeconds),TimeSpan.FromSeconds(Math.Min(1,session.Settings.PollingIntervalSeconds))));
        dc.PushTransform(scroll);
        void Trace(IReadOnlyList<SamplePoint> values,string metric,string color,bool dashed,bool dotted=false,double? scale=null,double fillStrength=1)
        {
            double maximumValue=scale??maximum;
            bands.TryGetValue(metric,out var range);
            DrawSeries(dc,values,now,maximumValue,left,top,pw,ph,color,dashed,dotted,query.Seconds>=3600,range,dotted?0:fillStrength);
        }
        Trace(points,session.MetricId(kind),Theme.GraphKey(kind),false,fillStrength:(kind=="network"||compare) ? .65 : 1);
        if(kind=="network")Trace(secondary,"network/upload",Theme.GraphKey("upload"),true,fillStrength:.42);
        else if(compare)Trace(secondary,session.MetricId(kind=="cpu"?"gpu":"gputemp"),Theme.GraphKey(kind=="cpu"?"gpu":"gputemp"),true,fillStrength:.45);
        if(showFan)Trace(fan,session.MetricId("gpufan"),Theme.GraphKey("gpufan"),false,true,fanMaximum);
        dc.Pop();dc.Pop();
        if(!points.Concat(secondary).Concat(fan).Any(p=>p.Value.HasValue)) Label(dc,sample is null?(compact?"Waiting for data…":"Waiting for first sample…"):(compact?"No readings in this range":"No available readings in this range"),left+(compact?4:15),top+ph/2);
    }
    private void DrawRange(DrawingContext dc,IReadOnlyList<ChartRangePoint> points,double now,double maximum,double left,double top,double width,double height,string color)
    {
        var band=new StreamGeometry();using(var context=band.Open())
        foreach(var point in points)
        {
            if(point.Minimum is not { } min||point.Maximum is not { } max)continue;
            double x=left+width*(1-(now-point.StartSeconds)/session.HistorySeconds),end=left+width*(1-(now-point.EndSeconds)/session.HistorySeconds);
            double y=top+height*(1-Math.Clamp(max/maximum,0,1)),bottom=top+height*(1-Math.Clamp(min/maximum,0,1));
            context.BeginFigure(new(x,y),true,true);context.LineTo(new(Math.Max(x+.8,end),y),true,false);context.LineTo(new(Math.Max(x+.8,end),bottom),true,false);context.LineTo(new(x,bottom),true,false);
        }
        band.Freeze();dc.PushOpacity(.14);dc.DrawGeometry(Theme.Brush(color),null,band);dc.Pop();
    }
    private void DrawSeries(DrawingContext dc,IReadOnlyList<SamplePoint> points,double now,double maximum,double left,double top,double width,double height,string color,bool dashed,bool dotted=false,bool reduced=false,IReadOnlyList<ChartRangePoint>? range=null,double fillStrength=1)
    {
        var geometry=new StreamGeometry();bool open=false;Point? lastPoint=null;
        List<IReadOnlyList<Point>>? segments=(Theme.GlassEnabled&&fillStrength>0)||RuneTrace.Enabled?new():null;List<Point>? segment=null;
        using(var context=geometry.Open())
        {
            foreach(var point in reduced?points:SampleHistory.ReduceForChart(points,now,session.HistorySeconds,(int)Math.Ceiling(width)))
            {
                if(!point.Value.HasValue){open=false;segment=null;continue;}
                var p=new Point(left+width*(1-(now-point.MonotonicSeconds)/session.HistorySeconds),top+height*(1-Math.Clamp(point.Value.Value/maximum,0,1)));
                if(!open){context.BeginFigure(p,false,false);open=true;if(segments is not null){segment=new();segments.Add(segment);}}else context.LineTo(p,true,false);
                segment?.Add(p);
                lastPoint=p;
            }
        }
        bool dense=CompareDevices&&session.HistorySeconds>=3600;
        geometry.Freeze();
        if(segments is not null)GraphDepth.Area(dc,segments,new(left,top,width,height),color,fillStrength);
        if(range is not null)DrawRange(dc,range,now,maximum,left,top,width,height,color);
        GraphDepth.TraceShadow(dc,geometry,session.Settings,color,dense?1:2,dashed,dotted);
        GraphStroke.Draw(dc,geometry,session.Settings,color,dense?1:2,dashed,dotted);
        if(segments is not null)RuneTrace.Draw(dc,segments,new(left,top,width,height),color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double markerRadius=dense?1.5:2.5;
        if(lastPoint.HasValue){GraphDepth.PointHalo(dc,lastPoint.Value,color,markerRadius);dc.DrawEllipse(Theme.Brush(color),null,lastPoint.Value,markerRadius,markerRadius);}
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
    private readonly TextBlock fan=Ui.Text("",11,Theme.GraphKey("gpufan"),font:"MetricFont");
    private readonly Run downloadValue=new(),uploadValue=new();
    private readonly HistoryChart chart;
    private readonly bool overview;
    private readonly Border? tint;
    private readonly TextBlock[] overviewStats=[];
    public ChartCard(UiSession session,string kind,bool popButton=true):this(session,kind,popButton,false) {}
    public ChartCard(UiSession session,string kind,bool popButton,bool overview)
    {
        this.session=session;this.kind=kind;this.overview=overview;CornerRadius=new(overview?14:10);BorderThickness=new(1);Padding=overview?new(16,13,16,11):new(16,12,16,10);Margin=new(6);SetResourceReference(BackgroundProperty,"GlassPanel");SetResourceReference(BorderBrushProperty,"GlassBorder");
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            value.FontSize=24;value.Inlines.Clear();value.Inlines.Add(downloadValue);value.Inlines.Add(uploadValue);
            downloadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("download"));uploadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("upload"));
        }
        // A forced MinHeight larger than the grid's remaining row caused WPF to clip axis labels.
        chart=new(session,kind){ClipToBounds=true};
        chart.StatisticsReady+=UpdateStatistics;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new DockPanel {LastChildFill=true};var title=overview?Ui.Text(Ui.Title(kind),13):Ui.Heading(Ui.Title(kind),14);title.FontWeight=FontWeights.SemiBold;header.Children.Add(title);
        if(popButton){var pop=overview?OverviewVisual.PopOut(session,kind):Ui.Button("Pop out ↗",()=>session.OpenDetached(kind));pop.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(pop,Dock.Right);header.Children.Insert(0,pop);}
        var colors=GraphColorPicker.Create(session,kind);DockPanel.SetDock(colors,Dock.Right);header.Children.Insert(0,colors);
        if(overview)
        {
            title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=Ui.Title(kind);
            var glyph=OverviewVisual.MetricGlyph(kind);glyph.Margin=new(0,0,9,0);DockPanel.SetDock(glyph,Dock.Left);header.Children.Insert(header.Children.Count-1,glyph);
            value.FontSize=34;value.FontWeight=FontWeights.SemiBold;
            subtitle.TextWrapping=TextWrapping.NoWrap;subtitle.TextTrimming=TextTrimming.CharacterEllipsis;
            fan.TextWrapping=TextWrapping.NoWrap;fan.TextTrimming=TextTrimming.CharacterEllipsis;
        }
        grid.Children.Add(header);
        var metric=new StackPanel {Margin=new Thickness(0,5,0,5)};metric.Children.Add(value);metric.Children.Add(subtitle);
        fan.Visibility=Visibility.Collapsed;fan.ToolTip="Selected GPU's first reported fan · separate left RPM scale";metric.Children.Add(fan);Grid.SetRow(metric,1);grid.Children.Add(metric);
        if(overview&&kind=="network")
        {
            metric.Children.Remove(value);
            var rates=new Grid();rates.ColumnDefinitions.Add(new());rates.ColumnDefinitions.Add(new());
            value.Inlines.Clear();
            foreach(var (run,column,label) in new[]{(downloadValue,0,"DOWNLOAD"),(uploadValue,1,"UPLOAD")})
            {
                var columnPanel=new StackPanel {Margin=new Thickness(0,0,column==0?8:0,0)};
                var caption=Ui.Text(label,9,"Muted");caption.Margin=new(0,1,0,0);columnPanel.Children.Add(caption);
                var reading=Ui.Text("",26,font:"MetricFont");reading.TextWrapping=TextWrapping.NoWrap;reading.Inlines.Add(run);
                columnPanel.Children.Add(new Viewbox {Child=reading,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Height=34});
                Grid.SetColumn(columnPanel,column);rates.Children.Add(columnPanel);
            }
            metric.Children.Insert(0,rates);subtitle.FontSize=10;
        }
        Grid.SetRow(chart,2);grid.Children.Add(chart);
        if(overview)
        {
            var footer=new Grid();
            overviewStats=new TextBlock[3];
            string[] labels=["MIN","AVG","MAX"];
            for(int i=0;i<3;i++)
            {
                footer.ColumnDefinitions.Add(new());var cell=new StackPanel();
                cell.Children.Add(Ui.Text((kind=="network"?"↓ ":"")+labels[i],9,"Muted"));overviewStats[i]=Ui.Text("—",11,"Muted",font:"MetricFont");overviewStats[i].TextWrapping=TextWrapping.NoWrap;
                overviewStats[i].TextTrimming=TextTrimming.CharacterEllipsis;cell.Children.Add(overviewStats[i]);Grid.SetColumn(cell,i);footer.Children.Add(cell);
            }
            if(kind=="network")footer.ToolTip="Download statistics over the selected history range";
            var divider=new Border {BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,7,0,0),Margin=new Thickness(0,4,0,0),Child=footer};divider.SetResourceReference(BorderBrushProperty,"Line");Grid.SetRow(divider,3);grid.Children.Add(divider);
            tint=new Border {CornerRadius=new(13),Opacity=.035,IsHitTestVisible=false,OpacityMask=new LinearGradientBrush(Colors.White,Colors.Transparent,new Point(0,0),new Point(.85,1))};
            tint.SetResourceReference(BackgroundProperty,Theme.GraphKey(kind));
            var layers=new Grid();layers.Children.Add(tint);layers.Children.Add(grid);Child=layers;
            void HoverTint(bool hovered)
            {
                double opacity=hovered ? .065 : .035;
                if(SystemParameters.ClientAreaAnimation&&!SystemParameters.HighContrast)
                    tint.BeginAnimation(OpacityProperty,new DoubleAnimation(opacity,TimeSpan.FromMilliseconds(120)));
                else {tint.BeginAnimation(OpacityProperty,null);tint.Opacity=opacity;}
            }
            MouseEnter+=(_,_)=>HoverTint(true);MouseLeave+=(_,_)=>HoverTint(false);
        }
        else {Grid.SetRow(stats,3);grid.Children.Add(stats);Child=grid;}
        Loaded+=(_,_)=>{session.Changed+=Refresh;Refresh();};Unloaded+=(_,_)=>session.Changed-=Refresh;
        IsVisibleChanged+=(_,_)=>{if(IsVisible)Refresh();};
    }
    private void Refresh()
    {
        if(!IsVisible)return;
        if(tint is not null)tint.Visibility=Theme.GlassEnabled?Visibility.Visible:Visibility.Collapsed;
        string id=session.MetricId(kind);double? current=session.Stale?null:session.Value(id);var data=session.Current;
        bool showFan=kind=="gputemp"&&session.Settings.ShowGpuFanSpeed;
        fan.Visibility=showFan?Visibility.Visible:Visibility.Collapsed;
        if(showFan)fan.Text=Ui.GpuFanText(session)+$" · {GraphStroke.Pattern(session.Settings,"gpufan",dotted:true)} · left RPM";
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            downloadValue.Text=(overview?"":"↓ ")+Ui.Bytes(current,true)+(overview?"":"    ");uploadValue.Text=(overview?"":"↑ ")+Ui.Bytes(session.Stale?null:session.Value("network/upload"),true);
            string Pattern(string metric,bool dashed)=>CultureInfo.CurrentCulture.TextInfo.ToTitleCase(GraphStroke.Pattern(session.Settings,metric,dashed));
            subtitle.Text=$"{Pattern("download",false)}: download    ·    {Pattern("upload",true)}: upload    ·    Shared scale";
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
        if(overview)subtitle.ToolTip=subtitle.Text;
        chart.Refresh();
    }
    private void UpdateStatistics(Summary summary)
    {
        Func<double?,string> format=kind=="network"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,kind.Contains("temp")?"°C":"%");
        stats.Text=$"MIN  {format(summary.Minimum)}      AVG  {format(summary.Average)}      MAX  {format(summary.Maximum)}"+(kind=="network"?"    · Download":"");
        if(overview)
        {
            double?[] values=[summary.Minimum,summary.Average,summary.Maximum];
            for(int i=0;i<overviewStats.Length;i++){overviewStats[i].Text=format(values[i]);overviewStats[i].ToolTip=overviewStats[i].Text;}
        }
    }
}
