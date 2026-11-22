namespace DeadWare.Core;

// Lighting 4: the SteelSeries Aerox 9 is one device under one fixed key, whether OpenRGB lists it on its cable (its name starts with
// NamePrefix) or the helper lights it on its wireless receiver. Adopt is the one migration: a copy, never a move, of the cable's old
// serial|location entries, so a rollback to 0.21.0 still finds them. Each kind (zone colours, tuning, hardware mode) is copied only while
// the fixed key has none of that kind; the layout is never copied.
public static class AeroxIdentity
{
    public const string Key="aerox9",NamePrefix="SteelSeries Aerox 9",ReceiverName="SteelSeries Aerox 9 Wireless";
    public const int MouseType=6;
    public static readonly string[] Zones=["Front","Middle","Rear","Reactive"];
    const string Reactive="Reactive";

    public static bool Is(string? deviceName)=>deviceName is not null&&deviceName.StartsWith(NamePrefix,StringComparison.Ordinal);

    static bool Mine(string zoneKey)=>zoneKey.StartsWith(Key+"#",StringComparison.Ordinal);

    // Anything saved under the fixed key: a zone colour, a tuning, a hardware mode or a layout entry.
    public static bool HasSaved(LightingSettings s)
    {
        if(s.ZoneColors?.Keys.Any(Mine)==true||s.Devices?.ContainsKey(Key)==true||s.HardwareModes?.ContainsKey(Key)==true) return true;
        var l=s.Layout;
        return l is not null&&(l.Zones.Keys.Any(Mine)||l.Tower.Members.Contains(Key)||l.Tower.Excluded.Contains(Key)||l.Unplaced.Contains(Key));
    }

    // The cable key first when there is one, then, in ordinal order, every other key whose saved zone colours name only the mouse's zones
    // and include Reactive (no other device of the rig has a zone of that name).
    static List<string> Candidates(LightingSettings s,string? cableKey)
    {
        var r=new List<string>();
        if(cableKey is not null&&cableKey!=Key) r.Add(cableKey);
        var names=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach(var (k,e) in s.ZoneColors??new())
        {
            if(e is null||!LightingKeys.TrySplitZone(k,out var device,out _)||device==Key) continue;
            if(!names.TryGetValue(device,out var list)) names[device]=list=new();
            list.Add(e.ZoneName);
        }
        foreach(var (device,list) in names.OrderBy(e=>e.Key,StringComparer.Ordinal))
            if(device!=cableKey&&list.Contains(Reactive)&&list.All(n=>Zones.Contains(n))) r.Add(device);
        return r;
    }

    // True when anything was copied.
    public static bool Adopt(LightingSettings s,string? cableKey)
    {
        var copied=false;
        var candidates=Candidates(s,cableKey);
        if(candidates.Count==0) return false;
        if(s.ZoneColors is not null&&!s.ZoneColors.Keys.Any(Mine))
        {
            foreach(var c in candidates)
            {
                var entries=s.ZoneColors.Where(e=>e.Value is not null&&LightingKeys.TrySplitZone(e.Key,out var d,out _)&&d==c).ToList();
                if(entries.Count==0) continue;
                foreach(var (k,e) in entries)
                {
                    LightingKeys.TrySplitZone(k,out _,out var i);
                    s.ZoneColors[LightingKeys.Zone(Key,i)]=new ZoneColorEntry{Color=e.Color,ZoneName=e.ZoneName};
                }
                copied=true;
                break;
            }
        }
        if(s.Devices is not null&&!s.Devices.ContainsKey(Key)&&candidates.FirstOrDefault(c=>s.Devices.TryGetValue(c,out var t)&&t is not null) is{} from)
        {
            s.Devices[Key]=DeviceTunings.Clone(new(){[from]=s.Devices[from]})[from];
            copied=true;
        }
        if(s.HardwareModes is not null&&!s.HardwareModes.ContainsKey(Key)&&candidates.FirstOrDefault(c=>s.HardwareModes.ContainsKey(c)) is{} mode)
        {
            s.HardwareModes[Key]=s.HardwareModes[mode];
            copied=true;
        }
        return copied;
    }

    // The receiver mouse as the helper lists it: four one-light zones and Direct only.
    public static LightingDevice Receiver()=>new(Key,ReceiverName,MouseType,Zones.Select((n,i)=>new LightingZone(i,n,1)).ToList(),[LightingPaint.DirectMode],0);
}
