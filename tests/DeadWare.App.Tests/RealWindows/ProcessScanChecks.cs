using System.Diagnostics;
using System.IO;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace DeadWare.App.Tests;

/// <summary>Checks of the real process scan and of disk-session cleanup on GitHub's elevated Windows runner. They fail the build.</summary>
[Collection("RealWindows")]
public sealed class ProcessScanChecks
{
    // Windows gives these processes no image path, so NativeProcessIdentity.Read names them "PID n".
    private static readonly string[] Imageless=["Registry","Memory Compression","Secure System"];

    [ElevatedGitHubFact] public void Scan_contains_this_process_and_system()
    {
        var source=new WindowsProcesses();
        var first=Scan(source);
        var own=Assert.Single(first.Processes,p=>p.Pid==Environment.ProcessId);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath),own.Name,ignoreCase:true);
        Assert.Equal("System",Assert.Single(first.Processes,p=>p.Pid==4).Name);
        // Spend some CPU so the second reading has something to add.
        var spin=Stopwatch.StartNew();
        while(spin.ElapsedMilliseconds<100)Thread.SpinWait(1000);
        var again=Assert.Single(Scan(source).Processes,p=>p.Pid==Environment.ProcessId);
        Assert.Equal(own.Generation,again.Generation);
        Assert.True(again.CpuTime>=own.CpuTime,$"Own CPU time went from {own.CpuTime} to {again.CpuTime}");
    }

    [ElevatedGitHubFact] public void Scan_generations_match_native_identity()
    {
        var source=new WindowsProcesses();
        var read=Scan(source).Processes.Select(p=>(Entry:p,Identity:NativeProcessIdentity.Read(p.Pid))).ToList();
        var after=Scan(source).Processes.ToDictionary(p=>p.Pid,p=>p.Generation);
        // A PID that exited or was reused between the scans may have been read from another process.
        var stable=read.Where(r=>r.Identity is not null&&after.TryGetValue(r.Entry.Pid,out long generation)&&generation==r.Entry.Generation).ToList();
        Assert.NotEmpty(stable);
        foreach(var (entry,identity) in stable)
        {
            Assert.True(identity!.Generation==entry.Generation,$"PID {entry.Pid} ({entry.Name}): scan Generation {entry.Generation}, native {identity.Generation}");
            if(identity.Name==$"PID {entry.Pid}")
                Assert.True(entry.Pid==4||Imageless.Contains(entry.Name),$"PID {entry.Pid} ({entry.Name}) has no image path");
            else Assert.Equal(entry.Name,identity.Name,ignoreCase:true);
        }
    }

    [ElevatedGitHubFact] public void Orphan_disk_sessions_are_removed()
    {
        string orphan=$"DeadWare-Disk-999999-{Guid.NewGuid():N}",live=$"DeadWare-Disk-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            StartOwnerlessSession(orphan);StartOwnerlessSession(live);
            var before=TraceEventSession.GetActiveSessionNames().ToList();
            Assert.Contains(orphan,before);Assert.Contains(live,before);
            EtwDisk.RemoveOrphans(before,pid=>pid==Environment.ProcessId,StopSession);
            var after=TraceEventSession.GetActiveSessionNames().ToList();
            Assert.DoesNotContain(orphan,after);
            Assert.Contains(live,after);
        }
        finally
        {
            foreach(string name in new[]{orphan,live})
            {
                try {StopSession(name);}
                catch(Exception) {}
            }
        }
    }

    private static ProcessScan Scan(WindowsProcesses source)
    {
        var scan=source.Scan(Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency,1);
        Assert.True(scan is not null,$"Process scan failed: {source.Failure}");
        return scan!;
    }
    // A real session left running with no owner, as a crashed DeadWare leaves it. Enabling any provider starts it;
    // a provider nobody registers sends it no events.
    private static void StartOwnerlessSession(string name)
    {
        using var session=new TraceEventSession(name) {StopOnDispose=false};
        session.EnableProvider(Guid.NewGuid(),Microsoft.Diagnostics.Tracing.TraceEventLevel.Informational,0UL,null);
    }
    private static void StopSession(string name) {using var session=TraceEventSession.GetActiveSession(name);session?.Stop();}
}
