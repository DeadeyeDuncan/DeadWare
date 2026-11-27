using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
// Lighting 5, Task 12: DeadLight's lines, its folder rule and Retire DeadLight, all against fakes. Nothing here touches Windows.
public class DeadLightTests
{
    const string Folder=@"C:\Apps\DL",Electron=@"C:\Apps\DL\node_modules\electron\dist\electron.exe",OpenRgb=@"C:\Apps\DL\resources\openrgb\OpenRGB.exe";
    const string NotConfirmed="DeadWare switched off the start-up task but could not confirm which programs are DeadLight's, so it stopped none. If DeadLight is still running, quit it from its icon by the clock.";
    const string LeftRunning="DeadWare's own OpenRGB runs from DeadLight's folder, so it was left running.";
    const string DidNotStop="DeadLight did not stop within 5 seconds. Quit it from its icon by the clock.";
    static void Line(FactLine line,FactMark mark,string text)
    {
        Assert.Equal(mark,line.Mark);
        Assert.Equal(text,line.Text);
        Assert.Empty(line.Instructions??[]);
    }

    // A pretend Windows: every call is recorded; the changes (task, ending, waiting) are told apart from the reads.
    sealed class FakeSystem:IDeadLightSystem
    {
        public DeadLightTask? TaskOnDisk=new(true,"\""+Electron+"\"","\""+Folder+"\"",Folder);
        public List<RunningProcess> Running=[];
        public Dictionary<string,string> Files=new(StringComparer.OrdinalIgnoreCase){[Folder+@"\package.json"]="{\"name\":\"deadlight\",\"version\":\"0.4.0\"}"};
        public Exception? ReadFails,DisableFails,EndTaskFails;
        public bool Gone=true;
        public readonly List<string> Calls=[];
        public IReadOnlyList<string> Changes=>Calls.Where(c=>c is "DisableTask" or "EndTask"||c.StartsWith("End ")||c.StartsWith("WaitGone ")).ToList();
        public DeadLightTask? ReadTask(){Calls.Add("ReadTask");if(ReadFails!=null) throw ReadFails;return TaskOnDisk;}
        public void DisableTask(){Calls.Add("DisableTask");if(DisableFails!=null) throw DisableFails;}
        public void EndTask(){Calls.Add("EndTask");if(EndTaskFails!=null) throw EndTaskFails;}
        public IReadOnlyList<RunningProcess> Processes(){Calls.Add("Processes");return Running;}
        public string? ReadText(string path){Calls.Add("ReadText "+path);return Files.GetValueOrDefault(path);}
        public bool End(int pid,string path){Calls.Add($"End {pid} {path}");return true;}
        public bool WaitGone(IReadOnlyList<int> pids,TimeSpan limit){Calls.Add($"WaitGone {string.Join(",",pids)} {limit}");return Gone;}
    }

