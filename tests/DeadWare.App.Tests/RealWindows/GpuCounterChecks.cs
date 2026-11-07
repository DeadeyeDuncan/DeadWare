using System.Diagnostics;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// The real GPU counters on GitHub's Windows runner (spec, Testing → Real Windows), which has no real GPU workload. The parse
/// check fails the build; the timing measurement records a floor and always passes. Neither records an adapter description
/// or a process name.
/// </summary>
[Collection("RealWindows")]
public sealed class GpuCounterChecks
{
    private const int Reads=240,Listed=10;
    private static readonly GpuCounterKind[] Kinds=[GpuCounterKind.Engine,GpuCounterKind.Dedicated,GpuCounterKind.Shared];
    // Array statuses the reader takes as a success, as no instances, or as not ready for one read.
    private static readonly uint[] Accepted=[GpuPdhStatus.CStatusValidData,GpuPdhStatus.NoData,GpuPdhStatus.CStatusNoInstance,GpuPdhStatus.CStatusInvalidData,GpuPdhStatus.InvalidData,GpuPdhStatus.CalcNegativeDenominator,GpuPdhStatus.CalcNegativeValue];

    [ElevatedGitHubFact] public async Task Gpu_counters_open_and_parse()
    {
        var scans=new WindowsProcesses();
        var first=scans.Scan(0,1)!;
        var api=new PdhGpuApi();
        try
        {
            var open=api.Open();
            if(open is GpuPdhStatus.CStatusNoObject or GpuPdhStatus.CStatusNoCounter) {MeasurementRecorder.Write("gpu-counters",new{counters="absent"});return;}
            var collect1=api.Collect();
            var early=ReadAll(api);
            await Task.Delay(1000);
            var collect2=api.Collect();
            var late=ReadAll(api);
            var second=scans.Scan(1,1)!;
            IReadOnlyList<GpuAdapterInfo>? adapters;
            string? adaptersError=null;
            try {adapters=api.Adapters();}
            catch(Exception e) {adapters=null;adaptersError=$"{e.GetType().Name} (0x{e.HResult:X8})";}

            var pids=first.Processes.Select(p=>p.Pid).Concat(second.Processes.Select(p=>p.Pid)).ToHashSet();
            var parsed=Kinds.ToDictionary(kind=>kind,kind=>late[(int)kind].Items.Select(item=>new Parsed(item,GpuCounterInstance.TryParse(item.Name,kind,out var instance)?instance:null)).ToList());
            var unmatched=Kinds.SelectMany(kind=>parsed[kind].Where(p=>p.Instance is {} instance&&!pids.Contains(instance.Pid)).Select(p=>(Kind:kind,Pid:p.Instance!.Pid))).ToList();
            var noInstances=NoInstances(collect1)||NoInstances(collect2)||late.All(array=>NoInstances(array.Status)||array.Items.Count==0);
            var engineValues=late[(int)GpuCounterKind.Engine].Items.Where(Ready).Select(item=>item.Value).ToList();
            var memoryValues=late[(int)GpuCounterKind.Dedicated].Items.Concat(late[(int)GpuCounterKind.Shared].Items).Where(Ready).Select(item=>item.Value).ToList();
            var luids=Kinds.SelectMany(kind=>parsed[kind]).Select(p=>p.Instance).OfType<GpuCounterInstance>().GroupBy(instance=>instance.Luid).OrderBy(group=>group.Key).Select(group=>
            {
                var adapter=adapters?.FirstOrDefault(a=>a.Luid==group.Key);
                return new{luid=LuidText(group.Key),listed=adapters is null?(bool?)null:adapter is not null,flags=adapter?.Flags,engineTypes=group.Select(instance=>instance.Type).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray()};
            }).ToArray();
            MeasurementRecorder.Write("gpu-counters",new
            {
                counters="present",
                open=Hex(open),collect1=Hex(collect1),collect2=Hex(collect2),
                early=Arrays(early),late=Arrays(late),
                instances=noInstances?(object)0:Kinds.ToDictionary(Key,kind=>late[(int)kind].Items.Count),
                skipped=Kinds.ToDictionary(Key,kind=>parsed[kind].Count(p=>p.Instance is null)),
                unmatched=unmatched.Count,
                unmatchedPids=unmatched.Select(u=>u.Pid).Distinct().Take(Listed).ToArray(),
                skippedNames=Kinds.SelectMany(kind=>parsed[kind]).Where(p=>p.Instance is null).Select(p=>p.Item.Name).Take(Listed).ToArray(),
                readyEngines=engineValues.Count,readyMemory=memoryValues.Count,
                adapters=adapters?.Count,adapterFlags=adapters?.Select(a=>a.Flags).ToArray(),adaptersError,
                luids,
            });

            Assert.True(open==0,$"Open failed with 0x{open:X8}");
            if(noInstances)return;
            Assert.True(collect1==0,$"The first collect failed with 0x{collect1:X8}");
            Assert.True(collect2==0,$"The second collect failed with 0x{collect2:X8}");
            foreach(var (read,arrays) in new[]{("early",early),("late",late)})
                foreach(var kind in Kinds)
                    Assert.True(Accepted.Contains(arrays[(int)kind].Status),$"The {read} {Key(kind)} array status was 0x{arrays[(int)kind].Status:X8}");
            Assert.True(unmatched.Count==0,unmatched.Select(u=>$"A {Key(u.Kind)} instance has PID {u.Pid}, which is in neither scan").FirstOrDefault());
            Assert.True(adapters is {Count:>0},$"DXGI listed no adapters ({adaptersError??(adapters is null?"failed":"an empty list")})");
            foreach(var adapter in adapters!)Assert.True(adapter.Description.Length>0,$"The adapter with LUID {LuidText(adapter.Luid)} has no description");
            foreach(var value in engineValues)Assert.True(double.IsFinite(value)&&value is >=0 and <=100,$"A ready engine value was {value}");
            foreach(var value in memoryValues)Assert.True(double.IsFinite(value)&&value>=0&&value==Math.Floor(value),$"A ready memory value was {value}");
        }
        finally
        {
            api.Close();
        }
    }

