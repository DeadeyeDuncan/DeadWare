using DeadWare.Core;
using System.Threading.Channels;

namespace DeadWare.App;

public sealed class GraphHistoryService
{
    private sealed record Pending(Reading Reading,long DropGeneration);
    private readonly Channel<Pending> queue=Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        {SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private readonly object lifecycle=new();
    private readonly GraphHistoryStore store;
    private readonly string machineKey;
    private readonly SampleHistory history;
    private readonly TimeProvider time;
    private readonly TimeSpan flushInterval;
    private Task? worker;
    private bool started,stopped,accepting;
    private long dropped;
    private string status="Graph history starts with monitoring.";
    public long DroppedSamples=>Interlocked.Read(ref dropped);
    public int PendingSamples=>queue.Reader.Count;
    public string Status=>Volatile.Read(ref status)+(DroppedSamples>0?$" · {DroppedSamples} samples dropped; saved history has gaps":"");

    public GraphHistoryService(string path,string machineKey,SampleHistory history,TimeProvider? timeProvider=null,TimeSpan? flushInterval=null)
    {
        store=new(path);this.machineKey=machineKey;this.history=history;time=timeProvider??TimeProvider.System;
        this.flushInterval=flushInterval??TimeSpan.FromSeconds(15);
        if(this.flushInterval<=TimeSpan.Zero||this.flushInterval>TimeSpan.FromMinutes(1))throw new ArgumentOutOfRangeException(nameof(flushInterval));
    }
    public void Start()
    {
        lock(lifecycle)
        {
            if(started||stopped)return;
            started=true;Volatile.Write(ref accepting,true);worker=Task.Run(RunAsync);
        }
    }
    /// <summary>The collector only enqueues its immutable publication; it never waits for disk.</summary>
    public void TryRecord(Reading reading)
    {
        if(!Volatile.Read(ref accepting))return;
        // Associate a loss with the first accepted publication after it. Reading a
        // global counter while draining older queued frames would consume that gap early.
        if(!queue.Writer.TryWrite(new(reading,DroppedSamples)))Interlocked.Increment(ref dropped);
    }
    public Task StopAsync()
    {
        lock(lifecycle)
        {
            stopped=true;Volatile.Write(ref accepting,false);queue.Writer.TryComplete();
            return worker??Task.CompletedTask;
        }
    }
    private async Task RunAsync()
    {
        try
        {
            store.Initialize();
            var saved=store.Read(time.GetUtcNow());
            var restoredKeys=new HashSet<string>(StringComparer.Ordinal);
            var pending=new List<GraphHistoryRecord>(120);
            Snapshot? anchor=null,previous=null;
            string segment=Guid.NewGuid().ToString("N");
            long seenDrops=0;
            bool skippedFrame=false;
            void Flush()
            {
                store.Append(pending,time.GetUtcNow());pending.Clear();
                Volatile.Write(ref status,"Graph history saved locally · last 6 hours");
            }
            void Process(Pending queued)
            {
                var reading=queued.Reading;
                var sample=reading.Sample;
                if(!double.IsFinite(sample.MonotonicSeconds)){skippedFrame=true;return;}
                // Repeated or reordered publications must not duplicate a durable sample.
                if(previous is not null&&sample.MonotonicSeconds<=previous.MonotonicSeconds)return;
                var now=time.GetUtcNow();
                if(sample.Timestamp>now||sample.Timestamp<now.AddHours(-6)){skippedFrame=true;return;}
                anchor??=sample;
                var mappings=GraphHistoryProjection.Mappings(reading,machineKey);
                var newMappings=mappings.Where(p=>!restoredKeys.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
                if(newMappings.Count>0)
                {
                    var restored=new List<Snapshot>();
                    foreach(var record in saved)
                    {
                        if(record.Timestamp>anchor.Timestamp||record.Timestamp>now||record.Timestamp<now.AddHours(-6))continue;
                        var values=new Dictionary<string,double?>(StringComparer.Ordinal);
                        foreach(var pair in newMappings)
                            if(record.Values.TryGetValue(pair.Key,out var value))values[pair.Value]=value;
                        if(values.Count==0)continue;
                        restored.Add(new(record.Timestamp,anchor.MonotonicSeconds+(record.Timestamp-anchor.Timestamp).TotalSeconds,values)
                            {SessionId=record.SessionId,PollingIntervalSeconds=record.PollingIntervalSeconds});
                    }
                    history.Restore(restored,sample.MonotonicSeconds);
                    foreach(string key in newMappings.Keys)restoredKeys.Add(key);
                }
                if(previous is not null&&(Math.Abs((sample.Timestamp-previous.Timestamp).TotalSeconds-
                    (sample.MonotonicSeconds-previous.MonotonicSeconds))>3||seenDrops!=queued.DropGeneration||skippedFrame))segment=Guid.NewGuid().ToString("N");
                previous=sample;seenDrops=queued.DropGeneration;skippedFrame=false;
                var projected=mappings.ToDictionary(p=>p.Key,p=>SampleHistory.Finite(sample.Values.GetValueOrDefault(p.Value)),StringComparer.Ordinal);
                pending.Add(new(segment,sample.Timestamp.ToUniversalTime(),sample.MonotonicSeconds,projected)
                    {PollingIntervalSeconds=sample.PollingIntervalSeconds});
                if(pending.Count==120)Flush();
            }
            Volatile.Write(ref status,"Saving graph history locally · last 6 hours");
            using var timer=new PeriodicTimer(flushInterval,time);
            Task<bool> tick=timer.WaitForNextTickAsync().AsTask();
            Task<bool> available=queue.Reader.WaitToReadAsync().AsTask();
            while(true)
            {
                await Task.WhenAny(tick,available).ConfigureAwait(false);
                if(tick.IsCompleted)
                {
                    if(!await tick.ConfigureAwait(false))break;
                    Flush();tick=timer.WaitForNextTickAsync().AsTask();
                }
                if(available.IsCompleted)
                {
                    if(!await available.ConfigureAwait(false))break;
                    // Bound work between timer checks even under synthetic publication bursts.
                    for(int i=0;i<120&&queue.Reader.TryRead(out var reading);i++)Process(reading);
                    available=queue.Reader.WaitToReadAsync().AsTask();
                }
            }
            Flush();
        }
        catch(Exception error)
        {
            Volatile.Write(ref accepting,false);queue.Writer.TryComplete();
            while(queue.Reader.TryRead(out _))Interlocked.Increment(ref dropped);
            Volatile.Write(ref status,$"Graph history unavailable ({error.GetType().Name}). Live monitoring continues; the cache was preserved. Resolve storage access and restart to retry.");
        }
    }
}
