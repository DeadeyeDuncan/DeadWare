using System.Diagnostics;
using DeadWare.Core;
namespace DeadWare.Devices;

// Lighting 5: the hub worker's logic, single-threaded. Only Step touches the transport and the Corsair lock, on the thread that calls it (the
// shell's, Task 6; a test's). The IHub calls only store the newest value under a small lock and call Nudge; Step copies them at its start and
// never holds that lock during I/O. Every exchange with the hub happens inside a locked unit (Enter, try, finally{Exit}); any exception inside one
// is a failure, and no exception leaves Step. The hub also carries the pump and the radiator fans: nothing here sets a speed (HubFrame has no such
// factory), and every way off the hub either hands it back (hardware mode under the lock) or, when it stopped answering, closes the port.
internal sealed class HubCore:IHub
{
    public static readonly TimeSpan FrameGap=TimeSpan.FromMilliseconds(125),KeepAlive=TimeSpan.FromSeconds(2),ReadingsEvery=TimeSpan.FromSeconds(3),
        TransactionLimit=TimeSpan.FromMilliseconds(1500),ReadingLimit=TimeSpan.FromSeconds(2),RetryMin=TimeSpan.FromSeconds(30),RetryMax=TimeSpan.FromMinutes(10),
        ReEntryWindow=TimeSpan.FromMinutes(10),IdleWait=TimeSpan.FromSeconds(5),
        // Fix wave 2, B: after software mode is sent to a hub that was in its own mode (a take's probe said so, or a re-entry), nothing is sent for
        // this long, with the lock released; then the take or the interrupted transaction goes on in a unit of its own.
        SoftwarePause=TimeSpan.FromSeconds(1);
    public const int LockLimitMs=250,ReplyLimitMs=250,DrainLimitMs=1,MaxSkipped=16,ReEntries=3;
    // A dialog's reading that found the lock busy is tried again this soon (inside its 2 s); a take refused for a busy lock no sooner than
    // TakeAgain (ruling R21: each try lists the hubs, opens one and waits up to LockLimitMs). A reading round or frame skipped for a busy lock
    // keeps its own timing.
    static readonly TimeSpan BusyAgain=TimeSpan.FromMilliseconds(250),TakeAgain=TimeSpan.FromSeconds(1),Least=TimeSpan.FromMilliseconds(1);
    const string SilentText="the hub stopped answering",SlowText="the hub answered too slowly",BusyText="the Corsair lock was busy";

    enum Outcome{Done,Busy,Failed,OwnMode,FallsBack,NoLock,Quiet,Paused}
    // Fix wave 2, B: what goes on once a pause after software mode has ended: the rest of the take, or the round that was interrupted. An interrupted
    // colour write needs no mark: it is due again at once (coloured false).
    enum Then{None,Take,Round}
    // NoHub, TwoHubs and Unsupported end at a Recheck that shows the change, or at Resume; NoLock lasts the run; PumpStopped ends at an Allow(null)
    // that follows a forbid.
    enum Hold{None,NoHub,TwoHubs,Unsupported,NoLock,PumpStopped}
    readonly record struct Inputs(Rgb[]? Posted,bool Dark,string? Forbidden,string? Detail,int Forbids,bool Quiet,int Quiets,int Resumes,bool Recheck,long? Asked,
        List<TaskCompletionSource>? Waiters);
    // Fix wave 2, C: each carries what happened at which exchange, for the log ("read speeds: no reply", "colour write answered 03").
    abstract class HubError(string what):Exception{public string What{get;}=what;}
    sealed class Fault(string what):HubError(what);                       // a missing or malformed reply
    sealed class Slow(string what):HubError(what);                        // a unit past its limit on the core's clock
    sealed class WrongMode(string what):HubError(what);                   // status 03: the hub is in its own mode
    sealed class FallsBack(string what):HubError(what);                   // a fourth wrong mode inside the window
    sealed class Quieted:Exception;                                        // Quiet was called during this step
    sealed class Paused:Exception;                                         // software mode was sent to a hub in its own mode: the unit ends

    readonly IHubTransport transport;
    readonly IHubLock corsairLock;
    readonly TimeProvider time;
    readonly HostLog log;

    // What the calls store, under sync. quiets is also read by a step under way (Volatile), so nothing more is written once Quiet was called.
    readonly object sync=new();
    Rgb[]? posted;
    bool dark,quiet,recheck;
    string? forbidden=HubReasons.Starting,detail;                         // nobody has allowed holding yet
    int forbids,quiets,resumes;
    long? asked;
    List<TaskCompletionSource>? waiters;
    volatile HubSnapshot snapshot=HubSnapshot.Idle;
    long busySince;
    IHubPort? port;                                                        // Interlocked: Abort takes it from another thread

