namespace DeadWare.Core;

public sealed class DeviceCorrection{public int R{get;set;}=100;public int G{get;set;}=100;public int B{get;set;}=100;}
public sealed class DeviceMode{public string Kind{get;set;}="static";public int Speed{get;set;}=40;}
// Per-device tuning; a null member is the default (brightness 100, correction 100/100/100, follow the desk effect).
public sealed class DeviceTuning{public int? Brightness{get;set;}public DeviceCorrection? Correction{get;set;}public DeviceMode? Mode{get;set;}}
public static class DeviceTunings
{
    public const int MaxEntries=256;
    public static readonly string[] Kinds=["static","breathing","cycle","wave"];
    public static DeviceTuning? Of(LightingSettings s,string deviceKey)=>s.Devices is not null&&s.Devices.TryGetValue(deviceKey,out var t)?t:null;
    public static Dictionary<string,DeviceTuning> Clone(Dictionary<string,DeviceTuning> t)=>(t??new()).ToDictionary(e=>e.Key,e=>e.Value is null?null!:new DeviceTuning
    {
        Brightness=e.Value.Brightness,
        Correction=e.Value.Correction is null?null:new(){R=e.Value.Correction.R,G=e.Value.Correction.G,B=e.Value.Correction.B},
        Mode=e.Value.Mode is null?null:new(){Kind=e.Value.Mode.Kind,Speed=e.Value.Mode.Speed},
    });
    // Defaults become null and an entry with nothing left is removed. A null entry is left for Validate to report.
    public static void Normalize(LightingSettings s)
    {
        if(s.Devices is null) return;
        foreach(var (key,t) in s.Devices.ToList())
        {
            if(t is null) continue;
            if(t.Brightness==100) t.Brightness=null;
            if(t.Correction is{R:100,G:100,B:100}) t.Correction=null;
            if(t.Brightness is null&&t.Correction is null&&t.Mode is null) s.Devices.Remove(key);
        }
    }
    public static string? Validate(LightingSettings s)
    {
        if(s.Devices is null) return "Devices is missing.";
        if(s.Devices.Count>MaxEntries) return "Too many device entries.";
        foreach(var (key,t) in s.Devices)
        {
            if(key.Length>LightingSettingsCodec.MaxKeyLength) return "A device key is too long.";
            if(t is null) return "A device entry is invalid.";
            if(t.Brightness is<0 or>100) return "Device brightness must be 0 to 100.";
            if(t.Correction is{} c&&(c.R is<0 or>200||c.G is<0 or>200||c.B is<0 or>200)) return "Colour correction must be 0 to 200.";
            if(t.Mode is{} m)
            {
                if(m.Kind is null||!Kinds.Contains(m.Kind)) return "Lighting mode is not known.";
                if(m.Speed is<0 or>100) return "Mode speed must be 0 to 100.";
            }
        }
        return null;
    }
    public static int WrittenVersion(LightingSettings s)=>s.Version>LightingSettings.CurrentVersion?s.Version:s.Devices.Count>0?2:1;
}