    [Fact] public void The_texts_are_the_designs()
    {
        Assert.Equal("DeadLight",DeadLightLines.Heading);
        Assert.Equal("Retire DeadLight",DeadLightLines.Retire);
        Assert.Equal("Restart DeadWare as administrator to retire DeadLight.",DeadLightLines.NeedsAdmin);
        Assert.Equal("DeadLight is retired. Its folder and its settings are still on this PC; delete them yourself whenever you like.",DeadLightLines.Retired);
        Assert.Equal("Retired just now.",DeadLightLines.JustNow);
        Assert.Equal("Retire DeadLight?",DeadLightLines.DialogTitle);
        Assert.Equal("DeadWare will:",DeadLightLines.DialogLead);
        Assert.Equal("• switch off DeadLight's start-up task. It is not deleted, so you can switch it back on in Task Scheduler;",DeadLightLines.DialogTask);
        Assert.Equal("• stop DeadLight and the OpenRGB it started, if they are running.",DeadLightLines.DialogStop);
        Assert.Equal("Nothing is deleted.",DeadLightLines.DialogNothing);
        Assert.Equal(NotConfirmed,DeadLightLines.NotConfirmed);
    }
    [Fact] public void Without_a_task_or_a_process_it_is_not_found()
    {
        var none=new DeadLightFacts(false,null);
        Assert.False(DeadLightLines.Found(none));
        Assert.False(DeadLightLines.CanRetire(none));
        // A task that could not be read is no proof of absence: the section shows, with the warning in place of the task line.
        var unreadable=new DeadLightFacts(false,null,"Access is denied.");
        Assert.True(DeadLightLines.Found(unreadable));
        Assert.False(DeadLightLines.CanRetire(unreadable));
        var lines=DeadLightLines.Lines(unreadable);
        Assert.Equal(2,lines.Count);
        Line(lines[0],FactMark.Good,"DeadLight is not running.");
        Line(lines[1],FactMark.Warning,"DeadWare could not check DeadLight's start-up task: Access is denied.");
    }
    [Theory]
    [InlineData(false,false,FactMark.Good,"DeadLight is not running.",FactMark.Good,"DeadLight's start-up task is switched off.",false)]
    [InlineData(false,true,FactMark.Good,"DeadLight is not running.",FactMark.Warning,"DeadLight starts when you sign in (the Task Scheduler task \"DeadLight\").",true)]
    [InlineData(true,false,FactMark.Blocking,"DeadLight is running.",FactMark.Good,"DeadLight's start-up task is switched off.",true)]
    [InlineData(true,true,FactMark.Blocking,"DeadLight is running.",FactMark.Warning,"DeadLight starts when you sign in (the Task Scheduler task \"DeadLight\").",true)]
    public void The_lines_by_running_and_task(bool running,bool enabled,FactMark runMark,string runText,FactMark taskMark,string taskText,bool canRetire)
    {
        var facts=new DeadLightFacts(running,new(enabled,Electron,Folder,Folder));
        Assert.True(DeadLightLines.Found(facts));
        Assert.Equal(canRetire,DeadLightLines.CanRetire(facts));
        var lines=DeadLightLines.Lines(facts);
        Assert.Equal(2,lines.Count);
        Line(lines[0],runMark,runText);
        Line(lines[1],taskMark,taskText);
    }
    [Fact] public void The_task_line_is_left_out_without_a_task()
    {
        var facts=new DeadLightFacts(true,null);
        Assert.True(DeadLightLines.Found(facts));
        Assert.True(DeadLightLines.CanRetire(facts));
        Line(Assert.Single(DeadLightLines.Lines(facts)),FactMark.Blocking,"DeadLight is running.");
    }

