using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Xunit;

namespace DeadWare.App.Tests;

public class StartupRegistrationTests
{
    private const string Sid="S-1-5-21-123-456-789-1001",Name="DeadWare.Startup."+Sid,Exe=@"C:\Program Files\DeadWare\DeadWare.exe";
    private const string Unsafe="Startup runs DeadWare as administrator from a folder other programs can change. Move DeadWare to a protected folder such as C:\\Program Files\\DeadWare and repair startup, or turn startup off.";
    [Fact] public void Owned_task_in_an_unprotected_folder_is_not_ready_and_says_so()
    {
        string? audited=null;
        var status=StartupRegistration.Describe(Name,StartupTaskPolicy.CreateXml(Sid,Exe),true,Sid,Exe,null,executable=>{audited=executable;return false;});
        Assert.Equal(Exe,audited);
        Assert.True(status.Registered&&status.Owned&&status.Enabled&&status.Unprotected);Assert.False(status.NeedsRepair);
        Assert.False(status.Ready);Assert.Null(status.Error);Assert.Equal(Unsafe,status.Message);
    }
    [Fact] public void Protected_current_task_is_ready()
    {
        var status=StartupRegistration.Describe(Name,StartupTaskPolicy.CreateXml(Sid,Exe),true,Sid,Exe,null,_=>true);
        Assert.True(status.Ready);Assert.False(status.Unprotected);Assert.Equal("On — starts as administrator when you sign in.",status.Message);
    }
    [Fact] public void Unprotected_location_outranks_repair_and_disabled_messages()
    {
        var status=StartupRegistration.Describe(Name,StartupTaskPolicy.CreateXml(Sid,@"D:\Old\DeadWare.exe"),false,Sid,Exe,null,executable=>executable!=@"D:\Old\DeadWare.exe");
        Assert.True(status.NeedsRepair&&status.Unprotected);Assert.False(status.Ready);Assert.Equal(Unsafe,status.Message);
    }
    [Fact] public void Unowned_task_is_never_audited()
    {
        string xml=StartupTaskPolicy.CreateXml(Sid,Exe).Replace(StartupTaskPolicy.Marker,"Some other task");
        var status=StartupRegistration.Describe(Name,xml,true,Sid,Exe,null,_=>throw new InvalidOperationException("An unowned task must not be audited."));
        Assert.False(status.Owned||status.Unprotected);Assert.Equal("A task with this name belongs to something else. DeadWare will not change it.",status.Message);
    }
    [Fact] public void Unprotected_enable_result_explains_where_to_move_DeadWare()
    {
        var status=new StartupRegistrationStatus(Name,false,false,false,false,null,"Off — DeadWare will not start at sign-in.");
        Assert.Equal("DeadWare can start as administrator at sign-in only from a folder standard users can't change, such as C:\\Program Files\\DeadWare. Move the DeadWare folder there, then turn startup on.",StartupRegistration.ResultMessage(6,true,false,status));
        Assert.Equal("Startup disabled.",StartupRegistration.ResultMessage(0,false,true,status));
    }
    [Fact] public void Default_protection_check_rejects_temp_and_relative_executables()
    {
        string directory=Path.Combine(Path.GetTempPath(),"DeadWare-tests-"+Guid.NewGuid().ToString("N"));
        try {Directory.CreateDirectory(directory);Assert.False(StartupRegistration.IsProtectedExecutable(Path.Combine(directory,"DeadWare.exe")));}
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
        Assert.False(StartupRegistration.IsProtectedExecutable("DeadWare.exe"));
    }
}
