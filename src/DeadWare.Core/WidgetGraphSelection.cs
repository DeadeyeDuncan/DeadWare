namespace DeadWare.Core;

public static class WidgetGraphSelection
{
    public static IReadOnlyList<string> Kinds {get;}=Array.AsReadOnly(new[]{"cpu","cputemp","memory","network"});

    public static List<string> Normalize(IEnumerable<string>? kinds)
    {
        if(kinds is null)return Kinds.ToList();
        var selected=new HashSet<string>(kinds,StringComparer.Ordinal);
        return Kinds.Where(selected.Contains).ToList();
    }

    public static double MinimumHeight(int count)=>count==1?200:Math.Max(180,40+120*Math.Clamp(count,0,Kinds.Count));
    public static double DefaultHeight(int count)=>Math.Max(180,120+120*Math.Clamp(count,0,Kinds.Count));
}
