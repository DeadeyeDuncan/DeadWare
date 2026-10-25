using System.Diagnostics;
using System.Globalization;
using DeadWare.Core;
namespace DeadWare.App;

internal sealed class GuiInstance : IDisposable
{
    private readonly Mutex? marker;
    public bool Acquired {get;}
    public GuiInstance()
    {
        string name=$"Local\\DeadWare.Gui.{StartupRegistration.CurrentSid}.{Process.GetCurrentProcess().SessionId}";
        // Handle lifetime is the guard; no thread-affine mutex ownership or cross-process IPC is needed.
        try
        {
            var candidate=new Mutex(false,name,out bool created);Acquired=created;
            if(created)marker=candidate;else candidate.Dispose();
        }
        catch(UnauthorizedAccessException) {Acquired=false;} // Same-user elevated instance may deny lower-integrity opens.
    }
    public void Dispose()=>marker?.Dispose();
    private static string ReadyName(string sid,string nonce)=>$"Local\\DeadWare.Restart.{sid}.{Guid.ParseExact(nonce,"N"):N}";
    public static async Task<bool> LaunchElevatedReplacementAsync()
    {
        using var current=Process.GetCurrentProcess();string sid=StartupRegistration.CurrentSid,nonce=Guid.NewGuid().ToString("N");
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,ReadyName(sid,nonce));
        var start=new ProcessStartInfo(StartupRegistration.Executable) {UseShellExecute=true,Verb="runas",WorkingDirectory=AppContext.BaseDirectory};
        foreach(string argument in new[]{"--restart-from",current.Id.ToString(CultureInfo.InvariantCulture),"--restart-start",current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),"--expected-user",sid,"--restart-ready",nonce})start.ArgumentList.Add(argument);
        using var child=Process.Start(start)??throw new InvalidOperationException("Windows did not start DeadWare.");
        // The replacement checks identity and opens the parent's ready event before asking it to exit.
        var deadline=Stopwatch.StartNew();
        while(deadline.Elapsed<TimeSpan.FromSeconds(15))
        {
            if(ready.WaitOne(0))return true;
            if(child.HasExited)return false;
            await Task.Delay(100);
        }
        return false;
    }
    public static async Task WaitForPreviousAsync(string[] args)
    {
        int index=Array.IndexOf(args,"--restart-from");if(index<0)return;
        string Required(string option)
        {
            int i=Array.IndexOf(args,option);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("Missing restart argument.");
        }
        StartupTaskPolicy.RequireSameUser(Required("--expected-user"),StartupRegistration.CurrentSid);
        using var parent=Process.GetProcessById(int.Parse(Required("--restart-from"),CultureInfo.InvariantCulture));
        using var current=Process.GetCurrentProcess();
        if(parent.StartTime.ToUniversalTime().Ticks!=long.Parse(Required("--restart-start"),CultureInfo.InvariantCulture)||parent.SessionId!=current.SessionId||!string.Equals(parent.MainModule?.FileName,StartupRegistration.Executable,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The original DeadWare process no longer matches this restart request.");
        using var ready=EventWaitHandle.OpenExisting(ReadyName(StartupRegistration.CurrentSid,Required("--restart-ready")));
        ready.Set();
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        // Only now may startup acquire the GUI guard or read settings saved by the old instance.
    }
}
