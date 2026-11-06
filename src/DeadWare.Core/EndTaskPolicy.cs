namespace DeadWare.Core;
/// <summary>Whether End task ends one process. After CanEnd, declaration order is the order of the refusal rules: a process counts once, under the first rule that refuses it.</summary>
public enum EndTaskVerdict {CanEnd,SystemProcess,DeadWareItself,AlreadyExited,Critical,Protected,WindowsProcess,NeedsAdministrator,WindowsDenied}
/// <summary>What End task read on one process's handle. Null is a fact that could not be read, and an unread fact never allows ending.</summary>
public sealed record EndTaskFacts(bool Exited,bool TerminateOpened,bool FactsOpened,long? CreationTicks,bool? Critical,bool? Protected,string? ImagePath,string? TokenUserSid);
/// <summary>End task's rules and copy (spec, Actions): only a process proven endable is ended. The copy holds fixed text and counts; only the question names the app.</summary>
public static class EndTaskPolicy
{
    const string QuitDeadWare="Use Quit DeadWare";
    const string RestartAsAdministrator=" Use Restart as administrator on this page.";
    /// <summary>Refusals decided before anything is opened; null when the process must be opened to decide.</summary>
    public static EndTaskVerdict? Precheck(int pid,int ownPid)=>pid is 0 or 4?EndTaskVerdict.SystemProcess:pid==ownPid?EndTaskVerdict.DeadWareItself:null;
    public static EndTaskVerdict Classify(EndTaskFacts facts,long generation,string windowsFolder,bool elevated)
    {
        var undecided=elevated?EndTaskVerdict.WindowsDenied:EndTaskVerdict.NeedsAdministrator;
        if(facts.Exited)return EndTaskVerdict.AlreadyExited;
        if(!facts.FactsOpened||facts.CreationTicks is not {} created)return undecided;
        if(created!=generation)return EndTaskVerdict.AlreadyExited;
        if(facts.Critical==true)return EndTaskVerdict.Critical;
        if(facts.Protected==true)return EndTaskVerdict.Protected;
        bool? windowsOwn=IsWindowsOwn(facts.ImagePath,facts.TokenUserSid,windowsFolder);
        if(windowsOwn==true)return EndTaskVerdict.WindowsProcess;
        return facts.TerminateOpened&&facts.Critical==false&&facts.Protected==false&&windowsOwn==false?EndTaskVerdict.CanEnd:undecided;
    }
    /// <summary>Windows' own rule: true refuses, false is settled, null cannot be decided (unknown path or Windows folder, or an unknown token user for a path inside the Windows folder).</summary>
    static bool? IsWindowsOwn(string? path,string? sid,string windowsFolder)
    {
        if(string.IsNullOrEmpty(path)||Root(windowsFolder) is null)return null;
        if(IsWindowsSvchost(path,windowsFolder))return true;
        if(!IsInsideWindows(path,windowsFolder))return false;
        return string.IsNullOrEmpty(sid)?null:IsServiceAccount(sid);
    }
    public static bool IsServiceAccount(string sid)=>
        sid.Equals("S-1-5-18",StringComparison.OrdinalIgnoreCase)||sid.Equals("S-1-5-19",StringComparison.OrdinalIgnoreCase)||sid.Equals("S-1-5-20",StringComparison.OrdinalIgnoreCase)||
        sid.StartsWith("S-1-5-90-",StringComparison.OrdinalIgnoreCase)||sid.StartsWith("S-1-5-96-",StringComparison.OrdinalIgnoreCase);
    /// <summary>Inside the Windows folder (SafeFiles.Child's containment test), the driver store excepted. Nothing is inside an empty folder.</summary>
    public static bool IsInsideWindows(string path,string windowsFolder)=>Root(windowsFolder) is {} root&&
        path.StartsWith(root+@"\",StringComparison.OrdinalIgnoreCase)&&!path.StartsWith(root+@"\System32\DriverStore\",StringComparison.OrdinalIgnoreCase);
    /// <summary>The Windows svchost.exe, directly in the Windows folder's System32 or SysWOW64.</summary>
    public static bool IsWindowsSvchost(string path,string windowsFolder)=>Root(windowsFolder) is {} root&&
        (path.Equals(root+@"\System32\svchost.exe",StringComparison.OrdinalIgnoreCase)||path.Equals(root+@"\SysWOW64\svchost.exe",StringComparison.OrdinalIgnoreCase));
    static string? Root(string windowsFolder)=>windowsFolder.TrimEnd('\\') is var root&&!string.IsNullOrWhiteSpace(root)?root:null;
    public static string Question(string appName,int pid,int canEnd,int running,bool wholeApp)=>
        !wholeApp?$"End {appName} (PID {pid})? Unsaved work in it will be lost."
        :canEnd>=running?$"End {appName} ({Count(canEnd,"process","processes")})? Unsaved work in it will be lost."
        :$"End {canEnd} of {running} {appName} processes? Unsaved work in them will be lost.";
    /// <summary>The members that will not be ended, counted by reason in rule order; null when there are none. Already exited members are left out.</summary>
    public static string? NotEnded(IEnumerable<EndTaskVerdict> verdicts)=>Reasons(verdicts) is {} reasons?reasons+".":null;
    /// <summary>The status line when no member can be ended.</summary>
    public static string Refusal(IEnumerable<EndTaskVerdict> verdicts)
    {
        var all=verdicts.ToList();
        var refused=all.Where(IsRefusal).ToList();
        if(refused.Count==0)return Result(0,0,all.Count(v=>v==EndTaskVerdict.AlreadyExited),0);
        if(refused.All(v=>v==EndTaskVerdict.DeadWareItself))return QuitDeadWare;
        string reasons=Reasons(refused)!;
        return refused.Contains(EndTaskVerdict.NeedsAdministrator)?reasons+"."+RestartAsAdministrator:reasons;
    }
    public static string Result(int ended,int notEnded,int alreadyExited,int stillRunning)=>
        $"Ended {ended} · not ended {notEnded} · already exited {alreadyExited} · still running {stillRunning}";
    static bool IsRefusal(EndTaskVerdict verdict)=>verdict is not (EndTaskVerdict.CanEnd or EndTaskVerdict.AlreadyExited);
    static string? Reasons(IEnumerable<EndTaskVerdict> verdicts)
    {
        var reasons=verdicts.Where(IsRefusal).GroupBy(v=>v).OrderBy(g=>g.Key).Select(g=>Reason(g.Key,g.Count())).ToList();
        return reasons.Count==0?null:"Not ended: "+string.Join(", ",reasons);
    }
    static string Reason(EndTaskVerdict verdict,int n)=>verdict switch
    {
        EndTaskVerdict.SystemProcess=>Count(n,"system process","system processes"),
        EndTaskVerdict.DeadWareItself=>QuitDeadWare,
        EndTaskVerdict.Critical=>Count(n,"critical process","critical processes"),
        EndTaskVerdict.Protected=>Count(n,"protected process","protected processes"),
        EndTaskVerdict.WindowsProcess=>Count(n,"Windows' own process","Windows' own processes"),
        EndTaskVerdict.NeedsAdministrator=>Count(n,"needs administrator","need administrator"),
        EndTaskVerdict.WindowsDenied=>$"{n} denied by Windows",
        _=>throw new ArgumentOutOfRangeException(nameof(verdict)),
    };
    static string Count(int n,string one,string many)=>$"{n} {(n==1?one:many)}";
}
