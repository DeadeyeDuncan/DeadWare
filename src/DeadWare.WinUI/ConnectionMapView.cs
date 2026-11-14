using System.Diagnostics;
using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>The offline map geography, read once from the maps folder beside the executable.</summary>
internal static class MapAssets
{
    private static readonly Lazy<MapGeography> Data = new(() =>
    {
        var geography = MapGeography.Load(name => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", name + ".json")));
        if (geography.Status.Length > 0) App.Note("Map assets: " + geography.Status);
        return geography;
    });

    public static MapGeography Geography => Data.Value;
}

/// <summary>
/// The connections map drawn with Win2D from <see cref="MapFrame"/>: a globe or flat map with one node per grouped location and an
/// arc from the origin to each. Drag to move, wheel or +/− to zoom, Home to reset, click a node to select it. Arcs animate only
/// while the map is loaded, its window is visible, animation is enabled and some traffic is moving.
/// </summary>
internal sealed class ConnectionMapView : UserControl
{
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat SmallFormat = new() { FontSize = 10 };
    private static readonly CanvasTextFormat StatusFormat = new() { FontSize = 11, WordWrapping = CanvasWordWrapping.Wrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };

    private sealed record Activity(ConnectionMotion Download, ConnectionMotion Upload);
    private sealed class DrawnRoute(MapRoute route, Activity activity, double seed)
    {
        public MapRoute Route { get; } = route;
        public Activity Activity { get; } = activity;
        public double Seed { get; } = seed;
        public CanvasGeometry? Arc, DownloadLane, UploadLane;
        public void Dispose() { Arc?.Dispose(); DownloadLane?.Dispose(); UploadLane?.Dispose(); Arc = DownloadLane = UploadLane = null; }
    }

    private readonly MonitorSession session;
    private readonly CanvasControl canvas = new();
    private readonly Grid layers;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private MapCamera camera = new(-20, 20, 1, false);
    private IReadOnlyList<ConnectionMapPoint> points = [], input = [];
    private HashSet<(double Latitude, double Longitude)> highlighted = [];
    private bool pointSelected;
    private string? selectedPoint;
    private GeoPoint? origin;
    private string status = "Waiting for connections";
    private bool available, enabled = true, loaded, subscribed, mapOnly;
    private Point? dragStart, hoverPosition;
    private MapCamera? dragCamera;
    private bool dragged;
    private double lastFrame, phase, motionPhase;
    private readonly Dictionary<(string Key, GeoPoint Point), Activity> activity = [];
    private readonly List<DrawnRoute> routes = [];
    private DrawnRoute[] busiest = [];
    private readonly List<(PlotPoint Center, string Key)> hitPoints = [];
    private bool routesDirty = true;
    // Geometry for one size and camera; rebuilt when either changes or the device is recreated.
    private MapFrame? frame;
    private CanvasGeometry? coast, land, borders, stateBorders, grid;

    public ConnectionMapView(MonitorSession session)
    {
        this.session = session;
        IsTabStop = true; UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Connections map");
        AutomationProperties.SetHelpText(this, "Drag to move, wheel or plus and minus to zoom, Home to reset, select a location.");
        canvas.Draw += Draw;
        canvas.CreateResources += (_, _) => DropGeometry();
        layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        layers.Children.Add(canvas);
        Content = layers;
        layers.PointerPressed += OnPointerPressed;
        layers.PointerMoved += OnPointerMoved;
        layers.PointerReleased += OnPointerReleased;
        layers.PointerExited += (_, _) => { if (dragStart is null) SetHover(null); };
        layers.PointerCaptureLost += (_, _) => { dragStart = null; dragCamera = null; SetHover(hoverPosition); };
        layers.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(layers).Properties.MouseWheelDelta;
            if (delta != 0) ZoomBy(delta > 0 ? 1.2 : 1 / 1.2);
            e.Handled = true;
        };
        KeyDown += OnKeyDown;
        SizeChanged += (_, _) => { DropGeometry(); canvas.Invalidate(); ViewChanged?.Invoke(); };
        ActualThemeChanged += (_, _) => canvas.Invalidate();
        this.WhileLoaded(() =>
        {
            loaded = true;
            if (XamlRoot is { } root) root.Changed += OnRootChanged;
            UpdateAnimation();
        },
        () =>
        {
            loaded = false;
            if (XamlRoot is { } root) root.Changed -= OnRootChanged;
            UpdateAnimation();
        });
    }

