using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Lighting 5, Task 12: how the Windows facts reader maps what it reads into facts. Only literal inputs: nothing here reads this PC's
/// registry, services or Task Scheduler, and nothing changes anything.</summary>
public class WindowsHubFactsTests
{
    // Stand-ins for the Task Scheduler objects ParseTask reads late-bound (public, so the reader's late binding can see them).
    public sealed class FakeAction{public int Type{get;set;}public string? Path{get;set;}public string? Arguments{get;set;}public string? WorkingDirectory{get;set;}}
    public sealed class FakeActions(params object[] items){public int Count=>items.Length;public object Item(int index)=>items[index-1];}
    public sealed class FakeDefinition{public FakeActions Actions{get;set;}=new();}
    public sealed class FakeTask{public bool Enabled{get;set;}public FakeDefinition Definition{get;set;}=new();}

    private const string Electron=@"C:\Apps\DL\node_modules\electron\dist\electron.exe";
    private static readonly IReadOnlyList<RunningProcess> NoProcesses=[];

    [Fact] public void No_task_parses_to_null()=>Assert.Null(WindowsHubFacts.ParseTask(null));
    [Fact] public void A_task_gives_its_switch_and_its_first_action()
    {
        var task=new FakeTask{Enabled=false,Definition=new(){Actions=new(new FakeAction{Type=0,Path=Electron,Arguments=@"C:\Apps\DL",WorkingDirectory=@"C:\Apps\DL"},new FakeAction{Type=0,Path=@"C:\Second.exe"})}};
        Assert.Equal(new DeadLightTask(false,Electron,@"C:\Apps\DL",@"C:\Apps\DL"),WindowsHubFacts.ParseTask(task));
    }
    [Fact] public void Empty_action_texts_are_absent()
    {
        var task=new FakeTask{Enabled=true,Definition=new(){Actions=new(new FakeAction{Type=0,Path=Electron,Arguments="",WorkingDirectory=" "})}};
        Assert.Equal(new DeadLightTask(true,Electron,null,null),WindowsHubFacts.ParseTask(task));
    }
    [Fact] public void A_task_without_a_program_action_has_no_command()
    {
        // TASK_ACTION_SEND_EMAIL (6) and no action at all: the switch is still read, there is nothing to walk up from. (The stand-in has a Path
        // only to show it is not read: a real e-mail action has none.)
        Assert.Equal(new DeadLightTask(true,null,null,null),WindowsHubFacts.ParseTask(new FakeTask{Enabled=true,Definition=new(){Actions=new(new FakeAction{Type=6,Path=@"C:\Mail.exe"})}}));
        Assert.Equal(new DeadLightTask(false,null,null,null),WindowsHubFacts.ParseTask(new FakeTask{Enabled=false}));
    }

    [Theory]
    [InlineData("Corsair Service","Corsair Service")]
    [InlineData(null,"CorsairService")]
    [InlineData("","CorsairService")]
    [InlineData(@"@%ProgramFiles%\Corsair\CorsairService.exe,-101","CorsairService")]
    [InlineData(42,"CorsairService")]
    public void The_display_name_falls_back_to_the_service_name(object? value,string expected)
        =>Assert.Equal(expected,WindowsHubFacts.DisplayName("CorsairService",value));
    [Theory]
    [InlineData(2,true)]
    [InlineData(3,false)]
    [InlineData(4,false)]
    [InlineData(null,false)]
    [InlineData("2",false)]
    public void The_start_value_decides_starts_with_Windows(object? start,bool expected)
        =>Assert.Equal(new ServiceFact("Corsair Service",false,expected),WindowsHubFacts.Service("CorsairService",true,"Corsair Service",start,NoProcesses));
    [Fact] public void A_service_runs_when_its_program_runs()
    {
        IReadOnlyList<RunningProcess> processes=[new(1,"Corsair.Service",null),new(2,"corsairdevicelisterservice",null),new(3,"CorsairService",null)];
        // The legacy service's program is Corsair.Service; a process named after the service itself is not it.
        Assert.True(WindowsHubFacts.Service("CorsairService",true,null,3,processes)!.Running);
        Assert.True(WindowsHubFacts.Service("CorsairDeviceListerService",true,null,3,processes)!.Running);
        Assert.False(WindowsHubFacts.Service("CorsairDeviceControlService",true,null,3,processes)!.Running);
        Assert.False(WindowsHubFacts.Service("CorsairService",true,null,3,[new(3,"CorsairService",null)])!.Running);
    }
    [Fact] public void A_missing_service_is_left_out_unless_its_program_runs()
    {
        Assert.Null(WindowsHubFacts.Service("CorsairDeviceControlService",false,null,null,NoProcesses));
        // The block comes from the running program, so the line agrees with it even without a service key.
        Assert.Equal(new ServiceFact("CorsairDeviceControlService",true,false),WindowsHubFacts.Service("CorsairDeviceControlService",false,null,null,[new(5,"CorsairDeviceControlService",null)]));
    }

