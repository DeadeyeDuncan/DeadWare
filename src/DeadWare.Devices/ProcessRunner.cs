using System.Diagnostics;

namespace DeadWare.Devices;

// The process seam: the engine never touches System.Diagnostics directly, so tests never launch OpenRGB.
internal interface IProcessRunner { IRunningProcess Start(ProcessStartInfo info); }

internal interface IRunningProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Task WaitForExitAsync(CancellationToken token);
    void KillTree();
}

// job: the host's kill-on-close job (Ruling Z), which every started process joins; null when Windows refused to create one.
internal sealed class SystemProcessRunner(ProcessJob? job=null,HostLog? log=null) : IProcessRunner
{
    int joinFailed;
    public IRunningProcess Start(ProcessStartInfo info)
    {
        info.UseShellExecute=false;info.CreateNoWindow=true;
        if(string.IsNullOrEmpty(info.WorkingDirectory)) info.WorkingDirectory=Path.GetDirectoryName(info.FileName)??"";
        var process=Process.Start(info)??throw new InvalidOperationException("The process did not start.");
        // Into the job at once. Process.Start can't start it suspended, so for a few instructions it runs outside the job: a host that
        // died in exactly that moment would leave it behind. A refusal is logged once and the process runs on, as before the job existed.
        if(job is not null)
            try{job.Assign(process);}
            catch(Exception e){if(Interlocked.Exchange(ref joinFailed,1)==0) log?.Write("openrgb could not join the host's job, so it may outlive a host that crashes: "+e.Message);}
        return new Running(process);
    }
    sealed class Running(Process process):IRunningProcess
    {
        public int Id=>process.Id;
        public bool HasExited{get{try{return process.HasExited;}catch(InvalidOperationException){return true;}}}
        public Task WaitForExitAsync(CancellationToken token)=>process.WaitForExitAsync(token);
        // A tree kill that fails part-way throws AggregateException (for example a child the job is already ending); it is best effort.
        public void KillTree(){try{process.Kill(entireProcessTree:true);}catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException){}}
        public void Dispose()=>process.Dispose();
    }
}
