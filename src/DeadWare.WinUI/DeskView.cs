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
/// device whose zones are shown separately. A tap selects an item; a tap anywhere on the closed tower opens and selects it, and on
/// the open tower its label closes it and its box selects it; a tap on empty desk clears the selection. The desk keeps a 4:3 shape,
/// filling its width up to 720 px, so desk units map to pixels the same way at
/// every size. Off screen it lets go of its drawing surface, as <see cref="ConnectionMapView"/> does. The canvas can't be read by a
/// screen reader; the page's device list is the accessible way to the same selection.
/// <para>
/// In edit layout (<see cref="Editing"/>) a device box, a zone dot or the tower (by its label, or anywhere on it while it is closed)
/// can be dragged, and with a device or the tower selected the arrow keys move it a grid step at a time; the canvas is then a tab
/// stop whose accessible name states the selection and its position. Each finished edit is raised once, as
/// <see cref="LayoutEdited"/> for a drop or <see cref="Nudged"/> for an arrow key, and held as a draft, which the view draws instead of the shown layout until its owner drops it
/// (<see cref="Release"/>, <see cref="DropDraft"/>): arriving layouts never move a box while an edit is on its way.
/// </para>
/// </summary>
internal sealed class DeskView : UserControl
{
    /// <summary>The value of <see cref="Selected"/> while the tower is selected.</summary>
    public const string Tower = DeskGeometry.TowerKey;
    private const string IdleName = "Desk layout. Use the device list below to select a device.";
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
    // The devices whose zones are drawn as separate dots, which edit layout's Move zones separately fills; neither this nor towerOpen is saved.
    private readonly HashSet<string> split = new(StringComparer.Ordinal);
    private DeskScene scene;
    private Action<DeskLayout>? layoutEdited;
    private string? selected, shownName;
    // What a drag moves: a device by its key, a zone dot by its zone key, or the tower.
    private enum DragKind { Device, Zone, Tower }
    private readonly record struct DragTarget(DragKind Kind, string Key);
    // The press in progress, one pointer at a time. Base: the layout at press time, which a drag moves by the total delta so snapping
    // never drifts. TowerBox: the tower as drawn at press time, which a dropped device is tested against. Editing: edit layout was on.
    private sealed record Press(uint Pointer, Point Start, DeskLayout Base, DragTarget? Target, DeskBox TowerBox, bool Editing);
    private Press? press;
    // held: the last finished edit, drawn until the owner drops it. drag: the drag in progress, drawn over held.
    private DeskLayout? held, drag;
    private bool lightsOn = true, live, towerOpen, editing, moved;
    // Lighting 4: the devices drawn unlit whatever the lights do, a mouse that isn't connected (MouseTexts.Unlit). Still selectable.
    private HashSet<string> unlit = new(StringComparer.Ordinal);

    public DeskView()
    {
        IsTabStop = false;
        UseSystemFocusVisuals = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        UpdateName();
        var back = Caption("BACK OF DESK");
        var front = Caption("FRONT · YOUR SEAT");
        Content = new DeskFrame { Children = { back, surface, front } };
        scene = DeskGeometry.Scene(layout, devices, towerOpen, split);
        surface.PointerPressed += OnPointerPressed;
        surface.PointerMoved += OnPointerMoved;
        surface.PointerReleased += OnPointerReleased;
        surface.PointerCaptureLost += OnPointerLost;
        surface.PointerCanceled += OnPointerLost;
        surface.SizeChanged += (_, _) => { ClearTexts(); Redraw(); };
        ActualThemeChanged += (_, _) => Redraw();
        this.WhileShowing(ShowSurface, HideSurface);
    }

    /// <summary>A device key, <see cref="Tower"/>, or null. Setting it from outside raises no <see cref="SelectionChanged"/>.</summary>
    public string? Selected { get => selected; set => Select(value, false); }

    /// <summary>
    /// Edit layout is on: items can be dragged and nudged with the arrow keys, and the canvas is a tab stop. Turning it off ends a
    /// drag in progress, ends Move zones separately for every device and takes the canvas out of the tab order again.
    /// </summary>
    public bool Editing
    {
        get => editing;
        set
        {
            if (editing == value) return;
            editing = value;
            IsTabStop = value;
            if (!value)
            {
                CancelPress();
                if (split.Count > 0)
                {
                    split.Clear();
                    Rebuild();
                }
            }
            UpdateName();
        }
    }

    /// <summary>The layout edits start from: the held draft while there is one, else the layout last shown. A drag in progress is not in it.</summary>
    public DeskLayout Layout => held ?? layout;

    /// <summary>The held draft, or null.</summary>
    public DeskLayout? Held => held;

