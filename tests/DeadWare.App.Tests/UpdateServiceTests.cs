using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using DeadWare.App;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.App.Tests;

public class UpdateServiceTests
{
    [Fact] public async Task Startup_checks_once_and_announces_each_newer_version_once_without_downloading()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-startup-update-"+Guid.NewGuid().ToString("N"));
        var handler=new Handler{Content=Release()};using var http=new HttpClient(handler);
        var service=new UpdateService("0.6.0",Path.Combine(root,"app"),http,Path.Combine(root,"cache"));
        var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);Assert.Equal(0,handler.Calls);
        await Task.WhenAll(Startup(service),Startup(service));
        Assert.Equal(1,handler.Calls);Assert.Single(notices);Assert.False(Directory.Exists(root));
        Assert.Equal(1,handler.NotesCalls);
        Assert.False(service.State.CanUpdate); // The availability notice does not require or change app files.
        await service.CheckAsync(CancellationToken.None);Assert.Single(notices);
        handler.Content=Release().Replace("0.6.1","0.6.2");await service.CheckAsync(CancellationToken.None);
        handler.Content=Release();await service.CheckAsync(CancellationToken.None);
        Assert.Equal(new[]{new Version(0,6,1),new Version(0,6,2)},notices.Select(n=>n.Version));
        Assert.Equal(4,handler.Calls);Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData("0.6.1",false)] [InlineData("0.7.0",false)] [InlineData("0.6.0",true)]
    public async Task Startup_does_not_announce_current_older_or_preview_releases(string installed,bool preview)
    {
        var handler=new Handler{Content=preview?Release().Replace("\"prerelease\":false","\"prerelease\":true"):Release()};
        using var http=new HttpClient(handler);var service=new UpdateService(installed,http:http);
        var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);await Startup(service);
        Assert.Empty(notices);Assert.False(service.State.Busy);
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound)] [InlineData(HttpStatusCode.Forbidden)] [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Unavailable_startup_checks_finish_without_a_notification_or_install(HttpStatusCode status)
    {
        using var http=new HttpClient(new Handler{Status=status});var service=new UpdateService("0.6.0",http:http);
        var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);await Startup(service);
        Assert.Empty(notices);Assert.False(service.State.Busy);Assert.False(service.State.CanUpdate);
    }
    [Fact] public async Task Startup_network_work_is_asynchronous_and_can_be_cancelled_for_shutdown()
    {
        var handler=new WaitingHandler();using var http=new HttpClient(handler);using var cancellation=new CancellationTokenSource();
        var service=new UpdateService("0.6.0",http:http);var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);
        var check=Startup(service,cancellation.Token);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(check.IsCompleted);cancellation.Cancel();await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(notices);Assert.False(service.State.Busy);Assert.Contains("cancelled",service.State.Status,StringComparison.OrdinalIgnoreCase);
    }
    private static Task Startup(UpdateService service,CancellationToken token=default)
        =>service.CheckAtStartupAsync(token);
    private static void Subscribe(UpdateService service,Action<ReleaseInfo> notification)
        =>service.UpdateAvailable+=notification;
    private sealed class WaitingHandler:HttpMessageHandler
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Started.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return new(HttpStatusCode.OK);}
    }
    [Fact] public async Task An_unfinished_install_offers_recovery_instead_of_another_update()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-pending-"+Guid.NewGuid().ToString("N"));
        try
        {
            string Package(string name,string version)
            {
                string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
                foreach(string file in new[]{"DeadWare.exe","DeadWare.dll","DeadWare.Updater.exe"})File.WriteAllText(Path.Combine(folder,file),version+file);
                SafeFiles.WriteJson(Path.Combine(folder,Packages.ManifestName),new PackageManifest(1,version,Directory.GetFiles(folder).ToDictionary(f=>Path.GetFileName(f)!,Packages.Hash)));return folder;
            }
            string installed=Package("app","0.6.0"),stage=Package("stage","0.6.1"),cache=Path.Combine(root,"cache");
            new InstallTransaction(new UpdatePaths(installed,cache)).Apply(stage,"0.6.0","0.6.1");
            using var http=new HttpClient(new Handler{Content=Release().Replace("0.6.1","0.6.2")});
            var service=new UpdateService("0.6.1",installed,http,cache);await service.ReadLocalAsync();await service.CheckAsync(CancellationToken.None);
            Assert.True(service.State.CanRollback);Assert.False(service.State.CanUpdate);Assert.Contains("unfinished",service.State.LocalStatus);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task Opening_update_view_does_not_check_online_and_missing_package_disables_install()
    {
        var handler=new Handler();using var http=new HttpClient(handler);
        var service=new UpdateService("0.6.0",Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")),http);
        Assert.Equal(0,handler.Calls);await service.ReadLocalAsync();Assert.Equal(0,handler.Calls);
        Assert.False(service.State.CanUpdate);Assert.False(service.State.CanRollback);
        await service.CheckAsync(CancellationToken.None);Assert.Equal(1,handler.Calls);
        Assert.Contains("No normal release",service.State.Status);Assert.False(service.State.Busy);
    }
    [Fact] public async Task A_managed_file_changed_after_verification_disables_install_instead_of_throwing()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-changed-"+Guid.NewGuid().ToString("N"));
        try
        {
            string installed=Package(root,"app","0.6.0");var service=new UpdateService("0.6.0",installed,cacheBase:Path.Combine(root,"cache"));
            await service.ReadLocalAsync();Assert.True(service.State.InstallationVerified);
            File.WriteAllText(Path.Combine(installed,"DeadWare.dll"),"modified");
            await service.ReadLocalAsync();
            Assert.False(service.State.InstallationVerified);Assert.Null(service.State.Previous);Assert.False(service.State.CanUpdate);Assert.False(service.State.CanRollback);
            Assert.StartsWith("Install the complete updater-enabled package before updating from this folder. ",service.State.LocalStatus);Assert.Contains("DeadWare.dll",service.State.LocalStatus);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task A_corrupted_rollback_snapshot_disables_install_instead_of_throwing()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-rollback-"+Guid.NewGuid().ToString("N"));
        try
        {
            string installed=Package(root,"app","0.6.0"),stage=Package(root,"stage","0.6.1");var paths=new UpdatePaths(installed,Path.Combine(root,"cache"));
            var transaction=new InstallTransaction(paths);transaction.Apply(stage,"0.6.0","0.6.1");transaction.Commit();
            var service=new UpdateService("0.6.1",installed,cacheBase:Path.Combine(root,"cache"));
            await service.ReadLocalAsync();Assert.True(service.State.InstallationVerified);Assert.True(service.State.CanRollback);
            var record=SafeFiles.ReadJson<BackupRecord>(paths.RollbackFile);File.WriteAllText(Path.Combine(paths.BackupDirectory(record.Id),"DeadWare.dll"),"corrupted");
            await service.ReadLocalAsync();
            Assert.False(service.State.InstallationVerified);Assert.Null(service.State.Previous);Assert.False(service.State.CanUpdate);Assert.False(service.State.CanRollback);
            Assert.StartsWith("Install the complete updater-enabled package before updating from this folder. ",service.State.LocalStatus);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static string Package(string root,string name,string version)
    {
        string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
        foreach(string file in new[]{"DeadWare.exe","DeadWare.dll","DeadWare.Updater.exe"})File.WriteAllText(Path.Combine(folder,file),version+file);
        SafeFiles.WriteJson(Path.Combine(folder,Packages.ManifestName),new PackageManifest(1,version,Directory.GetFiles(folder).ToDictionary(f=>Path.GetFileName(f)!,Packages.Hash)));return folder;
    }
    [Fact] public async Task Verified_package_allows_a_newer_normal_release_and_never_automatic_downgrade()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-updates-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var files=new Dictionary<string,string>{{"DeadWare.exe","app"},{"DeadWare.dll","assembly"},{"DeadWare.Updater.exe","helper"}};
            foreach(var pair in files)File.WriteAllText(Path.Combine(root,pair.Key),pair.Value);
            SafeFiles.WriteJson(Path.Combine(root,Packages.ManifestName),new PackageManifest(1,"0.6.0",files.ToDictionary(p=>p.Key,p=>Packages.Hash(Path.Combine(root,p.Key)))));
            var handler=new Handler{Content=Release()};using var http=new HttpClient(handler);
            var service=new UpdateService("0.6.0",root,http,Path.Combine(root,"cache"));
            await service.ReadLocalAsync();await service.CheckAsync(CancellationToken.None);Assert.True(service.State.CanUpdate);
            Assert.False(service.State.CanRollback);Assert.Equal(new Version(0,6,1),service.State.Release!.Version);
            var newer=new UpdateService("0.7.0",root,http,Path.Combine(root,"cache"));await newer.CheckAsync(CancellationToken.None);
            Assert.False(newer.State.CanUpdate);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task User_cancel_and_rate_limit_leave_the_app_available()
    {
        using var http=new HttpClient(new Handler{Status=HttpStatusCode.Forbidden});var service=new UpdateService("0.6.0",http:http);
        await service.CheckAsync(CancellationToken.None);Assert.Contains("limit",service.State.Status);Assert.False(service.State.Busy);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await service.CheckAsync(cancelled.Token);
        Assert.Contains("cancelled",service.State.Status,StringComparison.OrdinalIgnoreCase);Assert.False(service.State.Busy);
    }
    [Fact] public async Task Check_collects_every_missed_normal_version_and_clears_old_notes_when_release_disappears()
    {
        var handler=new Handler {Content=Release("0.6.5","Latest notes"),NotesContent=JsonSerializer.Serialize(new[]{
            new{tag_name="v0.6.3",draft=false,prerelease=false,body="Widget graphs"},
            new{tag_name="v0.6.2",draft=false,prerelease=false,body="Already installed"},
            new{tag_name="v0.6.4",draft=false,prerelease=false,body="Banner fix"}})};
        using var http=new HttpClient(handler);var service=new UpdateService("0.6.2",http:http);
        await service.CheckAsync(CancellationToken.None);
        Assert.Equal(new[]{"0.6.5","0.6.4","0.6.3"},service.State.ReleaseNotes.Select(n=>n.Version.ToString()));
        Assert.Equal(new Version(0,6,5),service.State.Release!.Version);Assert.Equal("Latest notes",service.State.Release.Notes);
        Assert.Equal("",service.State.ReleaseNotesStatus);Assert.False(service.State.Busy);
        handler.Content=null;await service.CheckAsync(CancellationToken.None);
        Assert.Null(service.State.Release);Assert.Empty(service.State.ReleaseNotes);Assert.Equal("",service.State.ReleaseNotesStatus);
    }
    [Theory] [InlineData("0.6.5")] [InlineData("0.7.0")]
    public async Task Current_or_newer_installs_do_not_fetch_history(string installed)
    {
        var handler=new Handler{Content=Release("0.6.5")};using var http=new HttpClient(handler);
        var service=new UpdateService(installed,http:http);await service.CheckAsync(CancellationToken.None);
        Assert.Equal(0,handler.NotesCalls);Assert.False(service.State.HasNewerRelease);
        Assert.Single(service.State.ReleaseNotes);Assert.Equal("",service.State.ReleaseNotesStatus);
    }
    [Theory] [InlineData(HttpStatusCode.ServiceUnavailable)] [InlineData(HttpStatusCode.Forbidden)]
    public async Task Failed_note_history_keeps_latest_update_and_notification(HttpStatusCode status)
    {
        var handler=new Handler{Content=Release("0.6.5","Latest notes"),NotesStatus=status};using var http=new HttpClient(handler);
        var service=new UpdateService("0.6.2",http:http);var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);
        typeof(UpdateService).GetField("state",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
            .SetValue(service,service.State with {InstallationVerified=true});
        await service.CheckAsync(CancellationToken.None);
        Assert.True(service.State.CanUpdate);Assert.Single(notices);Assert.False(service.State.Busy);
        Assert.Equal("Latest notes",Assert.Single(service.State.ReleaseNotes).Notes);
        Assert.Contains("latest",service.State.ReleaseNotesStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("available",service.State.Status,StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task Cancelling_note_history_finishes_check_and_keeps_latest_fallback()
    {
        var handler=new NotesWaitingHandler();using var http=new HttpClient(handler);using var cancellation=new CancellationTokenSource();
        var service=new UpdateService("0.6.2",http:http);var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);
        var check=service.CheckAsync(cancellation.Token);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(notices);Assert.True(service.State.HasNewerRelease);Assert.Single(service.State.ReleaseNotes);
        cancellation.Cancel();await check.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.State.Busy);Assert.Contains("cancelled",service.State.Status,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Loading",service.State.ReleaseNotesStatus);Assert.Single(service.State.ReleaseNotes);
    }
    [Fact] public async Task Local_install_verification_can_finish_while_note_history_is_loading()
    {
        string root=Path.Combine(Path.GetTempPath(),"DeadWare-notes-verification-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var handler=new NotesWaitingHandler();using var http=new HttpClient(handler);using var cancellation=new CancellationTokenSource();
        var service=new UpdateService("0.6.2",root,http,Path.Combine(root,"cache"));
        Task check=Task.CompletedTask;
        try
        {
            var files=new Dictionary<string,string>{{"DeadWare.exe","app"},{"DeadWare.dll","assembly"},{"DeadWare.Updater.exe","helper"}};
            foreach(var pair in files)File.WriteAllText(Path.Combine(root,pair.Key),pair.Value);
            SafeFiles.WriteJson(Path.Combine(root,Packages.ManifestName),new PackageManifest(1,"0.6.2",files.ToDictionary(p=>p.Key,p=>Packages.Hash(Path.Combine(root,p.Key)))));
            check=service.CheckAsync(cancellation.Token);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.State.Busy);await service.ReadLocalAsync();
            Assert.True(service.State.InstallationVerified);Assert.False(service.State.CanUpdate);
            cancellation.Cancel();await check.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(service.State.CanUpdate);
        }
        finally{cancellation.Cancel();await check;Directory.Delete(root,true);}
    }
    [Fact] public async Task History_can_be_requested_when_current_without_checking_latest_or_announcing()
    {
        var handler=new Handler{NotesContent=JsonSerializer.Serialize(new[]{
            new{tag_name="v0.1.0",draft=false,prerelease=false,body="First release"},
            new{tag_name="v0.6.5",draft=false,prerelease=false,body="Current release"}})};
        using var http=new HttpClient(handler);var service=new UpdateService("0.6.5",http:http);
        var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);string status=service.State.Status;
        Assert.False(service.State.ReleaseHistoryLoaded);
        await service.ReadHistoryAsync(CancellationToken.None);
        Assert.True(service.State.ReleaseHistoryLoaded);Assert.Equal("",service.State.ReleaseHistoryStatus);
        Assert.Equal(new[]{"v0.6.5","v0.1.0"},service.State.ReleaseHistory.Select(note=>note.Tag));
        Assert.Equal(0,handler.Calls);Assert.Equal(1,handler.NotesCalls);Assert.Empty(notices);
        Assert.Null(service.State.Release);Assert.Empty(service.State.ReleaseNotes);Assert.Null(service.State.LastChecked);
        Assert.Equal(status,service.State.Status);Assert.False(service.State.Busy);Assert.False(service.State.CanCancel);
    }
    [Fact] public async Task History_preserves_available_update_missed_notes_and_last_checked()
    {
        var handler=new Handler{Content=Release("0.6.5","Latest notes"),NotesContent=JsonSerializer.Serialize(new[]{
            new{tag_name="v0.6.4",draft=false,prerelease=false,body="Missed release"},
            new{tag_name="v0.6.1",draft=false,prerelease=false,body="Older release"}})};
        using var http=new HttpClient(handler);var service=new UpdateService("0.6.2",http:http);
        var notices=new List<ReleaseInfo>();Subscribe(service,notices.Add);
        typeof(UpdateService).GetField("state",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
            .SetValue(service,service.State with {InstallationVerified=true});
        await service.CheckAsync(CancellationToken.None);var before=service.State;
        await service.ReadHistoryAsync(CancellationToken.None);
        Assert.Same(before.Release,service.State.Release);Assert.Same(before.ReleaseNotes,service.State.ReleaseNotes);
        Assert.Equal(before.ReleaseNotesStatus,service.State.ReleaseNotesStatus);Assert.Equal(before.LastChecked,service.State.LastChecked);
        Assert.Equal(before.Status,service.State.Status);Assert.True(service.State.CanUpdate);Assert.Single(notices);
        Assert.Equal(new[]{"v0.6.4","v0.6.1"},service.State.ReleaseHistory.Select(note=>note.Tag));
    }
    [Theory] [InlineData(HttpStatusCode.ServiceUnavailable)] [InlineData(HttpStatusCode.Forbidden)] [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Failed_history_refresh_keeps_previous_notes_and_can_be_retried(HttpStatusCode status)
    {
        var handler=new Handler{NotesContent="[{\"tag_name\":\"v0.6.1\",\"draft\":false,\"prerelease\":false,\"body\":\"Earlier notes\"}]"};
        using var http=new HttpClient(handler);var service=new UpdateService("0.6.5",http:http);
        await service.ReadHistoryAsync(CancellationToken.None);var before=service.State;
        handler.NotesStatus=status;await service.ReadHistoryAsync(CancellationToken.None);
        Assert.Same(before.ReleaseHistory,service.State.ReleaseHistory);Assert.True(service.State.ReleaseHistoryLoaded);
        Assert.Equal(before.Status,service.State.Status);Assert.False(service.State.Busy);Assert.False(service.State.CanCancel);
        Assert.Contains("retry",service.State.ReleaseHistoryStatus,StringComparison.OrdinalIgnoreCase);
        if(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)Assert.Contains("limit",service.State.ReleaseHistoryStatus,StringComparison.OrdinalIgnoreCase);
        handler.NotesStatus=HttpStatusCode.OK;handler.NotesContent="[]";await service.ReadHistoryAsync(CancellationToken.None);
        Assert.True(service.State.ReleaseHistoryLoaded);Assert.Empty(service.State.ReleaseHistory);Assert.Equal("",service.State.ReleaseHistoryStatus);
        Assert.Equal(3,handler.NotesCalls);Assert.Equal(0,handler.Calls);
    }
    [Fact] public async Task History_loading_is_cancellable_blocks_overlapping_operations_and_can_be_retried()
    {
        var handler=new HistoryWaitingHandler();using var http=new HttpClient(handler);using var cancellation=new CancellationTokenSource();
        var service=new UpdateService("0.6.5",http:http);string status=service.State.Status;
        var loading=service.ReadHistoryAsync(cancellation.Token);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.State.Busy);Assert.True(service.State.CanCancel);Assert.Contains("Loading",service.State.ReleaseHistoryStatus);
        await service.ReadHistoryAsync(CancellationToken.None);await service.CheckAsync(CancellationToken.None);
        Assert.Equal(1,handler.Calls);
        cancellation.Cancel();await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.State.ReleaseHistoryLoaded);Assert.Empty(service.State.ReleaseHistory);
        Assert.Contains("cancelled",service.State.ReleaseHistoryStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status,service.State.Status);Assert.False(service.State.Busy);Assert.False(service.State.CanCancel);
        handler.Continue.TrySetResult();await service.ReadHistoryAsync(CancellationToken.None);
        Assert.True(service.State.ReleaseHistoryLoaded);Assert.Empty(service.State.ReleaseHistory);Assert.Equal("",service.State.ReleaseHistoryStatus);
        Assert.Equal(2,handler.Calls);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Status_reported_while_history_is_loading_survives_completion_or_cancellation(bool cancel)
    {
        var handler=new HistoryWaitingHandler();using var http=new HttpClient(handler);using var cancellation=new CancellationTokenSource();
        var service=new UpdateService("0.6.5",http:http);
        var loading=service.ReadHistoryAsync(cancellation.Token);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ReportFailure(new IOException("Newer operation status"));string reported=service.State.Status;
        if(cancel)cancellation.Cancel();else handler.Continue.TrySetResult();
        await loading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(reported,service.State.Status);Assert.False(service.State.Busy);
    }
    [Fact] public async Task Empty_history_is_successful_and_explicit_requests_refresh_it()
    {
        var handler=new Handler();using var http=new HttpClient(handler);var service=new UpdateService("0.6.5",http:http);
        await service.ReadHistoryAsync(CancellationToken.None);await service.ReadHistoryAsync(CancellationToken.None);
        Assert.True(service.State.ReleaseHistoryLoaded);Assert.Empty(service.State.ReleaseHistory);Assert.Equal("",service.State.ReleaseHistoryStatus);
        Assert.Equal(2,handler.NotesCalls);Assert.Equal(0,handler.Calls);
    }
    private sealed class HistoryWaitingHandler:HttpMessageHandler
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Assert.EndsWith("/releases",request.RequestUri!.AbsolutePath);Calls++;Started.TrySetResult();
            await Continue.Task.WaitAsync(token);return new(HttpStatusCode.OK){Content=new StringContent("[]")};
        }
    }
    private sealed class NotesWaitingHandler:HttpMessageHandler
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/latest"))return new(HttpStatusCode.OK){Content=new StringContent(Release("0.6.5"))};
            Started.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);return new(HttpStatusCode.OK);
        }
    }
    private static string Release(string version="0.6.1",string notes="Notes")=>JsonSerializer.Serialize(new{tag_name="v"+version,draft=false,prerelease=false,body=notes,assets=new[]{
        new{name=$"DeadWare-{version}-win-x64.zip",size=100,digest="sha256:"+new string('a',64),browser_download_url=$"https://github.com/DeadeyeDuncan/DeadWare/releases/download/v{version}/DeadWare-{version}-win-x64.zip"},
        new{name=$"DeadWare-{version}-win-x64.zip.sha256",size=94,digest="sha256:"+new string('b',64),browser_download_url=$"https://github.com/DeadeyeDuncan/DeadWare/releases/download/v{version}/DeadWare-{version}-win-x64.zip.sha256"}}});
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls,NotesCalls;public string? Content;public HttpStatusCode Status=HttpStatusCode.NotFound;
        public string NotesContent="[]";public HttpStatusCode NotesStatus=HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(request.RequestUri!.AbsolutePath.EndsWith("/releases")){NotesCalls++;return Task.FromResult(new HttpResponseMessage(NotesStatus){Content=new StringContent(NotesContent)});}
            Calls++;return Task.FromResult(new HttpResponseMessage(Content is null?Status:HttpStatusCode.OK){Content=new StringContent(Content??"")});
        }
    }
}
