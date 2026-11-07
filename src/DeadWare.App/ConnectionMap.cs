using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using DeadWare.Core;

namespace DeadWare.App;

public sealed class ConnectionMap : FrameworkElement
{
    private MapCamera camera = new(-20, 20, 1, false);
    private IReadOnlyList<ConnectionMapPoint> points = [];
    /// <summary>The last <see cref="SetData"/> input, regrouped when the highlight changes.</summary>
    private IReadOnlyList<ConnectionMapPoint> input = [];
    private HashSet<(double Latitude, double Longitude)> highlighted = [];
    /// <summary>The last <see cref="SelectPoint"/> key, re-applied by a regroup; a new <see cref="SetData"/> input carries its own selection.</summary>
    private bool pointSelected;
    private string? selectedPoint;
    private GeoPoint? origin;
    private string status = "Waiting for connections";
    private bool available, enabled = true, loaded, subscribed, mapOnly;
    private Point? dragStart;
    private MapCamera? dragCamera;
    private bool dragged;
    private TimeSpan lastFrame;
    private double phase;
    private double motionPhase;
    private sealed record Activity(ConnectionMotion Download, ConnectionMotion Upload);
    private readonly Dictionary<(string Key,GeoPoint Point),Activity> activity = [];
    private Size cachedSize;
    private MapCamera? cachedCamera;
    private StreamGeometry? coast, borders, stateBorders, grid, landFill;
    private IReadOnlyList<IReadOnlyList<GeoPoint>>? landOverride;
    private sealed record Route(ConnectionMapPoint Data,MapCoordinate[] Samples,Point[][] Segments,double Radius,double Width,double Seed,Activity Activity)
    {
        /// <summary>The arc through the projected samples, built once per projection.</summary>
        public StreamGeometry? Arc { get; set; }
        /// <summary>Two hairline lanes offset ±1 px when both directions are active.</summary>
        public StreamGeometry? DownloadLane { get; set; }
        public StreamGeometry? UploadLane { get; set; }
    }
    private readonly Dictionary<(Brush Brush,double Width),Pen> pens = [];
    private readonly Dictionary<string,Brush> brushes = [];
    private string brushTheme = "";
    private readonly List<Route> routes = [];
    /// <summary>The forty routes carrying the most bytes, recomputed with the routes rather than per frame.</summary>
    private Route[] busiest = [];
    private readonly List<(Point Center, string Key)> hitPoints = [];
    private bool routesDirty = true;
    private bool projectionDirty = true;
    private Point? hoverPosition;

    internal const double HitRadius = 9, WidgetHitRadius = 12;
    /// <summary>Key of the node under the pointer, or null.</summary>
    public string? HoveredKey { get; private set; }
    public event Action<string?>? PointHovered;
    /// <summary>Tests substitute the embedded land rings (an empty list simulates a failed asset).</summary>
    internal IReadOnlyList<IReadOnlyList<GeoPoint>>? LandOverride { get => landOverride; set { landOverride = value; cachedCamera = null; InvalidateVisual(); } }

    public ConnectionMap()
    {
        Focusable = true; ClipToBounds = true;
        Cursor = Cursors.Arrow;
        ToolTip = "Drag to move · wheel or +/− to zoom · Home to reset · select a location";
        Loaded += (_, _) => { loaded = true; UpdateAnimation(); };
        Unloaded += (_, _) => { loaded = false; UpdateAnimation(); };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        // Resource references invalidate the drawing when any theme or graph role changes.
        SetResourceReference(PaletteProperty, "Ink");
        SetResourceReference(DownloadProperty, Theme.GraphKey("download"));
        SetResourceReference(UploadProperty, Theme.GraphKey("upload"));
        SetResourceReference(AccentProperty, "Accent");
    }

