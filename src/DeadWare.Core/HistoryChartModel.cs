namespace DeadWare.Core;

/// <summary>The plot area of a chart inside its control, in device-independent pixels.</summary>
public readonly record struct ChartPlot(double Left,double Top,double Width,double Height);

/// <summary>What a live history chart shows: one of cpu, cputemp, gpu, gputemp, memory, network, storage.</summary>
/// <param name="GpuId">The selected graphics card's id, or null when none is selected.</param>
/// <param name="CompareDevices">Adds the GPU to a cpu or cputemp chart; ignored for other kinds.</param>
/// <param name="Now">The collector's monotonic clock, in seconds, at the right-hand edge.</param>
/// <param name="Options">The optional extra lines; null draws only the main traces.</param>
public sealed record HistoryChartRequest(SampleHistory History,string Kind,string? GpuId,bool CompareDevices,int Seconds,double Now,DateTimeOffset WallNow,ChartPlot Plot,TimeZoneInfo? Zone=null,bool Compact=false,HistoryChartOptions? Options=null);

/// <summary>The optional lines of a history chart. The settings switch them on; the chart's kind decides which apply.</summary>
/// <param name="GpuFan">Show GPU fan speed: on a gputemp chart, or a cputemp chart that compares devices.</param>
/// <param name="CpuPump">Show CPU pump speed: on a cputemp chart that does not compare devices, or does with <paramref name="PumpWithDevices"/>.</param>
/// <param name="Cores">How many per-core lines a cpu chart carries; 0 for none. They replace the aggregate line.</param>
/// <param name="Drives">Drive ids whose read rate a storage chart traces, in display order.</param>
/// <param name="DriveNames">Drive id to model name, for the hover readout; an unlisted drive shows its id.</param>
/// <param name="PumpWithDevices">Keeps the pump line on a device comparison (the widget).</param>
/// <param name="CoresWithDevices">Keeps the per-core lines on a device comparison (the widget).</param>
public sealed record HistoryChartOptions(bool GpuFan=false,bool CpuPump=false,int Cores=0,IReadOnlyList<string>? Drives=null,IReadOnlyDictionary<string,string>? DriveNames=null,bool PumpWithDevices=false,bool CoresWithDevices=false);

/// <summary>What a trace is, which decides its stroke and where it sits: cores and drives underneath, then the local share, then the rest.</summary>
public enum ChartTraceRole {Main,Core,Drive,Local,Rpm}

/// <summary>A horizontal value line and its axis label.</summary>
public sealed record ChartLine(double Y,string Label);
/// <summary>A time label under the plot.</summary>
public sealed record ChartTick(double X,string Label);
/// <summary>One line on the chart. A missing reading ends a run; the next reading starts another.</summary>
/// <param name="ColorKey">The graph colour this trace uses, such as cpu, download or diskwrite.</param>
/// <param name="Fill">How strongly the area under the trace is filled, 0 to 1.</param>
/// <param name="Samples">The drawn samples in time order, gaps included as null values; the hover readout reads them.</param>
public sealed record ChartTrace(string Metric,string ColorKey,bool Dashed,double Fill,IReadOnlyList<IReadOnlyList<ChartPoint>> Runs,IReadOnlyList<SamplePoint> Samples)
{
    public ChartTraceRole Role {get;init;}
    /// <summary>Laid out and read by hover but not drawn: the aggregate CPU line once per-core lines replace it.</summary>
    public bool Hidden {get;init;}
    /// <summary>Fan and pump lines are dotted and read against the RPM axis.</summary>
    public bool Dotted {get;init;}
    /// <summary>The core number or the drive's position, zero-based, for telling the lines apart by tint; -1 otherwise.</summary>
    public int Index {get;init;}=-1;
    /// <summary>The value at the top of the plot for this trace: the chart's maximum, or the RPM maximum.</summary>
    public double Maximum {get;init;}
    /// <summary>A drive's id, for a <see cref="ChartTraceRole.Drive"/> trace.</summary>
    public string? DriveId {get;init;}
    /// <summary>A drive's write rate, kept for the hover readout only; it is never drawn.</summary>
    public IReadOnlyList<SamplePoint> WriteSamples {get;init;}=[];
}
/// <summary>Everything a renderer needs to draw one frame of a history chart, already laid out in plot coordinates.</summary>
/// <param name="Request">What the frame was built from; the hover readout needs its range, clock and plot.</param>
public sealed record HistoryChartFrame(double Maximum,IReadOnlyList<ChartLine> Lines,IReadOnlyList<ChartTick> Ticks,IReadOnlyList<ChartTrace> Traces,Summary Statistics,bool HasReadings,HistoryChartRequest Request)
{
    /// <summary>The top of the left-hand RPM axis that fan and pump lines share; 0 when neither is shown.</summary>
    public double RpmMaximum {get;init;}
    /// <summary>The RPM labels for the same three value lines; empty when no fan or pump line is shown.</summary>
    public IReadOnlyList<ChartLine> RpmLines {get;init;}=[];
}

