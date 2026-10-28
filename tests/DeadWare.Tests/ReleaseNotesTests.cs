using System.Net;
using System.Net.Http;
using System.Text.Json;
using DeadWare.UpdateCore;
using Xunit;

namespace DeadWare.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public async Task Full_history_includes_all_normal_versions_without_a_latest_release_or_assets()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("/repos/DeadeyeDuncan/DeadWare/releases", request.RequestUri.AbsolutePath);
            return Task.FromResult(Response(Page(
                Row("v0.1.0", "Old"), Row("v0.6.2", "Installed"), Row("v0.10.0", "Newest"),
                Row("v0.0.0", "First"), Row("0.6.2", "Duplicate"), Row("v0.11.0", draft: true),
                Row("v0.12.0", prerelease: true), Row("v0.13.0-rc.1"), Row("latest"), Row("v0.13.0+build"))));
        }));

        var notes = await new ReleaseClient(http).ReadHistoryAsync(CancellationToken.None);

        Assert.Equal(new[] { "v0.10.0", "v0.6.2", "v0.1.0", "v0.0.0" }, notes.Select(note => note.Tag));
        Assert.Equal(new[] { "Newest", "Installed", "Old", "First" }, notes.Select(note => note.Notes));
        Assert.Throws<NotSupportedException>((Action)(() => ((IList<ReleaseNote>)notes).Add(notes[0])));
    }

    [Fact]
    public async Task Full_history_can_be_empty_without_seeding_a_latest_release()
    {
        using var http = Client("[]");

        Assert.Empty(await new ReleaseClient(http).ReadHistoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task All_newer_published_versions_are_returned_newest_first_without_requiring_assets()
    {
        using var http = Client(Page(Row("v0.6.3", "Three"), Row("v0.6.4", "Four"), Row("v0.6.5", "List five")));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None);

        Assert.Equal(new[] { "0.6.5", "0.6.4", "0.6.3" }, notes.Select(note => note.Version.ToString()));
        Assert.Equal(new[] { "Latest five", "Four", "Three" }, notes.Select(note => note.Notes));
    }

    [Fact]
    public async Task Bounds_previews_drafts_and_malformed_tags_are_excluded()
    {
        using var http = Client(Page(
            Row("v0.6.1"), Row("v0.6.2"), Row("0.6.3", "Included"), Row("v0.6.6"),
            Row("v0.6.4", prerelease: true), Row("v0.6.4", draft: true), Row("v0.6.4-rc.1"),
            Row("v0.6.4+build"), Row("latest"), Row("v00.6.4"), Row("v0.6"), Row("v0.6.4.1"),
            Row("V0.6.4"), Row("v99999999999.6.4"), new { tag_name = 42 }, new { body = true }));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2+installed", Latest(), CancellationToken.None);

        Assert.Equal(new[] { "v0.6.5", "0.6.3" }, notes.Select(note => note.Tag));
    }

    [Fact]
    public async Task Numeric_versions_are_sorted_even_when_older_releases_appear_first()
    {
        using var http = Client(Page(Row("v0.6.1"), Row("v0.9.0", "Nine"), Row("v0.7.0", "Seven"), Row("v0.10.0")));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest("0.10.0"), CancellationToken.None);

        Assert.Equal(new[] { "0.10.0", "0.9.0", "0.7.0" }, notes.Select(note => note.Version.ToString()));
    }

    [Fact]
    public async Task Versions_are_deduplicated_and_validated_latest_notes_win()
    {
        using var http = Client(Page(Row("0.6.5", "Wrong latest"), Row("v0.6.4", "First four"), Row("0.6.4", "Other four")));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None);

        Assert.Equal(2, notes.Count);
        Assert.Equal("Latest five", notes[0].Notes);
        Assert.Equal("v0.6.5", notes[0].Tag);
        Assert.Equal("First four", notes[1].Notes);
        Assert.Throws<NotSupportedException>(() => ((IList<ReleaseNote>)notes).Add(notes[0]));
    }

    [Fact]
    public async Task Missing_null_and_empty_bodies_are_empty_and_long_notes_are_bounded()
    {
        using var http = Client(Page(Row("v0.6.1", null), Row("v0.6.2", ""),
            new { tag_name = "v0.6.3", draft = false, prerelease = false }, Row("v0.6.4", new string('a', 16001))));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.0", Latest(), CancellationToken.None);

        Assert.Equal(new[] { "Latest five", new string('a', 16000), "", "", "" }, notes.Select(note => note.Notes));
    }

    [Theory]
    [InlineData("0.6.5")]
    [InlineData("0.6.6")]
    public async Task Latest_not_newer_than_installed_returns_no_notes_without_http(string installed)
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("HTTP was unnecessary.")));

        Assert.Empty(await new ReleaseClient(http).ReadNotesAsync(installed, Latest(), CancellationToken.None));
    }

    [Fact]
    public async Task All_pages_are_read_even_after_old_tags_and_notes_use_pinned_public_repository()
    {
        var first = Enumerable.Range(0, 100).Select(_ => Row("v0.1.0")).ToArray();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("/repos/DeadeyeDuncan/DeadWare/releases", request.RequestUri.AbsolutePath);
            Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            Assert.Contains("DeadWare", request.Headers.UserAgent.ToString());
            return Task.FromResult(request.RequestUri.Query switch
            {
                "?per_page=100&page=1" => Response(Page(first)),
                "?per_page=100&page=2" => Response(Page(Row("v0.6.3", "Three"), Row("v0.6.4", "Four"))),
                _ => throw new InvalidOperationException("Unexpected pagination request.")
            });
        }));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None);

        Assert.Equal(new[] { "0.6.5", "0.6.4", "0.6.3" }, notes.Select(note => note.Version.ToString()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("not json")]
    [InlineData("[{\"tag_name\":\"v0.6.4\",\"draft\":false,\"prerelease\":false,\"body\":12}]")]
    [InlineData("[{\"tag_name\":\"v0.6.4\",\"draft\":false,\"prerelease\":\"false\",\"body\":\"Four\"}]")]
    public async Task Invalid_response_envelopes_and_relevant_metadata_report_readable_errors(string json)
    {
        using var http = Client(json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None));

        Assert.Contains("release", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_bodies_for_irrelevant_tags_are_not_parsed()
    {
        using var http = Client("[{\"tag_name\":\"junk\",\"body\":42},{\"tag_name\":\"v0.1.0\",\"body\":42}]");

        Assert.Single(await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None));
    }

    [Fact]
    public async Task Ten_full_pages_fail_instead_of_returning_partial_history()
    {
        string page = Page(Enumerable.Range(0, 100).Select(_ => Row("v0.1.0")).ToArray());
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal($"?per_page=100&page={requests}", request.RequestUri!.Query);
            return Task.FromResult(Response(page));
        }));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None));

        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, requests);
    }

    [Fact]
    public async Task A_short_tenth_page_completes_the_history()
    {
        string fullPage = Page(Enumerable.Range(0, 100).Select(_ => Row("v0.1.0")).ToArray());
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Response(
            request.RequestUri!.Query == "?per_page=100&page=10" ? Page(Row("v0.6.3")) : fullPage))));

        var notes = await new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None);

        Assert.Equal(new[] { "0.6.5", "0.6.3" }, notes.Select(note => note.Version.ToString()));
    }

    [Fact]
    public async Task Later_page_network_failures_do_not_return_partial_notes()
    {
        string fullPage = Page(Enumerable.Range(0, 100).Select(_ => Row("v0.6.4")).ToArray());
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(
            request.RequestUri!.Query.EndsWith("page=1") ? Response(fullPage) : new HttpResponseMessage(HttpStatusCode.Forbidden))));

        await Assert.ThrowsAsync<HttpRequestException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_during_a_later_page_is_propagated()
    {
        using var cancel = new CancellationTokenSource();
        string fullPage = Page(Enumerable.Range(0, 100).Select(_ => Row("v0.6.4")).ToArray());
        using var http = new HttpClient(new Handler((request, token) =>
        {
            if (request.RequestUri!.Query.EndsWith("page=1")) return Task.FromResult(Response(fullPage));
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Response("[]"));
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), cancel.Token));
    }

    [Fact]
    public async Task Already_cancelled_history_is_not_read()
    {
        using var http = Client("[]");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), cancel.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_history_metadata_is_rejected_with_or_without_a_declared_length(bool streamed)
    {
        byte[] bytes = new byte[2 * 1024 * 1024 + 1];
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = streamed ? new StreamContent(new UnseekableStream(bytes)) : new ByteArrayContent(bytes)
        })));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).ReadNotesAsync("0.6.2", Latest(), CancellationToken.None));
    }

    private static ReleaseInfo Latest(string version = "0.6.5") => new(new Version(version), "v" + version, "Latest five",
        new Uri("https://github.com/DeadeyeDuncan/DeadWare/releases/download/v" + version + "/DeadWare-" + version + "-win-x64.zip"),
        new Uri("https://github.com/DeadeyeDuncan/DeadWare/releases/download/v" + version + "/DeadWare-" + version + "-win-x64.zip.sha256"),
        new string('a', 64), 123);
    private static object Row(string tag, string? body = "Notes", bool draft = false, bool prerelease = false) => new { tag_name = tag, body, draft, prerelease };
    private static string Page(params object[] rows) => JsonSerializer.Serialize(rows);
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpClient Client(string json) => new(new Handler((_, _) => Task.FromResult(Response(json))));
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return response(request, cancellationToken);
        }
    }
}