    // The step's own state, touched only by Step.
    // quieted: a Quiet landed during this step (GoQuiet after the step's start); the step then ends without a take or a paint.
    bool held,everHeld,takenOnce,skipped,quieted,spell,coloured,sentDark,wantDark,firstFrame,sizeLogged,missing,armed,published;
    Hold hold;
    string? holdReason,holdDetail;                                         // what the hold's let-go says, to show again after the wake's end
    int seenQuiets,seenResumes,stopForbids,lastCount=-1,reEntries,stalled;
    long softwareAt,readAt,colourAt,armedAt,skippedAt,reEntryFrom,unitFrom,pauseFrom;   // pauseFrom: when the pause began, 0 when none
    Then resume;
    HubFrame? sending;HubEndpoint? sendingData;                            // the exchange under way, for the log
    string? what;                                                          // the last unit's failure at its exchange, null when none
    HubValue[]? takeT,roundT;                                              // temperatures read before a pause, kept for the repeat
    List<TaskCompletionSource>? stepWaiters;                               // the let-go tasks this step took: completed on every way out, the last guard's too
    long? reading;                                                         // the dialog's request being served: when it was made
    TimeSpan unitLimit,retryWait,nextWait=RetryMin;
    IReadOnlyList<HubPart> parts=[];
    Rgb[]? good,seenPost,sentZones;
    HubBuild? built;
    HubReadings? first;
    string why="";
    List<string>? deferred;
    readonly Dictionary<int,(bool Rpm,bool Celsius)> seen=new();

    public HubCore(IHubTransport transport,IHubLock corsairLock,TimeProvider time,HostLog log){this.transport=transport;this.corsairLock=corsairLock;this.time=time;this.log=log;}

    // Called by every IHub call so the shell ends its wait.
    public Action Nudge{get;set;}=()=>{};
    public HubSnapshot Snapshot=>snapshot;
    public event Action? Changed;
    // Stopwatch.GetTimestamp() when the locked unit under way began, 0 when none: real time, for the watchdog's timer thread.
    public long BusySince=>Volatile.Read(ref busySince);

    public void Post(Rgb[] zones){var copy=(zones??[]).ToArray();lock(sync)posted=copy;Nudge?.Invoke();}
    public void SetDark(bool on){lock(sync)dark=on;Nudge?.Invoke();}
    public void Allow(string? forbidden,string? detail=null){lock(sync){this.forbidden=forbidden;this.detail=forbidden is null?null:detail;if(forbidden is not null)forbids++;}Nudge?.Invoke();}
    public void Quiet(){lock(sync){quiet=true;Volatile.Write(ref quiets,quiets+1);}Nudge?.Invoke();}
    public void Resume(){lock(sync){quiet=false;resumes++;}Nudge?.Invoke();}
    public void Recheck(){lock(sync)recheck=true;Nudge?.Invoke();}
    public void RequestReading(){var at=time.GetTimestamp();lock(sync)asked=at;Nudge?.Invoke();}
    public Task LetGoAsync()
    {
        var t=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(sync)(waiters??=[]).Add(t);
        Nudge?.Invoke();
        return t.Task;
    }

    // Any thread, any number of times: closes the open port, which ends a call under way; the step it interrupts ends as a failure and still exits
    // the lock. Nothing happens, and nothing is logged, with no port open.
    public void Abort()
    {
        var p=Interlocked.Exchange(ref port,null);
        if(p is null) return;
        log.Write("hub: a USB call was stuck; its handle was closed");
        try{p.Dispose();}catch(Exception){}
    }

    // Does what is due now and returns how long until the next due work: a reading, a frame, a keep-alive or a retry; IdleWait when nothing is.
    public TimeSpan Step()
    {
        published=false;
        TimeSpan wait;
        try{wait=Work();}
        catch(Exception e){wait=Broken(e);}
        if(published) Raise();
        return wait<Least?Least:wait;
    }

    TimeSpan Work()
    {
        Inputs i;
        lock(sync){i=new(posted,dark,forbidden,detail,forbids,quiet,quiets,resumes,recheck,asked,waiters);recheck=false;asked=null;waiters=null;}
        stepWaiters=i.Waiters;wantDark=i.Dark;
        if(i.Quiets!=seenQuiets) GoQuiet(i.Quiets);
        if(i.Resumes!=seenResumes){seenResumes=i.Resumes;Unhold();}
        quieted=false;                                                     // from here on a GoQuiet is a Quiet that landed during this step
        if(i.Asked is long at) reading=at;
        if(hold==Hold.PumpStopped&&i.Forbidden is null&&i.Forbids!=stopForbids){hold=Hold.None;Publish(snapshot with{PumpStopped=false});}
        if(i.Quiet)
        {
            if(held||Volatile.Read(ref port) is not null) GoQuiet(i.Quiets);   // defensive: nothing stays held or open through a suspend
            if(reading is not null) Answer("the PC is going to sleep");
            Complete();
            return IdleWait;
        }
        if(held&&(i.Forbidden is not null||i.Waiters is not null))
        {
            HandBack(i.Forbidden??HubReasons.NoLighting,i.Forbidden is null?null:i.Detail);
            Complete();
            return quieted?IdleWait:Wait(i);
        }
        Complete();
        if(!held&&i.Recheck) Recount();
        if(reading is not null) Reading();
        if(quieted) return IdleWait;                                       // a Quiet during the reading: no take during suspend
        if(!held&&i.Forbidden is null&&hold==Hold.None&&RetryDue()){if(!skipped||time.GetElapsedTime(skippedAt)>=TakeAgain) Take();}
        else skipped=false;                                                // a take refused for a busy lock is a new take once anything else came between
        if(held&&!quieted) Drive(i.Posted);
        if(quieted) return IdleWait;
        Settle(i);
        return Wait(i);
    }

