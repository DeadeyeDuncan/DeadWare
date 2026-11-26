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
        foreach(var reason in ConflictOrder) if(f.Conflicts.Any(c=>Conflict(c.Kind)==reason)) return (reason,null);
        if(f.ScanFailed||!f.EngineActive||f.Status is HostStatus.Blocked or HostStatus.OpenRgbMissing or HostStatus.OpenRgbFailed) return (HubReasons.NoLighting,null);
        // Ruling R4 (P33): not a fault, the hub is taken in a moment.
        if(!f.ConnectedOnce) return (HubReasons.Starting,null);
        if(f.LaunchedWithHub||f.OpenRgbListsHub) return (HubReasons.OpenRgbHasIt,f.RestartSpent?HubReasons.RestartSpent:null);
        return (null,null);
    }

    // The one mapping of a conflict kind to its HubReasons code (null: none), for the hold and for the hub requests' blockers (Task 10 review, M6);
    // the legacy Corsair service doesn't block lighting but is the service all the same. ConflictOrder: which wins when several are found.
    public static string? Conflict(ConflictKind kind)=>kind switch
    {
        ConflictKind.ICue=>HubReasons.ICue,
        ConflictKind.CorsairService or ConflictKind.LegacyCorsairService=>HubReasons.Service,
        ConflictKind.DeadLight=>HubReasons.DeadLight,
        ConflictKind.ForeignOpenRgb=>HubReasons.OtherOpenRgb,
        _=>null,
    };
    static readonly string[] ConflictOrder=[HubReasons.ICue,HubReasons.Service,HubReasons.DeadLight,HubReasons.OtherOpenRgb];
}
