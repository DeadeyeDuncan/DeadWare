using System.Diagnostics;
using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4: the SteelSeries Aerox 9 on its 2.4 GHz receiver, ported from DeadLight's electron/aerox-wireless.ts. Offsets are DeadLight's
// (see AeroxTransport.cs). One exchange at a time: a second caller gets Busy at once, never a queue. Each exchange runs on a worker and is
// waited for the deadline; a HID call that never returns cannot be cancelled, so on timeout its port is abandoned (the worker disposes it
// when the call returns, never the caller) and every call while that worker still hangs is Busy. A missing reply is not a failure: a
// sleeping or out-of-reach mouse does not answer, so the port stays open, Answering goes false and the frame stops at the first silent
// write, so a silent mouse costs one 250 ms read per attempt. A write or read that throws closes the port; the next exchange reopens it.
internal sealed class AeroxDriver:IDisposable
{
    public const int Vendor=0x1038,Receiver=0x1858,Cable=0x185A,ReadLimitMs=250,MaxSkipped=16;
    public static readonly TimeSpan Deadline=TimeSpan.FromMilliseconds(1500);
    const int Running=0,Done=1,Expired=2,NoValue=-1;
    readonly IAeroxTransport transport;
    readonly HostLog log;
    readonly TimeSpan deadline;
    readonly object gate=new();
    Task<AeroxResult>? job;
    IAeroxPort? port;                                     // the receiver port; touched by the running worker, or under gate while none runs
    int saved=NoValue;                                    // the sleep time last saved (0x51), once per changed value per driver lifetime
    volatile int sent=NoValue;                            // the sleep time delivered on the current port
    volatile bool receiver,cable,answering,dropRequested,silent,timedOut;

    public AeroxDriver(IAeroxTransport transport,HostLog log,TimeSpan? deadline=null){this.transport=transport;this.log=log;this.deadline=deadline??Deadline;}

    public bool ReceiverPresent=>receiver;
    public bool CablePresent=>cable;
    public bool Answering=>answering;
    // The last exchange ended Failed at the deadline (its port abandoned), not by a throw.
    public bool TimedOut=>timedOut;
    public bool SleepSent(int? minutes)=>minutes is not int m||sent==m;

    // Re-reads which products are present; a vanished receiver closes its port.
    public void Scan()
    {
        IReadOnlyCollection<int> present;
        try{present=transport.Present();}
        catch(Exception e){log.Write("mouse scan failed: "+e.Message);return;}
        cable=present.Contains(Cable);
        var had=receiver;
        receiver=present.Contains(Receiver);
        if(had&&!receiver) Drop();
    }

    // Wake: the next exchange reopens the receiver.
    public void Close()=>Drop();
    public void Dispose()=>Drop();

    public AeroxResult Paint(ReadOnlySpan<Rgb> four,int? sleepMinutes)
    {
        if(four.Length!=4) throw new ArgumentException("the Aerox 9 has four zones",nameof(four));
        if(sleepMinutes is <0 or >279) throw new ArgumentOutOfRangeException(nameof(sleepMinutes));   // 24-bit milliseconds
        if(!receiver) return AeroxResult.Absent;
        var colours=four.ToArray();
        return Run(true,j=>PaintOn(j,colours,sleepMinutes));
    }

    public AeroxResult Battery(bool cable,out int percent,out bool charging)
    {
        percent=0;charging=false;
        if(!(cable?this.cable:receiver)) return AeroxResult.Absent;
        int p=0;bool c=false;
        var r=Run(!cable,j=>BatteryOn(j,cable,out p,out c));
        if(r==AeroxResult.Sent){percent=p;charging=c;}
        return r;
    }

    // Reply byte 1: ((b & 0x7F) - 1) * 5 percent, bit 7 charging.
    public static bool TryBattery(byte[]? reply,byte opcode,out int percent,out bool charging)
    {
        percent=0;charging=false;
        if(reply is null||reply.Length<2||reply[0]!=opcode) return false;
        var p=((reply[1]&0x7F)-1)*5;
        if(p is <0 or >100) return false;
        percent=p;charging=(reply[1]&0x80)!=0;
        return true;
    }

    AeroxResult PaintOn(Job j,Rgb[] four,int? sleepMinutes)
    {
        var p=Port();
        if(sleepMinutes is int m&&sent!=m)
        {
            var ms=m*60000;
            if(Exchange(j,p,[0x69,(byte)(ms&255),(byte)((ms>>8)&255),(byte)((ms>>16)&255)]) is null) return Silent();
            if(saved!=m)
            {
                Exchange(j,p,[0x51,0]);                   // the receiver does not acknowledge the save; its missing reply is accepted
                saved=m;
                log.Write("mouse sleep time saved");
            }
        }
        if(Exchange(j,p,[0x63,10]) is null) return Silent();
        for(var z=0;z<4;z++)
        {
            var c=four[z];
            if(Exchange(j,p,[z==3?(byte)0x66:(byte)0x61,1,z==3?(byte)0:(byte)z,c.R,c.G,c.B]) is null) return Silent();
        }
        Heard();
        if(sleepMinutes is int s&&sent!=s){sent=s;log.Write($"mouse sleep time sent: {s} min");}
        return AeroxResult.Sent;
    }

