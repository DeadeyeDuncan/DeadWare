namespace DeadWare.Core;

public sealed record MapCamera(double Longitude, double Latitude, double Zoom, bool Flat);
public readonly record struct MapCoordinate(double X, double Y, double Depth, bool Visible);
public sealed record ConnectionMapPoint(string Key, GeoPoint Point, long ReceivedBytes, long SentBytes, bool Selected);
public sealed record ConnectionMapGroups(IReadOnlyList<ConnectionMapPoint> Points, int Overflow);

public static class ConnectionProjection
{
    private const double Radians = Math.PI / 180;
    public static double Wrap(double longitude) => double.IsFinite(longitude) ? ((longitude + 180) % 360 + 360) % 360 - 180 : 0;
    public static bool IsValid(GeoPoint point) => double.IsFinite(point.Latitude) && Math.Abs(point.Latitude) <= 90 && double.IsFinite(point.Longitude);
    public static MapCamera Normalize(MapCamera camera) => new(Wrap(camera.Longitude),
        double.IsFinite(camera.Latitude) ? Math.Clamp(camera.Latitude, -90, 90) : 0,
        double.IsFinite(camera.Zoom) ? Math.Clamp(camera.Zoom, 1, 8) : 1, camera.Flat);

    public static MapCoordinate Project(GeoPoint point, MapCamera camera)
    {
        if (!IsValid(point)) return default;
        camera = Normalize(camera);
        double longitude = Wrap(point.Longitude - camera.Longitude);
        if (camera.Flat) return new(longitude / 180 * camera.Zoom, (camera.Latitude - point.Latitude) / 90 * camera.Zoom, 1, true);
        double lat = point.Latitude * Radians, lon = longitude * Radians, center = camera.Latitude * Radians;
        double depth = Math.Sin(center) * Math.Sin(lat) + Math.Cos(center) * Math.Cos(lat) * Math.Cos(lon);
        return new(Math.Cos(lat) * Math.Sin(lon) * camera.Zoom,
            (Math.Sin(center) * Math.Cos(lat) * Math.Cos(lon) - Math.Cos(center) * Math.Sin(lat)) * camera.Zoom,
            depth, depth >= -1e-10);
    }

    public static IReadOnlyList<GeoPoint> GreatCircle(GeoPoint from, GeoPoint to, int steps = 48)
    {
        if (!IsValid(from) || !IsValid(to)) return [];
        steps = Math.Clamp(steps, 1, 128);
        var result = new GeoPoint[steps + 1];
        for (int i = 0; i <= steps; i++) result[i] = Interpolate(from, to, (double)i / steps);
        result[0] = from; result[^1] = to;
        return Array.AsReadOnly(result);
    }

    private static GeoPoint Interpolate(GeoPoint from, GeoPoint to, double fraction)
    {
        static (double X, double Y, double Z) Vector(GeoPoint p)
        {
            double lat = p.Latitude * Radians, lon = p.Longitude * Radians;
            return (Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        }
        var a = Vector(from); var b = Vector(to);
        double dot = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1, 1), angle = Math.Acos(dot);
        if (angle < 1e-10) return from;
        double x, y, z;
        if (Math.PI - angle < 1e-7)
        {
            // Antipodes have no unique shortest path. Pick a deterministic perpendicular.
            var axis = Math.Abs(a.Z) < .9 ? (X: -a.Y, Y: a.X, Z: 0d) : (X: 0d, Y: -a.Z, Z: a.Y);
            double length = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
            double c = Math.Cos(Math.PI * fraction), s = Math.Sin(Math.PI * fraction) / length;
            x = a.X * c + axis.X * s; y = a.Y * c + axis.Y * s; z = a.Z * c + axis.Z * s;
        }
        else
        {
            double s = Math.Sin(angle), left = Math.Sin((1 - fraction) * angle) / s, right = Math.Sin(fraction * angle) / s;
            x = a.X * left + b.X * right; y = a.Y * left + b.Y * right; z = a.Z * left + b.Z * right;
        }
        return new(Math.Atan2(z, Math.Sqrt(x * x + y * y)) / Radians, Wrap(Math.Atan2(y, x) / Radians));
    }

    /// <summary>Open line segments only: clip the back hemisphere, and split at the flat camera's seam.</summary>
    public static IReadOnlyList<IReadOnlyList<MapCoordinate>> ProjectPath(IReadOnlyList<GeoPoint> path, MapCamera camera)
    {
        camera = Normalize(camera);
        var result = new List<IReadOnlyList<MapCoordinate>>();
        List<MapCoordinate> current = [];
        void Flush() { if (current.Count > 1) result.Add(current.AsReadOnly()); current = []; }
        for (int i = 1; i < path.Count; i++)
        {
            var a = Project(path[i - 1], camera); var b = Project(path[i], camera);
            if (!IsValid(path[i - 1]) || !IsValid(path[i])) { Flush(); continue; }
            if (camera.Flat)
            {
                if (current.Count == 0) current.Add(a);
                if (Math.Abs(a.X - b.X) > camera.Zoom)
                {
                    double edge = a.X > 0 ? camera.Zoom : -camera.Zoom;
                    double unwrapped = b.X + (a.X > 0 ? 2 : -2) * camera.Zoom;
                    double y = a.Y + (b.Y - a.Y) * (edge - a.X) / (unwrapped - a.X);
                    current.Add(new(edge, y, 1, true)); Flush(); current.Add(new(-edge, y, 1, true));
                }
                current.Add(b);
            }
            else if (a.Visible && b.Visible)
            {
                if (current.Count == 0) current.Add(a);
                current.Add(b);
            }
            else if (a.Visible != b.Visible)
            {
                double low = 0, high = 1;
                for (int iteration = 0; iteration < 40; iteration++)
                {
                    double mid = (low + high) / 2;
                    if (Project(Interpolate(path[i - 1], path[i], mid), camera).Visible == a.Visible) low = mid; else high = mid;
                }
                var horizon = Project(Interpolate(path[i - 1], path[i], (low + high) / 2), camera) with { Visible = true, Depth = 0 };
                if (a.Visible) { if (current.Count == 0) current.Add(a); current.Add(horizon); Flush(); }
                else { current.Add(horizon); current.Add(b); }
            }
            else Flush();
        }
        Flush(); return result.AsReadOnly();
    }

    public static ConnectionMapGroups Group(IEnumerable<ConnectionMapPoint> points)
    {
        static long Add(long a, long b) => a > long.MaxValue - Math.Max(0, b) ? long.MaxValue : a + Math.Max(0, b);
        var groups = points.Where(p => IsValid(p.Point)).GroupBy(p => (Math.Round(p.Point.Latitude, 3), Math.Round(Wrap(p.Point.Longitude), 3)))
            .Select(group =>
            {
                var first = group.OrderBy(p => p.Key, StringComparer.Ordinal).First();
                return first with { ReceivedBytes = group.Aggregate(0L, (sum, p) => Add(sum, p.ReceivedBytes)),
                    SentBytes = group.Aggregate(0L, (sum, p) => Add(sum, p.SentBytes)), Selected = group.Any(p => p.Selected) };
            }).OrderByDescending(p => p.Selected).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        return new(Array.AsReadOnly(groups.Take(256).ToArray()), Math.Max(0, groups.Length - 256));
    }
}
