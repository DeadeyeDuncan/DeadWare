using System.Text.Json.Serialization;

namespace DeadWare.Core;

// Lighting 3b: what a keyboard's table holds beyond a mode: painted keys, up to 8 layers and up to 12 saved looks.
// Every new member is null when unused and never written then, so an entry without them is the file 3a wrote.
public sealed class PaintedKey{public string Name{get;set;}="";public string Color{get;set;}="#000000";}
public sealed class LayerLight{public int Index{get;set;}public string Name{get;set;}="";}
// Lights null is every light, empty is none. An unknown Kind, Effect or Mix is kept in a file (a newer build wrote it) and does nothing here.
public sealed class DeviceLayer
{
    public string Kind{get;set;}="fade";public string Color{get;set;}="#38E9FF";public string Effect{get;set;}="wave";
    public int Speed{get;set;}=40;public int DurationMs{get;set;}=600;public int Strength{get;set;}=100;public string Mix{get;set;}="add";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public List<LayerLight>? Lights{get;set;}
}
public sealed class DeviceLook
{
    public string Name{get;set;}="";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DeviceMode? Mode{get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public Dictionary<int,PaintedKey>? Keys{get;set;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public List<DeviceLayer>? Layers{get;set;}
}
public static class DeviceLayers
{
    public const int MaxKeys=4096,MaxLayers=8,MaxLooks=12,MaxLookName=40,MaxLightName=128;
    public const string Color="color",Effect="effect",Fade="fade",Ripple="ripple",Cover="cover",Add="add";
    public static readonly string[] Kinds=[Color,Effect,Fade,Ripple],Effects=["wave","breathing","spectrum"],Mixes=[Cover,Add];
    public static bool Has(DeviceTuning t)=>t.Keys is{Count:>0}||t.Layers is{Count:>0}||t.Looks is{Count:>0}||t.Look is not null;
    public static Dictionary<int,PaintedKey>? Clone(Dictionary<int,PaintedKey>? k)=>k?.ToDictionary(e=>e.Key,e=>e.Value is null?null!:new PaintedKey{Name=e.Value.Name,Color=e.Value.Color});
    public static List<DeviceLayer>? Clone(List<DeviceLayer>? l)=>l?.Select(x=>x is null?null!:new DeviceLayer
    {
        Kind=x.Kind,Color=x.Color,Effect=x.Effect,Speed=x.Speed,DurationMs=x.DurationMs,Strength=x.Strength,Mix=x.Mix,
        Lights=x.Lights?.Select(g=>g is null?null!:new LayerLight{Index=g.Index,Name=g.Name}).ToList(),
    }).ToList();
    public static DeviceLook Clone(DeviceLook l)=>new(){Name=l.Name,Mode=l.Mode is null?null:new(){Kind=l.Mode.Kind,Speed=l.Mode.Speed},Keys=Clone(l.Keys),Layers=Clone(l.Layers)};
    // A null and an empty Keys or Layers are the same; a layer's Lights null (every key) and empty (no key) are not.
    public static bool Same(DeviceTuning t,DeviceLook look)=>SameMode(t.Mode,look.Mode)&&SameKeys(t.Keys,look.Keys)&&SameLayers(t.Layers,look.Layers);
    static bool SameMode(DeviceMode? a,DeviceMode? b)=>a is null?b is null:b is not null&&a.Kind==b.Kind&&a.Speed==b.Speed;
    static bool SameKeys(Dictionary<int,PaintedKey>? a,Dictionary<int,PaintedKey>? b)
    {
        if((a?.Count??0)!=(b?.Count??0)) return false;
        if(a is null||b is null) return true;
        foreach(var (i,k) in a) if(!b.TryGetValue(i,out var o)||k.Name!=o.Name||k.Color!=o.Color) return false;
        return true;
    }
    static bool SameLayers(List<DeviceLayer>? a,List<DeviceLayer>? b)
    {
        if((a?.Count??0)!=(b?.Count??0)) return false;
        if(a is null||b is null) return true;
        for(int i=0;i<a.Count;i++)
        {
            DeviceLayer x=a[i],y=b[i];
            if(x.Kind!=y.Kind||x.Color!=y.Color||x.Effect!=y.Effect||x.Speed!=y.Speed||x.DurationMs!=y.DurationMs||x.Strength!=y.Strength||x.Mix!=y.Mix) return false;
            if((x.Lights is null)!=(y.Lights is null)) return false;
            if(x.Lights is null||y.Lights is null) continue;
            if(x.Lights.Count!=y.Lights.Count) return false;
            for(int j=0;j<x.Lights.Count;j++) if(x.Lights[j].Index!=y.Lights[j].Index||x.Lights[j].Name!=y.Lights[j].Name) return false;
        }
        return true;
    }
    public static DeviceLook? Find(DeviceTuning t,string name)
    {
        var n=name?.Trim();
        return n is null||t.Looks is null?null:t.Looks.FirstOrDefault(l=>l is not null&&string.Equals(l.Name?.Trim(),n,StringComparison.OrdinalIgnoreCase));
    }
    public static bool Changed(DeviceTuning t)=>t.Look is not null&&Find(t,t.Look) is{} l&&!Same(t,l);
    public static string? ValidateSetup(DeviceMode? mode,Dictionary<int,PaintedKey>? keys,List<DeviceLayer>? layers)
    {
        if(keys is not null)
        {
            if(keys.Count>MaxKeys) return "Too many painted keys.";
            foreach(var (i,k) in keys)
            {
                if(i<0||k is null) return "A painted key is invalid.";
                if(k.Name is null||k.Name.Length is<1 or>MaxLightName) return "A light's name must be 1 to 128 characters.";
                if(!Rgb.TryParse(k.Color,out _)) return "A colour is invalid.";
            }
        }
        if(layers is not null)
        {
            if(layers.Count>MaxLayers) return "A keyboard can have up to 8 layers.";
            foreach(var l in layers)
            {
                if(l is null||l.Kind is null||l.Kind.Length>32||l.Effect is null||l.Effect.Length>32||l.Mix is null||l.Mix.Length>32) return "A layer is invalid.";
                if(!Rgb.TryParse(l.Color,out _)) return "A colour is invalid.";
                if(l.Strength is<0 or>100) return "Layer strength must be 0 to 100.";
                if(l.DurationMs is<100 or>3000) return "A layer must last 0.1 to 3 seconds.";
                if(l.Speed is<0 or>100) return "Layer speed must be 0 to 100.";
                if(l.Lights is{} g)
                {
                    if(g.Count>MaxKeys) return "A layer's keys are invalid.";
                    foreach(var x in g)
                    {
                        if(x is null||x.Index<0) return "A layer's keys are invalid.";
                        if(x.Name is null||x.Name.Length is<1 or>MaxLightName) return "A light's name must be 1 to 128 characters.";
                    }
                }
            }
        }
        return null;
    }
    // For a request, where an unknown kind, effect or mix is a mistake; a file keeps them (a newer build may know them).
    public static string? CheckNew(List<DeviceLayer>? layers)
    {
        if(layers is null) return null;
        foreach(var l in layers) if(l is null||!Kinds.Contains(l.Kind)||!Effects.Contains(l.Effect)||!Mixes.Contains(l.Mix)) return "That layer isn't of a kind DeadWare knows.";
        return null;
    }
}
