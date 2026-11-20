using System.Diagnostics;
using DeadWare.Core;
using Microsoft.UI.Dispatching;

namespace DeadWare.WinUI;

/// <summary>
/// Sends one target's changes at most once per 100 ms. The first goes at once; later ones within the interval wait for its end,
/// and only the latest of them goes, so the final value always does. <see cref="Flush"/> sends a waiting change now, as a slider
/// release or a closing flyout does; <see cref="Cancel"/> drops it.
/// </summary>
internal sealed class Throttle
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly DispatcherQueueTimer timer;
    private readonly Action<HostMessage> send;
    private HostMessage? waiting;
    private long sentAt;

    public Throttle(DispatcherQueue queue, Action<HostMessage> send)
    {
        this.send = send;
        timer = queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Flush();
    }

    /// <summary>True while a change waits for the end of the interval.</summary>
    public bool Pending => waiting is not null;

    public void Post(HostMessage request)
    {
        waiting = request;
        var since = sentAt == 0 ? Interval : Stopwatch.GetElapsedTime(sentAt);
        if (since >= Interval) Flush();
        else if (!timer.IsRunning)
        {
            timer.Interval = Interval - since;
            timer.Start();
        }
    }

    public void Flush()
    {
        timer.Stop();
        if (waiting is not { } request) return;
        waiting = null;
        sentAt = Stopwatch.GetTimestamp();
        send(request);
    }

    public void Cancel()
    {
        timer.Stop();
        waiting = null;
    }
}
