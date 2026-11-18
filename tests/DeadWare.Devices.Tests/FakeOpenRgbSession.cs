using System.Text.Json;
using DeadWare.Core;
using DeadWare.Devices;

namespace DeadWare.Devices.Tests;

sealed class FakeOpenRgbSession:IOpenRgbSession
{
    // The host writes these under its own lock and tests read them from their own threads, so every touch goes through this one: a read
    // never sees a list mid-Add (a torn read once returned a default tuple whose null Colors threw outside the tests' catch).
    readonly object sync=new();
    readonly List<(int Index,Rgb[] Colors)> leds=new();
    readonly List<(int Index,int Mode,Rgb[]? Colors)> modes=new();
    TaskCompletionSource? hang;
    int hanging,aborts;
    public List<OpenRgbController> Controllers=new();
    public Func<int,bool>? FailLeds;
    public bool Connected{get;private set;}
    public int Connects,Reads;
    public event Action? DeviceListUpdated;
    public bool FailConnect;
    // Calls named here (Connect, ReadControllers, UpdateLeds, UpdateMode) hang as OpenRGB.NET's do when OpenRGB never answers: only Abort,
    // which is what disposing the real client does, releases them, by failing them.
    public volatile Func<string,bool>? HangWhen;
    public IReadOnlyList<(int Index,Rgb[] Colors)> Leds{get{lock(sync)return leds.ToList();}}
    public IReadOnlyList<(int Index,int Mode,Rgb[]? Colors)> Modes{get{lock(sync)return modes.ToList();}}
    public int LedCount{get{lock(sync)return leds.Count;}}
    public void ClearLeds(){lock(sync)leds.Clear();}
    public bool Hanging=>Volatile.Read(ref hanging)>0;
    public int Aborts=>Volatile.Read(ref aborts);
    public void Connect(){Hang("Connect");Connects++;if(FailConnect) throw new IOException("fake connect failure");Connected=true;}
    public void RaiseUpdated()=>DeviceListUpdated?.Invoke();
    public IReadOnlyList<OpenRgbController> ReadControllers(){Hang("ReadControllers");Reads++;return Controllers.ToList();}
    public void UpdateLeds(int index,ReadOnlySpan<Rgb> colors)
    {
        var copy=colors.ToArray();
        Hang("UpdateLeds");
        if(FailLeds?.Invoke(index)==true) throw new InvalidOperationException("fake leds failure"); // a device refusing a frame, not a broken connection
        lock(sync)leds.Add((index,copy));
    }
    public void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors){Hang("UpdateMode");lock(sync)modes.Add((index,modeIndex,colors?.ToArray()));}
    public void Abort()
    {
        Interlocked.Increment(ref aborts);
        TaskCompletionSource? waiting;
        lock(sync){waiting=hang;hang=null;Connected=false;}
        waiting?.TrySetResult();
    }
    public void Dispose()=>Abort();

    void Hang(string call)
    {
        if(HangWhen?.Invoke(call)!=true) return;
        TaskCompletionSource waiting;
        lock(sync)waiting=hang??=new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref hanging);
        try
        {
            // A test that forgets to release it fails instead of hanging the run.
            if(!waiting.Task.Wait(Waits.RealTimeCap*2)) throw new TimeoutException($"fake {call} was never released");
            throw new OperationCanceledException($"fake {call} aborted");
        }
        finally{Interlocked.Decrement(ref hanging);}
    }

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
