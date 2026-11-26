using System.Collections;
using System.Text;
using DeadWare.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DeadWare.Devices.Tests;

// Lighting 5: the shared Corsair lock as the hub worker sees it. Busy (another program holds it) refuses an Enter: a skipped round. Problem
// refuses it too and says why (the lock could not be made). It fails the test on an Enter while a thread of ours holds it: by the holder (the
// Win32 mutex is recursive per thread, so one Exit would leave it held) or by another thread (the worker makes every Enter on its own thread);
// on an Exit while not held; and on an Exit from another thread than the Enter. Each successful Enter starts a new lock unit, numbered by Enters.
// The worker catches every exception inside a step, so a failure here, or one FakeHubTransport reports, also sticks: from then on every recorded
// value (Held, Enters, Exits, and the transport's records) fails the test again when it is read. HubCoreTests' Settled() reads Held, so a
// failure the worker swallowed still fails the test.
sealed class FakeHubLock:IHubLock
{
    readonly object sync=new();
    int enters,exits,owner;
    bool held;
    string? failure;
    volatile string? problem;
    public volatile bool Busy;
    public string? Problem{get=>problem;set=>problem=value;}
    public int Enters{get{lock(sync){Check();return enters;}}}
    public int Exits{get{lock(sync){Check();return exits;}}}
    public bool Held{get{lock(sync){Check();return held;}}}

    public bool Enter(int limitMs)
    {
        lock(sync)
        {
            if(held) Fail(owner==Environment.CurrentManagedThreadId
                ?"the Corsair lock was entered again by the thread that holds it (the mutex is recursive: one Exit would leave it held)"
                :"the Corsair lock was entered by another thread while a thread of ours held it (the worker makes every Enter on its own thread)");
            if(Busy||problem is not null) return false;
            held=true;owner=Environment.CurrentManagedThreadId;enters++;
            return true;
        }
    }

    public void Exit()
    {
        lock(sync)
        {
            if(!held) Fail("the Corsair lock was let go while not held");
            if(owner!=Environment.CurrentManagedThreadId) Fail("the Corsair lock was let go by another thread than the one that entered it");
            held=false;exits++;
        }
    }

    // Null when the calling thread holds the lock, else what `what` (a hub write, a hub read) now breaks. unit: the lock unit it is in.
    internal string? NotHeldHere(string what,out int unit)
    {
        lock(sync)
        {
            unit=enters;
            return !held?what+" outside the Corsair lock":owner!=Environment.CurrentManagedThreadId?what+" from another thread than the one that entered the Corsair lock":null;
        }
    }

    // Fails the test now and keeps the first reason, so the failure outlives code that catches it.
    internal void Fail(string why)
    {
        lock(sync) failure??=why;
        Assert.Fail(why);
    }

    internal void Check()
    {
        string? f;
        lock(sync) f=failure;
        if(f is not null) Assert.Fail("an earlier check of the hub fakes failed: "+f);
    }
}

