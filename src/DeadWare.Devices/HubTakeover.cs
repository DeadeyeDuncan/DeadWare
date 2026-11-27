using System.Globalization;
using DeadWare.Core;
namespace DeadWare.Devices;

// Lighting 5, Task 10: the hub's two requests and the take-over. The take-over is a small state machine the host drives on its loop, at most one
// step per turn, under the host's gate; the host does each step's work off the lock (the fresh scan, OpenRGB's stop, its start) and never holds the
// pipe. Nothing here waits for the hub: the worker is seen through its snapshot, turn by turn, and while backing out or switching off also through
// a let-go asked for once the last Allow sent forbade holding, looked at turn by turn and never awaited (it completes in the worker's step that has
// handed the hub back, which takes nothing while forbidden). The order that holds everywhere, with no time limit that overrides it: OpenRGB is
// stopped before the worker may take the hub, and the worker has let go before OpenRGB is started with its detector on. A mode is saved only when
// settled, and always through the host's SetHubModeLocked (adopted even when its save throws): driven after a passed check, stopped after a failed
// one or a stopped pump, openrgb when switching off. A take-over cut short saves nothing, so openrgb stays on disk; so does a verdict the settings
// refuse, which backs out as an interruption does.
internal enum HubStep{None,Scan,StopOpenRgb,Take,StartOpenRgb,Check,BackOut,SwitchOff}
// The work a turn leaves for the host, off the lock: OpenRGB's deliberate stop (its answer goes to Stopped), its start, or the restart (a deliberate
// stop while an OpenRGB of ours exists, Starting included, then the start).
internal enum HubChore{None,StopOpenRgb,StartOpenRgb,Restart}
// One loop turn: the worker's snapshot, the host's facts (their Mode is HubHost.Mode, so driven while Taking), whether this turn's fresh scan stood
// (the Scan step's) and the saved hub mode.
internal readonly record struct HubTurn(HubSnapshot Snap,HubFacts Facts,bool Scanned,string Saved);
// What a hub request is judged by, under the host's gate, never by a scan: the last scan's conflicts and whether it failed (at most 5 s old). Lit:
// lighting is enabled, connected and Running.
internal readonly record struct HubAsk(bool Quiet,bool SaveAllowed,string Saved,IReadOnlyList<LightingConflict> Conflicts,bool ScanFailed,bool Lit,bool TwoHubs,
    DateTimeOffset Now);

internal sealed class HubTakeover
{
    public static readonly TimeSpan CheckAfter=TimeSpan.FromSeconds(15),TakeLimit=TimeSpan.FromSeconds(10),CheckLimit=TimeSpan.FromSeconds(60);
    const string InterruptedLine="hub take-over interrupted: ",SlowLine="hub hand-back is taking longer than 1.5 s";
    readonly HubHost host;
    readonly TimeProvider time;
    readonly HostLog log;
    // The dialog's reading the take-over began with.
    HubReading? read;
    // Take: the snapshot when it began (a let-go counts only when it says something new: one left from before is no answer) and when it began.
    // From the take on: the take's SoftwareAt, and whether a snapshot without PumpStopped has been seen (a stopped pump left from before is no verdict).
    HubSnapshot atTake=HubSnapshot.Idle;
    long takeFrom,softwareAt;
    bool pumpClear;
    // BackOut: why, and the log line's phrase. BackOut and SwitchOff: when the wait for the worker began, the let-go asked for, whether the slow line
    // was written, whether the worker has let go (then only the restart is owed) and whether this turn's restart ends it (Issued).
    string? reason,detail,phrase;
    long waitFrom;
    Task? letGo;
    bool slowLogged,letGone,ending;

    public HubTakeover(HubHost host,TimeProvider time,HostLog log){this.host=host;this.time=time;this.log=log;}
    public HubStep Step{get;private set;}

    public void BeginOn(HubReading? read)
    {
        this.read=read;
        host.Switching=true;host.Problem=null;host.ProblemDetail=null;host.Before=null;host.TakenAt=null;
        Step=HubStep.Scan;
    }
    public void BeginOff()
    {
        host.Switching=true;host.Force=HubReasons.Off;host.ForceDetail=null;
        Step=HubStep.SwitchOff;
        Wait();
    }

    // readHub. Null: accepted, the worker asked for one reading (its result reaches the window as HubView.Read); else the refusal.
    public string? Read(HubAsk a)
    {
        if(host.Switching||a.Quiet||a.Saved!=HubModes.OpenRgb) return HostReplyCodes.HubBusy;
        if(Blockers(a) is{Count:>0} blockers) return HubTexts.Blocked(blockers);
        host.Problem=null;host.ProblemDetail=null;
        host.Hub.RequestReading();
        log.Write("hub reading requested");
        return null;
    }

