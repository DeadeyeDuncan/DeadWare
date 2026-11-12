namespace DeadWare.Core;

/// <summary>The plot area of a chart inside its control, in device-independent pixels.</summary>
public readonly record struct ChartPlot(double Left,double Top,double Width,double Height);

/// <summary>What a live history chart shows: one of cpu, cputemp, gpu, gputemp, memory, network, storage.</summary>
/// <param name="GpuId">The selected graphics card's id, or null when none is selected.</param>
/// <param name="CompareDevices">Adds the GPU to a cpu or cputemp chart; ignored for other kinds.</param>
/// <param name="Now">The collector's monotonic clock, in seconds, at the right-hand edge.</param>
public sealed record HistoryChartRequest(SampleHistory History,string Kind,string? GpuId,bool CompareDevices,int Seconds,double Now,DateTimeOffset WallNow,ChartPlot Plot,TimeZoneInfo? Zone=null,bool Compact=false);

/// <summary>A horizontal value line and its axis label.</summary>
public sealed record ChartLine(double Y,string Label);
/// <summary>A time label under the plot.</summary>
public sealed record ChartTick(double X,string Label);
/// <summary>One line on the chart. A missing reading ends a run; the next reading starts another.</summary>
/// <param name="ColorKey">The graph colour this trace uses, such as cpu, download or diskwrite.</param>
/// <param name="Fill">How strongly the area under the trace is filled, 0 to 1.</param>
public sealed record ChartTrace(string Metric,string ColorKey,bool Dashed,double Fill,IReadOnlyList<IReadOnlyList<ChartPoint>> Runs);
/// <summary>Everything a renderer needs to draw one frame of a history chart, already laid out in plot coordinates.</summary>
public sealed record HistoryChartFrame(double Maximum,IReadOnlyList<ChartLine> Lines,IReadOnlyList<ChartTick> Ticks,IReadOnlyList<ChartTrace> Traces,Summary Statistics,bool HasReadings);

/// <summary>
/// Lays out a live history chart with no user-interface framework, so any front end can draw it and the rules can be
/// tested directly. The axis, colour and fill rules are those of the WPF HistoryChart. Not yet covered: per-core and
/// per-drive traces, the local-network share, fan and pump lines, smoothed ranges, splines and hover.
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
            return new(0,[],[],[],new(null,null,null),false);

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

        string Axis(double value)=>bytes?Bytes(value,true):$"{value:0}{(kind.Contains("temp")?(compare?"°C":"°"):"%")}";
        var lines=new ChartLine[3];
        for(int i=0;i<3;i++)
        {
            double value=maximum*i/2;
            lines[i]=new(plot.Top+plot.Height*(1-value/maximum),Axis(value));
        }

        IReadOnlyList<IReadOnlyList<ChartPoint>> Runs(IReadOnlyList<SamplePoint> raw)
        {
            var runs=new List<IReadOnlyList<ChartPoint>>();List<ChartPoint>? run=null;
            foreach(var point in SampleHistory.ReduceForChart(raw,request.Now,request.Seconds,columns))
            {
                if(point.Value is not { } value||!double.IsFinite(value)){run=null;continue;}
                if(run is null)runs.Add(run=[]);
                run.Add(new(plot.Left+plot.Width*(1-(request.Now-point.MonotonicSeconds)/request.Seconds),plot.Top+plot.Height*(1-Math.Clamp(value/maximum,0,1))));
            }
            return runs;
        }

        var traces=new List<ChartTrace> {new(primary,ColorKey(kind),false,bytes||compare?.65:1,Runs(primaryRaw))};
        if(second is { } added)traces.Add(new(added.Metric,added.ColorKey,true,added.Fill,Runs(secondRaw)));

        var ticks=TimeTicks.For(request.Seconds,request.Now,request.WallNow,plot.Width,request.Compact,request.Zone)
            .Select(t=>new ChartTick(plot.Left+plot.Width*(1-(request.Now-t.MonotonicSeconds)/request.Seconds),t.Label)).ToArray();
        return new(maximum,lines,ticks,traces,SampleHistory.Statistics(primaryRaw),traces.Any(t=>t.Runs.Count>0));
    }

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
