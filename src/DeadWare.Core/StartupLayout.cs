using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
namespace DeadWare.Core;
public sealed class DetachedWindowState(IEnumerable<string>? remembered)
{
    private readonly List<string> open=Normalize(remembered);
    public bool Ending {get;private set;}
    public IReadOnlyList<string> Remembered=>open.ToArray();
    public static bool IsAllowed(string? key)=>key is "cpu" or "cputemp" or "gpu" or "gputemp" or "network" or "widget" or "batteryhistory";
    public static List<string> Normalize(IEnumerable<string>? keys)=>(keys??[]).Where(IsAllowed).Distinct(StringComparer.Ordinal).ToList();
    public void Open(string key) {if(!Ending&&IsAllowed(key)&&!open.Contains(key))open.Add(key);}
    public void Close(string key) {if(!Ending)open.Remove(key);}
    public void BeginShutdown()=>Ending=true;
}
/// <summary>Atomic, revision-ordered writes. A final synchronous checkpoint wins over older queued work.</summary>
public sealed class SettingsSnapshotWriter(string path)
{
    private readonly object gate=new();
    private long savedRevision=-1;
    public void Save(long revision,string text)
    {
        lock(gate)
        {
            if(revision<=savedRevision)return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {File.WriteAllText(temporary,text);File.Move(temporary,path,true);savedRevision=revision;}
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
public static class StartupTaskPolicy
{
    public const string Marker="DeadWare.Startup.v1";
    private static readonly XNamespace Ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
    public static string TaskName(string sid)
    {
        if(!Regex.IsMatch(sid,@"^S-1-(\d+-)+\d+$",RegexOptions.CultureInvariant))throw new ArgumentException("A Windows user SID is required.");
        return "DeadWare.Startup."+sid;
    }
    public static void RequireSameUser(string expected,string current)
    {
        if(!string.Equals(expected,current,StringComparison.Ordinal))
            throw new InvalidOperationException("Windows used a different administrator account. Sign in with the original user's administrator account; no startup task was changed.");
    }
    public static string CreateXml(string sid,string executable)
    {
        _=TaskName(sid);
        if(!Path.IsPathFullyQualified(executable)||!string.Equals(Path.GetExtension(executable),".exe",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("An absolute executable path is required.");
        XElement E(string name,object value)=>new(Ns+name,value);
        return new XDocument(new XElement(Ns+"Task",new XAttribute("version","1.2"),
            E("RegistrationInfo",new[]{E("Source",Marker),E("Description","DeadWare administrator startup for this Windows user.")}),
            E("Triggers",E("LogonTrigger",new[]{E("Enabled","true"),E("ExecutionTimeLimit","PT0S"),E("UserId",sid),E("Delay","PT10S")})),
            E("Principals",new XElement(Ns+"Principal",new XAttribute("id","CurrentUser"),E("UserId",sid),E("LogonType","InteractiveToken"),E("RunLevel","HighestAvailable"))),
            E("Settings",new[]{E("MultipleInstancesPolicy","IgnoreNew"),E("DisallowStartIfOnBatteries","false"),E("StopIfGoingOnBatteries","false"),E("AllowHardTerminate","false"),E("StartWhenAvailable","true"),E("RunOnlyIfNetworkAvailable","false"),E("AllowStartOnDemand","true"),E("Enabled","true"),E("Hidden","false"),E("RunOnlyIfIdle","false"),E("WakeToRun","false"),E("ExecutionTimeLimit","PT0S")}),
            new XElement(Ns+"Actions",new XAttribute("Context","CurrentUser"),E("Exec",new[]{E("Command",executable),E("Arguments","--startup"),E("WorkingDirectory",Path.GetDirectoryName(executable)!)})))).ToString();
    }
    private static XElement? Parse(string xml)
    {
        try
        {
            using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
            var root=XDocument.Load(reader).Root;return root?.Name==Ns+"Task"?root:null;
        }
        catch(XmlException) {return null;}
    }
    private static string? Value(XElement? parent,string name)=>parent?.Element(Ns+name)?.Value;
    private static bool Owned(XElement? task,string sid)
    {
        var principals=task?.Element(Ns+"Principals")?.Elements().ToArray();
        return Value(task?.Element(Ns+"RegistrationInfo"),"Source")==Marker&&principals?.Length==1&&principals[0].Name==Ns+"Principal"&&Value(principals[0],"UserId")==sid;
    }
    public static bool IsOwned(string xml,string sid)=>Owned(Parse(xml),sid);
    public static string? Executable(string xml)=>Value(Parse(xml)?.Element(Ns+"Actions")?.Element(Ns+"Exec"),"Command");
    private static bool MatchesTriggerUser(string? user,string sid,Func<string,string?>? resolveUserSid)
    {
        if(user==sid)return true;
        if(string.IsNullOrWhiteSpace(user)||resolveUserSid is null)return false;
        // Registration may canonicalize a trigger SID to DOMAIN\account. Validate its
        // Windows identity; never assume a matching display/account name implies ownership.
        try {return resolveUserSid(user)==sid;}catch(Exception) {return false;}
    }
    public static bool IsCurrent(string xml,string sid,string executable,Func<string,string?>? resolveUserSid=null)
    {
        var task=Parse(xml);if(!Owned(task,sid))return false;
        var principal=task!.Element(Ns+"Principals")!.Element(Ns+"Principal");
        var triggers=task.Element(Ns+"Triggers")?.Elements().ToArray();
        var repetition=triggers?.Length==1?triggers[0].Element(Ns+"Repetition"):null;
        var actions=task.Element(Ns+"Actions")?.Elements().ToArray();
        var settings=task.Element(Ns+"Settings");
        return Value(principal,"LogonType")=="InteractiveToken"&&Value(principal,"RunLevel")=="HighestAvailable"
            &&triggers?.Length==1&&triggers[0].Name==Ns+"LogonTrigger"&&MatchesTriggerUser(Value(triggers[0],"UserId"),sid,resolveUserSid)&&Value(triggers[0],"Enabled")!="false"
            &&Value(triggers[0],"ExecutionTimeLimit")=="PT0S"&&Value(triggers[0],"StartBoundary") is null&&Value(triggers[0],"EndBoundary") is null
            &&Value(repetition,"Interval") is null&&Value(repetition,"Duration") is null&&(Value(repetition,"StopAtDurationEnd") is null or "false" or "0")
            &&actions?.Length==1&&actions[0].Name==Ns+"Exec"&&string.Equals(Value(actions[0],"Command"),executable,StringComparison.OrdinalIgnoreCase)
            &&Value(actions[0],"Arguments")=="--startup"&&string.Equals(Value(actions[0],"WorkingDirectory"),Path.GetDirectoryName(executable),StringComparison.OrdinalIgnoreCase)
            &&Value(settings,"Enabled")!="false"&&Value(settings,"MultipleInstancesPolicy")=="IgnoreNew"&&Value(settings,"ExecutionTimeLimit")=="PT0S"
            &&Value(settings,"DisallowStartIfOnBatteries")=="false"&&Value(settings,"StopIfGoingOnBatteries")=="false"
            &&Value(settings,"RunOnlyIfIdle")!="true"&&Value(settings,"RunOnlyIfNetworkAvailable")!="true";
    }
}
