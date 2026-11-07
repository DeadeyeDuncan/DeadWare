using System.ComponentModel;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.App;
using Xunit;

namespace DeadWare.App.Tests;

public class ProcessActionsTests
{
    private const int Own=999;
    private const string NeedsAdministrator="Not ended: 1 needs administrator. Use Restart as administrator on this page.";

    /// <summary>The confirmation hook: records the question and summary and answers as scripted.</summary>
    private sealed class Box(bool answer)
    {
        public int Calls;
        public string? Question,Summary;
        public Action? During;
        public bool Ask(string question,string? summary){Calls++;Question=question;Summary=summary;During?.Invoke();return answer;}
    }

    private static (ProcessActions Actions,FakeProcessControl Control,Box Box) Actions(int own=Own,bool elevated=false,bool answer=true)
    {
        var control=new FakeProcessControl();var box=new Box(answer);
        return (new ProcessActions(control,box.Ask,_=>throw new InvalidOperationException("End task never reveals."),own,elevated),control,box);
    }
    /// <summary>Ends PID 8 alone, as a standard user unless <paramref name="elevated"/>, answering Yes; PID 8 opens and reads as endable unless <paramref name="configure"/> changes it.</summary>
    private static Task<string> EndWith(Action<FakeProcessControl> configure,bool elevated=false)
    {
        var (actions,control,_)=Actions(elevated:elevated);
        configure(control);
        return actions.EndAsync("a.exe",[(8,1)],false);
    }

    [Fact]
    public async Task Pid_0_4_and_DeadWare_are_refused_without_opening()
    {
        var (a,c,box)=Actions(own:Own);
        Assert.Equal("Use Quit DeadWare",await a.EndAsync("DeadWare.exe",[(Own,1)],true));
        Assert.Equal("Not ended: 1 system process",await a.EndAsync("System",[(4,1)],true));
        Assert.Equal("Not ended: 2 system processes",await a.EndAsync("System",[(0,1),(4,1)],true));
        Assert.Empty(c.Opened);
        Assert.Equal(0,box.Calls);
    }

    [Fact]
    public async Task Confirm_counts_only_endable_members_and_summarises_the_rest()
    {
        var (a,c,asked)=Actions(answer:false);
        (int Pid,long Generation)[] members=[..Enumerable.Range(100,31).Select(pid=>(pid,1L))];
        // The terminate open of the last three is denied; their facts-only read shows nothing that refuses them, so a standard user needs administrator.
        foreach(int pid in new[]{128,129,130})c.OpenError(pid,5);
        Assert.Equal("",await a.EndAsync("chrome.exe",members,true));
        Assert.Equal("End 28 of 31 chrome.exe processes? Unsaved work in them will be lost.",asked.Question);
        Assert.Equal("Not ended: 3 need administrator.",asked.Summary);
    }

    [Fact]
    public async Task No_endable_member_opens_no_box_and_points_to_restart()
    {
        var (a,c,box)=Actions();
        c.OpenError(8,5);
        c[9].Critical=true;
        string status=await a.EndAsync("a.exe",[(8,1),(9,1)],true);
        Assert.Equal(0,box.Calls);
        Assert.EndsWith("Use Restart as administrator on this page.",status);
        Assert.Equal("Not ended: 1 critical process, 1 needs administrator. Use Restart as administrator on this page.",status);
        Assert.Empty(c.Terminated);
        Assert.Equal([9],c.Closed);
        Assert.Empty(c.StillOpen);
    }

    [Fact]
    public async Task No_ends_nothing_and_closes_kept_handles()
    {
        var (a,c,box)=Actions(answer:false);
        c[10].Critical=true;
        IReadOnlyList<int>? closedDuringBox=null,openDuringBox=null;
        box.During=()=>{closedDuringBox=c.Closed;openDuringBox=c.StillOpen;};
        Assert.Equal("",await a.EndAsync("a.exe",[(8,1),(9,1),(10,1)],true));
        // The refused member's handle closes at once; the endable members' handles stay open while the box is up.
        Assert.Equal([10],closedDuringBox);
        Assert.Equal([8,9],openDuringBox);
        Assert.Empty(c.Terminated);
        Assert.Equal(0,c.Waits);
        Assert.Equal(c.Opened.Count,c.Closed.Count);
        Assert.Empty(c.StillOpen);
    }