/// <summary>One trace's value at the hovered sample, and where its dot goes.</summary>
/// <param name="ColorKey">The trace's graph colour, or "muted" for a summary line.</param>
/// <param name="Dot">Where the trace's dot goes; null for lines that have none (cores, drives, the local share, a hidden trace).</param>
/// <param name="Index">The core number or drive position behind the line, for its tint; -1 otherwise.</param>
public sealed record ChartHoverLine(string ColorKey,string Text,ChartPoint? Dot,int Index=-1);
/// <summary>The hover readout: a vertical line at <paramref name="X"/>, the sample's local time, and a line per trace that has a reading there.</summary>
public sealed record ChartHover(double X,string Time,IReadOnlyList<ChartHoverLine> Lines);

/// <summary>
/// Lays out a live history chart with no user-interface framework, so any front end can draw it and the rules can be
/// tested directly. The axis, colour, fill and readout rules are those of the WPF HistoryChart. Not yet covered:
/// smoothed ranges, splines and the single-drive storage chart.
/// </summary>
public static class HistoryChartModel
{
    private static readonly string[] Kinds=["cpu","cputemp","gpu","gputemp","memory","network","storage"];

    public static HistoryChartFrame Build(HistoryChartRequest request)
    {
        string kind=request.Kind;
        if(!Kinds.Contains(kind))throw new ArgumentException($"Unknown chart kind '{kind}'.",nameof(request));
        var plot=request.Plot;
        if(!(plot.Width>0)||!(plot.Height>0)||!double.IsFinite(plot.Width)||!double.IsFinite(plot.Height)||request.Seconds<=0||!double.IsFinite(request.Now))
            return new(0,[],[],[],new(null,null,null),false,request);

        bool compare=request.CompareDevices&&kind is "cpu" or "cputemp";
        bool bytes=kind is "network" or "storage";
        string primary=MetricId(kind,request.GpuId);
        (string Metric,string ColorKey,double Fill)? second=kind switch
        {
            "network"=>("network/upload","upload",.42),
            "storage"=>("storage/total/write","diskwrite",.42),
            "cpu" when compare=>(MetricId("gpu",request.GpuId),"gpu",.45),
            "cputemp" when compare=>(MetricId("gputemp",request.GpuId),"gputemp",.45),
            _=>null,
        };

        int columns=(int)Math.Ceiling(plot.Width);
        var primaryRaw=request.History.Series(primary,request.Now,request.Seconds);
        var secondRaw=second is { } other?request.History.Series(other.Metric,request.Now,request.Seconds):[];
        double peak=Math.Max(primaryRaw.Max(p=>p.Value)??0,secondRaw.Max(p=>p.Value)??0);
        double maximum=kind is "cpu" or "gpu" or "memory"?100:bytes?Math.Max(1000,peak*1.15):Math.Max(100,peak*1.1);
        if(compare&&kind=="cputemp")maximum=Math.Ceiling(maximum/20)*20;

        string Axis(double value)=>AxisLabel(kind,compare,value);
        var lines=new ChartLine[3];
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2;
            lines[i]=new(plot.Top+plot.Height*(1-value/maximum),Axis(value));
        }

