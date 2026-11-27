using DeadWare.Core;
namespace DeadWare.Devices;

internal sealed record HubPart(int Channel,byte Type,byte Model,string Name,int Leds,bool Coolant,string Kind);
internal sealed record HubBuild(IReadOnlyList<HubPart> Parts,string? Blocker,string? Detail);

// The parts DeadWare knows (names and LED counts as OpenRGB 1.0rc3 lists them). Any other type and model makes the hub unsupported.
internal static class HubParts
{
    static readonly Dictionary<(byte Type,byte Model),(string Name,int Leds,bool Coolant)> Table=new()
    {
        [(0x0F,0x00)]=("iCUE LINK RX RGB",8,false),
        [(0x01,0x00)]=("iCUE LINK QX RGB",34,false),
        [(0x13,0x00)]=("iCUE LINK RX",0,false),
        [(0x11,0x00)]=("iCUE LINK TITAN 240",20,true),
        [(0x11,0x04)]=("iCUE LINK TITAN 240",20,true),
        [(0x11,0x01)]=("iCUE LINK TITAN 280",20,true),
        [(0x11,0x02)]=("iCUE LINK TITAN 360",20,true),
        [(0x11,0x05)]=("iCUE LINK TITAN 360",20,true),
        [(0x11,0x03)]=("iCUE LINK TITAN 420",20,true),
        [(0x07,0x00)]=("iCUE LINK H100i RGB",20,true),
        [(0x07,0x04)]=("iCUE LINK H100i RGB",20,true),
        [(0x07,0x01)]=("iCUE LINK H115i RGB",20,true),
        [(0x07,0x02)]=("iCUE LINK H150i RGB",20,true),
        [(0x07,0x05)]=("iCUE LINK H150i RGB",20,true),
        [(0x07,0x03)]=("iCUE LINK H170i RGB",20,true),
        [(0x0C,0x00)]=("iCUE LINK XD5 ELITE",22,true),
        [(0x19,0x00)]=("iCUE LINK XD6 ELITE",22,true),
    };

    public static HubBuild Build(HubList list)
    {
        if(list.Overflow) return new HubBuild([],HubReasons.TooManyParts,null);
        var parts=new List<HubPart>();
        foreach(var c in list.Occupied.OrderBy(c=>c.Channel))
        {
            if(!Table.TryGetValue((c.Type,c.Model),out var row)) return new HubBuild([],HubReasons.UnknownPart,HubTexts.UnknownPart(c.Type,c.Model));
            parts.Add(new HubPart(c.Channel,c.Type,c.Model,row.Name,row.Leds,row.Coolant,row.Coolant?HubPartKinds.Pump:HubPartKinds.Fan));
        }
        return new HubBuild(parts,null,null);
    }

    // A channel at or beyond an array's length reads as unavailable. Entry 0, the hub itself, is used nowhere. A speed is the same bits read unsigned.
    public static HubReadings Readings(IReadOnlyList<HubPart> parts,HubValue[] temperatures,HubValue[] speeds)
    {
        var list=new List<HubPartReading>();
        foreach(var p in parts)
        {
            int? rpm=p.Channel>=0&&p.Channel<speeds.Length&&speeds[p.Channel].Available?(ushort)speeds[p.Channel].Raw:null;
            double? celsius=p.Coolant&&p.Channel>=0&&p.Channel<temperatures.Length&&temperatures[p.Channel].Available?temperatures[p.Channel].Raw/10.0:null;
            list.Add(new HubPartReading(p.Channel,p.Kind,p.Name,rpm,celsius));
        }
        return new HubReadings(list);
    }

    // The saved parts by the table, in the saved order; all or nothing (null when there are none or an entry has no row now).
    public static IReadOnlyList<HubPart>? FromSaved(IReadOnlyList<HubSavedPart>? saved)
    {
        if(saved is null||saved.Count==0) return null;
        var parts=new List<HubPart>();
        foreach(var p in saved)
        {
            if(p.Type is<0 or>255||p.Model is<0 or>255||!Table.TryGetValue(((byte)p.Type,(byte)p.Model),out var row)) return null;
            parts.Add(new HubPart(p.Channel,(byte)p.Type,(byte)p.Model,row.Name,row.Leds,row.Coolant,row.Coolant?HubPartKinds.Pump:HubPartKinds.Fan));
        }
        return parts;
    }

    public static List<HubSavedPart> ToSaved(IReadOnlyList<HubPart> parts)=>parts.Select(p=>new HubSavedPart{Channel=p.Channel,Type=p.Type,Model=p.Model}).ToList();

    public static int Leds(IReadOnlyList<HubPart> parts)=>parts.Sum(p=>p.Leds);
}
