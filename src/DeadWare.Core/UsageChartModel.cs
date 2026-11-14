using System.Globalization;
namespace DeadWare.Core;

/// <summary>One series on a usage chart: aligned buckets drawn as an average line over a min/max band.</summary>
public sealed record UsageChartSeries(UsageHistoryView View,string ColorKey,bool Dashed,string Label);
/// <summary>A bucket's min/max band in view coordinates.</summary>
public readonly record struct UsageChartBand(double X,double Y,double Width,double Height);
public readonly record struct UsageChartPoint(double X,double Y);
/// <summary>One drawn series: its bands, and its average line as runs of adjacent full buckets (a run of one is a dot).</summary>
public sealed record UsageChartTrace(string ColorKey,bool Dashed,IReadOnlyList<UsageChartBand> Bands,IReadOnlyList<IReadOnlyList<UsageChartPoint>> Runs);
public readonly record struct UsageChartLine(double Y,string Label);
/// <summary>A hover or selection target: the middle of one of the first series' buckets and half its width.</summary>
public readonly record struct UsageChartTarget(double X,double Tolerance);
/// <summary>Everything a view draws for one usage chart.</summary>
public sealed record UsageChartFrame(ChartPlot Plot,double Maximum,IReadOnlyList<UsageChartLine> Lines,IReadOnlyList<UsageChartTrace> Traces,
    IReadOnlyList<UsageChartTarget> Targets,string FromLabel,string ToLabel,bool Any);
public readonly record struct UsageChartHoverValue(string Text,string ColorKey,double Y);
/// <summary>The readout for the bucket under the pointer: its start time and each series' average there.</summary>
public sealed record UsageChartHover(int Index,double X,string Time,IReadOnlyList<UsageChartHoverValue> Values);

/// <summary>Average line plus min/max band per bucket. Missing slots and partial buckets never get a line drawn across them.</summary>
public static class UsageChartModel
{
    /// <summary>A bucket counts as full when Minutes ≥ max(1, n − 0.5), with n the whole minutes that start in its slot; only adjacent full buckets connect.</summary>
    public static bool IsFull(UsageHistoryBucket bucket)=>bucket.Minutes>=Math.Max(1,TrafficStrips.MinuteStarts(bucket.Start,bucket.End)-0.5);
    public static bool CanConnect(UsageHistoryBucket a,UsageHistoryBucket b)=>IsFull(a)&&IsFull(b)&&b.Start==a.End;

    /// <summary>A value in the chart's unit. <paramref name="tenths"/> keeps one decimal for % values under 10.</summary>
    public static string Format(double value,string unit,bool tenths=false)=>unit switch
    {
        "%"=>value.ToString(tenths&&Math.Abs(value)<10?"0.#":"0",CultureInfo.CurrentCulture)+"%",
        "°C"=>string.Create(CultureInfo.CurrentCulture,$"{value:0}°C"),
        "B/s"=>HistoryChartModel.Bytes(value,true),
        "B"=>HistoryChartModel.Bytes(value),
        _=>string.Create(CultureInfo.CurrentCulture,$"{value:0.#} {unit}"),
    };

    private static string DateLabel(DateTimeOffset time,DateTimeOffset from,DateTimeOffset to,TimeZoneInfo zone)=>
        TimeZoneInfo.ConvertTime(time,zone).ToString((to-from).TotalDays<=2?"MMM d HH:mm":"MMM d yyyy",CultureInfo.CurrentCulture);

