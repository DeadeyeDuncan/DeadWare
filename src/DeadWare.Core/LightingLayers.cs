namespace DeadWare.Core;

// A key press as the paint stage sees it: the device, the lights the key drives and when (ms on the paint clock). Never gains a member:
// nothing else about a press is kept (the key-press rule).
public sealed record LightPress(string DeviceKey,IReadOnlyList<int> Lights,double AtMs);

// Key paint then layers, carried as fractions to one rounding (spec, "Layer maths"; DeadLight painter.ts compose).
public static class LightingLayers
{
    private static EffectKind? KindFor(string? effect)=>effect switch{"wave"=>EffectKind.Wave,"breathing"=>EffectKind.Breathing,"spectrum"=>EffectKind.Spectrum,_=>null};
    private static IReadOnlyList<DeviceLayer> LayersOf(DeviceTuning? t)=>t?.Layers is{} l?l.Where(x=>x is not null).ToList():[];
    public static bool Active(LightingSettings s,LightingDevice d)=>KeyMaps.CanPaint(d)&&DeviceTunings.Of(s,d.Key) is{} t&&(t.Keys is{Count:>0}||t.Layers is{Count:>0});
    private static bool Matches(LightingDevice d,int index,string? name)=>d.LedNames is{} n&&index>=0&&index<n.Count&&name is not null&&n[index]==name;
    // The lights a layer covers: all of them when its list is null, else the listed lights whose name still matches.
    private static List<int> Covered(LightingDevice d,DeviceLayer l)
    {
        var n=KeyMaps.LightCount(d);var r=new List<int>();
        if(l.Lights is null){for(var i=0;i<n;i++) r.Add(i);return r;}
        foreach(var g in l.Lights) if(g is not null&&g.Index<n&&Matches(d,g.Index,g.Name)&&!r.Contains(g.Index)) r.Add(g.Index);
        return r;
    }
    public static double[]? Compose(LightingSettings s,LightingDevice d,Rgb[] baseFrame,double tMs,IReadOnlyList<LightPress>? presses)
    {
        if(!Active(s,d)) return null;
        var t=DeviceTunings.Of(s,d.Key);var map=KeyMaps.Of(d)!;
        var n=baseFrame.Length;var f=new double[3*n];
        for(var i=0;i<n;i++){f[3*i]=baseFrame[i].R;f[3*i+1]=baseFrame[i].G;f[3*i+2]=baseFrame[i].B;}
        if(t!.Keys is{} keys)
            foreach(var (i,k) in keys)
                if(k is not null&&i<n&&Matches(d,i,k.Name)&&Rgb.TryParse(k.Color,out var c)){f[3*i]=c.R;f[3*i+1]=c.G;f[3*i+2]=c.B;}
        var mine=presses?.Where(p=>p is not null&&p.DeviceKey==d.Key&&p.Lights is not null).ToList()??[];
        DeskPoint? Pos(int i)=>i>=0&&i<map.X.Count&&map.X[i] is{} x&&map.Y[i] is{} y?new DeskPoint(x,y):null;
        foreach(var l in LayersOf(t))
        {
            if(!DeviceLayers.Kinds.Contains(l.Kind)||!Rgb.TryParse(l.Color,out var lc)) continue;
            var effect=KindFor(l.Effect);
            if(l.Kind==DeviceLayers.Effect&&effect is null) continue;
            var writes=new List<(int,double,double,double)>();
            foreach(var i in Covered(d,l))
            {
                if(i>=n) continue;
                double a0;var p=lc;
                switch(l.Kind)
                {
                    case DeviceLayers.Color:a0=1;break;
                    case DeviceLayers.Effect:
                        p=LightingEffects.Evaluate(effect!.Value,Pos(i)??new DeskPoint(0.5,0.5),tMs,new EffectParams{Speed=l.Speed},lc);a0=1;break;
                    case DeviceLayers.Fade:
                        a0=0;
                        foreach(var pr in mine)
                        {
                            var age=tMs-pr.AtMs;
                            if(age>=0&&age<l.DurationMs&&pr.Lights.Contains(i)) a0=Math.Max(a0,1-age/l.DurationMs);
                        }
                        break;
                    default:
                        a0=0;
                        if(Pos(i) is not{} here) break;
                        foreach(var pr in mine)
                        {
                            var e=(tMs-pr.AtMs)/l.DurationMs;
                            if(e<0||e>=1) continue;
                            foreach(var o in pr.Lights)
                            {
                                if(Pos(o) is not{} there) continue;
                                var dist=Math.Sqrt((here.X-there.X)*(here.X-there.X)+(here.Y-there.Y)*(here.Y-there.Y));
                                var ring=Math.Max(0,1-Math.Abs(dist-e*Math.Sqrt(2))/0.18);
                                a0=Math.Max(a0,ring*(1-e));
                            }
                        }
                        break;
                }
                if(a0==0) continue;
                var a=a0*l.Strength/100.0;
                var cover=l.Mix==DeviceLayers.Cover;
                if(!cover&&l.Mix!=DeviceLayers.Add) continue;
                double Mix(double c,double v)=>cover?c*(1-a)+v*a:c+v*a;
                writes.Add((i,Mix(f[3*i],p.R),Mix(f[3*i+1],p.G),Mix(f[3*i+2],p.B)));
            }
            foreach(var (i,r,g,b) in writes){f[3*i]=r;f[3*i+1]=g;f[3*i+2]=b;}
        }
        return f;
    }
    // Every channel carried as a fraction to this one step: gain clamped at 255, then device and page brightness, rounded once.
    public static Rgb TuneExact(double r,double g,double b,DeviceTuning? t,int pageBrightness)
    {
        int device=t?.Brightness??100,page=Math.Clamp(pageBrightness,0,100);var k=t?.Correction;
        byte Ch(double v,int gain)=>(byte)Math.Round(Math.Min(255,Math.Max(0,v*gain/100.0))*device/100.0*page/100.0,MidpointRounding.AwayFromZero);
        return new(Ch(r,k?.R??100),Ch(g,k?.G??100),Ch(b,k?.B??100));
    }
    public static Rgb Round(double r,double g,double b)
    {
        byte Ch(double v)=>(byte)Math.Round(Math.Min(255,Math.Max(0,v)),MidpointRounding.AwayFromZero);
        return new(Ch(r),Ch(g),Ch(b));
    }
    // Painted keys whose index is out of range or whose name no longer matches the light there.
    public static int SkippedKeys(LightingDevice d,DeviceTuning? t)=>t?.Keys is null?0:t.Keys.Count(e=>e.Value is null||!Matches(d,e.Key,e.Value.Name));
    // One count per layer: listed lights that no longer match.
    public static IReadOnlyList<int> SkippedLayerLights(LightingDevice d,DeviceTuning? t)=>
        (t?.Layers??[]).Select(l=>l?.Lights is null?0:l.Lights.Count(g=>g is null||!Matches(d,g.Index,g.Name))).ToList();
    private static bool Reacts(DeviceLayer l)=>l.Kind is DeviceLayers.Fade or DeviceLayers.Ripple;
    private static IEnumerable<DeviceLayer> Moving(LightingSettings s,LightingDevice d,Func<DeviceLayer,bool> kind)=>
        LayersOf(DeviceTunings.Of(s,d.Key)).Where(l=>kind(l)&&l.Strength>0&&Covered(d,l).Count>0);
    public static bool EffectLayerRuns(LightingSettings s,LightingDevice d)=>
        Active(s,d)&&Moving(s,d,l=>l.Kind==DeviceLayers.Effect&&KindFor(l.Effect) is not null).Any();
    public static bool NeedsKeys(LightingSettings s,LightingDevice d)=>
        s.LightsOn&&LightingPaint.Painted(s,d)&&KeyMaps.CanPaint(d)&&Moving(s,d,Reacts).Any();
    public static int LongestReactionMs(LightingSettings s,LightingDevice d)=>NeedsKeys(s,d)?Moving(s,d,Reacts).Max(l=>l.DurationMs):0;
}
