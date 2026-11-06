namespace DeadWare.Core;

public sealed record RowMove(string Id,int From,int To);

/// <summary>Keeps the Processes table calm: moves as few rows as possible when re-sorting, and updates a held table without reordering.</summary>
public static class CalmOrderPlanner
{
    /// <summary>
    /// Moves that turn <paramref name="current"/> into <paramref name="sorted"/>, applied one after another in the order returned.
    /// Only rows outside the longest run that already keeps its relative order move; each move's From and To are indexes in the list as it stands before and after that move.
    /// Ids missing from either list are the caller's adds and removes and are ignored.
    /// </summary>
    public static IReadOnlyList<RowMove> Plan(IReadOnlyList<string> current,IReadOnlyList<string> sorted)
    {
        var sortedSet=new HashSet<string>(sorted);
        var working=current.Where(sortedSet.Contains).ToList();
        var workingSet=new HashSet<string>(working);
        var target=sorted.Where(workingSet.Contains).ToList();
        var targetIndex=new Dictionary<string,int>();
        for(var i=0;i<target.Count;i++)targetIndex[target[i]]=i;
        var keep=LongestIncreasing(working.Select(id=>targetIndex[id]).ToList()).Select(i=>working[i]).ToHashSet();
        var moves=new List<RowMove>();
        for(var t=0;t<target.Count;t++)
        {
            var id=target[t];
            if(keep.Contains(id))continue;
            var from=working.IndexOf(id);
            working.RemoveAt(from);
            var to=t==0?0:working.IndexOf(target[t-1])+1;
            working.Insert(to,id);
            moves.Add(new RowMove(id,from,to));
        }
        return moves;
    }

    /// <summary>
    /// The held order after a new reading: exited ids removed, new apps (no parent) appended in <paramref name="present"/> order,
    /// new children placed after the last current row of their parent (at the bottom, after the new apps, when the parent was not already shown).
    /// </summary>
    public static IReadOnlyList<string> Held(IReadOnlyList<string> current,IReadOnlyList<string> present,Func<string,string?> parentOf)
    {
        var presentSet=new HashSet<string>(present);
        var result=current.Where(presentSet.Contains).ToList();
        var shown=new HashSet<string>(result);
        var fresh=present.Where(id=>!shown.Contains(id)).ToList();
        foreach(var id in fresh.Where(id=>parentOf(id) is null))result.Add(id);
        foreach(var id in fresh.Where(id=>parentOf(id) is not null))
        {
            var parent=parentOf(id)!;
            var at=shown.Contains(parent)?result.FindLastIndex(r=>r==parent||parentOf(r)==parent):-1;
            if(at<0)result.Add(id);else result.Insert(at+1,id);
        }
        return result;
    }

    // Indexes of a longest strictly increasing subsequence (patience sorting, O(n log n)).
    static List<int> LongestIncreasing(IReadOnlyList<int> values)
    {
        var tails=new List<int>();
        var previous=new int[values.Count];
        for(var i=0;i<values.Count;i++)
        {
            int lo=0,hi=tails.Count;
            while(lo<hi){var mid=(lo+hi)/2;if(values[tails[mid]]<values[i])lo=mid+1;else hi=mid;}
            previous[i]=lo==0?-1:tails[lo-1];
            if(lo==tails.Count)tails.Add(i);else tails[lo]=i;
        }
        var run=new List<int>();
        for(var i=tails.Count==0?-1:tails[^1];i>=0;i=previous[i])run.Add(i);
        run.Reverse();
        return run;
    }
}

/// <summary>Decides whether the table order is held: pointer over the table, a menu or dialog open, or a recent navigation key.</summary>
public sealed class OrderHold
{
    public const double KeySeconds=3;
    double? lastKey;
    public bool PointerOver{get;set;}
    public bool MenuOpen{get;set;}
    public void KeyNavigated(double now)=>lastKey=now;
    public bool IsHeld(double now)=>PointerOver||MenuOpen||(lastKey is double k&&now-k<KeySeconds);
}
