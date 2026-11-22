using DeadWare.Core;

namespace DeadWare.Devices;

// Lighting 4: the Aerox 9 on its receiver as the host paints it, with the sleep gate (the anti-goal). A colour write restarts the mouse's own
// sleep countdown, so while a sleep time other than Never has been chosen and delivered, a frame goes only within MoveWindow of the pointer
// moving, or when it is a must-send (lights changed, an edit, the port (re)opened, the mouse answering again, wake). Only a frame that
// reached the mouse counts as sent, so the next allowed frame carries the current colours. Ruling P1: after any missing reply nothing is
// sent until the pointer moves, then at most one attempt per MoveWindow, so a silent mouse never holds the host's lock for its 250 ms reads
// tick after tick. After a real failure nothing is sent until the next Scan (the 5 s slow work), whose fresh port is a must-send.
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
    bool seen,must,silent,stale,holding,heard,receiver;

    public AeroxMouse(AeroxDriver driver,IInputClock input,TimeProvider time,HostLog log){this.driver=driver;this.input=input;this.time=time;this.log=log;}

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
        var answering=driver.Answering;
        if(answering&&!heard){heard=true;silent=false;Must();}
        else if(!answering) heard=false;
    }

    // Slow work: which products HID lists. A receiver that appears makes the next frame a must-send; after a failure the next frame may go
    // again (its port is reopened, and the failure already made that frame a must-send).
    public void Scan()
    {
        driver.Scan();
        var now=driver.ReceiverPresent;
        if(now||driver.CablePresent) seen=true;
        if(now&&!receiver) Must();
        if(!now) last=null;
        receiver=now;stale=false;
    }

    public void Must()=>must=true;

    // Wake: the receiver port is closed now and reopened by the next frame, which is a must-send.
    public void Reopen(){driver.Close();stale=false;Must();}

    // True when the frame reached the mouse.
    public bool Paint(ReadOnlySpan<Rgb> four,int? sleepMinutes)
    {
        if(stale||!driver.ReceiverPresent) return false;
        var sleepDue=!driver.SleepSent(sleepMinutes);
        var changed=last is null||!last.AsSpan().SequenceEqual(four);
        if(!changed&&!must&&!sleepDue) return false;
        var moved=Moved;
        if(silent&&(!moved||triedAt is long t&&time.GetElapsedTime(t)<MoveWindow)) return false;
        var gated=sleepMinutes is>0&&!sleepDue;
        if(gated&&!must&&!moved)
        {
            if(!holding){holding=true;log.Write("mouse: holding frames (still)");}
            return false;
        }
        var wasMust=must;
        if(silent) triedAt=time.GetTimestamp();
        switch(driver.Paint(four,sleepMinutes))
        {
            case AeroxResult.Sent:
                last=four.ToArray();must=false;silent=false;heard=true;triedAt=null;
                if(holding){holding=false;log.Write("mouse: released ("+(wasMust?"must-send":"moved")+")");}
                return true;
            case AeroxResult.NoReply:
                silent=true;heard=false;triedAt=time.GetTimestamp();
                return false;
            case AeroxResult.Failed:
                stale=true;Must();
                return false;
            default:return false;                         // Busy: tried again next tick; Absent: the next Scan says so
        }
    }
}
