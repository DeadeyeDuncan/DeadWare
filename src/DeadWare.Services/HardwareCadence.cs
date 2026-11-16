using LibreHardwareMonitor.Hardware;

namespace DeadWare.App;

/// <summary>
/// Which hardware a poll updates. CPU and GPU update every poll, and so does the board or cooler chip holding the CPU pump header
/// (<see cref="PumpHardwareId"/>): the app charts those every poll. The rest, which Extended sensors adds (motherboard voltages, fans
/// and temperatures, memory, controllers), changes slowly and updates every <see cref="SlowSeconds"/>; between updates its last rows
/// are shown again. The motherboard's own update does nothing, so its SuperIO and embedded controller each follow their own cadence,
/// and a pump on one of them keeps only that chip at full speed. One caller at a time.
/// </summary>
internal sealed class HardwareCadence
{
    internal const double SlowSeconds=2;
    private readonly Dictionary<string,double> updated=new(StringComparer.Ordinal);
    private readonly Dictionary<string,(List<SensorReading> Rows,string? Error)> kept=new(StringComparer.Ordinal);

    /// <summary>The hardware holding the CPU pump reading (<see cref="CpuPumpSpeed.HardwareId"/>); it updates every poll.</summary>
    public string? PumpHardwareId {get;set;}

    internal static bool EveryPoll(HardwareType type)=>type is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>Whether <paramref name="id"/> updates on this poll; slow hardware that does is timed from <paramref name="now"/>.</summary>
    public bool Due(string id,HardwareType type,double now,double intervalSeconds)
    {
        if(EveryPoll(type)||id==PumpHardwareId)return true;
        // Due half a poll early, so poll jitter never pushes an update a whole poll later; a clock that went back updates at once.
        if(updated.TryGetValue(id,out double last)&&now>=last&&now-last<SlowSeconds-intervalSeconds/2)return false;
        updated[id]=now;
        return true;
    }

    /// <summary>The rows and error of an update, shown again until the next one.</summary>
    public void Keep(string id,List<SensorReading> rows,string? error)=>kept[id]=(rows,error);

    public bool TryRecall(string id,out List<SensorReading> rows,out string? error)
    {
        if(kept.TryGetValue(id,out var last)){rows=last.Rows;error=last.Error;return true;}
        rows=[];error=null;
        return false;
    }
}