    public bool Flat { get => camera.Flat; set { if (camera.Flat != value) ChangeCamera(camera with { Flat = value }); } }
    public bool MapOnly { get => mapOnly; set { if (mapOnly != value) { mapOnly = value; DropGeometry(); canvas.Invalidate(); } } }
    public bool AnimationEnabled { get => enabled; set { enabled = value; UpdateAnimation(); canvas.Invalidate(); } }
    public MapCamera Camera => camera;
    public IReadOnlyList<ConnectionMapPoint> DisplayedPoints => points;
    /// <summary>Displayed nodes whose location key is highlighted.</summary>
    public IReadOnlyList<string> HighlightedKeys { get; private set; } = [];
    public int OverflowCount { get; private set; }
    /// <summary>A region in map coordinates (the page's overlay toolbar) that hover chips keep clear of.</summary>
    public Rect? ReservedArea { get; set; }
    /// <summary>Key of the node under the pointer, or null.</summary>
    public string? HoveredKey { get; private set; }
    public event Action? ViewChanged;
    public event Action<string>? PointSelected;
    public event Action<string?>? PointHovered;

    private MapFrame Scene => frame ??= new MapFrame(ActualWidth, ActualHeight, camera, mapOnly);
    private string StatusSummary => MapFrame.StatusSummary(points.Count, OverflowCount, origin is not null, available, status, MapAssets.Geography.Status);
    private bool IsHighlighted(ConnectionMapPoint point) => highlighted.Count > 0 && highlighted.Contains(ConnectionProjection.LocationKey(point.Point));
    private static IReadOnlyList<ConnectionMapPoint> Selecting(IReadOnlyList<ConnectionMapPoint> points, string? key) =>
        Array.AsReadOnly(points.Select(p => p with { Selected = p.Key == key }).ToArray());

    public void SelectPoint(string? key)
    {
        pointSelected = true; selectedPoint = key;
        points = Selecting(points, key);
        routesDirty = true; canvas.Invalidate();
    }

    /// <summary>Highlights the nodes at these locations (null or empty clears). Regroups only when the location key set changes.</summary>
    public void Highlight(IReadOnlyCollection<GeoPoint>? locations)
    {
        var next = (locations ?? []).Where(ConnectionProjection.IsValid).Select(ConnectionProjection.LocationKey).ToHashSet();
        if (next.SetEquals(highlighted)) return;
        highlighted = next;
        StepMotion(); Regroup();
    }

    /// <summary>A displayed node's position in this control, or null when it is not in view.</summary>
    public Point? PointPosition(string key)
    {
        var point = points.FirstOrDefault(p => p.Key == key);
        if (point is null || ActualWidth < 20 || ActualHeight < 60) return null;
        return Scene.Position(point.Point) is { } pixel ? new Point(pixel.X, pixel.Y) : null;
    }

    public void SetData(IReadOnlyList<ConnectionMapPoint> points, GeoPoint? origin, bool available, string status)
    {
        StepMotion();
        input = points; pointSelected = false; selectedPoint = null;
        var nextOrigin = origin is not null && ConnectionProjection.IsValid(origin) ? origin : null;
        if (this.origin != nextOrigin) activity.Clear();
        this.origin = nextOrigin;
        this.available = available; this.status = status.Length > 240 ? status[..240] : status;
        Regroup();
    }

