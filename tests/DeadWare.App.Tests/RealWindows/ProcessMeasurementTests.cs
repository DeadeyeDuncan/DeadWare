using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// Real-Windows measurements. They record numbers in artifacts/measurements instead of asserting thresholds, so a number past a
/// threshold leads to a decision rather than a red build. On a run that is not elevated each writes only elevated:false and
/// passes; the one exception is the disk gate in a build that includes the Disk columns.
/// </summary>
[Collection("RealWindows")]
public sealed class ProcessMeasurementTests
{
    private const int Scans=240;
    private const int Megabyte=1<<20,TotalBytes=256*Megabyte,RandomBlock=4096,Concurrency=32,Alignment=4096;
    private const double TakeSeconds=0.5,Gate=0.90;
    private const FileOptions NoBuffering=(FileOptions)0x20000000;
    private static readonly TimeSpan WarmLimit=TimeSpan.FromSeconds(20),Tail=TimeSpan.FromSeconds(5),RandomPhase=TimeSpan.FromSeconds(30),Drain=TimeSpan.FromSeconds(1.5);
    private static readonly IReadOnlyDictionary<int,ProcessIdentity> NoIndex=new Dictionary<int,ProcessIdentity>();

    [GitHubWindowsFact] public void Scan_timing_over_240_scans()
    {
        if(!MeasurementRecorder.Elevated) {MeasurementRecorder.Write("scan-timing",new{elevated=false});return;}
        var source=new WindowsProcesses();
        var milliseconds=new double[Scans];
        ProcessScan? last=null;
        int failures=0;
        for(int i=0;i<Scans;i++)
        {
            long started=Stopwatch.GetTimestamp();
            var scan=source.Scan(started/(double)Stopwatch.Frequency,1);
            milliseconds[i]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if(scan is null)failures++;
            else last=scan;
        }
        Array.Sort(milliseconds);
        MeasurementRecorder.Write("scan-timing",new{elevated=true,scans=Scans,p50Ms=Percentile(milliseconds,0.50),p95Ms=Percentile(milliseconds,0.95),bufferBytes=last?.BufferBytes,processes=last?.Processes.Count,threads=last?.Threads,failures});
    }
    // Nearest rank in an ascending array, in milliseconds to the microsecond.
    private static double Percentile(double[] sorted,double fraction)=>Math.Round(sorted[Math.Clamp((int)Math.Ceiling(fraction*sorted.Length)-1,0,sorted.Length-1)],3);

    [GitHubWindowsFact] public async Task Disk_attribution_and_event_loss()
    {
        if(!MeasurementRecorder.Elevated)
        {
            MeasurementRecorder.Write("disk-attribution",new{elevated=false});
            Assert.False(ProcessFeatures.DiskColumns,"A build with the Disk columns needs an elevated run to prove the disk gate.");
            return;
        }
        var runs=new List<SizeRun>();
        foreach(int factor in new[]{1,2,4})
        {
            var run=await MeasureAsync(EtwDisk.DefaultBufferSizeMB*factor);
            runs.Add(run);
            if(run.LostNothing)break;
        }
        var chosen=runs.FirstOrDefault(r=>r.LostNothing);
        int chosenSize=chosen?.BufferSizeMB??EtwDisk.DefaultBufferSizeMB*4;
        MeasurementRecorder.Write("disk-attribution",new{elevated=true,sizes=runs.Select(r=>r.Values).ToArray(),chosenBufferSizeMB=chosenSize,lossKnownLimit=chosen is null,diskColumns=ProcessFeatures.DiskColumns});
        if(!ProcessFeatures.DiskColumns)return;
        double? share=runs.FirstOrDefault(r=>r.BufferSizeMB==chosenSize)?.Share;
        Assert.True(share>=Gate,$"Disk gate: {share?.ToString("P1")??"no share"} of {TotalBytes:N0} bytes credited to this process at {chosenSize} MB; at least {Gate:P0} is required.");
    }

