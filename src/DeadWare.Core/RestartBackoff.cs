namespace DeadWare.Core;
// Paces restarts: 4 s doubling to a 60 s cap, and gives up after 5 failures inside 10 minutes. Never restarts faster than Base (rapid cycles have wedged USB firmware).
// F6 item 4 (whole-branch review D4): failures are stamped and pruned on the monotonic clock (TimeProvider.GetTimestamp), never the wall
// clock, so a step of the system clock can neither stretch nor shorten the 10-minute window. GetElapsedTime converts timestamp units at
// the provider's own frequency. Every call must pass the same provider.
public sealed class RestartBackoff
{
    public static readonly TimeSpan Base=TimeSpan.FromSeconds(4),Cap=TimeSpan.FromSeconds(60),Window=TimeSpan.FromMinutes(10);
    public const int MaxFailures=5;
    readonly List<long> failures=new();
    public TimeSpan? RecordFailure(TimeProvider time)
    {
        var now=time.GetTimestamp();
        failures.RemoveAll(t=>time.GetElapsedTime(t,now)>Window);
        failures.Add(now);
        if(failures.Count>=MaxFailures) return null;
        double seconds=Base.TotalSeconds*Math.Pow(2,failures.Count-1);
        return TimeSpan.FromSeconds(Math.Clamp(seconds,Base.TotalSeconds,Cap.TotalSeconds));
    }
    public void Reset()=>failures.Clear();
}