        IReadOnlyList<SamplePoint> Read(string metric)=>request.History.Series(metric,request.Now,request.Seconds);
        ChartTrace Trace(string metric,string colorKey,bool dashed,double fill,IReadOnlyList<SamplePoint> raw,double top)
        {
            var runs=new List<IReadOnlyList<ChartPoint>>();List<ChartPoint>? run=null;
            var drawn=SampleHistory.ReduceForChart(raw,request.Now,request.Seconds,columns);
            foreach(var point in drawn)
            {
                if(point.Value is not { } value||!double.IsFinite(value)){run=null;continue;}
                if(run is null)runs.Add(run=[]);
                run.Add(new(plot.Left+plot.Width*(1-(request.Now-point.MonotonicSeconds)/request.Seconds),plot.Top+plot.Height*(1-Math.Clamp(value/top,0,1))));
            }
            return new(metric,colorKey,dashed,fill,runs,drawn){Maximum=top};
        }

        var options=request.Options??new HistoryChartOptions();
        int cores=kind=="cpu"&&(!request.CompareDevices||options.CoresWithDevices)?Math.Clamp(options.Cores,0,GraphColorSettings.MaxCores):0;
        bool showFan=options.GpuFan&&(kind=="gputemp"||kind=="cputemp"&&compare);
        bool showPump=options.CpuPump&&kind=="cputemp"&&(!compare||options.PumpWithDevices);

        // With per-core lines the aggregate CPU line is hidden (the cores replace it) but still laid out for the readout.
        var traces=new List<ChartTrace> {Trace(primary,ColorKey(kind),false,bytes||compare?.65:1,primaryRaw,maximum) with {Hidden=cores>0}};
        if(second is { } added)traces.Add(Trace(added.Metric,added.ColorKey,true,added.Fill,secondRaw,maximum));
        if(kind=="network")
        {
            // The local share never exceeds its total, so it rides the totals' axis without driving it.
            traces.Add(Trace(LocalDownload,"download",false,.3,Read(LocalDownload),maximum) with {Role=ChartTraceRole.Local});
            traces.Add(Trace(LocalUpload,"upload",true,.2,Read(LocalUpload),maximum) with {Role=ChartTraceRole.Local});
        }
        double rpmMaximum=0;ChartLine[] rpmLines=[];
        if(showFan||showPump)
        {
            // GPU fan and CPU pump share the left RPM scale when both are shown.
            var fanRaw=showFan?Read(MetricId("gpufan",request.GpuId)):[];
            var pumpRaw=showPump?Read(MetricId("cpupump",request.GpuId)):[];
            rpmMaximum=Math.Max(1000,Math.Ceiling(Math.Max(fanRaw.Max(p=>p.Value)??0,pumpRaw.Max(p=>p.Value)??0)/1000)*1000);
            rpmLines=lines.Select((line,i)=>new ChartLine(line.Y,FormattableString.Invariant($"{rpmMaximum*i/2:0} RPM"))).ToArray();
            if(showFan)traces.Add(Trace(MetricId("gpufan",request.GpuId),"gpufan",false,0,fanRaw,rpmMaximum) with {Role=ChartTraceRole.Rpm,Dotted=true});
            if(showPump)traces.Add(Trace(MetricId("cpupump",request.GpuId),"cpupump",false,0,pumpRaw,rpmMaximum) with {Role=ChartTraceRole.Rpm,Dotted=true});
        }
        for(int i=0;i<cores;i++)traces.Add(Trace(CoreMetric(i),GraphColorSettings.CoreKey(i),false,0,Read(CoreMetric(i)),maximum) with {Role=ChartTraceRole.Core,Index=i});
        if(kind=="storage"&&options.Drives is { } drives)
            for(int i=0;i<drives.Count;i++)
                traces.Add(Trace(StorageMetrics.Key(drives[i],"read"),"diskread",false,0,Read(StorageMetrics.Key(drives[i],"read")),maximum)
                    with {Role=ChartTraceRole.Drive,Index=i,DriveId=drives[i],WriteSamples=SampleHistory.ReduceForChart(Read(StorageMetrics.Key(drives[i],"write")),request.Now,request.Seconds,columns)});

