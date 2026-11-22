using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4: the Aerox 9 on its receiver as the host paints it, with the sleep gate (the anti-goal). A colour write restarts the mouse's own
// sleep countdown, so while a sleep time other than Never has been chosen and delivered, a frame goes only within MoveWindow of the pointer
// moving, or when it is a must-send (lights changed, an edit, the port (re)opened, the mouse answering again, wake). Only a frame that
// reached the mouse counts as sent, so the next allowed frame carries the current colours. Ruling P1: after any missing reply nothing is
// sent until the pointer moves, then at most one attempt per MoveWindow, so a silent mouse never holds the host's lock for its 250 ms reads
// tick after tick; an exchange that ran into the driver's deadline counts as a missing reply (fix round 1, R17). After a thrown failure
// nothing is sent until the next Scan (the 5 s slow work), whose fresh port is a must-send. A port (re)opened (that Scan, a receiver that
// appears, wake's Reopen) gets one attempt even while silent (R14): the mouse may have woken with the PC; a missing reply then silences again.
// Task 8: it owns the battery poller and gives the mouse card's view; a battery request on the receiver obeys the same silence.
// Called only under the host's gate. The pointer position is compared, never logged.
internal sealed class AeroxMouse
{
    public static readonly TimeSpan MoveWindow=TimeSpan.FromSeconds(1);
    readonly AeroxDriver driver;
    readonly IInputClock input;
    readonly TimeProvider time;
    readonly HostLog log;
    (int X,int Y)? pointer;
    long? movedAt,triedAt;
    Rgb[]? last;                                          // the last frame that reached the mouse
    bool seen,must,silent,stale,holding,heard,receiver,oneTry;

    MouseView? shown;                                     // the view as the last Poll saw it

    public AeroxMouse(AeroxDriver driver,IInputClock input,TimeProvider time,HostLog log){this.driver=driver;this.input=input;this.time=time;this.log=log;Battery=new(driver,time,log);}

    public AeroxBattery Battery{get;}

    public bool ReceiverPresent=>driver.ReceiverPresent;
    public bool CablePresent=>driver.CablePresent;
    public bool Seen=>seen;
    public bool Moved=>movedAt is long at&&time.GetElapsedTime(at)<MoveWindow;

    // Each loop turn: a changed pointer position stamps "moved now"; the first reading only sets the starting point. The mouse answering
    // again (a battery read can be what notices) is a must-send and ends the silence.
    public void Tick()
    {
        var p=input.Pointer();
        if(pointer is{} was&&was!=p) movedAt=time.GetTimestamp();
        pointer=p;
        Hear();
    }
    void Hear()
    {
        var answering=driver.Answering;
        if(answering&&!heard){heard=true;silent=false;Must();}
        else if(!answering) heard=false;
    }

    // Slow work: which products HID lists. A receiver that appears makes the next frame a must-send; after a thrown failure the next frame
    // may go again (its port is reopened, and the failure already made that frame a must-send). Either reopen gets its one attempt.
    public void Scan()
    {
        driver.Scan();
        var now=driver.ReceiverPresent;
        if(now||driver.CablePresent) seen=true;
        if(now&&!receiver) Must();
        if(now&&(!receiver||stale)) oneTry=true;
        if(!now) last=null;
        receiver=now;stale=false;
    }

    public void Must()=>must=true;

    // Task 8, the mouse card: cable if its product is listed; else none without the receiver; else wireless with a fresh reading from a mouse
    // that answers; else receiver. The sleep time is never sent on the cable, so there it is never delivered.
    public MouseView View(int? sleepMinutes)
    {
        var cable=driver.CablePresent;
        var connection=cable?MouseConnections.Cable:!driver.ReceiverPresent?MouseConnections.None
            :Battery.Percent is not null&&!Battery.Stale&&driver.Answering?MouseConnections.Wireless:MouseConnections.Receiver;
        return new(connection,Battery.Percent,Battery.Charging,Battery.Stale,sleepMinutes,!cable&&driver.SleepSent(sleepMinutes));
    }

    // Each loop turn while lighting runs: the battery, then whether the view changed since the last Poll. The receiver is asked only as a
    // frame would be (P1): not after a thrown failure until the next Scan, and while silent only once per MoveWindow, sharing the frame's
    // attempt; a missing reply silences the frames too, and a reply that shows the mouse answering again is a must-send (P6).
    public bool Poll(int? sleepMinutes)
    {
        var before=shown??View(sleepMinutes);
        var receiver=driver.ReceiverPresent&&!stale&&(!silent||triedAt is not long t||time.GetElapsedTime(t)>=MoveWindow);
        Battery.Poll(driver.CablePresent,receiver,Moved);
        switch(Battery.Asked)
        {
            case null:break;
            case AeroxResult.NoReply:
            case AeroxResult.Failed when driver.TimedOut:
                silent=true;heard=false;triedAt=time.GetTimestamp();
                break;
            case AeroxResult.Failed when !driver.Answering:       // thrown: the port is closed until the next Scan, as for a frame
                stale=true;Must();Battery.ReceiverFailed();
                break;
            default:
                if(silent) triedAt=time.GetTimestamp();
                Hear();
                break;
        }
        var now=View(sleepMinutes);
        shown=now;
        return now!=before;
    }

    // Wake: the receiver port is closed now and reopened by the next frame, which is a must-send.
    public void Reopen(){driver.Close();stale=false;oneTry=true;Must();}

    // True when the frame reached the mouse.
    public bool Paint(ReadOnlySpan<Rgb> four,int? sleepMinutes)
    {
        if(stale||!driver.ReceiverPresent) return false;
        var sleepDue=!driver.SleepSent(sleepMinutes);
        var changed=last is null||!last.AsSpan().SequenceEqual(four);
        if(!changed&&!must&&!sleepDue) return false;
        var moved=Moved;
        if(silent&&!oneTry&&(!moved||triedAt is long t&&time.GetElapsedTime(t)<MoveWindow)) return false;
        var gated=sleepMinutes is>0&&!sleepDue;
        if(gated&&!must&&!moved)
        {
            if(!holding){holding=true;log.Write("mouse: holding frames (still)");}
            return false;
        }
        // The release's reason as the hardware check reads it; a frame let through because the sleep time is due or the gate is off names none.
        var reason=must?"must-send":gated&&moved?"moved":null;
        if(silent) triedAt=time.GetTimestamp();
        oneTry=false;                                     // the reopen's attempt is this one, whatever it brings
        switch(driver.Paint(four,sleepMinutes))
        {
            case AeroxResult.Sent:
                last=four.ToArray();must=false;silent=false;heard=true;triedAt=null;oneTry=false;
                if(holding){holding=false;if(reason is not null) log.Write("mouse: released ("+reason+")");}
                return true;
            case AeroxResult.NoReply:
                silent=true;heard=false;triedAt=time.GetTimestamp();
                return false;
            // At the deadline: as a missing reply (R17), so a HID call that hangs and returns can't hold the host's lock every 5 s.
            case AeroxResult.Failed when driver.TimedOut:
                silent=true;heard=false;oneTry=false;triedAt=time.GetTimestamp();Battery.ReceiverFailed();
                return false;
            case AeroxResult.Failed:
                stale=true;Must();Battery.ReceiverFailed();
                return false;
            default:return false;                         // Busy: tried again next tick; Absent: the next Scan says so
        }
    }
}
