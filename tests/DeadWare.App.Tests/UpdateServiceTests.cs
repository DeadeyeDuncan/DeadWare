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