    [Fact]
    public async Task Yes_terminates_with_one_wait_and_reports_the_aggregate()
    {
        var (a,c,box)=Actions();
        c[10].Critical=true;
        c[11].Ending=FakeEnding.ExitedBeforeBox;
        c[12].Ending=FakeEnding.StaysRunning;
        string status=await a.EndAsync("a.exe",[(8,1),(9,1),(10,1),(11,1),(12,1)],true);
        Assert.Equal("Ended 2 · not ended 1 · already exited 1 · still running 1",status);
        Assert.Equal(1,box.Calls);
        Assert.Equal([8,9,12],c.Terminated);
        Assert.Equal(1,c.Waits);
        Assert.Equal(TimeSpan.FromSeconds(2),c.WaitTimeout);
        Assert.Empty(c.StillOpen);
    }

    [Fact]
    public async Task Failed_terminate_whose_handle_signals_counts_as_already_exited()
    {
        FakeProcessControl? control=null;
        string status=await EndWith(c=>{control=c;c[8].Ending=FakeEnding.FailsAndExits;});
        Assert.Equal("Ended 0 · not ended 0 · already exited 1 · still running 0",status);
        Assert.Equal([8],control!.Terminated);
        Assert.Empty(control.StillOpen);
    }

    [Fact]
    public async Task Denied_open_shows_the_refusal_from_the_facts_only_read()
    {
        Assert.Equal("Not ended: 1 critical process",await EndWith(c=>{c.OpenError(8,5);c[8].Critical=true;}));
        // When even the facts-only open fails, nothing is known, so a standard user needs administrator.
        Assert.Equal(NeedsAdministrator,await EndWith(c=>{c.OpenError(8,5);c[8].Critical=true;c[8].FactsOpened=false;}));
    }

    [Fact]
    public async Task Debug_privilege_is_requested_only_when_elevated()
    {
        var (standardActions,standard,_)=Actions(elevated:false);
        var (elevatedActions,elevated,_)=Actions(elevated:true);
        await standardActions.EndAsync("a.exe",[(8,1),(9,1)],true);
        await elevatedActions.EndAsync("a.exe",[(8,1),(9,1)],true);
        Assert.Equal(2,standard.Opened.Count);
        Assert.All(standard.Opened,o=>Assert.False(o.Debug));
        Assert.Equal(2,elevated.Opened.Count);
        Assert.All(elevated.Opened,o=>Assert.True(o.Debug));
    }

    [Fact]
    public async Task Native_failures_never_throw_and_map_win32_codes()
    {
        FakeProcessControl? control=null;
        Assert.Equal("End task failed (Windows error 5)",await EndWith(c=>{control=c;c.ThrowOnFacts=new Win32Exception(5);}));
        Assert.Empty(control!.StillOpen);
        Assert.Empty(control.Terminated);
        Assert.Equal("End task failed (InvalidOperationException)",await EndWith(c=>c.ThrowOnFacts=new InvalidOperationException()));
    }

    [Fact]
    public async Task Other_open_errors_count_as_denied()
    {
        Assert.Equal(NeedsAdministrator,await EndWith(c=>c.OpenError(8,error:1450)));
        Assert.Equal("Not ended: 1 denied by Windows",await EndWith(c=>c.OpenError(8,error:1450),elevated:true));
    }

    [Fact]
    public async Task Gone_or_reused_pids_count_as_already_exited_and_are_never_terminated()
    {
        const string AllExited="Ended 0 · not ended 0 · already exited 1 · still running 0";
        FakeProcessControl? gone=null,reused=null;
        // ERROR_INVALID_PARAMETER: the PID no longer exists.
        Assert.Equal(AllExited,await EndWith(c=>{gone=c;c.OpenError(8,error:87);}));
        // The PID now belongs to a process created after the row's Generation.
        Assert.Equal(AllExited,await EndWith(c=>{reused=c;c[8].CreationTicks=2;}));
        Assert.Empty(gone!.Terminated);
        Assert.Empty(reused!.Terminated);
        Assert.Empty(reused.StillOpen);
        // The creation time is compared with each row's own Generation.
        var (a,c,_)=Actions();
        c[8].CreationTicks=7;
        Assert.Equal("Ended 1 · not ended 0 · already exited 0 · still running 0",await a.EndAsync("a.exe",[(8,7)],false));
        Assert.Equal([8],c.Terminated);
    }