    /// <summary>The user selected an item on the desk, or cleared the selection with a tap on empty desk.</summary>
    public event Action<string?>? SelectionChanged;

    /// <summary>One per finished drag in edit layout, raised at the drop. The edit is already held when it is raised.</summary>
    public event Action<DeskLayout>? LayoutEdited { add => layoutEdited += value; remove => layoutEdited -= value; }

    /// <summary>One per arrow key press in edit layout, with the held draft moved one grid step. The move is already held when it is raised.</summary>
    public event Action<DeskLayout>? Nudged;

    /// <summary>Holds an edit made outside the view (the panel, the desk shape, reset): drawn instead of the shown layout until dropped.</summary>
    public void Hold(DeskLayout draft)
    {
        held = draft;
        OpenForSelection();
        Rebuild();
    }

    /// <summary>The held edit is saved and shown: the view draws the shown layout again. A drag in progress goes on.</summary>
    public void Release()
    {
        if (held is null) return;
        held = null;
        Rebuild();
    }

    /// <summary>The edits on their way are given up: the held edit and any drag in progress are dropped, and the shown layout is drawn.</summary>
    public void DropDraft()
    {
        bool had = held is not null;
        held = null;
        CancelPress();
        if (had) Rebuild();
    }

    /// <summary>Whether a device's zones are drawn and dragged as separate dots.</summary>
    public bool IsSplit(string key) => split.Contains(key);

    public void SetSplit(string key, bool separate)
    {
        if (!(separate ? split.Add(key) : split.Remove(key))) return;
        Rebuild();
    }

    /// <summary>The device is on the desk (or inside the tower) with a point to move: what edit layout's panel and the arrow keys need.</summary>
    public bool CanEdit(string key) => Placed(Layout, key);

    /// <summary>The tower's caption: "PC tower · 1 device", "PC tower · 3 devices".</summary>
    public static string TowerCaption(int count) => count == 1 ? "PC tower · 1 device" : $"PC tower · {count} devices";

