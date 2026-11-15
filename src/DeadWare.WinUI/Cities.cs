using System.Text.Json;
using DeadWare.Core;

namespace DeadWare.WinUI;

/// <summary>
/// The home-city list (id to position) from maps/cities.json beside the executable. Only what the session needs:
/// the Connections page will load the full map assets when it is ported.
/// </summary>
internal static class Cities
{
    public static IReadOnlyDictionary<string, GeoPoint> Load()
    {
        var cities = new Dictionary<string, GeoPoint>(StringComparer.Ordinal);
        string path = Path.Combine(AppContext.BaseDirectory, "maps", "cities.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 12 });
            foreach (var city in document.RootElement.EnumerateArray())
            {
                if (city.GetProperty("id").GetString() is not { Length: > 0 } id) continue;
                double latitude = city.GetProperty("latitude").GetDouble(), longitude = city.GetProperty("longitude").GetDouble();
                if (double.IsFinite(latitude) && double.IsFinite(longitude) && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180)
                    cities[id] = new GeoPoint(latitude, longitude);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            App.Note($"City list unavailable: {error.Message}");
            cities.Clear();
        }
        return cities;
    }
}
