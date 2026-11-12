using System.Text.RegularExpressions;
using DeadWare.Core;
using LibreHardwareMonitor.Hardware;

namespace DeadWare.App;

/// <summary>The SMART fields DeadWare reads from one drive; a seam over DiskInfoToolkit so the summary is testable.</summary>
internal interface ISmartDevice
{
    string? Serial {get;}
    int? DriveNumber {get;}
    /// <summary>True when the drive exposed a SMART object at all.</summary>
    bool HasSmart {get;}
    /// <summary>DiskInfoToolkit's overall verdict reads <c>Bad</c>.</summary>
    bool StatusBad {get;}
    int? Temperature {get;}
    int? TemperatureWarning {get;}
    int? TemperatureCritical {get;}
    double? Life {get;}
    ulong PowerOnHours {get;}
    IReadOnlyList<SmartAttributeReading> Attributes {get;}
}
internal sealed record SmartAttributeReading(byte Id,double Value,byte Threshold,ulong Raw);

/// <summary>Adapts LibreHardwareMonitor's public <see cref="LibreHardwareMonitor.Hardware.Storage.StorageDevice"/> (0.9.6, DiskInfoToolkit 1.1.2).</summary>
internal sealed class LibreSmartDevice(LibreHardwareMonitor.Hardware.Storage.StorageDevice device) : ISmartDevice
{
    private DiskInfoToolkit.SmartInfo? Smart=>device.Storage?.Smart;
    public string? Serial=>device.Storage?.SerialNumber;
    public int? DriveNumber=>device.Storage?.DriveNumber;
    public bool HasSmart=>Smart is not null;
    public bool StatusBad=>Smart?.DiskStatus==DiskInfoToolkit.Enums.Interop.DiskStatus.Bad;
    public int? Temperature=>Smart?.Temperature;
    public int? TemperatureWarning=>Smart?.TemperatureWarning;
    public int? TemperatureCritical=>Smart?.TemperatureCritical;
    public double? Life=>Smart?.Life;
    public ulong PowerOnHours=>Smart?.MeasuredPowerOnHours??0;
    public IReadOnlyList<SmartAttributeReading> Attributes=>(device.Attributes??[]).Select(a=>new SmartAttributeReading(a.Id,a.Value,a.Threshold,a.Attribute?.Attribute.RawValueULong??0)).ToList();
}

