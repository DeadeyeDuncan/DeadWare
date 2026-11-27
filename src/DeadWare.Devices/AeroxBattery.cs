namespace DeadWare.Devices;

// Lighting 4: the Aerox 9's battery (spec "Battery"), ported from DeadLight's refreshBattery (electron/aerox-wireless.ts). The receiver is
// asked at most once per ReceiverEvery and only while the pointer moved (background traffic must not keep the mouse awake); the cable every
// CableEvery, through the driver's short-lived port. A busy driver is skipped, not queued, and counts as neither an attempt nor a failure.
// A reading older than StaleAfter, a failed or invalid read, a change of cable and Invalidate (wake) make it stale; the last value is kept
// as last known. A receiver failure spoils only a receiver reading. Not saved to disk. Called only under the host's gate, through AeroxMouse,
// which decides whether the receiver may be asked at all (the silent-mouse rule, P1).
internal sealed class AeroxBattery
{
    public static readonly TimeSpan ReceiverEvery=TimeSpan.FromSeconds(60),CableEvery=TimeSpan.FromSeconds(10),StaleAfter=TimeSpan.FromSeconds(120);
    readonly AeroxDriver driver;
    readonly TimeProvider time;
    readonly HostLog log;
    long? triedAt,readAt;
    bool? wasCable;
    bool fromCable,spoiled;
    (int? Percent,bool? Charging,bool Stale) shown=(null,null,true);
    (bool Cable,int Percent,bool Charging)? logged;                       // fix wave 2, F: the last reading logged

    public AeroxBattery(AeroxDriver driver,TimeProvider time,HostLog log){this.driver=driver;this.time=time;this.log=log;}

    public int? Percent{get;private set;}
    public bool? Charging{get;private set;}
    public bool Stale=>readAt is not long at||spoiled||time.GetElapsedTime(at)>StaleAfter;
    // The result of the receiver request this Poll made; null when it asked the receiver nothing (AeroxMouse's silence follows it).
    public AeroxResult? Asked{get;private set;}

    // True when the published reading (percent, charging, stale) changed.
    public bool Poll(bool cable,bool receiver,bool moved)
    {
        Asked=null;
        if(wasCable is bool was&&was!=cable){spoiled=true;triedAt=null;}   // plugged or unplugged: stale, and read again at once
        wasCable=cable;
        if((cable||receiver&&moved)&&(triedAt is not long t||time.GetElapsedTime(t)>=(cable?CableEvery:ReceiverEvery))) Read(cable);
        var now=(Percent,Charging,Stale);
        if(now==shown) return false;
        shown=now;
        return true;
    }

    public void Invalidate(){spoiled=true;triedAt=null;}
    public void ReceiverFailed(){if(readAt is not null&&!fromCable) spoiled=true;}

    void Read(bool cable)
    {
        var r=driver.Battery(cable,out var p,out var c);
        if(r is AeroxResult.Busy or AeroxResult.Absent) return;   // nothing was asked: tried again next turn
        if(!cable) Asked=r;
        triedAt=time.GetTimestamp();
        if(r!=AeroxResult.Sent){spoiled=true;return;}
        // Fix wave 2, F: logged only when the source, percentage or charging state changed, or after a stale spell.
        bool wasStale=Stale;
        Percent=p;Charging=c;readAt=triedAt;fromCable=cable;spoiled=false;
        if(!wasStale&&logged==(cable,p,c)) return;
        logged=(cable,p,c);
        log.Write($"battery: {(cable?"cable":"receiver")} {p}% {(c?"charging":"not charging")}");
    }
}
