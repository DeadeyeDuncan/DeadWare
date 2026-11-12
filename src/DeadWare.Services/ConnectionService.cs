using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Channels;
using DeadWare.Core;
namespace DeadWare.App;

/// <summary>One Append of the writer: the store's, or a test's wrapper around it. <paramref name="maintenanceTransactions"/> bounds routine cleanup.</summary>
internal delegate void ConnectionAppend(IReadOnlyList<ConnectionHistoryRecord> records,IReadOnlyList<ConnectionCoverage> coverage,DateTimeOffset now,int maintenanceTransactions);
/// <summary>A write attempt's result: Saved is false exactly when an Append it called threw (FailureType is then its type name). Held units do not make it false.</summary>
internal sealed record ConnectionFlushResult(bool Saved,string? FailureType);
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
        :this(path,resolver,clock,static append=>append,options) { }
    /// <summary>A real store at <paramref name="maximumDatabaseBytes"/> whose write attempts go through <c>wrapAppend(store.Append)</c> (tests).</summary>
    internal ConnectionService(string path,IConnectionGeolocation resolver,Func<DateTimeOffset> clock,Func<ConnectionAppend,ConnectionAppend> wrapAppend,ConnectionServiceOptions? options=null,long maximumDatabaseBytes=ConnectionHistoryStore.MaximumDatabaseBytes)
        :this(resolver,clock,(_,_)=>{},options)
    {
        ArgumentNullException.ThrowIfNull(wrapAppend);
        store=new(path,maximumDatabaseBytes);append=wrapAppend(store.Append);
    }
    /// <summary>The bound on routine cleanup transactions for every Append except the final write's (0).</summary>
    internal const int MaintenanceTransactions=64;
    /// <summary>A test seam: receives each Append's maintenance bound, InitializeAsync's included.</summary>
    internal Action<int>? maintenanceObserved;
    private Task InitializeAsync()
    {
        lock(gate)return initialization??=store is null?Task.CompletedTask:Task.Run(()=>
        {
            store.Initialize();maintenanceObserved?.Invoke(MaintenanceTransactions);store.Append([],[],clock(),MaintenanceTransactions);
            lock(gate)storageStatus="Connection history saved locally · rolling 30 days.";
        });
    }
    public async Task<ConnectionHistoryView> ReadViewAsync(ConnectionHistoryQuery query)
    {
        await InitializeAsync().ConfigureAwait(false);
        return await Task.Run(()=>store!.Read(query)).ConfigureAwait(false);
    }
    /// <summary>Every saved application name for the first-use seed, or null on any failure or while a clear is in progress.</summary>
    public async Task<IReadOnlyList<ApplicationSeen>?> ReadApplicationNamesAsync()
    {
        try
        {
            await InitializeAsync().ConfigureAwait(false);
            return await Task.Run(()=>store?.ReadApplicationNames()).ConfigureAwait(false);
        }
        catch(Exception){return null;}
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
    /// <summary>Writes every destination in <paramref name="scope"/> over the whole interval to a new file, off the UI thread, in UTF-8 without a BOM.</summary>
    public async Task ExportDestinationsCsvAsync(string path,ConnectionHistoryQuery query,DestinationScope scope)
    {
        await InitializeAsync().ConfigureAwait(false);
        await Task.Run(()=>
        {
            using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            using var writer=new StreamWriter(stream,new System.Text.UTF8Encoding(false));
            store!.ExportDestinationsCsv(writer,query,scope);
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
    private readonly ConnectionAppend append;
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
    private IReadOnlySet<string> exclusions=System.Collections.Frozen.FrozenSet<string>.Empty;
    private readonly HistoryClearCommand clears=new();
    private string? initializationFailure;

    internal ConnectionService(IConnectionGeolocation resolver,Func<DateTimeOffset> clock,
        Action<IReadOnlyList<ConnectionHistoryRecord>,IReadOnlyList<ConnectionCoverage>> append,ConnectionServiceOptions? options=null)
    {
        this.resolver=resolver;this.clock=clock;this.append=(records,coverage,_,_)=>append(records,coverage);this.options=options??new();
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
    public string StorageStatus {get {lock(gate)return (recording?storageStatus:"Recording paused. Saved history is retained. "+storageStatus)+(dropped>0?$" · {dropped:N0} observations dropped; history has gaps.":"")+HistoryClearCopy.StatusSuffix(clears.State,false);}}
    /// <summary>This store's clear. The internal setter is a test seam; the app never calls it.</summary>
    public HistoryClearState ClearState {get=>clears.State;internal set=>clears.State=value;}
    /// <summary>Puts the request in the writer's command channel (capacity 1) and sets Queued. Refused while a clear is active.</summary>
    public void Clear(HistoryClear request)=>clears.Post(request);
    /// <summary>Re-posts the kept request while Failed and the writer runs: the full order without a saved marker, otherwise a resume
    /// that repeats nothing already done (BeginClear re-commits nothing, no Forget, the boundary only while Before is ahead).</summary>
    public void RetryClear()=>clears.Retry(WriterRunning);
    public bool CanRetryClear=>clears.CanRetry(WriterRunning);
    /// <summary>A test seam that replaces the store's step.</summary>
    internal Func<TimeSpan,HistoryClearProgress>? ClearStepOverride {get=>clears.StepOverride;set=>clears.StepOverride=value;}
    /// <summary>The store's clear marker: set from BeginClear, or from Initialize while a marker exists, until the last step.</summary>
    internal PendingConnectionClear? PendingClear=>store?.PendingClear;
    private bool WriterRunning {get {lock(gate)return !stopped&&writer is {IsCompleted:false};}}
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
    /// <summary>The Don't record keys (<see cref="AppKey.Of"/>, ordinal). Read by the writer once per publication at merge time only:
    /// the live view, lookups, coverage and anything already merged or frozen are unaffected.</summary>
    public void SetExclusions(IReadOnlySet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Volatile.Write(ref exclusions,keys);
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
        catch(Exception error) {lock(gate){storageStatus=$"Connection history unavailable ({error.GetType().Name}). Live capture continues.";initializationFailure=error.GetType().Name;}}
        ConnectionMinuteFolder folder;
        lock(gate)folder=new(locations.TryGetValue,Math.Max(1,options.MaximumBufferedDeltas/2));
        // Every folder call runs under the gate, because the lookup worker changes the location cache under it.
        // buffered stays the queued deltas plus the folder's open (Held) and frozen (Waiting) sizes.
        T Fold<T>(Func<T> call)
        {
            Debug.Assert(Monitor.IsEntered(gate));
            int before=folder.Held+folder.Waiting;
            var result=call();
            buffered+=folder.Held+folder.Waiting-before;
            return result;
        }
        // One write attempt (drain: until nothing sendable is left). Units are immutable once frozen, so a retry resends exactly the
        // same payload under the same BatchId. The final write never purges.
        ConnectionFlushResult Write(bool final=false,bool maintenance=false,bool drain=false)
        {
            int bound=final?0:MaintenanceTransactions;
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
                if(sendable.Send.Count==0&&!maintenance)return new(true,null);
                maintenance=false;
                var records=sendable.Send.SelectMany(u=>u.Records).ToArray();var coverage=sendable.Send.SelectMany(u=>u.Coverage).ToArray();
                try
                {
                    maintenanceObserved?.Invoke(bound);
                    append(Array.AsReadOnly(records),Array.AsReadOnly(coverage),now,bound);
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
                    return new(false,error.GetType().Name);
                }
                if(!(final||drain)||sendable.Send.Count==0)return new(true,null);
            }
        }
        // The boundary rule of an All time clear, beside the exclusion set: in-scope deltas before Before are skipped until a
        // minute at or after Before is merged or BoundaryLimit has passed since the command. Writer-only state.
        HistoryClear? boundary=null;long boundarySince=0;bool stepping=false;
        void InstallBoundary(HistoryClear clear){boundary=clear;boundarySince=Stopwatch.GetTimestamp();}
        // The clear command, in the spec's writer order. A resume (Try again with the marker saved) repeats nothing already done.
        void RunClear(HistoryClearCommand.Command command)
        {
            var clear=command.Clear;
            string? unavailable;lock(gate)unavailable=initializationFailure;
            if(store is null)unavailable??=nameof(InvalidOperationException);
            if(unavailable is not null){clears.Unavailable(unavailable);return;}
            clears.Clearing(null);
            try {store!.BeginClear(clear);}
            catch(Exception error){clears.Fail(error.GetType().Name,command.Resume&&store!.PendingClear is not null);return;}
            clears.MarkerSaved();
            if(!command.Resume&&clear.AllTime)
            {
                lock(gate)Fold(()=>folder.Forget(clear.AppKey));
                InstallBoundary(clear);
            }
            else if(command.Resume&&clear.Before>clock())InstallBoundary(clear);
            var flush=Write(drain:true);
            if(!flush.Saved){clears.Fail(flush.FailureType??nameof(IOException),true);return;}
            // Held units holding an in-scope record are retired whole: sent after the delete, they would add their rows back.
            lock(gate)
            {
                var retired=Fold(()=>folder.RetireWaiting(r=>clear.Covers(r.Delta.Peer.Process.Name,r.Delta.Minute)));
                foreach(var unit in retired)DropLocked(Math.Max(1,unit.Observations));
            }
            stepping=true;
        }
        // One step. ClearStep throws without a pending clear, so a marker already gone is Done.
        void Step()
        {
            if(ClearStepOverride is null&&store?.PendingClear is null){clears.Done();stepping=false;return;}
            if(clears.Step(budget=>store!.ClearStep(budget)))stepping=false;
        }
        async Task Process(Pending item)
        {
            await EnrichAsync(item.Sample).ConfigureAwait(false);
            bool complete=item.Complete&&item.Sample.Available;
            var accepted=new List<ConnectionDelta>();
            var excluded=Volatile.Read(ref exclusions);
            if(boundary is not null&&Stopwatch.GetElapsedTime(boundarySince)>=HistoryClear.BoundaryLimit)boundary=null;
            var rule=boundary;
            foreach(var delta in item.Sample.Deltas)
            {
                // Never shift skewed/paused observations into a fabricated publication-time minute.
                if((item.Since.HasValue&&delta.FirstSeen<item.Since)||delta.FirstSeen<item.Sample.Timestamp.AddSeconds(-60)||delta.LastSeen>item.Sample.Timestamp.AddSeconds(2))
                {lock(gate)DropLocked(1);complete=false;continue;}
                // Don't record: skipped, not dropped; complete and coverage stand, and it still leaves buffered below with the publication's count.
                if(excluded.Count>0&&excluded.Contains(AppKey.Of(delta.Peer.Process.Name)))continue;
                // A clear's boundary rule: skipped the same way.
                if(rule is not null&&rule.Covers(delta.Peer.Process.Name,delta.Minute))continue;
                accepted.Add(delta);
            }
            // The rule ends once a minute at or after Before is merged (a delta's, or a coverage piece's).
            if(rule is not null&&(accepted.Any(d=>d.Minute>=rule.Before)||item.Sample.CoverageFrom is not null&&item.Sample.Timestamp>rule.Before))boundary=null;
            int frozen;
            lock(gate)
            {
                frozen=Fold(()=>folder.Merge(item.Sample,item.Generation,item.Origin,complete,accepted));
                buffered-=item.Sample.Deltas.Count;bufferedPublications--;
            }
            if(frozen>0)Write();
        }
        using var timer=new PeriodicTimer(options.FlushInterval??DefaultFlushInterval);
        Task<bool> tick=timer.WaitForNextTickAsync().AsTask(),available=queue.Reader.WaitToReadAsync().AsTask(),command=clears.Reader.WaitToReadAsync().AsTask();
        // A marker found at startup: only the steps run, and the boundary again while Before is still ahead.
        if(initializationFailure is null&&store?.PendingClear is {} pending)
        {
            var resumed=Resumed(pending,clock());clears.Resume(resumed);
            if(resumed.Before>clock())InstallBoundary(resumed);
            stepping=true;
        }
        bool yielded=false;
        while(true)
        {
            // While a clear steps, the writer alternates one data pass with one step; it waits only after a step that yielded early.
            if(!stepping)await Task.WhenAny(tick,available,command).ConfigureAwait(false);
            else if(yielded)await Task.WhenAny(tick,available,Task.Delay(HistoryClear.StepBudget)).ConfigureAwait(false);
            // A command is taken only when no clear steps: Post refuses while one is active, so a command found then was posted before
            // a startup resume, and it runs after that clear.
            if(command.IsCompleted&&!stepping)
            {
                // The command comes before the next queue item. None starts after StopAsync.
                bool open=await command.ConfigureAwait(false);bool ending=finishing.IsCancellationRequested;
                if(open&&!ending&&clears.Reader.TryRead(out var request))RunClear(request);
                command=open&&!ending?clears.Reader.WaitToReadAsync().AsTask():new TaskCompletionSource<bool>().Task;
            }
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
                // While stepping, one pass takes only what was queued when it began.
                int pass=stepping?Math.Max(1,queue.Reader.Count):int.MaxValue;
                while(pass-->0&&queue.Reader.TryRead(out var item))
                {
                    await Process(item).ConfigureAwait(false);
                    if(tick.IsCompleted||(command.IsCompleted&&!stepping))break;
                }
                available=queue.Reader.WaitToReadAsync().AsTask();
            }
            yielded=false;
            if(stepping&&!finishing.IsCancellationRequested)
            {
                long started=Stopwatch.GetTimestamp();Step();
                yielded=stepping&&Stopwatch.GetElapsedTime(started)<HistoryClear.StepBudget/2;
            }
        }
        // R6: the queue is complete and drained, so every open unit is final.
        lock(gate)Fold(folder.FreezeAll);
        Write(final:true);
    }
    /// <summary>A marker read at startup as a request. The marker keeps only AppKey and Before; Requested is chosen so that AllTime
    /// holds exactly when Before is still ahead of now (the only case a resume installs the boundary).</summary>
    private static HistoryClear Resumed(PendingConnectionClear pending,DateTimeOffset now)
        =>new(pending.AppKey,pending.Before,pending.Before>now?pending.Before.AddMinutes(-1):pending.Before);
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