    [Theory]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\Apps\DL\package.json","{\"name\":\"deadlight\"}",@"C:\Apps\DL")]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\Apps\DL\package.json","{\"name\":\"deadlight2\"}",null)]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\Apps\DL\package.json","{\"name\":\"DeadLight\"}",null)]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\Apps\DL\package.json","not json",null)]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\Elsewhere\package.json","{\"name\":\"deadlight\"}",null)]
    [InlineData(null,null,@"C:\Apps\DL",null,@"C:\package.json","{\"name\":\"deadlight\"}",null)]
    [InlineData("\"C:\\Apps\\DL\\node_modules\\electron\\dist\\electron.exe\"",null,null,null,@"C:\Apps\DL\package.json","{\"name\":\"deadlight\"}",@"C:\Apps\DL")]
    [InlineData(@"electron.exe","\"C:\\Apps\\DL\" --hidden",null,null,@"C:\Apps\DL\package.json","{\"name\":\"deadlight\"}",@"C:\Apps\DL")]
    [InlineData(null,null,null,@"C:\Apps\DL\node_modules\electron\dist\electron.exe",@"C:\Apps\DL\package.json","{\"name\":\"deadlight\"}",@"C:\Apps\DL")]
    [InlineData(null,null,null,@"C:\Apps\DL\electron.exe",@"C:\Apps\DL\package.json","{\"name\":\"deadlight\"}",null)]
    [InlineData(null,null,null,@"C:\node_modules\electron\dist\electron.exe",@"C:\package.json","{\"name\":\"deadlight\"}",null)]
    public void A_folder_needs_a_package_json_named_deadlight(string? command,string? arguments,string? workingDirectory,string? electron,string packageAt,string packageText,string? expected)
    {
        var read=new List<string>();
        string? ReadText(string path){read.Add(path);return string.Equals(path,packageAt,StringComparison.OrdinalIgnoreCase)?packageText:null;}
        Assert.Equal(expected,DeadLightFolder.Find([command,arguments,workingDirectory],electron is null?[]:[electron],ReadText));
        // A drive root is never asked.
        Assert.DoesNotContain(@"C:\package.json",read,StringComparer.OrdinalIgnoreCase);
    }
    [Fact] public void The_task_comes_before_a_running_electron()
    {
        var files=new Dictionary<string,string>{[@"C:\Apps\DL\package.json"]="{\"name\":\"deadlight\"}",[@"D:\Old\DL\package.json"]="{\"name\":\"deadlight\"}"};
        Assert.Equal(@"C:\Apps\DL",DeadLightFolder.Find([null,null,@"C:\Apps\DL"],[@"D:\Old\DL\node_modules\electron\dist\electron.exe"],files.GetValueOrDefault));
    }
    [Fact] public void The_two_paths_are_inside_the_folder()
    {
        Assert.Equal(@"C:\Apps\DL\node_modules\electron\dist\electron.exe",DeadLightFolder.Electron(@"C:\Apps\DL"));
        Assert.Equal(@"C:\Apps\DL\resources\openrgb\OpenRGB.exe",DeadLightFolder.OpenRgb(@"C:\Apps\DL"));
    }

    [Fact] public void Not_elevated_does_nothing()
    {
        var system=new FakeSystem{Running=[new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        Assert.Equal(new RetireResult(false,"Restart DeadWare as administrator to retire DeadLight."),DeadLightRetire.Run(system,false,null));
        Assert.Empty(system.Calls);
    }
    [Fact] public void Retire_goes_task_then_electron_then_its_OpenRGB()
    {
        // OpenRGB is listed first and electron's path is in another case: the order is still electron first, and both match.
        var system=new FakeSystem{Running=[new(12,"OpenRGB",OpenRgb),new(11,"electron",Electron.ToLowerInvariant()),new(13,"electron",Electron)]};
        Assert.Equal(new RetireResult(true,null),DeadLightRetire.Run(system,true,@"C:\Program Files\OpenRGB\OpenRGB.exe"));
        Assert.Equal(["DisableTask","EndTask",
            @"End 11 C:\Apps\DL\node_modules\electron\dist\electron.exe",@"End 13 C:\Apps\DL\node_modules\electron\dist\electron.exe",
            @"End 12 C:\Apps\DL\resources\openrgb\OpenRGB.exe","WaitGone 11,13,12 00:00:05"],system.Changes);
        // The task is read before it is changed, and the processes after it was stopped.
        Assert.Equal(["ReadTask","DisableTask","EndTask","Processes"],system.Calls.Take(4));
    }
    [Fact] public void Only_DeadLights_two_paths_are_ended()
    {
        var system=new FakeSystem{Running=[
            new(21,"electron",@"C:\Other\node_modules\electron\dist\electron.exe"),
            new(22,"OpenRGB",@"C:\Program Files\OpenRGB\OpenRGB.exe"),
            new(23,"electron",null),
            new(24,"OpenRGB",@"C:\Apps\DL\resources\openrgb\OpenRGB.exe.old"),
            new(25,"electron",@"C:\Apps\DL\electron.exe"),
            new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        Assert.Equal(new RetireResult(true,null),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["DisableTask","EndTask",$"End 11 {Electron}",$"End 12 {OpenRgb}","WaitGone 11,12 00:00:05"],system.Changes);
    }
    [Fact] public void Without_a_confirmed_folder_nothing_is_ended()
    {
        var system=new FakeSystem{Running=[new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        system.Files.Clear();
        Assert.Equal(new RetireResult(true,NotConfirmed),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["DisableTask","EndTask"],system.Changes);
    }
    [Fact] public void Without_a_task_or_a_confirmed_folder_the_line_does_not_claim_a_task_was_switched_off()
    {
        var system=new FakeSystem{TaskOnDisk=null,Running=[new(11,"electron",Electron)]};
        system.Files.Clear();
        Assert.Equal(new RetireResult(true,"DeadWare could not confirm which programs are DeadLight's, so it stopped none. If DeadLight is still running, quit it from its icon by the clock."),
            DeadLightRetire.Run(system,true,null));
        Assert.Empty(system.Changes);
    }
    [Fact] public void Without_a_task_only_DeadLights_processes_are_ended()
    {
        // The folder is confirmed from the running electron alone.
        var system=new FakeSystem{TaskOnDisk=null,Running=[new(11,"electron",Electron)]};
        Assert.Equal(new RetireResult(true,null),DeadLightRetire.Run(system,true,null));
        Assert.Equal([$"End 11 {Electron}","WaitGone 11 00:00:05"],system.Changes);
    }
    [Fact] public void Nothing_running_needs_no_wait()
    {
        var system=new FakeSystem();
        Assert.Equal(new RetireResult(true,null),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["DisableTask","EndTask"],system.Changes);
    }
    [Fact] public void A_task_that_cannot_be_switched_off_stops_there()
    {
        var system=new FakeSystem{DisableFails=new UnauthorizedAccessException("Access is denied."),Running=[new(11,"electron",Electron)]};
        Assert.Equal(new RetireResult(false,"DeadWare couldn't switch off DeadLight's start-up task: Access is denied."),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["ReadTask","DisableTask"],system.Calls);
        // Ending the running task fails the same way; a message without a full stop gets one.
        system=new FakeSystem{EndTaskFails=new InvalidOperationException("Access is denied. (0x80070005 (E_ACCESSDENIED))"),Running=[new(11,"electron",Electron)]};
        Assert.Equal(new RetireResult(false,"DeadWare couldn't switch off DeadLight's start-up task: Access is denied. (0x80070005 (E_ACCESSDENIED))."),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["ReadTask","DisableTask","EndTask"],system.Calls);
    }
    [Fact] public void A_task_that_cannot_be_read_stops_there()
    {
        var system=new FakeSystem{ReadFails=new InvalidOperationException("Windows Task Scheduler is unavailable."),Running=[new(11,"electron",Electron)]};
        Assert.Equal(new RetireResult(false,"DeadWare couldn't switch off DeadLight's start-up task: Windows Task Scheduler is unavailable."),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["ReadTask"],system.Calls);
    }
    [Fact] public void A_process_that_stays_is_reported()
    {
        var system=new FakeSystem{Gone=false,Running=[new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        Assert.Equal(new RetireResult(false,DidNotStop),DeadLightRetire.Run(system,true,null));
        Assert.Equal(["DisableTask","EndTask",$"End 11 {Electron}",$"End 12 {OpenRgb}","WaitGone 11,12 00:00:05"],system.Changes);
    }
    [Fact] public void Retire_leaves_OpenRGB_alone_when_it_is_DeadWares_own()
    {
        // DeadWare's chosen OpenRGB location is the one inside DeadLight's folder (another case): no OpenRGB.exe is ended at all.
        var system=new FakeSystem{Running=[new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        Assert.Equal(new RetireResult(true,LeftRunning),DeadLightRetire.Run(system,true,@"c:\apps\dl\RESOURCES\openrgb\openrgb.exe"));
        Assert.Equal(["DisableTask","EndTask",$"End 11 {Electron}","WaitGone 11 00:00:05"],system.Changes);
        // A DeadLight that stays: both sentences, the failure first.
        system=new FakeSystem{Gone=false,Running=[new(11,"electron",Electron),new(12,"OpenRGB",OpenRgb)]};
        Assert.Equal(new RetireResult(false,DidNotStop+" "+LeftRunning),DeadLightRetire.Run(system,true,OpenRgb));
        Assert.DoesNotContain(system.Calls,c=>c.StartsWith("End 12"));
    }
}
