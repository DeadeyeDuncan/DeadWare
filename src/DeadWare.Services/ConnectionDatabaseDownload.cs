using System.Net.Http;
using System.Net;
using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Globalization;
using MaxMind.Db;

namespace DeadWare.App;

public sealed class ConnectionDatabaseDownload
{
    internal const long CompressedLimit = 256L * 1024 * 1024;
    internal const long ExpandedLimit = 512L * 1024 * 1024;
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler
    { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient http;
    private readonly Func<DateTimeOffset> now;
    private readonly long compressedLimit, expandedLimit;
    private int updating;

    /// <summary>Injected clients must disable automatic redirects and automatic decompression.</summary>
    public ConnectionDatabaseDownload(HttpClient? http = null) : this(http ?? DefaultHttp, () => DateTimeOffset.UtcNow, CompressedLimit, ExpandedLimit) { }
    internal ConnectionDatabaseDownload(HttpClient http, Func<DateTimeOffset> now, long compressedLimit, long expandedLimit)
    { this.http = http; this.now = now; this.compressedLimit = compressedLimit; this.expandedLimit = expandedLimit; }

    public async Task UpdateAsync(string databasePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref updating, 1, 0) != 0) throw new InvalidOperationException("A location database update is already running.");
        string? compressed = null, expanded = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.GetFullPath(databasePath);
            string directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            string unique = Path.Combine(directory, ".location-" + Guid.NewGuid().ToString("N"));
            compressed = unique + ".gz"; expanded = unique + ".mmdb";
            progress?.Report(0);
            DateTimeOffset month = now().ToUniversalTime();
            using var response = await GetMonthAsync(month, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                using var fallback = await GetMonthAsync(month.AddMonths(-1), cancellationToken).ConfigureAwait(false);
                fallback.EnsureSuccessStatusCode();
                await SaveCompressedAsync(fallback, compressed, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                response.EnsureSuccessStatusCode();
                await SaveCompressedAsync(response, compressed, progress, cancellationToken).ConfigureAwait(false);
            }
            await ExpandAsync(compressed, expanded, progress, cancellationToken).ConfigureAwait(false);
            // Parsing and record validation run off the UI thread even when an injected HTTP client completes synchronously.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var database = new Reader(expanded, FileAccessMode.MemoryMapped);
                ConnectionGeolocation.Validate(database, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(expanded, destination, null);
            else File.Move(expanded, destination);
            progress?.Report(1);
        }
        finally
        {
            DeleteTemporary(compressed); DeleteTemporary(expanded);
            Volatile.Write(ref updating, 0);
        }
    }

    private async Task<HttpResponseMessage> GetMonthAsync(DateTimeOffset month, CancellationToken token)
    {
        var uri = new Uri("https://download.db-ip.com/free/dbip-city-lite-" + month.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".mmdb.gz");
        for (int redirects = 0; ; redirects++)
        {
            RequireOfficialHost(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                // Also reject an unexpected final URI from a misconfigured injected client.
                if (response.RequestMessage?.RequestUri is { } finalUri) RequireOfficialHost(finalUri);
                int status = (int)response.StatusCode;
                if (status is not (301 or 302 or 303 or 307 or 308)) return response;
                if (redirects >= 3 || response.Headers.Location is null) throw new InvalidDataException("Invalid or excessive database download redirects.");
                uri = new Uri(uri, response.Headers.Location);
                RequireOfficialHost(uri);
            }
            catch { response.Dispose(); throw; }
            response.Dispose();
        }
    }

    private static void RequireOfficialHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("download.db-ip.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Database download must remain on the official HTTPS download.db-ip.com host.");
    }

    private async Task SaveCompressedAsync(HttpResponseMessage response, string path, IProgress<double>? progress, CancellationToken token)
    {
        long? length = response.Content.Headers.ContentLength;
        if (length > compressedLimit) throw new InvalidDataException("Compressed database exceeds 256 MiB limit.");
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new InvalidDataException("Unexpected HTTP content encoding for database archive.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (total > compressedLimit - read) throw new InvalidDataException("Compressed database exceeds 256 MiB limit.");
            total += read;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            if (length > 0) progress?.Report(Math.Min(0.75, 0.75 * total / length.Value));
        }
        if (length.HasValue && total != length.Value) throw new InvalidDataException("Database download is truncated.");
        if (total < 18) throw new InvalidDataException("Database gzip archive is truncated.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task ExpandAsync(string compressed, string expanded, IProgress<double>? progress, CancellationToken token)
    {
        await using var input = new FileStream(compressed, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] trailer = new byte[8]; input.Seek(-8, SeekOrigin.End); await input.ReadExactlyAsync(trailer, token).ConfigureAwait(false); input.Position = 0;
        using var gzip = new GZipStream(input, CompressionMode.Decompress, true);
        await using var output = new FileStream(expanded, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[65536];
        long total = 0; uint crc = uint.MaxValue;
        int read;
        while ((read = await gzip.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (total > expandedLimit - read) throw new InvalidDataException("Expanded database exceeds 512 MiB limit.");
            total += read;
            crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        // GZipStream can accept a missing trailer. Check its CRC and ISIZE explicitly.
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != ~crc || BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(4)) != total)
            throw new InvalidDataException("Database gzip trailer is truncated or corrupt.");
        await output.FlushAsync(token).ConfigureAwait(false);
        progress?.Report(0.9);
    }

    private static readonly uint[] CrcTable = CreateCrcTable();
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320U);
            table[i] = value;
        }
        return table;
    }
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    { foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return crc; }
    private static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
