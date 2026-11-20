using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace DeadWare.Devices.Tests;

// Real-time waits shared by the host and engine tests.
static class Waits
{
    // Real time any wait may take before it fails the test. Every product deadline under test runs on fake time, so this only has to
    // cover thread-pool hops; it is generous because a loaded machine stretches those to seconds.
    public static readonly TimeSpan RealTimeCap=TimeSpan.FromSeconds(15);

    // Polls in real time, never moving a fake clock, until the condition holds; a failure names the condition.
    public static async Task Until(Func<bool> cond,[CallerArgumentExpression(nameof(cond))] string what="")
    {
        var clock=Stopwatch.StartNew();
        while(!cond())
        {
            if(clock.Elapsed>RealTimeCap) Assert.Fail($"condition not reached within {RealTimeCap.TotalSeconds:0} s: {what}");
            await Task.Delay(10);
        }
    }
}
