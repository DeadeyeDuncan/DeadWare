using System.Diagnostics;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// The real runner, with a harmless child (ping) in place of OpenRGB.
public sealed class SystemProcessRunnerTests
{
    static ProcessStartInfo Ping()=>new(Path.Combine(Environment.SystemDirectory,"PING.EXE")){ArgumentList={"-n","120","127.0.0.1"}};

    // A wrongly sized limit structure makes SetInformationJobObject fail with ERROR_BAD_LENGTH, leaving no kill-on-close.
    [Fact]
    public void Job_limit_structure_has_the_native_size()=>Assert.Equal(IntPtr.Size==8?144:112,ProcessJob.ExtendedLimitSize);

    // Ruling Z: every process the runner starts joins the host's kill-on-close job at once, so when the host ends, however it ends,
    // Windows ends that process too. Closing the job's only handle is what a host's death does.
    [Fact]
    public async Task Started_process_joins_the_kill_on_close_job_and_ends_with_it()
    {
        var job=ProcessJob.Create();
        try
        {
            Assert.True(job.KillsOnClose);
            using var child=new SystemProcessRunner(job).Start(Ping());
            try
            {
                using(var process=Process.GetProcessById(child.Id)) Assert.True(job.Contains(process));
                job.Dispose();
                await child.WaitForExitAsync(CancellationToken.None).WaitAsync(RealTimeCap);
                Assert.True(child.HasExited);
            }
            finally{child.KillTree();}
        }
        finally{job.Dispose();}
    }
}
