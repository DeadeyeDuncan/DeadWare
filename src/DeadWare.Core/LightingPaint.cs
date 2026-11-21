namespace DeadWare.Core;

public sealed record LightingZone(int Index,string Name,int LedCount,ZoneMatrix? Matrix=null);
public sealed record LightingDevice(string Key,string Name,int Type,IReadOnlyList<LightingZone> Zones,IReadOnlyList<string> Modes,int ActiveMode,IReadOnlyList<string>? LedNames=null);
public enum PaintKind { Direct, Hardware, Unsupported }
public sealed record DevicePaint(PaintKind Kind,string? Mode,Rgb[] Colors,bool StaysLitWhenOff);

public static class LightingPaint
{
    public const string DirectMode="Direct",StaticMode="Static";
    public static IReadOnlyDictionary<string,string> HardwareModeDefaults{get;}=new Dictionary<string,string>{["Corsair Lighting Node Pro"]="Rainbow Wave"};
    // The device's own hardware effect: a saved choice the device offers, else its default by name. A saved "Direct" is the explicit choice
    // of DeadWare's colours (Task 3), so it means no hardware mode and the device is painted; it is never returned as a hardware mode.
    public static string? HardwareModeFor(LightingSettings settings,LightingDevice device)
    {
        if(settings.HardwareModes.TryGetValue(device.Key,out var m))
        {
            if(m==DirectMode) return null;
            if(device.Modes.Contains(m)) return m;
        }
        return HardwareModeDefaults.TryGetValue(device.Name,out var d)&&device.Modes.Contains(d)?d:null;
    }
    // Unscaled zone colour: the stored entry when its zone name still matches, else the default colour.
    public static Rgb ZoneColor(LightingSettings settings,LightingDevice device,LightingZone zone)
    {
        if(settings.ZoneColors.TryGetValue(LightingKeys.Zone(device.Key,zone.Index),out var e)&&e.ZoneName==zone.Name&&Rgb.TryParse(e.Color,out var c)) return c;
        return Rgb.TryParse(settings.DefaultColor,out var def)?def:new Rgb(0x46,0x82,0xB4);
    }
    // The device as the window shows it (Ruling AB): Kind and Mode from the current paint, and its own effect and lights-off behaviour as
    // capabilities that don't change with Lights on, so its card can say before the lights go off that it will stay lit.
    public static DeviceView View(LightingSettings settings,LightingDevice device)
    {
        var p=Paint(settings,device);
        var own=HardwareModeFor(settings,device);
        var zones=device.Zones.Select(z=>
        {
            var zk=LightingKeys.Zone(device.Key,z.Index);
            var own=settings.ZoneColors.TryGetValue(zk,out var e)&&e.ZoneName==z.Name;
            return new ZoneView(zk,z.Index,z.Name,z.LedCount,ZoneColor(settings,device,z).ToHex(),own);
        }).ToList();
        // Step 3a: the saved tuning and mode, copied so a view never shares the live settings' objects; the mode even when it can't run,
        // with the reason.
        var t=DeviceTunings.Of(settings,device.Key);
        var c=t?.Correction is{} k?new DeviceCorrection{R=k.R,G=k.G,B=k.B}:null;
        var mode=t?.Mode is{} m?new DeviceMode{Kind=m.Kind,Speed=m.Speed}:null;
        return new DeviceView(device.Key,device.Name,device.Type,p.Kind,p.Mode,own,own is not null&&!device.Modes.Contains(StaticMode),zones,
            t?.Brightness??100,c,mode,mode is null?null:DeviceModes.CantRun(device,mode.Kind),KeyMaps.IsKeyboard(device),KeyMaps.SupportsWave(device),KeyMaps.Of(device));
    }
    // The base frame, one colour per light before tuning: the device's running keyboard mode; Static for a saved mode it can't run now
    // (spec: painted as Static, nothing deleted); else each zone's colour as the desk shows it.
    public static Rgb[] Lights(LightingSettings s,LightingDevice d,double tMs)
    {
        if(DeviceModes.Running(s,d) is{} m) return DeviceModes.Frame(s,d,m,tMs);
        if(DeviceTunings.Of(s,d.Key)?.Mode is not null) return DeviceModes.Frame(s,d,new DeviceMode{Kind=DeviceModes.Static},tMs);
        var colors=new List<Rgb>();
        foreach(var z in d.Zones){var c=ZoneShown(s,d,z,tMs);for(var i=0;i<z.LedCount;i++) colors.Add(c);}
        return colors.ToArray();
    }
    // Colour correction, then device and page brightness, with one rounding at the end. Integers until that last step, so that with no
    // tuning it is exactly Rgb.Scale(page): v*100*100*page/1e6 is the same double as v*page/100.0. Never multiply by fractions here.
    public static Rgb Tune(Rgb c,DeviceTuning? t,int pageBrightness)
    {
        int device=t?.Brightness??100,page=Math.Clamp(pageBrightness,0,100);
        var k=t?.Correction;
        byte Ch(byte v,int gain){var n=Math.Min(v*gain,25500);return (byte)Math.Round(n*device*page/1_000_000.0,MidpointRounding.AwayFromZero);}
        return new(Ch(c.R,k?.R??100),Ch(c.G,k?.G??100),Ch(c.B,k?.B??100));
    }
    internal static bool Painted(LightingSettings s,LightingDevice d)=>HardwareModeFor(s,d) is null&&d.Modes.Contains(DirectMode);
    // Something moves: lights on and a desk effect running, a painted device running an animated keyboard mode or an effect layer, or a key
    // press still live for one of its fade or ripple layers.
    public static bool Animating(LightingSettings s,IEnumerable<LightingDevice> devices,IReadOnlyList<LightPress>? presses=null,double nowMs=0)
    {
        if(!s.LightsOn) return false;
        if(EffectRunning(s)) return true;
        var list=devices as IReadOnlyCollection<LightingDevice>??devices.ToList();
        foreach(var d in list)
        {
            if(!Painted(s,d)) continue;
            if(DeviceModes.Animated(DeviceModes.Running(s,d)?.Kind)||LightingLayers.EffectLayerRuns(s,d)) return true;
            if(presses is not null&&LightingLayers.LongestReactionMs(s,d) is>0 and var ms&&presses.Any(p=>p is not null&&p.DeviceKey==d.Key&&nowMs-p.AtMs<ms)) return true;
        }
        return false;
    }
    // A null view, as a damaged state over the pipe could hold, is skipped as the window's other view code does.
    public static bool Animating(LightingSettings s,IEnumerable<DeviceView?> devices)=>
        s.LightsOn&&(EffectRunning(s)||devices.Any(v=>v is not null&&v.Kind==PaintKind.Direct&&DeviceModes.Animated(v.LightingMode?.Kind)&&v.ModeProblem is null));
    public static bool EffectRunning(LightingSettings settings)=>settings.LightsOn&&LightingEffects.KindOf(settings.Effect.Kind)!=EffectKind.Static;
    // The zone's colour before brightness: its static colour, unless an effect is selected and the zone's device is placed with a point for the zone.
    public static Rgb ZoneShown(LightingSettings settings,LightingDevice device,LightingZone zone,double tMs)
    {
        var baseColor=ZoneColor(settings,device,zone);
        var kind=LightingEffects.KindOf(settings.Effect.Kind);
        if(kind==EffectKind.Static||settings.Layout.Unplaced.Contains(device.Key)||!settings.Layout.Zones.TryGetValue(LightingKeys.Zone(device.Key,zone.Index),out var pt)) return baseColor;
        return LightingEffects.Evaluate(kind,pt,tMs,LightingEffects.ParamsFor(settings.Effect,kind)!,baseColor);
    }
    // The colours the window shows for a device: its base frame, or key paint and layers after the stage, rounded once and not tuned.
    public static Rgb[] Shown(LightingSettings s,LightingDevice d,double tMs,IReadOnlyList<LightPress>? presses)
    {
        var lights=Lights(s,d,tMs);
        if(LightingLayers.Compose(s,d,lights,tMs,presses) is not{} f) return lights;
        var r=new Rgb[lights.Length];
        for(var i=0;i<r.Length;i++) r[i]=LightingLayers.Round(f[3*i],f[3*i+1],f[3*i+2]);
        return r;
    }
    public static DevicePaint Paint(LightingSettings settings,LightingDevice device,double tMs=0,IReadOnlyList<LightPress>? presses=null)
    {
        var mode=HardwareModeFor(settings,device);
        if(mode is not null)
        {
            if(settings.LightsOn) return new(PaintKind.Hardware,mode,[],false);
            return device.Modes.Contains(StaticMode)?new(PaintKind.Hardware,StaticMode,[Rgb.Black],false):new(PaintKind.Hardware,mode,[],true);
        }
        if(!device.Modes.Contains(DirectMode)) return new(PaintKind.Unsupported,null,[],false);
        var lights=Lights(settings,device,tMs);
        var tuning=DeviceTunings.Of(settings,device.Key);
        // Key paint and layers carry fractions to one rounding; with none of either (or lights off) the integer loop below is the whole path.
        if(settings.LightsOn&&LightingLayers.Compose(settings,device,lights,tMs,presses) is{} f)
        {
            for(var i=0;i<lights.Length;i++) lights[i]=LightingLayers.TuneExact(f[3*i],f[3*i+1],f[3*i+2],tuning,settings.Brightness);
            return new(PaintKind.Direct,DirectMode,lights,false);
        }
        for(var i=0;i<lights.Length;i++) lights[i]=settings.LightsOn?Tune(lights[i],tuning,settings.Brightness):Rgb.Black;
        return new(PaintKind.Direct,DirectMode,lights,false);
    }
}
