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
    // Sets a resizable zone's LED count (OpenRGB's sizes.ors); OpenRGB sends no reply.
    void ResizeZone(int index,int zoneIndex,int size);
    // Drops the connection from any thread, without the host's lock: a call waiting on OpenRGB fails at once. Safe to call again.
    void Abort();
    event Action? DeviceListUpdated;
}

// Thin OpenRGB.NET adapter. OpenRGB.NET 3.1.1 reads all controllers from OpenRGB 1.0rc3 at protocol V4.
// Its requests wait for OpenRGB's reply with no timeout, so the host bounds every call (BoundedSession) and Abort ends one that waits:
// disposing the client cancels the wait and closes the socket, which also ends a blocked send. Each call works on the client it started
// with, so a call abandoned by a timeout never reaches the client of a later connection.
internal sealed class OpenRgbSession : IOpenRgbSession
{
    public const int Port=6744;
    public const string ClientName="DeadWare";
    OpenRgbClient? client;
    public event Action? DeviceListUpdated;
    public bool Connected=>Volatile.Read(ref client)?.Connected==true;
    public void Connect()
    {
        Abort();
        var c=new OpenRgbClient(OpenRgbEngine.Host,Port,ClientName,autoConnect:false);
        c.DeviceListUpdated+=OnUpdated;
        Volatile.Write(ref client,c);
        c.Connect();
    }
    void OnUpdated(object? s,EventArgs e)=>DeviceListUpdated?.Invoke();
    // Gone after an Abort: reported as a disposed connection, so the caller treats it as the session ending, not as one device failing.
    OpenRgbClient Client=>Volatile.Read(ref client)??throw new ObjectDisposedException(nameof(OpenRgbSession),"The OpenRGB session is not connected.");
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
        var c=Client;
        Rgb[]? shaped=null;
        if(colors is {Count:>0}) shaped=ModeColors(colors,c.GetControllerData(index).Modes[modeIndex].Colors.Length);
        c.UpdateMode(index,modeIndex,colors:shaped?.Select(x=>new Color(x.R,x.G,x.B)).ToArray());
    }
    public void ResizeZone(int index,int zoneIndex,int size)=>Client.ResizeZone(index,zoneIndex,size);
    // Exactly one caller takes a client out, so each is disposed once; a second Dispose of OpenRGB.NET's connection would throw.
    public void Abort()
    {
        var c=Interlocked.Exchange(ref client,null);
        if(c is null) return;
        c.DeviceListUpdated-=OnUpdated;
        try{c.Dispose();}catch(Exception){}
    }
    public void Dispose()=>Abort();
}
