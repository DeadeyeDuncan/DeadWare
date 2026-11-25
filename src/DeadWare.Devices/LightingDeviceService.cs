using DeadWare.Core;

namespace DeadWare.Devices;

// Paints every OpenRGB device with the user's static colours. Pacing ported from DeadLight's pushFrame
// (electron/device-service.ts): per-kind frame caps, a 400 ms refresh TTL, and a one-time Direct switch.
internal sealed class LightingDeviceService
{
    public static readonly TimeSpan HidCap=TimeSpan.FromMilliseconds(33),DramCap=TimeSpan.FromMilliseconds(200),RefreshTtl=TimeSpan.FromMilliseconds(400);
    const int DramType=1;
    // DeadLight's seedZones (electron/seed-zones.ts), ported with its rule, though not how often it runs (see Rescan): the first device
    // named Corsair Lighting Node Pro whose zone 0 (channel 1, the three LL fans, 3 x 16 LEDs) reports 0 LEDs is resized to 48, so its
    // hardware effect has LEDs to run on. DeadWare's OpenRGB keeps its own settings, where no size was ever saved. Zone 1 (channel 2) is physically empty and is never touched: oversizing it strobed
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
    // modeProblem: why a device's saved mode can't run, as last logged, so it is logged once per change, not per frame (step 3a).
    readonly Dictionary<string,string> modeProblem=new();
    // skipped: how many painted or layered keys of a device no longer match its lights, as last logged (lighting 3b), so it is logged once
    // per change of the count, not per frame.
    readonly Dictionary<string,int> skipped=new();
    IReadOnlyList<LightingDevice> devices=[];
    // seeded: a resize held on this connection, so channel 1 isn't sized again until OpenRGB restarts or reconnects. seedFailureLogged: the
    // read straight after a resize on this connection didn't show it. That is logged once, and the retries at later rescans add no further
    // line (B9); a later read that shows 48 counts as the resize holding late (polish round 2, item 9).
    bool seeded,seedFailureLogged;
    public IReadOnlyList<LightingDevice> Devices{get{lock(gate)return devices;}}
    // Lighting 4, one mouse two ways in: the Aerox 9 is listed under AeroxIdentity.Key. On its cable (HID lists the cable product and OpenRGB
    // its device) OpenRGB lights it (CableKey is its own serial|location key); with no such cable device and the receiver present the helper lists AeroxIdentity.Receiver() and
    // paints it through mouse, never through an OpenRGB index; else, while it was seen in this run or anything is saved under the key
    // (MouseSaved, set by the host before each Rescan), it is listed absent: never painted and without preview colours.
    readonly AeroxMouse? mouse;
    enum MouseRoute{None,Cable,Receiver,Absent}
    MouseRoute route;
    public bool MouseSaved{get;set;}
    public string? CableKey{get;private set;}
    public bool MouseAbsent=>route==MouseRoute.Absent;
    public LightingDeviceService(IOpenRgbSession session,TimeProvider time,HostLog log,AeroxMouse? mouse=null){this.session=session;this.time=time;this.log=log;this.mouse=mouse;}

