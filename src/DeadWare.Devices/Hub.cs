using DeadWare.Core;
namespace DeadWare.Devices;

// Lighting 5: what the host and the device service see of the hub worker. Every call returns at once, from any thread, and never waits on the
// hub: it only stores the newest value for the worker's next step.
internal interface IHub
{
    // One colour per LED-bearing part, in zone order; the newest wins.
    void Post(Rgb[] zones);
    void SetDark(bool dark);
    // null: holding is allowed; else the HubReasons code why not.
    void Allow(string? forbidden,string? detail=null);
    // Suspend; the wake's end.
    void Quiet();void Resume();
    // The hub may have appeared, gone or changed.
    void Recheck();
    // The dialog's one reading, while not held.
    void RequestReading();
    // Completed by the worker's next step: once the hub is handed back and closed, or at once there when it is not held.
    Task LetGoAsync();
    HubSnapshot Snapshot{get;}
    // Raised on the worker's thread after Snapshot changed.
    event Action? Changed;
}

// State: off (never held in this run and nothing to say), driving or letGo. Parts: the parts last read in this helper run (by a take or by the
// dialog's reading), kept after a let-go. ReadingsAt and SoftwareAt are TimeProvider.GetTimestamp() values; SoftwareAt is 0 while not held.
// PumpStopped: from the stopped-pump hand-back until holding has been forbidden and allowed anew.
internal sealed record HubSnapshot(string State,string? Reason=null,string? Detail=null,IReadOnlyList<HubPart>? Parts=null,HubReadings? Readings=null,
    long ReadingsAt=0,long SoftwareAt=0,HubReading? Read=null,bool PumpStopped=false){public static readonly HubSnapshot Idle=new(HubStates.Off);}

internal sealed record HubListing(IReadOnlyList<HubPart> Parts,bool Driving);
