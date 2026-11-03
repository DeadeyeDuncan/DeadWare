using System.Text.RegularExpressions;
using DeadWare.Core;
using LibreHardwareMonitor.Hardware;

namespace DeadWare.App;

/// <summary>Storage health from a dedicated LibreHardwareMonitor <see cref="Computer"/>.</summary>
/// <remarks>LibreHardwareMonitorLib 0.9.6 keeps its storage types (AbstractStorage, SMART) internal, so the
/// serial is unavailable (null) and the index comes from the trailing number of the hardware identifier.</remarks>
internal sealed partial class LibreStorageHealth : IStorageHealthSource
{
    private readonly Computer computer=new() {IsStorageEnabled=true};
    private readonly object gate=new();
    private bool opened,disposed;
    public IReadOnlyList<(string Serial,string Model,int? Index,SmartSummary Summary)> Read(TimeSpan perHardwareTimeout)
    {
        IHardware[] hardware;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!opened){computer.Open();opened=true;}
            hardware=computer.Hardware.Where(h=>h.HardwareType==HardwareType.Storage).ToArray();
        }
        var result=new List<(string,string,int?,SmartSummary)>(hardware.Length);
        foreach(var h in hardware)
        {
            int? index=Index(h.Identifier.ToString());
            var update=Task.Run(h.Update);
            bool finished;
            try {finished=update.Wait(perHardwareTimeout);} catch(AggregateException) {finished=true;}
            if(!finished){result.Add((null!,h.Name,index,StorageSensorLoop.TimedOut));continue;}
            result.Add((null!,h.Name,index,Summary(h,update.IsCompletedSuccessfully)));
        }
        return result;
    }
    private static SmartSummary Summary(IHardware h,bool updated)
    {
        var sensors=h.Sensors;
        double? Find(SensorType type,string? name)=>sensors.FirstOrDefault(s=>s.SensorType==type&&s.Value.HasValue&&(name is null||s.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))?.Value;
        double? temperature=Find(SensorType.Temperature,null),life=Find(SensorType.Level,"Remaining Life"),used=Find(SensorType.Load,"Used Space");
        bool readable=updated&&sensors.Any(s=>s.Value.HasValue);
        bool needsAdmin=sensors.Length==0&&!Collector.IsElevated;
        // SMART attributes (critical warning, reallocated/pending/uncorrectable, power-on hours) are not public in 0.9.6.
        return new(readable,false,life,temperature,null,0,0,0,null,used,needsAdmin);
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
            if(opened)computer.Close();
        }
    }
}
