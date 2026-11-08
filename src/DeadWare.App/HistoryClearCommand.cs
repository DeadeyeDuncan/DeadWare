using System.Threading.Channels;
using DeadWare.Core;
namespace DeadWare.App;

/// <summary>
/// The clear-command state a history service's worker shares with the UI: the <see cref="HistoryClearState"/>, the kept request,
/// the command channel of capacity 1, Try again, and one step with its failure handling. Each service keeps its own store calls
/// (the marker, Forget, the flush, the boundary) and calls these members from its worker.
/// </summary>
/// <remarks>
/// <para>UI side: <see cref="Post"/> (the service's <c>Clear</c>), <see cref="Retry"/> (<c>RetryClear</c>), <see cref="CanRetry"/>
/// (<c>CanRetryClear</c>), <see cref="State"/> (<c>ClearState</c>; its setter is a test seam), <see cref="StepOverride"/>
/// (<c>ClearStepOverride</c>).</para>
/// <para>Worker side: wait on <see cref="Reader"/> beside the data queue and take a command before the next data item. For a
/// <see cref="Command"/> with <c>Resume</c> false run the full order; with <c>Resume</c> true repeat nothing that already ran. Report
/// with <see cref="Clearing"/> once the worker takes it, <see cref="MarkerSaved"/> after the marker, <see cref="Fail"/>
/// (marker or flush failures) or <see cref="Unavailable"/>; then call <see cref="Step"/> between data passes until it returns true.
/// <see cref="Resume"/> is for a marker found at startup. A step never starts after the service's stop.</para>
/// </remarks>
internal sealed class HistoryClearCommand
{
    /// <summary>A queued clear. <paramref name="Resume"/> is true for Try again after the marker was saved.</summary>
    internal sealed record Command(HistoryClear Clear, bool Resume);

    private readonly object gate = new();
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private HistoryClearState state = HistoryClearState.None;
    private HistoryClear? kept;

    public ChannelReader<Command> Reader => commands.Reader;

    public HistoryClearState State
    {
        get { lock (gate) return state; }
        set { ArgumentNullException.ThrowIfNull(value); lock (gate) state = value; }
    }

    /// <summary>The request a failed clear re-posts: the last one posted or resumed.</summary>
    public HistoryClear? Kept { get { lock (gate) return kept; } }

    /// <summary>A test seam that replaces the store's step.</summary>
    public Func<TimeSpan, HistoryClearProgress>? StepOverride { get; set; }

    /// <summary>True while a clear is Queued or Clearing, or Failed with its marker saved: one clear at a time.</summary>
    public bool Active
    {
        get
        {
            lock (gate) return state.Phase is HistoryClearPhase.Queued or HistoryClearPhase.Clearing || state is { Phase: HistoryClearPhase.Failed, MarkerSaved: true };
        }
    }

    /// <summary>Keeps the request, sets Queued and puts it in the channel. Refused while <see cref="Active"/>.</summary>
    public void Post(HistoryClear request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (Active) throw new InvalidOperationException("A clear is in progress.");
            kept = request;
            state = new(HistoryClearPhase.Queued, null, null, false, false);
            commands.Writer.TryWrite(new(request, false));
        }
    }

    /// <summary>Failed with a kept request while the worker runs.</summary>
    public bool CanRetry(bool workerRunning)
    {
        lock (gate) return workerRunning && kept is not null && state.Phase == HistoryClearPhase.Failed;
    }

    /// <summary>Re-posts the kept request while <see cref="CanRetry"/>: as a resume when the marker was saved, otherwise as a new run.</summary>
    public bool Retry(bool workerRunning)
    {
        lock (gate)
        {
            if (!CanRetry(workerRunning)) return false;
            state = state with { Phase = HistoryClearPhase.Queued, Percent = null, Error = null };
            commands.Writer.TryWrite(new(kept!, state.MarkerSaved));
            return true;
        }
    }

    /// <summary>The kept request of a marker that exists but cannot be read. Try again posts it as a resume, and the worker reads the
    /// marker again instead of running a request.</summary>
    public static readonly HistoryClear Unread = new(null, DateTimeOffset.MinValue, DateTimeOffset.MinValue);

    /// <summary>A marker found at startup that cannot be read: Failed with the read's exception type, marker saved, Resumed. Try again
    /// re-reads it (<see cref="Unread"/>).</summary>
    public void Unreadable(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate) { kept = Unread; state = new(HistoryClearPhase.Failed, null, error, true, true); }
    }

    /// <summary>A marker found at startup: Clearing, marker saved, Resumed.</summary>
    public void Resume(HistoryClear request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate) { kept = request; state = new(HistoryClearPhase.Clearing, null, null, true, true); }
    }

    public void Clearing(int? percent) { lock (gate) state = state with { Phase = HistoryClearPhase.Clearing, Percent = percent, Error = null }; }

    public void MarkerSaved() { lock (gate) state = state with { MarkerSaved = true }; }

    public void Fail(string error, bool markerSaved) { lock (gate) state = state with { Phase = HistoryClearPhase.Failed, Percent = null, Error = error, MarkerSaved = markerSaved }; }

    public void Unavailable(string error) { lock (gate) state = state with { Phase = HistoryClearPhase.Unavailable, Percent = null, Error = error, MarkerSaved = false }; }

    public void Done() { lock (gate) state = state with { Phase = HistoryClearPhase.Done, Percent = 100, Error = null, MarkerSaved = false }; }

    /// <summary>
    /// Runs one step (<see cref="StepOverride"/> or <paramref name="storeStep"/>) with <see cref="HistoryClear.StepBudget"/>. Finished: Done.
    /// Otherwise Clearing with its percent. A throw: Failed with the type name and the marker saved; the worker keeps recording.
    /// </summary>
    /// <returns>True when the clear's steps ended, Done or Failed.</returns>
    public bool Step(Func<TimeSpan, HistoryClearProgress> storeStep)
    {
        ArgumentNullException.ThrowIfNull(storeStep);
        try
        {
            var progress = (StepOverride ?? storeStep)(HistoryClear.StepBudget);
            if (progress.Finished) { Done(); return true; }
            Clearing(progress.Percent);
            return false;
        }
        catch (Exception error)
        {
            Fail(error.GetType().Name, markerSaved: true);
            return true;
        }
    }
}
