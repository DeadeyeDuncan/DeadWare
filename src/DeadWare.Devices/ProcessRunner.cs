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

internal sealed class SystemProcessRunner : IProcessRunner
{
    public IRunningProcess Start(ProcessStartInfo info)
    {
        info.UseShellExecute=false;info.CreateNoWindow=true;
        if(string.IsNullOrEmpty(info.WorkingDirectory)) info.WorkingDirectory=Path.GetDirectoryName(info.FileName)??"";
        return new Running(Process.Start(info)??throw new InvalidOperationException("The process did not start."));
    }
    sealed class Running(Process process):IRunningProcess
    {
        public int Id=>process.Id;
        public bool HasExited{get{try{return process.HasExited;}catch(InvalidOperationException){return true;}}}
        public Task WaitForExitAsync(CancellationToken token)=>process.WaitForExitAsync(token);
        public void KillTree(){try{process.Kill(entireProcessTree:true);}catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}}
        public void Dispose()=>process.Dispose();
    }
}
