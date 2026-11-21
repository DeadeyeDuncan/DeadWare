using System.Numerics;
using DeadWare.App;
using DeadWare.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
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
/// The Lighting page's desk as a picture of one known rig (<see cref="DeskPictures.OwnerRig"/>), drawn with Win2D: the desk seen from
/// above with its furniture, keyboard, mouse and PC tower, or the inside of the tower through its side window. Everything is drawn in a
/// box 720 wide by 450 high, the numbers of the approved mockups, scaled uniformly to the control's width (at most 1280 px) and centred;
/// a tap converts back to box units with the same scale, so <see cref="DeskPictures.Hit"/> and <see cref="DeskPictures.HitTower"/>
/// take it as it is. Each lit part shows its colour with a soft glow of that colour; the stage stays dark in both themes, because it
/// shows lights. Nothing moves by itself: the view draws again only when it is given something new. Off screen it lets go of its
/// drawing surface, as <see cref="DeskView"/> does. The drawing is one tab stop: the arrow keys move the selection through the found parts
/// of the view in the picture's own order (written top to bottom, left to right), the PC tower being the last stop of the desk view;
/// Enter or Space on the tower opens the inside view, and Escape in the inside view asks for the desk. The control's name says which
/// drawing it is and what is selected; the canvas itself can't be read by a screen reader. The third view (lighting 3b) is one keyboard
/// drawn large at its keys' real shapes (<see cref="KeyShapes"/>), in the box's middle with its aspect kept, each key in its live colour
/// with its label; while <see cref="PickingKeys"/> a press, a drag and a release on its keys are raised as <see cref="KeyPickDown"/>,
/// <see cref="KeyPickOver"/> and <see cref="KeyPickUp"/>, and the keys in <see cref="PickedKeys"/> have an accent outline.
/// </summary>
internal sealed class DeskPictureView : UserControl
{
    private const double BoxW = 720, BoxH = 450, MaxDrawWidth = 1280, GlowDeviation = 5, GlowMargin = 0.4;
    private const float NoteSize = 9;
    // The keyboard view: the box less a margin, the keys fitted into it; each key shrunk by KeyGap key units on every side (the shapes
    // touch), its corners rounded by KeyRound key units, and its picked outline PickedStroke pixels wide whatever the scale.
    private const double KeysMargin = 20, KeyGap = 0.06, KeyRound = 0.12, PickedStroke = 3;
    private const string OwnEffectNote = "own effect", NotFound = "not found";

    // The mockups' colours (page.html). The stage is the card the mockup's drawings sit on.
    private static readonly Color Stage = Hex(0x111722), Wood = Hex(0x2B211A), WoodEdge = Hex(0x4B3A2B), Furniture = Hex(0x3A4556), FurnitureLight = Hex(0x9AA6B6);
    private static readonly Color Labels = Hex(0xC9D3E0), MutedLabels = Hex(0x7C899B), Accent = Hex(0x5EE0FF), Ink = Hex(0xE6EDF6), BackInk = Hex(0x5D6B80), Quiet = Hex(0x6C7A8F);
    private static readonly Color WaveBlue = Hex(0x4FD8FF), WaveCaption = Hex(0x6FB9CC), MatFill = Hex(0x1A1512), MatEdge = Hex(0x3B2E23), YouFill = Hex(0x121A27), YouEdge = Hex(0x263247);
    private static readonly Color TowerFill = Hex(0x0C1A26), CaseFill = Hex(0x090D14), CaseEdge = Hex(0x33415A), CaptionGrey = Hex(0x8B98AB), BoardFill = Hex(0x0E131C), BoardEdge = Hex(0x232F42);
    private static readonly Color PumpFill = Hex(0x0B1118), CardFill = Hex(0x141B27), CardEdge = Hex(0x2B384D), PsuFill = Hex(0x0D121A), PsuEdge = Hex(0x1E2939), PsuInk = Hex(0x465367);
    // An unlit part is outlined in the mockup's unlit rear-fan colour; a part running its own effect is drawn in the mockup's front-fan grey.
    private static readonly Color Unlit = Hex(0x33415A), Grey = Hex(0x5B6778), White = Hex(0xFFFFFF), Black = Hex(0x000000);
    private static readonly CanvasStrokeStyle RoundCaps = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    // SVG's stroke-dasharray "9 5" on a 2-wide stroke; Win2D counts dashes in stroke widths.
    private static readonly CanvasStrokeStyle KeyRows = new() { CustomDashStyle = [4.5f, 2.5f] };

    private readonly Grid surface = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    // Made when the picture shows and dropped when it hides, as the desk does: a CanvasControl keeps its surface for as long as it exists.
    private CanvasControl? canvas;
    // Text laid out once per text and format, in box units, so a resize keeps it; let go with the device and on hiding.
    private readonly record struct TextKey(string Text, float Size, bool Bold, CanvasHorizontalAlignment Align, float Spacing, float Width);
    private readonly Dictionary<TextKey, CanvasTextLayout> texts = [];
    private PictureScene? scene;
    private PreviewColors? frame;
    // Device key to its key map, for a device whose lights the keyboard part draws one by one.
    private IReadOnlyDictionary<string, KeyMapView> keyMaps = new Dictionary<string, KeyMapView>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> stateColors = new Dictionary<string, string>(StringComparer.Ordinal);
    private PictureView view = PictureView.Desk;
    // The keyboard view: the device it draws, the keys picked, whether the pointer picks keys, and a drag in progress (the key it is on).
    private string? keysDevice;
    private IReadOnlySet<int>? pickedKeys;
    private bool pickingKeys, keyDragging;
    private int? dragKey;
    // The shapes of the last key map drawn, made again only when the map changes.
    private (KeyMapView Map, KeyShapeSet? Set)? keyShapes;
    private string? selected, shownName;
    private bool lightsOn = true, live, waveRunning;
    // The keyboard's stop is the PC tower (desk view only), which is not a part and so never the selection; keyboardFocus: the control
    // has focus from the keyboard, the only time the tower's stop is drawn.
    private bool towerStop, keyboardFocus;
    private double waveAngle;

