namespace DeadWare.Core;

// A zone's matrix as OpenRGB reports it: row-major light indices, KeyMaps.Empty for a cell with no light.
public sealed record ZoneMatrix(int Width,int Height,IReadOnlyList<uint> Cells);
// One entry per light: its name, its position 0..1 (null when it is in no cell) and a key width as a fraction of the keyboard's width.
public sealed record KeyMapView(IReadOnlyList<string> Names,IReadOnlyList<double?> X,IReadOnlyList<double?> Y,double KeyWidth);
// Light positions from a keyboard's matrix, as DeadLight's keyboardLedPositions and supportsPerKeyWave (shared/keyboard-layout.ts).
public static class KeyMaps
{
    public const uint Empty=0xFFFFFFFF;
    public const int KeyboardType=5;
    public static bool IsKeyboard(LightingDevice d)=>d.Type==KeyboardType;
    // The device's own total as OpenRGB reports it (one name per light), else the sum of its zones.
    public static int LightCount(LightingDevice d)=>d.LedNames?.Count??d.Zones.Sum(z=>z.LedCount);
    // A map only when one zone with a matrix addresses every light of the device. Cells naming no light of it are ignored.
    public static KeyMapView? Of(LightingDevice d)
    {
        if(d.Zones.Count!=1||d.Zones[0].Matrix is not{} m) return null;
        var n=LightCount(d);
        if(n<1||d.Zones[0].LedCount!=n||m.Width<1||m.Height<1||m.Cells is null||m.Cells.Count!=(long)m.Width*m.Height) return null;
        var sx=new double[n];var sy=new double[n];var count=new int[n];
        for(var y=0;y<m.Height;y++) for(var x=0;x<m.Width;x++){var i=m.Cells[y*m.Width+x];if(i>=(uint)n) continue;sx[i]+=x;sy[i]+=y;count[i]++;}
        var xs=new double?[n];var ys=new double?[n];
        for(var i=0;i<n;i++)
        {
            if(count[i]==0) continue;
            xs[i]=m.Width==1?0.5:sx[i]/(count[i]*(m.Width-1.0));
            ys[i]=m.Height==1?0.5:sy[i]/(count[i]*(m.Height-1.0));
        }
        var names=d.LedNames is{} ln&&ln.Count==n?ln.ToArray():Enumerable.Repeat("",n).ToArray();
        return new(names,xs,ys,KeyWidth(xs));
    }
    // DeadLight's editor sizes a key from the smallest horizontal spacing.
    private static double KeyWidth(double?[] xs)
    {
        var distinct=xs.Where(x=>x is not null).Select(x=>x!.Value).Distinct().Order().ToArray();
        var gap=double.MaxValue;
        for(var i=1;i<distinct.Length;i++) gap=Math.Min(gap,distinct[i]-distinct[i-1]);
        return Math.Clamp((gap==double.MaxValue?0.04:gap)*0.85,0.008,0.10);
    }
    public static bool SupportsWave(LightingDevice d)=>IsKeyboard(d)&&Of(d) is{} m&&m.X.Any(x=>x is not null);
}
