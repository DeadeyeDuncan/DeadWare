using System.Diagnostics;
using System.IO;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// Real-Windows measurement of the Clear history… durations on full-size files. This part fills a Connections store and a Usage
/// store to their 256 MiB caps and times each fill, so CI time can be checked. It records numbers and decides nothing.
/// </summary>
[Collection("RealWindows")]
public sealed class HistoryClearMeasurementTests
{
    private const string Machine="measurement",Segment="measurement";
    private const int AppsPerMinute=50,MinutesPerAppend=80,UsageMinuteLimit=120*24*60;
    private const long Megabyte=1<<20;
    // Five spellings of one app: 5 of the 50 rows a minute, the one-app clear's 10 %.
    private static readonly string[] Spellings=["app0.exe","APP0.EXE","App0.exe","aPP0.exe","app0.EXE"];

    [GitHubWindowsFact] public void History_clear_timing_on_large_files()
    {
        string root=Path.Combine(Path.GetTempPath(),$"deadware-history-clear-{Guid.NewGuid():N}");
        try
        {
            var connections=FillConnections(Path.Combine(root,"connections","connections.db"));
            var usage=FillUsage(Path.Combine(root,"usage","usage-history.db"));
            MeasurementRecorder.Write("history-clear",new{fills=new{connections,usage}});
        }
        finally
        {
            try {Directory.Delete(root,true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {}
        }
    }

    /// <summary>The 0.16.0 fill with pairs 0 and 1 sharing app0.exe, so 2 of the 20 pairs belong to one application.</summary>
    private static object FillConnections(string file)
    {
        var store=new ConnectionHistoryStore(file);
        store.Initialize();
        long ticks=DateTimeOffset.UtcNow.UtcTicks;
        var start=new DateTimeOffset(ticks-ticks%TimeSpan.TicksPerMinute,TimeSpan.Zero)-TimeSpan.FromDays(10);
        var fill=ConnectionHistoryMeasurementTests.FillToCap(store,start,i=>i<2?"app0.exe":$"app{i}.exe");
        return new{seconds=fill.Seconds,hours=fill.Hours,trafficRows=fill.TrafficRows,fileBytes=fill.FileBytes,evicted=fill.Evicted};
    }

    /// <summary>
    /// One CPU row and 50 application rows a minute, 80 minutes (4,080 rows) per Append, until the main file is within 4 MiB of
    /// its cap or the current minute is reached.
    /// </summary>
    private static object FillUsage(string file)
    {
        var store=new UsageHistoryStore(file);
        store.Initialize();
        long ticks=DateTimeOffset.UtcNow.UtcTicks;
        var floor=new DateTimeOffset(ticks-ticks%TimeSpan.TicksPerMinute,TimeSpan.Zero);
        var start=floor-TimeSpan.FromDays(120);
        var clock=Stopwatch.StartNew();
        long minutes=0;
        var batches=new List<UsageMinuteBatch>(MinutesPerAppend);
        while(start.AddMinutes(minutes)<floor&&minutes<UsageMinuteLimit)
        {
            batches.Clear();
            for(int i=0;i<MinutesPerAppend&&start.AddMinutes(minutes)<floor;i++,minutes++)
            {
                var at=start.AddMinutes(minutes);
                var apps=new UsageApplicationMinute[AppsPerMinute];
                for(int a=0;a<AppsPerMinute;a++)
                    apps[a]=new(a<Spellings.Length?Spellings[a]:$"app{a-Spellings.Length+1}.exe",at,1_000L*(a+1),100L*(a+1),Segment);
                batches.Add(new(at,[new(UsageMetrics.Cpu,null,at,10,5,15,60,true,Segment)],apps,false,Segment));
            }
            store.Append(Machine,batches,start.AddMinutes(minutes).AddMinutes(1));
            if(new FileInfo(file).Length>=UsageHistoryStore.MaximumDatabaseBytes-4*Megabyte)break;
        }
        double seconds=Math.Round(clock.Elapsed.TotalSeconds,1);
        bool evicted=store.EarliestRetained(Machine)>start;
        return new{seconds,minutes,applicationRows=minutes*AppsPerMinute,fileBytes=new FileInfo(file).Length,evicted};
    }
}