    /// <summary>Groups the stored input under the current highlight, re-applies the stored selection and redraws.</summary>
    private void Regroup()
    {
        var grouped = ConnectionProjection.Group(input, highlighted.Count > 0 ? highlighted : null);
        points = pointSelected ? Selecting(grouped.Points, selectedPoint) : grouped.Points; OverflowCount = grouped.Overflow;
        HighlightedKeys = Array.AsReadOnly(points.Where(IsHighlighted).Select(p => p.Key).ToArray());
        var keys = points.Select(p => (p.Key, p.Point)).ToHashSet();
        foreach (var key in activity.Keys.Where(k => !keys.Contains(k)).ToArray()) activity.Remove(key);
        foreach (var point in points)
        {
            var key = (point.Key, point.Point);
            bool receive = available && point.ReceiveActive && point.ReceivedBytes > 0, send = available && point.SendActive && point.SentBytes > 0;
            if (!activity.TryGetValue(key, out var state)) activity[key] = state = new(new(receive), new(send));
            state.Download.Active = receive; state.Upload.Active = send;
        }
        routesDirty = true;
        UpdateAnimation();
        AutomationProperties.SetName(this, "Connections map. " + StatusSummary + (HighlightedKeys.Count > 0 ? $" · {HighlightedKeys.Count} highlighted" : ""));
        canvas.Invalidate();
    }

    public void ResetView() => ChangeCamera(new(origin?.Longitude ?? -20, Flat ? 0 : origin?.Latitude ?? 20, 1, Flat));
    public void SetView(GeoPoint center, double zoom) => ChangeCamera(new(center.Longitude, center.Latitude, zoom, Flat));
    public void ZoomBy(double factor)
    {
        if (double.IsFinite(factor) && factor > 0) ChangeCamera(camera with { Zoom = Math.Clamp(camera.Zoom * Math.Min(factor, 8), 1, 8) });
    }

    private void ChangeCamera(MapCamera next)
    {
        camera = ConnectionProjection.Normalize(next);
        DropGeometry(); canvas.Invalidate();
        ViewChanged?.Invoke();
    }

