using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeadWare.UpdateCore;

/// <summary>The administrator-only update folder (%ProgramData%\DeadWare\updates) used by installations in protected folders. Existing folders are verified, never repaired, taken over or deleted.</summary>
public static class ProtectedRoot
{
    public const string Refusal=@"The update folder %ProgramData%\DeadWare\updates is not restricted to administrators. Delete it, then try again.";
    public const string Elevation="Updating DeadWare in a protected folder needs administrator rights. Use Restart as administrator, then try again.";

    /// <summary>Refuses unless every entry passes the install-location policy.</summary>
    public static void Verify(IEnumerable<PathSecurity> entries){if(!InstallLocation.Evaluate(entries).Protected)throw new InvalidDataException(Refusal);}

    /// <summary>Audits the key root and everything beneath it, updates and DeadWare as protected folders, and %ProgramData% and above as parents.</summary>
    [SupportedOSPlatform("windows")]
    public static void Verify(string root){if(!InstallLocation.Check(root,2).Protected)throw new InvalidDataException(Refusal);}

    /// <summary>Verifies the deepest part of the protected chain that exists (key root, else updates, else DeadWare, links included); before %ProgramData%\DeadWare exists there is nothing to trust or refuse.</summary>
    [SupportedOSPlatform("windows")]
    public static void VerifyExisting(UpdatePaths paths)
    {
        if(!paths.Protected)return;
        string updates=Path.GetDirectoryName(paths.Root)!,deadWare=Path.GetDirectoryName(updates)!;
        if(Path.Exists(paths.Root))Verify(paths.Root);
        else if(Path.Exists(updates)){if(!InstallLocation.Check(updates,1).Protected)throw new InvalidDataException(Refusal);}
        else if(Path.Exists(deadWare)&&!InstallLocation.Check(deadWare).Protected)throw new InvalidDataException(Refusal);
    }

    [SupportedOSPlatform("windows")]
    public static void Verify(UpdatePaths paths){if(paths.Protected)Verify(paths.Root);}

    /// <summary>Creates missing DeadWare and updates folders with <see cref="Security"/>, verifies existing parents before creating beneath them, then verifies the whole key root.</summary>
    [SupportedOSPlatform("windows")]
    public static void Prepare(UpdatePaths paths)
    {
        if(!paths.Protected)return;
        if(!Elevated())throw new UnauthorizedAccessException(Elevation);
        string updates=Path.GetDirectoryName(paths.Root)!,deadWare=Path.GetDirectoryName(updates)!;
        if(!Directory.Exists(paths.Root))
        {
            // Create(DirectorySecurity) applies the DACL atomically and leaves a folder created in the meantime unchanged; the check below then judges it.
            if(!Directory.Exists(deadWare))new DirectoryInfo(deadWare).Create(Security());
            if(!InstallLocation.Check(deadWare).Protected)throw new InvalidDataException(Refusal);
            if(!Directory.Exists(updates))new DirectoryInfo(updates).Create(Security());
            Directory.CreateDirectory(paths.Root);
        }
        Verify(paths.Root);
    }

    /// <summary>SYSTEM and Administrators full control, Users read and execute, inherited by everything created beneath; owned by Administrators and blocking inheritance from %ProgramData%.</summary>
    [SupportedOSPlatform("windows")]
    public static DirectorySecurity Security()
    {
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        const InheritanceFlags inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(administrators,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,inherit,PropagationFlags.None,AccessControlType.Allow));
        security.SetOwner(administrators);return security;
    }

    [SupportedOSPlatform("windows")]
    private static bool Elevated(){using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
}
