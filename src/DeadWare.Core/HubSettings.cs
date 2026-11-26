namespace DeadWare.Core;

public static class HubModes
{
    public const string OpenRgb="openrgb",Driven="driven",Stopped="stopped";
    public static bool Known(string? mode)=>mode is OpenRgb or Driven or Stopped;
}
// Why a check gave the hub back.
public static class HubStops{public const string Slower="slower",NoReading="noReading",PumpStopped="pumpStopped";}
public static class HubPartKinds{public const string Fan="fan",Pump="pump";}
public sealed class HubStop
{
    public string Kind {get;set;}=HubStops.Slower;
    public string Part {get;set;}=HubPartKinds.Pump;
    // 1-based among the parts of that kind, in channel order.
    public int Number {get;set;}=1;
    // RPM.
    public int? Before {get;set;}
    public int? After {get;set;}
}
// One occupied channel as the hub last reported it.
public sealed class HubSavedPart
{
    public int Channel {get;set;}
    public int Type {get;set;}
    public int Model {get;set;}
}
public sealed class HubSettings
{
    public string Mode {get;set;}=HubModes.OpenRgb;
    public HubStop? Stop {get;set;}
    // The hub's last known parts, in channel order; always null in mode openrgb.
    public List<HubSavedPart>? Parts {get;set;}
}
public static class Hubs
{
    public const string InvalidText="The saved hub mode isn't one DeadWare knows.";
    public const int MaxParts=64;
    public static bool IsDefault(HubSettings h)=>h.Mode==HubModes.OpenRgb&&h.Stop is null&&h.Parts is null;
    public static HubSettings Clone(HubSettings h)=>new()
    {
        Mode=h.Mode,
        Stop=h.Stop is null?null:new(){Kind=h.Stop.Kind,Part=h.Stop.Part,Number=h.Stop.Number,Before=h.Stop.Before,After=h.Stop.After},
        Parts=h.Parts?.Select(p=>new HubSavedPart{Channel=p.Channel,Type=p.Type,Model=p.Model}).ToList(),
    };
    public static string? Validate(HubSettings h)
    {
        if(!HubModes.Known(h.Mode)) return InvalidText;
        if(h.Mode!=HubModes.Stopped){if(h.Stop is not null) return InvalidText;}
        else
        {
            var s=h.Stop;
            if(s is null||s.Kind is not(HubStops.Slower or HubStops.NoReading or HubStops.PumpStopped)||s.Part is not(HubPartKinds.Fan or HubPartKinds.Pump)
                ||s.Number is<1 or>64||s.Before is<0 or>100000||s.After is<0 or>100000) return InvalidText;
        }
        if(h.Parts is null) return null;
        if(h.Mode==HubModes.OpenRgb||h.Parts.Count>MaxParts) return InvalidText;
        var last=0;
        foreach(var p in h.Parts)
        {
            if(p is null||p.Channel is<1 or>255||p.Channel<=last||p.Type is<0 or>255||p.Model is<0 or>255) return InvalidText;
            last=p.Channel;
        }
        return null;
    }
}
