using System.Text.RegularExpressions;

namespace DeadWare.Core;

// Plain classes on purpose: System.Text.Json reflects over them. Ported from DeadLight shared/layout.ts, without rotate and mirror.
public sealed class DeskRect { public double X{get;set;} public double Y{get;set;} public double W{get;set;} public double H{get;set;} }
public sealed class TowerGroup { public DeskPoint At{get;set;}=new(0.14,0.3); public List<string> Members{get;set;}=new(); public List<string> Excluded{get;set;}=new(); }
public sealed class DeskLayout { public int Version{get;set;}=1; public Dictionary<string,DeskPoint> Zones{get;set;}=new(); public TowerGroup Tower{get;set;}=new(); public List<DeskRect> Desk{get;set;}=DeskLayouts.Preset(DeskShape.LLeft); public List<string> Unplaced{get;set;}=new(); }
public enum DeskShape { Straight, LLeft, LRight }
public sealed record ArrangeDevice(string Key,string Name,IReadOnlyList<int> ZoneIndexes);

public static class DeskLayouts
{
    public const int Columns=12,Rows=8,MaxEntries=4096;
    private const int MaxKeyLength=512;
    private const double Slack=1e-9,CellW=1.0/Columns,CellH=1.0/Rows;
    // A wrong guess costs one drag, and the exclusion list makes it stick.
    private static readonly Regex Peripheral=new("keyboard|keypad|mouse|mousepad|mousemat|headset|apex|aerox|arctis|rival|deathadder",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private readonly record struct Region(int C0,int C1,int R0,int R1);
    private static readonly Region PeripheralRegion=new(2,11,6,7);

    private static DeskRect R(double x,double y,double w,double h)=>new(){X=x,Y=y,W=w,H=h};
    public static List<DeskRect> Preset(DeskShape shape)=>shape switch
    {
        DeskShape.Straight=>new(){R(0.05,0.55,0.9,0.35)},
        DeskShape.LLeft=>new(){R(0.28,0.55,0.67,0.35),R(0.05,0.15,0.23,0.75)},
        _=>new(){R(0.05,0.55,0.67,0.35),R(0.72,0.15,0.23,0.75)},
    };
    private static bool SameRects(List<DeskRect> a,List<DeskRect> b)
    {
        if(a.Count!=b.Count) return false;
        for(int i=0;i<a.Count;i++) if(a[i] is null||Math.Abs(a[i].X-b[i].X)>Slack||Math.Abs(a[i].Y-b[i].Y)>Slack||Math.Abs(a[i].W-b[i].W)>Slack||Math.Abs(a[i].H-b[i].H)>Slack) return false;
        return true;
    }
    public static DeskShape? ShapeOf(DeskLayout layout)
    {
        if(layout.Desk is null) return null;
        foreach(var s in new[]{DeskShape.Straight,DeskShape.LLeft,DeskShape.LRight}) if(SameRects(layout.Desk,Preset(s))) return s;
        return null;
    }
    public static DeskLayout Empty()=>new();
    public static DeskLayout Clone(DeskLayout l)=>new()
    {
        Version=l.Version,Zones=new(l.Zones),
        Tower=new(){At=l.Tower.At,Members=new(l.Tower.Members),Excluded=new(l.Tower.Excluded)},
        Desk=l.Desk.Select(r=>R(r.X,r.Y,r.W,r.H)).ToList(),Unplaced=new(l.Unplaced),
    };

    private static bool InUnit(double n)=>n>=-Slack&&n<=1+Slack;   // false for NaN and infinity too
    private static string? ValidateKeys(string what,List<string>? keys)
    {
        if(keys is null) return $"layout {what} must be a list";
        if(keys.Count>MaxEntries) return $"layout {what} holds too many entries";
        foreach(var k in keys){if(k is null) return $"layout {what} must contain only strings";if(k.Length>MaxKeyLength) return $"layout {what} holds a key that is too long";}
        return null;
    }
    private static string? ValidateRect(DeskRect? d,int i)
    {
        if(d is null) return $"desk[{i}] must be an object";
        if(!InUnit(d.X)||!InUnit(d.Y)||!InUnit(d.W)||!InUnit(d.H)) return $"desk[{i}] must lie in the unit square";
        if(!(d.W>0)||!(d.H>0)) return $"desk[{i}] must have area";
        if(d.X+d.W>1+Slack||d.Y+d.H>1+Slack) return $"desk[{i}] must lie in the unit square";
        return null;
    }
    // Returns the reason a layout is refused, or null when it is sound. Never throws on a null member or element.
    public static string? Validate(DeskLayout layout)
    {
        if(layout is null) return "layout must be an object";
        if(layout.Version!=1) return "unsupported layout version";
        if(layout.Zones is null) return "layout zones must be an object";
        if(layout.Zones.Count>MaxEntries) return "layout zones hold too many entries";
        foreach(var (key,p) in layout.Zones)
        {
            if(key is null||key.Length>MaxKeyLength) return "layout zones hold a key that is too long";
            if(!InUnit(p.X)||!InUnit(p.Y)) return $"layout zone {key} must be a point in the unit square";
        }
        var t=layout.Tower;
        if(t is null) return "layout tower must be an object";
        if(!InUnit(t.At.X)||!InUnit(t.At.Y)) return "layout tower anchor must be a point in the unit square";
        var reason=ValidateKeys("tower members",t.Members)??ValidateKeys("tower excluded",t.Excluded);
        if(reason is not null) return reason;
        if(layout.Desk is null) return "layout desk must be a list";
        if(layout.Desk.Count>2) return "layout desk may hold at most two rectangles";
        for(int i=0;i<layout.Desk.Count;i++){reason=ValidateRect(layout.Desk[i],i);if(reason is not null) return reason;}
        return ValidateKeys("unplaced",layout.Unplaced);
    }

    // Name-based, because the device type does not tell a keyboard from a fan hub.
    public static bool IsTowerDevice(string name)=>!Peripheral.IsMatch(name);

    // The devices a desk layout can place: painted ones (not Unsupported), and only the zones that have LEDs, which the painter skips otherwise.
    public static IReadOnlyList<ArrangeDevice> Arrangeable(IEnumerable<DeviceView> devices)
    {
        var list=new List<ArrangeDevice>();
        foreach(var d in devices)
        {
            if(d.Kind==PaintKind.Unsupported) continue;
            var idx=d.Zones.Where(z=>z.LedCount>0).Select(z=>z.Index).ToList();
            if(idx.Count>0) list.Add(new ArrangeDevice(d.Key,d.Name,idx));
        }
        return list;
    }

    // Anchored on the tower's own position, so a device that registers after the tower was dragged lands inside it.
    private static Region TowerRegion(DeskPoint at)
    {
        int c=Math.Min(Columns-4,Math.Max(0,(int)Math.Floor(at.X*Columns-2+0.5))),r=Math.Min(Rows-4,Math.Max(0,(int)Math.Floor(at.Y*Rows-2+0.5)));
        return new(c,c+3,r,r+3);
    }
    private static DeskPoint CellAt(double col,double row)=>new((col+0.5)/Columns,(row+0.5)/Rows);
    private static bool Occupied(DeskPoint p,List<DeskPoint> taken)=>taken.Any(q=>Math.Abs(q.X-p.X)<CellW/2&&Math.Abs(q.Y-p.Y)<CellH/2);
    // Overflow spills to the NEAREST free cell, ties broken by row then column, so it stays deterministic and the tower stays together.
    private static DeskPoint FirstFreeCell(Region region,List<DeskPoint> taken)
    {
        for(int row=region.R0;row<=region.R1;row++) for(int col=region.C0;col<=region.C1;col++){var p=CellAt(col,row);if(!Occupied(p,taken)) return p;}
        var near=CellAt((region.C0+region.C1)/2.0,(region.R0+region.R1)/2.0);
        var free=new List<DeskPoint>();
        for(int row=0;row<Rows;row++) for(int col=0;col<Columns;col++){var p=CellAt(col,row);if(!Occupied(p,taken)) free.Add(p);}
        if(free.Count==0) return CellAt(region.C0,region.R0);   // grid full: stack, deterministically
        double D(DeskPoint p)=>(p.X-near.X)*(p.X-near.X)+(p.Y-near.Y)*(p.Y-near.Y);
        return free.OrderBy(D).ThenBy(p=>p.Y).ThenBy(p=>p.X).First();
    }

    // Deterministic, stable and total. Returns the SAME object when nothing changed, so the caller can skip a write.
    public static DeskLayout AutoArrange(DeskLayout layout,IReadOnlyList<ArrangeDevice> devices)
    {
        var zones=new Dictionary<string,DeskPoint>(layout.Zones);
        var members=new List<string>(layout.Tower.Members);
        var live=devices.Where(d=>!layout.Unplaced.Contains(d.Key)).ToList();
        bool changed=false;
        foreach(var d in live.OrderBy(d=>d.Key,StringComparer.Ordinal))
        {
            if(!IsTowerDevice(d.Name)||layout.Tower.Excluded.Contains(d.Key)||members.Contains(d.Key)) continue;
            members.Add(d.Key);changed=true;
        }
        var pending=new List<(string Key,bool Tower)>();
        foreach(var d in live) foreach(var i in d.ZoneIndexes){var k=LightingKeys.Zone(d.Key,i);if(!zones.ContainsKey(k)) pending.Add((k,members.Contains(d.Key)));}
        pending.Sort((a,b)=>string.CompareOrdinal(a.Key,b.Key));
        var taken=zones.Values.ToList();
        foreach(var item in pending)
        {
            var p=FirstFreeCell(item.Tower?TowerRegion(layout.Tower.At):PeripheralRegion,taken);
            zones[item.Key]=p;taken.Add(p);changed=true;
        }
        if(!changed) return layout;
        var next=Clone(layout);next.Zones=zones;next.Tower.Members=members;
        return next;
    }
}