    /// <summary>Lays the series out in <paramref name="plot"/>. The first series sets the period and the hover targets.
    /// A null <paramref name="fixedMaximum"/> scales to the highest bucket maximum plus headroom (at least 40 for °C).</summary>
    public static UsageChartFrame Build(IReadOnlyList<UsageChartSeries> series,string unit,double? fixedMaximum,ChartPlot plot,bool tenths=false,TimeZoneInfo? zone=null)
    {
        zone??=TimeZoneInfo.Local;
        if(series.Count==0||plot.Width<=0||plot.Height<=0)return new(plot,fixedMaximum??1,[],[],[],"","",false);
        var first=series[0];DateTimeOffset from=first.View.From,to=first.View.To;
        double seconds=Math.Max(60,(to-from).TotalSeconds);
        double maximum=fixedMaximum??Math.Max(1,series.SelectMany(s=>s.View.Buckets).Select(b=>b.Maximum).DefaultIfEmpty(0).Max()*1.15);
        if(fixedMaximum is null&&unit=="°C")maximum=Math.Max(maximum,40);
        double X(DateTimeOffset time)=>plot.Left+plot.Width*Math.Clamp((time-from).TotalSeconds/seconds,0,1);
        double Y(double value)=>plot.Top+plot.Height*(1-Math.Clamp(value/maximum,0,1));
        var lines=Enumerable.Range(0,5).Select(i=>new UsageChartLine(Y(maximum*i/4),Format(maximum*i/4,unit,tenths))).ToArray();
        var targets=new List<UsageChartTarget>();var traces=new List<UsageChartTrace>();bool any=false;
        foreach(var item in series)
        {
            var bands=new List<UsageChartBand>();var runs=new List<IReadOnlyList<UsageChartPoint>>();List<UsageChartPoint>? run=null;
            UsageHistoryBucket? previous=null;
            foreach(var bucket in item.View.Buckets)
            {
                any=true;
                double x0=X(bucket.Start),x1=Math.Max(x0+1,X(bucket.End)),mid=(x0+x1)/2;
                // Only the bucket's own width counts, so a missing neighbour never borrows a value.
                if(ReferenceEquals(item,first))targets.Add(new(mid,Math.Max(1,(x1-x0)/2)));
                bands.Add(new(x0,Y(bucket.Maximum),x1-x0,Math.Max(1,Y(bucket.Minimum)-Y(bucket.Maximum))));
                if(run is null||previous is null||!CanConnect(previous,bucket)){run=[];runs.Add(run);}
                run.Add(new(mid,Y(bucket.Average)));
                previous=bucket;
            }
            traces.Add(new(item.ColorKey,item.Dashed,bands,runs));
        }
        return new(plot,maximum,lines,traces,targets,DateLabel(from,from,to,zone),DateLabel(to,from,to,zone),any);
    }

    /// <summary>Index of the target nearest <paramref name="x"/> within its own tolerance, for a point inside the plot; otherwise -1.</summary>
    public static int BucketAt(UsageChartFrame frame,double x,double y)
    {
        var plot=frame.Plot;
        if(x<plot.Left||x>plot.Left+plot.Width||y<plot.Top||y>plot.Top+plot.Height)return -1;
        int best=-1;double bestDistance=double.PositiveInfinity;
        for(int i=0;i<frame.Targets.Count;i++)
        {
            double distance=Math.Abs(frame.Targets[i].X-x);
            if(distance<=frame.Targets[i].Tolerance&&distance<bestDistance){best=i;bestDistance=distance;}
        }
        return best;
    }

    /// <summary>The readout for bucket <paramref name="index"/> of the first series, with every series' bucket that starts at the same time.</summary>
    public static UsageChartHover? Hover(UsageChartFrame frame,IReadOnlyList<UsageChartSeries> series,int index,string unit,bool tenths=false,TimeZoneInfo? zone=null)
    {
        zone??=TimeZoneInfo.Local;
        if(series.Count==0||index<0||index>=series[0].View.Buckets.Count||index>=frame.Targets.Count)return null;
        var view=series[0].View;var bucket=view.Buckets[index];
        var values=new List<UsageChartHoverValue>();
        foreach(var item in series)
        {
            var match=item.View.Buckets.FirstOrDefault(b=>b.Start==bucket.Start);
            if(match is null)continue;
            double y=frame.Plot.Top+frame.Plot.Height*(1-Math.Clamp(match.Average/frame.Maximum,0,1));
            values.Add(new(Format(match.Average,unit,tenths),item.ColorKey,y));
        }
        return new(index,frame.Targets[index].X,DateLabel(bucket.Start,view.From,view.To,zone),values);
    }

