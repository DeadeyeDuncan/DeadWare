using System.Runtime.Versioning;
using System.Security.Principal;
using Xunit;

namespace DeadWare.Devices.Tests;

/// <summary>Runs only in an elevated test process (CI runners are elevated) and is reported as skipped otherwise.</summary>
public sealed class ElevatedFactAttribute:FactAttribute
{
    public ElevatedFactAttribute(){if(!OperatingSystem.IsWindows()||!Elevated())Skip="Needs an elevated test run; CI runs it elevated.";}
    [SupportedOSPlatform("windows")]
    private static bool Elevated(){using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
}