    /// <summary>
    /// One buffer size: an EtwNetwork and an EtwDisk session run side by side and are taken every 0.5 s while this process writes
    /// 256 MB unbuffered and write-through, then makes 30 s of 4 KiB unbuffered random writes from 32 concurrent requests.
    /// Nothing is reported from a trace that did not stay Running (a loss-storm pause counts as running).
    /// </summary>
    private static async Task<SizeRun> MeasureAsync(int bufferSizeMB)
    {
        var own=NativeProcessIdentity.Read(Environment.ProcessId);
        var existing=TraceEventSession.GetActiveSessionNames().ToHashSet(StringComparer.Ordinal);
        var network=new EtwNetwork();
        var disk=new EtwDisk(bufferSizeMB);
        string path=Path.Combine(Path.GetTempPath(),$"deadware-disk-{Guid.NewGuid():N}.bin");
        TraceEventSession? lossReader=null;
        Taker? taker=null;
        try
        {
            network.Start();disk.Start();
            lossReader=AttachLossReader($"DeadWare-Disk-{Environment.ProcessId}-",existing);
            taker=new Taker(network,disk,lossReader);
            // The first take shows whether the session opened; the write starts only once the window is warm, so the
            // takes it is measured against all come before it.
            var ready=await taker.WaitAsync(t=>t.Take.State!=DiskTraceState.Running||t.Take.Warm,WarmLimit);
            if(ready is null||ready.Take.State!=DiskTraceState.Running)
            {
                taker.Dispose();
                return SizeRun.NotRunning(bufferSizeMB,ready?.Take.Status??taker.Failure??"No disk take was recorded");
            }
            using var buffer=new AlignedBuffer(Megabyte,Alignment);
            taker.Phase=Phase.Write;
            WriteSequential(path,buffer);
            // Real-time buffers flush about every second and the rate window spans 2 s, so after the tail every write is
            // credited and the last takes of the phase see no more of it.
            await Task.Delay(Tail);
            taker.Phase=Phase.Random;
            await RandomWritesAsync(path,buffer);
            await Task.Delay(Drain);
            taker.Dispose();
            return Summarize(bufferSizeMB,taker,own);
        }
        catch(Exception e)
        {
            return SizeRun.Failed(bufferSizeMB,e);
        }
        finally
        {
            taker?.Dispose();
            lossReader?.Dispose();
            await disk.DisposeAsync();
            await network.DisposeAsync();
            try {File.Delete(path);}
            catch(IOException) {}
        }
    }

    private static SizeRun Summarize(int bufferSizeMB,Taker taker,ProcessIdentity? own)
    {
        var taken=taker.Taken;
        if(taker.Failure is {} failure)return SizeRun.NotRunning(bufferSizeMB,failure);
        var stopped=taken.FirstOrDefault(t=>t.Take.State is not (DiskTraceState.Running or DiskTraceState.Paused));
        if(taken.Count==0||stopped is not null)return SizeRun.NotRunning(bufferSizeMB,stopped?.Take.Status??"No disk take was recorded");
        var write=taken.Where(t=>t.Phase==Phase.Write).ToList();
        // `now` advances exactly 0.5 per take, so each warm take's rate covers the 2.0 s since the take four before it, and
        // rate × 0.5 summed over the phase telescopes to the bytes credited between the takes before and after the write.
        // Lossy, paused and cold takes publish no rates, so whatever a loss reset is not credited.
        double? share=own is null?null:Math.Round(write.Sum(t=>Written(t.Take,own))*TakeSeconds/TotalBytes,4);
        // The join hides the Disk columns when more than half of a window's events are unattributed, so the worst window counts.
        double unattributed=Math.Round(write.Where(t=>t.Take.State==DiskTraceState.Running&&t.Take.Warm).Select(t=>t.Take.UnattributedShare).DefaultIfEmpty(0).Max(),4);
        long? lost=taken.Max(t=>t.EventsLost);
        int longest=0,run=0;
        bool paused=false;
        foreach(var t in taken)
        {
            // EtwDisk answers Paused on the tenth consecutive lossy take; after that it has stopped and reports nothing.
            if(t.Take.State==DiskTraceState.Paused) {if(!paused)longest=Math.Max(longest,++run);paused=true;}
            else if(t.Take.Lossy)longest=Math.Max(longest,++run);
            else run=0;
        }
        return new(bufferSizeMB,true,taken[^1].Take.Status,share,unattributed,lost,longest,paused,null);
    }
    private static double Written(DiskTake take,ProcessIdentity own)=>take.Rates.FirstOrDefault(r=>r.Pid==own.Pid&&r.Generation==own.Generation)?.Write??0;

    // EtwDisk keeps its session to itself, so its loss counter is read through a second handle on the same session.
    private static TraceEventSession? AttachLossReader(string prefix,HashSet<string> existing)
    {
        try
        {
            string? name=TraceEventSession.GetActiveSessionNames().FirstOrDefault(n=>n.StartsWith(prefix,StringComparison.Ordinal)&&!existing.Contains(n));
            if(name is null)return null;
            var session=TraceEventSession.GetActiveSession(name);
            // Disposing this handle must never stop EtwDisk's session.
            if(session is not null)session.StopOnDispose=false;
            return session;
        }
        catch(Exception) {return null;}
    }

    private static void WriteSequential(string path,AlignedBuffer buffer)
    {
        using var file=File.OpenHandle(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,NoBuffering|FileOptions.WriteThrough);
        for(long offset=0;offset<TotalBytes;offset+=Megabyte)RandomAccess.Write(file,buffer.GetSpan(),offset);
    }
    // Each request owns a 4 KiB, 4 KiB-aligned slice of the buffer and writes it to random 4 KiB-aligned offsets.
    private static async Task RandomWritesAsync(string path,AlignedBuffer buffer)
    {
        using var file=File.OpenHandle(path,FileMode.Open,FileAccess.Write,FileShare.None,NoBuffering|FileOptions.Asynchronous);
        const long blocks=TotalBytes/RandomBlock;
        long end=Stopwatch.GetTimestamp()+(long)(RandomPhase.TotalSeconds*Stopwatch.Frequency);
        var requests=new Task[Concurrency];
        for(int i=0;i<Concurrency;i++)
        {
            var block=buffer.Memory.Slice(i*RandomBlock,RandomBlock);
            var random=new Random(i);
            requests[i]=Task.Run(async()=>{while(Stopwatch.GetTimestamp()<end)await RandomAccess.WriteAsync(file,block,random.NextInt64(blocks)*RandomBlock);});
        }
        await Task.WhenAll(requests);
    }

