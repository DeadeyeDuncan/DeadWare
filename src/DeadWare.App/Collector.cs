using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DeadWare.Core;
using LibreHardwareMonitor.Hardware;

namespace DeadWare.App;
public record SensorReading(string Id,string Hardware,string Name,string Unit,double? Value,
    string HardwareId="",string HardwareType="",string SensorType="",string Validity="Unknown");
public record Reading(Snapshot Sample,IReadOnlyList<Device> Gpus,IReadOnlyList<SensorReading> Sensors,IReadOnlyList<ProcessTraffic> Processes,string CpuName,string HardwareStatus,string NetworkStatus,string EtwStatus,bool Elevated,IReadOnlyDictionary<string,double>? ProviderMilliseconds=null,DiagnosticFailure? EtwFailure=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ConnectionSample? Connections {get;init;}
    public BatteryReading Battery {get;init;}=BatteryReading.Unavailable;
}

/// <summary>Reads the first GPU RPM fan from existing publications and keeps its source through outages.</summary>
public sealed class GpuFanSpeed
{
    private readonly Dictionary<string,string> sources=new(StringComparer.Ordinal);
    public double? Read(string hardwareId,IReadOnlyList<SensorReading> sensors)
    {
        var fans=sensors.Where(s=>s.HardwareId==hardwareId&&s.SensorType=="Fan"&&s.Unit=="RPM"&&
            s.HardwareType is "GpuAmd" or "GpuNvidia" or "GpuIntel").ToArray();
        if(!sources.TryGetValue(hardwareId,out var source))
        {
            // Choose metadata rather than a live value, so an unavailable first fan
            // never changes which physical fan the graph represents.
            var first=fans.OrderBy(s=>s.Id,StringComparer.Ordinal).FirstOrDefault();
            if(first is null)return null;
            sources[hardwareId]=source=first.Id;
        }
        double? value=fans.FirstOrDefault(s=>s.Id==source)?.Value;
        return value is >=0&&double.IsFinite(value.Value)?value:null;
    }
}

/// <summary>Reads the CPU cooler's pump (or CPU Fan header) RPM and keeps its source through outages.</summary>
public sealed class CpuPumpSpeed
{
    private string? source;
    /// <summary>Board and AIO headers are machine-wide, so they describe the CPU cooler only on single-socket hosts.</summary>
    public static SensorReading? Select(IEnumerable<SensorReading> sensors)
    {
        var fans=sensors.Where(s=>s.SensorType=="Fan"&&s.Unit=="RPM"&&s.HardwareType is "SuperIO" or "Cooler" or "EmbeddedController")
            .OrderBy(s=>s.Id,StringComparer.Ordinal).ToArray();
        // Unlabeled headers such as "Fan #2" cannot be attributed to the CPU cooler.
        return fans.FirstOrDefault(s=>s.Name.Contains("Pump",StringComparison.OrdinalIgnoreCase))
            ??fans.FirstOrDefault(s=>s.Name.Equals("CPU Fan",StringComparison.OrdinalIgnoreCase));
    }
    public double? Read(IReadOnlyList<SensorReading> sensors,int cpuCount)
    {
        if(cpuCount!=1)return null;
        source??=Select(sensors)?.Id;
        if(source is null)return null;
        double? value=sensors.FirstOrDefault(s=>s.Id==source)?.Value;
        return value is >=0&&double.IsFinite(value.Value)?value:null;
    }
}

