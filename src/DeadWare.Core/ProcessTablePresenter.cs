using System.Globalization;
namespace DeadWare.Core;

/// <summary>
/// Turns a process scan into the Processes table's rows, with no user-interface framework: one row per application,
/// its processes' totals, the chosen columns, search and sort, with an application's processes listed under it when
/// it is expanded, and an order that can be held still between refreshes.
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

    /// <summary>The key of an application's row and of one process's row.</summary>
    public static string AppRowKey(string appKey)=>"app:"+appKey;
    public static string ProcessRowKey(ProcessSample process)=>string.Create(CultureInfo.InvariantCulture,$"pid:{process.Pid}:{process.Generation}");

    /// <summary>
    /// The table with expansion: one row per matching application, followed by a row per process when the application
    /// is in <paramref name="expanded"/> (by row key) and has more than one. An application with several processes
    /// starts its name with a closed or open marker. With <paramref name="keepOrder"/> (application row keys), the
    /// applications already listed there keep those positions and new ones follow in sort order, so the rows hold
    /// still under the pointer.
    /// </summary>
    public static IReadOnlyList<ProcessTableRow> Tree(IReadOnlyList<ProcessSample> processes,IReadOnlyList<string> visible,string? sortKey,bool descending,string query,
        IReadOnlySet<string> expanded,Func<string,bool>? notRecorded=null,IReadOnlyList<string>? keepOrder=null)
    {
        var sort=ProcessTableColumns.EffectiveSort(sortKey,descending,visible);
        var groups=ProcessGrouping.Group(processes).Where(group=>query.Length==0||ProcessGrouping.Matches(group,query)).ToList();
        groups.Sort((a,b)=>ProcessSort.Compare(a,b,sort.Key,sort.Descending));
        if(keepOrder is {Count:>0})
        {
            var position=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int i=0;i<keepOrder.Count;i++)position.TryAdd(keepOrder[i],i);
            // A stable sort: listed applications by their held position, the rest after them in the order just sorted.
            groups=groups.Select((group,index)=>(group,index)).OrderBy(g=>position.TryGetValue(AppRowKey(g.group.Key),out int at)?at:int.MaxValue).ThenBy(g=>g.index).Select(g=>g.group).ToList();
        }
        var rows=new List<ProcessTableRow>();
        foreach(var group in groups)
        {
            string key=AppRowKey(group.Key);
            bool expandable=group.Members.Count>1,open=expandable&&expanded.Contains(key);
            string name=(expandable?(open?"▾ ":"▸ "):"   ")+(expandable?$"{group.Name} ({group.Members.Count})":group.Name)+(notRecorded?.Invoke(group.Name)==true?ProcessCopy.NotRecordedSuffix:"");
            var cells=new List<string>(visible.Count+1) {name};
            foreach(string column in visible)
                cells.Add(column=="gpuengine"?ProcessGrouping.EngineText(group) is {Length:>0} engine?engine:"—"
                    :column=="pid"&&expandable?""
                    :ProcessGrouping.Value(group,column) is { } value?Format(column,value):"—");
            string tip=expandable?$"{group.Members.Count} processes":"PID "+group.Members[0].Pid.ToString(CultureInfo.InvariantCulture);
            var members=group.Members.Select(m=>(m.Pid,m.Generation)).ToList();
            rows.Add(new(new TableRow(key,cells,tip),group.Name,true,expandable,open,null,members));
            if(!open)continue;
            var children=group.Members.ToList();
            children.Sort((a,b)=>ProcessSort.Compare(a,b,sort.Key,sort.Descending));
            foreach(var process in children)
            {
                string pid=process.Pid.ToString(CultureInfo.InvariantCulture);
                var childCells=new List<string>(visible.Count+1) {"        "+process.Name+(visible.Contains("pid")?"":" · PID "+pid)};
                foreach(string column in visible)
                    childCells.Add(column=="gpuengine"?ProcessGrouping.EngineText(process) is {Length:>0} engine?engine:"—"
                        :ProcessGrouping.Value(process,column) is { } value?Format(column,value):"—");
                rows.Add(new(new TableRow(ProcessRowKey(process),childCells,"PID "+pid),group.Name,false,false,false,key,[(process.Pid,process.Generation)]));
            }
        }
        return rows;
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

/// <summary>One row of the expandable Processes table and what the page needs to act on it.</summary>
/// <param name="Name">The application's bare name, for End task's question.</param>
/// <param name="ParentKey">The application row a process row belongs to; null on an application row.</param>
/// <param name="Members">The processes the row stands for: every process of an application, or the one process.</param>
public sealed record ProcessTableRow(TableRow Row,string Name,bool IsApp,bool Expandable,bool Open,string? ParentKey,IReadOnlyList<(int Pid,long Generation)> Members);
