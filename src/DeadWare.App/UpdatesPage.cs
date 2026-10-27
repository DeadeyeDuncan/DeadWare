using System.Windows;
using System.Windows.Controls;

namespace DeadWare.App;

internal sealed class UpdatesPage : ScrollViewer
{
    private readonly UpdateService service;
    private readonly TextBlock installed=Ui.Text("",24,"Accent"),latest=Ui.Text("",16),status=Ui.Text("",13),local=Ui.Text("",12,"Muted"),notes=Ui.Text("",12),last=Ui.Text("",12,"Muted");
    private readonly Button check,update,rollback,cancel;
    private readonly ProgressBar progress=new(){Minimum=0,Maximum=100,Height=6,Margin=new Thickness(0,8,0,12)};
    private CancellationTokenSource? operation;
    public UpdatesPage(UiSession session)
    {
        service=session.Updates;VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty,"Background");
        var body=new StackPanel{Margin=new Thickness(6,10,6,10)};Content=body;
        var heading=new StackPanel();heading.Children.Add(installed);latest.Margin=new(0,8,0,8);heading.Children.Add(latest);heading.Children.Add(last);body.Children.Add(Card(heading));
        check=Ui.Button("Check for updates",()=>_ = RunAsync(token=>service.CheckAsync(token)));
        update=Ui.Button("Update & restart",()=>_ = InstallAsync(false));rollback=Ui.Button("Roll back & restart",()=>_ = InstallAsync(true));
        cancel=Ui.Button("Cancel",()=>operation?.Cancel());
        var buttons=new WrapPanel{Margin=new(0,0,0,10)};foreach(var button in new[]{check,update,rollback,cancel})buttons.Children.Add(button);body.Children.Add(buttons);
        body.Children.Add(progress);status.Margin=new(3,6,3,12);body.Children.Add(status);local.Margin=new(3,0,3,16);body.Children.Add(local);
        var information=new StackPanel();information.Children.Add(Ui.Text("Your history stays with you",16));
        var explanation=Ui.Text("Updates install normal releases published on DeadWare's GitHub. Checking and installation happen when you choose. Update & restart saves your workspace and history, installs the verified package, then reopens DeadWare. Rollback restores the previous app files while keeping current settings and recordings.",13,"Muted");explanation.Margin=new(0,8,0,8);information.Children.Add(explanation);
        information.Children.Add(Ui.Text("The first updater-enabled installation may have no earlier version to restore. A previous version becomes available after an in-app update.",12,"Muted"));body.Children.Add(Card(information));
        var releaseNotes=new StackPanel();releaseNotes.Children.Add(Ui.Text("Release notes",16));notes.Margin=new(0,10,0,0);releaseNotes.Children.Add(notes);body.Children.Add(Card(releaseNotes));
        Loaded+=async(_,_)=>{service.Changed+=Changed;ApplyState(service.State);await service.ReadLocalAsync();};
        Unloaded+=(_,_)=>service.Changed-=Changed;
        IsVisibleChanged+=async(_,_)=>{if(IsLoaded&&IsVisible)await service.ReadLocalAsync();};ApplyState(service.State);
    }
    private static Border Card(UIElement child){var panel=Ui.Panel(child,new Thickness(16));panel.Margin=new(0,0,0,12);return panel;}
    private void Changed(){if(Dispatcher.CheckAccess())ApplyState(service.State);else Dispatcher.InvokeAsync(()=>ApplyState(service.State));}
    internal void ApplyState(UpdateState state)
    {
        installed.Text="Installed version "+state.InstalledVersion;
        latest.Text=state.Release is null?"Normal GitHub releases":"Published version "+state.Release.Version;
        last.Text=state.LastChecked is { } checkedAt?$"Last checked {checkedAt:g}":"Updates are checked only when you click.";
        status.Text=state.Status;local.Text=state.LocalStatus+(state.LastOperation.Length>0?"\nLast operation: "+state.LastOperation:"");
        notes.Text=state.Release?.Notes??"Release notes appear after a published release is found.";
        check.IsEnabled=!state.Busy;update.IsEnabled=state.CanUpdate;rollback.IsEnabled=state.CanRollback;
        rollback.Content=state.Previous is { } prior?$"Roll back to {prior.Version} & restart":"Roll back & restart";
        cancel.IsEnabled=state.CanCancel;cancel.Visibility=state.Busy?Visibility.Visible:Visibility.Collapsed;
        progress.Visibility=state.Busy?Visibility.Visible:Visibility.Collapsed;progress.Value=state.Progress;progress.IsIndeterminate=state.Busy&&state.Progress==0;
    }
    private async Task RunAsync(Func<CancellationToken,Task> action)
    {
        if(operation is not null)return;
        using var cancellation=new CancellationTokenSource();operation=cancellation;
        try{await action(cancellation.Token);}catch(Exception error){service.ReportFailure(error);}finally{operation=null;}
    }
    private Task InstallAsync(bool restore)=>RunAsync(async token=>
    {
        var job=await service.PrepareAsync(restore,token);
        if(job is not null&&await service.StartHelperAsync(job,token))await ((App)Application.Current).QuitAsync();
    });
}
