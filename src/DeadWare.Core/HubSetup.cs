using System.Text.Json;
namespace DeadWare.Core;
// Lighting 5: what Settings → Lighting shows about iCUE and DeadLight, and the rules of Retire DeadLight. Pure: Windows is read in DeadWare.Services
// (WindowsHubFacts, WindowsDeadLight) and reaches these rules only as the records below or through IDeadLightSystem.
public enum FactMark{Good,Warning,Blocking}
public sealed record FactLine(FactMark Mark,string Text,IReadOnlyList<string>? Instructions=null);
public sealed record ServiceFact(string DisplayName,bool Running,bool StartsWithWindows);
public sealed record StartupEntry(string Name,bool Disabled);
// A null list: that fact could not be read, and its error says why.
public sealed record ICueFacts(bool Running,IReadOnlyList<ServiceFact>? Services,IReadOnlyList<StartupEntry>? Startup,string? ServicesError=null,string? StartupError=null);
public sealed record DeadLightTask(bool Enabled,string? Command,string? Arguments,string? WorkingDirectory);
// TaskError: the task could not be read (a missing task is a null Task, not an error).
public sealed record DeadLightFacts(bool Running,DeadLightTask? Task,string? TaskError=null);

public static class ICueLines
{
    public const string Heading="iCUE",Lead="iCUE and DeadWare can't both drive the hub. iCUE stays installed; these three things only need to be off. DeadWare shows them and never changes them for you.",NotInstalled="iCUE is not installed on this PC.";
    // Exactly the services whose programs the helper's scan blocks on.
    public static readonly string[] Services=["CorsairService","CorsairDeviceControlService","CorsairDeviceListerService"];
    const string ServicesMsc="Press Win + R, type services.msc and press Enter. Find ";
    // No trace of iCUE only counts when both lists could be read: an unreadable fact is no proof of absence, and is never good.
    public static IReadOnlyList<FactLine> Lines(ICueFacts f)
    {
        if(!f.Running&&f.Services is {Count:0}&&f.Startup is {Count:0}) return [new(FactMark.Good,NotInstalled)];
        return [ProgramLine(f.Running),ServiceLine(f),SignInLine(f)];
    }
    static FactLine ProgramLine(bool running)=>running?new(FactMark.Blocking,"iCUE is running.",["Quit it from its icon by the clock: right-click the icon and choose Quit."]):new(FactMark.Good,"iCUE is not running.");
    static FactLine ServiceLine(ICueFacts f)
    {
        if(f.Services is not {} services) return new(FactMark.Warning,Sentence.With("DeadWare could not check the Corsair services: ",f.ServicesError));
        bool running=services.Any(s=>s.Running),starts=services.Any(s=>s.StartsWithWindows);
        var steps=services.Where(s=>s.Running||s.StartsWithWindows).Select(Step).ToList();
        if(running) return new(FactMark.Blocking,starts?"The Corsair service is running, and starts with Windows.":"The Corsair service is running.",steps);
        return starts?new(FactMark.Warning,"The Corsair service starts with Windows.",steps):new(FactMark.Good,"The Corsair service is not running and does not start with Windows.");
    }
    // One instruction per service that is not good: the Stop sentence only when it runs, the Startup type sentence only when it starts with Windows.
    static string Step(ServiceFact s)=>ServicesMsc+s.DisplayName+(s.Running?", right-click it and choose Stop."+(s.StartsWithWindows?" Then double-click it and set Startup type to Manual.":""):", double-click it and set Startup type to Manual.");
    static FactLine SignInLine(ICueFacts f)
    {
        if(f.Startup is not {} startup) return new(FactMark.Warning,Sentence.With("DeadWare could not check whether iCUE starts when you sign in: ",f.StartupError));
        // A disabled entry does not start. The same name in two Run keys is one line in Task Manager's list, so one instruction.
        var steps=startup.Where(e=>!e.Disabled).Select(e=>e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Select(n=>"Open Task Manager (Ctrl + Shift + Esc), choose Startup apps, find "+n+" and choose Disable.").ToList();
        return steps.Count>0?new(FactMark.Warning,"iCUE starts when you sign in.",steps):new(FactMark.Good,"iCUE does not start when you sign in.");
    }
    // The program a service runs, as the helper's scan names its process.
    public static string ProcessName(string service)=>string.Equals(service,"CorsairService",StringComparison.OrdinalIgnoreCase)?"Corsair.Service":service;
    // The service key's Start value 2 (automatic, delayed or not). Manual, disabled, boot and system do not start with Windows here.
    public static bool StartsWithWindows(int? start)=>start==2;
    // A Run value is iCUE's by its name, or by its program's file name exactly (not a suffix of it), ignoring case.
    public static bool IsICueEntry(string valueName,string? command)
    {
        if(string.Equals(valueName,"Corsair iCUE5 Software",StringComparison.OrdinalIgnoreCase)) return true;
        if(CommandProgram(command) is not {} program) return false;
        string file=Path.GetFileName(program);
        return file.Equals("iCUE Launcher.exe",StringComparison.OrdinalIgnoreCase)||file.Equals("iCUE.exe",StringComparison.OrdinalIgnoreCase);
    }
    // The program of a command line: the quoted part when it starts with a quote, else up to the first ".exe" that ends a word, else the first word.
    static string? CommandProgram(string? command)
    {
        string c=command?.Trim()??"";
        if(c.Length==0) return null;
        if(c[0]=='"'){int close=c.IndexOf('"',1);return close<0?c[1..]:c[1..close];}
        for(int i=c.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);i>=0;i=c.IndexOf(".exe",i+1,StringComparison.OrdinalIgnoreCase))
            if(i+4==c.Length||char.IsWhiteSpace(c[i+4])) return c[..(i+4)];
        int blank=c.IndexOfAny([' ','\t']);
        return blank<0?c:c[..blank];
    }
    // Task Manager's StartupApproved value: an odd first byte is disabled; no value, or an empty one, is enabled.
    public static bool Disabled(byte[]? approved)=>approved is {Length:>0}&&(approved[0]&1)==1;
}