    // setHubMode. Null: accepted (the saved mode already, with nothing done, included); else the refusal. Driven only from openrgb, with the
    // dialog's reading at most two minutes old and no blocker; the before reading is the one the window says the dialog showed, none when it showed none.
    public string? SetMode(string? mode,HubAsk a,HubReading? shown=null)
    {
        if(mode is not(HubModes.OpenRgb or HubModes.Driven)) return Refuse(HostReplyCodes.BadHubMode);
        if(host.Switching||a.Quiet) return Refuse(HostReplyCodes.HubBusy);
        if(!a.SaveAllowed) return Refuse(HubTexts.Blocked([(HubReasons.CantSave,null)]));
        if(mode==a.Saved) return null;
        if(mode==HubModes.OpenRgb){log.Write("hub mode change requested: openrgb");BeginOff();return null;}
        if(a.Saved!=HubModes.OpenRgb) return Refuse(HostReplyCodes.HubBusy);
        if(Blockers(a) is{Count:>0} blockers) return Refuse(HubTexts.Blocked(blockers));
        var reading=host.Hub.Snapshot.Read;
        if(reading is null||HubChecks.Stale(reading.At,a.Now)) return Refuse(HostReplyCodes.HubStale);
        if(reading.Blocker is not null) return Refuse(HubTexts.Blocked([(reading.Blocker,reading.Detail)]));
        if(shown is not null&&HubChecks.Stale(shown.At,a.Now)) return Refuse(HostReplyCodes.HubStale);
        log.Write("hub mode change requested: driven");
        // The check compares against exactly the reading the dialog showed, never against what the snapshot holds now.
        BeginOn(shown);
        return null;
    }
    string Refuse(string why){log.Write("hub mode change refused: "+why);return why;}

    // The conflict kinds as HubHold reads them; lighting not running only when no conflict that blocks lighting already says why; two hubs; no saves.
    static List<(string Reason,string? Detail)> Blockers(HubAsk a)
    {
        var list=new List<(string Reason,string? Detail)>();
        void Add(string? r){if(r is not null&&!list.Exists(x=>x.Reason==r)) list.Add((r,null));}
        foreach(var c in a.Conflicts) Add(HubHold.Conflict(c.Kind));
        if((a.ScanFailed||!a.Lit)&&!a.Conflicts.Any(c=>c.BlocksLighting)) Add(HubReasons.NoLighting);
        if(a.TwoHubs) Add(HubReasons.TwoHubs);
        if(!a.SaveAllowed) Add(HubReasons.CantSave);
        return list;
    }

    // The step's work for this turn, under the host's gate, before the host sends its Allow; settle is the host's SetHubModeLocked. Nothing while
    // the host is stopping: its finally hands the hub back and writes the detector on, and a Stop waits for nothing else.
    public HubChore Turn(HubTurn t,Func<HubSettings,string,bool> settle)
    {
        if(Step==HubStep.None||t.Facts.Stopping) return HubChore.None;
        switch(Step)
        {
            case HubStep.Scan:
                if(!t.Scanned) return HubChore.None;                   // the scan raced a relaunch and was dropped: again next turn
                if(Refused(t.Facts) is{} scan){End(scan);return HubChore.None;}
                Step=HubStep.StopOpenRgb;
                return HubChore.None;
            case HubStep.StopOpenRgb:
                if(Refused(t.Facts) is{} stop){End(stop);return HubChore.None;}
                // The host's Allow this turn still forbids (no scan stood for a take), and OpenRGB's next launch reads the detector off.
                host.Taking=true;
                host.Before=read is{Ok:true}?read.Readings:null;
                host.Hub.Resume();                                     // the owner asked: no retry delay or hold left by an earlier take
                return HubChore.StopOpenRgb;
            case HubStep.BackOut:return BackOut(t);
            case HubStep.SwitchOff:return SwitchOff(t,settle);
            default:return Taking(t,settle);
        }
    }

    // OpenRGB's deliberate stop has returned. It exited: the worker may take the hub from the next turn. It did not: a hub still in use.
    public void Stopped(bool exited)
    {
        if(Step!=HubStep.StopOpenRgb) return;
        if(!exited){Back(HubReasons.NoLighting,null,"OpenRGB did not exit");return;}
        log.Write("hub mode change: OpenRGB stopped");
        atTake=host.Hub.Snapshot;pumpClear=!atTake.PumpStopped;takeFrom=time.GetTimestamp();
        Step=HubStep.Take;
    }