    private static readonly DependencyProperty PaletteProperty = DependencyProperty.Register("Palette", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DownloadProperty = DependencyProperty.Register("Download", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty UploadProperty = DependencyProperty.Register("Upload", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AccentProperty = DependencyProperty.Register("Accent", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Flat { get => camera.Flat; set { if (camera.Flat != value) ChangeCamera(camera with { Flat = value }); } }
    public bool MapOnly { get => mapOnly; set { if (mapOnly != value) { mapOnly = value; cachedCamera = null; routesDirty = true; InvalidateVisual(); } } }
    public bool AnimationEnabled { get => enabled; set { enabled = value; UpdateAnimation(); InvalidateVisual(); } }
    public bool IsAnimating => subscribed;
    public MapCamera Camera => camera;
    public int DisplayedPointCount => points.Count;
    public IReadOnlyList<ConnectionMapPoint> DisplayedPoints => points;
    public void SelectPoint(string? key)
    {
        pointSelected=true; selectedPoint=key;
        points=Selecting(points,key);
        routesDirty=true; InvalidateVisual();
    }
    private static IReadOnlyList<ConnectionMapPoint> Selecting(IReadOnlyList<ConnectionMapPoint> points,string? key)=>Array.AsReadOnly(points.Select(p=>p with {Selected=p.Key==key}).ToArray());
    /// <summary>Displayed nodes whose location key is highlighted.</summary>
    public IReadOnlyList<string> HighlightedKeys { get; private set; } = [];
    private bool IsHighlighted(ConnectionMapPoint point) => highlighted.Count > 0 && highlighted.Contains(ConnectionProjection.LocationKey(point.Point));
    /// <summary>Highlights the nodes at these locations (null or empty clears). Regroups only when the location key set changes.</summary>
    public void Highlight(IReadOnlyCollection<GeoPoint>? locations)
    {
        var next=(locations??[]).Where(ConnectionProjection.IsValid).Select(ConnectionProjection.LocationKey).ToHashSet();
        if(next.SetEquals(highlighted))return;
        highlighted=next;
        StepMotion(); Regroup();
    }
    public event Action? ViewChanged;
    public Point? PointPosition(string key)
    {
        var point = points.FirstOrDefault(p => p.Key == key);
        if (point is null || ActualWidth < 20 || ActualHeight < 60) return null;
        var projected = ConnectionProjection.Project(point.Point, camera);
        var pixel = Pixel(projected);
        return projected.Visible && MapBounds.Contains(pixel) ? pixel : null;
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ViewChanged?.Invoke();
    }
    public int OverflowCount { get; private set; }
    public string StatusSummary => $"{points.Count} locations{(OverflowCount > 0 ? $" · +{OverflowCount} omitted" : "")}" +
        (origin is null ? " · Unknown origin" : " · Approximate locations") +
        (!available ? " · Capture unavailable" : "") + (status.Length > 0 ? " · " + status : "") +
        (ConnectionAssets.Status.Length > 0 ? " · " + ConnectionAssets.Status : "");
    public event Action<string>? PointSelected;

    public void SetData(IReadOnlyList<ConnectionMapPoint> points, GeoPoint? origin, bool available, string status)
    {
        StepMotion();
        input = points; pointSelected = false; selectedPoint = null;
        var nextOrigin = origin is not null && ConnectionProjection.IsValid(origin) ? origin : null;
        if(this.origin!=nextOrigin){activity.Clear();projectionDirty=true;}
        this.origin = nextOrigin;
        this.available = available; this.status = status.Length > 240 ? status[..240] : status;
        Regroup();
    }
    /// <summary>Groups the stored input under the current highlight, re-applies the stored <see cref="SelectPoint"/> key and redraws.</summary>
    private void Regroup()
    {
        var grouped = ConnectionProjection.Group(input, highlighted.Count > 0 ? highlighted : null);
        points = pointSelected ? Selecting(grouped.Points, selectedPoint) : grouped.Points; OverflowCount = grouped.Overflow;
        HighlightedKeys = Array.AsReadOnly(points.Where(IsHighlighted).Select(p => p.Key).ToArray());
        var keys=points.Select(p=>(p.Key,p.Point)).ToHashSet();
        foreach(var key in activity.Keys.Where(k=>!keys.Contains(k)).ToArray())activity.Remove(key);
        foreach(var point in points)
        {
            var key=(point.Key,point.Point);
            bool receive=available&&point.ReceiveActive&&point.ReceivedBytes>0,send=available&&point.SendActive&&point.SentBytes>0;
            if(!activity.TryGetValue(key,out var state))activity[key]=state=new(new(receive),new(send));
            state.Download.Active=receive;state.Upload.Active=send;
        }
        routesDirty = true;
        UpdateAnimation();
        AutomationProperties.SetName(this, "Connections map. " + StatusSummary + (HighlightedKeys.Count > 0 ? $" · {HighlightedKeys.Count} highlighted" : ""));
        InvalidateVisual();
    }

    public void ResetView() => ChangeCamera(new(origin?.Longitude ?? -20, Flat ? 0 : origin?.Latitude ?? 20, 1, Flat));
    public void SetView(GeoPoint center,double zoom) => ChangeCamera(new(center.Longitude,center.Latitude,zoom,Flat));
    public void ZoomBy(double factor)
    {
        if (double.IsFinite(factor) && factor > 0) ChangeCamera(camera with { Zoom = Math.Clamp(camera.Zoom * Math.Min(factor, 8), 1, 8) });
    }
    private void ChangeCamera(MapCamera next)
    {
        camera = ConnectionProjection.Normalize(next); routesDirty = true; InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void UpdateAnimation()
    {
        bool animate = loaded && IsVisible && enabled && activity.Values.Any(a=>a.Download.Moving||a.Upload.Moving);
        if (animate == subscribed) return;
        subscribed = animate;
        if (animate) { lastFrame = TimeSpan.Zero; CompositionTarget.Rendering += Animate; }
        else CompositionTarget.Rendering -= Animate;
    }
    private void Animate(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || frame.RenderingTime - lastFrame < TimeSpan.FromSeconds(1d / 30)) return;
        // No wall-clock jump when a hidden/paused map resumes.
        double elapsed=lastFrame==TimeSpan.Zero?0:Math.Clamp((frame.RenderingTime-lastFrame).TotalSeconds,0,.1);
        lastFrame = frame.RenderingTime; phase += elapsed; StepMotion(); UpdateAnimation(); InvalidateVisual();
    }
    private void StepMotion()
    {
        double elapsed=Math.Max(0,phase-motionPhase);motionPhase=phase;
        if(!subscribed)return;
        foreach(var state in activity.Values){state.Download.Advance(elapsed);state.Upload.Advance(elapsed);}
    }

    private Rect MapBounds => MapOnly ? new(3, 3, Math.Max(1, ActualWidth - 6), Math.Max(1, ActualHeight - 6)) :
        new(10, 21, Math.Max(1, ActualWidth - 20), Math.Max(1, ActualHeight - 55));
    private (double X, double Y) Scales()
    {
        var bounds = MapBounds;
        double yScale = Flat ? Math.Min(bounds.Height / 2, bounds.Width / 4) : Math.Min(bounds.Width, bounds.Height) / 2;
        double xScale = Flat ? yScale * 2 : yScale;
        if (Flat && MapOnly) { xScale = bounds.Width / 2; yScale = bounds.Height / 2; }
        return (xScale, yScale);
    }
    private Point Pixel(MapCoordinate coordinate)
    {
        var bounds = MapBounds; var (xScale, yScale) = Scales();
        return new(bounds.X + bounds.Width / 2 + coordinate.X * xScale, bounds.Y + bounds.Height / 2 + coordinate.Y * yScale);
    }
    private StreamGeometry Geometry(IEnumerable<IReadOnlyList<GeoPoint>> paths)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        foreach (var path in paths)
        foreach (var segment in ConnectionProjection.ProjectPath(path, camera))
        {
            context.BeginFigure(Pixel(segment[0]), false, false);
            for (int i = 1; i < segment.Count; i++) context.LineTo(Pixel(segment[i]), true, false);
        }
        geometry.Freeze(); return geometry;
    }
    /// <summary>Closed, filled land rings. On the globe, back-hemisphere points sit on the limb; on the flat map a ring that
    /// crosses the seam is unwrapped so the seam copies drawn at ±360° cover it.</summary>
    private StreamGeometry LandGeometry(IEnumerable<IReadOnlyList<GeoPoint>> rings)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        foreach (var ring in rings)
        {
            if (ring.Count < 3) continue;
            var projected = ConnectionProjection.ProjectRing(ring, camera);
            if (!Flat && projected.All(p => !p.Visible)) continue;
            double shift = 0; MapCoordinate previous = projected[0];
            context.BeginFigure(Pixel(previous), true, true);
            for (int i = 1; i < projected.Count; i++)
            {
                var point = projected[i];
                if (Flat && Math.Abs(point.X - previous.X) > camera.Zoom) shift += point.X > previous.X ? -2 * camera.Zoom : 2 * camera.Zoom;
                // On the globe an edge that crosses the horizon passes through its true crossing before the limb point,
                // so the fill never bulges past the coastline.
                if (!Flat && point.Visible != previous.Visible) context.LineTo(Pixel(ConnectionProjection.HorizonCrossing(ring[i - 1], ring[i], camera)), false, false);
                previous = point;
                context.LineTo(Pixel(point with { X = point.X + shift }), false, false);
            }
        }
        geometry.Freeze(); return geometry;
    }
    private static IEnumerable<IReadOnlyList<GeoPoint>> Graticules()
    {
        for (int lat = -60; lat <= 60; lat += 30)
            yield return Enumerable.Range(0, 121).Select(i => new GeoPoint(lat, -180 + i * 3)).ToArray();
        for (int lon = -180; lon < 180; lon += 30)
            yield return Enumerable.Range(0, 61).Select(i => new GeoPoint(-90 + i * 3, lon)).ToArray();
    }
    private void EnsureGeometry()
    {
        if (cachedSize != RenderSize || cachedCamera != camera)
        {
            cachedSize = RenderSize; cachedCamera = camera;
            var land = landOverride ?? ConnectionAssets.Land;
            coast = Geometry(land); landFill = LandGeometry(land); borders = Geometry(ConnectionAssets.Borders); grid = Geometry(Graticules()); routesDirty = true;
            stateBorders = camera.Zoom > 1.25 ? Geometry(ConnectionAssets.StateBorders) : null;
            projectionDirty=true;
        }
        if (!routesDirty) return;
        var previous=projectionDirty?null:routes.ToDictionary(r=>(r.Data.Key,r.Data.Point));
        routes.Clear();
        double maximum=points.Select(p=>(double)p.ReceivedBytes+p.SentBytes).DefaultIfEmpty(0).Max();
        foreach (var point in points)
        {
            double relative=maximum>0?Math.Log(1+(double)point.ReceivedBytes+point.SentBytes)/Math.Log(1+maximum):0;
            if(previous is not null&&previous.TryGetValue((point.Key,point.Point),out var cached))
            {
                routes.Add(cached with {Data=point,Radius=3+relative*3,Width=.6+Math.Round(relative*4)*.15,Activity=activity[(point.Key,point.Point)]});
                continue;
            }
            var samples = origin is null ? Array.Empty<GeoPoint>() : ConnectionProjection.GreatCircle(origin, point.Point);
            uint seed=2166136261;foreach(char c in point.Key)seed=unchecked((seed^c)*16777619);
            routes.Add(new(point,samples.Select(p=>ConnectionProjection.Project(p,camera)).ToArray(),
                ConnectionProjection.ProjectPath(samples,camera).Select(s=>s.Select(Pixel).ToArray()).ToArray(),3+relative*3,.6+Math.Round(relative*4)*.15,seed%1000/100d,activity[(point.Key,point.Point)]));
        }
        busiest = routes.OrderByDescending(r => (double)r.Data.ReceivedBytes + r.Data.SentBytes).Take(40).ToArray();
        routesDirty = false;projectionDirty=false;
    }
    /// <summary>A polyline through the route's screen segments, shifted <paramref name="offset"/> px along the local normal.</summary>
    private static StreamGeometry Polyline(Point[][] segments, double offset)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        foreach (var segment in segments)
        {
            if (segment.Length < 2) continue;
            for (int i = 0; i < segment.Length; i++)
            {
                var position = segment[i];
                if (offset != 0)
                {
                    var tangent = segment[Math.Min(i + 1, segment.Length - 1)] - segment[Math.Max(0, i - 1)];
                    if (tangent.Length > 0) position += new Vector(-tangent.Y, tangent.X) / tangent.Length * offset;
                }
                if (i == 0) context.BeginFigure(position, false, false); else context.LineTo(position, true, false);
            }
        }
        geometry.Freeze(); return geometry;
    }
    /// <summary>Screen position at a fraction of the route, or null across the rear hemisphere or the flat seam.</summary>
    private Point? Sample(Route route, double fraction)
    {
        if (route.Samples.Length < 2) return null;
        double position=Math.Clamp(fraction,0,1)*(route.Samples.Length-1);int index=Math.Min(route.Samples.Length-2,(int)position);
        var a=route.Samples[index];var b=route.Samples[index+1];
        if(!a.Visible||!b.Visible)return null;
        double progress=position-index,x=b.X;
        if(Flat&&Math.Abs(a.X-x)>camera.Zoom)x+=a.X>0?2*camera.Zoom:-2*camera.Zoom;
        x=a.X+(x-a.X)*progress;
        if(Flat){if(x>camera.Zoom)x-=2*camera.Zoom;else if(x< -camera.Zoom)x+=2*camera.Zoom;}
        return Pixel(new(x,a.Y+(b.Y-a.Y)*progress,1,true));
    }
    private Pen Stroke(Brush brush, double width)
    {
        var key=(brush,width);if(pens.TryGetValue(key,out var cached))return cached;
        var pen = new Pen(brush, width) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        if (pen.CanFreeze) pen.Freeze();if(pens.Count>=128)pens.Clear();pens[key]=pen;return pen;
    }
    private static Color ColorOf(string resource) => Theme.Brush(resource) is SolidColorBrush solid ? solid.Color : Colors.Transparent;
    private static Color WithAlpha(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(255 * opacity), 0, 255), color.R, color.G, color.B);
    /// <summary>Gradient brushes are rebuilt once per theme and cached; a frozen brush per role keeps rendering allocation-free.</summary>
    private Brush Cached(string key, Func<Brush> build)
    {
        if (!brushes.TryGetValue(key, out var brush)) { brush = build(); if (brush.CanFreeze) brush.Freeze(); brushes[key] = brush; }
        return brush;
    }
    private Brush OceanBrush(bool flat, bool blackout)
    {
        if (blackout) return Theme.Brush("Background");
        if (flat) return SystemParameters.HighContrast ? Theme.Brush("Panel") : Theme.Brush("Surface1");
        if (Flat) return Cached("ocean-flat", () => new LinearGradientBrush(ColorOf("Surface1"), ColorOf("Background"), 90));
        return Cached("ocean-globe", () => new RadialGradientBrush(ColorOf("Surface1"), ColorOf("Background")) { GradientOrigin = new(.38, .32), Center = new(.5, .5), RadiusX = .55, RadiusY = .55 });
    }
    private Brush LandBrush(bool blackout) => blackout ? Cached("land-blackout", () => new SolidColorBrush(WithAlpha(ColorOf("Ink"), .10))) : Theme.Brush("Surface2");
    private Brush LimbBrush() => Cached("limb", () =>
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new(Colors.Transparent, 0)); brush.GradientStops.Add(new(Colors.Transparent, .8));
        brush.GradientStops.Add(new(Color.FromArgb(Theme.IsDark ? (byte)140 : (byte)70, 0, 0, 0), 1));
        return brush;
    });
    private Brush AtmosphereBrush() => Cached("atmosphere", () =>
    {
        var glow = ColorOf("Glow"); var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new(Colors.Transparent, 0)); brush.GradientStops.Add(new(Colors.Transparent, .86));
        brush.GradientStops.Add(new(glow, .90)); brush.GradientStops.Add(new(Colors.Transparent, 1));
        return brush;
    });
    private Brush Halo(Brush source, double opacity, string role)
    {
        var color = source is SolidColorBrush solid ? solid.Color : ColorOf("Accent");
        return Cached("halo-" + role + "-" + opacity + "-" + color, () => new RadialGradientBrush(WithAlpha(color, opacity), Colors.Transparent));
    }

    private void DrawConnection(DrawingContext dc,Route route,Brush download,Brush upload,Brush ink,bool glow)
    {
        if(route.Samples.Length==0||route.Segments.Length==0)return;
        route.Arc??=Polyline(route.Segments,0);
        bool selected=route.Data.Selected||route.Data.Key==HoveredKey||IsHighlighted(route.Data);
        // A silent peer keeps a faint download-colored arc so its location still connects to the origin.
        bool hasDownload=route.Data.ReceivedBytes>0||route.Data.SentBytes==0,hasUpload=route.Data.SentBytes>0,both=hasDownload&&hasUpload;
        if(hasDownload)DrawDirection(dc,route,true,download,both,selected,glow);
        if(hasUpload)DrawDirection(dc,route,false,upload,both,selected,glow);
        if(selected){dc.PushOpacity(.65);dc.DrawGeometry(null,Stroke(ink,.5),route.Arc);dc.Pop();}
    }
    private void DrawDirection(DrawingContext dc,Route route,bool received,Brush brush,bool both,bool selected,bool glow)
    {
        {
            var motion=received?route.Activity.Download:route.Activity.Upload;
            Geometry core=both?(received?route.DownloadLane??=Polyline(route.Segments,1):route.UploadLane??=Polyline(route.Segments,-1)):route.Arc!;
            if(glow)
            {
                dc.PushOpacity(selected?.12:.06);dc.DrawGeometry(null,Stroke(brush,6),route.Arc);dc.Pop();
                dc.PushOpacity(selected?.30:.18);dc.DrawGeometry(null,Stroke(brush,2.5),route.Arc);dc.Pop();
            }
            dc.PushOpacity(selected?.95:.30+.50*motion.Level);dc.DrawGeometry(null,Stroke(brush,1.2),core);dc.Pop();
        }
    }
    private void DrawParticles(DrawingContext dc,Route route,Brush brush,Brush? spark,bool received)
    {
        var motion=received?route.Activity.Download:route.Activity.Upload;
        if(motion.Level<=0)return;
        int count=ArcParticles.Count(motion.Level);if(MapOnly)count=Math.Max(1,count/2);
        for(int i=0;i<count;i++)
        {
            if(Sample(route,ArcParticles.Fraction(motion.Time,.33,count,i,received)) is not { } center)continue;
            dc.PushOpacity(motion.Level);
            if(spark is not null)dc.DrawEllipse(spark,null,center,3.5,3.5);
            dc.DrawEllipse(brush,null,center,1.1,1.1);
            dc.Pop();
        }
    }
    private FormattedText Formatted(string text,double size,Brush brush)=>new(text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,Theme.ChartTypeface,size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private double LegendChip(DrawingContext dc,double x,string label,Brush dot,Brush hairline,Brush fill,Brush text)
    {
        var formatted=Formatted(label,10,text);
        var rect=new Rect(x,2,8+8+6+formatted.Width+8,18);
        dc.DrawRoundedRectangle(fill,Stroke(hairline,1),rect,9,9);
        dc.DrawRoundedRectangle(dot,null,new Rect(x+8,7,8,8),4,4);
        dc.DrawText(formatted,new(x+22,rect.Top+(rect.Height-formatted.Height)/2));
        return rect.Width;
    }
    private void Text(DrawingContext dc, string text, Point at, double width, double height, Brush brush)
    {
        if (width < 1 || height < 1) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Theme.ChartTypeface,
            ActualWidth < 350 ? 10 : 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = width, MaxTextHeight = height, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, at);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 20 || ActualHeight < 60) return;
        var bounds = MapBounds;
        bool highContrast = SystemParameters.HighContrast, blackout = Theme.CurrentName == "Blackout";
        bool glow = !highContrast && (Theme.GlassEnabled || blackout);
        // Gradients (ocean, limb) need glass or Blackout too: disabled transparency stays flat.
        bool gradients = glow;
        var ink = Theme.Brush("Ink"); var muted = Theme.Brush("Muted"); var hairline = Theme.Brush("Hairline");
        var download = Theme.Brush(Theme.GraphKey("download")); var upload = Theme.Brush(Theme.GraphKey("upload")); var accent = Theme.Brush("Accent");
        // One cache key and four halo brushes per render; the node and particle loops allocate nothing.
        string theme = Theme.CurrentName + "|" + Theme.GlassEnabled + "|" + Flat + "|" + MapOnly;
        if (theme != brushTheme) { brushes.Clear(); brushTheme = theme; }
        Brush? downloadHalo = glow ? Halo(download, .25, "download") : null, uploadHalo = glow ? Halo(upload, .25, "upload") : null;
        Brush? downloadSpark = glow ? Halo(download, 1, "download") : null, uploadSpark = glow ? Halo(upload, 1, "upload") : null;
        dc.DrawRectangle(Theme.Brush("Background"), null, new Rect(RenderSize));
        StepMotion();UpdateAnimation();EnsureGeometry(); hitPoints.Clear();
        dc.PushClip(new RectangleGeometry(bounds));
        Geometry clip; Point globeCenter = default; double globeRadius = 0;
        if (Flat)
        {
            var topLeft = Pixel(new(-camera.Zoom, (camera.Latitude / 90 - 1) * camera.Zoom, 1, true));
            var bottomRight = Pixel(new(camera.Zoom, (camera.Latitude / 90 + 1) * camera.Zoom, 1, true));
            clip = new RectangleGeometry(new Rect(topLeft, bottomRight));
        }
        else
        {
            globeCenter = Pixel(new(0, 0, 1, true)); globeRadius = Math.Min(bounds.Width, bounds.Height) / 2 * camera.Zoom;
            clip = new EllipseGeometry(globeCenter, globeRadius, globeRadius);
        }
        // Ocean, graticule, land, coast, borders.
        dc.DrawGeometry(OceanBrush(highContrast || !gradients, blackout), Stroke(hairline, 1), clip);
        dc.PushClip(clip);
        dc.PushOpacity(.2); dc.DrawGeometry(null, Stroke(hairline, .6), grid); dc.Pop();
        var land = LandBrush(blackout);
        if (Flat)
        {
            double shift = 2 * camera.Zoom * Scales().X;
            foreach (double offset in new[] { -shift, 0, shift })
            {
                if (offset != 0) dc.PushTransform(new TranslateTransform(offset, 0));
                dc.DrawGeometry(land, null, landFill);
                if (offset != 0) dc.Pop();
            }
        }
        else dc.DrawGeometry(land, null, landFill);
        dc.DrawGeometry(null, Stroke(hairline, 1), coast);
        if(stateBorders is not null)
        {
            dc.PushOpacity(.48*Math.Clamp((camera.Zoom-1.25)/1.25,0,1));
            dc.DrawGeometry(null,Stroke(muted,.5),stateBorders);dc.Pop();
        }
        dc.PushOpacity(.28);dc.DrawGeometry(null,Stroke(muted,.65),borders);dc.Pop();
        if (!Flat && gradients) dc.DrawGeometry(LimbBrush(), null, clip);
        // Arcs, then particles on the busiest routes, then nodes.
        foreach (var route in routes) DrawConnection(dc, route, download, upload, ink, glow);
        if (subscribed)
            foreach (var route in busiest)
            {
                if (route.Data.ReceivedBytes > 0) DrawParticles(dc, route, download, downloadSpark, true);
                if (route.Data.SentBytes > 0) DrawParticles(dc, route, upload, uploadSpark, false);
            }
        double scale = MapOnly ? .5 : 1;
        foreach (var route in routes)
        {
            var projected = ConnectionProjection.Project(route.Data.Point, camera);
            if (!projected.Visible) continue;
            var center = Pixel(projected); if (!bounds.Contains(center)) continue;
            hitPoints.Add((center, route.Data.Key));
            bool highlight = IsHighlighted(route.Data), selected = route.Data.Selected || route.Data.Key == HoveredKey || highlight;
            double level = Math.Max(route.Activity.Download.Level, route.Activity.Upload.Level);
            double radius = (route.Radius + (selected ? 1.5 : 0)) * scale;
            if (subscribed) radius += .25 * scale * level * Math.Sin(phase * 2);
            bool receives = route.Data.ReceivedBytes > 0; var brush = receives ? download : upload; var halo = receives ? downloadHalo : uploadHalo;
            if (halo is not null) dc.DrawEllipse(halo, null, center, radius * 3, radius * 3);
            dc.DrawEllipse(brush, Stroke(ink, (selected ? 1.2 : .6) * scale), center, radius, radius);
            if (highlight) dc.DrawEllipse(null, Stroke(accent, scale), center, radius + 4 * scale, radius + 4 * scale);
            if (subscribed && level > 0)
            {
                double pulse = phase / 1.6 - Math.Floor(phase / 1.6);
                dc.PushOpacity(.35 * (1 - pulse)); dc.DrawEllipse(null, Stroke(brush, 1), center, radius + 6 * pulse * scale, radius + 6 * pulse * scale); dc.Pop();
            }
        }
        // A poll can move or drop the hovered node; the hover follows the pointer, never the key.
        if (hoverPosition is { } pointer && dragStart is null)
        {
            string? key = Hit(pointer)?.Key;
            if (key != HoveredKey) { HoveredKey = key; Cursor = key is not null ? Cursors.Hand : Cursors.Arrow; PointHovered?.Invoke(key); }
        }
        if (origin is not null)
        {
            var projected = ConnectionProjection.Project(origin, camera);
            if (projected.Visible)
            {
                var center = Pixel(projected); double s = MapOnly ? .6 : 1;
                if (glow) dc.DrawEllipse(Cached("origin-glow", () => new RadialGradientBrush(ColorOf("Glow"), Colors.Transparent)), null, center, 22 * s, 22 * s);
                dc.PushOpacity(.6); dc.DrawEllipse(null, Stroke(ink, 1), center, 8 * s, 8 * s); dc.Pop();
                dc.PushOpacity(.3); dc.DrawEllipse(null, Stroke(ink, 1), center, 13 * s, 13 * s); dc.Pop();
                if (subscribed)
                {
                    double pulse = phase / 2 - Math.Floor(phase / 2);
                    dc.PushOpacity(.5 * (1 - pulse)); dc.DrawEllipse(null, Stroke(ink, 1), center, (8 + 10 * pulse) * s, (8 + 10 * pulse) * s); dc.Pop();
                }
                dc.DrawEllipse(ink, null, center, 3.6 * s, 3.6 * s);
            }
        }
        dc.Pop();
        // Atmosphere sits just outside the limb; it needs the bounds clip only.
        if (glow && !Flat) dc.DrawEllipse(AtmosphereBrush(), null, globeCenter, globeRadius * 1.11, globeRadius * 1.11);
        dc.Pop();
        if (!MapOnly)
        {
            var chipFill = Theme.Brush("Surface1");
            double next = 8 + LegendChip(dc, 8, "Download", download, hairline, chipFill, ink) + 6;
            LegendChip(dc, next, "Upload", upload, hairline, chipFill, ink);
            if (HoveredKey is { } hoveredKey && hitPoints.FirstOrDefault(h => h.Key == hoveredKey) is { Key: not null } hit)
            {
                var data = points.FirstOrDefault(p => p.Key == hoveredKey);
                var lines = new List<(string Text, Brush Color)> { (data?.Label ?? hoveredKey, ink) };
                if (data?.Detail is { } detail) lines.Add((detail, muted));
                ChartFrame.Chip(dc, hit.Center, bounds, lines, Theme.ChartTypeface, VisualTreeHelper.GetDpi(this).PixelsPerDip, false);
            }
            Text(dc, StatusSummary, new(8, ActualHeight - 30), ActualWidth - 16, 28, muted);
        }
    }

    private (Point Center, string Key)? Hit(Point position)
    {
        double limit = MapOnly ? WidgetHitRadius : HitRadius, best = limit * limit; (Point Center, string Key)? hit = null;
        foreach (var candidate in hitPoints)
        {
            double distance = (candidate.Center - position).LengthSquared;
            if (distance <= best) { best = distance; hit = candidate; }
        }
        return hit;
    }
    /// <summary>Resolves the node under <paramref name="position"/> (null clears), raises <see cref="PointHovered"/> and sets the cursor.</summary>
    internal void SetHover(Point? position)
    {
        hoverPosition = position;
        string? key = position is { } p && dragStart is null ? Hit(p)?.Key : null;
        Cursor = dragStart is not null && dragged ? Cursors.SizeAll : key is not null ? Cursors.Hand : Cursors.Arrow;
        if (key == HoveredKey) return;
        HoveredKey = key; PointHovered?.Invoke(key); InvalidateVisual();
    }
    private void SelectAt(Point position)
    {
        if (Hit(position) is { } hit) PointSelected?.Invoke(hit.Key);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); dragStart = e.GetPosition(this); dragCamera = camera; dragged = false; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragStart is not Point start || dragCamera is null || e.LeftButton != MouseButtonState.Pressed) { SetHover(e.GetPosition(this)); return; }
        var delta = e.GetPosition(this) - start; if (delta.Length > 3) dragged = true;
        if (dragged)
        {
            Cursor = Cursors.SizeAll;
            ChangeCamera(dragCamera with { Longitude = dragCamera.Longitude - delta.X / Math.Max(1, MapBounds.Width) * 360 / camera.Zoom,
                Latitude = dragCamera.Latitude + delta.Y / Math.Max(1, MapBounds.Height) * 180 / camera.Zoom });
        }
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (dragStart is not null && !dragged) SelectAt(e.GetPosition(this));
        dragStart = null; dragCamera = null; ReleaseMouseCapture(); SetHover(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (dragStart is null) SetHover(null); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragStart = null; dragCamera = null; SetHover(hoverPosition); }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { base.OnMouseWheel(e); ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Home: ResetView(); break;
            case Key.Add: case Key.OemPlus: ZoomBy(1.2); break;
            case Key.Subtract: case Key.OemMinus: ZoomBy(1 / 1.2); break;
            case Key.Left: ChangeCamera(camera with { Longitude = camera.Longitude - 10 / camera.Zoom }); break;
            case Key.Right: ChangeCamera(camera with { Longitude = camera.Longitude + 10 / camera.Zoom }); break;
            case Key.Up: ChangeCamera(camera with { Latitude = camera.Latitude + 10 / camera.Zoom }); break;
            case Key.Down: ChangeCamera(camera with { Latitude = camera.Latitude - 10 / camera.Zoom }); break;
            default: return;
        }
        e.Handled = true;
    }
}
