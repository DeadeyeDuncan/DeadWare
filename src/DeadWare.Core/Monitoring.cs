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
    // Columns, not a dictionary per poll. Samples sit in time order in chunks of 1,024. A chunk holds each sample's time, stamp,
    // session, polling interval and breaks, and one float column for each metric with a value in it. A poll used to keep its own
    // copy of every value, some 11 KB for 300 of them; a column costs 4 bytes a value, a metric missing from a whole chunk costs
    // nothing there, and a chunk leaves whole once all its samples have aged out, so nothing is copied as the window moves.
    // Reads binary-search their window, so a one-minute chart costs the same after six hours as after six minutes, and a chart
    // line is a sequential scan of one column. Every read and write holds the gate; no scan is long enough to need a copy.
    private const int Shift=10,Size=1<<Shift,Mask=Size-1;
    // A cell holds a reading, a key present without a reading (null), or a key the sample did not have: two NaNs told apart by
    // their payload. Restore must not fill a key that was present without a reading, and Read() hands back each sample's own
    // keys. Only finite readings are stored and nothing computes with a cell before testing it, so neither NaN can be blurred.
    private const int NullBits=0x7FC00000,AbsentBits=0x7FC00001;
    // A sample's breaks, set from the sample before it as it is appended: another session, or longer than the cadence allows.
    private const byte NewSession=1,AfterGap=2;
    // Bounds however many ids appear: past them a new metric is not kept and a new session id is not interned.
    private const int MaximumMetrics=8192,MaximumSessions=1024;
    private sealed class Chunk(int metrics)
    {
        public readonly double[] Times=new double[Size],Intervals=new double[Size];
        public readonly DateTimeOffset[] Stamps=new DateTimeOffset[Size];
        public readonly string?[] Sessions=new string?[Size];
        public readonly byte[] Breaks=new byte[Size];
        public float[]?[] Columns=new float[]?[Math.Max(16,metrics)];
        public float[]? Column(int metric)=>(uint)metric<(uint)Columns.Length?Columns[metric]:null;
    }
    private readonly List<Chunk> chunks=[];
    // The oldest sample's slot in chunks[0], and how many samples there are.
    private int first,count;
    // Metric ids are interned once. holders counts the chunks with a column for each metric: a metric whose last column leaves
    // with its chunk is forgotten and its index reused, so a drive unplugged six hours ago costs nothing.
    private readonly Dictionary<string,int> metrics=new(StringComparer.Ordinal);
    private readonly List<string?> names=[];
    private readonly List<int> holders=[];
    private readonly Stack<int> unused=new();
    private readonly Dictionary<string,string> sessions=new(StringComparer.Ordinal);
    private double lastSeconds=double.NegativeInfinity;
    public int Count {get {lock(gate)return count;}}
    /// <summary>
    /// What history keeps: everything read back from it, which is the chart lines and every sensor (the Sensors page shows each
    /// one's minimum, average and maximum over the range). Memory in bytes, and each drive's activity, temperature, life, used
    /// share and volume free space, are only ever read from the newest reading; six hours of them would only cost memory.
    /// </summary>
    private static bool Keeps(string id)=>id is not ("memory/used" or "memory/total")&&!(id.StartsWith("storage/",StringComparison.Ordinal)&&
        (id.EndsWith("/active",StringComparison.Ordinal)||id.EndsWith("/temperature",StringComparison.Ordinal)||id.EndsWith("/life",StringComparison.Ordinal)
        ||id.EndsWith("/used",StringComparison.Ordinal)||id.EndsWith("/free",StringComparison.Ordinal)));
    // A reading past the float range is clamped, not turned into an infinity, so a stored reading is always finite.
    private static float Encode(double? value)=>value is { } number&&double.IsFinite(number)?(float)Math.Clamp(number,float.MinValue,float.MaxValue):BitConverter.Int32BitsToSingle(NullBits);
    private static bool IsAbsent(float cell)=>BitConverter.SingleToInt32Bits(cell)==AbsentBits;
    private static double? Decode(float cell)=>float.IsNaN(cell)?null:cell;
    private Chunk Locate(int index,out int offset) {int position=first+index;offset=position&Mask;return chunks[position>>Shift];}
    private double TimeAt(int index)=>Locate(index,out int offset).Times[offset];
    private int Find(string id)=>metrics.TryGetValue(id,out int metric)?metric:-1;
    /// <summary>The index range [Start, End) of samples with now-seconds &lt;= time &lt;= now. Samples are strictly increasing in time.</summary>
    private (int Start,int End) Window(double now,double seconds)
    {
        // A NaN bound compares false to everything, which the linear scans this replaces read as "no bound".
        double from=now-seconds;
        int start=0,end=count;
        if(!double.IsNaN(from))
        {
            int low=0,high=count;
            while(low<high){int middle=(low+high)>>>1;if(TimeAt(middle)<from)low=middle+1;else high=middle;}
            start=low;
        }
        if(!double.IsNaN(now))
        {
            int low=start,high=count;
            while(low<high){int middle=(low+high)>>>1;if(TimeAt(middle)<=now)low=middle+1;else high=middle;}
            end=low;
        }
        return (start,end);
    }
    public void Add(Snapshot s)
    {
        if (!double.IsFinite(s.MonotonicSeconds)) return;
        lock(gate)
        {
            if(s.MonotonicSeconds<=lastSeconds) return;
            var (chunk,offset)=Append(s.MonotonicSeconds,s.Timestamp,s.SessionId,s.PollingIntervalSeconds);
            Write(chunk,offset,s.Values,fill:false);
            lastSeconds=s.MonotonicSeconds;
            while(count>HistoryRanges.MaximumSamples || (count>0 && TimeAt(0)<s.MonotonicSeconds-HistoryRanges.MaximumSeconds)) Dequeue();
        }
    }
    /// <summary>Appends a sample's own fields after the newest and returns where its values go.</summary>
    private (Chunk Chunk,int Offset) Append(double time,DateTimeOffset stamp,string? session,double interval)
    {
        session=Intern(session);
        byte breaks=0;
        if(count>0)
        {
            var before=Locate(count-1,out int at);
            if(!string.Equals(before.Sessions[at],session,StringComparison.Ordinal))breaks|=NewSession;
            if(time-before.Times[at]>Math.Max(PollingPolicy.Normalize(before.Intervals[at]),PollingPolicy.Normalize(interval))+1.5)breaks|=AfterGap;
        }
        int position=first+count;
        if((position>>Shift)==chunks.Count)chunks.Add(new Chunk(names.Count));
        var chunk=chunks[position>>Shift];int offset=position&Mask;
        chunk.Times[offset]=time;chunk.Stamps[offset]=stamp;chunk.Sessions[offset]=session;chunk.Intervals[offset]=interval;chunk.Breaks[offset]=breaks;
        count++;
        return (chunk,offset);
    }
    /// <summary>Stores a sample's values; with <paramref name="fill"/>, only those of keys it does not have yet.</summary>
    private void Write(Chunk chunk,int offset,IReadOnlyDictionary<string,double?> values,bool fill)
    {
        foreach(var pair in values)
        {
            int metric=Metric(pair.Key);
            if(metric<0)continue;
            var column=ColumnFor(chunk,metric);
            if(!fill||IsAbsent(column[offset]))column[offset]=Encode(pair.Value);
        }
    }
    // Collection gate owns the bounded identifier tables.
    private int Metric(string id)
    {
        if(metrics.TryGetValue(id,out int metric))return metric;
        if(!Keeps(id)||metrics.Count>=MaximumMetrics)return -1;
        if(unused.TryPop(out metric))names[metric]=id;
        else {metric=names.Count;names.Add(id);holders.Add(0);}
        metrics.Add(id,metric);
        return metric;
    }
    private float[] ColumnFor(Chunk chunk,int metric)
    {
        if(metric>=chunk.Columns.Length)Array.Resize(ref chunk.Columns,Math.Max(metric+1,chunk.Columns.Length*2));
        if(chunk.Columns[metric] is { } column)return column;
        column=new float[Size];column.AsSpan().Fill(BitConverter.Int32BitsToSingle(AbsentBits));
        holders[metric]++;
        return chunk.Columns[metric]=column;
    }
    private string? Intern(string? session)
    {
        if(session is null)return null;
        if(sessions.TryGetValue(session,out var known))return known;
        if(sessions.Count<MaximumSessions)sessions.Add(session,session);
        return session;
    }
    private void Dequeue()
    {
        first++;count--;
        if(first<Size)return;
        // The oldest chunk is spent. Its columns leave with it, and a metric left with no column anywhere is forgotten.
        var spent=chunks[0];chunks.RemoveAt(0);first=0;
        for(int metric=0;metric<spent.Columns.Length;metric++)
            if(spent.Columns[metric] is not null&&--holders[metric]==0)Forget(metric);
    }
    private void Forget(int metric) {metrics.Remove(names[metric]!);names[metric]=null;unused.Push(metric);}
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
            double firstLive=double.PositiveInfinity;
            for(int index=0;index<count;index++){var chunk=Locate(index,out int at);if(chunk.Sessions[at] is null){firstLive=chunk.Times[at];break;}}
            // The saved samples that may join, oldest first. At one time the first given stays first and later ones only add keys.
            var accepted=new List<(double Time,int Order)>();
            for(int i=0;i<restored.Count;i++)
            {
                double time=restored[i].MonotonicSeconds;
                if(string.IsNullOrEmpty(restored[i].SessionId)||!double.IsFinite(time)||time<cutoff||time>now||time>=firstLive)continue;
                accepted.Add((time,i));
            }
            accepted.Sort();
            // Merged by time into rows: each an existing sample or the first saved one at its time, with the saved samples at that
            // time that may add the keys it lacks (those of its own session).
            var plan=new List<Row>(count+accepted.Count);bool added=false;
            for(int j=0,k=0;j<count||k<accepted.Count;)
            {
                double existing=j<count?TimeAt(j):double.PositiveInfinity,incoming=k<accepted.Count?accepted[k].Time:double.PositiveInfinity;
                if(existing<incoming){plan.Add(new(j++,-1,0,0));continue;}
                int from=k;
                while(k<accepted.Count&&accepted[k].Time==incoming)k++;
                if(existing==incoming)plan.Add(new(j++,-1,from,k));
                else {plan.Add(new(-1,from,from+1,k));added=true;}
            }
            int skip=0;
            while(skip<plan.Count&&(plan[skip].Kept>=0?TimeAt(plan[skip].Kept):accepted[plan[skip].Added].Time)<cutoff)skip++;
            skip=Math.Max(skip,plan.Count-HistoryRanges.MaximumSamples);
            void Merge(Row row,Chunk chunk,int offset)
            {
                for(int t=row.From;t<row.To;t++)
                {
                    var sample=restored[accepted[t].Order];
                    if(string.Equals(sample.SessionId,chunk.Sessions[offset],StringComparison.Ordinal))Write(chunk,offset,sample.Values,fill:true);
                }
            }
            if(!added)
            {
                // Nothing to place between samples: saved keys join the samples where they stand.
                for(int i=skip;i<plan.Count;i++)if(plan[i].From<plan[i].To){var chunk=Locate(plan[i].Kept,out int at);Merge(plan[i],chunk,at);}
                for(int i=0;i<skip;i++)Dequeue();
            }
            else
            {
                // Saved samples fall between existing ones, so the store is rebuilt in time order; the old chunks stay readable meanwhile.
                var old=chunks.ToArray();int oldFirst=first;
                chunks.Clear();first=count=0;
                for(int metric=0;metric<holders.Count;metric++)holders[metric]=0;
                for(int i=skip;i<plan.Count;i++)
                {
                    var row=plan[i];Chunk chunk;int offset;
                    if(row.Kept>=0)
                    {
                        int position=oldFirst+row.Kept,at=position&Mask;var source=old[position>>Shift];
                        (chunk,offset)=Append(source.Times[at],source.Stamps[at],source.Sessions[at],source.Intervals[at]);
                        for(int metric=0;metric<source.Columns.Length;metric++)
                            if(source.Columns[metric] is { } column&&!IsAbsent(column[at]))ColumnFor(chunk,metric)[offset]=column[at];
                    }
                    else
                    {
                        var sample=restored[accepted[row.Added].Order];
                        (chunk,offset)=Append(sample.MonotonicSeconds,sample.Timestamp,sample.SessionId,sample.PollingIntervalSeconds);
                        Write(chunk,offset,sample.Values,fill:false);
                    }
                    Merge(row,chunk,offset);
                }
                for(int metric=0;metric<holders.Count;metric++)if(holders[metric]==0&&names[metric] is not null)Forget(metric);
            }
            if(count>0)lastSeconds=TimeAt(count-1);
        }
    }
    /// <summary>A row of a restore: the existing sample <paramref name="Kept"/>, or the saved sample <paramref name="Added"/>
    /// (an index into the accepted list), and the accepted range [From, To) at its time that may add keys to it.</summary>
    private readonly record struct Row(int Kept,int Added,int From,int To);
    public IReadOnlyList<Snapshot> Read()
    {
        lock(gate)
        {
            var all=new Snapshot[count];
            for(int index=0;index<count;index++)
            {
                var chunk=Locate(index,out int at);
                var values=new Dictionary<string,double?>(StringComparer.Ordinal);
                for(int metric=0;metric<chunk.Columns.Length;metric++)
                    if(chunk.Columns[metric] is { } column&&!IsAbsent(column[at]))values.Add(names[metric]!,Decode(column[at]));
                all[index]=new(chunk.Stamps[at],chunk.Times[at],new ReadOnlyDictionary<string,double?>(values)){SessionId=chunk.Sessions[at],PollingIntervalSeconds=chunk.Intervals[at]};
            }
            return all;
        }
    }
    public IReadOnlyList<SamplePoint> Series(string id,double now,int seconds)
    {
        lock(gate)
        {
            var (start,end)=Window(now,seconds);int metric=Find(id);
            var points=new List<SamplePoint>(end-start);
            for(int index=start;index<end;index++)points.Add(Point(index,start,metric));
            return points;
        }
    }
    /// <summary>Several <see cref="Series"/> reads under one lock; a missing id yields null points.</summary>
    public IReadOnlyDictionary<string,IReadOnlyList<SamplePoint>> SeriesMany(IReadOnlyList<string> ids,double now,int seconds)
    {
        lock(gate)
        {
            var (start,end)=Window(now,seconds);
            var lists=new Dictionary<string,IReadOnlyList<SamplePoint>>(StringComparer.Ordinal);
            foreach(var id in ids)
            {
                if(lists.ContainsKey(id))continue;
                int metric=Find(id);var points=new List<SamplePoint>(end-start);
                for(int index=start;index<end;index++)points.Add(Point(index,start,metric));
                lists[id]=points;
            }
            return lists;
        }
    }
    /// <summary>The sample at <paramref name="index"/> as a point; it breaks the line when its session differs from the sample before it in the window.</summary>
    private SamplePoint Point(int index,int start,int metric,bool reading=true)
    {
        var chunk=Locate(index,out int at);
        double? value=reading&&chunk.Column(metric) is { } column?Decode(column[at]):null;
        return new(chunk.Stamps[at],chunk.Times[at],value){BreakBefore=index>start&&(chunk.Breaks[at]&NewSession)!=0,PollingIntervalSeconds=PollingPolicy.Normalize(chunk.Intervals[at])};
    }
    /// <summary>
    /// <see cref="ReduceForChart"/> of <see cref="Series"/>, and the <see cref="Statistics"/> of that series, from one pass over the
    /// metric's column and without a point for every sample: a six-hour line reads 43,201 values and keeps a few per pixel column.
    /// </summary>
    public ReducedSeries ReduceSeries(string id,double now,int seconds,int pixelWidth)
    {
        if(!double.IsFinite(now))throw new ArgumentOutOfRangeException(nameof(now));
        if(seconds<=0)throw new ArgumentOutOfRangeException(nameof(seconds));
        if(pixelWidth<=0)throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        lock(gate)
        {
            var (start,end)=Window(now,seconds);int metric=Find(id);
            var result=new List<SamplePoint>();
            int bucket=-1,firstAt=-1,lowAt=-1,highAt=-1,lastAt=-1,samples=0;
            double low=0,high=0,minimum=0,maximum=0,total=0,from=now-seconds;
            void Flush()
            {
                if(firstAt<0)return;
                Span<int> kept=stackalloc int[]{firstAt,lowAt,highAt,lastAt};kept.Sort();
                int added=-1;
                foreach(int index in kept)if(index!=added){result.Add(Point(index,start,metric));added=index;}
                firstAt=lowAt=highAt=lastAt=-1;bucket=-1;
            }
            void Gap(int index)
            {
                Flush();
                if(result.Count==0||result[^1].Value.HasValue)result.Add(Point(index,start,metric,reading:false));
            }
            for(int index=start;index<end;)
            {
                var chunk=Locate(index,out int offset);
                int length=Math.Min(Size-offset,end-index);var column=chunk.Column(metric);
                for(int at=offset;at<offset+length;at++,index++)
                {
                    int breaks=index>start?chunk.Breaks[at]:0;
                    if((breaks&NewSession)!=0)Gap(index);
                    if(column is null||float.IsNaN(column[at])){Gap(index);continue;}
                    double value=column[at];
                    // The statistics keep the first of equal extremes, as Enumerable.Min and Max do.
                    if(samples++==0)minimum=maximum=value;
                    else {if(value<minimum)minimum=value;if(value>maximum)maximum=value;}
                    total+=value;
                    if((breaks&AfterGap)!=0)Gap(index);
                    int pixel=(int)Math.Clamp((chunk.Times[at]-from)*pixelWidth/seconds,0,pixelWidth-1);
                    if(pixel!=bucket){Flush();bucket=pixel;}
                    if(firstAt<0){firstAt=lowAt=highAt=index;low=high=value;}
                    else {if(value<low){lowAt=index;low=value;}if(value>high){highAt=index;high=value;}}
                    lastAt=index;
                }
            }
            Flush();
            return new(result,samples==0?new(null,null,null):new(minimum,maximum,total/samples));
        }
    }
    /// <summary>Pixel-bucket averages and extrema from one pass over the metric's column; gaps are never averaged across.</summary>
    public IReadOnlyList<ChartRangePoint> ChartRanges(string id,double now,int seconds,int pixelWidth)
    {
        if(!double.IsFinite(now)||seconds<=0||pixelWidth<=0)throw new ArgumentOutOfRangeException(nameof(now));
        lock(gate)return Ranges(Find(id),now,seconds,pixelWidth);
    }
    /// <summary>Several <see cref="ChartRanges"/> reads under one lock; results match per-id calls.</summary>
    public IReadOnlyDictionary<string,IReadOnlyList<ChartRangePoint>> ChartRangesMany(IReadOnlyList<string> ids,double now,int seconds,int pixelWidth)
    {
        if(!double.IsFinite(now)||seconds<=0||pixelWidth<=0)throw new ArgumentOutOfRangeException(nameof(now));
        ArgumentNullException.ThrowIfNull(ids);
        var result=new Dictionary<string,IReadOnlyList<ChartRangePoint>>(StringComparer.Ordinal);
        lock(gate)foreach(var id in ids)if(!result.ContainsKey(id))result[id]=Ranges(Find(id),now,seconds,pixelWidth);
        return result;
    }
    private List<ChartRangePoint> Ranges(int metric,double now,int seconds,int pixelWidth)
    {
        var (start,end)=Window(now,seconds);
        var state=new RangeState(Math.Min(pixelWidth,4096));
        double bucketSeconds=seconds/(double)pixelWidth;
        for(int index=start;index<end;)
        {
            var chunk=Locate(index,out int offset);
            int length=Math.Min(Size-offset,end-index);var column=chunk.Column(metric);
            for(int at=offset;at<offset+length;at++,index++)
            {
                double time=chunk.Times[at];
                if(index>start&&chunk.Breaks[at]!=0)state.Gap(time);
                if(column is null||float.IsNaN(column[at])){state.Gap(time);continue;}
                // Anchor buckets to the monotonic timeline, not the moving viewport.
                // Completed historical averages then scroll without being re-binned on each poll.
                // Floor also assigns restored negative timestamps to the correct interval.
                state.Add(Math.Floor(time/bucketSeconds),time,column[at]);
            }
        }
        state.Flush();return state.Result;
    }
    private sealed class RangeState(int capacity)
    {
        public readonly List<ChartRangePoint> Result=new(capacity);
        private int count;private double bucket=double.NaN,total,min,max,start,end;
        public void Flush()
        {
            if(count==0)return;
            Result.Add(new(start,end,total/count,min,max,count));count=0;bucket=double.NaN;
        }
        public void Gap(double time)
        {
            Flush();if(Result.Count==0||Result[^1].Average.HasValue)Result.Add(new(time,time,null,null,null,0));
        }
        public void Add(double column,double time,double value)
        {
            if(column!=bucket)Flush();
            if(count==0){bucket=column;start=time;min=max=value;total=0;}
            end=time;min=Math.Min(min,value);max=Math.Max(max,value);total+=value;count++;
        }
    }
    public Summary Summarize(string id,double now,int seconds)
    {
        lock(gate){var (start,end)=Window(now,seconds);return SummaryOf(Find(id),start,end);}
    }
    public IReadOnlyDictionary<string,Summary> Summaries(IEnumerable<string> ids,double now,int seconds)
    {
        var distinct=ids.Distinct(StringComparer.Ordinal).ToArray();
        var summaries=new Dictionary<string,Summary>(distinct.Length,StringComparer.Ordinal);
        // One sequential scan of each sensor's column, with no per-reading points or arrays. The lock is taken per sensor, so a
        // page of several hundred over six hours never holds the collector up for the whole table.
        foreach(var id in distinct)
            lock(gate){var (start,end)=Window(now,seconds);summaries[id]=SummaryOf(Find(id),start,end);}
        return summaries;
    }
    private Summary SummaryOf(int metric,int start,int end)
    {
        int samples=0;double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity,total=0;
        for(int index=start;index<end;)
        {
            var chunk=Locate(index,out int offset);
            int length=Math.Min(Size-offset,end-index);
            if(chunk.Column(metric) is { } column)
                foreach(float cell in column.AsSpan(offset,length))
                {
                    if(float.IsNaN(cell))continue;
                    double value=cell;minimum=Math.Min(minimum,value);maximum=Math.Max(maximum,value);total+=value;samples++;
                }
            index+=length;
        }
        return samples==0?new(null,null,null):new(minimum,maximum,total/samples);
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
/// <summary>A chart line kept to a few points for each pixel column, and the statistics of every reading it covers.</summary>
public readonly record struct ReducedSeries(IReadOnlyList<SamplePoint> Points,Summary Statistics);
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
    public string ConnectionsDestinationScope {get;set;}="All";
    public List<string> ExcludedApps {get;set;}=new();
    public List<string> WidgetGraphs {get;set;}=WidgetGraphSelection.Default.ToList();
    public bool WidgetAlwaysOnTop {get;set;}=true;
    public bool JoinCpuGpuPopouts {get;set;}
    public bool ShowGpuFanSpeed {get;set;}
    public bool ShowCpuPumpSpeed {get;set;}
    public bool ShowCpuCoreUsage {get;set;}
    public double PollingIntervalSeconds {get;set;}=1;
    public bool BatteryRecording {get;set;}=true;
    public bool UsageRecording {get;set;}=true;
    public List<AlertRule> Alerts {get;set;}=new(){AlertRules.DefaultFirstNetworkUse(DateTimeOffset.UtcNow)};
    public bool FirstUseRuleAdded {get;set;}=true;
    public List<string> FirstUseIgnoredApps {get;set;}=new();
    public bool AlertNotifications {get;set;}=true;
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
    public bool StorageSensors {get;set;}=true;
    public bool ProcessMonitoring {get;set;}
    public List<string> ProcessColumns {get;set;}=ProcessColumnKeys.Default.ToList();
    public int ProcessColumnsVersion {get;set;}=ProcessColumnKeys.CurrentVersion;
    public string Theme {get;set;}="Midnight";
    public string GlassLevel {get;set;}=GlassLevels.Balanced;
    public Dictionary<string,string> GraphColors {get;set;}=new();
    public Dictionary<string,GraphLineStyle> GraphStyles {get;set;}=new();
    public bool GraphSmoothing {get;set;}=true;
    public int HistorySeconds {get;set;}=60;
    public string? SelectedGpuId {get;set;}
    public Dictionary<string,WindowPlacement> Windows {get;set;}=new();
    public bool LightingEnabled {get;set;}
    public string? OpenRgbPath {get;set;}
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
            s.ConnectionsDestinationScope=ConnectionDestinations.ParseScope(s.ConnectionsDestinationScope).ToString();
            if(string.IsNullOrWhiteSpace(s.ConnectionsHomeCityId)||s.ConnectionsHomeCityId.Length>80||s.ConnectionsHomeCityId.Any(char.IsControl))s.ConnectionsHomeCityId=null;
            bool invalid=!HistoryRanges.IsSupported(s.HistorySeconds) || s.Windows is null || PollingPolicy.Normalize(s.PollingIntervalSeconds)!=s.PollingIntervalSeconds;
            var widgetGraphs=WidgetGraphSelection.Normalize(s.WidgetGraphs);
            invalid|=s.WidgetGraphs is null||!s.WidgetGraphs.SequenceEqual(widgetGraphs,StringComparer.Ordinal);
            s.WidgetGraphs=widgetGraphs;
            s.PollingIntervalSeconds=PollingPolicy.Normalize(s.PollingIntervalSeconds);
            if(!HistoryRanges.IsSupported(s.HistorySeconds)) s.HistorySeconds=60;
            if(s.OpenRgbPath is {} openRgb&&(openRgb.Length>260||openRgb.Any(char.IsControl)||!openRgb.EndsWith("OpenRGB.exe",StringComparison.OrdinalIgnoreCase)))s.OpenRgbPath=null;
            s.Windows??=new();
            using var document=JsonDocument.Parse(json);
            // Retain the pin choice even when legacy coordinates must be discarded.
            if(!document.RootElement.TryGetProperty(nameof(UserSettings.WidgetAlwaysOnTop),out _)&&
                s.Windows.TryGetValue("widget",out var widget)&&widget is not null)s.WidgetAlwaysOnTop=widget.Topmost;
            if(s.CoolingDetailedDays is not (7 or 30))s.CoolingDetailedDays=30;
            if(s.CoolingSummaryDays is not (30 or 90))s.CoolingSummaryDays=90;
            if(!Guid.TryParse(s.CoolingConfigurationId,out _))s.CoolingConfigurationId=Guid.NewGuid().ToString("N");
            s.OpenWindows=DetachedWindowState.Normalize(s.OpenWindows);
            s.ProcessColumns=ProcessColumnKeys.Normalize(s.ProcessColumns);
            // A list saved before the GPU columns upgrades once, and only while it still equals the 0.13 default.
            bool legacyColumns=!document.RootElement.TryGetProperty(nameof(UserSettings.ProcessColumnsVersion),out _)||s.ProcessColumnsVersion<ProcessColumnKeys.CurrentVersion;
            if(legacyColumns&&s.ProcessColumns.ToHashSet(StringComparer.Ordinal).SetEquals(ProcessColumnKeys.PreviousDefault))s.ProcessColumns=[..ProcessColumnKeys.Default];
            s.ProcessColumnsVersion=ProcessColumnKeys.CurrentVersion;
            s.Alerts=AlertRules.Normalize(s.Alerts);
            // The first network use rule is added once; a user who removes it keeps it removed.
            bool firstUseKnown=document.RootElement.TryGetProperty(nameof(UserSettings.FirstUseRuleAdded),out _)&&s.FirstUseRuleAdded;
            if(!firstUseKnown&&s.Alerts.Count<AlertRules.MaximumRules&&!s.Alerts.Any(a=>a.Kind==AlertKind.FirstNetworkUse))
                s.Alerts.Add(AlertRules.DefaultFirstNetworkUse(DateTimeOffset.UtcNow));
            s.FirstUseRuleAdded=true;
            s.FirstUseIgnoredApps=FirstNetworkUse.NormalizeIgnored(s.FirstUseIgnoredApps);
            s.ExcludedApps=AppExclusions.Normalize(s.ExcludedApps);
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


