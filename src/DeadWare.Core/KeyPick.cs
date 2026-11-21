namespace DeadWare.Core;

// The large keyboard view's picked keys, by light index. A press on a key starts a drag that adds keys when that key was not picked and
// drops them when it was; every key the drag crosses goes the same way until it ends. A click is a drag that crosses nothing else.
public sealed class KeyPick
{
    private readonly HashSet<int> picked=[];
    private bool dragging,adding;
    public IReadOnlySet<int> Picked=>picked;
    public void Down(int light){dragging=true;adding=!picked.Contains(light);Apply(light);}
    public void Over(int light){if(dragging) Apply(light);}
    public void Up()=>dragging=false;
    public void Set(IEnumerable<int> lights){picked.Clear();picked.UnionWith(lights);}
    public void Clear()=>picked.Clear();
    private void Apply(int light){if(adding) picked.Add(light);else picked.Remove(light);}
}

// Undo for key paint: the whole painted set as it was before each change, newest last, at most Size kept. Each is a deep copy, so
// nothing changed later (a state, a request, the caller's dictionary) changes what comes back. Null is "nothing painted".
public sealed class PaintHistory
{
    public const int Size=50;
    private readonly List<Dictionary<int,PaintedKey>?> states=[];
    public void Push(Dictionary<int,PaintedKey>? before){states.Add(Copy(before));if(states.Count>Size) states.RemoveAt(0);}
    public bool CanUndo=>states.Count>0;
    public Dictionary<int,PaintedKey>? Pop()
    {
        if(states.Count==0) return null;
        var last=states[^1];states.RemoveAt(states.Count-1);
        return last;
    }
    public void Clear()=>states.Clear();
    public static Dictionary<int,PaintedKey>? Copy(Dictionary<int,PaintedKey>? keys)=>
        keys?.Where(k=>k.Value is not null).ToDictionary(k=>k.Key,k=>new PaintedKey{Name=k.Value.Name,Color=k.Value.Color});
}