    /// <summary>Where Left (−1) or Right (+1) moves the selection among <paramref name="count"/> buckets; with none selected Left starts at the last and Right at the first.</summary>
    public static int? Step(int? selected,int count,int direction)
    {
        if(count<=0)return null;
        if(direction<0)return selected is { } l?Math.Max(0,Math.Min(l,count)-1):count-1;
        return selected is { } r?Math.Min(count-1,r+1):0;
    }

    /// <summary>The index of the bucket holding <paramref name="time"/>, or null.</summary>
    public static int? BucketHolding(IReadOnlyList<UsageHistoryBucket> buckets,DateTimeOffset time)
    {
        for(int i=0;i<buckets.Count;i++)if(buckets[i].Start<=time&&time<buckets[i].End)return i;
        return null;
    }
}

/// <summary>Text the Usage history page shows, kept apart from any UI framework.</summary>
public static class UsagePresenter
{
    private static string Number(double? value,string unit)=>
        value.HasValue&&double.IsFinite(value.Value)?string.Create(CultureInfo.CurrentCulture,$"{value.Value:0.0}{unit}"):"—";

    /// <summary>The MIN / AVG / MAX line under a Device chart, from the stored minute values of the whole period.</summary>
    public static string Statistics(UsageHistoryView view,string unit)
    {
        if(view.Minutes==0||view.Statistics.Average is null)return "No readings in this period.";
        string Format(double? value)=>unit=="B/s"?HistoryChartModel.Bytes(value,true):Number(value,unit=="%"?"%":" "+unit);
        return string.Create(CultureInfo.CurrentCulture,$"MIN {Format(view.Statistics.Minimum)}    AVG {Format(view.Statistics.Average)}    MAX {Format(view.Statistics.Maximum)}    ·    {view.Minutes:N0} minutes");
    }

