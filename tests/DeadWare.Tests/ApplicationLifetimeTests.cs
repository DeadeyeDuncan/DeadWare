using DeadWare.Core;
using Xunit;

namespace DeadWare.Tests;

public class ApplicationLifetimeTests
{
    [Fact] public void DashboardCloseHidesOnlyWhenTheTrayCanRestoreIt()
    {
        var lifetime=new ApplicationLifetime();
        Assert.Equal(DashboardCloseBehavior.Hide,lifetime.GetDashboardCloseBehavior(true));
        Assert.Equal(DashboardCloseBehavior.KeepVisible,lifetime.GetDashboardCloseBehavior(false));
        Assert.False(lifetime.IsEnding);
    }

    [Fact] public void WindowsSessionEndingBypassesHideEvenWithoutATray()
    {
        var lifetime=new ApplicationLifetime();lifetime.BeginSessionEnding();
        Assert.True(lifetime.IsEnding);
        Assert.Equal(DashboardCloseBehavior.AllowClose,lifetime.GetDashboardCloseBehavior(true));
        Assert.Equal(DashboardCloseBehavior.AllowClose,lifetime.GetDashboardCloseBehavior(false));
    }

    [Fact] public async Task QuitChangesClosePolicyBeforeTeardownAndRunsOnlyOnce()
    {
        var lifetime=new ApplicationLifetime();var stopped=new TaskCompletionSource();int shutdowns=0,exits=0;
        Task? reentrant=null;
        var first=lifetime.QuitAsync(()=>
        {
            shutdowns++;
            Assert.True(lifetime.IsEnding);
            Assert.Equal(DashboardCloseBehavior.AllowClose,lifetime.GetDashboardCloseBehavior(true));
            reentrant=lifetime.QuitAsync(()=>throw new Exception("Duplicate teardown"),()=>throw new Exception("Duplicate exit"));
            return stopped.Task;
        },()=>exits++);
        var second=lifetime.QuitAsync(()=>throw new Exception("Duplicate teardown"),()=>throw new Exception("Duplicate exit"));
        Assert.Same(first,reentrant);Assert.Same(first,second);Assert.Equal(1,shutdowns);Assert.Equal(0,exits);
        stopped.SetResult();await first;
        Assert.Equal(1,exits);
        Assert.Same(first,lifetime.QuitAsync(()=>Task.CompletedTask,()=>exits++));
        Assert.Equal(1,exits);
    }

    [Fact] public async Task TeardownFailureStillRunsExitAndKeepsCloseBypassed()
    {
        var lifetime=new ApplicationLifetime();int exits=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>lifetime.QuitAsync(()=>throw new InvalidOperationException("Disk failed"),()=>exits++));
        Assert.Equal(1,exits);Assert.True(lifetime.IsEnding);
        Assert.Equal(DashboardCloseBehavior.AllowClose,lifetime.GetDashboardCloseBehavior(true));
    }
}
