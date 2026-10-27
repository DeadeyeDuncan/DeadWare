using System.Security.Cryptography;
using System.Text;

namespace DeadWare.UpdateCore;

public sealed record BackupRecord(string Id, string Version, string ManifestHash);
public sealed record PendingInstall(string Install, BackupRecord Previous, string TargetVersion, Dictionary<string,string> TargetFiles);
public sealed record UpdateResult(bool Success, string Message, string Version);

public sealed class UpdatePaths
{
    public string Install { get; }
    public string Key { get; }
    public string Root { get; }
    public string PendingFile => SafeFiles.Child(Root,"pending.json");
    public string RollbackFile => SafeFiles.Child(Root,"rollback.json");
    public string ResultFile => SafeFiles.Child(Root,"result.json");
    public static string CacheBase => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DeadWare","updates");
    public UpdatePaths(string install, string? cacheBase = null)
    {
        Install = SafeFiles.DirectoryPath(install);
        Key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Install.ToUpperInvariant()))).ToLowerInvariant();
        Root = RootForKey(Key,cacheBase);
    }
    public static string RootForKey(string key,string? cacheBase=null)
    {
        if(!ReleasePolicy.HashPattern().IsMatch(key))throw new InvalidDataException("Invalid installation key.");
        return SafeFiles.Child(cacheBase??CacheBase,key.ToLowerInvariant());
    }
    public static string Id(string id)=>Guid.TryParseExact(id,"N",out var value)?value.ToString("N"):throw new InvalidDataException("Invalid update identifier.");
    public string JobDirectory(string id)=>SafeFiles.Child(Root,"jobs/"+Id(id));
    public string BackupDirectory(string id)=>SafeFiles.Child(Root,"previous/"+Id(id));
}

