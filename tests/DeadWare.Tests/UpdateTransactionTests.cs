using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class UpdateTransactionTests
{
    [Fact] public void Update_then_explicit_rollback_preserves_unmanaged_files_and_outside_history()
    {
        using var fixture=new UpdateFixture(); string installed=fixture.Package("app","0.6.0","old app");
        string stage=fixture.Package("stage","0.6.1","new app");
        File.WriteAllText(Path.Combine(installed,"personal.txt"),"keep"); File.WriteAllText(Path.Combine(fixture.Root,"battery.db"),"history");
        var paths=new UpdatePaths(installed,Path.Combine(fixture.Root,"cache")); var transaction=new InstallTransaction(paths);
        transaction.Apply(stage,"0.6.0","0.6.1"); transaction.Commit();
        Assert.Equal("new app",File.ReadAllText(Path.Combine(installed,"DeadWare.exe")));
        var previous=transaction.Previous()!; Assert.Equal("0.6.0",previous.Version);
        string rollback=transaction.PrepareRollback(Path.Combine(fixture.Root,"rollback-stage"));
        transaction.Apply(rollback,"0.6.1","0.6.0"); transaction.Commit();
        Assert.Equal("old app",File.ReadAllText(Path.Combine(installed,"DeadWare.exe")));
        Assert.Equal("keep",File.ReadAllText(Path.Combine(installed,"personal.txt")));
        Assert.Equal("history",File.ReadAllText(Path.Combine(fixture.Root,"battery.db")));
        Assert.Null(transaction.Previous()); // A newer backup is not presented as a downgrade.
    }
    [Fact] public void A_failed_replace_restores_already_replaced_files()
    {
        using var fixture=new UpdateFixture(); string installed=fixture.Package("app","0.6.0","old app");
        string stage=fixture.Package("stage","0.6.1","new app");
        var paths=new UpdatePaths(installed,Path.Combine(fixture.Root,"cache")); var transaction=new InstallTransaction(paths);
        fixture.AddFile(installed,"zz-locked.dll","old library"); fixture.AddFile(stage,"zz-locked.dll","new library");
        // Reading remains possible for backup/verification, but the library cannot be replaced.
        using(var locked=new FileStream(Path.Combine(installed,"zz-locked.dll"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var error=Record.Exception(()=>transaction.Apply(stage,"0.6.0","0.6.1"));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("old app",File.ReadAllText(Path.Combine(installed,"DeadWare.exe")));
        Assert.False(File.Exists(paths.PendingFile)); Packages.ValidateDirectory(installed,"0.6.0");
    }
    [Fact] public void Pending_transaction_can_be_recovered_after_an_interruption()
    {
        using var fixture=new UpdateFixture(); string installed=fixture.Package("app","0.6.0","old app");
        string stage=fixture.Package("stage","0.6.1","new app");
        var paths=new UpdatePaths(installed,Path.Combine(fixture.Root,"cache"));
        new InstallTransaction(paths).Apply(stage,"0.6.0","0.6.1");
        Assert.True(File.Exists(paths.PendingFile)); new InstallTransaction(paths).Recover();
        Packages.ValidateDirectory(installed,"0.6.0"); Assert.False(File.Exists(paths.PendingFile));
    }
    [Fact] public void Tampered_install_backup_and_unmanaged_collisions_are_rejected()
    {
        using var fixture=new UpdateFixture(); string installed=fixture.Package("app","0.6.0","old app");
        string stage=fixture.Package("stage","0.6.1","new app");
        var paths=new UpdatePaths(installed,Path.Combine(fixture.Root,"cache")); var transaction=new InstallTransaction(paths);
        File.WriteAllText(Path.Combine(installed,"DeadWare.exe"),"changed");
        Assert.Throws<InvalidDataException>(()=>transaction.Apply(stage,"0.6.0","0.6.1"));
        File.WriteAllText(Path.Combine(installed,"DeadWare.exe"),"old app");
        transaction.Apply(stage,"0.6.0","0.6.1"); transaction.Commit();
        var previous=transaction.Previous()!;
        File.WriteAllText(Path.Combine(paths.BackupDirectory(previous.Id),"DeadWare.exe"),"changed backup");
        Assert.Throws<InvalidDataException>(()=>transaction.Previous());
        Assert.Equal("new app",File.ReadAllText(Path.Combine(installed,"DeadWare.exe")));
    }
    [Theory] [InlineData("../escape")] [InlineData("C:/outside")] [InlineData("not-a-guid")]
    public void Backup_ids_cannot_escape_the_owned_cache(string id)
    {
        using var fixture=new UpdateFixture(); var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        Assert.Throws<InvalidDataException>(()=>paths.BackupDirectory(id));
    }
}
