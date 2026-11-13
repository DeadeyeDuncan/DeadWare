using System.Globalization;
namespace DeadWare.Core;

/// <summary>How a charge point and its line are drawn: solid for saver off, dashed for saver on, dotted when the state is unknown.</summary>
public enum BatterySaverLook {Off,On,Unknown}
public enum BatteryMarkKind {Charging,Saver}

/// <summary>One bucket's charge on the chart.</summary>
/// <param name="Certain">False when the bucket mixes saver states or does not know them; the point is drawn hollow.</param>
/// <param name="Range">The top and bottom of the bucket's lowest-to-highest charge, for a vertical line; null without both.</param>
/// <param name="Connector">Where a line to this point starts and the look it is drawn in (the earlier observation's); null when the two are not joined.</param>
/// <param name="Tolerance">How far, horizontally, the pointer may be from the point and still hover it.</param>
public sealed record BatteryChartPoint(double X,double Y,BatterySaverLook Look,bool Certain,(double Top,double Bottom)? Range,(double X,double Y,BatterySaverLook Look)? Connector,double Tolerance,DateTimeOffset Start,double Average);
/// <summary>A tick under the plot for a bucket that was charging or in battery saver: filled when the whole bucket was, outlined when only part.</summary>
public sealed record BatteryChartMark(double X,BatteryMarkKind Kind,bool Whole);
public sealed record BatteryChartFrame(ChartPlot Plot,IReadOnlyList<ChartLine> Lines,IReadOnlyList<BatteryChartPoint> Points,IReadOnlyList<BatteryChartMark> Marks,string FromLabel,string ToLabel,bool HasReadings,TimeZoneInfo Zone);
public sealed record BatteryChartHover(double X,double Y,string Time,string Text,BatterySaverLook Look);

/// <summary>
/// Lays out the battery charge history with no user-interface framework, by the WPF BatteryHistoryChart's rules:
/// which observations are joined, how saver state changes a point's look, and which marks sit under the plot.
/// </summary>
public static class BatteryChartModel
{
    public static BatteryChartFrame Build(BatteryHistoryView view,ChartPlot plot,TimeZoneInfo? zone=null)
    {
        zone??=TimeZoneInfo.Local;
        if(!(plot.Width>0)||!(plot.Height>0)||!double.IsFinite(plot.Width)||!double.IsFinite(plot.Height))return new(plot,[],[],[],"","",false,zone);
        double seconds=Math.Max(60,(view.To-view.From).TotalSeconds);
        double X(DateTimeOffset time)=>plot.Left+plot.Width*Math.Clamp((time-view.From).TotalSeconds/seconds,0,1);
        double Y(double value)=>plot.Top+plot.Height*(1-Math.Clamp(value,0,100)/100);

        var lines=Enumerable.Range(0,5).Select(i=>new ChartLine(Y(i*25),$"{i*25}%")).ToArray();
        var points=new List<BatteryChartPoint>();var marks=new List<BatteryChartMark>();
        BatteryHistoryBucket? previous=null;
        foreach(var bucket in view.Buckets)
        {
            double x=X(bucket.Start+(bucket.End-bucket.Start)/2);
            if(bucket.Average is { } average)
            {
                // The earlier observation's state lasts until the next recorded change, so the joining line takes its look.
                (double,double,BatterySaverLook)? connector=previous is not null&&CanConnect(previous,bucket)
                    ?(X(previous.Start),Y(previous.Average!.Value),Look(previous)):null;
                (double,double)? range=bucket.Minimum is { } minimum&&bucket.Maximum is { } maximum?(Y(maximum),Y(minimum)):null;
                bool certain=bucket.SaverKnownSamples==bucket.Samples&&(bucket.SaverSamples==0||bucket.SaverSamples==bucket.Samples);
                points.Add(new(x,Y(average),Look(bucket),certain,range,connector,Math.Max(6,(X(bucket.End)-X(bucket.Start))/2),bucket.Start,average));
            }
            if(bucket.ChargingSamples>0)marks.Add(new(x,BatteryMarkKind.Charging,bucket.ChargingSamples==bucket.Samples));
            if(bucket.SaverSamples>0)marks.Add(new(x,BatteryMarkKind.Saver,bucket.SaverSamples==bucket.Samples));
            // An unknown percentage cannot be bridged, even when the state is known.
            previous=bucket;
        }
        string format=(view.To-view.From).TotalDays<=2?"MMM d HH:mm":"MMM d yyyy";
        string Date(DateTimeOffset time)=>TimeZoneInfo.ConvertTime(time,zone).ToString(format,CultureInfo.CurrentCulture);
        return new(plot,lines,points,marks,Date(view.From),Date(view.To),view.Buckets.Any(b=>b.ValidSamples>0),zone);
    }

