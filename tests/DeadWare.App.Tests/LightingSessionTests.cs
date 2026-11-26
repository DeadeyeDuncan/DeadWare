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

    /// <summary>
    /// Lighting 5, Task 9 (ruling P30): at the Windows session ending the window waits for the lighting stop only for what is left of 2 s;
    /// a stop that has finished, or none, is done at once, and one that hangs is left without a throw.
    /// </summary>
    [Fact]
    public void A_finished_stop_is_waited_for_and_a_hanging_one_is_left()
    {
        Assert.True(MonitorSession.WaitBriefly(Task.CompletedTask,TimeSpan.FromSeconds(2)));
        Assert.True(MonitorSession.WaitBriefly(null,TimeSpan.FromSeconds(2)));
        Assert.True(MonitorSession.WaitBriefly(Task.FromException(new InvalidOperationException("the stop failed")),TimeSpan.Zero));
        var hanging=new TaskCompletionSource().Task;
        Assert.False(MonitorSession.WaitBriefly(hanging,TimeSpan.Zero));
        Assert.False(MonitorSession.WaitBriefly(hanging,TimeSpan.FromMilliseconds(-300)));
        Assert.False(MonitorSession.WaitBriefly(hanging,TimeSpan.FromMilliseconds(50)));
    }

    /// <summary>Without lighting the session ending's stop is a finished task.</summary>
    [Fact]
    public void Without_lighting_the_session_ending_stop_is_already_done()
    {
        var session=(MonitorSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MonitorSession));
        Assert.True(session.StopLightingForSessionEnding().IsCompletedSuccessfully);
    }

    [Fact]
    public void A_new_path_or_a_client_problem_restarts()
    {
        Assert.False(MonitorSession.KeepsLightingClient(true,null,HostStatus.Running));
        Assert.False(MonitorSession.KeepsLightingClient(false,"The lighting helper stopped 5 times in 10 minutes.",null));
    }
}