// Lighting 5 (fix round 1, M5): a channel map a test may change from its own thread while a step runs on another. Inside a FakeHubTransport
// every touch takes the fake's lock; one a test makes (`fake.Parts=new(){[1]=(0x13,0x00)}`) is copied in under it.
sealed class HubChannels<T>:IEnumerable<KeyValuePair<int,T>>
{
    readonly object sync;
    internal readonly Dictionary<int,T> Map=new();     // read only under sync
    public HubChannels():this(new object()){}
    internal HubChannels(object sync){this.sync=sync;}
    public T this[int channel]{get{lock(sync)return Map[channel];}set{lock(sync)Map[channel]=value;}}
    public void Add(int channel,T value){lock(sync)Map.Add(channel,value);}
    public bool Remove(int channel){lock(sync)return Map.Remove(channel);}
    public void Clear(){lock(sync)Map.Clear();}
    public int Count{get{lock(sync)return Map.Count;}}
    public bool ContainsKey(int channel){lock(sync)return Map.ContainsKey(channel);}
    internal void ReplaceWith(HubChannels<T> other)
    {
        var copy=other.ToList();
        lock(sync){Map.Clear();foreach(var (k,v) in copy) Map[k]=v;}
    }
    public IEnumerator<KeyValuePair<int,T>> GetEnumerator(){List<KeyValuePair<int,T>> copy;lock(sync)copy=Map.ToList();return copy.GetEnumerator();}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

// Lighting 5: a small iCUE LINK hub. It answers each written frame as the notes and HubFixtures describe: the firmware; status 00 to a mode
// command, a close or an open; an endpoint read with the endpoint's data, built from Parts, Coolant and Rpm (with the defaults, byte for byte
// HubFixtures.DeviceList, Temperatures and Speeds); a reply to anything but a read or the firmware is 00 00 <opcode> <status> followed by the
// leftovers of the reply before it from offset 4 on, as the hub's are. In its own mode (Software false) an open, a read and a colour chunk
// answer 03; 01 03 00 02 ends its own mode and 01 03 00 01 starts it. A read with no endpoint open, or a colour chunk without the colour
// endpoint open, answers status 01 (the fake's choice: the notes give no reply for either). The hub's mode lives across ports, as the real
// hub's does; an open endpoint, the open colour endpoint and a colour write under way belong to the port that opened them and go when it is
// closed (the worker closes the port on every failure, so the next transaction's opening close is not a split, below). Replies wait in the
// port that asked, and a new port starts with none.
// It guards every test that uses it (Assert.Fail, kept by the lock as above):
//   - a Write or a Read unless the calling thread holds the lock (drain reads included);
//   - a frame unless it is 513 bytes, starts 00 00 01, does not start 06 01, names no endpoint 18 and is one of Task 3's commands: the fixed
//     ones exactly, zero-padded, or 06 00 / 07 00 followed by a chunk;
//   - fix round 1, I1: a transaction split across lock units. A read (08 01) or the close of an open endpoint, and a colour chunk or the close
//     of the open colour endpoint, must come in the lock unit in which that endpoint was opened.
sealed class FakeHubTransport:IHubTransport
{
    static readonly byte[][] Fixed=new[]{"02 13","01 03 00 02","01 03 00 01","05 01 01 36","05 01 01 21","05 01 01 17","05 01 01 22","0D 01 36","0D 01 21","0D 01 17","0D 00 22","08 01"}.Select(HubFixtures.Hex).ToArray();
    static readonly byte[][] Chunked=[[0x06,0x00],[0x07,0x00]];
    // Only Task 3's synthetic ids: the k-th occupied channel takes the k-th, and a fifth part the first again.
    static readonly string[] Ids=[HubFixtures.IdA,HubFixtures.IdB,HubFixtures.IdC,HubFixtures.IdD];
    // Part types with a coolant sensor (the notes' table: TITAN, H-series, XD5, XD6, the V2 pump).
    static readonly byte[] CoolantTypes=[0x11,0x07,0x0C,0x19,0x1A];
    const byte NotOpen=0x01;
    const int ChunkBytes=508;

    readonly FakeTimeProvider time;
    readonly FakeHubLock corsairLock;
    readonly object sync=new();
    readonly List<byte[]> writes=new();
    readonly List<int> limits=new();
    readonly HubChannels<(byte Type,byte Model)> parts;
    readonly HubChannels<int?> rpm;
    int opens,closes,payloadTotal,stale,wrongMode,endpointUnit,coloursUnit;
    bool software=true,coloursOpen;
    byte? openEndpoint;
    Port? endpointPort,coloursPort;
    List<byte>? payload;
    Rgb[]? pending,lastColours;
    byte[]? last;
    double? coolant=43.7;
    TimeSpan latency;
    Exception? throwOnRead;

    public FakeHubTransport(FakeTimeProvider time,FakeHubLock corsairLock)
    {
        this.time=time;this.corsairLock=corsairLock;
        parts=new(sync){[1]=(0x0F,0x00),[2]=(0x0F,0x00),[3]=(0x0F,0x00),[13]=(0x11,0x02)};
        rpm=new(sync){[1]=1660,[2]=1654,[3]=1663,[13]=2774};
    }

