namespace DeadWare.UpdateCore;

public static class UpdateCache
{
    /// <summary>Keep the latest completed job for diagnostics; never touch an active or interrupted operation.</summary>
    public static void CleanupCompletedJobs(UpdatePaths paths)
    {
        string jobs=SafeFiles.Child(paths.Root,"jobs");
        if(!Directory.Exists(jobs)||File.Exists(paths.PendingFile))return;
        try
        {
            using var gate=new FileStream(SafeFiles.Child(paths.Root,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var completed=Directory.GetDirectories(jobs).Where(dir=>Guid.TryParseExact(Path.GetFileName(dir),"N",out _)&&File.Exists(Path.Combine(dir,"complete.json")))
                .OrderByDescending(Directory.GetCreationTimeUtc).Skip(1).ToArray();
            foreach(string directory in completed)
            {
                SafeFiles.NoLinks(directory);SafeFiles.ReadJson<UpdateResult>(SafeFiles.Child(directory,"complete.json"));
                string helper=SafeFiles.Child(directory,"helper.exe");
                if(File.Exists(helper)){using var probe=new FileStream(helper,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
                foreach(string entry in Directory.EnumerateFileSystemEntries(directory,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0}))SafeFiles.NoLinks(entry);
                Directory.Delete(directory,true);
            }
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
