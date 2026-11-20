namespace DeadWare.Core;

public enum DeskItemKind { Device, Zone, Tower }
// ZoneKeys: the placed zone keys the item stands for (all of a device's for a Device item, the one for a Zone item, none for the Tower).
public sealed record DeskItem(DeskItemKind Kind,string? DeviceKey,string? ZoneKey,DeskPoint At,string Label,bool OwnEffect,IReadOnlyList<string> ZoneKeys);
public readonly record struct DeskBox(double X0,double Y0,double X1,double Y1);
public sealed record DeskScene(IReadOnlyList<DeskItem> Items,DeskBox Tower,int TowerDevices,IReadOnlyList<DeviceView> NotPlaced);

// Pure geometry for the layout editor, ported from DeadLight LayoutTab.tsx. Every function returns a new layout and never mutates its argument.
public static class DeskGeometry
{
    public const int Snap=48;
    public const double DragEpsilon=0.01,TowerHalf=0.06;
    private static double Clamp01(double n)=>Math.Min(1,Math.Max(0,n));
    private static double SnapTo(double n)=>Math.Round(Clamp01(n)*Snap,MidpointRounding.AwayFromZero)/Snap;
    private static bool Own(DeviceView d)=>d.Kind==PaintKind.Hardware;

    private static List<DeviceView> Placeable(IEnumerable<DeviceView> devices)
    {
        var keys=DeskLayouts.Arrangeable(devices).Select(a=>a.Key).ToHashSet();
        return devices.Where(d=>keys.Contains(d.Key)).ToList();
    }
    // The device's zones with LEDs that have a point in the layout, in the device's own order.
    private static List<ZoneView> PlacedZones(DeskLayout l,DeviceView d)=>d.Zones.Where(z=>z.LedCount>0&&l.Zones.ContainsKey(LightingKeys.Zone(d.Key,z.Index))).ToList();
    private static List<string> ZoneKeysOf(DeviceView d)=>d.Zones.Select(z=>LightingKeys.Zone(d.Key,z.Index)).ToList();

    private static DeskBox ClosedBox(DeskLayout l)=>new(Clamp01(l.Tower.At.X-TowerHalf),Clamp01(l.Tower.At.Y-TowerHalf),Clamp01(l.Tower.At.X+TowerHalf),Clamp01(l.Tower.At.Y+TowerHalf));

    public static DeskScene Scene(DeskLayout layout,IReadOnlyList<DeviceView> devices,bool towerOpen,IReadOnlySet<string> splitDevices)
    {
        var items=new List<DeskItem>();var notPlaced=new List<DeviceView>();var memberPoints=new List<DeskPoint>();int towerDevices=0;
        foreach(var d in Placeable(devices))
        {
            bool member=layout.Tower.Members.Contains(d.Key);
            if(layout.Unplaced.Contains(d.Key)){notPlaced.Add(d);continue;}
            if(member) towerDevices++;
            var placed=PlacedZones(layout,d);
            if(placed.Count==0) continue;
            if(member) foreach(var z in placed) memberPoints.Add(layout.Zones[LightingKeys.Zone(d.Key,z.Index)]);
            if(member&&!towerOpen) continue;
            var keys=placed.Select(z=>LightingKeys.Zone(d.Key,z.Index)).ToList();
            if(splitDevices.Contains(d.Key)) for(int i=0;i<placed.Count;i++) items.Add(new(DeskItemKind.Zone,d.Key,keys[i],layout.Zones[keys[i]],placed[i].Name,Own(d),new[]{keys[i]}));
            else items.Add(new(DeskItemKind.Device,d.Key,null,layout.Zones[keys[0]],d.Name,Own(d),keys));
        }
        var box=ClosedBox(layout);
        if(towerOpen&&memberPoints.Count>0) box=new(Clamp01(memberPoints.Min(p=>p.X)-TowerHalf),Clamp01(memberPoints.Min(p=>p.Y)-TowerHalf),Clamp01(memberPoints.Max(p=>p.X)+TowerHalf),Clamp01(memberPoints.Max(p=>p.Y)+TowerHalf));
        items.Add(new(DeskItemKind.Tower,null,null,layout.Tower.At,"PC tower",false,Array.Empty<string>()));
        return new(items,box,towerDevices,notPlaced);
    }

