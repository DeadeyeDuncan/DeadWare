using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using DeadWare.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// Real-Windows measurement of per-minute connection history: storage rates for four loads, the time to fill a store to its
/// 256 MiB cap, and History read timings on that full file. It records numbers and decides nothing.
/// </summary>
[Collection("RealWindows")]
public sealed class ConnectionHistoryMeasurementTests
{
    private const int RateHours=12,FillHourLimit=240,Pairs=20;
    private const double Megabyte=1<<20,ChurnProbability=0.05;
    private const long ReceivedPerMinute=60_000,SentPerMinute=6_000;
    // A steady pair is seen from 0.5 s to 59.5 s of every minute.
    private const double FirstSecond=0.5,LastSecond=59.5;
    private static readonly GeoPoint Origin=new(41.88,-87.63);
    private static readonly FieldInfo DefaultTempStore=typeof(ConnectionHistoryStore).GetField("defaultTempStore",BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingFieldException(nameof(ConnectionHistoryStore),"defaultTempStore");
    private static readonly FieldInfo DestinationStepsTimed=typeof(ConnectionHistoryStore).GetField("destinationStepsTimed",BindingFlags.Instance|BindingFlags.NonPublic)
        ??throw new MissingFieldException(nameof(ConnectionHistoryStore),"destinationStepsTimed");

    [GitHubWindowsFact] public void Connection_history_rates_and_read_timings()
    {
        string root=Path.Combine(Path.GetTempPath(),$"deadware-connection-history-{Guid.NewGuid():N}");
        try
        {
            var rates=new
            {
                idle=Rate(Path.Combine(root,"idle"),new Load(0,false)),
                onePair=Rate(Path.Combine(root,"one-pair"),new Load(1,false)),
                twentyPairs=Rate(Path.Combine(root,"twenty-pairs"),new Load(Pairs,false)),
                twentyPairsChurn=Rate(Path.Combine(root,"twenty-pairs-churn"),new Load(Pairs,true)),
            };
            string file=Path.Combine(root,"fill","connections.db");
            var (store,fill,end)=Fill(file);
            var reads=Reads(store,end);
            var strip30d=StripReads(store,end);
            MeasurementRecorder.Write("connection-history",new{rates,fill,reads,strip30d});
        }
        finally
        {
            try {Directory.Delete(root,true);}
            catch(IOException) {}
        }
    }

    /// <summary>MiB per simulated hour: 12 hours of one load, one Append per simulated minute as the writer makes.</summary>
    private static double Rate(string folder,Load load)
    {
        var store=Open(Path.Combine(folder,"connections.db"));
        var start=FlooredNow()-TimeSpan.FromHours(RateHours);
        for(int minute=0;minute<RateHours*60;minute++)
        {
            var from=start.AddMinutes(minute);
            var (records,coverage)=load.Minutes(from,1);
            store.Append(records,coverage,from.AddMinutes(1).AddSeconds(3));
        }
        return Math.Round(store.StorageBytes/(double)RateHours/Megabyte,3);
    }

    /// <summary>
    /// Writes 20 steady pairs, one Append per simulated hour, until the file is within 4 MiB of its cap and the oldest minutes
    /// have been evicted, or for 240 hours. Returns the filled store, the fill's values and the last simulated minute's end.
    /// </summary>
    private static (ConnectionHistoryStore Store,object Values,DateTimeOffset End) Fill(string file)
    {
        var store=Open(file);
        var load=new Load(Pairs,false);
        var start=FlooredNow()-TimeSpan.FromDays(10);
        var clock=Stopwatch.StartNew();
        int hours=0;
        while(hours<FillHourLimit)
        {
            var from=start.AddHours(hours);
            var (records,coverage)=load.Minutes(from,60);
            store.Append(records,coverage,from.AddHours(1).AddSeconds(3));
            hours++;
            if(new FileInfo(file).Length<ConnectionHistoryStore.MaximumDatabaseBytes-4*(long)Megabyte)continue;
            var end=start.AddHours(hours);
            if(store.Read(new(end.AddMinutes(-1),end)).EarliestRetainedTime>start)break;
        }
        double seconds=Math.Round(clock.Elapsed.TotalSeconds,1);
        var last=start.AddHours(hours);
        bool evicted=store.Read(new(last.AddMinutes(-1),last)).Status.StartsWith("Capacity shortened retention.",StringComparison.Ordinal);
        long trafficRows;
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Mode=SqliteOpenMode.ReadOnly,Pooling=false,DefaultTimeout=1}.ToString()))
        {
            db.Open();
            using var count=db.CreateCommand();
            count.CommandText="SELECT count(*) FROM traffic";
            trafficRows=Convert.ToInt64(count.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        return (store,new{seconds,hours,trafficRows,fileBytes=new FileInfo(file).Length,evicted},last);
    }

    /// <summary>
    /// The 31 d, 7 d, 1 d and 1-minute reads ending at <paramref name="end"/>, with temp_store MEMORY and then SQLite's default.
    /// Each entry also records the Destinations table's History steps 1–7, which the store times inside the read.
    /// </summary>
    private static object[] Reads(ConnectionHistoryStore store,DateTimeOffset end)
    {
        var ranges=new (string Name,TimeSpan Length)[]{("31d",TimeSpan.FromDays(31)),("7d",TimeSpan.FromDays(7)),("1d",TimeSpan.FromDays(1)),("1min",TimeSpan.FromMinutes(1))};
        // Warm the file cache and the code paths; a failure here shows again in the recorded 1 d read.
        try {store.Read(new(end-TimeSpan.FromDays(1),end));}
        catch(Exception) {}
        var reads=new List<object>();
        foreach(bool defaultTemp in new[]{false,true})
        {
            DefaultTempStore.SetValue(store,defaultTemp);
            foreach(var (name,length) in ranges)
            {
                SQLitePCL.raw.sqlite3_memory_highwater(1);
                long started=Stopwatch.GetTimestamp();
                double? ms=null,destinationStepsMs=null;long? totalRows=null;string? error=null;
                DestinationStepsTimed.SetValue(store,new Action<TimeSpan>(elapsed=>destinationStepsMs=Math.Round(elapsed.TotalMilliseconds,3)));
                try
                {
                    var view=store.Read(new(end-length,end));
                    ms=Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds,3);
                    totalRows=view.TotalRows;
                }
                catch(Exception e) {error=e.GetType().Name;}
                finally {DestinationStepsTimed.SetValue(store,null);}
                long heapHighwaterBytes=SQLitePCL.raw.sqlite3_memory_highwater(0);
                reads.Add(new{range=name,tempStore=defaultTemp?"default":"memory",ms,destinationStepsMs,heapHighwaterBytes,totalRows,error});
            }
        }
        return reads.ToArray();
    }

    /// <summary>
    /// Five 30-day reads [end − 30 d, end) with temp_store MEMORY, then five with the busiest application's exact filter: the key of
    /// the first run's first strip application. The key and its name are never recorded.
    /// </summary>
    private static object StripReads(ConnectionHistoryStore store,DateTimeOffset end)
    {
        const int Runs=5;
        DefaultTempStore.SetValue(store,false);
        var interval=new ConnectionHistoryQuery(end-TimeSpan.FromDays(30),end);
        string? busiest=null;
        object Timings(ConnectionHistoryQuery query,bool first)
        {
            var times=new List<double>(Runs);string? error=null;
            for(int run=0;run<Runs;run++)
            {
                long started=Stopwatch.GetTimestamp();
                try
                {
                    var view=store.Read(query);
                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if(first&&run==0)busiest=view.Strip?.Applications.FirstOrDefault()?.Key;
                }
                catch(Exception e) {error??=e.GetType().Name;}
            }
            times.Sort();
            // Nearest rank over the successful runs: p50 is the median of five, p95 the slowest.
            double? Percentile(double p)=>times.Count==0?null:Math.Round(times[(int)Math.Ceiling(p*times.Count)-1],3);
            return new{p50Ms=Percentile(0.5),p95Ms=Percentile(0.95),runs=times.Count,error};
        }
        var allApplications=Timings(interval,true);
        // Null when the first run failed or found no application.
        object? busiestApplication=busiest is null?null:Timings(interval with{Application=busiest},false);
        return new{allApplications,busiestApplication};
    }

    private static ConnectionHistoryStore Open(string file)
    {
        var store=new ConnectionHistoryStore(file);
        store.Initialize();
        return store;
    }
    private static DateTimeOffset FlooredNow()
    {
        long ticks=DateTimeOffset.UtcNow.UtcTicks;
        return new(ticks-ticks%TimeSpan.TicksPerMinute,TimeSpan.Zero);
    }

    /// <summary>
    /// The folder's output for one store, written directly: one segment, one complete coverage run per minute, and one row per
    /// pair per minute under that minute's BatchId. With churn, every simulated second replaces one pair with probability 0.05;
    /// the replaced pair and its replacement each save a row for that minute.
    /// </summary>
    private sealed class Load(int pairs,bool churn)
    {
        private readonly string segment=Guid.NewGuid().ToString("N");
        private readonly Random random=new(20);
        private readonly Pair[] current=Enumerable.Range(0,pairs).Select(i=>new Pair(100+i,i)).ToArray();
        private int nextPid=100+pairs,nextAddress=pairs;
        private long serial;

        /// <summary>The records and coverage of <paramref name="count"/> consecutive minutes from <paramref name="from"/>.</summary>
        public (List<ConnectionHistoryRecord> Records,List<ConnectionCoverage> Coverage) Minutes(DateTimeOffset from,int count)
        {
            var records=new List<ConnectionHistoryRecord>(count*(current.Length+4));
            var coverage=new List<ConnectionCoverage>(count);
            for(int i=0;i<count;i++)
            {
                var minute=from.AddMinutes(i);
                string batch=ConnectionMinuteFolder.BatchId(segment,minute,1,serial++);
                coverage.Add(new(batch,segment,minute,minute.AddMinutes(1),true));
                var first=new double[current.Length];
                Array.Fill(first,FirstSecond);
                if(churn)
                    for(int second=0;second<60;second++)
                    {
                        if(random.NextDouble()>=ChurnProbability)continue;
                        int slot=random.Next(current.Length);
                        double at=second+FirstSecond;
                        records.Add(Record(batch,minute,current[slot],first[slot],at));
                        current[slot]=new(nextPid++,nextAddress++);
                        first[slot]=at;
                    }
                for(int slot=0;slot<current.Length;slot++)records.Add(Record(batch,minute,current[slot],first[slot],LastSecond));
            }
            return (records,coverage);
        }

        // Bytes follow the seconds seen: a pair seen the whole minute receives 60,000 bytes and sends 6,000.
        private ConnectionHistoryRecord Record(string batch,DateTimeOffset minute,Pair pair,double firstSecond,double lastSecond)
        {
            double share=(lastSecond-firstSecond)/(LastSecond-FirstSecond);
            var peer=new ConnectionPeer(new ProcessIdentity(pair.Pid,1,$"app{pair.Pid-100}.exe"),"10.0.0.2",50000,Address(pair.Address),443,ConnectionProtocol.Tcp);
            var delta=new ConnectionDelta(minute,peer,(long)Math.Round(ReceivedPerMinute*share),(long)Math.Round(SentPerMinute*share),minute.AddSeconds(firstSecond),minute.AddSeconds(lastSecond));
            return new(batch,segment,delta,Location(pair.Address),Origin,true);
        }
    }

    /// <summary>One process instance talking to one remote; <see cref="Address"/> is the remote's index.</summary>
    private sealed record Pair(int Pid,int Address);

    // 20.0.0.1 to 20.0.0.250, then 20.0.1.1 and on.
    private static string Address(int index)=>string.Create(CultureInfo.InvariantCulture,$"20.0.{index/250}.{index%250+1}");
    // A located DB-IP-shaped location, distinct for every remote.
    private static ConnectionLocation Location(int index)=>new("US","United States","Illinois",string.Create(CultureInfo.InvariantCulture,$"City {index}"),
        new(Math.Round(25+index*7.3%24,4),Math.Round(-124+index*13.7%57,4)),"DB-IP City Lite");
}
