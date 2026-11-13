namespace DeadWare.Core;

/// <summary>One row of a table: its cell texts in column order. Two rows are equal when their key, cells and tip are.</summary>
/// <param name="Key">Identifies the row across refreshes, such as a sensor id or a process id.</param>
/// <param name="Tip">An explanation shown when the pointer rests on the row, or null.</param>
public sealed record TableRow(string Key,IReadOnlyList<string> Cells,string? Tip=null)
{
    public bool Equals(TableRow? other)=>other is not null&&Key==other.Key&&Tip==other.Tip&&Cells.SequenceEqual(other.Cells);
    public override int GetHashCode()
    {
        var hash=new HashCode();hash.Add(Key);hash.Add(Tip);
        foreach(string cell in Cells)hash.Add(cell);
        return hash.ToHashCode();
    }
}

/// <summary>Keeps a table that refreshes every second from redrawing rows that did not change.</summary>
public static class TableSync
{
    /// <summary>
    /// Makes <paramref name="target"/> equal <paramref name="source"/>, position by position, writing only where they
    /// differ, so an observing list control redraws only those rows. Returns the number of writes.
    /// </summary>
    public static int Apply<T>(IList<T> target,IReadOnlyList<T> source)
    {
        int writes=0,shared=Math.Min(target.Count,source.Count);
        for(int i=0;i<shared;i++)
            if(!EqualityComparer<T>.Default.Equals(target[i],source[i])){target[i]=source[i];writes++;}
        for(int i=target.Count-1;i>=source.Count;i--){target.RemoveAt(i);writes++;}
        for(int i=target.Count;i<source.Count;i++){target.Add(source[i]);writes++;}
        return writes;
    }

    /// <summary>A reading to one decimal in the user's locale with its unit, or a dash when there is none, as the dashboard's tables write it.</summary>
    public static string Number(double? value,string unit="")=>
        value.HasValue&&double.IsFinite(value.Value)?string.Create(System.Globalization.CultureInfo.CurrentCulture,$"{value.Value:0.0}{unit}"):"—";
}
