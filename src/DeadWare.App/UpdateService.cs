using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using DeadWare.UpdateCore;

namespace DeadWare.App;

public sealed record UpdateState(string InstalledVersion)
{
    public string Status {get;init;}="Check GitHub for a newer normal release when you are ready.";
    public string LocalStatus {get;init;}="";
    public string LastOperation {get;init;}="";
    public ReleaseInfo? Release {get;init;}
    public BackupRecord? Previous {get;init;}
    public bool InstallationVerified {get;init;}
    public bool Busy {get;init;}
    public bool CanCancel {get;init;}
    public int Progress {get;init;}
    public DateTimeOffset? LastChecked {get;init;}
    public bool CanUpdate=>!Busy&&InstallationVerified&&Release is not null&&ReleasePolicy.IsNewer(Release,InstalledVersion);
    public bool CanRollback=>!Busy&&InstallationVerified&&Previous is not null;
}

/// <summary>On-demand updates only. File and network work never runs on the collector.</summary>
public sealed class UpdateService
{
    private readonly UpdatePaths paths;
    private readonly ReleaseClient releases;
    private readonly object gate=new();
    private UpdateState state;
    private int busy;
    public UpdateState State=>Volatile.Read(ref state);
    public event Action? Changed;
    public UpdateService(string version,string? install=null,HttpClient? http=null,string? cacheBase=null)
    {state=new(version);paths=new(install??AppContext.BaseDirectory,cacheBase);releases=new(http);}
    private void Publish(Func<UpdateState,UpdateState> change)
    {lock(gate)Volatile.Write(ref state,change(state));Changed?.Invoke();}
    public void ReportFailure(Exception error)=>Publish(s=>s with {Status="Update could not continue: "+error.Message});
    private bool Begin(string message)
    {
        if(Interlocked.CompareExchange(ref busy,1,0)!=0)return false;
        Publish(s=>s with {Busy=true,CanCancel=true,Progress=0,Status=message});return true;
    }
    private void End(){Interlocked.Exchange(ref busy,0);Publish(s=>s with {Busy=false,CanCancel=false});}
    public async Task ReadLocalAsync()
    {
        if(Volatile.Read(ref busy)!=0)return;
        try
        {
            var result=await Task.Run(()=>
            {
                UpdateCache.CleanupCompletedJobs(paths);
                if(!File.Exists(paths.PendingFile))Packages.ValidateDirectory(paths.Install,ReleasePolicy.VersionOf(State.InstalledVersion).ToString());
                var previous=new InstallTransaction(paths).Previous();
                var last=File.Exists(paths.ResultFile)?SafeFiles.ReadJson<UpdateResult>(paths.ResultFile):null;
                return(previous,last);
            });
            Publish(s=>s with {InstallationVerified=true,Previous=result.previous,
                LocalStatus=result.previous is null?"No earlier packaged installation is available for rollback yet.":$"Rollback available: {result.previous.Version}.",LastOperation=result.last?.Message??""});
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {Publish(s=>s with {InstallationVerified=false,Previous=null,LocalStatus="Install the complete updater-enabled package before updating from this folder. "+error.Message});}
    }
    public async Task CheckAsync(CancellationToken token)
    {
        if(!Begin("Checking GitHub…"))return;
        try
        {
            var release=await releases.CheckAsync(token);
            Publish(s=>s with {Release=release,LastChecked=DateTimeOffset.Now,Status=release is null?"No normal release has been published yet.":
                ReleasePolicy.IsNewer(release,s.InstalledVersion)?$"Version {release.Version} is available.":$"No newer release. GitHub has {release.Version}; installed {s.InstalledVersion}."});
        }
        catch(OperationCanceledException){Publish(s=>s with {Status=token.IsCancellationRequested?"Check cancelled.":"GitHub did not respond in time. Try again later."});}
        catch(HttpRequestException error) when(error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {Publish(s=>s with {Status="GitHub's request limit was reached. Try again later."});}
        catch(Exception error){ReportFailure(error);}
        finally{End();}
    }
    public async Task<UpdateJob?> PrepareAsync(bool rollback,CancellationToken token)
    {
        if(!Begin(rollback?"Verifying the previous installation…":"Downloading and verifying the update…"))return null;
        string? jobDirectory=null;
        try
        {
            string currentVersion=ReleasePolicy.VersionOf(State.InstalledVersion).ToString();
            if(!File.Exists(paths.PendingFile))await Task.Run(()=>Packages.ValidateDirectory(paths.Install,currentVersion),token);
            string id=Guid.NewGuid().ToString("N");jobDirectory=paths.JobDirectory(id);Directory.CreateDirectory(jobDirectory);
            string stage=SafeFiles.Child(jobDirectory,"package"),target;
            if(rollback)
            {
                var previous=await Task.Run(()=>new InstallTransaction(paths).Previous(),token)??throw new InvalidOperationException("No verified earlier version is available.");
                target=previous.Version;await Task.Run(()=>new InstallTransaction(paths).PrepareRollback(stage),token);
            }
            else
            {
                var release=State.Release??throw new InvalidOperationException("Check for an update first.");
                if(!ReleasePolicy.IsNewer(release,currentVersion))throw new InvalidOperationException("This release is not newer than the installed app.");
                target=release.Version.ToString();
                var progress=new Progress<int>(value=>Publish(s=>s with {Progress=value,Status=$"Downloading {target} · {value}%"}));
                string archive=SafeFiles.Child(jobDirectory,"download.zip");await releases.DownloadAsync(release,archive,progress,token);
                Publish(s=>s with {Status="Verifying package files…"});
                await Task.Run(()=>Packages.Extract(archive,stage,target),token);
            }
            token.ThrowIfCancellationRequested();
            using var process=Process.GetCurrentProcess();
            var parent=new ProcessIdentity(process.Id,process.StartTime.ToUniversalTime().Ticks,process.SessionId,StartupRegistration.CurrentSid,StartupRegistration.Executable);
            var job=new UpdateJob(id,paths.Key,paths.Install,stage,currentVersion,target,rollback?"rollback":"update",parent,
                Packages.Hash(SafeFiles.Child(stage,Packages.ManifestName)),DateTimeOffset.UtcNow);
            UpdateHandoff.ValidateJob(paths,job);SafeFiles.WriteJson(UpdateHandoff.JobFile(paths,id,"request.json"),job);return job;
        }
        catch(OperationCanceledException){CompleteFailedPreparation(jobDirectory,"Preparation cancelled.");Publish(s=>s with {Status="Preparation cancelled. The running app was not changed."});return null;}
        catch(Exception error){CompleteFailedPreparation(jobDirectory,error.Message);ReportFailure(error);return null;}
        finally{End();}
    }
    private static void CompleteFailedPreparation(string? directory,string message)
    {if(directory is null)return;try{SafeFiles.WriteJson(SafeFiles.Child(directory,"complete.json"),new UpdateResult(false,message,""));}catch(IOException){}catch(UnauthorizedAccessException){}}
    public async Task<bool> StartHelperAsync(UpdateJob job,CancellationToken token)
    {
        if(!Begin("Preparing the installer; monitoring continues…"))return false;
        try
        {
            await Task.Run(()=>
            {
                UpdateHandoff.ValidateJob(paths,job);
                var manifest=Packages.ReadManifest(paths.Install);
                string source=SafeFiles.Child(paths.Install,"DeadWare.Updater.exe");
                if(!manifest.Files.TryGetValue("DeadWare.Updater.exe",out var expected)||!Packages.Hash(source).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The installed updater could not be verified.");
                File.Copy(source,UpdateHandoff.JobFile(paths,job.Id,"helper.exe"),false);
            },token);
            var start=new ProcessStartInfo(UpdateHandoff.JobFile(paths,job.Id,"helper.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=paths.JobDirectory(job.Id)};
            foreach(string argument in new[]{"--key",paths.Key,"--job",job.Id})start.ArgumentList.Add(argument);
            using var helper=Process.Start(start)??throw new IOException("Windows did not start the updater.");
            var deadline=Stopwatch.StartNew();
            while(deadline.Elapsed<TimeSpan.FromSeconds(60))
            {
                token.ThrowIfCancellationRequested();
                if(helper.HasExited)throw new IOException("The installer stopped before it was ready.");
                string ready=UpdateHandoff.JobFile(paths,job.Id,"ready.json");
                if(File.Exists(ready))
                {
                    var response=SafeFiles.ReadJson<HelperReady>(ready);
                    if(response.Id!=job.Id)throw new InvalidDataException("The installer response does not match this operation.");
                    UpdateHandoff.RequireIdentity(new(helper.Id,helper.StartTime.ToUniversalTime().Ticks,helper.SessionId,job.Parent.UserSid,start.FileName),response.Process);
                    Publish(s=>s with {CanCancel=false,Status="Saving history and restarting…"});return true;
                }
                if(helper.HasExited)throw new IOException("The installer stopped before it was ready.");
                await Task.Delay(100,token);
            }
            throw new TimeoutException("The installer did not become ready.");
        }
        catch(Exception error)
        {
            try{File.WriteAllText(UpdateHandoff.JobFile(paths,job.Id,"cancel"),"");}catch(IOException){}catch(UnauthorizedAccessException){}
            if(error is OperationCanceledException)Publish(s=>s with {Status="Update cancelled. Monitoring continues."});else ReportFailure(error);
            End();return false;
        }
        // A ready helper holds the operation gate until normal Quit exits this process.
    }
    public static void AcknowledgeStartup(string[] args,string install,string version)
    {
        int index=Array.IndexOf(args,"--update-handoff");if(index<0)return;
        if(index+1>=args.Length)throw new InvalidDataException("Missing update handoff identifier.");
        var paths=new UpdatePaths(install);string id=UpdatePaths.Id(args[index+1]);
        var job=SafeFiles.ReadJson<UpdateJob>(UpdateHandoff.JobFile(paths,id,"request.json"));UpdateHandoff.ValidateJob(paths,job);
        if(job.ToVersion!=version)throw new InvalidDataException("The installed version does not match the update.");
        var ready=SafeFiles.ReadJson<HelperReady>(UpdateHandoff.JobFile(paths,id,"ready.json"));
        if(ready.Id!=id||ready.Process.UserSid!=job.Parent.UserSid||ready.Process.SessionId!=job.Parent.SessionId||
            !ready.Process.Executable.Equals(UpdateHandoff.JobFile(paths,id,"helper.exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The installer identity does not match this handoff.");
        using var helper=Process.GetProcessById(ready.Process.Pid);
        UpdateHandoff.RequireIdentity(ready.Process,new(helper.Id,helper.StartTime.ToUniversalTime().Ticks,helper.SessionId,StartupRegistration.CurrentSid,
            helper.MainModule?.FileName??throw new InvalidDataException("Updater process unavailable.")));
        using var self=Process.GetCurrentProcess();
        SafeFiles.WriteJson(UpdateHandoff.JobFile(paths,id,"started.json"),new StartupAck(id,version,
            new(self.Id,self.StartTime.ToUniversalTime().Ticks,self.SessionId,StartupRegistration.CurrentSid,StartupRegistration.Executable)));
    }
}
