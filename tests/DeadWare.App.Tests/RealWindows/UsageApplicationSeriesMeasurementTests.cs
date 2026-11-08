using System.Diagnostics;
using System.IO;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// Real-Windows measurement of the Usage history traffic strip's read: a month of minutes with 15 applications each, read with
/// <see cref="UsageHistoryStore.ReadApplicationSeries"/> at 450 and 1,500 points. It records numbers and decides nothing.
/// </summary>
[Collection("RealWindows")]
public sealed class UsageApplicationSeriesMeasurementTests
{
    private const int Minutes=30*24*60,ApplicationsPerMinute=15,Runs=5;
    private const string Machine="measurement",Segment="measurement";
    // One CPU row and 15 application rows a minute: 240 minutes is 3,840 rows, within MaximumBatchRows (4,096).
    private const int MinutesPerAppend=240;

    [GitHubWindowsFact] public void Usage_application_series_timing_for_a_month()
    {
        string root=Path.Combine(Path.GetTempPath(),$"deadware-usage-series-{Guid.NewGuid():N}");
        try
        {
            string file=Path.Combine(root,"usage-history.db");
            var store=new UsageHistoryStore(file);
            store.Initialize();
            var (from,to)=Fill(store);
            var points450=Timings(store,from,to,450);
            var points1500=Timings(store,from,to,1500);
            MeasurementRecorder.Write("usage-application-series",new
            {
                minutes=Minutes,applicationsPerMinute=ApplicationsPerMinute,fileBytes=new FileInfo(file).Length,points450,points1500,
            });
        }
        finally
        {
            try {Directory.Delete(root,true);}
            catch(IOException) {}
            catch(UnauthorizedAccessException) {}
        }
    }

    /// <summary>43,200 minutes ending at the current minute, each with a CPU row and app0.exe–app14.exe. Returns the month [from, to).</summary>
    private static (DateTimeOffset From,DateTimeOffset To) Fill(UsageHistoryStore store)
    {
        var now=DateTimeOffset.UtcNow;
        var to=new DateTimeOffset(now.UtcTicks-now.UtcTicks%TimeSpan.TicksPerMinute,TimeSpan.Zero).AddMinutes(1);
        var from=to.AddMinutes(-Minutes);
        var batches=new List<UsageMinuteBatch>(MinutesPerAppend);
        for(int minute=0;minute<Minutes;minute++)
        {
            var at=from.AddMinutes(minute);
            batches.Add(new(at,[new(UsageMetrics.Cpu,null,at,10,5,15,60,true,Segment)],
                Enumerable.Range(0,ApplicationsPerMinute).Select(i=>new UsageApplicationMinute($"app{i}.exe",at,1_000L*(i+1),100L*(i+1),Segment)).ToArray(),
                false,Segment));
            if(batches.Count<MinutesPerAppend&&minute<Minutes-1)continue;
            store.Append(Machine,batches,now);
            batches.Clear();
        }
        return (from,to);
    }

    /// <summary>One warm-up read, then five timed reads; a failed read is recorded by type name and the runs continue.</summary>
    private static object Timings(UsageHistoryStore store,DateTimeOffset from,DateTimeOffset to,int points)
    {
        try {store.ReadApplicationSeries(Machine,from,to,points);}
        catch(Exception) {}
        var times=new List<double>(Runs);string? error=null;
        for(int run=0;run<Runs;run++)
        {
            long started=Stopwatch.GetTimestamp();
            try
            {
                store.ReadApplicationSeries(Machine,from,to,points);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch(Exception e) {error??=e.GetType().Name;}
        }
        times.Sort();
        // Nearest rank over the successful runs: p50 is the median of five, p95 the slowest.
        double? Percentile(double p)=>times.Count==0?null:Math.Round(times[(int)Math.Ceiling(p*times.Count)-1],3);
        return new{p50Ms=Percentile(0.5),p95Ms=Percentile(0.95),runs=times.Count,error};
    }
}