        var ticks=TimeTicks.For(request.Seconds,request.Now,request.WallNow,plot.Width,request.Compact,request.Zone)
            .Select(t=>new ChartTick(plot.Left+plot.Width*(1-(request.Now-t.MonotonicSeconds)/request.Seconds),t.Label)).ToArray();
        // Cores, drives and the local share never decide whether the chart has readings: their totals do.
        bool readings=traces.Any(t=>t.Role is ChartTraceRole.Main or ChartTraceRole.Rpm&&t.Runs.Count>0);
        return new(maximum,lines,ticks,traces,SampleHistory.Statistics(primaryRaw),readings,request){RpmMaximum=rpmMaximum,RpmLines=rpmLines};
    }

    /// <summary>
    /// The readout for a pointer at (<paramref name="x"/>, <paramref name="y"/>) in the chart's coordinates: the sample of
    /// the main trace nearest in time, or null outside the plot, over a gap, or farther from any sample than one and a
    /// half polling intervals (or one plot column, if that is longer).
    /// </summary>
    public static ChartHover? Hover(HistoryChartFrame frame,double x,double y,double pollingSeconds)
    {
        var request=frame.Request;var plot=request.Plot;
        if(frame.Traces.Count==0||x<plot.Left||x>plot.Left+plot.Width||y<plot.Top||y>plot.Top+plot.Height)return null;
        var primary=frame.Traces[0];
        double seconds=request.Now-(plot.Left+plot.Width-x)/plot.Width*request.Seconds;
        double tolerance=Math.Max(1.5*Math.Max(1,pollingSeconds),request.Seconds/Math.Max(1,Math.Ceiling(plot.Width)));
        int index=NearestSample.Find(primary.Samples,seconds,tolerance);
        if(index<0)return null;
        var sample=primary.Samples[index];
        double at=plot.Left+plot.Width*(1-(request.Now-sample.MonotonicSeconds)/request.Seconds);
        bool compare=request.CompareDevices&&request.Kind is "cpu" or "cputemp";
        var lines=new List<ChartHoverLine>();
        var coreValues=new List<(int Core,double Value,string ColorKey)>();
        double? localDown=null,localUp=null;
        double? At(IReadOnlyList<SamplePoint> samples)
        {
            int found=NearestSample.Find(samples,sample.MonotonicSeconds,tolerance);
            return found<0?null:samples[found].Value;
        }
        foreach(var trace in frame.Traces)
        {
            if(trace.Role==ChartTraceRole.Drive)continue;
            double? read=ReferenceEquals(trace,primary)?sample.Value:At(trace.Samples);
            if(read is not { } value)continue;
            // Cores join the readout after the loop, by value; the two local shares make one line. Neither has a dot.
            if(trace.Role==ChartTraceRole.Core){coreValues.Add((trace.Index,value,trace.ColorKey));continue;}
            if(trace.Role==ChartTraceRole.Local){if(trace.Metric==LocalDownload)localDown=value;else localUp=value;continue;}
            ChartPoint? dot=trace.Hidden?null:new ChartPoint(at,plot.Top+plot.Height*(1-Math.Clamp(value/trace.Maximum,0,1)));
            lines.Add(new(trace.ColorKey,trace.Dotted?FormattableString.Invariant($"{value:0} RPM"):AxisLabel(request.Kind,compare,value),dot));
        }
        if(localDown is not null||localUp is not null)lines.Add(new("muted",$"Local ↓ {Bytes(localDown,true)} ↑ {Bytes(localUp,true)}",null));
        // The eight busiest cores, then a count of the rest.
        const int shown=8;
        foreach(var core in coreValues.OrderByDescending(c=>c.Value).ThenBy(c=>c.Core).Take(shown))
            lines.Add(new(core.ColorKey,FormattableString.Invariant($"Core {core.Core+1} · {core.Value:0}%"),null,core.Core));
        if(coreValues.Count>shown)lines.Add(new("muted",$"+{coreValues.Count-shown} more",null));
        // Drives follow the totals, busiest first; a drive with neither rate at this sample is left out.
        var names=request.Options?.DriveNames;
        var driveLines=new List<(ChartHoverLine Line,double Rate)>();
        foreach(var trace in frame.Traces.Where(t=>t.Role==ChartTraceRole.Drive))
        {
            double? read=At(trace.Samples),write=At(trace.WriteSamples);
            if(read is null&&write is null)continue;
            string name=names is not null&&names.TryGetValue(trace.DriveId!,out string? model)?model:trace.DriveId!;
            driveLines.Add((new(trace.ColorKey,$"{name} · {Bytes(read,true)} ↓ {Bytes(write,true)} ↑",null,trace.Index),(read??0)+(write??0)));
        }
        lines.AddRange(driveLines.OrderByDescending(d=>d.Rate).Select(d=>d.Line));
        var wall=request.WallNow.AddSeconds(sample.MonotonicSeconds-request.Now);
        string time=(request.Zone is null?wall:TimeZoneInfo.ConvertTime(wall,request.Zone)).ToString("HH:mm:ss",System.Globalization.CultureInfo.InvariantCulture);
        return new(at,time,lines);
    }

    /// <summary>A value as the chart's axis and readout write it: a byte rate, a percentage, or degrees (°C when devices are compared).</summary>
    public static string AxisLabel(string kind,bool compare,double value)=>kind is "network" or "storage"?Bytes(value,true)
        :FormattableString.Invariant($"{value:0}{(kind.Contains("temp")?(compare?"°C":"°"):"%")}");

    /// <summary>The local-network share of the network totals (per-app capture); drawn muted under the totals.</summary>
    private const string LocalDownload="network/download/local",LocalUpload="network/upload/local";

    /// <summary>The sample id of one CPU core's usage.</summary>
    public static string CoreMetric(int index)=>$"cpu/core/{index}/usage";

    /// <summary>The sample id a chart kind reads; a GPU kind with no card selected reads an id that never has samples.</summary>
    public static string MetricId(string kind,string? gpuId)=>kind switch
    {
        "cpu"=>"cpu/usage","cputemp"=>"cpu/temperature","cpupump"=>"cpu/pump",
        "gpu"=>(gpuId??"unavailable")+"/usage","gputemp"=>(gpuId??"unavailable")+"/temperature","gpufan"=>(gpuId??"unavailable")+"/fan",
        "memory"=>"memory/usage","storage"=>"storage/total/read",_=>"network/download",
    };

    /// <summary>The graph colour a kind's main trace uses.</summary>
    public static string ColorKey(string kind)=>kind switch {"network"=>"download","storage"=>"diskread",_=>kind};

    /// <summary>A byte count in decimal units, as the dashboard writes it: 1.5 MB, or 1.5 MB/s for a rate.</summary>
    public static string Bytes(double? bytes,bool rate=false)
    {
        if(!bytes.HasValue||!double.IsFinite(bytes.Value))return "—";
        double value=bytes.Value;string[] units=["B","KB","MB","GB","TB"];int i=0;
        while(value>=1000&&i<units.Length-1){value/=1000;i++;}
        return FormattableString.Invariant($"{value:0.0} {units[i]}{(rate?"/s":"")}");
    }
}
