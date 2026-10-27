using System.Diagnostics;
using System.Security.Principal;
using DeadWare.UpdateCore;

return await RunAsync(args);

static ProcessIdentity Identity(Process process,string sid)=>new(process.Id,process.StartTime.ToUniversalTime().Ticks,process.SessionId,sid,
    process.MainModule?.FileName??throw new InvalidDataException("The process executable is unavailable."));

static void WriteResult(UpdatePaths paths,UpdateJob job,UpdateResult result)
{SafeFiles.WriteJson(paths.ResultFile,result);SafeFiles.WriteJson(UpdateHandoff.JobFile(paths,job.Id,"complete.json"),result);}

static async Task<int> RunAsync(string[] args)
{
    UpdatePaths? paths=null;UpdateJob? job=null;Process? replacement=null;bool applied=false,parentExited=false;
    try
    {
        if(args.Length!=4||args[0]!="--key"||args[2]!="--job")throw new ArgumentException("Invalid updater arguments.");
        string root=UpdatePaths.RootForKey(args[1]),id=UpdatePaths.Id(args[3]);
        job=SafeFiles.ReadJson<UpdateJob>(SafeFiles.Child(root,"jobs/"+id+"/request.json"));
        paths=new UpdatePaths(job.InstallDirectory);UpdateHandoff.ValidateJob(paths,job);
        if(paths.Key!=args[1]||job.Id!=id)throw new InvalidDataException("Update handoff mismatch.");
        Directory.CreateDirectory(paths.Root);
        using var installationLock=new FileStream(SafeFiles.Child(paths.Root,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        using var identity=WindowsIdentity.GetCurrent();string sid=identity.User?.Value??throw new InvalidDataException("Windows user unavailable.");
        using var self=Process.GetCurrentProcess();using var parent=Process.GetProcessById(job.Parent.Pid);
        UpdateHandoff.RequireIdentity(job.Parent,Identity(parent,sid));
        if(parent.SessionId!=self.SessionId||!self.MainModule!.FileName!.Equals(UpdateHandoff.JobFile(paths,id,"helper.exe"),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The updater is outside its staged handoff.");
        foreach(var process in Process.GetProcessesByName("DeadWare"))
        {
            using(process)
            {
                if(process.Id==parent.Id)continue;
                if(process.MainModule?.FileName?.Equals(job.Parent.Executable,StringComparison.OrdinalIgnoreCase)==true)
                    throw new IOException("Another DeadWare instance is using this installation.");
            }
        }
        Packages.ValidateDirectory(job.StageDirectory,job.ToVersion);
        if(!File.Exists(paths.PendingFile))Packages.ValidateDirectory(paths.Install,job.FromVersion);
        using(var writeProbe=new FileStream(SafeFiles.Child(paths.Install,".deadware-write-"+id),FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){}
        SafeFiles.WriteJson(UpdateHandoff.JobFile(paths,id,"ready.json"),new HelperReady(id,Identity(self,sid)));
        var deadline=Stopwatch.StartNew();
        while(!parent.HasExited)
        {
            if(File.Exists(UpdateHandoff.JobFile(paths,id,"cancel"))||deadline.Elapsed>TimeSpan.FromSeconds(60))
                throw new OperationCanceledException("DeadWare did not finish its normal shutdown. App files were not replaced.");
            await Task.Delay(100);
        }
        if(File.Exists(UpdateHandoff.JobFile(paths,id,"cancel")))throw new OperationCanceledException("Update cancelled before file replacement.");
        parentExited=true;
        UpdateHandoff.ValidateJob(paths,job);
        var transaction=new InstallTransaction(paths);
        if(File.Exists(paths.PendingFile))transaction.Recover();
        string current=Packages.ReadManifest(paths.Install).Version;
        if(current!=job.ToVersion){transaction.Apply(job.StageDirectory,job.FromVersion,job.ToVersion);applied=true;}
        var start=new ProcessStartInfo(SafeFiles.Child(paths.Install,"DeadWare.exe")){UseShellExecute=false,WorkingDirectory=paths.Install};
        start.ArgumentList.Add("--update-handoff");start.ArgumentList.Add(id);
        replacement=Process.Start(start)??throw new IOException("Windows did not start the updated app.");
        deadline.Restart();bool acknowledged=false;
        while(deadline.Elapsed<TimeSpan.FromSeconds(60))
        {
            string response=UpdateHandoff.JobFile(paths,id,"started.json");
            if(File.Exists(response))
            {
                var ack=SafeFiles.ReadJson<StartupAck>(response);
                if(ack.Id!=id||ack.Version!=job.ToVersion)throw new InvalidDataException("The new app did not confirm its expected version.");
                UpdateHandoff.RequireIdentity(Identity(replacement,sid),ack.Process);acknowledged=true;break;
            }
            if(replacement.HasExited)break;
            await Task.Delay(100);
        }
        if(!acknowledged)throw new IOException("The new app did not confirm startup.");
        if(applied)transaction.Commit();
        try{WriteResult(paths,job,new UpdateResult(true,$"{(job.Operation=="rollback"?"Rolled back":"Updated")} to {job.ToVersion}.",job.ToVersion));}catch(IOException){}catch(UnauthorizedAccessException){}
        return 0;
    }
    catch(Exception error)
    {
        if(paths is not null&&job is not null)
        {
            string message="Update stopped: "+error.Message;
            try
            {
                if(parentExited&&replacement?.HasExited!=false)
                {
                    new InstallTransaction(paths).Recover();
                    message="The update failed. The previous installation was restored. "+error.Message;
                    Process.Start(new ProcessStartInfo(SafeFiles.Child(paths.Install,"DeadWare.exe")){UseShellExecute=false,WorkingDirectory=paths.Install})?.Dispose();
                }
                else if(applied)message="The new app did not confirm startup. Its files were left in place because it is still running. Quit it normally, then use Roll back & restart; the previous installation is preserved.";
                WriteResult(paths,job,new UpdateResult(false,message,job.FromVersion));
            }
            catch(Exception recovery){try{SafeFiles.WriteJson(paths.ResultFile,new UpdateResult(false,message+" Recovery needs attention: "+recovery.Message,job.FromVersion));}catch{}}
        }
        return 1;
    }
    finally{replacement?.Dispose();}
}
