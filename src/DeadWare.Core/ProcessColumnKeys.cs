namespace DeadWare.Core;
/// <summary>The Processes table columns after NAME, in their fixed order, and the saved subset of them.</summary>
public static class ProcessColumnKeys
{
    public static IReadOnlyList<string> All {get;}=["pid","cpu","memory","io","ioread","iowrite","disk","diskread","diskwrite","network","download","upload","gpu","gpuengine","gpumemory","gpushared"];
    public static IReadOnlyList<string> Default {get;}=["cpu","memory","io","disk","network","gpu","gpumemory"];
    public static IReadOnlyList<string> AdministratorOnly {get;}=["disk","diskread","diskwrite","network","download","upload"];
    public static IReadOnlyList<string> DiskKeys {get;}=["disk","diskread","diskwrite"];
    /// <summary>Null gives the default; null, unknown and duplicate keys are dropped; the rest follow <see cref="All"/>. An empty list is kept.</summary>
    public static List<string> Normalize(IEnumerable<string?>? keys)
    {
        if(keys is null)return Default.ToList();
        var wanted=keys.Where(k=>k is not null).ToHashSet(StringComparer.Ordinal);
        return All.Where(wanted.Contains).ToList();
    }
    /// <summary>Adds or removes only <paramref name="key"/>; every other saved key stays, including ones the current build or rights hide.</summary>
    public static List<string> Toggle(IReadOnlyList<string> saved,string key,bool on)
    {
        var next=saved.Where(k=>k!=key).ToList();
        if(on)next.Add(key);
        return Normalize(next);
    }
}
