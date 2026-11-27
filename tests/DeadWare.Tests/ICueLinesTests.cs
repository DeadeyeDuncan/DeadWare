using DeadWare.Core;
using Xunit;
namespace DeadWare.Tests;
// Lighting 5, Task 12: the iCUE lines Settings → Lighting shows, from facts read elsewhere. Every text is the design's, written out here.
public class ICueLinesTests
{
    static void Line(FactLine line,FactMark mark,string text,params string[] instructions)
    {
        Assert.Equal(mark,line.Mark);
        Assert.Equal(text,line.Text);
        Assert.Equal(instructions,line.Instructions??[]);
    }
    static ServiceFact CorsairService(bool running,bool starts)=>new("Corsair Service",running,starts);
    static readonly IReadOnlyList<ServiceFact> QuietService=[CorsairService(false,false)];

    [Fact] public void The_texts_are_the_designs()
    {
        Assert.Equal("iCUE",ICueLines.Heading);
        Assert.Equal("iCUE and DeadWare can't both drive the hub. iCUE stays installed; these three things only need to be off. DeadWare shows them and never changes them for you.",ICueLines.Lead);
        Assert.Equal("iCUE is not installed on this PC.",ICueLines.NotInstalled);
        Assert.Equal(["CorsairService","CorsairDeviceControlService","CorsairDeviceListerService"],ICueLines.Services);
    }
    [Fact] public void No_trace_of_iCUE_is_one_line()
    {
        Line(Assert.Single(ICueLines.Lines(new(false,[],[]))),FactMark.Good,"iCUE is not installed on this PC.");
        // An unreadable fact is no proof of absence: the three lines show instead.
        var lines=ICueLines.Lines(new(false,null,[],ServicesError:"Access is denied."));
        Assert.Equal(3,lines.Count);
        Line(lines[0],FactMark.Good,"iCUE is not running.");
        Line(lines[1],FactMark.Warning,"DeadWare could not check the Corsair services: Access is denied.");
        Line(lines[2],FactMark.Good,"iCUE does not start when you sign in.");
    }
    [Fact] public void A_running_iCUE_is_never_not_installed()
    {
        // Running with no service and no start-up entry, both lists read: the red program line, never the green "not installed".
        var lines=ICueLines.Lines(new(true,[],[]));
        Assert.Equal(3,lines.Count);
        Line(lines[0],FactMark.Blocking,"iCUE is running.","Quit it from its icon by the clock: right-click the icon and choose Quit.");
        Assert.DoesNotContain(lines,l=>l.Text=="iCUE is not installed on this PC.");
    }
    [Theory]
    [InlineData(true,FactMark.Blocking,"iCUE is running.","Quit it from its icon by the clock: right-click the icon and choose Quit.")]
    [InlineData(false,FactMark.Good,"iCUE is not running.",null)]
    public void The_program_line(bool running,FactMark mark,string text,string? instruction)
    {
        var lines=ICueLines.Lines(new(running,QuietService,[]));
        Assert.Equal(3,lines.Count);
        Line(lines[0],mark,text,instruction is null?[]:[instruction]);
    }
    [Theory]
    [InlineData(false,false,FactMark.Good,"The Corsair service is not running and does not start with Windows.",null)]
    [InlineData(true,true,FactMark.Blocking,"The Corsair service is running, and starts with Windows.","Press Win + R, type services.msc and press Enter. Find Corsair Service, right-click it and choose Stop. Then double-click it and set Startup type to Manual.")]
    [InlineData(true,false,FactMark.Blocking,"The Corsair service is running.","Press Win + R, type services.msc and press Enter. Find Corsair Service, right-click it and choose Stop.")]
    [InlineData(false,true,FactMark.Warning,"The Corsair service starts with Windows.","Press Win + R, type services.msc and press Enter. Find Corsair Service, double-click it and set Startup type to Manual.")]
    public void The_service_line_by_running_and_start_type(bool running,bool starts,FactMark mark,string text,string? instruction)
    {
        var lines=ICueLines.Lines(new(false,[CorsairService(running,starts)],[]));
        Assert.Equal(3,lines.Count);
        Line(lines[1],mark,text,instruction is null?[]:[instruction]);
    }
    [Fact] public void Two_services_get_an_instruction_each()
    {
        var lines=ICueLines.Lines(new(false,[new("Corsair Service",true,false),new("Corsair Device Control Service",false,true),new("Corsair Device Lister Service",false,false)],[]));
        // One runs and another starts with Windows: the line names both; the good one gets no instruction.
        Line(lines[1],FactMark.Blocking,"The Corsair service is running, and starts with Windows.",
            "Press Win + R, type services.msc and press Enter. Find Corsair Service, right-click it and choose Stop.",
            "Press Win + R, type services.msc and press Enter. Find Corsair Device Control Service, double-click it and set Startup type to Manual.");
    }
    [Theory]
    [InlineData(null,false,FactMark.Good,"iCUE does not start when you sign in.",null)]
    [InlineData("Corsair iCUE5 Software",false,FactMark.Warning,"iCUE starts when you sign in.","Open Task Manager (Ctrl + Shift + Esc), choose Startup apps, find Corsair iCUE5 Software and choose Disable.")]
    [InlineData("Corsair iCUE5 Software",true,FactMark.Good,"iCUE does not start when you sign in.",null)]
    public void The_sign_in_line(string? entry,bool disabled,FactMark mark,string text,string? instruction)
    {
        IReadOnlyList<StartupEntry> startup=entry is null?[]:[new(entry,disabled)];
        var lines=ICueLines.Lines(new(false,QuietService,startup));
        Assert.Equal(3,lines.Count);
        Line(lines[2],mark,text,instruction is null?[]:[instruction]);
    }
    [Fact] public void Only_enabled_entries_get_an_instruction()
    {
        var lines=ICueLines.Lines(new(false,[],[new("Corsair iCUE5 Software",true),new("iCUE helper",false)]));
        Line(lines[2],FactMark.Warning,"iCUE starts when you sign in.","Open Task Manager (Ctrl + Shift + Esc), choose Startup apps, find iCUE helper and choose Disable.");
    }
    [Theory]
    [InlineData(true,"Access is denied.","DeadWare could not check the Corsair services: Access is denied.")]
    [InlineData(true,"access denied","DeadWare could not check the Corsair services: access denied.")]
    [InlineData(false,"Requested registry access is not allowed.","DeadWare could not check whether iCUE starts when you sign in: Requested registry access is not allowed.")]
    [InlineData(false,"no reason","DeadWare could not check whether iCUE starts when you sign in: no reason.")]
    public void An_unreadable_fact_is_a_warning_never_good(bool services,string reason,string text)
    {
        var facts=services?new ICueFacts(false,null,[],ServicesError:reason):new ICueFacts(false,[],null,StartupError:reason);
        var lines=ICueLines.Lines(facts);
        Assert.Equal(3,lines.Count);
        Line(lines[services?1:2],FactMark.Warning,text);
        // The other fact still reads as it is.
        Line(lines[services?2:1],FactMark.Good,services?"iCUE does not start when you sign in.":"The Corsair service is not running and does not start with Windows.");
    }
    [Theory]
    [InlineData("020000000000000000000000",false)]
    [InlineData("060000000000000000000000",false)]
    [InlineData("0300000010C8A3B2E0F4DA01",true)]
    [InlineData(null,false)]
    [InlineData("",false)]
    public void An_odd_first_byte_means_disabled(string? hex,bool disabled)
        =>Assert.Equal(disabled,ICueLines.Disabled(hex is null?null:Convert.FromHexString(hex)));
    [Theory]
    [InlineData("Corsair iCUE5 Software",@"C:\anything\else.exe",true)]
    [InlineData("Corsair iCUE5 Software",null,true)]
    [InlineData("X","\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe\" --autorun",true)]
    [InlineData("X",@"C:\Program Files\Corsair\Corsair iCUE5 Software\iCUE.exe",true)]
    [InlineData("X",@"C:\PROGRAM FILES\CORSAIR\ICUE.EXE --minimized",true)]
    [InlineData("Steam","\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent",false)]
    [InlineData("Steam","steam.exe",false)]
    [InlineData("X",@"C:\Tools\NotiCUE.exe",false)]
    [InlineData("X",@"C:\Tools\iCUE.exe.old\other.exe",false)]
    [InlineData("X",null,false)]
    public void The_iCUE_entry_is_known_by_name_or_command(string name,string? command,bool expected)
        =>Assert.Equal(expected,ICueLines.IsICueEntry(name,command));
    [Theory]
    [InlineData(2,true)]
    [InlineData(3,false)]
    [InlineData(4,false)]
    [InlineData(0,false)]
    [InlineData(null,false)]
    public void Only_start_type_2_starts_with_Windows(int? start,bool expected)
        =>Assert.Equal(expected,ICueLines.StartsWithWindows(start));
    [Fact] public void The_legacy_service_runs_as_Corsair_Service()
    {
        Assert.Equal("Corsair.Service",ICueLines.ProcessName("CorsairService"));
        Assert.Equal("CorsairDeviceControlService",ICueLines.ProcessName("CorsairDeviceControlService"));
        Assert.Equal("CorsairDeviceListerService",ICueLines.ProcessName("CorsairDeviceListerService"));
    }
}
