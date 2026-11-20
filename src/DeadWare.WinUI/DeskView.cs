using System.Numerics;
using DeadWare.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeadWare.WinUI;

/// <summary>
/// The Lighting page's desk, drawn with Win2D from <see cref="DeskGeometry.Scene"/>: the layout's desk rectangles as a muted outline,
/// the PC tower as a labelled box, a rounded box per placed device in the average of its zones' colours, and a dot per zone for a
/// device whose zones are shown separately. A tap selects an item, a tap on the tower's label opens or closes it, and a tap on empty
/// desk clears the selection. The desk keeps a 4:3 shape, filling its width up to 720 px, so desk units map to pixels the same way at
/// every size. Off screen it lets go of its drawing surface, as <see cref="ConnectionMapView"/> does. The canvas can't be read by a
/// screen reader; the page's device list is the accessible way to the same selection.
/// </summary>
internal sealed class DeskView : UserControl
{
    /// <summary>The value of <see cref="Selected"/> while the tower is selected.</summary>
    public const string Tower = "tower";
    private const double MaxDeskWidth = 720, Aspect = 0.75, BoxWidth = 132, BoxHeight = 44, DotSize = 14, LabelHeight = 20, LabelWidth = 160, ZoneLabelWidth = 96;
    private static readonly CanvasTextFormat NameFormat = Format(12, true);
    private static readonly CanvasTextFormat SmallFormat = Format(11, false);
    private static readonly CanvasStrokeStyle Dashed = new() { DashStyle = CanvasDashStyle.Dash };

    private readonly Grid surface = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    // Made when the desk shows and dropped when it hides, as the map does: a CanvasControl keeps its surface for as long as it exists.
    private CanvasControl? canvas;
    // Text laid out once per text, format and width rather than on every draw; let go with the device, on a resize and on hiding.
    private readonly Dictionary<(string Text, bool Name, float Width), CanvasTextLayout> texts = [];
    private DeskLayout layout = DeskLayouts.Empty();
    private IReadOnlyList<DeviceView> devices = [];
    private Dictionary<string, DeviceView> byKey = new(StringComparer.Ordinal);
    private Dictionary<string, string> names = new(StringComparer.Ordinal), zoneColors = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? colors;
    // The devices whose zones are drawn as separate dots. Empty until edit mode (Task 12) fills it; neither this nor towerOpen is saved.
    private readonly HashSet<string> split = new(StringComparer.Ordinal);
    private DeskScene scene;
    private Action<DeskLayout>? layoutEdited;
    private string? selected;
    private Point? pressedAt;
    private bool lightsOn = true, live, towerOpen;

    public DeskView()
    {
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(this, "Desk layout. Use the device list below to select a device.");
        var back = Caption("BACK OF DESK");
        var front = Caption("FRONT · YOUR SEAT");
        Content = new DeskFrame { Children = { back, surface, front } };
        scene = DeskGeometry.Scene(layout, devices, towerOpen, split);
        surface.PointerPressed += OnPointerPressed;
        surface.PointerReleased += OnPointerReleased;
        surface.PointerCaptureLost += (_, _) => pressedAt = null;
        surface.SizeChanged += (_, _) => { ClearTexts(); Redraw(); };
        ActualThemeChanged += (_, _) => Redraw();
        this.WhileShowing(ShowSurface, HideSurface);
    }

    /// <summary>A device key, <see cref="Tower"/>, or null. Setting it from outside raises no <see cref="SelectionChanged"/>.</summary>
    public string? Selected { get => selected; set => Select(value, false); }

    /// <summary>Edit layout is on. Task 12 gives it dragging, the desk shape and reset; until then it changes nothing.</summary>
    public bool Editing { get; set; }

    /// <summary>The user selected an item on the desk, or cleared the selection with a tap on empty desk.</summary>
    public event Action<string?>? SelectionChanged;

    /// <summary>One per completed edit in edit layout (Task 12).</summary>
    public event Action<DeskLayout>? LayoutEdited { add => layoutEdited += value; remove => layoutEdited -= value; }

    /// <summary>The tower's caption: "PC tower · 1 device", "PC tower · 3 devices".</summary>
    public static string TowerCaption(int count) => count == 1 ? "PC tower · 1 device" : $"PC tower · {count} devices";