/// <summary>Verified per-file replacement. User data and unmanaged application files are never copied or removed.</summary>
public sealed class InstallTransaction(UpdatePaths paths)
{
    private readonly UpdatePaths paths=paths;
    public BackupRecord? Previous()
    {
        if(!File.Exists(paths.RollbackFile)&&!File.Exists(paths.PendingFile))return null;
        var current=Packages.ReadManifest(paths.Install);
        var previous=File.Exists(paths.PendingFile)?Pending().Previous:SafeFiles.ReadJson<BackupRecord>(paths.RollbackFile);ValidateBackup(previous);
        return ReleasePolicy.VersionOf(previous.Version)<ReleasePolicy.VersionOf(current.Version)?previous:null;
    }
    public string PrepareRollback(string destination)
    {
        var previous=Previous()??throw new InvalidOperationException("No verified earlier installation is available.");
        CopySnapshot(paths.BackupDirectory(previous.Id),destination,Packages.ReadManifest(paths.BackupDirectory(previous.Id),previous.Version));
        Packages.ValidateDirectory(destination,previous.Version);return destination;
    }
    public void Apply(string stage,string currentVersion,string targetVersion)
    {
        if(File.Exists(paths.PendingFile))throw new IOException("An interrupted update requires recovery first.");
        stage=SafeFiles.DirectoryPath(stage);
        if(stage.Equals(paths.Install,StringComparison.OrdinalIgnoreCase)||stage.StartsWith(paths.Install+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||paths.Install.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installation and staging must be separate directories.");
        var old=Packages.ValidateDirectory(paths.Install,currentVersion);var next=Packages.ValidateDirectory(stage,targetVersion);
        foreach(string file in next.Files.Keys)
            if(!old.Files.ContainsKey(file)&&File.Exists(SafeFiles.Child(paths.Install,file)))throw new InvalidDataException("An update would overwrite an unmanaged file: "+file);
        string id=Guid.NewGuid().ToString("N"),backup=paths.BackupDirectory(id);
        CopySnapshot(paths.Install,backup,old);Packages.ValidateDirectory(backup,currentVersion);
        var record=new BackupRecord(id,currentVersion,Packages.Hash(Path.Combine(backup,Packages.ManifestName)));
        var pending=new PendingInstall(paths.Install,record,targetVersion,next.Files);
        SafeFiles.WriteJson(paths.PendingFile,pending);
        try
        {
            CopySnapshot(stage,paths.Install,next);
            foreach(string removed in old.Files.Keys.Except(next.Files.Keys,StringComparer.OrdinalIgnoreCase))File.Delete(SafeFiles.Child(paths.Install,removed));
            Packages.ValidateDirectory(paths.Install,targetVersion);
        }
        catch
        {
            Recover();throw;
        }
    }
    public void Commit()
    {
        var pending=Pending();Packages.ValidateDirectory(paths.Install,pending.TargetVersion);ValidateBackup(pending.Previous);
        BackupRecord? old=File.Exists(paths.RollbackFile)?SafeFiles.ReadJson<BackupRecord>(paths.RollbackFile):null;
        SafeFiles.WriteJson(paths.RollbackFile,pending.Previous);File.Delete(paths.PendingFile);
        if(old is not null&&old.Id!=pending.Previous.Id)
        {
            // Cleanup only a validated snapshot owned by this installation. A cleanup error
            // must never turn a successful install into a rollback.
            try{ValidateBackup(old);DeleteOwnedBackup(old.Id);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    public void Recover()
    {
        if(!File.Exists(paths.PendingFile))return;
        var pending=Pending();ValidateBackup(pending.Previous);
        string backup=paths.BackupDirectory(pending.Previous.Id);var old=Packages.ReadManifest(backup,pending.Previous.Version);
        foreach(var added in pending.TargetFiles.Where(p=>!old.Files.ContainsKey(p.Key)))
        {
            string file=SafeFiles.Child(paths.Install,added.Key);
            if(!File.Exists(file))continue;
            if(!Packages.Hash(file).Equals(added.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A newly installed file was modified; automatic recovery stopped.");
            File.Delete(file);
        }
        CopySnapshot(backup,paths.Install,old);Packages.ValidateDirectory(paths.Install,pending.Previous.Version);File.Delete(paths.PendingFile);
        try
        {
            var referenced=File.Exists(paths.RollbackFile)?SafeFiles.ReadJson<BackupRecord>(paths.RollbackFile):null;
            if(referenced is null||!string.Equals(referenced.Id,pending.Previous.Id,StringComparison.OrdinalIgnoreCase))DeleteOwnedBackup(pending.Previous.Id);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private PendingInstall Pending()
    {
        var pending=SafeFiles.ReadJson<PendingInstall>(paths.PendingFile);
        if(pending.Previous is null||!Path.GetFullPath(pending.Install).Equals(paths.Install,StringComparison.OrdinalIgnoreCase)||pending.TargetFiles is null||pending.TargetFiles.Count>2048)
            throw new InvalidDataException("Invalid interrupted-update journal.");
        ReleasePolicy.VersionOf(pending.TargetVersion);
        foreach(var file in pending.TargetFiles){SafeFiles.RelativeName(file.Key);if(!ReleasePolicy.HashPattern().IsMatch(file.Value))throw new InvalidDataException("Invalid journal hash.");}
        return pending;
    }
    private void ValidateBackup(BackupRecord record)
    {
        if(record is null)throw new InvalidDataException("Missing rollback metadata.");
        ReleasePolicy.VersionOf(record.Version);
        string backup=paths.BackupDirectory(record.Id);
        if(string.IsNullOrWhiteSpace(record.ManifestHash)||!ReleasePolicy.HashPattern().IsMatch(record.ManifestHash)||!Packages.Hash(SafeFiles.Child(backup,Packages.ManifestName)).Equals(record.ManifestHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rollback verification failed.");
        Packages.ValidateDirectory(backup,record.Version);
    }
    private static void CopySnapshot(string source,string destination,PackageManifest manifest)
    {
        foreach(var pair in manifest.Files.OrderBy(p=>p.Key,StringComparer.Ordinal))CopyFile(source,destination,pair.Key,pair.Value);
        CopyFile(source,destination,Packages.ManifestName,Packages.Hash(SafeFiles.Child(source,Packages.ManifestName)));
    }
    private static void CopyFile(string source,string destination,string relative,string hash)
    {
        string input=SafeFiles.Child(source,relative),output=SafeFiles.Child(destination,relative);
        if(File.Exists(output)&&Packages.Hash(output).Equals(hash,StringComparison.OrdinalIgnoreCase))return;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary=output+".dw-"+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.Copy(input,temporary,false);
            if(!Packages.Hash(temporary).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("An update source file changed during copying.");
            File.Move(temporary,output,true);
        }
        finally{if(File.Exists(temporary)){SafeFiles.NoLinks(temporary);File.Delete(temporary);}}
    }
    private void DeleteOwnedBackup(string id)
    {
        string root=paths.BackupDirectory(id);SafeFiles.NoLinks(root);
        var options=new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0};
        foreach(string item in Directory.EnumerateFileSystemEntries(root,"*",options))SafeFiles.NoLinks(item);
        Directory.Delete(root,true);
    }
}
