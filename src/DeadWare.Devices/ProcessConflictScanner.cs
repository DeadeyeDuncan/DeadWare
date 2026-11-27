using DeadWare.Core;

namespace DeadWare.Devices;

// The host's conflict scan over LightingConflicts.RunningProcesses, which Settings also uses while lighting is off. Fix wave 4b: when a scan finds
// another OpenRGB it logs which OpenRGB processes it listed (LightingConflicts.ForeignOpenRgbLine), only when that text changed since the last line
// written; a scan that finds no other OpenRGB forgets it, so the next occurrence is logged again. listing: the process list (the real one unless a
// test gives its own).
internal sealed class ProcessConflictScanner(HostLog? log=null,Func<IReadOnlyList<RunningProcess>>? listing=null) : IConflictScanner
{
    readonly object gate=new();
    string? lastLine;
    public IReadOnlyList<LightingConflict> Scan(int? ownOpenRgbPid,(int Id,DateTime Started)? ended,out bool endedListed)
    {
        var processes=listing is null?LightingConflicts.RunningProcesses():listing();
        endedListed=ended is{} e&&LightingConflicts.Listed(processes,e);
        if(log is not null)
        {
            var line=LightingConflicts.ForeignOpenRgbLine(processes,ownOpenRgbPid,ended);
            bool write;
            lock(gate){write=line is not null&&line!=lastLine;lastLine=line;}
            if(write) log.Write(line!);
        }
        return LightingConflicts.Find(processes,ownOpenRgbPid,ended);
    }
}