    /// <summary>
    /// Shows a layout and the devices it places. <paramref name="lightsOn"/> false draws every box unlit; <paramref name="live"/> false
    /// draws the desk dimmed and ignores taps, as while the lighting host isn't running.
    /// </summary>
    public void Show(DeskLayout layout, IReadOnlyList<DeviceView> devices, bool lightsOn, bool live)
    {
        this.layout = layout;
        this.devices = devices;
        this.lightsOn = lightsOn;
        this.live = live;
        if (!live) pressedAt = null;
        var list = devices.ToArray();
        var numbered = LightingPage.Names(list);
        byKey = new(StringComparer.Ordinal);
        names = new(StringComparer.Ordinal);
        zoneColors = new(StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i]?.Key is not { } key || !byKey.TryAdd(key, list[i])) continue;
            names[key] = numbered[i];
            foreach (var zone in list[i].Zones ?? []) if (zone?.Key is { } zoneKey) zoneColors[zoneKey] = zone.Color;
        }
        Rebuild();
    }

    /// <summary>The latest preview frame, zone key to #RRGGBB; null uses each zone's colour from the state.</summary>
    public void ShowColors(IReadOnlyDictionary<string, string>? colors)
    {
        if (colors is null && this.colors is null) return;
        this.colors = colors;
        Redraw();
    }

    private static CanvasTextFormat Format(float size, bool strong) => new()
    {
        FontSize = size, FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
    };

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, CharacterSpacing = 60 };

    private void Rebuild()
    {
        scene = DeskGeometry.Scene(layout, devices, towerOpen, split);
        Redraw();
    }

    private void Redraw() => canvas?.Invalidate();

    private void Select(string? key, bool raise)
    {
        // Selecting a device inside the tower opens it, so the selection shows.
        if (key is not null && InTower(key) && !towerOpen)
        {
            towerOpen = true;
            Rebuild();
        }
        if (key == selected) return;
        selected = key;
        Redraw();
        if (raise) SelectionChanged?.Invoke(key);
    }

    private bool InTower(string key) => key != Tower && layout.Tower.Members.Contains(key) && !layout.Unplaced.Contains(key);

    // ---- Geometry: desk units (the unit square) to pixels on the surface. ----

    private double W => surface.ActualWidth;
    private double H => surface.ActualHeight;
    private bool TooSmall => W < 160 || H < 100;
    /// <summary>The device boxes narrow with a narrow desk, down to 60% of their full width.</summary>
    private double BoxW => BoxWidth * Math.Clamp(W / MaxDeskWidth, 0.6, 1);
    // A box centred on the unit square's edge still fits, and the tower's label fits above a tower at the back edge.
    private double InsetX => BoxW / 2 + 4;
    private double InsetY => Math.Max(BoxHeight / 2 + 4, LabelHeight + 6);
    private double SpanX => Math.Max(1, W - 2 * InsetX);
    private double SpanY => Math.Max(1, H - 2 * InsetY);
    private Vector2 ToPixel(double x, double y) => new((float)(InsetX + x * SpanX), (float)(InsetY + y * SpanY));
    private DeskPoint ToDesk(Point point) => new((point.X - InsetX) / SpanX, (point.Y - InsetY) / SpanY);

    private Rect TowerBox()
    {
        var (a, b) = (ToPixel(scene.Tower.X0, scene.Tower.Y0), ToPixel(scene.Tower.X1, scene.Tower.Y1));
        return new Rect(a.X, a.Y, Math.Max(1, b.X - a.X), Math.Max(1, b.Y - a.Y));
    }

    /// <summary>The tower's label: a tab on top of its box, kept inside the surface. A tap on it opens or closes the tower.</summary>
    private Rect TowerLabel()
    {
        var box = TowerBox();
        double width = Math.Max(1, Math.Min(LabelWidth, W - 4));
        double x = Math.Clamp(box.X, 2, Math.Max(2, W - width - 2)), y = Math.Max(0, box.Y - LabelHeight - 2);
        return new Rect(x, y, width, LabelHeight);
    }

    /// <summary>The item under a point: zone dots first with their own size, then devices with theirs, then the tower by its box.</summary>
    private DeskItem? HitAt(DeskPoint at)
    {
        var zones = scene.Items.Where(item => item.Kind == DeskItemKind.Zone).ToArray();
        // No tower in the zones' pass, so the tower's box can't take a tap meant for a device drawn over it.
        if (zones.Length > 0 && DeskGeometry.Hit(scene with { Items = zones, Tower = new DeskBox(2, 2, 2, 2) }, at, DotSize / 2 / SpanX, DotSize / 2 / SpanY) is { } zone) return zone;
        return DeskGeometry.Hit(scene with { Items = scene.Items.Where(item => item.Kind != DeskItemKind.Zone).ToArray() }, at, BoxW / 2 / SpanX, BoxHeight / 2 / SpanY);
    }

    // ---- Input ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(surface);
        if (!live || TooSmall || !point.Properties.IsLeftButtonPressed) return;
        pressedAt = point.Position;
        surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (pressedAt is not { } start) return;
        pressedAt = null;
        surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!live || TooSmall) return;
        var end = e.GetCurrentPoint(surface).Position;
        // A movement under the drag threshold is a tap.
        double dx = (end.X - start.X) / SpanX, dy = (end.Y - start.Y) / SpanY;
        if (Math.Sqrt(dx * dx + dy * dy) < DeskGeometry.DragEpsilon) Tap(end);
    }

    private void Tap(Point at)
    {
        if (TowerLabel().Contains(at))
        {
            towerOpen = !towerOpen;
            Rebuild();
            return;
        }
        string? key = HitAt(ToDesk(at)) switch
        {
            { Kind: DeskItemKind.Tower } => Tower,
            { DeviceKey: { } device } => device,
            _ => null,
        };
        Select(key, true);
    }

    // ---- Surface lifetime ----

    private void ShowSurface()
    {
        if (canvas is null)
        {
            canvas = new CanvasControl();
            canvas.Draw += Draw;
            canvas.CreateResources += OnCreateResources;
            surface.Children.Insert(0, canvas);
        }
        Redraw();
    }

    private void HideSurface()
    {
        pressedAt = null;
        ClearTexts();
        if (canvas is { } old)
        {
            canvas = null;
            old.Draw -= Draw;
            old.CreateResources -= OnCreateResources;
            // As Win2D advises: out of the tree and no references left, so the collector can free the control and its surface.
            old.RemoveFromVisualTree();
        }
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) => ClearTexts();

    private void ClearTexts()
    {
        foreach (var text in texts.Values) text.Dispose();
        texts.Clear();
    }

    /// <summary>Text laid out for one line of an explicit, positive width, trimmed with an ellipsis when it doesn't fit.</summary>
    private CanvasTextLayout Text(ICanvasResourceCreator creator, string text, bool name, double width)
    {
        float w = (float)Math.Max(1, width);
        if (texts.TryGetValue((text, name, w), out var found)) return found;
        if (texts.Count > 512) ClearTexts();
        var made = new CanvasTextLayout(creator, text, name ? NameFormat : SmallFormat, w, (float)LabelHeight);
        texts[(text, name, w)] = made;
        return made;
    }

    // ---- Drawing ----

    // An exception that escapes a Win2D draw callback ends the process, so a drawing fault is recorded and that frame is skipped.
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try { DrawDesk(sender, args.DrawingSession); }
        catch (Exception error) when (error is not OutOfMemoryException) { App.Note($"The desk could not be drawn: {error}"); }
    }

    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255), color.R, color.G, color.B);

    private void DrawDesk(CanvasControl sender, CanvasDrawingSession ds)
    {
        if (TooSmall) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)255 : (byte)0;
        Color ink = Color.FromArgb(230, c, c, c), muted = Color.FromArgb(150, c, c, c), hairline = Color.FromArgb(70, c, c, c), wash = Color.FromArgb(14, c, c, c);
        Color accent = UsageColors.Accent;
        // While the host isn't running the desk stays as it was, dimmed.
        using var dim = ds.CreateLayer(live ? 1f : 0.5f);

        foreach (var rect in layout.Desk ?? [])
        {
            if (rect is null) continue;
            var (a, b) = (ToPixel(rect.X, rect.Y), ToPixel(rect.X + rect.W, rect.Y + rect.H));
            ds.FillRoundedRectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y, 6, 6, wash);
            ds.DrawRoundedRectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y, 6, 6, hairline, 1);
        }

        // The tower under everything else, as Hit takes it last. It shows selected while it is, or while a device inside it is and it is closed.
        var box = TowerBox();
        bool towerSelected = selected == Tower || (!towerOpen && selected is { } key && InTower(key) && byKey.ContainsKey(key));
        ds.FillRoundedRectangle(box, 6, 6, Alpha(ink, towerOpen ? 0.03 : 0.07));
        ds.DrawRoundedRectangle(box, 6, 6, towerSelected ? accent : muted, towerSelected ? 2 : 1, Dashed);
        var label = TowerLabel();
        ds.FillRoundedRectangle(label, 4, 4, Alpha(ink, 0.08));
        ds.DrawRoundedRectangle(label, 4, 4, towerSelected ? accent : hairline, 1);
        var caption = Text(sender, DeskView.TowerCaption(scene.TowerDevices), false, label.Width - 12);
        ds.DrawTextLayout(caption, (float)label.X + 6, (float)(label.Y + (label.Height - caption.LayoutBounds.Height) / 2), ink);

        foreach (var item in scene.Items) if (item.Kind == DeskItemKind.Device) DrawDevice(sender, ds, item, ink, muted, hairline, accent);
        foreach (var item in scene.Items) if (item.Kind == DeskItemKind.Zone) DrawZone(sender, ds, item, ink, muted, hairline, accent);
    }

    private void DrawDevice(ICanvasResourceCreator creator, CanvasDrawingSession ds, DeskItem item, Color ink, Color muted, Color hairline, Color accent)
    {
        var center = ToPixel(item.At.X, item.At.Y);
        float w = (float)BoxW, h = (float)BoxHeight, x = center.X - w / 2, y = center.Y - h / 2;
        var rect = new Rect(x, y, w, h);
        if (!lightsOn) { ds.FillRoundedRectangle(rect, 8, 8, Alpha(ink, 0.05)); ds.DrawRoundedRectangle(rect, 8, 8, hairline, 1); }
        else if (item.OwnEffect) { ds.FillRoundedRectangle(rect, 8, 8, Alpha(muted, 0.12)); ds.DrawRoundedRectangle(rect, 8, 8, muted, 1); }
        else
        {
            var color = Average(item.ZoneKeys) ?? muted;
            ds.FillRoundedRectangle(rect, 8, 8, Alpha(color, 0.22));
            ds.DrawRoundedRectangle(rect, 8, 8, color, 1.5f);
        }
        if (item.DeviceKey is { } key && key == selected) ds.DrawRoundedRectangle(x - 3, y - 3, w + 6, h + 6, 10, 10, accent, 2);
        string name = item.DeviceKey is { } k && names.TryGetValue(k, out var numbered) ? numbered : item.Label;
        double inner = w - 20;
        ds.DrawTextLayout(Text(creator, name, true, inner), x + 10, y + 5, ink);
        // The zones the desk can place: those with LEDs, each key once.
        int zones = item.DeviceKey is { } d && byKey.TryGetValue(d, out var device)
            ? (device.Zones ?? []).Where(zone => zone is { Key: not null, LedCount: > 0 }).Select(zone => zone.Key).Distinct(StringComparer.Ordinal).Count()
            : item.ZoneKeys.Count;
        string count = zones == 1 ? "1 zone" : $"{zones} zones";
        if (!item.OwnEffect) { ds.DrawTextLayout(Text(creator, count, false, inner), x + 10, y + 23, muted); return; }
        // A device running its own effect: its zone count, then the tag, each in half the line.
        ds.DrawTextLayout(Text(creator, count, false, inner / 2 - 2), x + 10, y + 23, muted);
        var tag = Text(creator, "own effect", false, inner / 2 - 2);
        ds.DrawTextLayout(tag, (float)(x + 10 + inner - Math.Min(tag.LayoutBounds.Width, inner / 2 - 2)), y + 23, ink);
    }

    private void DrawZone(ICanvasResourceCreator creator, CanvasDrawingSession ds, DeskItem item, Color ink, Color muted, Color hairline, Color accent)
    {
        var center = ToPixel(item.At.X, item.At.Y);
        float radius = (float)(DotSize / 2);
        var fill = !lightsOn ? Alpha(ink, 0.08) : item.OwnEffect ? Alpha(muted, 0.4) : Average(item.ZoneKeys) ?? muted;
        ds.FillCircle(center, radius, fill);
        ds.DrawCircle(center, radius, hairline, 1);
        if (item.DeviceKey is { } key && key == selected) ds.DrawCircle(center, radius + 3, accent, 2);
        var label = Text(creator, item.Label, false, ZoneLabelWidth);
        ds.DrawTextLayout(label, center.X + radius + 4, (float)(center.Y - label.LayoutBounds.Height / 2), ink);
    }

    /// <summary>The average of the zones' colours: each from the latest preview frame when it has the zone, else from the state.</summary>
    private Color? Average(IReadOnlyList<string> zoneKeys)
    {
        int r = 0, g = 0, b = 0, n = 0;
        foreach (string key in zoneKeys)
        {
            string? hex = colors is not null && colors.TryGetValue(key, out var shown) ? shown : zoneColors.GetValueOrDefault(key);
            if (!Rgb.TryParse(hex, out var rgb)) continue;
            r += rgb.R; g += rgb.G; b += rgb.B; n++;
        }
        return n == 0 ? null : Color.FromArgb(255, (byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>
    /// The desk's frame: "BACK OF DESK", the drawing surface and "FRONT · YOUR SEAT", one above the other. The surface is 4:3 and as
    /// wide as the space allows up to 720 px, sized here in layout so nothing sets a size from a size change.
    /// </summary>
    private sealed class DeskFrame : Panel
    {
        private const double Gap = 4;

        private static double DeskWidth(double available) => double.IsFinite(available) && available >= 0 ? Math.Min(available, MaxDeskWidth) : MaxDeskWidth;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = DeskWidth(availableSize.Width), height = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                child.Measure(i == 1 ? new Size(width, width * Aspect) : new Size(width, double.PositiveInfinity));
                height += (i == 1 ? width * Aspect : child.DesiredSize.Height) + (i > 0 ? Gap : 0);
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = DeskWidth(finalSize.Width), y = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (i > 0) y += Gap;
                double height = i == 1 ? width * Aspect : child.DesiredSize.Height;
                child.Arrange(new Rect(0, y, width, height));
                y += height;
            }
            return new Size(width, y);
        }
    }
}
