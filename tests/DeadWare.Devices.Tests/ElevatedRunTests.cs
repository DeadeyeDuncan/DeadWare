using Xunit;

namespace DeadWare.Devices.Tests;

// F6 item 7 (whole-branch review D13): every [ElevatedFact] is skipped in a run without administrator rights, and dotnet test still
// passes. CI's runner is elevated; if that ever changes, this test fails, so the CI result shows red instead of quiet skips.
public sealed class ElevatedRunTests
{
    [Fact]
    public void On_CI_the_tests_run_with_administrator_rights()
    {
        if(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true") return;
        Assert.True(OperatingSystem.IsWindows()&&Elevated.Now(),"CI ran DeadWare.Devices.Tests without administrator rights, so every [ElevatedFact] was skipped.");
    }
}