/// <summary>Storage health from a dedicated LibreHardwareMonitor <see cref="Computer"/>.</summary>
/// <remarks>LibreHardwareMonitorLib 0.9.6 exposes each drive as a public <c>StorageDevice</c> whose DiskInfoToolkit
/// <c>Storage</c> carries the serial, drive number and SMART data; hardware that is not a <c>StorageDevice</c>
/// falls back to its sensors and the trailing number of its identifier.</remarks>
internal sealed partial class LibreStorageHealth : IStorageHealthSource
{
    private readonly Computer computer=new() {IsStorageEnabled=true};
    private readonly object gate=new();
    private bool opened,disposed;
    private readonly HardwareUpdates updates=new();
    private string status="";
    public string Status=>Volatile.Read(ref status);
    public IReadOnlyList<(string? Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout)
    {
        IHardware[] hardware;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!opened){computer.Open();opened=true;}
            hardware=computer.Hardware.Where(h=>h.HardwareType==HardwareType.Storage).ToArray();
        }
        var result=new List<(string?,string,int?,SmartSummary)>(hardware.Length);
        var errors=new List<string>();
        foreach(var h in hardware)
        {
            ISmartDevice? smart=h is LibreHardwareMonitor.Hardware.Storage.StorageDevice device?new LibreSmartDevice(device):null;
            string? serial=null;int? index=null;
            try {serial=smart?.Serial;index=smart?.DriveNumber;} catch(Exception) {smart=null;}
            index??=Index(h.Identifier.ToString());
            var outcome=updates.Run(h,h.Update,perHardwareTimeout,out var error);
            if(error is not null)errors.Add($"Storage health unavailable for {h.Name} ({error})");
            if(outcome is null){result.Add((serial,h.Name,index,StorageSensorLoop.TimedOut));continue;}
            SmartSummary summary;
            try {summary=Summary(h.Sensors,smart,outcome.Value);}
            catch(Exception e) {summary=Summary(h.Sensors,null,outcome.Value);errors.Add($"Storage health unavailable for {h.Name} ({e.GetType().Name})");}
            result.Add((serial,h.Name,index,summary));
        }
        Volatile.Write(ref status,string.Join(" · ",errors.Distinct()));
        return result;
    }
    private static SmartSummary Summary(ISensor[] sensors,ISmartDevice? smart,bool updated)
    {
        double? Find(SensorType type,string? name)=>sensors.FirstOrDefault(s=>s.SensorType==type&&s.Value.HasValue&&(name is null||s.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))?.Value;
        return Summary(Find(SensorType.Temperature,null),Find(SensorType.Level,"Remaining Life"),Find(SensorType.Load,"Used Space"),
            updated&&sensors.Any(s=>s.Value.HasValue),sensors.Length==0&&!Collector.IsElevated,updated?smart:null);
    }
    /// <summary>Sensor values plus, when the drive exposed SMART, its warning state, error counts, limits and power-on hours.</summary>
    internal static SmartSummary Summary(double? temperature,double? life,double? used,bool sensorsReadable,bool needsAdmin,ISmartDevice? smart)
    {
        if(smart is null||!smart.HasSmart)return new(sensorsReadable,false,life,temperature,null,0,0,0,null,used,needsAdmin);
        var attributes=smart.Attributes;
        long Raw(byte id)=>(long)Math.Min(attributes.FirstOrDefault(a=>a.Id==id)?.Raw??0,long.MaxValue);
        bool critical=smart.StatusBad||attributes.Any(a=>a.Threshold>0&&a.Value<a.Threshold);
        int? max=smart.TemperatureCritical??smart.TemperatureWarning;
        // DiskInfoToolkit reports 0 hours when it could not read them; 0 is shown as unknown, never as a brand-new drive.
        double? hours=smart.PowerOnHours>0?smart.PowerOnHours:null;
        return new(true,critical,life??smart.Life,temperature??smart.Temperature,max,Raw(0x05),Raw(0xC5),Raw(0xC6),hours,used,false);
    }
    internal static int? Index(string identifier)
    {
        var m=TrailingNumber().Match(identifier);
        return m.Success&&int.TryParse(m.Value,out var i)?i:null;
    }
    [GeneratedRegex(@"\d+$")] private static partial Regex TrailingNumber();
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            if(!opened)return;
        }
        // Close only after every stray update has left native/SMART IO. An update that never returns means
        // Close never runs: deliberate, quit must never block on a stuck drive.
        updates.Close().ContinueWith(_=>computer.Close(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}

/// <summary>Runs at most one update per hardware; an update still running from an earlier read is never started again.</summary>
internal sealed class HardwareUpdates
{
    private readonly Dictionary<object,Task> running=new(ReferenceEqualityComparer.Instance);
    private readonly object gate=new();
    private bool closed;
    /// <summary>Returns null on timeout (or while a previous update is still pending), else whether the update succeeded.</summary>
    public bool? Run(object key,Action update,TimeSpan timeout)=>Run(key,update,timeout,out _);
    /// <summary>As <see cref="Run(object,Action,TimeSpan)"/>; <paramref name="error"/> names the exception type when the update threw.</summary>
    public bool? Run(object key,Action update,TimeSpan timeout,out string? error)
    {
        error=null;
        Task task;
        lock(gate)
        {
            if(closed)return null;
            if(running.TryGetValue(key,out var previous)&&!previous.IsCompleted)return null;
            task=Task.Run(update);running[key]=task;
        }
        bool finished;
        try {finished=task.Wait(timeout);} catch(AggregateException) {finished=true;}
        if(finished&&task.Exception?.GetBaseException() is {} thrown)error=thrown.GetType().Name;
        return finished?task.IsCompletedSuccessfully:null;
    }
    /// <summary>Refuses new updates from now on and returns a task that completes when the running ones finish.</summary>
    public Task Close()
    {
        lock(gate){closed=true;return Outstanding();}
    }
    public Task Outstanding()
    {
        lock(gate)return Task.WhenAll(running.Values.Select(t=>t.ContinueWith(_=>{},TaskScheduler.Default)));
    }
}
