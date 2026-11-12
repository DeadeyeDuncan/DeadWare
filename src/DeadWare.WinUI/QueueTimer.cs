using DeadWare.App;
using Microsoft.UI.Dispatching;

namespace DeadWare.WinUI;

/// <summary>The session's timer on the WinUI dispatcher queue.</summary>
internal sealed class QueueTimer : ISessionTimer
{
    private readonly DispatcherQueueTimer timer;

    public QueueTimer(DispatcherQueue queue)
    {
        timer = queue.CreateTimer();
        timer.Tick += (_, _) => Tick?.Invoke();
    }

    public event Action? Tick;
    public TimeSpan Interval { get => timer.Interval; set => timer.Interval = value; }
    public bool IsEnabled => timer.IsRunning;
    public void Start() => timer.Start();
    public void Stop() => timer.Stop();
}