    // Held: the next reading or colour frame. Not held: a reading being tried again, or the next take (after a busy one TakeAgain, else at the
    // retry time). IdleWait only when nothing is due.
    TimeSpan Wait(Inputs i)
    {
        var now=time.GetTimestamp();
        if(held)
        {
            if(pauseFrom!=0) return SoftwarePause-time.GetElapsedTime(pauseFrom,now);
            var next=ReadingsEvery-time.GetElapsedTime(readAt,now);
            return ColourIn(now) is TimeSpan c?Min(next,c):next;
        }
        TimeSpan? w=null;
        if(reading is long at) w=Min(BusyAgain,ReadingLimit-time.GetElapsedTime(at,now));
        if(i.Forbidden is null&&hold==Hold.None)
            w=Min(w??TimeSpan.MaxValue,!RetryDue()?retryWait-time.GetElapsedTime(armedAt,now):skipped?TakeAgain-time.GetElapsedTime(skippedAt,now):BusyAgain);
        return w??IdleWait;
    }

    // After the wake's end letGo/waking stays only while a take is about to happen (a core that has held the hub, allowed, with no hold; ruling R20).
    // Otherwise the snapshot shows the state the core is in: its hold's let-go, off for a core that never held the hub, or the forbid.
    void Settle(Inputs i)
    {
        if(held||snapshot.State!=HubStates.LetGo||snapshot.Reason!=HubReasons.Waking) return;
        if(hold!=Hold.None) Publish(snapshot with{Reason=holdReason,Detail=holdDetail,PumpStopped=hold==Hold.PumpStopped});
        else if(!everHeld) Publish(HubSnapshot.Idle with{Parts=snapshot.Parts,Read=snapshot.Read});
        else if(i.Forbidden is not null) Publish(snapshot with{Reason=i.Forbidden,Detail=i.Detail});
    }

    static TimeSpan Min(TimeSpan a,TimeSpan b)=>a<b?a:b;
    bool RetryDue()=>!armed||time.GetElapsedTime(armedAt)>=retryWait;

    // ---- taking the hub

    void Take()
    {
        if(!skipped) Log("hub: take started");
        skipped=false;
        int n;
        try{n=transport.Count();}
        catch(Exception){Failed();return;}
        Counted(n);
        if(n!=1){HoldOn(n==0?Hold.NoHub:Hold.TwoHubs);return;}
        if(corsairLock.Problem is string problem){Lost(problem);return;}
        if(!Opened()){Failed();return;}
        built=null;first=null;takeT=null;pauseFrom=0;resume=Then.None;
        Took(Run(TransactionLimit,TakeUnit),false);
    }

    // again: the outcome of the take's second unit, after a pause; a busy lock there waits another pause, the hub stays held.
    void Took(Outcome r,bool again)
    {
        switch(r)
        {
            case Outcome.Done when built!.Blocker is not null:
                parts=built.Parts;
                Publish(snapshot with{Parts=parts});
                HandBack(built.Blocker,built.Detail,"hub unsupported: "+Phrase(built.Blocker,built.Detail));
                hold=Hold.Unsupported;holdReason=built.Blocker;holdDetail=built.Detail;missing=false;
                break;
            case Outcome.Done: Taken();break;
            case Outcome.Paused: resume=Then.Take;break;
            case Outcome.Busy when again: resume=Then.Take;pauseFrom=time.GetTimestamp();break;
            case Outcome.Busy: ClosePort();held=false;skipped=true;skippedAt=time.GetTimestamp();break;   // a skipped round: its port is closed again
            case Outcome.FallsBack: FellBack();break;
            case Outcome.NoLock: Lost(why);break;
            case Outcome.Quiet: GoQuiet(Volatile.Read(ref quiets));break;
            default: Failed(what);break;
        }
    }