    [GitHubWindowsFact] public async Task Gpu_counter_timing_over_240_reads()
    {
        var source=new WindowsGpuCounters();
        try
        {
            var clock=Stopwatch.StartNew();
            var times=new List<double>();
            double? firstMs=null;
            GpuCounterRead? last=null;
            int notReady=0,unavailable=0;
            for(var i=0;i<Reads;i++)
            {
                if(i>0)await Task.Delay(100);
                var started=Stopwatch.GetTimestamp();
                var read=source.Read(clock.Elapsed.TotalSeconds,1);
                var milliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if(source.LastReadOpened&&firstMs is null)firstMs=Math.Round(milliseconds,3);
                else times.Add(milliseconds);
                if(i==0&&read.State is {Available:false,Status:GpuCounterStatus.Unavailable}&&read.Instances==0&&!source.IsOpen)
                {
                    MeasurementRecorder.Write("gpu-timing",new{elevated=MeasurementRecorder.Elevated,counters="absent"});
                    return;
                }
                if(!read.EnginesReady)notReady++;
                if(!read.State.Available)unavailable++;
                last=read;
            }
            double[] sorted=[..times.Order()];
            MeasurementRecorder.Write("gpu-timing",new
            {
                elevated=MeasurementRecorder.Elevated,
                reads=Reads,
                p50Ms=sorted.Length==0?(double?)null:ProcessMeasurementTests.Percentile(sorted,0.50),
                p95Ms=sorted.Length==0?(double?)null:ProcessMeasurementTests.Percentile(sorted,0.95),
                firstMs,
                engines=last!.Engines.Count,dedicated=last.Dedicated.Count,shared=last.Shared.Count,
                skipped=last.Skipped,
                adapters=last.Adapters.Count,
                state=last.State.Available?"available":last.State.Status,
                instances=last.Instances,
                notReadyReads=notReady,unavailableReads=unavailable,
            });
        }
        finally
        {
            source.Close();
        }
    }

    private sealed record Parsed(GpuRawItem Item,GpuCounterInstance? Instance);
    private static GpuRawArray[] ReadAll(IGpuCounterApi api)=>[..Kinds.Select(api.Read)];
    private static bool NoInstances(uint status)=>status is GpuPdhStatus.NoData or GpuPdhStatus.CStatusNoInstance;
    private static bool Ready(GpuRawItem item)=>item.CStatus is GpuPdhStatus.CStatusValidData or GpuPdhStatus.CStatusNewData;
    private static string Key(GpuCounterKind kind)=>kind switch{GpuCounterKind.Engine=>"engine",GpuCounterKind.Dedicated=>"dedicated",_=>"shared"};
    private static string Hex(uint status)=>$"0x{status:X8}";
    // As the counter names write it: luid_0x<hi>_0x<lo>.
    private static string LuidText(long luid)=>$"0x{(uint)(luid>>32):X8}_0x{(uint)luid:X8}";
    // Each kind's array status and its item count per CStatus.
    private static Dictionary<string,object> Arrays(GpuRawArray[] arrays)=>Kinds.ToDictionary(Key,kind=>(object)new{status=Hex(arrays[(int)kind].Status),items=arrays[(int)kind].Items.GroupBy(item=>item.CStatus).OrderBy(group=>group.Key).ToDictionary(group=>Hex(group.Key),group=>group.Count())});
}