    private enum Phase{Warm,Write,Random}
    private sealed record Taken(Phase Phase,DiskTake Take,long? EventsLost);

    /// <summary>Takes both sessions every 0.5 s on its own thread, as the poll loop would, and keeps every disk take.</summary>
    private sealed class Taker:IDisposable
    {
        private readonly EtwNetwork network;
        private readonly EtwDisk disk;
        private readonly TraceEventSession? lossReader;
        private readonly Thread thread;
        private readonly object gate=new();
        private readonly List<Taken> taken=[];
        private volatile bool stopping;
        private volatile Phase phase=Phase.Warm;
        private volatile string? failure;
        public Taker(EtwNetwork network,EtwDisk disk,TraceEventSession? lossReader)
        {
            this.network=network;this.disk=disk;this.lossReader=lossReader;
            thread=new Thread(Run) {IsBackground=true,Name="Disk measurement takes"};
            thread.Start();
        }
        public Phase Phase {set=>phase=value;}
        /// <summary>Set when a take threw; the takes after it were not made.</summary>
        public string? Failure=>failure;
        public IReadOnlyList<Taken> Taken {get{lock(gate)return taken.ToArray();}}
        public async Task<Taken?> WaitAsync(Func<Taken,bool> condition,TimeSpan limit)
        {
            var clock=Stopwatch.StartNew();
            while(true)
            {
                Taken? latest;
                lock(gate)latest=taken.Count>0?taken[^1]:null;
                if(latest is not null&&condition(latest)||failure is not null||clock.Elapsed>=limit)return latest;
                await Task.Delay(100);
            }
        }
        private void Run()
        {
            // An exception escaping a dedicated thread would end the test process, so it is recorded instead.
            try
            {
                long origin=Stopwatch.GetTimestamp();
                for(int index=1;!stopping;index++)
                {
                    double now=index*TakeSeconds;
                    network.Take(now);
                    var take=disk.Take(now,NoIndex);
                    long? lost=ReadLoss();
                    // Read after the take: a take marked Warm finished before the write began, one marked Write before the random writes.
                    lock(gate)taken.Add(new(phase,take,lost));
                    var wait=TimeSpan.FromSeconds(index*TakeSeconds)-Stopwatch.GetElapsedTime(origin);
                    if(wait>TimeSpan.Zero)Thread.Sleep(wait);
                }
            }
            catch(Exception e) {failure=$"Disk take failed ({e.GetType().Name})";}
        }
        private long? ReadLoss()
        {
            try {return lossReader?.EventsLost;}
            // The session is gone once a loss storm has stopped it.
            catch(Exception) {return null;}
        }
        public void Dispose() {stopping=true;thread.Join();}
    }

    private sealed record SizeRun(int BufferSizeMB,bool Running,string Status,double? Share,double? UnattributedShare,long? EventsLost,int? LongestLossyRun,bool Paused,string? Error)
    {
        public static SizeRun NotRunning(int bufferSizeMB,string status)=>new(bufferSizeMB,false,status,null,null,null,null,false,null);
        public static SizeRun Failed(int bufferSizeMB,Exception e)=>new(bufferSizeMB,false,"",null,null,null,null,false,$"{e.GetType().Name}: {e.Message}");
        public bool LostNothing=>Running&&Error is null&&EventsLost==0&&LongestLossyRun==0&&!Paused;
        public object Values=>new{bufferSizeMB=BufferSizeMB,running=Running,status=Status,share=Share,unattributedShare=UnattributedShare,eventsLost=EventsLost,longestLossyRun=LongestLossyRun,paused=Paused,error=Error};
    }

    /// <summary>Sector-aligned native memory, as FILE_FLAG_NO_BUFFERING requires of every transfer buffer.</summary>
    private sealed unsafe class AlignedBuffer:MemoryManager<byte>
    {
        private readonly byte* pointer;
        private readonly int length;
        private int freed;
        public AlignedBuffer(int length,int alignment)
        {
            this.length=length;
            pointer=(byte*)NativeMemory.AlignedAlloc((nuint)length,(nuint)alignment);
            GetSpan().Fill(0x5A);
        }
        public override Span<byte> GetSpan()=>new(pointer,length);
        // Native memory never moves, so pinning costs nothing.
        public override MemoryHandle Pin(int elementIndex=0)=>new(pointer+elementIndex);
        public override void Unpin() {}
        protected override void Dispose(bool disposing) {if(Interlocked.Exchange(ref freed,1)==0)NativeMemory.AlignedFree(pointer);}
    }
}
