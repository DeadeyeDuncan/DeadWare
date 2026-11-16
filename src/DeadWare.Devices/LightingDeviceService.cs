using DeadWare.Core;

namespace DeadWare.Devices;

// Paints every OpenRGB device with the user's static colours. Pacing ported from DeadLight's pushFrame
// (electron/device-service.ts): per-kind frame caps, a 400 ms refresh TTL, and a one-time Direct switch.
internal sealed class LightingDeviceService
{
    public static readonly TimeSpan HidCap=TimeSpan.FromMilliseconds(33),DramCap=TimeSpan.FromMilliseconds(200),RefreshTtl=TimeSpan.FromMilliseconds(400);
    const int DramType=1;
    sealed record Sent(long At,Rgb[] Colors);
    readonly IOpenRgbSession session;
    readonly TimeProvider time;
    readonly HostLog log;
    readonly object gate=new();
    readonly Dictionary<string,int> indexByKey=new();
    readonly Dictionary<string,Sent> lastSent=new();
    readonly HashSet<string> directSet=new();
    readonly Dictionary<string,(string Mode,Rgb[] Colors)> applied=new();
    readonly Dictionary<string,int> activeMode=new();
    readonly Dictionary<string,string> lastError=new();
    IReadOnlyList<LightingDevice> devices=[];
    public IReadOnlyList<LightingDevice> Devices{get{lock(gate)return devices;}}
    public LightingDeviceService(IOpenRgbSession session,TimeProvider time,HostLog log){this.session=session;this.time=time;this.log=log;}

    static string Shape(IEnumerable<(string Key,int Index,IReadOnlyList<LightingZone> Zones)> d)
        =>string.Join(";",d.Select(x=>$"{x.Key}@{x.Index}:"+string.Join(",",x.Zones.Select(z=>$"{z.Index}/{z.Name}/{z.LedCount}"))));

    public bool Rescan()
    {
        var read=session.ReadControllers();
        lock(gate)
        {
            var oldShape=Shape(devices.Select(d=>(d.Key,indexByKey[d.Key],d.Zones)));
            var next=new List<LightingDevice>();
            var idx=new Dictionary<string,int>();
            foreach(var c in read)
            {
                var key=LightingKeys.Device(c.Serial,c.Location);
                if(idx.ContainsKey(key)) continue; // an identical serial+location cannot be told apart
                idx[key]=c.Index;
                next.Add(new(key,c.Name,c.Type,c.Zones,c.Modes,c.ActiveMode));
                activeMode[key]=c.ActiveMode;
            }
            devices=next;
            indexByKey.Clear();
            foreach(var e in idx) indexByKey[e.Key]=e.Value;
            foreach(var k in lastSent.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) lastSent.Remove(k);
            foreach(var k in applied.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) applied.Remove(k);
            foreach(var k in activeMode.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) activeMode.Remove(k);
            foreach(var k in lastError.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) lastError.Remove(k);
            directSet.RemoveWhere(k=>!idx.ContainsKey(k));
            return oldShape!=Shape(next.Select(d=>(d.Key,idx[d.Key],d.Zones)));
        }
    }

    // After an OpenRGB restart or reconnect the fresh process has received nothing: forget what was sent.
    public void Reset()
    {
        lock(gate){lastSent.Clear();applied.Clear();directSet.Clear();activeMode.Clear();}
    }

    public void Paint(LightingSettings settings)
    {
        lock(gate)
        {
            foreach(var d in devices)
            {
                try{PaintDevice(settings,d);lastError.Remove(d.Key);}
                catch(Exception e)
                {
                    lastSent.Remove(d.Key);applied.Remove(d.Key);directSet.Remove(d.Key);
                    var msg=e.GetType().Name+": "+e.Message;
                    if(!lastError.TryGetValue(d.Key,out var prev)||prev!=msg)
                    {
                        lastError[d.Key]=msg;
                        log.Write($"paint failed for {d.Name} ({d.Key}): {msg}");
                    }
                }
            }
        }
    }

    void PaintDevice(LightingSettings settings,LightingDevice d)
    {
        var paint=LightingPaint.Paint(settings,d);
        if(paint.Kind==PaintKind.Unsupported) return;
        var index=indexByKey[d.Key];
        if(paint.Kind==PaintKind.Hardware)
        {
            var mi=d.Modes.ToList().IndexOf(paint.Mode!);
            if(mi<0) return;
            if(applied.TryGetValue(d.Key,out var a)&&a.Mode==paint.Mode&&a.Colors.AsSpan().SequenceEqual(paint.Colors)) return;
            session.UpdateMode(index,mi,paint.Colors.Length>0?paint.Colors:null);
            applied[d.Key]=(paint.Mode!,paint.Colors);
            activeMode[d.Key]=mi;
            directSet.Remove(d.Key);
            lastSent.Remove(d.Key);
            return;
        }
        // Direct: pushFrame.
        applied.Remove(d.Key);
        var now=time.GetTimestamp();
        lastSent.TryGetValue(d.Key,out var prev);
        if(prev is not null)
        {
            var age=time.GetElapsedTime(prev.At,now);
            if(age<(d.Type==DramType?DramCap:HidCap)) return;
            if(age<RefreshTtl&&prev.Colors.AsSpan().SequenceEqual(paint.Colors)) return;
        }
        if(directSet.Add(d.Key))
        {
            var direct=d.Modes.ToList().IndexOf(LightingPaint.DirectMode);
            if(direct>=0&&(!activeMode.TryGetValue(d.Key,out var active)||active!=direct))
            {
                try{session.UpdateMode(index,direct,null);}catch{directSet.Remove(d.Key);throw;}
                activeMode[d.Key]=direct;
            }
        }
        session.UpdateLeds(index,paint.Colors);
        lastSent[d.Key]=new(now,paint.Colors);
    }

    public IReadOnlyList<DeviceView> Views(LightingSettings settings)
    {
        lock(gate)
        {
            return devices.Select(d=>
            {
                var p=LightingPaint.Paint(settings,d);
                var zones=d.Zones.Select(z=>
                {
                    var zk=LightingKeys.Zone(d.Key,z.Index);
                    var own=settings.ZoneColors.TryGetValue(zk,out var e)&&e.ZoneName==z.Name;
                    return new ZoneView(zk,z.Index,z.Name,z.LedCount,LightingPaint.ZoneColor(settings,d,z).ToHex(),own);
                }).ToList();
                return new DeviceView(d.Key,d.Name,d.Type,p.Kind,p.Mode,p.StaysLitWhenOff,zones);
            }).ToList();
        }
    }
}
