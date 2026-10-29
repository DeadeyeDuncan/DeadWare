using System.IO;
using System.Net;
using System.Net.Http;
using System.Globalization;
using DeadWare.App;
using Xunit;
using GeoFile = DeadWare.App.Tests.ConnectionGeolocationTests.GeoFile;

namespace DeadWare.App.Tests;

public sealed class ConnectionDatabaseDownloadTests
{
    [Fact]
    public async Task Explicit_update_falls_back_only_on_current_month_404_and_atomically_replaces_valid_database()
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create());
        using var resolver = new ConnectionGeolocation(file.Path); resolver.Reload();
        var paths = new List<string>(); var progress = new List<double>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(paths.Count == 1 ? new HttpResponseMessage(HttpStatusCode.NotFound) : Response(ConnectionMmdbFixture.Gzip(ConnectionMmdbFixture.Create(record: ConnectionMmdbFixture.Record(city: "Replacement")))));
        }));
        var download = Download(http);
        Assert.Empty(paths); await download.UpdateAsync(file.Path, new ProgressSink(progress.Add), default);
        Assert.Equal(new[] { "https://download.db-ip.com/free/dbip-city-lite-2026-01.mmdb.gz", "https://download.db-ip.com/free/dbip-city-lite-2025-12.mmdb.gz" }, paths);
        Assert.Equal("Example City", resolver.Lookup("8.8.8.8")!.City);
        resolver.Reload(); Assert.Equal("Replacement", resolver.Lookup("8.8.8.8")!.City);
        Assert.Equal(1, progress[^1]); Assert.All(progress, value => Assert.InRange(value, 0, 1));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
    }

    [Theory]
    [InlineData("https://evil.example/db.mmdb.gz")]
    [InlineData("http://download.db-ip.com/free/file.gz")]
    [InlineData("https://download.db-ip.com.evil.example/file.gz")]
    public async Task Redirect_outside_official_https_host_is_rejected_before_following(string destination)
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); byte[] original = File.ReadAllBytes(file.Path); int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri(destination); return Task.FromResult(r); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(http).UpdateAsync(file.Path, null, default));
        Assert.Equal(1, calls); Assert.Equal(original, File.ReadAllBytes(file.Path)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
    }

    [Fact]
    public async Task Same_host_redirect_is_bounded_and_forbidden_response_does_not_fall_back()
    {
        using var file = new GeoFile(); int calls = 0;
        using var http = new HttpClient(new Handler((request, _) => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = request.RequestUri; return Task.FromResult(response); }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(http).UpdateAsync(file.Path, null, default)); Assert.InRange(calls, 1, 5);
        calls = 0; using var denied = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(denied).UpdateAsync(file.Path, null, default)); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("invalid")] [InlineData("wrong-type")] [InlineData("wrong-record")] [InlineData("truncated-gzip")]
    [InlineData("compressed-limit")] [InlineData("expanded-limit")] [InlineData("length-mismatch")]
    public async Task Invalid_or_unbounded_data_preserves_existing_file_and_cleans_temporary_files(string kind)
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); byte[] original = File.ReadAllBytes(file.Path);
        byte[] plain = kind switch { "invalid" => new byte[100], "wrong-type" => ConnectionMmdbFixture.Create("Other-Country"), "wrong-record" => ConnectionMmdbFixture.Create(record: new() { ["wrong"] = "shape" }), _ => ConnectionMmdbFixture.Create() };
        byte[] compressed = ConnectionMmdbFixture.Gzip(plain); if (kind == "truncated-gzip") compressed = compressed[..^6];
        using var http = new HttpClient(new Handler((_, _) => { var response = Response(compressed); if (kind == "length-mismatch") response.Content.Headers.ContentLength = compressed.Length + 50; return Task.FromResult(response); }));
        long compressedLimit = kind == "compressed-limit" ? compressed.Length - 1 : 1024 * 1024;
        long expandedLimit = kind == "expanded-limit" ? plain.Length - 1 : 1024 * 1024;
        await Assert.ThrowsAnyAsync<Exception>(() => Download(http, compressedLimit, expandedLimit).UpdateAsync(file.Path, null, default));
        Assert.Equal(original, File.ReadAllBytes(file.Path)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
        using var resolver = new ConnectionGeolocation(file.Path); resolver.Reload(); Assert.NotNull(resolver.Lookup("8.8.8.8"));
    }

    [Fact]
    public async Task Stream_without_content_length_still_enforces_compressed_limit()
    {
        using var file = new GeoFile(); byte[] compressed = ConnectionMmdbFixture.Gzip(ConnectionMmdbFixture.Create());
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekingStream(compressed)) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(http, compressed.Length - 1).UpdateAsync(file.Path, null, default)); Assert.False(File.Exists(file.Path)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
    }

    [Fact]
    public async Task Cancellation_and_concurrent_update_leave_old_database_and_release_update_gate()
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Response([]); }));
        var download = Download(http); using var cancel = new CancellationTokenSource();
        var pending = download.UpdateAsync(file.Path, null, cancel.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => download.UpdateAsync(file.Path, null, default));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
        using var resolver = new ConnectionGeolocation(file.Path); resolver.Reload(); Assert.NotNull(resolver.Lookup("8.8.8.8"));
    }

    [Fact]
    public async Task Cancellation_while_reading_body_cleans_partial_file_and_allows_retry()
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); byte[] original = File.ReadAllBytes(file.Path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream(started)) }
            : Response(ConnectionMmdbFixture.Gzip(ConnectionMmdbFixture.Create())))));
        var download = Download(http); using var cancel = new CancellationTokenSource();
        var update = download.UpdateAsync(file.Path, null, cancel.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        Assert.Equal(original, File.ReadAllBytes(file.Path)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
        await download.UpdateAsync(file.Path, null, default); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Vendor_month_filename_uses_gregorian_calendar_under_other_current_culture()
    {
        using var file = new GeoFile(); var old = CultureInfo.CurrentCulture; string? path = null;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            using var http = new HttpClient(new Handler((request, _) => { path = request.RequestUri!.AbsolutePath; return Task.FromResult(Response(ConnectionMmdbFixture.Gzip(ConnectionMmdbFixture.Create()))); }));
            await Download(http).UpdateAsync(file.Path, null, default);
            Assert.Equal("/free/dbip-city-lite-2026-01.mmdb.gz", path);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public async Task Failed_atomic_replacement_keeps_previous_database()
    {
        using var file = new GeoFile(ConnectionMmdbFixture.Create()); byte[] original = File.ReadAllBytes(file.Path);
        using var locked = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(ConnectionMmdbFixture.Gzip(ConnectionMmdbFixture.Create(record: ConnectionMmdbFixture.Record(city: "Replacement")))))));
        await Assert.ThrowsAnyAsync<IOException>(() => Download(http).UpdateAsync(file.Path, null, default));
        Assert.Equal(original, File.ReadAllBytes(file.Path)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file.Path)!));
    }

    private static ConnectionDatabaseDownload Download(HttpClient http, long compressed = 1024 * 1024, long expanded = 1024 * 1024)
        => new(http, () => new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), compressed, expanded);
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class ProgressSink(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class WaitingStream(TaskCompletionSource started) : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
