using System.Windows.Media;

namespace DeadWare.App;

/// <summary>Tokens every surface shares, derived from a palette so all nine themes agree on borders, tints and glow.</summary>
internal readonly record struct DerivedTokens(Color Hairline,Color AccentSoft,Color Surface1,Color Surface2,Color Glow);

internal static class ThemeTokens
{
    // Hairline 45%, AccentSoft 12%, Glow 22% of 255.
    private const byte HairlineAlpha=115,AccentSoftAlpha=31,GlowAlpha=56;

    public static DerivedTokens Derive(Color line,Color accent,Color panel,bool highContrast,bool blackout)
    {
        if(highContrast)return new(line,Transparent(accent),panel,panel,Transparent(accent));
        var hairline=Alpha(line,HairlineAlpha);
        var glow=Alpha(accent,GlowAlpha);
        if(blackout)return new(hairline,Alpha(accent,AccentSoftAlpha),Colors.Black,Colors.Black,glow);
        return new(hairline,Alpha(accent,AccentSoftAlpha),Mix(panel,Colors.White,.03),Mix(panel,Colors.Black,.06),glow);
    }

    /// <summary>Per-core trace colour: a hue/lightness fan around the CPU graph colour so neighbouring cores stay distinguishable.</summary>
    public static Color CoreTint(Color cpu,int index,int count,bool highContrast)
    {
        if(count<=1)return cpu;
        double p=Math.Clamp(index/(count-1.0),0,1);
        var (h,s,l)=ToHsl(cpu);
        if(highContrast)return FromHsl(h,s,0.35+0.40*p);
        double dir=(l<0.5?0.78:0.28)>l?1:-1;
        // The lightness ramp is linear in p, so clamp its two ends and interpolate: clamping each step
        // would flatten the ramp (identical colours) once it runs into the 0.22/0.85 limits.
        (double From,double To) Ramp(double d)=>(Math.Clamp(l+d*0.12,0.22,0.85),Math.Clamp(l+d*0.37,0.22,0.85));
        var (from,to)=Ramp(dir);
        // Squeezed against a limit (a mid-lightness colour near 0.22/0.85), the other way has more room.
        if(Math.Abs(to-from)<0.2){var other=Ramp(-dir);if(Math.Abs(other.To-other.From)>Math.Abs(to-from))(from,to)=other;}
        double lightness=from+(to-from)*p;
        // A grey has no hue to fan and too few 8-bit levels in the clamped range, so give it a faint one.
        return FromHsl(h+(-18+36*p),s<0.25?0.7+(0.25*0.85-0.7)*(s/0.25):s*0.85,lightness);
    }

    private static (double H,double S,double L) ToHsl(Color c)
    {
        double r=c.R/255.0,g=c.G/255.0,b=c.B/255.0;
        double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),l=(max+min)/2,d=max-min;
        if(d<1e-9)return (0,0,l);
        double s=d/(1-Math.Abs(2*l-1));
        double h=max==r?((g-b)/d)%6:max==g?(b-r)/d+2:(r-g)/d+4;
        return ((h*60+360)%360,s,l);
    }

    private static Color FromHsl(double h,double s,double l)
    {
        h=((h%360)+360)%360;
        double c=(1-Math.Abs(2*l-1))*s,x=c*(1-Math.Abs(h/60%2-1)),m=l-c/2;
        var (r,g,b)=(int)(h/60) switch{0=>(c,x,0d),1=>(x,c,0d),2=>(0d,c,x),3=>(0d,x,c),4=>(x,0d,c),_=>(c,0d,x)};
        byte B(double v)=>(byte)Math.Round(Math.Clamp((v+m)*255,0,255));
        return Color.FromRgb(B(r),B(g),B(b));
    }

    private static Color Alpha(Color color,byte alpha)=>Color.FromArgb(alpha,color.R,color.G,color.B);
    private static Color Transparent(Color color)=>Color.FromArgb(0,color.R,color.G,color.B);
    private static Color Mix(Color from,Color to,double amount)=>Color.FromRgb(
        (byte)Math.Round(from.R+(to.R-from.R)*amount),(byte)Math.Round(from.G+(to.G-from.G)*amount),(byte)Math.Round(from.B+(to.B-from.B)*amount));
}