    // After this turn's chore has been issued (or there was none to issue): a back-out or a switch-off whose restart that was ends here, so Switching
    // stays true until the start has been issued. True when one ended (a state is due).
    public bool Issued()
    {
        if(!ending) return false;
        ending=false;
        host.Switching=false;
        if(Step==HubStep.BackOut){host.Problem=reason;host.ProblemDetail=detail;log.Write(InterruptedLine+phrase);}
        Step=HubStep.None;
        return true;
    }

    // Take, StartOpenRgb and Check: from the moment the worker may take the hub until the verdict. A stopped pump is the verdict, never a back-out;
    // then a forbid (quiet reads as asleep) and a snapshot that has left driving are back-outs.
    HubChore Taking(HubTurn t,Func<HubSettings,string,bool> settle)
    {
        var snap=t.Snap;
        if(!snap.PumpStopped) pumpClear=true;
        else if(pumpClear)
        {
            var started=Step==HubStep.Check;
            if(!settle(Settled(HubModes.Stopped,new HubStop{Kind=HubStops.PumpStopped,Part=HubPartKinds.Pump,Number=1},snap),"hub mode settled: openrgb -> stopped (the pump read 0 RPM)"))
            {Back(HubReasons.CantSave,null);return HubChore.None;}
            Done();
            return started?HubChore.None:Start();                     // OpenRGB without the hub: mode stopped keeps the detector off
        }
        var why=HubHold.Why(t.Facts);
        if(why.Reason is not null){Back(why.Reason==HubReasons.Waking?HubReasons.Asleep:why.Reason,why.Detail);return HubChore.None;}
        if(Step==HubStep.Take)
        {
            if(snap.State==HubStates.Driving){host.TakenAt=time.GetUtcNow();softwareAt=snap.SoftwareAt;Step=HubStep.StartOpenRgb;}
            else if(snap.State==HubStates.LetGo&&snap.Reason is{} gone&&(atTake.State!=HubStates.LetGo||gone!=atTake.Reason||snap.Detail!=atTake.Detail)) Back(gone,snap.Detail);
            else if(time.GetElapsedTime(takeFrom)>=TakeLimit) Back(HubReasons.Silent,null);
            return HubChore.None;
        }
        if(snap.State!=HubStates.Driving||snap.SoftwareAt!=softwareAt)
        {
            if(snap.State==HubStates.LetGo&&snap.Reason is{} left) Back(left,snap.Detail);
            else Back(HubReasons.Silent,null);
            return HubChore.None;
        }
        if(Step==HubStep.StartOpenRgb){Step=HubStep.Check;return Start();}
        if(snap.Readings is not null&&time.GetElapsedTime(softwareAt,snap.ReadingsAt)>=CheckAfter){Verdict(snap,settle);return HubChore.None;}
        if(time.GetElapsedTime(softwareAt)>=CheckLimit) Back(HubReasons.Silent,null);
        return HubChore.None;
    }
    HubChore Start(){log.Write("hub mode change: OpenRGB started without the hub");return HubChore.StartOpenRgb;}

    // The check at the first round 15 s or more after the take: a pass (or no before reading) settles driven, a fail settles stopped, whose mode
    // forbids holding, so the worker hands back at its next step while OpenRGB keeps running without the hub. Before and TakenAt stay. A verdict the
    // settings refuse (fix wave 1, D3) is never dropped: it is an interruption, so the worker lets go first, nothing is saved and openrgb stays.
    void Verdict(HubSnapshot snap,Func<HubSettings,string,bool> settle)
    {
        var after=snap.Readings!;
        var stop=host.Before is{} before?HubChecks.Compare(before,after):null;
        log.Write("hub check: "+CheckText(host.Before,after)+(stop is null?": pass":": fail"));
        var settled=stop is null?settle(Settled(HubModes.Driven,null,snap),"hub mode settled: openrgb -> driven")
            :settle(Settled(HubModes.Stopped,stop,snap),"hub mode settled: openrgb -> stopped");
        if(!settled){Back(HubReasons.CantSave,null);return;}
        Done();
    }
    static HubSettings Settled(string mode,HubStop? stop,HubSnapshot snap)=>new(){Mode=mode,Stop=stop,Parts=snap.Parts is{Count:>0} parts?HubParts.ToSaved(parts):null};
    void Done(){host.Taking=false;host.Switching=false;Step=HubStep.None;}

    // A back-out: holding is forbidden with the reason from the host's next Allow on (Taking stays true, so the detector stays off).
    void Back(string why,string? whyDetail,string? line=null)
    {
        reason=why;detail=whyDetail;phrase=line??HubTexts.Reason(why,whyDetail)??why;
        host.Force=why;host.ForceDetail=whyDetail;
        Step=HubStep.BackOut;
        Wait();
    }
    void Wait(){waitFrom=time.GetTimestamp();letGo=null;slowLogged=false;letGone=false;ending=false;}