    // How many hubs Windows lists; Open opens only when it is 1.
    public volatile int Hubs=1;
    // True: somebody holds the hub (software mode). False: its own mode, until 01 03 00 02 arrives.
    public bool Software{get{lock(sync)return software;}set{lock(sync)software=value;}}
    // Type and model per channel: the owner's three RX RGB fans and the TITAN 360 pump. Assigning copies the given map in.
    public HubChannels<(byte Type,byte Model)> Parts{get=>parts;set=>parts.ReplaceWith(value);}
    // °C at each coolant part; null: unavailable.
    public double? Coolant{get{lock(sync)return coolant;}set{lock(sync)coolant=value;}}
    // RPM per channel, reported only where Parts has a part; null or missing: unavailable. Assigning copies the given map in.
    public HubChannels<int?> Rpm{get=>rpm;set=>rpm.ReplaceWith(value);}
    // Silent: writes are recorded and nothing happens. ThrowOnWrite: each write is recorded, then throws IOException. ThrowOnOpen: Open throws IOException.
    public volatile bool Silent,ThrowOnWrite,ThrowOnOpen;
    // The exception the next read throws (that read only).
    public Exception? ThrowOnRead{get{lock(sync)return throwOnRead;}set{lock(sync)throwOnRead=value;}}
    // While HangRead is set to an unset event, each read waits for it, setting ReadEntered first; a waiting read ends with ObjectDisposedException
    // when its port is disposed. A test that hangs a read must set the event or dispose the port in its cleanup.
    public volatile ManualResetEventSlim? HangRead,ReadEntered;
    // Task 6 fix round 1: while StuckRead is set to an unset event, each read waits for it, setting ReadEntered first, and disposing its port does
    // not end the wait (a call the driver never returns); once the event is set, a read whose port was disposed meanwhile ends with
    // ObjectDisposedException. A test that sets it must set the event in its cleanup.
    public volatile ManualResetEventSlim? StuckRead;
    // Reports another program left in the queue (a speeds reply of 26 entries at 1 RPM), handed out by the coming reads before any reply.
    public int Stale{get{lock(sync)return stale;}set{lock(sync)stale=value;}}
    // How many coming opens (endpoint or colour) answer 03 although the hub is held.
    public int WrongMode{get{lock(sync)return wrongMode;}set{lock(sync)wrongMode=value;}}
    // Fake time each read moves on, drain reads included.
    public TimeSpan Latency{get{lock(sync)return latency;}set{lock(sync)latency=value;}}
    // When set, what a reply becomes: (the command, as in Commands; the fake's reply) -> the reply handed out, or null for none.
    public volatile Func<byte[],byte[],byte[]?>? Reply;

    public int Opens{get{corsairLock.Check();lock(sync)return opens;}}
    public int Closes{get{corsairLock.Check();lock(sync)return closes;}}
    // Each frame's 513 bytes.
    public List<byte[]> Writes{get{corsairLock.Check();lock(sync)return writes.Select(w=>w.ToArray()).ToList();}}
    // Each frame's bytes from 3 up to its last non-zero byte.
    public List<byte[]> Commands{get{corsairLock.Check();lock(sync)return writes.Select(Command).ToList();}}
    // Each read's limit in milliseconds.
    public List<int> Limits{get{corsairLock.Check();lock(sync)return limits.ToList();}}
    // The LEDs of the last finished colour write (its close after the whole payload); null before the first.
    public Rgb[]? LastColours{get{corsairLock.Check();lock(sync)return lastColours?.ToArray();}}
    // Frames whose bytes from 3 start with command (compared before the trailing zeros are cut, so 07 00 counts an all-black chunk).
    public int Count(params byte[] command){corsairLock.Check();lock(sync)return writes.Count(w=>w.AsSpan(3).StartsWith(command));}

    public int Count()=>Hubs;

    public IHubPort Open()
    {
        if(ThrowOnOpen) throw new IOException("the hub could not be opened");
        int hubs=Hubs;
        if(hubs!=1) throw new IOException(hubs==0?"no iCUE LINK hub is connected":"more than one iCUE LINK hub is listed");
        var port=new Port(this);
        lock(sync) opens++;
        return port;
    }

    static byte[] Command(byte[] frame){int end=frame.Length;while(end>3&&frame[end-1]==0) end--;return frame[3..end];}

    // The lock unit of this write, after the lock and frame checks.
    int Guard(byte[] frame)
    {
        var why=corsairLock.NotHeldHere("a hub write",out var unit)??Off(frame);
        if(why is not null) corsairLock.Fail(why);
        return unit;
    }