    // One locked unit, from the firmware read to the first speeds reply, when the hub was still held. The core holds the hub from the moment
    // software mode is sent. Fix wave 2, B: a hub that was in its own mode gets the rest (TakeRest) in a second unit after a pause.
    void TakeUnit()
    {
        var firmware=HubParser.Firmware(Send(HubFrame.Firmware()))??throw new Fault("firmware: unreadable reply");
        Log("hub firmware "+firmware);
        Send(HubFrame.Close(HubEndpoint.Temperatures));
        var reply=Send(HubFrame.Open(HubEndpoint.Temperatures));
        var probe=HubParser.Status(reply);
        if(probe==HubStatus.Other) throw new Fault(Answered(reply));
        Send(HubFrame.Close(HubEndpoint.Temperatures));
        Log(probe==HubStatus.WrongMode?"hub was in its own mode":"hub was still held");
        held=true;everHeld=true;
        Mode(HubFrame.SoftwareMode());
        softwareAt=time.GetTimestamp();
        Log("hub: software mode entered");
        if(probe==HubStatus.WrongMode) Pause();
        TakeRest();
    }

    // The device list, temperatures and speeds; what a pause interrupted is done again, what came before it is kept.
    void TakeRest()
    {
        if(built is null)
        {
            HubList? list=null;
            Again(()=>list=HubParser.Devices(Transaction(HubEndpoint.Devices)));
            built=HubParts.Build(list??throw new Fault("read device list: unreadable data"));
            if(built.Blocker is not null) return;
            foreach(var p in built.Parts) Log($"hub part: channel {p.Channel}, type 0x{p.Type:X2}, model 0x{p.Model:X2}, {p.Leds} LEDs");
        }
        if(built.Blocker is not null) return;
        if(takeT is null) Again(()=>takeT=Values(HubEndpoint.Temperatures));
        HubValue[]? s=null;
        Again(()=>s=Values(HubEndpoint.Speeds));
        first=HubParts.Readings(built.Parts,takeT!,s!);
    }

    void Taken()
    {
        parts=built!.Parts;
        armed=false;nextWait=RetryMin;
        readAt=time.GetTimestamp();
        coloured=false;sentDark=false;sentZones=null;good=null;seenPost=null;firstFrame=false;sizeLogged=false;
        seen.Clear();
        Note(first!);
        stalled=HubChecks.PumpStalled(first!)?1:0;
        Publish(new HubSnapshot(HubStates.Driving,null,null,parts,first,readAt,softwareAt,snapshot.Read));
        Log(takenOnce?"hub taken again":"hub taken");
        takenOnce=true;
    }

    // ---- while held

    // Fix wave 2, B: nothing during a pause after software mode; then the rest of the take or the interrupted round first.
    void Drive(Rgb[]? posted)
    {
        if(pauseFrom!=0){if(time.GetElapsedTime(pauseFrom)<SoftwarePause) return;pauseFrom=0;}
        if(resume==Then.Take){resume=Then.None;Took(Run(TransactionLimit,TakeRest),true);if(!held||pauseFrom!=0) return;}
        if(resume==Then.Round||time.GetElapsedTime(readAt)>=ReadingsEvery) Round();
        if(held&&pauseFrom==0) Colours(posted);
    }

    // Temperatures, then speeds, each its own unit. A round skipped for a busy lock waits for the next one, 3 s on. A round a pause interrupted
    // goes on after it: temperatures already read are kept, and its 3 s still count from its start.
    void Round()
    {
        if(resume!=Then.Round){readAt=time.GetTimestamp();roundT=null;}
        resume=Then.None;
        HubValue[]? s=null;
        if((roundT is null&&!Held(()=>roundT=Values(HubEndpoint.Temperatures)))||!Held(()=>s=Values(HubEndpoint.Speeds)))
        {
            if(pauseFrom!=0) resume=Then.Round;
            return;
        }
        var readings=HubParts.Readings(parts,roundT!,s!);
        roundT=null;
        Note(readings);
        Publish(snapshot with{Readings=readings,ReadingsAt=time.GetTimestamp()});
        stalled=HubChecks.PumpStalled(readings)?stalled+1:0;
        if(stalled<HubChecks.StalledRounds) return;
        HandBack(null,null,$"hub: the pump read 0 RPM for {HubChecks.StalledRounds} readings",true);
        hold=Hold.PumpStopped;holdReason=null;holdDetail=null;
        lock(sync)stopForbids=forbids;                                     // only a forbid after the stop counts (fix round 1, M2)
    }

    // Dark sends black and needs no posted frame; otherwise nothing is written before the first frame of the right size. A hub whose parts have no
    // LEDs gets no colour transaction at all.
    void Colours(Rgb[]? posted)
    {
        int lit=parts.Count(p=>p.Leds>0);
        if(lit==0) return;
        if(!ReferenceEquals(posted,seenPost)&&posted is not null)
        {
            if(posted.Length==lit){good=posted;sizeLogged=false;}
            else if(!sizeLogged){sizeLogged=true;Log($"hub: a frame of {posted.Length} colours was ignored (the hub has {lit} lit parts)");}
        }
        seenPost=posted;
        var now=time.GetTimestamp();
        if(ColourIn(now) is not TimeSpan due||due>TimeSpan.Zero) return;
        var zones=wantDark?null:good;
        var leds=Expand(zones);
        colourAt=now;coloured=true;
        if(!Held(()=>Paint(leds))){if(pauseFrom!=0) coloured=false;return;}   // after a pause the frame is due at once
        sentDark=wantDark;sentZones=zones;
        if(!firstFrame){firstFrame=true;Log("hub: first frame sent");}
    }

