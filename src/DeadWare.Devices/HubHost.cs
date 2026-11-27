using DeadWare.Core;
namespace DeadWare.Devices;

// Lighting 5: the host's side of the hub. Everything here is touched under the host's gate, on its loop or in a request, so it needs no lock of
// its own; nothing here waits for the hub except LetGoAsync, which only the host's stop calls, off the lock. The worker is seen through its
// snapshot, turn by turn.
internal sealed class HubHost
{
    // The one wait for the hub: the hand-back at the host's stop.
    public static readonly TimeSpan HandBack=TimeSpan.FromMilliseconds(1500);
    readonly HubDetector? detector;
    readonly TimeProvider time;
    readonly HostLog log;
    // The last Allow sent; none before the first.
    bool sent;
    string? sentReason,sentDetail;
    // What the last turn saw (Moved), the last ReadingsAt (Readings), the parts last given to be saved (NewParts) and whether the stopped-pump
    // branch has acted on the PumpStopped snapshot it sees (StoppedPump).
    string lastState=HubStates.Off;
    IReadOnlyList<HubPart>? lastParts;
    bool lastListed,lastDriving,pumpActed;
    long lastReadingsAt;
    List<HubSavedPart>? partsTried;

    public HubHost(IHub hub,HubDetector? detector,TimeProvider time,HostLog log){Hub=hub;this.detector=detector;this.time=time;this.log=log;}
    public IHub Hub{get;}
    // Task 10 sets Taking and Switching; false until then.
    public bool Taking,Switching;
    // Task 10: when set, holding is forbidden with this reason whatever the conditions say.
    public string? Force,ForceDetail;
    public HubReadings? Before;public DateTimeOffset? TakenAt;public string? Problem,ProblemDetail;
    // Restarted: the one restart for a detector left on has been spent in this helper run. Restarting: that restart is under way, from its stop
    // until the relaunched OpenRGB has connected; until then the OpenRGB that had the hub may still be about, so the host keeps forbidding.
    public bool Restarted,Restarting;
    public bool LaunchedOn=>detector?.LaunchedOn??false;

    public string Mode(LightingSettings s)=>Taking?HubModes.Driven:s.Hub.Mode;

    // The state member. The before readings, the take-over time, the last reading and the last problem ride along in every state.
    public HubView View(LightingSettings s,(string? Reason,string? Detail) why)
    {
        var snap=Hub.Snapshot;
        string state;
        string? reason=null,detail=null;
        HubStop? stop=null;
        if(Switching) state=HubStates.Switching;
        else switch(Mode(s))
        {
            case HubModes.OpenRgb: state=HubStates.Off;break;
            case HubModes.Stopped: state=HubStates.GaveBack;stop=Copy(s.Hub.Stop);break;
            default:
                if(snap.State==HubStates.Driving) state=HubStates.Driving;
                else
                {
                    state=HubStates.LetGo;
                    (reason,detail)=why.Reason is not null?why:(snap.Reason,snap.Detail);
                    // Ruling P33 (fix round 1, M5): the worker has never held the hub in this run (its snapshot is off) and nothing else applies,
                    // as between the first connect and the first take: the hub is taken in a moment.
                    if(reason is null&&s.Hub.Mode==HubModes.Driven&&snap.State==HubStates.Off) reason=HubReasons.Starting;
                }
                break;
        }
        return new(state,reason,detail,Before,TakenAt,stop,snap.Read,Problem,ProblemDetail);
    }

    // For LightingDeviceService.Hub: none in mode openrgb (OpenRGB's hub is listed); else the worker's parts, or the saved ones while the
    // worker has none in this helper run, so a hub on its own stays listed across a restart (ruling P14).
    public HubListing? Listing(LightingSettings s)
    {
        if(Mode(s)==HubModes.OpenRgb) return null;
        var snap=Hub.Snapshot;
        return new(snap.Parts??HubParts.FromSaved(s.Hub.Parts)??[],snap.State==HubStates.Driving);
    }

    // OpenRGB's hub detector for the next launch: on only in mode openrgb outside a take-over.
    public void SyncDetector(LightingSettings s){if(detector is not null) detector.On=Mode(s)==HubModes.OpenRgb;}

    // The last Allow sent allowed holding (ruling R30: only a forbid, or none yet, is followed by a fresh scan before allowing).
    public bool Allowing=>sent&&sentReason is null;
    // Every Allow the host sends goes through here, and is sent only when it differs from the last one sent.
    public void Allow(string? reason,string? detail=null)
    {
        if(reason is null) detail=null;
        if(sent&&reason==sentReason&&detail==sentDetail) return;
        sent=true;sentReason=reason;sentDetail=detail;
        Hub.Allow(reason,detail);
    }