    private void DropGeometry()
    {
        frame = null;
        coast?.Dispose(); land?.Dispose(); borders?.Dispose(); stateBorders?.Dispose(); grid?.Dispose();
        coast = land = borders = stateBorders = grid = null;
        routesDirty = true;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateAnimation();

    private void UpdateAnimation()
    {
        bool visible = XamlRoot?.IsHostVisible ?? false;
        bool animate = loaded && visible && enabled && activity.Values.Any(a => a.Download.Moving || a.Upload.Moving);
        if (animate == subscribed) return;
        subscribed = animate;
        if (animate) { lastFrame = 0; CompositionTarget.Rendering += Animate; }
        else CompositionTarget.Rendering -= Animate;
    }

    private void Animate(object? sender, object args)
    {
        try
        {
            double time = clock.Elapsed.TotalSeconds;
            if (lastFrame != 0 && time - lastFrame < 1d / 30) return;
            // No jump when a hidden or paused map resumes.
            double elapsed = lastFrame == 0 ? 0 : Math.Clamp(time - lastFrame, 0, .1);
            lastFrame = time; phase += elapsed; StepMotion(); UpdateAnimation(); canvas.Invalidate();
        }
        catch (Exception error)
        {
            App.Note($"Map animation stopped: {error}");
            enabled = false; UpdateAnimation();
        }
    }

    private void StepMotion()
    {
        double elapsed = Math.Max(0, phase - motionPhase); motionPhase = phase;
        if (!subscribed) return;
        foreach (var state in activity.Values) { state.Download.Advance(elapsed); state.Upload.Advance(elapsed); }
    }

    private static CanvasGeometry Polylines(ICanvasResourceCreator creator, IEnumerable<PlotPoint[]> lines, bool closed)
    {
        using var builder = new CanvasPathBuilder(creator);
        if (closed) builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        foreach (var line in lines)
        {
            if (line.Length < 2) continue;
            builder.BeginFigure(new Vector2((float)line[0].X, (float)line[0].Y), closed ? CanvasFigureFill.Default : CanvasFigureFill.DoesNotAffectFills);
            for (int i = 1; i < line.Length; i++) builder.AddLine(new Vector2((float)line[i].X, (float)line[i].Y));
            builder.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        }
        return CanvasGeometry.CreatePath(builder);
    }

    private void EnsureGeometry(ICanvasResourceCreator creator)
    {
        var current = Scene;
        if (coast is null)
        {
            var geography = MapAssets.Geography;
            coast = Polylines(creator, current.Lines(geography.Land), false);
            land = Polylines(creator, current.LandPolygons(geography.Land), true);
            borders = Polylines(creator, current.Lines(geography.Borders), false);
            grid = Polylines(creator, current.Lines(MapFrame.Graticules()), false);
            stateBorders = camera.Zoom > 1.25 ? Polylines(creator, current.Lines(geography.StateBorders), false) : null;
            routesDirty = true;
        }
        if (!routesDirty) return;
        foreach (var route in routes) route.Dispose();
        routes.Clear();
        foreach (var route in current.Routes(points, origin))
            routes.Add(new(route, activity[(route.Data.Key, route.Data.Point)], MapFrame.Seed(route.Data.Key)));
        busiest = routes.OrderByDescending(r => (double)r.Route.Data.ReceivedBytes + r.Route.Data.SentBytes).Take(40).ToArray();
        routesDirty = false;
    }

    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255), color.R, color.G, color.B);
    private static Vector2 V(PlotPoint point) => new((float)point.X, (float)point.Y);

    // An exception that escapes a Win2D draw callback ends the process, so a drawing fault is recorded and that frame is skipped.
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try { DrawMap(sender, args); }
        catch (Exception error) when (error is not OutOfMemoryException) { App.Note($"The connections map could not be drawn: {error}"); }
    }

    private void DrawMap(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ActualWidth < 20 || ActualHeight < 60) return;
        var ds = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(230, c, c, c), muted = Color.FromArgb(150, c, c, c), hairline = Color.FromArgb(70, c, c, c);
        Color ocean = dark ? Color.FromArgb(255, 22, 28, 38) : Color.FromArgb(255, 226, 234, 244);
        Color landColor = dark ? Color.FromArgb(255, 46, 54, 66) : Color.FromArgb(255, 250, 250, 248);
        Color download = UsageColors.Of(session, "download"), upload = UsageColors.Of(session, "upload"), accent = UsageColors.Accent;
        StepMotion(); UpdateAnimation(); EnsureGeometry(sender); hitPoints.Clear();
        var current = Scene; var bounds = current.Bounds;
        var boundsRect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using var round = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };

        using (ds.CreateLayer(1, boundsRect))
        {
            var (globeCenter, globeRadius) = current.Globe;
            using CanvasGeometry clip = Flat
                ? CanvasGeometry.CreateRectangle(sender, new Rect(current.FlatArea.X, current.FlatArea.Y, current.FlatArea.Width, current.FlatArea.Height))
                : CanvasGeometry.CreateCircle(sender, V(globeCenter), (float)globeRadius);
            // Ocean, graticule, land, coast, borders.
            ds.FillGeometry(clip, ocean);
            ds.DrawGeometry(clip, hairline, 1);
            using (ds.CreateLayer(1, clip))
            {
                ds.DrawGeometry(grid!, Alpha(hairline, .5), .6f);
                if (Flat)
                {
                    float shift = (float)current.SeamShift;
                    foreach (float offset in new[] { -shift, 0f, shift })
                    {
                        ds.Transform = Matrix3x2.CreateTranslation(offset, 0);
                        ds.FillGeometry(land!, landColor);
                    }
                    ds.Transform = Matrix3x2.Identity;
                }
                else ds.FillGeometry(land!, landColor);
                ds.DrawGeometry(coast!, hairline, 1);
                if (stateBorders is not null) ds.DrawGeometry(stateBorders, Alpha(muted, .48 * Math.Clamp((camera.Zoom - 1.25) / 1.25, 0, 1)), .5f);
                ds.DrawGeometry(borders!, Alpha(muted, .28), .65f);
                if (!Flat)
                {
                    using var limb = new CanvasRadialGradientBrush(sender,
                    [
                        new CanvasGradientStop { Position = 0, Color = Color.FromArgb(0, 0, 0, 0) }, new CanvasGradientStop { Position = .8f, Color = Color.FromArgb(0, 0, 0, 0) },
                        new CanvasGradientStop { Position = 1, Color = Color.FromArgb(dark ? (byte)140 : (byte)60, 0, 0, 0) },
                    ])
                    { Center = V(globeCenter), RadiusX = (float)globeRadius, RadiusY = (float)globeRadius };
                    ds.FillGeometry(clip, limb);
                }

                // Arcs, then particles on the busiest routes, then nodes.
                foreach (var route in routes) DrawConnection(sender, ds, route, download, upload, ink, round);
                if (subscribed)
                    foreach (var route in busiest)
                    {
                        if (route.Route.Data.ReceivedBytes > 0) DrawParticles(ds, current, route, download, true);
                        if (route.Route.Data.SentBytes > 0) DrawParticles(ds, current, route, upload, false);
                    }
                float scale = mapOnly ? .5f : 1;
                foreach (var route in routes)
                {
                    var data = route.Route.Data;
                    if (current.Position(data.Point) is not { } position) continue;
                    var center = V(position);
                    hitPoints.Add((position, data.Key));
                    bool highlight = IsHighlighted(data), selected = data.Selected || data.Key == HoveredKey || highlight;
                    double level = Math.Max(route.Activity.Download.Level, route.Activity.Upload.Level);
                    float radius = (float)(route.Route.Radius + (selected ? 1.5 : 0)) * scale;
                    if (subscribed) radius += (float)(.25 * scale * level * Math.Sin(phase * 2));
                    var color = data.ReceivedBytes > 0 ? download : upload;
                    ds.FillCircle(center, radius * 2.4f, Alpha(color, .16));
                    ds.FillCircle(center, radius, color);
                    ds.DrawCircle(center, radius, ink, (selected ? 1.2f : .6f) * scale);
                    if (highlight) ds.DrawCircle(center, radius + 4 * scale, accent, scale);
                    if (subscribed && level > 0)
                    {
                        double pulse = phase / 1.6 - Math.Floor(phase / 1.6);
                        ds.DrawCircle(center, radius + (float)(6 * pulse) * scale, Alpha(color, .35 * (1 - pulse)), 1);
                    }
                }
                if (origin is not null && current.Position(origin) is { } home)
                {
                    var center = V(home); float s = mapOnly ? .6f : 1;
                    ds.FillCircle(center, 16 * s, Alpha(accent, .14));
                    ds.DrawCircle(center, 8 * s, Alpha(ink, .6), 1);
                    ds.DrawCircle(center, 13 * s, Alpha(ink, .3), 1);
                    if (subscribed)
                    {
                        double pulse = phase / 2 - Math.Floor(phase / 2);
                        ds.DrawCircle(center, (float)(8 + 10 * pulse) * s, Alpha(ink, .5 * (1 - pulse)), 1);
                    }
                    ds.FillCircle(center, 3.6f * s, ink);
                }
            }
        }

        // A poll can move or drop the hovered node; the hover follows the pointer, never the key.
        if (hoverPosition is { } pointer && dragStart is null)
        {
            string? key = current.Hit(hitPoints, new PlotPoint(pointer.X, pointer.Y))?.Key;
            if (key != HoveredKey) { HoveredKey = key; SetCursor(key is not null); PointHovered?.Invoke(key); }
        }
        if (mapOnly) return;
        float next = 8 + LegendChip(sender, ds, 8, "Download", download, hairline, ink, dark) + 6;
        LegendChip(sender, ds, next, "Upload", upload, hairline, ink, dark);
        if (HoveredKey is { } hoveredKey && hitPoints.FirstOrDefault(h => h.Key == hoveredKey) is { Key: not null } hit)
        {
            var data = points.FirstOrDefault(p => p.Key == hoveredKey);
            var lines = new List<(string Text, Color Color)> { (data?.Label ?? hoveredKey, ink) };
            if (data?.Detail is { } detail) lines.Add((detail, muted));
            // A chip near the reserved toolbar area is placed in the plot below it, so the toolbar never covers the name.
            double top = bounds.Y;
            if (ReservedArea is { } reserved && hit.Center.X > reserved.Left - 220 && hit.Center.Y < reserved.Bottom + 80 && reserved.Bottom + 8 < bounds.Bottom) top = reserved.Bottom + 8;
            DrawChip(sender, ds, hit.Center, new PlotRect(bounds.X, top, bounds.Width, bounds.Bottom - top), lines, dark, hairline);
        }
        if (ActualWidth > 16) ds.DrawText(StatusSummary, new Rect(8, ActualHeight - 30, ActualWidth - 16, 28), muted, StatusFormat);
    }

    private static CanvasGeometry Arcs(ICanvasResourceCreator creator, PlotPoint[][] segments) => Polylines(creator, segments, false);

    private void DrawConnection(ICanvasResourceCreator creator, CanvasDrawingSession ds, DrawnRoute route, Color download, Color upload, Color ink, CanvasStrokeStyle round)
    {
        var data = route.Route.Data;
        if (route.Route.Samples.Length == 0 || route.Route.Segments.Length == 0) return;
        route.Arc ??= Arcs(creator, route.Route.Segments);
        bool selected = data.Selected || data.Key == HoveredKey || IsHighlighted(data);
        // A silent peer keeps a faint download-coloured arc so its location still connects to the origin.
        bool hasDownload = data.ReceivedBytes > 0 || data.SentBytes == 0, hasUpload = data.SentBytes > 0, both = hasDownload && hasUpload;
        void Direction(bool received, Color color)
        {
            var motion = received ? route.Activity.Download : route.Activity.Upload;
            var core = !both ? route.Arc!
                : received ? route.DownloadLane ??= Arcs(creator, MapFrame.Offset(route.Route.Segments, 1))
                : route.UploadLane ??= Arcs(creator, MapFrame.Offset(route.Route.Segments, -1));
            ds.DrawGeometry(route.Arc, Alpha(color, selected ? .12 : .06), 6, round);
            ds.DrawGeometry(route.Arc, Alpha(color, selected ? .30 : .18), 2.5f, round);
            ds.DrawGeometry(core, Alpha(color, selected ? .95 : .30 + .50 * motion.Level), 1.2f, round);
        }
        if (hasDownload) Direction(true, download);
        if (hasUpload) Direction(false, upload);
        if (selected) ds.DrawGeometry(route.Arc, Alpha(ink, .65), .5f, round);
    }

    private void DrawParticles(CanvasDrawingSession ds, MapFrame current, DrawnRoute route, Color color, bool received)
    {
        var motion = received ? route.Activity.Download : route.Activity.Upload;
        if (motion.Level <= 0) return;
        int count = ArcParticles.Count(motion.Level); if (mapOnly) count = Math.Max(1, count / 2);
        for (int i = 0; i < count; i++)
        {
            if (current.Sample(route.Route, ArcParticles.Fraction(motion.Time, .33, count, i, received)) is not { } center) continue;
            ds.FillCircle(V(center), 3.5f, Alpha(color, .30 * motion.Level));
            ds.FillCircle(V(center), 1.3f, Alpha(color, motion.Level));
        }
    }

    private static float LegendChip(ICanvasResourceCreator creator, CanvasDrawingSession ds, float x, string label, Color dot, Color hairline, Color text, bool dark)
    {
        using var layout = new CanvasTextLayout(creator, label, SmallFormat, 0, 0) { WordWrapping = CanvasWordWrapping.NoWrap };
        float width = 8 + 8 + 6 + (float)layout.LayoutBounds.Width + 8;
        ds.FillRoundedRectangle(x, 2, width, 18, 9, 9, dark ? Color.FromArgb(200, 40, 40, 40) : Color.FromArgb(220, 250, 250, 250));
        ds.DrawRoundedRectangle(x, 2, width, 18, 9, 9, hairline, 1);
        ds.FillRoundedRectangle(x + 8, 7, 8, 8, 4, 4, dot);
        ds.DrawTextLayout(layout, x + 22, 2 + (18 - (float)layout.LayoutBounds.Height) / 2, text);
        return width;
    }

    /// <summary>A small text box beside an anchor, kept inside the plot.</summary>
    internal static void DrawChip(ICanvasResourceCreator creator, CanvasDrawingSession ds, PlotPoint anchor, PlotRect plot, IReadOnlyList<(string Text, Color Color)> lines, bool dark, Color hairline)
    {
        if (lines.Count == 0) return;
        float width = 0;
        foreach (var line in lines)
        {
            using var layout = new CanvasTextLayout(creator, line.Text, LabelFormat, 0, 0) { WordWrapping = CanvasWordWrapping.NoWrap };
            width = Math.Max(width, (float)layout.LayoutBounds.Width);
        }
        width += 12;
        float height = lines.Count * 16 + 12;
        float x = (float)anchor.X + 10;
        if (x + width > plot.Right) x = (float)anchor.X - 10 - width;
        x = (float)Math.Clamp(x, plot.X, Math.Max(plot.X, plot.Right - width));
        float top = (float)Math.Clamp(anchor.Y - height / 2, plot.Y, Math.Max(plot.Y, plot.Bottom - height));
        ds.FillRoundedRectangle(x, top, width, height, 4, 4, dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(240, 252, 252, 252));
        ds.DrawRoundedRectangle(x, top, width, height, 4, 4, hairline, 1);
        for (int i = 0; i < lines.Count; i++) ds.DrawText(lines[i].Text, x + 6, top + 6 + i * 16, lines[i].Color, LabelFormat);
    }

    private void SetCursor(bool overNode) =>
        ProtectedCursor = InputSystemCursor.Create(dragStart is not null && dragged ? InputSystemCursorShape.SizeAll : overNode ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);

    /// <summary>Resolves the node under <paramref name="position"/> (null clears) and raises <see cref="PointHovered"/>.</summary>
    private void SetHover(Point? position)
    {
        hoverPosition = position;
        string? key = position is { } p && dragStart is null ? Scene.Hit(hitPoints, new PlotPoint(p.X, p.Y))?.Key : null;
        SetCursor(key is not null);
        if (key == HoveredKey) return;
        HoveredKey = key; PointHovered?.Invoke(key); canvas.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(layers);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        dragStart = point.Position; dragCamera = camera; dragged = false;
        layers.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(layers);
        if (dragStart is not { } start || dragCamera is null || !point.Properties.IsLeftButtonPressed) { SetHover(point.Position); return; }
        double dx = point.Position.X - start.X, dy = point.Position.Y - start.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > 3) dragged = true;
        if (dragged)
        {
            SetCursor(false);
            ChangeCamera(Scene.Dragged(dragCamera, dx, dy));
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(layers).Position;
        bool click = dragStart is not null && !dragged;
        dragStart = null; dragCamera = null;
        layers.ReleasePointerCapture(e.Pointer);
        if (click && Scene.Hit(hitPoints, new PlotPoint(position.X, position.Y)) is { } hit) PointSelected?.Invoke(hit.Key);
        SetHover(position);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Home: ResetView(); break;
            case VirtualKey.Add: case (VirtualKey)187: ZoomBy(1.2); break;
            case VirtualKey.Subtract: case (VirtualKey)189: ZoomBy(1 / 1.2); break;
            case VirtualKey.Left: ChangeCamera(camera with { Longitude = camera.Longitude - 10 / camera.Zoom }); break;
            case VirtualKey.Right: ChangeCamera(camera with { Longitude = camera.Longitude + 10 / camera.Zoom }); break;
            case VirtualKey.Up: ChangeCamera(camera with { Latitude = camera.Latitude + 10 / camera.Zoom }); break;
            case VirtualKey.Down: ChangeCamera(camera with { Latitude = camera.Latitude - 10 / camera.Zoom }); break;
            default: return;
        }
        e.Handled = true;
    }
}
