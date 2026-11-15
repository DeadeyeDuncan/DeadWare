using System.Text.Json;

namespace DeadWare.Core;

/// <summary>A home-city choice from the offline city list.</summary>
public sealed record MapCity(string Id, string Name, string Country, GeoPoint Point);

/// <summary>The map's offline geography. A part that failed to load is empty and named in <see cref="Status"/>.</summary>
public sealed record MapGeography(IReadOnlyList<IReadOnlyList<GeoPoint>> Land, IReadOnlyList<IReadOnlyList<GeoPoint>> Borders,
    IReadOnlyList<IReadOnlyList<GeoPoint>> StateBorders, IReadOnlyList<MapCity> Cities, string Status)
{
    public static MapGeography Empty { get; } = new([], [], [], [], "");

    /// <summary>Reads land, borders, states and cities by name through <paramref name="read"/>; each part fails on its own.</summary>
    public static MapGeography Load(Func<string, string> read)
    {
        IReadOnlyList<IReadOnlyList<GeoPoint>> land = [], borders = [], states = []; IReadOnlyList<MapCity> cities = [];
        var errors = new List<string>();
        static bool Expected(Exception e) => e is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException;
        try { land = ParseLand(read("land")); } catch (Exception e) when (Expected(e)) { errors.Add("Map geography unavailable"); }
        try { borders = ParseBorders(read("borders")); } catch (Exception e) when (Expected(e)) { errors.Add("Country borders unavailable"); }
        try { states = ParseBorders(read("states")); } catch (Exception e) when (Expected(e)) { errors.Add("State borders unavailable"); }
        try { cities = ParseCities(read("cities")); } catch (Exception e) when (Expected(e)) { errors.Add("Home city choices unavailable"); }
        return new(land, borders, states, cities, string.Join(" · ", errors));
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
        { throw new InvalidDataException("Invalid geography.", e); }
    }

    private static GeoPoint Point(double latitude, double longitude)
    {
        var point = new GeoPoint(latitude, longitude);
        if (!ConnectionProjection.IsValid(point) || Math.Abs(longitude) > 180) throw new InvalidDataException("Invalid geographic coordinate.");
        return point;
    }

    private static IReadOnlyList<IReadOnlyList<GeoPoint>> ParsePaths(JsonElement root, int maximum, int minimumPoints, bool closed)
    {
        if (root.GetArrayLength() > maximum) throw new InvalidDataException("Too many paths.");
        var paths = new List<IReadOnlyList<GeoPoint>>(); int count = 0;
        foreach (var path in root.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Array || path.GetArrayLength() < minimumPoints) throw new InvalidDataException("Invalid path.");
            var points = new List<GeoPoint>();
            foreach (var coordinate in path.EnumerateArray())
            {
                if (++count > 32768 || coordinate.ValueKind != JsonValueKind.Array || coordinate.GetArrayLength() != 2) throw new InvalidDataException("Invalid coordinate array.");
                points.Add(Point(coordinate[1].GetDouble(), coordinate[0].GetDouble()));
            }
            if (closed && points[0] != points[^1]) throw new InvalidDataException("Unclosed land ring.");
            paths.Add(points.AsReadOnly());
        }
        return paths.AsReadOnly();
    }

    /// <summary>Closed land rings of [longitude, latitude] pairs.</summary>
    public static IReadOnlyList<IReadOnlyList<GeoPoint>> ParseLand(string json) => Parse(json, root => ParsePaths(root, 2048, 4, true));

    /// <summary>Open boundary paths: never closed, so no segment is invented between countries.</summary>
    public static IReadOnlyList<IReadOnlyList<GeoPoint>> ParseBorders(string json) => Parse(json, root => ParsePaths(root, 4096, 2, false));

    public static IReadOnlyList<MapCity> ParseCities(string json) => Parse(json, root =>
    {
        if (root.GetArrayLength() > 10000) throw new InvalidDataException("Too many cities.");
        var cities = new List<MapCity>(); var ids = new HashSet<string>(StringComparer.Ordinal);
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
        return (IReadOnlyList<MapCity>)cities.AsReadOnly();
    });
}

