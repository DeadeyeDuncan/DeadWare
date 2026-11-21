namespace DeadWare.Core;

// Lighting 3b: the layers editor's edits. Each returns a new deep copy of the list (null is no layers) and never changes its input, so a
// list the state or a pending request holds is never touched; an edit that can't apply (a full list, an index out of range, a move past an
// end) returns the copy unchanged.
public static class LayerEdits
{
    public static List<DeviceLayer> Add(List<DeviceLayer>? layers)
    {
        var next=Copy(layers);
        if(next.Count<DeviceLayers.MaxLayers) next.Add(new DeviceLayer());
        return next;
    }
    public static List<DeviceLayer> Remove(List<DeviceLayer>? layers,int i)
    {
        var next=Copy(layers);
        if(i>=0&&i<next.Count) next.RemoveAt(i);
        return next;
    }
    // by is -1 (up the list, further under) or +1 (down the list, further on top).
    public static List<DeviceLayer> Move(List<DeviceLayer>? layers,int i,int by)
    {
        var next=Copy(layers);
        int to=i+by;
        if(by is not(-1 or 1)||i<0||i>=next.Count||to<0||to>=next.Count) return next;
        (next[i],next[to])=(next[to],next[i]);
        return next;
    }
    public static List<DeviceLayer> With(List<DeviceLayer>? layers,int i,Action<DeviceLayer> change)
    {
        var next=Copy(layers);
        if(i>=0&&i<next.Count&&next[i] is{} l) change(l);
        return next;
    }
    // "Key ripple · all keys", "A colour · 1 key", "A colour · 4 keys", "An effect · no keys".
    public static string Title(DeviceLayer l)
    {
        string? kind=l?.Kind switch{DeviceLayers.Color=>"A colour",DeviceLayers.Effect=>"An effect",DeviceLayers.Fade=>"Key fade",DeviceLayers.Ripple=>"Key ripple",_=>null};
        if(kind is null) return "A layer this version doesn't know";
        int? n=l!.Lights?.Count;
        return kind+" · "+(n switch{null=>"all keys",0=>"no keys",1=>"1 key",_=>$"{n} keys"});
    }
    public static bool Reacts(List<DeviceLayer>? layers)=>layers?.Any(l=>l?.Kind is DeviceLayers.Fade or DeviceLayers.Ripple)??false;
    static List<DeviceLayer> Copy(List<DeviceLayer>? layers)=>DeviceLayers.Clone(layers)??[];
}
