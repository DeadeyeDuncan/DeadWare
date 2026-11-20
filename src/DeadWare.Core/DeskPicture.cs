namespace DeadWare.Core;

public enum PictureView { Desk, Inside }
public enum PartKind { Keyboard, Mouse, FanBar, FanRing, PumpRing, RamStick, Card }
public readonly record struct PicBox(double X,double Y,double W,double H) { public double CenterX=>X+W/2; public double CenterY=>Y+H/2; }
public sealed record PicturePart(string Id,PartKind Kind,PictureView View,PicBox Box,string Label,string DeviceName,bool Prefix,int Ordinal,IReadOnlyList<int> Zones,int ShownZone);
public sealed record DeskPicture(string Name,IReadOnlyList<string> Signature,PicBox Tower,PicBox Case,IReadOnlyList<PicturePart> Parts,IReadOnlyDictionary<string,string> OffPictureNames);
public sealed record ResolvedPart(PicturePart Part,string? DeviceKey,IReadOnlyList<string> ZoneKeys,string? ShownZoneKey,bool OwnEffect) { public bool Found=>DeviceKey is not null; }
public sealed record PictureScene(DeskPicture Picture,IReadOnlyList<ResolvedPart> Parts,IReadOnlyList<string> NotInPicture);

// A fixed drawing of one known rig: where each device sits in the 720 by 450 box of the desk and inside views, and how that maps onto the wave's unit square.
public static class DeskPictures
{
    public const string OwnerRigName="b650m-link-hub";
    private const double BoxW=720,BoxH=450,MinTap=28;
    private const string Hub="Corsair iCUE Link System Hub",Board="MSI MAG B650M MORTAR WIFI (MS-7D76)",Node="Corsair Lighting Node Pro",Ram="Corsair Vengeance RGB DDR5";
    private static PicturePart P(string id,PartKind kind,PictureView view,double x,double y,double w,double h,string label,string device,bool prefix,int ordinal,int[] zones,int shown)=>new(id,kind,view,new PicBox(x,y,w,h),label,device,prefix,ordinal,zones,shown);
    public static DeskPicture OwnerRig {get;}=new(OwnerRigName,[Board,Hub],new PicBox(40,388,74,56),new PicBox(140,22,440,418),
    [
        P("keyboard",PartKind.Keyboard,PictureView.Desk,290,96,200,58,"Keyboard","SteelSeries Apex Pro",false,0,[0],0),
        P("mouse",PartKind.Mouse,PictureView.Desk,555,108,22,36,"Mouse","SteelSeries Aerox 9",true,0,[0,1,2,3],1),
        P("fan1",PartKind.FanBar,PictureView.Inside,160,38,118,26,"Radiator fan 1",Hub,false,0,[0],0),
        P("fan2",PartKind.FanBar,PictureView.Inside,290,38,118,26,"Radiator fan 2",Hub,false,0,[1],1),
        P("fan3",PartKind.FanBar,PictureView.Inside,420,38,118,26,"Radiator fan 3",Hub,false,0,[2],2),
        P("pump",PartKind.PumpRing,PictureView.Inside,276,138,60,60,"Pump",Hub,false,0,[3],3),
        P("ram1",PartKind.RamStick,PictureView.Inside,392,112,8,112,"RAM 1",Ram,false,0,[0],0),
        P("ram2",PartKind.RamStick,PictureView.Inside,408,112,8,112,"RAM 2",Ram,false,1,[0],0),
        P("gpu",PartKind.Card,PictureView.Inside,170,270,290,50,"Graphics card","Gigabyte Radeon RX 6800 XT",true,0,[0,1,2,3,4],0),
        P("front1",PartKind.FanRing,PictureView.Inside,502,114,52,52,"Front fans",Node,false,0,[0],0),
        P("front2",PartKind.FanRing,PictureView.Inside,502,190,52,52,"Front fans",Node,false,0,[0],0),
        P("front3",PartKind.FanRing,PictureView.Inside,502,266,52,52,"Front fans",Node,false,0,[0],0),
    ],new Dictionary<string,string>{[Board]="MSI motherboard headers"});

    public static DeskPicture? Named(string? name)=>name==OwnerRigName?OwnerRig:null;
    // A picture applies when all its signature devices are present, or once saved, whatever devices are found: one of them is the PC's own
    // motherboard, which it can't lose, so a missing one is OpenRGB not detecting it yet (it lists devices a few at a time at each start).
    public static DeskPicture? Applies(IEnumerable<string> deviceNames,string? savedPicture)
    {
        var saved=Named(savedPicture);
        if(saved is not null) return saved;
        var names=new HashSet<string>(deviceNames,StringComparer.Ordinal);
        return OwnerRig.Signature.All(names.Contains)?OwnerRig:null;
    }