public static class DeadLightLines
{
    public const string Heading="DeadLight",Retire="Retire DeadLight",NeedsAdmin="Restart DeadWare as administrator to retire DeadLight.",
        Retired="DeadLight is retired. Its folder and its settings are still on this PC; delete them yourself whenever you like.",JustNow="Retired just now.",
        DialogTitle="Retire DeadLight?",DialogLead="DeadWare will:",DialogTask="• switch off DeadLight's start-up task. It is not deleted, so you can switch it back on in Task Scheduler;",
        DialogStop="• stop DeadLight and the OpenRGB it started, if they are running.",DialogNothing="Nothing is deleted.",
        NotConfirmed="DeadWare switched off the start-up task but could not confirm which programs are DeadLight's, so it stopped none. If DeadLight is still running, quit it from its icon by the clock.";
    // A task that could not be read shows the section, so its "could not check" line is seen instead of the section being hidden.
    public static bool Found(DeadLightFacts f)=>f.Running||f.Task!=null||f.TaskError!=null;
    public static bool CanRetire(DeadLightFacts f)=>f.Running||f.Task is {Enabled:true};
    public static IReadOnlyList<FactLine> Lines(DeadLightFacts f)
    {
        var lines=new List<FactLine>{f.Running?new(FactMark.Blocking,"DeadLight is running."):new(FactMark.Good,"DeadLight is not running.")};
        if(f.TaskError!=null) lines.Add(new(FactMark.Warning,Sentence.With("DeadWare could not check DeadLight's start-up task: ",f.TaskError)));
        else if(f.Task is {} task) lines.Add(task.Enabled?new(FactMark.Warning,"DeadLight starts when you sign in (the Task Scheduler task \"DeadLight\")."):new(FactMark.Good,"DeadLight's start-up task is switched off."));
        return lines;
    }
}

// DeadLight's folder is confirmed, never guessed: a folder is DeadLight's only if its package.json is JSON whose name is exactly "deadlight".
public static class DeadLightFolder
{
    const string ElectronTail=@"node_modules\electron\dist\electron.exe",OpenRgbTail=@"resources\openrgb\OpenRGB.exe";
    // Candidates: every folder from each fully qualified path in the task's texts upwards, then the folder above node_modules of each running
    // electron.exe at ...\node_modules\electron\dist\electron.exe. A drive root never is DeadLight's and is never read. The first that confirms wins.
    public static string? Find(IEnumerable<string?> taskTexts,IEnumerable<string> electronPaths,Func<string,string?> readText)
    {
        foreach(var folder in Candidates(taskTexts,electronPaths)) if(IsDeadLights(folder,readText)) return folder;
        return null;
    }
    public static string Electron(string folder)=>Path.Join(folder,ElectronTail);
    public static string OpenRgb(string folder)=>Path.Join(folder,OpenRgbTail);
    static IEnumerable<string> Candidates(IEnumerable<string?> taskTexts,IEnumerable<string> electronPaths)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var text in taskTexts)
            foreach(var path in Pieces(text).Select(Full).OfType<string>())
                for(string? folder=path;folder!=null&&!IsRoot(folder);folder=Path.GetDirectoryName(folder))
                    if(seen.Add(folder)) yield return folder;
        foreach(var exe in electronPaths)
            if(exe.EndsWith(@"\"+ElectronTail,StringComparison.OrdinalIgnoreCase)&&Full(exe) is {} full&&Up(full,4) is {} folder&&!IsRoot(folder)&&seen.Add(folder)) yield return folder;
    }
    // The path-like pieces of a command, its arguments or a working directory: quoted parts and words; an unquoted text whole as well, for a path with spaces.
    static IEnumerable<string> Pieces(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)) yield break;
        char[] blanks=[' ','\t'];
        if(!text.Contains('"')) yield return text.Trim();
        var parts=text.Split('"');
        for(int i=0;i<parts.Length;i++)
            if(i%2==1) yield return parts[i].Trim();
            else foreach(var word in parts[i].Split(blanks,StringSplitOptions.RemoveEmptyEntries)) yield return word;
    }
    static string? Full(string path)
    {
        if(path.Length==0||!Path.IsPathFullyQualified(path)) return null;
        try{return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));}
        catch(Exception e) when(e is ArgumentException or NotSupportedException or PathTooLongException){return null;}
    }
    static string? Up(string path,int levels){string? p=path;for(int i=0;i<levels&&p!=null;i++) p=Path.GetDirectoryName(p);return p;}
    static bool IsRoot(string folder)=>Path.GetDirectoryName(folder) is null;
    static bool IsDeadLights(string folder,Func<string,string?> readText)
    {
        string? text;
        // A package.json that cannot be read confirms nothing.
        try{text=readText(Path.Join(folder,"package.json"));}catch(Exception){return false;}
        if(text is null) return false;
        try
        {
            using var json=JsonDocument.Parse(text);
            return json.RootElement.ValueKind==JsonValueKind.Object&&json.RootElement.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString()=="deadlight";
        }
        catch(JsonException){return false;}
    }
    internal static bool SamePath(string? a,string b)=>a is not null&&Full(a) is {} full&&string.Equals(full,b,StringComparison.OrdinalIgnoreCase);
}

