using System.Runtime.Versioning;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

// A folder the policy refused: Message is plain words for the window, Reason is InstallLocation's account of why, for the log.
internal sealed class UnprotectedFolderException(string message,string? reason):Exception(message)
{
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
    public const string Refusal=@"The OpenRGB settings folder %ProgramData%\DeadWare\openrgb isn't restricted to administrators, so DeadWare won't start OpenRGB. Delete that folder as administrator, then turn lighting off and on.";

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
    internal static string Protected(string commonAppData,string name,string refusal)
    {
        var deadWare=Path.Combine(commonAppData,"DeadWare");
        var dir=Path.Combine(deadWare,name);
        if(!Directory.Exists(dir))
        {
            if(!Directory.Exists(deadWare)) new DirectoryInfo(deadWare).Create(ProtectedRoot.Security());
            Require(InstallLocation.Check(deadWare),refusal);
            new DirectoryInfo(dir).Create(ProtectedRoot.Security());
        }
        Require(InstallLocation.Check(dir,StrictParents),refusal);
        return dir;
    }
    static void Require(LocationProtection verdict,string refusal){if(!verdict.Protected) throw new UnprotectedFolderException(refusal,verdict.Reason);}

    // The folder holding OpenRGB.exe must not be writable either, or a planted DLL there runs elevated.
    public static LocationProtection CheckOpenRgbFolder(string exePath)=>InstallLocation.Check(Path.GetDirectoryName(Path.GetFullPath(exePath))!);

    // Ruling AM: an elevated host checks OpenRGB's folder before every launch; a host without administrator rights gains nothing from the
    // check, since anything OpenRGB loads runs with the user's own rights.
    public static Func<string,LocationProtection>? OpenRgbFolderCheck(bool elevated)=>elevated?CheckOpenRgbFolder:null;
}
