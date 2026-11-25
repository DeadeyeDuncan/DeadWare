namespace DeadWare.Core;
// The Everyday card's wording and the reason line, as pure functions the window draws from.
public static class EverydayTexts
{
    public const string Heading="Everyday",Sub="ways to turn the lights off without opening this page",
        ShortcutLabel="Lights on/off shortcut",Change="Change",Cancel="Cancel",NoShortcut="No shortcut",Recording="Press the new keys…",
        ShortcutHint="Works anywhere in Windows. Same as the Lights on switch.",NoShortcutHint="No shortcut. Use the switch or the tray instead.",
        IdleLabel="Off when the PC sits unused",IdleHint="Lights come back the moment you touch the mouse or keyboard.",
        LockLabel="Off when the PC is locked",LockRow="Lights off at the lock screen",LockHint="Lights come back when you sign back in.",
        Waking="Waking up… lights return in a few seconds.",Locked="Lights are off because the PC is locked. They come back when you sign in.";
    static string Minutes(int m)=>m==1?"1 minute":m+" minutes";
    public static string Idle(int minutes)=>$"Lights are off because the PC has sat unused for {Minutes(minutes)}. They come back when you touch the mouse or keyboard.";
    // The caution line for a LightsOff reason; null when there is none.
    public static string? ReasonLine(string? reason,int idleMinutes)=>reason switch{LightsOff.Idle=>Idle(idleMinutes),LightsOff.Locked=>Locked,LightsOff.Waking=>Waking,_=>null};
    // Whether the lights are really lit: the switch is on and no reason holds them off.
    public static bool Lit(HostState state)=>LightsOff.Effective(state.Settings,state.LightsOffReason);
    // The recorded keys as a shortcut, or null with the invalid line when they do not make a valid one.
    public static Shortcut? FromKeys(bool ctrl,bool alt,bool shift,bool win,int key,out string? error)
    {
        var s=new Shortcut((alt?Shortcuts.Alt:0)|(ctrl?Shortcuts.Ctrl:0)|(shift?Shortcuts.Shift:0)|(win?Shortcuts.Win:0),key);
        if(Shortcuts.Valid(s)){error=null;return s;}
        error=Shortcuts.InvalidText;return null;
    }
}
