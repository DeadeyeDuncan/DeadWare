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

    private static Color Alpha(Color color,byte alpha)=>Color.FromArgb(alpha,color.R,color.G,color.B);
    private static Color Transparent(Color color)=>Color.FromArgb(0,color.R,color.G,color.B);
    private static Color Mix(Color from,Color to,double amount)=>Color.FromRgb(
        (byte)Math.Round(from.R+(to.R-from.R)*amount),(byte)Math.Round(from.G+(to.G-from.G)*amount),(byte)Math.Round(from.B+(to.B-from.B)*amount));
}
