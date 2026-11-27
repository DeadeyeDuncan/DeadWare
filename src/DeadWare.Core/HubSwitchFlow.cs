namespace DeadWare.Core;
// The request to send now: ReadHubRequest, SetHubModeRequest(driven) or SetHubModeRequest(openrgb).
public enum HubSend{None,Read,Driven,OpenRgb}
// Open the take-over dialog. Read null or not Ok: the dialog's no-reading form.
public sealed record HubAsk(HubReading? Read)
{
    // What the dialog shows: the reading when it is a good one, else its no-reading form (null). The take-over request names exactly this.
    public HubReading? Shown=>Read is{Ok:true,Readings:not null}?Read:null;
}
// What the switch shows now. Ask and Send are set in exactly the one result that asks; nothing is ever asked for on a timer alone.
public sealed record HubSwitchShow(bool On,bool Enabled,string? Red,HubAsk? Ask=null,HubSend Send=HubSend.None);

// The whole sequence of Settings → Lighting's hub switch: the dialog's reading with its 3 s wait, the take-over only after the owner's yes, the way
// back through OpenRGB with its 60 s wait, and the red lines. Stepped by clicks, replies, states and time (TimeProvider); it sends nothing itself.
// The hub's mode never changes without the owner's say-so: Driven is sent only by Answer(…,true) after an Ask.
public sealed class HubSwitchFlow
{
    // The text of a reply that came with no words (no client to answer).
    public const string NoReplyText="The lighting helper isn't connected.",NoAnswerText="The lighting helper didn't answer.";
    enum Phase{Idle,Reading,WaitingRead,Asking,Taking,Backing,RetryBacking,WaitBack}
    readonly TimeProvider time;
    Phase phase=Phase.Idle;
    // turningOn: the direction last asked for; wasOn: the one before it, which a refused change goes back to.
    bool turningOn,wasOn;
    string? ownRed;
    DateTimeOffset clicked;
    long started,lastLook;
    // The reading the open dialog shows; a take-over on a reading that has since gone stale is not sent.
    HubReading? asked;
    public HubSwitchFlow(TimeProvider time)=>this.time=time;

