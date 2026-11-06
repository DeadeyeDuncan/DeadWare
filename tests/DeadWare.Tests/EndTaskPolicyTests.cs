using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
public class EndTaskPolicyTests
{
    const string W=@"C:\WINDOWS";const string User="S-1-5-21-1-2-3-1001";
    static EndTaskFacts F(bool exited=false,bool terminate=true,bool factsOpened=true,long? creation=1,bool? critical=false,bool? prot=false,string? path=@"C:\Program Files\App\app.exe",string? sid=User)=>
        new(exited,terminate,factsOpened,creation,critical,prot,path,sid);
    static EndTaskVerdict C(EndTaskFacts f,long generation=1,bool elevated=false)=>EndTaskPolicy.Classify(f,generation,W,elevated);

    [Theory][InlineData(0)][InlineData(4)] public void Pid_0_and_4_are_refused_before_opening(int pid)=>Assert.Equal(EndTaskVerdict.SystemProcess,EndTaskPolicy.Precheck(pid,999));
    [Fact] public void DeadWare_itself_is_refused()=>Assert.Equal(EndTaskVerdict.DeadWareItself,EndTaskPolicy.Precheck(999,999));
    [Fact] public void Changed_creation_time_is_already_exited()=>Assert.Equal(EndTaskVerdict.AlreadyExited,C(F(creation:2),generation:1));
    [Theory][InlineData(false,EndTaskVerdict.NeedsAdministrator)][InlineData(true,EndTaskVerdict.WindowsDenied)]
    public void Unreadable_creation_time_is_undecided(bool elevated,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(creation:null),elevated:elevated));
    [Theory][InlineData(false,EndTaskVerdict.NeedsAdministrator)][InlineData(true,EndTaskVerdict.WindowsDenied)]
    public void Failed_facts_only_open_is_undecided(bool elevated,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(terminate:false,factsOpened:false,creation:null,critical:null,prot:null,path:null,sid:null),elevated:elevated));
    [Theory][InlineData(@"C:\Windows\System32\spoolsv.exe","S-1-5-18",EndTaskVerdict.WindowsProcess)][InlineData(@"C:\Windows\System32\notepad.exe",User,EndTaskVerdict.CanEnd)]
    [InlineData(@"C:\Windows.old\x.exe","S-1-5-18",EndTaskVerdict.CanEnd)][InlineData(@"C:\WindowsAzure\x.exe","S-1-5-18",EndTaskVerdict.CanEnd)][InlineData(@"C:\Windows\System32\x.exe","S-1-5-90-0-1",EndTaskVerdict.WindowsProcess)]
    public void Windows_folder_plus_service_account(string path,string sid,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(path:path,sid:sid),elevated:true));
    [Fact] public void System32_svchost_of_the_signed_in_user_is_refused()=>Assert.Equal(EndTaskVerdict.WindowsProcess,C(F(path:@"c:\windows\system32\SVCHOST.EXE",sid:User)));
    [Fact] public void Driver_store_service_is_allowed_when_elevated()=>Assert.Equal(EndTaskVerdict.CanEnd,C(F(path:@"C:\Windows\System32\DriverStore\FileRepository\nv.inf_amd64\Display.NvContainer\NVDisplay.Container.exe",sid:"S-1-5-18"),elevated:true));
    [Fact] public void Service_directly_in_System32_is_refused()=>Assert.Equal(EndTaskVerdict.WindowsProcess,C(F(path:@"C:\Windows\System32\oldvendor.exe",sid:"S-1-5-18"),elevated:true));
    [Fact] public void Unreadable_sid_inside_windows_is_undecided()=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(path:@"C:\Windows\System32\x.exe",sid:null)));
    [Fact] public void Unreadable_sid_outside_windows_is_settled_by_the_path()=>Assert.Equal(EndTaskVerdict.CanEnd,C(F(path:@"C:\Program Files\App\app.exe",sid:null)));
    [Fact] public void Unknown_path_is_never_settled()=>Assert.Equal(EndTaskVerdict.WindowsDenied,C(F(path:null,sid:User),elevated:true));
    [Theory][InlineData(null,false)][InlineData(false,null)] public void Unknown_critical_or_protection_is_undecided(bool? critical,bool? prot)=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(critical:critical,prot:prot)));
    [Fact] public void Denied_terminate_open_keeps_a_known_refusal()=>Assert.Equal(EndTaskVerdict.Critical,C(F(terminate:false,critical:true)));
    [Fact] public void Denied_terminate_open_otherwise_needs_administrator()=>Assert.Equal(EndTaskVerdict.NeedsAdministrator,C(F(terminate:false)));
    [Fact] public void Questions(){ Assert.Equal("End chrome.exe (31 processes)? Unsaved work in it will be lost.",EndTaskPolicy.Question("chrome.exe",0,31,31,true));
        Assert.Equal("End 28 of 31 chrome.exe processes? Unsaved work in them will be lost.",EndTaskPolicy.Question("chrome.exe",0,28,31,true));}
    [Fact] public void Not_ended_summary_counts_by_first_rule()=>Assert.Equal("Not ended: 3 need administrator.",EndTaskPolicy.NotEnded(Enumerable.Repeat(EndTaskVerdict.NeedsAdministrator,3).Append(EndTaskVerdict.AlreadyExited)));
    [Fact] public void Refusals(){ Assert.Equal("Use Quit DeadWare",EndTaskPolicy.Refusal([EndTaskVerdict.DeadWareItself]));Assert.Equal("Not ended: 2 Windows' own processes",EndTaskPolicy.Refusal([EndTaskVerdict.WindowsProcess,EndTaskVerdict.WindowsProcess]));
        Assert.EndsWith("Use Restart as administrator on this page.",EndTaskPolicy.Refusal([EndTaskVerdict.NeedsAdministrator]));}
    [Fact] public void Result_line()=>Assert.Equal("Ended 2 · not ended 1 · already exited 1 · still running 0",EndTaskPolicy.Result(2,1,1,0));

    [Fact] public void Ordinary_pid_passes_the_precheck()=>Assert.Null(EndTaskPolicy.Precheck(1234,999));
    [Fact] public void Exited_pid_is_already_exited()=>Assert.Equal(EndTaskVerdict.AlreadyExited,C(F(exited:true,terminate:false,factsOpened:false,creation:null)));
    [Theory][InlineData(false,EndTaskVerdict.NeedsAdministrator)][InlineData(true,EndTaskVerdict.WindowsDenied)]
    public void Failed_facts_only_open_is_undecided_before_the_generation_comparison(bool elevated,EndTaskVerdict expected)=>Assert.Equal(expected,C(F(terminate:false,factsOpened:false,creation:2,critical:true),generation:1,elevated:elevated));
    [Fact] public void Readable_facts_with_no_refusal_can_end()=>Assert.Equal(EndTaskVerdict.CanEnd,C(F()));
    [Fact] public void Known_refusals_count_under_the_first_rule(){ Assert.Equal(EndTaskVerdict.Critical,C(F(critical:true,prot:true,path:@"C:\Windows\System32\svchost.exe")));
        Assert.Equal(EndTaskVerdict.Protected,C(F(critical:null,prot:true,path:@"C:\Windows\System32\svchost.exe")));
        Assert.Equal(EndTaskVerdict.WindowsProcess,C(F(critical:null,prot:null,path:@"C:\Windows\SysWOW64\svchost.exe",sid:null)));}
    [Theory][InlineData(@"C:\Windows\SysWOW64\svchost.exe",true)][InlineData(@"C:\Windows\System32\svchost.exe",true)][InlineData(@"C:\Windows\svchost.exe",false)]
    [InlineData(@"C:\Windows\System32\sub\svchost.exe",false)][InlineData(@"C:\Temp\System32\svchost.exe",false)][InlineData(@"C:\Windows\System32\svchost.exe.bak",false)]
    public void Windows_svchost_is_only_directly_in_System32_or_SysWOW64(string path,bool expected)=>Assert.Equal(expected,EndTaskPolicy.IsWindowsSvchost(path,W));
    [Theory][InlineData("S-1-5-18",true)][InlineData("S-1-5-19",true)][InlineData("S-1-5-20",true)][InlineData("S-1-5-90-0-3",true)][InlineData("S-1-5-96-0-1",true)]
    [InlineData("S-1-5-180",false)][InlineData("S-1-5-21-1-2-3-500",false)][InlineData("S-1-5-32-544",false)][InlineData("S-1-5-901-1",false)]
    public void Service_accounts(string sid,bool expected)=>Assert.Equal(expected,EndTaskPolicy.IsServiceAccount(sid));
    [Fact] public void Windows_folder_with_a_trailing_separator_still_contains_its_files()=>
        Assert.Equal(EndTaskVerdict.WindowsProcess,EndTaskPolicy.Classify(F(path:@"C:\Windows\System32\spoolsv.exe",sid:"S-1-5-18"),1,@"C:\Windows\",true));
    [Theory][InlineData("")][InlineData(@"\")]
    public void Unknown_windows_folder_is_never_settled(string windowsFolder)=>Assert.Equal(EndTaskVerdict.WindowsDenied,EndTaskPolicy.Classify(F(),1,windowsFolder,true));
    [Fact] public void Single_process_and_one_process_questions(){ Assert.Equal("End chrome.exe (PID 1234)? Unsaved work in it will be lost.",EndTaskPolicy.Question("chrome.exe",1234,1,1,false));
        Assert.Equal("End chrome.exe (1 process)? Unsaved work in it will be lost.",EndTaskPolicy.Question("chrome.exe",0,1,1,true));}
    [Fact] public void Not_ended_lists_every_reason_in_rule_order(){
        EndTaskVerdict[] v=[EndTaskVerdict.WindowsDenied,EndTaskVerdict.CanEnd,EndTaskVerdict.NeedsAdministrator,EndTaskVerdict.WindowsProcess,EndTaskVerdict.Protected,EndTaskVerdict.Critical,
            EndTaskVerdict.AlreadyExited,EndTaskVerdict.DeadWareItself,EndTaskVerdict.SystemProcess,EndTaskVerdict.Critical,EndTaskVerdict.WindowsDenied];
        Assert.Equal("Not ended: 1 system process, Use Quit DeadWare, 2 critical processes, 1 protected process, 1 Windows' own process, 1 needs administrator, 2 denied by Windows.",EndTaskPolicy.NotEnded(v));
        Assert.Equal("Not ended: 2 system processes, 2 protected processes, 2 denied by Windows.",EndTaskPolicy.NotEnded([EndTaskVerdict.SystemProcess,EndTaskVerdict.Protected,EndTaskVerdict.WindowsDenied,EndTaskVerdict.SystemProcess,EndTaskVerdict.Protected,EndTaskVerdict.WindowsDenied]));}
    [Fact] public void Nothing_refused_is_no_summary(){ Assert.Null(EndTaskPolicy.NotEnded([EndTaskVerdict.CanEnd,EndTaskVerdict.AlreadyExited]));Assert.Null(EndTaskPolicy.NotEnded([]));}
    [Fact] public void Refusal_points_to_restart_as_administrator_after_the_counts(){
        Assert.Equal("Not ended: 1 needs administrator. Use Restart as administrator on this page.",EndTaskPolicy.Refusal([EndTaskVerdict.NeedsAdministrator,EndTaskVerdict.AlreadyExited]));
        Assert.Equal("Not ended: Use Quit DeadWare, 2 Windows' own processes",EndTaskPolicy.Refusal([EndTaskVerdict.DeadWareItself,EndTaskVerdict.WindowsProcess,EndTaskVerdict.WindowsProcess]));
        Assert.Equal("Use Quit DeadWare",EndTaskPolicy.Refusal([EndTaskVerdict.DeadWareItself,EndTaskVerdict.AlreadyExited]));}
    [Fact] public void Refusal_when_every_member_already_exited_is_the_result_line()=>
        Assert.Equal("Ended 0 · not ended 0 · already exited 2 · still running 0",EndTaskPolicy.Refusal([EndTaskVerdict.AlreadyExited,EndTaskVerdict.AlreadyExited]));
}
