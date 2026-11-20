using System.Diagnostics;
using DeadWare.Devices;
using Xunit;
using static DeadWare.Devices.Tests.Waits;

namespace DeadWare.Devices.Tests;

// The real runner, with a harmless child (ping) in place of OpenRGB.
public sealed class SystemProcessRunnerTests
{
    static ProcessStartInfo Ping()=>new(Path.Combine(Environment.SystemDirectory,"PING.EXE")){ArgumentList={"-n","120","127.0.0.1"}};

    // F5 item 3a (whole-branch review R4-D15, R4-D13): the environment the engine builds reaches a real child through the real runner.
    // cmd.exe stands in for OpenRGB and writes what it received; /d skips any AutoRun command.
    [Fact]
    public async Task A_real_child_receives_the_engines_environment_and_nothing_planted()
    {
        var dir=Path.Combine(Path.GetTempPath(),"dw-env-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("DW_PLANTED_VARIABLE","planted");
        try
        {
            var output=Path.Combine(dir,"env.txt");
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe")){Arguments=$"/d /c set > \"{output}\""};
            OpenRgbEngine.SetEnvironment(info,dir,@"C:\cfg");
            using(var child=new SystemProcessRunner().Start(info)) await child.WaitForExitAsync(CancellationToken.None).WaitAsync(RealTimeCap);
            var received=File.ReadAllLines(output).Where(l=>l.Contains('=')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0],p=>p[1],StringComparer.OrdinalIgnoreCase);
            Assert.Equal(@"C:\cfg",received["APPDATA"]);
            Assert.Equal(OpenRgbEngine.SystemPath(dir),received["PATH"]);
            Assert.False(received.ContainsKey("DW_PLANTED_VARIABLE"));
        }
        finally{Environment.SetEnvironmentVariable("DW_PLANTED_VARIABLE",null);try{Directory.Delete(dir,true);}catch{}}
    }

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
