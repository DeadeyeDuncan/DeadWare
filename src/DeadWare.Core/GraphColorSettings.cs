namespace DeadWare.Core;

public static class GraphColorSettings
{
    public static IReadOnlyList<string> Kinds {get;}=Array.AsReadOnly(new[]{"cpu","cputemp","gpu","gputemp","memory","download","upload"});
    public static bool IsColor(string? value)=>value is {Length:7}&&value[0]=='#'&&value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")==false;
    public static Dictionary<string,string> Normalize(IReadOnlyDictionary<string,string>? colors)
        =>colors?.Where(p=>Kinds.Contains(p.Key)&&IsColor(p.Value)).ToDictionary(p=>p.Key,p=>p.Value.ToUpperInvariant())??new();
}