// Windows as Retire DeadLight needs it. Nothing here deletes anything.
public interface IDeadLightSystem
{
    // ReadTask: the task \DeadLight, or null when there is none; throws when it cannot be read. DisableTask switches it off (never deletes it);
    // EndTask stops its running instance; both throw on failure.
    DeadLightTask? ReadTask();void DisableTask();void EndTask();
    // Processes: the running processes with the helper's paths (electron and OpenRGB). ReadText: a file's text, or null when it cannot be read.
    IReadOnlyList<RunningProcess> Processes();string? ReadText(string path);
    // Ends that process only if its path still is `path`, checked on the handle it is ended through; false when it was not ended.
    bool End(int pid,string path);
    // True when every one of those processes is gone within the limit.
    bool WaitGone(IReadOnlyList<int> pids,TimeSpan limit);
}
public sealed record RetireResult(bool Ok,string? Line);
public static class DeadLightRetire
{
    const string DidNotStop="DeadLight did not stop within 5 seconds. Quit it from its icon by the clock.",LeftRunning="DeadWare's own OpenRGB runs from DeadLight's folder, so it was left running.";
    // Only elevated: switch off the task and stop its instance; then, only with a confirmed folder, end exactly DeadLight's electron.exe and then its
    // OpenRGB.exe by full path, and wait up to 5 s. It ends nothing by name alone, deletes nothing and sends nothing to the hub. When DeadWare's own
    // OpenRGB location (openRgbPath) is DeadLight's OpenRGB.exe, no OpenRGB.exe is ended at all.
    public static RetireResult Run(IDeadLightSystem system,bool elevated,string? openRgbPath)
    {
        if(!elevated) return new(false,DeadLightLines.NeedsAdmin);
        DeadLightTask? task;
        try
        {
            task=system.ReadTask();
            if(task!=null){system.DisableTask();system.EndTask();}
        }
        catch(Exception e){return new(false,Sentence.With("DeadWare couldn't switch off DeadLight's start-up task: ",e.Message));}
        var processes=system.Processes();
        var folder=DeadLightFolder.Find([task?.Command,task?.Arguments,task?.WorkingDirectory],processes.Select(p=>p.Path).OfType<string>(),system.ReadText);
        if(folder is null) return new(true,DeadLightLines.NotConfirmed);
        string electron=DeadLightFolder.Electron(folder),openRgb=DeadLightFolder.OpenRgb(folder);
        bool own=DeadLightFolder.SamePath(openRgbPath,openRgb);
        var asked=new List<int>();
        foreach(var p in processes) if(string.Equals(p.Path,electron,StringComparison.OrdinalIgnoreCase)){system.End(p.Id,electron);asked.Add(p.Id);}
        if(!own) foreach(var p in processes) if(string.Equals(p.Path,openRgb,StringComparison.OrdinalIgnoreCase)){system.End(p.Id,openRgb);asked.Add(p.Id);}
        // Every process asked to end is waited for, ended or not: one that could not be ended is still there, and is reported.
        bool gone=asked.Count==0||system.WaitGone(asked,TimeSpan.FromSeconds(5));
        string? line=gone?null:DidNotStop;
        if(own) line=line is null?LeftRunning:line+" "+LeftRunning;
        return new(gone,line);
    }
}

file static class Sentence
{
    // "<lead><reason>." with exactly one full stop, however the reason ends.
    public static string With(string lead,string? reason)
    {
        string r=(reason??"").Trim().TrimEnd('.').TrimEnd();
        return lead+(r.Length>0?r:"no reason was given")+".";
    }
}