    // null: no colour is wanted; else how long until one is due: FrameGap after the last write when the colours or dark changed, KeepAlive when not.
    TimeSpan? ColourIn(long now)
    {
        if(HubParts.Leds(parts)==0||!wantDark&&good is null) return null;
        if(!coloured) return TimeSpan.Zero;
        bool changed=wantDark!=sentDark||!wantDark&&!good.AsSpan().SequenceEqual(sentZones);
        return (changed?FrameGap:KeepAlive)-time.GetElapsedTime(colourAt,now);
    }

    // Each zone's colour repeated for its part's LEDs, parts in channel order; no zones: black.
    Rgb[] Expand(Rgb[]? zones)
    {
        var leds=new Rgb[HubParts.Leds(parts)];
        int at=0,z=0;
        foreach(var p in parts)
        {
            if(p.Leds==0) continue;
            leds.AsSpan(at,p.Leds).Fill(zones is null?default:zones[z]);
            at+=p.Leds;z++;
        }
        return leds;
    }

    // One transaction while held, in its own unit. False when it did not complete; a failure has then let go.
    bool Held(Action transaction)
    {
        switch(Run(TransactionLimit,()=>Again(transaction)))
        {
            case Outcome.Done: return true;
            case Outcome.Busy: return false;
            case Outcome.Paused: return false;
            case Outcome.FallsBack: FellBack();return false;
            case Outcome.NoLock: Lost(why);return false;
            case Outcome.Quiet: GoQuiet(Volatile.Read(ref quiets));return false;
            default: Failed(what);return false;
        }
    }

    void FellBack(){HandBack(HubReasons.FallsBack,null,$"hub: {what} (let go: {Phrase(HubReasons.FallsBack,null)})");Arm();}

    // While held a 03 reply means the hub fell back to its own mode (open colours' 03 excepted, fix wave 2, A): software mode again at once, at
    // most ReEntries times in ReEntryWindow; the next one lets go. The window is fixed, from its first re-entry, and the count persists across a
    // fallsBack let-go and Resume, so a wrong mode soon after the retake lets go at once (ruling R22). Fix wave 2, B: software mode ends the unit,
    // and the interrupted transaction is repeated SoftwarePause later in a unit of its own.
    void Again(Action transaction)
    {
        try{transaction();}
        catch(WrongMode e){ReEnter(e.What);}
    }

    void ReEnter(string failed)
    {
        var now=time.GetTimestamp();
        if(reEntries==0||time.GetElapsedTime(reEntryFrom,now)>ReEntryWindow){reEntries=0;reEntryFrom=now;}
        if(reEntries>=ReEntries) throw new FallsBack(failed);
        reEntries++;
        Log($"hub: {failed} (entering software mode again, {reEntries} of {ReEntries})");
        Mode(HubFrame.SoftwareMode());
        Pause();
    }

    // Ends the unit after software mode; Drive sends nothing more until SoftwarePause has passed.
    void Pause(){pauseFrom=time.GetTimestamp();throw new Paused();}

    // ---- letting go

    // Hardware mode once under the lock, then the port is closed either way. line: the event's log line, by default "hub: let go (<why>)".
    void HandBack(string? reason,string? detail,string? line=null,bool pumpStopped=false)
    {
        Log(line??$"hub: let go ({Phrase(reason,detail)})");
        var r=Run(TransactionLimit,()=>Mode(HubFrame.HardwareMode()));
        if(r==Outcome.Quiet){GoQuiet(Volatile.Read(ref quiets),pumpStopped);return;}
        Log(r==Outcome.Done?"hub: hardware mode sent":what is not null?$"hub: {what} (hardware mode not sent)":"hub: hardware mode not sent: "+why);
        Release();
        Publish(snapshot with{State=HubStates.LetGo,Reason=reason,Detail=detail,SoftwareAt=0,PumpStopped=pumpStopped});
    }

    // The hub stopped answering (or answered too slowly, or something threw): no hardware mode, the port closed, the retry armed. failed: the
    // exchange and what it answered, when the failure was at one.
    void Failed(string? failed=null)
    {
        Release();
        Publish(snapshot with{State=HubStates.LetGo,Reason=HubReasons.Silent,Detail=null,SoftwareAt=0});
        Log(failed is null?$"hub: let go ({SilentText})":$"hub: {failed} (let go: {SilentText})");
        Arm();
    }

