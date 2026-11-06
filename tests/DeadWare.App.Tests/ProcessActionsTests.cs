using System.ComponentModel;
using System.IO;
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

    /// <summary>Opens the location of PID 8 with the row's <paramref name="generation"/>; its image path is <paramref name="path"/>, or a new file in a temp folder whose name has a space.</summary>
    private static async Task<(string Status,string? Revealed,bool OnSta,string File)> OpenWith(bool reveal=true,Exception? throwOnReveal=null,string? path=null,long? creationTicks=1,long generation=1)
    {
        string folder=Path.Combine(Path.GetTempPath(),$"DeadWare actions {Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string file=Path.Combine(folder,"app.exe");
            File.WriteAllBytes(file,[]);
            var control=new FakeProcessControl();
            control[8].ImagePath=path??file;
            control[8].CreationTicks=creationTicks;
            string? revealed=null;bool onSta=false;
            var actions=new ProcessActions(control,(_,_)=>throw new InvalidOperationException("Open file location never confirms."),p=>
            {
                onSta=Thread.CurrentThread.GetApartmentState()==ApartmentState.STA;
                if(throwOnReveal is not null)throw throwOnReveal;
                revealed=p;
                return reveal;
            },Own,false);
            string status=await actions.OpenLocationAsync([(8,generation)]);
            return (status,revealed,onSta,file);
        }
        finally{Directory.Delete(folder,true);}
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
        Assert.Equal("The process has already exited.",(await OpenWith(creationTicks:null)).Status);
        var missing=await OpenWith(path:Path.Combine(Path.GetTempPath(),$"DeadWare missing {Guid.NewGuid():N}","app.exe"));
        Assert.Equal("The program file is no longer there.",missing.Status);
        Assert.Null(missing.Revealed);
        var opened=await OpenWith(creationTicks:7,generation:7);
        Assert.Equal("",opened.Status);
        Assert.Equal(opened.File,opened.Revealed);
        Assert.True(opened.OnSta);
    }
}