    static string? Off(byte[] b)
    {
        if(b.Length!=513) return $"a hub frame of {b.Length} bytes, not 513";
        if(b[0]!=0||b[1]!=0||b[2]!=1) return "a hub frame that does not start 00 00 01: "+Convert.ToHexString(b,0,3);
        var rest=b.AsSpan(3);
        if(rest[0]==0x06&&rest[1]==0x01) return "a hub frame that starts 06 01 (a write on handle 1)";
        if(rest[0]==0x05&&rest[3]==0x18||rest[0]==0x0D&&rest[2]==0x18) return "a hub frame that names endpoint 18 (the speed endpoint)";
        foreach(var c in Chunked) if(rest.StartsWith(c)) return null;
        foreach(var c in Fixed) if(rest.StartsWith(c)&&!rest[c.Length..].ContainsAnyExcept((byte)0)) return null;
        return "a hub frame off Task 3's list: "+Convert.ToHexString(Command(b));
    }

    // Under sync. A transaction is held from its first frame to its last: what follows an open must come in the open's lock unit. A close of an
    // endpoint that is not open (the opening close of a transaction) is free.
    string? Split(ReadOnlySpan<byte> c,int unit)
    {
        if(c[0] is 0x06 or 0x07||c[0]==0x05&&c[3]==0x22)
            return coloursOpen&&coloursUnit!=unit?$"a colour {(c[0]==0x05?"close":"chunk")} in lock unit {unit}, but the colour endpoint was opened in unit {coloursUnit}":null;
        if(c[0]==0x08||c[0]==0x05&&openEndpoint==c[3])
            return openEndpoint is byte ep&&endpointUnit!=unit?$"a {(c[0]==0x08?"read":"close")} of endpoint {ep:X2} in lock unit {unit}, but it was opened in unit {endpointUnit}":null;
        return null;
    }

    void Receive(Port port,byte[] frame,int unit)
    {
        lock(sync)
        {
            if(Split(frame.AsSpan(3),unit) is {} why) corsairLock.Fail(why);
            writes.Add(frame);
        }
        if(ThrowOnWrite) throw new IOException("the hub was removed");
        if(Silent) return;
        lock(sync)
        {
            var reply=Answer(port,frame.AsSpan(3),unit);
            last=reply;
            var shown=Reply is {} replace?replace(Command(frame),reply.ToArray()):reply.ToArray();
            if(shown is not null) port.Replies.Enqueue(shown);
        }
    }

    // Under sync.
    byte[] Answer(Port port,ReadOnlySpan<byte> c,int unit)
    {
        byte op=c[0];
        switch(op)
        {
            case 0x02:
                return HubFixtures.Firmware();
            case 0x01:
                software=c[3]==0x02;
                if(!software){openEndpoint=null;coloursOpen=false;payload=null;pending=null;}
                return Ack(op,0);
            case 0x05:
                if(c[3]==0x22)
                {
                    if(coloursOpen&&pending is not null) lastColours=pending;
                    coloursOpen=false;payload=null;pending=null;
                }
                else if(openEndpoint==c[3]) openEndpoint=null;
                return Ack(op,0);
            case 0x0D:
                if(wrongMode>0){wrongMode--;return Ack(op,3);}
                if(!software) return Ack(op,3);
                if(c[1]==0x00){coloursOpen=true;payload=null;pending=null;coloursUnit=unit;coloursPort=port;}
                else{openEndpoint=c[2];endpointUnit=unit;endpointPort=port;}
                return Ack(op,0);
            case 0x08:
                if(!software) return Ack(op,3);
                return openEndpoint switch{0x36=>DeviceList(),0x21=>Temperatures(),0x17=>Speeds(),_=>Ack(op,NotOpen)};
            case 0x06:
            case 0x07:
                if(!software) return Ack(op,3);
                if(!coloursOpen||op==0x07&&payload is null) return Ack(op,NotOpen);
                Chunk(op,c[2..]);
                return Ack(op,0);
            default:
                return Ack(op,NotOpen);                                   // never reached: Guard lets nothing else through
        }
    }

    // Under sync: what the closed port had open goes with it.
    void Forget(Port port)
    {
        if(endpointPort==port){openEndpoint=null;endpointPort=null;}
        if(coloursPort==port){coloursOpen=false;payload=null;pending=null;coloursPort=null;}
    }

    byte[] Ack(byte op,byte status)=>HubFixtures.Ack(op,status,last);

    // The payload [n+2 low, n+2 high, 00, 00, 12, 00, R,G,B...] arrives in chunks of at most 508 bytes: 06 00 starts it, 07 00 goes on.
    void Chunk(byte op,ReadOnlySpan<byte> data)
    {
        if(op==0x06){payload=new List<byte>();payloadTotal=(data[0]|data[1]<<8)+4;pending=null;}
        int take=Math.Min(ChunkBytes,payloadTotal-payload!.Count);
        if(take<=0) return;
        payload.AddRange(data[..take].ToArray());
        if(payload.Count==payloadTotal) pending=Decode(payload);
    }

