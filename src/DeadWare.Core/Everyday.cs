namespace DeadWare.Core;

// A global shortcut: Modifiers are Shortcuts.Alt, Ctrl, Shift and Win added together, Key is a Windows virtual-key number.
public sealed record Shortcut(int Modifiers,int Key);
// The Everyday card's saved settings. A null Shortcut is "no shortcut", and is written as such.
public sealed class EverydaySettings{public Shortcut? Shortcut{get;set;}=Shortcuts.Default;public bool IdleOff{get;set;}public int IdleMinutes{get;set;}=10;public bool LockOff{get;set;}}
// SleepMinutes: null leaves the mouse as it is, 0 is never.
public sealed class MouseSettings{public int? SleepMinutes{get;set;}}
public static class Shortcuts
{
    public const int Alt=1,Ctrl=2,Shift=4,Win=8;
    public static readonly Shortcut Default=new(Ctrl|Alt,0x4C);
    public const string InvalidText="Hold Ctrl, Alt or the Windows key together with another key.";
    static readonly Dictionary<int,string> Keys=BuildKeys();
    static Dictionary<int,string> BuildKeys()
    {
        var k=new Dictionary<int,string>();
        for(var c='A';c<='Z';c++) k[c]=c.ToString();
        for(var c='0';c<='9';c++) k[c]=c.ToString();
        for(var i=1;i<=24;i++) k[0x6F+i]="F"+i;
        k[0x20]="Space";k[0x2D]="Insert";k[0x2E]="Delete";k[0x24]="Home";k[0x23]="End";k[0x21]="Page Up";k[0x22]="Page Down";
        k[0x25]="Left";k[0x26]="Up";k[0x27]="Right";k[0x28]="Down";k[0x13]="Pause";
        return k;
    }
    public static bool Valid(Shortcut s)=>s is not null&&Keys.ContainsKey(s.Key)&&(s.Modifiers&~(Alt|Ctrl|Shift|Win))==0&&(s.Modifiers&(Ctrl|Alt|Win))!=0;
    public static string? KeyName(int key)=>Keys.GetValueOrDefault(key);
    public static string Text(Shortcut? s)
    {
        if(s is null) return "None";
        var parts=new List<string>();
        if((s.Modifiers&Ctrl)!=0) parts.Add("Ctrl");
        if((s.Modifiers&Alt)!=0) parts.Add("Alt");
        if((s.Modifiers&Shift)!=0) parts.Add("Shift");
        if((s.Modifiers&Win)!=0) parts.Add("Win");
        parts.Add(KeyName(s.Key)??"Key "+s.Key);
        return string.Join(" + ",parts);
    }
    public static string TakenText(Shortcut s)=>$"Another program already uses {Text(s)}. Choose different keys.";
}
public static class Everydays
{
    public static readonly int[] IdleChoices=[1,2,5,10,15,30,60];
    public static bool IsDefault(EverydaySettings e)=>e.Shortcut==Shortcuts.Default&&!e.IdleOff&&e.IdleMinutes==10&&!e.LockOff;
    public static EverydaySettings Clone(EverydaySettings e)=>new(){Shortcut=e.Shortcut,IdleOff=e.IdleOff,IdleMinutes=e.IdleMinutes,LockOff=e.LockOff};
    public static string? Validate(EverydaySettings e)=>(e.Shortcut is{} s&&!Shortcuts.Valid(s))||!IdleChoices.Contains(e.IdleMinutes)?HostReplyCodes.BadEveryday:null;
}
public static class MouseSleep
{
    public static readonly int[] Choices=[0,1,2,3,5,10,15,20];
    public static bool Valid(int? minutes)=>minutes is null||Choices.Contains(minutes.Value);
}