    // For the stop only: the worker's let-go, bounded by HandBack on the host's clock. False, and a log line, when it ran out.
    public async Task<bool> LetGoAsync()
    {
        var letGo=Hub.LetGoAsync();
        if(letGo.IsCompleted) return true;
        using var limit=new CancellationTokenSource();
        var expired=Task.Delay(HandBack,time,limit.Token);
        var first=await Task.WhenAny(letGo,expired).ConfigureAwait(false);
        limit.Cancel();
        if(first==letGo) return true;
        log.Write("hub hand-back did not finish in time");
        return false;
    }

    // The device list needs reading again: the snapshot's state or parts, or the listing given to the service, changed since the last turn.
    public bool Moved(HubSnapshot snap,HubListing? listing)
    {
        var moved=snap.State!=lastState||!SameParts(snap.Parts,lastParts)||(listing is not null)!=lastListed||(listing?.Driving??false)!=lastDriving;
        lastState=snap.State;lastParts=snap.Parts;lastListed=listing is not null;lastDriving=listing?.Driving??false;
        return moved;
    }

    // The readings event, once per round: when ReadingsAt moved while the snapshot is driving, with its age on the host's clock. A ReadingsAt
    // that moved while not driving is passed over.
    public HubReadingsEvent? Readings(HubSnapshot snap)
    {
        if(snap.ReadingsAt==lastReadingsAt) return null;
        lastReadingsAt=snap.ReadingsAt;
        if(snap.State!=HubStates.Driving||snap.Readings is null) return null;
        var age=time.GetElapsedTime(snap.ReadingsAt).TotalMilliseconds;
        return new(snap.Readings with{AgeMs=(int)Math.Clamp(age,0,int.MaxValue)});
    }

    // The saved parts follow the worker in mode driven: a copy of the hub settings with the snapshot's parts when they differ from the saved
    // ones in any channel, type or model; once per change, so parts that cannot be adopted are not tried again on every turn.
    public HubSettings? NewParts(LightingSettings s,HubSnapshot snap)
    {
        if(s.Hub.Mode!=HubModes.Driven){partsTried=null;return null;}
        if(snap.Parts is not{Count:>0} parts) return null;
        var saved=HubParts.ToSaved(parts);
        if(SameSaved(saved,s.Hub.Parts)||partsTried is not null&&SameSaved(saved,partsTried)) return null;
        partsTried=saved;
        var next=Hubs.Clone(s.Hub);
        next.Parts=saved.Select(p=>new HubSavedPart{Channel=p.Channel,Type=p.Type,Model=p.Model}).ToList();
        return next;
    }

    // A stopped pump while the saved mode is driven: a copy in mode stopped, with the parts kept. It acts once per PumpStopped snapshot and
    // remembers that it has acted until the mode changes, so a save that fails is logged once (ruling P17).
    public HubSettings? StoppedPump(LightingSettings s,HubSnapshot snap)
    {
        if(!snap.PumpStopped||s.Hub.Mode!=HubModes.Driven){pumpActed=false;return null;}
        if(pumpActed) return null;
        pumpActed=true;
        var next=Hubs.Clone(s.Hub);
        next.Mode=HubModes.Stopped;
        next.Stop=new HubStop{Kind=HubStops.PumpStopped,Part=HubPartKinds.Pump,Number=1};
        return next;
    }

    // Record equality, with Stop compared by its members (HubStop is a class).
    public static bool Same(HubView? a,HubView? b)
        =>a is null||b is null?a is null&&b is null:a with{Stop=null}==b with{Stop=null}&&SameStop(a.Stop,b.Stop);
    static bool SameStop(HubStop? a,HubStop? b)
        =>a is null||b is null?a is null&&b is null:a.Kind==b.Kind&&a.Part==b.Part&&a.Number==b.Number&&a.Before==b.Before&&a.After==b.After;
    static HubStop? Copy(HubStop? s)=>s is null?null:new(){Kind=s.Kind,Part=s.Part,Number=s.Number,Before=s.Before,After=s.After};
    static bool SameParts(IReadOnlyList<HubPart>? a,IReadOnlyList<HubPart>? b)=>a is null||b is null?a is null&&b is null:a.SequenceEqual(b);
    static bool SameSaved(IReadOnlyList<HubSavedPart> a,IReadOnlyList<HubSavedPart>? b)
        =>b is not null&&a.Count==b.Count&&a.Zip(b).All(x=>x.First.Channel==x.Second.Channel&&x.First.Type==x.Second.Type&&x.First.Model==x.Second.Model);
}
