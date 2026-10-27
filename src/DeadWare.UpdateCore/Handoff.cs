namespace DeadWare.UpdateCore;

public sealed record ProcessIdentity(int Pid,long StartTicks,int SessionId,string UserSid,string Executable);
public sealed record UpdateJob(string Id,string Key,string InstallDirectory,string StageDirectory,string FromVersion,string ToVersion,
    string Operation,ProcessIdentity Parent,string ManifestHash,DateTimeOffset Created);
public sealed record HelperReady(string Id,ProcessIdentity Process);
public sealed record StartupAck(string Id,string Version,ProcessIdentity Process);

public static class UpdateHandoff
{
    public static string JobFile(UpdatePaths paths,string id,string name)=>SafeFiles.Child(paths.JobDirectory(id),name);
    public static void RequireIdentity(ProcessIdentity expected,ProcessIdentity actual)
    {
        if(expected is null||actual is null||expected.Pid<=0||expected.Pid!=actual.Pid||expected.StartTicks!=actual.StartTicks||expected.SessionId!=actual.SessionId||
            expected.UserSid!=actual.UserSid||!Path.GetFullPath(expected.Executable).Equals(Path.GetFullPath(actual.Executable),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update process identity no longer matches.");
    }
    public static void ValidateJob(UpdatePaths paths,UpdateJob job)
    {
        UpdatePaths.Id(job.Id);
        if(job.Parent is null||job.Key!=paths.Key||!Path.GetFullPath(job.InstallDirectory).Equals(paths.Install,StringComparison.OrdinalIgnoreCase)||
            !Path.GetFullPath(job.StageDirectory).Equals(SafeFiles.Child(paths.JobDirectory(job.Id),"package"),StringComparison.OrdinalIgnoreCase)||
            !Path.GetFullPath(job.Parent.Executable).Equals(SafeFiles.Child(paths.Install,"DeadWare.exe"),StringComparison.OrdinalIgnoreCase)||
            job.Parent.Pid<=0||string.IsNullOrWhiteSpace(job.Parent.UserSid)||job.Created<DateTimeOffset.UtcNow.AddDays(-1)||job.Created>DateTimeOffset.UtcNow.AddMinutes(5)||
            !ReleasePolicy.HashPattern().IsMatch(job.ManifestHash)||!Packages.Hash(SafeFiles.Child(job.StageDirectory,Packages.ManifestName)).Equals(job.ManifestHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid or stale update handoff.");
        var from=ReleasePolicy.VersionOf(job.FromVersion);var to=ReleasePolicy.VersionOf(job.ToVersion);
        if(!(job.Operation=="update"&&to>from||job.Operation=="rollback"&&to<from))throw new InvalidDataException("The requested version does not match this operation.");
        Packages.ReadManifest(job.StageDirectory,job.ToVersion);
    }
}
