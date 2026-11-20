namespace DeadWare.Core;

public readonly record struct DeskPoint(double X,double Y);
public enum EffectKind { Static, Wave, Breathing, Spectrum }
public sealed class EffectParams { public double Speed{get;set;}=40; public double AngleDeg{get;set;} public double Spread{get;set;}=50; public List<string> Colors{get;set;}=new(); }
public sealed class EffectSettings { public string Kind{get;set;}="Static"; public EffectParams Wave{get;set;}=new(); public EffectParams Breathing{get;set;}=new(); public EffectParams Spectrum{get;set;}=new(); }
// Pure effect maths for the desk lighting. Time is an argument; nothing here reads a clock or does I/O. Ported from DeadLight shared/effects.ts and shared/color.ts.
public static class LightingEffects
{
    public const int MaxColors=8;
    private const double DefaultSpeed=40, DefaultSpread=50;
    public static readonly IReadOnlyList<(string Name,double Angle)> Directions=new (string,double)[]
    {
        ("Left to right",0),("Back-left to front-right",45),("Back to front",90),("Back-right to front-left",135),
        ("Right to left",180),("Front-right to back-left",225),("Front to back",270),("Front-left to back-right",315),
    };
    private static double Clamp100(double n)=>Math.Min(100,Math.Max(0,n));
    public static double Wrap01(double n)=>n-Math.Floor(n);
    private static EffectParams CloneParams(EffectParams? p)=>p is null?new():new(){Speed=p.Speed,AngleDeg=p.AngleDeg,Spread=p.Spread,Colors=p.Colors is null?new():new List<string>(p.Colors)};
    public static EffectSettings Clone(EffectSettings s)=>new(){Kind=s.Kind,Wave=CloneParams(s.Wave),Breathing=CloneParams(s.Breathing),Spectrum=CloneParams(s.Spectrum)};
    private static bool TryKind(string? kind,out EffectKind k){k=EffectKind.Static;if(kind is null) return false;foreach(var n in Enum.GetNames<EffectKind>()) if(string.Equals(n,kind,StringComparison.Ordinal)){k=Enum.Parse<EffectKind>(n);return true;}return false;}
    public static EffectKind KindOf(string? kind)=>TryKind(kind,out var k)?k:EffectKind.Static;
    public static EffectParams? ParamsFor(EffectSettings s,EffectKind kind)=>kind switch{EffectKind.Wave=>s.Wave,EffectKind.Breathing=>s.Breathing,EffectKind.Spectrum=>s.Spectrum,_=>null};
    public static double CyclesPerSecond(double speed)=>0.05+Clamp100(speed)/100*(2-0.05);
    // Inverted on purpose: wavelength shrinks as spread grows.
    public static double Wavelength(double spread)=>3.0-Clamp100(spread)/100*(3.0-0.2);
    public static double Project(DeskPoint p,double angleDeg){var a=angleDeg*Math.PI/180;return p.X*Math.Cos(a)+p.Y*Math.Sin(a);}
    public static double PhaseAt(DeskPoint p,double tMs,EffectParams q)=>Wrap01(Project(p,q.AngleDeg)/Wavelength(q.Spread)-tMs/1000*CyclesPerSecond(q.Speed));
    private static byte Ch(double v)=>(byte)Math.Round(v,MidpointRounding.AwayFromZero);
    public static Rgb HsvToRgb(double h,double s,double v)
    {
        double c=v*s,x=c*(1-Math.Abs(h/60%2-1)),m=v-c;
        var (r,g,b)=h<60?(c,x,0d):h<120?(x,c,0d):h<180?(0d,c,x):h<240?(0d,x,c):h<300?(x,0d,c):(c,0d,x);
        return new(Ch((r+m)*255),Ch((g+m)*255),Ch((b+m)*255));
    }
    private static Rgb Lerp(Rgb a,Rgb b,double f){var t=Math.Min(1,Math.Max(0,f));return new(Ch(a.R+(b.R-a.R)*t),Ch(a.G+(b.G-a.G)*t),Ch(a.B+(b.B-a.B)*t));}
    public static Rgb Dim(Rgb c,double f){var t=Math.Min(1,Math.Max(0,f));return new(Ch(c.R*t),Ch(c.G*t),Ch(c.B*t));}
    // N colours interpolate across N segments; the last wraps back to the first.
    public static Rgb SamplePalette(IReadOnlyList<Rgb> colors,double phase)
    {
        var t=Wrap01(phase);
        if(colors.Count==0) return HsvToRgb(t*360,1,1);
        if(colors.Count==1) return colors[0];
        var scaled=t*colors.Count;var i=(int)Math.Floor(scaled)%colors.Count;
        return Lerp(colors[i],colors[(i+1)%colors.Count],scaled-Math.Floor(scaled));
    }
    private static List<Rgb> Parse(List<string>? colors){var r=new List<Rgb>();if(colors is not null) foreach(var c in colors) if(Rgb.TryParse(c,out var p)) r.Add(p);return r;}
    // Breathing and Spectrum are in step across the desk: they use the phase at (0,0).
    public static Rgb Evaluate(EffectKind kind,DeskPoint pos,double tMs,EffectParams q,Rgb zoneColor)=>kind switch
    {
        EffectKind.Wave=>SamplePalette(Parse(q.Colors),PhaseAt(pos,tMs,q)),
        EffectKind.Breathing=>Dim(zoneColor,0.5-0.5*Math.Cos(2*Math.PI*PhaseAt(new(0,0),tMs,q))),
        EffectKind.Spectrum=>HsvToRgb(PhaseAt(new(0,0),tMs,q)*360,1,1),
        _=>zoneColor,
    };
    public static int NearestDirection(double angleDeg)
    {
        var a=double.IsFinite(angleDeg)?((angleDeg%360)+360)%360:0;var best=0;var bestD=double.MaxValue;
        for(var i=0;i<Directions.Count;i++){var d=Math.Abs(a-Directions[i].Angle);d=Math.Min(d,360-d);if(d<bestD){bestD=d;best=i;}}
        return best;
    }
    private static string? ValidateParams(string name,EffectParams? q)
    {
        if(q is null) return $"{name} settings are missing";
        if(!(q.Speed>=0&&q.Speed<=100)) return $"{name} speed must be 0 to 100";
        if(!(q.AngleDeg>=0&&q.AngleDeg<360)) return $"{name} angle must be 0 up to 360";
        if(!(q.Spread>=0&&q.Spread<=100)) return $"{name} spread must be 0 to 100";
        if(q.Colors is null) return $"{name} colours are missing";
        if(q.Colors.Count>MaxColors) return $"{name} has more than {MaxColors} colours";
        foreach(var c in q.Colors) if(!Rgb.TryParse(c,out _)) return $"{name} has a colour that is not #RRGGBB";
        return null;
    }
    public static string? Validate(EffectSettings s)
    {
        if(s is null) return "effect settings are missing";
        if(!TryKind(s.Kind,out _)) return "effect kind is not known";
        return ValidateParams("Wave",s.Wave)??ValidateParams("Breathing",s.Breathing)??ValidateParams("Spectrum",s.Spectrum);
    }
    private static EffectParams NormalizeParams(EffectParams? q)
    {
        q??=new();
        q.Speed=double.IsNaN(q.Speed)?DefaultSpeed:Clamp100(q.Speed);
        q.Spread=double.IsNaN(q.Spread)?DefaultSpread:Clamp100(q.Spread);
        q.AngleDeg=double.IsFinite(q.AngleDeg)?(q.AngleDeg%360+360)%360:0;
        q.Colors??=new();
        return q;
    }
    public static void Normalize(EffectSettings s)
    {
        s.Kind=TryKind(s.Kind,out var k)?k.ToString():"Static";
        s.Wave=NormalizeParams(s.Wave);s.Breathing=NormalizeParams(s.Breathing);s.Spectrum=NormalizeParams(s.Spectrum);
    }
}