    // Until the worker has let go nothing more happens: no Taking=false, no detector on, no start. Then nothing is saved, and OpenRGB comes back with
    // its detector on.
    HubChore BackOut(HubTurn t)
    {
        if(!letGone)
        {
            if(!LetGo(t.Snap)) return HubChore.None;
            letGone=true;
            host.Taking=false;host.Force=null;host.ForceDetail=null;host.Before=null;host.TakenAt=null;
        }
        return Restart(t.Facts);
    }
    // Until the worker has let go nothing more happens: no save, no detector on, no start. Then openrgb is settled (a new HubSettings: no stop, no
    // parts) and OpenRGB comes back with its detector on.
    HubChore SwitchOff(HubTurn t,Func<HubSettings,string,bool> settle)
    {
        if(!letGone)
        {
            if(!LetGo(t.Snap)) return HubChore.None;
            letGone=true;
            settle(new HubSettings(),$"hub mode settled: {t.Saved} -> openrgb");
            host.Force=null;host.ForceDetail=null;host.Before=null;host.TakenAt=null;host.Problem=null;host.ProblemDetail=null;
        }
        return Restart(t.Facts);
    }
    // The worker has let go once its snapshot has left driving and the let-go asked for (only once the last Allow sent forbade holding) has
    // completed. No limit: the watchdog closes the port 1.5 s into a transaction and ends the helper 3 s later; at 1.5 s this only logs, once.
    bool LetGo(HubSnapshot snap)
    {
        if(letGo is null&&!host.Allowing) letGo=host.Hub.LetGoAsync();
        if(letGo is{IsCompleted:true}&&snap.State!=HubStates.Driving) return true;
        if(!slowLogged&&time.GetElapsedTime(waitFrom)>=HubHost.HandBack){slowLogged=true;log.Write(SlowLine);}
        return false;
    }
    // The restart after the let-go: not while quiet (an OpenRGB launch is a full USB detection, kept out of suspend and the wake's wait; the wake's
    // end issues it), none while lighting is not enabled (its ordinary start brings OpenRGB back once the conflict clears).
    HubChore Restart(HubFacts f)
    {
        if(f.Quiet) return HubChore.None;
        ending=true;
        return f.EngineActive?HubChore.Restart:HubChore.None;
    }

    // Ended before anything was stopped.
    void End((string Reason,string? Detail) why)
    {
        host.Switching=false;host.Problem=why.Reason;host.ProblemDetail=why.Detail;
        Step=HubStep.None;
        log.Write(InterruptedLine+(HubTexts.Reason(why.Reason,why.Detail)??why.Reason));
    }
    // What HubHold would refuse a take for, on the conflicts and the host's condition alone (the OpenRGB that has the hub now is the one this
    // take-over stops); quiet reads as asleep.
    static (string Reason,string? Detail)? Refused(HubFacts f)
    {
        var (r,d)=HubHold.Why(f with{Mode=HubModes.Driven,ConnectedOnce=true,OpenRgbListsHub=false,LaunchedWithHub=false,RestartSpent=false});
        return r is null?null:(r==HubReasons.Waking?HubReasons.Asleep:r,d);
    }

    // "pump 2,716 -> 2,709 RPM, fan 1 1,337 -> 1,331 RPM, …": the pump parts, then the fans, each in channel order, as Compare reads them.
    static string CheckText(HubReadings? before,HubReadings after)
    {
        if(before is null) return "no before reading, "+string.Join(", ",Rows(after).Select(x=>$"{x.Name} {Rpm(x.Rpm)}"));
        return string.Join(", ",Rows(before).Select(x=>
        {
            var now=after.Parts.FirstOrDefault(p=>p.Channel==x.Channel)?.Rpm;
            return x.Rpm is{} b&&now is{} n?$"{x.Name} {Num(b)} -> {Num(n)} RPM":$"{x.Name} {Rpm(x.Rpm)} -> {Rpm(now)}";
        }));
    }
    static IEnumerable<(string Name,int Channel,int? Rpm)> Rows(HubReadings r)
    {
        foreach(var kind in new[]{HubPartKinds.Pump,HubPartKinds.Fan})
        {
            var parts=r.Parts.Where(p=>p.Kind==kind).OrderBy(p=>p.Channel).ToList();
            for(int i=0;i<parts.Count;i++) yield return (kind==HubPartKinds.Pump&&parts.Count==1?"pump":$"{kind} {i+1}",parts[i].Channel,parts[i].Rpm);
        }
    }
    static string Num(int v)=>v.ToString("N0",CultureInfo.InvariantCulture);
    static string Rpm(int? v)=>v is{} n?Num(n)+" RPM":"no reading";
}