/// <summary>A position in a drawing, in device-independent pixels.</summary>
public readonly record struct PlotPoint(double X, double Y);

/// <summary>A rectangle in a drawing; its edges count as inside.</summary>
public readonly record struct PlotRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(PlotPoint point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public bool Intersects(PlotRect other) => other.X <= Right && other.Right >= X && other.Y <= Bottom && other.Bottom >= Y;
}

/// <summary>One drawn connection: its node, the great-circle samples from the origin, their screen segments, and its size by traffic.</summary>
public sealed record MapRoute(ConnectionMapPoint Data, MapCoordinate[] Samples, PlotPoint[][] Segments, double Radius, double Width);

/// <summary>
/// The connections map for one size and camera, as screen geometry: where the globe or flat map sits, the coast, land, borders and
/// graticule as polylines, and the routes. It draws nothing itself.
/// </summary>
public sealed class MapFrame
{
    public const double HitRadius = 9, WidgetHitRadius = 12;
    private readonly double xScale, yScale;

    public MapFrame(double width, double height, MapCamera camera, bool mapOnly)
    {
        Width = width; Height = height; Camera = camera; MapOnly = mapOnly;
        Bounds = mapOnly ? new(3, 3, Math.Max(1, width - 6), Math.Max(1, height - 6)) : new(10, 21, Math.Max(1, width - 20), Math.Max(1, height - 55));
        yScale = camera.Flat ? Math.Min(Bounds.Height / 2, Bounds.Width / 4) : Math.Min(Bounds.Width, Bounds.Height) / 2;
        xScale = camera.Flat ? yScale * 2 : yScale;
        if (camera.Flat && mapOnly) { xScale = Bounds.Width / 2; yScale = Bounds.Height / 2; }
    }

    public double Width { get; }
    public double Height { get; }
    public MapCamera Camera { get; }
    public bool MapOnly { get; }
    /// <summary>The area the map is clipped to, inside the legend row and the status line.</summary>
    public PlotRect Bounds { get; }
    public double XScale => xScale;
    /// <summary>Too small to draw or hit-test.</summary>
    public bool TooSmall => Width < 20 || Height < 60;

    public PlotPoint Pixel(MapCoordinate coordinate) =>
        new(Bounds.X + Bounds.Width / 2 + coordinate.X * xScale, Bounds.Y + Bounds.Height / 2 + coordinate.Y * yScale);

    /// <summary>The globe's centre and radius; meaningful only when the camera is not flat.</summary>
    public (PlotPoint Center, double Radius) Globe => (Pixel(new(0, 0, 1, true)), Math.Min(Bounds.Width, Bounds.Height) / 2 * Camera.Zoom);

    /// <summary>The flat map's rectangle; meaningful only when the camera is flat.</summary>
    public PlotRect FlatArea
    {
        get
        {
            var topLeft = Pixel(new(-Camera.Zoom, (Camera.Latitude / 90 - 1) * Camera.Zoom, 1, true));
            var bottomRight = Pixel(new(Camera.Zoom, (Camera.Latitude / 90 + 1) * Camera.Zoom, 1, true));
            return new(Math.Min(topLeft.X, bottomRight.X), Math.Min(topLeft.Y, bottomRight.Y), Math.Abs(bottomRight.X - topLeft.X), Math.Abs(bottomRight.Y - topLeft.Y));
        }
    }

    /// <summary>The flat map repeats every this many pixels across the seam.</summary>
    public double SeamShift => 2 * Camera.Zoom * xScale;

