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
    /// <summary>A large reading. Tabular numerals keep the digits from shifting as values change.</summary>
    public static TextBlock Metric(string value,double size=34,string color="Ink")
    {var t=Text(value,size,color,"MetricFont");t.FontWeight=FontWeights.SemiBold;t.TextWrapping=TextWrapping.NoWrap;Typography.SetNumeralAlignment(t,FontNumeralAlignment.Tabular);return t;}
    /// <summary>A small uppercase label above a value or section (MIN / AVG / MAX, group titles).</summary>
    public static TextBlock Caption(string value,string color="Muted")
    {var t=Text(value.ToUpperInvariant(),9.5,color);t.TextWrapping=TextWrapping.NoWrap;return t;}
    public static Button Button(string value,Action action)
    {var b=new Button {Content=value};b.Click+=(_,_)=>action();return b;}
    public static StackPanel Row(params UIElement[] children)
    {var p=new StackPanel {Orientation=Orientation.Horizontal};foreach(var c in children)p.Children.Add(c);return p;}
    public static Border Panel(UIElement child,Thickness? padding=null)
    {var b=new Border {Child=child,Padding=padding??new Thickness(18)};b.SetResourceReference(FrameworkElement.StyleProperty,"Card");return b;}
    /// <summary>The value cell style of the Processes and Destinations tables: tabular numerals, so digits never shift as values change, and an ellipsis.</summary>
    public static readonly Style ValueText=ValueTextStyle();
    private static Style ValueTextStyle()
    {
        var style=new Style(typeof(TextBlock),DataGridTextColumn.DefaultElementStyle);
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(Typography.NumeralAlignmentProperty,FontNumeralAlignment.Tabular));
        style.Seal();return style;
    }
    public static string Number(double? value,string unit="")=>value.HasValue&&double.IsFinite(value.Value)?$"{value.Value:0.0}{unit}":"—";
    public static string GpuFanText(UiSession session)
    {
        double? rpm=session.Stale?null:session.Value(session.MetricId("gpufan"));
        return "GPU fan · "+(session.Stale?"stale":rpm.HasValue&&double.IsFinite(rpm.Value)?$"{rpm.Value:0} RPM":"unavailable");
    }
    public static string CpuPumpText(UiSession session)
    {
        double? rpm=session.Stale?null:session.Value(session.MetricId("cpupump"));
        return "CPU pump · "+(session.Stale?"stale":rpm.HasValue&&double.IsFinite(rpm.Value)?$"{rpm.Value:0} RPM":"unavailable");
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
    public static string Title(string kind)=>kind switch {"cpu"=>"CPU usage","cputemp"=>"CPU temperature","cpupump"=>"CPU pump speed","gpu"=>"GPU usage","gputemp"=>"GPU temperature","gpufan"=>"GPU fan speed","usage"=>"CPU / GPU usage","temperature"=>"CPU / GPU temperature","memory"=>"RAM usage","storage"=>"Disk activity",_=>"Network activity"};
    public static FrameworkElement Range(UiSession session)=>new RangeSegments(session);

    private static readonly ControlTemplate NavTemplate=SealedTemplate("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Grid>
            <Border x:Name="Frame" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="9" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
              <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center" RecognizesAccessKey="True"/>
            </Border>
            <Border x:Name="Bar" Width="3" HorizontalAlignment="Left" Margin="0,8,0,8" CornerRadius="1.5" Background="{DynamicResource Accent}" Visibility="Collapsed"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource GlassHover}"/><Setter Property="Foreground" Value="{DynamicResource Ink}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Accent}"/></Trigger>
            <Trigger Property="Tag" Value="active"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource AccentSoft}"/><Setter TargetName="Bar" Property="Visibility" Value="Visible"/><Setter Property="Foreground" Value="{DynamicResource Ink}"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private static ControlTemplate SealedTemplate(string xaml){var template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);template.Seal();return template;}
    private static readonly Style NavStyle=NavButtonStyle();
    private static Style NavButtonStyle()
    {
        var style=new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty,NavTemplate));
        style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Muted")));
        style.Setters.Add(new Setter(Control.FontFamilyProperty,new DynamicResourceExtension("BodyFont")));
        style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,9,10,9)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,2,0,2)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty,System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Left));
        style.Seal();return style;
    }
    /// <summary>A rail button: vector glyph plus label. <c>Tag=="active"</c> shows the accent bar and soft fill.</summary>
    public static Button NavButton(string route,string label,Action click)
    {
        var glyph=OverviewVisual.NavGlyph(route);glyph.Margin=new Thickness(0,0,11,0);glyph.VerticalAlignment=VerticalAlignment.Center;
        var text=new TextBlock {Text=label,FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        var row=new DockPanel();row.Children.Add(glyph);row.Children.Add(text);
        var button=new Button {Content=row,Style=NavStyle,HorizontalAlignment=HorizontalAlignment.Stretch};
        System.Windows.Automation.AutomationProperties.SetName(button,label);
        button.Click+=(_,_)=>click();
        return button;
    }
}

