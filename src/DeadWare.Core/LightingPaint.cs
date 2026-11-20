namespace DeadWare.Core;

public sealed record LightingZone(int Index,string Name,int LedCount);
public sealed record LightingDevice(string Key,string Name,int Type,IReadOnlyList<LightingZone> Zones,IReadOnlyList<string> Modes,int ActiveMode);
public enum PaintKind { Direct, Hardware, Unsupported }
public sealed record DevicePaint(PaintKind Kind,string? Mode,Rgb[] Colors,bool StaysLitWhenOff);

public static class LightingPaint
{
    public const string DirectMode="Direct",StaticMode="Static";
    public static IReadOnlyDictionary<string,string> HardwareModeDefaults{get;}=new Dictionary<string,string>{["Corsair Lighting Node Pro"]="Rainbow Wave"};
    // The device's own hardware effect: a saved choice the device offers, else its default by name. A saved "Direct" is the explicit choice
    // of DeadWare's colours (Task 3), so it means no hardware mode and the device is painted; it is never returned as a hardware mode.
    public static string? HardwareModeFor(LightingSettings settings,LightingDevice device)
    {
        if(settings.HardwareModes.TryGetValue(device.Key,out var m))
        {
            if(m==DirectMode) return null;
            if(device.Modes.Contains(m)) return m;
        }
        return HardwareModeDefaults.TryGetValue(device.Name,out var d)&&device.Modes.Contains(d)?d:null;
    }
    // Unscaled zone colour: the stored entry when its zone name still matches, else the default colour.
    public static Rgb ZoneColor(LightingSettings settings,LightingDevice device,LightingZone zone)
    {
        if(settings.ZoneColors.TryGetValue(LightingKeys.Zone(device.Key,zone.Index),out var e)&&e.ZoneName==zone.Name&&Rgb.TryParse(e.Color,out var c)) return c;
        return Rgb.TryParse(settings.DefaultColor,out var def)?def:new Rgb(0x46,0x82,0xB4);
    }
    // The device as the window shows it (Ruling AB): Kind and Mode from the current paint, and its own effect and lights-off behaviour as
    // capabilities that don't change with Lights on, so its card can say before the lights go off that it will stay lit.
    public static DeviceView View(LightingSettings settings,LightingDevice device)
    {
        var p=Paint(settings,device);
        var own=HardwareModeFor(settings,device);
        var zones=device.Zones.Select(z=>
        {
            var zk=LightingKeys.Zone(device.Key,z.Index);
            var own=settings.ZoneColors.TryGetValue(zk,out var e)&&e.ZoneName==z.Name;
            return new ZoneView(zk,z.Index,z.Name,z.LedCount,ZoneColor(settings,device,z).ToHex(),own);
        }).ToList();
        return new DeviceView(device.Key,device.Name,device.Type,p.Kind,p.Mode,own,own is not null&&!device.Modes.Contains(StaticMode),zones);
    }
    public static DevicePaint Paint(LightingSettings settings,LightingDevice device)
    {
        var mode=HardwareModeFor(settings,device);
        if(mode is not null)
        {
            if(settings.LightsOn) return new(PaintKind.Hardware,mode,[],false);
            return device.Modes.Contains(StaticMode)?new(PaintKind.Hardware,StaticMode,[Rgb.Black],false):new(PaintKind.Hardware,mode,[],true);
        }
        if(!device.Modes.Contains(DirectMode)) return new(PaintKind.Unsupported,null,[],false);
        var colors=new List<Rgb>();
        foreach(var z in device.Zones)
        {
            var c=settings.LightsOn?ZoneColor(settings,device,z).Scale(settings.Brightness):Rgb.Black;
            for(int i=0;i<z.LedCount;i++) colors.Add(c);
        }
        return new(PaintKind.Direct,DirectMode,colors.ToArray(),false);
    }
}
