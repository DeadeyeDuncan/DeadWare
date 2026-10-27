using DeadWare.UpdateCore;
using Xunit;
namespace DeadWare.Tests;

public class UpdateCacheTests
{
    [Fact] public void Cleanup_only_removes_older_completed_owned_jobs()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string old=paths.JobDirectory(Guid.NewGuid().ToString("N")),recent=paths.JobDirectory(Guid.NewGuid().ToString("N")),active=paths.JobDirectory(Guid.NewGuid().ToString("N"));
        foreach(string folder in new[]{old,recent,active})Directory.CreateDirectory(folder);
        foreach(string folder in new[]{old,recent})SafeFiles.WriteJson(Path.Combine(folder,"complete.json"),new UpdateResult(true,"Done","0.6.0"));
        Directory.SetCreationTimeUtc(old,DateTime.UtcNow.AddHours(-2));Directory.SetCreationTimeUtc(recent,DateTime.UtcNow.AddHours(-1));
        string outside=Path.Combine(fixture.Root,"battery.db");File.WriteAllText(outside,"history");
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.False(Directory.Exists(old));Assert.True(Directory.Exists(recent));Assert.True(Directory.Exists(active));
        Assert.Equal("history",File.ReadAllText(outside));
    }
    [Fact] public void Pending_recovery_prevents_cache_cleanup()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        foreach(int i in Enumerable.Range(0,3)){string job=paths.JobDirectory(Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);SafeFiles.WriteJson(Path.Combine(job,"complete.json"),new UpdateResult(true,"Done","0.6.0"));}
        File.WriteAllText(paths.PendingFile,"interrupted");UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(3,Directory.GetDirectories(Path.Combine(paths.Root,"jobs")).Length);
    }
}