    private static bool Near(DeskItem i,DeskPoint at,double hw,double hh)=>Math.Abs(at.X-i.At.X)<=hw&&Math.Abs(at.Y-i.At.Y)<=hh;
    // Topmost wins: zone dots over devices over the tower; later items over earlier ones within a kind.
    public static DeskItem? Hit(DeskScene scene,DeskPoint at,double halfWidth,double halfHeight)
    {
        foreach(var kind in new[]{DeskItemKind.Zone,DeskItemKind.Device})
            for(int i=scene.Items.Count-1;i>=0;i--){var it=scene.Items[i];if(it.Kind==kind&&Near(it,at,halfWidth,halfHeight)) return it;}
        return InTower(scene,at)?scene.Items.LastOrDefault(i=>i.Kind==DeskItemKind.Tower):null;
    }
    public static bool InTower(DeskScene scene,DeskPoint at)=>at.X>=scene.Tower.X0&&at.X<=scene.Tower.X1&&at.Y>=scene.Tower.Y0&&at.Y<=scene.Tower.Y1;

    private static DeskLayout MoveKeys(DeskLayout layout,IEnumerable<string> keys,double dx,double dy)
    {
        var next=DeskLayouts.Clone(layout);
        foreach(var k in keys) if(layout.Zones.TryGetValue(k,out var p)) next.Zones[k]=new(SnapTo(p.X+dx),SnapTo(p.Y+dy));
        return next;
    }
    public static DeskLayout MoveDevice(DeskLayout layout,IReadOnlyList<DeviceView> devices,string deviceKey,double dx,double dy)
    {
        var d=devices.FirstOrDefault(x=>x.Key==deviceKey);
        return MoveKeys(layout,d is null?Array.Empty<string>():ZoneKeysOf(d),dx,dy);
    }
    public static DeskLayout MoveZone(DeskLayout layout,string zoneKey,double dx,double dy)=>MoveKeys(layout,new[]{zoneKey},dx,dy);
    public static DeskLayout MoveTower(DeskLayout layout,IReadOnlyList<DeviceView> devices,double dx,double dy)
    {
        var next=MoveKeys(layout,devices.Where(d=>layout.Tower.Members.Contains(d.Key)).SelectMany(ZoneKeysOf),dx,dy);
        next.Tower.At=new(SnapTo(layout.Tower.At.X+dx),SnapTo(layout.Tower.At.Y+dy));
        return next;
    }

    public static DeskLayout SetInTower(DeskLayout layout,string deviceKey,bool inside)
    {
        var next=DeskLayouts.Clone(layout);
        if(inside){if(!next.Tower.Members.Contains(deviceKey)) next.Tower.Members.Add(deviceKey);next.Tower.Excluded.RemoveAll(k=>k==deviceKey);}
        else{next.Tower.Members.RemoveAll(k=>k==deviceKey);if(!next.Tower.Excluded.Contains(deviceKey)) next.Tower.Excluded.Add(deviceKey);}
        return next;
    }
    public static DeskLayout SetUnplaced(DeskLayout layout,IReadOnlyList<DeviceView> devices,string deviceKey,bool unplaced)
    {
        var next=DeskLayouts.Clone(layout);
        if(unplaced)
        {
            if(!next.Unplaced.Contains(deviceKey)) next.Unplaced.Add(deviceKey);
            var d=devices.FirstOrDefault(x=>x.Key==deviceKey);
            if(d is not null) foreach(var k in ZoneKeysOf(d)) next.Zones.Remove(k);
            next.Tower.Members.RemoveAll(k=>k==deviceKey);
            return next;
        }
        next.Unplaced.RemoveAll(k=>k==deviceKey);
        return DeskLayouts.AutoArrange(next,DeskLayouts.Arrangeable(devices));
    }
    public static DeskLayout WithShape(DeskLayout layout,DeskShape shape){var next=DeskLayouts.Clone(layout);next.Desk=DeskLayouts.Preset(shape);return next;}
    public static DeskLayout Reset(DeskLayout layout,IReadOnlyList<DeviceView> devices)
    {
        var empty=DeskLayouts.Empty();empty.Desk=DeskLayouts.Clone(layout).Desk;
        return DeskLayouts.AutoArrange(empty,DeskLayouts.Arrangeable(devices));
    }
    public static string Where(DeskLayout layout,DeviceView device)=>Own(device)?"Own effect":layout.Unplaced.Contains(device.Key)?"Not placed":layout.Tower.Members.Contains(device.Key)?"Inside PC":"On desk";
}
