namespace DeadWare.Core;

// Keyboard lighting modes, maths as DeadLight (shared/effects.ts, electron/painter.ts). Time is an argument, as in LightingEffects.
public static class DeviceModes
{
    public const string Static="static",Breathing="breathing",Cycle="cycle",Wave="wave";
    public const double WaveLength=1.6;
    public static bool Animated(string? kind)=>kind is Breathing or Cycle or Wave;
    public static string? CantRun(LightingDevice d,string kind)=>
        !KeyMaps.IsKeyboard(d)?"Lighting modes are for keyboards.":kind==Wave&&!KeyMaps.SupportsWave(d)?"This keyboard doesn't report its key positions.":null;
    // The saved mode when the device can run it now; a saved mode it can't run stays in the settings but isn't run.
    public static DeviceMode? Running(LightingSettings s,LightingDevice d)=>DeviceTunings.Of(s,d.Key)?.Mode is{} m&&CantRun(d,m.Kind) is null?m:null;
    // One colour per light, before tuning. Static and Breathing use each zone's own colour for its lights; Colour cycle is one hue for all;
    // Rainbow wave needs the key map, built once per call (without one it paints as Static).
    public static Rgb[] Frame(LightingSettings s,LightingDevice d,DeviceMode mode,double tMs)
    {
        var colors=new List<Rgb>();
        var map=mode.Kind==Wave?KeyMaps.Of(d):null;
        if(map is not null)
        {
            var shift=tMs/1000*LightingEffects.CyclesPerSecond(mode.Speed);
            foreach(var x in map.X) colors.Add(LightingEffects.HsvToRgb(LightingEffects.Wrap01((x??0.5)/WaveLength-shift)*360,1,1));
            return colors.ToArray();
        }
        var phase=LightingEffects.PhaseAt(new(0,0),tMs,new EffectParams{Speed=mode.Speed});
        var level=0.5-0.5*Math.Cos(2*Math.PI*phase);
        foreach(var z in d.Zones)
        {
            var c=mode.Kind switch
            {
                Cycle=>LightingEffects.HsvToRgb(phase*360,1,1),
                Breathing=>LightingEffects.Dim(LightingPaint.ZoneColor(s,d,z),level),
                _=>LightingPaint.ZoneColor(s,d,z),
            };
            for(var i=0;i<z.LedCount;i++) colors.Add(c);
        }
        return colors.ToArray();
    }
}
