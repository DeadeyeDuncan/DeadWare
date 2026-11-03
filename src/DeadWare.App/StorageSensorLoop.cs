using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Reads SMART-style storage health off the poll thread.</summary>
internal interface IStorageHealthSource : IDisposable
{
    IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout);
    /// <summary>Per-hardware problems from the last read (exception type names); empty when every drive read.</summary>
    string Status=>"";
}

/// <summary>Polls an <see cref="IStorageHealthSource"/> on its own task and publishes matched health; never blocks the collector.</summary>
internal sealed class StorageSensorLoop : IAsyncDisposable
{
    /// <summary>Sentinel summary a source returns for hardware whose read exceeded the per-hardware timeout.</summary>
    internal static readonly SmartSummary TimedOut=new(false,false,null,null,null,0,0,0,null,null,false);
    internal static readonly TimeSpan PerHardwareTimeout=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisposeBudget=TimeSpan.FromSeconds(5);
    private readonly IStorageHealthSource source;
    private readonly TimeSpan period;
    private readonly Func<IReadOnlyList<StorageDrive>> drives;
    private readonly CancellationTokenSource cancellation=new();
    private Task? worker;
    private Task? pendingRead;
    private IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> readings=[];
    private string status="Storage health not read yet";
    public StorageSensorLoop(IStorageHealthSource source,TimeSpan period,Func<IReadOnlyList<StorageDrive>> drives)
    {this.source=source;this.period=period;this.drives=drives;}
    /// <summary>The last read, matched against the current drive list so drives discovered after the read still match.</summary>
    public IReadOnlyList<StorageHealth> Latest=>Match(Volatile.Read(ref readings),drives());
    public string Status=>Volatile.Read(ref status);
    public void Start()=>worker??=Task.Run(RunAsync);
    private async Task RunAsync()
    {
        var token=cancellation.Token;
        try
        {
            while(!token.IsCancellationRequested)
            {
                try
                {
                    var read=Task.Run(()=>source.Read(PerHardwareTimeout));
                    Volatile.Write(ref pendingRead,read);
                    Volatile.Write(ref readings,await read.WaitAsync(token).ConfigureAwait(false));
                    Volatile.Write(ref status,source.Status??"");
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
                catch(Exception e) {Volatile.Write(ref status,$"Storage health unavailable ({e.GetType().Name})");}
                await Task.Delay(period,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {}
    }
    internal static IReadOnlyList<StorageHealth> Match(IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> readings,IReadOnlyList<StorageDrive> drives)
    {
        var result=new List<StorageHealth>(readings.Count);
        // One reading per drive and one drive per reading: a drive already claimed is not matched again.
        var free=drives.ToList();
        foreach(var r in readings)
        {
            var serial=StorageProjection.Normalize(r.Serial);
            StorageDrive? drive=serial is null?null:free.FirstOrDefault(d=>StorageProjection.Normalize(d.Serial)==serial);
            if(drive is null&&r.Index is{} index)drive=free.FirstOrDefault(d=>d.Index==index);
            if(drive is null)
            {
                var models=free.Where(d=>string.Equals(d.Model,r.Model,StringComparison.Ordinal)).Take(2).ToList();
                if(models.Count==1)drive=models[0];
            }
            if(drive is not null){free.Remove(drive);}
            var id=drive?.Id??"unmatched:"+r.Model;
            if(drive is null){int n=1;var baseId=id;while(result.Any(x=>x.DriveId==id))id=$"{baseId}#{++n}";}
            result.Add(ReferenceEquals(r.Summary,TimedOut)?StorageProjection.Health(id,null,"Unavailable · sensor timeout"):StorageProjection.Health(id,r.Summary,null));
        }
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        if(worker is not null)
        {
            try {await worker.WaitAsync(DisposeBudget).ConfigureAwait(false);} catch(TimeoutException) {}
        }
        // Never dispose the source under a read that is still running; release it once that read ends.
        var read=Volatile.Read(ref pendingRead);
        if(read is null||read.IsCompleted)source.Dispose();
        else _=read.ContinueWith(_=>source.Dispose(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        cancellation.Dispose();
    }
}
