namespace DeadWare.Core;

public sealed record GraphLineStyle(string Pattern="default",bool Glow=false,double Thickness=1,double GlowStrength=1);

public static class GraphLineStyles
{
    public static bool IsPattern(string? pattern)=>pattern is "default" or "solid" or "dashed" or "dotted";
    public static GraphLineStyle Normalize(GraphLineStyle style)=>new(IsPattern(style.Pattern)?style.Pattern:"default",style.Glow,
        double.IsFinite(style.Thickness)?Math.Clamp(style.Thickness,.5,3):1,
        double.IsFinite(style.GlowStrength)?Math.Clamp(style.GlowStrength,0,3):1);
    public static Dictionary<string,GraphLineStyle> Normalize(IReadOnlyDictionary<string,GraphLineStyle>? styles)
        =>styles?.Where(p=>GraphColorSettings.Kinds.Contains(p.Key)&&p.Value is not null)
            .Select(p=>new KeyValuePair<string,GraphLineStyle>(p.Key,Normalize(p.Value)))
            .Where(p=>p.Value!=new GraphLineStyle()).ToDictionary(p=>p.Key,p=>p.Value)??new();
}
