using Xunit;

namespace DeadWare.App.Tests;

/// <summary>
/// A real-Windows measurement: runs only on GitHub's Windows runner (<c>GITHUB_ACTIONS</c> is "true"), so a local test run never
/// writes 256 MB or opens a kernel session. It never skips there; the test checks elevation in its own body.
/// </summary>
public sealed class GitHubWindowsFactAttribute:FactAttribute
{
    internal const string GitHubOnly="Runs only on GitHub's Windows runner.";
    internal static bool OnGitHubWindows=>OperatingSystem.IsWindows()&&Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true";
    public GitHubWindowsFactAttribute() {if(!OnGitHubWindows)Skip=GitHubOnly;}
}

/// <summary>A real-Windows check: runs only in an elevated test run on GitHub's Windows runner, which runs elevated.</summary>
public sealed class ElevatedGitHubFactAttribute:FactAttribute
{
    public ElevatedGitHubFactAttribute()
    {
        if(!GitHubWindowsFactAttribute.OnGitHubWindows)Skip=GitHubWindowsFactAttribute.GitHubOnly;
        else if(!MeasurementRecorder.Elevated)Skip="Needs an elevated test run; GitHub's Windows runner is elevated.";
    }
}

/// <summary>Real-Windows tests never run beside another test class: the 240-scan timing, the 256 MB write and the kernel sessions would disturb it.</summary>
[CollectionDefinition("RealWindows",DisableParallelization=true)] public sealed class RealWindowsCollection;
