using System.Text.Json;
using DeadWare.Core;
using DeadWare.Devices;

namespace DeadWare.Devices.Tests;

sealed class FakeOpenRgbSession:IOpenRgbSession
{
    public List<OpenRgbController> Controllers=new();
    public readonly List<(int Index,Rgb[] Colors)> Leds=new();
    public readonly List<(int Index,int Mode,Rgb[]? Colors)> Modes=new();
    public Func<int,bool>? FailLeds;
    public bool Connected{get;private set;}
    public int Connects,Reads;
    public event Action? DeviceListUpdated;
    public void Connect(){Connects++;Connected=true;}
    public void RaiseUpdated()=>DeviceListUpdated?.Invoke();
    public IReadOnlyList<OpenRgbController> ReadControllers(){Reads++;return Controllers.ToList();}
    public void UpdateLeds(int index,ReadOnlySpan<Rgb> colors)
    {
        if(FailLeds?.Invoke(index)==true) throw new IOException("fake leds failure");
        Leds.Add((index,colors.ToArray()));
    }
    public void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors)=>Modes.Add((index,modeIndex,colors?.ToArray()));
    public void Dispose(){}

    sealed record Zone(string Name,int LedCount);
    sealed record Dev(string Name,string Serial,string Location,int Type,Zone[] Zones,string[] Modes,int ActiveMode);
    public static FakeOpenRgbSession FromFixture()
    {
        var json=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","controllers.json"));
        var devs=JsonSerializer.Deserialize<Dev[]>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        return new(){Controllers=devs.Select((d,i)=>new OpenRgbController(i,d.Name,d.Type,d.Serial,d.Location,
            d.Zones.Select((z,zi)=>new LightingZone(zi,z.Name,z.LedCount)).ToList(),d.Modes,d.ActiveMode)).ToList()};
    }
    // Re-index in list order, as OpenRGB does after a device list change.
    public void Reorder(Func<List<OpenRgbController>,List<OpenRgbController>> f)
        =>Controllers=f(Controllers).Select((c,i)=>c with{Index=i}).ToList();
}