    public static PictureScene Resolve(DeskPicture picture,IReadOnlyList<DeviceView> devices)
    {
        var parts=new List<ResolvedPart>();var used=new HashSet<string>(StringComparer.Ordinal);
        var lit=DeskLayouts.Arrangeable(devices);   // a part only matches what the desk can place
        foreach(var p in picture.Parts)
        {
            var match=lit.Where(d=>p.Prefix?d.Name.StartsWith(p.DeviceName,StringComparison.Ordinal):d.Name==p.DeviceName).OrderBy(d=>d.Key,StringComparer.Ordinal).Skip(p.Ordinal).FirstOrDefault();
            if(match is null||!match.ZoneIndexes.Contains(p.ShownZone)){parts.Add(new(p,null,[],null,false));continue;}
            used.Add(match.Key);
            var keys=p.Zones.Where(match.ZoneIndexes.Contains).Select(i=>LightingKeys.Zone(match.Key,i)).ToList();
            parts.Add(new(p,match.Key,keys,LightingKeys.Zone(match.Key,p.ShownZone),devices.First(d=>d.Key==match.Key).Kind==PaintKind.Hardware));
        }
        var off=new List<string>();
        foreach(var a in DeskLayouts.Arrangeable(devices))
        {
            if(used.Contains(a.Key)) continue;
            var name=picture.OffPictureNames.TryGetValue(a.Name,out var n)?n:a.Name;
            off.Add($"{name} ({a.ZoneIndexes.Count} zone{(a.ZoneIndexes.Count==1?"":"s")})");
        }
        return new(picture,parts,off);
    }

    public static DeskPoint PointOf(DeskPicture picture,PicturePart part)
    {
        var b=part.Box;
        if(part.View==PictureView.Desk) return new(b.CenterX/BoxW,b.CenterY/BoxH);
        var c=picture.Case;
        return new(0.03+0.27*(b.CenterX-c.X)/c.W,0.60+0.38*(b.CenterY-c.Y)/c.H);
    }

    // The way the wave's colours travel across the desk drawing, as a unit vector in drawing units. The wave runs in the unit square, so a
    // step (dx,dy) in the drawing moves its phase by dx*cos/720+dy*sin/450: the wavefront is at right angles to (cos/720,sin/450), not (cos,sin).
    public static (double X,double Y) WaveDirection(double angleDeg)
    {
        double a=angleDeg*Math.PI/180,x=Math.Cos(a)/BoxW,y=Math.Sin(a)/BoxH,n=Math.Sqrt(x*x+y*y);
        return (x/n,y/n);
    }

    public static DeskLayout Layout(DeskPicture picture,IReadOnlyList<DeviceView> devices)
    {
        var scene=Resolve(picture,devices);var layout=new DeskLayout{Picture=picture.Name};
        var used=new HashSet<string>(scene.Parts.Where(p=>p.Found).Select(p=>p.DeviceKey!),StringComparer.Ordinal);
        var points=new Dictionary<string,List<DeskPoint>>();   // a zone lit by several parts takes the mean of their points
        foreach(var r in scene.Parts){var at=PointOf(picture,r.Part);foreach(var k in r.ZoneKeys){if(!points.TryGetValue(k,out var l)) points[k]=l=new();l.Add(at);}}
        foreach(var (k,l) in points){var o=l.OrderBy(q=>q.X).ThenBy(q=>q.Y).ToList();layout.Zones[k]=new(o.Sum(q=>q.X)/o.Count,o.Sum(q=>q.Y)/o.Count);}
        layout.Unplaced=DeskLayouts.Arrangeable(devices).Where(a=>!used.Contains(a.Key)).Select(a=>a.Key).Order(StringComparer.Ordinal).ToList();   // by key, so OpenRGB's list order never changes the layout
        return layout;
    }

    // The tap area: the box grown about its centre to at least 28 by 28. Returns the squared distance to the centre, or null when outside.
    private static double? Reach(PicBox b,double x,double y)
    {
        double w=Math.Max(b.W,MinTap),h=Math.Max(b.H,MinTap),dx=x-b.CenterX,dy=y-b.CenterY;
        if(Math.Abs(dx)>w/2||Math.Abs(dy)>h/2) return null;
        return dx*dx+dy*dy;
    }
    public static ResolvedPart? Hit(PictureScene scene,PictureView view,double x,double y)
    {
        ResolvedPart? best=null;double bestD=double.MaxValue;
        foreach(var r in scene.Parts)
        {
            if(!r.Found||r.Part.View!=view) continue;
            var d=Reach(r.Part.Box,x,y);
            if(d is null||d.Value>=bestD) continue;
            best=r;bestD=d.Value;
        }
        return best;
    }
    public static bool HitTower(DeskPicture picture,double x,double y)=>Reach(picture.Tower,x,y) is not null;
}
