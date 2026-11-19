using DeadWare.Core;

namespace DeadWare.Devices;

// Paints every OpenRGB device with the user's static colours. Pacing ported from DeadLight's pushFrame
// (electron/device-service.ts): per-kind frame caps, a 400 ms refresh TTL, and a one-time Direct switch.
internal sealed class LightingDeviceService
{
    public static readonly TimeSpan HidCap=TimeSpan.FromMilliseconds(33),DramCap=TimeSpan.FromMilliseconds(200),RefreshTtl=TimeSpan.FromMilliseconds(400);
    const int DramType=1;
    // DeadLight's seedZones (electron/seed-zones.ts), ported exactly: the first device named Corsair Lighting Node Pro whose zone 0 (channel 1,
    // the three LL fans, 3 x 16 LEDs) reports 0 LEDs is resized to 48, so its hardware effect has LEDs to run on. DeadWare's OpenRGB keeps
    // its own settings, where no size was ever saved. Zone 1 (channel 2) is physically empty and is never touched: oversizing it strobed
    // the chain under DeadLight.
    public const string NodePro="Corsair Lighting Node Pro";
    public const int NodeProZone=0,NodeProLeds=48;
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
    // seeded: a resize held on this connection, so channel 1 isn't sized again until OpenRGB restarts or reconnects. seedFailureLogged: the
    // read straight after a resize on this connection didn't show it. That is logged once, and the retries at later rescans add no further
    // line (B9); a later read that shows 48 counts as the resize holding late (polish round 2, item 9).
    bool seeded,seedFailureLogged;
    public IReadOnlyList<LightingDevice> Devices{get{lock(gate)return devices;}}
    public LightingDeviceService(IOpenRgbSession session,TimeProvider time,HostLog log){this.session=session;this.time=time;this.log=log;}

    static string Shape(IEnumerable<(string Key,int Index,IReadOnlyList<LightingZone> Zones)> d)
        =>string.Join(";",d.Select(x=>$"{x.Key}@{x.Index}:"+string.Join(",",x.Zones.Select(z=>$"{z.Index}/{z.Name}/{z.LedCount}"))));

    public bool Rescan()
    {
        var read=session.ReadControllers();
        if(!Volatile.Read(ref seeded)&&read.FirstOrDefault(c=>c.Name==NodePro) is {} node&&node.Zones.FirstOrDefault(z=>z.Index==NodeProZone) is {} zone)
        {
            var key=LightingKeys.Device(node.Serial,node.Location);
            if(zone.LedCount==0)
            {
                session.ResizeZone(node.Index,NodeProZone,NodeProLeds);
                read=session.ReadControllers();
                // B9: OpenRGB sends no reply to a resize and ignores one outside the zone's limits, so only a read says whether it held. One
                // that this read doesn't show is logged once and tried again at the next rescan, as DeadLight re-runs its seed while the zone
                // reads 0.
                var now=read.FirstOrDefault(c=>LightingKeys.Device(c.Serial,c.Location)==key)?.Zones.FirstOrDefault(z=>z.Index==NodeProZone)?.LedCount;
                if(now==NodeProLeds) Sized(node,zone,key);
                else
                {
                    if(!seedFailureLogged)
                    {
                        seedFailureLogged=true;
                        log.Write($"sizing {node.Name} {zone.Name} to {NodeProLeds} LEDs didn't hold: OpenRGB reports {(now is {} leds?leds+" LEDs":"no such zone")}; trying again at each rescan");
                    }
                    // Its hardware effect was set up for the old size: once the size changed, forget what was sent, so the next paint sends it again.
                    if(now!=zone.LedCount) Forget(key);
                }
            }
            // Polish round 2, item 9: the read straight after a resize didn't show it, and this rescan's read shows 48, so it held late.
            else if(seedFailureLogged&&zone.LedCount==NodeProLeds) Sized(node,zone,key);
        }
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

    // Channel 1 now has its 48 LEDs: it isn't sized again on this connection, and the hardware effect, set up for the old size, is sent again
    // at the next paint.
    void Sized(OpenRgbController node,LightingZone zone,string key)
    {
        Volatile.Write(ref seeded,true);
        log.Write($"sized {node.Name} {zone.Name} to {NodeProLeds} LEDs");
        Forget(key);
    }
    void Forget(string key){lock(gate){applied.Remove(key);directSet.Remove(key);lastSent.Remove(key);}}

    // After an OpenRGB restart or reconnect the fresh process has received nothing: forget what was sent.
    public void Reset()
    {
        lock(gate){lastSent.Clear();applied.Clear();directSet.Clear();activeMode.Clear();seeded=false;seedFailureLogged=false;}
    }

    public void Paint(LightingSettings settings)
    {
        lock(gate)
        {
            foreach(var d in devices)
            {
                try{PaintDevice(settings,d);lastError.Remove(d.Key);}
                // The connection is gone, not this device: the caller drops the session and every device is repainted after it reconnects.
                catch(OpenRgbSessionLostException){throw;}
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
        lock(gate)return devices.Select(d=>LightingPaint.View(settings,d)).ToList();
    }
}
