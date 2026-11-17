using DeadWare.Core;

namespace DeadWare.App;

/// <summary>A poll's view of the loop's latest read: <paramref name="Fresh"/>, with its read time, the first time it is taken; afterwards the same read without credit.
/// <paramref name="ReturnedSeconds"/> is when that read returned, on the collector's monotonic clock: a poll ages the read from there, not from its start.
/// <paramref name="PeriodSeconds"/> is the <see cref="GpuCounterLoop.Period"/> the loop read it under.</summary>
internal sealed record GpuCounterTake(GpuCounterRead Read,bool Fresh,double? ReadMilliseconds,double ReturnedSeconds,double PeriodSeconds=1);

/// <summary>
/// Reads GPU counters on their own task once <see cref="GpuPlacementGuard"/> moves them off the poll (spec, Placement and cost), in
/// <see cref="StorageSensorLoop"/>'s style: one read at a time, timed on the collector's monotonic clock, and none while <c>enabled</c> is false.
/// Each read comes a <see cref="Period"/> after the previous one returned, the first a Period after the loop starts; the period follows
/// <c>shown</c> (whether a page shows live per-app GPU values; always, when null). A read that throws is kept as "GPU counters unavailable"
/// and the loop reads on. Nothing else may use the source until <see cref="Stopped"/> completes.
/// </summary>
internal sealed class GpuCounterLoop(IGpuCounterSource source,Func<double> monotonic,Func<double> pollingInterval,Func<bool> enabled,Func<bool>? shown=null) : IAsyncDisposable
{
    private static readonly TimeSpan DisposeBudget=TimeSpan.FromSeconds(5);
    // The longest single wait, so a shorter polling interval shortens the current wait within this long.
    private const double WaitStepSeconds=0.5;
    // Never disposed, so a later Start or DisposeAsync cannot throw ObjectDisposedException.
    private readonly CancellationTokenSource cancellation=new();
    private readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously),disposed=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate=new();
    private int started,disposing;
    private GpuCounterRead? latest;
    // When the latest read returned, and the period it was read under.
    private double returned,latestPeriod=1;
    // The latest read's time while no Take has returned it.
    private double? untaken;

    /// <summary>The period while no page shows live per-app GPU values.</summary>
    public const double IdlePeriodSeconds=5;
    /// <summary>
    /// Never more often than every-poll reading would read, and every <see cref="IdlePeriodSeconds"/> while nothing shows live per-app GPU
    /// values. PDH's utilization is the average over the time between two collections, so a longer period still counts every GPU-second,
    /// in coarser steps. The period is also passed to the read as its freshness bound, so a gap that long still forms a rate.
    /// </summary>
    public static double Period(double pollingInterval,bool shown=true)=>Math.Max(shown?1:IdlePeriodSeconds,pollingInterval);
    private double CurrentPeriod()=>Period(pollingInterval(),shown?.Invoke()??true);
    /// <summary>Completes once the loop has exited, its last read has returned and <see cref="IGpuCounterSource.Close"/> has run.</summary>
    public Task Stopped=>stopped.Task;
    public void Start() {if(Interlocked.Exchange(ref started,1)==0)_=Task.Run(RunAsync);}
    /// <summary>
    /// Null before the first completed read, after a disabled pass dropped it and once the loop has stopped. The latest completed read comes
    /// back fresh once; later takes return it again without credit, for display only, until a newer read replaces it. A read replaced before
    /// any take is never returned.
    /// </summary>
    public GpuCounterTake? Take()
    {
        lock(gate)
        {
            if(latest is null)return null;
            if(untaken is {} milliseconds) {untaken=null;return new(latest,true,milliseconds,returned,latestPeriod);}
            return new(latest with {Credit=false},false,null,returned,latestPeriod);
        }
    }
    private async Task RunAsync()
    {
        var token=cancellation.Token;
        var closed=false;
        // The poll that started the loop has just read, so the first loop read also waits a period.
        var from=monotonic();
        try
        {
            while(true)
            {
                await WaitForPeriodAsync(from,token).ConfigureAwait(false);
                if(enabled())
                {
                    closed=false;
                    var readStart=monotonic();
                    double period=CurrentPeriod();
                    GpuCounterRead read;
                    // A throw would end the loop unobserved and leave every later poll a first sample; the column says unavailable instead.
                    try {read=source.Read(readStart,period);}
                    catch(Exception) {read=GpuCounterRead.Unavailable(GpuCounterStatus.Unavailable,readStart,DateTimeOffset.UtcNow);}
                    from=monotonic();
                    lock(gate) {latest=read;returned=from;untaken=(from-readStart)*1000;latestPeriod=period;}
                }
                else
                {
                    if(!closed)
                    {
                        source.Close();
                        closed=true;
                        lock(gate) {latest=null;untaken=null;}
                    }
                    from=monotonic();
                }
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {}
        finally
        {
            lock(gate) {latest=null;untaken=null;}
            // After the last read has returned, never under it.
            try {source.Close();}
            finally {stopped.TrySetResult();}
        }
    }
    /// <summary>
    /// Returns once <see cref="Period"/> of the current polling interval has passed since <paramref name="from"/>. It waits in steps of at
    /// most <see cref="WaitStepSeconds"/> and re-reads the interval and whether a page shows the values at each, so a change (a page
    /// opening, say) takes effect within a step; it throws once cancelled.
    /// </summary>
    private async Task WaitForPeriodAsync(double from,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();
            var left=from+CurrentPeriod()-monotonic();
            if(left<=0)return;
            // Whole milliseconds, rounded up: Task.Delay truncates, and a sub-millisecond wait would return at once and spin.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(Math.Min(WaitStepSeconds,left)*1000)),token).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Cancels the loop and waits for <see cref="Stopped"/> for at most the 5 s hardware stop budget. A read still in flight keeps running and
    /// the loop closes the source when it returns. Idempotent and never throws: monitoring off, the worker's finally and the collector's disposal
    /// can each reach the same loop, and every call returns the first call's task.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposing,1)==0)_=StopAsync();
        return new(disposed.Task);
    }
    private async Task StopAsync()
    {
        try
        {
            cancellation.Cancel();
            // A loop that was never started exits at once without reading, so Stopped still follows a Close.
            Start();
            await Stopped.WaitAsync(DisposeBudget).ConfigureAwait(false);
        }
        catch(TimeoutException) {}
        finally {disposed.TrySetResult();}
    }
}