    /// <summary>Open polylines for the visible parts of each path.</summary>
    public IReadOnlyList<PlotPoint[]> Lines(IEnumerable<IReadOnlyList<GeoPoint>> paths)
    {
        var lines = new List<PlotPoint[]>();
        foreach (var path in paths)
            foreach (var segment in ConnectionProjection.ProjectPath(path, Camera))
                if (segment.Count >= 2) lines.Add(segment.Select(Pixel).ToArray());
        return lines;
    }

    /// <summary>
    /// Closed polygons for land rings. On the globe, back-hemisphere points sit on the limb and an edge that crosses the horizon passes
    /// through its true crossing first; on the flat map a ring that crosses the seam is unwrapped, so copies drawn a
    /// <see cref="SeamShift"/> to either side cover it.
    /// </summary>
    public IReadOnlyList<PlotPoint[]> LandPolygons(IEnumerable<IReadOnlyList<GeoPoint>> rings)
    {
        var polygons = new List<PlotPoint[]>();
        foreach (var ring in rings)
        {
            if (ring.Count < 3) continue;
            var projected = ConnectionProjection.ProjectRing(ring, Camera);
            if (!Camera.Flat && projected.All(p => !p.Visible)) continue;
            double shift = 0; var previous = projected[0];
            var points = new List<PlotPoint>(projected.Count + 4) { Pixel(previous) };
            for (int i = 1; i < projected.Count; i++)
            {
                var point = projected[i];
                if (Camera.Flat && Math.Abs(point.X - previous.X) > Camera.Zoom) shift += point.X > previous.X ? -2 * Camera.Zoom : 2 * Camera.Zoom;
                if (!Camera.Flat && point.Visible != previous.Visible) points.Add(Pixel(ConnectionProjection.HorizonCrossing(ring[i - 1], ring[i], Camera)));
                previous = point;
                points.Add(Pixel(point with { X = point.X + shift }));
            }
            polygons.Add([.. points]);
        }
        return polygons;
    }

    /// <summary>Parallels every 30° between ±60° and meridians every 30°.</summary>
    public static IEnumerable<IReadOnlyList<GeoPoint>> Graticules()
    {
        for (int lat = -60; lat <= 60; lat += 30)
        {
            int latitude = lat;
            yield return Enumerable.Range(0, 121).Select(i => new GeoPoint(latitude, -180 + i * 3)).ToArray();
        }
        for (int lon = -180; lon < 180; lon += 30)
        {
            int longitude = lon;
            yield return Enumerable.Range(0, 61).Select(i => new GeoPoint(-90 + i * 3, longitude)).ToArray();
        }
    }

    /// <summary>One route per point, sized by the logarithm of its bytes against the busiest point. Without an origin a route has no arc.</summary>
    public IReadOnlyList<MapRoute> Routes(IReadOnlyList<ConnectionMapPoint> points, GeoPoint? origin)
    {
        double maximum = points.Select(p => (double)p.ReceivedBytes + p.SentBytes).DefaultIfEmpty(0).Max();
        var routes = new List<MapRoute>(points.Count);
        foreach (var point in points)
        {
            double relative = maximum > 0 ? Math.Log(1 + (double)point.ReceivedBytes + point.SentBytes) / Math.Log(1 + maximum) : 0;
            IReadOnlyList<GeoPoint> samples = origin is null ? [] : ConnectionProjection.GreatCircle(origin, point.Point);
            routes.Add(new(point, samples.Select(p => ConnectionProjection.Project(p, Camera)).ToArray(),
                ConnectionProjection.ProjectPath(samples, Camera).Select(s => s.Select(Pixel).ToArray()).ToArray(),
                3 + relative * 3, .6 + Math.Round(relative * 4) * .15));
        }
        return routes;
    }

    /// <summary>A node's screen position, or null when it is on the far side or outside the bounds.</summary>
    public PlotPoint? Position(GeoPoint point)
    {
        if (TooSmall) return null;
        var projected = ConnectionProjection.Project(point, Camera);
        if (!projected.Visible) return null;
        var pixel = Pixel(projected);
        return Bounds.Contains(pixel) ? pixel : null;
    }