    /// <summary>Opens the location of PID 8 with the row's <paramref name="generation"/>; its image path is <paramref name="path"/>, or a new file in a temp folder whose name has a space.
    /// <paramref name="configure"/> adjusts PID 8's script; the reveal waits for <paramref name="hold"/> when given.</summary>
    private static async Task<(string Status,string? Revealed,bool OnSta,string File)> OpenWith(bool reveal=true,Exception? throwOnReveal=null,string? path=null,long? creationTicks=1,long generation=1,
        Action<FakeProcessControl.Script>? configure=null,ManualResetEventSlim? hold=null,TimeSpan? revealTimeout=null,Exception? throwOnAttributes=null)
    {
        string folder=Path.Combine(Path.GetTempPath(),$"DeadWare actions {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string file=Path.Combine(folder,"app.exe");
            File.WriteAllBytes(file,[]);
            var control=new FakeProcessControl{ThrowOnAttributes=throwOnAttributes};
            control[8].ImagePath=path??file;
            control[8].CreationTicks=creationTicks;
            configure?.Invoke(control[8]);
            string? revealed=null;bool onSta=false;
            Func<string,string?,bool> confirm=(_,_)=>throw new InvalidOperationException("Open file location never confirms.");
            bool Reveal(string p)
            {
                onSta=Thread.CurrentThread.GetApartmentState()==ApartmentState.STA;
                hold?.Wait();
                if(throwOnReveal is not null)throw throwOnReveal;
                revealed=p;
                return reveal;
            }
            var actions=revealTimeout is TimeSpan timeout?new ProcessActions(control,confirm,Reveal,Own,false){RevealTimeout=timeout}:new ProcessActions(control,confirm,Reveal,Own,false);
            string status=await actions.OpenLocationAsync([(8,generation)]);
            return (status,revealed,onSta,file);
        }
        finally{Directory.Delete(folder,true);}
    }

    [Fact]
    public async Task Open_location_tells_a_denied_program_file_from_a_missing_one()
    {
        var denied=await OpenWith(throwOnAttributes:new UnauthorizedAccessException());
        Assert.Equal("Could not open the file location (denied by Windows).",denied.Status);Assert.Null(denied.Revealed);
        foreach(var gone in new Exception[]{new FileNotFoundException(),new DirectoryNotFoundException()})
        {
            var r=await OpenWith(throwOnAttributes:gone);
            Assert.Equal("The program file is no longer there.",r.Status);Assert.Null(r.Revealed);
        }
        var other=await OpenWith(throwOnAttributes:new IOException());
        Assert.Equal("Could not open the file location (IOException).",other.Status);Assert.Null(other.Revealed);
        // A folder at the path is not a program file, as File.Exists read it.
        var folder=await OpenWith(configure:s=>s.ImagePath=Path.GetDirectoryName(s.ImagePath));
        Assert.Equal("The program file is no longer there.",folder.Status);Assert.Null(folder.Revealed);
    }

