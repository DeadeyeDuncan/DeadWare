using System.Collections.ObjectModel;
using System.Text.Json;
namespace DeadWare.Core;

public record Snapshot(DateTimeOffset Timestamp,double MonotonicSeconds,IReadOnlyDictionary<string,double?> Values)
{
    public string? SessionId {get;init;}
    public double PollingIntervalSeconds {get;init;}=1;
}
public record SamplePoint(DateTimeOffset Timestamp,double MonotonicSeconds,double? Value)
{
    public bool BreakBefore {get;init;}
    public double PollingIntervalSeconds {get;init;}=1;
}
public record Summary(double? Minimum,double? Maximum,double? Average);
public static class HistoryRanges
{
    public const int MaximumSeconds=21600;
    public const int MaximumSamples=43201;
    public static IReadOnlyList<int> Seconds {get;}=Array.AsReadOnly(new[]{60,300,900,3600,10800,MaximumSeconds});
    public static bool IsSupported(int seconds)=>Seconds.Contains(seconds);
    public static string Label(int seconds)=>seconds<3600?$"{seconds/60} min":$"{seconds/3600} h";
}
public sealed class SampleHistory
{
    private readonly object gate=new();
    private readonly Queue<Snapshot> samples=new();
    private readonly Dictionary<string,string> keys=new(StringComparer.Ordinal);
    private double lastSeconds=double.NegativeInfinity;
    private Snapshot[]? chartSnapshot;
    public int Count {get {lock(gate)return samples.Count;}}
    public void Add(Snapshot s)
    {
        if (!double.IsFinite(s.MonotonicSeconds)) return;
        lock(gate)
        {
            if(s.MonotonicSeconds<=lastSeconds) return;
            samples.Enqueue(Copy(s));
            chartSnapshot=null;
            lastSeconds=s.MonotonicSeconds;
            while(samples.Count>HistoryRanges.MaximumSamples || (samples.Count>0 && samples.Peek().MonotonicSeconds<s.MonotonicSeconds-HistoryRanges.MaximumSeconds)) samples.Dequeue();
        }
    }
    public static double? Finite(double? value)=>value.HasValue && double.IsFinite(value.Value)?value:null;
    // Caller supplies timestamps rebased to this collector's monotonic timeline.
    // Existing live samples win; later hardware discovery may add metrics to the
    // same restored timestamp without replacing earlier restored metrics.
    public void Restore(IReadOnlyList<Snapshot> restored,double now)
    {
        if(!double.IsFinite(now))return;
        lock(gate)
        {
            double cutoff=Math.Max(now,lastSeconds)-HistoryRanges.MaximumSeconds;
            double firstLive=samples.FirstOrDefault(s=>s.SessionId is null)?.MonotonicSeconds??double.PositiveInfinity;
            var merged=samples.ToDictionary(s=>s.MonotonicSeconds);
            foreach(var sample in restored)
            {
                double time=sample.MonotonicSeconds;
                if(string.IsNullOrEmpty(sample.SessionId)||!double.IsFinite(time)||time<cutoff||time>now||time>=firstLive)continue;
                if(merged.TryGetValue(time,out var existing))
                {
                    if(existing.SessionId!=sample.SessionId)continue;
                    var values=existing.Values.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                    foreach(var pair in sample.Values)values.TryAdd(pair.Key,pair.Value);
                    merged[time]=Copy(existing with {Values=values});
                }
                else merged[time]=Copy(sample);
            }
            var ordered=merged.Values.Where(s=>s.MonotonicSeconds>=cutoff).OrderBy(s=>s.MonotonicSeconds).TakeLast(HistoryRanges.MaximumSamples).ToArray();
            samples.Clear();foreach(var sample in ordered)samples.Enqueue(sample);
            chartSnapshot=null;
            if(ordered.Length>0)lastSeconds=ordered[^1].MonotonicSeconds;
        }
    }
    // Collection gate owns the bounded identifier cache.
    private Snapshot Copy(Snapshot sample)
    {
        var values=new Dictionary<string,double?>(sample.Values.Count,StringComparer.Ordinal);
        foreach(var pair in sample.Values)
        {
            if(!keys.TryGetValue(pair.Key,out var key))
            {
                key=pair.Key;if(keys.Count<4096)keys.Add(key,key);
            }
            values.Add(key,Finite(pair.Value));
        }
        return sample with {Values=new ReadOnlyDictionary<string,double?>(values)};
    }
    public IReadOnlyList<Snapshot> Read() {lock(gate) return samples.ToArray();}
    public IReadOnlyList<SamplePoint> Series(string id,double now,int seconds)
    {
        lock(gate)
        {
            var points=new List<SamplePoint>();Snapshot? previous=null;
            foreach(var sample in samples)
            {
                if(sample.MonotonicSeconds<now-seconds||sample.MonotonicSeconds>now)continue;
                points.Add(new(sample.Timestamp,sample.MonotonicSeconds,sample.Values.GetValueOrDefault(id))
                    {BreakBefore=previous is not null&&previous.SessionId!=sample.SessionId,
                     PollingIntervalSeconds=PollingPolicy.Normalize(sample.PollingIntervalSeconds)});
                previous=sample;
            }
            return points;
        }
    }
    private Snapshot[] Capture(double now,int seconds)
    {
        lock(gate)return samples.Where(s=>s.MonotonicSeconds>=now-seconds&&s.MonotonicSeconds<=now).ToArray();
    }
    /// <summary>Pixel-bucket averages and extrema from immutable snapshot references; gaps are never averaged across.</summary>
    public IReadOnlyList<ChartRangePoint> ChartRanges(string id,double now,int seconds,int pixelWidth)
    {
        if(!double.IsFinite(now)||seconds<=0||pixelWidth<=0)throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot[] captured;lock(gate)captured=chartSnapshot??=samples.ToArray();
        var result=new List<ChartRangePoint>(Math.Min(pixelWidth,4096));
        int count=0;double bucket=double.NaN,total=0,min=0,max=0,start=0,end=0;
        double bucketSeconds=seconds/(double)pixelWidth;
        Snapshot? previous=null;double previousInterval=1;
        void Flush()
        {
            if(count==0)return;
            result.Add(new(start,end,total/count,min,max,count));count=0;bucket=double.NaN;
        }
        void Gap(double time)
        {
            Flush();if(result.Count==0||result[^1].Average.HasValue)result.Add(new(time,time,null,null,null,0));
        }
        foreach(var sample in captured)
        {
            double time=sample.MonotonicSeconds;
            if(time<now-seconds||time>now)continue;
            double interval=PollingPolicy.Normalize(sample.PollingIntervalSeconds);
            if(previous is not null&&(sample.SessionId!=previous.SessionId||time-previous.MonotonicSeconds>Math.Max(previousInterval,interval)+1.5))Gap(time);
            previous=sample;previousInterval=interval;
            if(Finite(sample.Values.GetValueOrDefault(id)) is not { } value){Gap(time);continue;}
            // Anchor buckets to the monotonic timeline, not the moving viewport.
            // Completed historical averages then scroll without being re-binned on each poll.
            // Floor also assigns restored negative timestamps to the correct interval.
            double column=Math.Floor(time/bucketSeconds);
            if(column!=bucket)Flush();
            if(count==0){bucket=column;start=time;min=max=value;total=0;}
            end=time;min=Math.Min(min,value);max=Math.Max(max,value);total+=value;count++;
        }
        Flush();return result;
    }
    public Summary Summarize(string id,double now,int seconds)
    {
        var summary=new Accumulator();
        foreach(var sample in Capture(now,seconds))summary.Add(sample.Values.GetValueOrDefault(id));
        return summary.Finish();
    }
    public IReadOnlyDictionary<string,Summary> Summaries(IEnumerable<string> ids,double now,int seconds)
    {
        var summaries=ids.Distinct(StringComparer.Ordinal).ToDictionary(id=>id,_=>new Accumulator(),StringComparer.Ordinal);
        if(summaries.Count==0)return new Dictionary<string,Summary>();
        // Capture references once and release the collection lock before walking all sensors.
        // No per-reading SamplePoint objects or arrays are needed for table statistics.
        foreach(var sample in Capture(now,seconds))
            foreach(var pair in summaries)
                if(sample.Values.TryGetValue(pair.Key,out var value))pair.Value.Add(value);
        return summaries.ToDictionary(p=>p.Key,p=>p.Value.Finish(),StringComparer.Ordinal);
    }
    private sealed class Accumulator
    {
        private int count;
        private double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity,total;
        public void Add(double? value)
        {
            if(Finite(value) is not { } number)return;
            minimum=Math.Min(minimum,number);maximum=Math.Max(maximum,number);total+=number;count++;
        }
        public Summary Finish()=>count==0?new(null,null,null):new(minimum,maximum,total/count);
    }
    /// <summary>
    /// Keep each pixel column's first, minimum, maximum and last reading in time order.
    /// Null markers preserve actual collection gaps; callers must not infer gaps from
    /// elapsed time between the retained points, which can be far apart after reduction.
    /// </summary>
    public static IReadOnlyList<SamplePoint> ReduceForChart(IReadOnlyList<SamplePoint> points,double now,int seconds,int pixelWidth)
    {
        if(!double.IsFinite(now))throw new ArgumentOutOfRangeException(nameof(now));
        if(seconds<=0)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(pixelWidth<=0)throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        var result=new List<SamplePoint>();
        int bucket=-1,first=-1,minimum=-1,maximum=-1,last=-1;
        double previous=double.NaN,previousInterval=1;
        void Flush()
        {
            if(first<0)return;
            Span<int> indices=stackalloc int[]{first,minimum,maximum,last};indices.Sort();
            int added=-1;
            foreach(int index in indices)if(index!=added){result.Add(points[index]);added=index;}
            first=minimum=maximum=last=-1;bucket=-1;
        }
        void Gap(SamplePoint point)
        {
            Flush();
            if(result.Count==0||result[^1].Value.HasValue)result.Add(point with {Value=null});
        }
        for(int i=0;i<points.Count;i++)
        {
            var point=points[i];double time=point.MonotonicSeconds;
            if(!double.IsFinite(time)||time<now-seconds||time>now)continue;
            if(point.BreakBefore)Gap(point);
            double interval=PollingPolicy.Normalize(point.PollingIntervalSeconds);
            if(Finite(point.Value) is not { } value){Gap(point);previous=time;previousInterval=interval;continue;}
            if(time-previous>Math.Max(previousInterval,interval)+1.5)Gap(point);
            int column=(int)Math.Clamp((time-(now-seconds))*pixelWidth/seconds,0,pixelWidth-1);
            if(column!=bucket){Flush();bucket=column;}
            if(first<0)first=minimum=maximum=i;
            else
            {
                if(value<points[minimum].Value)minimum=i;
                if(value>points[maximum].Value)maximum=i;
            }
            last=i;previous=time;previousInterval=interval;
        }
        Flush();return result;
    }
    public static Summary Statistics(IEnumerable<SamplePoint> points)
    {
        var v=points.Select(p=>Finite(p.Value)).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();
        return v.Length==0?new(null,null,null):new(v.Min(),v.Max(),v.Average());
    }
}
public readonly record struct ChartRangePoint(double StartSeconds,double EndSeconds,double? Average,double? Minimum,double? Maximum,int Samples);
public sealed class CounterRate
{
    private long? previous;
    private double last;
    public double? Update(long counter,double seconds)
    {
        if(counter<0 || !double.IsFinite(seconds)) {previous=null;return null;}
        double? result=previous.HasValue && counter>=previous.Value && seconds>last ? (counter-previous.Value)/(seconds-last):null;
        previous=counter;last=seconds;
        return SampleHistory.Finite(result);
    }
}
public record Device(string Id,string Name,bool HasCoreTemperature=false)
{
    public override string ToString()=>Name;
}
public static class DeviceSelection
{
    public static string? Resolve(string? requested,IReadOnlyList<Device> devices)=>requested is null?(devices.FirstOrDefault(d=>d.HasCoreTemperature)??devices.FirstOrDefault())?.Id:devices.FirstOrDefault(d=>d.Id==requested)?.Id;
}
public record ProcessTraffic(int Pid,long Generation,string Name,double? DownloadBytesPerSecond,double? UploadBytesPerSecond,long ReceivedBytes,long SentBytes);
/// <summary>Generation is the process creation time. Events with a previous generation never enter a reused PID's counters.</summary>
public sealed class ProcessLedger
{
    private sealed class Entry(long generation,string name)
    {public long Generation=generation;public string Name=name;public long Down,Up,PreviousDown,PreviousUp;}
    private readonly Dictionary<int,Entry> entries=new();
    private readonly object gate=new();
    private double? last;
    public void Start(int pid,long generation,string name)
    {
        lock(gate)
        {
            if(entries.TryGetValue(pid,out var e) && e.Generation>=generation) return;
            entries[pid]=new(generation,name);
        }
    }
    public void Stop(int pid,long generation) {lock(gate) if(entries.TryGetValue(pid,out var e)&&e.Generation==generation) entries.Remove(pid);}
    public void Add(int pid,long generation,int down,int up)
    {
        lock(gate) if(entries.TryGetValue(pid,out var e)&&e.Generation==generation && down>=0 && up>=0) {e.Down+=down;e.Up+=up;}
    }
    public IReadOnlyList<ProcessTraffic> Take(double now)
    {
        lock(gate)
        {
            double elapsed=last.HasValue?now-last.Value:double.NaN;
            bool valid=double.IsFinite(elapsed)&&elapsed>0;
            var result=entries.Where(p=>p.Value.Down>0||p.Value.Up>0).Select(p=>new ProcessTraffic(p.Key,p.Value.Generation,p.Value.Name,
                valid?(p.Value.Down-p.Value.PreviousDown)/elapsed:null,valid?(p.Value.Up-p.Value.PreviousUp)/elapsed:null,p.Value.Down,p.Value.Up)).ToArray();
            foreach(var e in entries.Values) {e.PreviousDown=e.Down;e.PreviousUp=e.Up;}
            last=double.IsFinite(now)?now:null;
            return result;
        }
    }
}
public record WorkArea(double Left,double Top,double Width,double Height);
public record WindowPlacement(double Left,double Top,double Width,double Height,bool Topmost)
{
    public static WindowPlacement Clamp(WindowPlacement p,IReadOnlyList<WorkArea> areas,double width,double height)
    {
        var valid=areas.Where(a=>double.IsFinite(a.Left)&&double.IsFinite(a.Top)&&a.Width>0&&a.Height>0).ToArray();
        if(valid.Length==0) valid=[new(0,0,1920,1040)];
        double left=double.IsFinite(p.Left)?p.Left:valid[0].Left+40,top=double.IsFinite(p.Top)?p.Top:valid[0].Top+40;
        var area=valid.OrderByDescending(a=>Math.Max(0,Math.Min(left+Math.Max(1,p.Width),a.Left+a.Width)-Math.Max(left,a.Left))*Math.Max(0,Math.Min(top+Math.Max(1,p.Height),a.Top+a.Height)-Math.Max(top,a.Top))).First();
        double w=Math.Min(area.Width,double.IsFinite(p.Width)&&p.Width>=200?p.Width:width);
        double h=Math.Min(area.Height,double.IsFinite(p.Height)&&p.Height>=150?p.Height:height);
        return new(Math.Clamp(left,area.Left,area.Left+area.Width-w),Math.Clamp(top,area.Top,area.Top+area.Height-h),w,h,p.Topmost);
    }
}
public static class GlassLevels
{
    public const string Off="Off",Subtle="Subtle",Balanced="Balanced",Strong="Strong";
    public static readonly string[] All=[Off,Subtle,Balanced,Strong];
    public static string Normalize(string? level)=>All.FirstOrDefault(l=>l==level)??Balanced;
}
public sealed class UserSettings
{
    public bool ConnectionsRecording {get;set;}=true;
    public bool WidgetConnectionsMap {get;set;}
    public bool WidgetConnectionsNorthAmerica {get;set;}
    public string? ConnectionsHomeCityId {get;set;}
    public bool ConnectionsAnimation {get;set;}=true;
    public List<string> WidgetGraphs {get;set;}=WidgetGraphSelection.Kinds.ToList();
    public bool WidgetAlwaysOnTop {get;set;}=true;
    public bool JoinCpuGpuPopouts {get;set;}
    public bool ShowGpuFanSpeed {get;set;}
    public double PollingIntervalSeconds {get;set;}=1;
    public bool BatteryRecording {get;set;}=true;
    public bool RestoreOpenWindows {get;set;}=true;
    public bool StartInTray {get;set;}
    public bool CoolingRecording {get;set;}=true;
    public bool CoolingNotifications {get;set;}
    public int CoolingDetailedDays {get;set;}=30;
    public int CoolingSummaryDays {get;set;}=90;
    public string CoolingConfigurationId {get;set;}=Guid.NewGuid().ToString("N");
    public List<string> OpenWindows {get;set;}=new();
    public string WindowPlacementFormat {get;set;}=WindowCoordinates.Format;
    public bool ExtendedSensors {get;set;}
    public string Theme {get;set;}="Midnight";
    public string GlassLevel {get;set;}=GlassLevels.Balanced;
    public Dictionary<string,string> GraphColors {get;set;}=new();
    public Dictionary<string,GraphLineStyle> GraphStyles {get;set;}=new();
    public bool GraphSmoothing {get;set;}=true;
    public int HistorySeconds {get;set;}=60;
    public string? SelectedGpuId {get;set;}
    public Dictionary<string,WindowPlacement> Windows {get;set;}=new();
}
public record SettingsRead(UserSettings Settings,string? Error);
public static class SettingsCodec
{
    public static SettingsRead Read(string json)
    {
        try
        {
            var s=JsonSerializer.Deserialize<UserSettings>(json);
            if(s is null) return new(new(),"Settings were empty; defaults restored.");
            if(s.Theme is not ("Midnight" or "Ember" or "Glacier" or "Amethyst" or "Neon" or "Blackout" or "Skyrim" or "Mage" or "Daylight")) s.Theme="Midnight";
            s.GlassLevel=GlassLevels.Normalize(s.GlassLevel);
            s.GraphColors=GraphColorSettings.Normalize(s.GraphColors);
            s.GraphStyles=GraphLineStyles.Normalize(s.GraphStyles);
            if(string.IsNullOrWhiteSpace(s.ConnectionsHomeCityId)||s.ConnectionsHomeCityId.Length>80||s.ConnectionsHomeCityId.Any(char.IsControl))s.ConnectionsHomeCityId=null;
            bool invalid=!HistoryRanges.IsSupported(s.HistorySeconds) || s.Windows is null || PollingPolicy.Normalize(s.PollingIntervalSeconds)!=s.PollingIntervalSeconds;
            var widgetGraphs=WidgetGraphSelection.Normalize(s.WidgetGraphs);
            invalid|=s.WidgetGraphs is null||!s.WidgetGraphs.SequenceEqual(widgetGraphs,StringComparer.Ordinal);
            s.WidgetGraphs=widgetGraphs;
            s.PollingIntervalSeconds=PollingPolicy.Normalize(s.PollingIntervalSeconds);
            if(!HistoryRanges.IsSupported(s.HistorySeconds)) s.HistorySeconds=60;
            s.Windows??=new();
            using var document=JsonDocument.Parse(json);
            // Retain the pin choice even when legacy coordinates must be discarded.
            if(!document.RootElement.TryGetProperty(nameof(UserSettings.WidgetAlwaysOnTop),out _)&&
                s.Windows.TryGetValue("widget",out var widget)&&widget is not null)s.WidgetAlwaysOnTop=widget.Topmost;
            if(s.CoolingDetailedDays is not (7 or 30))s.CoolingDetailedDays=30;
            if(s.CoolingSummaryDays is not (30 or 90))s.CoolingSummaryDays=90;
            if(!Guid.TryParse(s.CoolingConfigurationId,out _))s.CoolingConfigurationId=Guid.NewGuid().ToString("N");
            s.OpenWindows=DetachedWindowState.Normalize(s.OpenWindows);
            s.Windows=s.Windows.Where(p=>p.Value is not null).Take(16).ToDictionary(p=>p.Key,p=>p.Value);
            bool legacyPlacement=!document.RootElement.TryGetProperty(nameof(UserSettings.WindowPlacementFormat),out _)||s.WindowPlacementFormat!=WindowCoordinates.Format;
            bool resetPlacement=legacyPlacement&&s.Windows.Count>0;
            if(legacyPlacement)s.Windows.Clear();
            s.WindowPlacementFormat=WindowCoordinates.Format;
            return new(s,resetPlacement?"Window layout was reset for the new DPI-safe placement format.":invalid?"Invalid settings were replaced with defaults.":null);
        }
        catch(JsonException) {return new(new(),"Settings could not be read; defaults restored.");}
    }
    public static string Write(UserSettings settings)=>JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true});
}


