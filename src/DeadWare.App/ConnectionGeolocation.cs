using System.Net;
using System.IO;
using System.Runtime.CompilerServices;
using DeadWare.Core;
using MaxMind.Db;

[assembly: InternalsVisibleTo("DeadWare.App.Tests")]

namespace DeadWare.App;

public interface IConnectionGeolocation
{
    ConnectionLocation? Lookup(string address);
}

internal interface IConnectionGeoReader : IDisposable
{
    DateTimeOffset DatabaseDate { get; }
    Dictionary<string, object>? Find(IPAddress address);
}

public sealed class ConnectionGeolocation : IConnectionGeolocation, IDisposable
{
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<string, IConnectionGeoReader> open;
    private readonly Dictionary<string, ConnectionLocation?> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private IConnectionGeoReader? reader;
    private bool disposed;
    private string status = "Local location database is not loaded. Download explicitly to enable approximate locations.";

    // Deliberately no disk IO here. The session service calls Reload on its worker.
    public ConnectionGeolocation(string databasePath) : this(databasePath, path => new MmdbReader(path)) { }
    internal ConnectionGeolocation(string path, Func<string, IConnectionGeoReader> open)
    { this.path = path; this.open = open; }
    public DateTimeOffset? DatabaseDate { get { lock (gate) return reader?.DatabaseDate; } }
    public string Status
    {
        get
        {
            lock (gate)
            {
                if (reader is null) return status;
                return $"{status} Built {reader.DatabaseDate:yyyy-MM-dd} UTC." +
                    (DateTimeOffset.UtcNow - reader.DatabaseDate > TimeSpan.FromDays(45) ? " Database is outdated (over 45 days)." : "");
            }
        }
    }

    public void Reload()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var replacement = open(path);
                var previous = reader;
                reader = replacement;
                cache.Clear(); order.Clear();
                previous?.Dispose();
                status = "Approximate locations from DB-IP City Lite (CC BY 4.0).";
            }
            catch (Exception error) when (IsDatabaseFailure(error))
            {
                status = reader is null
                    ? (error is FileNotFoundException or DirectoryNotFoundException ? "Local location database is missing." : "Local location database could not be read.")
                    : "Database reload failed; previous valid database retained.";
            }
        }
    }

    public ConnectionLocation? Lookup(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 64 || !IPAddress.TryParse(address, out var ip)) return null;
        ip = NetworkAddress.Normalize(ip);
        if (NetworkAddress.Classify(ip) != NetworkAddressKind.Public) return null;
        string key = ip.ToString();
        lock (gate)
        {
            if (disposed || reader is null) return null;
            if (cache.TryGetValue(key, out var result)) return result;
            try { result = Parse(reader.Find(ip)); }
            catch (Exception error) when (IsDatabaseFailure(error)) { result = null; }
            if (cache.Count == 8192) cache.Remove(order.Dequeue());
            cache.Add(key, result); order.Enqueue(key);
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; reader?.Dispose(); reader = null; cache.Clear(); order.Clear(); status = "Local location database is closed.";
        }
    }

    internal static bool IsDatabaseFailure(Exception error) => error is IOException or InvalidDatabaseException or
        UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException or InvalidCastException;

    internal static ConnectionLocation? Parse(IDictionary<string, object>? record)
    {
        if (record is null) return null;
        var country = Map(record, "country");
        if (country is null || !country.TryGetValue("iso_code", out var rawCode) || rawCode is not string code ||
            code.Length != 2 || !code.All(char.IsAsciiLetter)) return null;
        string name = Name(country) ?? code;
        GeoPoint? point = null;
        var location = Map(record, "location");
        if (location is not null && location.TryGetValue("latitude", out var lat) && lat is double latitude &&
            location.TryGetValue("longitude", out var lon) && lon is double longitude &&
            double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
            point = new(latitude, longitude);
        IDictionary<string, object>? region = null;
        if (record.TryGetValue("subdivisions", out var subdivisions) && subdivisions is IEnumerable<object> list)
            region = list.FirstOrDefault() as IDictionary<string, object>;
        return new(code.ToUpperInvariant(), name, Name(region), Name(Map(record, "city")), point, "DB-IP City Lite");
    }

    private static IDictionary<string, object>? Map(IDictionary<string, object>? record, string key)
        => record is not null && record.TryGetValue(key, out var value) ? value as IDictionary<string, object> : null;
    private static string? Name(IDictionary<string, object>? record) => Text(Map(record, "names"), "en", 160);
    private static string? Text(IDictionary<string, object>? map, string key, int maximum)
    {
        if (map is null || !map.TryGetValue(key, out var raw) || raw is not string value || value.Length == 0) return null;
        // Bound before inspecting untrusted text. Strip control characters from display strings.
        string bounded = value[..Math.Min(maximum, value.Length)];
        return new string(bounded.Where(c => !char.IsControl(c)).ToArray());
    }

    private sealed class MmdbReader : IConnectionGeoReader
    {
        private readonly Reader database;
        public MmdbReader(string path)
        {
            if (new FileInfo(path).Length > ConnectionDatabaseDownload.ExpandedLimit) throw new InvalidDataException("Location database exceeds size limit.");
            // Pinned Reader 5.2.0 opens mapped files with FileShare.Delete | FileShare.Read;
            // atomic replacement keeps existing mappings alive without copying the entire database into RAM.
            database = new Reader(path, FileAccessMode.MemoryMapped);
            try { Validate(database, CancellationToken.None); }
            catch { database.Dispose(); throw; }
        }
        public DateTimeOffset DatabaseDate => new(database.Metadata.BuildDate);
        public Dictionary<string, object>? Find(IPAddress address) => database.Find<Dictionary<string, object>>(address);
        public void Dispose() => database.Dispose();
    }

    internal static void Validate(Reader database, CancellationToken cancellationToken)
    {
        var metadata = database.Metadata;
        if (metadata.DatabaseType != "DBIP-City-Lite" || metadata.BinaryFormatMajorVersion != 2 || metadata.IPVersion is not (4 or 6) ||
            metadata.BuildDate > DateTime.UtcNow.AddDays(2) || metadata.BuildDate < new DateTime(2000, 1, 1))
            throw new InvalidDataException("Expected a DB-IP City Lite MMDB database.");
        // Probe real tree records rather than trusting the metadata label alone. Decoding is bounded.
        int checkedRecords = 0;
        bool usable = false;
        foreach (var node in database.FindAll<Dictionary<string, object>>(cacheSize: 128))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Parse(node.Data) is not null) usable = true;
            if (++checkedRecords >= 128) break;
        }
        if (!usable) throw new InvalidDataException("Location database has no valid city/country records in its validation sample.");
    }
}