    // Not held any more: the port closed, and no pause or repeat left over.
    void Release(){ClosePort();held=false;softwareAt=0;pauseFrom=0;resume=Then.None;}

    void Arm(){armed=true;armedAt=time.GetTimestamp();retryWait=nextWait;nextWait=nextWait+nextWait>RetryMax?RetryMax:nextWait+nextWait;}

    // The lock cannot be had at all: nothing can be sent, so the port is only closed, and the core does not try again in this run.
    void Lost(string problem)
    {
        Release();hold=Hold.NoLock;holdReason=HubReasons.NoLock;holdDetail=null;
        Publish(snapshot with{State=HubStates.LetGo,Reason=HubReasons.NoLock,Detail=null,SoftwareAt=0});
        Log($"hub: let go ({Phrase(HubReasons.NoLock,null)}: {problem})");
    }

    void HoldOn(Hold h)
    {
        hold=h;
        var reason=h==Hold.NoHub?HubReasons.NoHub:HubReasons.TwoHubs;
        holdReason=reason;holdDetail=null;
        if(snapshot.State==HubStates.LetGo&&snapshot.Reason==reason) return;
        Publish(snapshot with{State=HubStates.LetGo,Reason=reason,Detail=null,SoftwareAt=0});
        Log($"hub: let go ({Phrase(reason,null)})");
    }

    // Suspend: nothing more is sent, not even hardware mode; the port is closed. A stopped pump stays in the snapshot (pumpStopped: the hand-back
    // that Quiet interrupted was the stopped-pump one). Called during a step, it ends that step (quieted).
    void GoQuiet(int seq,bool pumpStopped=false)
    {
        seenQuiets=seq;quieted=true;
        Release();
        Publish(snapshot with{State=HubStates.LetGo,Reason=HubReasons.Waking,Detail=null,SoftwareAt=0,PumpStopped=pumpStopped||snapshot.PumpStopped});
        Log("hub: quiet until the wake's end");
    }

    // Resume: every hold that waits for the hub to change, and the retry time, end; the next allowed step takes from the top.
    void Unhold(){if(hold is Hold.NoHub or Hold.TwoHubs or Hold.Unsupported) hold=Hold.None;armed=false;nextWait=RetryMin;}

    void Recount()
    {
        if(hold is not(Hold.NoHub or Hold.TwoHubs or Hold.Unsupported)) return;
        int n;
        try{n=transport.Count();}
        catch(Exception){return;}
        Counted(n);
        if(hold==Hold.Unsupported){if(n==0) missing=true;else if(missing) hold=Hold.None;return;}
        if(n==1) hold=Hold.None;
        else HoldOn(n==0?Hold.NoHub:Hold.TwoHubs);
    }

    // After the run's first Count(), a change between none, one and more than one hub is logged once.
    void Counted(int n)
    {
        int kind=n<=0?0:n==1?1:2;
        if(lastCount>=0&&kind!=lastCount) Log(kind switch{0=>"hub disappeared",1=>"hub appeared",_=>"more than one hub listed"});
        lastCount=kind;
    }

    // ---- the dialog's reading

    // Only while not held: firmware, the device list, temperatures and speeds in one unit bounded by ReadingLimit, and no mode command. A busy lock
    // skips the round; it is tried again inside ReadingLimit of the request.
    void Reading()
    {
        if(held){Answer("DeadWare is driving the hub");return;}
        if(time.GetElapsedTime(reading!.Value)>=ReadingLimit){Answer("the Corsair lock stayed busy");return;}
        int n;
        try{n=transport.Count();}
        catch(Exception){Answer(SilentText);return;}
        Counted(n);
        if(n!=1){Read(n==0?HubReasons.NoHub:HubReasons.TwoHubs,null,null);return;}
        if(corsairLock.Problem is string problem){Answer(problem);return;}
        if(!Opened()){Answer("the hub could not be opened");return;}
        built=null;first=null;
        var r=Run(ReadingLimit-time.GetElapsedTime(reading.Value),ReadingUnit);   // 2 s from the request, busy tries included (fix round 1, M1)
        ClosePort();
        if(r==Outcome.Busy) return;
        if(r==Outcome.Quiet){GoQuiet(Volatile.Read(ref quiets));Answer("the PC is going to sleep");return;}
        if(r!=Outcome.Done){Answer(why,what);return;}
        Publish(snapshot with{Parts=built!.Parts});
        Read(built.Blocker,built.Detail,first);
    }

    void ReadingUnit()
    {
        if(HubParser.Firmware(Send(HubFrame.Firmware())) is null) throw new Fault("firmware: unreadable reply");
        built=HubParts.Build(HubParser.Devices(Transaction(HubEndpoint.Devices))??throw new Fault("read device list: unreadable data"));
        if(built.Blocker is not null) return;
        first=HubParts.Readings(built.Parts,Values(HubEndpoint.Temperatures),Values(HubEndpoint.Speeds));
    }