    // The switch.
    public HubSwitchShow Click(HostState? state,bool on)
    {
        ownRed=null;
        if(phase!=Phase.Idle) return Show(state);
        var hub=state?.Hub;
        if(on)
        {
            if(HubFlow.RetryFirst(hub)) return StartBack();
            return hub?.State==HubStates.Off?StartRead():Show(state);
        }
        if(hub?.State is HubStates.Driving or HubStates.LetGo){wasOn=turningOn;turningOn=false;phase=Phase.Backing;return new(false,false,null,Send:HubSend.OpenRgb);}
        return Show(state);
    }
    // Back to OpenRGB, as before.
    public HubSwitchShow Back(HostState? state)
    {
        ownRed=null;
        if(phase!=Phase.Idle||state?.Hub?.State!=HubStates.GaveBack) return Show(state);
        wasOn=turningOn;turningOn=false;phase=Phase.Backing;
        return new(false,false,null,Send:HubSend.OpenRgb);
    }
    // Try the take-over again.
    public HubSwitchShow Retry(HostState? state)
    {
        ownRed=null;
        if(phase!=Phase.Idle||!HubFlow.RetryFirst(state?.Hub)) return Show(state);
        return StartBack();
    }
    // The answer to the request the flow last asked for.
    public HubSwitchShow Reply(HostState? state,bool ok,string? error)
    {
        // A request nobody answered may still have been accepted: the state tells, and the flow goes on as if it were.
        if(!ok&&error==NoAnswerText) ok=true;
        switch(phase)
        {
            case Phase.Reading:
                if(!ok) return Refused(state,error);
                phase=Phase.WaitingRead;started=lastLook=time.GetTimestamp();
                return Look(state);
            case Phase.Taking:
                if(!ok) return Refused(state,error);
                // The state now says switching; until it does, the switch stays where the owner put it.
                phase=Phase.Idle;turningOn=true;
                return Show(state);
            case Phase.Backing:
                phase=Phase.Idle;
                if(!ok){ownRed=Words(error);turningOn=wasOn;}
                return Show(state);
            case Phase.RetryBacking:
                if(!ok) return Refused(state,error);
                phase=Phase.WaitBack;started=time.GetTimestamp();
                return Look(state);
            default: return Show(state);
        }
    }
    // The dialog was closed.
    public HubSwitchShow Answer(HostState? state,bool takeOver)
    {
        if(phase!=Phase.Asking) return Show(state);
        if(!takeOver){phase=Phase.Idle;turningOn=false;return Show(state);}
        if(asked is {} shown&&HubChecks.Stale(shown.At,time.GetUtcNow())) return Refused(state,HostReplyCodes.HubStale);
        phase=Phase.Taking;
        return new(true,false,null,Send:HubSend.Driven);
    }
    // Every pushed state and every tick: runs the 3 s and 60 s limits.
    public HubSwitchShow Look(HostState? state)
    {
        switch(phase)
        {
            case Phase.WaitingRead:
            {
                // The helper is gone (lighting switched off, or the connection lost): nothing to ask about.
                if(state is null) return Refused(null,null);
                var gap=time.GetElapsedTime(lastLook);
                lastLook=time.GetTimestamp();
                if(HubFlow.Fresh(state.Hub,clicked) is {} reading)
                {
                    // A reading the helper would refuse as stale is not shown as "just now".
                    if(HubChecks.Stale(reading.At,time.GetUtcNow())) return Refused(state,HostReplyCodes.HubStale);
                    if(reading.Blocker is {} blocker){phase=Phase.Idle;turningOn=false;ownRed=HubTexts.Blocked([(blocker,reading.Detail)]);return Show(state);}
                    phase=Phase.Asking;asked=reading;
                    return new(true,false,null,new HubAsk(reading));
                }
                // The 3 s wait was left unwatched for longer than itself (the owner was away from Settings): it ends, it does not open a dialog late.
                if(gap>HubFlow.ReadWait){phase=Phase.Idle;turningOn=false;return Show(state);}
                if(time.GetElapsedTime(started)>=HubFlow.ReadWait){phase=Phase.Asking;asked=null;return new(true,false,null,new HubAsk(null));}
                return Show(state);
            }
            case Phase.WaitBack:
                if(state is null) return Refused(null,null);
                if(HubFlow.BackThroughOpenRgb(state)) return StartRead();
                if(time.GetElapsedTime(started)>=HubFlow.BackWait){phase=Phase.Idle;turningOn=false;ownRed=HubTexts.NotBack;}
                return Show(state);
            default: return Show(state);
        }
    }

    HubSwitchShow StartRead()
    {
        wasOn=turningOn;turningOn=true;phase=Phase.Reading;clicked=time.GetUtcNow();
        return new(true,false,null,Send:HubSend.Read);
    }
    // Gave back, and the owner asked for the take-over again: the hub goes back through OpenRGB first.
    HubSwitchShow StartBack()
    {
        wasOn=turningOn;turningOn=true;phase=Phase.RetryBacking;
        return new(true,false,null,Send:HubSend.OpenRgb);
    }
    HubSwitchShow Refused(HostState? state,string? error)
    {
        asked=null;
        phase=Phase.Idle;turningOn=false;ownRed=Words(error);
        return Show(state);
    }
    static string Words(string? error)
    {
        string said=HostReplyCodes.Explain(error??"");
        return said.Length>0?said:NoReplyText;
    }
    // Where a running sequence is heading, with the switch out of reach; idle, what the state says.
    HubSwitchShow Show(HostState? state)
    {
        if(phase!=Phase.Idle) return new(phase!=Phase.Backing,false,null);
        var hub=state?.Hub;
        string? red=ownRed??(hub?.Problem is {} problem?HubTexts.Interrupted(problem,hub.ProblemDetail):null);
        return new(HubFlow.SwitchOn(hub,turningOn),state is not null&&hub?.State!=HubStates.Switching,red);
    }
}