/// <summary>Native WPF drawing; real timestamps set X coordinates and missing readings split the line.</summary>
internal sealed class HistoryChart(UiSession session,string kind,bool compact=false) : FrameworkElement
{
    private readonly UiSession session=session;
    private readonly string kind=kind;
    public bool CompareDevices {get;init;}
    /// <summary>Lets a joined CPU/GPU temperature chart carry the CPU pump line too (the widget).</summary>
    public bool PumpWithDevices {get;init;}
    /// <summary>Lets a joined CPU/GPU usage chart carry the per-core CPU traces too (the widget).</summary>
    public bool CoresWithDevices {get;init;}
    /// <summary>Limits a storage chart to one drive: its read and write become the traces and the per-drive lines are off.</summary>
    public string? DriveId {get;init;}
    private sealed record Query(SampleHistory History,string Primary,string? Secondary,string? Fan,string? Pump,int Seconds,int Columns,bool Smoothed,int Cores,string Drives="",bool Local=false);
    /// <summary>The local-network share of the network totals (per-app capture); drawn muted under the totals.</summary>
    private const string LocalDownload="network/download/local",LocalUpload="network/upload/local";
    private sealed record ChartData(Query Query,Dictionary<string,IReadOnlyList<SamplePoint>> Series,Dictionary<string,IReadOnlyList<ChartRangePoint>> Ranges,Summary Statistics);
    public event Action<Summary>? StatisticsReady;
    private ChartData? prepared;
    private (Query Query,double Now,int Revision)? pending;
    private int revision;
    private bool preparing,preparationFailed;
    private Query CurrentQuery()
    {
        bool compare=CompareDevices&&kind is "cpu" or "cputemp";
        bool drive=kind=="storage"&&DriveId is not null;
        return new(session.Collector.History,drive?StorageMetrics.Key(DriveId!,"read"):session.MetricId(kind),kind=="network"?"network/upload":drive?StorageMetrics.Key(DriveId!,"write"):kind=="storage"?"storage/total/write":compare?session.MetricId(kind=="cpu"?"gpu":"gputemp"):null,
            ShowsFan(compare)?session.MetricId("gpufan"):null,ShowsPump(compare)?session.MetricId("cpupump"):null,session.HistorySeconds,Math.Max(1,(int)ActualWidth-90),session.Settings.GraphSmoothing&&session.HistorySeconds>=3600,
            ShowsCores()?session.Current?.CpuCoreCount??0:0,
            // The drive set is part of the key, so a changed set rebuilds the per-drive traces.
            kind=="storage"&&!drive?string.Join("\n",TracedDrives().Select(d=>d.Id)):"",kind=="network");
    }
    /// <summary>Every present drive by disk index, removable ones included: their live samples are traced even though only
    /// persistable drives reach the six-hour graph history.</summary>
    private IReadOnlyList<StorageDrive> TracedDrives()=>(session.Current?.Storage.Drives??[]).OrderBy(d=>d.Index).ToList();
    private static string[] DriveIds(Query query)=>query.Drives.Length==0?[]:query.Drives.Split('\n');
    private bool ShowsCores()=>session.Settings.ShowCpuCoreUsage&&kind=="cpu"&&(!CompareDevices||CoresWithDevices);
    private bool ShowsFan(bool compare)=>session.Settings.ShowGpuFanSpeed&&(kind=="gputemp"||kind=="cputemp"&&compare);
    private bool ShowsPump(bool compare)=>session.Settings.ShowCpuPumpSpeed&&kind=="cputemp"&&(!compare||PumpWithDevices);
    private static ChartData Prepare(Query query,double now)
    {
        var series=new Dictionary<string,IReadOnlyList<SamplePoint>>();var ranges=new Dictionary<string,IReadOnlyList<ChartRangePoint>>();
        Summary rawStatistics=new(null,null,null);
        var cores=Enumerable.Range(0,query.Cores).Select(CpuCoreUsage.Key).ToArray();
        // Core traces come from one pass over the samples instead of one locked scan per core.
        var coreRaw=!query.Smoothed&&cores.Length>0?query.History.SeriesMany(cores,now,query.Seconds):null;
        var coreRanges=query.Smoothed&&cores.Length>0?query.History.ChartRangesMany(cores,now,query.Seconds,query.Columns):null;
        // Per-drive write is prepared for the hover chip only; it is never drawn.
        var drives=DriveIds(query).SelectMany(id=>new[]{StorageMetrics.Key(id,"read"),StorageMetrics.Key(id,"write")});
        string[] local=query.Local?[LocalDownload,LocalUpload]:[];
        foreach(var metric in new[]{query.Primary,query.Secondary,query.Fan,query.Pump}.OfType<string>().Concat(cores).Concat(drives).Concat(local).Distinct())
        {
            if(!query.Smoothed)
            {
                var raw=coreRaw is not null&&coreRaw.TryGetValue(metric,out var many)?many:query.History.Series(metric,now,query.Seconds);
                if(metric==query.Primary)rawStatistics=SampleHistory.Statistics(raw);
                // Keep raw peaks/gaps while preparing dense traces off the UI thread too.
                series[metric]=query.Seconds>=3600?SampleHistory.ReduceForChart(raw,now,query.Seconds,query.Columns):raw;
            }
            else
            {
                var values=coreRanges is not null&&coreRanges.TryGetValue(metric,out var manyRanges)?manyRanges:query.History.ChartRanges(metric,now,query.Seconds,query.Columns);ranges[metric]=values;
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
    private sealed record TracePath(IReadOnlyList<SamplePoint> Points,StreamGeometry Path,PathGeometry? Copy,List<IReadOnlyList<Point>> Runs,Point? Last)
    {
        /// <summary>Area fill and range band, built once per frame so hover re-renders only redraw.</summary>
        public PathGeometry? Area { get; set; }
        public bool AreaBuilt { get; set; }
        public StreamGeometry? Band { get; set; }
    }
    private sealed record TraceSpec(string Metric,string Color,bool Dashed,bool Dotted,double Maximum,double Fill)
    {
        /// <summary>A per-core CPU trace: thin plain stroke in <see cref="Brush"/>, listed in the hover chip by value.</summary>
        public bool Core {get;init;}
        public Brush? Brush {get;init;}
        /// <summary>The CPU core index behind a <see cref="Core"/> trace (its cpucore/N style key); -1 for per-drive traces.</summary>
        public int CoreIndex {get;init;}=-1;
        /// <summary>The core's cpucore/N style key, precomputed so the draw loop allocates nothing.</summary>
        public string? CoreKey {get;init;}
        /// <summary>Built and hovered but not drawn: the aggregate CPU trace once the per-core traces replace it.</summary>
        public bool Hidden {get;init;}
        /// <summary>A per-drive storage trace (drawn like a core); <see cref="Metric"/> is its read key.</summary>
        public string? DriveId {get;init;}
        /// <summary>The local-network share of a network total: muted, thinner, under the totals; one shared "Local" chip line.</summary>
        public bool Local {get;init;}
    }
    private sealed record Frame(Query Query,int Revision,ChartData Data,Size Size,string Theme,bool Glass,double Now,DateTimeOffset WallNow,TraceSpec[] Specs,Dictionary<string,TracePath> Paths);
    /// <summary>Geometry built once per poll, size and theme; hover re-renders reuse it.</summary>
    private Frame? cache;
    private Point? hover,hoverAnchor;
    private List<(string Text,Brush Color)> hoverLines=[];
    internal Rect PlotRect {get;private set;}
    /// <summary>Index into the primary series of the sample under the pointer, or -1 (no pointer, a gap, or nothing nearby).</summary>
    internal int HoverIndex {get;private set;}=-1;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e){base.OnMouseMove(e);SetHover(e.GetPosition(this));}
    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e){base.OnMouseLeave(e);SetHover(null);}
    internal void SetHover(Point? position)
    {
        hover=position;
        int previous=HoverIndex;
        ResolveHover();
        if(HoverIndex!=previous)InvalidateVisual();
    }
    private double HoverTolerance(Query query)=>Math.Max(1.5*Math.Max(1,session.Settings.PollingIntervalSeconds),session.HistorySeconds/(double)Math.Max(1,query.Columns));
    private void ResolveHover()
    {
        HoverIndex=-1;
        if(hover is not { } p||cache is not { } frame||!PlotRect.Contains(p)||frame.Specs.Length==0)return;
        var primary=frame.Paths[frame.Specs[0].Metric];
        if(primary.Points.Count==0)return;
        double seconds=frame.Now-(PlotRect.Right-p.X)/PlotRect.Width*session.HistorySeconds;
        HoverIndex=NearestSample.Find(primary.Points,seconds,HoverTolerance(frame.Query));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w=ActualWidth,h=ActualHeight;if(w<90||h<(compact?48:65))return;
        // A transparent surface so the pointer reaches this element for hover.
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,w,h));
        if(preparationFailed){Label(dc,"Chart temporarily unavailable",8,8);return;}
        var sample=session.Current?.Sample;
        var query=CurrentQuery();bool averaged=query.Smoothed;
        if(prepared?.Query!=query){prepared=Prepare(query,session.Collector.ElapsedSeconds);StatisticsReady?.Invoke(prepared.Statistics);}
        var bands=prepared.Ranges;
        IReadOnlyList<SamplePoint> Read(string metric)=>prepared.Series[metric];
        string primaryMetric=query.Primary;var points=Read(primaryMetric);
        bool compare=CompareDevices&&kind is "cpu" or "cputemp";
        bool bytes=kind is "network" or "storage";
        string? secondaryMetric=query.Secondary;
        var secondary=secondaryMetric is null?[]:Read(secondaryMetric);
        bool showFan=ShowsFan(compare),showPump=ShowsPump(compare),showRpm=showFan||showPump;
        var fan=showFan?Read(session.MetricId("gpufan")):[];
        var pump=showPump?Read(session.MetricId("cpupump")):[];
        // Smoothed network axes follow the average trace; raw peaks remain in the band and statistics.
        double Peak(string metric,IReadOnlyList<SamplePoint> fallback)=>bands.TryGetValue(metric,out var range)?range.Max(p=>bytes&&averaged?p.Average:p.Maximum)??0:fallback.Max(p=>p.Value)??0;
        double peak=Math.Max(Peak(primaryMetric,points),secondaryMetric is null?0:Peak(secondaryMetric,secondary));
        double maximum=kind is "cpu" or "gpu" or "memory"?100:bytes?Math.Max(1000,peak*1.15):Math.Max(100,peak*1.1);
        if(compare&&kind=="cputemp")maximum=Math.Ceiling(maximum/20)*20;
        string Axis(double value)=>bytes?Ui.Bytes(value,true):$"{value:0}{(kind.Contains("temp")?(compare?"°C":"°"):"%")}";
        // GPU fan and CPU pump share the left RPM scale when both are shown.
        double fanMaximum=Math.Max(1000,Math.Ceiling(Math.Max(showFan?Peak(session.MetricId("gpufan"),fan):0,showPump?Peak(session.MetricId("cpupump"),pump):0)/1000)*1000);
        string FanAxis(double value)=>$"{value:0} RPM";
        var typeface=Theme.ChartTypeface;double pixelsPerDip=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double left=compact?3:8,top=compact?8:14;
        double right=8,bottom=Formatted("Now").Height+(compact?6:9),pw=w-left-right,ph=h-top-bottom;
        if(pw<=0||ph<=0)return;
        var plot=new Rect(left,top,pw,ph);PlotRect=plot;
        bool hit=cache is { } current&&current.Query==query&&current.Revision==revision&&ReferenceEquals(current.Data,prepared)&&current.Size==RenderSize&&current.Theme==Theme.CurrentName&&current.Glass==Theme.GlassEnabled;
        if(!hit)
        {
            double built=session.Collector.ElapsedSeconds;
            // With per-core traces the aggregate CPU line is hidden (the cores replace it) but still built for hover and the chip.
            var specs=new List<TraceSpec> {new(primaryMetric,Theme.GraphKey(kind),false,false,maximum,(bytes||compare)?.65:1){Hidden=query.Cores>0}};
            if(kind=="network")
            {
                specs.Add(new("network/upload",Theme.GraphKey("upload"),true,false,maximum,.42));
                // Local share never exceeds its total, so it rides the totals' axis without driving it.
                specs.Add(new(LocalDownload,Theme.GraphKey("download"),false,false,maximum,.3){Local=true});
                specs.Add(new(LocalUpload,Theme.GraphKey("upload"),true,false,maximum,.2){Local=true});
            }
            else if(kind=="storage")specs.Add(new(secondaryMetric!,Theme.GraphKey("diskwrite"),true,false,maximum,.42));
            else if(compare)specs.Add(new(secondaryMetric!,Theme.GraphKey(kind=="cpu"?"gpu":"gputemp"),true,false,maximum,.45));
            if(showFan)specs.Add(new(session.MetricId("gpufan"),Theme.GraphKey("gpufan"),false,true,fanMaximum,0));
            if(showPump)specs.Add(new(session.MetricId("cpupump"),Theme.GraphKey("cpupump"),false,true,fanMaximum,0));
            for(int i=0;i<query.Cores;i++)specs.Add(new(CpuCoreUsage.Key(i),Theme.GraphKey("cpu"),false,false,maximum,0){Core=true,Brush=Theme.CoreBrush(i,query.Cores),CoreIndex=i,CoreKey=GraphColorSettings.CoreKey(i)});
            var driveIds=DriveIds(query);
            for(int i=0;i<driveIds.Length;i++)specs.Add(new(StorageMetrics.Key(driveIds[i],"read"),Theme.GraphKey("diskread"),false,false,maximum,0){Core=true,Brush=Theme.DriveBrush(i,driveIds.Length),DriveId=driveIds[i]});
            bool spline=!(query.Seconds>=3600&&!query.Smoothed);
            var paths=new Dictionary<string,TracePath>();
            foreach(var spec in specs)paths[spec.Metric]=BuildTrace(Read(spec.Metric),built,spec.Maximum,plot,query.Seconds>=3600,spline,glowCopy:!spec.Core&&!spec.Local);
            cache=new(query,revision,prepared,RenderSize,Theme.CurrentName,Theme.GlassEnabled,built,DateTimeOffset.Now,specs.ToArray(),paths);
            if(hover is not null)ResolveHover();
        }
        var frame=cache!;double now=frame.Now;
        GraphDepth.Surface(dc,plot);
        var lines=new (double Y,string Label)[3];var leftLabels=showRpm?new (double Y,string Label)[3]:null;
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2,y=top+ph*(1-value/maximum);
            lines[i]=(y,Axis(value));if(leftLabels is not null)leftLabels[i]=(y,FanAxis(fanMaximum*i/2));
        }
        // Retained WPF transform scrolls the recorded trace between polls without rebuilding it each frame.
        var scroll=new TranslateTransform();
        double interval=Math.Min(1,session.Settings.PollingIntervalSeconds);
        // The hidden aggregate trace still counts: its points mean the cores it stands in for have data to scroll.
        bool anyPoints=frame.Specs.Where(s=>!s.Core&&!s.Local).Any(s=>frame.Paths[s.Metric].Points.Count>0);
        if(!hit&&IsLoaded&&IsVisible&&anyPoints)scroll.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(0,-pw/session.HistorySeconds*interval,TimeSpan.FromSeconds(interval)));
        else if(hit)scroll.X=-pw/session.HistorySeconds*Math.Clamp(session.Collector.ElapsedSeconds-now,0,interval);
        // Value lines stay put; the time labels ride the same scroll as the traces so they never jump at a poll.
        var ticks=TimeTicks.For(session.HistorySeconds,now,frame.WallNow,pw,compact,TimeZoneInfo.Local).Select(t=>(left+pw*(1-(now-t.MonotonicSeconds)/session.HistorySeconds),t.Label,t.Ordinal)).ToArray();
        ChartFrame.Grid(dc,plot,lines,[],typeface,pixelsPerDip,compact,leftLabels,nowLabel:null);
        ChartFrame.ScrollingTicks(dc,plot,ticks,typeface,pixelsPerDip,compact,scroll);
        dc.PushClip(new RectangleGeometry(plot));
        dc.PushTransform(scroll);
        // Cores sit underneath the primary and comparison traces.
        foreach(var spec in frame.Specs.Where(s=>s.Core))DrawTrace(dc,frame.Paths[spec.Metric],spec,plot,null,now);
        // The local share sits under its total: the gap between the two is the internet share.
        foreach(var spec in frame.Specs.Where(s=>s.Local))DrawTrace(dc,frame.Paths[spec.Metric],spec,plot,null,now);
        foreach(var spec in frame.Specs.Where(s=>!s.Core&&!s.Local&&!s.Hidden))DrawTrace(dc,frame.Paths[spec.Metric],spec,plot,bands.GetValueOrDefault(spec.Metric),now);
        hoverAnchor=null;hoverLines=[];
        if(HoverIndex>=0&&frame.Paths[primaryMetric].Points.Count>HoverIndex)DrawHover(dc,frame,primaryMetric,plot,now,Axis,FanAxis);
        dc.Pop();dc.Pop();
        if(hoverAnchor is { } anchor)ChartFrame.Chip(dc,anchor,plot,hoverLines,typeface,pixelsPerDip,compact);
        if(!points.Concat(secondary).Concat(fan).Concat(pump).Any(p=>p.Value.HasValue)) Label(dc,sample is null?(compact?"Waiting for data…":"Waiting for first sample…"):(compact?"No readings in this range":"No available readings in this range"),left+(compact?4:15),top+ph/2);
    }
    private void DrawHover(DrawingContext dc,Frame frame,string primaryMetric,Rect plot,double now,Func<double,string> axis,Func<double,string> fanAxis)
    {
        var primary=frame.Paths[primaryMetric];var sample=primary.Points[HoverIndex];
        double x=plot.Left+plot.Width*(1-(now-sample.MonotonicSeconds)/session.HistorySeconds);
        var pen=new Pen(Theme.Brush("Hairline"),1);pen.Freeze();
        dc.DrawLine(pen,new(x,plot.Top),new(x,plot.Bottom));
        if(!compact)hoverLines.Add((TimeZoneInfo.ConvertTime(frame.WallNow.AddSeconds(sample.MonotonicSeconds-now),TimeZoneInfo.Local).ToString("HH:mm:ss",CultureInfo.CurrentCulture),Theme.Brush("Muted")));
        double tolerance=HoverTolerance(frame.Query);
        var cores=new List<(int Core,double Value)>();var coreBrushes=new Dictionary<int,Brush>();
        int firstCore=Array.FindIndex(frame.Specs,s=>s.Core&&s.DriveId is null);
        Point? hiddenAnchor=null;double? localDown=null,localUp=null;
        for(int position=0;position<frame.Specs.Length;position++)
        {
            var spec=frame.Specs[position];if(spec.DriveId is not null)continue;var path=frame.Paths[spec.Metric];
            int index=spec.Metric==primaryMetric?HoverIndex:NearestSample.Find(path.Points,sample.MonotonicSeconds,tolerance);
            if(index<0||path.Points[index].Value is not { } value)continue;
            // Cores join the chip after the loop, by value, with no dot of their own.
            if(spec.Core){int core=position-firstCore;cores.Add((core,value));coreBrushes[core]=spec.Brush!;continue;}
            // The two local shares make one muted chip line after the totals, with no dot of their own.
            if(spec.Local){if(spec.Metric==LocalDownload)localDown=value;else localUp=value;continue;}
            var brush=spec.Brush??Theme.Brush(spec.Color);
            double y=plot.Top+plot.Height*(1-Math.Clamp(value/spec.Maximum,0,1));
            // A hidden trace keeps its chip line but draws no dot; it anchors the chip only when nothing drawn can.
            if(spec.Hidden)hiddenAnchor??=new(x,y);
            else{dc.DrawEllipse(brush,null,new(x,y),3,3);hoverAnchor??=new(x,y);}
            hoverLines.Add((spec.Dotted?fanAxis(value):axis(value),brush));
        }
        hoverAnchor??=hiddenAnchor;
        if(localDown is not null||localUp is not null)hoverLines.Add(($"Local ↓ {Ui.Bytes(localDown,true)} ↑ {Ui.Bytes(localUp,true)}",Theme.Brush("Muted")));
        foreach(var (text,core) in HoverCoreLines(cores))hoverLines.Add((text,core>=0?coreBrushes[core]:Theme.Brush("Muted")));
        // Drives follow the totals, busiest first; a drive with neither rate at this sample is left out.
        var models=(session.Current?.Storage.Drives??[]).GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.First().Model);
        double? At(string metric)
        {
            if(!frame.Data.Series.TryGetValue(metric,out var points))return null;
            int index=NearestSample.Find(points,sample.MonotonicSeconds,tolerance);
            return index<0?null:points[index].Value;
        }
        var drives=new List<(string Text,double Rate,Brush Brush)>();
        foreach(var spec in frame.Specs.Where(s=>s.DriveId is not null))
        {
            double? read=At(StorageMetrics.Key(spec.DriveId!,"read")),write=At(StorageMetrics.Key(spec.DriveId!,"write"));
            if(read is null&&write is null)continue;
            drives.Add(($"{models.GetValueOrDefault(spec.DriveId!,spec.DriveId!)} \u00b7 {Ui.Bytes(read,true)} \u2193 {Ui.Bytes(write,true)} \u2191",(read??0)+(write??0),spec.Brush!));
        }
        foreach(var drive in drives.OrderByDescending(d=>d.Rate))hoverLines.Add((drive.Text,drive.Brush));
    }
    /// <summary>The hover chip's core section: the eight busiest cores, then a count of the rest.</summary>
    internal static IReadOnlyList<(string Text,int Core)> HoverCoreLines(IReadOnlyList<(int Core,double Value)> cores)
    {
        const int shown=8;
        var lines=cores.OrderByDescending(c=>c.Value).ThenBy(c=>c.Core).Take(shown)
            .Select(c=>(string.Create(CultureInfo.CurrentCulture,$"Core {c.Core+1} \u00b7 {c.Value:0}%"),c.Core)).ToList();
        if(cores.Count>shown)lines.Add(($"+{cores.Count-shown} more",-1));
        return lines;
    }
    private TracePath BuildTrace(IReadOnlyList<SamplePoint> points,double now,double maximum,Rect plot,bool reduced,bool spline,bool glowCopy=true)
    {
        var runs=new List<IReadOnlyList<Point>>();List<Point>? run=null;Point? last=null;
        var drawn=reduced?points:SampleHistory.ReduceForChart(points,now,session.HistorySeconds,(int)Math.Ceiling(plot.Width));
        foreach(var point in drawn)
        {
            if(!point.Value.HasValue){run=null;continue;}
            var p=new Point(plot.Left+plot.Width*(1-(now-point.MonotonicSeconds)/session.HistorySeconds),plot.Top+plot.Height*(1-Math.Clamp(point.Value.Value/maximum,0,1)));
            if(run is null){run=new();runs.Add(run);}
            run.Add(p);last=p;
        }
        StreamGeometry path;
        if(spline)path=ChartFrame.Spline(runs);
        else
        {
            path=new StreamGeometry();
            using(var context=path.Open())
                foreach(var r in runs){context.BeginFigure(r[0],false,false);for(int i=1;i<r.Count;i++)context.LineTo(r[i],true,false);}
            path.Freeze();
        }
        // A PathGeometry copy (flattening drops open stroked figures) so the glow pass stays off the StreamGeometry count.
        // Core traces never glow, so they skip the throw-away copy.
        PathGeometry? copy=null;
        if(glowCopy){copy=PathGeometry.CreateFromGeometry(path);copy.Freeze();}
        return new(drawn,path,copy,runs,last);
    }
    private void DrawTrace(DrawingContext dc,TracePath path,TraceSpec spec,Rect plot,IReadOnlyList<ChartRangePoint>? range,double now)
    {
        bool dense=CompareDevices&&session.HistorySeconds>=3600;double width=dense?1:2;
        if(spec.Core)
        {
            // Thin and unmarked: half the CPU trace's stroke, and no range band (eight bands would pile up).
            // A core with its own cpucore/N style takes that style's thickness, pattern and glow in the core brush instead.
            // A core's own thickness is relative to that thin width, so 100% on a core slider keeps its siblings' width.
            double thin=.5*width*(session.Settings.GraphStyles.TryGetValue("cpu",out var cpuStyle)?cpuStyle.Thickness:1);
            if(spec.CoreKey is not null&&session.Settings.GraphStyles.TryGetValue(spec.CoreKey,out var coreStyle))GraphStroke.DrawStyled(dc,path.Path,spec.Brush!,coreStyle,thin*coreStyle.Thickness);
            else GraphStroke.DrawPlain(dc,path.Path,spec.Brush!,thin);
            return;
        }
        if(spec.Local)
        {
            // Muted and half-width in the total's own colour and pattern; no glow, band or end marker.
            // With the per-app capture off the series is all gaps, and nothing is drawn.
            if(path.Runs.Count==0)return;
            if(!path.AreaBuilt){path.Area=GraphDepth.AreaGeometry(path.Runs,plot);path.AreaBuilt=true;}
            GraphDepth.Area(dc,path.Area,plot,spec.Color,spec.Fill);
            dc.PushOpacity(.55);GraphStroke.Draw(dc,path.Path,session.Settings,spec.Color,.5*width,spec.Dashed,spec.Dotted);dc.Pop();
            return;
        }
        if(spec.Fill>0)
        {
            if(!path.AreaBuilt){path.Area=GraphDepth.AreaGeometry(path.Runs,plot);path.AreaBuilt=true;}
            GraphDepth.Area(dc,path.Area,plot,spec.Color,spec.Fill);
        }
        if(range is not null)
        {
            path.Band??=RangeBand(range,now,spec.Maximum,plot.Left,plot.Top,plot.Width,plot.Height);
            dc.PushOpacity(.14);dc.DrawGeometry(Theme.Brush(spec.Color),null,path.Band);dc.Pop();
        }
        var style=GraphStroke.Style(session.Settings,spec.Color);
        if(!style.Glow&&path.Copy is not null)GraphDepth.UnderGlow(dc,path.Copy,spec.Color,width*style.Thickness);
        GraphStroke.Draw(dc,path.Path,session.Settings,spec.Color,width,spec.Dashed,spec.Dotted);
        RuneTrace.Draw(dc,path.Runs,plot,spec.Color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double markerRadius=dense?1.5:2.5;
        if(path.Last is { } last){GraphDepth.PointHalo(dc,last,spec.Color,markerRadius);dc.DrawEllipse(Theme.Brush(spec.Color),null,last,markerRadius,markerRadius);}
    }
    private StreamGeometry RangeBand(IReadOnlyList<ChartRangePoint> points,double now,double maximum,double left,double top,double width,double height)
    {
        var band=new StreamGeometry();using(var context=band.Open())
        foreach(var point in points)
        {
            if(point.Minimum is not { } min||point.Maximum is not { } max)continue;
            double x=left+width*(1-(now-point.StartSeconds)/session.HistorySeconds),end=left+width*(1-(now-point.EndSeconds)/session.HistorySeconds);
            double y=top+height*(1-Math.Clamp(max/maximum,0,1)),bottom=top+height*(1-Math.Clamp(min/maximum,0,1));
            context.BeginFigure(new(x,y),true,true);context.LineTo(new(Math.Max(x+.8,end),y),true,false);context.LineTo(new(Math.Max(x+.8,end),bottom),true,false);context.LineTo(new(x,bottom),true,false);
        }
        band.Freeze();return band;
    }
    /// <summary>Draws one series directly (pop-out fixtures and tests); the dashboard path goes through the cached frame.</summary>
    private void DrawSeries(DrawingContext dc,IReadOnlyList<SamplePoint> points,double now,double maximum,double left,double top,double width,double height,string color,bool dashed,bool dotted=false,bool reduced=false,IReadOnlyList<ChartRangePoint>? range=null,double fillStrength=1)
    {
        var plot=new Rect(left,top,width,height);
        bool spline=!(session.HistorySeconds>=3600&&!session.Settings.GraphSmoothing);
        DrawTrace(dc,BuildTrace(points,now,maximum,plot,reduced,spline),new("",color,dashed,dotted,maximum,dotted?0:fillStrength),plot,range,now);
    }
    private void Label(DrawingContext dc,string text,double x,double y)
    {dc.DrawText(Formatted(text),new(x,y));}
    private FormattedText Formatted(string text)=>new(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Theme.ChartTypeface,11,Theme.Brush("Muted"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
internal sealed class ChartCard : Border
{
    private readonly UiSession session;
    private readonly string kind;
    private readonly TextBlock value=Ui.Metric("—",30),subtitle=Ui.Text("Waiting for hardware…",11,"Muted"),stats=Ui.Text("",11,"Muted",font:"MetricFont");
    private readonly TextBlock title;
    private readonly TextBlock fan=Ui.Text("",11,Theme.GraphKey("gpufan"),font:"MetricFont");
    private readonly TextBlock pump=Ui.Text("",11,Theme.GraphKey("cpupump"),font:"MetricFont");
    private readonly Run downloadValue=new(),uploadValue=new();
    private readonly HistoryChart chart;
    private readonly bool overview;
    private readonly Border? tint;
    private readonly TextBlock[] overviewStats=[];
    private readonly string? driveId;
    /// <summary>The drive named in the Disk card's subtitle; another drive takes over only after it has been busier for
    /// <see cref="BusiestHold"/> consecutive polls, so bursty I/O across many drives does not flip the name every second.</summary>
    private string? busiestId;
    private int busiestStreak;
    internal const int BusiestHold=5;
    public ChartCard(UiSession session,string kind,bool popButton=true):this(session,kind,popButton,false) {}
    public ChartCard(UiSession session,string kind,bool popButton,bool overview,string? driveId=null)
    {
        this.session=session;this.driveId=kind=="storage"?driveId:null;this.kind=kind;this.overview=overview;SetResourceReference(StyleProperty,"Card");if(!overview)CornerRadius=new(10);Padding=overview?new(16,13,16,11):new(16,12,16,10);Margin=new(6);
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            value.FontSize=24;value.Inlines.Clear();value.Inlines.Add(downloadValue);value.Inlines.Add(uploadValue);
            downloadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("download"));uploadValue.SetResourceReference(TextElement.ForegroundProperty,Theme.GraphKey("upload"));
        }
        // A forced MinHeight larger than the grid's remaining row caused WPF to clip axis labels.
        chart=new(session,kind){ClipToBounds=true,DriveId=this.driveId};
        chart.StatisticsReady+=UpdateStatistics;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});if(!overview)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        // An overview drive card is titled with the drive's model and pops out that drive's own window.
        bool driveCard=overview&&this.driveId is not null;
        title=overview?Ui.Text(driveCard?"Disk":Ui.Title(kind),13):Ui.Heading(Ui.Title(kind),14);title.FontWeight=FontWeights.SemiBold;
        var header=new DockPanel {LastChildFill=true};header.Children.Add(title);
        if(popButton){var pop=driveCard?OverviewVisual.PopOut(session,kind,OpenDrive,"Pop out this drive"):overview?OverviewVisual.PopOut(session,kind):Ui.Button("Pop out ↗",()=>session.OpenDetached(kind));pop.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(pop,Dock.Right);header.Children.Insert(0,pop);}
        // Drive cards share the Disk card's read/write colours, so they carry no picker of their own.
        if(!driveCard){var colors=GraphColorPicker.Create(session,kind);DockPanel.SetDock(colors,Dock.Right);header.Children.Insert(0,colors);}
        if(overview)
        {
            title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;title.ToolTip=Ui.Title(kind);
            var glyph=OverviewVisual.IconTile(kind);glyph.Margin=new(0,0,9,0);DockPanel.SetDock(glyph,Dock.Left);header.Children.Insert(header.Children.Count-1,glyph);
            value.FontSize=28;
            subtitle.TextWrapping=TextWrapping.NoWrap;subtitle.TextTrimming=TextTrimming.CharacterEllipsis;
            fan.TextWrapping=TextWrapping.NoWrap;fan.TextTrimming=TextTrimming.CharacterEllipsis;
            pump.TextWrapping=TextWrapping.NoWrap;pump.TextTrimming=TextTrimming.CharacterEllipsis;
        }
        grid.Children.Add(header);
        var metric=new StackPanel {Margin=new Thickness(0,5,0,5)};
        StackPanel? statsLine=null;
        if(overview)
        {
            // MIN/AVG/MAX share the value's line instead of a footer row, so the star chart row absorbs the height.
            statsLine=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom};
            overviewStats=new TextBlock[3];string[] labels=["MIN","AVG","MAX"];
            for(int i=0;i<3;i++)
            {
                var pair=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(i==0?0:14,0,0,0)};
                var caption=Ui.Caption((kind=="network"?"↓ ":"")+labels[i]);caption.VerticalAlignment=VerticalAlignment.Bottom;caption.Margin=new(0,0,4,1);pair.Children.Add(caption);
                overviewStats[i]=Ui.Text("—",11,"Muted",font:"MetricFont");overviewStats[i].TextWrapping=TextWrapping.NoWrap;overviewStats[i].TextTrimming=TextTrimming.CharacterEllipsis;overviewStats[i].VerticalAlignment=VerticalAlignment.Bottom;
                pair.Children.Add(overviewStats[i]);statsLine.Children.Add(pair);
            }
            if(kind=="network")statsLine.ToolTip="Download statistics over the selected history range";
        }
        if(statsLine is not null&&kind!="network")
        {
            var line=new DockPanel {LastChildFill=false};DockPanel.SetDock(value,Dock.Left);line.Children.Add(value);
            statsLine.Margin=new(14,0,0,5);DockPanel.SetDock(statsLine,Dock.Left);line.Children.Add(statsLine);metric.Children.Add(line);
        }
        else metric.Children.Add(value);
        metric.Children.Add(subtitle);
        fan.Visibility=Visibility.Collapsed;fan.ToolTip="Selected GPU's first reported fan · separate left RPM scale";metric.Children.Add(fan);
        pump.Visibility=Visibility.Collapsed;pump.ToolTip="CPU cooler pump, or the CPU Fan header · separate left RPM scale";metric.Children.Add(pump);Grid.SetRow(metric,1);grid.Children.Add(metric);
        if(overview&&kind=="network")
        {
            metric.Children.Remove(value);
            var rates=new Grid();rates.ColumnDefinitions.Add(new());rates.ColumnDefinitions.Add(new());
            value.Inlines.Clear();
            foreach(var (run,column,label) in new[]{(downloadValue,0,"DOWNLOAD"),(uploadValue,1,"UPLOAD")})
            {
                var columnPanel=new StackPanel {Margin=new Thickness(0,0,column==0?8:0,0)};
                var caption=Ui.Caption(label);caption.Margin=new(0,1,0,0);columnPanel.Children.Add(caption);
                var reading=Ui.Text("",26,font:"MetricFont");reading.TextWrapping=TextWrapping.NoWrap;reading.Inlines.Add(run);
                columnPanel.Children.Add(new Viewbox {Child=reading,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Height=34});
                Grid.SetColumn(columnPanel,column);rates.Children.Add(columnPanel);
            }
            metric.Children.Insert(0,rates);statsLine!.Margin=new(0,2,0,0);metric.Children.Insert(1,statsLine);subtitle.FontSize=10;
        }
        Grid.SetRow(chart,2);grid.Children.Add(chart);
        if(overview)
        {
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
        UpdateReadings();
        chart.Refresh();
    }
    private StorageDrive? Drive=>driveId is null?null:session.Current?.Storage.Drives.FirstOrDefault(d=>d.Id==driveId);
    private void OpenDrive(){if(Drive is { } drive)session.OpenDriveWindow(drive);}
    /// <summary>The card's header text; split from <see cref="Refresh"/> so hidden fixture cards can be checked.</summary>
    private void UpdateReadings()
    {
        if(tint is not null)tint.Visibility=Theme.GlassEnabled?Visibility.Visible:Visibility.Collapsed;
        string id=driveId is not null?StorageMetrics.Key(driveId,"read"):session.MetricId(kind);double? current=session.Stale?null:session.Value(id);var data=session.Current;
        bool showFan=kind=="gputemp"&&session.Settings.ShowGpuFanSpeed;
        fan.Visibility=showFan?Visibility.Visible:Visibility.Collapsed;
        if(showFan)fan.Text=Ui.GpuFanText(session)+$" · {GraphStroke.Pattern(session.Settings,"gpufan",dotted:true)} · left RPM";
        bool showPump=kind=="cputemp"&&session.Settings.ShowCpuPumpSpeed;
        pump.Visibility=showPump?Visibility.Visible:Visibility.Collapsed;
        if(showPump)pump.Text=Ui.CpuPumpText(session)+$" · {GraphStroke.Pattern(session.Settings,"cpupump",dotted:true)} · left RPM";
        value.SetResourceReference(TextBlock.ForegroundProperty,Theme.GraphKey(kind));
        if(kind=="network")
        {
            downloadValue.Text=(overview?"":"↓ ")+Ui.Bytes(current,true)+(overview?"":"    ");uploadValue.Text=(overview?"":"↑ ")+Ui.Bytes(session.Stale?null:session.Value("network/upload"),true);
            string Pattern(string metric,bool dashed)=>CultureInfo.CurrentCulture.TextInfo.ToTitleCase(GraphStroke.Pattern(session.Settings,metric,dashed));
            subtitle.Text=$"{Pattern("download",false)}: download    ·    {Pattern("upload",true)}: upload    ·    Shared scale";
        }
        else if(kind=="storage")
        {
            value.Text=Ui.Bytes(current,true);
            var drives=data?.Storage.Drives??[];
            var only=driveId is null?null:drives.FirstOrDefault(d=>d.Id==driveId);
            if(driveId is not null&&overview)
            {
                // The model is the title, so the subtitle carries the bus, size and write rate instead.
                title.Text=only?.Model??"Disk";title.ToolTip=title.Text;
                string write=$"↑ {Ui.Bytes(session.Stale?null:session.Value(StorageMetrics.Key(driveId,"write")),true)} write";
                subtitle.Text=only is null?"Drive not detected":$"{only.BusType} · {Ui.Bytes(only.SizeBytes)}{(only.Removable?" · removable":"")} · {write}";
            }
            else if(driveId is not null)subtitle.Text=only is null?"Drive not detected":$"{only.Model} · ↑ {Ui.Bytes(session.Stale?null:session.Value(StorageMetrics.Key(driveId,"write")),true)} write";
            else subtitle.Text=data is null?"Discovering drives…":drives.Count==0?"No drives detected"
                :$"{Busiest(drives).Model} \u00b7 {drives.Count} drive{(drives.Count==1?"":"s")}";
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
            if(data is not null&&kind=="cpu"&&session.Settings.ShowCpuCoreUsage&&data.CpuCoreCount>0)subtitle.Text+=$" · {data.CpuCoreCount} cores";
            if(current is null&&data is not null)subtitle.Text+=" · "+(session.Stale?"stale":"sensor unavailable");
        }
        if(overview)subtitle.ToolTip=subtitle.Text;
    }
    /// <summary>The drive to name: the current one while it is present, until a different drive has led for <see cref="BusiestHold"/> polls in a row.</summary>
    private StorageDrive Busiest(IReadOnlyList<StorageDrive> drives)
    {
        double Rate(StorageDrive d)=>(session.Value(StorageMetrics.Key(d.Id,"read"))??0)+(session.Value(StorageMetrics.Key(d.Id,"write"))??0);
        var leader=drives.OrderByDescending(Rate).ThenBy(d=>d.Index).First();
        var current=busiestId is null?null:drives.FirstOrDefault(d=>d.Id==busiestId);
        if(current is null||leader.Id==current.Id){busiestId=leader.Id;busiestStreak=0;return current??leader;}
        // A tie (both idle) keeps the current drive; a strictly busier drive has to hold the lead.
        if(Rate(leader)<=Rate(current)){busiestStreak=0;return current;}
        if(++busiestStreak<BusiestHold)return current;
        busiestId=leader.Id;busiestStreak=0;return leader;
    }
    private void UpdateStatistics(Summary summary)
    {
        Func<double?,string> format=kind is "network" or "storage"?v=>Ui.Bytes(v,true):v=>Ui.Number(v,kind.Contains("temp")?"°C":"%");
        stats.Text=$"MIN  {format(summary.Minimum)}      AVG  {format(summary.Average)}      MAX  {format(summary.Maximum)}"+(kind=="network"?"    · Download":"");
        if(overview)
        {
            double?[] values=[summary.Minimum,summary.Average,summary.Maximum];
            for(int i=0;i<overviewStats.Length;i++){overviewStats[i].Text=format(values[i]);overviewStats[i].ToolTip=overviewStats[i].Text;}
        }
    }
}