    void Read(string? blocker,string? detail,HubReadings? readings)
    {
        reading=null;
        Publish(snapshot with{Read=new HubReading(true,time.GetUtcNow(),readings,blocker,detail)});
        Log("hub reading: "+(blocker is not null?Phrase(blocker,detail):ReadingText(readings!)));
    }

    void Answer(string because,string? failed=null)
    {
        reading=null;
        Publish(snapshot with{Read=new HubReading(false,time.GetUtcNow())});
        Log(failed is null?$"hub reading: could not read ({because})":$"hub reading: {failed} (could not read: {because})");
    }

    static string ReadingText(HubReadings r)
    {
        var pump=r.Parts.Where(p=>p.Kind==HubPartKinds.Pump).OrderBy(p=>p.Channel).FirstOrDefault();
        var fans=r.Parts.Where(p=>p.Kind==HubPartKinds.Fan).OrderBy(p=>p.Channel).Select(p=>p.Rpm);
        return $"coolant {HubTexts.Celsius(pump?.Celsius)}, pump {HubTexts.Rpm(pump?.Rpm)}, fans {HubTexts.Fans(fans)}";
    }

    // ---- locked units and exchanges

    // Every way out of a unit is an outcome; its log lines are written after the lock is let go.
    Outcome Run(TimeSpan limit,Action body)
    {
        deferred=[];what=null;sending=null;
        try{return Unit(limit,body);}
        catch(Quieted){return Outcome.Quiet;}
        catch(Paused){return Outcome.Paused;}
        catch(FallsBack e){what=e.What;return Outcome.FallsBack;}
        catch(WrongMode e){why="nobody holds the hub";what=e.What;return Outcome.OwnMode;}
        catch(Slow e){why=SlowText;what=e.What;return Outcome.Failed;}
        catch(HubError e){why=SilentText;what=e.What;return Outcome.Failed;}
        catch(Exception){why=SilentText;what=sending is null?null:Sending()+": failed";return Outcome.Failed;}
        finally{var lines=deferred;deferred=null;foreach(var line in lines) log.Write(line);}
    }

    // The locked unit: Exit, and BusySince back to 0, on every way out, an exception included.
    Outcome Unit(TimeSpan limit,Action body)
    {
        if(!corsairLock.Enter(LockLimitMs)) return Refused();
        try
        {
            Volatile.Write(ref busySince,Stopwatch.GetTimestamp());
            if(spell){spell=false;Log("Corsair lock free again");}
            unitFrom=time.GetTimestamp();unitLimit=limit;
            body();
            return Outcome.Done;
        }
        finally{Volatile.Write(ref busySince,0);corsairLock.Exit();}
    }

    // Enter false: busy (a skipped round, logged once per spell), or the lock cannot be had at all.
    Outcome Refused()
    {
        if(corsairLock.Problem is string problem){why=problem;return Outcome.NoLock;}
        if(!spell){spell=true;Log("Corsair lock busy");}
        why=BusyText;
        return Outcome.Busy;
    }

    // One exchange: what waits is read and dropped first (at most MaxSkipped, inside the unit's time), the frame is written, then replies are read
    // until one echoes the opcode; a Read() reply must also carry the endpoint's data type with status 00 (03 there is wrong mode, any other status
    // a failure). Up to MaxSkipped other reports are skipped.
    byte[] Send(HubFrame frame,HubEndpoint? data=null)
    {
        var p=Volatile.Read(ref port)??throw new ObjectDisposedException(nameof(IHubPort));
        sending=frame;sendingData=data;
        for(int n=0;n<MaxSkipped;n++){var waiting=p.Read(DrainLimitMs);Within();if(waiting is null) break;}
        if(Volatile.Read(ref quiets)!=seenQuiets) throw new Quieted();
        p.Write(frame);
        for(int others=0;;others++)
        {
            var reply=p.Read(ReplyLimitMs);
            Within();
            if(reply is null) throw new Fault(Sending()+": no reply");
            if(HubParser.Echoes(reply,frame.Opcode)&&(data is not HubEndpoint ep||IsData(reply,ep))) return reply;
            if(others>=MaxSkipped) throw new Fault(Sending()+": no matching reply");
        }
    }

    bool IsData(byte[] reply,HubEndpoint ep)=>HubParser.Status(reply) switch
    {
        HubStatus.WrongMode=>throw new WrongMode(Answered(reply)),
        HubStatus.Other=>throw new Fault(Answered(reply)),
        _=>HubParser.IsData(reply,ep),
    };

    void Within(){if(time.GetElapsedTime(unitFrom)>unitLimit) throw new Slow(Sending()+": too slow");}

    // close, open, read, close of one endpoint. A close's status is not looked at. Fix wave 2, B: a read answering 03 closes its endpoint in the
    // same unit before the re-entry, so no transaction is split across units.
    byte[] Transaction(HubEndpoint ep)
    {
        Send(HubFrame.Close(ep));
        Ok(Send(HubFrame.Open(ep)));
        byte[] data;
        try{data=Send(HubFrame.Read(),ep);}
        catch(WrongMode){Send(HubFrame.Close(ep));throw;}
        Send(HubFrame.Close(ep));
        return data;
    }

