using System.Globalization;
namespace DeadWare.Core;

public static class GraphColorSettings
{
    public static IReadOnlyList<string> Kinds {get;}=Array.AsReadOnly(new[]{"cpu","cputemp","cpupump","gpu","gputemp","gpufan","memory","download","upload","diskread","diskwrite"});
    public const int MaxCores=64;
    private const string CorePrefix="cpucore/";
    public static string CoreKey(int index)=>$"{CorePrefix}{index}";
    /// <summary>Strict zero-based core key: "cpucore/0".."cpucore/63", plain ASCII decimal, no leading zeros or sign.</summary>
    public static bool TryCoreIndex(string? key,out int index)
    {
        index=0;
        if(key is null||!key.StartsWith(CorePrefix,StringComparison.Ordinal))return false;
        var digits=key.AsSpan(CorePrefix.Length);
        if(digits.Length is 0 or >2||digits.ContainsAnyExcept("0123456789")||digits.Length==2&&digits[0]=='0')return false;
        if(int.TryParse(digits,NumberStyles.None,CultureInfo.InvariantCulture,out index)&&index<MaxCores)return true;
        index=0;return false;
    }
    public static bool IsKind(string? key)=>key is not null&&(Kinds.Contains(key)||TryCoreIndex(key,out _));
    public static bool IsColor(string? value)=>value is {Length:7}&&value[0]=='#'&&value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")==false;
    public static Dictionary<string,string> Normalize(IReadOnlyDictionary<string,string>? colors)
        =>colors?.Where(p=>IsKind(p.Key)&&IsColor(p.Value)).ToDictionary(p=>p.Key,p=>p.Value.ToUpperInvariant())??new();
}
