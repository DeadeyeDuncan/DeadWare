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
    private GeoPoint? origin;
    private string status = "Waiting for connections";
    private bool available, enabled = true, loaded, subscribed, mapOnly;
    private Point? dragStart;
    private MapCamera? dragCamera;
    private bool dragged;
    private TimeSpan lastFrame;
    private double phase;
    private Size cachedSize;
    private MapCamera? cachedCamera;
    private StreamGeometry? coast, grid;
    private sealed record Route(ConnectionMapPoint Data,StreamGeometry? Arc,MapCoordinate[] Samples,double Radius,double Width);
    private readonly List<Route> routes = [];
    private readonly List<(Point Center, string Key)> hitPoints = [];
    private bool routesDirty = true;

    public ConnectionMap()
    {
        Focusable = true; ClipToBounds = true;
        Cursor = Cursors.Hand;
        ToolTip = "Drag to move · wheel or +/− to zoom · Home to reset · select a location";
        Loaded += (_, _) => { loaded = true; UpdateAnimation(); };
        Unloaded += (_, _) => { loaded = false; UpdateAnimation(); };
        IsVisibleChanged += (_, _) => UpdateAnimation();
        // Resource references invalidate the drawing when any theme or graph role changes.
        SetResourceReference(PaletteProperty, "Ink");
        SetResourceReference(DownloadProperty, Theme.GraphKey("download"));
        SetResourceReference(UploadProperty, Theme.GraphKey("upload"));
    }

    private static readonly DependencyProperty PaletteProperty = DependencyProperty.Register("Palette", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DownloadProperty = DependencyProperty.Register("Download", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty UploadProperty = DependencyProperty.Register("Upload", typeof(Brush), typeof(ConnectionMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Flat { get => camera.Flat; set { if (camera.Flat != value) ChangeCamera(camera with { Flat = value }); } }
    public bool MapOnly { get => mapOnly; set { if (mapOnly != value) { mapOnly = value; cachedCamera = null; routesDirty = true; InvalidateVisual(); } } }
    public bool AnimationEnabled { get => enabled; set { enabled = value; UpdateAnimation(); InvalidateVisual(); } }
    public bool IsAnimating => subscribed;
    public MapCamera Camera => camera;
    public int DisplayedPointCount => points.Count;
    public IReadOnlyList<ConnectionMapPoint> DisplayedPoints => points;
    public void SelectPoint(string? key)
    {
        points=Array.AsReadOnly(points.Select(p=>p with {Selected=p.Key==key}).ToArray());
        routesDirty=true; InvalidateVisual();
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
        var grouped = ConnectionProjection.Group(points);
        this.points = grouped.Points; OverflowCount = grouped.Overflow;
        this.origin = origin is not null && ConnectionProjection.IsValid(origin) ? origin : null;
        this.available = available; this.status = status.Length > 240 ? status[..240] : status;
        routesDirty = true;
        UpdateAnimation();
        AutomationProperties.SetName(this, "Connections map. " + StatusSummary);
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
        bool animate = loaded && IsVisible && enabled && available && points.Any(p=>p.ReceiveActive&&p.ReceivedBytes>0||p.SendActive&&p.SentBytes>0);
        if (animate == subscribed) return;
        subscribed = animate;
        if (animate) { lastFrame = TimeSpan.Zero; CompositionTarget.Rendering += Animate; }
        else CompositionTarget.Rendering -= Animate;
    }
    private void Animate(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || frame.RenderingTime - lastFrame < TimeSpan.FromSeconds(1d / 30)) return;
        lastFrame = frame.RenderingTime; phase = frame.RenderingTime.TotalSeconds; InvalidateVisual();
    }

    private Rect MapBounds => MapOnly ? new(3, 3, Math.Max(1, ActualWidth - 6), Math.Max(1, ActualHeight - 6)) :
        new(10, 21, Math.Max(1, ActualWidth - 20), Math.Max(1, ActualHeight - 55));
    private Point Pixel(MapCoordinate coordinate)
    {
        var bounds = MapBounds;
        double yScale = Flat ? Math.Min(bounds.Height / 2, bounds.Width / 4) : Math.Min(bounds.Width, bounds.Height) / 2;
        double xScale = Flat ? yScale * 2 : yScale;
        if (Flat && MapOnly) { xScale = bounds.Width / 2; yScale = bounds.Height / 2; }
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
            coast = Geometry(ConnectionAssets.Land); grid = Geometry(Graticules()); routesDirty = true;
        }
        if (!routesDirty) return;
        routes.Clear();
        double maximum=points.Select(p=>(double)p.ReceivedBytes+p.SentBytes).DefaultIfEmpty(0).Max();
        foreach (var point in points)
        {
            var samples = origin is null ? Array.Empty<GeoPoint>() : ConnectionProjection.GreatCircle(origin, point.Point);
            double relative=maximum>0?Math.Log(1+(double)point.ReceivedBytes+point.SentBytes)/Math.Log(1+maximum):0;
            routes.Add(new(point,samples.Count>0?Geometry([samples]):null,samples.Select(p=>ConnectionProjection.Project(p,camera)).ToArray(),3+relative*3,1+relative*1.5));
        }
        routesDirty = false;
    }
    private void DrawFlow(DrawingContext dc,Route route,Brush brush,bool received)
    {
        if(!subscribed||route.Samples.Length<2)return;
        double fraction=(phase/3)%1;if(fraction<0)fraction+=1;if(received)fraction=1-fraction;
        double position=fraction*(route.Samples.Length-1);int index=Math.Min(route.Samples.Length-2,(int)position);
        var a=route.Samples[index];var b=route.Samples[index+1];
        // Never interpolate a highlight through the rear hemisphere or across the flat seam.
        if(!a.Visible||!b.Visible)return;
        double progress=position-index,x=b.X;
        if(Flat&&Math.Abs(a.X-x)>camera.Zoom)x+=a.X>0?2*camera.Zoom:-2*camera.Zoom;
        x=a.X+(x-a.X)*progress;
        if(Flat){if(x>camera.Zoom)x-=2*camera.Zoom;else if(x< -camera.Zoom)x+=2*camera.Zoom;}
        var center=Pixel(new(x,a.Y+(b.Y-a.Y)*progress,1,true));
        dc.DrawEllipse(brush,null,center,2.2,2.2);
    }
    private static Pen Stroke(Brush brush, double width, bool dashed = false)
    {
        var pen = new Pen(brush, width); if (dashed) pen.DashStyle = DashStyles.Dash;
        if (pen.CanFreeze) pen.Freeze(); return pen;
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
        var ink = Theme.Brush("Ink"); var muted = Theme.Brush("Muted");
        var download = Theme.Brush(Theme.GraphKey("download")); var upload = Theme.Brush(Theme.GraphKey("upload"));
        dc.DrawRectangle(Theme.Brush("Background"), null, new Rect(RenderSize));
        if (!MapOnly) Text(dc, "↓ Download —   ↑ Upload ┄", new(8, 2), ActualWidth - 16, 17, muted);
        EnsureGeometry(); hitPoints.Clear();
        dc.PushClip(new RectangleGeometry(bounds));
        Geometry clip;
        if (Flat)
        {
            var topLeft = Pixel(new(-camera.Zoom, (camera.Latitude / 90 - 1) * camera.Zoom, 1, true));
            var bottomRight = Pixel(new(camera.Zoom, (camera.Latitude / 90 + 1) * camera.Zoom, 1, true));
            clip = new RectangleGeometry(new Rect(topLeft, bottomRight));
        }
        else
        {
            var center = Pixel(new(0, 0, 1, true)); double radius = Math.Min(bounds.Width, bounds.Height) / 2 * camera.Zoom;
            clip = new EllipseGeometry(center, radius, radius);
        }
        dc.DrawGeometry(Theme.Brush("Panel"), Stroke(Theme.Brush("Line"), 1), clip);
        dc.PushClip(clip);
        dc.DrawGeometry(null, Stroke(Theme.Brush("Line"), .6), grid);
        // Open coastlines avoid an invented closing chord across the horizon or seam.
        dc.DrawGeometry(null, Stroke(muted, 1), coast);
        foreach (var route in routes)
        {
            if (route.Arc is not null)
            {
                if (route.Data.Selected) dc.DrawGeometry(null, Stroke(ink, 4), route.Arc);
                if (route.Data.ReceivedBytes > 0) dc.DrawGeometry(null, Stroke(download, route.Width), route.Arc);
                if (route.Data.SentBytes > 0) dc.DrawGeometry(null, Stroke(upload, route.Width, true), route.Arc);
                if(route.Data.ReceiveActive&&route.Data.ReceivedBytes>0)DrawFlow(dc,route,download,true);
                if(route.Data.SendActive&&route.Data.SentBytes>0)DrawFlow(dc,route,upload,false);
            }
            var projected = ConnectionProjection.Project(route.Data.Point, camera);
            if (!projected.Visible) continue;
            var center = Pixel(projected); if (!bounds.Contains(center)) continue;
            hitPoints.Add((center, route.Data.Key));
            double radius = route.Radius+(route.Data.Selected?1.5:0);
            if(subscribed&&(route.Data.ReceiveActive||route.Data.SendActive))radius+=.25*Math.Sin(phase*2);
            dc.DrawEllipse(route.Data.ReceivedBytes > 0 ? download : upload, Stroke(ink, route.Data.Selected ? 1.5 : .7), center, radius, radius);
        }
        if (origin is not null)
        {
            var projected = ConnectionProjection.Project(origin, camera);
            if (projected.Visible)
            {
                var center = Pixel(projected);
                dc.DrawEllipse(Theme.Brush("Panel"), Stroke(ink, 1.3), center, 4, 4);
                dc.DrawLine(Stroke(ink, 1), new(center.X - 6, center.Y), new(center.X + 6, center.Y));
                dc.DrawLine(Stroke(ink, 1), new(center.X, center.Y - 6), new(center.X, center.Y + 6));
            }
        }
        dc.Pop(); dc.Pop();
        if (!MapOnly) Text(dc, StatusSummary, new(8, ActualHeight - 30), ActualWidth - 16, 28, muted);
    }

    private void SelectAt(Point position)
    {
        var hit = hitPoints.OrderBy(p => (p.Center - position).LengthSquared).FirstOrDefault();
        if (hit.Key is not null && (hit.Center - position).Length <= 10) PointSelected?.Invoke(hit.Key);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); dragStart = e.GetPosition(this); dragCamera = camera; dragged = false; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragStart is not Point start || dragCamera is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start; if (delta.Length > 3) dragged = true;
        if (dragged) ChangeCamera(dragCamera with { Longitude = dragCamera.Longitude - delta.X / Math.Max(1, MapBounds.Width) * 360 / camera.Zoom,
            Latitude = dragCamera.Latitude + delta.Y / Math.Max(1, MapBounds.Height) * 180 / camera.Zoom });
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e); if (dragStart is not null && !dragged) SelectAt(e.GetPosition(this));
        dragStart = null; dragCamera = null; ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); dragStart = null; dragCamera = null; }
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