    /// <summary>Two buckets are joined only when each is one valid observation, both in the same recording segment, and no more than 90 seconds apart.</summary>
    public static bool CanConnect(BatteryHistoryBucket a,BatteryHistoryBucket b)=>
        a.Samples==1&&b.Samples==1&&a.ValidSamples==1&&b.ValidSamples==1&&
        a.Average.HasValue&&b.Average.HasValue&&a.Segment is not null&&a.Segment==b.Segment&&
        b.Start>a.End&&b.Start-a.End<=TimeSpan.FromSeconds(90);

    public static BatterySaverLook Look(BatteryHistoryBucket bucket)=>
        bucket.SaverKnownSamples!=bucket.Samples?BatterySaverLook.Unknown:bucket.SaverSamples>0?BatterySaverLook.On:BatterySaverLook.Off;

    /// <summary>The point nearest the pointer horizontally, within that point's tolerance and inside the plot; otherwise null.</summary>
    public static BatteryChartHover? Hover(BatteryChartFrame frame,double x,double y)
    {
        var plot=frame.Plot;
        if(x<plot.Left||x>plot.Left+plot.Width||y<plot.Top||y>plot.Top+plot.Height)return null;
        BatteryChartPoint? best=null;double bestDistance=double.PositiveInfinity;
        foreach(var point in frame.Points)
        {
            double distance=Math.Abs(point.X-x);
            if(distance<=point.Tolerance&&distance<bestDistance){best=point;bestDistance=distance;}
        }
        return best is null?null:new(best.X,best.Y,TimeZoneInfo.ConvertTime(best.Start,frame.Zone).ToString("MMM d HH:mm",CultureInfo.CurrentCulture),
            best.Average.ToString("0.#",CultureInfo.CurrentCulture)+"%",best.Look);
    }
}

/// <summary>The Battery history page's wording.</summary>
public static class BatteryPresenter
{
    public static string Power(BatteryPowerState state,bool stale)=>stale?"Waiting for a fresh reading":state switch
    {
        BatteryPowerState.NoBattery=>"No battery detected",BatteryPowerState.Charging=>"Charging",
        BatteryPowerState.OnBattery=>"On battery",BatteryPowerState.PluggedIn=>"Plugged in · not charging",_=>"Battery reading unavailable",
    };

    /// <summary>The lowest and highest charge in the view and how many observations it holds.</summary>
    public static string Summary(BatteryHistoryView view)
    {
        long count=view.Buckets.Sum(b=>b.Samples);
        var valid=view.Buckets.Where(b=>b.Minimum.HasValue&&b.Maximum.HasValue).ToArray();
        return valid.Length==0?"No charge observations in this range yet. Saved readings will appear as monitoring continues."
            :string.Create(CultureInfo.CurrentCulture,$"MIN {TableSync.Number(valid.Min(b=>b.Minimum),"%")}    MAX {TableSync.Number(valid.Max(b=>b.Maximum),"%")}    ·    {count:N0} observations in this view");
    }

    /// <summary>How much the local history holds and since when.</summary>
    public static string Storage(BatteryHistoryView view)=>
        string.Create(CultureInfo.CurrentCulture,$"Local history: {HistoryChartModel.Bytes(view.StorageBytes)} · {view.TotalObservations:N0} observations retained indefinitely")+
        (view.FirstRecorded is { } first?string.Create(CultureInfo.CurrentCulture,$"\nRecorded since {first.ToLocalTime():g}"):"");
}