    // Names the light count and each zone's matrix size too, so a key map arriving or changing size raises a state; and (lighting 3b) a hash
    // of the light names in order, so lights that come back renamed or reordered raise one too, and the skipped counts follow.
    // A device with no OpenRGB index (the receiver or absent mouse) is named at index -1, and the mouse's route is named first.
    static string Shape(IEnumerable<(string Key,int Index,IReadOnlyList<LightingZone> Zones,IReadOnlyList<string>? Names)> d,MouseRoute route)
        =>"mouse:"+route+";"+string.Join(";",d.Select(x=>$"{x.Key}@{x.Index}/{x.Names?.Count??-1}/{NamesHash(x.Names):X16}:"+string.Join(",",x.Zones.Select(z=>$"{z.Index}/{z.Name}/{z.LedCount}/{(z.Matrix is {} m?$"{m.Width}x{m.Height}":"-")}"))));
    // FNV-1a over each name's characters, with a step between names that no character gives, so ["ab","c"] and ["a","bc"] differ.
    static ulong NamesHash(IReadOnlyList<string>? names)
    {
        if(names is null) return 0;
        var h=14695981039346656037UL;
        foreach(var n in names)
        {
            foreach(var c in n??"") h=(h^c)*1099511628211UL;
            h=(h^0x10000)*1099511628211UL;
        }
        return h;
    }

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
                // that this read doesn't show is logged once. It is tried again only at a rescan where channel 1 reads 0 LEDs (F3 item 1), the
                // rule of DeadLight's seed. DeadLight runs that seed on every pass of its paint loop (onDevices in electron/main.ts at 0497cd3):
                // each tick, 500 ms or 33 ms while an effect runs, and after each rescan, for as long as it runs, not only when its engine starts,
                // and it never stops after a sizing holds. DeadWare checks at each rescan, every 5 s, and stops on a connection once one holds.
                // So after a read that still shows 0, or no channel 1, each rescan that reads 0 tries again; after one that shows another size,
                // nothing is sent while channel 1 keeps it. The line says which.
                var now=read.FirstOrDefault(c=>LightingKeys.Device(c.Serial,c.Location)==key)?.Zones.FirstOrDefault(z=>z.Index==NodeProZone)?.LedCount;
                if(now==NodeProLeds) Sized(node,zone,key);
                else
                {
                    if(!seedFailureLogged)
                    {
                        seedFailureLogged=true;
                        var retry=now is >0?"not trying again unless it reads 0 LEDs":"trying again at each rescan where it reads 0 LEDs";
                        log.Write($"sizing {node.Name} {zone.Name} to {NodeProLeds} LEDs didn't hold: OpenRGB reports {(now is {} leds?leds+" LEDs":"no such zone")}; {retry}");
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
            var oldShape=Shape(devices.Select(d=>(d.Key,indexByKey.TryGetValue(d.Key,out var i)?i:-1,d.Zones,d.LedNames)),route);
            var next=new List<LightingDevice>();
            var idx=new Dictionary<string,int>();
            string? cable=null;
            foreach(var c in read)
            {
                var own=LightingKeys.Device(c.Serial,c.Location);
                var aerox=AeroxIdentity.Is(c.Name);
                // Fix wave 1, F1 (spec: presence is what HID lists): OpenRGB keeps listing the cable device after the cable leaves, so with
                // the receiver driver that device counts only while HID lists the cable product; else it is neither listed nor painted.
                if(aerox&&mouse is not null&&!mouse.CablePresent) continue;
                var key=aerox?AeroxIdentity.Key:own;
                if(idx.ContainsKey(key)) continue; // an identical serial+location cannot be told apart; a second Aerox cable device neither
                if(aerox) cable=own;
                idx[key]=c.Index;
                next.Add(new(key,c.Name,c.Type,c.Zones,c.Modes,c.ActiveMode,c.LedNames));
                activeMode[key]=c.ActiveMode;
            }
            var was=route;
            route=cable is not null?MouseRoute.Cable
                :mouse?.ReceiverPresent==true?MouseRoute.Receiver
                :mouse is not null&&(mouse.Seen||MouseSaved)?MouseRoute.Absent:MouseRoute.None;
            if(route is MouseRoute.Receiver or MouseRoute.Absent) next.Add(AeroxIdentity.Receiver());
            if(route==MouseRoute.Receiver&&was!=MouseRoute.Receiver) mouse!.Must();
            // Another cable device (a new port gives a new location) is another OpenRGB controller: what was sent to the old one is forgotten.
            if(cable!=CableKey&&cable is not null) Forget(AeroxIdentity.Key);
            CableKey=cable;
            devices=next;
            indexByKey.Clear();
            foreach(var e in idx) indexByKey[e.Key]=e.Value;
            foreach(var k in lastSent.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) lastSent.Remove(k);
            foreach(var k in applied.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) applied.Remove(k);
            foreach(var k in activeMode.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) activeMode.Remove(k);
            foreach(var k in lastError.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) lastError.Remove(k);
            foreach(var k in modeProblem.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) modeProblem.Remove(k);
            directSet.RemoveWhere(k=>!idx.ContainsKey(k));
            foreach(var k in skipped.Keys.Where(k=>!idx.ContainsKey(k)).ToList()) skipped.Remove(k);
            return oldShape!=Shape(next.Select(d=>(d.Key,idx.TryGetValue(d.Key,out var i)?i:-1,d.Zones,d.LedNames)),route);
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
    void Forget(string key){lock(gate){applied.Remove(key);directSet.Remove(key);lastSent.Remove(key);modeProblem.Remove(key);skipped.Remove(key);}}

    // After an OpenRGB restart or reconnect the fresh process has received nothing: forget what was sent.
    public void Reset()
    {
        lock(gate){lastSent.Clear();applied.Clear();directSet.Clear();activeMode.Clear();modeProblem.Clear();skipped.Clear();seeded=false;seedFailureLogged=false;}
    }

    // presses (lighting 3b): live key presses for fade and ripple layers, stamped on the same clock as tMs.
    public void Paint(LightingSettings settings,double tMs=0,IReadOnlyList<LightPress>? presses=null)
    {
        lock(gate)
        {
            foreach(var d in devices)
            {
                try{PaintDevice(settings,d,tMs,presses);lastError.Remove(d.Key);}
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

    void PaintDevice(LightingSettings settings,LightingDevice d,double tMs,IReadOnlyList<LightPress>? presses)
    {
        var paint=LightingPaint.Paint(settings,d,tMs,presses);
        if(paint.Kind==PaintKind.Unsupported) return;
        if(!indexByKey.TryGetValue(d.Key,out var index)){PaintMouse(settings,d,paint);return;}
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
        NoteModeProblem(settings,d);
        NoteSkipped(settings,d);
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

    // The receiver mouse (amendment 11): a frame only when its colours change or one is a must-send, through AeroxMouse's sleep gate;
    // never the 400 ms refresh. An absent mouse is never painted.
    void PaintMouse(LightingSettings settings,LightingDevice d,DevicePaint paint)
    {
        if(d.Key!=AeroxIdentity.Key||route!=MouseRoute.Receiver||mouse is null||paint.Kind!=PaintKind.Direct||paint.Colors.Length!=4) return;
        NoteModeProblem(settings,d);
        mouse.Paint(paint.Colors,settings.Mouse.SleepMinutes);
    }
    bool Unlit(LightingDevice d)=>route==MouseRoute.Absent&&d.Key==AeroxIdentity.Key;
    // Fix wave 1, F3 (spec line 232): whether an edit changes the receiver mouse's frame at tMs (the effective lights, its colours, tuning or
    // mode), which makes its next frame a must-send. An edit of other devices, looks, the layout or the everyday rules leaves it as it was.
    public bool MouseFrameDiffers(LightingSettings before,LightingSettings after,double tMs)
    {
        lock(gate)
        {
            if(route!=MouseRoute.Receiver||devices.FirstOrDefault(d=>d.Key==AeroxIdentity.Key) is not{} d) return false;
            var a=LightingPaint.Paint(before,d,tMs);var b=LightingPaint.Paint(after,d,tMs);
            return a.Kind!=b.Kind||a.Mode!=b.Mode||!a.Colors.AsSpan().SequenceEqual(b.Colors);
        }
    }

    // A saved mode the device can't run is painted as Static (LightingPaint.Lights); the reason is logged when it first appears or changes,
    // and forgotten once the mode runs, so it is logged again if it stops being able to.
    void NoteModeProblem(LightingSettings settings,LightingDevice d)
    {
        var mode=DeviceTunings.Of(settings,d.Key)?.Mode;
        if(mode is null||DeviceModes.CantRun(d,mode.Kind) is not {} why){modeProblem.Remove(d.Key);return;}
        if(modeProblem.TryGetValue(d.Key,out var prev)&&prev==why) return;
        modeProblem[d.Key]=why;
        log.Write($"saved lighting mode {mode.Kind} can't run on {d.Name} ({d.Key}), so it is painted as Static: {why}");
    }

    // Lighting 3b's name guard: painted keys and layer lights whose name no longer matches the light at their index are skipped by the paint
    // stage; the count is logged when it first appears or changes, and forgotten at none, so it is logged again if it comes back.
    void NoteSkipped(LightingSettings settings,LightingDevice d)
    {
        var t=DeviceTunings.Of(settings,d.Key);
        var n=t is null?0:LightingLayers.SkippedKeys(d,t)+(t.Layers is{Count:>0}?LightingLayers.SkippedLayerLights(d,t).Sum():0);
        if(n==0){skipped.Remove(d.Key);return;}
        if(skipped.TryGetValue(d.Key,out var prev)&&prev==n) return;
        skipped[d.Key]=n;
        log.Write($"{d.Name}: {n} painted or layered keys don't match this keyboard's keys any more and are skipped.");
    }

    // Lighting 4, Task 8: the Aerox 9's view carries the mouse card's facts, whichever way it is listed (none without a mouse).
    public IReadOnlyList<DeviceView> Views(LightingSettings settings)
    {
        lock(gate)return devices.Select(d=>d.Key==AeroxIdentity.Key&&mouse is not null?LightingPaint.View(settings,d) with{Mouse=mouse.View(settings.Mouse.SleepMinutes)}:LightingPaint.View(settings,d)).ToList();
    }

    // Zone key -> #RRGGBB at tMs before tuning and brightness, for Direct devices and zones with LEDs: the colour of the zone's first light in
    // the device's base frame (its keyboard mode, else the desk effect or static colour), so the picture shows a mode on a device with no key
    // map too. Without a mode the base frame is each zone's ZoneShown colour, as before step 3a.
    public Dictionary<string,string> Preview(LightingSettings settings,double tMs,IReadOnlyList<LightPress>? presses=null)
    {
        var r=new Dictionary<string,string>();
        lock(gate)foreach(var d in devices)
        {
            if(Unlit(d)||LightingPaint.Paint(settings,d,tMs,presses).Kind!=PaintKind.Direct) continue;
            var lights=LightingPaint.Lights(settings,d,tMs);
            var first=0;
            foreach(var z in d.Zones)
            {
                if(z.LedCount>0) r[LightingKeys.Zone(d.Key,z.Index)]=(first<lights.Length?lights[first]:LightingPaint.ZoneShown(settings,d,z,tMs)).ToHex();
                first+=z.LedCount;
            }
        }
        return r;
    }

    // Device key -> its lights' colours at tMs before tuning and brightness, packed as upper-case RRGGBB per light, for each Direct device
    // with a key map; null when there is none. Lighting 3b: the paint stage's colours (LightingPaint.Shown), so painted keys and layers show.
    // Key presses show here (the key-press rule, point 4's one exception): these frames go only to a window subscribed to them.
    public Dictionary<string,string>? PreviewLights(LightingSettings settings,double tMs,IReadOnlyList<LightPress>? presses=null)
    {
        Dictionary<string,string>? r=null;
        lock(gate)foreach(var d in devices)
        {
            if(Unlit(d)||KeyMaps.Of(d) is null||LightingPaint.Paint(settings,d,tMs,presses).Kind!=PaintKind.Direct) continue;
            var lights=LightingPaint.Shown(settings,d,tMs,presses);
            var bytes=new byte[lights.Length*3];
            for(var i=0;i<lights.Length;i++){bytes[i*3]=lights[i].R;bytes[i*3+1]=lights[i].G;bytes[i*3+2]=lights[i].B;}
            (r??=new())[d.Key]=Convert.ToHexString(bytes);
        }
        return r;
    }
}