    // How a part shows: in its colour, grey while it runs its own effect, unlit, or not found at all.
    private enum Look { Lit, Own, Unlit, Missing }
    private enum Anchor { Start, Middle, End }

    public DeskPictureView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        UpdateName();
        Content = new StagePanel { Children = { surface } };
        surface.Tapped += OnTapped;
        surface.PointerPressed += OnPointerPressed;
        surface.PointerMoved += OnPointerMoved;
        surface.PointerReleased += (_, e) => EndKeyDrag(e.Pointer);
        surface.PointerCanceled += (_, e) => EndKeyDrag(e.Pointer);
        surface.PointerCaptureLost += (_, _) => EndKeyDrag(null);
        surface.SizeChanged += (_, _) => Redraw();
        GotFocus += (_, _) => SetKeyboardFocus(FocusState == FocusState.Keyboard);
        LostFocus += (_, _) => SetKeyboardFocus(false);
        this.WhileShowing(ShowSurface, HideSurface);
    }

    /// <summary>Which drawing shows. Setting it raises nothing.</summary>
    public PictureView View
    {
        get => view;
        set
        {
            if (view == value) return;
            // A drag on the keyboard ends with its view.
            if (view == PictureView.Keys) EndKeyDrag(null);
            view = value;
            towerStop = false;
            UpdateName();
            Redraw();
        }
    }

    /// <summary>The device the keyboard view draws, by key; its key map comes from <see cref="SetKeyMaps"/>. Setting it raises nothing but a drag's end.</summary>
    public string? KeysDevice
    {
        get => keysDevice;
        set { if (keysDevice == value) return; EndKeyDrag(null); keysDevice = value; Redraw(); }
    }

    /// <summary>
    /// The keyboard view's picked keys, by light index, drawn with an accent outline while <see cref="PickingKeys"/>. Every set redraws,
    /// so the same set, changed in place, can be given again.
    /// </summary>
    public IReadOnlySet<int>? PickedKeys
    {
        get => pickedKeys;
        set { pickedKeys = value; UpdateName(); Redraw(); }
    }

    /// <summary>Whether the pointer picks keys in the keyboard view. Turning it off ends a drag in progress.</summary>
    public bool PickingKeys
    {
        get => pickingKeys;
        set { if (pickingKeys == value) return; if (!value) EndKeyDrag(null); pickingKeys = value; UpdateName(); Redraw(); }
    }

    /// <summary>A press on a key of the keyboard view while picking: the light it lights.</summary>
    public event Action<int>? KeyPickDown;

    /// <summary>The pointer, still pressed after <see cref="KeyPickDown"/>, moved onto another key of the keyboard view.</summary>
    public event Action<int>? KeyPickOver;

    /// <summary>
    /// The press that raised <see cref="KeyPickDown"/> is over: released, cancelled or its capture lost, or the view, the device or picking
    /// changed under it. Raised once per <see cref="KeyPickDown"/>.
    /// </summary>
    public event Action? KeyPickUp;

    /// <summary>A part id, or null. Setting it raises nothing.</summary>
    public string? SelectedPart
    {
        get => selected;
        set { if (selected == value) return; selected = value; if (value is not null) towerStop = false; UpdateName(); Redraw(); }
    }

    /// <summary>A tap on a found part, or null for a tap on empty space, furniture or a part that isn't found.</summary>
    public event Action<ResolvedPart?>? PartTapped;

    /// <summary>A tap on the PC tower in the desk view, or Enter or Space with the keyboard's stop on it.</summary>
    public event Action? TowerTapped;

    /// <summary>Escape in the inside view: the desk view is asked for.</summary>
    public event Action? DeskRequested;

    /// <summary>
    /// Shows the picture's parts as resolved. <paramref name="lightsOn"/> false draws every part unlit; <paramref name="live"/> false
    /// draws the whole picture dimmed and ignores taps. While <paramref name="waveRunning"/> the desk view shows the wave's band across
    /// the middle, at right angles to <paramref name="waveAngleDeg"/>, with an arrow and the direction's name.
    /// </summary>
    public void Show(PictureScene scene, bool lightsOn, bool live, bool waveRunning, double waveAngleDeg)
    {
        this.scene = scene;
        this.lightsOn = lightsOn;
        this.live = live;
        this.waveRunning = waveRunning;
        waveAngle = double.IsFinite(waveAngleDeg) ? waveAngleDeg : 0;
        UpdateName();
        Redraw();
    }

    /// <summary>The latest preview frame (zone colours and per-light colours); null uses the colours given by <see cref="SetStateColors"/>.</summary>
    public void ShowColors(PreviewColors? colors)
    {
        if (colors is null && frame is null) return;
        frame = colors;
        Redraw();
    }

    /// <summary>Device key to its key map, for the devices that have one. The keyboard part draws its keys when its device is here.</summary>
    public void SetKeyMaps(IReadOnlyDictionary<string, KeyMapView> maps)
    {
        keyMaps = maps;
        Redraw();
    }

    /// <summary>Zone key to the state's colour, used when no frame has the zone.</summary>
    public void SetStateColors(IReadOnlyDictionary<string, string> colors)
    {
        stateColors = colors;
        Redraw();
    }

    private void Redraw() => canvas?.Invalidate();

    /// <summary>
    /// "Desk picture" or "Inside the PC picture"; with a part selected "Desk picture. Keyboard, SteelSeries Apex Pro."; with the keyboard's
    /// stop on the tower "Desk picture. PC tower."
    /// </summary>
    private void UpdateName()
    {
        var part = selected is null ? null : scene?.Parts.FirstOrDefault(p => p.Part.Id == selected);
        string what = view switch { PictureView.Inside => "Inside the PC picture", PictureView.Keys => "Keyboard picture", _ => "Desk picture" };
        string name = view == PictureView.Keys ? (pickingKeys ? $"{what}. {pickedKeys?.Count ?? 0} keys picked." : what) : part is not null ? $"{what}. {part.Part.Label}, {part.Part.DeviceName}." : towerStop ? $"{what}. PC tower." : what;
        if (name == shownName) return;
        shownName = name;
        AutomationProperties.SetName(this, name);
    }

    // ---- Geometry: one scale from the 720 by 450 box to pixels on the surface, for drawing and taps alike. ----

    private double BoxScale => surface.ActualWidth / BoxW;
    private bool TooSmall => surface.ActualWidth < 100;

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // The keyboard view takes presses, not taps.
        if (view == PictureView.Keys || !live || scene is not { } shown || TooSmall) return;
        e.Handled = true;
        // The arrow keys carry on from what was tapped.
        Focus(FocusState.Pointer);
        SetTowerStop(false);
        var at = e.GetPosition(surface);
        double s = BoxScale, x = at.X / s, y = at.Y / s;
        // Core decides what a tap hits in the view showing: a part first, then the tower, which only the desk view has.
        if (DeskPictures.Hit(shown, view, x, y) is { } part) PartTapped?.Invoke(part);
        else if (DeskPictures.HitTower(shown.Picture, view, x, y)) TowerTapped?.Invoke();
        else PartTapped?.Invoke(null);
    }

    // ---- The keyboard view's pointer: a press on a key, a drag across keys, a release ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (view != PictureView.Keys || !pickingKeys || !live || keyDragging || TooSmall) return;
        if (KeyAt(e.GetCurrentPoint(surface).Position) is not { } light) return;
        e.Handled = true;
        Focus(FocusState.Pointer);
        // Captured, so the drag goes on across the gaps and outside the keys, and its release comes here wherever it happens.
        keyDragging = surface.CapturePointer(e.Pointer);
        if (!keyDragging) return;
        dragKey = light;
        KeyPickDown?.Invoke(light);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!keyDragging) return;
        e.Handled = true;
        if (KeyAt(e.GetCurrentPoint(surface).Position) is not { } over || over == dragKey) return;
        dragKey = over;
        KeyPickOver?.Invoke(over);
    }

    /// <summary>Ends a drag on the keyboard, if one is in progress, and raises <see cref="KeyPickUp"/> once.</summary>
    private void EndKeyDrag(Pointer? pointer)
    {
        if (!keyDragging) return;
        // Cleared first: letting go of the capture raises PointerCaptureLost, which comes back here.
        keyDragging = false;
        dragKey = null;
        if (pointer is not null) surface.ReleasePointerCapture(pointer);
        else surface.ReleasePointerCaptures();
        KeyPickUp?.Invoke();
    }

    /// <summary>The light whose key is under a point on the surface, or null.</summary>
    private int? KeyAt(Point at)
    {
        if (KeyFit() is not { } fit) return null;
        double s = BoxScale;
        return KeyShapes.Hit(fit.Set, (at.X / s - fit.X) / fit.Scale, (at.Y / s - fit.Y) / fit.Scale);
    }

    /// <summary>The keyboard view's key map, from the maps given, or null when its device has none.</summary>
    private KeyMapView? KeysMap() => keysDevice is { } key && keyMaps.TryGetValue(key, out var map) ? map : null;

    /// <summary>
    /// The keyboard view's shapes and where they go in box units: key units times Scale, from (X, Y), fitted into the box less its margin
    /// with the aspect kept and centred. Null without a key map or without shapes.
    /// </summary>
    private (KeyShapeSet Set, double Scale, double X, double Y)? KeyFit()
    {
        if (KeysMap() is not { } map) return null;
        if (keyShapes is not { } cached || !ReferenceEquals(cached.Map, map)) keyShapes = cached = (map, KeyShapes.Of(map));
        if (cached.Set is not { Keys.Count: > 0 } set || set.Width <= 0 || set.Height <= 0) return null;
        double w = BoxW - 2 * KeysMargin, h = BoxH - 2 * KeysMargin, scale = Math.Min(w / set.Width, h / set.Height);
        return (set, scale, KeysMargin + (w - set.Width * scale) / 2, KeysMargin + (h - set.Height * scale) / 2);
    }

    // ---- Keyboard: one tab stop; the arrow keys step through the found parts, and the tower in the desk view. ----

    private void SetKeyboardFocus(bool on)
    {
        if (keyboardFocus == on) return;
        keyboardFocus = on;
        Redraw();
    }

    /// <summary>Forgets the keyboard's stop on the PC tower, for when the page resets the picture's view.</summary>
    public void ClearStop() => SetTowerStop(false);

    private void SetTowerStop(bool on)
    {
        if (towerStop == on) return;
        towerStop = on;
        UpdateName();
        Redraw();
    }

    /// <summary>
    /// Left and Up go to the stop before, Right and Down to the one after; the first arrow key goes to the first stop (or, going back, the
    /// last). The stops are the view's found parts in the picture's order, then the tower in the desk view. A part is selected as a tap
    /// selects it; the tower is only stopped at, with nothing selected, and Enter or Space there opens the inside view. Escape in the
    /// inside view asks for the desk, with the stop on the tower it came from. Nothing while the picture is dimmed, as for taps.
    /// </summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (live && scene is { } shown && HandleKey(shown, e.Key)) e.Handled = true;
        else base.OnKeyDown(e);
    }

    private bool HandleKey(PictureScene shown, Windows.System.VirtualKey key)
    {
        switch (key)
        {
            case Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up: Step(shown, -1); return true;
            case Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down: Step(shown, 1); return true;
            case Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space:
                // On a part there is nothing to do: the arrow key that reached it selected it.
                if (view != PictureView.Desk || !towerStop) return selected is not null;
                TowerTapped?.Invoke();
                return true;
            case Windows.System.VirtualKey.Escape:
                if (view != PictureView.Inside) return false;
                DeskRequested?.Invoke();
                if (view == PictureView.Desk) SetTowerStop(true);
                return true;
            default: return false;
        }
    }

    private void Step(PictureScene shown, int by)
    {
        var stops = shown.Parts.Where(p => p.Found && p.Part.View == view).ToList();
        int count = stops.Count + (view == PictureView.Desk ? 1 : 0);
        if (count == 0) return;
        int at = towerStop && view == PictureView.Desk ? stops.Count : stops.FindIndex(p => p.Part.Id == selected);
        int next = at < 0 ? (by > 0 ? 0 : count - 1) : Math.Clamp(at + by, 0, count - 1);
        if (next == at) return;
        if (next < stops.Count)
        {
            // The page selects it, which takes the stop off the tower (SelectedPart).
            PartTapped?.Invoke(stops[next]);
            return;
        }
        // The tower: nothing is selected while the stop is on it.
        if (selected is not null) PartTapped?.Invoke(null);
        SetTowerStop(true);
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

    /// <summary>One line of text in box units, in an explicit positive width, trimmed with an ellipsis when it doesn't fit.</summary>
    private CanvasTextLayout Text(ICanvasResourceCreator creator, string text, float size, bool bold, Anchor anchor, float spacing, double width)
    {
        var align = anchor switch { Anchor.Middle => CanvasHorizontalAlignment.Center, Anchor.End => CanvasHorizontalAlignment.Right, _ => CanvasHorizontalAlignment.Left };
        var key = new TextKey(text, size, bold, align, spacing, (float)Math.Max(1, width));
        if (texts.TryGetValue(key, out var found)) return found;
        if (texts.Count > 256) ClearTexts();
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI", FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis,
            HorizontalAlignment = align,
        };
        var made = new CanvasTextLayout(creator, text, format, key.Width, size * 2);
        // SVG's letter-spacing: extra space after every character.
        if (spacing != 0) made.SetCharacterSpacing(0, text.Length, 0, spacing, 0);
        texts[key] = made;
        return made;
    }

    /// <summary>Where a layout of the given width is drawn so its text sits as SVG's text at (x, baseline) with that anchor.</summary>
    private static Vector2 Origin(CanvasTextLayout layout, double x, double baseline, Anchor anchor, double width)
    {
        double left = anchor switch { Anchor.Middle => x - width / 2, Anchor.End => x - width, _ => x };
        return new Vector2((float)left, (float)(baseline - layout.LineMetrics[0].Baseline));
    }

    /// <summary>A caption as SVG's &lt;text&gt;: x and baseline in box units, its font size, colour and anchor.</summary>
    private void Caption(CanvasDrawingSession ds, string text, double x, double baseline, float size, Color color, Anchor anchor = Anchor.Start, double width = 240, bool bold = false, float spacing = 0)
    {
        var layout = Text(ds, text, size, bold, anchor, spacing, width);
        ds.DrawTextLayout(layout, Origin(layout, x, baseline, anchor, width), color);
    }

    /// <summary>"not found" in small muted text, centred on a point, or ending or starting there.</summary>
    private void NotFoundNote(CanvasDrawingSession ds, double x, double cy, Anchor anchor)
    {
        const double width = 60;
        var layout = Text(ds, NotFound, NoteSize, false, anchor, 0, width);
        double left = anchor switch { Anchor.Middle => x - width / 2, Anchor.End => x - width, _ => x };
        ds.DrawTextLayout(layout, (float)left, (float)(cy - layout.LayoutBounds.Height / 2), MutedLabels);
    }

    // ---- Colour ----

    private static Color Hex(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Clamp(Math.Round(255 * opacity), 0, 255), color.R, color.G, color.B);
    // The keyboard's dark fill: the mockup's #0C2733 under its #4FD8FF outline is about a sixth of the colour.
    private static Color Dark(Color color) => Color.FromArgb(255, (byte)(color.R * 0.17), (byte)(color.G * 0.17), (byte)(color.B * 0.17));

    /// <summary>The colour of a zone: from the latest frame when it has the zone, else from the state; null when neither has it.</summary>
    private Color? ZoneColor(string key)
    {
        string? hex = frame is not null && frame.Zones.TryGetValue(key, out var shown) ? shown : stateColors.GetValueOrDefault(key);
        return Rgb.TryParse(hex, out var rgb) ? Color.FromArgb(255, rgb.R, rgb.G, rgb.B) : null;
    }

    /// <summary>How a part shows, and the colour it is drawn in.</summary>
    private (Look Look, Color Color) LookOf(ResolvedPart? part)
    {
        if (part is not { Found: true }) return (Look.Missing, Unlit);
        if (!lightsOn) return (Look.Unlit, Unlit);
        if (part.OwnEffect) return (Look.Own, Grey);
        return part.ShownZoneKey is { } key && ZoneColor(key) is { } color ? (Look.Lit, color) : (Look.Unlit, Unlit);
    }

    private ResolvedPart? Find(string id) => scene?.Parts.FirstOrDefault(p => p.Part.Id == id);

    private static Rect R(PicBox b) => new(b.X, b.Y, b.W, b.H);

    /// <summary>The union of the boxes: an SVG group's bounding box, which its glow filter's region is measured from.</summary>
    private static Rect Union(IEnumerable<PicBox> boxes)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var b in boxes) { x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y); x1 = Math.Max(x1, b.X + b.W); y1 = Math.Max(y1, b.Y + b.H); }
        return x1 < x0 ? Rect.Empty : new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    // ---- Drawing ----

    // An exception that escapes a Win2D draw callback ends the process, so a drawing fault is recorded and that frame is skipped.
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try { DrawPicture(args.DrawingSession); }
        catch (Exception error) when (error is not OutOfMemoryException) { App.Note($"The desk picture could not be drawn: {error}"); }
    }

    private void DrawPicture(CanvasDrawingSession ds)
    {
        ds.Clear(Stage);
        if (TooSmall || scene is not { } shown) return;
        // While the host isn't running the picture stays as it was, dimmed.
        using var dim = ds.CreateLayer(live ? 1f : 0.5f);
        ds.Transform = Matrix3x2.CreateScale((float)BoxScale);
        if (view == PictureView.Inside) DrawInside(ds, shown);
        else if (view == PictureView.Keys) DrawKeyboardView(ds, shown);
        else DrawDesk(ds, shown);
        ds.Transform = Matrix3x2.Identity;
    }

    /// <summary>
    /// SVG's glow filter: the shapes blurred with a standard deviation of 5 box units under the crisp shapes, cut to the filter's region
    /// (the group's bounding box grown by 40% of its size on every side). The shapes are drawn into a command list in pixels and blurred
    /// there, so the blur scales with the drawing.
    /// </summary>
    private void Glow(CanvasDrawingSession ds, Rect bounds, Action<CanvasDrawingSession> shapes)
    {
        if (bounds.IsEmpty) return;
        float s = (float)BoxScale;
        using var list = new CanvasCommandList(ds);
        using (var inner = list.CreateDrawingSession())
        {
            inner.Transform = Matrix3x2.CreateScale(s);
            shapes(inner);
        }
        using var blur = new GaussianBlurEffect { Source = list, BlurAmount = (float)(GlowDeviation * s), BorderMode = EffectBorderMode.Soft };
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.Identity;
        double mx = bounds.Width * GlowMargin, my = bounds.Height * GlowMargin;
        var region = new Rect((bounds.X - mx) * s, (bounds.Y - my) * s, (bounds.Width + 2 * mx) * s, (bounds.Height + 2 * my) * s);
        using (ds.CreateLayer(1f, region)) ds.DrawImage(blur);
        ds.Transform = saved;
    }

    // ---- The desk view: page.html's first SVG ----

    private void DrawDesk(CanvasDrawingSession ds, PictureScene shown)
    {
        // The desk: long part along the back, short part down the left.
        using (var desk = CanvasGeometry.CreatePolygon(ds, [new(22, 22), new(698, 22), new(698, 178), new(176, 178), new(176, 372), new(22, 372)]))
        {
            ds.FillGeometry(desk, Wood);
            ds.DrawGeometry(desk, WoodEdge, 2);
        }
        Caption(ds, "BACK OF DESK", 360, 13, 9, BackInk, Anchor.Middle, 200, spacing: 1.5f);

        // Grey furniture, orientation only: the curved main monitor, the speakers, the second monitor, the headphones and amp.
        var monitorPath = new CanvasPathBuilder(ds);
        monitorPath.BeginFigure(250, 52);
        monitorPath.AddQuadraticBezier(new Vector2(360, 30), new Vector2(470, 52));
        monitorPath.EndFigure(CanvasFigureLoop.Open);
        using (var monitor = CanvasGeometry.CreatePath(monitorPath)) ds.DrawGeometry(monitor, FurnitureLight, 6, RoundCaps);
        Caption(ds, "main monitor", 360, 72, 9.5f, MutedLabels, Anchor.Middle, 160);
        ds.FillRoundedRectangle(196, 34, 24, 20, 3, 3, Furniture);
        ds.FillRoundedRectangle(640, 34, 24, 20, 3, 3, Furniture);
        ds.FillRoundedRectangle(38, 70, 7, 150, 3.5f, 3.5f, FurnitureLight);
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(-MathF.PI / 2, new Vector2(62, 150)) * saved;
        Caption(ds, "second monitor", 62, 150, 9.5f, MutedLabels, Anchor.Middle, 160);
        ds.Transform = saved;
        ds.FillRoundedRectangle(42, 322, 26, 26, 4, 4, Furniture);
        ds.FillCircle(96, 335, 13, Furniture);

        if (waveRunning) DrawWaveBand(ds);

        foreach (var part in shown.Parts) if (part.Part.View == PictureView.Desk && part.Part.Kind == PartKind.Keyboard) DrawKeyboard(ds, part);
        // The mouse mat, furniture, under the mouse.
        ds.FillRoundedRectangle(512, 92, 120, 68, 6, 6, MatFill);
        ds.DrawRoundedRectangle(512, 92, 120, 68, 6, 6, MatEdge, 1);
        foreach (var part in shown.Parts) if (part.Part.View == PictureView.Desk && part.Part.Kind == PartKind.Mouse) DrawMouse(ds, part);

        // You.
        ds.FillCircle(390, 290, 46, YouFill);
        ds.DrawCircle(390, 290, 46, YouEdge, 1);
        Caption(ds, "you", 390, 294, 10, Quiet, Anchor.Middle, 80);

        DrawTower(ds, shown.Picture.Tower);
        if (waveRunning) DrawWaveArrow(ds);
    }

    /// <summary>
    /// The wave's soft band: 150 wide along the way the colours travel across the drawing (<see cref="DeskPictures.WaveDirection"/>, which
    /// on a diagonal is not the angle itself, the box being wider than it is high), centred where the mockup's is, clipped to the box.
    /// </summary>
    private void DrawWaveBand(CanvasDrawingSession ds)
    {
        var (dx, dy) = DeskPictures.WaveDirection(waveAngle);
        var d = new Vector2((float)dx, (float)dy);
        var n = new Vector2(-d.Y, d.X);
        var c = new Vector2(393, 225);
        Vector2 back = c - d * 75, ahead = c + d * 75;
        using var brush = new CanvasLinearGradientBrush(ds,
        [
            new CanvasGradientStop { Position = 0, Color = Alpha(WaveBlue, 0) },
            new CanvasGradientStop { Position = 0.5f, Color = Alpha(WaveBlue, 0.22) },
            new CanvasGradientStop { Position = 1, Color = Alpha(WaveBlue, 0) },
        ]) { StartPoint = back, EndPoint = ahead };
        using var band = CanvasGeometry.CreatePolygon(ds, [back - n * 700, ahead - n * 700, ahead + n * 700, back + n * 700]);
        using (ds.CreateLayer(1f, new Rect(0, 0, BoxW, BoxH))) ds.FillGeometry(band, brush);
    }

    /// <summary>
    /// The wave's arrow and caption. Left to right they sit exactly as in the mockup; for other directions the arrow turns about its
    /// centre, which rises and moves right with the arrow's slope so it stays in the box and clear of "you", and the caption moves beside it.
    /// The arrow points the way the band travels, at right angles to it.
    /// </summary>
    private void DrawWaveArrow(CanvasDrawingSession ds)
    {
        var (dx, dy) = DeskPictures.WaveDirection(waveAngle);
        double a = Math.Atan2(dy, dx), sin = Math.Abs(dy), cos = Math.Abs(dx);
        var c = new Vector2((float)(370 + 110 * sin), (float)(438 - 62 * sin));
        var saved = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation((float)a, c) * saved;
        ds.DrawLine(c.X - 70, c.Y, c.X + 70, c.Y, WaveBlue, 1.5f);
        using (var head = CanvasGeometry.CreatePolygon(ds, [new(c.X + 70, c.Y), new(c.X + 63, c.Y - 4), new(c.X + 63, c.Y + 4)])) ds.FillGeometry(head, WaveBlue);
        ds.Transform = saved;
        string text = "wave moving " + LightingEffects.Directions[LightingEffects.NearestDirection(waveAngle)].Name.ToLowerInvariant();
        if (sin < 0.5) Caption(ds, text, 370, 431, 9.5f, WaveCaption, Anchor.Middle, 260);
        else Caption(ds, text, c.X + 70 * cos + 10, c.Y + 3.5, 9.5f, WaveCaption, Anchor.Start, 200);
    }

    private void DrawKeyboard(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var box = R(b);
        var (look, color) = LookOf(part);
        bool chosen = selected == part.Part.Id;
        Color fill = look == Look.Lit ? Dark(color) : look == Look.Own ? Dark(Grey) : BoardFill;
        void Body(CanvasDrawingSession g, Color edge)
        {
            g.FillRoundedRectangle(box, 7, 7, fill);
            g.DrawRoundedRectangle(box, 7, 7, edge, 2.5f);
        }
        if (look == Look.Lit) Glow(ds, box, g => Body(g, color));
        Body(ds, chosen ? Accent : color);
        var keys = look != Look.Missing && part.DeviceKey is { } deviceKey && keyMaps.TryGetValue(deviceKey, out var map) ? map : null;
        if (keys is not null) DrawKeys(ds, b, keys, part.DeviceKey!, look == Look.Lit ? color : null);
        else if (look != Look.Missing)
            for (int i = 0; i < 4; i++)
            {
                float y = (float)(b.Y + 14 + 11 * i);
                ds.DrawLine((float)(b.X + 14), y, (float)(b.X + b.W - 14), y, Alpha(color, 0.75), 2, KeyRows);
            }
        else NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
        Caption(ds, chosen ? part.Part.Label + " · selected" : part.Part.Label, 390, 172, 10.5f, chosen ? Accent : Labels, Anchor.Middle, 200);
    }

    /// <summary>
    /// One rounded rectangle per positioned light. Each takes its colour from the frame's packed colours for the device (RRGGBB per light);
    /// a key the frame has no readable colour for (no frame, no entry for the device, a string that isn't 6 characters per light, or
    /// a bad hex pair) takes <paramref name="zone"/>, the part's zone colour. A null <paramref name="zone"/> draws every key unlit.
    /// </summary>
    private void DrawKeys(CanvasDrawingSession ds, PicBox box, KeyMapView keys, string deviceKey, Color? zone)
    {
        string? packed = null;
        if (zone is not null && frame?.Lights is { } lights && lights.TryGetValue(deviceKey, out var text) && text.Length == 6 * keys.Names.Count) packed = text;
        foreach (var key in DeskPictures.KeyRects(box, keys))
        {
            Color fill = zone ?? Unlit;
            if (packed is not null && int.TryParse(packed.AsSpan(6 * key.Light, 6), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int rgb)) fill = Hex(rgb);
            ds.FillRoundedRectangle((float)key.X, (float)key.Y, (float)key.W, (float)key.H, 1.5f, 1.5f, fill);
        }
    }

    private void DrawMouse(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var center = new Vector2((float)b.CenterX, (float)b.CenterY);
        float rx = (float)(b.W / 2), ry = (float)(b.H / 2);
        var (look, color) = LookOf(part);
        bool chosen = selected == part.Part.Id;
        if (look is Look.Lit or Look.Own)
        {
            if (look == Look.Lit) Glow(ds, R(b), g => g.FillEllipse(center, rx, ry, color));
            ds.FillEllipse(center, rx, ry, color);
            if (chosen) ds.DrawEllipse(center, rx, ry, Accent, 2);
        }
        else ds.DrawEllipse(center, rx, ry, chosen ? Accent : Unlit, 2);
        if (look == Look.Missing) NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
        Caption(ds, chosen ? part.Part.Label + " · selected" : part.Part.Label, 572, 172, 10.5f, chosen ? Accent : Labels, Anchor.Middle, 200);
    }

    /// <summary>The tower on the floor, in the colours of radiator fans 1 to 3 (its strip), the pump (its ring) and the RAM (its lines).</summary>
    private void DrawTower(CanvasDrawingSession ds, PicBox t)
    {
        var box = R(t);
        float x = (float)t.X, y = (float)t.Y;
        var (look, edge) = LookOf(Find("fan1"));
        void Body(CanvasDrawingSession g)
        {
            g.FillRoundedRectangle(box, 6, 6, TowerFill);
            g.DrawRoundedRectangle(box, 6, 6, edge, 2);
        }
        if (look == Look.Lit) Glow(ds, box, Body);
        Body(ds);
        ds.FillRoundedRectangle(x + 10, y + 10, 16, 6, 3, 3, LookOf(Find("fan1")).Color);
        ds.FillRoundedRectangle(x + 30, y + 10, 16, 6, 3, 3, LookOf(Find("fan2")).Color);
        ds.FillRoundedRectangle(x + 50, y + 10, 16, 6, 3, 3, LookOf(Find("fan3")).Color);
        ds.DrawCircle(x + 24, y + 36, 8, LookOf(Find("pump")).Color, 2.5f);
        var ram = LookOf(Find("ram1")).Color;
        ds.FillRectangle(x + 40, y + 26, 3, 20, ram);
        ds.FillRectangle(x + 46, y + 26, 3, 20, ram);
        Caption(ds, "PC tower", t.X + 86, t.Y + 24, 11.5f, Ink, Anchor.Start, 160);
        Caption(ds, "tap to see inside ↗", t.X + 86, t.Y + 39, 10, WaveBlue, Anchor.Start, 160);
        // The keyboard's stop: a white outline just outside the tower, as the selected radiator fan's.
        if (towerStop) ds.DrawRoundedRectangle((float)t.X - 4, (float)t.Y - 4, (float)t.W + 8, (float)t.H + 8, 8, 8, White, 1.5f);
    }

    // ---- The keyboard view (lighting 3b): key-painting.html's large keyboard ----

    /// <summary>
    /// Every key at its shape, as a rounded rectangle in its live colour: the frame's colour for its light (read as <see cref="DrawKeys"/>
    /// reads it), else the colour of the zone the keyboard's part shows; unlit with the lights off. Its label goes on it in white or black
    /// by the fill's brightness (key-painting.html's rule), and a picked key, while picking, has a 3 px accent outline inside its edge.
    /// </summary>
    private void DrawKeyboardView(CanvasDrawingSession ds, PictureScene shown)
    {
        if (KeysMap() is not { } map || KeyFit() is not { } fit) return;
        Color? zone = null;
        if (lightsOn && shown.Parts.FirstOrDefault(p => p.Found && p.DeviceKey == keysDevice) is { ShownZoneKey: { } zoneKey } && ZoneColor(zoneKey) is { } zoneColor) zone = zoneColor;
        string? packed = null;
        if (lightsOn && frame?.Lights is { } lights && keysDevice is { } deviceKey && lights.TryGetValue(deviceKey, out var text) && text.Length == 6 * map.Names.Count) packed = text;
        double k = fit.Scale;
        float round = (float)(KeyRound * k), stroke = (float)(PickedStroke / BoxScale);
        foreach (var key in fit.Set.Keys)
        {
            Color fill = zone ?? Unlit;
            if (packed is not null && key.Light >= 0 && key.Light < map.Names.Count
                && int.TryParse(packed.AsSpan(6 * key.Light, 6), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int rgb)) fill = Hex(rgb);
            var rect = new Rect(fit.X + (key.X + KeyGap) * k, fit.Y + (key.Y + KeyGap) * k, Math.Max(0, (key.W - 2 * KeyGap) * k), Math.Max(0, (key.H - 2 * KeyGap) * k));
            ds.FillRoundedRectangle(rect, round, round, fill);
            if (pickingKeys && pickedKeys is { } picked && picked.Contains(key.Light))
                ds.DrawRoundedRectangle(new Rect(rect.X + stroke / 2, rect.Y + stroke / 2, Math.Max(0, rect.Width - stroke), Math.Max(0, rect.Height - stroke)), round, round, Accent, stroke);
            if (key.Label.Length == 0) continue;
            float size = (float)((key.Label.Length > 3 ? 0.24 : 0.3) * k);
            var label = Text(ds, key.Label, size, false, Anchor.Middle, 0, rect.Width);
            ds.DrawTextLayout(label, (float)rect.X, (float)(rect.Y + (rect.Height - label.LayoutBounds.Height) / 2), Bright(fill) ? Black : White);
        }
    }

    /// <summary>A fill light enough for black text: key-painting.html's (299 R + 587 G + 114 B) / 1000 of at least 150.</summary>
    private static bool Bright(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000.0 >= 150;

    // ---- The inside view: page.html's second SVG ----

    private void DrawInside(CanvasDrawingSession ds, PictureScene shown)
    {
        var parts = shown.Parts.Where(p => p.Part.View == PictureView.Inside).ToArray();
        ResolvedPart[] Of(PartKind kind) => parts.Where(p => p.Part.Kind == kind).ToArray();

        Caption(ds, "rear", 150, 16, 9.5f, Quiet, Anchor.Start, 100);
        Caption(ds, "front", 566, 16, 9.5f, Quiet, Anchor.End, 100);
        var c = shown.Picture.Case;
        ds.FillRoundedRectangle(R(c), 10, 10, CaseFill);
        ds.DrawRoundedRectangle(R(c), 10, 10, CaseEdge, 2.5f);

        // Radiator fans: one glow for the three, as the mockup's group.
        var fans = Of(PartKind.FanBar);
        var litFans = fans.Where(f => LookOf(f).Look == Look.Lit).ToArray();
        if (litFans.Length > 0) Glow(ds, Union(fans.Select(f => f.Part.Box)), g => { foreach (var f in litFans) g.FillRoundedRectangle(R(f.Part.Box), 6, 6, LookOf(f).Color); });
        foreach (var f in fans) DrawFanBar(ds, f);
        Caption(ds, "radiator fans 1–3 · Corsair iCUE LINK hub", 160, 80, 10, CaptionGrey, Anchor.Start, 410);

        // Rear fan, unlit: furniture.
        ds.DrawCircle(186, 150, 30, CaseEdge, 3);
        ds.FillCircle(186, 150, 5, CaseEdge);
        Caption(ds, "rear fan, unlit", 186, 198, 9.5f, Quiet, Anchor.Middle, 100);

        // Board area.
        ds.FillRoundedRectangle(236, 100, 210, 150, 6, 6, BoardFill);
        ds.DrawRoundedRectangle(236, 100, 210, 150, 6, 6, BoardEdge, 1);
        foreach (var p in Of(PartKind.PumpRing)) DrawPump(ds, p);
        Caption(ds, "pump", 306, 222, 10, Labels, Anchor.Middle, 100);
        var sticks = Of(PartKind.RamStick);
        var litSticks = sticks.Where(r => LookOf(r).Look == Look.Lit).ToArray();
        if (litSticks.Length > 0) Glow(ds, Union(sticks.Select(r => r.Part.Box)), g => { foreach (var r in litSticks) g.FillRoundedRectangle(R(r.Part.Box), 2, 2, LookOf(r).Color); });
        foreach (var r in sticks) DrawRamStick(ds, r, sticks);
        Caption(ds, "RAM ×2", 404, 240, 10, Labels, Anchor.Middle, 100);

        foreach (var p in Of(PartKind.Card)) DrawCard(ds, p);
        Caption(ds, "graphics card", 172, 336, 10, Labels, Anchor.Start, 200);

        // Front fans: one glow for the three while they're lit.
        var rings = Of(PartKind.FanRing);
        var litRings = rings.Where(r => LookOf(r).Look == Look.Lit).ToArray();
        if (litRings.Length > 0) Glow(ds, Union(rings.Select(r => r.Part.Box)), g => { foreach (var r in litRings) g.DrawCircle((float)r.Part.Box.CenterX, (float)r.Part.Box.CenterY, (float)(r.Part.Box.W / 2), LookOf(r).Color, 6); });
        foreach (var r in rings) DrawFanRing(ds, r);
        Caption(ds, "front fans ×3", 528, 336, 10, Labels, Anchor.Middle, 120);
        if (rings.Any(r => r.OwnEffect)) Caption(ds, OwnEffectNote, 528, 349, 9, CaptionGrey, Anchor.Middle, 120);

        // Power supply shroud.
        ds.FillRoundedRectangle(150, 372, 420, 58, 6, 6, PsuFill);
        ds.DrawRoundedRectangle(150, 372, 420, 58, 6, 6, PsuEdge, 1);
        Caption(ds, "power supply", 360, 405, 10, PsuInk, Anchor.Middle, 160);
    }

    private void DrawFanBar(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var box = R(b);
        var (look, color) = LookOf(part);
        bool chosen = selected == part.Part.Id;
        if (look is Look.Lit or Look.Own) ds.FillRoundedRectangle(box, 6, 6, color);
        else ds.DrawRoundedRectangle(box, 6, 6, Unlit, 2);
        // Selected: a white outline, as the mockup's radiator fan 2.
        if (chosen) ds.DrawRoundedRectangle(box, 6, 6, White, 2);
        if (look == Look.Missing) NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
    }

    private void DrawPump(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var center = new Vector2((float)b.CenterX, (float)b.CenterY);
        float r = (float)(b.W / 2);
        var (look, color) = LookOf(part);
        void Ring(CanvasDrawingSession g)
        {
            g.FillCircle(center, r, PumpFill);
            g.DrawCircle(center, r, color, 7);
        }
        if (look == Look.Lit) Glow(ds, R(b), Ring);
        Ring(ds);
        // Selected: an accent ring just outside the pump's own ring.
        if (selected == part.Part.Id) ds.DrawCircle(center, r + 3.5f + 2.5f, Accent, 2);
        if (look == Look.Missing) NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
    }

    private void DrawRamStick(CanvasDrawingSession ds, ResolvedPart part, IReadOnlyList<ResolvedPart> sticks)
    {
        var b = part.Part.Box;
        var box = R(b);
        var (look, color) = LookOf(part);
        if (look is Look.Lit or Look.Own) ds.FillRoundedRectangle(box, 2, 2, color);
        else ds.DrawRoundedRectangle(box, 2, 2, Unlit, 1.5f);
        if (selected == part.Part.Id) ds.DrawRoundedRectangle(box, 2, 2, Accent, 2);
        if (look != Look.Missing) return;
        // Beside the stick, on the side away from its neighbour, as a stick is too narrow to hold the note.
        bool left = sticks.Any(s => s.Part.Box.CenterX > b.CenterX);
        if (left) NotFoundNote(ds, b.X - 4, b.CenterY, Anchor.End);
        else NotFoundNote(ds, b.X + b.W + 4, b.CenterY, Anchor.Start);
    }

    private void DrawCard(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var box = R(b);
        var (look, color) = LookOf(part);
        bool chosen = selected == part.Part.Id;
        ds.FillRoundedRectangle(box, 6, 6, CardFill);
        ds.DrawRoundedRectangle(box, 6, 6, chosen ? Accent : CardEdge, chosen ? 2 : 1);
        if (look == Look.Missing)
        {
            NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
            return;
        }
        // The card's lit name, at the mockup's (250, 302) from its box at (170, 270).
        const double width = 200;
        var name = Text(ds, "GIGABYTE", 17, true, Anchor.Start, 1.5f, width);
        var at = Origin(name, b.X + 80, b.Y + 32, Anchor.Start, width);
        if (look == Look.Lit)
        {
            var bounds = name.LayoutBounds;
            var text = new Rect(at.X + bounds.X, at.Y + bounds.Y, bounds.Width, bounds.Height);
            Glow(ds, text, g => g.DrawTextLayout(name, at, color));
        }
        ds.DrawTextLayout(name, at, color);
    }

    private void DrawFanRing(CanvasDrawingSession ds, ResolvedPart part)
    {
        var b = part.Part.Box;
        var center = new Vector2((float)b.CenterX, (float)b.CenterY);
        float r = (float)(b.W / 2);
        var (look, color) = LookOf(part);
        ds.DrawCircle(center, r, color, 6);
        // Selected: a white outline just outside the ring, as the other fans.
        if (selected == part.Part.Id) ds.DrawCircle(center, r + 3 + 2, White, 2);
        if (look == Look.Missing) NotFoundNote(ds, b.CenterX, b.CenterY, Anchor.Middle);
    }

    /// <summary>The surface at the box's 720:450 shape, as wide as the space allows up to 1280 px and centred, sized in layout.</summary>
    private sealed class StagePanel : Panel
    {
        private static double DrawWidth(double available) => double.IsFinite(available) && available >= 0 ? Math.Min(available, MaxDrawWidth) : MaxDrawWidth;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = DrawWidth(availableSize.Width), height = width * BoxH / BoxW;
            foreach (var child in Children) child.Measure(new Size(width, height));
            return new Size(double.IsFinite(availableSize.Width) ? Math.Max(width, availableSize.Width) : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = DrawWidth(finalSize.Width), height = width * BoxH / BoxW;
            foreach (var child in Children) child.Arrange(new Rect(Math.Max(0, (finalSize.Width - width) / 2), 0, width, height));
            return finalSize;
        }
    }
}
