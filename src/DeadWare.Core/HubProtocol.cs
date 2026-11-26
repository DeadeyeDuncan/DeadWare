namespace DeadWare.Core;

// What the hub is doing, as the helper reports it (HubView.State).
public static class HubStates{public const string Off="off",Switching="switching",Driving="driving",LetGo="letGo",GaveBack="gaveBack";}
// DeviceView.Hub: how the hub is lit.
public static class HubRoutes{public const string OpenRgb="openrgb",Driven="driven",Own="own";}
// Why DeadWare has let go of the hub, cannot take it, or did not finish taking it. Starting: lighting has not connected yet in this helper run and
// the hub has not been held (it reads as "in a moment", never as a fault). RestartSpent: the detail of OpenRgbHasIt once the one restart is used up.
public static class HubReasons
{
    public const string ICue="icue",Service="service",DeadLight="deadlight",OtherOpenRgb="otherOpenRgb",NoLighting="noLighting",
        NoHub="noHub",Silent="silent",FallsBack="fallsBack",UnknownPart="unknownPart",TooManyParts="tooManyParts",TwoHubs="twoHubs",OpenRgbHasIt="openRgbHasIt",
        CantSave="cantSave",NoLock="noLock",Asleep="asleep",Waking="waking",Starting="starting",Off="off";
    public const string RestartSpent="restartSpent";
}
// Rpm null: no reading. Celsius only on a part with a coolant sensor.
public sealed record HubPartReading(int Channel,string Kind,string Name,int? Rpm,double? Celsius=null);
// Parts in channel order.
public sealed record HubReadings(IReadOnlyList<HubPartReading> Parts,int AgeMs=0);
// The dialog's reading. Blocker: a HubReasons code that forbids the take-over.
public sealed record HubReading(bool Ok,DateTimeOffset At,HubReadings? Readings=null,string? Blocker=null,string? Detail=null);
// Problem: why the last take-over did not finish.
public sealed record HubView(string State,string? Reason=null,string? Detail=null,HubReadings? Before=null,DateTimeOffset? TakenAt=null,
    HubStop? Stop=null,HubReading? Read=null,string? Problem=null,string? ProblemDetail=null);
