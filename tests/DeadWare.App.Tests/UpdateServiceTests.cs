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
    private static string Release()=>JsonSerializer.Serialize(new{tag_name="v0.6.1",draft=false,prerelease=false,body="Notes",assets=new[]{
        new{name="DeadWare-0.6.1-win-x64.zip",size=100,digest="sha256:"+new string('a',64),browser_download_url="https://github.com/DeadeyeDuncan/DeadWare/releases/download/v0.6.1/DeadWare-0.6.1-win-x64.zip"},
        new{name="DeadWare-0.6.1-win-x64.zip.sha256",size=94,digest="sha256:"+new string('b',64),browser_download_url="https://github.com/DeadeyeDuncan/DeadWare/releases/download/v0.6.1/DeadWare-0.6.1-win-x64.zip.sha256"}}});
    private sealed class Handler:HttpMessageHandler
    {
        public int Calls;public string? Content;public HttpStatusCode Status=HttpStatusCode.NotFound;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls++;return Task.FromResult(new HttpResponseMessage(Content is null?Status:HttpStatusCode.OK){Content=new StringContent(Content??"")});}
    }
}
