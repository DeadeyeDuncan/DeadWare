using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeadWare.UpdateCore;

/// <summary>Directory and File: a protected folder, its subfolders and files (inheritable rules count on folders). Ancestor: a parent up to the volume root (only renames and permission changes count).</summary>
public enum PathKind{Directory,File,Ancestor}
public sealed record AclRule(string Sid,int Rights,bool Allow,bool InheritOnly);
public sealed record PathSecurity(string Path,PathKind Kind,string? Owner,IReadOnlyList<AclRule> Rules,bool ReparsePoint,string? Error=null,bool Network=false);
public sealed record LocationProtection(bool Protected,string? Reason=null);

/// <summary>Decides whether only SYSTEM, Administrators and TrustedInstaller can change a folder tree and the parents above it.</summary>
public static class InstallLocation
{
    // 0x100 FILE_WRITE_ATTRIBUTES is enough to turn an empty folder into a mount point.
    public const int DirectoryRights=0x2|0x4|0x40|0x100|0x10000|0x40000|0x80000|0x40000000|0x10000000;
    public const int FileRights=0x2|0x4|0x100|0x10000|0x40000|0x80000|0x40000000|0x10000000;
    public const int AncestorRights=0x40|0x10000|0x40000|0x80000|0x10000000;
    private static readonly string[] TrustedSids=["S-1-5-18","S-1-5-32-544","S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
    private static bool Trusted(string? sid)=>sid is not null&&TrustedSids.Contains(sid,StringComparer.Ordinal);
    // OWNER RIGHTS and CREATOR OWNER grant the (future) owner; only trusted principals can own or create in a protected tree.
    private static bool Trusted(string sid,string? owner)=>Trusted(sid)||sid is "S-1-3-4" or "S-1-3-0"&&Trusted(owner);

    /// <summary>Protected only when every entry is local, readable, not a link, trusted-owned and grants no untrusted Allow rule a write right for its kind; the reason names the first offending path.</summary>
    public static LocationProtection Evaluate(IEnumerable<PathSecurity> entries)
    {
        bool any=false;
        foreach(var entry in entries)
        {
            any=true;
            // On a network location SYSTEM and Administrators are the server's accounts, not this computer's.
            if(entry.Network||entry.Path.StartsWith(@"\\",StringComparison.Ordinal))return new(false,$"{entry.Path} is not on a local drive.");
            if(entry.Error is not null)return new(false,$"Permissions for {entry.Path} could not be read: {entry.Error}");
            if(entry.ReparsePoint)return new(false,$"{entry.Path} is a link or junction.");
            if(!Trusted(entry.Owner))return new(false,$"{entry.Path} is not owned by SYSTEM, Administrators or TrustedInstaller.");
            int mask=entry.Kind switch {PathKind.Directory=>DirectoryRights,PathKind.File=>FileRights,_=>AncestorRights};
            // Inherit-only rules shape only future children: they count on protected folders (DirectoryRights covers FileRights) and nowhere else.
            foreach(var rule in entry.Rules)
                if(rule.Allow&&(!rule.InheritOnly||entry.Kind==PathKind.Directory)&&(rule.Rights&mask)!=0&&!Trusted(rule.Sid,entry.Owner))
                    return new(false,$"{entry.Path} can be changed by {rule.Sid}.");
        }
        return any?new(true):new(false,"No folder permissions were checked.");
    }

    /// <summary>Evaluates <see cref="Read"/> lazily, so an unprotected tree stops at its first offending path.</summary>
    [SupportedOSPlatform("windows")]
    public static LocationProtection Check(string directory,int strictParents=0)=>Evaluate(Enumerate(directory,strictParents));

    /// <summary>Reads <paramref name="directory"/> and everything beneath it as Directory/File entries, its nearest <paramref name="strictParents"/> parents as Directory entries (without their other children), and the remaining parents up to the volume root as Ancestor entries.</summary>
    [SupportedOSPlatform("windows")]
    public static List<PathSecurity> Read(string directory,int strictParents=0)=>Enumerate(directory,strictParents).ToList();

    [SupportedOSPlatform("windows")]
    private static IEnumerable<PathSecurity> Enumerate(string directory,int strictParents)
    {
        string? root=null,invalid=null;bool network=false;
        try {root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));network=Remote(root);}catch(Exception e) {root=null;invalid=e.Message;}
        if(root is null||network) {yield return new(root??directory,PathKind.Directory,null,[],false,invalid,network);yield break;}
        var pending=new Stack<DirectoryInfo>();pending.Push(new DirectoryInfo(root));
        var options=new EnumerationOptions{AttributesToSkip=0,IgnoreInaccessible=false};
        while(pending.TryPop(out var folder))
        {
            var entry=Entry(folder,PathKind.Directory);yield return entry;
            if(entry.Error is not null||entry.ReparsePoint)continue;
            FileSystemInfo[] children;string? error=null;
            try {children=folder.GetFileSystemInfos("*",options);}catch(Exception e) {children=[];error=e.Message;}
            if(error is not null)yield return new(folder.FullName,PathKind.Directory,null,[],false,error);
            foreach(var child in children)if(child is DirectoryInfo subfolder)pending.Push(subfolder);else yield return Entry(child,PathKind.File);
        }
        int depth=0;
        for(string? parent=Path.GetDirectoryName(root);parent is not null;parent=Path.GetDirectoryName(parent))yield return ReadEntry(parent,depth++<strictParents?PathKind.Directory:PathKind.Ancestor);
    }

    /// <summary>Reads one path's owner, Allow/Deny rules (inherited included) and link flag; any failure is returned as <see cref="PathSecurity.Error"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static PathSecurity ReadEntry(string path,PathKind kind)
    {
        try {return Remote(Path.GetFullPath(path))?new(path,kind,null,[],false,Network:true):Entry(kind==PathKind.File?new FileInfo(path):new DirectoryInfo(path),kind);}
        catch(Exception e) {return new(path,kind,null,[],false,e.Message);}
    }

    /// <summary>UNC and device paths (\\server\share, \\?\UNC\...) or a mapped network drive; decided without touching the location.</summary>
    [SupportedOSPlatform("windows")]
    private static bool Remote(string fullPath)=>fullPath.StartsWith(@"\\",StringComparison.Ordinal)||new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType==DriveType.Network;

    [SupportedOSPlatform("windows")]
    private static PathSecurity Entry(FileSystemInfo info,PathKind kind)
    {
        try
        {
            if(!info.Exists)return new(info.FullName,kind,null,[],false,"The path does not exist.");
            const AccessControlSections sections=AccessControlSections.Owner|AccessControlSections.Access;
            FileSystemSecurity security=info is DirectoryInfo folder?folder.GetAccessControl(sections):((FileInfo)info).GetAccessControl(sections);
            var rules=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(rule=>new AclRule(rule.IdentityReference.Value,(int)rule.FileSystemRights,rule.AccessControlType==AccessControlType.Allow,(rule.PropagationFlags&PropagationFlags.InheritOnly)!=0)).ToArray();
            return new(info.FullName,kind,security.GetOwner(typeof(SecurityIdentifier))?.Value,rules,(info.Attributes&FileAttributes.ReparsePoint)!=0);
        }
        catch(Exception e) {return new(info.FullName,kind,null,[],false,e.Message);}
    }
}