    HubValue[] Values(HubEndpoint ep)=>HubParser.Values(Transaction(ep))??throw new Fault($"read {Endpoint((byte)ep)}: unreadable data");

    // close colours, open colours, the chunks, close colours. Fix wave 2, A: open colours' status is not a mode signal (the real hub answers 03
    // there while held): 00 and 03 go on, anything else is a failure; wrong mode is judged on each chunk's reply, and a chunk answering 03 closes
    // the colour endpoint in the same unit before the re-entry.
    void Paint(Rgb[] leds)
    {
        Send(HubFrame.CloseColours());
        var opened=Send(HubFrame.OpenColours());
        if(HubParser.Status(opened)==HubStatus.Other) throw new Fault(Answered(opened));
        try{foreach(var f in HubFrame.Colours(leds)) Ok(Send(f));}
        catch(WrongMode){Send(HubFrame.CloseColours());throw;}
        Send(HubFrame.CloseColours());
    }

    void Mode(HubFrame frame){var reply=Send(frame);if(HubParser.Status(reply)!=HubStatus.Ok) throw new Fault(Answered(reply));}

    // A reply that must say 00: 03 is wrong mode, anything else a failure.
    void Ok(byte[] reply)
    {
        switch(HubParser.Status(reply))
        {
            case HubStatus.Ok: return;
            case HubStatus.WrongMode: throw new WrongMode(Answered(reply));
            default: throw new Fault(Answered(reply));
        }
    }

    // Fix wave 2, C: the exchange under way and its reply's status byte, "open temperatures answered 03"; never any byte past the status.
    string Answered(byte[] reply)=>reply.Length<4?Sending()+": a short reply":$"{Sending()} answered {reply[3]:X2}";

    string Sending()=>sending is null?"no exchange":Name(sending,sendingData);

    // The exchange a frame is, by its command bytes (from [3]).
    static string Name(HubFrame frame,HubEndpoint? data)
    {
        var b=frame.Bytes.Span;
        return b[3] switch
        {
            0x02=>"firmware",
            0x01=>b[6]==0x02?"software mode":"hardware mode",
            0x05=>"close "+Endpoint(b[6]),
            0x0D=>"open "+Endpoint(b[5]),
            0x08=>data is HubEndpoint ep?"read "+Endpoint((byte)ep):"read",
            _=>"colour write",
        };
    }

    static string Endpoint(byte ep)=>ep switch{0x36=>"device list",0x21=>"temperatures",0x17=>"speeds",_=>"colours"};

    // ---- small parts

    bool Opened()
    {
        ClosePort();
        try{Volatile.Write(ref port,transport.Open());return true;}
        catch(Exception){return false;}
    }

    void ClosePort(){var p=Interlocked.Exchange(ref port,null);try{p?.Dispose();}catch(Exception){}}

    // A part whose reading appears or disappears is logged once; values never.
    void Note(HubReadings r)
    {
        for(int k=0;k<r.Parts.Count&&k<parts.Count;k++)
        {
            var p=r.Parts[k];
            var was=seen.TryGetValue(p.Channel,out var s)?s:(Rpm:true,Celsius:true);
            bool rpm=p.Rpm is not null,celsius=!parts[k].Coolant||p.Celsius is not null;
            if(rpm!=was.Rpm) Log(rpm?$"hub: channel {p.Channel} has a speed reading again":$"hub: channel {p.Channel} has no speed reading");
            if(celsius!=was.Celsius) Log(celsius?$"hub: channel {p.Channel} has a coolant reading again":$"hub: channel {p.Channel} has no coolant reading");
            seen[p.Channel]=(rpm,celsius);
        }
    }

    static string Phrase(string? reason,string? detail)=>HubTexts.Reason(reason,detail)??reason??"no reason";
    void Log(string line){if(deferred is not null) deferred.Add(line);else log.Write(line);}
    void Publish(HubSnapshot s){snapshot=s;published=true;}
    void Complete(){var list=stepWaiters;stepWaiters=null;if(list is not null) foreach(var t in list) t.TrySetResult();}
    void Raise(){try{Changed?.Invoke();}catch(Exception e){log.Write($"hub: a change handler failed ({e.GetType().Name})");}}

    // The last guard, for an exception outside every unit (a fault of this code): the step's let-go tasks are completed (they cannot throw) and it is
    // counted as any failure. Only the exception's type is logged, never its message, which can carry a device path.
    TimeSpan Broken(Exception e)
    {
        Complete();
        try{deferred=null;log.Write($"hub: a step failed ({e.GetType().Name})");Failed();}
        catch(Exception){}
        return IdleWait;
    }
}
