using System.Diagnostics;
using System.IO;
using System.Reflection;
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
            string connectionsFile=Path.Combine(root,"connections","connections.db"),usageFile=Path.Combine(root,"usage","usage-history.db");
            var connectionStore=new ConnectionHistoryStore(connectionsFile);
            var connections=FillConnections(connectionStore);
            var clears=new List<ClearTiming>
            {
                TimeClear(connectionStore,"connections","oneApp",connectionsFile,HistoryClear.Create("app0.exe",allTime:true,DateTimeOffset.UtcNow),connectionStore.BeginClear,connectionStore.ClearStep),
                TimeClear(connectionStore,"connections","allApps",connectionsFile,HistoryClear.Create(null,allTime:true,DateTimeOffset.UtcNow),connectionStore.BeginClear,connectionStore.ClearStep),
            };
            var usageStore=new UsageHistoryStore(usageFile);
            var usage=FillUsage(usageStore,usageFile);
            clears.Add(TimeClear(usageStore,"usage","oneApp",usageFile,HistoryClear.Create("app0.exe",allTime:true,DateTimeOffset.UtcNow),usageStore.BeginClear,usageStore.ClearStep));
            clears.Add(TimeClear(usageStore,"usage","allApps",usageFile,HistoryClear.Create(null,allTime:true,DateTimeOffset.UtcNow),usageStore.BeginClear,usageStore.ClearStep));
            MeasurementRecorder.Write("history-clear",new{fills=new{connections,usage},clears});
            foreach(var clear in clears)
            {
                Assert.True(clear.largestWalBytes<=8*Megabyte,$"{clear.store} {clear.scope} clear: WAL reached {clear.largestWalBytes} bytes with no reader.");
                Assert.True(clear.finished,$"{clear.store} {clear.scope} clear did not finish: {clear.error}");
            }
        }
        finally
        {
            try {Directory.Delete(root,true);}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {}
        }
    }

    /// <summary>The 0.16.0 fill with pairs 0 and 1 sharing app0.exe, so 2 of the 20 pairs belong to one application.</summary>
    private static object FillConnections(ConnectionHistoryStore store)
    {
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
    private static object FillUsage(UsageHistoryStore store,string file)
    {
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

    /// <summary>
    /// Times one clear with no reader: BeginClear, then 250 ms steps until Finished, each step timed. The largest -wal is read in
    /// the store's clearCommitted seam (set by reflection), after each clear transaction commits and before its TRUNCATE checkpoint.
    /// </summary>
    private static ClearTiming TimeClear(object store,string name,string scope,string file,HistoryClear clear,Action<HistoryClear> begin,Func<TimeSpan,HistoryClearProgress> step)
    {
        long largestWal=0;
        store.GetType().GetField("clearCommitted",BindingFlags.NonPublic|BindingFlags.Instance)!
            .SetValue(store,(Action<int>)(_=>largestWal=Math.Max(largestWal,File.Exists(file+"-wal")?new FileInfo(file+"-wal").Length:0)));
        var stepMs=new List<double>();
        var total=Stopwatch.StartNew();
        bool finished=false; string? error=null;
        try
        {
            begin(clear);
            while(!finished)
            {
                var watch=Stopwatch.StartNew();
                finished=step(TimeSpan.FromMilliseconds(250)).Finished;
                stepMs.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) {error=e.GetType().Name;}
        double p95=stepMs.Count==0?0:ProcessMeasurementTests.Percentile(stepMs.Order().ToArray(),0.95);
        return new(name,scope,Math.Round(total.Elapsed.TotalMilliseconds,1),stepMs.Count,p95,largestWal,finished,error);
    }

    private sealed record ClearTiming(string store,string scope,double totalMs,int steps,double p95StepMs,long largestWalBytes,bool finished,string? error);
}
