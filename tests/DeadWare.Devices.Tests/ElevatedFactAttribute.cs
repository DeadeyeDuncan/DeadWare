using System.Runtime.Versioning;
using System.Security.Principal;
using Xunit;

namespace DeadWare.Devices.Tests;

/// <summary>Runs only in an elevated test process (CI runners are elevated) and is reported as skipped otherwise.</summary>
public sealed class ElevatedFactAttribute:FactAttribute
{
    public ElevatedFactAttribute(){if(!OperatingSystem.IsWindows()||!Elevated.Now())Skip="Needs an elevated test run; CI runs it elevated.";}
}

/// <summary>Runs only in a test process without administrator rights, and is reported as skipped in an elevated one (CI's).</summary>
public sealed class NotElevatedFactAttribute:FactAttribute
{
    public NotElevatedFactAttribute(){if(!OperatingSystem.IsWindows()||Elevated.Now())Skip="Needs a test run without administrator rights.";}
}

static class Elevated
{
    [SupportedOSPlatform("windows")]
    public static bool Now(){using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
}
