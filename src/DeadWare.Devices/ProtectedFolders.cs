using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DeadWare.Core;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

// A folder the policy refused. Message is plain words for the window: the path at fault, which InstallLocation's reason names, what is
// wrong with it and the remedy (polish item 7, round 2 item 2). Folder is the folder being prepared, and Reason is InstallLocation's
// account of why.
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

    // Polish item 7 and round 2 item 2 (B4/B13/B18): a refusal names the path InstallLocation's reason names, which can be the folder being
    // prepared, something inside it or a folder above it, says what is wrong with it in words that fit the reason, and gives a remedy that
    // works there. Inside %ProgramData%\DeadWare, DeadWare itself included, the one remedy is removal, which needs administrator rights
    // (whole-branch review D1): what programs without administrator rights could change, or another account owns, may already have been
    // changed, and OpenRGB runs as administrator and loads plugin DLLs from its config folder, so a new owner or new permissions would keep
    // those bytes and the check would then pass. What is to be removed is the path at fault, except for UpdateCore's updates folder and
    // anything in it, where it is the whole updates folder (F2 item 3): a snapshot of the earlier version there that lost one file fails
    // validation, which stops both Update & restart and Roll back & restart until the updates folder goes, and reinstalling DeadWare doesn't
    // clear it. The updater's own refusal, ProtectedRoot.Refusal, names the whole folder too. DeadWare makes its own folders again,
    // protected, but not what was in them, so removing updates takes the earlier version with it. %ProgramData% and the folders above it
    // are never to be removed: they get their standard permissions back. A path the check couldn't judge gets the can't-check words with the
    // reason, never a claim that it isn't restricted.
    internal static string Refusal(string deadWare,string folder,string? reason)=>Explain(deadWare,folder,reason,"DeadWare won't start OpenRGB","it won't start OpenRGB");
    internal static string LogRefusal(string deadWare,string folder,string? reason)
        =>Explain(deadWare,folder,reason,"the lighting helper running as administrator keeps no log","the lighting helper running as administrator keeps no log");

    static string Explain(string deadWare,string folder,string? reason,string outcome,string cantCheckOutcome)
    {
        var (fault,named)=LocationReason.Read(reason);
        var entry=named??folder;
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(deadWare));
        var inside=Within(entry,root);
        // F2 item 3: for the updates folder or anything in it, the whole updates folder is to be removed.
        var updates=Path.Combine(root,UpdatesFolder);
        var remove=Within(entry,updates)?$"Remove {updates}, which needs administrator rights, then turn lighting off and on.":"Remove it, which needs administrator rights, then turn lighting off and on.";
        const string Restore="Restore its standard permissions, then turn lighting off and on.";
        // InstallLocation's sentence opens the message: it names the path first. One in no form it uses follows the folder being prepared.
        var opening=named is null?$"{folder} ({reason?.TrimEnd('.')})":reason!.TrimEnd('.');
        var said=reason is not {Length:>0}?"its permissions couldn't be read.":reason.EndsWith('.')?reason:reason+".";
        return fault switch
        {
            LocationFault.Writable=>$"{opening}, so programs without administrator rights can change it and {outcome}. "+(inside?remove:Restore),
            LocationFault.NotOwned=>$"{opening}, so {outcome}. "+(inside?remove:Restore),
            // Inside DeadWare's folder, removing the path, or the updates folder it is in, clears any of these: what DeadWare needs there it
            // makes again, protected. Above it, only an unreadable folder has a fix of its own: a link or a network drive there is how this PC
            // is set up.
            _=>$"DeadWare can't check {entry}, so {cantCheckOutcome}: {said}"+(named is null?"":inside?" "+remove:fault==LocationFault.Unreadable?" "+Restore:""),
        };
    }
    // UpdateCore's updates folder, %ProgramData%\DeadWare\updates (UpdatePaths.ProtectedCacheBase).
    const string UpdatesFolder="updates";
    // path is folder or lies beneath it, compared by whole path segments.
    static bool Within(string path,string folder)
        =>path.Equals(folder,StringComparison.OrdinalIgnoreCase)||path.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

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
    // refusal: the message for (%ProgramData%\DeadWare, the folder being prepared, InstallLocation's reason).
    internal static string Protected(string commonAppData,string name,Func<string,string,string?,string> refusal)
    {
        var deadWare=Path.Combine(commonAppData,"DeadWare");
        var dir=Path.Combine(deadWare,name);
        if(!Directory.Exists(dir))
        {
            if(!Directory.Exists(deadWare)) new DirectoryInfo(deadWare).Create(ProtectedRoot.Security());
            Require(deadWare,deadWare,InstallLocation.Check(deadWare),refusal);
            new DirectoryInfo(dir).Create(ProtectedRoot.Security());
        }
        Require(deadWare,deadWare,InstallLocation.Evaluate([InstallLocation.ReadEntry(deadWare,PathKind.Directory)]),refusal);
        Require(deadWare,dir,InstallLocation.Check(dir,StrictParents),refusal);
        return dir;
    }
    static void Require(string deadWare,string folder,LocationProtection verdict,Func<string,string,string?,string> refusal)
    {
        if(!verdict.Protected) throw new UnprotectedFolderException(folder,verdict.Reason,refusal(deadWare,folder,verdict.Reason));
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
        Require(Path.Combine(commonAppData,"DeadWare"),dir,InstallLocation.Check(dir,StrictParents+1),LogRefusal);
        // Polish round 2, item 6: once it passes, it gets its private rights again however it was made, so one made again with the logs
        // folder's inherited rights, which let every user read, is private from this start on; Windows passes the rights on to the log
        // files in it. Only after the check: only administrators can change it then, and it is no link whose target would take the rights.
        new DirectoryInfo(dir).SetAccessControl(UserLogSecurity(user));
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
