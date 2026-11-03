using DeadWare.Core;

namespace DeadWare.App;

/// <summary>Reads SMART-style storage health off the poll thread.</summary>
internal interface IStorageHealthSource : IDisposable
{
    IReadOnlyList<(string Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout);
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
    private IReadOnlyList<StorageHealth> latest=[];
    private string status="Storage health not read yet";
    public StorageSensorLoop(IStorageHealthSource source,TimeSpan period,Func<IReadOnlyList<StorageDrive>> drives)
    {this.source=source;this.period=period;this.drives=drives;}
    public IReadOnlyList<StorageHealth> Latest=>Volatile.Read(ref latest);
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
                    var readings=await read.WaitAsync(token).ConfigureAwait(false);
                    Volatile.Write(ref latest,Match(readings,drives()));
                    Volatile.Write(ref status,"");
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) {throw;}
                catch(Exception e) {Volatile.Write(ref status,$"Storage health unavailable ({e.GetType().Name})");}
                await Task.Delay(period,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {}
    }
    internal static IReadOnlyList<StorageHealth> Match(IReadOnlyList<(string Serial,string Model,int? Index,SmartSummary Summary)> readings,IReadOnlyList<StorageDrive> drives)
    {
        var result=new List<StorageHealth>(readings.Count);
        foreach(var r in readings)
        {
            var serial=StorageProjection.Normalize(r.Serial);
            StorageDrive? drive=serial is null?null:drives.FirstOrDefault(d=>StorageProjection.Normalize(d.Serial)==serial);
            if(drive is null&&r.Index is{} index)drive=drives.FirstOrDefault(d=>d.Index==index);
            if(drive is null)
            {
                var models=drives.Where(d=>string.Equals(d.Model,r.Model,StringComparison.Ordinal)).Take(2).ToList();
                if(models.Count==1)drive=models[0];
            }
            var id=drive?.Id??"unmatched:"+r.Model;
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
