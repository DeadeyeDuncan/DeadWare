namespace DeadWare.Core;

// Lighting 5: the Corsair iCUE LINK System Hub is one device under one fixed key, whether OpenRGB lists it (by this name) or the helper
// drives it from the parts it read. Adopt is the one migration: a copy, never a move, of the hub's old serial|location entries (zone
// colours and tuning), so a rollback to 0.22.0 still finds them. The hardware mode and the layout are never copied.
public static class HubIdentity
{
    public const string Key="linkhub",Name="Corsair iCUE Link System Hub",ZonePrefix="iCUE LINK ";
    public const int CoolerType=3;

    public static bool Is(string? deviceName)=>deviceName is not null&&string.Equals(deviceName,Name,StringComparison.Ordinal);

    static bool Mine(string zoneKey)=>zoneKey.StartsWith(Key+"#",StringComparison.Ordinal);

    // The old key first when there is one, then, in ordinal order, every other key whose saved zone colours all carry the hub parts' name prefix.
    static List<string> Candidates(LightingSettings s,string? oldKey)
    {
        var r=new List<string>();
        if(oldKey is not null&&oldKey!=Key) r.Add(oldKey);
        var names=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach(var (k,e) in s.ZoneColors??new())
        {
            if(e is null||!LightingKeys.TrySplitZone(k,out var device,out _)||device==Key) continue;
            if(!names.TryGetValue(device,out var list)) names[device]=list=new();
            list.Add(e.ZoneName);
        }
        foreach(var (device,list) in names.OrderBy(e=>e.Key,StringComparer.Ordinal))
            if(device!=oldKey&&list.All(n=>n is not null&&n.StartsWith(ZonePrefix,StringComparison.Ordinal))) r.Add(device);
        return r;
    }

    // Whether Adopt could copy anything: zone colours or tuning is missing under the key and some old entry exists. Cheap, so a caller can skip its copy.
    public static bool CanAdopt(LightingSettings s,string? oldKey)
    {
        if(s.ZoneColors?.Keys.Any(Mine)!=false&&s.Devices?.ContainsKey(Key)!=false) return false;
        return Candidates(s,oldKey).Count>0;
    }

    // True when anything was copied.
    public static bool Adopt(LightingSettings s,string? oldKey)
    {
        var copied=false;
        var candidates=Candidates(s,oldKey);
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
        return copied;
    }

    // The hub as the helper lists it: one zone per LED-bearing part, in channel order, and Direct only.
    public static LightingDevice Device(IEnumerable<(string Name,int Leds)> zones)
        =>new(Key,Name,CoolerType,zones.Select((z,i)=>new LightingZone(i,z.Name,z.Leds)).ToList(),[LightingPaint.DirectMode],0);
}
