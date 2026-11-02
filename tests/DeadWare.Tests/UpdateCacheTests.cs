using System.Diagnostics;
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
        string payload=Job(paths,TimeSpan.Zero,true),orphan=Job(paths,TimeSpan.FromDays(3),false);
        File.WriteAllText(paths.PendingFile,"interrupted");UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(5,Directory.GetDirectories(Path.Combine(paths.Root,"jobs")).Length);
        Assert.Equal(Full,Names(payload));Assert.Equal(Unfinished,Names(orphan));
    }
    [Fact] public void Cleanup_trims_payload_of_newest_completed_job()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string older=Job(paths,TimeSpan.FromHours(3),true),newest=Job(paths,TimeSpan.FromHours(1),true);
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.False(Directory.Exists(older));
        Assert.Equal(Metadata,Names(newest));Assert.Equal("request.json",File.ReadAllText(Path.Combine(newest,"request.json")));
    }
    [Fact] public void Cleanup_removes_orphaned_jobs_older_than_a_day()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string stale=Job(paths,TimeSpan.FromHours(25),false);
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.False(Directory.Exists(stale));
    }
    [Fact] public void Cleanup_leaves_unfinished_jobs_younger_than_a_day_untouched()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string fresh=Job(paths,TimeSpan.FromHours(23),false),started=Job(paths,TimeSpan.FromMinutes(5),false),done=Job(paths,TimeSpan.FromHours(1),true);
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(Unfinished,Names(fresh));Assert.Equal(Unfinished,Names(started));Assert.Equal(Metadata,Names(done));
    }
    [Fact] public void Cleanup_keeps_orphaned_job_whose_helper_is_locked()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string stale=Job(paths,TimeSpan.FromDays(2),false),other=Job(paths,TimeSpan.FromDays(2),false);
        using(new FileStream(Path.Combine(stale,"helper.exe"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(Unfinished,Names(stale));Assert.False(Directory.Exists(other));
    }
    [Fact] public void Cleanup_trims_newest_completed_job_but_keeps_a_locked_helper()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string newest=Job(paths,TimeSpan.FromHours(1),true);
        using(new FileStream(Path.Combine(newest,"helper.exe"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(Sorted("complete.json","helper.exe","ready.json","request.json","started.json"),Names(newest));
    }
    [Fact] public void Cleanup_skips_older_completed_job_with_corrupt_result_and_still_cleans_the_rest()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string oldest=Job(paths,TimeSpan.FromHours(3),true),corrupt=Job(paths,TimeSpan.FromHours(2),true),newest=Job(paths,TimeSpan.FromHours(1),true),stale=Job(paths,TimeSpan.FromDays(2),false);
        File.WriteAllText(Path.Combine(corrupt,"complete.json"),"{ not json");
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(Sorted("complete.json","download.zip","helper.exe","package","ready.json","request.json","started.json"),Names(corrupt));
        Assert.False(Directory.Exists(oldest));Assert.False(Directory.Exists(stale));Assert.Equal(Metadata,Names(newest));
    }
    [Fact] public void Cleanup_keeps_old_directory_whose_request_was_just_written()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string reused=Job(paths,TimeSpan.FromDays(3),false),stale=Job(paths,TimeSpan.FromDays(3),false);
        File.SetLastWriteTimeUtc(Path.Combine(reused,"request.json"),DateTime.UtcNow.AddMinutes(-5));
        UpdateCache.CleanupCompletedJobs(paths);
        Assert.Equal(Unfinished,Names(reused));Assert.False(Directory.Exists(stale));
    }
    [Fact] public void Cleanup_keeps_orphan_containing_a_junction_without_following_it()
    {
        using var fixture=new UpdateFixture();var paths=new UpdatePaths(Path.Combine(fixture.Root,"app"),Path.Combine(fixture.Root,"cache"));
        string linked=Job(paths,TimeSpan.FromDays(3),false),stale=Job(paths,TimeSpan.FromDays(3),false),outside=Path.Combine(fixture.Root,"outside");
        Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"battery.db"),"history");
        string link=Path.Combine(linked,"package","link");Junction(link,outside);
        try
        {
            UpdateCache.CleanupCompletedJobs(paths);
            Assert.Equal(Unfinished,Names(linked));Assert.True(Directory.Exists(link));
            Assert.Equal("history",File.ReadAllText(Path.Combine(outside,"battery.db")));Assert.False(Directory.Exists(stale));
        }
        finally{if(Directory.Exists(link))Directory.Delete(link);}
    }
    private static string[] Sorted(params string[] names)=>names.Order().ToArray();
    private static readonly string[] Metadata=Sorted("complete.json","ready.json","request.json","started.json");
    private static readonly string[] Unfinished=Sorted("download.zip","helper.exe","package","ready.json","request.json","started.json");
    private static readonly string[] Full=Sorted("complete.json","download.zip","helper.exe","package","ready.json","request.json","started.json");
    private static string[] Names(string job)=>Directory.GetFileSystemEntries(job).Select(entry=>Path.GetFileName(entry)).Order().ToArray();
    private static string Job(UpdatePaths paths,TimeSpan age,bool completed)
    {
        string job=paths.JobDirectory(Guid.NewGuid().ToString("N")),package=Path.Combine(job,"package","nested");Directory.CreateDirectory(package);
        foreach(string name in new[]{"request.json","ready.json","started.json","download.zip","helper.exe"})File.WriteAllText(Path.Combine(job,name),name);
        File.WriteAllText(Path.Combine(package,"DeadWare.exe"),"app");
        if(completed)SafeFiles.WriteJson(Path.Combine(job,"complete.json"),new UpdateResult(true,"Done","0.6.0"));
        Directory.SetCreationTimeUtc(job,DateTime.UtcNow-age);File.SetLastWriteTimeUtc(Path.Combine(job,"request.json"),DateTime.UtcNow-age);return job;
    }
    private static void Junction(string link,string target)
    {
        using var process=Process.Start(new ProcessStartInfo("cmd.exe",$"/c mklink /J \"{link}\" \"{target}\""){CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true})!;
        process.StandardOutput.ReadToEnd();process.WaitForExit();Assert.Equal(0,process.ExitCode);
    }
}
