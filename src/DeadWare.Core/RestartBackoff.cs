namespace DeadWare.Core;
// Paces restarts: 4 s doubling to a 60 s cap, and gives up after 5 failures inside 10 minutes. Never restarts faster than Base (rapid cycles have wedged USB firmware).
public sealed class RestartBackoff
{
    public static readonly TimeSpan Base=TimeSpan.FromSeconds(4),Cap=TimeSpan.FromSeconds(60),Window=TimeSpan.FromMinutes(10);
    public const int MaxFailures=5;
    readonly List<DateTimeOffset> failures=new();
    public TimeSpan? RecordFailure(DateTimeOffset now)
    {
        failures.RemoveAll(t=>now-t>Window);
        failures.Add(now);
        if(failures.Count>=MaxFailures) return null;
        double seconds=Base.TotalSeconds*Math.Pow(2,failures.Count-1);
        return TimeSpan.FromSeconds(Math.Clamp(seconds,Base.TotalSeconds,Cap.TotalSeconds));
    }
    public void Reset()=>failures.Clear();
}
