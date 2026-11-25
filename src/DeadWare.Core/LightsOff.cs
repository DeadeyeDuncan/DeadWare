namespace DeadWare.Core;
// Why the lights are off beyond the saved switch: the pure rule. The saved LightsOn is never changed by a reason; it only holds what the owner chose.
public static class LightsOff
{
    public const string Idle="idle",Locked="locked",Waking="waking";
    // Null when the switch is off or nothing applies; else the first of waking, locked, idle.
    public static string? Reason(LightingSettings s,long idleMs,bool locked,bool waking)
    {
        if(!s.LightsOn)return null;
        if(waking)return Waking;
        if(locked&&s.Everyday.LockOff)return Locked;
        if(s.Everyday.IdleOff&&idleMs>=s.Everyday.IdleMinutes*60_000L)return Idle;
        return null;
    }
    public static bool Effective(LightingSettings s,string? reason)=>s.LightsOn&&reason is null;
    // The new saved LightsOn after a shortcut press: a press that only ends an idle spell brings the lights back and leaves the switch on; any other press flips it.
    public static bool ShortcutTarget(bool lightsOn,bool idleJustEnded)=>lightsOn&&idleJustEnded||!lightsOn;
}
