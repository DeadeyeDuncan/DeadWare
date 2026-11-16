using System.Runtime.Versioning;
using DeadWare.UpdateCore;

namespace DeadWare.Devices;

// OpenRGB loads plugin DLLs from its config folder and the host runs elevated, so an elevated host only uses a folder ordinary programs cannot write.
[SupportedOSPlatform("windows")]
internal static class ProtectedFolders
{
    public const string Refusal="The OpenRGB settings folder isn't protected from other programs.";

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
        var deadWare=Path.Combine(commonAppData,"DeadWare");
        var dir=Path.Combine(deadWare,"openrgb");
        if(!Directory.Exists(deadWare)) new DirectoryInfo(deadWare).Create(ProtectedRoot.Security());
        if(!Directory.Exists(dir)) new DirectoryInfo(dir).Create(ProtectedRoot.Security());
        if(!InstallLocation.Check(dir,2).Protected) throw new InvalidDataException(Refusal);
        return dir;
    }

    // The folder holding OpenRGB.exe must not be writable either, or a planted DLL there runs elevated.
    public static LocationProtection CheckOpenRgbFolder(string exePath)=>InstallLocation.Check(Path.GetDirectoryName(Path.GetFullPath(exePath))!);
}
