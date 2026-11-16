using System.Diagnostics;
using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
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
/// Windows' "Animation effects" setting (Settings > Accessibility > Visual effects). With it off the map's arcs stand still, as they
/// do with the Connections page's Animate box cleared. Read once, then kept current from Windows' change notification.
/// </summary>
internal static class SystemAnimations
{
    private static readonly Windows.UI.ViewManagement.UISettings? Settings = Create();
    private static volatile bool enabled = Read();

    /// <summary>Raised on a background thread when the setting changes.</summary>
    public static event Action? Changed;

    public static bool Enabled => enabled;

    private static Windows.UI.ViewManagement.UISettings? Create()
    {
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            settings.AnimationsEnabledChanged += (_, _) => { enabled = Read(); Changed?.Invoke(); };
            return settings;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            App.Note($"The Windows animation setting cannot be read: {error.Message}");
            return null;
        }
    }

    private static bool Read()
    {
        try { return Settings?.AnimationsEnabled ?? true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return true; }
    }
}

/// <summary>
/// The connections map drawn with Win2D from <see cref="MapFrame"/>: a globe or flat map with one node per grouped location and an
/// arc from the origin to each. Drag to move, wheel or +/− to zoom, Home to reset, click a node to select it. Arcs animate only while
/// the map is showing (loaded, its window visible and not covered, scrolled into view), animation is on here and in Windows, and some
/// traffic is moving. A timer then draws about 30 frames a second, fewer once nobody has touched the map for a while
/// (<see cref="MapAnimation"/>). Off screen the map lets go of its drawing surface and everything drawn on the graphics device, and
/// makes them again when it shows.
/// </summary>
internal sealed class ConnectionMapView : UserControl
{
    private static readonly CanvasTextFormat LabelFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat SmallFormat = new() { FontSize = 10 };
    private static readonly CanvasTextFormat StatusFormat = new() { FontSize = 11, WordWrapping = CanvasWordWrapping.Wrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character };
    private static readonly CanvasStrokeStyle Round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
    // While the map animates, whether other windows cover it is looked at again this often; while it is covered, the timer wakes only this often.
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1);

    private sealed record Activity(ConnectionMotion Download, ConnectionMotion Upload);
    private sealed record DrawnRoute(MapRoute Route, Activity Activity, ArcPath Path);

    /// <summary>The strokes an arc is drawn with: a wide glow and a halo along it, a thin core (the arc itself for one direction, a lane
    /// beside it for each direction when traffic flows both ways) and a fine outline while it is selected.</summary>
    private enum ArcStroke { Glow, Halo, Core, DownloadCore, UploadCore, Outline }

    /// <summary>
    /// One location's arc for the current origin, size and camera: its great-circle samples and screen segments, and the strokes drawn
    /// from them. Direct2D tessellates a plain geometry's stroke again on every draw; each stroke here is tessellated once into a mesh
    /// kept on the graphics device, so a frame only fills meshes. They are made when first drawn and let go when the device is lost,
    /// the DPI changes or the map is hidden.
    /// </summary>
    private sealed class ArcPath(MapCoordinate[] samples, PlotPoint[][] segments)
    {
        private static readonly float[] Widths = [6, 2.5f, 1.2f, 1.2f, 1.2f, .5f];
        private readonly CanvasCachedGeometry?[] strokes = new CanvasCachedGeometry?[Widths.Length];
        private CanvasGeometry? arc, downloadLane, uploadLane;
        private float tolerance;

        public MapCoordinate[] Samples { get; } = samples;
        public PlotPoint[][] Segments { get; } = segments;
        /// <summary>The route rebuild that last drew this arc; one the latest rebuild did not use is let go.</summary>
        public int Generation;

        public CanvasCachedGeometry Stroke(ICanvasResourceCreator creator, ArcStroke kind, float flattening)
        {
            // A mesh is flattened for one DPI, so a new DPI makes them all again.
            if (flattening != tolerance) { ReleaseDevice(); tolerance = flattening; }
            ref var stroke = ref strokes[(int)kind];
            return stroke ??= CanvasCachedGeometry.CreateStroke(kind switch
            {
                ArcStroke.DownloadCore => downloadLane ??= Polylines(creator, MapFrame.Offset(Segments, 1), false),
                ArcStroke.UploadCore => uploadLane ??= Polylines(creator, MapFrame.Offset(Segments, -1), false),
                _ => arc ??= Polylines(creator, Segments, false),
            }, Widths[(int)kind], Round, flattening);
        }

        public void ReleaseDevice()
        {
            for (int i = 0; i < strokes.Length; i++) { strokes[i]?.Dispose(); strokes[i] = null; }
            arc?.Dispose(); downloadLane?.Dispose(); uploadLane?.Dispose();
            arc = downloadLane = uploadLane = null;
        }
    }

    private readonly MonitorSession session;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue queue;
    private readonly Grid layers;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly QueueTimer timer;
    // Made when the map shows and dropped when it hides: a CanvasControl keeps its surface, as large as the map, for as long as it exists.
    private CanvasControl? canvas;
    private MapCamera camera = new(-20, 20, 1, false);
    private IReadOnlyList<ConnectionMapPoint> points = [], input = [];
    private HashSet<(double Latitude, double Longitude)> highlighted = [];
    private bool pointSelected;
    private string? selectedPoint;
    private GeoPoint? origin;
    private string status = "Waiting for connections";
    private string? spokenName;
    private bool available, enabled = true, mapOnly;
    // Showing: loaded with its window visible. In view: not scrolled out of sight. Covered: other windows hide all of it.
    private bool showing, inView = true, covered, coverageFailed, animating;
    private Point? dragStart, hoverPosition;
    private MapCamera? dragCamera;
    private bool dragged;
    private double lastFrame, phase, motionPhase, lastInput, lastProbe = double.NegativeInfinity;
    private TimeSpan cadence;
    private XamlRoot? windowRoot;
    private nint window;
    private readonly Dictionary<(string Key, GeoPoint Point), Activity> activity = [];
    private readonly List<DrawnRoute> routes = [];
    private DrawnRoute[] busiest = [];
    private readonly List<(PlotPoint Center, string Key)> hitPoints = [];
    private bool routesDirty = true;
    // Each drawn location's arc, kept from poll to poll while the origin and the frame stay the same.
    private readonly Dictionary<GeoPoint, ArcPath> paths = [];
    private MapFrame? pathsFrame;
    private GeoPoint? pathsOrigin;
    private int pathGeneration;
    // The frame for one size and camera; rebuilt when either changes.
    private MapFrame? frame;
    // What only the size, camera and theme change (ocean, graticule, land, coasts, borders), drawn once into a bitmap.
    // Filling and stroking those outlines afresh cost about 20 ms a draw, on the UI thread, up to 30 times a second.
    private CanvasRenderTarget? baseLayer;
    private bool baseDark;
    // The globe's disc, which clips what is drawn over the bitmap; the flat map clips to a rectangle and needs none.
    private CanvasGeometry? globeClip;
    // The legend's labels and the status line, laid out once instead of on every frame.
    private CanvasTextLayout? downloadLabel, uploadLabel, statusLayout;
    private string? statusText;
    private float statusWidth;

    public ConnectionMapView(MonitorSession session)
    {
        this.session = session;
        queue = DispatcherQueue;
        IsTabStop = true; UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "Connections map");
        AutomationProperties.SetHelpText(this, "Drag to move, wheel or plus and minus to zoom, Home to reset, select a location.");
        timer = new QueueTimer(queue);
        timer.Tick += Tick;
        layers = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        Content = layers;
        layers.PointerEntered += (_, _) => Touched();
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
        SizeChanged += (_, _) => { DropGeometry(); canvas?.Invalidate(); ViewChanged?.Invoke(); UpdateAnimation(); };
        ActualThemeChanged += (_, _) => canvas?.Invalidate();
        EffectiveViewportChanged += OnViewportChanged;
        this.WhileShowing(Show, Hide);
    }

    public bool Flat { get => camera.Flat; set { if (camera.Flat != value) ChangeCamera(camera with { Flat = value }); } }
    public bool MapOnly { get => mapOnly; set { if (mapOnly != value) { mapOnly = value; DropGeometry(); canvas?.Invalidate(); } } }
    public bool AnimationEnabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            if (value) Touched();
            UpdateAnimation(); canvas?.Invalidate();
        }
    }
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
    private double Now => clock.Elapsed.TotalSeconds;
    private bool TooSmall => ActualWidth < 20 || ActualHeight < 60;
    private string StatusSummary => MapFrame.StatusSummary(points.Count, OverflowCount, origin is not null, available, status, MapAssets.Geography.Status);
    private bool IsHighlighted(ConnectionMapPoint point) => highlighted.Count > 0 && highlighted.Contains(ConnectionProjection.LocationKey(point.Point));
    private static IReadOnlyList<ConnectionMapPoint> Selecting(IReadOnlyList<ConnectionMapPoint> points, string? key) =>
        Array.AsReadOnly(points.Select(p => p with { Selected = p.Key == key }).ToArray());

    public void SelectPoint(string? key)
    {
        pointSelected = true; selectedPoint = key;
        points = Selecting(points, key);
        routesDirty = true; Touched(); canvas?.Invalidate();
    }

    /// <summary>Highlights the nodes at these locations (null or empty clears). Regroups only when the location key set changes.</summary>
    public void Highlight(IReadOnlyCollection<GeoPoint>? locations)
    {
        var next = (locations ?? []).Where(ConnectionProjection.IsValid).Select(ConnectionProjection.LocationKey).ToHashSet();
        if (next.SetEquals(highlighted)) return;
        highlighted = next;
        Touched(); StepMotion(); Regroup();
    }

    /// <summary>A displayed node's position in this control, or null when it is not in view.</summary>
    public Point? PointPosition(string key)
    {
        var point = points.FirstOrDefault(p => p.Key == key);
        if (point is null || TooSmall) return null;
        return Scene.Position(point.Point) is { } pixel ? new Point(pixel.X, pixel.Y) : null;
    }

    public void SetData(IReadOnlyList<ConnectionMapPoint> points, GeoPoint? origin, bool available, string status)
    {
        var nextOrigin = origin is not null && ConnectionProjection.IsValid(origin) ? origin : null;
        string nextStatus = status.Length > 240 ? status[..240] : status;
        // A poll that brings nothing new (the same traffic, or none) costs no regrouping, no route rebuild and no redraw.
        if (!pointSelected && nextOrigin == this.origin && available == this.available && nextStatus == this.status
            && (ReferenceEquals(points, input) || points.SequenceEqual(input))) return;
        StepMotion();
        input = points; pointSelected = false; selectedPoint = null;
        if (this.origin != nextOrigin) activity.Clear();
        this.origin = nextOrigin;
        this.available = available; this.status = nextStatus;
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
        string name = "Connections map. " + StatusSummary + (HighlightedKeys.Count > 0 ? $" · {HighlightedKeys.Count} highlighted" : "");
        if (name != spokenName) { spokenName = name; AutomationProperties.SetName(this, name); }
        // An animating map shows it in its next frame; one out of sight is drawn when it comes back.
        if (!animating && inView) canvas?.Invalidate();
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
        DropGeometry(); Touched(); canvas?.Invalidate();
        ViewChanged?.Invoke();
    }

    /// <summary>The size, camera or map-only look changed: the frame, the bitmap and every arc are made again for it.</summary>
    private void DropGeometry()
    {
        frame = null;
        baseLayer?.Dispose(); baseLayer = null;
        globeClip?.Dispose(); globeClip = null;
        ClearPaths();
    }

    private void ClearPaths()
    {
        foreach (var path in paths.Values) path.ReleaseDevice();
        paths.Clear(); pathsFrame = null; routesDirty = true;
    }

    /// <summary>Lets go of everything made on the graphics device: after the device is lost or the DPI changes, and when the map hides.</summary>
    private void ReleaseDevice()
    {
        baseLayer?.Dispose(); baseLayer = null;
        globeClip?.Dispose(); globeClip = null;
        foreach (var path in paths.Values) path.ReleaseDevice();
        downloadLabel?.Dispose(); uploadLabel?.Dispose(); statusLayout?.Dispose();
        downloadLabel = uploadLabel = statusLayout = null; statusText = null;
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) => ReleaseDevice();

    /// <summary>On screen again: a fresh drawing surface, and the map counts as just looked at.</summary>
    private void Show()
    {
        showing = true;
        if (canvas is null)
        {
            canvas = new CanvasControl();
            canvas.Draw += Draw;
            canvas.CreateResources += OnCreateResources;
            layers.Children.Insert(0, canvas);
        }
        SystemAnimations.Changed += OnSystemAnimationsChanged;
        lastInput = Now; lastProbe = double.NegativeInfinity;
        UpdateAnimation();
        canvas?.Invalidate();
    }

    /// <summary>
    /// Off screen (another page, a hidden or minimised window): the timer stops, and the map lets go of the bitmap, the arc meshes, the
    /// text layouts and the canvas with its surface. Its data stays, so showing it again draws at once.
    /// </summary>
    private void Hide()
    {
        showing = false;
        SystemAnimations.Changed -= OnSystemAnimationsChanged;
        UpdateAnimation();
        ReleaseDevice();
        if (canvas is { } old)
        {
            canvas = null;
            old.Draw -= Draw; old.CreateResources -= OnCreateResources;
            // As Win2D advises: out of the tree and no references left, so the collector can free the control and its surface.
            old.RemoveFromVisualTree();
        }
    }

    private void OnSystemAnimationsChanged() => queue.TryEnqueue(() => { UpdateAnimation(); canvas?.Invalidate(); });

    /// <summary>Scrolled out of sight (under the page's lower cards, below the widget's graphs) the map stops; back in sight it starts again.</summary>
    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var view = args.EffectiveViewport;
        bool next = view.Width > 0 && view.Height > 0 && view.X < ActualWidth && view.Y < ActualHeight && view.X + view.Width > 0 && view.Y + view.Height > 0;
        if (next == inView) return;
        inView = next;
        if (next) { Touched(); canvas?.Invalidate(); }
        UpdateAnimation();
    }

    /// <summary>Starts, retimes or stops the frame timer from what the map can show now.</summary>
    private void UpdateAnimation()
    {
        bool wanted = showing && inView && !TooSmall && enabled && SystemAnimations.Enabled && Moving();
        // Coverage is looked at before a start and then about once a second; the margin keeps a covered map's one-second tick, which
        // may come a hair early by this clock, from skipping its look.
        if (wanted && Now - lastProbe >= ProbeInterval.TotalSeconds * .9) Probe();
        bool next = wanted && !covered;
        if (next != animating)
        {
            animating = next;
            // No jump when a hidden or paused map resumes.
            if (next) lastFrame = 0;
        }
        Retime(animating ? MapAnimation.Interval(Now - lastInput) : wanted ? ProbeInterval : TimeSpan.Zero);
    }

    private void Retime(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) { cadence = TimeSpan.Zero; if (timer.IsEnabled) timer.Stop(); return; }
        if (interval == cadence && timer.IsEnabled) return;
        cadence = interval;
        timer.Stop(); timer.Interval = interval; timer.Start();
    }

    /// <summary>Input on the map, or the map coming back into sight: the full frame rate again.</summary>
    private void Touched()
    {
        lastInput = Now;
        if (animating) Retime(MapAnimation.Interval(0));
    }

    /// <summary>Looks whether the map's window is minimised, cloaked or off every monitor, or other windows cover all of the map.</summary>
    private void Probe()
    {
        lastProbe = Now;
        if (coverageFailed) return;
        bool hidden;
        try
        {
            nint handle = WindowHandle();
            hidden = handle != 0 && ScreenArea(handle) is { } area && WindowCoverage.Hidden(handle, area);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Without the check the map animates whenever it shows, as it did before there was one.
            App.Note($"The connections map cannot tell whether it is covered: {error.Message}");
            coverageFailed = true; hidden = false;
        }
        if (hidden == covered) return;
        covered = hidden;
        // Uncovered, someone may be looking again.
        if (!hidden) lastInput = Now;
    }

    /// <summary>The handle of the window the map is in, looked up once per XamlRoot.</summary>
    private nint WindowHandle()
    {
        if (XamlRoot is not { } root) return 0;
        if (!ReferenceEquals(root, windowRoot))
        {
            windowRoot = root;
            window = root.ContentIslandEnvironment is { } environment ? Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
        }
        return window;
    }

    /// <summary>The map's rectangle in screen pixels, or null when it cannot be placed.</summary>
    private ScreenRect? ScreenArea(nint handle)
    {
        if (XamlRoot is not { } root || TooSmall) return null;
        var corner = TransformToVisual(null).TransformPoint(new Point(0, 0));
        double scale = root.RasterizationScale;
        return WindowCoverage.ClientToScreen(handle, corner.X * scale, corner.Y * scale, (corner.X + ActualWidth) * scale, (corner.Y + ActualHeight) * scale);
    }

    /// <summary>
    /// A frame: the motion advances by the real time since the last one, and the map is redrawn while it animates and once more as it
    /// stops, so nothing is left mid-pulse. While the map is covered the tick only looks again whether it still is.
    /// </summary>
    private void Tick()
    {
        try
        {
            bool was = animating;
            if (was)
            {
                double now = Now;
                double elapsed = lastFrame == 0 ? 0 : MapAnimation.Step(now - lastFrame);
                lastFrame = now; phase += elapsed; StepMotion();
            }
            UpdateAnimation();
            if (animating || was) canvas?.Invalidate();
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
        if (!animating) return;
        foreach (var state in activity.Values) { state.Download.Advance(elapsed); state.Upload.Advance(elapsed); }
    }

    private bool Moving()
    {
        foreach (var state in activity.Values) if (state.Download.Moving || state.Upload.Moving) return true;
        return false;
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

    /// <summary>The globe's disc or the flat map's rectangle: nothing of the map is drawn outside it.</summary>
    private CanvasGeometry ClipShape(ICanvasResourceCreator creator, MapFrame current)
    {
        var (center, radius) = current.Globe;
        return Flat
            ? CanvasGeometry.CreateRectangle(creator, new Rect(current.FlatArea.X, current.FlatArea.Y, current.FlatArea.Width, current.FlatArea.Height))
            : CanvasGeometry.CreateCircle(creator, V(center), (float)radius);
    }

    /// <summary>The part of the bounds inside the flat map's area.</summary>
    private static Rect Within(Rect bounds, PlotRect area)
    {
        double left = Math.Max(bounds.X, area.X), top = Math.Max(bounds.Y, area.Y);
        double right = Math.Min(bounds.X + bounds.Width, area.Right), bottom = Math.Min(bounds.Y + bounds.Height, area.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Draws ocean, graticule, land, coast and borders into <see cref="baseLayer"/> when the size, camera, theme or DPI changed.</summary>
    private void EnsureBase(CanvasControl sender, bool dark, Color ocean, Color landColor, Color hairline, Color muted)
    {
        if (baseLayer is not null && baseDark == dark && baseLayer.Dpi == sender.Dpi) return;
        baseLayer?.Dispose();
        baseLayer = new CanvasRenderTarget(sender, (float)ActualWidth, (float)ActualHeight, sender.Dpi);
        baseDark = dark;
        var current = Scene; var bounds = current.Bounds; var geography = MapAssets.Geography;
        using var ds = baseLayer.CreateDrawingSession();
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        using var clip = ClipShape(sender, current);
        using var grid = Polylines(sender, current.Lines(MapFrame.Graticules()), false);
        using var land = Polylines(sender, current.LandPolygons(geography.Land), true);
        using var coast = Polylines(sender, current.Lines(geography.Land), false);
        using var borders = Polylines(sender, current.Lines(geography.Borders), false);
        using var stateBorders = camera.Zoom > 1.25 ? Polylines(sender, current.Lines(geography.StateBorders), false) : null;
        using (ds.CreateLayer(1, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)))
        {
            ds.FillGeometry(clip, ocean);
            ds.DrawGeometry(clip, hairline, 1);
            using (ds.CreateLayer(1, clip))
            {
                ds.DrawGeometry(grid, Alpha(hairline, .5), .6f);
                if (Flat)
                {
                    float shift = (float)current.SeamShift;
                    foreach (float offset in new[] { -shift, 0f, shift })
                    {
                        ds.Transform = Matrix3x2.CreateTranslation(offset, 0);
                        ds.FillGeometry(land, landColor);
                    }
                    ds.Transform = Matrix3x2.Identity;
                }
                else ds.FillGeometry(land, landColor);
                ds.DrawGeometry(coast, hairline, 1);
                if (stateBorders is not null) ds.DrawGeometry(stateBorders, Alpha(muted, .48 * Math.Clamp((camera.Zoom - 1.25) / 1.25, 0, 1)), .5f);
                ds.DrawGeometry(borders, Alpha(muted, .28), .65f);
                if (!Flat)
                {
                    var (globeCenter, globeRadius) = current.Globe;
                    using var limb = new CanvasRadialGradientBrush(sender,
                    [
                        new CanvasGradientStop { Position = 0, Color = Color.FromArgb(0, 0, 0, 0) }, new CanvasGradientStop { Position = .8f, Color = Color.FromArgb(0, 0, 0, 0) },
                        new CanvasGradientStop { Position = 1, Color = Color.FromArgb(dark ? (byte)140 : (byte)60, 0, 0, 0) },
                    ])
                    { Center = V(globeCenter), RadiusX = (float)globeRadius, RadiusY = (float)globeRadius };
                    ds.FillGeometry(clip, limb);
                }
            }
        }
    }

    /// <summary>
    /// Brings the drawn routes up to date with the points. A location's arc is reused while the origin and the frame stay the same, so a
    /// poll in which only the traffic changed rebuilds no geometry; only the node sizes, which follow the traffic, are worked out again.
    /// </summary>
    private void EnsureRoutes(MapFrame current)
    {
        // Arcs depend on the origin and the frame (size and camera): a change of either makes every arc anew.
        if (!ReferenceEquals(pathsFrame, current) || pathsOrigin != origin)
        {
            ClearPaths();
            pathsFrame = current; pathsOrigin = origin;
        }
        if (!routesDirty) return;
        routesDirty = false;
        int generation = ++pathGeneration, used = 0;
        double most = MapFrame.Busiest(points);
        routes.Clear();
        foreach (var point in points)
        {
            if (!paths.TryGetValue(point.Point, out var path))
            {
                var (samples, segments) = current.Path(point.Point, origin);
                paths[point.Point] = path = new ArcPath(samples, segments);
            }
            if (path.Generation != generation) { path.Generation = generation; used++; }
            var (radius, width) = MapFrame.Size(point, most);
            routes.Add(new(new MapRoute(point, path.Samples, path.Segments, radius, width), activity[(point.Key, point.Point)], path));
        }
        // Arcs to locations no longer drawn are let go.
        if (paths.Count > used)
            foreach (var gone in paths.Where(p => p.Value.Generation != generation).Select(p => p.Key).ToArray()) { paths[gone].ReleaseDevice(); paths.Remove(gone); }
        busiest = routes.OrderByDescending(r => (double)r.Route.Data.ReceivedBytes + r.Route.Data.SentBytes).Take(40).ToArray();
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
        if (TooSmall) return;
        var ds = args.DrawingSession;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(230, c, c, c), muted = Color.FromArgb(150, c, c, c), hairline = Color.FromArgb(70, c, c, c);
        Color ocean = dark ? Color.FromArgb(255, 22, 28, 38) : Color.FromArgb(255, 226, 234, 244);
        Color landColor = dark ? Color.FromArgb(255, 46, 54, 66) : Color.FromArgb(255, 250, 250, 248);
        Color download = UsageColors.Of(session, "download"), upload = UsageColors.Of(session, "upload"), accent = UsageColors.Accent;
        StepMotion(); EnsureBase(sender, dark, ocean, landColor, hairline, muted);
        var current = Scene; var bounds = current.Bounds;
        EnsureRoutes(current); hitPoints.Clear();
        var boundsRect = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        float tolerance = CanvasGeometry.ComputeFlatteningTolerance(sender.Dpi, 1);

        // Ocean, graticule, land, coast, borders: one bitmap, drawn when the view last changed.
        ds.DrawImage(baseLayer);
        // Nothing is drawn outside the map. The flat map's edge is a rectangle, which one axis-aligned clip gives cheaply; the globe's
        // disc needs a layer with a geometry mask, which costs an offscreen surface on every frame.
        using (ds.CreateLayer(1, Flat ? Within(boundsRect, current.FlatArea) : boundsRect))
        using (Flat ? null : ds.CreateLayer(1, globeClip ??= ClipShape(sender, current)))
        {
            // Arcs, then particles on the busiest routes, then nodes.
            foreach (var route in routes) DrawConnection(sender, ds, route, tolerance, download, upload, ink);
            if (animating)
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
                if (animating) radius += (float)(.25 * scale * level * Math.Sin(phase * 2));
                var color = data.ReceivedBytes > 0 ? download : upload;
                ds.FillCircle(center, radius * 2.4f, Alpha(color, .16));
                ds.FillCircle(center, radius, color);
                ds.DrawCircle(center, radius, ink, (selected ? 1.2f : .6f) * scale);
                if (highlight) ds.DrawCircle(center, radius + 4 * scale, accent, scale);
                if (animating && level > 0)
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
                if (animating)
                {
                    double pulse = phase / 2 - Math.Floor(phase / 2);
                    ds.DrawCircle(center, (float)(8 + 10 * pulse) * s, Alpha(ink, .5 * (1 - pulse)), 1);
                }
                ds.FillCircle(center, 3.6f * s, ink);
            }
        }

        // A poll can move or drop the hovered node; the hover follows the pointer, never the key.
        if (hoverPosition is { } pointer && dragStart is null)
        {
            string? key = current.Hit(hitPoints, new PlotPoint(pointer.X, pointer.Y))?.Key;
            if (key != HoveredKey) { HoveredKey = key; SetCursor(key is not null); PointHovered?.Invoke(key); }
        }
        if (mapOnly) return;
        downloadLabel ??= Label(sender, "Download");
        uploadLabel ??= Label(sender, "Upload");
        float next = 8 + LegendChip(ds, 8, downloadLabel, download, hairline, ink, dark) + 6;
        LegendChip(ds, next, uploadLabel, upload, hairline, ink, dark);
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
        if (ActualWidth > 16)
        {
            string text = StatusSummary; float width = (float)(ActualWidth - 16);
            if (statusLayout is null || text != statusText || width != statusWidth)
            {
                statusLayout?.Dispose();
                statusLayout = new CanvasTextLayout(sender, text, StatusFormat, width, 28);
                statusText = text; statusWidth = width;
            }
            ds.DrawTextLayout(statusLayout, 8, (float)(ActualHeight - 30), muted);
        }
    }

    private void DrawConnection(ICanvasResourceCreator creator, CanvasDrawingSession ds, DrawnRoute route, float tolerance, Color download, Color upload, Color ink)
    {
        var data = route.Route.Data; var path = route.Path;
        if (path.Samples.Length == 0 || path.Segments.Length == 0) return;
        bool selected = data.Selected || data.Key == HoveredKey || IsHighlighted(data);
        // A silent peer keeps a faint download-coloured arc so its location still connects to the origin.
        bool hasDownload = data.ReceivedBytes > 0 || data.SentBytes == 0, hasUpload = data.SentBytes > 0, both = hasDownload && hasUpload;
        void Direction(bool received, Color color)
        {
            var motion = received ? route.Activity.Download : route.Activity.Upload;
            var core = path.Stroke(creator, !both ? ArcStroke.Core : received ? ArcStroke.DownloadCore : ArcStroke.UploadCore, tolerance);
            ds.DrawCachedGeometry(path.Stroke(creator, ArcStroke.Glow, tolerance), Alpha(color, selected ? .12 : .06));
            ds.DrawCachedGeometry(path.Stroke(creator, ArcStroke.Halo, tolerance), Alpha(color, selected ? .30 : .18));
            ds.DrawCachedGeometry(core, Alpha(color, selected ? .95 : .30 + .50 * motion.Level));
        }
        if (hasDownload) Direction(true, download);
        if (hasUpload) Direction(false, upload);
        if (selected) ds.DrawCachedGeometry(path.Stroke(creator, ArcStroke.Outline, tolerance), Alpha(ink, .65));
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

    private static CanvasTextLayout Label(ICanvasResourceCreator creator, string text) =>
        new(creator, text, SmallFormat, 0, 0) { WordWrapping = CanvasWordWrapping.NoWrap };

    private static float LegendChip(CanvasDrawingSession ds, float x, CanvasTextLayout layout, Color dot, Color hairline, Color text, bool dark)
    {
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
        HoveredKey = key; PointHovered?.Invoke(key); canvas?.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(layers);
        if (!point.Properties.IsLeftButtonPressed) return;
        Touched();
        Focus(FocusState.Pointer);
        dragStart = point.Position; dragCamera = camera; dragged = false;
        layers.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Touched();
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
