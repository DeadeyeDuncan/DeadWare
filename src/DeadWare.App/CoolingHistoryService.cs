using System.Threading.Channels;
using DeadWare.Core;

namespace DeadWare.App;

public sealed record CoolingView(IReadOnlyList<ThermalDevice> Devices, ThermalDevice? SelectedDevice,
    IReadOnlyList<ThermalWindow> History, IReadOnlyList<ThermalReference> References,
    ThermalReference? ActiveReference, CoolingAssessment Assessment, IReadOnlyList<CoolingWarning> Warnings,
    long StorageBytes, string Status, long DroppedSamples);

/// <summary>Owns history independently of live monitoring. No method called by the collector performs I/O.</summary>
public sealed class CoolingHistoryService
{
    private sealed record Options(bool Recording, bool Notifications, int DetailedDays, int SummaryDays, string ConfigurationId, long Generation);
    private sealed record Pending(Reading? Reading, long Generation);
    private readonly Channel<Pending> queue=Channel.CreateBounded<Pending>(new BoundedChannelOptions(120)
        {SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private readonly SemaphoreSlim databaseGate=new(1,1);
    private readonly ThermalStore store;
    private readonly ThermalAggregator aggregator=new();
    // References are immutable. Keep a bounded cache so a seven-day reference isn't
    // decompressed and allocated again on each page refresh or recorded minute.
    private readonly Dictionary<(string Device,string Configuration),ThermalReference?> referenceCache=new();
    private readonly string machineKey,sessionId=Guid.NewGuid().ToString("N");
    private readonly object lifecycle=new();
    private Options options;
    private Task? worker;
    private bool accepting,started,stopped;
    private volatile bool initialized,faulted;
    private string status="History starts with monitoring.";
    private long dropped;
    public long DroppedSamples=>Interlocked.Read(ref dropped);
    public int PendingSamples=>queue.Reader.Count;
    public string Status=>Volatile.Read(ref status)+(DroppedSamples>0?$" · {DroppedSamples} samples dropped; history has gaps":"");
    public event Action<CoolingWarning>? WarningRaised;

    public CoolingHistoryService(string path,string machineKey,string configurationId)
    {
        store=new(path);this.machineKey=machineKey;
        options=new(true,false,30,90,configurationId,0);
    }
    public void Configure(bool recording,bool notifications,int detailedDays,int summaryDays,string configurationId)
    {
        var old=Volatile.Read(ref options);
        long generation=old.Generation+((recording!=old.Recording||configurationId!=old.ConfigurationId)?1:0);
        Volatile.Write(ref options,new(recording,notifications,detailedDays==7?7:30,summaryDays==30?30:90,configurationId,generation));
        if(generation!=old.Generation||old.DetailedDays!=detailedDays||old.SummaryDays!=summaryDays)
            queue.Writer.TryWrite(new(null,generation));
        if(!faulted)Volatile.Write(ref status,recording?"Recording local history · minute windows":"Recording paused · live monitoring continues");
    }
    public void Start()
    {
        lock(lifecycle)
        {
            if(started||stopped)return;
            started=true;Volatile.Write(ref accepting,true);worker=Task.Run(RunAsync);
        }
    }
    public void TryRecord(Reading reading)
    {
        var current=Volatile.Read(ref options);
        if(!Volatile.Read(ref accepting)||!current.Recording||faulted)return;
        if(!queue.Writer.TryWrite(new(reading,current.Generation)))Interlocked.Increment(ref dropped);
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
        long generation=Volatile.Read(ref options).Generation,seenDrops=0;
        int appliedDetailed=0,appliedSummary=0;
        try
        {
            await databaseGate.WaitAsync().ConfigureAwait(false);
            try {store.Initialize();initialized=true;}
            finally {databaseGate.Release();}
            Volatile.Write(ref status,options.Recording?"Recording local history · first window takes about a minute":"Recording paused · live monitoring continues");
            await foreach(var pending in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var current=Volatile.Read(ref options);
                if(current.DetailedDays!=appliedDetailed||current.SummaryDays!=appliedSummary)
                {
                    await databaseGate.WaitAsync().ConfigureAwait(false);
                    try {store.Append([],DateTimeOffset.UtcNow,current.DetailedDays,current.SummaryDays);}
                    finally {databaseGate.Release();}
                    appliedDetailed=current.DetailedDays;appliedSummary=current.SummaryDays;
                }
                if(generation!=current.Generation||seenDrops!=DroppedSamples)
                {
                    await PersistAsync(aggregator.Flush(),current).ConfigureAwait(false);
                    aggregator.Reset();generation=current.Generation;seenDrops=DroppedSamples;
                }
                if(pending.Reading is null||pending.Generation!=generation||!current.Recording)continue;
                var projected=ThermalProjection.Project(pending.Reading,sessionId,machineKey,current.ConfigurationId);
                if(projected.Count==0)
                {
                    await PersistAsync(aggregator.Flush(),current).ConfigureAwait(false);
                    aggregator.Reset();
                    Volatile.Write(ref status,"Recording enabled · waiting for supported CPU/GPU sensor data");
                    continue;
                }
                var windows=new List<ThermalWindow>();
                foreach(var sample in projected)windows.AddRange(aggregator.Add(sample));
                await PersistAsync(windows,current).ConfigureAwait(false);
            }
            await PersistAsync(aggregator.Flush(),Volatile.Read(ref options)).ConfigureAwait(false);
            if(!faulted)Volatile.Write(ref status,"History saved · monitoring stopped");
        }
        catch(Exception e)
        {
            faulted=true;Volatile.Write(ref accepting,false);queue.Writer.TryComplete();
            while(queue.Reader.TryRead(out _))Interlocked.Increment(ref dropped);
            Volatile.Write(ref status,$"History recording unavailable ({e.GetType().Name}). Live monitoring continues. Resolve the storage problem, then restart DeadWare to retry.");
        }
    }
    private async Task PersistAsync(IReadOnlyList<ThermalWindow> windows,Options current)
    {
        if(windows.Count==0)return;
        var notifications=new List<CoolingWarning>();
        await databaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var now=DateTimeOffset.UtcNow;
            store.Append(windows,now,current.DetailedDays,current.SummaryDays);
            // Muting tray delivery does not discard the persistent warning history.
            {
                foreach(var device in windows.Select(w=>w.Device).DistinctBy(d=>(d.Id,d.ConfigurationId)))
                {
                    var reference=ReadActiveReference(device.Id,device.ConfigurationId);
                    if(reference is null)continue;
                    var recent=store.Read(device.Id,device.ConfigurationId,now.AddMinutes(-30),now,100,true);
                    var assessment=CoolingAnalyzer.Assess(recent,reference,now);
                    var warnings=store.Warnings(device.Id,device.ConfigurationId);
                    if(!CoolingAlertPolicy.ShouldNotify(assessment,reference,warnings,now))continue;
                    var warning=new CoolingWarning(Guid.NewGuid().ToString("N"),device.Id,device.ConfigurationId,reference.Id,now,
                        device.Name+": "+assessment.Message,assessment.Delta??0);
                    store.AddWarning(warning);if(current.Notifications)notifications.Add(warning);
                }
            }
            if(!faulted)Volatile.Write(ref status,current.Recording?"Recording local history · minute windows":"Recording paused · live monitoring continues");
        }
        finally {databaseGate.Release();}
        // Notification failures cannot turn a successful recording into a monitoring failure.
        foreach(var warning in notifications)
            try {WarningRaised?.Invoke(warning);} catch { }
    }
    private async Task<T> InStoreAsync<T>(Func<T> action)
    {
        if(!initialized)throw new InvalidOperationException("History is not ready. "+Status);
        return await Task.Run(async ()=>
        {
            await databaseGate.WaitAsync().ConfigureAwait(false);
            try {return action();}finally {databaseGate.Release();}
        }).ConfigureAwait(false);
    }
    public async Task<CoolingView> ReadViewAsync(string? deviceId,string? configurationId,int days)
    {
        if(!initialized)return Empty(Status);
        try
        {
            return await InStoreAsync(()=>
            {
                var devices=store.Devices();var config=Volatile.Read(ref options).ConfigurationId;
                var selected=devices.FirstOrDefault(d=>d.Id==deviceId&&d.ConfigurationId==configurationId)
                    ??devices.FirstOrDefault(d=>d.ConfigurationId==config&&d.Kind=="CPU")
                    ??devices.FirstOrDefault(d=>d.ConfigurationId==config);
                if(selected is null)return Empty(Status+" · Waiting for the current setup's first minute; earlier setups remain selectable.") with {Devices=devices,StorageBytes=store.StorageBytes};
                var now=DateTimeOffset.UtcNow;
                var history=store.Read(selected.Id,selected.ConfigurationId,now.AddDays(-Math.Clamp(days,1,90)),now);
                var references=store.ReferenceHeaders(selected.Id,selected.ConfigurationId);
                var reference=ReadActiveReference(selected.Id,selected.ConfigurationId);
                var recent=store.Read(selected.Id,selected.ConfigurationId,now.AddMinutes(-30),now,100,true);
                return new CoolingView(devices,selected,history,references,reference,CoolingAnalyzer.Assess(recent,reference,now),
                    store.Warnings(selected.Id,selected.ConfigurationId),store.StorageBytes,Status,DroppedSamples);
            }).ConfigureAwait(false);
        }
        catch(Exception e) {return Empty($"History unavailable ({e.GetType().Name}). Live monitoring continues. "+Status);}
    }
    public Task<string> CreateReferenceAsync(ThermalDevice device,DateTimeOffset from,DateTimeOffset to,string label)=>InStoreAsync(()=>
    {
        if(to<=from||to-from>TimeSpan.FromDays(7)||to>DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentException("Choose a past period of at most seven days.");
        if(!device.ComparisonAllowed)throw new InvalidOperationException(device.IdentityNote);
        if(string.IsNullOrWhiteSpace(label)||label.Trim().Length>80)throw new ArgumentException("Give the reference a name of 1–80 characters.");
        var windows=store.Read(device.Id,device.ConfigurationId,from,to,10081,true)
            .Where(w=>w.Eligible&&w.ResolutionSeconds==60&&w.Start>=from&&w.End<=to).ToArray();
        if(windows.Length<30)throw new InvalidOperationException("A reference needs at least 30 eligible minute windows with temperature, package power and load. Warm-up, missing readings and changing loads are excluded.");
        store.SaveReference(new(Guid.NewGuid().ToString("N"),device.Id,device.ConfigurationId,label.Trim(),DateTimeOffset.UtcNow,from,to,windows));
        referenceCache.Remove((device.Id,device.ConfigurationId));
        return $"Saved fixed reference: {windows.Length} eligible minutes. It will not learn from later changes.";
    });
    public async Task ActivateReferenceAsync(string id)=>await InStoreAsync(()=>{store.ActivateReference(id);referenceCache.Clear();return true;}).ConfigureAwait(false);
    public async Task AcknowledgeAsync(string id)=>await InStoreAsync(()=>{store.AcknowledgeWarning(id);return true;}).ConfigureAwait(false);
    public Task<int> ExportAsync(ThermalDevice device,DateTimeOffset from,DateTimeOffset to,string path)=>
        InStoreAsync(()=>store.ExportCsv(device.Id,device.ConfigurationId,from,to,path));
    // All callers hold databaseGate; no reference is mutated after caching.
    private ThermalReference? ReadActiveReference(string deviceId,string configurationId)
    {
        var key=(deviceId,configurationId);
        if(referenceCache.TryGetValue(key,out var reference))return reference;
        if(referenceCache.Count>=8)referenceCache.Remove(referenceCache.Keys.First());
        reference=store.ActiveReference(deviceId,configurationId);referenceCache[key]=reference;return reference;
    }
    private CoolingView Empty(string message)=>new([],null,[],[],null,
        new("Learning","No comparable history yet. Keep recording, then save a reference from a period you believe was normal.",0,10,null,null,null,null,null,false),[],0,message,DroppedSamples);
}
