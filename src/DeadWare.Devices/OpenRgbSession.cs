using DeadWare.Core;
using OpenRGB.NET;

namespace DeadWare.Devices;

internal sealed record OpenRgbController(int Index,string Name,int Type,string Serial,string Location,IReadOnlyList<LightingZone> Zones,IReadOnlyList<string> Modes,int ActiveMode);

// The seam over OpenRGB.NET so the device service can be tested without a live OpenRGB.
internal interface IOpenRgbSession : IDisposable
{
    bool Connected{get;}
    void Connect();
    IReadOnlyList<OpenRgbController> ReadControllers();
    void UpdateLeds(int index,ReadOnlySpan<Rgb> colors);
    void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors);
    event Action? DeviceListUpdated;
}

// Thin OpenRGB.NET adapter. OpenRGB.NET 3.1.1 reads all controllers from OpenRGB 1.0rc3 at protocol V4.
internal sealed class OpenRgbSession : IOpenRgbSession
{
    public const int Port=6744;
    public const string ClientName="DeadWare";
    OpenRgbClient? client;
    public event Action? DeviceListUpdated;
    public bool Connected=>client?.Connected==true;
    public void Connect()
    {
        Detach();
        var c=new OpenRgbClient("127.0.0.1",Port,ClientName,autoConnect:false);
        c.DeviceListUpdated+=OnUpdated;
        client=c;
        c.Connect();
    }
    void OnUpdated(object? s,EventArgs e)=>DeviceListUpdated?.Invoke();
    OpenRgbClient Client=>client??throw new InvalidOperationException("OpenRGB session is not connected");
    public IReadOnlyList<OpenRgbController> ReadControllers()
    {
        var all=Client.GetAllControllerData();
        var list=new List<OpenRgbController>(all.Length);
        for(var i=0;i<all.Length;i++)
        {
            var d=all[i];
            list.Add(new(i,d.Name,(int)d.Type,d.Serial??"",d.Location??"",
                d.Zones.Select((z,zi)=>new LightingZone(zi,z.Name,(int)z.LedCount)).ToList(),
                d.Modes.Select(m=>m.Name).ToList(),d.ActiveModeIndex));
        }
        return list;
    }
    public void UpdateLeds(int index,ReadOnlySpan<Rgb> colors)
    {
        var c=new Color[colors.Length];
        for(var i=0;i<c.Length;i++) c[i]=new Color(colors[i].R,colors[i].G,colors[i].B);
        Client.UpdateLeds(index,c);
    }
    // OpenRGB.NET throws unless a supplied colour list matches the mode's slot count exactly; null keeps the mode's own colours.
    public static Rgb[]? ModeColors(IReadOnlyList<Rgb>? requested,int modeColorCount)
        =>requested is not {Count:>0}||modeColorCount<=0?null:Enumerable.Repeat(requested[0],modeColorCount).ToArray();
    public void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors)
    {
        Rgb[]? shaped=null;
        if(colors is {Count:>0}) shaped=ModeColors(colors,Client.GetControllerData(index).Modes[modeIndex].Colors.Length);
        Client.UpdateMode(index,modeIndex,colors:shaped?.Select(c=>new Color(c.R,c.G,c.B)).ToArray());
    }
    void Detach()
    {
        var c=client;client=null;
        if(c is null) return;
        c.DeviceListUpdated-=OnUpdated;
        c.Dispose();
    }
    public void Dispose()=>Detach();
}
