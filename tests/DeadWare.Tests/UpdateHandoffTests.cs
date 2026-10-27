using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class UpdateHandoffTests
{
    [Fact] public void Reused_pid_wrong_user_session_or_executable_cannot_authorize_replacement()
    {
        var expected=new ProcessIdentity(42,123,7,"user",Path.GetFullPath("DeadWare.exe"));
        UpdateHandoff.RequireIdentity(expected,expected);
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.RequireIdentity(expected,expected with {StartTicks=124}));
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.RequireIdentity(expected,expected with {UserSid="other"}));
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.RequireIdentity(expected,expected with {SessionId=8}));
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.RequireIdentity(expected,expected with {Executable=Path.GetFullPath("Other.exe")}));
    }
    [Fact] public void Jobs_are_bound_to_their_installation_owned_stage_and_manifest()
    {
        using var fixture=new UpdateFixture();string installed=fixture.Package("app","0.6.0","old");
        var paths=new UpdatePaths(installed,Path.Combine(fixture.Root,"cache")); string id=Guid.NewGuid().ToString("N");
        string stage=fixture.Package("stage","0.6.1","new");
        var parent=new ProcessIdentity(42,123,7,"user",Path.Combine(installed,"DeadWare.exe"));
        var job=new UpdateJob(id,paths.Key,installed,stage,"0.6.0","0.6.1","update",parent,Packages.Hash(Path.Combine(stage,Packages.ManifestName)),DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.ValidateJob(paths,job));
        Directory.CreateDirectory(paths.JobDirectory(id));Directory.Move(stage,Path.Combine(paths.JobDirectory(id),"package"));
        job=job with {StageDirectory=Path.Combine(paths.JobDirectory(id),"package")};
        UpdateHandoff.ValidateJob(paths,job);
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.ValidateJob(paths,job with {Key=new string('0',64)}));
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.ValidateJob(paths,job with {Created=DateTimeOffset.UtcNow.AddDays(-2)}));
        Assert.Throws<InvalidDataException>(()=>UpdateHandoff.ValidateJob(paths,job with {ToVersion="0.6.0"}));
    }
}
