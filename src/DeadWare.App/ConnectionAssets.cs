using System.IO;
using System.Text.Json;
using System.Windows;
using DeadWare.Core;
namespace DeadWare.App;

public sealed record HomeCity(string Id, string Name, string Country, GeoPoint Point);
public static class ConnectionAssets
{
    private sealed record Assets(IReadOnlyList<IReadOnlyList<GeoPoint>> Land, IReadOnlyList<HomeCity> Cities, string Status);
    private static readonly Lazy<Assets> Data = new(() => Load(Read));
    public static IReadOnlyList<IReadOnlyList<GeoPoint>> Land => Data.Value.Land;
    public static IReadOnlyList<HomeCity> Cities => Data.Value.Cities;
    public static string Status => Data.Value.Status;

    private static Assets Load(Func<string, string> read)
    {
        IReadOnlyList<IReadOnlyList<GeoPoint>> land = []; IReadOnlyList<HomeCity> cities = [];
        var errors = new List<string>();
        try { land = ParseLand(read("land")); } catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or System.Resources.MissingManifestResourceException) { errors.Add("Map geography unavailable"); }
        try { cities = ParseCities(read("cities")); } catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or System.Resources.MissingManifestResourceException) { errors.Add("Home city choices unavailable"); }
        return new(land, cities, string.Join(" · ", errors));
    }

    private static string Read(string name)
    {
        using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/DeadWare;component/Assets/maps/{name}.json"))?.Stream
            ?? throw new InvalidDataException("Missing embedded geography.");
        if (stream.Length > 2_000_000) throw new InvalidDataException("Oversized geography.");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }

    private static T Parse<T>(string json, Func<JsonElement, T> convert)
    {
        if (json.Length > 2_000_000) throw new InvalidDataException("Oversized geography.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected geography array.");
            return convert(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid embedded geography.", e); }
    }

    private static GeoPoint Point(double latitude, double longitude)
    {
        var point = new GeoPoint(latitude, longitude);
        if (!ConnectionProjection.IsValid(point) || Math.Abs(longitude) > 180) throw new InvalidDataException("Invalid geographic coordinate.");
        return point;
    }

    public static IReadOnlyList<IReadOnlyList<GeoPoint>> ParseLand(string json) => Parse(json, root =>
    {
        if (root.GetArrayLength() > 2048) throw new InvalidDataException("Too many land rings.");
        var rings = new List<IReadOnlyList<GeoPoint>>(); int count = 0;
        foreach (var ring in root.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4) throw new InvalidDataException("Invalid land ring.");
            var points = new List<GeoPoint>();
            foreach (var coordinate in ring.EnumerateArray())
            {
                if (++count > 32768 || coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() != 2) throw new InvalidDataException("Invalid land coordinate array.");
                points.Add(Point(coordinate[1].GetDouble(), coordinate[0].GetDouble()));
            }
            if (points[0] != points[^1]) throw new InvalidDataException("Unclosed land ring.");
            rings.Add(points.AsReadOnly());
        }
        return (IReadOnlyList<IReadOnlyList<GeoPoint>>)rings.AsReadOnly();
    });

    public static IReadOnlyList<HomeCity> ParseCities(string json) => Parse(json, root =>
    {
        if (root.GetArrayLength() > 10000) throw new InvalidDataException("Too many cities.");
        var cities = new List<HomeCity>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var city in root.EnumerateArray())
        {
            string Text(string key)
            {
                string? value = city.GetProperty(key).GetString();
                return !string.IsNullOrWhiteSpace(value) && value.Length <= 160 ? value : throw new InvalidDataException("Invalid city label.");
            }
            string id = Text("id");
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate city identifier.");
            cities.Add(new(id, Text("name"), Text("country"), Point(city.GetProperty("latitude").GetDouble(), city.GetProperty("longitude").GetDouble())));
        }
        return (IReadOnlyList<HomeCity>)cities.AsReadOnly();
    });
}
