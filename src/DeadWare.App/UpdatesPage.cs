using System.Windows;
using System.Windows.Controls;
using DeadWare.UpdateCore;

namespace DeadWare.App;

internal sealed class UpdatesPage : ScrollViewer
{
    private readonly UpdateService service;
    private readonly TextBlock installed=Ui.Text("",24,"Accent",font:"MetricFont"),latest=Ui.Text("",16),status=Ui.Text("",13),local=Ui.Text("",12,"Muted"),notesStatus=Ui.Text("",12,"Warning"),last=Ui.Text("",12,"Muted");
    private readonly StackPanel noteSections=new();
    private readonly TextBlock notesHeading=Ui.Heading("Release notes",16);
    private readonly Button history,retryHistory;
    private bool showHistory,displayedHistory,displayedHistoryLoaded;
    private IReadOnlyList<ReleaseNote>? displayedNotes;
    private ReleaseInfo? displayedRelease;
    private readonly Button check,update,rollback,cancel;
    private readonly ProgressBar progress=new(){Minimum=0,Maximum=100,Height=6,Margin=new Thickness(0,8,0,12)};
    private CancellationTokenSource? operation;
    public UpdatesPage(UiSession session)
    {
        service=session.Updates;VerticalScrollBarVisibility=ScrollBarVisibility.Auto;HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled;
        Background=System.Windows.Media.Brushes.Transparent;
        var body=new StackPanel{Margin=new Thickness(6,10,6,10)};Content=body;
        var heading=new StackPanel();heading.Children.Add(installed);latest.Margin=new(0,8,0,8);heading.Children.Add(latest);heading.Children.Add(last);body.Children.Add(Card(heading));
        check=Ui.Button("Check for updates",()=>_ = RunAsync(token=>service.CheckAsync(token)));
        update=Ui.Button("Update & restart",()=>_ = InstallAsync(false));rollback=Ui.Button("Roll back & restart",()=>_ = InstallAsync(true));
        cancel=Ui.Button("Cancel",()=>operation?.Cancel());
        var buttons=new WrapPanel{Margin=new(0,0,0,10)};foreach(var button in new[]{check,update,rollback,cancel})buttons.Children.Add(button);body.Children.Add(buttons);
        body.Children.Add(progress);status.Margin=new(3,6,3,12);body.Children.Add(status);local.Margin=new(3,0,3,16);body.Children.Add(local);
        var information=new StackPanel();information.Children.Add(Ui.Heading("Your history stays with you",16));
        var explanation=Ui.Text("DeadWare checks normal GitHub releases once at startup. A notice lets you know when a newer version is available; you choose when to download and install it. Update & restart saves your workspace and history, installs the verified package, then reopens DeadWare. Rollback restores the previous app files while keeping current settings and recordings.",13,"Muted");explanation.Margin=new(0,8,0,8);information.Children.Add(explanation);
        information.Children.Add(Ui.Text("The first updater-enabled installation may have no earlier version to restore. A previous version becomes available after an in-app update.",12,"Muted"));body.Children.Add(Card(information));
        var releaseNotes=new StackPanel();
        var notesHeader=new DockPanel();history=Ui.Button("Show release note history",()=>_ = ToggleHistoryAsync());
        history.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(history,Dock.Right);notesHeader.Children.Add(history);notesHeader.Children.Add(notesHeading);releaseNotes.Children.Add(notesHeader);
        notesStatus.Margin=new(0,10,0,0);releaseNotes.Children.Add(notesStatus);
        retryHistory=Ui.Button("Retry history",()=>_ = RunAsync(service.ReadHistoryAsync));retryHistory.HorizontalAlignment=HorizontalAlignment.Left;releaseNotes.Children.Add(retryHistory);
        releaseNotes.Children.Add(noteSections);body.Children.Add(Card(releaseNotes));
        Loaded+=async(_,_)=>{service.Changed+=Changed;ApplyState(service.State);await service.ReadLocalAsync();};
        Unloaded+=(_,_)=>service.Changed-=Changed;
        IsVisibleChanged+=async(_,_)=>{if(IsLoaded&&IsVisible)await service.ReadLocalAsync();};ApplyState(service.State);
    }
    private static Border Card(UIElement child){var panel=Ui.Panel(child,new Thickness(16));panel.Margin=new(0,0,0,12);return panel;}
    private void Changed(){if(Dispatcher.HasShutdownStarted)return;if(Dispatcher.CheckAccess())ApplyState(service.State);else Dispatcher.InvokeAsync(()=>{if(!Dispatcher.HasShutdownStarted)ApplyState(service.State);});}
    internal void ApplyState(UpdateState state)
    {
        installed.Text="Installed version "+state.InstalledVersion;
        latest.Text=state.Release is null?"Normal GitHub releases":"Published version "+state.Release.Version;
        last.Text=state.LastChecked is { } checkedAt?$"Last checked {checkedAt:g}":"Checks once at startup. You can also check here.";
        status.Text=state.Status;local.Text=state.LocalStatus+(state.LastOperation.Length>0?"\nLast operation: "+state.LastOperation:"");
        ApplyReleaseNotes(state);
        check.IsEnabled=!state.Busy;update.IsEnabled=state.CanUpdate;rollback.IsEnabled=state.CanRollback;
        history.IsEnabled=retryHistory.IsEnabled=!state.Busy;
        history.Content=showHistory?"Hide release note history":"Show release note history";
        retryHistory.Visibility=showHistory&&!state.Busy&&state.ReleaseHistoryStatus.Length>0?Visibility.Visible:Visibility.Collapsed;
        rollback.Content=state.Previous is { } prior?$"Roll back to {prior.Version} & restart":"Roll back & restart";
        cancel.IsEnabled=state.CanCancel;cancel.Visibility=state.Busy?Visibility.Visible:Visibility.Collapsed;
        progress.Visibility=state.Busy?Visibility.Visible:Visibility.Collapsed;progress.Value=state.Progress;progress.IsIndeterminate=state.Busy&&state.Progress==0;
    }
    private void ApplyReleaseNotes(UpdateState state)
    {
        notesHeading.Text=showHistory?"Release note history":"Release notes";
        notesStatus.Text=showHistory?state.ReleaseHistoryStatus:state.ReleaseNotesStatus;notesStatus.Visibility=notesStatus.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        var notes=showHistory?state.ReleaseHistory:state.ReleaseNotes;
        var selectedRelease=showHistory?null:state.Release;
        if(ReferenceEquals(displayedNotes,notes)&&displayedRelease==selectedRelease&&displayedHistory==showHistory&&displayedHistoryLoaded==state.ReleaseHistoryLoaded)return;
        displayedNotes=notes;displayedRelease=selectedRelease;displayedHistory=showHistory;displayedHistoryLoaded=state.ReleaseHistoryLoaded;noteSections.Children.Clear();
        IReadOnlyList<ReleaseNote> entries=notes.Count>0?notes:selectedRelease is { } release?[new(release.Version,release.Tag,release.Notes)]:[];
        if(entries.Count==0)
        {
            if(!showHistory)noteSections.Children.Add(Ui.Text("Release notes appear after a published release is found.",12));
            else if(state.ReleaseHistoryLoaded)noteSections.Children.Add(Ui.Text("No normal release notes have been published.",12));
            return;
        }
        foreach(var entry in entries)
        {
            var section=new StackPanel {Margin=new Thickness(0,12,0,0)};
            var heading=Ui.Heading(entry.Tag,14);heading.FontWeight=FontWeights.SemiBold;section.Children.Add(heading);
            var text=Ui.Text(string.IsNullOrWhiteSpace(entry.Notes)?"No release notes provided.":entry.Notes,12);text.Margin=new(0,6,0,0);section.Children.Add(text);noteSections.Children.Add(section);
        }
    }
    private async Task ToggleHistoryAsync()
    {
        showHistory=!showHistory;ApplyState(service.State);
        if(showHistory)await RunAsync(service.ReadHistoryAsync);
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