    [Fact]
    public async Task A_program_file_Windows_denies_is_not_reported_gone()
    {
        string folder=Path.Combine(Path.GetTempPath(),$"DeadWare denied {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string file=Path.Combine(folder,"app.exe");
        var folderRule=new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ListDirectory,AccessControlType.Deny);
        var fileRule=new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ReadAttributes,AccessControlType.Deny);
        bool folderDenied=false,fileDenied=false;
        try
        {
            File.WriteAllBytes(file,[]);
            // Listing a folder also grants read-attributes on its files, so both are denied.
            var fileInfo=new FileInfo(file);var fileSecurity=fileInfo.GetAccessControl();fileSecurity.AddAccessRule(fileRule);fileInfo.SetAccessControl(fileSecurity);fileDenied=true;
            var dirInfo=new DirectoryInfo(folder);var dirSecurity=dirInfo.GetAccessControl();dirSecurity.AddAccessRule(folderRule);dirInfo.SetAccessControl(dirSecurity);folderDenied=true;
            Assert.False(File.Exists(file));                       // the bug's premise
            Assert.Throws<UnauthorizedAccessException>(()=>new NativeProcessControl().Attributes(file));
            Assert.Throws<FileNotFoundException>(()=>new NativeProcessControl().Attributes(Path.Combine(folder,"gone.exe")));
            var opened=await OpenWith(path:file);
            Assert.Equal("Could not open the file location (denied by Windows).",opened.Status);Assert.Null(opened.Revealed);
        }
        finally
        {
            try
            {
                // The folder rule first, then the file rule (the owner keeps WRITE_DAC), so the folder can be deleted even when an assert failed.
                if(folderDenied){var d=new DirectoryInfo(folder);var s=d.GetAccessControl();s.RemoveAccessRule(folderRule);d.SetAccessControl(s);}
                if(fileDenied){var f=new FileInfo(file);var s=f.GetAccessControl();s.RemoveAccessRule(fileRule);f.SetAccessControl(s);}
            }
            finally{Directory.Delete(folder,true);}
        }
    }

    [Fact]
    public async Task Open_location_reveals_a_program_file_past_260_characters()
    {
        string root=Path.Combine(Path.GetTempPath(),$"DeadWare long {Guid.NewGuid():N}");
        try
        {
            string folder=Path.Combine(root,new string('a',120),new string('b',120));
            Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"app.exe");
            File.WriteAllBytes(file,[]);
            Assert.True(file.Length>260);
            // The native read itself, which the fake below replaces: it must find the file, not throw or report it gone.
            Assert.Equal((FileAttributes)0,new NativeProcessControl().Attributes(file)&FileAttributes.Directory);
            var opened=await OpenWith(path:file);
            Assert.Equal("",opened.Status);Assert.Equal(file,opened.Revealed);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Open_location_failures_map_to_copy()
    {
        Assert.Equal("Could not open the file location.",(await OpenWith(reveal:false)).Status);
        Assert.Equal("Could not open the file location (Windows error 2).",(await OpenWith(throwOnReveal:new Win32Exception(2))).Status);
        Assert.Equal("Could not open the file location (InvalidOperationException).",(await OpenWith(throwOnReveal:new InvalidOperationException())).Status);
    }

    [Fact]
    public async Task Open_location_with_two_paths_asks_to_expand()
    {
        var (a,c,_)=Actions();
        c[8].ImagePath=@"C:\Program Files\App\app.exe";
        c[9].ImagePath=@"C:\Users\me\AppData\Local\App\app.exe";
        Assert.Equal("runs from 2 locations; expand to choose one",await a.OpenLocationAsync([(8,1),(9,1)]));
        // Paths that differ only in case are one location.
        string missing=Path.Combine(Path.GetTempPath(),$"DeadWare missing {Guid.NewGuid():N}","app.exe");
        c[8].ImagePath=missing;
        c[9].ImagePath=missing.ToUpperInvariant();
        Assert.Equal("The program file is no longer there.",await a.OpenLocationAsync([(8,1),(9,1)]));
    }

    [Fact]
    public async Task Open_location_checks_generation_and_existence()
    {
        var stale=await OpenWith(creationTicks:2);
        Assert.Equal("The process has already exited.",stale.Status);
        Assert.Null(stale.Revealed);
        // Null is a process that is gone.
        Assert.Equal("The process has already exited.",(await OpenWith(creationTicks:null)).Status);
        // A running process whose creation time cannot be read has not exited.
        var unreadable=await OpenWith(configure:s=>s.CreationTicksError=5);
        Assert.Equal("Could not open the file location (Windows error 5).",unreadable.Status);
        Assert.Null(unreadable.Revealed);
        var missing=await OpenWith(path:Path.Combine(Path.GetTempPath(),$"DeadWare missing {Guid.NewGuid():N}","app.exe"));
        Assert.Equal("The program file is no longer there.",missing.Status);
        Assert.Null(missing.Revealed);
        var opened=await OpenWith(creationTicks:7,generation:7);
        Assert.Equal("",opened.Status);
        Assert.Equal(opened.File,opened.Revealed);
        Assert.True(opened.OnSta);
    }

    [Fact]
    public async Task Open_location_for_an_app_reports_an_unreadable_member_only_when_none_is_confirmed()
    {
        var (a,c,_)=Actions();
        c[8].CreationTicksError=5;
        c[9].CreationTicks=2;
        Assert.Equal("Could not open the file location (Windows error 5).",await a.OpenLocationAsync([(8,1),(9,1)]));
        // A confirmed member is used even though another cannot be read.
        c[9].CreationTicks=1;
        c[9].ImagePath=Path.Combine(Path.GetTempPath(),$"DeadWare missing {Guid.NewGuid():N}","app.exe");
        Assert.Equal("The program file is no longer there.",await a.OpenLocationAsync([(8,1),(9,1)]));
    }

    [Fact]
    public async Task Open_location_rechecks_the_generation_after_reading_the_path()
    {
        var reused=await OpenWith(configure:s=>s.CreationTicksAfterPathRead=2);
        Assert.Equal("The process has already exited.",reused.Status);
        Assert.Null(reused.Revealed);
        // For an app, the reused member's path is neither counted nor revealed.
        var (a,c,_)=Actions();
        c[8].ImagePath=Path.Combine(Path.GetTempPath(),$"DeadWare missing {Guid.NewGuid():N}","app.exe");
        c[9].ImagePath=@"C:\Other\app.exe";
        c[9].CreationTicksAfterPathRead=2;
        Assert.Equal("The program file is no longer there.",await a.OpenLocationAsync([(8,1),(9,1)]));
    }

    [Fact]
    public async Task Open_location_gives_up_on_a_hung_shell()
    {
        Assert.Equal(TimeSpan.FromSeconds(10),new ProcessActions(new FakeProcessControl(),(_,_)=>false,_=>true,Own,false).RevealTimeout);
        var hold=new ManualResetEventSlim();
        try
        {
            var hung=await OpenWith(hold:hold,revealTimeout:TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("Could not open the file location.",hung.Status);
            Assert.Null(hung.Revealed);
        }
        finally{hold.Set();}
    }
}
