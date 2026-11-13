using System.Globalization;
namespace DeadWare.Core;

/// <summary>
/// Turns a process scan into the Processes table's rows, with no user-interface framework: one row per application,
/// its processes' totals, the chosen columns, search and sort. Not yet covered: expanding an application into its
/// processes, and the calm reordering that holds rows still between refreshes.
/// </summary>
public static class ProcessTablePresenter
{
    /// <summary>The column keys in table order: name first, then the visible columns.</summary>
    public static IReadOnlyList<string> Keys(IReadOnlyList<string> visible)=>["name",..visible];

    public static IReadOnlyList<string> Headers(IReadOnlyList<string> visible)=>Keys(visible).Select(ProcessTableColumns.Header).ToList();

    /// <summary>One row per application that matches <paramref name="query"/>, sorted by the chosen column, or by the default when that column is hidden.</summary>
    public static IReadOnlyList<TableRow> Rows(IReadOnlyList<ProcessSample> processes,IReadOnlyList<string> visible,string? sortKey,bool descending,string query)
    {
        var sort=ProcessTableColumns.EffectiveSort(sortKey,descending,visible);
        var groups=ProcessGrouping.Group(processes).Where(group=>query.Length==0||ProcessGrouping.Matches(group,query)).ToList();
        groups.Sort((a,b)=>ProcessSort.Compare(a,b,sort.Key,sort.Descending));
        return groups.Select(group=>
        {
            var cells=new List<string>(visible.Count+1) {group.Members.Count>1?$"{group.Name} ({group.Members.Count})":group.Name};
            foreach(string key in visible)
                cells.Add(key=="gpuengine"?ProcessGrouping.EngineText(group) is {Length:>0} engine?engine:"—"
                    :ProcessGrouping.Value(group,key) is { } value?Format(key,value):"—");
            string tip=group.Members.Count==1?"PID "+group.Members[0].Pid.ToString(CultureInfo.InvariantCulture):$"{group.Members.Count} processes";
            return new TableRow(group.Key,cells,tip);
        }).ToList();
    }

    /// <summary>The sort after a header click: the same column flips direction; a new one starts in its natural direction.</summary>
    public static (string Key,bool Descending) Clicked(string? currentKey,bool currentDescending,string clickedKey)=>
        clickedKey==currentKey?(clickedKey,!currentDescending):(clickedKey,!ProcessTableColumns.AscendingFirst(clickedKey));

    /// <summary>A value as its column writes it, in the user's locale.</summary>
    public static string Format(string key,double value)=>key switch
    {
        "pid"=>value.ToString("0",CultureInfo.InvariantCulture),
        "cpu" or "gpu"=>value.ToString("0.#",CultureInfo.CurrentCulture)+" %",
        "memory"=>HistoryChartModel.Bytes(value),
        // A known zero reads "0 B", not "0.0 B".
        "gpumemory" or "gpushared"=>value==0?"0 B":HistoryChartModel.Bytes(value),
        _=>HistoryChartModel.Bytes(value,true),
    };
}
