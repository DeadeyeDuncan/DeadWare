using System.Net;
using System.Net.Http;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class UpdateNetworkTests
{
    [Fact] public async Task Download_verifies_real_bytes_and_does_not_overwrite_an_existing_file()
    {
        using var fixture=new UpdateFixture();string zip=fixture.Zip();byte[] bytes=File.ReadAllBytes(zip);
        var release=ReleasePolicy.Parse(UpdatePolicyTests.Release())! with {Size=bytes.Length,Sha256=Packages.Hash(zip)};
        using var http=new HttpClient(new Handler(request=>new(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath.EndsWith(".sha256")?
            new StringContent(release.Sha256+"  "+release.FileName):new ByteArrayContent(bytes)}));
        string destination=Path.Combine(fixture.Root,"download.zip");
        await new ReleaseClient(http).DownloadAsync(release,destination,null,CancellationToken.None);
        Assert.Equal(bytes,File.ReadAllBytes(destination));
        await Assert.ThrowsAsync<IOException>(()=>new ReleaseClient(http).DownloadAsync(release,destination,null,CancellationToken.None));
    }
    [Fact] public async Task No_requests_are_sent_until_manual_check_and_404_means_no_normal_release()
    {
        var handler=new Handler(_=>new(HttpStatusCode.NotFound));using var client=new HttpClient(handler);
        var releases=new ReleaseClient(client);Assert.Equal(0,handler.Count);
        Assert.Null(await releases.CheckAsync(CancellationToken.None));Assert.Equal(1,handler.Count);
        Assert.Equal(ReleasePolicy.Endpoint,handler.Last!.RequestUri!.AbsoluteUri);
    }
    [Fact] public async Task Rate_limits_and_oversized_metadata_report_errors()
    {
        using var limited=new HttpClient(new Handler(_=>new(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<HttpRequestException>(()=>new ReleaseClient(limited).CheckAsync(CancellationToken.None));
        using var huge=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(new string('x',2*1024*1024+1))}));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new ReleaseClient(huge).CheckAsync(CancellationToken.None));
    }
    [Fact] public async Task Cancellation_does_not_create_or_replace_an_installation()
    {
        using var client=new HttpClient(new Handler(_=>new(HttpStatusCode.OK){Content=new StringContent(UpdatePolicyTests.Release())}));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ReleaseClient(client).CheckAsync(cancelled.Token));
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler
    {
        public int Count;public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {cancellationToken.ThrowIfCancellationRequested();Count++;Last=request;return Task.FromResult(response(request));}
    }
}