    /// <summary>Like "18 min 0 s": seconds under a minute, minutes and seconds under an hour, then hours and minutes.</summary>
    public static string Duration(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0)return "—";
        if(seconds<59.95)return seconds.ToString("0.#",CultureInfo.CurrentCulture)+" s";
        var time=TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours>=1?string.Create(CultureInfo.CurrentCulture,$"{(long)time.TotalHours:N0} h {time.Minutes} min"):$"{time.Minutes} min {time.Seconds} s";
    }

    /// <summary>Whole minutes that start in [from, to) and have ended by <paramref name="now"/>: every minute the store could hold for the period.</summary>
    public static long ElapsedMinutes(DateTimeOffset from,DateTimeOffset to,DateTimeOffset now)
    {
        const long Minute=TimeSpan.TicksPerMinute;
        static long Ceiling(long ticks)=>(ticks+Minute-1)/Minute*Minute;
        long end=Math.Min(Ceiling(to.UtcTicks),now.UtcTicks/Minute*Minute);
        return Math.Max(0,(end-Ceiling(from.UtcTicks))/Minute);
    }

    /// <summary>Like "03:13–03:16" in local time; <paramref name="withDate"/> adds the date, on the end too when the span crosses midnight.</summary>
    public static string SlotSpan(DateTimeOffset first,DateTimeOffset lastEnd,bool withDate,TimeZoneInfo? zone=null)
    {
        zone??=TimeZoneInfo.Local;
        DateTime start=TimeZoneInfo.ConvertTime(first,zone).DateTime,end=TimeZoneInfo.ConvertTime(lastEnd,zone).DateTime;
        static string Format(DateTime time,bool date)=>time.ToString(date?"MMM d HH:mm":"HH:mm",CultureInfo.CurrentCulture);
        return $"{Format(start,withDate)}–{Format(end,withDate&&end.Date!=start.Date)}";
    }

    /// <summary>Like "Busiest apps · 03:13–03:16".</summary>
    public static string SlotHeading(DateTimeOffset first,DateTimeOffset lastEnd,bool withDate,TimeZoneInfo? zone=null)=>"Busiest apps · "+SlotSpan(first,lastEnd,withDate,zone);

    private static string Percent(double value)=>value.ToString("0.#",CultureInfo.CurrentCulture)+" %";

    /// <summary>An Application resources row's cells: name, CPU time, Avg CPU, Avg memory, Avg GPU, Avg GPU memory, I/O read, I/O written, minutes listed.</summary>
    public static IReadOnlyList<string> ResourceCells(ProcessResourceRow row)=>
    [
        row.Name,Duration(row.CpuSeconds),Percent(row.AverageCpuPercent),HistoryChartModel.Bytes(row.AverageMemoryBytes),
        row.AverageGpuPercent is { } gpu?Percent(gpu):"—",row.AverageGpuMemoryBytes is { } bytes?HistoryChartModel.Bytes(bytes):"—",
        HistoryChartModel.Bytes(row.IoRead),HistoryChartModel.Bytes(row.IoWritten),row.MinutesListed.ToString("N0",CultureInfo.CurrentCulture),
    ];

    /// <summary>The reason a row's GPU cells show a dash, or Everything else's note on a shown Avg GPU; null otherwise.</summary>
    public static string? ResourceTip(ProcessResourceRow row,double gpuCovered)
    {
        string missing=gpuCovered>0?"No GPU data in the minutes this app was listed":"No GPU data in this range";
        return row.AverageGpuPercent is null||row.AverageGpuMemoryBytes is null?missing:row.EverythingElse?"Avg GPU is a sum over many apps; it can exceed 100 %":null;
    }

    /// <summary>The Applications table's rows for a view filtered by <paramref name="query"/>: name, download, upload, total, share. The Other row's key is "\0".</summary>
    public static IReadOnlyList<TableRow> ApplicationRows(UsageApplicationView view,string query)
    {
        long total=view.Applications.Sum(a=>a.Received+a.Sent)+view.OtherReceived+view.OtherSent;
        string Share(long bytes)=>total<=0?"—":string.Create(CultureInfo.CurrentCulture,$"{100.0*bytes/total:0.#}%");
        TableRow Row(string key,string name,long received,long sent)=>new(key,[name,HistoryChartModel.Bytes(received),HistoryChartModel.Bytes(sent),HistoryChartModel.Bytes(received+sent),Share(received+sent)]);
        var rows=view.Applications.Where(a=>query.Length==0||a.Name.Contains(query,StringComparison.OrdinalIgnoreCase)).Select(a=>Row(a.Name,a.Name,a.Received,a.Sent)).ToList();
        if(view.OtherApplications>0&&query.Length==0)
            rows.Add(Row("\0",string.Create(CultureInfo.CurrentCulture,$"Other applications ({view.OtherApplications:N0} more)"),view.OtherReceived,view.OtherSent));
        return rows;
    }

    /// <summary>The line above the Applications table.</summary>
    public static string ApplicationSummary(UsageApplicationView view,string query,int shown,bool slot)
    {
        long total=view.Applications.Sum(a=>a.Received+a.Sent)+view.OtherReceived+view.OtherSent;
        return view.Applications.Count==0?slot?"No application traffic recorded in this slot.":"No application traffic recorded in this period."
            :query.Length>0?string.Create(CultureInfo.CurrentCulture,$"{shown} of {view.Applications.Count+view.OtherApplications:N0} applications match.")
            :string.Create(CultureInfo.CurrentCulture,$"{view.Applications.Count+view.OtherApplications:N0} applications · {HistoryChartModel.Bytes(total)} observed · {view.RecordedMinutes:N0} recorded minutes");
    }
}
