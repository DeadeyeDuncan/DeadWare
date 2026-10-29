namespace DeadWare.Core;
public record ProcessIdentity(int Pid,long Generation,string Name);
/// <summary>Repairs lifecycle gaps using OS process creation times, not recycled PIDs or ETW arrival time.</summary>
public sealed class ProcessAttribution(Func<int,ProcessIdentity?> resolve)
{
    private readonly object gate=new();
    private readonly Dictionary<int,ProcessIdentity> identities=new();
    private ProcessLedger ledger=new();
    private long observedLoss;
    public bool Record(int pid,long eventTime,int down,int up,out ProcessIdentity? acceptedIdentity)
    {
        acceptedIdentity=null;
        if(pid<0||down<0||up<0)return false;
        lock(gate)
        {
            if(!identities.ContainsKey(pid)) Refresh(pid);
            if(!identities.TryGetValue(pid,out var identity)||eventTime<identity.Generation)return false;
            ledger.Add(pid,identity.Generation,down,up);
            acceptedIdentity=identity;
            return true;
        }
    }
    public bool Record(int pid,long eventTime,int down,int up)=>Record(pid,eventTime,down,up,out _);
    public bool ObserveLoss(long cumulativeCount)
    {
        lock(gate)
        {
            if(cumulativeCount<=observedLoss)return false;
            observedLoss=cumulativeCount;
            // We cannot know which lifecycle events were lost. Discard every potentially mixed total.
            identities.Clear();ledger=new();
            return true;
        }
    }
    public void Reconcile()
    {
        lock(gate) foreach(int pid in identities.Keys.ToArray()) Refresh(pid);
    }
    public void Refresh(int pid)
    {
        lock(gate)
        {
            var current=resolve(pid);
            if(current?.Pid!=pid)current=null;
            if(identities.TryGetValue(pid,out var previous))
            {
                if(previous.Generation==current?.Generation)return;
                ledger.Stop(pid,previous.Generation);identities.Remove(pid);
            }
            if(current is null)return;
            identities[pid]=current;ledger.Start(pid,current.Generation,current.Name);
        }
    }
    public void Stop(int pid,long eventTime)
    {
        lock(gate) if(identities.TryGetValue(pid,out var identity)&&eventTime>=identity.Generation)
        {ledger.Stop(pid,identity.Generation);identities.Remove(pid);}
    }
    public IReadOnlyList<ProcessTraffic> Take(double now) {lock(gate)return ledger.Take(now);}
}
public static class WindowCoordinates
{
    public const string Format="physical-pixels-v1";
    public static WindowPlacement FromDipSize(double leftPixels,double topPixels,double widthDips,double heightDips,uint dpi,bool topmost)
    {
        double scale=(dpi==0?96:dpi)/96d;
        return new(leftPixels,topPixels,Math.Round(widthDips*scale),Math.Round(heightDips*scale),topmost);
    }
}
