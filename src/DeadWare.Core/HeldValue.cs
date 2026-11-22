namespace DeadWare.Core;

// Lighting 3b: a value the window asked the host for, shown in place of the state's until a state carries it. The host writes a reply
// before the state its request caused, so the asked value is let go once nothing waits or is on its way and a state counted above the
// last accepted reply's StatesBefore is shown. A refusal lets it go at once, so the state's value shows again. (The key paint and the
// layer list of the large keyboard view each hold one.)
public sealed class HeldValue<T> where T:class
{
    private T? value;
    private int inFlight;
    private long settleAfter=-1;
    public bool Held{get;private set;}
    public T? Current(T? state)=>Held?value:state;
    public void Ask(T? next){value=next;Held=true;}
    public void Sent()=>inFlight++;
    public void Replied(bool ok,long statesBefore)
    {
        inFlight=Math.Max(0,inFlight-1);
        if(ok) settleAfter=Math.Max(settleAfter,statesBefore);
        else Drop();
    }
    // waiting: something of the window's waits to be sent (a throttle's interval, a slider's value).
    public void Settle(long shown,bool waiting){if(Held&&inFlight==0&&!waiting&&shown>settleAfter) Drop();}
    public void Reset(){Drop();inFlight=0;settleAfter=-1;}
    private void Drop(){Held=false;value=null;}
}