    /// <summary>
    /// Shows a layout and the devices it places. <paramref name="lightsOn"/> false draws every box unlit; <paramref name="live"/> false
    /// draws the desk dimmed and ignores taps, as while the lighting host isn't running. A held draft or a drag in progress keeps
    /// being drawn in place of the layout's positions; everything else shows from here.
    /// </summary>
    public void Show(DeskLayout layout, IReadOnlyList<DeviceView> devices, bool lightsOn, bool live)
    {
        this.layout = layout;
        this.devices = devices;
        this.lightsOn = lightsOn;
        this.live = live;
        if (!live) CancelPress();
        var list = devices.ToArray();
        var numbered = LightingPage.Names(list);
        byKey = new(StringComparer.Ordinal);
        names = new(StringComparer.Ordinal);
        zoneColors = new(StringComparer.Ordinal);
        unlit = new(StringComparer.Ordinal);
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i]?.Key is not { } key || !byKey.TryAdd(key, list[i])) continue;
            names[key] = numbered[i];
            if (MouseTexts.Unlit(list[i])) unlit.Add(key);
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

    /// <summary>What is drawn: the drag in progress, else the held draft, else the layout last shown.</summary>
    private DeskLayout Current => drag ?? held ?? layout;

    private void Rebuild()
    {
        scene = DeskGeometry.Scene(Current, devices, towerOpen, split);
        UpdateName();
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
        UpdateName();
        Redraw();
        if (raise) SelectionChanged?.Invoke(key);
    }

    /// <summary>A selected device that an edit put inside the closed tower opens it, so the selection still shows.</summary>
    private void OpenForSelection()
    {
        if (selected is { } key && InTower(key) && !towerOpen) towerOpen = true;
    }

    private bool InTower(string key) => key != Tower && Current.Tower.Members.Contains(key) && !Current.Unplaced.Contains(key);

    /// <summary>A device the layout places, with at least one zone point: not unsupported, not without LEDs, not taken off the desk.</summary>
    private bool Placed(DeskLayout from, string key) => key != Tower && PointOf(from, key) is not null;

    /// <summary>Where the desk draws a placed device: its first placed zone's point, as <see cref="DeskGeometry.Scene"/> does.</summary>
    private DeskPoint? PointOf(DeskLayout from, string key)
    {
        if (!byKey.TryGetValue(key, out var device) || device.Kind == PaintKind.Unsupported || from.Unplaced.Contains(key)) return null;
        foreach (var zone in device.Zones ?? [])
            if (zone is { LedCount: > 0 } && from.Zones.TryGetValue(LightingKeys.Zone(key, zone.Index), out var at)) return at;
        return null;
    }

    /// <summary>The grid has 49 positions a side, 0/48 to 48/48.</summary>
    private const int Positions = DeskGeometry.Snap + 1;

    /// <summary>A coordinate's grid column or row, 1 to 49.</summary>
    private static int Cell(double n) => Math.Clamp((int)Math.Round(n * DeskGeometry.Snap, MidpointRounding.AwayFromZero) + 1, 1, Positions);

    /// <summary>
    /// The canvas's accessible name: in edit layout, the selection and where it is ("Apex Pro, On desk, column 24 of 49, row 30 of
    /// 49"); otherwise, or with nothing that can move selected, the pointer to the device list.
    /// </summary>
    private void UpdateName()
    {
        string name = IdleName;
        var from = Current;
        if (editing && selected == Tower) name = $"PC tower, column {Cell(from.Tower.At.X)} of {Positions}, row {Cell(from.Tower.At.Y)} of {Positions}";
        else if (editing && selected is { } key && PointOf(from, key) is { } at)
        {
            string where = from.Tower.Members.Contains(key) ? "Inside PC" : "On desk";
            name = $"{names.GetValueOrDefault(key) ?? key}, {where}, column {Cell(at.X)} of {Positions}, row {Cell(at.Y)} of {Positions}";
        }
        if (name == shownName) return;
        shownName = name;
        AutomationProperties.SetName(this, name);
    }

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
        // One pointer at a time: a second one pressed during a press or drag is ignored.
        if (press is not null) return;
        var point = e.GetCurrentPoint(surface);
        if (!live || TooSmall || !point.Properties.IsLeftButtonPressed) return;
        press = new Press(e.Pointer.PointerId, point.Position, Layout, editing ? TargetAt(point.Position) : null, scene.Tower, editing);
        moved = false;
        surface.CapturePointer(e.Pointer);
        // In edit layout a press gives the canvas focus, so the arrow keys move what it selects.
        if (editing) Focus(FocusState.Pointer);
        e.Handled = true;
    }

    /// <summary>What a press in edit layout would drag: the tower by its label (or anywhere on it while closed), a zone dot, a device box.</summary>
    private DragTarget? TargetAt(Point at)
    {
        if (TowerLabel().Contains(at)) return new DragTarget(DragKind.Tower, Tower);
        return HitAt(ToDesk(at)) switch
        {
            { Kind: DeskItemKind.Zone, ZoneKey: { } zone } => new DragTarget(DragKind.Zone, zone),
            { Kind: DeskItemKind.Device, DeviceKey: { } device } => new DragTarget(DragKind.Device, device),
            { Kind: DeskItemKind.Tower } when !towerOpen => new DragTarget(DragKind.Tower, Tower),
            _ => null,
        };
    }

    /// <summary>The pointer's movement from the press, in desk units, by the same mapping the drawing uses.</summary>
    private (double Dx, double Dy) Delta(Point start, Point end) => ((end.X - start.X) / SpanX, (end.Y - start.Y) / SpanY);

    private static bool Beyond(double dx, double dy) => Math.Abs(dx) >= DeskGeometry.DragEpsilon || Math.Abs(dy) >= DeskGeometry.DragEpsilon;

    /// <summary>The layout at press time with the target moved by the total delta.</summary>
    private DeskLayout Moved(DeskLayout from, DragTarget target, double dx, double dy) => target.Kind switch
    {
        DragKind.Device => DeskGeometry.MoveDevice(from, devices, target.Key, dx, dy),
        DragKind.Zone => DeskGeometry.MoveZone(from, target.Key, dx, dy),
        _ => DeskGeometry.MoveTower(from, dx, dy),
    };

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (press is not { Editing: true, Target: { } target } current || e.Pointer.PointerId != current.Pointer || !live || TooSmall) return;
        var (dx, dy) = Delta(current.Start, e.GetCurrentPoint(surface).Position);
        // Under the drag threshold in both axes it is still a click.
        if (!moved && !Beyond(dx, dy)) return;
        moved = true;
        drag = Moved(current.Base, target, dx, dy);
        Rebuild();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (press is not { } current || e.Pointer.PointerId != current.Pointer) return;
        // Cleared before the capture goes, so the capture-lost event that follows finds no press.
        press = null;
        bool dragged = drag is not null;
        drag = null;
        surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!live || TooSmall)
        {
            if (dragged) Rebuild();
            return;
        }
        var end = e.GetCurrentPoint(surface).Position;
        var (dx, dy) = Delta(current.Start, end);
        if (!current.Editing)
        {
            // A movement under the drag threshold is a tap.
            if (Math.Sqrt(dx * dx + dy * dy) < DeskGeometry.DragEpsilon) Tap(end);
            return;
        }
        // In edit layout a press that never went past the threshold in either axis is a click, and selects as a tap does.
        if (!moved && !Beyond(dx, dy))
        {
            Tap(end);
            return;
        }
        if (current.Target is not { } target)
        {
            if (dragged) Rebuild();
            return;
        }
        var next = Moved(current.Base, target, dx, dy);
        // Dropped on the tower as it was drawn at press time, a device goes inside; dragged out of it, it goes outside and stays out.
        if (target.Kind == DragKind.Device) next = DeskGeometry.Drop(next, scene with { Tower = current.TowerBox }, target.Key, ToDesk(end));
        Finish(next);
    }

    /// <summary>A pointer cancelled or its capture lost: the drag's draft goes, and the held draft from an earlier edit, if any, shows again.</summary>
    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (press is not { } current || e.Pointer.PointerId != current.Pointer) return;
        CancelPress();
    }

    /// <summary>Ends the press in progress without an edit, dropping a drag's draft.</summary>
    private void CancelPress()
    {
        if (press is null && drag is null) return;
        press = null;
        bool dragged = drag is not null;
        drag = null;
        // Cleared first, so the capture-lost event this raises finds no press.
        surface.ReleasePointerCaptures();
        if (dragged) Rebuild();
    }

    /// <summary>A finished edit: held, drawn and raised once, as a nudge for an arrow key and as an edit for a drop.</summary>
    private void Finish(DeskLayout next, bool nudge = false)
    {
        held = next;
        OpenForSelection();
        Rebuild();
        (nudge ? Nudged : layoutEdited)?.Invoke(next);
    }

    /// <summary>In edit layout the arrow keys move the selected device or the tower one grid step; Up is towards the back of the desk.</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        (int Dx, int Dy)? move = !editing ? null : e.Key switch
        {
            Windows.System.VirtualKey.Left => (-1, 0),
            Windows.System.VirtualKey.Right => (1, 0),
            Windows.System.VirtualKey.Up => (0, -1),
            Windows.System.VirtualKey.Down => (0, 1),
            _ => null,
        };
        if (move is not { } by)
        {
            base.OnKeyDown(e);
            return;
        }
        // Handled either way, so the page doesn't scroll under the desk.
        e.Handled = true;
        Nudge(by.Dx, by.Dy);
    }

    private void Nudge(int dx, int dy)
    {
        // Not while the host isn't running, nor during a drag, which would replace the nudge when it drops.
        if (!live || press is not null || selected is not { } key) return;
        // The same layout back: nothing could move (at the desk's edge, or nothing placed is selected), so nothing is held or sent.
        var from = Layout;
        var next = DeskGeometry.Nudge(from, devices, key, dx, dy);
        if (!ReferenceEquals(next, from)) Finish(next, true);
    }

    private void Tap(Point at)
    {
        var hit = HitAt(ToDesk(at));
        bool onLabel = TowerLabel().Contains(at);
        // Anywhere on the closed tower, its label included, opens it in place and selects it.
        if (!towerOpen && (onLabel || hit is { Kind: DeskItemKind.Tower }))
        {
            towerOpen = true;
            Rebuild();
            Select(Tower, true);
            return;
        }
        // The open tower's label closes it; its box selects it, below.
        if (onLabel)
        {
            towerOpen = false;
            Rebuild();
            return;
        }
        string? key = hit switch
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
        CancelPress();
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

        foreach (var rect in Current.Desk ?? [])
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
        if (!Lit(item)) { ds.FillRoundedRectangle(rect, 8, 8, Alpha(ink, 0.05)); ds.DrawRoundedRectangle(rect, 8, 8, hairline, 1); }
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
        var fill = !Lit(item) ? Alpha(ink, 0.08) : item.OwnEffect ? Alpha(muted, 0.4) : Average(item.ZoneKeys) ?? muted;
        ds.FillCircle(center, radius, fill);
        ds.DrawCircle(center, radius, hairline, 1);
        if (item.DeviceKey is { } key && key == selected) ds.DrawCircle(center, radius + 3, accent, 2);
        var label = Text(creator, item.Label, false, ZoneLabelWidth);
        ds.DrawTextLayout(label, center.X + radius + 4, (float)(center.Y - label.LayoutBounds.Height / 2), ink);
    }

    /// <summary>Whether a box or dot is drawn lit: the lights are on and its device isn't one drawn unlit (lighting 4, an absent mouse).</summary>
    private bool Lit(DeskItem item) => lightsOn && !(item.DeviceKey is { } key && unlit.Contains(key));

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