    AeroxResult BatteryOn(Job j,bool cable,out int percent,out bool charging)
    {
        percent=0;charging=false;
        var opcode=cable?(byte)0x92:(byte)0xD2;
        byte[]? reply;
        if(cable){using var p=transport.Open(Cable);reply=Exchange(j,p,[opcode],opcode);}   // a short-lived port per reading
        else reply=Exchange(j,Port(),[opcode],opcode);
        if(reply is null) return cable?AeroxResult.NoReply:Silent();
        if(!cable) Heard();
        return TryBattery(reply,opcode,out percent,out charging)?AeroxResult.Sent:AeroxResult.Failed;
    }

    IAeroxPort Port()
    {
        if(port is null){port=transport.Open(Receiver);log.Write("receiver opened");}
        return port;
    }

    // One 64-byte write and one read with the 250 ms limit. With an expected opcode, up to MaxSkipped replies that start with another
    // opcode are skipped within that one window, never resending (on the cable, OpenRGB's lighting acknowledgements can arrive first).
    static byte[]? Exchange(Job j,IAeroxPort p,ReadOnlySpan<byte> body,byte? expect=null)
    {
        if(Volatile.Read(ref j.Phase)==Expired) throw new AbandonedException();
        Span<byte> packet=stackalloc byte[64];
        packet.Clear();
        body.CopyTo(packet[1..]);
        p.Write(packet);
        if(Volatile.Read(ref j.Phase)==Expired) throw new AbandonedException();
        var clock=Stopwatch.StartNew();
        var reply=p.Read(ReadLimitMs);
        for(var skipped=0;expect is byte e&&reply is{Length:>0}&&reply[0]!=e&&skipped<MaxSkipped;skipped++)
        {
            var left=ReadLimitMs-(int)clock.ElapsedMilliseconds;
            if(left<=0) break;
            reply=p.Read(left);
        }
        return reply is{Length:>0}?reply:null;
    }

    AeroxResult Silent()
    {
        answering=false;
        if(!silent){silent=true;log.Write("receiver: no reply (asleep or out of reach)");}
        return AeroxResult.NoReply;
    }

    void Heard()
    {
        answering=true;
        if(silent){silent=false;log.Write("receiver: replying again");}
    }

    void ClosePort()
    {
        var p=port;
        port=null;sent=NoValue;
        if(p is null) return;
        try{p.Dispose();}catch{}
        log.Write("receiver closed");
    }

    // Closes the receiver port now when no exchange runs, else at the start of the next one.
    void Drop()
    {
        if(!Monitor.TryEnter(gate)){dropRequested=true;return;}
        try
        {
            if(job is{IsCompleted:false}){dropRequested=true;return;}
            dropRequested=false;
            ClosePort();
        }
        finally{Monitor.Exit(gate);}
    }

    AeroxResult Run(bool onReceiver,Func<Job,AeroxResult> body)
    {
        if(!Monitor.TryEnter(gate)) return AeroxResult.Busy;
        try
        {
            if(job is{IsCompleted:false}) return AeroxResult.Busy;
            if(dropRequested){dropRequested=false;ClosePort();}
            timedOut=false;
            var j=new Job();
            var name=onReceiver?"receiver":"mouse cable";
            var t=Task.Run(()=>
            {
                var r=AeroxResult.Failed;
                try{r=body(j);}
                catch(AbandonedException){}
                catch(Exception e)
                {
                    if(Volatile.Read(ref j.Phase)!=Expired)
                    {
                        log.Write($"{name} failed: {e.Message}");
                        if(onReceiver){answering=false;ClosePort();}
                    }
                }
                finally
                {
                    // The caller gave up on this exchange: its port is never reused, and is disposed here, on the worker.
                    if(Interlocked.Exchange(ref j.Phase,Done)==Expired&&onReceiver) ClosePort();
                }
                return r;
            });
            job=t;
            if(t.Wait(deadline)) return t.Result;
            if(Interlocked.CompareExchange(ref j.Phase,Expired,Running)==Done){t.Wait();return t.Result;}
            if(onReceiver) answering=false;
            timedOut=true;
            log.Write($"{name} failed: no answer within {(int)deadline.TotalMilliseconds} ms; the port is abandoned");
            return AeroxResult.Failed;
        }
        finally{Monitor.Exit(gate);}
    }

    sealed class Job{public int Phase=Running;}
    sealed class AbandonedException:Exception;
}
