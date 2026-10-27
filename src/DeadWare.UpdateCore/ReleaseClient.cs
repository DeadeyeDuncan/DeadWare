using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DeadWare.UpdateCore;

/// <summary>No background activity. The caller explicitly invokes each check/download.</summary>
public sealed class ReleaseClient(HttpClient? client = null)
{
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = client ?? Shared;
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(new Uri(ReleasePolicy.Endpoint));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ReleasePolicy.Parse(Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, deadline.Token).ConfigureAwait(false)));
    }
    public async Task DownloadAsync(ReleaseInfo release, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using (var request = Request(release.Checksum))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            ReleasePolicy.VerifyChecksum(release, Encoding.UTF8.GetString(await ReadLimitedAsync(response.Content, 4096, deadline.Token).ConfigureAwait(false)));
        }
        SafeFiles.NoLinks(destination); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var request = Request(release.Download))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } declared && declared != release.Size) throw new InvalidDataException("The release asset size changed.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536]; long received = 0; int read, priorProgress = -1;
            while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (read > release.Size - received) throw new InvalidDataException("The download exceeded its expected size.");
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false); received += read;
                int value = (int)(received * 100 / release.Size); if (value != priorProgress) { progress?.Report(value); priorProgress = value; }
            }
        }
        Packages.VerifyDownload(destination, release.Sha256, release.Size);
    }
    private static HttpRequestMessage Request(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Updates require HTTPS.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DeadWare", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); return request;
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Update metadata exceeded its size limit.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (count > limit - output.Length) throw new InvalidDataException("Update metadata exceeded its size limit."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
}
