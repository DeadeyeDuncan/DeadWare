using DeadWare.Core;

namespace DeadWare.Devices;

// The host's conflict scan over LightingConflicts.RunningProcesses, which Settings also uses while lighting is off.
internal sealed class ProcessConflictScanner : IConflictScanner
{
    public IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid)=>LightingConflicts.Find(LightingConflicts.RunningProcesses(),ownOpenRgbPid);
}
