namespace DeadWare.Core;

public sealed record GraphLineStyle(string Pattern="default",bool Glow=false);

public static class GraphLineStyles
{
    public static bool IsPattern(string? pattern)=>pattern is "default" or "solid" or "dashed" or "dotted";
    public static Dictionary<string,GraphLineStyle> Normalize(IReadOnlyDictionary<string,GraphLineStyle>? styles)
        =>styles?.Where(p=>GraphColorSettings.Kinds.Contains(p.Key)&&p.Value is not null)
            .Select(p=>new KeyValuePair<string,GraphLineStyle>(p.Key,new(IsPattern(p.Value.Pattern)?p.Value.Pattern:"default",p.Value.Glow)))
            .Where(p=>p.Value.Pattern!="default"||p.Value.Glow).ToDictionary(p=>p.Key,p=>p.Value)??new();
}
