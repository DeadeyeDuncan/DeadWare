using DeadWare.Core;
namespace DeadWare.Devices;

// Lighting 5: what the host knows on a loop turn that decides whether the worker may hold the hub. Conflicts: everything the last scan found,
// the legacy Corsair service included (it doesn't block lighting). LaunchedWithHub: an OpenRGB of ours exists and its launch wrote the hub
// detector on. RestartSpent: the one restart for a detector left on has been used in this helper run.
internal readonly record struct HubFacts(string Mode,bool Stopping,bool Quiet,bool EngineActive,bool ConnectedOnce,HostStatus Status,
    IReadOnlyList<LightingConflict> Conflicts,bool ScanFailed,bool OpenRgbListsHub,bool LaunchedWithHub,bool RestartSpent=false);

// The hub is also the pump and the radiator fans: two drivers must never hold it. Status Starting, Waiting and Running all hold, so the hub is
// kept through OpenRGB crashes, restarts and reconnects; it is never held beside an OpenRGB launched with its hub detector on, whatever that
// OpenRGB has listed so far (OpenRGB lists devices a few at a time after a start).
internal static class HubHold
{
    // (null,null): the hub may be held. Else the HubReasons code, first match wins, with openRgbHasIt's detail (ruling P32).
    public static (string? Reason,string? Detail) Why(HubFacts f)
    {
        if(f.Mode!=HubModes.Driven) return (HubReasons.Off,null);
        if(f.Stopping) return (HubReasons.NoLighting,null);
        if(f.Quiet) return (HubReasons.Waking,null);
        if(Has(f,ConflictKind.ICue)) return (HubReasons.ICue,null);
        if(Has(f,ConflictKind.CorsairService)||Has(f,ConflictKind.LegacyCorsairService)) return (HubReasons.Service,null);
        if(Has(f,ConflictKind.DeadLight)) return (HubReasons.DeadLight,null);
        if(Has(f,ConflictKind.ForeignOpenRgb)) return (HubReasons.OtherOpenRgb,null);
        if(f.ScanFailed||!f.EngineActive||f.Status is HostStatus.Blocked or HostStatus.OpenRgbMissing or HostStatus.OpenRgbFailed) return (HubReasons.NoLighting,null);
        // Ruling R4 (P33): not a fault, the hub is taken in a moment.
        if(!f.ConnectedOnce) return (HubReasons.Starting,null);
        if(f.LaunchedWithHub||f.OpenRgbListsHub) return (HubReasons.OpenRgbHasIt,f.RestartSpent?HubReasons.RestartSpent:null);
        return (null,null);
    }

    static bool Has(HubFacts f,ConflictKind kind)=>f.Conflicts.Any(c=>c.Kind==kind);
}
