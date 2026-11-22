using System.Text.Json.Serialization;

namespace DeadWare.Core;

public sealed class DeviceCorrection{public int R{get;set;}=100;public int G{get;set;}=100;public int B{get;set;}=100;}
public sealed class DeviceMode{public string Kind{get;set;}="static";public int Speed{get;set;}=40;}
// Per-device tuning; a null member is the default (brightness 100, correction 100/100/100, follow the desk effect).
public sealed class DeviceTuning
{
    public int? Brightness{get;set;}public DeviceCorrection? Correction{get;set;}public DeviceMode? Mode{get;set;}
    // Lighting 3b: painted keys, layers, saved looks and the name of the look last loaded; null (and not written) when unused.
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public Dictionary<int,PaintedKey>? Keys{get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public List<DeviceLayer>? Layers{get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public List<DeviceLook>? Looks{get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Look{get;set;}
}
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
        Keys=DeviceLayers.Clone(e.Value.Keys),Layers=DeviceLayers.Clone(e.Value.Layers),Look=e.Value.Look,
        Looks=e.Value.Looks?.Select(l=>l is null?null!:DeviceLayers.Clone(l)).ToList(),
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
            if(t.Keys is{Count:0}) t.Keys=null;
            if(t.Layers is{Count:0}) t.Layers=null;
            if(t.Looks is{Count:0}) t.Looks=null;
            if(t.Look is not null&&DeviceLayers.Find(t,t.Look) is null) t.Look=null;
            if(t.Brightness is null&&t.Correction is null&&t.Mode is null&&!DeviceLayers.Has(t)) s.Devices.Remove(key);
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
            if(DeviceLayers.ValidateSetup(null,t.Keys,t.Layers) is{} bad) return bad;
            if(t.Looks is{} looks)
            {
                if(looks.Count>DeviceLayers.MaxLooks) return "A keyboard can have up to 12 saved looks.";
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var l in looks)
                {
                    var n=l?.Name?.Trim();
                    if(l is null||n is null||n.Length is<1 or>DeviceLayers.MaxLookName) return "A look's name must be 1 to 40 characters.";
                    if(!names.Add(n)) return "Two looks have the same name.";
                    if(l.Mode is{} lm)
                    {
                        if(lm.Kind is null||!Kinds.Contains(lm.Kind)) return "Lighting mode is not known.";
                        if(lm.Speed is<0 or>100) return "Mode speed must be 0 to 100.";
                    }
                    if(DeviceLayers.ValidateSetup(l.Mode,l.Keys,l.Layers) is{} lb) return lb;
                }
            }
        }
        return null;
    }
    public static int WrittenVersion(LightingSettings s)=>s.Version>LightingSettings.CurrentVersion?s.Version:!Everydays.IsDefault(s.Everyday)||s.Mouse.SleepMinutes is not null?4:s.Devices.Values.Any(t=>t is not null&&DeviceLayers.Has(t))?3:s.Devices.Count>0?2:1;
}