/// <summary>One serial background loop owns all hardware updates; windows only read immutable publications.</summary>
public sealed class Collector : IAsyncDisposable
{
    private readonly CollectorOptions options;
    private volatile string stage="Not started";
    public string Stage=>stage;
    public Task Completion=>worker??Task.CompletedTask;
    public Collector(CollectorOptions? options=null) {this.options=options??new();SetPollingInterval(this.options.PollingIntervalSeconds);etw=new(this.options.DiagnosticTimings);}
    private double pollingIntervalSeconds=1;
    public double PollingIntervalSeconds=>Volatile.Read(ref pollingIntervalSeconds);
    public void SetPollingInterval(double intervalSeconds)=>Volatile.Write(ref pollingIntervalSeconds,PollingPolicy.Normalize(intervalSeconds));
    private readonly CancellationTokenSource cancellation=new();
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly WindowsCounters windows=new();
    private readonly GpuFanSpeed gpuFanSpeed=new();
    private readonly CpuPumpSpeed cpuPumpSpeed=new();
    private readonly EtwNetwork etw;
    private bool lowLevelAvailable;
    private Task? worker;
    private Reading? latest;
    public Reading? Latest=>Volatile.Read(ref latest);
    public double ElapsedSeconds=>clock.Elapsed.TotalSeconds;
    public SampleHistory History {get;}=new();
    public event Action<Reading>? SampleAvailable;
    public event Action<ConnectionSample>? FinalConnectionsAvailable;
    private readonly object etwStopGate=new(),publicationGate=new();
    private Task? etwStop,disposal;
    public static bool IsElevated {get;}=CheckElevation();
    private string EtwStatus=>options.EtwEnabled?etw.Status:"Per-app accounting disabled for diagnostic isolation";
    private static bool CheckElevation() {using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
    public void Start()=>worker??=Task.Run(RunAsync);
    private async Task RunAsync()
    {
        Computer? computer=null;
        string initialization="";
        double hardwareOpenMilliseconds=0,etwOpenMilliseconds=0;
        double intervalSeconds=PollingIntervalSeconds;
        try
        {
            long initializationStarted=Stopwatch.GetTimestamp();
            stage="Starting ETW";
            if(options.EtwEnabled)etw.Start();
            etwOpenMilliseconds=Stopwatch.GetElapsedTime(initializationStarted).TotalMilliseconds;
            if(options.HardwareEnabled)
            {
                stage="Opening hardware providers";initializationStarted=Stopwatch.GetTimestamp();
                lowLevelAvailable=IsElevated&&WindowsCounters.CanAccessPawnIo();
                var sources=options.Sources;
                computer=new Computer {IsCpuEnabled=sources.Cpu,IsGpuEnabled=sources.Gpu,IsMemoryEnabled=sources.Memory,IsMotherboardEnabled=sources.Motherboard,IsControllerEnabled=sources.Controllers,IsStorageEnabled=sources.Storage,IsNetworkEnabled=false};
                try { computer.Open(); } catch(Exception e) { initialization=$"Hardware discovery failed ({e.GetType().Name}). Windows counters remain available."; }
                hardwareOpenMilliseconds=Stopwatch.GetElapsedTime(initializationStarted).TotalMilliseconds;
            }
            while(!cancellation.IsCancellationRequested)
            {
                intervalSeconds=PollingIntervalSeconds;
                double started=clock.Elapsed.TotalSeconds;
                try
                {
                    Dictionary<string,double>? timings=options.DiagnosticTimings?new(){["open/hardware"]=hardwareOpenMilliseconds,["open/etw"]=etwOpenMilliseconds}:null;
                    var values=new Dictionary<string,double?>();
                    var sensors=new List<SensorReading>();
                    var gpus=new List<Device>();
                    var errors=new List<string>();
                    string cpuName="Windows processor";
                    double? cpuLoad=null,cpuTemp=null;
                    foreach(var hardware in computer?.Hardware??[])
                    {
                        stage=$"Polling {hardware.HardwareType}";
                        var hardwareSensors=new List<SensorReading>();
                        ReadHardware(hardware,hardwareSensors,errors,lowLevelAvailable,timings);
                        sensors.AddRange(hardwareSensors);
                        if(hardware.HardwareType==HardwareType.Cpu)
                        {
                            cpuName=hardware.Name;
                            cpuLoad??=Choose(hardwareSensors,"%","CPU Total");
                            cpuTemp??=Choose(hardwareSensors,"°C","CPU Package","Core (Tctl/Tdie)","CPU (Tctl/Tdie)","Core (Tdie)","Core Average");
                        }
                        if(hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                        {
                            var id=hardware.Identifier.ToString();gpus.Add(new(id,hardware.Name,hardwareSensors.Any(s=>s.Unit=="°C"&&s.Name is "GPU Core" or "GPU Temperature")));
                            values[id+"/usage"]=Choose(hardwareSensors,"%","GPU Core","GPU D3D 3D");
                            values[id+"/temperature"]=Choose(hardwareSensors,"°C","GPU Core","GPU Temperature");
                            values[id+"/fan"]=gpuFanSpeed.Read(id,hardwareSensors);
                        }
                    }
                    stage="Reading Windows counters";
                    long providerStarted=Stopwatch.GetTimestamp();var fallbackCpu=windows.Cpu();
                    if(timings is not null)timings["windows/cpu"]=Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds;
                    values["cpu/usage"]=cpuLoad??fallbackCpu;
                    values["cpu/temperature"]=cpuTemp;
                    values["cpu/pump"]=cpuPumpSpeed.Read(sensors,sensors.Where(s=>s.HardwareType=="Cpu"&&s.HardwareId.Length>0).Select(s=>s.HardwareId).Distinct(StringComparer.Ordinal).Count());
                    providerStarted=Stopwatch.GetTimestamp();var memory=windows.Memory();values["memory/used"]=memory.Used;values["memory/total"]=memory.Total;
                    values["memory/usage"]=MemoryUsage.Percentage(memory.Used,memory.Total);
                    if(timings is not null)timings["windows/memory"]=Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds;
                    providerStarted=Stopwatch.GetTimestamp();var network=windows.Network(clock.Elapsed.TotalSeconds);
                    if(timings is not null)timings["windows/network"]=Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds;
                    values["network/download"]=network.Down;values["network/upload"]=network.Up;
                    var battery=WindowsBattery.Read();
                    foreach(var sensor in sensors) values[sensor.Id]=sensor.Value;
                    var now=clock.Elapsed.TotalSeconds;
                    var sample=new Snapshot(DateTimeOffset.Now,now,new System.Collections.ObjectModel.ReadOnlyDictionary<string,double?>(values)) {PollingIntervalSeconds=intervalSeconds};
                    string hardwareStatus=initialization.Length>0?initialization:!options.HardwareEnabled?"Hardware collection disabled for diagnostic isolation":$"{sensors.Count} sensors · {(IsElevated?"Administrator":"Standard user")} · {(options.ExtendedSensors?"Extended sensors enabled":"CPU/GPU sensors")}";
                    if(cpuTemp is null) hardwareStatus+=" · CPU temperature unavailable (hardware support, elevation or PawnIO may be required).";
                    if(gpus.Count==0&&options.HardwareEnabled) hardwareStatus+=" · No supported GPU discovered.";
                    if(errors.Count>0) hardwareStatus+=" · "+string.Join("; ",errors.Distinct());
                    stage="Reading ETW counters";
                    // Snapshot and delivery are one step, so the final drain never overtakes (and
                    // makes ConnectionService drop) a sample whose observations were already taken.
                    lock(publicationGate)
                    {
                        providerStarted=Stopwatch.GetTimestamp();
                        var capture=etw.TakeSnapshot(sample.Timestamp,now,options.EtwEnabled);
                        if(timings is not null){timings["etw/snapshot"]=Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds;timings["poll/total"]=(clock.Elapsed.TotalSeconds-started)*1000;}
                        double elapsed=clock.Elapsed.TotalSeconds-started;
                        if(elapsed>=intervalSeconds)hardwareStatus+=$" · Slow sample ({elapsed:0.0}s); pausing before the next poll.";
                        var reading=new Reading(sample,gpus.AsReadOnly(),sensors.AsReadOnly(),capture.Processes,cpuName,hardwareStatus,network.Status,EtwStatus,IsElevated,timings,etw.Failure) {Battery=battery,Connections=capture.Connections};
                        History.Add(sample);Volatile.Write(ref latest,reading);SampleAvailable?.Invoke(reading);
                    }
                }
                catch(Exception e)
                {
                    var sample=new Snapshot(DateTimeOffset.Now,clock.Elapsed.TotalSeconds,new Dictionary<string,double?>()) {PollingIntervalSeconds=intervalSeconds};
                    lock(publicationGate)
                    {
                        var failed=new Reading(sample,Latest?.Gpus??[],[],[],Latest?.CpuName??"Processor",$"Collection failed ({e.GetType().Name}); retrying at the selected polling interval.","Interface readings unavailable",EtwStatus,IsElevated,EtwFailure:etw.Failure) {Connections=etw.TakeSnapshot(sample.Timestamp,sample.MonotonicSeconds,available:false).Connections};
                        History.Add(sample);Volatile.Write(ref latest,failed);SampleAvailable?.Invoke(failed);
                    }
                }
                stage="Idle between samples";
                await Task.Delay(PollingPolicy.DelayAfter(clock.Elapsed.TotalSeconds-started,PollingIntervalSeconds),cancellation.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        catch(Exception e)
        {
            var sample=new Snapshot(DateTimeOffset.Now,clock.Elapsed.TotalSeconds,new Dictionary<string,double?>()) {PollingIntervalSeconds=intervalSeconds};
            lock(publicationGate)
            {
                var failed=new Reading(sample,[],[],[],"Processor",$"Collector stopped ({e.GetType().Name}). Restart DeadWare to retry.","Interface readings unavailable",EtwStatus,IsElevated,EtwFailure:etw.Failure) {Connections=etw.TakeSnapshot(sample.Timestamp,sample.MonotonicSeconds,available:false).Connections};
                History.Add(sample);Volatile.Write(ref latest,failed);SampleAvailable?.Invoke(failed);
            }
        }
        finally
        {
            stage="Stopping providers";
            try {computer?.Close();} finally {await StopEtwAsync();}
            stage="Stopped";
        }
    }
    private static double? Choose(List<SensorReading> sensors,string unit,params string[] names)
    {
        foreach(var name in names)
        {
            var found=sensors.FirstOrDefault(s=>s.Unit==unit&&s.Name.Equals(name,StringComparison.OrdinalIgnoreCase)&&s.Value.HasValue);
            if(found is not null) return found.Value;
        }
        // Temperatures may have model-specific names. Core sensors are safer than unrelated hotspot/memory sensors.
        if(unit=="°C") return sensors.FirstOrDefault(s=>s.Unit==unit&&s.Name.Contains("Core",StringComparison.OrdinalIgnoreCase)&&s.Value.HasValue)?.Value;
        return null;
    }
    private static void ReadHardware(IHardware h,List<SensorReading> sensors,List<string> errors,bool lowLevelAvailable,Dictionary<string,double>? timings)
    {
        long started=Stopwatch.GetTimestamp();
        try
        {
            h.Update();
            foreach(var s in h.Sensors)
            {
                bool lowLevel=h.HardwareType==HardwareType.Cpu && s.SensorType is SensorType.Temperature or SensorType.Power or SensorType.Voltage or SensorType.Clock;
                double? v=SensorValidity.Normalize(s.Value,lowLevel,lowLevelAvailable);
                if(s.SensorType==SensorType.Load && (v<0||v>100)) v=null;
                sensors.Add(new(s.Identifier.ToString(),h.Name,s.Name,Unit(s.SensorType),v,
                    h.Identifier.ToString(),h.HardwareType.ToString(),s.SensorType.ToString(),
                    v.HasValue?"Available":lowLevel&&!lowLevelAvailable?"Low-level access unavailable":"Missing or invalid provider value"));
            }
        }
        catch(Exception e) {errors.Add($"{h.Name}: {e.GetType().Name}");}
        finally {if(timings is not null)timings["hardware"+h.Identifier]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
        foreach(var sub in h.SubHardware) ReadHardware(sub,sensors,errors,lowLevelAvailable,timings);
    }
    private static string Unit(SensorType t)=>t switch {SensorType.Load=>"%",SensorType.Temperature=>"°C",SensorType.Clock=>"MHz",SensorType.Fan=>"RPM",SensorType.Voltage=>"V",SensorType.Current=>"A",SensorType.Power=>"W",SensorType.Data=>"GB",SensorType.SmallData=>"MB",SensorType.Throughput=>"B/s",SensorType.Control=>"%",SensorType.Frequency=>"Hz",_=>t.ToString()};
    public ValueTask DisposeAsync()
    {
        // Stop our trace even if a hardware provider is stuck. Native stop never runs on the UI thread.
        lock(etwStopGate)return new(disposal??=Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            var networkStop=StopNetworkCaptureAsync();
            try {if(worker is not null)await worker.ConfigureAwait(false);}
            finally {await networkStop.ConfigureAwait(false);}
        }
        finally {cancellation.Dispose();}
    }
    /// <summary>Cancels collection and stops network capture (final drain, publication, disposal) without waiting for the hardware worker.</summary>
    internal Task StopNetworkCaptureAsync()
    {
        // Callbacks run on the pool, so a worker resuming into a stuck provider Close never blocks this caller.
        lock(etwStopGate) {if(etwStop is null)_=cancellation.CancelAsync();return StopEtwAsync();}
    }
    private Task StopEtwAsync()
    {
        // Publish the shared stop under the short lock; native stop runs on the pool so no caller blocks on it.
        lock(etwStopGate)return etwStop??=Task.Run(StopEtwCoreAsync);
    }
    private async Task StopEtwCoreAsync()
    {
        try
        {
            await etw.StopAsync().ConfigureAwait(false);
            lock(publicationGate)FinalConnectionsAvailable?.Invoke(etw.TakeSnapshot(DateTimeOffset.UtcNow,clock.Elapsed.TotalSeconds,options.EtwEnabled).Connections);
        }
        finally {await etw.DisposeAsync().ConfigureAwait(false);}
    }
}

internal sealed class WindowsCounters
{
    public static bool CanAccessPawnIo()
    {
        if(!LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled)return false;
        // Open the existing device under normal Windows access checks; never install or modify a driver.
        using var handle=CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",0xC0000000,3,IntPtr.Zero,3,0x80,IntPtr.Zero);
        return !handle.IsInvalid;
    }
    private ulong? idle,kernel,user;
    private readonly Dictionary<string,(CounterRate Down,CounterRate Up)> adapters=new();
    public double? Cpu()
    {
        if(!GetSystemTimes(out var i,out var k,out var u)) return null;
        double? result=null;
        if(idle.HasValue && i>=idle.Value && k>=kernel!.Value && u>=user!.Value)
        {
            ulong total=(k-kernel.Value)+(u-user.Value),unused=i-idle.Value;
            if(total>0 && unused<=total) result=100d*(total-unused)/total;
        }
        idle=i;kernel=k;user=u;return result;
    }
    public (double? Used,double? Total) Memory()
    {
        var state=new MemoryStatus {Length=(uint)Marshal.SizeOf<MemoryStatus>()};
        return GlobalMemoryStatusEx(ref state)?(state.TotalPhysical-state.AvailablePhysical,state.TotalPhysical):(null,null);
    }
    public (double? Down,double? Up,string Status) Network(double now)
    {
        double down=0,up=0;bool valid=true;int count=0;
        var active=new HashSet<string>();
        try
        {
            foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
            {
                count++;active.Add(adapter.Id);
                if(!adapters.TryGetValue(adapter.Id,out var rates)) adapters[adapter.Id]=rates=(new(),new());
                try
                {
                    var stats=adapter.GetIPStatistics();var d=rates.Down.Update(stats.BytesReceived,now);var u=rates.Up.Update(stats.BytesSent,now);
                    if(d is null || u is null) valid=false; else {down+=d.Value;up+=u.Value;}
                }
                catch(NetworkInformationException) {adapters.Remove(adapter.Id);valid=false;}
            }
            foreach(var id in adapters.Keys.Except(active).ToArray()) adapters.Remove(id);
            return count==0?(null,null,"No active network interface"):(valid?down:null,valid?up:null,$"{count} active interface{(count==1?"":"s")} · totals include virtual adapters; VPN traffic can be counted twice{(!valid?" · waiting for counter baseline / reset":"")}");
        }
        catch(Exception e) {return (null,null,$"Interface counters unavailable ({e.GetType().Name})");}
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus {public uint Length,Load;public ulong TotalPhysical,AvailablePhysical,TotalPageFile,AvailablePageFile,TotalVirtual,AvailableVirtual,AvailableExtendedVirtual;}
}
