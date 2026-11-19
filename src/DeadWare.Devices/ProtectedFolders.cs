using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

// A folder the policy refused. Message is plain words for the window: the folder at fault, InstallLocation's reason and the remedy
// (polish item 7). Folder is that folder, and Reason is InstallLocation's account of why.
internal sealed class UnprotectedFolderException(string folder,string? reason,string message):Exception(message)
{
    public string Folder{get;}=folder;
    public string? Reason{get;}=reason;
}

// OpenRGB loads plugin DLLs from its config folder and the host runs elevated, so an elevated host only uses a folder ordinary programs cannot write.
[SupportedOSPlatform("windows")]
internal static class ProtectedFolders
{
    // openrgb (and logs) sit directly under %ProgramData%\DeadWare, as UpdateCore's updates folder does: DeadWare is checked as a protected
    // folder and %ProgramData% and above as parents, the depth ProtectedRoot.VerifyExisting uses for updates (Check(updates,1)).
    // %ProgramData% itself lets Users create folders, so a depth of 2 would refuse every stock Windows installation.
    internal const int StrictParents=1;

    // Polish item 7 (B4/B13/B18): the folder that failed, which is %ProgramData%\DeadWare itself when that is at fault, so the remedy works.
    internal static string Refusal(string folder,string? reason)
        =>$"{folder} isn't restricted to administrators ({Clause(reason)}), so DeadWare won't start OpenRGB. Remove that folder, or give only administrators write access to it, then turn lighting off and on.";
    internal static string LogRefusal(string folder,string? reason)
        =>$"{folder} isn't restricted to administrators ({Clause(reason)}), so the lighting helper running as administrator keeps no log.";
    // InstallLocation's reasons are sentences; inside the parentheses they lose the full stop.
    static string Clause(string? reason)=>reason is {Length:>0}?reason.TrimEnd('.'):"its permissions couldn't be checked";

    public static string OpenRgbConfigDirectory()=>OpenRgbConfigDirectory(Environment.IsPrivilegedProcess,Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    // Roots and elevation are parameters so tests never touch the real %ProgramData%.
    internal static string OpenRgbConfigDirectory(bool elevated,string commonAppData,string localAppData)
    {
        if(!elevated)
        {
            var local=Path.Combine(localAppData,"DeadWare","openrgb");
            Directory.CreateDirectory(local);
            return local;
        }
        return Protected(commonAppData,"openrgb",Refusal);
    }

    // %ProgramData%\DeadWare\<name>, created the way ProtectedRoot.Prepare creates the updates folder: a missing DeadWare folder is created
    // with the protected DACL, an existing one is verified before anything is created beneath it, and the folder itself must then pass.
    // DeadWare's own entry is judged before the folder, so a refusal of DeadWare names DeadWare, not the folder beneath it.
    internal static string Protected(string commonAppData,string name,Func<string,string?,string> refusal)
    {
        var deadWare=Path.Combine(commonAppData,"DeadWare");
        var dir=Path.Combine(deadWare,name);
        if(!Directory.Exists(dir))
        {
            if(!Directory.Exists(deadWare)) new DirectoryInfo(deadWare).Create(ProtectedRoot.Security());
            Require(deadWare,InstallLocation.Check(deadWare),refusal);
            new DirectoryInfo(dir).Create(ProtectedRoot.Security());
        }
        Require(deadWare,InstallLocation.Evaluate([InstallLocation.ReadEntry(deadWare,PathKind.Directory)]),refusal);
        Require(dir,InstallLocation.Check(dir,StrictParents),refusal);
        return dir;
    }
    static void Require(string folder,LocationProtection verdict,Func<string,string?,string> refusal)
    {
        if(!verdict.Protected) throw new UnprotectedFolderException(folder,verdict.Reason,refusal(folder,verdict.Reason));
    }

    public static string LogDirectory(string sid)=>LogDirectory(Environment.IsPrivilegedProcess,Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),sid);

    // Ruling AT: an elevated host appends to and rotates its log by fixed names; in user-writable %LOCALAPPDATA% a program without
    // administrator rights could turn the folder into a junction and have those moves land anywhere, as administrator. So an elevated host
    // logs under %ProgramData%\DeadWare\logs, created and checked like the openrgb folder. B1/B20: in a folder of its own user's,
    // logs\<user SID>, which only that user, SYSTEM and Administrators can read, since the log names the user's paths and devices, and two
    // users' elevated hosts never share or rotate one file. Without administrator rights it logs where it did.
    internal static string LogDirectory(bool elevated,string commonAppData,string localAppData,string sid)
    {
        if(!elevated) return Path.Combine(localAppData,"DeadWare","logs");
        var user=new SecurityIdentifier(sid); // a SID or nothing: it names a folder
        var dir=Path.Combine(Protected(commonAppData,"logs",LogRefusal),user.Value);
        if(!Directory.Exists(dir)) new DirectoryInfo(dir).Create(UserLogSecurity(user));
        Require(dir,InstallLocation.Check(dir,StrictParents+1),LogRefusal);
        return dir;
    }

    // SYSTEM and Administrators full control and the user read, inherited by the log files; owned by Administrators, and blocking the logs
    // folder's own rules, which let every user read. Other users may list the folder and read its files' attributes and permissions, never
    // their contents: the updater's audit of %ProgramData%\DeadWare, which can run in any user's DeadWare without administrator rights,
    // reads the permissions of everything beneath it and refuses what it can't read.
    internal static DirectorySecurity UserLogSecurity(SecurityIdentifier user)
    {
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        const InheritanceFlags inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(administrators,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(user,FileSystemRights.ReadAndExecute,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(users,FileSystemRights.ListDirectory|FileSystemRights.ReadAttributes|FileSystemRights.ReadPermissions,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(users,FileSystemRights.ReadAttributes|FileSystemRights.ReadPermissions,InheritanceFlags.ObjectInherit,PropagationFlags.InheritOnly,AccessControlType.Allow));
        security.SetOwner(administrators);return security;
    }

    // The folder holding OpenRGB.exe must not be writable either, or a planted DLL there runs elevated.
    public static LocationProtection CheckOpenRgbFolder(string exePath)=>InstallLocation.Check(Path.GetDirectoryName(Path.GetFullPath(exePath))!);

    // Ruling AM: an elevated host checks OpenRGB's folder before every launch; a host without administrator rights gains nothing from the
    // check, since anything OpenRGB loads runs with the user's own rights.
    public static Func<string,LocationProtection>? OpenRgbFolderCheck(bool elevated)=>elevated?CheckOpenRgbFolder:null;
}