    /// <summary>Screen position at a fraction of the route, or null across the rear hemisphere.</summary>
    public PlotPoint? Sample(MapRoute route, double fraction)
    {
        if (route.Samples.Length < 2) return null;
        double position = Math.Clamp(fraction, 0, 1) * (route.Samples.Length - 1); int index = Math.Min(route.Samples.Length - 2, (int)position);
        var a = route.Samples[index]; var b = route.Samples[index + 1];
        if (!a.Visible || !b.Visible) return null;
        double progress = position - index, x = b.X, zoom = Camera.Zoom;
        if (Camera.Flat && Math.Abs(a.X - x) > zoom) x += a.X > 0 ? 2 * zoom : -2 * zoom;
        x = a.X + (x - a.X) * progress;
        if (Camera.Flat) { if (x > zoom) x -= 2 * zoom; else if (x < -zoom) x += 2 * zoom; }
        return Pixel(new(x, a.Y + (b.Y - a.Y) * progress, 1, true));
    }

    /// <summary>The segments shifted <paramref name="offset"/> px along their local normal, for two lanes on one arc.</summary>
    public static PlotPoint[][] Offset(PlotPoint[][] segments, double offset)
    {
        if (offset == 0) return segments;
        var result = new PlotPoint[segments.Length][];
        for (int s = 0; s < segments.Length; s++)
        {
            var segment = segments[s]; var shifted = new PlotPoint[segment.Length];
            for (int i = 0; i < segment.Length; i++)
            {
                var next = segment[Math.Min(i + 1, segment.Length - 1)]; var previous = segment[Math.Max(0, i - 1)];
                double tx = next.X - previous.X, ty = next.Y - previous.Y, length = Math.Sqrt(tx * tx + ty * ty);
                shifted[i] = length > 0 ? new(segment[i].X - ty / length * offset, segment[i].Y + tx / length * offset) : segment[i];
            }
            result[s] = shifted;
        }
        return result;
    }

    /// <summary>The nearest node centre within the hit radius, or null.</summary>
    public (PlotPoint Center, string Key)? Hit(IReadOnlyList<(PlotPoint Center, string Key)> nodes, PlotPoint position)
    {
        double limit = MapOnly ? WidgetHitRadius : HitRadius, best = limit * limit; (PlotPoint Center, string Key)? hit = null;
        foreach (var candidate in nodes)
        {
            double dx = candidate.Center.X - position.X, dy = candidate.Center.Y - position.Y, distance = dx * dx + dy * dy;
            if (distance <= best) { best = distance; hit = candidate; }
        }
        return hit;
    }

    /// <summary>The camera after dragging by (dx, dy) pixels from where the drag began.</summary>
    public MapCamera Dragged(MapCamera start, double dx, double dy) => ConnectionProjection.Normalize(start with
    {
        Longitude = start.Longitude - dx / Math.Max(1, Bounds.Width) * 360 / Camera.Zoom,
        Latitude = start.Latitude + dy / Math.Max(1, Bounds.Height) * 180 / Camera.Zoom,
    });

    /// <summary>A stable per-route phase offset from the key, so particles on different arcs do not move in step.</summary>
    public static double Seed(string key)
    {
        uint seed = 2166136261; foreach (char c in key) seed = unchecked((seed ^ c) * 16777619);
        return seed % 1000 / 100d;
    }

    /// <summary>The status line under the map.</summary>
    public static string StatusSummary(int locations, int overflow, bool hasOrigin, bool available, string status, string geographyStatus) =>
        $"{locations} locations{(overflow > 0 ? $" · +{overflow} omitted" : "")}" + (hasOrigin ? " · Approximate locations" : " · Unknown origin") +
        (!available ? " · Capture unavailable" : "") + (status.Length > 0 ? " · " + status : "") + (geographyStatus.Length > 0 ? " · " + geographyStatus : "");
}
