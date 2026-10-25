namespace DeadWare.Core;

public enum DashboardCloseBehavior {Hide,KeepVisible,AllowClose}

/// <summary>UI-thread lifetime policy: hiding a dashboard is separate from ending monitoring.</summary>
public sealed class ApplicationLifetime
{
    private Task? quit;
    public bool IsEnding {get;private set;}
    public DashboardCloseBehavior GetDashboardCloseBehavior(bool trayAvailable)=>IsEnding?DashboardCloseBehavior.AllowClose:trayAvailable?DashboardCloseBehavior.Hide:DashboardCloseBehavior.KeepVisible;
    public void BeginSessionEnding()=>IsEnding=true;

    public Task QuitAsync(Func<Task> shutdown,Action exit)
    {
        if(quit is not null)return quit;
        IsEnding=true;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Publish the gate before invoking callbacks, which may synchronously reenter Quit.
        quit=completion.Task;
        _=FinishAsync();
        return quit;

        async Task FinishAsync()
        {
            try
            {
                try {await shutdown();}finally {exit();}
                completion.TrySetResult();
            }
            catch(Exception e) {completion.TrySetException(e);}
        }
    }
}
