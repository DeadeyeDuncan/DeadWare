using System.Diagnostics;
using System.Net.Sockets;
using DeadWare.Core;
using OpenRGB.NET;

namespace DeadWare.Devices;

internal sealed record OpenRgbController(int Index,string Name,int Type,string Serial,string Location,IReadOnlyList<LightingZone> Zones,IReadOnlyList<string> Modes,int ActiveMode,IReadOnlyList<string>? LedNames=null);

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
    // Task 9: asks OpenRGB for one real hardware rescan on a short connection of its own; the session's connection is never touched.
    void RequestHardwareRescan();
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
    // Each controller's mode colour-slot counts from the last ReadControllers (Task 8): UpdateMode shapes colours from them instead of
    // asking OpenRGB for the controller's data again.
    IReadOnlyList<IReadOnlyList<int>> modeSlots=[];
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
    // OpenRGB.NET 3.1.1's client gives its connection the event's handlers when it is constructed, before anyone can subscribe, so this
    // never runs against a real OpenRGB (O21, read in its IL); the host's 5 s rescan finds device-list changes.
    void OnUpdated(object? s,EventArgs e)=>DeviceListUpdated?.Invoke();
    // Gone after an Abort: reported as a disposed connection, so the caller treats it as the session ending, not as one device failing.
    OpenRgbClient Client=>Volatile.Read(ref client)??throw new ObjectDisposedException(nameof(OpenRgbSession),"The OpenRGB session is not connected.");
    public IReadOnlyList<OpenRgbController> ReadControllers()
    {
        var all=Client.GetAllControllerData();
        var list=new List<OpenRgbController>(all.Length);
        var slots=new List<IReadOnlyList<int>>(all.Length);
        for(var i=0;i<all.Length;i++)
        {
            var d=all[i];
            list.Add(new(i,d.Name,(int)d.Type,d.Serial??"",d.Location??"",
                d.Zones.Select((z,zi)=>new LightingZone(zi,z.Name,(int)z.LedCount,MatrixOf(z.MatrixMap))).ToList(),
                d.Modes.Select(m=>m.Name).ToList(),d.ActiveModeIndex,d.Leds.Select(l=>l.Name??"").ToList()));
            slots.Add(d.Modes.Select(m=>m.Colors.Length).ToList());
        }
        Volatile.Write(ref modeSlots,slots);
        return list;
    }
    // OpenRGB.NET 3.1.1's MatrixMap is Height, Width and a uint[Height,Width] indexed [row,column] (read in the IL of MatrixMapReader.ReadFrom);
    // a zone that isn't a matrix has none, and one with a zero side or a cell array that doesn't match gets none here.
    static ZoneMatrix? MatrixOf(MatrixMap? m)=>m is null?null:MatrixOf(m.Matrix,m.Width,m.Height);
    // The flattening on its own, so tests reach it without OpenRGB.NET's MatrixMap, which only its reader builds (whole-branch review 1, D8).
    internal static ZoneMatrix? MatrixOf(uint[,]? cells,uint width,uint height)
    {
        if(cells is null||width==0||height==0||cells.GetLength(0)!=(int)height||cells.GetLength(1)!=(int)width) return null;
        var flat=new uint[cells.Length];
        for(var y=0;y<(int)height;y++) for(var x=0;x<(int)width;x++) flat[y*(int)width+x]=cells[y,x];
        return new((int)width,(int)height,flat);
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
    // A mode's colours shaped to its slot count from the last read. A device or mode index that read doesn't have is refused plainly (the
    // caller treats it as one device's failure). The device service takes its indexes from that same read, so this can't see a change
    // OpenRGB made after it; such a change reaches OpenRGB.NET 3.1.1's UpdateMode, which first fetches the device's data again (read in
    // its IL). That fetch refuses only a negative device index: for one past the end it asks OpenRGB and waits for the reply with no limit,
    // and OpenRGB 1.0rc3 sends none for a device it doesn't have (NetworkServer::SendReply_ControllerData), so BoundedSession's 3 s bound
    // drops the session and the host reconnects. Against the data it fetched, UpdateMode throws for a mode index past the end
    // (ArgumentException above the mode count, IndexOutOfRangeException at it) and for colours, when any are supplied, whose count isn't
    // that mode's slot count (InvalidOperationException, "Incorrect number of colors supplied"). So an index that still fits either lands
    // on whichever device or mode now holds it or, when colours go with it and that mode's slot count isn't the one the last read gave,
    // throws, which fails that one device's paint (LightingDeviceService.Paint).
    public static Rgb[]? ShapeModeColors(IReadOnlyList<IReadOnlyList<int>> slots,int index,int modeIndex,IReadOnlyList<Rgb>? requested)
    {
        if((uint)index>=(uint)slots.Count||(uint)modeIndex>=(uint)slots[index].Count) throw new InvalidOperationException("That device or mode isn't in OpenRGB's last device list.");
        return ModeColors(requested,slots[index][modeIndex]);
    }
    public void UpdateMode(int index,int modeIndex,IReadOnlyList<Rgb>? colors)
    {
        var c=Client;
        var shaped=ShapeModeColors(Volatile.Read(ref modeSlots),index,modeIndex,colors);
        c.UpdateMode(index,modeIndex,colors:shaped?.Select(x=>new Color(x.R,x.G,x.B)).ToArray());
    }
    public void ResizeZone(int index,int zoneIndex,int size)=>Client.ResizeZone(index,zoneIndex,size);
    // Task 9 (amendment 1): OpenRGB 1.0rc3 honours packet 140 (NET_PACKET_ID_REQUEST_RESCAN_DEVICES) with no protocol-version check, and
    // OpenRGB.NET 3.1.1 can't send it, so it goes on a short connection of its own: magic "ORGB", device 0, id 140, no data. The limits sum
    // well under BoundedSession.CallBound (ruling P3), since the host makes the request under its lock.
    internal static readonly byte[] RescanPacket=[0x4F,0x52,0x47,0x42,0,0,0,0,0x8C,0,0,0,0,0,0,0];
    internal static readonly TimeSpan RescanConnectLimit=TimeSpan.FromSeconds(1),RescanDrainLimit=TimeSpan.FromSeconds(1);
    public void RequestHardwareRescan()=>SendRescan(OpenRgbEngine.Host,Port,RescanConnectLimit,RescanDrainLimit);
    // Connects within connectLimit, sends the packet, shuts down sending, then reads and discards until the server closes or drainLimit
    // passes. Any failure goes to the caller; the session's own client is never touched.
    internal static void SendRescan(string host,int port,TimeSpan connectLimit,TimeSpan drainLimit)
    {
        using var c=new TcpClient{NoDelay=true};
        using(var limit=new CancellationTokenSource(connectLimit))
        {
            try{c.ConnectAsync(host,port,limit.Token).AsTask().GetAwaiter().GetResult();}
            catch(OperationCanceledException){throw new TimeoutException($"OpenRGB didn't accept the rescan connection within {connectLimit.TotalSeconds:0.#} s.");}
        }
        var s=c.Client;
        s.Send(RescanPacket);
        s.Shutdown(SocketShutdown.Send);
        var end=Stopwatch.GetTimestamp()+(long)(drainLimit.TotalSeconds*Stopwatch.Frequency);
        var buffer=new byte[256];
        while(true)
        {
            var left=end-Stopwatch.GetTimestamp();
            if(left<=0||!s.Poll((int)Math.Max(1,left*1_000_000/Stopwatch.Frequency),SelectMode.SelectRead)) return;
            if(s.Receive(buffer)==0) return;
        }
    }
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
