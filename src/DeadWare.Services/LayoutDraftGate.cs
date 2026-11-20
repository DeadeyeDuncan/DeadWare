namespace DeadWare.App;

/// <summary>What a SetLayout reply means for the desk's draft: Stale (sent before a drop: ignore it), Refused (drop the draft), Waiting
/// (other sends are still on their way) or Last (none left on its way: the release now waits for a newer state).</summary>
internal enum LayoutReplied { Stale, Refused, Waiting, Last }

/// <summary>
/// When the Lighting page's desk may let go of its held layout draft (fix wave W6, moved out of the page unchanged). The pipe writes a
/// request's reply BEFORE the state that request caused, so the draft goes only once no SetLayout is on its way, no nudge waits to be
/// sent, and a state counted above the last Ok reply's StatesBefore is shown. A refusal, the host not running, a client swap or the page
/// leaving drops it: every ticket out then is stale. UI thread only; no locking.
/// </summary>
internal sealed class LayoutDraftGate
{
    // sends: SetLayout requests on their way under this epoch. releaseAfter: StatesBefore of the Ok reply that left none on its way.
    int sends,epoch;
    long? releaseAfter;

    /// <summary>A send is on its way, or a release is still awaited.</summary>
    public bool Holding=>sends>0||releaseAfter is not null;

    /// <summary>A SetLayout went out; its ticket comes back with <see cref="Replied"/>.</summary>
    public int Sent(){sends++;return epoch;}

    /// <summary>A reply arrived. A refusal drops every ticket out, as <see cref="Drop"/> does; the caller drops the draft.</summary>
    public LayoutReplied Replied(int ticket,bool ok,long statesBefore)
    {
        if(ticket!=epoch) return LayoutReplied.Stale;
        if(!ok){Drop();return LayoutReplied.Refused;}
        sends=Math.Max(0,sends-1);
        if(sends>0) return LayoutReplied.Waiting;
        releaseAfter=statesBefore;
        return LayoutReplied.Last;
    }

    /// <summary>True once, when the draft goes: nothing on its way, nothing waiting (<paramref name="moreWaiting"/>: a nudge not yet
    /// sent), and the shown state's count <paramref name="seq"/> above the last Ok reply's StatesBefore.</summary>
    public bool ShouldRelease(long seq,bool moreWaiting)
    {
        if(sends>0||moreWaiting||releaseAfter is not {} after||seq<=after) return false;
        releaseAfter=null;
        return true;
    }

    /// <summary>Gives up the edits on their way: every ticket out is stale and no release is awaited.</summary>
    public void Drop(){epoch++;sends=0;releaseAfter=null;}
}