    static Rgb[]? Decode(List<byte> p)
    {
        int n=p.Count-6;
        if(p[2]!=0||p[3]!=0||p[4]!=0x12||p[5]!=0||n%3!=0) return null;
        var leds=new Rgb[n/3];
        for(int i=0;i<leds.Length;i++) leds[i]=new Rgb(p[6+3*i],p[7+3*i],p[8+3*i]);
        return leds;
    }

    // Under sync. [6] the last channel; from [7] each channel 1 to it: an occupied one as 00 00 <type> <model> 00 00 05 1A and its 26-character
    // id, an empty one as 8 zeros.
    byte[] DeviceList()
    {
        var map=parts.Map;
        int lastChannel=map.Count==0?0:map.Keys.Max();
        var pieces=new List<byte[]>{HubFixtures.Hex("00 00 08 00 21 00"),new[]{(byte)lastChannel}};
        int k=0;
        for(int ch=1;ch<=lastChannel;ch++)
        {
            if(map.TryGetValue(ch,out var p)){pieces.Add([0,0,p.Type,p.Model,0,0,0x05,0x1A]);pieces.Add(Encoding.ASCII.GetBytes(Ids[k++%Ids.Length]));}
            else pieces.Add(new byte[8]);
        }
        return HubFixtures.Reply(512,pieces.ToArray());
    }

    // Under sync. 52 entries: the hub itself at 33.7 °C, then the coolant at each coolant part's channel.
    byte[] Temperatures()
    {
        var r=HubFixtures.Reply(512,HubFixtures.Hex("00 00 08 00 10 00 34"));
        Entry(r,0,337);
        for(int ch=1;ch<52;ch++) Entry(r,ch,parts.Map.TryGetValue(ch,out var p)&&CoolantTypes.Contains(p.Type)&&coolant is double c?(int)Math.Round(c*10):null);
        return r;
    }

    // Under sync. 26 entries: entry 0 unavailable, then each part's speed.
    byte[] Speeds()
    {
        var r=HubFixtures.Reply(512,HubFixtures.Hex("00 00 08 00 25 00 1A"));
        for(int ch=0;ch<26;ch++) Entry(r,ch,parts.Map.ContainsKey(ch)&&rpm.Map.TryGetValue(ch,out var v)?v:null);
        return r;
    }

    // Entry i at 7+3i: 00 and the value little-endian, or 01 00 00 when unavailable.
    static void Entry(byte[] r,int i,int? value)
    {
        int o=7+3*i;
        if(value is int v){r[o]=0;r[o+1]=(byte)v;r[o+2]=(byte)(v>>8);}
        else{r[o]=1;r[o+1]=0;r[o+2]=0;}
    }

    sealed class Port(FakeHubTransport t):IHubPort
    {
        readonly CancellationTokenSource gone=new();
        int disposed;
        public readonly Queue<byte[]> Replies=new();

        public void Write(HubFrame frame)
        {
            var bytes=frame.Bytes.ToArray();
            int unit=t.Guard(bytes);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            t.Receive(this,bytes,unit);
        }

        public byte[]? Read(int limitMs)
        {
            if(t.corsairLock.NotHeldHere("a hub read",out _) is {} why) t.corsairLock.Fail(why);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            lock(t.sync) t.limits.Add(limitMs);
            var hang=t.HangRead;
            if(hang is not null&&!hang.IsSet)
            {
                t.ReadEntered?.Set();
                try{hang.Wait(gone.Token);}
                catch(OperationCanceledException){throw new ObjectDisposedException(nameof(Port),"the port was closed while a read waited");}
            }
            var stuck=t.StuckRead;
            if(stuck is not null&&!stuck.IsSet)
            {
                t.ReadEntered?.Set();
                stuck.Wait();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            }
            var latency=t.Latency;
            if(latency>TimeSpan.Zero) t.time.Advance(latency);
            Exception? thrown;
            lock(t.sync){thrown=t.throwOnRead;t.throwOnRead=null;}
            if(thrown is not null) throw thrown;
            lock(t.sync)
            {
                if(t.stale>0){t.stale--;return HubFixtures.Values(512,26,"25 00");}
                return Replies.Count>0?Replies.Dequeue():null;
            }
        }

        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0) return;
            lock(t.sync){t.closes++;t.Forget(this);}
            gone.Cancel();
        }
    }
}

