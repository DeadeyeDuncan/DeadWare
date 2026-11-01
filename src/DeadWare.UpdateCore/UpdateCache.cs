namespace DeadWare.UpdateCore;

public static class UpdateCache
{
    /// <summary>An unfinished job idle this long can never run: <see cref="UpdateHandoff.ValidateJob"/> rejects handoffs older than one day.</summary>
    private const int OrphanAgeHours=24;

    /// <summary>Best effort: keep only the metadata of the latest completed job for diagnostics and drop abandoned jobs; never touch a recent unfinished job or an interrupted operation, and leave alone any job that cannot be verified safe.</summary>
    public static void CleanupCompletedJobs(UpdatePaths paths)
    {
        string jobs=SafeFiles.Child(paths.Root,"jobs");
        if(!Directory.Exists(jobs)||File.Exists(paths.PendingFile))return;
        try
        {
            using var gate=new FileStream(SafeFiles.Child(paths.Root,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(File.Exists(paths.PendingFile))return;
            var all=Directory.GetDirectories(jobs).Where(dir=>Guid.TryParseExact(Path.GetFileName(dir),"N",out _)).ToArray();
            var completed=all.Where(dir=>File.Exists(Path.Combine(dir,"complete.json"))).OrderByDescending(Directory.GetCreationTimeUtc).ToArray();
            DateTime cutoff=DateTime.UtcNow.AddHours(-OrphanAgeHours);
            var orphaned=all.Except(completed).Where(dir=>LastActive(dir)<cutoff).ToArray();
            foreach(string directory in completed.Take(1))Clean(directory,true);
            foreach(string directory in completed.Skip(1).Concat(orphaned))Clean(directory,false);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }

    /// <summary>A job is as young as its directory or its request file, whichever is newer, so a reused or freshly written job is never stale.</summary>
    private static DateTime LastActive(string directory)=>new[]{Directory.GetCreationTimeUtc(directory),File.GetLastWriteTimeUtc(Path.Combine(directory,"request.json"))}.Max();

    /// <summary>Trim a job to its small metadata files, or delete it entirely. A locked helper.exe is never deleted; it keeps a whole job and stays behind in a trimmed one. A job with a link or junction or an unreadable result is skipped, never deleted.</summary>
    private static void Clean(string directory,bool keepMetadata)
    {
        try
        {
            SafeFiles.NoLinks(directory);
            if(!keepMetadata&&File.Exists(Path.Combine(directory,"complete.json")))SafeFiles.ReadJson<UpdateResult>(SafeFiles.Child(directory,"complete.json"));
            string helper=SafeFiles.Child(directory,"helper.exe");
            bool idle=!File.Exists(helper)||Unlocked(helper);
            if(!keepMetadata){if(idle)Remove(directory);return;}
            Remove(SafeFiles.Child(directory,"download.zip"));Remove(SafeFiles.Child(directory,"package"));
            if(idle)Remove(helper);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}catch(InvalidDataException){}
    }

    private static bool Unlocked(string file)
    {
        try{using var probe=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return true;}
        catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
    }

    private static void Remove(string path)
    {
        if(Directory.Exists(path))
        {
            SafeFiles.NoLinks(path);
            foreach(string entry in Directory.EnumerateFileSystemEntries(path,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0}))SafeFiles.NoLinks(entry);
            Directory.Delete(path,true);
        }
        else if(File.Exists(path)){SafeFiles.NoLinks(path);File.Delete(path);}
    }
}
