using DeadWare.Core;
using Xunit;
namespace DeadWare.App.Tests;
/// <summary>When a lighting change keeps the running client and when it starts a fresh one.</summary>
public class LightingSessionTests
{
    /// <summary>
    /// Task 13 minor: choosing the same OpenRGB.exe again is the obvious retry after OpenRGB was missing or failed (say, copied into place
    /// since), so it starts a fresh client and host instead of doing nothing. A working client on the same path is kept.
    /// </summary>
    [Theory]
    [InlineData(HostStatus.OpenRgbMissing)]
    [InlineData(HostStatus.OpenRgbFailed)]
    public void Same_path_restarts_after_OpenRGB_was_missing_or_failed(HostStatus status)=>Assert.False(MonitorSession.KeepsLightingClient(false,null,status));

    [Theory]
    [InlineData(null)]
    [InlineData(HostStatus.Running)]
    [InlineData(HostStatus.Blocked)]
    [InlineData(HostStatus.Waiting)]
    public void Same_path_keeps_a_client_that_works(HostStatus? status)=>Assert.True(MonitorSession.KeepsLightingClient(false,null,status));

    [Fact]
    public void A_new_path_or_a_client_problem_restarts()
    {
        Assert.False(MonitorSession.KeepsLightingClient(true,null,HostStatus.Running));
        Assert.False(MonitorSession.KeepsLightingClient(false,"The lighting helper stopped 5 times in 10 minutes.",null));
    }
}