// Lighting 5: the hub worker as the host and the device service see it. It records each call; Snapshot is settable and Raise() raises Changed.
// HoldLetGo, when set, is the task LetGoAsync returns. AutoLetGo (on by default): an Allow with a reason while the snapshot is driving makes it
// letGo with that reason and raises. Drive sets a driving snapshot of the owner's four parts with readings, SoftwareAt and ReadingsAt now, and
// raises; Round replaces the readings and moves only ReadingsAt (ruling R12), and raises.
sealed class FakeHub:IHub
{
    static readonly IReadOnlyList<HubPart> OwnersFour=
    [
        new(1,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(2,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(3,0x0F,0x00,"iCUE LINK RX RGB",8,false,"fan"),
        new(13,0x11,0x02,"iCUE LINK TITAN 360",20,true,"pump"),
    ];
    readonly object sync=new();
    readonly List<Rgb[]> posts=new();
    readonly List<bool> darks=new();
    readonly List<(string? Reason,string? Detail)> allows=new();
    int quiets,resumes,rechecks,readingRequests,letGos;
    volatile HubSnapshot snapshot=HubSnapshot.Idle;
    public volatile TaskCompletionSource? HoldLetGo;
    public volatile bool AutoLetGo=true;

    public HubSnapshot Snapshot{get=>snapshot;set=>snapshot=value;}
    public event Action? Changed;
    public void Raise()=>Changed?.Invoke();

    public List<Rgb[]> Posts{get{lock(sync)return posts.Select(p=>p.ToArray()).ToList();}}
    public List<bool> Darks{get{lock(sync)return darks.ToList();}}
    public List<(string? Reason,string? Detail)> Allows{get{lock(sync)return allows.ToList();}}
    public int Quiets{get{lock(sync)return quiets;}}
    public int Resumes{get{lock(sync)return resumes;}}
    public int Rechecks{get{lock(sync)return rechecks;}}
    public int ReadingRequests{get{lock(sync)return readingRequests;}}
    public int LetGos{get{lock(sync)return letGos;}}

    public void Post(Rgb[] zones){lock(sync)posts.Add(zones.ToArray());}
    public void SetDark(bool dark){lock(sync)darks.Add(dark);}
    public void Allow(string? forbidden,string? detail=null)
    {
        bool raise=false;
        lock(sync)
        {
            allows.Add((forbidden,detail));
            if(AutoLetGo&&forbidden is not null&&snapshot.State=="driving"){snapshot=snapshot with{State="letGo",Reason=forbidden,Detail=detail,SoftwareAt=0};raise=true;}
        }
        if(raise) Raise();
    }
    public void Quiet(){lock(sync)quiets++;}
    public void Resume(){lock(sync)resumes++;}
    public void Recheck(){lock(sync)rechecks++;}
    public void RequestReading(){lock(sync)readingRequests++;}
    public Task LetGoAsync(){lock(sync)letGos++;return HoldLetGo?.Task??Task.CompletedTask;}

    // fans: the three fans' RPM in channel order (default 1660, 1654, 1663); the pump carries the coolant, 43.7 °C.
    public void Drive(TimeProvider time,int pump=2774,int?[]? fans=null)
    {
        var now=time.GetTimestamp();
        lock(sync) snapshot=snapshot with{State="driving",Reason=null,Detail=null,Parts=OwnersFour,Readings=Readings(pump,fans),ReadingsAt=now,SoftwareAt=now,PumpStopped=false};
        Raise();
    }

    public void Round(TimeProvider time,int pump=2774,int?[]? fans=null)
    {
        var now=time.GetTimestamp();
        lock(sync) snapshot=snapshot with{Readings=Readings(pump,fans),ReadingsAt=now};
        Raise();
    }

    static HubReadings Readings(int pump,int?[]? fans)
    {
        fans??=[1660,1654,1663];
        if(fans.Length!=3) throw new ArgumentException("the owner's hub has three fans",nameof(fans));
        return new([new(1,"fan","iCUE LINK RX RGB",fans[0]),new(2,"fan","iCUE LINK RX RGB",fans[1]),new(3,"fan","iCUE LINK RX RGB",fans[2]),
            new(13,"pump","iCUE LINK TITAN 360",pump,43.7)]);
    }
}
