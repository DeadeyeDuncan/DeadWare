using System.IO;
using System.Net;
using System.Threading.Channels;
using DeadWare.Core;
namespace DeadWare.App;

internal sealed record ConnectionServiceOptions(int QueueCapacity=120,int MaximumBufferedDeltas=32768,int MaximumLocations=8192,TimeSpan? FlushInterval=null,TimeSpan? EnrichmentBudget=null);

/// <summary>Shared immutable live publication, independent bounded enrichment and durable writer.</summary>
public sealed class ConnectionService
{
    public static readonly TimeSpan DefaultFlushInterval=TimeSpan.FromSeconds(15);
    private readonly ConnectionDatabaseDownload downloader=new();
    private readonly ConnectionHistoryStore? store;
    private Task? initialization;
    public string DatabasePath {get;}="";
    public ConnectionService(string path,string? databasePath=null)
        :this(path,new ConnectionGeolocation(databasePath??Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"dbip-city-lite.mmdb")),()=>DateTimeOffset.UtcNow)
    {DatabasePath=databasePath??Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,"dbip-city-lite.mmdb");}
    internal ConnectionService(string path,IConnectionGeolocation resolver,Func<DateTimeOffset> clock,ConnectionServiceOptions? options=null)
        :this(resolver,clock,(_,_)=>{},options)
    {
        store=new(path);append=(records,coverage,now)=>store.Append(records,coverage,now);
    }
    private Task InitializeAsync()
    {
        lock(gate)return initialization??=store is null?Task.CompletedTask:Task.Run(()=>
        {
            store.Initialize();store.Append([],[],clock());
            lock(gate)storageStatus="Connection history saved locally · rolling 30 days.";
        });
    }
    public async Task<ConnectionHistoryView> ReadViewAsync(ConnectionHistoryQuery query)
    {
        await InitializeAsync().ConfigureAwait(false);
        return await Task.Run(()=>store!.Read(query)).ConfigureAwait(false);
    }
    public async Task ExportCsvAsync(string path,ConnectionHistoryQuery query)
    {
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(()=>
        {
            using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false));
            store!.ExportCsv(writer,query);
        }).ConfigureAwait(false);
    }
    public async Task UpdateLocationDatabaseAsync(IProgress<double>? progress=null,CancellationToken cancellationToken=default)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,finishing.Token);
        await downloader.UpdateAsync(DatabasePath,progress,linked.Token).ConfigureAwait(false);
        await ReloadGeolocationAsync().WaitAsync(linked.Token).ConfigureAwait(false);
    }
    private sealed record Pending(ConnectionSample Sample,long Generation,GeoPoint? Origin,bool Complete,DateTimeOffset? Since);
    private sealed record LookupRequest(string? Address,TaskCompletionSource<ConnectionLocation?> Completion);
    private readonly object gate=new();
    private readonly Channel<Pending> queue;
    private readonly Channel<LookupRequest> lookups;
    private readonly Dictionary<string,ConnectionLocation?> locations=new(StringComparer.Ordinal);
    private readonly Dictionary<string,TaskCompletionSource<ConnectionLocation?>> resolving=new(StringComparer.Ordinal);
    private readonly Queue<string> locationOrder=new();
    private readonly IConnectionGeolocation resolver;
    private readonly Func<DateTimeOffset> clock;
    private readonly Action<IReadOnlyList<ConnectionHistoryRecord>,IReadOnlyList<ConnectionCoverage>,DateTimeOffset> append;
    private readonly ConnectionServiceOptions options;
    private readonly CancellationTokenSource finishing=new();
    private Task? writer,lookupWorker,stop,reload;
    private bool accepting,stopped,recording=true;
    private long generation,previousGeneration,lastSequence=-1,dropped,dropVersion,previousDropVersion;
    private int buffered,bufferedPublications;
    private DateTimeOffset? resumed,lastCaptureTimestamp;
    private GeoPoint? origin;
    private double freshnessSeconds=5;
    private string storageStatus="Connection history starts with monitoring.",lookupStatus="Location lookup has not started.";
    private DateTimeOffset? databaseDate;
    private ConnectionLiveView live=new(default,[],null,false,false,"Waiting for endpoint capture.");

    internal ConnectionService(IConnectionGeolocation resolver,Func<DateTimeOffset> clock,
        Action<IReadOnlyList<ConnectionHistoryRecord>,IReadOnlyList<ConnectionCoverage>> append,ConnectionServiceOptions? options=null)
    {
        this.resolver=resolver;this.clock=clock;this.append=(records,coverage,_)=>append(records,coverage);this.options=options??new();
        if(this.options.QueueCapacity is <=0 or >120||this.options.MaximumBufferedDeltas is <=0 or >32768||this.options.MaximumLocations is <=0 or >8192||
            this.options.FlushInterval is {TotalMilliseconds:<=0}||this.options.EnrichmentBudget is {TotalMilliseconds:<0})throw new ArgumentOutOfRangeException(nameof(options));
        queue=Channel.CreateBounded<Pending>(new BoundedChannelOptions(this.options.QueueCapacity){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        lookups=Channel.CreateBounded<LookupRequest>(new BoundedChannelOptions(this.options.MaximumLocations){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    }
    public ConnectionLiveView Live
    {
        get
        {
            lock(gate)
            {
                if(live.Available&&(clock()-live.Timestamp).TotalSeconds>freshnessSeconds)
                    return live with {Available=false,Complete=false,Status="Endpoint capture is stale; waiting for a fresh sample. "+live.Status};
                return live;
            }
        }
    }
    public long DroppedObservations {get {lock(gate)return dropped;}}
    public int PendingPublications=>queue.Reader.Count;
    public int BufferedDeltas {get {lock(gate)return buffered;}}
    public string StorageStatus {get {lock(gate)return (recording?storageStatus:"Recording paused. Saved history is retained. "+storageStatus)+(dropped>0?$" · {dropped:N0} observations dropped; history has gaps.":"");}}
    public string LookupStatus {get {lock(gate)return lookupStatus;}}
    public DateTimeOffset? DatabaseDate {get {lock(gate)return databaseDate;}}
    public void Configure(bool recording,GeoPoint? origin)
    {
        if(origin is not null&&(!double.IsFinite(origin.Latitude)||!double.IsFinite(origin.Longitude)||Math.Abs(origin.Latitude)>90||Math.Abs(origin.Longitude)>180))origin=null;
        lock(gate)
        {
            if(this.recording!=recording){generation++;if(recording)resumed=clock().ToUniversalTime();}
            this.recording=recording;this.origin=origin;live=live with {Origin=origin};
        }
    }
    public void Start()
    {
        lock(gate)
        {
            if(stopped||writer is not null)return;
            accepting=true;lookupWorker=Task.Run(LookupLoopAsync);writer=Task.Run(WriteLoopAsync);
        }
    }
    public void TryRecord(Reading reading)
    {
        if(reading.Connections is not { } sample)return;
        lock(gate)freshnessSeconds=PollingPolicy.FreshnessSeconds(reading.Sample.PollingIntervalSeconds);
        TryRecord(sample);
    }
    public void TryRecord(ConnectionSample sample)
    {
        lock(gate)
        {
            if(!accepting||sample.Sequence<=lastSequence)return;
            lastSequence=sample.Sequence;
            // A ledger clock break starts a new time domain. The old pause boundary cannot
            // reject every fresh event until a backwards-adjusted wall clock catches up.
            if(lastCaptureTimestamp is { } previous&&sample.Timestamp<previous&&sample.CoverageFrom is null&&!sample.Complete)resumed=null;
            lastCaptureTimestamp=sample.Timestamp;
            string captureStatus=sample.Status;
            if(sample.DroppedEvents>0||sample.LostEvents>0)captureStatus+=$" · {sample.DroppedEvents:N0} dropped · {sample.LostEvents:N0} lost observations";
            live=new(sample.Timestamp,Array.AsReadOnly(sample.Live.Select(t=>new LocatedConnection(t,locations.GetValueOrDefault(t.Peer.RemoteAddress))).ToArray()),origin,sample.Available,sample.Complete,captureStatus);
            foreach(var traffic in sample.Live)RequestLookupLocked(traffic.Peer.RemoteAddress);
            if(!recording)return;
            int count=sample.Deltas.Count;
            var pending=new Pending(sample,generation,origin,sample.Complete&&generation==previousGeneration&&dropVersion==previousDropVersion,resumed);
            if(count>options.MaximumBufferedDeltas-buffered||bufferedPublications>=options.QueueCapacity*2L||!queue.Writer.TryWrite(pending)) {DropLocked(Math.Max(1,count));return;}
            buffered+=count;bufferedPublications++;previousGeneration=generation;previousDropVersion=dropVersion;
        }
    }
    private void DropLocked(long count) {dropped=count>long.MaxValue-dropped?long.MaxValue:dropped+count;dropVersion++;}
    private Task<ConnectionLocation?>? RequestLookupLocked(string address)
    {
        if(locations.ContainsKey(address)||!IPAddress.TryParse(address,out var ip)||NetworkAddress.Classify(ip)!=NetworkAddressKind.Public)return null;
        if(resolving.TryGetValue(address,out var existing))return existing.Task;
        if(finishing.IsCancellationRequested||resolving.Count>=options.MaximumLocations)return null;
        var completion=new TaskCompletionSource<ConnectionLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!lookups.Writer.TryWrite(new(address,completion)))return null;
        resolving[address]=completion;return completion.Task;
    }
    private void RefreshLookupStatus()
    {
        if(resolver is ConnectionGeolocation geo)
        {
            string status=geo.Status;var date=geo.DatabaseDate;
            lock(gate){lookupStatus=status;databaseDate=date;}
        }
    }
    private async Task LookupLoopAsync()
    {
        try
        {
            // The first load must never take the worker down: an unreadable file leaves lookups answering null until a reload.
            try {if(resolver is ConnectionGeolocation geo)geo.Reload();}
            catch(Exception error){lock(gate)lookupStatus=$"Location lookup unavailable ({error.GetType().Name}); traffic remains available.";}
            RefreshLookupStatus();
            await foreach(var request in lookups.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if(finishing.IsCancellationRequested){request.Completion.TrySetResult(null);continue;}
                ConnectionLocation? location=null;
                try
                {
                    if(request.Address is null)
                    {
                        if(resolver is ConnectionGeolocation reloaded)reloaded.Reload();
                        lock(gate){locations.Clear();locationOrder.Clear();}
                        RefreshLookupStatus();
                    }
                    else location=resolver.Lookup(request.Address);
                }
                catch(Exception error){lock(gate)lookupStatus=$"Location lookup unavailable ({error.GetType().Name}); traffic remains available.";}
                lock(gate)
                {
                    if(request.Address is { } address)
                    {
                        resolving.Remove(address);
                        if(!locations.ContainsKey(address)){if(locations.Count>=options.MaximumLocations)locations.Remove(locationOrder.Dequeue());locationOrder.Enqueue(address);}
                        locations[address]=location;
                    }
                    // Refresh cached geography only; never replace traffic with history-query results.
                    if(lookups.Reader.Count==0)
                        live=live with {Connections=Array.AsReadOnly(live.Connections.Select(c=>c with {Location=locations.GetValueOrDefault(c.Traffic.Peer.RemoteAddress)}).ToArray())};
                }
                request.Completion.TrySetResult(location);
            }
        }
        finally
        {
            lock(gate){foreach(var pending in resolving.Values)pending.TrySetResult(null);resolving.Clear();}
            (resolver as IDisposable)?.Dispose();
        }
    }
    public Task ReloadGeolocationAsync()
    {
        lock(gate)
        {
            if(stopped)return Task.CompletedTask;
            if(reload is {IsCompleted:false})return reload;
            var completion=new TaskCompletionSource<ConnectionLocation?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!lookups.Writer.TryWrite(new(null,completion)))return Task.FromException(new InvalidOperationException("Location lookup queue is busy. Retry after current lookups finish."));
            return reload=completion.Task;
        }
    }
    private async Task EnrichAsync(ConnectionSample sample)
    {
        var tasks=new List<Task<ConnectionLocation?>>();
        lock(gate)foreach(var delta in sample.Deltas)
            if(RequestLookupLocked(delta.Peer.RemoteAddress) is { } task)tasks.Add(task);
        if(tasks.Count==0||finishing.IsCancellationRequested)return;
        try {await Task.WhenAll(tasks).WaitAsync(options.EnrichmentBudget??TimeSpan.FromMilliseconds(250),finishing.Token).ConfigureAwait(false);}
        catch(TimeoutException) { }
        catch(OperationCanceledException) when(finishing.IsCancellationRequested) { }
    }
    private async Task WriteLoopAsync()
    {
        try {await InitializeAsync().ConfigureAwait(false);}
        catch(Exception error) {lock(gate)storageStatus=$"Connection history unavailable ({error.GetType().Name}). Live capture continues.";}
        ConnectionMinuteFolder folder;
        lock(gate)folder=new(locations.TryGetValue,Math.Max(1,options.MaximumBufferedDeltas/2));
        // Every folder call runs under the gate, because the lookup worker changes the location cache under it.
        // buffered stays the queued deltas plus the folder's open (Held) and frozen (Waiting) sizes.
        T Fold<T>(Func<T> call)
        {
            int before=folder.Held+folder.Waiting;
            var result=call();
            buffered+=folder.Held+folder.Waiting-before;
            return result;
        }
        // One write attempt. Units are immutable once frozen, so a retry resends exactly the same payload under the same BatchId.
        void Write(bool final=false,bool maintenance=false)
        {
            while(true)
            {
                var now=clock();
                ConnectionSendable sendable;
                lock(gate)
                {
                    sendable=Fold(()=>folder.Sendable(now,final));
                    foreach(var unit in sendable.Retired)DropLocked(Math.Max(1,unit.Observations));
                }
                // A maintenance tick still appends with nothing to send, so expiry and Cleanup run.
                if(sendable.Send.Count==0&&!maintenance)return;
                maintenance=false;
                var records=sendable.Send.SelectMany(u=>u.Records).ToArray();var coverage=sendable.Send.SelectMany(u=>u.Coverage).ToArray();
                try
                {
                    append(Array.AsReadOnly(records),Array.AsReadOnly(coverage),now);
                    lock(gate){storageStatus="Connection history saved locally · rolling 30 days.";Fold(()=>{folder.Sent(sendable.Send);return 0;});}
                }
                catch(Exception error)
                {
                    lock(gate)
                    {
                        storageStatus=$"Connection history unavailable ({error.GetType().Name}). Live capture continues.";
                        // Not final: every unit waits unchanged for the next attempt.
                        if(final)
                        {
                            var unsent=Fold(folder.DropWaiting);
                            DropLocked(Math.Max(1,unsent.Sum(u=>u.Observations)));
                        }
                    }
                    return;
                }
                if(!final||sendable.Send.Count==0)return;
            }
        }
        async Task Process(Pending item)
        {
            await EnrichAsync(item.Sample).ConfigureAwait(false);
            bool complete=item.Complete&&item.Sample.Available;
            var accepted=new List<ConnectionDelta>();
            foreach(var delta in item.Sample.Deltas)
            {
                // Never shift skewed/paused observations into a fabricated publication-time minute.
                if((item.Since.HasValue&&delta.FirstSeen<item.Since)||delta.FirstSeen<item.Sample.Timestamp.AddSeconds(-60)||delta.LastSeen>item.Sample.Timestamp.AddSeconds(2))
                {lock(gate)DropLocked(1);complete=false;continue;}
                accepted.Add(delta);
            }
            int frozen;
            lock(gate)
            {
                frozen=Fold(()=>folder.Merge(item.Sample,item.Generation,item.Origin,complete,accepted));
                buffered-=item.Sample.Deltas.Count;bufferedPublications--;
            }
            if(frozen>0)Write();
        }
        using var timer=new PeriodicTimer(options.FlushInterval??DefaultFlushInterval);
        Task<bool> tick=timer.WaitForNextTickAsync().AsTask(),available=queue.Reader.WaitToReadAsync().AsTask();
        while(true)
        {
            await Task.WhenAny(tick,available).ConfigureAwait(false);
            if(tick.IsCompleted)
            {
                if(!await tick.ConfigureAwait(false))break;
                var now=clock();
                lock(gate)Fold(()=>folder.Tick(now,generation));
                Write(maintenance:true);tick=timer.WaitForNextTickAsync().AsTask();
            }
            if(available.IsCompleted)
            {
                if(!await available.ConfigureAwait(false))break;
                while(queue.Reader.TryRead(out var item))
                {
                    await Process(item).ConfigureAwait(false);
                    if(tick.IsCompleted)break;
                }
                available=queue.Reader.WaitToReadAsync().AsTask();
            }
        }
        // R6: the queue is complete and drained, so every open unit is final.
        lock(gate)Fold(folder.FreezeAll);
        Write(final:true);
    }
    public Task StopAsync()
    {
        lock(gate)
        {
            if(stop is not null)return stop;
            stopped=true;accepting=false;finishing.Cancel();queue.Writer.TryComplete();lookups.Writer.TryComplete();
            // Writer drains every accepted publication. A blocked external resolver owns its own
            // eventual disposal on the lookup worker; shutdown never waits for enrichment.
            return stop=writer??Task.CompletedTask;
        }
    }
}