    [Fact] public void Startup_entries_are_iCUEs_values_with_their_approved_state()
    {
        var run=new Dictionary<string,object?>
        {
            ["Steam"]="\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent",
            ["Corsair iCUE5 Software"]="\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe\" --autorun",
            ["Helper"]=@"C:\Program Files\Corsair\iCUE.exe",
            ["Odd"]=new byte[]{1,2,3},
        };
        var approved=new Dictionary<string,object?>{["Corsair iCUE5 Software"]=Convert.FromHexString("0300000010C8A3B2E0F4DA01"),["Steam"]=Convert.FromHexString("030000000000000000000000")};
        Assert.Equal([new StartupEntry("Corsair iCUE5 Software",true),new StartupEntry("Helper",false)],WindowsHubFacts.Entries(run,name=>approved.GetValueOrDefault(name)));
    }
    [Fact] public void An_approved_value_that_is_not_binary_means_enabled()
        =>Assert.Equal([new StartupEntry("Corsair iCUE5 Software",false)],WindowsHubFacts.Entries(new Dictionary<string,object?>{["Corsair iCUE5 Software"]="x"},_=>"03"));

    [Fact] public void Each_iCUE_fact_is_read_in_its_own_try()
    {
        IReadOnlyList<LightingConflict> conflicts=[new(ConflictKind.ICue,"iCUE is running.",true)];
        var facts=WindowsHubFacts.ICueFrom(conflicts,()=>throw new UnauthorizedAccessException("Access is denied."),()=>[new("Corsair iCUE5 Software",false)]);
        Assert.True(facts.Running);
        Assert.Null(facts.Services);Assert.Equal("Access is denied.",facts.ServicesError);
        Assert.Equal([new StartupEntry("Corsair iCUE5 Software",false)],facts.Startup);Assert.Null(facts.StartupError);
        facts=WindowsHubFacts.ICueFrom([],()=>[new("Corsair Service",false,true)],()=>throw new System.Security.SecurityException("Requested registry access is not allowed."));
        Assert.False(facts.Running);
        Assert.Equal([new ServiceFact("Corsair Service",false,true)],facts.Services);Assert.Null(facts.ServicesError);
        Assert.Null(facts.Startup);Assert.Equal("Requested registry access is not allowed.",facts.StartupError);
    }
    [Fact] public void iCUE_runs_only_by_the_helpers_rule()
    {
        // The legacy service and DeadLight are other kinds: iCUE itself is not running.
        IReadOnlyList<LightingConflict> conflicts=[new(ConflictKind.LegacyCorsairService,"",false),new(ConflictKind.DeadLight,"",true),new(ConflictKind.CorsairService,"",true)];
        Assert.False(WindowsHubFacts.ICueFrom(conflicts,()=>[],()=>[]).Running);
    }
    [Fact] public void DeadLight_runs_by_the_helpers_rule_and_its_task_is_read_in_its_own_try()
    {
        var task=new DeadLightTask(true,Electron,null,null);
        Assert.Equal(new DeadLightFacts(true,task),WindowsHubFacts.DeadLightFrom([new(ConflictKind.DeadLight,"",true)],()=>task));
        Assert.Equal(new DeadLightFacts(false,null),WindowsHubFacts.DeadLightFrom([new(ConflictKind.ICue,"",true)],()=>null));
        Assert.Equal(new DeadLightFacts(false,null,"Windows Task Scheduler is unavailable."),WindowsHubFacts.DeadLightFrom([],()=>throw new InvalidOperationException("Windows Task Scheduler is unavailable.")));
    }
}
